using Amazon.CognitoIdentityProvider;
using Amazon.DynamoDBv2;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.RateLimiting;
using WeredaConnect.Server.Services;
using WeredaConnect.Server.Validation;

namespace WeredaConnect.Server.Functions;

/// <summary>
/// Password reset Lambda handler.
/// POST /auth/forgot-password — initiates password reset, sends verification code via SMS.
/// POST /auth/reset-password — confirms password reset with verification code.
/// Rate limiting: 3 requests per phone per 10 minutes.
/// </summary>
public class PasswordResetFunction
{
    private readonly ICognitoAuthService _authService;
    private readonly IRateLimitService _rateLimitService;

    // Cognito error codes that burn the OTP attempt counter.
    // UserNotFound is included to prevent phone number enumeration via the reset endpoint.
    private const string CodeMismatchCode = "AUTH_018";
    private const string CodeExpiredCode = "AUTH_019";
    private const string UserNotFoundCode = "AUTH_013";

    /// <summary>
    /// Default constructor for Lambda runtime.
    /// </summary>
    public PasswordResetFunction()
    {
        var clientId = Environment.GetEnvironmentVariable("COGNITO_CLIENT_ID")
                       ?? throw new InvalidOperationException("COGNITO_CLIENT_ID is not set.");
        var rateLimitTable = Environment.GetEnvironmentVariable("RATE_LIMITS_TABLE")
                             ?? throw new InvalidOperationException("RATE_LIMITS_TABLE is not set.");

        var cognito = new AmazonCognitoIdentityProviderClient();
        var dynamoDb = new AmazonDynamoDBClient();

        _authService = new CognitoAuthService(cognito, clientId);
        _rateLimitService = new RateLimitService(new RateLimitRepository(dynamoDb, rateLimitTable));
    }

    /// <summary>
    /// Constructor for testing.
    /// </summary>
    public PasswordResetFunction(ICognitoAuthService authService, IRateLimitService rateLimitService)
    {
        _authService = authService ?? throw new ArgumentNullException(nameof(authService));
        _rateLimitService = rateLimitService ?? throw new ArgumentNullException(nameof(rateLimitService));
    }

    public async Task<APIGatewayProxyResponse> FunctionHandler(
        APIGatewayProxyRequest request,
        ILambdaContext context)
    {
        var path = request.Path?.ToLowerInvariant() ?? "";

        if (path.EndsWith("/forgot-password"))
        {
            return await HandleForgotPassword(request, context);
        }
        else if (path.EndsWith("/reset-password"))
        {
            return await HandleResetPassword(request, context);
        }

        return LambdaResponse.Error(404, "AUTH_020", "Endpoint not found.");
    }

    private async Task<APIGatewayProxyResponse> HandleForgotPassword(
        APIGatewayProxyRequest request,
        ILambdaContext context)
    {
        context.Logger.LogInformation("ForgotPassword invoked");

        // Parse
        ForgotPasswordRequest? fpRequest;
        try
        {
            fpRequest = JsonSerializer.Deserialize(
                request.Body ?? "",
                AppJsonContext.Default.ForgotPasswordRequest);
        }
        catch (JsonException)
        {
            return LambdaResponse.Error(400, "AUTH_007", "Invalid JSON in request body.");
        }

        if (fpRequest == null || string.IsNullOrWhiteSpace(fpRequest.PhoneNumber))
        {
            return LambdaResponse.Error(400, "AUTH_007", "Phone number is required.", "phoneNumber");
        }

        // Normalize phone number (accept +251, 00251, or 0 prefix)
        var normalizedPhone = RequestValidator.NormalizePhoneNumber(fpRequest.PhoneNumber);
        if (normalizedPhone != null)
        {
            fpRequest = fpRequest with { PhoneNumber = normalizedPhone };
        }

        // Validate phone format
        var phoneValidation = RequestValidator.ValidatePhoneNumber(fpRequest.PhoneNumber);
        if (!phoneValidation.IsValid)
        {
            return LambdaResponse.ValidationError(phoneValidation);
        }

        var correlationId = AuditLogger.GetCorrelationId(request);
        var sourceIp = AuditLogger.GetSourceIp(request);

        var rateLimitResponse = await EnforceOtpRateLimitAsync(
            fpRequest.PhoneNumber, "FORGOT_PASSWORD_RATE_LIMITED", correlationId, sourceIp);
        if (rateLimitResponse != null) return rateLimitResponse;

        var result = await _authService.ForgotPasswordAsync(fpRequest.PhoneNumber);

        if (!result.IsSuccess)
        {
            var error = result.Error!;
            await _rateLimitService.RecordFailedOtpAttemptAsync(fpRequest.PhoneNumber);
            AuditLogger.LogSecurity("FORGOT_PASSWORD",
                correlationId: correlationId, detail: "code send failed",
                outcome: "failure", sourceIp: sourceIp);
            return LambdaResponse.Error(error.HttpStatus, error.Code, error.Message, error.Field);
        }

        AuditLogger.LogSecurity("FORGOT_PASSWORD",
            correlationId: correlationId, detail: "code sent",
            outcome: "success", sourceIp: sourceIp);

        var response = new ForgotPasswordResponse
        {
            Message = "Verification code sent to your phone.",
            DeliveryMedium = result.Value ?? "SMS"
        };

        return LambdaResponse.Ok(response, AppJsonContext.Default.ForgotPasswordResponse);
    }

    private async Task<APIGatewayProxyResponse> HandleResetPassword(
        APIGatewayProxyRequest request,
        ILambdaContext context)
    {
        context.Logger.LogInformation("ResetPassword invoked");

        // Parse
        ResetPasswordRequest? rpRequest;
        try
        {
            rpRequest = JsonSerializer.Deserialize(
                request.Body ?? "",
                AppJsonContext.Default.ResetPasswordRequest);
        }
        catch (JsonException)
        {
            return LambdaResponse.Error(400, "AUTH_007", "Invalid JSON in request body.");
        }

        if (rpRequest == null)
        {
            return LambdaResponse.Error(400, "AUTH_007", "Request body is required.");
        }

        // Normalize phone number (accept +251, 00251, or 0 prefix)
        var normalizedPhone = RequestValidator.NormalizePhoneNumber(rpRequest.PhoneNumber);
        if (normalizedPhone != null)
        {
            rpRequest = rpRequest with { PhoneNumber = normalizedPhone };
        }

        // Validate phone format
        var phoneValidation = RequestValidator.ValidatePhoneNumber(rpRequest.PhoneNumber);
        if (!phoneValidation.IsValid)
        {
            return LambdaResponse.ValidationError(phoneValidation);
        }

        if (string.IsNullOrWhiteSpace(rpRequest.ConfirmationCode))
        {
            return LambdaResponse.Error(400, "AUTH_007", "Confirmation code is required.", "confirmationCode");
        }

        if (string.IsNullOrWhiteSpace(rpRequest.NewPassword))
        {
            return LambdaResponse.Error(400, "AUTH_007", "New password is required.", "newPassword");
        }

        var correlationId = AuditLogger.GetCorrelationId(request);
        var sourceIp = AuditLogger.GetSourceIp(request);

        // Rate limit: check before calling Cognito so recorded failures have teeth.
        var rateLimitResponse = await EnforceOtpRateLimitAsync(
            rpRequest.PhoneNumber, "RESET_PASSWORD_RATE_LIMITED", correlationId, sourceIp);
        if (rateLimitResponse != null) return rateLimitResponse;

        var result = await _authService.ConfirmForgotPasswordAsync(
            rpRequest.PhoneNumber,
            rpRequest.ConfirmationCode,
            rpRequest.NewPassword);

        if (!result.IsSuccess)
        {
            var error = result.Error!;
            // Penalise wrong/expired codes and unknown users to prevent phone number
            // enumeration via the reset endpoint. Password-policy failures and
            // infrastructure errors do not burn the OTP budget.
            if (error.Code is CodeMismatchCode or CodeExpiredCode or UserNotFoundCode)
            {
                await _rateLimitService.RecordFailedOtpAttemptAsync(rpRequest.PhoneNumber);
            }
            AuditLogger.LogSecurity("PASSWORD_RESET_FAILED",
                correlationId: correlationId, detail: error.Code,
                outcome: "failure", sourceIp: sourceIp);
            return LambdaResponse.Error(error.HttpStatus, error.Code, error.Message, error.Field);
        }

        AuditLogger.LogSecurity("PASSWORD_RESET",
            correlationId: correlationId,
            outcome: "success", sourceIp: sourceIp);

        var response = new ResetPasswordResponse
        {
            PasswordReset = true,
            Message = "Password reset successful. You can now log in with your new password."
        };

        return LambdaResponse.Ok(response, AppJsonContext.Default.ResetPasswordResponse);
    }

    /// <summary>
    /// Checks the OTP rate limit for [phone]. Returns an error response if the limit is
    /// exceeded (caller should return it immediately), or null if the request is allowed.
    /// </summary>
    private async Task<APIGatewayProxyResponse?> EnforceOtpRateLimitAsync(
        string phone, string auditAction, string correlationId, string? sourceIp)
    {
        var check = await _rateLimitService.CheckOtpAttemptAllowedAsync(phone);
        if (check.IsSuccess) return null;

        var error = check.Error!;
        AuditLogger.LogSecurity(auditAction,
            correlationId: correlationId, outcome: "rate_limited", sourceIp: sourceIp);
        return LambdaResponse.Error(error.HttpStatus, error.Code, error.Message,
            retryAfterSeconds: error.RetryAfterSeconds);
    }
}
