# 🎯 TASK.md - Current Mission

## Purpose
This file tracks the **active work** for the current development cycle. It's the single source of truth for "what am I doing right now?"

---

## 🎯 Current Sprint Goal

**Sprint 3: Deploy and Secure the Backend, Lay Mobile Foundation (Feb 14 - Feb 28, 2026)**

Close Phase R1 (100% completion) and achieve 80%+ completion of Phase R2 by:
1. Eliminating critical security gaps (git history purge, Lambda deployment, CI/CD scanning)
2. Building mobile foundation (Isar schemas, encryption, auth service)
3. Enabling user registration and login (online + offline)

**Branch:** `Authentication-Encryptionministries`
**Sprint Plan**: `SPRINT_PLANNING.md`
**Roadmap Reference**: `specs/features/ROADMAP.md` — Phase R1 (Week 1-2) + R2 (Week 3-5)

---

## 📊 Phase Status Summary

| Phase | Status | Done | Remaining |
|-------|--------|------|-----------|
| **R1: Security Foundation** | INCOMPLETE | 2/4 | F-01 history purge, F-19 CI/CD scanning |
| **R2: Auth + Data Models** | INCOMPLETE | 3/6 (server done) | F-07 mobile models, F-04 Isar encryption, F-03 mobile auth, F-05 cert pinning |

---

## 📋 Completed Tasks

> **Task Claiming:** All tasks use `@Scope` tags per `.agents/task-protocol.md`. Claim a task by adding your scope tag and committing before starting work.

### Phase 1-8: Foundation through CI/CD ✅
<details>
<summary>Click to expand completed phases 1-8</summary>

#### Phase 1: Foundation ✅
- [x] `@Architect` Define project structure requirements
- [x] `@Architect` Create root governance files (CLAUDE.md, STATE.md, TASK.md)
- [x] `@Architect` Create .ai/ governance directory
- [x] `@Architect` Create .agents/ directory for agent SOPs
- [x] `@Architect` Create docs/ structure with local_reality/
- [x] `@Architect` Create specs/ directory for feature tickets

#### Phase 2: Mobile Structure ✅
- [x] `@Mobile` Create mobile/ directory
- [x] `@Mobile` Create mobile/lib/core/ (sync engine, Isar)
- [x] `@Mobile` Create mobile/lib/features/
- [x] `@Mobile` Create mobile/lib/shared/
- [x] `@Localization` Create mobile/lib/shared/l10n/ (Amharic/Oromo/English)
- [x] `@Localization` Add sample .arb files for localization

#### Phase 3: Server Structure ✅
- [x] `@Server` Create server/ directory
- [x] `@Server` Create server/AOT_COMPLIANCE.md
- [x] `@Server` Create server/src/ structure
- [x] `@Server` Add .NET project scaffolding notes

#### Phase 4: Infrastructure & Automation ✅
- [x] `@Infra` Create infra/ directory for AWS CDK
- [x] `@Infra` Create scripts/ directory
- [x] `@Infra` Create scripts/checkpoint.sh automation
- [x] `@Infra` Make checkpoint.sh executable
- [x] `@Security` Create qa/ directory for threat models

#### Phase 5: Documentation ✅
- [x] `@Architect` Create .ai/agents.md (role definitions)
- [x] `@Architect` Create .ai/standards.md (coding patterns)
- [x] `@Architect` Create .ai/tech_stack.md (framework versions)
- [x] `@Architect` Create docs/local_reality/README.md (Ethiopian context)

#### Phase 6: Audit & Consistency ✅
- [x] `@Architect` Audit all documentation for consistency
- [x] `@Architect` Fix civic engagement → agritech logistics across all files
- [x] `@Architect` Standardize naming (server scope, branch prefixes)
- [x] `@Architect` Rewrite user docs for agritech domain
- [x] `@Architect` Rewrite API reference for agritech domain
- [x] `@Architect` Fix broken relative links in docs/developer/
- [x] `@Architect` Create missing directories (specs/features, qa/threat_models, etc.)
- [x] `@Architect` Standardize cold start target to <2 seconds
- [x] `@Architect` Fix DynamoDB table design (multi-table, not single-table)
- [x] `@Architect` Align agent roles between agents.md and project-background.md
- [x] `@Architect` Fix Amharic LTR (not RTL) in system-architecture.md
- [x] `@Architect` Fix WIP commit format in session_handoff.md

#### Phase 7: Project Initialization ✅
- [x] `@Mobile` Initialize Flutter project in `mobile/`
- [x] `@Mobile` Create pubspec.yaml with offline-first dependencies
- [x] `@Mobile` Create Android build configuration (API 26+, ARM64)
- [x] `@Mobile` Create main.dart with l10n support
- [x] `@Localization` Update l10n files with missing keys (appTitle, offlineReady)
- [x] `@Server` Initialize .NET 10 project in `server/`
- [x] `@Server` Create WeredaConnect.Server.csproj with AOT configuration
- [x] `@Server` Create HealthCheckFunction with cold start tracking
- [x] `@Server` Create test project with xUnit and NSubstitute
- [x] `@Infra` Initialize CDK project in `infra/`
- [x] `@Infra` Create CDK stack with DynamoDB tables and S3 bucket
- [x] `@Infra` Configure TypeScript and CDK synth

#### Phase 8: CI/CD & Security Audit ✅
- [x] `@Infra` Create PR validation workflow (`pr-validation.yml`)
- [x] `@Infra` Create mobile test workflow (`mobile-tests.yml`)
- [x] `@Infra` Create server test workflow (`server-tests.yml`)
- [x] `@Infra` Create infra test workflow (`infra-tests.yml`)
- [x] `@Security` Conduct comprehensive security audit (40 findings documented)
- [x] `@Security` Harden Android security configuration
- [x] `@Server` Add security headers to Lambda responses
- [x] `@Architect` Create product roadmap (`specs/features/ROADMAP.md`)

</details>

### Phase 9: Server-Side Authentication (F-03 server) ✅
- [x] `@Server` Create `AuthResult.cs` generic result type (Success/Failure)
- [x] `@Server` Create `LambdaResponse.cs` AOT-safe response builder with security headers
- [x] `@Server` Create `CognitoAuthService.cs` Cognito SDK wrapper with AUTH_* error codes
- [x] `@Server` Create `ICognitoAuthService.cs` interface
- [x] `@Server` Create `UserRepository.cs` manual DynamoDB marshalling (AOT-safe)
- [x] `@Server` Create `IUserRepository.cs` interface
- [x] `@Server` Create `UserDto.cs` and `AuthDtos.cs` models
- [x] `@Server` Create `RegisterFunction.cs` Lambda handler
- [x] `@Server` Create `VerifyOtpFunction.cs` Lambda handler
- [x] `@Server` Create `LoginFunction.cs` Lambda handler
- [x] `@Server` Create `RefreshFunction.cs` Lambda handler
- [x] `@Server` Create `LogoutFunction.cs` Lambda handler
- [x] `@Server` Create `AuthorizerFunction.cs` JWT validator Lambda
- [x] `@Server` Create `JwtValidator.cs` AOT-compatible JWT validation
- [x] `@Server` Create `RequestValidator.cs` input validation (F-09)
- [x] `@Server` Create `AppJsonContext.cs` source-generated JSON serialization
- [x] `@Server` Create unit tests for all auth handlers (169 tests passing)
- [x] `@Server` Create `RateLimitService.cs` + `RateLimitRepository.cs` (F-06)
- [x] `@Server` Create rate limiting unit tests

### Phase R1: Security Foundation — Partial
- [x] `@Security` F-02: Install gitleaks + pre-commit hooks (`.gitleaks.toml`, `.pre-commit-config.yaml`)
- [x] `@Infra` F-16: CDK api-stack with Cognito, HealthCheck Lambda, auth Lambda functions, API Gateway routes, throttling
- [x] `@Infra` F-16: CDK constructs with per-function least-privilege IAM, ARM64, 512MB
- [x] `@Server` F-16: `Program.cs` AOT bootstrap entry point with handler routing
- [x] `@Infra` F-16: `scripts/deploy.sh` deployment script created
- [x] `@Infra` CDK tests fixed — unit, integration, snapshot (11 tests passing)

### Phase R2: Server-Side Complete
- [x] `@Server` F-03: All 5 auth Lambda handlers with 169 passing tests
- [x] `@Server` F-06: Rate limiting — `RateLimitService` with 4 rules + API Gateway throttling
- [x] `@Server` F-09: Input validation — `RequestValidator` with AOT-safe `GeneratedRegex`
- [x] `@Architect` Auth API contract — `specs/contracts/auth-api.md` (672 lines)

---

## 🚧 Sprint 3 Active Tasks — Prioritized Backlog

**Sprint Duration**: Feb 14 - Feb 28, 2026
**Full Sprint Plan**: See `SPRINT_PLANNING.md` for detailed acceptance criteria and timeline

### Priority Group A: Security & Deployment (MUST DO — Week 1)

#### A1: F-01 — Force-Push Cleaned Git History
> Private key removed from working tree but git history NOT purged on remote.
- [ ] `@Security` Force-push cleaned git history: `git push --force-with-lease --all`
- [ ] `@Security` Notify any collaborators to re-clone after force-push

#### A2: F-16 — Lambda Deployment Verification
> CDK code exists and tests pass. Never deployed to AWS.
- [ ] `@Infra` Verify `dotnet publish -c Release -r linux-arm64` produces valid AOT binary
- [ ] `@Infra` Deploy HealthCheckFunction to AWS dev environment using `scripts/deploy.sh dev`
- [ ] `@Infra` Measure cold start and confirm <2s

#### A3: F-19 — Security Scanning in CI/CD
> Dependabot exists but NO inline security scanning in GitHub Actions workflows.
- [ ] `@Infra` Add `dotnet list package --vulnerable --include-transitive` step to `server-tests.yml` — fail on CRITICAL/HIGH
- [ ] `@Infra` Add `npm audit --audit-level=high` step to `infra-tests.yml` — fail on HIGH+
- [ ] `@Infra` Add gitleaks secret scanning step to `pr-validation.yml` using `gitleaks/gitleaks-action@v2`
- [ ] `@Infra` Verify all workflow YAML is valid and references correct action versions

**R1 Exit Criteria:**
- [ ] Zero secrets in git history (remote)
- [ ] Pre-commit hooks catch test secrets ✅ (already done)
- [ ] Lambda cold start <2s (measured on AWS)
- [ ] CI/CD pipeline runs security scans on every PR

---

### Priority Group B: Mobile Foundation (SPRINT FOCUS — Week 1-2)

#### B1: F-07 — Core Domain Models (Isar Schemas)
> Server DTOs exist. Mobile `mobile/lib/core/database/` is EMPTY. DynamoDB missing 3 tables.
- [ ] `@Mobile` Create Isar schema: `User` (userId, phoneNumber, name, faydaId, role, woreda, timestamps, version)
- [ ] `@Mobile` Create Isar schema: `CropListing` (listingId, farmerId, cropType, quantity, price, location, status, timestamps, version)
- [ ] `@Mobile` Create Isar schema: `Trip` (tripId, truckerId, route, date, capacity, price, status, matchedListings, timestamps, version)
- [ ] `@Mobile` Create Isar schema: `Delivery` (deliveryId, tripId, listingId, qrToken, GPS, photo, status, timestamps, version)
- [ ] `@Mobile` Create Isar schema: `Payment` (paymentId, deliveryId, farmerId, buyerId, amount, telebirrRef, status, timestamps, version)
- [ ] `@Mobile` Create Isar schema: `SyncOperation` (operationId, idempotencyKey, entityType, entityId, action, data, status, retryCount, timestamps)
- [ ] `@Infra` Add Deliveries DynamoDB table to `database-stack.ts`
- [ ] `@Infra` Add Payments DynamoDB table to `database-stack.ts`
- [ ] `@Infra` Add SyncMetadata DynamoDB table to `database-stack.ts`
- [ ] `@Infra` Add GSIs: CropsTable (status-createdAt), TripsTable (status-departureDate)

#### B2: F-04 — Isar Database Encryption
> No encryption code exists. Must be enabled BEFORE any user data is written.
- [ ] `@Mobile` Implement `_getEncryptionKey()` using `flutter_secure_storage` backed by Android Keystore
- [ ] `@Mobile` Pass encryption key to `Isar.open()` on database initialization
- [ ] `@Mobile` Write integration test verifying DB file is unreadable without key

#### B3: F-03 — Mobile Auth Flow
> Server auth complete (169 tests). Mobile `mobile/lib/core/auth/` is EMPTY.
- [ ] `@Mobile` Create `AuthService` with online/offline login branching
- [ ] `@Mobile` Create `OfflineAuthCache` with PBKDF2 password hashing
- [ ] `@Mobile` Integrate `flutter_secure_storage` for token persistence
- [ ] `@Mobile` Create login/registration UI screens (low-literacy, icon-heavy, Amharic/Oromo)

#### B4: F-05 — Certificate Pinning
> `mobile/lib/core/network/` is EMPTY. No Dio configuration exists.
- [ ] `@Mobile` Configure Dio `IOHttpClientAdapter` with SHA-256 pin for API Gateway/CloudFront cert
- [ ] `@Mobile` Add backup pin and document cert rotation procedure

**R2 Exit Criteria:**
- [ ] All 6 Isar schemas generated and tested
- [ ] DynamoDB tables include Deliveries, Payments, SyncMetadata with correct GSIs
- [ ] Isar encryption enabled with Android Keystore key management
- [ ] User can register with Ethiopian phone number (+251...)
- [ ] User can log in online (Cognito) and offline (cached credentials)
- [ ] JWT validation works in Lambda authorizer ✅ (server done)
- [ ] All API endpoints require authentication ✅ (CDK done)
- [ ] Rate limiting active on auth endpoints ✅ (done)
- [ ] Input validation on all Lambda handlers ✅ (done)
- [ ] Certificate pinning configured in Dio client

---

### Priority Group C: Nice-to-Have (If Time Permits — Week 2)

#### C1: F-05 — Certificate Pinning (Mobile)
> 3G MITM protection. Configure after Lambda deployed (A2).
- [ ] `@Mobile` Add `dio: ^5.4.0` to pubspec.yaml
- [ ] `@Mobile` Configure `IOHttpClientAdapter` with SHA-256 certificate pinning
- [ ] `@Mobile` Extract API Gateway cert hash after F-16 deployment
- [ ] `@Mobile` Add backup pin for cert rotation
- [ ] `@Mobile` Document rotation procedure in `specs/decisions/cert-pinning.md`

#### C2: Update Auth API Contract
> Documentation for mobile team reference.
- [ ] `@Architect` Review `specs/contracts/auth-api.md`
- [ ] `@Architect` Add offline auth flow documentation
- [ ] `@Architect` Add token refresh strategy
- [ ] `@Architect` Add sequence diagrams for register + login

---

## 🚫 Sprint 3 Blockers

| Blocker | Impact | Probability | Mitigation |
|---------|--------|-------------|------------|
| **Flutter SDK not installed** | 🔴 HIGH (blocks B1-B4) | 100% (KNOWN) | **INSTALL BEFORE SPRINT START** (Day 1 priority) |
| **AWS credentials missing** | 🔴 HIGH (blocks A2) | MEDIUM | Configure AWS CLI with dev account on Day 1 |
| **Node.js 18.x** (should be 20.x LTS) | 🟡 MEDIUM (CDK compat) | LOW | Upgrade to 20.x LTS |
| **.NET AOT publish failure** | 🟡 MEDIUM (blocks A2) | LOW | Test `dotnet publish -r linux-arm64` locally first |
| **Cognito SMS in Ethiopia** | 🟡 MEDIUM (blocks B3 test) | MEDIUM | Use test phone numbers initially |

---

## 🔗 Related Documents

- **Constitution**: `CLAUDE.md` - Core principles and constraints
- **State**: `STATE.md` - Current session context
- **Standards**: `.ai/standards.md` - Coding patterns
- **Roadmap**: `specs/features/ROADMAP.md` - Full product roadmap
- **Auth Plan**: `specs/decisions/authentication-encryption-plan.md` - Approved for implementation
- **Auth Contract**: `specs/contracts/auth-api.md` - 672-line API spec
- **Session Summary**: `session_summary.md` - Consolidated project knowledge

---

## 📝 Notes

- **Server auth code is complete** — 169 tests passing, 0 failures
- **Key design decisions**: Result types over exceptions, manual DynamoDB marshalling (AOT-safe), constructor DI
- **Rate limiting**: DynamoDB-backed, 5 attempts/15min on login, 3 attempts/15min on register
- **Security**: UserNotFound returns NotAuthorized (prevents enumeration), Logout treats NotAuthorized as success (idempotent)

---

**Created:** 2026-02-09
**Last Updated:** 2026-02-14
**Sprint**: Sprint 3 (Feb 14 - Feb 28, 2026)
**Status:** R1 incomplete (2/4), R2 incomplete (server 100%, mobile 0%, infra 70%)
