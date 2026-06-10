# Claude ↔ Codex Workflow Diagram

Visual reference for the multi-agent pipeline. Pairs with
`.ai/orchestrator/state-machine.md` (states), `.ai/orchestrator/workflow.md`
(script flow), and `.ai/protocol.md` (rules).

Legend: **C** = Claude (Planner/Executor) · **X** = Codex (Reviewer/Validator) ·
**U** = User (final authority) · **O** = Orchestrator.

---

## 1. End-to-end pipeline

```
        U: task
          │
          ▼
   ┌─────────────┐
   │ TASK_CREATED│  O
   └──────┬──────┘
          ▼
 ╔══════════════════ PLANNING ══════════════════╗
 ║  PLAN_DRAFTING ──► PLAN_SELF_REVIEW           ║  C
 ║        │                                       ║
 ║   (transfer C→X)                               ║
 ║        ▼                                       ║
 ║  PLAN_REVIEW_BY_CODEX  ──verdict──►            ║  X
 ║        │                                       ║
 ║   (transfer X→C)                               ║
 ║        ▼                                       ║
 ║  PLAN_REVISION ──► (Codex validates rejects)   ║  C/X
 ║        │                                       ║
 ║        ▼                                       ║
 ║  PLAN_APPROVED ───────────────────────────────╫─► (--plan-only stops here)
 ╚════════════════════════════════════════════════╝
          │
 ╔════════════════ EXECUTION ════════════════════╗
 ║  IMPLEMENTATION_IN_PROGRESS                    ║  C
 ║        ▼                                       ║
 ║  IMPLEMENTATION_SELF_REVIEW  (diff captured)   ║  C
 ╚════════════════════════════════════════════════╝
          │  (transfer C→X)
 ╔══════════════════ REVIEW ═════════════════════╗
 ║  CODE_REVIEW_BY_CODEX  ──verdict──►            ║  X
 ║        │                                       ║
 ║   (transfer X→C)                               ║
 ║        ▼                                       ║
 ║  REVIEW_RESOLUTION ──► (Codex validates rejects)║ C/X
 ╚════════════════════════════════════════════════╝
          │
          ▼
   ┌────────────────────┐
   │ READY_FOR_APPROVAL │  U   ← orchestrator STOPS. User reviews & ships.
   └─────────┬──────────┘
             ▼ (user: --complete)
        ┌───────────┐
        │ COMPLETED │  U
        └───────────┘
```

Hard error at any point → **FAILED**.

---

## 2. Planning flow (detail)

```
C: draft + self-review  ──►  docs/ai-plans/draft-plan.md
        │  TRANSFER C→X "Plan review required"
        ▼
X: review               ──►  docs/ai-plans/codex-plan-review.md   [VERDICT]
        │
        ├─ APPROVED ───────────────────────────────►  C: finalize ► final-plan.md
        │
        └─ CHANGES_REQUESTED
              │  TRANSFER X→C "Findings to evaluate"
              ▼
           C: evaluate (ACCEPT/REJECT)  ►  03-claude-plan-evaluation.md  [EVALUATION_STATUS]
              │
              ├─ ALL_RESOLVED ──────────────────────►  C: finalize ► final-plan.md
              │
              └─ REJECTIONS_PENDING
                    │  TRANSFER C→X "Validate rejected findings"
                    ▼
                 X: validate  ►  04-codex-plan-validation.md  [DISPUTE_STATUS]
                    ├─ NONE ───────────────────────►  C: finalize ► final-plan.md
                    └─ OPEN ───────────────────────►  DISPUTE (see §5)
```

---

## 3. Execution flow (detail)

```
O: PLAN_APPROVED
   │  TRANSFER O→C "Implement approved plan"
   ▼
C: implement (acceptEdits, working tree only)  ►  06-execution-summary.md
   ▼
O: capture working-tree diff vs base-ref       ►  07-implementation.diff
   ▼
IMPLEMENTATION_SELF_REVIEW
```

No commit / push / merge / deploy. Changes stay uncommitted for review.

---

## 4. Review flow (detail)

```
C: IMPLEMENTATION_SELF_REVIEW
   │  TRANSFER C→X "Code review required"
   ▼
X: review diff vs plan  ►  docs/ai-plans/codex-code-review.md  [VERDICT]
   │
   ├─ APPROVED ──────────────────────────────────►  READY_FOR_APPROVAL
   │
   └─ CHANGES_REQUESTED
         │  TRANSFER X→C "Findings to resolve"
         ▼
      C: evaluate + fix accepted  ►  09-claude-review-evaluation.md  [EVALUATION_STATUS]
         │  (diff refreshed)
         ├─ ALL_RESOLVED ─────────────────────────►  READY_FOR_APPROVAL
         └─ REJECTIONS_PENDING
               │  TRANSFER C→X "Validate rejected findings"
               ▼
            X: validate  ►  10-codex-review-validation.md  [DISPUTE_STATUS]
               ├─ NONE ─────────────────────────────►  READY_FOR_APPROVAL
               └─ OPEN ─────────────────────────────►  DISPUTE (see §5)
```

---

## 5. Dispute flow

```
C rejects a Critical/Important finding  +  X upholds it  (DISPUTE_STATUS: OPEN)
        │
        ▼
   DISPUTE_OPEN
        │  TRANSFER X→U "Unresolved disagreement — user decision required"
        │  • dispute record appended to docs/ai-plans/disputes.md
        │  • disputes.log entry + DISPUTE_OPENED event
        ▼
   USER_DECISION_REQUIRED   ── workflow STOPS (exit 3); dashboard shows ⚖ banner
        │
        │  U records a decision in disputes.md, then:
        │  scripts/ai-orchestrator.sh --resume <run_id>
        ▼
   re-enter PLAN_DRAFTING (plan dispute) or IMPLEMENTATION_SELF_REVIEW (code dispute)
```

---

## 6. State-transition table

| From | Event | To |
|------|-------|----|
| TASK_CREATED | start planning | PLAN_DRAFTING |
| PLAN_DRAFTING | draft + self-review done | PLAN_SELF_REVIEW |
| PLAN_SELF_REVIEW | transfer to Codex | PLAN_REVIEW_BY_CODEX |
| PLAN_REVIEW_BY_CODEX | verdict APPROVED / findings | PLAN_REVISION |
| PLAN_REVISION | finalized, no open dispute | PLAN_APPROVED |
| PLAN_REVISION | rejection upheld | DISPUTE_OPEN |
| PLAN_APPROVED | begin implementation | IMPLEMENTATION_IN_PROGRESS |
| IMPLEMENTATION_IN_PROGRESS | code written, diff captured | IMPLEMENTATION_SELF_REVIEW |
| IMPLEMENTATION_SELF_REVIEW | transfer to Codex | CODE_REVIEW_BY_CODEX |
| CODE_REVIEW_BY_CODEX | verdict APPROVED / findings | REVIEW_RESOLUTION |
| REVIEW_RESOLUTION | resolved, no open dispute | READY_FOR_APPROVAL |
| REVIEW_RESOLUTION | rejection upheld | DISPUTE_OPEN |
| DISPUTE_OPEN | record dispute | USER_DECISION_REQUIRED |
| USER_DECISION_REQUIRED | user resumes | PLAN_DRAFTING \| IMPLEMENTATION_SELF_REVIEW |
| READY_FOR_APPROVAL | user approves | COMPLETED |
| any | hard error | FAILED |

---

## 7. Dashboard layout (`scripts/ai-dashboard.sh`)

```
┌──────────────────────────────┬──────────────────────────────┐
│ Orchestrator                 │ Live Status                  │
│  workflow controller         │  Current Agent / Phase / State│
│  current state / phase       │  Last Transfer / Decision     │
│  (status.log milestones)     │  recent events · ⚖ disputes   │
├──────────────────────────────┼──────────────────────────────┤
│ Claude Output                │ Codex Output                 │
│  complete Claude logs        │  complete Codex logs         │
│  (.ai/runs/latest/claude.log)│  (.ai/runs/latest/codex.log) │
└──────────────────────────────┴──────────────────────────────┘
```

---

## 8. Observability data flow

```
phase scripts ──► ai_state_set ──► state.json (audit) + current-state.json (live)
              ├──► ai_status     ──► status.log     + latest_status
              ├──► ai_transfer   ──► transfers.log  + last_transfer + TRANSFER event
              ├──► ai_decision   ──► decisions.log  + latest_decision + DECISION event
              ├──► ai_dispute_log──► disputes.log   + DISPUTE_OPENED event
              ├──► invoke_*      ──► claude.log / codex.log + AGENT_* events
              └──► ai_register_artifact ──► .ai/artifacts/index.json + ARTIFACT_CREATED event
                                   all events ──► events.jsonl  (the canonical stream)
                                   dashboard reads current-state.json + events.jsonl + *.log
```
