# `docs/ai-plans/` — Live AI Collaboration Artifacts

This folder holds the **latest** Claude ↔ Codex orchestration artifacts under
stable, human-friendly filenames. It is the place to look when you want to read
what the agents planned, reviewed, and decided for the most recent run.

> These files are **auto-mirrored** from the per-run archive in
> `.ai/runs/<run_id>/` by `scripts/ai-orchestrator.sh`. Each new run overwrites
> them. Do not hand-edit them (except `disputes.md`, where the **user records
> dispute decisions**). The immutable history lives in `.ai/runs/`.

| File | Produced by | Stage | Source in `.ai/runs/<run_id>/` |
|------|-------------|-------|--------------------------------|
| `draft-plan.md` | Claude | Plan draft | `01-draft-plan.md` |
| `codex-plan-review.md` | Codex | Plan review | `02-codex-plan-review.md` |
| `final-plan.md` | Claude | Plan finalized | `05-final-plan.md` |
| `codex-code-review.md` | Codex | Code review | `08-codex-code-review.md` |
| `disputes.md` | dispute-phase / **user** | Dispute | `disputes.md` |
| `workflow-diagram.md` | (static) | Reference | — (visual reference for the whole pipeline) |

## Roles

- **Claude** — Lead Planner & Lead Executor (drafts the plan, implements it,
  responds to reviews).
- **Codex** — Independent Reviewer & Validator (reviews the plan and the code,
  re-validates rejected findings).
- **User** — Final authority. Resolves any dispute recorded in `disputes.md`.

## How these are generated

```bash
# Full pipeline with the live control room (recommended)
scripts/ai-dashboard.sh "Your development task here"

# Full pipeline without the dashboard
scripts/ai-orchestrator.sh "Your development task here"

# Planning only (stop after the plan is approved)
scripts/ai-orchestrator.sh --plan-only "Your task"

# Resume after resolving a dispute or to continue a stopped run
scripts/ai-orchestrator.sh --resume <run_id>

# Observe an in-flight or finished run
scripts/ai-dashboard.sh                       # monitor the latest run
scripts/ai-orchestrator.sh --events <run_id>  # text event stream
```

See `.ai/protocol.md` for the full collaboration protocol,
`.ai/orchestrator/state-machine.md` for the states, and `workflow-diagram.md`
for the visual flow.

## What the orchestrator will NEVER do

It does not `git commit`, `git push`, merge, deploy, or release. It stops at
`READY_FOR_APPROVAL`; shipping is always the user's explicit action.
