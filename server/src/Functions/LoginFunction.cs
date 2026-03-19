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
/// POST /auth/login Lambda handler.
/// Flow: parse → validate → rate limit check → Cognito login → track result → DynamoDB get user → 200 response.
/// Rate limiting: 3 consecutive failures → 15-minute account lockout (AUTH_015).
/// </summary>
public class LoginFunction
{
    private readonly ICognitoAuthService _authService;
    private readonly IUserRepository _userRepository;
    private readonly IRateLimitService _rateLimitService;

    /// <summary>
    /// Default constructor for Lambda runtime.
    /// </summary>
    public LoginFunction()
    {
        var clientId = Environment.GetEnvironmentVariable("COGNITO_CLIENT_ID")
                       ?? throw new InvalidOperationException("COGNITO_CLIENT_ID is not set.");
        var tableName = Environment.GetEnvironmentVariable("USERS_TABLE")
                        ?? throw new InvalidOperationException("USERS_TABLE is not set.");
        var rateLimitTable = Environment.GetEnvironmentVariable("RATE_LIMITS_TABLE")
                             ?? throw new InvalidOperationException("RATE_LIMITS_TABLE is not set.");

        var cognito = new AmazonCognitoIdentityProviderClient();
        var dynamoDb = new AmazonDynamoDBClient();

        _authService = new CognitoAuthService(cognito, clientId);
        _userRepository = new UserRepository(dynamoDb, tableName);
        _rateLimitService = new RateLimitService(new RateLimitRepository(dynamoDb, rateLimitTable));
    }

    /// <summary>
    /// Constructor for testing.
    /// </summary>
    public LoginFunction(ICognitoAuthService authService, IUserRepository userRepository, IRateLimitService rateLimitService)
    {
        _authService = authService ?? throw new ArgumentNullException(nameof(authService));
        _userRepository = userRepository ?? throw new ArgumentNullException(nameof(userRepository));
        _rateLimitService = rateLimitService ?? throw new ArgumentNullException(nameof(rateLimitService));
    }

    public async Task<APIGatewayProxyResponse> FunctionHandler(
        APIGatewayProxyRequest request,
        ILambdaContext context)
    {
        context.Logger.LogInformation("Login invoked");

        // Parse
        LoginRequest? loginRequest;
        try
        {
            loginRequest = JsonSerializer.Deserialize(
                request.Body ?? "",
                AppJsonContext.Default.LoginRequest);
        }
        catch (JsonException)
        {
            return LambdaResponse.Error(400, "AUTH_007", "Invalid JSON in request body.");
        }

        // Normalize phone number (accept +251, 00251, or 0 prefix)
        if (loginRequest != null)
        {
            var normalizedPhone = RequestValidator.NormalizePhoneNumber(loginRequest.PhoneNumber);
            if (normalizedPhone != null)
            {
                loginRequest = loginRequest with { PhoneNumber = normalizedPhone };
            }
        }

        // Validate
        var validation = RequestValidator.ValidateLoginRequest(loginRequest);
        if (!validation.IsValid)
        {
            return LambdaResponse.ValidationError(validation);
        }

        var correlationId = AuditLogger.GetCorrelationId(request);
        var sourceIp = AuditLogger.GetSourceIp(request);

        // Rate limit check: is account locked?
        var rateLimitCheck = await _rateLimitService.CheckLoginAllowedAsync(loginRequest!.PhoneNumber);
        if (!rateLimitCheck.IsSuccess)
        {
            var rlError = rateLimitCheck.Error!;
            AuditLogger.LogSecurity("LOGIN_RATE_LIMITED",
                detail: loginRequest.PhoneNumber, correlationId: correlationId,
                outcome: "blocked", sourceIp: sourceIp);
            return LambdaResponse.Error(rlError.HttpStatus, rlError.Code, rlError.Message,
                retryAfterSeconds: rlError.RetryAfterSeconds);
        }

        // Cognito login
        var loginResult = await _authService.LoginAsync(
            loginRequest.PhoneNumber, loginRequest.Password);

        if (!loginResult.IsSuccess)
        {
            var error = loginResult.Error!;

            // Track failed login attempt (only for credential failures, not service errors)
            if (error.Code == "AUTH_014")
            {
                var failCount = await _rateLimitService.RecordFailedLoginAsync(loginRequest.PhoneNumber);
                if (failCount >= RateLimitService.MaxLoginFailures)
                {
                    return LambdaResponse.Error(403, "AUTH_015",
                        "Account locked due to too many failed attempts.",
                        retryAfterSeconds: RateLimitService.LoginLockoutSeconds);
                }
            }

            AuditLogger.LogSecurity("LOGIN_FAILED",
                detail: "Invalid credentials", correlationId: correlationId,
                outcome: "failure", sourceIp: sourceIp);
            return LambdaResponse.Error(error.HttpStatus, error.Code, error.Message, error.Field);
        }

        // Success: reset login failure counter
        await _rateLimitService.ResetLoginAttemptsAsync(loginRequest.PhoneNumber);

        var tokens = loginResult.Value!;

        // Get user from DynamoDB
        UserDto? user;
        try
        {
            user = await _userRepository.GetUserByPhoneAsync(loginRequest.PhoneNumber);
        }
        catch (Exception ex)
        {
            context.Logger.LogWarning(
                $"Failed to fetch user by phone: {ex.Message}");
            user = null;
        }

        if (user == null)
        {
            return LambdaResponse.Error(404, "AUTH_013",
                "User profile not found. Please contact support.");
        }

        var response = new LoginResponse
        {
            Tokens = new TokenResponse
            {
                AccessToken = tokens.AccessToken,
                IdToken = tokens.IdToken,
                RefreshToken = tokens.RefreshToken,
                ExpiresIn = tokens.ExpiresIn
            },
            User = user
        };

        AuditLogger.LogSecurity("LOGIN",
            userId: user.UserId, correlationId: correlationId,
            outcome: "success", sourceIp: sourceIp);

        return LambdaResponse.Ok(response, AppJsonContext.Default.LoginResponse);
    }
}
