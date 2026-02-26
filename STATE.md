# STATE.md - Session Checkpoint

## Checkpoint

**Date:** 2026-02-24
**Branch:** `telebirr-mock`
**Status:** R4 Implementation — F-11 ✅, F-12 ✅, F-13 ✅, F-14 ✅, F-15 ✅, F-18 ✅ — MVP ~95%
**Latest Commit:** f25d777 - build(ci): containerize .NET AOT build in deployment script

---

## Resumption Context

```
R4 IMPLEMENTATION NEARLY COMPLETE (Feb 24, 2026)
  - F-11 Trip Listings: ✅ COMPLETE (server Lambda + DynamoDB + mobile BLoC + UI + sync + tests)
    - Merged via PR #5 (trips-delivery-escrow) and PR #6 (listing — presentation layer + AOT regex fix)
  - F-12 Delivery Verification: ✅ COMPLETE (server + mobile + UI + QR + GPS + photo + tests)
    - Server: DeliveryFunction (GET /deliveries, GET /deliveries/{id}) with 4 security fixes
    - Mobile: 3 services (QR, GPS, Photo) + 5 screens + 3 widgets + 59 l10n strings × 3 languages
    - Photo upload endpoint added (S3 pre-signed URL pattern)
    - Tests: 78 delivery tests + server tests (all pass)
  - F-13 Telebirr Escrow: ✅ COMPLETE (server + mobile + 20 integration tests)
    - Server: ✅ Mock gateway + EscrowService + cron (auto-release/auto-refund) + DynamoDB + Lambda deployed
    - Mobile data: ✅ EscrowRepository + EscrowSyncService + models + Payment/PaymentIntent Isar schemas
    - Mobile UI: ✅ 4 screens + 4 widgets + BLoC + repo + Payments nav tab + Pay button
  - F-14 Fraud Detection: ✅ COMPLETE (commit 2d3740d)
    - Server: 5 rules engine (duplicate detection, price anomaly, velocity limits, GPS consistency, risk scoring)
    - FraudSignalsTable + 2 new GSIs
    - Mobile: basic error handling for fraud rejections
  - F-15 Conflict Resolution UI: ✅ COMPLETE (Feb 21, 2026)
    - Core: ConflictResolver (330 lines) + ConflictNotificationService (replay buffer) + ConflictBloc
    - UI: ConflictResolutionScreen (modal bottom sheet) + 4 widgets (banner, detail card, field diff, empty state)
    - Wiring: ConflictNotificationBanner via MaterialBanner in MainNavigation, 8s auto-dismiss
    - L10n: 16 keys × 3 languages (EN, AM, OM)
    - Security: All 7 findings verified (2 required fixes applied, 5 advisory confirmed)
    - Tests: 49 passing (resolver + service + bloc + screen + 4 widget tests)
  - F-18 S3 Access Logging + Versioning: ✅ COMPLETE (deployed via CDK)

MAJOR CHANGES SINCE FEB 21 (24 commits):
  - F-14 Fraud Detection implemented (server + mobile)
  - F-12 Photo upload endpoint completed
  - 6 critical P0 security fixes (1395264)
  - P1 security fixes — escrow logout, auth retry, Lambda limits (e942edf)
  - Offline rate limiting + commission rate from server (b601ef4)
  - Auth UX overhaul — offline session TTL, navigation guards (68d325f)
  - Profile screen fully wired, no more stubs (8410183)
  - Crop listing detail + delivery creation + browse filters (ae2a4c1)
  - Voice note recording/playback — bonus feature (0a472fd, cf3ec9a)
  - Data isolation enforcement (6489007)
  - Auth guards + widget tests (405c53a)
  - Role-based navigation (6303a58)
  - Multi-user E2E testing (096643c)
  - Containerized .NET AOT build (f25d777)
  - Client-side GPS validation done (T-SEC-03)
  - All Tier 1 ship blockers fixed (tests, lint, build warnings)
  - Last hardcoded string localized

WHAT WORKS: Register/login (+251 phone), create crop offline, auto-sync,
  conflict resolution (server-wins with notification banner + detail screen),
  browse crops from server, filter by crop/location,
  trip listings (create/browse/filter offline-first with sync),
  full delivery verification (QR + GPS + photo upload),
  escrow payment (initiate, status, history, disputes),
  fraud detection (5 rules engine), role-based navigation,
  voice note recording/playback, data isolation, profile management

CURRENT: Branch telebirr-mock is 46 commits ahead of main, NOT YET MERGED.
NEXT: Merge telebirr-mock → main, deploy to AWS dev environment.
  Then GA blockers: Real Cognito auth, real Telebirr API, cert pinning (F-05), git purge (F-01).
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
- **5 fraud rules** — duplicate detection, price anomaly, velocity limits, GPS consistency, risk scoring

---

## Deferred Items

- **F-01**: Git history purge — runbook ready at `docs/runbooks/f01-git-history-purge.md`, awaiting human execution
- **F-05**: Certificate pinning — real cert hashes extracted, activation steps at `docs/runbooks/f05-cert-pinning-activation.md`
- **OTP resend endpoint** — needed for production auth flow
- **Forgot password** — needed for production auth flow
- **DynamoDB table consolidation** — optimize table count for cost savings

---

## Build Health

- **Server:** 0 errors, 0 warnings, 611 tests passing
- **Mobile:** 0 errors, 6 info-level issues, ~345 unit tests passing
- **Integration tests:** 0 errors

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
| F-13 Architecture | `specs/decisions/r4-3-escrow-architecture.md` |
| F-13 Contract | `specs/contracts/escrow-protocol.md` |
| F-14 Fraud Detection | Server: `server/src/Functions/FraudFunction/` |
| F-15 Architecture | `specs/decisions/f15-conflict-resolution-ui.md` |
| F-15 UX Design | `specs/decisions/f15-ux-design.md` |
| F-15 Security Review | `specs/decisions/f15-security-review.md` |

---

## Deployment

- **API:** `https://s0om0hib4i.execute-api.me-south-1.amazonaws.com`
- **Region:** me-south-1 (Bahrain)
- **Lambda:** 10+ functions, ARM64 Native AOT (added Trip + Delivery + Escrow + Fraud)
- **DynamoDB:** 9+ tables (on-demand billing, added Trips + Deliveries + Escrow + FraudSignals)

---

**Last Updated:** 2026-02-24
