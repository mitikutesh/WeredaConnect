# Agent Workflows

Consolidated reference for branch naming, feature implementation, bug fixes, and session handoffs. For commit message format, see `CLAUDE.md`. For coding patterns, see `.ai/standards.md`. For deployment steps, see `.agents/deployment_checklist.md`.

---

## Branch Naming

**Format:** `<scope>/<description>` — one scope per branch, always branch from `main`.

**Scopes:** `mobile`, `server`, `infra`, `l10n`, `testing`, `security`, `architect`

**Examples:**
```
mobile/add-crop-listing
server/sync-endpoint
infra/lambda-memory-optimization
l10n/amharic-market-terms
```

**Rules:**
1. **One scope per branch.** If a feature needs mobile and server changes, create two branches.
2. **Branch from main.** Never branch from another scope's branch.
3. **Keep branches short-lived.** Merge within a few days to avoid conflicts.
4. **No cross-scope file edits.** Only modify files owned by your scope (see `.ai/ownership.md`).

**Hotfixes:** Use `hotfix/<description>` for urgent cross-scope fixes. Document why in `TASK.md`, keep the diff minimal, and require human approval before merging.

---

## Feature Implementation Checklist

### Pre-Implementation
- [ ] Read the feature spec in `specs/features/[feature-name].md`
- [ ] Identify offline behavior requirements
- [ ] Check `docs/local_reality/README.md` for Ethiopian constraints
- [ ] Create branch: `<scope>/<feature-description>`

### Design Offline-First
- [ ] Design Isar schema for local storage
- [ ] Plan sync queue (how changes reach the server)
- [ ] Define conflict resolution strategy (server wins, client merges)
- [ ] Define graceful degradation when offline

### Implement Mobile (if applicable)
- [ ] Create feature module in `mobile/lib/features/[feature]/`
- [ ] Follow clean architecture: `data/`, `domain/`, `presentation/`
- [ ] Use offline-first repository pattern (see `.ai/standards.md`)
- [ ] Add to sync queue for server updates

### Implement Server (if applicable)
- [ ] Create Lambda function in `server/src/Functions/`
- [ ] Ensure AOT compliance (check `server/AOT_COMPLIANCE.md`)
- [ ] Make API idempotent (use idempotency keys)
- [ ] Optimize for cold start (<2s)

### Implement Infrastructure (if applicable)
- [ ] Add CDK constructs in `infra/lib/`
- [ ] Configure Lambda: ARM64, 512MB memory, 10s timeout
- [ ] Set up DynamoDB tables (on-demand billing)
- [ ] Add CloudWatch alarms for errors

### Localization
- [ ] Add strings to `.arb` files in `mobile/lib/shared/l10n/`
- [ ] Translate to Amharic, Afan Oromo, English
- [ ] Use culturally appropriate terminology
- [ ] Test Ethiopic script rendering

### Testing
- [ ] Unit tests for business logic and repositories (>80% coverage)
- [ ] Widget tests for custom UI components
- [ ] Integration tests for offline-to-online sync flows
- [ ] Manual test on 2GB RAM Android 8 emulator, airplane mode, slow 3G, all three languages

### Documentation
- [ ] Update feature spec status to "Implemented"
- [ ] Add code comments explaining offline behavior
- [ ] Update API reference if server changes made

---

## Bug Fix Workflow

### 1. Investigate
- [ ] Reproduce locally; **reproduce offline first** (network disconnected)
- [ ] Test on low-end device (2GB RAM, Android 8) if mobile bug
- [ ] Check logs: CloudWatch (server) or Flutter logs (mobile)
- [ ] Check if it is an offline sync issue (common in WeredaConnect)
- [ ] Identify which scope owns the buggy code (see `.ai/ownership.md`)

### 2. Fix
- [ ] Write a failing test that reproduces the bug (include offline scenario if relevant)
- [ ] Implement the minimal fix — avoid refactoring unrelated code
- [ ] Ensure fix works offline (if applicable)
- [ ] Check AOT compliance if server fix

### 3. Regression Testing
- [ ] Verify the failing test now passes
- [ ] Run full test suite for the affected module
- [ ] Test related features that might be affected
- [ ] Test in all three languages
- [ ] Test sync scenarios (offline to online)
- [ ] Manual test: reproduce original bug to confirm fix

### Common Bug Patterns
| Pattern | Symptom | Fix approach |
|---|---|---|
| **Offline sync** | Data not syncing after reconnection | Check sync queue logic, verify idempotency keys |
| **AOT compilation** | Build fails with `PublishAot=true` | Check `server/AOT_COMPLIANCE.md`, use source generators |
| **Localization** | Text missing in Amharic/Oromo | Add missing `.arb` entries, verify Noto Sans Ethiopic font |
| **Low-end device** | Crash or lag on 2GB RAM | Optimize images, reduce memory footprint, profile with DevTools |

---

## Session Handoff

### When to Hand Off
- Token limit approaching (>80% of context window)
- Session ending or switching to a different scope
- Stuck on a problem and need a fresh perspective

### Core Steps
1. **Commit** all changes (even WIP: `chore(<scope>): wip <description>`)
2. **Update STATE.md** — run `./scripts/checkpoint.sh` or manually record:
   - Timestamp, active branch, last commit hash
   - What you were working on and what you just completed
   - Immediate next steps and any blockers
3. **Update TASK.md** — mark completed tasks `[x]`, add status notes on in-progress items
4. **Push** to remote branch

### Resumption Prompt
```
Read STATE.md and TASK.md. Continue work based on the last commit and incomplete tasks.

Current branch: [branch name from STATE.md]
Last commit: [commit message from STATE.md]
Current task: [task description from TASK.md]
```

### Emergency Handoff
If a session crashes unexpectedly:
1. `git commit -m "emergency checkpoint"`
2. `git push origin [branch]`
3. Manually update `STATE.md` with minimal context
4. Resume with: "Emergency handoff. Read STATE.md for context."

---

## Ethiopian Context Validation

Before marking any feature or fix complete, validate against these constraints:

- [ ] **Offline:** Works with network disconnected (airplane mode)
- [ ] **Low-end device:** Tested on 2GB RAM Android 8 emulator
- [ ] **App size:** APK size increase <5MB
- [ ] **Data usage:** <1MB per typical user session
- [ ] **Localization:** All UI strings in Amharic, Afan Oromo, and English
- [ ] **Low-literacy:** Icons/images used for key actions (not just text)
- [ ] **Battery:** <5% per hour of active use
- [ ] **Cultural fit:** Terminology and UX validated against local context

---

**Last Updated**: 2026-02-16
**Version**: 1.0.0
