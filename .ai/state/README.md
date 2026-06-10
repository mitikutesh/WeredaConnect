# `.ai/state/` — Live Orchestration State

This directory holds the **single live snapshot** of the active (or most recent)
Claude ↔ Codex run. It is the data source for the dashboard's *Live Status* pane.

## `current-state.json`

Overwritten continuously by the orchestrator as a run progresses. It always
reflects the **latest** run — for the full, immutable, per-run history see
`.ai/runs/<run_id>/state.json` and `.ai/runs/<run_id>/events.jsonl`.

```json
{
  "run_id":          "20260609-160707-add-phone-validation",
  "task":            "Add phone-number validation to the login screen",
  "state":           "PLAN_REVIEW_BY_CODEX",
  "phase":           "Planning",
  "active_agent":    "Codex",
  "last_transfer":   "Claude → Codex · Plan review required",
  "latest_decision": "None",
  "latest_status":   "Codex plan review in progress",
  "latest_event":    "AGENT_INVOKED (Codex)",
  "dispute_open":    "false",
  "updated_at":      "2026-06-09T16:08:01+0300",
  "run_dir":         ".ai/runs/20260609-160707-add-phone-validation"
}
```

| Field | Meaning |
|-------|---------|
| `run_id` | Active run identifier |
| `task` | The task being worked |
| `state` | Current state-machine state (see `.ai/orchestrator/state-machine.md`) |
| `phase` | Derived phase: Intake / Planning / Execution / Review / Dispute / Approval / Done |
| `active_agent` | Who currently owns the work: Claude / Codex / User / Orchestrator |
| `last_transfer` | Most recent ownership handoff |
| `latest_decision` | Most recent accepted/rejected review outcome |
| `latest_status` | Most recent milestone (mirrors the tail of `status.log`) |
| `latest_event` | Most recent event type (mirrors the tail of `events.jsonl`) |
| `dispute_open` | `"true"` while a dispute awaits a user decision |

> `IDLE` is a dashboard-only baseline used when no run has started. It is not a
> formal pipeline state.

This file is read by `scripts/ai-dashboard.sh --status-view`. Do not hand-edit it.
