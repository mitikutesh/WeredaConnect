#!/usr/bin/env bash
# scripts/execution-phase.sh
# Phase 2 of the Claude <-> Codex pipeline: EXECUTION.
#
#   Claude  implements the APPROVED plan (file edits allowed) + self-reviews
#   Script  captures a git diff of the working tree as the review artifact
#
# Runs only once the plan is PLAN_APPROVED. It performs NO commit, push, merge,
# or deploy — it leaves changes in the working tree for review.
#
# Usage: scripts/execution-phase.sh <run_id>
# State transitions: PLAN_APPROVED -> IMPLEMENTATION_IN_PROGRESS -> IMPLEMENTATION_SELF_REVIEW
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=lib/ai-common.sh
source "$SCRIPT_DIR/lib/ai-common.sh"

RUN_ID="${1:?Usage: execution-phase.sh <run_id>}"
RUN_DIR="$(ai_run_dir "$RUN_ID")"
PROMPTS="$(ai_prompts_dir)"
REPO_ROOT="$(ai_repo_root)"
[ -d "$RUN_DIR" ] || die "Unknown run id: $RUN_ID (run dir missing)"

STATE="$(ai_state_get "$RUN_ID")"
[ "$STATE" = "PLAN_APPROVED" ] || die "Cannot execute: state is '$STATE', expected PLAN_APPROVED."
[ -f "$RUN_DIR/05-final-plan.md" ] || die "Missing finalized plan (05-final-plan.md)."

log_step "EXECUTION PHASE — run $RUN_ID"
ai_status "$RUN_ID" "Execution phase started"

# Record the pre-implementation HEAD so the diff is reproducible/auditable.
BASE_REF="$(git -C "$REPO_ROOT" rev-parse HEAD)"
echo "$BASE_REF" > "$RUN_DIR/base-ref.txt"
log_info "Base ref: $BASE_REF"

# --- Claude implements the finalized plan ----------------------------------
ai_transfer "$RUN_ID" "Orchestrator" "Claude" "docs/ai-plans/final-plan.md" "Implement approved plan"
ai_state_set "$RUN_ID" "IMPLEMENTATION_IN_PROGRESS"
ai_status "$RUN_ID" "Claude started implementation"
# Permission mode for the implementing Claude. Default bypassPermissions: this
# repo path's TRAILING SPACE ("Personal /") trips Claude Code's built-in
# "suspicious Windows path" rule, which blocks Edit/Write even under
# acceptEdits (verified empirically). Safety is preserved by the PreToolUse
# hook tripwire (AI_ORCH_GUARD): commit/push/merge/deploy stay blocked.
# After renaming the folder (remove the trailing space), set
# AI_EXEC_PERMISSION_MODE=acceptEdits.
EXEC_MODE="${AI_EXEC_PERMISSION_MODE:-bypassPermissions}"
invoke_claude "$RUN_ID" "Execution" "implement finalized plan" \
  "$RUN_DIR/06-execution-summary.md" "$EXEC_MODE" \
  "$PROMPTS/claude-execution.md" \
  "$RUN_DIR/task.md" \
  "$RUN_DIR/05-final-plan.md"
ai_register_artifact "$RUN_ID" "06-execution-summary.md" "Claude" "Execution" ".ai/runs/$RUN_ID/06-execution-summary.md"
ai_status "$RUN_ID" "Implementation summary produced"

# --- Capture the working-tree diff for review ------------------------------
log_step "Capture implementation diff"
DIFF_FILE="$RUN_DIR/07-implementation.diff"
{
  echo "# Implementation diff for run $RUN_ID"
  echo "# Base: $BASE_REF"
  echo "# Generated: $(ai_now_iso)"
  echo "# (working tree vs base — includes staged + unstaged + untracked)"
  echo
} > "$DIFF_FILE"

# Tracked changes (staged + unstaged) against the base ref.
git -C "$REPO_ROOT" diff "$BASE_REF" >> "$DIFF_FILE" || true
# Untracked files (added but not yet tracked) — show them too.
while IFS= read -r f; do
  [ -n "$f" ] || continue
  echo "" >> "$DIFF_FILE"
  echo "=== NEW FILE (untracked): $f ===" >> "$DIFF_FILE"
  git -C "$REPO_ROOT" diff --no-index /dev/null "$REPO_ROOT/$f" >> "$DIFF_FILE" 2>/dev/null || true
done < <(git -C "$REPO_ROOT" ls-files --others --exclude-standard)

DIFF_LINES="$(grep -c '^' "$DIFF_FILE" || true)"
ai_register_artifact "$RUN_ID" "07-implementation.diff" "Claude" "Execution" ".ai/runs/$RUN_ID/07-implementation.diff"
ai_state_set "$RUN_ID" "IMPLEMENTATION_SELF_REVIEW"
ai_status "$RUN_ID" "Implementation complete; diff captured ($DIFF_LINES lines)"

if [ "$DIFF_LINES" -le 5 ]; then
  log_warn "Diff looks empty — implementation may not have changed any files."
fi
