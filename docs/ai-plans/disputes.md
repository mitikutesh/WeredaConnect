<!-- This file is mirrored from .ai/runs/<run_id>/disputes.md when a dispute is -->
<!-- opened. Unlike the other files here, the USER edits it to record decisions. -->

# Disputes (none open)

No unresolved Claude ↔ Codex disputes have been recorded.

A dispute is opened automatically when **Claude rejects a Codex finding** and
**Codex upholds it** on re-validation (a genuine, unresolved disagreement). When
that happens:

1. The workflow **stops** — state becomes `USER_DECISION_REQUIRED`.
2. A dispute record (see `.ai/templates/dispute.md`) is appended here and to the
   run archive `.ai/runs/<run_id>/disputes.md`.
3. **You (the user) are the final authority.** Record your decision in the
   dispute record.
4. Resume with `scripts/ai-orchestrator.sh --resume <run_id>`.

See `.ai/protocol.md` → *Dispute Resolution* for the full process.
