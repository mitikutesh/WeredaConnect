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
/// POST /auth/verify-otp and POST /auth/resend-otp.
/// Registration without a phone-bound Fayda proof leaves the Cognito user
/// unconfirmed and the profile with isPhoneVerified=false. verify-otp proves
/// the caller controls the SIM, confirms the user, flips the flag and mints
/// tokens with the password. resend-otp re-issues the code (never reveals
/// whether the phone exists).
/// </summary>
public class VerifyOtpFunction
{
    internal const int IpMaxPer5Min = 30;
    private const int IpWindowSeconds = 300;

    private readonly ICognitoAuthService _authService;
    private readonly IUserRepository _userRepository;
    private readonly IRateLimitService _rateLimitService;
    private readonly IOtpService _otpService;
    private readonly ISmsSender _smsSender;

    public VerifyOtpFunction()
    {
        var clientId = Environment.GetEnvironmentVariable("COGNITO_CLIENT_ID")
                       ?? throw new InvalidOperationException("COGNITO_CLIENT_ID is not set.");
        var userPoolId = Environment.GetEnvironmentVariable("COGNITO_USER_POOL_ID")
                         ?? throw new InvalidOperationException("COGNITO_USER_POOL_ID is not set.");
        var tableName = Environment.GetEnvironmentVariable("USERS_TABLE")
                        ?? throw new InvalidOperationException("USERS_TABLE is not set.");
        var rateLimitTable = Environment.GetEnvironmentVariable("RATE_LIMITS_TABLE")
                             ?? throw new InvalidOperationException("RATE_LIMITS_TABLE is not set.");
        var signingSecretArn = Environment.GetEnvironmentVariable("FAYDA_SIGNING_SECRET_ARN")
                               ?? throw new InvalidOperationException("FAYDA_SIGNING_SECRET_ARN is not set.");

        var secret = AwsParameterResolver.GetSecretOrParameterValue(signingSecretArn);
        var cognito = new AmazonCognitoIdentityProviderClient();
        var dynamoDb = new AmazonDynamoDBClient();

        _authService = new CognitoAuthService(cognito, clientId, userPoolId);
        _userRepository = new UserRepository(dynamoDb, tableName);
        _rateLimitService = new RateLimitService(new RateLimitRepository(dynamoDb, rateLimitTable));
        _otpService = new OtpService(
            new AuthStateRepository(dynamoDb, rateLimitTable),
            System.Text.Encoding.UTF8.GetBytes(secret));
        _smsSender = new SnsSmsSender(
            new AmazonSimpleNotificationServiceClient(), SnsSmsSender.IsEnabledByEnvironment());
    }

    public VerifyOtpFunction(
        ICognitoAuthService authService,
        IUserRepository userRepository,
        IRateLimitService rateLimitService,
        IOtpService otpService,
        ISmsSender smsSender)
    {
        _authService = authService ?? throw new ArgumentNullException(nameof(authService));
        _userRepository = userRepository ?? throw new ArgumentNullException(nameof(userRepository));
        _rateLimitService = rateLimitService ?? throw new ArgumentNullException(nameof(rateLimitService));
        _otpService = otpService ?? throw new ArgumentNullException(nameof(otpService));
        _smsSender = smsSender ?? throw new ArgumentNullException(nameof(smsSender));
    }

    public async Task<APIGatewayProxyResponse> FunctionHandler(
        APIGatewayProxyRequest request,
        ILambdaContext context)
    {
        var path = request.Path?.ToLowerInvariant() ?? "";
        return path.EndsWith("/resend-otp", StringComparison.Ordinal)
            ? await HandleResendAsync(request, context)
            : await HandleVerifyAsync(request, context);
    }

    private async Task<APIGatewayProxyResponse> HandleVerifyAsync(
        APIGatewayProxyRequest request, ILambdaContext context)
    {
        context.Logger.LogInformation("VerifyOtp invoked");

        VerifyOtpRequest? body;
        try
        {
            body = JsonSerializer.Deserialize(request.Body ?? "", AppJsonContext.Default.VerifyOtpRequest);
        }
        catch (JsonException)
        {
            return LambdaResponse.Error(400, "AUTH_007", "Invalid JSON in request body.");
        }

        if (body != null)
        {
            var normalized = RequestValidator.NormalizePhoneNumber(body.PhoneNumber);
            if (normalized != null)
            {
                body = body with { PhoneNumber = normalized };
            }
        }

        var validation = RequestValidator.ValidateVerifyOtpRequest(body);
        if (!validation.IsValid)
        {
            return LambdaResponse.ValidationError(validation);
        }

        var phone = body!.PhoneNumber;
        var correlationId = AuditLogger.GetCorrelationId(request);
        var sourceIp = AuditLogger.GetSourceIp(request);

        var ipCheck = await _rateLimitService.CheckIpAllowedAsync(sourceIp, "otp", IpMaxPer5Min, IpWindowSeconds);
        if (!ipCheck.IsSuccess)
        {
            return LambdaResponse.RateLimited(ipCheck.Error!.Code, ipCheck.Error.Message, ipCheck.Error.RetryAfterSeconds);
        }
        await _rateLimitService.RecordIpAsync(sourceIp, "otp", IpWindowSeconds);

        var attemptCheck = await _rateLimitService.CheckOtpAttemptAllowedAsync(phone);
        if (!attemptCheck.IsSuccess)
        {
            var rlError = attemptCheck.Error!;
            AuditLogger.LogSecurity("OTP_RATE_LIMITED", detail: phone, correlationId: correlationId,
                outcome: "blocked", sourceIp: sourceIp);
            return LambdaResponse.Error(rlError.HttpStatus, rlError.Code, rlError.Message, rlError.Field);
        }

        // 1. The password comes first, before the code is consumed. Cognito checks the
        //    password even for an unconfirmed user: a correct one answers "not
        //    confirmed" (AUTH_016), a wrong one "not authorized" (AUTH_014). Without
        //    this order, whoever re-registered a pending phone could get it confirmed
        //    by the code the real owner types.
        var passwordCheck = await _authService.LoginAsync(phone, body.Password);
        var alreadyConfirmed = passwordCheck.IsSuccess;
        if (!alreadyConfirmed && passwordCheck.Error!.Code != "AUTH_016")
        {
            var error = passwordCheck.Error;
            AuditLogger.LogSecurity("OTP_PASSWORD_REJECTED", detail: error.Code, correlationId: correlationId,
                outcome: "failure", sourceIp: sourceIp);
            return LambdaResponse.Error(error.HttpStatus, error.Code, error.Message, error.Field);
        }

        // 2. The code, bound to the registration it was issued for.
        var verify = await _otpService.VerifyAsync(phone, body.OtpCode);
        if (!verify.IsSuccess)
        {
            await _rateLimitService.RecordFailedOtpAttemptAsync(phone);
            AuditLogger.LogSecurity("OTP_FAILED", detail: phone, correlationId: correlationId,
                outcome: "failure", sourceIp: sourceIp);
            var error = verify.Error!;
            return LambdaResponse.Error(error.HttpStatus, error.Code, error.Message, error.Field);
        }

        await _rateLimitService.ResetOtpAttemptsAsync(phone);
        var boundUserId = verify.Value!;

        var user = await _userRepository.GetUserByPhoneAsync(phone);
        if (user == null)
        {
            return LambdaResponse.Error(404, "AUTH_013", "User profile not found. Please register again.");
        }

        if (user.Suspended)
        {
            return LambdaResponse.Error(403, "AUTH_025",
                "This account has been suspended. Please contact support.");
        }

        if (boundUserId != IOtpService.AnyUser && !string.Equals(boundUserId, user.UserId, StringComparison.Ordinal))
        {
            AuditLogger.LogSecurity("OTP_REGISTRATION_MISMATCH", userId: user.UserId, correlationId: correlationId,
                outcome: "blocked", sourceIp: sourceIp);
            return LambdaResponse.Error(400, "AUTH_012",
                "This code belongs to a different registration. Please request a new one.", "otpCode");
        }

        // 3. Confirm the Cognito user and flip the profile flag (idempotent on retry).
        if (!user.IsPhoneVerified)
        {
            var confirm = await _authService.AdminConfirmSignUpAsync(phone);
            if (!confirm.IsSuccess)
            {
                var error = confirm.Error!;
                return LambdaResponse.Error(error.HttpStatus, error.Code, error.Message, error.Field);
            }

            var updated = await _userRepository.UpdateUserAsync(
                user with { IsPhoneVerified = true }, user.Version);
            if (!updated.IsSuccess)
            {
                // Lost a version race: re-read and try once more before giving up.
                var fresh = await _userRepository.GetUserByPhoneAsync(phone);
                if (fresh == null)
                {
                    return LambdaResponse.Error(404, "AUTH_013", "User profile not found. Please register again.");
                }
                updated = fresh.IsPhoneVerified
                    ? AuthResult<UserDto>.Success(fresh)
                    : await _userRepository.UpdateUserAsync(fresh with { IsPhoneVerified = true }, fresh.Version);
                if (!updated.IsSuccess)
                {
                    var error = updated.Error!;
                    return LambdaResponse.Error(error.HttpStatus, error.Code, error.Message, error.Field);
                }
            }
            user = updated.Value!;
        }

        // 4. Tokens: the pre-check already produced them for a confirmed user.
        var login = alreadyConfirmed ? passwordCheck : await _authService.LoginAsync(phone, body.Password);
        if (!login.IsSuccess)
        {
            var error = login.Error!;
            AuditLogger.LogSecurity("OTP_VERIFIED_LOGIN_FAILED", userId: user.UserId,
                detail: error.Code, correlationId: correlationId, outcome: "failure", sourceIp: sourceIp);
            return LambdaResponse.Error(error.HttpStatus, error.Code, error.Message, error.Field);
        }

        var tokens = login.Value!;
        AuditLogger.LogSecurity("OTP_VERIFIED", userId: user.UserId, correlationId: correlationId,
            outcome: "success", sourceIp: sourceIp);

        return LambdaResponse.Ok(new LoginResponse
        {
            Tokens = new TokenResponse
            {
                AccessToken = tokens.AccessToken,
                IdToken = tokens.IdToken,
                RefreshToken = tokens.RefreshToken,
                ExpiresIn = tokens.ExpiresIn
            },
            User = user
        }, AppJsonContext.Default.LoginResponse);
    }

    private async Task<APIGatewayProxyResponse> HandleResendAsync(
        APIGatewayProxyRequest request, ILambdaContext context)
    {
        context.Logger.LogInformation("ResendOtp invoked");

        ResendOtpRequest? body;
        try
        {
            body = JsonSerializer.Deserialize(request.Body ?? "", AppJsonContext.Default.ResendOtpRequest);
        }
        catch (JsonException)
        {
            return LambdaResponse.Error(400, "AUTH_007", "Invalid JSON in request body.");
        }

        if (body != null)
        {
            var normalized = RequestValidator.NormalizePhoneNumber(body.PhoneNumber);
            if (normalized != null)
            {
                body = body with { PhoneNumber = normalized };
            }
        }

        var validation = RequestValidator.ValidateResendOtpRequest(body);
        if (!validation.IsValid)
        {
            return LambdaResponse.ValidationError(validation);
        }

        var phone = body!.PhoneNumber;
        var correlationId = AuditLogger.GetCorrelationId(request);
        var sourceIp = AuditLogger.GetSourceIp(request);

        var ipCheck = await _rateLimitService.CheckIpAllowedAsync(sourceIp, "otp", IpMaxPer5Min, IpWindowSeconds);
        if (!ipCheck.IsSuccess)
        {
            return LambdaResponse.RateLimited(ipCheck.Error!.Code, ipCheck.Error.Message, ipCheck.Error.RetryAfterSeconds);
        }
        await _rateLimitService.RecordIpAsync(sourceIp, "otp", IpWindowSeconds);

        var sendCheck = await _rateLimitService.CheckOtpSendAllowedAsync(phone);
        if (!sendCheck.IsSuccess)
        {
            var rlError = sendCheck.Error!;
            return LambdaResponse.RateLimited(rlError.Code, rlError.Message, rlError.RetryAfterSeconds);
        }

        // Uniform response whether or not the phone is pending: no enumeration.
        var genericResponse = new ResendOtpResponse
        {
            Message = "If this number is awaiting verification, a new code has been sent.",
            OtpExpiresAt = null
        };

        var user = await _userRepository.GetUserByPhoneAsync(phone);
        if (user == null || user.IsPhoneVerified)
        {
            return LambdaResponse.Ok(genericResponse, AppJsonContext.Default.ResendOtpResponse);
        }

        var issue = await _otpService.IssueAsync(phone, user.UserId);
        if (!issue.IsSuccess)
        {
            var error = issue.Error!;
            return LambdaResponse.RateLimited(error.Code, error.Message, error.RetryAfterSeconds);
        }

        await _rateLimitService.RecordOtpSendAsync(phone);
        var sent = await _smsSender.SendAsync(phone, OtpSms.Compose(issue.Value!.Code, user.PreferredLanguage));
        if (!sent)
        {
            context.Logger.LogWarning($"OTP SMS not sent for user {user.UserId} (sender disabled or failed).");
        }

        AuditLogger.LogSecurity("OTP_RESENT", userId: user.UserId, correlationId: correlationId,
            outcome: sent ? "success" : "not_sent", sourceIp: sourceIp);

        return LambdaResponse.Ok(genericResponse with { OtpExpiresAt = issue.Value.ExpiresAt },
            AppJsonContext.Default.ResendOtpResponse);
    }
}

/// <summary>The OTP text in the user's preferred language (am, om, en).</summary>
public static class OtpSms
{
    public static string Compose(string code, string? language) => language switch
    {
        "am" => $"የወረዳኮኔክት ኮድ፡ {code}። ለ5 ደቂቃ ብቻ ይሰራል። ለማንም አያጋሩ።",
        "om" => $"Koodii WeredaConnect: {code}. Daqiiqaa 5 qofaaf hojjeta. Namaaf hin qoodin.",
        _ => $"WeredaConnect code: {code}. Valid for 5 minutes. Do not share it."
    };
}
