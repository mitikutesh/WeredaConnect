# TASK.md - Current Mission

## Current Status

**Phase R4: IN PROGRESS** (started Feb 17, 2026)
**Branch:** `telebirr-mock`
**Current:** F-13 COMPLETE (server + mobile + integration tests)
**Next:** Security cleanup (F-01, F-05) → F-14 fraud detection → F-15 conflict UI → code quality fixes → GA prep

**Last Audit:** 2026-02-21 (4-agent team audit — impl, PO, architecture, mobile UI)
**MVP Completion:** ~84% | **Launch Target:** ~Mar 3-5, 2026

---

## Phase Summary

| Phase | Status | Completion |
|-------|--------|------------|
| R1: Security Foundation | ✅ 100% | Feb 2026 |
| R2: Auth + Data Models | ✅ 100% | Feb 14, 2026 |
| R3: Sync + First Feature | ✅ 100% (20/20 tasks) | Feb 15, 2026 |
| R4: Trips + Delivery + Escrow | ⚙️ ~75% impl | Target: Apr 30, 2026 |

**R3 Achievements:** Offline-first sync engine, crop listing (create + browse), 228 server tests + 12 integration tests, all deployed to AWS (me-south-1), zero data loss validated.

**R4 Progress (Feb 21 audit):**
- ✅ F-11 Trip Listings — COMPLETE (server + mobile + UI + tests, merged via PR #5 + PR #6)
- ✅ F-12 Delivery Verification — COMPLETE (server + mobile + QR + GPS + photo + 5 screens + tests)
- ✅ F-13 Telebirr Escrow — COMPLETE (server + mobile + 20 integration tests, 589 total pass)
  - Mobile: 4 screens, 4 widgets, BLoC, repository, sync service, Payments nav tab, Pay button in delivery detail
- ❌ F-14 Fraud Detection — Not started (LAUNCH BLOCKER)
- ⚙️ F-15 Conflict Resolution UI — Logic done (ConflictResolver class), UI missing (~30%)
- ✅ F-18 S3 Access Logging + Versioning — COMPLETE (deployed via CDK)

**Deferred security items (runbooks ready):** F-01 (git history purge), F-05 (cert pinning activation).
**GA blockers (not yet started):** Real Cognito auth, real Telebirr gateway integration.

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
11. F-14 Fraud Detection — **LAUNCH BLOCKER, not started**
12. F-15 Conflict Resolution UI — **pending (logic done, UI missing)**
13. Execute F-01 git history purge — **requires human maintenance window**
14. Activate F-05 cert pinning — real hashes extracted, code changes needed

---

## Remaining Work — Audit-Derived Backlog (2026-02-21)

Items are ordered by priority. All are required or strongly recommended before production launch.

---

### 🔴 CRITICAL — Production Launch Blockers

#### T-14: F-14 Fraud Detection
**Scope:** `@server` `@mobile`
**Effort:** ~16-20h
**Why:** Platform can be abused without it. Bad actors can spam listings and attempt payment fraud.
**Server tasks:**
- Duplicate listing detection (same farmer + crop + price within 1h)
- Price anomaly flagging (flag if price > 3 std devs from recent avg for that crop)
- Velocity limits: max 5 listings/hour per user, max 10 transactions/day per user
- GPS consistency checks on delivery verification (cross-check with trip route)
- Add `FraudDetectionService.cs` called from `CropFunction`, `TripFunction`, `EscrowFunction`
- Register `FraudDetectionService` in AOT JSON context if it uses serialization

**Mobile tasks:**
- Surface fraud flags to user (e.g., "suspicious activity detected" warning)
- Prevent duplicate listing submission in `CreateCropListingScreen`

**Tests:** Unit tests for each fraud rule + integration test for velocity limit

---

#### T-15: F-15 Conflict Resolution UI
**Scope:** `@mobile`
**Effort:** ~4-6h
**Why:** Without a UI, sync conflicts are silently resolved (server-wins). Users may lose edits without knowing.
**Tasks:**
- Create `ConflictResolutionScreen` — side-by-side diff (local vs server version)
- Create `ConflictBloc` — events: `LoadConflict`, `AcceptServer`, `AcceptLocal`; states: `ConflictLoaded`, `ConflictResolved`
- Wire `ConflictResolver` class into bloc (class already exists at `mobile/lib/core/sync/conflict_resolver.dart`)
- Add navigation: when sync detects a conflict, push `ConflictResolutionScreen`
- Add l10n keys for all 3 languages (en/am/om): `conflictTitle`, `conflictLocalVersion`, `conflictServerVersion`, `acceptLocal`, `acceptServer`
- Add test: BLoC unit tests + widget test for screen

---

#### T-SEC-01: Execute F-01 Git History Purge
**Scope:** `@infra` `@human`
**Effort:** ~1h
**Why:** Exposed private key in git history — security risk before any public/team access.
**Tasks:**
- Follow runbook at `docs/runbooks/f01-git-history-purge.md`
- Requires human execution (git filter-repo + force push)
- **Must be done before sharing repo or adding collaborators**

---

#### T-SEC-02: Activate F-05 Certificate Pinning
**Scope:** `@mobile`
**Effort:** ~2h
**Why:** Without cert pinning, MITM attacks are possible on 3G/WiFi networks (critical for Ethiopian rural context).
**Tasks:**
- Follow runbook at `docs/runbooks/f05-cert-pinning-activation.md`
- Real cert hashes already extracted
- Update `mobile/lib/core/network/` to enable pinning with extracted hashes
- Test on device: confirm app rejects invalid cert, accepts valid cert

---

### 🟠 HIGH — Required Before GA (Real Users/Money)

#### T-AUTH-01: Implement Real Cognito Auth
**Scope:** `@server`
**Effort:** ~1-2 weeks
**Why:** All endpoints currently use mock auth. Cannot go live with real users without proper authentication.
**Tasks:**
- Replace `MockAuthService` with `CognitoAuthService` in `server/src/Services/`
- Implement AWS Cognito token validation using `AWSSDK.CognitoIdentityProvider` (AOT-safe)
- Add token refresh endpoint
- Add logout/revoke endpoint
- Update `RegisterFunction`, `LoginFunction` to use real Cognito user pool
- Ensure AOT compliance: use source generators, no reflection
- Update SSM parameters for Cognito user pool ID + client ID
- Integration tests for auth flow end-to-end

---

#### T-PAY-01: Replace Mock Telebirr Gateway with Real API
**Scope:** `@server`
**Effort:** TBD (depends on Telebirr API access)
**Why:** Payments are currently simulated. Real money requires real gateway integration.
**Tasks:**
- Replace `MockTelebirrGateway.cs` with `TelebirrGateway.cs` implementing real Telebirr API
- Obtain Telebirr merchant credentials and sandbox access
- Implement: `InitiatePayment`, `CheckStatus`, `Release`, `Refund` against live endpoints
- Handle Telebirr-specific error codes and retry logic
- Add webhook handler for Telebirr payment callbacks
- AOT-safe HTTP client (use `System.Net.Http.HttpClient`, not HttpClientFactory with reflection)
- End-to-end integration tests against Telebirr sandbox

---

### 🟡 MEDIUM — Pre-Production Polish

#### T-F12-01: F-12 Delivery Integration Tests
**Scope:** `@server`
**Effort:** ~2-3h
**Why:** F-12 is marked 95% complete — missing E2E tests to reach 100%.
**Tasks:**
- Add integration test: full delivery verification flow (create → QR generate → QR scan → GPS verify → photo → status transition)
- Add test: QR tampering rejection
- Add test: GPS outside 5km threshold rejection
- Add test: photo upload endpoint (T12 — currently missing endpoint)

---

#### T-NET-01: Fix Auth Interceptor Retry — Reuse Dio Instance
**Scope:** `@mobile`
**Effort:** ~2-4h
**Why:** After 401 + token refresh, retry creates a new `Dio()` instance that bypasses logging and data usage interceptors. Creates audit/tracking blind spots.
**File:** `mobile/lib/core/network/auth_interceptor.dart:146`
**Tasks:**
- Inject parent `Dio` instance reference into `AuthInterceptor` constructor
- Replace `final dio = Dio(); return dio.request(...)` with the injected instance
- Verify retry requests appear in `LoggingInterceptor` and `DataUsageInterceptor`
- Add unit test: confirm interceptors fire on retried request

---

#### T-SEC-03: Add Client-Side GPS Validation in Delivery Repository
**Scope:** `@mobile`
**Effort:** ~3-4h
**Why:** Server validates GPS bounds but client doesn't. Defense-in-depth; prevents bad data from being queued to sync.
**File:** `mobile/lib/features/delivery_verification/data/repositories/delivery_repository.dart`
**Tasks:**
- Add `_validateGpsCoordinates(double lat, double lng)` — check lat ∈ [-90, 90], lng ∈ [-180, 180]
- Add accuracy threshold check (flag if GPS accuracy > 100m)
- Throw `ValidationException` with l10n-friendly message before enqueueing sync
- Add unit tests for boundary conditions

---

#### T-SERVER-01: Consolidate Duplicate DynamoDB Helper Functions
**Scope:** `@server`
**Effort:** ~2h
**Why:** `GetString()`, `GetInt()`, `GetDecimal()` are duplicated across `CropFunction`, `DeliveryFunction`, `TripFunction` (~50 lines of duplication).
**Tasks:**
- Move shared helpers to `server/src/Data/DynamoDbMapper.cs` (or extend existing `DynamoDbHelper`)
- Update `CropFunction`, `DeliveryFunction`, `TripFunction` to use shared helpers
- Verify build passes: `cd server && dotnet build --no-restore`

---

### 🔵 LOW — Nice to Have / Future Sprint

#### T-MOB-01: Wire Profile Screen Navigation Stubs
**Scope:** `@mobile`
**Effort:** ~2-3h
**Why:** 4 buttons in `profile_screen.dart` are stubs. Low impact (settings screen exists separately) but creates inconsistent UX.
**Tasks:**
- Identify the 4 stub buttons in `profile_screen.dart`
- Wire to existing screens where applicable (settings → `SettingsScreen`, etc.)
- For features not yet built, keep "Coming Soon" snackbar but ensure l10n is used

---

#### T-MOB-02: Fix Hardcoded Snackbar String in Welcome Screen
**Scope:** `@mobile`
**Effort:** ~30min
**File:** `mobile/lib/features/onboarding/screens/welcome_screen.dart:127`
**Tasks:**
- Move snackbar text to `app_en.arb` (key: `tutorialComingSoon`)
- Add Amharic translation to `app_am.arb`
- Add Afan Oromo translation to `app_om.arb`
- Update `welcome_screen.dart` to use `l10n.tutorialComingSoon`
- Run `flutter gen-l10n`

---

#### T-MOB-03: Implement OTP Resend
**Scope:** `@server` `@mobile`
**Effort:** ~3-4h
**File:** `mobile/lib/features/auth/screens/otp_verification_screen.dart:104`
**Tasks:**
- Add `POST /auth/resend-otp` endpoint in `server/src/Functions/`
- Rate-limit: max 3 resends per phone per 10min (prevent SMS abuse)
- Mobile: replace `_handleResendOtp()` stub with real API call
- Show countdown timer between resend attempts

---

#### T-MOB-04: Implement Forgot Password Flow
**Scope:** `@server` `@mobile`
**Effort:** ~4-6h
**File:** `mobile/lib/features/auth/screens/login_screen.dart:220`
**Tasks:**
- Add `POST /auth/forgot-password` and `POST /auth/reset-password` endpoints
- Mobile: create `ForgotPasswordScreen` and `ResetPasswordScreen`
- Wire from `LoginScreen` forgot password button
- SMS OTP flow for password reset (reuse OTP verification screen)

---

#### T-SERVER-02: Refactor Large SyncFunction (Future Sprint)
**Scope:** `@server`
**Effort:** ~4-6h (design + implementation)
**Why:** `SyncFunction.cs` is 107KB handling crop, trip, and delivery sync — hard to maintain.
**Tasks (non-blocking, future sprint):**
- Extract `SyncCropHandler`, `SyncTripHandler`, `SyncDeliveryHandler` classes
- `SyncFunction` becomes a thin router delegating to per-entity handlers
- Reduces cognitive load and simplifies per-entity test coverage

---

## Audit Summary (2026-02-21)

| Category | Finding |
|---|---|
| MVP completion | ~84% |
| Launch blockers | F-14 (fraud), F-15 (conflict UI), Cognito auth, Telebirr live API |
| Security (urgent) | F-01 git purge, F-05 cert pinning (runbooks ready) |
| Architecture | SOUND — no rewrites needed |
| Mobile UI | 18 screens, all reachable, l10n 100% (642 keys × 3 languages) |
| Tests | 589+ passing |
| Dead code | None significant |
| Projected launch | ~Mar 3-5, 2026 with focused effort |

---

**Last Updated:** 2026-02-21
