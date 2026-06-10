# AI Handoff — Live Pointer

This file is the **single place to look** to know what the Claude ↔ Codex
pipeline is doing right now and what the next action is. It is the resume point
for a human picking the work back up or a fresh session.

> Handoffs in this framework are **file-based**: each agent reads the prior
> artifacts and writes a new one plus a status marker; the orchestrator decides
> the next step deterministically. This file summarizes that state in one place.
> Update the "Current Handoff" block below when starting/finishing a run (or let
> a future automation hook keep it current).

---

## Current Handoff

| Field | Value |
|-------|-------|
| **Active run** | _none_ |
| **State** | `—` |
| **Last actor** | _none_ |
| **Next actor** | _none_ |
| **Next action** | Start a run: `scripts/ai-orchestrator.sh "<task>"` |
| **Dispute open?** | no |
| **Updated** | _—_ |

_To see the actual run state (live):_

```bash
scripts/ai-dashboard.sh                            # live control room (tmux)
scripts/ai-orchestrator.sh --list                  # all runs + states
scripts/ai-orchestrator.sh --status <run_id>       # one run's state + artifacts
scripts/ai-orchestrator.sh --events <run_id>       # the event stream
cat .ai/state/current-state.json                    # the live snapshot
```

---

## Handoff Contract (who hands what to whom)

| From → To | Trigger | Artifact handed over | Status marker |
|-----------|---------|----------------------|---------------|
| User → Claude | new task | `task.md` | — |
| Claude → Codex | plan drafted | `01-draft-plan.md` | (none) |
| Codex → Claude | plan reviewed | `02-codex-plan-review.md` | `VERDICT` |
| Claude → Codex | findings evaluated | `03-claude-plan-evaluation.md` | `EVALUATION_STATUS` |
| Codex → Claude/User | rejections validated | `04-codex-plan-validation.md` | `DISPUTE_STATUS` |
| Claude → orchestrator | plan finalized | `05-final-plan.md` | state `PLAN_APPROVED` |
| Claude → Codex | implemented | `06-execution-summary.md` + `07-implementation.diff` | (none) |
| Codex → Claude | code reviewed | `08-codex-code-review.md` | `VERDICT` |
| Claude → Codex | findings evaluated + fixed | `09-claude-review-evaluation.md` | `EVALUATION_STATUS` |
| Codex → Claude/User | rejections validated | `10-codex-review-validation.md` | `DISPUTE_STATUS` |
| orchestrator → User | clean | run summary | state `READY_FOR_APPROVAL` |
| orchestrator → User | dispute | `disputes.md` | state `USER_DECISION_REQUIRED` |

A handoff is valid only when the producing artifact exists **and** carries its
required status marker. The orchestrator enforces this; missing markers are
treated as a hard error.

---

## When a Run Stops for You (the User)

1. **`READY_FOR_APPROVAL`** — review `07-implementation.diff` and the
   reviews under `docs/ai-plans/`, then commit / open a PR / merge **yourself**.
   The orchestrator will not do this.
2. **`USER_DECISION_REQUIRED`** — open `docs/ai-plans/disputes.md`, record your
   ruling in the dispute record, then `scripts/ai-orchestrator.sh --resume <run_id>`.

---

**See:** `.ai/protocol.md` (rules) · `.ai/orchestrator/workflow.md` (flow) ·
`.ai/orchestrator/state-machine.md` (states).
