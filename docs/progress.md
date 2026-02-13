# WeredaConnect Development Progress

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
