# `.ai/runs/` — Orchestration Run Archive

Each Claude ↔ Codex orchestration run gets an **immutable, per-run directory**
here, created by `scripts/ai-orchestrator.sh`. This is the audit trail: every
artifact every agent produced, in order, plus the state-machine history.

The latest run's key artifacts are also **mirrored** to `docs/ai-plans/` under
stable filenames for easy human browsing — but `docs/ai-plans/` is overwritten
each run, whereas everything here is kept.

## Run directory layout

```
.ai/runs/<YYYYMMDD-HHMMSS>-<task-slug>/
├── task.md                       # the input task (canonical)
├── state.json                    # state-machine state + history (see below)
├── base-ref.txt                  # git sha the implementation diff is based on
│
├── 01-draft-plan.md              # Claude — draft plan + self-review
├── 02-codex-plan-review.md       # Codex  — plan review (findings + VERDICT)
├── 03-claude-plan-evaluation.md  # Claude — accept/reject each finding
├── 04-codex-plan-validation.md   # Codex  — validates rejected findings (if any)
├── 05-final-plan.md              # Claude — finalized plan
│
├── 06-execution-summary.md       # Claude — implementation summary
├── 07-implementation.diff        # working-tree diff vs base-ref
│
├── 08-codex-code-review.md       # Codex  — code review (findings + VERDICT)
├── 09-claude-review-evaluation.md# Claude — accept/reject + fixes
├── 10-codex-review-validation.md # Codex  — validates rejected findings (if any)
│
├── disputes.md                   # opened only if a disagreement is unresolved
│
│   ── observability (control room) ──
├── events.jsonl                  # canonical structured event stream
├── status.log                    # high-level milestones [HH:MM]
├── transfers.log                 # explicit agent ownership handoffs
├── decisions.log                 # accepted/rejected review outcomes
├── disputes.log                  # active disagreements
├── claude.log                    # complete Claude output (per invocation)
└── codex.log                     # complete Codex output (per invocation)
```

The active run is also reachable via the `.ai/runs/latest` symlink (gitignored;
the symlink target — the real run dir — is the committed audit copy).

Not every file appears in every run: the `03/04` and `09/10` evaluation/
validation artifacts are produced only when a review raises findings, and
`disputes.md` only when a rejection is upheld.

## `state.json`

```json
{
  "run_id": "20260610-090727-add-phone-validation",
  "task": "…",
  "state": "READY_FOR_APPROVAL",
  "phase": "Approval",
  "active_agent": "User",
  "dispute_open": false,
  "resume_from": "",
  "created_at": "…",
  "updated_at": "…",
  "history": [ { "state": "TASK_CREATED", "at": "…" }, … ]
}
```

States and transitions are documented in
`.ai/orchestrator/state-machine.md`.

## Housekeeping

Runs accumulate. They are intentionally committed for auditability, but you may
prune old runs once their work has shipped:

```bash
# inspect
scripts/ai-orchestrator.sh --list
scripts/ai-orchestrator.sh --status <run_id>

# prune a finished run
rm -rf .ai/runs/<run_id>
```
