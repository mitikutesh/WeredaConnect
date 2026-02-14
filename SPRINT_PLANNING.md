# WeredaConnect Sprint Planning - Sprint 3

**Date**: 2026-02-14
**PO**: Product Owner Assessment
**Branch**: `Authentication-Encryptionministries`
**Last Sprint End**: 2026-02-13
**Sprint Duration**: 2 weeks (Feb 14 - Feb 28, 2026)

---

## 📊 Current State Assessment

### What's Actually Implemented ✅

#### Server-Side (Strong Foundation)
| Component | Status | Evidence |
|-----------|--------|----------|
| **Authentication Logic** | ✅ COMPLETE | 169 tests passing |
| └─ RegisterFunction | ✅ | tests/Functions/RegisterFunctionTests.cs |
| └─ VerifyOtpFunction | ✅ | tests/Functions/VerifyOtpFunctionTests.cs |
| └─ LoginFunction | ✅ | tests/Functions/LoginFunctionTests.cs |
| └─ RefreshFunction | ✅ | tests/Functions/RefreshFunctionTests.cs |
| └─ LogoutFunction | ✅ | tests/Functions/LogoutFunctionTests.cs |
| └─ AuthorizerFunction | ✅ | src/Functions/AuthorizerFunction.cs |
| **Rate Limiting** | ✅ COMPLETE | RateLimitService + Repository + tests |
| **Input Validation** | ✅ COMPLETE | RequestValidator with Ethiopian phone format |
| **JWT Validation** | ✅ COMPLETE | JwtValidator.cs |
| **DynamoDB Integration** | ✅ COMPLETE | UserRepository with manual marshalling (AOT-safe) |
| **AOT Bootstrap** | ✅ COMPLETE | Program.cs with FUNCTION_HANDLER routing |

#### Infrastructure (Partially Complete)
| Component | Status | Evidence |
|-----------|--------|----------|
| **CDK Stacks** | ✅ COMPLETE | 11 tests passing |
| └─ DatabaseStack | ✅ | Users, Trips, Crops, RateLimits tables |
| └─ ApiStack | ✅ | Cognito User Pool, Lambda functions, API Gateway |
| └─ Lambda Constructs | ✅ | lambda-construct.ts with IAM roles |
| **DynamoDB Tables** | ⚠️ PARTIAL | **Missing: Deliveries, Payments, SyncMetadata** |
| **Lambda Deployment** | ❌ NOT DONE | CDK code exists but **NOT deployed to AWS** |
| **Deployment Scripts** | ✅ COMPLETE | scripts/deploy.sh |

#### Mobile (Critical Gaps)
| Component | Status | Evidence |
|-----------|--------|----------|
| **UI Screens** | ✅ BASIC | Home, Listings, Profile, Settings (navigation only) |
| **Localization** | ✅ COMPLETE | Amharic, Afan Oromo, English .arb files |
| **Isar Schemas** | ❌ MISSING | mobile/lib/core/database/ is EMPTY |
| **Auth Service** | ❌ MISSING | mobile/lib/core/auth/ is EMPTY |
| **Encryption** | ❌ MISSING | No Android Keystore integration |
| **Network Layer** | ❌ MISSING | mobile/lib/core/network/ is EMPTY |
| **Sync Engine** | ❌ MISSING | mobile/lib/core/sync/ is EMPTY |
| **BLoC State** | ❌ MISSING | No state management |

#### Security (Incomplete)
| Component | Status | Evidence |
|-----------|--------|----------|
| **Pre-commit Hooks** | ✅ COMPLETE | .gitleaks.toml, .pre-commit-config.yaml |
| **Git History Purge** | ❌ CRITICAL | Private key still in git history (remote) |
| **CI/CD Security Scans** | ❌ MISSING | No gitleaks/vuln scanning in workflows |

---

## 📈 Roadmap Progress vs. Reality

### Phase R1: Security Foundation (Week 1-2) — **50% Complete**
| Feature | Status | Blocker |
|---------|--------|---------|
| F-01: Purge private key | ❌ 0% | Git history not force-pushed |
| F-02: Pre-commit hooks | ✅ 100% | Done |
| F-16: Lambda deployment | ⚠️ 70% | CDK ready, **NOT deployed to AWS** |
| F-19: CI/CD security scans | ❌ 0% | Workflows don't scan |

**PO Assessment**: R1 cannot be closed until F-01 and F-16 are ACTUALLY deployed.

### Phase R2: Auth + Data Models (Week 3-5) — **50% Complete**
| Feature | Status | Server | Mobile | Infra |
|---------|--------|--------|--------|-------|
| F-07: Core domain models | ⚠️ 33% | N/A | ❌ 0% (no Isar) | ⚠️ 50% (3 tables missing) |
| F-04: Isar encryption | ❌ 0% | N/A | ❌ 0% | N/A |
| F-03: Auth (Cognito+JWT) | ⚠️ 60% | ✅ 100% | ❌ 0% | ✅ 100% |
| F-06: Rate limiting | ✅ 100% | ✅ 100% | N/A | ✅ 100% |
| F-09: Input validation | ✅ 100% | ✅ 100% | N/A | N/A |
| F-05: Cert pinning | ❌ 0% | N/A | ❌ 0% | N/A |

**PO Assessment**: Server-side is excellent. Mobile-side has ZERO implementation. This is a **major blocker** for user-testable features.

### Phase R3-R6 — **0% Complete**
Not started. Blocked by R1 + R2 completion.

---

## 🎯 Sprint 3 Goals (Feb 14 - Feb 28, 2026)

### Primary Objective
**Close Phase R1 and achieve 80%+ completion of Phase R2** by focusing on critical security gaps and mobile foundation.

### Sprint Theme
"**Deploy and Secure the Backend, Lay Mobile Foundation**"

---

## 📋 Sprint 3 Backlog (Prioritized)

### Priority Group A: Security & Deployment (MUST DO)
**These are production-blocking. Complete by mid-sprint (Feb 21).**

#### A1: F-01 — Purge Private Key from Git History 🔥 CRITICAL
**Priority**: P0 (BLOCKER)
**Effort**: 2 hours
**Owner**: @Security
**Acceptance Criteria**:
- [ ] Run `git filter-branch` or BFG Repo-Cleaner to remove `server/localstack-data/cache/server.test.pem.key`
- [ ] Force-push cleaned history: `git push --force-with-lease --all`
- [ ] Verify key no longer appears in remote history
- [ ] Document key rotation procedure in `.agents/security-sop.md`
- [ ] Notify team (if collaborators exist) to re-clone

**Why Now**: Anyone who clones the repo has the private key. This is a ticking time bomb.

---

#### A2: F-16 — Deploy Lambda to AWS Dev Environment
**Priority**: P0 (BLOCKER)
**Effort**: 1 day
**Owner**: @Infra + @Server
**Acceptance Criteria**:
- [ ] Verify `dotnet publish -c Release -r linux-arm64 --self-contained` produces valid AOT binary
- [ ] Test binary locally with `dotnet exec bootstrap.dll`
- [ ] Deploy HealthCheckFunction to AWS dev using `scripts/deploy.sh dev`
- [ ] Invoke deployed function and verify 200 response
- [ ] Measure cold start and confirm **<2 seconds**
- [ ] Document cold start measurement in `docs/performance/cold-start-metrics.md`

**Blocker Check**:
- AWS credentials configured? (Run `aws sts get-caller-identity`)
- .NET 10 AOT publish works? (Test locally first)

**Why Now**: No backend = no API testing. This blocks ALL integration work.

---

#### A3: F-19 — Security Scanning in CI/CD
**Priority**: P1 (HIGH)
**Effort**: 4 hours
**Owner**: @Infra
**Acceptance Criteria**:
- [ ] Add gitleaks step to `.github/workflows/pr-validation.yml`:
  ```yaml
  - uses: gitleaks/gitleaks-action@v2
    env:
      GITHUB_TOKEN: ${{ secrets.GITHUB_TOKEN }}
  ```
- [ ] Add `dotnet list package --vulnerable --include-transitive` to `server-tests.yml` (fail on CRITICAL/HIGH)
- [ ] Add `npm audit --audit-level=high` to `infra-tests.yml` (fail on HIGH+)
- [ ] Test by creating PR with known vulnerable package (then revert)
- [ ] Verify all workflows are valid YAML

**Why Now**: Prevents future security debt as dependencies are added.

---

### Priority Group B: Mobile Foundation (SPRINT FOCUS)
**Mobile is currently 0% implemented. This sprint lays the foundation.**

#### B1: F-07 (Mobile) — Create Isar Schemas
**Priority**: P0 (BLOCKER for all mobile features)
**Effort**: 2 days
**Owner**: @Mobile
**Acceptance Criteria**:
- [ ] Create `mobile/lib/core/database/schemas/user.dart` (8 fields + indexes)
- [ ] Create `mobile/lib/core/database/schemas/crop_listing.dart` (11 fields + indexes)
- [ ] Create `mobile/lib/core/database/schemas/trip.dart` (10 fields + indexes)
- [ ] Create `mobile/lib/core/database/schemas/delivery.dart` (9 fields + indexes)
- [ ] Create `mobile/lib/core/database/schemas/payment.dart` (8 fields + indexes)
- [ ] Create `mobile/lib/core/database/schemas/sync_operation.dart` (9 fields)
- [ ] Run `dart run build_runner build` and verify `.g.dart` files generate
- [ ] Write integration test: open Isar, create User record, query back
- [ ] Document schema design decisions in `specs/contracts/data-models.md`

**Field Reference**: See ROADMAP.md lines 228-235 for schema definitions.

**Why Now**: Every mobile feature depends on data models. This is the foundational layer.

---

#### B2: F-04 — Isar Database Encryption
**Priority**: P0 (MUST enable before user data)
**Effort**: 1 day
**Owner**: @Mobile
**Dependencies**: B1 (Isar schemas)
**Acceptance Criteria**:
- [ ] Add `flutter_secure_storage: ^9.0.0` to `pubspec.yaml`
- [ ] Implement `_getEncryptionKey()` in `mobile/lib/core/database/isar_service.dart`:
  ```dart
  final storage = FlutterSecureStorage(aOptions: AndroidOptions(encryptedSharedPreferences: true));
  final key = await storage.read(key: 'isar_key') ?? generateKey();
  ```
- [ ] Pass encryption key to `Isar.open()`: `encryptionCipher: IsarCipher(base64Decode(key))`
- [ ] Write test: verify DB file is binary (not JSON-readable)
- [ ] Test key persistence across app restarts
- [ ] Document key rotation procedure in `specs/decisions/encryption-strategy.md`

**Why Now**: Retrofitting encryption after data exists is painful. Enable now, populate later.

---

#### B3: F-03 (Mobile) — Auth Service + Login UI
**Priority**: P1 (HIGH)
**Effort**: 3 days
**Owner**: @Mobile
**Dependencies**: B2 (encryption for token storage)
**Acceptance Criteria**:

**Phase 1: Service Layer (1.5 days)**
- [ ] Create `mobile/lib/core/auth/auth_service.dart` with methods:
  - `Future<AuthResult> register(String phone, String password, String name)`
  - `Future<AuthResult> verifyOtp(String phone, String otp)`
  - `Future<AuthResult> loginOnline(String phone, String password)`
  - `Future<AuthResult> loginOffline(String phone, String password)`
  - `Future<void> logout()`
- [ ] Create `mobile/lib/core/auth/offline_auth_cache.dart`:
  - Store PBKDF2 hash of password (100,000 iterations, 32-byte salt)
  - Store userId, phoneNumber, role (encrypted via Isar)
- [ ] Integrate `flutter_secure_storage` for token persistence (access + refresh tokens)
- [ ] Write unit tests: online login, offline login, token refresh, logout

**Phase 2: UI Layer (1.5 days)**
- [ ] Create `mobile/lib/features/auth/screens/login_screen.dart`:
  - Ethiopian phone input (+251 prefix auto-filled)
  - Password input with visibility toggle
  - "Offline mode" switch (for testing)
  - Error messages in Amharic/Oromo/English
  - Large touch targets (44x44 minimum)
- [ ] Create `mobile/lib/features/auth/screens/register_screen.dart`:
  - Phone, name, password, role selection (farmer/trucker/buyer)
  - OTP verification step
- [ ] Create `mobile/lib/features/auth/bloc/auth_bloc.dart` with states:
  - AuthInitial, AuthLoading, AuthAuthenticated, AuthUnauthenticated, AuthError
- [ ] Integration test: register → verify OTP → login → logout

**Why Now**: Users need to register and log in before ANY other feature is usable.

---

#### B4: F-07 (Infra) — Add Missing DynamoDB Tables
**Priority**: P1 (HIGH)
**Effort**: 3 hours
**Owner**: @Infra
**Acceptance Criteria**:
- [ ] Add Deliveries table to `infra/lib/stacks/database-stack.ts`:
  - PK: `deliveryId`, SK: `tripId`
  - Attributes: listingId, qrToken, status, timestamps, version
- [ ] Add Payments table:
  - PK: `paymentId`, SK: `deliveryId`
  - Attributes: amount, telebirrRef, status, timestamps
- [ ] Add SyncMetadata table:
  - PK: `idempotencyKey`, SK: `timestamp`
  - TTL attribute for cleanup (7 days)
- [ ] Add GSIs:
  - CropsTable: `StatusCreatedAtIndex` (status + createdAt for filtering active listings)
  - TripsTable: `StatusDepartureDateIndex` (status + departureDate for upcoming trips)
- [ ] Update CDK tests to verify new tables exist
- [ ] Run `cdk synth` and verify CloudFormation template

**Why Now**: Sync engine (next sprint) depends on SyncMetadata table. Delivery/Payment flow (R4) needs these tables.

---

### Priority Group C: Nice-to-Have (If Time Permits)
**Only tackle these if A1-A3 and B1-B4 are DONE by Feb 24.**

#### C1: F-05 — Certificate Pinning (Mobile)
**Priority**: P2 (MEDIUM)
**Effort**: 4 hours
**Owner**: @Mobile
**Dependencies**: B3 (need API domain to pin against)
**Acceptance Criteria**:
- [ ] Add `dio: ^5.4.0` to `pubspec.yaml`
- [ ] Configure `IOHttpClientAdapter` in `mobile/lib/core/network/api_client.dart`:
  ```dart
  dio.httpClientAdapter = IOHttpClientAdapter(
    onHttpClientCreate: (_) {
      final client = HttpClient();
      client.badCertificateCallback = (cert, host, port) {
        // Pin SHA-256 hash of API Gateway/CloudFront cert
        return cert.sha256 == expectedPin;
      };
      return client;
    },
  );
  ```
- [ ] Extract API Gateway cert SHA-256 hash after F-16 deployment
- [ ] Add backup pin for cert rotation
- [ ] Document cert rotation procedure in `specs/decisions/cert-pinning.md`

**Why Now**: 3G networks in Ethiopia are susceptible to MITM. Auth tokens need protection.

---

#### C2: Update Auth API Contract
**Priority**: P2 (DOCUMENTATION)
**Effort**: 2 hours
**Owner**: @Architect
**Acceptance Criteria**:
- [ ] Review `specs/contracts/auth-api.md` (672 lines)
- [ ] Add mobile-specific sections:
  - Offline auth flow (cached credentials)
  - Token refresh strategy
  - Error handling for poor connectivity
- [ ] Add sequence diagrams for register + login flows
- [ ] Link to Ethiopian phone format spec

**Why Now**: Mobile team needs contract reference for B3.

---

## 🚫 Sprint 3 Blockers & Mitigation

| Blocker | Impact | Probability | Mitigation Strategy |
|---------|--------|-------------|---------------------|
| **Flutter SDK not installed** | 🔴 HIGH (blocks B1-B4) | 100% (KNOWN) | **INSTALL BEFORE SPRINT START** (1 hour setup) |
| **AWS credentials missing** | 🔴 HIGH (blocks A2) | MEDIUM | Configure AWS CLI with dev account on Day 1 |
| **.NET AOT publish failure** | 🟡 MEDIUM (blocks A2) | LOW | Test locally before AWS deploy |
| **Cognito SMS delivery in Ethiopia** | 🟡 MEDIUM (blocks B3 integration test) | MEDIUM | Use test phone numbers initially; plan voice OTP fallback |
| **Solo developer burnout** | 🔴 HIGH (long-term) | MEDIUM | Strict WIP limits: max 2 tasks in progress. Take weekends off. |

---

## 📊 Sprint Success Metrics

### Must-Have (Sprint Cannot Close Without These)
- [ ] ✅ Zero secrets in git history (verified with `git log --all --full-history --source -- "*pem*"`)
- [ ] ✅ HealthCheckFunction deployed to AWS and responding <2s cold start
- [ ] ✅ All 6 Isar schemas generating without errors
- [ ] ✅ Isar encryption enabled and tested
- [ ] ✅ User can register with Ethiopian phone (+251XXXXXXXXX)
- [ ] ✅ User can log in online (Cognito)
- [ ] ✅ User can log in offline (cached credentials)

### Should-Have (High Value)
- [ ] ✅ CI/CD security scans running on every PR
- [ ] ✅ All 9 DynamoDB tables deployed (Users, Trips, Crops, Deliveries, Payments, SyncMetadata, RateLimits + 2 GSIs)
- [ ] ✅ Auth tokens stored in flutter_secure_storage with Android Keystore backing

### Could-Have (If Time Permits)
- [ ] ✅ Certificate pinning configured
- [ ] ✅ Auth API contract updated with mobile flows
- [ ] ✅ End-to-end test: register → verify → login → logout (automated)

---

## 🔄 Definition of Done (Sprint Level)

A feature is "DONE" when:
1. ✅ Code is written and merged to `Authentication-Encryptionministries` branch
2. ✅ Tests are passing (unit + integration where applicable)
3. ✅ Localized in all 3 languages (if user-facing)
4. ✅ Works offline (if applicable)
5. ✅ Documented in `specs/` or contract files
6. ✅ Reviewed against CLAUDE.md constraints (offline-first, low-end devices, data efficiency)

**Sprint is "DONE" when**:
- All Must-Have metrics achieved
- Phase R1 can be declared COMPLETE (100%)
- Phase R2 is 80%+ complete (mobile foundation exists)
- TASK.md updated with remaining R2 work
- STATE.md updated with sprint results

---

## 📅 Sprint Timeline (Recommended)

### Week 1 (Feb 14-21): Security & Deployment
**Focus**: Close R1 gaps

| Day | Tasks | Owner |
|-----|-------|-------|
| **Fri 14** | Setup day: Install Flutter SDK, verify AWS creds, plan tasks | Developer |
| **Mon 17** | A1: Purge git history (2hr) → A2: .NET AOT publish test (3hr) | @Security + @Infra |
| **Tue 18** | A2: Deploy Lambda to AWS (full day) | @Infra + @Server |
| **Wed 19** | A3: CI/CD security scans (4hr) → B4: DynamoDB tables (3hr) | @Infra |
| **Thu 20** | B1: Isar schemas (Day 1 of 2) — User, CropListing, Trip | @Mobile |
| **Fri 21** | B1: Isar schemas (Day 2 of 2) — Delivery, Payment, SyncOperation + tests | @Mobile |

**Week 1 Exit Criteria**: R1 100% complete, Isar schemas exist.

### Week 2 (Feb 24-28): Mobile Foundation
**Focus**: Encryption + Auth

| Day | Tasks | Owner |
|-----|-------|-------|
| **Mon 24** | B2: Isar encryption (full day) | @Mobile |
| **Tue 25** | B3: Auth service layer (Day 1 of 3) | @Mobile |
| **Wed 26** | B3: Auth service layer (Day 2 of 3) | @Mobile |
| **Thu 27** | B3: Login/Register UI (Day 3 of 3) | @Mobile |
| **Fri 28** | Buffer day: Polish, integration testing, sprint review | All |

**Week 2 Exit Criteria**: User can register and log in (online + offline).

---

## 🎬 Sprint Kickoff Checklist

Before starting Sprint 3, verify:
- [ ] Flutter SDK installed (`flutter doctor` shows no blocking issues)
- [ ] AWS CLI configured (`aws sts get-caller-identity` succeeds)
- [ ] Node.js 20.x LTS installed (`node --version`)
- [ ] .NET 10 SDK installed (`dotnet --version`)
- [ ] Git history purge plan approved (F-01 is destructive)
- [ ] Team understands sprint goals (even if solo, review this doc)
- [ ] TASK.md updated with Sprint 3 backlog

---

## 📝 Post-Sprint Activities (Feb 28)

1. **Update STATE.md**:
   - Current checkpoint timestamp
   - Phase R1 status (should be 100%)
   - Phase R2 status (should be 80%+)
   - Blockers for next sprint

2. **Update TASK.md**:
   - Move completed tasks to "Completed" section
   - Add remaining R2 tasks (F-05 if not done, plus R3 prep)
   - Update phase status summary

3. **Update ROADMAP.md**:
   - Mark R1 as COMPLETE
   - Update R2 progress percentages
   - Adjust timeline if behind schedule

4. **Create Sprint Retrospective**:
   - What went well?
   - What blocked us?
   - What should we do differently in Sprint 4?
   - Document in `docs/retrospectives/sprint-3.md`

---

## 🚀 Looking Ahead: Sprint 4 Preview

**Likely Sprint 4 Goals (Mar 1-14)**:
- Complete remaining R2 work (F-05 cert pinning if not done)
- Start F-08: Sync engine foundation
  - Phase A: Local queue (SyncQueue + ConnectivityMonitor)
  - Phase B: Sync executor with retry logic
- Start F-10: Crop listing flow (mobile UI + BLoC)
- Create sync protocol integration tests

**Dependencies**:
- Sprint 3 must achieve 80%+ R2 completion
- Isar schemas must be stable
- Auth must work end-to-end

---

**Document Version**: 1.0.0
**Created**: 2026-02-14
**Sprint Goal**: Deploy and Secure the Backend, Lay Mobile Foundation
**Next Review**: 2026-02-28 (Sprint Retrospective)
