# Session Summary: Phase 2 — Cognito Integration + Lambda Handlers

**Date**: 2026-02-12
**Branch**: `Authentication-Encryptionministries`
**Result**: 127 tests passing, 0 build warnings

---

## Scope

Implemented the full server-side auth business logic on top of Phase 1 (JWT validator, authorizer, DTOs, validation). Phase 2 adds: Cognito service layer, DynamoDB user repository, and 5 Lambda handler functions — all AOT-compatible.

## Files created (14 source + 8 test)

### Source

| File | Purpose |
|------|---------|
| `src/Services/AuthResult.cs` | Generic `AuthResult<T>` result type with `Success`/`Failure` factory methods |
| `src/Helpers/LambdaResponse.cs` | Static response builder — `Ok<T>`, `Created<T>`, `Error`, `ValidationError` — with security headers |
| `src/Services/ICognitoAuthService.cs` | Interface + `CognitoAuthTokens` record (5 methods) |
| `src/Services/CognitoAuthService.cs` | Cognito SDK wrapper mapping exceptions → AUTH_* codes |
| `src/Data/IUserRepository.cs` | Repository interface (CRUD + phone lookup via GSI) |
| `src/Data/UserRepository.cs` | Low-level DynamoDB, manual `ToItem`/`FromItem`, optimistic concurrency |
| `src/Functions/RegisterFunction.cs` | POST /auth/register → 201 |
| `src/Functions/VerifyOtpFunction.cs` | POST /auth/verify-otp → 200 |
| `src/Functions/LoginFunction.cs` | POST /auth/login → 200 (tokens + user) |
| `src/Functions/RefreshFunction.cs` | POST /auth/refresh → 200 |
| `src/Functions/LogoutFunction.cs` | POST /auth/logout → 200 |

### Modified

| File | Change |
|------|--------|
| `src/Validation/RequestValidator.cs` | Added `ValidateLogoutRequest` |
| `tests/Validation/RequestValidatorTests.cs` | Added 4 logout validation tests |

### Tests (57 new)

| File | Count |
|------|-------|
| `tests/Helpers/LambdaResponseTests.cs` | ~6 |
| `tests/Services/CognitoAuthServiceTests.cs` | ~15 |
| `tests/Data/UserRepositoryTests.cs` | ~8 |
| `tests/Functions/RegisterFunctionTests.cs` | ~6 |
| `tests/Functions/VerifyOtpFunctionTests.cs` | ~5 |
| `tests/Functions/LoginFunctionTests.cs` | ~6 |
| `tests/Functions/RefreshFunctionTests.cs` | ~4 |
| `tests/Functions/LogoutFunctionTests.cs` | ~4 |

## Key code patterns

### AuthResult (replaces exceptions for expected failures)

```csharp
public sealed class AuthResult<T>
{
    public bool IsSuccess { get; }
    public T? Value { get; }
    public AuthResultError? Error { get; }

    public static AuthResult<T> Success(T value) => new(value);
    public static AuthResult<T> Failure(int httpStatus, string code, string message, string? field = null) =>
        new(new AuthResultError { HttpStatus = httpStatus, Code = code, Message = message, Field = field });
}
```

### LambdaResponse — AOT-safe via JsonTypeInfo<T>

```csharp
public static APIGatewayProxyResponse Ok<T>(T body, JsonTypeInfo<T> typeInfo) =>
    new()
    {
        StatusCode = 200,
        Body = JsonSerializer.Serialize(body, typeInfo),
        Headers = SecurityHeaders   // HSTS, nosniff, DENY, no-store, application/json
    };
```

### Cognito exception mapping

```csharp
catch (UsernameExistsException)
    => AuthResult<string>.Failure(409, "AUTH_005", "Phone number already registered.", "phoneNumber");
catch (NotAuthorizedException)
    => AuthResult<CognitoAuthTokens>.Failure(401, "AUTH_014", "Invalid credentials.");
catch (CodeMismatchException)
    => AuthResult<bool>.Failure(400, "AUTH_011", "Invalid OTP code.", "otpCode");
catch (ExpiredCodeException)
    => AuthResult<bool>.Failure(400, "AUTH_010", "OTP code has expired.", "otpCode");
catch (UserNotConfirmedException)
    => AuthResult<CognitoAuthTokens>.Failure(403, "AUTH_016", "Phone number not verified.");
catch (TooManyRequestsException)
    => AuthResult<T>.Failure(429, "AUTH_006", "Too many requests. Please try again later.");
catch (InternalErrorException) | catch (AmazonServiceException)
    => AuthResult<T>.Failure(503, "AUTH_008", "Authentication service temporarily unavailable.");
```

### DynamoDB manual marshalling (AOT-safe)

```csharp
internal static Dictionary<string, AttributeValue> ToItem(UserDto user)
{
    var item = new Dictionary<string, AttributeValue>
    {
        { "userId", new AttributeValue { S = user.UserId } },
        { "phoneNumber", new AttributeValue { S = user.PhoneNumber } },
        { "role", new AttributeValue { S = user.Role } },
        { "woreda", new AttributeValue { S = user.Woreda } },
        { "isPhoneVerified", new AttributeValue { BOOL = user.IsPhoneVerified } },
        { "createdAt", new AttributeValue { S = user.CreatedAt } },
        { "updatedAt", new AttributeValue { S = user.UpdatedAt } },
        { "version", new AttributeValue { N = user.Version.ToString() } }
    };
    if (user.DisplayName != null)
        item["displayName"] = new AttributeValue { S = user.DisplayName };
    return item;
}
```

### Optimistic concurrency on update

```csharp
var updateRequest = new UpdateItemRequest
{
    TableName = _tableName,
    Key = new() { { "userId", new AttributeValue { S = user.UserId } } },
    ConditionExpression = "#v = :expectedVersion",
    ExpressionAttributeNames = new() { { "#v", "version" } },
    ExpressionAttributeValues = new()
    {
        { ":expectedVersion", new AttributeValue { N = expectedVersion.ToString() } },
        { ":newVersion", new AttributeValue { N = (expectedVersion + 1).ToString() } },
        // ... other update values
    },
    UpdateExpression = "SET #v = :newVersion, ..."
};
```

### Lambda handler pattern (constructor DI)

```csharp
public class RegisterFunction
{
    private readonly ICognitoAuthService _authService;
    private readonly IUserRepository _userRepository;

    // Default: Lambda runtime (reads env vars, creates real services)
    public RegisterFunction()
    {
        var cognitoClient = new AmazonCognitoIdentityProviderClient();
        var dynamoClient = new AmazonDynamoDBClient();
        _authService = new CognitoAuthService(cognitoClient, Environment.GetEnvironmentVariable("COGNITO_USER_POOL_ID")!, ...);
        _userRepository = new UserRepository(dynamoClient, Environment.GetEnvironmentVariable("USERS_TABLE_NAME")!);
    }

    // Test: inject mocks
    public RegisterFunction(ICognitoAuthService authService, IUserRepository userRepository)
    {
        _authService = authService;
        _userRepository = userRepository;
    }

    public async Task<APIGatewayProxyResponse> FunctionHandler(APIGatewayProxyRequest request, ILambdaContext context)
    {
        // 1. Parse
        // 2. Validate
        // 3. Call Cognito
        // 4. Create DB record
        // 5. Return response
    }
}
```

## Bugs fixed during implementation

| Bug | Root cause | Fix |
|-----|-----------|-----|
| `ServiceUnavailableException` not found | Type doesn't exist in `Amazon.CognitoIdentityProvider.Model` | Used `AmazonServiceException` (from `Amazon.Runtime`) as general catch-all |
| Anonymous types fail with source-gen JSON | `AppJsonContext.Default.Options` can't serialize anonymous types | Tests use plain `JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }` |
| Missing `preferredLanguage` in test data | .NET 10 source-gen deserializer doesn't apply `init` property defaults for absent fields | Added `preferredLanguage = "am"` to test anonymous objects |

## Design decisions

| Decision | Rationale |
|----------|-----------|
| Result types over exceptions | Auth failures are expected outcomes, not exceptional |
| `CognitoAuthTokens` separate from `TokenResponse` DTO | Decouple service layer from API contract |
| Manual DynamoDB marshalling | AOT-safe, no Document Model reflection |
| VerifyOTP returns `tokens: null` | Cognito doesn't issue tokens after ConfirmSignUp without password |
| Register: Cognito first, DynamoDB second | Cognito is auth source of truth; DB failure is non-fatal |
| Constructor DI (default + parameterized) | Matches existing HealthCheckFunction/AuthorizerFunction patterns |
| `UserNotFoundException` same as `NotAuthorizedException` in login | Prevents user enumeration |
| `LogoutAsync` treats `NotAuthorizedException` as success | Idempotent — token already revoked |

## Not in scope (follow-up)

- CDK: PhoneIndex + RoleWaredaIndex GSIs on users table
- CDK: Cognito User Pool + App Client provisioning
- CDK: API Gateway routes + authorizer wiring
- CDK: Lambda function deployments
- `Program.cs` / AOT bootstrap entry point (F-16)
- Security review was initiated but interrupted