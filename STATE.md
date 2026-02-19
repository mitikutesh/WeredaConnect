# STATE.md - Session Checkpoint

## Checkpoint

**Date:** 2026-02-19
**Branch:** `main`
**Status:** R4 Implementation — F-11 complete, F-12 backend done, F-13 escrow starting
**Latest Commit:** 1e40b93 - Merge pull request #6 (listing → main)

---

## Resumption Context

```
R4 IMPLEMENTATION IN PROGRESS (Feb 19, 2026)
  - F-11 Trip Listings: ✅ COMPLETE (server Lambda + DynamoDB + mobile BLoC + UI + sync + tests)
    - Merged via PR #5 (trips-delivery-escrow) and PR #6 (listing — presentation layer + AOT regex fix)
  - F-12 Delivery Verification: ⚙️ BACKEND DONE, UI MISSING
    - Server Lambda + DynamoDB + mobile BLoC + repository implemented
    - No screens/widgets yet
  - F-13 Telebirr Escrow: 🏗️ INFRA SCAFFOLDED, IMPLEMENTATION STARTING
    - DynamoDB tables, Lambda CDK definitions, SSM params, EventBridge rules deployed
    - No server logic or mobile code yet — active implementation in progress
  - F-14 Fraud Detection: ❌ NOT STARTED
  - F-15 Conflict Resolution UI: ⚙️ LOGIC DONE (ConflictResolver class), UI MISSING
  - F-18 S3 Access Logging + Versioning: ✅ COMPLETE (deployed via CDK)

WHAT WORKS: Register/login (+251 phone), create crop offline, auto-sync,
  conflict resolution (server-wins), browse from server, filter by crop/location,
  trip listings (create/browse/filter offline-first with sync)

NEXT: Implement F-13 Telebirr Escrow server logic and mobile code (active now),
  then F-12 delivery verification UI, F-14 fraud detection, F-15 conflict resolution UI.
```

---

## Key Decisions

- **Server-wins conflict resolution** — simplest for solo farmers (rare conflicts)
- **Composite idempotency keys** — `{userId}:{entityType}:{entityId}:{action}:{timestamp}`
- **Exponential backoff** — 5s, 15s, 45s (max 3 retries)
- **7-day TTL on SyncMetadata** — balances idempotency vs storage costs
- **Batch size 50** — completes in <5s on 3G

---

## Deferred Items

- **F-01**: Git history purge — runbook ready at `docs/runbooks/f01-git-history-purge.md`, awaiting human execution
- **F-05**: Certificate pinning — real cert hashes extracted, activation steps at `docs/runbooks/f05-cert-pinning-activation.md`

---

## Key File Locations

| Purpose | Path |
|---------|------|
| AI Quick Start | `.ai/quick-start.md` |
| Constitution | `CLAUDE.md` |
| Current Mission | `TASK.md` |
| Standards | `.ai/standards.md` |
| Tech Stack | `.ai/tech_stack.md` |
| AOT Rules | `server/AOT_COMPLIANCE.md` |
| Ethiopian Context | `docs/local_reality/README.md` |

---

## Deployment

- **API:** `https://s0om0hib4i.execute-api.me-south-1.amazonaws.com`
- **Region:** me-south-1 (Bahrain)
- **Lambda:** 8 functions, ARM64 Native AOT
- **DynamoDB:** 7 tables (on-demand billing)

---

**Last Updated:** 2026-02-19
