# 🎯 TASK.md - Current Mission

## Purpose
This file tracks the **active work** for the current development cycle. It's the single source of truth for "what am I doing right now?"

---

## 🎯 Current Sprint Goal

**Deploy Authentication & Begin Core Domain (R1→R2→R3)**

Wire CDK infrastructure for server auth (169 tests passing, code + rate limiting complete), deploy to dev, then build core domain models (crops, trips) and sync engine.

> Previous sprint "Create Production-Ready Project Structure" — **COMPLETE** ✅

---

## 📋 Completed Tasks

> **Task Claiming:** All tasks use `@Scope` tags per `.agents/task-protocol.md`. Claim a task by adding your scope tag and committing before starting work.

### Phase 1: Foundation ✅
- [x] `@Architect` Define project structure requirements
- [x] `@Architect` Create root governance files (CLAUDE.md, STATE.md, TASK.md)
- [x] `@Architect` Create .ai/ governance directory
- [x] `@Architect` Create .agents/ directory for agent SOPs
- [x] `@Architect` Create docs/ structure with local_reality/
- [x] `@Architect` Create specs/ directory for feature tickets

### Phase 2: Mobile Structure ✅
- [x] `@Mobile` Create mobile/ directory
- [x] `@Mobile` Create mobile/lib/core/ (sync engine, Isar)
- [x] `@Mobile` Create mobile/lib/features/
- [x] `@Mobile` Create mobile/lib/shared/
- [x] `@Localization` Create mobile/lib/shared/l10n/ (Amharic/Oromo/English)
- [x] `@Localization` Add sample .arb files for localization

### Phase 3: Server Structure ✅
- [x] `@Server` Create server/ directory
- [x] `@Server` Create server/AOT_COMPLIANCE.md
- [x] `@Server` Create server/src/ structure
- [x] `@Server` Add .NET project scaffolding notes

### Phase 4: Infrastructure & Automation ✅
- [x] `@Infra` Create infra/ directory for AWS CDK
- [x] `@Infra` Create scripts/ directory
- [x] `@Infra` Create scripts/checkpoint.sh automation
- [x] `@Infra` Make checkpoint.sh executable
- [x] `@Security` Create qa/ directory for threat models

### Phase 5: Documentation ✅
- [x] `@Architect` Create .ai/agents.md (role definitions)
- [x] `@Architect` Create .ai/standards.md (coding patterns)
- [x] `@Architect` Create .ai/tech_stack.md (framework versions)
- [x] `@Architect` Create docs/local_reality/README.md (Ethiopian context)

### Phase 6: Audit & Consistency ✅
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

### Phase 7: Project Initialization ✅
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

### Phase 8: QA & Security Audit ✅
- [x] `@Security` Run QA validation across all three projects (47 PASS, 12 WARN, 0 FAIL)
- [x] `@Security` Run security audit (1 CRITICAL, 7 HIGH, 9 MEDIUM, 5 LOW found)
- [x] `@Server` Fix duplicate JSON serialization contexts (AppJsonContext + HealthCheckJsonContext)
- [x] `@Server` Add security headers (HSTS, X-Content-Type-Options, X-Frame-Options, Cache-Control)
- [x] `@Server` Remove version disclosure header (X-WeredaConnect-Version)
- [x] `@Server` Fix misleading AOT comment on NSubstitute in test .csproj
- [x] `@Infra` Fix package.json main field (was pointing to nonexistent lib/index.js)
- [x] `@Infra` Add test files to tsconfig.json include array
- [x] `@Mobile` Add android:allowBackup="false" to AndroidManifest.xml
- [x] `@Mobile` Add network_security_config.xml (HTTPS enforcement, cleartext blocked)

### Phase 9: Application-Level Rate Limiting ✅
- [x] `@Server` Add failed login attempt tracking + account lockout (3 failures → 15-min lockout, AUTH_015)
- [x] `@Server` Add registration flood protection (5 per phone per day, AUTH_006)
- [x] `@Server` Add OTP attempt limits (3 per code, AUTH_012)
- [x] `@Server` Add RateLimitService + RateLimitRepository (dedicated DynamoDB table, atomic counters, TTL auto-cleanup)
- [x] `@Server` Add LambdaResponse.RateLimited() helper with Retry-After header
- [x] `@Server` Wire rate limiting into LoginFunction, RegisterFunction, VerifyOtpFunction
- [x] `@Server` Add comprehensive tests (42 new tests — 169 total passing)

---

## 🔒 Security Remediation Backlog

> Items from the security audit that require architectural decisions or production setup.

### Before ANY Deployment (CRITICAL + HIGH)
- [ ] `@Security` **S-01** Purge private key from git history (server/localstack-data/cache/server.test.pem.key committed in e3c2e8e, removed in 5ec0a1a — still in history)
- [ ] `@Security` **X-02** Install pre-commit secret detection hooks (gitleaks or detect-secrets)
- [ ] `@Infra` **I-01** Add API authentication (Cognito User Pool or JWT authorizer) to API Gateway
- [ ] `@Infra` **I-02** Add API rate limiting (throttling on API Gateway stage)
- [ ] `@Mobile` **M-01** Enable Isar database encryption with key stored in Android Keystore
- [ ] `@Mobile` **M-03** Add certificate pinning for production API domain (pins in network_security_config.xml)

### Before Production (MEDIUM)
- [ ] `@Server` **S-04** Establish input validation pattern/middleware for Lambda handlers
- [ ] `@Infra` **I-03** Apply least-privilege IAM per Lambda function (not blanket ReadWriteData on all tables)
- [ ] `@Infra` **I-06** Enable S3 access logging on uploads bucket
- [ ] `@Infra` **I-07** Enable S3 versioning on production uploads bucket
- [ ] `@Infra` **I-08** Evaluate VPC deployment for Lambdas (test cold start impact)
- [ ] `@Mobile` **M-04** Add ProGuard obfuscation rules; use --obfuscate --split-debug-info for release
- [ ] `@Mobile` **M-05** Integrate crash reporting service (Sentry/Crashlytics) for production
- [ ] `@Security` **X-03** Add security scanning (npm audit, dotnet list --vulnerable) before Dependabot auto-merge

### Hardening (LOW)
- [ ] `@Infra` **I-04** Upgrade DynamoDB encryption to KMS-managed keys for production
- [ ] `@Infra` **I-05** Upgrade S3 encryption to KMS-managed for production
- [ ] `@Mobile` **M-06** Dynamic image cache sizing based on device RAM

---

## 🚀 Next Planned Tasks (PO Review — 2026-02-12)

> Ordered by priority. Based on roadmap phases R1→R2→R3.

### Priority 0 — Security Critical (Blocks Everything)

1. **`@Security` S-01: Purge private key from git history**
   - `server/localstack-data/cache/server.test.pem.key` committed in e3c2e8e, removed in 5ec0a1a — still in git history
   - Use `git filter-repo` or BFG Repo Cleaner
   - Force-push required after cleanup
   - **Status:** CRITICAL — must be done before any public/shared repo access

2. **`@Security` X-02: Install pre-commit secret detection hooks**
   - Install `gitleaks` or `detect-secrets` as pre-commit hook
   - Prevents future accidental secret commits

### Priority 1 — Infrastructure Wiring (Blocks Deployment)

> Server auth code (169 tests passing, incl. rate limiting) is complete but cannot be deployed — CDK has zero wiring.

3. **`@Infra` Create Cognito stack in CDK**
   - Provision Cognito User Pool (phone-based username, SMS OTP via SNS)
   - Configure custom attributes: `custom:role`, `custom:woreda`, `custom:language`, `custom:faydaId`
   - Create App Client (USER_PASSWORD_AUTH + REFRESH_TOKEN_AUTH flows)
   - Password policy: min 6 chars (per `specs/contracts/auth-api.md`)
   - Token expiry: access 15 min, refresh 30 days

4. **`@Infra` Wire Lambda functions in `api-stack.ts`**
   - Uncomment/create Lambda constructs for all 7 handlers:
     - RegisterFunction, VerifyOtpFunction, LoginFunction, RefreshFunction, LogoutFunction, AuthorizerFunction, HealthCheckFunction
   - Set environment variables: COGNITO_CLIENT_ID, COGNITO_USER_POOL_ID, COGNITO_REGION, USERS_TABLE, RATE_LIMITS_TABLE
   - Configure IAM: DynamoDB read/write on users table, Cognito IdP access
   - Runtime: provided.al2023, Architecture: arm64, Memory: 512MB

5. **`@Infra` Configure API Gateway routes + authorizer**
   - Add routes: POST /auth/register, /auth/verify-otp, /auth/login, /auth/refresh, /auth/logout, GET /health
   - Create custom Lambda authorizer from AuthorizerFunction
   - Attach authorizer to /auth/logout (only authenticated endpoint for now)
   - Configure throttling: 10 req/s on auth endpoints (per auth-api.md contract)

6. **`@Infra` Deploy to dev environment and smoke test**
   - `cdk deploy --all` to dev (me-south-1)
   - Verify end-to-end: register → OTP → login → refresh → logout
   - Document API Gateway URL + Cognito pool IDs

### Priority 2 — Auth Gaps (Contract Compliance)

> Items defined in `specs/contracts/auth-api.md` but not yet implemented server-side.

7. ~~**`@Server` Add failed login attempt tracking + account lockout**~~ ✅ DONE
   - Dedicated `rate_limits` DynamoDB table with atomic counters + TTL
   - 3 consecutive failures → 15-min lockout (AUTH_015), reset on success
   - Wired into LoginFunction with lockout check before Cognito call

8. ~~**`@Server` Add rate limiting middleware (server-side)**~~ ✅ DONE
   - RateLimitService: login lockout, registration flood (5/day), OTP limits (3/code)
   - 429 with Retry-After header via LambdaResponse.RateLimited()
   - Wired into LoginFunction, RegisterFunction, VerifyOtpFunction
   - 42 new tests (169 total passing)

9. **`@Server` Add GET /auth/me endpoint**
   - Return authenticated user profile from JWT claims + DynamoDB
   - Required for mobile app to load user state after token refresh

10. **`@Server` Add PATCH /auth/profile endpoint**
    - Update displayName, preferredLanguage, faydaId
    - Version-based optimistic concurrency (existing pattern)

### Priority 3 — Next Feature Phase (R3: Sync + Core Domain)

11. **`@Server` F-07: Core domain models — Crop entity**
    - CropListing model: cropId, farmerId, cropType, quantity, unit, pricePerUnit, woreda, location, photos, status, createdAt, version
    - DynamoDB schema (crops table already provisioned in CDK)
    - CropRepository with CRUD + query by woreda/status

12. **`@Server` F-07: Core domain models — Trip entity**
    - Trip model: tripId, truckerId, origin, destination, capacity, pricePerKg, departureDate, status, createdAt, version
    - DynamoDB schema (trips table already provisioned in CDK)
    - TripRepository with CRUD + query by route/status

13. **`@Server` F-08: Sync engine — batch sync endpoint**
    - POST /sync endpoint accepting batched operations
    - Version-based conflict resolution per `specs/contracts/sync-protocol.md`
    - Idempotency key handling (UUID, 7-day expiry)
    - Delta sync response (only changed records since last sync)

14. **`@Server` F-10: Crop listing endpoints**
    - POST /listings — create crop listing
    - GET /listings — query by woreda, crop type, status
    - GET /listings/{id} — get single listing
    - PATCH /listings/{id} — update listing (owner only)

15. **`@Mobile` Install Flutter SDK and verify mobile app builds**
    - `flutter pub get`, `flutter gen-l10n`, `flutter run`
    - Implement Isar schemas for User, CropListing, Trip
    - Begin offline-first auth flow (PBKDF2 caching)

### Priority 4 — CI/CD + Hardening

16. **`@Infra` Set up GitHub Actions CI/CD pipeline**
    - .NET build + test + AOT publish
    - Flutter build + test
    - CDK synth validation
    - Security scanning (npm audit, dotnet list --vulnerable)

17. **`@Infra` I-03: Least-privilege IAM per Lambda**
    - Each function gets only the permissions it needs (not blanket ReadWriteData)

18. **`@Mobile` M-01: Enable Isar encryption**
    - Device-bound AES key stored in Android Keystore
    - Required before storing any user data locally

---

## 🎯 Definition of Done (Initialization Phase)

This task is complete when:
- ✅ All directories exist with proper structure
- ✅ All governance files are created and populated
- ✅ checkpoint.sh script is executable and tested
- ✅ AOT_COMPLIANCE.md has banned libraries documented
- ✅ l10n/ structure is ready for localization files
- ✅ README files exist in key directories explaining their purpose
- ✅ All documentation is consistent (agritech, not civic)
- ✅ All cross-references and links are correct
- ✅ Flutter project initialized with pubspec.yaml and Android config
- ✅ .NET project initialized with AOT configuration and builds successfully
- ✅ CDK project initialized with TypeScript and synths successfully

---

## 🔗 Related Documents

- **Constitution**: `CLAUDE.md` - Core principles and constraints
- **State**: `STATE.md` - Current session context
- **Standards**: `.ai/standards.md` - Coding patterns

---

## 📝 Notes

- **Initialization phase is COMPLETE** ✅
- **Server auth code is COMPLETE** ✅ — 7 Lambda handlers + rate limiting, 169 tests passing, AOT-compliant
- **Rate limiting is COMPLETE** ✅ — Login lockout, registration flood, OTP limits (dedicated DynamoDB table)
- **Infrastructure is NOT wired** ⚠️ — Lambda functions commented out in CDK, no Cognito, no routes
- **CDK needs `rate_limits` table** ⚠️ — New DynamoDB table required (pk: string, TTL-enabled)
- All documentation has been audited for consistency (2026-02-10)

### PO Review Summary (2026-02-12)
- Server auth matches `specs/contracts/auth-api.md` — rate limiting + lockout now COMPLETE
- Code quality: excellent (result types, source generators, security headers, no reflection)
- Biggest risk: gap between complete server code and zero infrastructure deployment
- Remaining auth gaps: device fingerprinting, GET /auth/me, PATCH /auth/profile

### Known Limitations
- **Flutter SDK not installed** on this machine — need to install before running mobile app
- **Node.js 18.x** detected (should upgrade to 20.x LTS per tech stack)
- **Lambda functions** commented out in CDK stack — zero deployment capability
- **Cognito User Pool** not provisioned in CDK — no auth backend exists in AWS
- **Private key in git history** (S-01) — CRITICAL, must purge before sharing repo

---

**Created:** 2026-02-09
**Last Updated:** 2026-02-12
**Status:** Auth Code Complete — Awaiting Infrastructure Wiring & Deployment
