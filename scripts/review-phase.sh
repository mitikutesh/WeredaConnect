#!/usr/bin/env bash
# scripts/review-phase.sh
# Phase 3 of the Claude <-> Codex pipeline: IMPLEMENTATION REVIEW.
#
#   Codex   reviews the implementation diff          (CODE_REVIEW_BY_CODEX)
#   Claude  evaluates findings, fixes accepted ones  (REVIEW_RESOLUTION)
#   Codex   validates Claude's REJECTED findings      (CODE_REVIEW_BY_CODEX)
#
# If any rejection is upheld by Codex -> a dispute is opened, the workflow
# stops, and the user must decide. On clean completion the run reaches
# READY_FOR_APPROVAL — the user (not the orchestrator) merges/ships.
#
# Usage: scripts/review-phase.sh <run_id>
# State transitions: IMPLEMENTATION_SELF_REVIEW -> CODE_REVIEW_BY_CODEX
#                    -> REVIEW_RESOLUTION -> READY_FOR_APPROVAL
#                    (or -> DISPUTE_OPEN / USER_DECISION_REQUIRED)
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=lib/ai-common.sh
source "$SCRIPT_DIR/lib/ai-common.sh"

RUN_ID="${1:?Usage: review-phase.sh <run_id>}"
RUN_DIR="$(ai_run_dir "$RUN_ID")"
PROMPTS="$(ai_prompts_dir)"
[ -d "$RUN_DIR" ] || die "Unknown run id: $RUN_ID (run dir missing)"

STATE="$(ai_state_get "$RUN_ID")"
[ "$STATE" = "IMPLEMENTATION_SELF_REVIEW" ] || die "Cannot review: state is '$STATE', expected IMPLEMENTATION_SELF_REVIEW."
[ -f "$RUN_DIR/07-implementation.diff" ] || die "Missing implementation diff (07-implementation.diff)."

log_step "REVIEW PHASE — run $RUN_ID"
ai_status "$RUN_ID" "Review phase started"

# --- Codex reviews the implementation diff ---------------------------------
ai_transfer "$RUN_ID" "Claude" "Codex" ".ai/runs/$RUN_ID/07-implementation.diff" "Code review required"
ai_state_set "$RUN_ID" "CODE_REVIEW_BY_CODEX"
# disputes.md is included when present (on --resume after a dispute) so Codex
# HONORS the user's recorded decision and never re-raises a waived finding.
invoke_codex "$RUN_ID" "Review" "review implementation diff" \
  "$RUN_DIR/08-codex-code-review.md" \
  "$PROMPTS/codex-code-review.md" \
  "$RUN_DIR/05-final-plan.md" \
  "$RUN_DIR/06-execution-summary.md" \
  "$RUN_DIR/07-implementation.diff" \
  "$RUN_DIR/disputes.md"
ai_mirror "$RUN_ID" "08-codex-code-review.md" "codex-code-review.md"
ai_register_artifact "$RUN_ID" "codex-code-review.md" "Codex" "Review" "docs/ai-plans/codex-code-review.md"

# Missing verdict degrades to CHANGES_REQUESTED (force evaluation; never auto-approve).
VERDICT="$(ai_marker_or VERDICT "$RUN_DIR/08-codex-code-review.md" "CHANGES_REQUESTED")"
ai_status "$RUN_ID" "Codex code review complete: $VERDICT"

# --- Fast path: clean approval --------------------------------------------
if [ "$VERDICT" = "APPROVED" ]; then
  ai_decision "$RUN_ID" "Codex APPROVED the implementation (no findings)"
  ai_transfer "$RUN_ID" "Codex" "User" "docs/ai-plans/codex-code-review.md" "Approved; ready for user approval"
  ai_state_set "$RUN_ID" "READY_FOR_APPROVAL"
  ai_status "$RUN_ID" "Ready for user approval"
  exit 0
fi

# --- Claude evaluates findings, fixes accepted ones ------------------------
# disputes.md is included when present (e.g. on --resume after a CODE dispute)
# so the user's recorded decision guides the re-evaluation. No-op otherwise.
ai_transfer "$RUN_ID" "Codex" "Claude" "docs/ai-plans/codex-code-review.md" "Findings to resolve (${VERDICT:-<none>})"
ai_state_set "$RUN_ID" "REVIEW_RESOLUTION"
ai_status "$RUN_ID" "Claude resolving code-review findings"
# Same permission-mode rationale as execution-phase.sh (path trailing-space
# blocks acceptEdits; AI_ORCH_GUARD hook tripwire keeps ship-actions blocked).
EXEC_MODE="${AI_EXEC_PERMISSION_MODE:-bypassPermissions}"
invoke_claude "$RUN_ID" "Review" "evaluate code-review findings + fix accepted" \
  "$RUN_DIR/09-claude-review-evaluation.md" "$EXEC_MODE" \
  "$PROMPTS/claude-review-response.md" \
  "$RUN_DIR/05-final-plan.md" \
  "$RUN_DIR/07-implementation.diff" \
  "$RUN_DIR/08-codex-code-review.md" \
  "$RUN_DIR/disputes.md"
ai_register_artifact "$RUN_ID" "09-claude-review-evaluation.md" "Claude" "Review" ".ai/runs/$RUN_ID/09-claude-review-evaluation.md"

# Missing eval status degrades to REJECTIONS_PENDING (triggers Codex validation).
EVAL_STATUS="$(ai_marker_or EVALUATION_STATUS "$RUN_DIR/09-claude-review-evaluation.md" "REJECTIONS_PENDING")"
ai_decision "$RUN_ID" "Claude code-review evaluation: $EVAL_STATUS"

# Refresh the diff so the validation pass sees the fixes Claude just made.
if [ -f "$RUN_DIR/base-ref.txt" ]; then
  BASE_REF="$(cat "$RUN_DIR/base-ref.txt")"
  git -C "$(ai_repo_root)" diff "$BASE_REF" > "$RUN_DIR/07-implementation.diff.post" || true
fi

# --- Codex validates rejected findings (only if rejections pending) --------
if [ "$EVAL_STATUS" = "REJECTIONS_PENDING" ]; then
  ai_transfer "$RUN_ID" "Claude" "Codex" ".ai/runs/$RUN_ID/09-claude-review-evaluation.md" "Validate rejected findings"
  ai_state_set "$RUN_ID" "CODE_REVIEW_BY_CODEX"
  invoke_codex "$RUN_ID" "Review" "validate rejected code-review findings" \
    "$RUN_DIR/10-codex-review-validation.md" \
    "$PROMPTS/codex-code-review.md" \
    "$RUN_DIR/08-codex-code-review.md" \
    "$RUN_DIR/09-claude-review-evaluation.md" \
    "$RUN_DIR/disputes.md"
  ai_register_artifact "$RUN_ID" "10-codex-review-validation.md" "Codex" "Review" ".ai/runs/$RUN_ID/10-codex-review-validation.md"

  # POSITIVE-EVIDENCE rule: a dispute opens only on an explicit
  # `DISPUTE_STATUS: OPEN` or named UPHELD finding ids — a missing marker
  # must not fabricate a dispute (see ai_dispute_status).
  DISPUTE_STATUS="$(ai_dispute_status "$RUN_DIR/10-codex-review-validation.md")"
  ai_status "$RUN_ID" "Codex validation: dispute status $DISPUTE_STATUS"

  if [ "$DISPUTE_STATUS" = "OPEN" ]; then
    log_warn "Unresolved disagreement on the IMPLEMENTATION. Opening dispute."
    "$SCRIPT_DIR/dispute-phase.sh" "$RUN_ID" "CODE" \
      "$RUN_DIR/10-codex-review-validation.md"
    exit 3   # exit code 3 == stopped for user decision
  fi
fi

# --- Clean: ready for the user ---------------------------------------------
ai_transfer "$RUN_ID" "Codex" "User" "docs/ai-plans/codex-code-review.md" "Findings resolved; ready for user approval"
ai_state_set "$RUN_ID" "READY_FOR_APPROVAL"
ai_status "$RUN_ID" "Ready for user approval"
