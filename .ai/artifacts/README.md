# `.ai/artifacts/` — Artifact Index

`index.json` is a running catalogue of every artifact the Claude ↔ Codex
pipeline produces, across all runs. It answers: *what was produced, by whom,
when, and in which phase?*

The orchestrator updates it automatically via `ai_register_artifact`
(`scripts/lib/ai-common.sh`) whenever an agent emits an artifact.

## Schema

```json
{
  "artifacts": [
    {
      "name":        "draft-plan.md",
      "path":        "docs/ai-plans/draft-plan.md",
      "creator":     "Claude",
      "phase":       "Planning",
      "run_id":      "20260609-160707-add-phone-validation",
      "created_at":  "2026-06-09T13:08:01Z",
      "modified_at": "2026-06-09T13:08:01Z"
    }
  ]
}
```

| Field | Meaning |
|-------|---------|
| `name` | Artifact filename |
| `path` | Repo-relative path (canonical `docs/ai-plans/` copy or the run-dir copy) |
| `creator` | Agent that produced it: Claude / Codex / User / Orchestrator |
| `phase` | Workflow phase at creation/last update |
| `run_id` | Run that last touched it |
| `created_at` | First time it was registered (UTC) |
| `modified_at` | Last time it was registered (UTC) |

## Tracked artifacts (per run)

`task.md`, `draft-plan.md`, `codex-plan-review.md`, `03-claude-plan-evaluation.md`,
`04-codex-plan-validation.md`, `final-plan.md`, `06-execution-summary.md`,
`07-implementation.diff`, `codex-code-review.md`, `09-claude-review-evaluation.md`,
`10-codex-review-validation.md`, `disputes.md`.

## Query examples

```bash
jq '.artifacts[] | select(.creator=="Codex")' .ai/artifacts/index.json
jq -r '.artifacts[] | "\(.modified_at)  \(.creator)  \(.path)"' .ai/artifacts/index.json
```
