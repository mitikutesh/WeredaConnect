#!/usr/bin/env bash
# scripts/plan-phase.sh
# Phase 1 of the Claude <-> Codex pipeline: PLANNING.
#
#   Claude  drafts the plan + self-reviews        (PLAN_DRAFTING, PLAN_SELF_REVIEW)
#   Codex   independently reviews the plan          (PLAN_REVIEW_BY_CODEX)
#   Claude  evaluates findings (ACCEPT/REJECT)      (PLAN_REVISION)
#   Codex   validates Claude's REJECTED findings    (PLAN_REVIEW_BY_CODEX)
#   Claude  finalizes the plan                      (PLAN_APPROVED)
#
# If any rejection is upheld by Codex -> a dispute is opened, the workflow
# stops, and the user must decide. See .ai/orchestrator/state-machine.md.
#
# Usage: scripts/plan-phase.sh <run_id>
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=lib/ai-common.sh
source "$SCRIPT_DIR/lib/ai-common.sh"

RUN_ID="${1:?Usage: plan-phase.sh <run_id>}"
RUN_DIR="$(ai_run_dir "$RUN_ID")"
PROMPTS="$(ai_prompts_dir)"
[ -d "$RUN_DIR" ] || die "Unknown run id: $RUN_ID (run dir missing)"

log_step "PLAN PHASE — run $RUN_ID"
ai_status "$RUN_ID" "Planning phase started"

# --- Claude drafts the plan ------------------------------------------------
# disputes.md is included when present (e.g. on --resume after a PLAN dispute)
# so the user's recorded decision shapes the re-draft. No-op otherwise.
ai_state_set "$RUN_ID" "PLAN_DRAFTING"
ai_status "$RUN_ID" "Claude started planning"
invoke_claude "$RUN_ID" "Planning" "draft plan" \
  "$RUN_DIR/01-draft-plan.md" "plan" \
  "$PROMPTS/claude-planning.md" \
  "$RUN_DIR/task.md" \
  "$RUN_DIR/disputes.md"
ai_mirror "$RUN_ID" "01-draft-plan.md" "draft-plan.md"
ai_register_artifact "$RUN_ID" "draft-plan.md" "Claude" "Planning" "docs/ai-plans/draft-plan.md"
ai_state_set "$RUN_ID" "PLAN_SELF_REVIEW"
ai_status "$RUN_ID" "Draft plan created (incl. self-review)"

# --- Hand off to Codex for review ------------------------------------------
ai_transfer "$RUN_ID" "Claude" "Codex" "docs/ai-plans/draft-plan.md" "Plan review required"
ai_state_set "$RUN_ID" "PLAN_REVIEW_BY_CODEX"
invoke_codex "$RUN_ID" "Planning" "review plan" \
  "$RUN_DIR/02-codex-plan-review.md" \
  "$PROMPTS/codex-plan-review.md" \
  "$RUN_DIR/task.md" \
  "$RUN_DIR/01-draft-plan.md"
ai_mirror "$RUN_ID" "02-codex-plan-review.md" "codex-plan-review.md"
ai_register_artifact "$RUN_ID" "codex-plan-review.md" "Codex" "Planning" "docs/ai-plans/codex-plan-review.md"

VERDICT="$(ai_marker VERDICT "$RUN_DIR/02-codex-plan-review.md")"
ai_status "$RUN_ID" "Codex plan review complete: ${VERDICT:-<none>}"

# --- Fast path: clean approval --------------------------------------------
if [ "$VERDICT" = "APPROVED" ]; then
  ai_decision "$RUN_ID" "Codex APPROVED the plan (no findings)"
  ai_transfer "$RUN_ID" "Codex" "Claude" "docs/ai-plans/final-plan.md" "Plan approved; finalize"
  ai_state_set "$RUN_ID" "PLAN_REVISION"
  invoke_claude "$RUN_ID" "Planning" "finalize plan (clean approval)" \
    "$RUN_DIR/05-final-plan.md" "plan" \
    "$PROMPTS/claude-planning.md" \
    "$RUN_DIR/task.md" \
    "$RUN_DIR/01-draft-plan.md" \
    "$RUN_DIR/02-codex-plan-review.md" \
    "$RUN_DIR/disputes.md"
  ai_mirror "$RUN_ID" "05-final-plan.md" "final-plan.md"
  ai_register_artifact "$RUN_ID" "final-plan.md" "Claude" "Planning" "docs/ai-plans/final-plan.md"
  ai_state_set "$RUN_ID" "PLAN_APPROVED"
  ai_status "$RUN_ID" "Plan finalized and APPROVED"
  exit 0
fi

# --- Claude evaluates findings (accept/reject) -----------------------------
ai_transfer "$RUN_ID" "Codex" "Claude" "docs/ai-plans/codex-plan-review.md" "Findings to evaluate (${VERDICT:-<none>})"
ai_state_set "$RUN_ID" "PLAN_REVISION"
ai_status "$RUN_ID" "Claude evaluating Codex plan findings"
invoke_claude "$RUN_ID" "Planning" "evaluate plan findings" \
  "$RUN_DIR/03-claude-plan-evaluation.md" "plan" \
  "$PROMPTS/claude-review-response.md" \
  "$RUN_DIR/task.md" \
  "$RUN_DIR/01-draft-plan.md" \
  "$RUN_DIR/02-codex-plan-review.md"
ai_register_artifact "$RUN_ID" "03-claude-plan-evaluation.md" "Claude" "Planning" ".ai/runs/$RUN_ID/03-claude-plan-evaluation.md"

EVAL_STATUS="$(ai_marker EVALUATION_STATUS "$RUN_DIR/03-claude-plan-evaluation.md")"
ai_decision "$RUN_ID" "Claude plan evaluation: ${EVAL_STATUS:-<none>}"

# --- Codex validates rejected findings (only if rejections pending) --------
if [ "$EVAL_STATUS" = "REJECTIONS_PENDING" ]; then
  ai_transfer "$RUN_ID" "Claude" "Codex" ".ai/runs/$RUN_ID/03-claude-plan-evaluation.md" "Validate rejected findings"
  ai_state_set "$RUN_ID" "PLAN_REVIEW_BY_CODEX"
  invoke_codex "$RUN_ID" "Planning" "validate rejected plan findings" \
    "$RUN_DIR/04-codex-plan-validation.md" \
    "$PROMPTS/codex-plan-review.md" \
    "$RUN_DIR/02-codex-plan-review.md" \
    "$RUN_DIR/03-claude-plan-evaluation.md"
  ai_register_artifact "$RUN_ID" "04-codex-plan-validation.md" "Codex" "Planning" ".ai/runs/$RUN_ID/04-codex-plan-validation.md"

  DISPUTE_STATUS="$(ai_marker DISPUTE_STATUS "$RUN_DIR/04-codex-plan-validation.md")"
  ai_status "$RUN_ID" "Codex validation: dispute status ${DISPUTE_STATUS:-<none>}"

  if [ "$DISPUTE_STATUS" = "OPEN" ]; then
    log_warn "Unresolved disagreement on the PLAN. Opening dispute."
    "$SCRIPT_DIR/dispute-phase.sh" "$RUN_ID" "PLAN" \
      "$RUN_DIR/04-codex-plan-validation.md"
    exit 3   # exit code 3 == stopped for user decision
  fi
fi

# --- Claude finalizes the plan ---------------------------------------------
ai_transfer "$RUN_ID" "Codex" "Claude" "docs/ai-plans/final-plan.md" "Finalize plan (findings resolved)"
ai_state_set "$RUN_ID" "PLAN_REVISION"
invoke_claude "$RUN_ID" "Planning" "finalize plan" \
  "$RUN_DIR/05-final-plan.md" "plan" \
  "$PROMPTS/claude-planning.md" \
  "$RUN_DIR/task.md" \
  "$RUN_DIR/01-draft-plan.md" \
  "$RUN_DIR/02-codex-plan-review.md" \
  "$RUN_DIR/03-claude-plan-evaluation.md" \
  "$RUN_DIR/disputes.md"
ai_mirror "$RUN_ID" "05-final-plan.md" "final-plan.md"
ai_register_artifact "$RUN_ID" "final-plan.md" "Claude" "Planning" "docs/ai-plans/final-plan.md"
ai_state_set "$RUN_ID" "PLAN_APPROVED"
ai_status "$RUN_ID" "Plan finalized and APPROVED"
