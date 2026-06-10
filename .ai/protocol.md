# Claude ↔ Codex Collaboration Protocol

The rules of engagement for multi-agent development on WeredaConnect. This is
the **root document** for the AI collaboration framework; the orchestrator
scripts, prompts, and templates implement it.

> This protocol adds an **independent adversarial review layer** on top of the
> existing single-agent SDLC in `.ai/sdlc.md`. Where `sdlc.md` describes how one
> agent + the human move a change through impact → implement → validate, this
> protocol introduces a **second, independent agent (Codex)** that reviews and
> validates Claude's work, with the **user as the final arbiter**.

---

## 1. Roles & Authority

| Agent | Role | Authority | Never does |
|-------|------|-----------|-----------|
| **Claude** | Lead **Planner** & Lead **Executor** | Drafts plans, implements code, responds to reviews, fixes accepted findings | Override Codex unilaterally on Critical/Important findings; commit/push/merge/deploy |
| **Codex** | Independent **Reviewer** & **Validator** | Reviews plans & code, classifies findings, validates Claude's rejections | Edit files; finalize or ship anything |
| **User** | **Final authority** | Resolves disputes; approves; merges; ships | — |

**Separation of duties is the point.** The planner/executor and the reviewer
are different agents so that mistakes, blind spots, and rule violations are
caught by a party that has no stake in the original design.

All agents are bound by the **project constitution** (`CLAUDE.md`) and the
**completeness rules** (`.ai/completeness-rules.md`). The HARD rules —
especially **role-based visibility** (Farmer/Trucker/Buyer) and **platform-pair
parity** — are automatic-FAIL gates for both agents.

---

## 2. The Six Capabilities

1. **Planning** — §3
2. **Implementation** — §4
3. **Review** — §5 (classification) + §3/§4 (where it runs)
4. **Dispute Resolution** — §6
5. **Auditability** — §7
6. **Automated Agent Handoffs** — §8 (see also `.ai/HANDOFF.md`)

---

## 3. Planning Protocol

```
Claude  drafts plan + self-reviews          → 01-draft-plan.md
Codex   reviews plan (classified findings)   → 02-codex-plan-review.md   [VERDICT]
Claude  accepts / rejects each comment       → 03-claude-plan-evaluation.md [EVALUATION_STATUS]
Codex   validates the REJECTED comments      → 04-codex-plan-validation.md  [DISPUTE_STATUS]
Claude  finalizes the plan                   → 05-final-plan.md
```

- If Codex's verdict is `APPROVED`, Claude finalizes directly.
- Claude must give a **specific, falsifiable reason** to reject a finding.
- If, after validation, any Critical/Important finding remains upheld
  (`DISPUTE_STATUS: OPEN`) → **dispute** (§6), workflow **stops**.

Driven by `scripts/plan-phase.sh`. Prompts: `claude-planning.md`,
`codex-plan-review.md`, `claude-review-response.md`.

---

## 4. Execution Protocol

```
Claude  implements the FINALIZED plan + self-reviews   → 06-execution-summary.md
orchestrator captures the working-tree diff            → 07-implementation.diff
Codex   reviews the implementation                      → 08-codex-code-review.md   [VERDICT]
Claude  evaluates comments; fixes ACCEPTED ones         → 09-claude-review-evaluation.md [EVALUATION_STATUS]
Codex   validates the REJECTED comments                 → 10-codex-review-validation.md  [DISPUTE_STATUS]
Claude  performs the final evaluation                   → (reflected in state)
```

- Execution only starts from `PLAN_APPROVED`.
- Claude implements **exactly** the finalized plan — no scope creep, no
  unrelated refactors, no business-logic changes beyond the task.
- Fixes for accepted findings are applied **in the working tree**; the diff is
  re-captured so Codex's validation sees them.
- If any Critical/Important finding remains upheld → **dispute** (§6), stop.
- On clean completion → `READY_FOR_APPROVAL`. **The user ships.**

Driven by `scripts/execution-phase.sh` + `scripts/review-phase.sh`. Prompts:
`claude-execution.md`, `codex-code-review.md`, `claude-review-response.md`.

---

## 5. Review Classification

Every finding — plan or code — MUST be classified and fully specified.

| Class | Definition | Consequence |
|-------|-----------|-------------|
| **Critical** | Breaks a HARD rule (role visibility, core marketplace flow), data loss, security hole, build break, or regression | Must be fixed before proceeding; blocks the workflow if unresolved |
| **Important** | Real defect or significant gap (missing test, contract drift, perf/offline regression) | Should be fixed; upheld rejection → dispute |
| **Optional** | Improvement / nice-to-have | Non-blocking; Claude may defer with rationale |
| **Invalid / Misunderstanding** | Not actually a problem | Dropped; reviewer explains why it was considered |

Every finding includes **all** of:

- **Description** — what is wrong / missing / risky.
- **Reasoning** — why it matters (cite a rule, contract, or concrete failure mode).
- **Recommended action** — the concrete fix.
- **Risk level** — High / Medium / Low.

Findings are identified `F1, F2, …` so evaluations and validations can reference
them unambiguously. See `.ai/templates/{plan-review,code-review}.md`.

---

## 6. Dispute Resolution

A **dispute** exists only when **Claude rejects a finding** AND **Codex upholds
it** on re-validation, and the finding is **Critical or Important**. (Optional
findings never block; Invalid findings are dropped by definition.)

```
Claude rejects finding ──► Codex re-validates
        │                          │
        │              CONCEDED ───┴──► resolved, continue
        │              UPHELD (Crit/Imp) ──► DISPUTE_OPEN
        ▼
   dispute-phase.sh:
     • append a dispute record (.ai/templates/dispute.md) to
       .ai/runs/<run_id>/disputes.md  (mirrored to docs/ai-plans/disputes.md)
     • state → DISPUTE_OPEN → USER_DECISION_REQUIRED
     • STOP the workflow (exit code 3)
```

**The user is the final authority.** They record a decision in the dispute
record (side with Claude, side with Codex, or a compromise), then resume:

```bash
scripts/ai-orchestrator.sh --resume <run_id>
```

On resume, the disputed phase re-runs with the user's decision now part of the
artifact context. Neither agent may override the other on a Critical/Important
finding — only the user can.

---

## 7. Auditability & Observability

The framework is a **control room**: every action is logged, streamed, and
visible in real time. See `.ai/orchestrator/workflow.md` for the full data flow.

- Every agent invocation produces a **dated, immutable artifact** in
  `.ai/runs/<run_id>/`, numbered in execution order (`01…10`).
- `state.json` records the full **state-machine history** with timestamps; the
  live `.ai/state/current-state.json` mirrors the active run for the dashboard.
- A canonical **event stream** (`.ai/runs/<run_id>/events.jsonl`) records every
  `STATE_CHANGED` / `AGENT_INVOKED` / `AGENT_COMPLETED` / `TRANSFER` /
  `DECISION` / `ARTIFACT_CREATED` / `DISPUTE_OPENED` with `{timestamp, agent,
  event, phase, artifact, summary}`.
- **Structured logs** segregate concerns: `status.log` (milestones),
  `transfers.log` (handoffs), `decisions.log` (accept/reject), `disputes.log`,
  `claude.log` / `codex.log` (full agent output).
- Every artifact is catalogued in `.ai/artifacts/index.json` (creator, phase,
  created/modified times).
- The **implementation diff** is captured against a recorded `base-ref` so the
  exact change reviewed is reproducible.
- Headline artifacts are **mirrored** to `docs/ai-plans/` for browsing; the
  archive in `.ai/runs/` is never overwritten.
- The **live dashboard** (`scripts/ai-dashboard.sh`) renders the active agent,
  phase, state, last transfer, latest decision, recent events, and any open
  dispute across four tmux panes.

---

## 8. Automated Agent Handoffs

Handoffs are **explicit, file-based, and machine-checked** — no shared mutable
chat state:

- Each step reads the **prior artifacts** as context and writes a **new
  artifact** plus a **status marker** (`VERDICT` / `EVALUATION_STATUS` /
  `DISPUTE_STATUS`).
- Every ownership transfer is recorded explicitly (`ai_transfer` →
  `transfers.log` + a `TRANSFER` event + `current-state.last_transfer`) with
  **from / to / artifact / reason**, so *who owns the work and why* is always
  answerable.
- The orchestrator parses markers to decide the next transition (deterministic
  control flow, not model-decided).
- `.ai/HANDOFF.md` documents the handoff contract and is the resume point for a
  human or a new session.

---

## 9. Integration With Existing Governance

| Existing | Relationship to this protocol |
|----------|-------------------------------|
| `CLAUDE.md` | Constitution — binds both agents; HARD rules are FAIL gates here too |
| `.ai/completeness-rules.md` | The 5 rules both agents check; central to review classification |
| `.ai/sdlc.md` | Single-agent lifecycle; this protocol is the multi-agent review layer over it |
| `.ai/agents.md` / `.claude/agents/` | The execution phase **delegates** `mobile/**`→`mobile`, `server/**`→`server`, `infra/**`→`infra` and runs `reviewer` as a self-pass — *when those subagents are available in the headless session*; otherwise Claude implements directly under the same rules |
| `.ai/ownership.md` | File ownership; plans must respect it |
| `specs/contracts/` | Cross-module contracts; Codex checks drift against these |
| `.github/workflows/` | CI; `ai-plan-validation.yml` checks artifact completeness on PRs (never auto-merges) |

The orchestrator does not replace `/implement`, `/validate`, `/review`, or
`/test` — Claude may invoke those **inside** the execution/review phases. This
protocol governs the **cross-agent** choreography around them.

---

## 10. Hard Constraints (recap)

- ❌ No auto-merge, auto-push, auto-deploy, auto-release — ever. User approval is
  mandatory to ship.
- ❌ No business-logic changes beyond what the task + finalized plan specify.
- ✅ Role-visibility and platform-pair rules are automatic-FAIL gates.
- ✅ Every finding is classified and fully specified.
- ✅ Every run is fully auditable.

---

**See also:** `.ai/orchestrator/workflow.md` (script flow) ·
`.ai/orchestrator/state-machine.md` (states) · `.ai/HANDOFF.md` (live handoff).
