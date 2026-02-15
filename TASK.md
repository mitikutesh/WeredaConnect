# 🎯 TASK.md - Current Mission

## Purpose
This file tracks the **active work** for the current development cycle. It's the single source of truth for "what am I doing right now?"

---

## 🎯 Current Sprint Goal

**Sprint 4 & 5: Make Offline-First Real - Sync Engine + First Feature (Mar 1 - Mar 28, 2026)**

Build and validate the offline-first sync engine and prove the architecture with the first user-facing feature by:
1. Building sync engine foundation (local queue, executor, server endpoint, conflict resolution)
2. Implementing first feature: Crop listing flow (create + browse)
3. Achieving Phase R3 exit criteria (100 offline-online cycles, zero data loss)

**Branch:** `sync-engine-crop-listing` (to be created)
**Sprint Plan**: `SPRINT_PLANNING.md` (Phase R3 detailed plan)
**Roadmap Reference**: `specs/features/ROADMAP.md` — Phase R3 (Week 6-9)

---

## 📊 Phase Status Summary

| Phase | Status | Done | Remaining |
|-------|--------|------|-----------|
| **R1: Security Foundation** | ⚠️ 75% | 3/4 | **A1 + A2 MUST complete before Sprint 4** |
| **R2: Auth + Data Models** | ✅ 90% MILESTONE MET | 8/9 exit criteria | 3 DynamoDB tables, cert pinning |
| **R3: Sync + First Feature** | 🚧 55% IN PROGRESS | 11/20 tasks | Mobile complete, server pending |

**R2 Milestone "User Can Register and Login": ACHIEVED** ✅ (PO assessment Feb 14, 2026)
**R3 Target**: First end-to-end demo of offline-first architecture by Mar 28, 2026

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

### Phase R1: Security Foundation — 75% Complete (3/4 done)
- [x] `@Security` F-02: Install gitleaks + pre-commit hooks (`.gitleaks.toml`, `.pre-commit-config.yaml`)
- [x] `@Infra` F-16: CDK api-stack with Cognito, HealthCheck Lambda, auth Lambda functions, API Gateway routes, throttling
- [x] `@Infra` F-16: CDK constructs with per-function least-privilege IAM, ARM64, 512MB
- [x] `@Server` F-16: `Program.cs` AOT bootstrap entry point with handler routing
- [x] `@Infra` F-16: `scripts/deploy.sh` deployment script created
- [x] `@Infra` CDK tests fixed — unit, integration, snapshot (11 tests passing)
- [x] `@Infra` F-19: CI/CD security scans — gitleaks in `pr-validation.yml`, vuln scans in `server-tests.yml` and `infra-tests.yml`

### Phase R2: Server-Side Complete, Mobile Foundation Laid
- [x] `@Server` F-03: All 5 auth Lambda handlers with 169 passing tests
- [x] `@Server` F-06: Rate limiting — `RateLimitService` with 4 rules + API Gateway throttling
- [x] `@Server` F-09: Input validation — `RequestValidator` with AOT-safe `GeneratedRegex`
- [x] `@Architect` Auth API contract — `specs/contracts/auth-api.md` (672 lines)
- [x] `@Mobile` F-07: All 6 Isar schemas created and tested (User, CropListing, Trip, Delivery, Payment, SyncOperation)
- [x] `@Mobile` F-04: Isar database encryption with Android Keystore integration
- [x] `@Mobile` F-03: Auth service + BLoC + Login/Register/OTP screens
- [x] `@Mobile` F-06: Offline rate limiting (5 attempts = 15-minute lockout)
- [x] `@Security` Fixed 2 critical vulnerabilities (password storage, rate limiting)

---

## 🚧 Sprint 4 & 5 Active Tasks — Phase R3 Backlog

**Sprint Duration**: Mar 1 - Mar 28, 2026 (4 weeks, 2 sprints)
**Full Sprint Plan**: See `SPRINT_PLANNING.md` for detailed acceptance criteria and timeline
**Branch**: `sync-engine-crop-listing` (create on Day 1)

⚠️ **CRITICAL PRE-FLIGHT**: Sprint 3 items A1, A2, B4 (SyncMetadata table) MUST complete before starting Sprint 4!

---

## 🔴 Sprint 4 Pre-Flight (Day 0 - Mar 1)

> **These MUST be complete before Sprint 4 tasks begin. If not done in Sprint 3, complete on Day 0.**

### Pre-Flight Checklist
- [ ] **Sprint 3 A1 Complete**: Git history purged, force-pushed, verified clean
- [ ] **Sprint 3 A2 Complete**: Lambda deployed to AWS dev, cold start <2s measured
- [ ] **Sprint 3 B4 Complete**: SyncMetadata DynamoDB table deployed (CRITICAL)
- [ ] AWS credentials verified: `aws sts get-caller-identity`
- [ ] Create new branch: `git checkout -b sync-engine-crop-listing`
- [ ] Verify Flutter doctor: `flutter doctor` (no blocking issues)
- [ ] Verify Node.js 20.x: `node --version`
- [ ] Verify .NET 10: `dotnet --version`

---

## 📋 Sprint 4 (Mar 1-14): Sync Engine Foundation

### Priority Group A: Infrastructure Prerequisites (Week 1, Days 1-2)

#### Task A1: Add Missing DynamoDB Tables 🔴 CRITICAL
**Feature**: F-07 (Infrastructure completion)
**Effort**: 3 hours | **Owner**: @Infra | **Day**: Week 1, Day 1

- [ ] Add **SyncMetadata** table to `infra/lib/stacks/database-stack.ts`
  - PK: `idempotencyKey`, SK: `timestamp`, TTL: `expiresAt` (7 days)
- [ ] Add **Deliveries** table: PK `deliveryId`, SK `tripId`
- [ ] Add **Payments** table: PK `paymentId`, SK `deliveryId`
- [ ] Add GSI to CropsTable: `StatusCreatedAtIndex` (status + createdAt)
- [ ] Add GSI to TripsTable: `StatusDepartureDateIndex` (status + departureDate)
- [ ] Run `cdk synth` and verify CloudFormation template
- [ ] Deploy: `cd infra && npm run deploy -- --profile dev`
- [ ] Verify tables exist in AWS Console
- [ ] Update CDK tests to assert new tables and GSIs

**Why Critical**: Sync engine depends on SyncMetadata for idempotency.

#### Task A2: Network Layer Foundation (Dio Client) ✅ COMPLETE
**Feature**: F-08 (Sync Engine dependency)
**Effort**: 1 day | **Owner**: @Mobile | **Days**: Week 1, Days 1-2

**Phase 1: Basic Dio Setup (4 hours)**
- [x] Add `dio: ^5.4.3` to `mobile/pubspec.yaml`
- [x] Create `mobile/lib/core/network/api_client.dart` with GET/POST/PUT/DELETE methods
- [x] Create `mobile/lib/core/network/api_exception.dart` (NetworkException, ServerException, etc.)

**Phase 2: Auth Token Interceptor (2 hours)**
- [x] Create `mobile/lib/core/network/interceptors/auth_interceptor.dart`
- [x] Inject access token into Authorization header
- [x] On 401, refresh token and retry

**Phase 3: Logging Interceptor (1 hour)**
- [x] Create `mobile/lib/core/network/interceptors/logging_interceptor.dart`
- [x] Log requests/responses (debug mode only)

**Phase 4: Environment Configuration (1 hour)**
- [x] Create `mobile/lib/core/config/environment.dart`
- [x] Document environment variables in `mobile/README.md`

**Phase 5: Unit Tests (2 hours)**
- [x] Create `mobile/test/core/network/api_client_test.dart` (5+ test cases)

**Completed**: Feb 15, 2026
**Why Critical**: All server communication depends on this client.

---

### Priority Group B: Sync Engine Phase A - Local Queue (Week 1, Days 3-5)

#### Task B1: Sync Queue Service (Local Persistence) ✅ COMPLETE
**Feature**: F-08 Phase A
**Effort**: 2 days | **Owner**: @Mobile | **Days**: Week 1, Days 3-4

**Phase 1: SyncQueue Class (1 day)**
- [x] Create `mobile/lib/core/sync/sync_queue.dart`
- [x] Implement methods: `enqueue()`, `getPendingOperations()`, `markAsSyncing()`, `markAsSynced()`, `markAsFailed()`, `retryFailed()`, `getPendingCount()`
- [x] Generate unique `operationId` (UUID v4)
- [x] Generate `idempotencyKey`: `{userId}:{entityType}:{entityId}:{action}:{timestamp}`
- [x] Store operation with status `pending`, retryCount `0`

**Phase 2: Idempotency Key Generation (4 hours)**
- [x] Create `mobile/lib/core/sync/idempotency_key_generator.dart`
- [x] Document format in `specs/contracts/sync-protocol.md`

**Phase 3: Unit Tests (4 hours)**
- [x] Create `mobile/test/core/sync/sync_queue_test.dart` (5+ test cases)

**Completed**: Feb 15, 2026
**Why Now**: Foundation of offline-first. All user actions needing sync go through this queue.

#### Task B2: Connectivity Monitor ✅ COMPLETE
**Feature**: F-08 Phase A
**Effort**: 1 day | **Owner**: @Mobile | **Day**: Week 1, Day 5

**Phase 1: ConnectivityMonitor Class (4 hours)**
- [x] Add `connectivity_plus: ^6.0.0` to `mobile/pubspec.yaml`
- [x] Create `mobile/lib/core/sync/connectivity_monitor.dart`
- [x] Implement `onConnectivityChanged` stream
- [x] Implement `isConnected()` and `hasInternetAccess()` (ping /health endpoint)

**Phase 2: Stream Debouncing (2 hours)**
- [x] Debounce connectivity changes by 2 seconds

**Phase 3: Unit Tests (2 hours)**
- [x] Create `mobile/test/core/sync/connectivity_monitor_test.dart` (5+ test cases)

**Completed**: Feb 15, 2026
**Why Now**: Sync executor needs to know when to start syncing.

---

### Priority Group C: Sync Engine Phase B - Sync Executor (Week 2, Days 1-3)

#### Task C1: Sync Executor (Batch Processing) ✅ COMPLETE
**Feature**: F-08 Phase B
**Effort**: 2 days | **Owner**: @Mobile | **Days**: Week 2, Days 1-2

**Phase 1: SyncExecutor Class (1 day)**
- [x] Create `mobile/lib/core/sync/sync_executor.dart`
- [x] Implement `start()`, `syncNow()`, `executeBatch()` methods
- [x] On connectivity detected, call `syncNow()`
- [x] Batch operations: fetch up to 50 pending operations
- [x] Send each operation to POST `/sync` endpoint

**Phase 2: Retry Logic with Exponential Backoff (4 hours)**
- [x] On failure, increment `retryCount`
- [x] Retry schedule: 5s, 15s, 45s (max 3 retries)
- [x] If retryCount >= 3, mark as `failed` permanently

**Phase 3: Error Handling (4 hours)**
- [x] Network error → retry | 401 → refresh token | 409 → conflict handler
- [x] 400 → failed permanent | 5xx → retry with backoff

**Phase 4: Unit Tests (4 hours)**
- [x] Create `mobile/test/core/sync/sync_executor_test.dart` (6+ test cases)

**Completed**: Feb 15, 2026

#### Task C2: Integration with User Actions ✅ COMPLETE
**Feature**: F-08 Phase B
**Effort**: 1 day | **Owner**: @Mobile | **Day**: Week 2, Day 3

- [x] Update CropListingBLoC to enqueue sync operations on create/update/delete
- [x] Create `mobile/lib/core/sync/sync_worker.dart` (background sync every 15 min)
- [x] Trigger sync on app foreground and connectivity restored
- [x] Unit tests: verify operations enqueue correctly

**Completed**: Feb 15, 2026

---

### Priority Group D: Sync Engine Phase C - Server Sync Endpoint (Week 2, Days 4-5)

#### Task D1: Server Sync Function
**Feature**: F-08 Phase C
**Effort**: 2 days | **Owner**: @Server | **Days**: Week 2, Days 4-5

**Phase 1: SyncFunction.cs Lambda Handler (1 day)**
- [ ] Create `server/src/Functions/SyncFunction.cs`
- [ ] Create `server/src/Models/SyncRequest.cs`
- [ ] Implement idempotency key check
- [ ] Route to correct entity handler (CropListing, User, etc.)
- [ ] Store idempotency key with 7-day TTL

**Phase 2: Idempotency Repository (4 hours)**
- [ ] Create `server/src/Repositories/SyncMetadataRepository.cs`
- [ ] Implement `IdempotencyKeyExists()` and `StoreIdempotencyKey()`
- [ ] Manual DynamoDB marshalling (AOT-safe)

**Phase 3: Version-Based Conflict Detection (4 hours)**
- [ ] In `CropRepository.Sync()`: check server version vs client version
- [ ] If server version > client: return 409 Conflict with server data
- [ ] If versions match: apply update, increment version
- [ ] Return conflict response format

**Phase 4: Unit Tests (4 hours)**
- [ ] Create `server/tests/Functions/SyncFunctionTests.cs` (6+ test cases)

#### Task D2: API Gateway Route for Sync
**Feature**: F-08 Phase C
**Effort**: 2 hours | **Owner**: @Infra | **Day**: Week 2, Day 5

- [ ] Add POST /sync route to `infra/lib/stacks/api-stack.ts`
- [ ] Require authentication (Cognito authorizer)
- [ ] Throttling: 100 req/s per user
- [ ] Add SyncFunction to Lambda constructs (IAM: read-write on SyncMetadata, CropsTable, UsersTable)
- [ ] Deploy: `cd infra && npm run deploy -- --profile dev`

---

## 📋 Sprint 5 (Mar 15-28): Crop Listing Flow

### Priority Group E: Crop Listing - Mobile UI (Week 3, Days 1-3)

#### Task E1: Crop Listing Creation Screen ✅ COMPLETE
**Feature**: F-10
**Effort**: 2 days | **Owner**: @Mobile | **Days**: Week 3, Days 1-2

**Phase 1: CropListingBloc (1 day)**
- [x] Create `mobile/lib/features/crop_listing/bloc/crop_listing_bloc.dart`
- [x] Events: `CreateCropListing`, `UpdateCropListing`, `DeleteCropListing`, `LoadCropListings`
- [x] States: `CropListingInitial`, `CropListingLoading`, `CropListingCreated`, `CropListingError`, `CropListingsLoaded`
- [x] On `CreateCropListing`: save to Isar, enqueue sync operation, emit state

**Phase 2: Create Listing Screen UI (1 day)**
- [x] Create `mobile/lib/features/crop_listing/screens/create_crop_listing_screen.dart`
- [x] Fields: Crop Type dropdown, Quantity, Price, Location (GPS + manual), Description, Audio Note placeholder
- [x] Form validation: crop type required, quantity > 0, price > 0, location required
- [x] Save button: dispatch `CreateCropListing` event
- [x] Show snackbar: "Listing saved. Will sync when online." (localized)

**Phase 3: Unit Tests (4 hours)**
- [x] Create `mobile/test/features/crop_listing/bloc/crop_listing_bloc_test.dart` (4+ test cases)

**Completed**: Feb 15, 2026
**Files Created**: 13 new files including BLoC, repository, widgets, screens, tests
**Tests Passing**: 10/10 (5 repository + 5 BLoC tests)

#### Task E2: Crop Listing Browse Screen ✅ COMPLETE
**Feature**: F-10
**Effort**: 1 day | **Owner**: @Mobile | **Day**: Week 3, Day 3

- [x] Create `mobile/lib/features/crop_listing/screens/browse_listings_screen.dart`
- [x] App bar with title (localized), filter chips (crop type)
- [x] List view with cards showing: icon, crop type, quantity, price, location, date
- [x] Empty state message, FAB "+" to create listing
- [x] Offline indicator banner (yellow/orange) when offline
- [x] Widget tests: `mobile/test/features/crop_listing/screens/browse_listings_screen_test.dart`

**Completed**: Feb 15, 2026
**Integration**: Wired into main_navigation.dart, home_screen.dart
**Localization**: All 3 languages (English, Amharic, Afan Oromo) - 25 new keys

---

### Priority Group F: Crop Listing - Server & API (Week 3, Days 4-5)

#### Task F1: Server Crop Listing Functions
**Feature**: F-10
**Effort**: 2 days | **Owner**: @Server | **Days**: Week 3, Days 4-5

**Phase 1: CropRepository (1 day)**
- [ ] Create `server/src/Repositories/CropRepository.cs`
- [ ] Interface: `Create()`, `GetAll()`, `GetById()`, `Sync()`
- [ ] Manual DynamoDB marshalling (AOT-safe)
- [ ] Create: insert with version 1, GetAll: query with filters, Sync: version checking

**Phase 2: CropFunction Lambda Handlers (1 day)**
- [ ] Create `server/src/Functions/CropFunction.cs`
- [ ] Handlers: `HandleGetCrops()` (GET /crops), `HandleGetCropById()` (GET /crops/{id})
- [ ] No CREATE endpoint — use /sync only (offline-first pattern)
- [ ] Input validation: crop type enum, quantity/price ranges

**Phase 3: Unit Tests (4 hours)**
- [ ] Create `server/tests/Functions/CropFunctionTests.cs` (5+ test cases)

#### Task F2: API Gateway Routes for Crops
**Feature**: F-10
**Effort**: 2 hours | **Owner**: @Infra | **Day**: Week 3, Day 5

- [ ] Add routes: GET /crops, GET /crops/{id}
- [ ] CropFunction IAM: read-only on CropsTable
- [ ] Memory: 256MB, Timeout: 10s
- [ ] Deploy

---

### Priority Group G: Conflict Resolution (Week 4, Days 1-2)

#### Task G1: Conflict Resolution Handler
**Feature**: F-08 Phase D
**Effort**: 1.5 days | **Owner**: @Mobile | **Days**: Week 4, Days 1-2

**Phase 1: ConflictResolver Service (1 day)**
- [ ] Create `mobile/lib/core/sync/conflict_resolver.dart`
- [ ] Default strategy: Accept server (server wins)
- [ ] Update local Isar record with server data
- [ ] Mark sync operation as synced

**Phase 2: Conflict UI Dialog (Optional - 4 hours)**
- [ ] Create `mobile/lib/features/sync/widgets/conflict_dialog.dart` (skip for MVP, auto-resolve)

**Phase 3: Integration with SyncExecutor (2 hours)**
- [ ] In `SyncExecutor.executeBatch()`: on 409 Conflict, call `ConflictResolver.resolve()`

**Phase 4: Unit Tests (2 hours)**
- [ ] Create `mobile/test/core/sync/conflict_resolver_test.dart`

---

### Priority Group H: Security Hardening (Week 4, Days 3-4)

#### Task H1: Least-Privilege IAM per Lambda
**Feature**: F-17
**Effort**: 1.5 days | **Owner**: @Infra | **Days**: Week 4, Days 3-4

**Phase 1: IAM Policy Audit (4 hours)**
- [ ] Review current Lambda IAM roles in `infra/lib/constructs/lambda-construct.ts`
- [ ] Document current permissions per function
- [ ] Identify over-privileged functions

**Phase 2: Per-Function IAM Roles (1 day)**
- [ ] Create minimal IAM policies for each Lambda
- [ ] Remove wildcard permissions (`dynamodb:*` → specific actions)
- [ ] Update `lambda-construct.ts` to accept per-function IAM policies

**Phase 3: Deploy and Verify (2 hours)**
- [ ] Deploy, test each Lambda, verify HealthCheck cannot write to DynamoDB

---

### Priority Group I: Integration Testing & Polish (Week 4, Day 5)

#### Task I1: End-to-End Integration Test
**Feature**: F-08 + F-10 (Phase R3 validation)
**Effort**: 1 day | **Owner**: @Mobile + @Server | **Day**: Week 4, Day 5

**Phase 1: Integration Test Scenario (6 hours)**
- [ ] Create `mobile/integration_test/sync_crop_listing_test.dart`
- [ ] Test: Create listing offline → verify in Isar → go online → sync → verify on server
- [ ] Test: 100 offline-online cycles, zero data loss

**Phase 2: Conflict Test Scenario (4 hours)**
- [ ] Create `mobile/integration_test/sync_conflict_test.dart`
- [ ] Test: User A creates offline, User B creates same offline, A syncs, B gets 409, B updates to server version

**Phase 3: Performance Test (2 hours)**
- [ ] Measure sync latency: <5s for 50 operations
- [ ] Document in `docs/performance/sync-performance-metrics.md`

#### Task I2: Documentation and ADRs
**Feature**: Phase R3 documentation
**Effort**: 4 hours | **Owner**: @Architect | **Day**: Week 4, Day 5

- [ ] Update `specs/contracts/sync-protocol.md` with conflict resolution, idempotency key format, retry logic
- [ ] Create `specs/decisions/adr-003-sync-engine-architecture.md`
- [ ] Create `specs/contracts/crop-listing-api.md`
- [ ] Update `STATE.md` with Phase R3 completion status
- [ ] Update `TASK.md` with Phase R4 preparation tasks

---

## 🚫 Sprint 4 & 5 Blockers

| Blocker | Impact | Probability | Mitigation |
|---------|--------|-------------|------------|
| **Sprint 3 incomplete (A1, A2, B4)** | 🔴 CRITICAL | MEDIUM | Day 0: Complete remaining Sprint 3 tasks |
| **AWS API Gateway throttling** | 🟡 MEDIUM | LOW | Monitor CloudWatch, request limit increase if needed |
| **Connectivity detection unreliable** | 🟡 MEDIUM | MEDIUM | Test on real devices, add manual "Sync Now" button |
| **Idempotency key collisions** | 🟡 MEDIUM | LOW | Use UUID v4 + timestamp |
| **Sync queue grows unbounded offline** | 🟡 MEDIUM | MEDIUM | Limit queue to 1000 operations, purge synced > 30 days |

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

---

## 📊 Phase R3 Exit Criteria (All Must Pass)

By March 28, 2026:
- [ ] Farmer can create crop listing OFFLINE on 2GB Android device
- [ ] Listing appears in local Isar database immediately
- [ ] When connectivity returns, listing syncs to server automatically
- [ ] Another user can browse listings from server
- [ ] Sync conflicts detected and resolved (server wins by default)
- [ ] Idempotency keys prevent duplicate listings on retry
- [ ] Each Lambda has minimum-necessary IAM permissions
- [ ] Zero data loss in 100 offline-online-offline cycles (integration test)
- [ ] Sync completes within 5s of connectivity restoration

**Phase R3 is DONE when**: All exit criteria met, integration tests passing, STATE.md and TASK.md updated

---

## 📝 Notes

### Sprint 3 Achievements (R2)
- ✅ Server auth code complete — 169 tests passing
- ✅ Mobile auth complete — online + offline login with BLoC
- ✅ All 6 Isar schemas created and tested
- ✅ Isar encryption with Android Keystore
- ✅ CI/CD security scans active

### Sprint 4 & 5 Focus (R3)
- **Sync engine**: First true offline-first capability
- **First feature**: Crop listings prove the architecture
- **Target**: 100 offline-online cycles with zero data loss
- **Key decision**: Local queue + executor pattern for resilience

### Key Design Decisions
- Result types over exceptions (server)
- Manual DynamoDB marshalling (AOT-safe)
- Server wins on conflicts (default strategy)
- Idempotency keys prevent duplicate operations
- Exponential backoff retry (5s, 15s, 45s)

---

**Created:** 2026-02-09
**Last Updated:** 2026-02-15 (Phase R3 mobile tasks completed)
**Sprint**: Sprint 4 & 5 (Mar 1 - Mar 28, 2026)
**Status:** R1 75% (A1+A2 pending), R2 90% (MILESTONE MET), **R3 55% IN PROGRESS** (11/20 tasks done - mobile complete, server pending)
