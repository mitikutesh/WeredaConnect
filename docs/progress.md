# WeredaConnect Development Progress

## Sprint 3 Progress Update: Mobile Foundation Complete (2026-02-14)

**Latest Commit**: 4087069 - fix(mobile): resolve Sprint 3 critical security and API compatibility issues
**Status**: Sprint 3 Week 1 complete — Mobile foundation laid, security vulnerabilities fixed
**Test Status**: 201/202 tests passing (99.5%)

### Completed This Week ✅

**B1: F-07 Mobile Domain Models (2 days)**
- Created all 6 Isar schemas with embedded objects and indexes
- User, CropListing, Trip, Delivery, Payment, SyncOperation
- Integration tests passing for all schemas

**B2: F-04 Isar Encryption (1 day)**
- Implemented AES-256 encryption with Android Keystore
- flutter_secure_storage integration
- Encryption verified in integration tests

**B3: F-03 Mobile Auth Flow (3 days)**
- AuthService with online/offline login branching
- OfflineAuthCache with PBKDF2 (100k iterations)
- AuthBloc with state management
- Login, Register, OTP verification screens
- Amharic/Oromo/English localization

**Security Fixes (0.5 days)**
- Fixed CRITICAL: Password storage vulnerability
- Fixed CRITICAL: Offline rate limiting (5 attempts = 15-min lockout)
- Fixed HIGH: ConnectivityResult API compatibility (6 occurrences)
- Fixed MEDIUM: Isar encryption API updates

### Sprint 3 Remaining Work

**Priority A (BLOCKERS):**
- [ ] A1: Purge git history (F-01) — 2 hours
- [ ] A2: Deploy Lambda to AWS (F-16) — 1 day
- [ ] A3: CI/CD security scans (F-19) — 4 hours

**Priority B (HIGH):**
- [ ] B4: Add 3 missing DynamoDB tables (F-07 infra) — 3 hours

**Priority C (STRETCH):**
- [ ] C1: Certificate pinning (F-05) — 4 hours

### Phase Progress
- **R1: Security Foundation** — 50% (2/4 complete)
- **R2: Auth + Data Models** — 65% (5/6 complete, up from 50%)

---

## Sprint 3 Kickoff: PO Assessment & Sprint Planning (2026-02-14)

**Sprint Goal**: Deploy and Secure the Backend, Lay Mobile Foundation
**Duration**: Feb 14 - Feb 28, 2026 (2 weeks)
**Status**: Active — Week 1 in progress

### Sprint Planning Documents Created
- **SPRINT_PLANNING.md** — Full sprint plan with acceptance criteria, timeline, blockers
- Updated **TASK.md** — Sprint 3 backlog prioritized (Priority Groups A, B, C)
- Updated **STATE.md** — Current status, sprint checkpoint, resumption prompt
- Updated **ROADMAP.md** — Phase R1/R2 progress tracking with Sprint 3 tasks

### Current State Assessment

#### What's Implemented ✅
**Server (100% complete):**
- 169 tests passing
- All 6 auth Lambda functions (Register, VerifyOTP, Login, Refresh, Logout, Authorizer)
- Rate limiting service + repository
- Input validation with Ethiopian phone format
- JWT validation
- DynamoDB UserRepository with optimistic concurrency
- Program.cs AOT bootstrap

**Infrastructure (70% complete):**
- CDK stacks: Database, API, Lambda constructs (11 tests passing)
- Cognito User Pool with SMS OTP
- API Gateway with 6 auth routes
- DynamoDB tables: Users, Trips, Crops, RateLimits
- ⚠️ **Missing:** Deliveries, Payments, SyncMetadata tables
- ⚠️ **NOT DEPLOYED:** Lambda not on AWS yet

**Mobile (5% complete):**
- ✅ Navigation structure (Home, Listings, Profile, Settings)
- ✅ Localization (Amharic, Afan Oromo, English)
- ❌ **Missing:** All core functionality (Isar, auth, sync, networking)

**Security (50% complete):**
- ✅ Pre-commit hooks (.gitleaks.toml, .pre-commit-config.yaml)
- ❌ **CRITICAL:** Private key still in git history

### Sprint 3 Priorities

**Priority Group A (MUST DO — Week 1):**
- A1: Purge git history (F-01) — 🔴 BLOCKER (2hr)
- A2: Deploy Lambda to AWS (F-16) — 🔴 BLOCKER (1 day)
- A3: CI/CD security scans (F-19) — 🟡 HIGH (4hr)

**Priority Group B (SPRINT FOCUS — Week 1-2):**
- B1: Create 6 Isar schemas (F-07 mobile) — 🔴 FOUNDATION (2 days)
- B2: Isar encryption (F-04) — 🔴 MUST DO (1 day)
- B3: Auth service + Login UI (F-03 mobile) — 🔴 MUST DO (3 days)
- B4: Add 3 missing DynamoDB tables (F-07 infra) — 🟡 HIGH (3hr)

**Priority Group C (STRETCH — Week 2):**
- C1: Certificate pinning (F-05) — 🟢 NICE TO HAVE (4hr)
- C2: Update auth API contract — 🟢 DOCUMENTATION (2hr)

### Sprint Success Metrics (Must-Have)
- [ ] Zero secrets in git history
- [ ] HealthCheckFunction deployed to AWS with <2s cold start
- [ ] All 6 Isar schemas generating without errors
- [ ] Isar encryption enabled and tested
- [ ] User can register with Ethiopian phone (+251XXXXXXXXX)
- [ ] User can log in online (Cognito)
- [ ] User can log in offline (cached credentials)

### Sprint Blockers
- 🔴 **Flutter SDK not installed** — Install on Day 1
- 🟡 **AWS credentials missing** — Configure on Day 1
- 🟡 **.NET AOT publish untested** — Test locally before AWS deploy

### Target Outcomes
- **R1: Security Foundation** → 100% complete by Feb 21 (Week 1 end)
- **R2: Auth + Data Models** → 80% complete by Feb 28 (Sprint end)

---

## Phase 3: CDK Infrastructure + AOT Bootstrap (2026-02-13)

**Branch**: `Authentication-Encryptionministries`
**Status**: Complete — 169 server tests + 11 CDK tests passing

### What was built

Wired the server auth code into deployable infrastructure: Program.cs AOT bootstrap, CDK Cognito User Pool, auth Lambda functions, API Gateway routes, DynamoDB GSIs, and rate limiting table.

### New/modified files

**Server:**
- `src/Program.cs` — AOT bootstrap entry point with `FUNCTION_HANDLER` routing to 7 handlers
- `WeredaConnect.Server.csproj` — Added `OutputType=Exe`, `Amazon.Lambda.RuntimeSupport` package

**CDK Infrastructure:**
- `lib/stacks/api-stack.ts` — Full rewrite: Cognito User Pool (phone-based, SMS OTP), 6 Lambda functions, HTTP API Gateway with 6 routes, per-function least-privilege IAM, throttling (100 req/s, 200 burst)
- `lib/stacks/database-stack.ts` — Added RateLimits table (pk/sk + TTL), PhoneIndex GSI, RoleWaredaIndex GSI on Users table
- `lib/constructs/lambda-construct.ts` — Added `handlerName` prop for `FUNCTION_HANDLER` env var
- `bin/wereda-connect.ts` — Updated stack wiring (3 stacks: Database → API → Monitoring)

**CDK Tests (fixed):**
- `test/unit/stack.test.ts` — Added `rateLimitsTable` prop, Cognito/Lambda/route assertions
- `test/integration/cdk-synth.test.ts` — Removed deleted AuthStack, fixed stack count to 3
- `test/snapshot/` — Regenerated snapshot

**Governance:**
- `specs/contracts/auth-api.md` — Verified complete (672 lines, all 5 endpoints)
- `scripts/deploy.sh` — Deployment script
- `.gitleaks.toml`, `.pre-commit-config.yaml` — Secret scanning

### Key architecture decisions

- **Consolidated ApiStack** — Cognito + auth Lambdas + routes in one stack (avoids cross-stack cyclic references)
- **`FUNCTION_HANDLER` env var** — `_HANDLER` is reserved by Lambda runtime
- **Least-privilege IAM** — Each Lambda gets only its specific Cognito actions
- **ARM64 + provided.al2023** — Graviton2 for better price/performance

### Bugs fixed

1. **`_HANDLER` reserved** — CDK synth failed; renamed to `FUNCTION_HANDLER`
2. **Cross-stack cyclic reference** — Separate AuthStack adding routes to ApiStack's HttpApi; consolidated into ApiStack
3. **CDK tests stale** — Tests referenced deleted AuthStack and missing rateLimitsTable prop

---

## Phase 2: Cognito Integration + Lambda Handlers (2026-02-12)

**Branch**: `Authentication-Encryptionministries`
**Status**: Complete — 169 tests passing (including rate limiting), 0 build warnings

### What was built

Implemented the full server-side auth business logic: Cognito service layer, DynamoDB user repository, 5 Lambda handler functions, rate limiting, and input validation — all AOT-compatible.

### New files (14 source + 8 test)

**Foundation layer:**
- `src/Services/AuthResult.cs` — Generic `AuthResult<T>` result type (replaces exceptions for expected auth failures)
- `src/Helpers/LambdaResponse.cs` — Static response builder with security headers, AOT-safe via `JsonTypeInfo<T>`

**Service layer:**
- `src/Services/ICognitoAuthService.cs` — Interface + `CognitoAuthTokens` record
- `src/Services/CognitoAuthService.cs` — Cognito SDK wrapper, maps exceptions to AUTH_* codes

**Data layer:**
- `src/Data/IUserRepository.cs` — Repository interface
- `src/Data/UserRepository.cs` — Low-level DynamoDB API, manual marshalling (AOT-safe), optimistic concurrency

**Lambda handlers (parse → validate → service → response):**
- `src/Functions/RegisterFunction.cs` — POST /auth/register → 201
- `src/Functions/VerifyOtpFunction.cs` — POST /auth/verify-otp → 200
- `src/Functions/LoginFunction.cs` — POST /auth/login → 200
- `src/Functions/RefreshFunction.cs` — POST /auth/refresh → 200
- `src/Functions/LogoutFunction.cs` — POST /auth/logout → 200

**Modified:**
- `src/Validation/RequestValidator.cs` — Added `ValidateLogoutRequest`
- `tests/Validation/RequestValidatorTests.cs` — Added 4 logout validation tests

**Tests (57 new):**
- `tests/Helpers/LambdaResponseTests.cs`
- `tests/Services/CognitoAuthServiceTests.cs` (~15 tests)
- `tests/Data/UserRepositoryTests.cs` (~8 tests)
- `tests/Functions/RegisterFunctionTests.cs` (~6 tests)
- `tests/Functions/VerifyOtpFunctionTests.cs` (~5 tests)
- `tests/Functions/LoginFunctionTests.cs` (~6 tests)
- `tests/Functions/RefreshFunctionTests.cs` (~4 tests)
- `tests/Functions/LogoutFunctionTests.cs` (~4 tests)

### Key code patterns

**AuthResult pattern:**
```csharp
public static AuthResult<T> Success(T value) => new(value);
public static AuthResult<T> Failure(int httpStatus, string code, string message, string? field = null) =>
    new(new AuthResultError { HttpStatus = httpStatus, Code = code, Message = message, Field = field });
```

**LambdaResponse with AOT-safe serialization:**
```csharp
public static APIGatewayProxyResponse Ok<T>(T body, JsonTypeInfo<T> typeInfo) =>
    new() { StatusCode = 200, Body = JsonSerializer.Serialize(body, typeInfo), Headers = SecurityHeaders };
```

**Cognito exception → AUTH code mapping:**
| Exception | AUTH Code | HTTP Status |
|-----------|-----------|-------------|
| `UsernameExistsException` | AUTH_005 | 409 |
| `NotAuthorizedException` (login) | AUTH_014 | 401 |
| `NotAuthorizedException` (refresh) | AUTH_018 | 401 |
| `CodeMismatchException` | AUTH_011 | 400 |
| `ExpiredCodeException` | AUTH_010 | 400 |
| `UserNotConfirmedException` | AUTH_016 | 403 |
| `TooManyRequestsException` | AUTH_006 | 429 |
| `InternalErrorException` / `AmazonServiceException` | AUTH_008 | 503 |

**DynamoDB manual marshalling (AOT-safe):**
```csharp
internal static Dictionary<string, AttributeValue> ToItem(UserDto user) =>
    new() {
        { "userId", new AttributeValue { S = user.UserId } },
        { "phoneNumber", new AttributeValue { S = user.PhoneNumber } },
        { "version", new AttributeValue { N = user.Version.ToString() } },
        // ... all fields manually mapped
    };
```

**Optimistic concurrency:**
```csharp
ConditionExpression = "#v = :expectedVersion",
ExpressionAttributeNames = new() { { "#v", "version" } },
ExpressionAttributeValues = new() { { ":expectedVersion", new AttributeValue { N = expectedVersion.ToString() } } }
```

### Bugs fixed during implementation

1. **`ServiceUnavailableException` not in SDK** — Replaced with `AmazonServiceException` catch-all (after specific catches)
2. **Anonymous types + source-generated JSON** — Tests can't use `AppJsonContext.Default.Options` for anonymous objects; switched to plain `JsonSerializerOptions`
3. **Missing `preferredLanguage` in test data** — .NET 10 source-generated deserializer doesn't apply `init` property defaults when field absent from JSON

### Follow-up (completed in Phase 3)
- ~~CDK: PhoneIndex + RoleWaredaIndex GSIs on users table~~ ✅
- ~~CDK: Cognito User Pool + App Client provisioning~~ ✅
- ~~CDK: API Gateway routes + authorizer wiring~~ ✅
- ~~CDK: Lambda function deployments~~ ✅
- ~~`Program.cs` / AOT bootstrap entry point~~ ✅
