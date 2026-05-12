# Sprint Plan — PO Audit (2026-03-19)

**Audit Type**: Full project audit — mobile, web, server, infra, security
**Auditor**: PO + Claude Code (automated validation)
**Branch**: `fix-feat`

---

## Validated Project Status

### Build Health (Verified 2026-03-19)

| Component | Build | Tests | Verdict |
|-----------|-------|-------|---------|
| Server (.NET 10) | 0 errors, 0 warnings | **684 pass, 0 fail** | SHIP-READY |
| Mobile (Flutter) | 0 errors, 0 lint issues | **732 pass, 3 fail** | Near-ready |
| Flutter Web | Builds clean | No dedicated tests | Functional |
| Admin Dashboard (React) | Builds clean | 27 Playwright E2E | SHIP-READY |
| CDK Infra | Synths clean | — | SHIP-READY |

### Audit Corrections (Initial Report vs Validation)

| Initial Finding | Validation Result |
|----------------|-------------------|
| 7 web data display bugs | **FALSE** — All fixed. Web repos use correct fallback `data['crops'] ?? data['items']` |
| Isar-on-web crash | **FALSE** — `kIsWeb` guard already in place at `crop_listing_detail_screen.dart:437` |
| Web matching stub returns empty | **FALSE** — Fully implemented, queries real API with pagination |
| AuthorizerFunction security bomb | **FALSE** — No `AuthorizerFunction.cs` exists. `JwtValidator.cs` is dead code, never instantiated |
| 3 failing tests | **CORRECTED** — 2 in trip BLoC + 1 related = 3 total out of 735 |
| Buyer intent in-memory (web) | **TRUE** — by design (web always has connectivity) |
| Hardcoded payment string | **TRUE** — `'1000XXXXXXXXXX'` at line 107 |
| Certificate pinning disabled | **TRUE** — Placeholder hashes, `enableCertPinning=false` |
| WAF missing | **TRUE** — Completely absent from CDK |
| Audit logging partial | **TRUE** — Escrow audit only, zero structured logging elsewhere |
| SNS absent | **TRUE** — No SNS configuration anywhere |
| Isar unencrypted | **TRUE** — Key generated but never used |

### Feature Completeness

| Area | Status | Details |
|------|--------|---------|
| **Server** | 15 Lambda functions, 42 endpoints | 100% complete, AOT-compliant |
| **Mobile** | 30+ screens, 9 BLoCs | All core flows working, offline sync operational |
| **Web** | All platform pairs exist | No active bugs (previous bugfix plan implemented) |
| **Admin** | 11 pages | Feature-complete with separate Cognito pool + MFA |
| **Localization** | 3 languages (EN, AM, OM) | Complete across all screens |
| **Role Visibility** | Server + client enforced | Farmer=own, Trucker=own, Buyer=all |
| **Offline Sync** | Full queue + Isar + conflict resolver | Working with idempotency |

### Security Posture: 6.5/10

**Strong**: Auth (Cognito + JWT), RBAC (server-enforced), Input validation, CORS, Rate limiting, IAM least-privilege, AOT compliance, Security headers

**Gaps (must fix before production)**:
1. Certificate pinning disabled (MITM risk on 3G)
2. AWS WAF missing (no web application firewall)
3. Audit logging incomplete (NBE compliance blocker)
4. Git history contains exposed private key
5. SNS in sandbox (SMS won't work for unverified numbers)

---

## Sprint 1: Ship-Ready (3-4 days)

**Goal**: Clean test suite, fix known UI issues, deploy to dev, merge to main

| ID | Task | Effort | Scope | Depends On |
|----|------|--------|-------|------------|
| S1-01 | Fix 3 failing trip BLoC tests | 1h | @mobile | — |
| S1-02 | Fix hardcoded payment account string | 30min | @mobile @l10n | — |
| S1-03 | Clean up dead JwtValidator code | 30min | @server | — |
| S1-04 | Update stale STATE.md and TASK.md | 1h | @docs | — |
| S1-05 | Deploy to dev environment | 2h | @infra | S1-01, S1-02, S1-03 |
| S1-06 | Merge fix-feat to main | 1h | @infra | S1-01 to S1-04 |

### S1-01: Fix 3 Failing Trip BLoC Tests

**File**: `mobile/test/features/trip_listing/presentation/bloc/trip_listing_bloc_test.dart`

**Failing tests**:
- Line 106: `emits [Loading, Loaded] on CancelTrip` — `Bad state: No element` (trips list empty after cancel)
- Line 333: `CancelTrip then LoadTrips includes cancelled trip` — `Expected: <1> Actual: <0>`
- 1 related test

**Root cause**: Mock repository `getMyTrips()` doesn't return cancelled trip after `cancelTrip()`. Test setup incomplete.

**Fix**: Update mock setup so cancelled trips persist in mock state and are returned on subsequent `getMyTrips()` calls.

**Acceptance**: `flutter test` — 735/735 pass

### S1-02: Fix Hardcoded Payment Account String

**File**: `mobile/lib/features/order/presentation/screens/payment_instructions_screen.dart:107`

**Issue**: `value: '1000XXXXXXXXXX'` — hardcoded CBE account placeholder.

**Fix**: Replace with localized string or server-fetched configuration value. Add key to all 3 ARB files.

### S1-03: Clean Up Dead JwtValidator Code

**File**: `server/src/Auth/JwtValidator.cs`

**Issue**: Contains `isTestingEnvironment` bypass for JWT signature validation. Never instantiated in production (API Gateway uses `HttpUserPoolAuthorizer`), but dangerous dead code.

**Fix**: Delete `JwtValidator.cs` or remove the bypass parameter.

### S1-04: Update Stale Documentation

**Files**: `STATE.md`, `TASK.md`

Update to reflect March 19 validated state with correct test counts and debunked web bugs.

### S1-05: Deploy to Dev Environment

Run `scripts/deploy.sh dev`. Verify health endpoint, test user login, CRUD operations.

### S1-06: Merge fix-feat to Main

PR `fix-feat` → `main`. Verify CI green, review diff, merge.

---

## Sprint 2: Security Hardening (5-7 days)

**Goal**: Close all HIGH security gaps before any external users

| ID | Task | Effort | Scope | Depends On |
|----|------|--------|-------|------------|
| S2-01 | Activate certificate pinning | 2h | @mobile | S1-06 |
| S2-02 | Configure AWS WAF on API Gateway | 3h | @infra | S1-06 |
| S2-03 | Structured audit logging (Phase 1) | 2 days | @server | S1-06 |
| S2-04 | Audit logging — compliance (Phase 2) | 3 days | @server @infra | S2-03 |
| S2-05 | Git history purge | 1h | @human | S1-06 |
| S2-06 | Exit SNS sandbox | 1h + wait | @infra | — |

### S2-01: Activate Certificate Pinning

**Files**: `api_client.dart:54-56`, `cert_pinning_io.dart`

1. Extract real SHA-256 hashes from API Gateway TLS cert
2. Replace `AAAA.../BBBB...` placeholders
3. Set `enableCertPinning = true` for production
4. Test with MITM proxy

### S2-02: Configure AWS WAF

**File**: `infra/lib/stacks/api-stack.ts`

HTTP API v2 doesn't support WAF directly. Options:
- CloudFront in front of HTTP API + WAF WebACL
- Managed rules: CommonRuleSet, SQLiRuleSet, KnownBadInputsRuleSet
- Rate-based rule: 100 req/5min per IP

### S2-03: Structured Audit Logging (Phase 1)

1. `AuditLogger` helper (AOT-safe JSON structured output)
2. `X-Correlation-Id` propagation across all Lambda handlers
3. Auth event logging (login, register, refresh, logout)
4. Admin action logging (suspend, resolve, release)

### S2-04: Audit Logging — Compliance (Phase 2)

1. SHA-256 hash chain on escrow-audit (tamper-evident)
2. CloudWatch Dashboard + 6 alarms
3. Auth-audit + admin-audit DynamoDB tables
4. S3 archival with Object Lock (10-year NBE retention)

### S2-05: Git History Purge

Follow runbook: `docs/runbooks/f01-git-history-purge.md`. Manual execution + force push.

### S2-06: Exit SNS Sandbox

AWS support request for production SNS in me-south-1. Set $10/month spending alarm.

---

## Sprint 3: Production Readiness (2-3 weeks)

**Goal**: Real auth, real payments, production launch

| ID | Task | Effort | Scope | Depends On |
|----|------|--------|-------|------------|
| S3-01 | Replace mock Telebirr with real API | TBD | @server | S2-01, S2-02, S2-03 |
| S3-02 | Wire real Cognito auth | 1-2 weeks | @server @mobile | S2-01, S2-02, S2-03 |
| S3-03 | Reduce offline session TTL to 48h | 1h | @mobile | — |
| S3-04 | Implement Isar field-level encryption | 4h | @mobile | — |
| S3-05 | Add Flutter web E2E tests | 4h | @e2e | — |
| S3-06 | OTP resend + forgot password | 8h | @server @mobile | — |

### S3-01: Replace Mock Telebirr

Obtain Telebirr merchant credentials. Replace `MockTelebirrGateway.cs` with real HTTP integration. E2E test against sandbox.

### S3-02: Wire Real Cognito Auth

Configure real Cognito pool IDs via SSM. Test SMS OTP to +251 numbers. Add voice OTP fallback.

### S3-03: Reduce Offline Session TTL

Change `_offlineSessionTtl` from 7 days to 48 hours. Add exponential backoff.

### S3-04: Isar Field-Level Encryption

Use already-generated encryption key + `cryptography` package. Encrypt PII fields (phone, name, faydaId).

### S3-05: Add Web E2E Tests

Playwright tests for Flutter web: login, CRUD, role isolation for all 3 roles.

### S3-06: OTP Resend + Forgot Password

`POST /auth/resend-otp` (rate limited), `POST /auth/forgot-password` + reset flow, 2 new screens, 3 languages.

---

## Risk Register

| Risk | Likelihood | Impact | Mitigation |
|------|-----------|--------|------------|
| Telebirr API unavailable | MEDIUM | Sprint 3 blocked | Keep mock gateway for demo |
| SNS approval delayed | MEDIUM | Can't send OTP | Voice OTP, manual verification |
| Ethio Telecom SMS unreliable | HIGH | Users can't register | Voice OTP, WhatsApp fallback |
| Flutter web bundle size (~30MB) | LOW | Slow on 3G | Consider HTML renderer |
| WAF not available on HTTP API v2 in me-south-1 | MEDIUM | Architecture change needed | CloudFront proxy approach |

## Definition of Done (per task)

- [ ] Code compiles: 0 errors, 0 warnings
- [ ] All tests pass (684 server + 735 mobile target)
- [ ] New code has corresponding tests
- [ ] All 3 roles verified (Farmer, Trucker, Buyer)
- [ ] Both platforms verified (mobile + web build)
- [ ] 3 language files updated (if UI strings changed)
- [ ] Platform pair counterparts updated (if applicable)

---

## Future Backlog (Post Sprint 3)

| Item | Effort | Notes |
|------|--------|-------|
| VeriFayda 2.0 OIDC integration | 2-3 weeks | NBE mandate for >10K ETB transactions |
| SyncFunction.cs refactor | 5h | 3,131 lines — extract per-entity handlers |
| DynamoDB KMS encryption | 1h | Customer-managed keys for compliance |
| DynamoDB table consolidation | 2h | Reduce table count for cost savings |
| Admin authenticated E2E tests | 3h | Requires admin Cognito test user |
| Web buyer intent persistence | 2h | localStorage for session survival |

---

**Last Updated**: 2026-03-19
**Author**: PO Audit (validated with automated build/test/security checks)
