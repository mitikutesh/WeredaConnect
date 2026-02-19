# STATE.md - Session Checkpoint

## Checkpoint

**Date:** 2026-02-19
**Branch:** `telebirr-mock`
**Status:** R4 Implementation — F-11 complete, F-12 complete, F-13 escrow server done
**Latest Commit:** 1e40b93 - Merge pull request #6 (listing → main)

---

## Resumption Context

```
R4 IMPLEMENTATION IN PROGRESS (Feb 19, 2026)
  - F-11 Trip Listings: ✅ COMPLETE (server Lambda + DynamoDB + mobile BLoC + UI + sync + tests)
    - Merged via PR #5 (trips-delivery-escrow) and PR #6 (listing — presentation layer + AOT regex fix)
  - F-12 Delivery Verification: ✅ COMPLETE (server + mobile + UI + QR + GPS + photo + tests)
    - Server: DeliveryFunction (GET /deliveries, GET /deliveries/{id}) with 4 security fixes
    - Mobile: 3 services (QR, GPS, Photo) + 5 screens + 3 widgets + 59 l10n strings × 3 languages
    - Tests: 78 delivery tests + 404 server tests (all pass)
    - REMAINING: T12 (photo upload endpoint), integration tests
  - F-13 Telebirr Escrow: ✅ SERVER DONE (mock gateway + escrow service + cron)
    - DynamoDB tables, Lambda CDK definitions, SSM params, EventBridge rules deployed
    - Server logic implemented with mock Telebirr gateway
    - Mobile code not started
  - F-14 Fraud Detection: ❌ NOT STARTED
  - F-15 Conflict Resolution UI: ⚙️ LOGIC DONE (ConflictResolver class), UI MISSING
  - F-18 S3 Access Logging + Versioning: ✅ COMPLETE (deployed via CDK)

WHAT WORKS: Register/login (+251 phone), create crop offline, auto-sync,
  conflict resolution (server-wins), browse crops from server, filter by crop/location,
  trip listings (create/browse/filter offline-first with sync),
  full delivery verification UI (QR + GPS + photo)

NEXT: F-13 mobile escrow code, F-14 fraud detection, F-15 conflict resolution UI.
```

---

## Key Decisions

- **Server-wins conflict resolution** — simplest for solo farmers (rare conflicts)
- **Composite idempotency keys** — `{userId}:{entityType}:{entityId}:{action}:{timestamp}`
- **Exponential backoff** — 5s, 15s, 45s (max 3 retries)
- **7-day TTL on SyncMetadata** — balances idempotency vs storage costs
- **Batch size 50** — completes in <5s on 3G
- **HMAC-SHA256 over JWT for QR** — smaller payload, better on cheap cameras, simpler
- **5km GPS threshold** — accounts for GPS inaccuracy + farm-to-road distance

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
| F-12 Architecture | `specs/decisions/r4-2-delivery-verification-architecture.md` |
| F-12 Contract | `specs/contracts/delivery-verification-api.md` |

---

## Deployment

- **API:** `https://s0om0hib4i.execute-api.me-south-1.amazonaws.com`
- **Region:** me-south-1 (Bahrain)
- **Lambda:** 10 functions, ARM64 Native AOT (added Trip + Delivery)
- **DynamoDB:** 9+ tables (on-demand billing, added Trips + Deliveries)

---

**Last Updated:** 2026-02-19
