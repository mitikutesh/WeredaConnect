# TASK.md - Current Mission

## Current Status

**Phase R4: COMPLETE** (started Feb 17, 2026)
**Branch:** `fix-feat` (current), `telebirr-mock` merged to `main` via PR #15
**Last PO Audit:** 2026-03-19 (validated with automated build/test/security checks)
**MVP Completion:** ~95%
**Launch Target:** Sprint 1 (ship-ready) → Sprint 2 (security) → Sprint 3 (production)
**Sprint Plan:** `specs/features/sprint-plan-2026-03-19.md`

---

## PO Audit Summary (2026-03-19)

### What Changed Since Last Audit (Mar 5)

Branch `telebirr-mock` merged to `main` via PR #15. Subsequent work on `fix-feat`:
- **Role-based visibility fixes** — server-side enforcement on crops/trips APIs
- **Web platform improvements** — response parsing, auth persistence, Dio fixes, trip matching
- **Admin dashboard alignment** — API response keys, escrow names enrichment
- **Playwright E2E tests** — 27 tests for admin dashboard
- **Security hardening** — input validation, auth error mapping, rate limiting fixes
- **Deploy script** — web app build + S3 deploy added
- **AI infrastructure** — centralized completeness rules, agent restructuring

### Audit Corrections (validated findings)

Previous reports listed 7 web bugs — **validation confirmed all were already fixed**:
- Web repos use correct fallback pattern `data['crops'] ?? data['items']`
- `kIsWeb` guard exists on all Isar access paths
- Web matching service fully implemented with API calls
- `AuthorizerFunction.cs` does not exist (false alarm)

**Actual remaining issues**: 3 test failures, 1 hardcoded string, dead JwtValidator code, security gaps.

---

## Phase Summary

| Phase | Status | Completion |
|-------|--------|------------|
| R1: Security Foundation | DONE | Feb 2026 |
| R2: Auth + Data Models | DONE | Feb 14, 2026 |
| R3: Sync + First Feature | DONE (20/20 tasks) | Feb 15, 2026 |
| R4: Trips + Delivery + Escrow + Fraud | DONE | Mar 2026 |

### R4 Feature Status (Verified Mar 19)

| Feature | Server | Mobile | Web | Infra | Tests | Overall |
|---------|--------|--------|-----|-------|-------|---------|
| F-11 Trip Listings | DONE | DONE | DONE | DONE | DONE | 100% |
| F-12 Delivery Verification | DONE (presigned S3) | DONE (5 screens) | DONE | DONE | DONE | 100% |
| F-13 Telebirr Escrow | DONE (mock gateway) | DONE (4 screens) | DONE | DONE | DONE | 100% |
| F-14 Fraud Detection | DONE (5 rules) | DONE (error handling) | N/A | DONE | DONE | 100% |
| F-15 Conflict Resolution | DONE | DONE (4 widgets) | DONE | N/A | DONE (49 tests) | 100% |
| F-18 S3 Logging | DONE | N/A | N/A | DONE | N/A | 100% |

### Build Health (Verified Mar 19)

| Component | Build | Tests | Issues |
|-----------|-------|-------|--------|
| Server (.NET 10) | 0 errors, 0 warnings | 684 pass, 0 fail | Clean |
| Mobile (Flutter) | 0 errors, 0 lint issues | 732 pass, 3 fail | Trip BLoC cancel tests |
| Flutter Web | Builds clean | No dedicated tests | Wasm warnings (expected) |
| Admin (React) | Builds clean | 27 E2E pass | Clean |
| Infra (CDK) | Synths clean | — | Clean |

---

## Sprint Plan

Full details: `specs/features/sprint-plan-2026-03-19.md`

### Sprint 1: Ship-Ready (3-4 days)

| ID | Task | Effort | Status |
|----|------|--------|--------|
| S1-01 | Fix 3 failing trip BLoC tests | 1h | Pending |
| S1-02 | Fix hardcoded payment account string | 30min | Pending |
| S1-03 | Clean up dead JwtValidator code | 30min | Pending |
| S1-04 | Update stale STATE.md and TASK.md | 1h | Pending |
| S1-05 | Deploy to dev environment | 2h | Pending |
| S1-06 | Merge fix-feat to main | 1h | Pending |

### Sprint 2: Security Hardening (5-7 days)

| ID | Task | Effort | Status |
|----|------|--------|--------|
| S2-01 | Activate certificate pinning | 2h | Pending |
| S2-02 | Configure AWS WAF | 3h | Pending |
| S2-03 | Structured audit logging (Phase 1) | 2 days | Pending |
| S2-04 | Audit logging — compliance (Phase 2) | 3 days | Pending |
| S2-05 | Git history purge | 1h | Pending |
| S2-06 | Exit SNS sandbox | 1h + wait | Pending |

### Sprint 3: Production Readiness (2-3 weeks)

| ID | Task | Effort | Status |
|----|------|--------|--------|
| S3-01 | Replace mock Telebirr with real API | TBD | Pending |
| S3-02 | Wire real Cognito auth | 1-2 weeks | Pending |
| S3-03 | Reduce offline session TTL to 48h | 1h | Pending |
| S3-04 | Implement Isar field-level encryption | 4h | Pending |
| S3-05 | Add Flutter web E2E tests | 4h | Pending |
| S3-06 | OTP resend + forgot password | 8h | Pending |

---

## Security Posture: 6.5/10

### Fixed (from pentest 2026-03-05)
- [x] Password policy (min 12, uppercase + symbols)
- [x] Cognito deletion protection
- [x] OAuth implicit flow removed
- [x] Mobile refresh token reduced (30d → 7d)
- [x] CSP unsafe-eval removed
- [x] Health endpoint stripped of version/region
- [x] Separate admin Cognito pool (no self-signup)
- [x] MFA required on admin pool (TOTP)
- [x] CI guard for DEV_FAYDA_BYPASS in production
- [x] CI guard for TELEBIRR_MOCK in production
- [x] version.json blocked from CloudFront
- [x] Localhost CORS stripped for production
- [x] Admin token validity reduced to 15 min

### Unfixed (HIGH priority)
- [ ] **T-SEC-CERT**: Certificate pinning disabled (MITM risk) — Sprint 2
- [ ] **T-SEC-WAF**: AWS WAF not configured — Sprint 2
- [ ] **T-SEC-AUDIT**: Audit logging incomplete (escrow only) — Sprint 2
- [ ] **T-SEC-GIT**: Git history contains exposed private key — Sprint 2
- [ ] **T-SEC-SNS**: SNS in sandbox mode — Sprint 2

### Unfixed (MEDIUM priority)
- [ ] **T-SEC-TTL**: 7-day offline session TTL (too long) — Sprint 3
- [ ] **T-SEC-ISAR**: Isar database unencrypted on disk — Sprint 3
- [ ] **T-SEC-JWT**: Dead JwtValidator with testing bypass — Sprint 1

---

## Completed Features (All Phases)

### R1: Security Foundation
F-01 (runbook), F-02 (gitleaks), F-16 (Lambda deploy), F-19 (CI security)

### R2: Auth + Data Models
F-03 (Cognito + mock), F-04 (Isar encryption key), F-06 (rate limiting), F-07 (6 Isar schemas), F-09 (input validation)

### R3: Sync + First Feature
F-08 (sync engine), F-10 (crop listings), F-17 (least-privilege IAM)

### R4: Transaction Flow
- F-11 Trip Listings
- F-12 Delivery Verification (QR + GPS + photo)
- F-13 Telebirr Escrow (mock gateway + cron)
- F-14 Fraud Detection (5 rules engine)
- F-15 Conflict Resolution UI
- F-18 S3 Access Logging + Versioning

### Bonus (beyond original scope)
- Voice note recording/playback
- Role-based navigation
- Data isolation enforcement
- Admin dashboard (11 pages, React)
- Playwright E2E tests (27 tests)
- Multi-user E2E testing support
- Containerized .NET AOT deployment

---

## Key Decision Log

| Decision | Rationale | Date |
|----------|-----------|------|
| Server-wins conflict resolution | Simplest for solo farmers (rare conflicts) | Feb 21 |
| HMAC-SHA256 over JWT for QR | Smaller payload, better on cheap cameras | Feb 18 |
| 5km GPS threshold | GPS inaccuracy + farm-to-road distance | Feb 18 |
| Mock auth/Telebirr for MVP | Enables E2E testing without vendor dependency | Feb 17 |
| Voice notes (bonus) | Low-literacy users benefit; minimal overhead | Feb 23 |
| Separate admin Cognito pool | Shared pool allowed self-registration into admin | Mar 5 |
| Sprint plan (3 sprints) | Ship-ready → Security → Production | Mar 19 |

---

## Deployment

- **API:** `https://b8xqjg4myb.execute-api.me-south-1.amazonaws.com`
- **Region:** me-south-1 (Bahrain)
- **Lambda:** 15 functions, ARM64 Native AOT
- **DynamoDB:** 12+ tables (on-demand billing)
- **Web:** CloudFront + S3 (webapp + admin + website)

---

**Last Updated:** 2026-03-19 (PO audit with validated findings)
