# Orchestration State Machine

The Claude ↔ Codex pipeline is a deterministic state machine. Each run owns a
`state.json` (audit) in `.ai/runs/<run_id>/` and continuously mirrors a live
`.ai/state/current-state.json` (the dashboard's data source). The phase scripts
read the current state and advance it through the single choke-point
`ai_state_set` (`scripts/lib/ai-common.sh`), which also derives the **phase** and
**active agent**, emits a `STATE_CHANGED` event, and updates the live view.

## States

| State | Phase | Active agent | Meaning |
|-------|-------|--------------|---------|
| `TASK_CREATED` | Intake | Orchestrator | Run created, task recorded |
| `PLAN_DRAFTING` | Planning | Claude | Claude is writing the draft plan |
| `PLAN_SELF_REVIEW` | Planning | Claude | Claude self-reviewed the draft |
| `PLAN_REVIEW_BY_CODEX` | Planning | Codex | Codex is reviewing the plan |
| `PLAN_REVISION` | Planning | Claude | Claude evaluating findings / finalizing |
| `PLAN_APPROVED` | Planning | Orchestrator | Plan finalized; no open plan dispute |
| `IMPLEMENTATION_IN_PROGRESS` | Execution | Claude | Claude is implementing the plan |
| `IMPLEMENTATION_SELF_REVIEW` | Execution | Claude | Implemented; diff captured |
| `CODE_REVIEW_BY_CODEX` | Review | Codex | Codex is reviewing the diff |
| `REVIEW_RESOLUTION` | Review | Claude | Claude evaluating/fixing findings |
| `DISPUTE_OPEN` | Dispute | User | An unresolved disagreement exists |
| `USER_DECISION_REQUIRED` | Dispute | User | Stopped; the user must decide |
| `READY_FOR_APPROVAL` | Approval | User | Clean; awaiting the user to merge/ship |
| `COMPLETED` | Done | User | User approved & marked the run done |
| `FAILED` | Failed | Orchestrator | A hard error aborted the run |

> `IDLE` appears only in `current-state.json` as a dashboard baseline when no run
> has started. It is not a pipeline state.

## Happy path

```
TASK_CREATED
   │  Claude drafts
   ▼
PLAN_DRAFTING ─► PLAN_SELF_REVIEW
   │  transfer → Codex
   ▼
PLAN_REVIEW_BY_CODEX
   │  transfer → Claude; evaluate + (Codex validates rejects) + finalize
   ▼
PLAN_REVISION ─► PLAN_APPROVED            ← (--plan-only stops here)
   │  implement (acceptEdits) + capture diff
   ▼
IMPLEMENTATION_IN_PROGRESS ─► IMPLEMENTATION_SELF_REVIEW
   │  transfer → Codex
   ▼
CODE_REVIEW_BY_CODEX
   │  transfer → Claude; evaluate/fix + (Codex validates rejects)
   ▼
REVIEW_RESOLUTION ─► READY_FOR_APPROVAL   ← orchestrator STOPS. User ships.
                          │  user: --complete
                          ▼
                     COMPLETED
```

## Dispute path

Reachable from `PLAN_REVIEW_BY_CODEX` (plan validation) or `CODE_REVIEW_BY_CODEX`
(code validation) whenever **Claude rejects a finding and Codex upholds it**
(`DISPUTE_STATUS: OPEN`).

```
PLAN_REVIEW_BY_CODEX ─┐
                       ├─► DISPUTE_OPEN ─► USER_DECISION_REQUIRED   (STOP, exit 3)
CODE_REVIEW_BY_CODEX ─┘                          │
                                                 │ user records decision in
                                                 │ docs/ai-plans/disputes.md, then
                                                 │ `--resume <run_id>`
                                                 ▼
                       re-enter PLAN_DRAFTING (plan) or
                       IMPLEMENTATION_SELF_REVIEW (code) and continue
```

`state.json` records `resume_from = PLAN | CODE` so `--resume` knows which phase
to re-run after the user decides.

## Failure path

Any hard error (CLI failure, missing artifact, phase exit ≠ 0/3) →
`ai_mark_failed` sets **FAILED** and logs the reason. The run is inspectable via
`--status` / `--events` and can be re-driven with `--resume` after the cause is
fixed.

## Verdict / status markers (machine-readable contract)

Agents emit single-line markers parsed by `ai_marker` (last occurrence wins):

| Marker | Emitted by | Values |
|--------|-----------|--------|
| `VERDICT:` | Codex review | `APPROVED` · `APPROVED_WITH_COMMENTS` · `CHANGES_REQUESTED` |
| `EVALUATION_STATUS:` | Claude evaluation | `ALL_RESOLVED` · `REJECTIONS_PENDING` |
| `DISPUTE_STATUS:` | Codex validation | `NONE` · `OPEN` |
| `UPHELD:` | Codex validation | comma-separated finding ids (may be empty) |

### Transition logic per review round

```
VERDICT == APPROVED                 → finalize/advance (no evaluation needed)
VERDICT == APPROVED_WITH_COMMENTS    ┐
VERDICT == CHANGES_REQUESTED         ┘→ Claude evaluation
    EVALUATION_STATUS == ALL_RESOLVED        → finalize/advance
    EVALUATION_STATUS == REJECTIONS_PENDING  → Codex validation
        DISPUTE_STATUS == NONE  → finalize/advance
        DISPUTE_STATUS == OPEN  → dispute-phase → STOP
```

## `state.json` schema (per-run audit)

```json
{
  "run_id":       "20260610-090727-add-phone-validation",
  "task":         "Add phone-number validation to login",
  "state":        "READY_FOR_APPROVAL",
  "phase":        "Approval",
  "active_agent": "User",
  "dispute_open": false,
  "resume_from":  "",
  "created_at":   "2026-06-10T09:07:27+0300",
  "updated_at":   "2026-06-10T09:21:44+0300",
  "history": [
    { "state": "TASK_CREATED",               "at": "…" },
    { "state": "PLAN_DRAFTING",              "at": "…" },
    { "state": "PLAN_SELF_REVIEW",           "at": "…" },
    { "state": "PLAN_REVIEW_BY_CODEX",       "at": "…" },
    { "state": "PLAN_REVISION",              "at": "…" },
    { "state": "PLAN_APPROVED",              "at": "…" },
    { "state": "IMPLEMENTATION_IN_PROGRESS", "at": "…" },
    { "state": "IMPLEMENTATION_SELF_REVIEW", "at": "…" },
    { "state": "CODE_REVIEW_BY_CODEX",       "at": "…" },
    { "state": "REVIEW_RESOLUTION",          "at": "…" },
    { "state": "READY_FOR_APPROVAL",         "at": "…" }
  ]
}
```

The live `.ai/state/current-state.json` is documented in `.ai/state/README.md`.

## Terminal states

- `READY_FOR_APPROVAL` — success; control returns to the user. The orchestrator
  never proceeds past this (no merge/push/deploy/release).
- `COMPLETED` — user-marked done (`--complete <run_id>`).
- `USER_DECISION_REQUIRED` — paused for a human ruling; resumable.
- `FAILED` — hard error; resumable after fixing the cause.
- `PLAN_APPROVED` is terminal **only** under `--plan-only`.

## Exit codes (orchestrator & phase scripts)

| Code | Meaning |
|------|---------|
| `0` | Phase/run completed (reached its target state) |
| `2` | Usage error (bad arguments) |
| `3` | Stopped for a user decision (dispute opened) |
| other | Hard failure — run marked FAILED, aborted |
