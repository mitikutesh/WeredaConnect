# TASK.md - Current Mission

## Current Status

**Phase R4: IN PROGRESS** (started Feb 17, 2026)
**Branch:** `telebirr-mock`
**Current:** F-13 COMPLETE (server + mobile + integration tests)
**Next:** F-14 fraud detection, F-15 conflict resolution UI

---

## Phase Summary

| Phase | Status | Completion |
|-------|--------|------------|
| R1: Security Foundation | ✅ 100% | Feb 2026 |
| R2: Auth + Data Models | ✅ 100% | Feb 14, 2026 |
| R3: Sync + First Feature | ✅ 100% (20/20 tasks) | Feb 15, 2026 |
| R4: Trips + Delivery + Escrow | ⚙️ ~75% impl | Target: Apr 30, 2026 |

**R3 Achievements:** Offline-first sync engine, crop listing (create + browse), 228 server tests + 12 integration tests, all deployed to AWS (me-south-1), zero data loss validated.

**R4 Progress (Feb 20):**
- ✅ F-11 Trip Listings — COMPLETE (server + mobile + UI + tests, merged via PR #5 + PR #6)
- ✅ F-12 Delivery Verification — COMPLETE (server + mobile + QR + GPS + photo + 5 screens + tests)
- ✅ F-13 Telebirr Escrow — COMPLETE (server + mobile + 20 integration tests, 589 total pass)
  - Mobile: 4 screens, 4 widgets, BLoC, repository, sync service, Payments nav tab, Pay button in delivery detail
- ❌ F-14 Fraud Detection — Not started
- ⚙️ F-15 Conflict Resolution UI — Logic done (ConflictResolver class), UI missing
- ✅ F-18 S3 Access Logging + Versioning — COMPLETE (deployed via CDK)

**Deferred items:** F-01 (git history purge — runbook ready, awaiting execution), F-05 (cert pinning — real hashes extracted, activation planned).

Completed task details: `docs/archive/completed-tasks.md`

---

## Phase R4: Trip Listings + Delivery Verification + Telebirr Escrow

**Target:** Apr 1 - Apr 30, 2026 (4 weeks)
**Prerequisites:** R3 merged to main ✅

### R4 Feature Progress

| Feature | Server | Mobile Data | Mobile UI | Infra | Overall |
|---------|--------|-------------|-----------|-------|---------|
| F-11 Trip Listings | ✅ Read API + Sync | ✅ Schema + BLoC + Repo | ✅ 3 screens | ✅ Table + Lambda + Routes | ✅ 100% |
| F-12 Delivery Verification | ✅ Read API + Sync + 4 sec fixes | ✅ Schema + BLoC + Repo + 3 services | ✅ 5 screens + 3 widgets + l10n | ✅ Table + Lambda + Routes | ~95% |
| F-13 Telebirr Escrow | ✅ Mock gateway + service + cron | ✅ Repo + models + sync service | ✅ 4 screens + 4 widgets + BLoC + nav | ✅ DynamoDB + Lambda + SSM + EventBridge | ✅ 100% |
| F-14 Fraud Detection | ❌ | ❌ | ❌ | ❌ | 0% |
| F-15 Conflict Resolution UI | ⚙️ Logic (ConflictResolver) | ⚙️ ConflictResolver class | ❌ UI missing | — | ~30% |

### R4 Exit Criteria

- [x] Transport provider can create trip listing (route, capacity, departure date)
- [x] Farmer can browse trips and request delivery
- [x] Driver can verify delivery with QR + GPS + photo
- [x] Telebirr escrow holds funds until delivery confirmed by farmer (server + mobile done)
- [x] Trip listings sync offline-first
- [x] Deliveries sync offline-first
- [x] Payments sync offline-first (PaymentIntent queued for offline, syncs on connectivity)
- [x] Integration tests validate escrow flow end-to-end

### Required Contracts

| Contract | Status |
|----------|--------|
| `specs/contracts/trip-listing-api.md` | ✅ v1.0.0 (Draft) |
| `specs/contracts/delivery-verification-api.md` | ✅ v1.0.0 — QR HMAC-SHA256, GPS 5km threshold, state machine |
| `specs/contracts/escrow-protocol.md` | ✅ v1.0.0 — Telebirr abstraction, 7-state lifecycle, idempotent ops |

### Implementation Progress

1. ~~Merge R3 to main (PR for `sync-first-feat`)~~ ✅ Done (PR #4, Feb 16)
2. ~~Write R4 contracts~~ ✅ Done (all 3 contracts written, Feb 17)
3. ~~Plan deferred items F-01 and F-05~~ ✅ Runbooks at `docs/runbooks/`
4. ~~F-11 Trip Listings~~ ✅ Done (PR #5 + PR #6, Feb 19)
5. ~~F-12 Delivery Verification~~ ✅ Done (server + mobile + UI + tests)
6. ~~F-18 S3 Access Logging + Versioning~~ ✅ Done (CDK deployed)
7. ~~F-13 Escrow infra scaffolding~~ ✅ Done (DynamoDB + Lambda CDK + SSM + EventBridge)
8. ~~F-13 Escrow server logic~~ ✅ Done (mock Telebirr gateway + escrow service + cron)
9. ~~F-13 Escrow mobile~~ ✅ Done (4 screens + 4 widgets + BLoC + repo + Payments nav tab + Pay button, Feb 20)
10. ~~F-13 Escrow integration tests~~ ✅ Done (20 flow tests, 589 total pass)
11. F-14 Fraud Detection — pending
12. F-15 Conflict Resolution UI — pending
13. Execute F-01 git history purge — **requires human maintenance window**
14. Activate F-05 cert pinning — real hashes extracted, code changes needed

---

**Last Updated:** 2026-02-20
