# Orchestrator Workflow

How `scripts/ai-orchestrator.sh` drives a task from request to
`READY_FOR_APPROVAL`, what each phase script does, and how the **control room**
(`scripts/ai-dashboard.sh`) makes every step observable. Pair with
`.ai/orchestrator/state-machine.md` (states) and `.ai/protocol.md` (rules).

## Actors

| Actor | Role | Tooling |
|-------|------|---------|
| **Claude** | Lead Planner + Lead Executor | `claude -p` (plan = read-only; execution = `acceptEdits`) |
| **Codex** | Independent Reviewer + Validator | `codex exec --sandbox read-only` |
| **User** | Final authority | resolves disputes, approves/ships |

## Scripts

```
scripts/
├── ai-orchestrator.sh   # entry point + state-machine driver + summary
├── ai-dashboard.sh      # tmux control room (4 panes) + live status renderer
├── plan-phase.sh        # Claude draft → Codex review → Claude eval → Codex validate → finalize
├── execution-phase.sh   # Claude implement (acceptEdits) → capture git diff
├── review-phase.sh      # Codex review → Claude eval+fix → Codex validate → ready
├── dispute-phase.sh     # record an unresolved disagreement, stop for the user
└── lib/ai-common.sh     # observability core: state, events, logs, transfers, artifacts, CLI wrappers
```

## End-to-end sequence

```
ai-orchestrator.sh "task"
  • create_run: .ai/runs/<run_id>/, task.md, state.json, logs, events.jsonl,
    .ai/runs/latest symlink, .ai/state/current-state.json, artifacts index
  • state = TASK_CREATED
        │
        ▼
 plan-phase.sh
  PLAN_DRAFTING → (Claude) 01-draft-plan.md → PLAN_SELF_REVIEW
  ─ transfer Claude→Codex ─► PLAN_REVIEW_BY_CODEX → (Codex) 02-codex-plan-review.md [VERDICT]
  if VERDICT≠APPROVED:
     ─ transfer Codex→Claude ─► PLAN_REVISION → 03-claude-plan-evaluation.md [EVALUATION_STATUS]
     if REJECTIONS_PENDING:
        ─ transfer Claude→Codex ─► 04-codex-plan-validation.md [DISPUTE_STATUS]
        if OPEN → dispute-phase.sh PLAN → STOP (exit 3)
  PLAN_REVISION → 05-final-plan.md → PLAN_APPROVED        (skipped onward if --plan-only)
        │
        ▼
 execution-phase.sh
  ─ transfer Orchestrator→Claude ─► IMPLEMENTATION_IN_PROGRESS
  (Claude, acceptEdits) → 06-execution-summary.md
  capture working-tree diff → 07-implementation.diff → IMPLEMENTATION_SELF_REVIEW
        │
        ▼
 review-phase.sh
  ─ transfer Claude→Codex ─► CODE_REVIEW_BY_CODEX → (Codex) 08-codex-code-review.md [VERDICT]
  if VERDICT≠APPROVED:
     ─ transfer Codex→Claude ─► REVIEW_RESOLUTION → 09-claude-review-evaluation.md [EVALUATION_STATUS]
     if REJECTIONS_PENDING:
        ─ transfer Claude→Codex ─► 10-codex-review-validation.md [DISPUTE_STATUS]
        if OPEN → dispute-phase.sh CODE → STOP (exit 3)
  READY_FOR_APPROVAL
        │
        ▼
 ai-orchestrator.sh prints FINAL SUMMARY and STOPS.
 User reviews the diff and merges/ships manually; `--complete` marks COMPLETED.
```

## Observability (the control room)

Every step writes to the per-run archive `.ai/runs/<run_id>/` (immutable) and
the live `.ai/state/current-state.json`. Reachable via the `latest` symlink.

### Structured logs (in the run dir)

| File | Contents |
|------|----------|
| `status.log` | High-level milestones — `[HH:MM] message` |
| `transfers.log` | Explicit agent ownership handoffs (FROM/TO/Artifact/Reason) |
| `decisions.log` | Accepted/rejected review outcomes |
| `disputes.log` | Active disagreements |
| `claude.log` | Complete Claude output (per invocation) |
| `codex.log` | Complete Codex output (per invocation) |
| `events.jsonl` | The canonical structured event stream (one JSON object/line) |

### Event schema (`events.jsonl`)

```json
{"timestamp":"2026-06-10T06:07:28Z","agent":"Claude","event":"ARTIFACT_CREATED",
 "phase":"Planning","artifact":"docs/ai-plans/draft-plan.md","summary":"Produced draft-plan.md"}
```

Event types: `TASK_CREATED`, `STATE_CHANGED`, `AGENT_INVOKED`, `AGENT_COMPLETED`,
`AGENT_FAILED`, `TRANSFER`, `DECISION`, `ARTIFACT_CREATED`, `DISPUTE_OPENED`.

### The dashboard

```
┌──────────────────────────────┬──────────────────────────────┐
│ Orchestrator                 │ Live Status                  │
│  controller log (status.log) │  agent · phase · state       │
│  current state / phase       │  last transfer · decision     │
│                              │  recent events · ⚖ disputes   │
├──────────────────────────────┼──────────────────────────────┤
│ Claude Output (claude.log)   │ Codex Output (codex.log)     │
└──────────────────────────────┴──────────────────────────────┘
```

```bash
scripts/ai-dashboard.sh "Implement feature X"   # prepare a run + watch it live
scripts/ai-dashboard.sh                          # monitor the latest run
```

When given a task the dashboard calls `ai-orchestrator.sh --prepare` (creates the
run at `TASK_CREATED` without running phases, prints the run id), wires the four
panes to `.ai/runs/latest/*.log` + the live renderer, then runs
`--resume <run_id>` in the Orchestrator pane.

## Stop conditions (the orchestrator halts)

1. **Plan dispute** — `DISPUTE_STATUS: OPEN` after Codex validates the plan.
2. **Code dispute** — `DISPUTE_STATUS: OPEN` after Codex validates the code.
3. **Ready for approval** — clean completion; user merges/ships.
4. **Hard error** — marks the run `FAILED` and aborts (exit ≠ 0/3).

## What the orchestrator will NEVER do

`ai-orchestrator.sh` and every phase script are forbidden from `git commit`,
`git push`, merging, deploying, or releasing. `execution-phase.sh` deliberately
leaves changes **uncommitted**; `ai_assert_no_forbidden_action` is a tripwire.

## Commands

```bash
# Full pipeline (with or without the dashboard)
scripts/ai-orchestrator.sh "Add phone-number validation to login"
scripts/ai-dashboard.sh   "Add phone-number validation to login"

# From a task file / plan-only / resume / complete
scripts/ai-orchestrator.sh --task-file specs/features/F-XX.md
scripts/ai-orchestrator.sh --plan-only "Refactor sync queue retry policy"
scripts/ai-orchestrator.sh --resume   <run_id>
scripts/ai-orchestrator.sh --complete <run_id>

# Inspect / observe
scripts/ai-orchestrator.sh --list
scripts/ai-orchestrator.sh --status <run_id>
scripts/ai-orchestrator.sh --events <run_id>
scripts/ai-dashboard.sh                          # monitor latest run

# Smoke-test the control flow without invoking any model
scripts/ai-orchestrator.sh --dry-run "anything"
```

## Configuration (environment)

| Var | Default | Purpose |
|-----|---------|---------|
| `CLAUDE_BIN` | `claude` | Claude CLI command |
| `CODEX_BIN` | `codex` | Codex CLI command |
| `CLAUDE_MODEL` | _(CLI default)_ | model alias for Claude |
| `CODEX_MODEL` | _(CLI default)_ | model for Codex |
| `AI_DRY_RUN` | `0` | `1` = placeholder artifacts, never call a model |
| `AI_MAX_ATTEMPTS` | `2` | attempts per agent invocation (garbage/CLI-failure retry) |
| `AI_DASH_ONCE` | `0` | `1` = render one status frame and exit (tests) |
| `AI_DASH_NO_ATTACH` | `0` | `1` = build the tmux session but don't attach (tests) |

## Concurrency — one run at a time

The pipeline shares `.ai/runs/latest` and `.ai/state/current-state.json`, so
`run`/`--resume` take an atomic lock (`.ai/state/orchestrator.lock`). A second
invocation while a run is active **refuses to start** and prints the active
run id, its pid, and how to watch (`ai-dashboard.sh --status-view`) or stop it.
The lock releases on exit (including dispute stops) and stale locks (dead pid)
clear automatically. Read-only modes (`--list/--status/--events`) never lock.
