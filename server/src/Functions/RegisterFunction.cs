using Amazon.CognitoIdentityProvider;
using Amazon.DynamoDBv2;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.RateLimiting;
using WeredaConnect.Server.Services;
using WeredaConnect.Server.Validation;

namespace WeredaConnect.Server.Functions;

/// <summary>
/// POST /auth/register Lambda handler.
/// Flow: parse → validate → validate Fayda token → rate limit check → Cognito register →
///       admin confirm → get tokens → track registration → DynamoDB create → 201 response.
/// Fayda-verified: skips OTP, auto-confirms user, returns tokens immediately.
/// Rate limiting: 5 registrations per phone per day (AUTH_006).
/// </summary>
public class RegisterFunction
{
    private readonly ICognitoAuthService _authService;
    private readonly IFaydaOidcService _faydaService;
    private readonly IUserRepository _userRepository;
    private readonly IRateLimitService _rateLimitService;

    /// <summary>
    /// Default constructor for Lambda runtime.
    /// Resolves SSM parameter names and Secrets Manager ARNs to actual values.
    /// </summary>
    public RegisterFunction()
    {
        var clientId = Environment.GetEnvironmentVariable("COGNITO_CLIENT_ID")
                       ?? throw new InvalidOperationException("COGNITO_CLIENT_ID is not set.");
        var userPoolId = Environment.GetEnvironmentVariable("COGNITO_USER_POOL_ID")
                         ?? throw new InvalidOperationException("COGNITO_USER_POOL_ID is not set.");
        var tableName = Environment.GetEnvironmentVariable("USERS_TABLE")
                        ?? throw new InvalidOperationException("USERS_TABLE is not set.");
        var rateLimitTable = Environment.GetEnvironmentVariable("RATE_LIMITS_TABLE")
                             ?? throw new InvalidOperationException("RATE_LIMITS_TABLE is not set.");
        var faydaSigningSecretArn = Environment.GetEnvironmentVariable("FAYDA_SIGNING_SECRET_ARN")
                                    ?? throw new InvalidOperationException("FAYDA_SIGNING_SECRET_ARN is not set.");

        // Resolve Secrets Manager ARN to actual signing secret
        var faydaSigningSecret = Helpers.AwsParameterResolver.GetSecretValue(faydaSigningSecretArn);

        var cognito = new AmazonCognitoIdentityProviderClient();
        var dynamoDb = new AmazonDynamoDBClient();

        _authService = new CognitoAuthService(cognito, clientId, userPoolId);
        // RegisterFunction only needs token validation — use a placeholder for unused OIDC params
        _faydaService = new FaydaOidcService(
            new HttpClient(), "unused", "unused", "unused", faydaSigningSecret);
        _userRepository = new UserRepository(dynamoDb, tableName);
        _rateLimitService = new RateLimitService(new RateLimitRepository(dynamoDb, rateLimitTable));
    }

    /// <summary>
    /// Constructor for testing.
    /// </summary>
    public RegisterFunction(
        ICognitoAuthService authService,
        IFaydaOidcService faydaService,
        IUserRepository userRepository,
        IRateLimitService rateLimitService)
    {
        _authService = authService ?? throw new ArgumentNullException(nameof(authService));
        _faydaService = faydaService ?? throw new ArgumentNullException(nameof(faydaService));
        _userRepository = userRepository ?? throw new ArgumentNullException(nameof(userRepository));
        _rateLimitService = rateLimitService ?? throw new ArgumentNullException(nameof(rateLimitService));
    }

    public async Task<APIGatewayProxyResponse> FunctionHandler(
        APIGatewayProxyRequest request,
        ILambdaContext context)
    {
        context.Logger.LogInformation("Register invoked");

        // Parse
        RegisterRequest? registerRequest;
        try
        {
            registerRequest = JsonSerializer.Deserialize(
                request.Body ?? "",
                AppJsonContext.Default.RegisterRequest);
        }
        catch (JsonException)
        {
            return LambdaResponse.Error(400, "AUTH_007", "Invalid JSON in request body.");
        }

        // Normalize phone number (accept +251, 00251, or 0 prefix)
        if (registerRequest != null)
        {
            var normalizedPhone = RequestValidator.NormalizePhoneNumber(registerRequest.PhoneNumber);
            if (normalizedPhone != null)
            {
                registerRequest = registerRequest with { PhoneNumber = normalizedPhone };
            }
        }

        // Validate
        var validation = RequestValidator.ValidateRegisterRequest(registerRequest);
        if (!validation.IsValid)
        {
            return LambdaResponse.ValidationError(validation);
        }

        var correlationId = AuditLogger.GetCorrelationId(request);
        var sourceIp = AuditLogger.GetSourceIp(request);

        // Validate Fayda verification token (optional — two-tier model)
        FaydaVerificationResult? faydaVerification = null;
        if (!string.IsNullOrWhiteSpace(registerRequest!.FaydaVerificationToken))
        {
            var faydaResult = _faydaService.ValidateVerificationToken(registerRequest.FaydaVerificationToken);
            if (!faydaResult.IsSuccess)
            {
                var faydaError = faydaResult.Error!;
                return LambdaResponse.Error(faydaError.HttpStatus, faydaError.Code, faydaError.Message, faydaError.Field);
            }
            faydaVerification = faydaResult.Value!;

            // Re-check Fayda ID uniqueness to close TOCTOU race between fayda-verify and register
            var existingUser = await _userRepository.GetUserByFaydaIdAsync(faydaVerification.FaydaId);
            if (existingUser != null)
            {
                return LambdaResponse.Error(409, "FAYDA_004",
                    "This Fayda ID is already linked to another account.", "faydaVerificationToken");
            }
        }

        // Rate limit check: registration flood protection
        var rateLimitCheck = await _rateLimitService.CheckRegistrationAllowedAsync(registerRequest.PhoneNumber);
        if (!rateLimitCheck.IsSuccess)
        {
            var rlError = rateLimitCheck.Error!;
            return LambdaResponse.RateLimited(rlError.Code, rlError.Message);
        }

        // Cognito register
        var cognitoResult = await _authService.RegisterAsync(
            registerRequest.PhoneNumber,
            registerRequest.Password,
            registerRequest.Role,
            registerRequest.Woreda,
            registerRequest.DisplayName,
            registerRequest.PreferredLanguage);

        if (!cognitoResult.IsSuccess)
        {
            var error = cognitoResult.Error!;
            AuditLogger.LogSecurity("REGISTER_FAILED",
                detail: $"Cognito error: {error.Code}", correlationId: correlationId,
                outcome: "failure", sourceIp: sourceIp);
            return LambdaResponse.Error(error.HttpStatus, error.Code, error.Message, error.Field);
        }

        var userId = cognitoResult.Value!;

        // Admin-confirm the user (skip OTP — Fayda-verified)
        var confirmResult = await _authService.AdminConfirmSignUpAsync(registerRequest.PhoneNumber);
        if (!confirmResult.IsSuccess)
        {
            var error = confirmResult.Error!;
            context.Logger.LogWarning(
                $"Failed to admin-confirm user {userId}: {error.Code} {error.Message}");
            return LambdaResponse.Error(error.HttpStatus, error.Code, error.Message, error.Field);
        }

        // Get tokens immediately via USER_PASSWORD_AUTH
        var loginResult = await _authService.LoginAsync(
            registerRequest.PhoneNumber, registerRequest.Password);
        if (!loginResult.IsSuccess)
        {
            var error = loginResult.Error!;
            context.Logger.LogWarning(
                $"Failed to get tokens for newly registered user {userId}: {error.Code} {error.Message}");
            return LambdaResponse.Error(error.HttpStatus, error.Code, error.Message, error.Field);
        }

        // Track registration for flood protection
        await _rateLimitService.RecordRegistrationAsync(registerRequest.PhoneNumber);

        var tokens = loginResult.Value!;
        var now = DateTime.UtcNow.ToString("o");

        // DynamoDB create user — Fayda fields set only when verified
        var userDto = new UserDto
        {
            UserId = userId,
            PhoneNumber = registerRequest.PhoneNumber,
            DisplayName = registerRequest.DisplayName,
            FullName = faydaVerification?.FullName,
            Role = registerRequest.Role,
            Woreda = registerRequest.Woreda,
            FaydaId = faydaVerification?.FaydaId,
            FaydaVerified = faydaVerification != null,
            FaydaVerifiedAt = faydaVerification != null ? now : null,
            PreferredLanguage = registerRequest.PreferredLanguage,
            IsPhoneVerified = true,
            BuyerType = registerRequest.BuyerType,
            CompanyName = registerRequest.CompanyName,
            CreatedAt = now,
            UpdatedAt = now,
            Version = 1
        };

        try
        {
            await _userRepository.CreateUserAsync(userDto);
        }
        catch (Exception ex)
        {
            // Do NOT return success: a Cognito user with no profile record can never
            // log in (LoginFunction returns AUTH_013 "profile not found"). Surface the
            // failure so the client can retry instead of storing tokens for a dead account.
            context.Logger.LogError(
                $"Failed to create DynamoDB user record for {userId}: [{ex.GetType().Name}]");
            AuditLogger.LogSecurity("REGISTER_PROFILE_FAILED",
                userId: userId, correlationId: correlationId,
                outcome: "failure", sourceIp: sourceIp);
            return LambdaResponse.Error(500, "AUTH_007",
                "Registration could not be completed. Please try again.");
        }

        var response = new RegisterResponse
        {
            UserId = userId,
            PhoneNumber = registerRequest.PhoneNumber,
            Tokens = new TokenResponse
            {
                AccessToken = tokens.AccessToken,
                IdToken = tokens.IdToken,
                RefreshToken = tokens.RefreshToken,
                ExpiresIn = tokens.ExpiresIn
            },
            User = userDto,
            Message = faydaVerification != null
                ? "Registration successful. Fayda identity verified."
                : "Registration successful. Verify Fayda ID to unlock trades above 10,000 ETB."
        };

        AuditLogger.LogSecurity("REGISTER",
            userId: userId, correlationId: correlationId,
            outcome: "success", sourceIp: sourceIp);

        return LambdaResponse.Created(response, AppJsonContext.Default.RegisterResponse);
    }
}
