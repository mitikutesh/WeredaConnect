# TASK.md - Current Mission

## Current Status

**Phase R4: NEAR COMPLETE** (started Feb 17, 2026)
**Branch:** `telebirr-mock` (45 commits ahead of `main`, NOT MERGED)
**Last PO Audit:** 2026-02-24
**MVP Completion:** ~92%
**Launch Target:** Pending merge + deploy + GA blockers

---

## PO Audit Summary (2026-02-24)

### What Changed Since Last Audit (Feb 21)

24 additional commits landed on `telebirr-mock` since the Feb 21 checkpoint, including:
- **F-14 Fraud Detection server (2d3740d)** — full rules engine implemented
- **6 critical P0 security fixes (1395264)** — auth, escrow, data integrity
- **P1 security fixes (e942edf)** — escrow logout, auth retry, Lambda limits
- **Offline rate limiting + commission (b601ef4)**
- **Auth UX overhaul (68d325f)** — offline session TTL, navigation guards
- **Profile screen wiring (8410183)** — all stubs replaced
- **Crop listing detail + delivery creation (ae2a4c1)**
- **Voice note recording/playback (0a472fd, cf3ec9a)** — bonus feature
- **Data isolation enforcement (6489007)**
- **Auth guards + widget tests (405c53a)**
- **Role-based navigation (6303a58)**
- **Multi-user E2E testing (096643c)**
- **Containerized .NET AOT build (f25d777)**

**STATE.md and ROADMAP.md were NOT updated** — they still reflect Feb 21 state.

---

## Phase Summary

| Phase | Status | Completion |
|-------|--------|------------|
| R1: Security Foundation | ✅ 100% | Feb 2026 |
| R2: Auth + Data Models | ✅ 100% | Feb 14, 2026 |
| R3: Sync + First Feature | ✅ 100% (20/20 tasks) | Feb 15, 2026 |
| R4: Trips + Delivery + Escrow + Fraud | ⚙️ ~92% | See below |

### R4 Feature Status (Verified Feb 24)

| Feature | Server | Mobile | Infra | Tests | Overall |
|---------|--------|--------|-------|-------|---------|
| F-11 Trip Listings | ✅ | ✅ | ✅ | ✅ | ✅ 100% |
| F-12 Delivery Verification | ✅ Read API | ✅ 5 screens + QR/GPS/photo | ✅ | ⚠️ 2 unit test failures | ~95% |
| F-13 Telebirr Escrow | ✅ Mock gateway + cron | ✅ 4 screens + BLoC | ✅ | ✅ | ✅ 100% |
| F-14 Fraud Detection | ✅ Rules engine + DB | ⚠️ Error handling only | ✅ GSIs + FraudSignals table | ✅ | ~85% |
| F-15 Conflict Resolution | ✅ | ✅ | — | ✅ 49 tests | ✅ 100% |
| F-18 S3 Logging | ✅ | — | ✅ | — | ✅ 100% |

### Build Health (Verified Feb 24)

| Component | Build | Tests | Issues |
|-----------|-------|-------|--------|
| Server (.NET) | ✅ 0 errors, 1 warning | ✅ 598 pass, 0 fail | CS8602 warning in SyncFunction.cs:1933 |
| Mobile (Flutter) | ✅ 0 errors in `lib/` | ⚠️ ~345 pass, 2 fail | 2 debug seed test failures |
| Mobile integration tests | ❌ 12 errors | — | Missing `locationName` in GpsCaptureResult |
| Mobile lint | ⚠️ 119 infos | — | All `require_trailing_commas` in test files |
| Infra (CDK) | ✅ | ✅ | — |

---

## MVP Readiness Checklist

Tasks are grouped by priority tier. Items within each tier are ordered by dependency/effort.

---

### TIER 1 — SHIP BLOCKERS (Must fix before any merge/deploy)

These prevent the branch from being mergeable and deployable.

#### T-MVP-01: Fix 2 Failing Mobile Unit Tests
**Scope:** `@mobile`
**Effort:** ~30min
**Files:** `mobile/test/features/delivery_verification/presentation/screens/delivery_list_screen_test.dart`
**Issue:** "debug seed button visible in debug mode for empty list" — 2 test failures
**Action:** Fix test expectations or underlying widget logic

---

#### T-MVP-02: Fix 12 Integration Test Errors (locationName)
**Scope:** `@mobile`
**Effort:** ~1h
**Files:** `mobile/integration_test/manual_stress_test.dart`, `offline_online_cycle_test.dart`, `sync_crop_listing_test.dart`
**Issue:** `GpsCaptureResult` constructor now requires `locationName` parameter but integration tests don't pass it
**Action:** Add `locationName` parameter to all 12 `GpsCaptureResult()` call sites in integration tests

---

#### T-MVP-03: Fix Trailing Comma Lint Issues in Test Files
**Scope:** `@mobile`
**Effort:** ~30min
**Files:** `mobile/test/shared/widgets/voice_note_widget_test.dart` (and others)
**Issue:** 119 `require_trailing_commas` lint infos — all in test files
**Action:** Run `dart fix --apply` or manually add trailing commas

---

#### T-MVP-04: Resolve SyncFunction Null Reference Warning
**Scope:** `@server`
**Effort:** ~15min
**File:** `server/src/Functions/SyncFunction.cs:1933`
**Issue:** CS8602 — dereference of possibly null reference
**Action:** Add null check or null-forgiving operator

---

### TIER 2 — MVP QUALITY (Required for a credible MVP demo/beta)

These are functional gaps in the core transaction flow.

#### T-MVP-05: Add Photo Upload Endpoint (F-12 completion)
**Scope:** `@server` `@infra`
**Effort:** ~4-6h
**Why:** Mobile captures delivery photos but has no server endpoint to upload them to. Core trust mechanism gap.
**Tasks:**
- Create `POST /deliveries/{id}/photos` handler (or repurpose existing stub)
- S3 pre-signed URL generation for upload
- Link photo key to delivery record in DynamoDB
- Integration test: upload → verify S3 object + delivery record updated
- Add API Gateway route + Lambda permissions

---

#### T-MVP-06: Fix Auth Interceptor Retry — Reuse Dio Instance
**Scope:** `@mobile`
**Effort:** ~2h
**File:** `mobile/lib/core/network/auth_interceptor.dart` (around line 138)
**Why:** After 401 + token refresh, retry creates a bare `Dio()` that bypasses logging and data-usage interceptors. Creates audit blind spot.
**Tasks:**
- Inject parent Dio instance into `AuthInterceptor`
- Replace `final dio = Dio(...)` in `_retry()` with injected instance
- Verify logging + data usage interceptors fire on retried requests
- Unit test

---

#### T-MVP-07: Localize Last Hardcoded String
**Scope:** `@mobile`
**Effort:** ~15min
**File:** `mobile/lib/features/onboarding/screens/welcome_screen.dart:116`
**Issue:** `'Tutorial carousel coming soon...'` is hardcoded English
**Action:** Add `tutorialComingSoon` key to EN/AM/OM `.arb` files, use `l10n.tutorialComingSoon`

---

### TIER 3 — MERGE & DEPLOY

#### T-MVP-08: Update Stale Documentation
**Scope:** `@architect`
**Effort:** ~1h
**Why:** STATE.md, ROADMAP.md, TASK.md are all stale (Feb 21). Must reflect current state before merge.
**Files:**
- `STATE.md` — update checkpoint to Feb 24, mark F-14 done, list all new commits
- `specs/features/ROADMAP.md` — update R4 to ~92%, mark F-14 complete
- `specs/features/BACKLOG.md` — mark F-14 complete
**Note:** This TASK.md replaces the stale version as of Feb 24.

---

#### T-MVP-09: Merge `telebirr-mock` to `main`
**Scope:** `@infra`
**Effort:** ~1h
**Why:** 45 commits including F-14 fraud detection, security fixes, auth improvements, and voice notes are sitting unmerged.
**Tasks:**
- Create PR from `telebirr-mock` → `main`
- Verify CI passes (all tests green)
- Review diff (268 files changed, ~56K lines added)
- Merge (squash or merge commit — user decision)

---

#### T-MVP-10: Deploy Updated Server to AWS
**Scope:** `@infra`
**Effort:** ~1h
**Why:** F-14 fraud detection, security fixes, and new endpoints need deployment.
**Tasks:**
- Run `scripts/deploy.sh` (containerized AOT build)
- Verify: `FraudSignalsTable` created in DynamoDB
- Verify: New GSIs on `CropsTable` provisioned
- Verify: `/health` endpoint returns 200
- Smoke test: create crop listing, verify fraud rules trigger

---

### TIER 4 — GA BLOCKERS (Required before real users/money)

These are pre-production requirements. MVP can demo without them, but cannot handle real transactions.

#### T-GA-01: Replace Mock Auth with Real Cognito
**Scope:** `@server` `@mobile`
**Effort:** ~1-2 weeks
**Why:** All endpoints currently use `MockAuthService`. Cannot go live with real users.
**Tasks:**
- Server: Wire `CognitoAuthService` (code exists, gated by `Environment.useMockAuth`)
- Server: Configure real Cognito user pool ID + client ID via SSM
- Mobile: Flip `Environment.useMockAuth` to `false` for production builds
- Test: SMS OTP delivery to Ethiopian phone numbers (+251)
- Test: Token refresh cycle with real JWT
- Risk: Ethio Telecom SMS delivery reliability — have voice OTP fallback ready

---

#### T-GA-02: Replace Mock Telebirr with Real API
**Scope:** `@server`
**Effort:** TBD (depends on Telebirr API availability)
**Why:** Payments are simulated. Real money requires real gateway.
**Tasks:**
- Obtain Telebirr merchant credentials and sandbox access
- Replace `MockTelebirrGateway.cs` with real API integration
- Implement: `InitiatePayment`, `CheckStatus`, `Release`, `Refund`
- Handle Telebirr-specific error codes and retry logic
- AOT-safe HTTP client (System.Net.Http, no reflection)
- E2E test against Telebirr sandbox

---

#### T-GA-03: Activate Certificate Pinning (F-05)
**Scope:** `@mobile`
**Effort:** ~2h
**Why:** Without cert pinning, MITM attacks possible on 3G/WiFi networks.
**Tasks:**
- Follow runbook at `docs/runbooks/f05-cert-pinning-activation.md`
- Update `api_client.dart` with real SHA-256 cert hashes
- Test: app rejects invalid cert, accepts valid cert

---

#### T-GA-04: Execute Git History Purge (F-01)
**Scope:** `@human`
**Effort:** ~1h
**Why:** Exposed private key in git history.
**Tasks:**
- Follow runbook at `docs/runbooks/f01-git-history-purge.md`
- Requires: `git filter-repo` + force push
- **Must complete before sharing repo or adding collaborators**

---

### TIER 5 — POLISH (Nice-to-have for MVP, can defer)

#### T-POLISH-01: Add OTP Resend Server Endpoint
**Scope:** `@server` `@mobile`
**Effort:** ~3h
**Why:** UI button exists but no backend. Users stuck if OTP doesn't arrive.
**Tasks:** Add `POST /auth/resend-otp` with rate limit (3 per 10min per phone)

---

#### T-POLISH-02: Implement Forgot Password Flow
**Scope:** `@server` `@mobile`
**Effort:** ~5h
**Why:** No way to recover account if password forgotten.
**Tasks:** `POST /auth/forgot-password`, `POST /auth/reset-password`, 2 new screens

---

#### T-POLISH-03: Consolidate DynamoDB Helpers
**Scope:** `@server`
**Effort:** ~2h
**Why:** `GetString()`, `GetInt()`, `GetDecimal()` duplicated across CropFunction, DeliveryFunction, TripFunction
**Action:** Move to shared `DynamoDbHelper.cs`, update callers

---

#### T-POLISH-04: Refactor SyncFunction (Future)
**Scope:** `@server`
**Effort:** ~5h
**Why:** SyncFunction.cs is 107KB. Extract per-entity handlers.
**Action:** Non-blocking, future sprint

---

## Completed Features (All Phases)

### R1: Security Foundation ✅
F-01 (runbook), F-02 (gitleaks), F-16 (Lambda deploy), F-19 (CI security)

### R2: Auth + Data Models ✅
F-03 (Cognito + mock), F-04 (Isar encryption), F-06 (rate limiting), F-07 (6 Isar schemas), F-09 (input validation)

### R3: Sync + First Feature ✅
F-08 (sync engine), F-10 (crop listings), F-17 (least-privilege IAM)

### R4: Transaction Flow (in progress)
- ✅ F-11 Trip Listings (PR #5 + PR #6, merged to main Feb 19)
- ✅ F-12 Delivery Verification — 95% (missing photo upload endpoint)
- ✅ F-13 Telebirr Escrow (server + mobile + 20 integration tests)
- ✅ F-14 Fraud Detection — server 100% (5 rules + DB + tests), mobile basic (error handling)
- ✅ F-15 Conflict Resolution UI (resolver + service + BLoC + 4 widgets + 49 tests)
- ✅ F-18 S3 Access Logging + Versioning (CDK deployed)

### Bonus (added beyond original MVP scope)
- ✅ Voice note recording/playback for crop listings
- ✅ Role-based navigation (farmer vs trucker vs buyer)
- ✅ Data isolation enforcement
- ✅ Crop listing detail screen with delivery creation
- ✅ Multi-user E2E testing support
- ✅ Containerized .NET AOT deployment script

---

## Test Health

| Layer | Pass | Fail | Total |
|-------|------|------|-------|
| Server (.NET) | 598 | 0 | 598 |
| Mobile unit tests | ~345 | 2 | ~347 |
| Mobile integration tests | — | 12 errors | ~12 broken |
| **Combined** | **~943** | **~14** | **~957** |

---

## Key Decision Log

| Decision | Rationale | Date |
|----------|-----------|------|
| Server-wins conflict resolution | Simplest for solo farmers (rare conflicts) | Feb 21 |
| HMAC-SHA256 over JWT for QR | Smaller payload, better on cheap cameras | Feb 18 |
| 5km GPS threshold | GPS inaccuracy + farm-to-road distance | Feb 18 |
| Mock auth/Telebirr for MVP | Enables E2E testing without vendor dependency | Feb 17 |
| Voice notes (bonus) | Low-literacy users benefit; minimal overhead | Feb 23 |

---

## Deployment

- **API:** `https://s0om0hib4i.execute-api.me-south-1.amazonaws.com`
- **Region:** me-south-1 (Bahrain)
- **Lambda:** 10+ functions, ARM64 Native AOT
- **DynamoDB:** 9+ tables (on-demand billing)
- **Note:** Server code on `telebirr-mock` is NOT YET DEPLOYED (F-14, security fixes pending)

---

**Last Updated:** 2026-02-24 (PO audit — comprehensive review)
