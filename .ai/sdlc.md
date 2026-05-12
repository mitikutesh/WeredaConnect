# Agentic Software Development Lifecycle

How AI agents collaborate on WeredaConnect — from task intake to production deployment.

---

## Lifecycle Phases

```
┌─────────────────────────────────────────────────────────────────────────┐
│  1. INTAKE          2. PLAN           3. IMPLEMENT       4. VALIDATE   │
│  ─────────          ─────────         ────────────       ──────────    │
│  User request  →   /impact      →   Code changes   →   /validate     │
│  or TASK.md        /plan             Platform pairs      /review       │
│                    Wait for OK       All 3 roles         /test         │
│                                                                        │
│  5. FIX LOOP        6. SHIP           7. MONITOR         8. HANDOFF   │
│  ──────────         ─────────         ──────────         ──────────   │
│  Fix failures  →   /commit      →   /monitor       →   /checkpoint   │
│  Re-validate       /deploy           /harden             STATE.md     │
│  Until clean       /backup           /backup              TASK.md     │
└─────────────────────────────────────────────────────────────────────────┘
```

---

## Phase Details

### 1. INTAKE — Understand the Task

**Who**: Main agent (reads CLAUDE.md automatically)

**Steps**:
1. Read `STATE.md` → `TASK.md` to understand current context
2. Read `.ai/completeness-rules.md` (MANDATORY before any work)
3. Clarify ambiguous requests with the user

**Context loaded**: CLAUDE.md (auto), role rules, platform pairs, test users, AOT bans (all inline in CLAUDE.md)

### 2. PLAN — Analyze Impact Before Writing Code

**Commands**: `/impact`, `/plan`

**Steps**:
1. Identify ALL files that need changes
2. Check platform pairs (CLAUDE.md table) — flag counterparts
3. Check role impact — does this touch Farmer/Trucker/Buyer flows?
4. Check cross-module impact — server ↔ mobile ↔ admin
5. Present impact summary to user
6. **WAIT for user confirmation** — never start coding without approval

**Agents involved**:
- Main agent does the analysis
- Spawn `reviewer` agent to pre-review the plan if HIGH risk

### 3. IMPLEMENT — Write Code

**Commands**: `/implement`, `/l10n`, `/error-mapping`

**Steps**:
1. Spawn domain agent if needed:
   - `mobile` agent for `mobile/**` changes
   - `server` agent for `server/**` changes
   - `infra` agent for `infra/**` changes
2. Update platform pair counterparts **in the same step** — never leave one out of sync
3. Verify all 3 role code paths if role-dependent code changed
4. Update localization (all 3 languages) if UI strings changed
5. Update tests alongside production code
6. Format all edited files

**Agent coordination**:
- If task spans mobile + server: main agent orchestrates, spawns both domain agents
- Each domain agent reads `.ai/completeness-rules.md` first
- Each agent flags cross-module impacts back to main agent

### 4. VALIDATE — Verify Nothing Broke

**Commands**: `/validate`, `/review`, `/test`

**Steps** (run ALL three in sequence):
1. `/validate` — Cross-platform pairs, cross-role visibility, cross-module contracts, builds
2. `/review` — Quality criteria with CRITICAL gates for roles + platform
3. `/test` — Run tests for all affected modules

**CRITICAL gates** (automatic FAIL):
- Role-based visibility violated → FAIL
- Platform pair out of sync → FAIL
- Web build broken → FAIL

### 5. FIX LOOP — Iterate Until Clean

If ANY validation fails:
1. Fix the issue
2. Re-run ALL THREE: `/validate` → `/review` → `/test`
3. Repeat until all pass
4. Report final results with completeness self-check

### 6. SHIP — Commit and Deploy

**Commands**: `/checkpoint`, `/deploy`, `/backup`

**Steps**:
1. Create commit following Conventional Commits (`.ai/standards.md`)
2. Create PR if on feature branch
3. For deployment:
   - Run `/backup` to verify DynamoDB PITR
   - Run `/deploy dev` (or staging/prod)
   - **NEVER deploy to prod without user approval**

### 7. MONITOR — Post-Ship Observability

**Commands**: `/monitor`, `/harden`

**Steps**:
1. Run `/monitor` to check Lambda errors, DynamoDB throttles, API health
2. Run `/harden` periodically to check security posture
3. Check CloudWatch alarms are in place

### 8. HANDOFF — Session Transfer

**Commands**: `/checkpoint`

**Steps**:
1. Run `/checkpoint` to auto-generate STATE.md
2. Update TASK.md with completed items and next steps
3. Commit WIP if needed
4. Push to remote branch

---

## Agent Orchestration Rules

### When to Spawn Agents

| Situation | Action |
|-----------|--------|
| Changes only in `mobile/` | Spawn `mobile` agent |
| Changes only in `server/` | Spawn `server` agent |
| Changes only in `infra/` | Spawn `infra` agent |
| Changes span 2+ modules | Main agent orchestrates, spawns domain agents as needed |
| Need quality review | Spawn `reviewer` agent (read-only) |
| Simple/small change | Main agent handles directly (no spawn needed) |

### Agent Communication

- Main agent passes task description + relevant context to domain agents
- Domain agents read `.ai/completeness-rules.md` first (MANDATORY)
- Domain agents flag cross-module impacts back to main agent
- Domain agents NEVER modify files outside their scope
- If a domain agent needs changes in another module, it reports the need — main agent handles it

### Conflict Resolution

- Agent conflicts → escalate to user
- Scope ambiguity → check `.ai/ownership.md`
- Architecture decisions → document in `specs/decisions/`

---

## Quality Gates

Every change passes through these gates. No exceptions.

| Gate | When | Enforced By | FAIL = |
|------|------|-------------|--------|
| Role visibility | Phase 4 | `/validate`, `/review`, `reviewer` agent | CRITICAL — must fix |
| Platform parity | Phase 4 | `/validate`, `/review` | CRITICAL — must fix |
| AOT compliance | Phase 3 | PostToolUse hook (automatic) | Build fails |
| Offline-first | Phase 4 | `/review`, `reviewer` agent | WARNING — must justify |
| Localization | Phase 4 | `/review` | WARNING — must fix |
| Tests | Phase 4 | `/test` | Must fix before ship |
| Security | Phase 7 | `/harden` | Tracked in TASK.md |

---

## Command Cheat Sheet

```bash
# Full implementation pipeline
/implement <task description>

# Individual steps (when not using /implement)
/impact <file or feature>        # Step 2: Analyze before coding
/validate                        # Step 4: Build + cross-checks
/review                          # Step 4: Quality review
/test                            # Step 4: Run tests

# Shipping
/checkpoint                      # Generate STATE.md
/deploy dev                      # Deploy to dev environment
/backup check                    # Verify DynamoDB backups

# Operations
/monitor                         # Check CloudWatch + Lambda health
/harden                          # Security audit
/harden T-SEC-04                 # Check specific finding

# Content
/l10n welcomeTitle "Welcome" "እንኳን ደህና መጡ" "Baga nagaan dhuftan"
/error-mapping escrow            # Audit escrow error handling
/plan <feature description>      # Create feature spec
```

---

**Last Updated**: 2026-03-19
