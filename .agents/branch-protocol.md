# Scoped Branching Protocol

## Purpose
Prevent merge conflicts and cross-scope interference by enforcing **one scope per branch**. Branch names make ownership visible in git.

---

## Branch Naming Convention

```
<scope>/<description>
```

**Examples:**
```
mobile/add-crop-listing
server/sync-endpoint
infra/lambda-memory-optimization
l10n/amharic-market-terms
testing/offline-sync-tests
security/threat-model-escrow
architect/adr-sync-protocol
```

**Scope prefixes:** `mobile`, `server`, `infra`, `l10n`, `testing`, `security`, `architect`

---

## Rules

### 1. One Scope Per Branch
Each branch must contain changes for only one scope. If a feature needs both mobile and server changes, create two branches.

### 2. Branch From Main
Always branch from `main` (or the current release branch). Never branch from another scope's branch.

### 3. Keep Branches Short-Lived
Merge branches within a few days. Long-lived branches accumulate conflicts.

### 4. Merge Order
When a feature spans multiple scopes, merge in this order:

```
architect → server → mobile → infra → l10n → testing → security
```

**Why this order:**
1. **Architect** defines contracts and specs first
2. **Server** implements the API (contracts must exist first)
3. **Mobile** implements the client (API must exist first)
4. **Infra** deploys the backend (code must be ready)
5. **L10n** translates the UI (strings must exist first)
6. **Testing** validates everything (features must be implemented)
7. **Security** reviews the whole feature (everything must be testable)

### 5. No Cross-Scope File Edits
A branch with scope `mobile/` should only modify files owned by the Mobile scope (see `.ai/ownership.md`). If you discover you need a server change, create a separate `server/` branch.

---

## Multi-Scope Features

For a feature like "crop listing with sync":

```
1. architect/crop-listing-contract   → merge to main
2. server/crop-listing-sync           → merge to main (after #1)
3. mobile/crop-listing-ui            → merge to main (after #2)
4. l10n/crop-listing-translations    → merge to main (after #3)
5. testing/crop-listing-e2e          → merge to main (after #4)
```

Each branch is independent and only touches its own scope's files.

---

## Hotfix Protocol

For urgent fixes that cross scope boundaries:
1. Create a `hotfix/<description>` branch (no scope prefix)
2. Document in `TASK.md` why the cross-scope edit is necessary
3. Require human developer approval before merging
4. Keep the diff minimal — fix only what's broken

---

**Last Updated**: 2026-02-09
**Version**: 1.0.0
