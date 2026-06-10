#!/usr/bin/env bash
# scripts/dispute-phase.sh
# Dispute handler for the Claude <-> Codex pipeline.
#
# Invoked by plan-phase.sh / review-phase.sh when Claude rejects a finding and
# Codex upholds it (a genuine, unresolved disagreement). It:
#   1. Records an explicit handoff to the USER (transfers.log + event)
#   2. Renders a dispute record (see .ai/templates/dispute.md) into disputes.md
#   3. Logs the dispute (disputes.log) and emits a DISPUTE_OPENED event
#   4. Sets state -> DISPUTE_OPEN -> USER_DECISION_REQUIRED
#   5. Stops the workflow. The USER is the final authority.
#
# Usage: scripts/dispute-phase.sh <run_id> <PLAN|CODE> <validation_artifact>
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=lib/ai-common.sh
source "$SCRIPT_DIR/lib/ai-common.sh"

RUN_ID="${1:?Usage: dispute-phase.sh <run_id> <PLAN|CODE> <validation_artifact>}"
SCOPE="${2:?Missing scope (PLAN|CODE)}"
VALIDATION="${3:?Missing validation artifact path}"
RUN_DIR="$(ai_run_dir "$RUN_ID")"
[ -d "$RUN_DIR" ] || die "Unknown run id: $RUN_ID (run dir missing)"

log_step "DISPUTE — run $RUN_ID (scope: $SCOPE)"

DISPUTES_FILE="$RUN_DIR/disputes.md"
TS="$(ai_now_iso)"

# Initialise the disputes record once per run.
if [ ! -f "$DISPUTES_FILE" ]; then
  {
    echo "# Disputes — run $RUN_ID"
    echo
    echo "Unresolved Claude ↔ Codex disagreements. **The user is the final authority.**"
    echo "Record the decision under each dispute, then resume with:"
    echo '`scripts/ai-orchestrator.sh --resume '"$RUN_ID"'`'
    echo
  } > "$DISPUTES_FILE"
fi

# Pull the upheld finding ids Codex reported, if any (e.g. "UPHELD: F3, F7").
UPHELD="$(ai_marker UPHELD "$VALIDATION")"
[ -n "$UPHELD" ] || UPHELD="(see validation artifact)"

DISPUTE_ID="D-$(date '+%Y%m%d-%H%M%S')"
{
  echo "## $DISPUTE_ID — $SCOPE dispute"
  echo
  echo "- **Opened:** $TS"
  echo "- **Scope:** $SCOPE"
  echo "- **Upheld findings (Codex):** $UPHELD"
  echo "- **State:** USER_DECISION_REQUIRED"
  echo
  echo "### Issue"
  echo "Claude rejected one or more Codex findings; Codex upheld them on re-validation."
  echo
  echo "### Claude position"
  echo "_Rejected the finding(s); see the evaluation artifact in the run dir._"
  echo
  echo "### Codex position (upheld)"
  echo '```'
  sed -n '1,200p' "$VALIDATION"
  echo '```'
  echo
  echo "### Recommendation"
  echo "Default to the safer course: for Critical/Important findings touching role"
  echo "visibility, platform pairs, security, or the core flow, prefer Codex unless"
  echo "you can confirm Claude's rebuttal is correct."
  echo
  echo "### ⚖️  User decision (REQUIRED — fill this in)"
  echo "- [ ] Side with **Claude** (proceed; finding waived)"
  echo "- [ ] Side with **Codex** (Claude must address the finding)"
  echo "- [ ] **Other / compromise:** _______________________________"
  echo
  echo "**Decision rationale:**"
  echo
  echo "**Decided by:** ____________   **On:** ____________"
  echo
  echo "---"
  echo
} >> "$DISPUTES_FILE"

ai_mirror "$RUN_ID" "disputes.md" "disputes.md"
ai_register_artifact "$RUN_ID" "disputes.md" "Orchestrator" "Dispute" "docs/ai-plans/disputes.md"

# Explicit handoff to the user + structured logging.
ai_transfer "$RUN_ID" "Codex" "User" "docs/ai-plans/disputes.md" "Unresolved $SCOPE disagreement — user decision required"
ai_dispute_log "$RUN_ID" "$DISPUTE_ID ($SCOPE): upheld $UPHELD"

# Remember which phase to re-run after the user resolves the dispute.
ai_state_field "$RUN_ID" "resume_from" "$SCOPE"
ai_state_set "$RUN_ID" "DISPUTE_OPEN" "true"
ai_state_set "$RUN_ID" "USER_DECISION_REQUIRED"

log_warn "Dispute $DISPUTE_ID recorded -> $DISPUTES_FILE"
log_warn "Workflow STOPPED. The user must resolve the dispute before continuing."
log_info "After deciding, resume with: scripts/ai-orchestrator.sh --resume $RUN_ID"
