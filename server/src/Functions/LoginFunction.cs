using Amazon.CognitoIdentityProvider;
using Amazon.DynamoDBv2;
using Amazon.SimpleNotificationService;
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
/// Rate limiting: 3 consecutive failures → 15-minute account lockout (AUTH_015);
/// per source IP, 20 attempts per 5 minutes (AUTH_006).
/// Device tracking: X-Device-Id is remembered per account; a sign-in from an unknown
/// install is audited and, when NEW_DEVICE_SMS_ENABLED=true, announced by SMS.
/// </summary>
public class LoginFunction
{
    internal const int IpMaxAttempts = 20;
    internal const int IpWindowSeconds = 300;

    private readonly ICognitoAuthService _authService;
    private readonly IUserRepository _userRepository;
    private readonly IRateLimitService _rateLimitService;
    private readonly ISmsSender? _smsSender;

    /// <summary>
    /// Default constructor for Lambda runtime.
    /// </summary>
    public LoginFunction()
    {
        var clientId = Environment.GetEnvironmentVariable("COGNITO_CLIENT_ID")
                       ?? throw new InvalidOperationException("COGNITO_CLIENT_ID is not set.");
        var userPoolId = Environment.GetEnvironmentVariable("COGNITO_USER_POOL_ID")
                         ?? throw new InvalidOperationException("COGNITO_USER_POOL_ID is not set.");
        var tableName = Environment.GetEnvironmentVariable("USERS_TABLE")
                        ?? throw new InvalidOperationException("USERS_TABLE is not set.");
        var rateLimitTable = Environment.GetEnvironmentVariable("RATE_LIMITS_TABLE")
                             ?? throw new InvalidOperationException("RATE_LIMITS_TABLE is not set.");

        var cognito = new AmazonCognitoIdentityProviderClient();
        var dynamoDb = new AmazonDynamoDBClient();

        _authService = new CognitoAuthService(cognito, clientId, userPoolId);
        _userRepository = new UserRepository(dynamoDb, tableName);
        _rateLimitService = new RateLimitService(new RateLimitRepository(dynamoDb, rateLimitTable));
        _smsSender = new SnsSmsSender(
            new AmazonSimpleNotificationServiceClient(), SnsSmsSender.IsEnabledByEnvironment());
    }

    /// <summary>
    /// Constructor for testing.
    /// </summary>
    public LoginFunction(ICognitoAuthService authService, IUserRepository userRepository,
        IRateLimitService rateLimitService, ISmsSender? smsSender = null)
    {
        _authService = authService ?? throw new ArgumentNullException(nameof(authService));
        _userRepository = userRepository ?? throw new ArgumentNullException(nameof(userRepository));
        _rateLimitService = rateLimitService ?? throw new ArgumentNullException(nameof(rateLimitService));
        _smsSender = smsSender;
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

        // Per-IP cap first: credential stuffing across many phones must not get
        // per-phone budgets for free (there is no WAF in front of the HTTP API).
        var ipCheck = await _rateLimitService.CheckIpAllowedAsync(sourceIp, "login", IpMaxAttempts, IpWindowSeconds);
        if (!ipCheck.IsSuccess)
        {
            AuditLogger.LogSecurity("LOGIN_IP_RATE_LIMITED", correlationId: correlationId,
                outcome: "blocked", sourceIp: sourceIp);
            return LambdaResponse.RateLimited(ipCheck.Error!.Code, ipCheck.Error.Message, ipCheck.Error.RetryAfterSeconds);
        }
        await _rateLimitService.RecordIpAsync(sourceIp, "login", IpWindowSeconds);

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

        // Get user from DynamoDB. Resolve by the authenticated Cognito sub
        // first — every downstream authorization check (sync writes, /messages)
        // compares against the token's sub, so the profile we hand the client
        // MUST be that row. A phone lookup alone can return a stale duplicate
        // (e.g. a re-created account whose old profile row still holds the
        // phone number), giving the client a userId the server then rejects.
        UserDto? user;
        try
        {
            var sub = AuthHelper.GetSubFromJwt(tokens.IdToken);
            user = sub != null ? await _userRepository.GetUserByIdAsync(sub) : null;
            if (user == null)
            {
                user = await _userRepository.GetUserByPhoneAsync(loginRequest.PhoneNumber);
                if (user != null && sub != null && user.UserId != sub)
                {
                    context.Logger.LogWarning(
                        $"Login profile mismatch: phone row userId={user.UserId} != token sub={sub}");
                }
            }
        }
        catch (Exception ex)
        {
            context.Logger.LogWarning(
                $"Failed to fetch user profile: {ex.Message}");
            user = null;
        }

        if (user == null)
        {
            return LambdaResponse.Error(404, "AUTH_013",
                "User profile not found. Please contact support.");
        }

        // ACC-02: a deleted account, or one whose Cognito user is still being deleted, answers like a
        // wrong password (no enumeration). A deletion in_progress may log in to finish it.
        if (user.DeletionState is AccountDeletionPolicy.CognitoPending or AccountDeletionPolicy.Completed)
        {
            AuditLogger.LogSecurity("LOGIN_BLOCKED",
                userId: user.UserId, detail: "deleted", correlationId: correlationId,
                outcome: "failure", sourceIp: sourceIp);
            return LambdaResponse.Error(401, "AUTH_014", "Invalid phone number or password.");
        }

        // Admin-suspended accounts must not authenticate even with valid credentials.
        if (user.Suspended)
        {
            AuditLogger.LogSecurity("LOGIN_BLOCKED",
                userId: user.UserId, detail: "suspended", correlationId: correlationId,
                outcome: "failure", sourceIp: sourceIp);
            return LambdaResponse.Error(403, "AUTH_025",
                "This account has been suspended. Please contact support.");
        }

        var (newDevice, trackedUser) = await TrackDeviceAsync(request, user, context, correlationId, sourceIp);

        var response = new LoginResponse
        {
            Tokens = new TokenResponse
            {
                AccessToken = tokens.AccessToken,
                IdToken = tokens.IdToken,
                RefreshToken = tokens.RefreshToken,
                ExpiresIn = tokens.ExpiresIn
            },
            User = trackedUser,
            NewDevice = newDevice
        };

        AuditLogger.LogSecurity("LOGIN",
            userId: user.UserId, correlationId: correlationId,
            outcome: "success", sourceIp: sourceIp);

        return LambdaResponse.Ok(response, AppJsonContext.Default.LoginResponse);
    }

    /// <summary>
    /// Remember the signing-in install and flag one the account has not used
    /// before (the very first device is not "new"). Best effort: a failed profile
    /// write never blocks the login.
    /// </summary>
    private async Task<(bool newDevice, UserDto user)> TrackDeviceAsync(
        APIGatewayProxyRequest request, UserDto user, ILambdaContext context,
        string correlationId, string? sourceIp)
    {
        var deviceId = DeviceIdentity.ReadId(request);
        if (deviceId == null || user.KnownDevices.Contains(deviceId))
        {
            return (false, user);
        }

        var firstDevice = user.KnownDevices.Count == 0;
        var updated = user with { KnownDevices = DeviceIdentity.Remember(user.KnownDevices, deviceId) };
        try
        {
            var saved = await _userRepository.UpdateUserAsync(updated, user.Version);
            if (saved.IsSuccess)
            {
                updated = saved.Value!;
            }
        }
        catch (Exception ex)
        {
            context.Logger.LogWarning($"Could not record device for user {user.UserId}: [{ex.GetType().Name}]");
        }

        if (firstDevice)
        {
            return (false, updated);
        }

        var deviceName = DeviceIdentity.ReadName(request);
        AuditLogger.LogSecurity("LOGIN_NEW_DEVICE", userId: user.UserId, detail: deviceName,
            correlationId: correlationId, outcome: "success", sourceIp: sourceIp);

        if (_smsSender != null
            && string.Equals(Environment.GetEnvironmentVariable("NEW_DEVICE_SMS_ENABLED"), "true", StringComparison.OrdinalIgnoreCase))
        {
            var sent = await _smsSender.SendAsync(user.PhoneNumber, DeviceIdentity.NewDeviceSms(deviceName, user.PreferredLanguage));
            if (!sent)
            {
                context.Logger.LogWarning($"New-device SMS not sent for user {user.UserId}.");
            }
        }

        return (true, updated);
    }
}
