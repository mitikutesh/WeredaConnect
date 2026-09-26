using System.Text;
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
/// POST /auth/register Lambda handler.
/// Flow: parse → validate → validate Fayda token (single use) → rate limit → Cognito sign-up →
///   phone proven by Fayda (its phone_number claim equals the registration phone):
///     admin-confirm, tokens, 201.
///   otherwise: profile stored with isPhoneVerified=false, SMS code sent, 202 otpRequired;
///     the client completes POST /auth/verify-otp (VerifyOtpFunction) to get tokens.
/// An earlier registration of the same phone that never confirmed its code is replaced
/// (CognitoAuthService replaces the unconfirmed Cognito user; the stale profile row is
/// removed here so one phone never maps to two profiles).
/// Rate limiting: 5 registrations per phone per day (AUTH_006).
/// </summary>
public class RegisterFunction
{
    private const int VerificationTokenJtiTtlSeconds = 45 * 60;
    internal const int IpMaxPerHour = 10;

    private readonly ICognitoAuthService _authService;
    private readonly IFaydaOidcService _faydaService;
    private readonly IUserRepository _userRepository;
    private readonly IRateLimitService _rateLimitService;
    private readonly IOtpService? _otpService;
    private readonly ISmsSender? _smsSender;
    private readonly IAuthStateRepository? _authState;

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

        // Resolve the signing secret from Secrets Manager (prod) or SSM (dev/staging)
        var faydaSigningSecret = AwsParameterResolver.GetSecretOrParameterValue(faydaSigningSecretArn);

        var cognito = new AmazonCognitoIdentityProviderClient();
        var dynamoDb = new AmazonDynamoDBClient();

        _authService = new CognitoAuthService(cognito, clientId, userPoolId);
        // RegisterFunction only needs token validation — use a placeholder for unused OIDC params
        _faydaService = new FaydaOidcService(
            new HttpClient(), "unused", "unused", "unused", faydaSigningSecret);
        _userRepository = new UserRepository(dynamoDb, tableName);
        _rateLimitService = new RateLimitService(new RateLimitRepository(dynamoDb, rateLimitTable));

        var authState = new AuthStateRepository(dynamoDb, rateLimitTable);
        _authState = authState;
        _otpService = new OtpService(authState, Encoding.UTF8.GetBytes(faydaSigningSecret));
        _smsSender = new SnsSmsSender(
            new AmazonSimpleNotificationServiceClient(), SnsSmsSender.IsEnabledByEnvironment());
    }

    /// <summary>
    /// Constructor for testing. The OTP, SMS and auth-state dependencies are optional
    /// so Fayda-only tests need not wire them; the OTP path reports them missing.
    /// </summary>
    public RegisterFunction(
        ICognitoAuthService authService,
        IFaydaOidcService faydaService,
        IUserRepository userRepository,
        IRateLimitService rateLimitService,
        IOtpService? otpService = null,
        ISmsSender? smsSender = null,
        IAuthStateRepository? authState = null)
    {
        _authService = authService ?? throw new ArgumentNullException(nameof(authService));
        _faydaService = faydaService ?? throw new ArgumentNullException(nameof(faydaService));
        _userRepository = userRepository ?? throw new ArgumentNullException(nameof(userRepository));
        _rateLimitService = rateLimitService ?? throw new ArgumentNullException(nameof(rateLimitService));
        _otpService = otpService;
        _smsSender = smsSender;
        _authState = authState;
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
        var phone = registerRequest!.PhoneNumber;

        // Per-IP cap: bulk account creation from one address (10 per hour).
        var ipCheck = await _rateLimitService.CheckIpAllowedAsync(sourceIp, "register", IpMaxPerHour, 3600);
        if (!ipCheck.IsSuccess)
        {
            AuditLogger.LogSecurity("REGISTER_IP_RATE_LIMITED", correlationId: correlationId,
                outcome: "blocked", sourceIp: sourceIp);
            return LambdaResponse.RateLimited(ipCheck.Error!.Code, ipCheck.Error.Message, ipCheck.Error.RetryAfterSeconds);
        }
        await _rateLimitService.RecordIpAsync(sourceIp, "register", 3600);

        // Validate Fayda verification token (optional — two-tier model)
        FaydaVerificationResult? faydaVerification = null;
        string? jtiKey = null;
        if (!string.IsNullOrWhiteSpace(registerRequest.FaydaVerificationToken))
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

            // A verification token registers exactly one account. Burn its id first;
            // every failure path below releases it so the user can retry.
            if (faydaVerification.Jti != null && _authState != null)
            {
                jtiKey = $"AUTHSTATE#JTI#{faydaVerification.Jti}";
                var firstUse = await _authState.PutIfAbsentAsync(
                    jtiKey,
                    new Dictionary<string, string> { { "phone", phone } },
                    VerificationTokenJtiTtlSeconds);
                if (!firstUse)
                {
                    AuditLogger.LogSecurity("REGISTER_FAYDA_TOKEN_REUSED",
                        detail: phone, correlationId: correlationId, outcome: "blocked", sourceIp: sourceIp);
                    return LambdaResponse.Error(400, "FAYDA_001",
                        "This verification token has already been used. Please verify with Fayda again.",
                        "faydaVerificationToken");
                }
            }
        }

        // Rate limit check: registration flood protection
        var rateLimitCheck = await _rateLimitService.CheckRegistrationAllowedAsync(phone);
        if (!rateLimitCheck.IsSuccess)
        {
            await ReleaseJtiAsync(jtiKey);
            var rlError = rateLimitCheck.Error!;
            return LambdaResponse.RateLimited(rlError.Code, rlError.Message);
        }

        var phoneProven = IsPhoneProven(faydaVerification, phone);

        try
        {
            return await CompleteRegistrationAsync(
                request, registerRequest, faydaVerification, phoneProven, jtiKey, correlationId, sourceIp, context);
        }
        catch (Exception)
        {
            // An unexpected failure must not leave the Fayda proof burned for 45 minutes.
            await ReleaseJtiAsync(jtiKey);
            throw;
        }
    }

    private async Task<APIGatewayProxyResponse> CompleteRegistrationAsync(
        APIGatewayProxyRequest request,
        RegisterRequest registerRequest,
        FaydaVerificationResult? faydaVerification,
        bool phoneProven,
        string? jtiKey,
        string correlationId,
        string? sourceIp,
        ILambdaContext context)
    {
        var phone = registerRequest.PhoneNumber;

        // Cognito register. CognitoAuthService replaces a still-unconfirmed user of the
        // same phone (its code was never entered, so the number is not proven to
        // belong to anyone yet) and returns the new sub; a confirmed user is AUTH_005.
        var cognitoResult = await CognitoRegisterAsync(registerRequest);
        if (!cognitoResult.IsSuccess)
        {
            await ReleaseJtiAsync(jtiKey);
            var error = cognitoResult.Error!;
            AuditLogger.LogSecurity("REGISTER_FAILED",
                detail: $"Cognito error: {error.Code}", correlationId: correlationId,
                outcome: "failure", sourceIp: sourceIp);
            return LambdaResponse.Error(error.HttpStatus, error.Code, error.Message, error.Field);
        }

        var userId = cognitoResult.Value!;

        // One phone, one profile. A row for this phone under another user id is the
        // leftover of a replaced (unconfirmed) or removed Cognito user. An unverified
        // leftover is dropped; a verified one is someone's account and is never
        // deleted here: the new sign-up is rolled back and refused.
        var leftover = await _userRepository.GetUserByPhoneAsync(phone);
        if (leftover != null && leftover.UserId != userId)
        {
            if (leftover.IsPhoneVerified)
            {
                await RollbackCognitoUserAsync(phone, userId, context);
                await ReleaseJtiAsync(jtiKey);
                AuditLogger.LogSecurity("REGISTER_FAILED",
                    detail: "verified profile already holds this phone", correlationId: correlationId,
                    outcome: "failure", sourceIp: sourceIp);
                return LambdaResponse.Error(409, "AUTH_005",
                    "This phone number is already registered. Please sign in or contact support.", "phoneNumber");
            }

            try
            {
                await _userRepository.DeleteUserAsync(leftover.UserId);
            }
            catch (Exception ex)
            {
                context.Logger.LogWarning(
                    $"Could not remove the unconfirmed profile {leftover.UserId}: [{ex.GetType().Name}]");
            }
        }

        CognitoAuthTokens? tokens = null;

        if (phoneProven)
        {
            // Fayda released the same phone number: the SIM is proven, skip the SMS code.
            var confirmResult = await _authService.AdminConfirmSignUpAsync(phone);
            if (!confirmResult.IsSuccess)
            {
                await ReleaseJtiAsync(jtiKey);
                var error = confirmResult.Error!;
                context.Logger.LogWarning(
                    $"Failed to admin-confirm user {userId}: {error.Code} {error.Message}");
                return LambdaResponse.Error(error.HttpStatus, error.Code, error.Message, error.Field);
            }

            // Get tokens immediately with the password
            var loginResult = await _authService.LoginAsync(phone, registerRequest.Password);
            if (!loginResult.IsSuccess)
            {
                await ReleaseJtiAsync(jtiKey);
                var error = loginResult.Error!;
                context.Logger.LogWarning(
                    $"Failed to get tokens for newly registered user {userId}: {error.Code} {error.Message}");
                return LambdaResponse.Error(error.HttpStatus, error.Code, error.Message, error.Field);
            }

            tokens = loginResult.Value!;
        }

        // Track registration for flood protection
        await _rateLimitService.RecordRegistrationAsync(phone);

        var now = DateTime.UtcNow.ToString("o");

        // DynamoDB create user: Fayda fields set only when verified; the phone flag
        // only when ownership is proven (Fayda phone match now, or verify-otp later).
        var userDto = new UserDto
        {
            UserId = userId,
            PhoneNumber = phone,
            DisplayName = registerRequest.DisplayName,
            FullName = faydaVerification?.FullName,
            Role = registerRequest.Role,
            Woreda = registerRequest.Woreda,
            FaydaId = faydaVerification?.FaydaId,
            FaydaVerified = faydaVerification != null,
            FaydaVerifiedAt = faydaVerification != null ? now : null,
            PreferredLanguage = registerRequest.PreferredLanguage,
            IsPhoneVerified = phoneProven,
            KnownDevices = DeviceIdentity.ReadId(request) is { } registeringDevice ? [registeringDevice] : [],
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
            // log in (LoginFunction returns AUTH_013 "profile not found"). Roll back
            // the Cognito user so the phone number isn't bricked, otherwise a retry
            // hits AUTH_005 "already registered" and login hits AUTH_013, with no
            // recovery. Then surface the failure so the client retries.
            context.Logger.LogError(
                $"Failed to create DynamoDB user record for {userId}: [{ex.GetType().Name}]");
            await RollbackCognitoUserAsync(phone, userId, context);
            await ReleaseJtiAsync(jtiKey);
            AuditLogger.LogSecurity("REGISTER_PROFILE_FAILED",
                userId: userId, correlationId: correlationId,
                outcome: "failure", sourceIp: sourceIp);
            return LambdaResponse.Error(500, "AUTH_007",
                "Registration could not be completed. Please try again.");
        }

        if (!phoneProven)
        {
            var otpExpiresAt = await IssueOtpAsync(userDto, context);

            AuditLogger.LogSecurity("REGISTER_PENDING_OTP",
                userId: userId, correlationId: correlationId,
                outcome: otpExpiresAt != null ? "success" : "otp_not_sent", sourceIp: sourceIp);

            return LambdaResponse.Accepted(new RegisterResponse
            {
                UserId = userId,
                PhoneNumber = phone,
                Tokens = null,
                User = userDto,
                OtpRequired = true,
                OtpExpiresAt = otpExpiresAt,
                Message = otpExpiresAt != null
                    ? "Enter the code we sent by SMS to confirm your phone number."
                    : "We could not send a code right now. Please request a new one."
            }, AppJsonContext.Default.RegisterResponse);
        }

        var response = new RegisterResponse
        {
            UserId = userId,
            PhoneNumber = phone,
            Tokens = new TokenResponse
            {
                AccessToken = tokens!.AccessToken,
                IdToken = tokens.IdToken,
                RefreshToken = tokens.RefreshToken,
                ExpiresIn = tokens.ExpiresIn
            },
            User = userDto,
            Message = "Registration successful. Fayda identity verified."
        };

        AuditLogger.LogSecurity("REGISTER",
            userId: userId, correlationId: correlationId,
            outcome: "success", sourceIp: sourceIp);

        return LambdaResponse.Created(response, AppJsonContext.Default.RegisterResponse);
    }

    private async Task RollbackCognitoUserAsync(string phone, string userId, ILambdaContext context)
    {
        try
        {
            await _authService.DeleteUserAsync(phone);
        }
        catch (Exception cleanupEx)
        {
            context.Logger.LogError(
                $"Cognito rollback failed for {userId}: [{cleanupEx.GetType().Name}]");
        }
    }

    private Task<AuthResult<string>> CognitoRegisterAsync(RegisterRequest registerRequest)
        => _authService.RegisterAsync(
            registerRequest.PhoneNumber,
            registerRequest.Password,
            registerRequest.Role,
            registerRequest.Woreda,
            registerRequest.DisplayName,
            registerRequest.PreferredLanguage);

    /// <summary>
    /// The SIM is proven only when Fayda itself released a phone_number claim and it
    /// is the number being registered. Any other case goes through the SMS code.
    /// </summary>
    internal static bool IsPhoneProven(FaydaVerificationResult? verification, string registrationPhone)
    {
        if (string.IsNullOrWhiteSpace(verification?.PhoneNumber))
        {
            return false;
        }

        var faydaPhone = RequestValidator.NormalizePhoneNumber(verification.PhoneNumber)
                         ?? verification.PhoneNumber.Trim();
        return string.Equals(faydaPhone, registrationPhone, StringComparison.Ordinal);
    }

    /// <summary>Issue and send the SMS code. Returns its expiry, or null when nothing was sent.</summary>
    private async Task<string?> IssueOtpAsync(UserDto user, ILambdaContext context)
    {
        if (_otpService == null)
        {
            context.Logger.LogError("OTP service not configured; registration left pending verification.");
            return null;
        }

        var issue = await _otpService.IssueAsync(user.PhoneNumber, user.UserId);
        if (!issue.IsSuccess)
        {
            context.Logger.LogWarning($"OTP not issued for {user.UserId}: {issue.Error!.Code}");
            return null;
        }

        await _rateLimitService.RecordOtpSendAsync(user.PhoneNumber);

        var sent = _smsSender != null
            && await _smsSender.SendAsync(user.PhoneNumber, OtpSms.Compose(issue.Value!.Code, user.PreferredLanguage));
        if (!sent)
        {
            context.Logger.LogWarning($"OTP SMS not sent for user {user.UserId} (sender disabled or failed).");
        }

        return issue.Value!.ExpiresAt;
    }

    private async Task ReleaseJtiAsync(string? jtiKey)
    {
        if (jtiKey == null || _authState == null)
        {
            return;
        }

        try
        {
            await _authState.DeleteAsync(jtiKey);
        }
        catch (Exception)
        {
            // Best effort: the row expires on its own.
        }
    }
}
