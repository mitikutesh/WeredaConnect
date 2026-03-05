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

### TIER 3.5 — SECURITY HARDENING (Pentest Findings 2026-03-05)

Findings from penetration test against `dev-admin.weredaconnect.com`. Must be resolved before production.

#### Completed (2026-03-05)
- [x] Strengthen password policy (min 12, require uppercase + symbols)
- [x] Enable deletion protection on Cognito User Pool
- [x] Remove implicit OAuth flow; set proper callback URLs
- [x] Reduce mobile refresh token from 30 to 7 days
- [x] Remove `unsafe-eval` from CSP `script-src`
- [x] Strip version/region/coldStart from health endpoint (T-SEC-03)
- [x] Create separate admin Cognito User Pool (`selfSignUpEnabled: false`)
- [x] Enable MFA (TOTP required on admin pool, optional on mobile pool) (T-SEC-08)
- [x] IAM `ListUsers` already scoped to admin Lambda only

#### T-SEC-01: Add CI/CD Guard for DEV_FAYDA_BYPASS in Production [CRITICAL]
**Scope:** `@infra`
**Effort:** ~1h
**Severity:** CRITICAL
**Finding:** `DEV_FAYDA_BYPASS=true` is active on dev. Sending `authorizationCode: "test-code-123"` to `POST /auth/fayda-verify` returns a valid verification token with `faydaId: "FAYDA-DEV-001"`, bypassing national ID verification entirely.
**Risk:** If this flag leaks to production, anyone can create accounts with a fake identity, undermining the entire trust model.
**Location:** `server/src/Functions/FaydaVerifyFunction.cs:85-103`, `infra/lib/stacks/api-stack.ts:377`
**Action:**
- Add CDK assertion: throw error if `DEV_FAYDA_BYPASS=true` when `stage === 'prod'`
- Add CI pipeline check that scans CloudFormation output for this env var in prod
- Consider removing the bypass entirely and using Cognito test users instead

---

#### T-SEC-02: Add CI/CD Guard for TELEBIRR_MOCK in Production [CRITICAL]
**Scope:** `@infra`
**Effort:** ~1h
**Severity:** CRITICAL
**Finding:** `TELEBIRR_MOCK=true` is set in non-prod environments. If this reaches production, real payments would use the mock gateway (no actual fund transfers).
**Location:** `server/src/Functions/EscrowFunction.cs:460-476`, `infra/lib/stacks/api-stack.ts:432`
**Action:**
- Add CDK assertion: throw error if `TELEBIRR_MOCK=true` when `stage === 'prod'`
- Add CI pipeline check mirroring T-SEC-01
- Both guards should be in a shared `validateProductionConfig()` function

---

#### ~~T-SEC-03: Remove Sensitive Data from Health Endpoint~~ DONE
Fixed in commit `4bbf890` — health endpoint now returns only `status` + `timestamp`.

---

#### T-SEC-04: Add AWS WAF to API Gateway [HIGH]
**Scope:** `@infra`
**Effort:** ~2-3h
**Severity:** HIGH
**Finding:** No WAF configured on the HTTP API Gateway. The API relies solely on Lambda-level validation for injection protection.
**Action:**
- Note: AWS WAF does not support HTTP API (v2) directly. Options:
  - Migrate to REST API (v1) + WAF, OR
  - Add CloudFront distribution in front of HTTP API + attach WAF there, OR
  - Add regional WAF via ALB proxy
- Managed rule groups: `AWSManagedRulesCommonRuleSet`, `AWSManagedRulesSQLiRuleSet`, `AWSManagedRulesKnownBadInputsRuleSet`
- Enable rate-based rules as additional layer

---

#### T-SEC-05: Remove version.json from Public Webapp [LOW]
**Scope:** `@infra` `@mobile`
**Effort:** ~15min
**Severity:** LOW
**Finding:** `https://dev-app.weredaconnect.com/version.json` publicly exposes:
```json
{"app_name":"wereda_connect","version":"0.1.0","build_number":"1","package_name":"wereda_connect"}
```
**Action:**
- Exclude `version.json` from S3 deploy, or
- Add CloudFront function to block access to this path

---

#### T-SEC-06: Strip Localhost CORS Origins for Production [LOW]
**Scope:** `@infra`
**Effort:** ~15min
**Severity:** LOW
**Finding:** Dev environment CORS allows `http://localhost:3000/4321/5173/8080`. Verified that `https://evil.com` and `https://evil.weredaconnect.com` are correctly rejected.
**Location:** `infra/lib/stacks/api-stack.ts:560-591`
**Action:**
- Verify CDK logic already strips localhost origins for `stage === 'prod'` (appears handled but needs explicit test)
- Add CDK unit test asserting prod CORS origins contain no localhost entries

---

#### T-SEC-07: Reduce Admin Token Validity [LOW]
**Scope:** `@infra`
**Effort:** ~15min
**Severity:** LOW
**Finding:** Admin Cognito client tokens are valid for 60 minutes vs. 15 minutes for regular users.
**Location:** `infra/lib/stacks/api-stack.ts:902-904`
**Action:**
- Reduce admin access token validity from 60 to 15 minutes
- Rely on refresh tokens for longer sessions

---

#### ~~T-SEC-08: Enable MFA for Admin Accounts~~ DONE
Fixed — separate admin pool with `mfa: REQUIRED` (TOTP), mobile pool with `mfa: OPTIONAL`.

---

#### T-SEC-09: Exit SNS Sandbox for Production SMS [MEDIUM]
**Scope:** `@infra` (manual AWS process)
**Effort:** ~1h + AWS approval wait
**Severity:** MEDIUM
**Finding:** `SmsConfigurationFailure: "SNSSandbox"` — SMS verification codes only work for pre-verified numbers.
**Action:**
- Submit AWS Support request for SNS production access in `me-south-1`
- Verify SMS delivery for `+251` (Ethiopian) numbers after approval
- Set up CloudWatch alarm on SNS spending > $10/month
- Not a code change; this is an AWS account-level configuration

---

#### T-SEC-10: Implement End-to-End Audit Logging [HIGH]
**Scope:** `@server` `@infra` `@mobile`
**Effort:** ~5-7 days (5 phases)
**Severity:** HIGH
**Spike:** `specs/features/audit-logging-spike.md`
**Why:** Cannot trace a transaction end-to-end today. No structured logs, no dashboards, no tamper-evident audit trail. Required for NBE compliance (10-year retention, fraud reporting), dispute resolution, and enterprise customer trust.
**Phases:**
1. **Correlation IDs + Structured Logging** (1-2 days) — `AuditLogger` helper, `X-Correlation-Id` propagation, JSON structured logs in all Lambda functions, mobile correlationId generation
2. **Tamper-Evident Audit Trail** (1 day) — SHA-256 hash chain on escrow-audit entries, admin verification endpoint, `sourceIp`/`amount`/`reason` fields
3. **CloudWatch Dashboard + Alarms** (1 day) — Real metrics in monitoring-stack, 6 alarms (error rate, cron failures, DynamoDB throttles, cold starts, dispute rate, brute force)
4. **Expanded Audit Tables** (1-2 days) — auth-audit, delivery-audit, listing-audit, admin-audit DynamoDB tables with TTL and hash chains
5. **Log Archival for Compliance** (2-3 days) — S3 bucket with Object Lock, lifecycle (Standard → IA → Glacier → Deep Archive), weekly export Lambda, 10-year retention

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

### FUTURE SPRINT — VeriFayda 2.0 Identity Verification

#### T-FUTURE-01: Integrate VeriFayda 2.0 OIDC for Registration
**Scope:** `@server` `@mobile` `@infra`
**Effort:** ~2-3 weeks
**Spec:** `specs/features/verifayda-integration.md`
**Why:** NBE mandates VeriFayda 2.0 for transactions >10,000 ETB. Current system stores Fayda ID as unverified string. Real OIDC verification needed for compliance and trust.
**Approach:** Fayda eSignet OIDC flow during registration with two-tier verification (full biometric vs manual entry fallback).
**Prerequisite:** Apply for Fayda API access at https://id.gov.et/api (2 working day turnaround).
**Tasks:**
- Server: `FaydaVerifyFunction` Lambda + `FaydaOidcService` (AOT-safe OIDC token exchange)
- Server: Update `RegisterFunction` to accept verification token, update `UserDto` + `EscrowService`
- Mobile: Rewrite `FayidaIdScreen` with OIDC button + manual fallback
- Mobile: `FaydaOidcService`, deep link config, `url_launcher` package
- Mobile: Update auth BLoC + Isar user schema (`faydaVerified`, `faydaVerifiedAt`)
- Infra: SSM/Secrets Manager for Fayda credentials, API Gateway route, Lambda
- Localization: 10 new l10n keys (en/am/om)
- Tests: Unit + widget + integration (18 test cases specified in plan)

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
| Pentest before production | 8 findings (2 critical, 1 high, 2 medium, 3 low). See TIER 3.5 | Mar 5 |
| Separate admin Cognito pool | Shared pool allowed self-registration into admin. Now isolated. | Mar 5 |

---

## Deployment

- **API:** `https://s0om0hib4i.execute-api.me-south-1.amazonaws.com`
- **Region:** me-south-1 (Bahrain)
- **Lambda:** 10+ functions, ARM64 Native AOT
- **DynamoDB:** 9+ tables (on-demand billing)
- **Note:** Server code on `telebirr-mock` is NOT YET DEPLOYED (F-14, security fixes pending)

---

**Last Updated:** 2026-03-05 (security hardening — pentest fixes applied)
