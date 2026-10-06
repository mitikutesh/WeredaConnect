using Amazon.CognitoIdentityProvider;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.Services;
using WeredaConnect.Server.Validation;

namespace WeredaConnect.Server.Functions;

/// <summary>
/// POST /auth/logout Lambda handler (authenticated endpoint).
/// Flow: parse -> validate -> caller identity -> resolve the refresh token's owner
/// -> owner must be the caller -> Cognito revoke token -> 200 response.
///
/// SIM-60: Cognito's RevokeToken revokes any refresh token of the app client, so the
/// token in the body is first bound to the bearer. Its owner is read from the id token
/// that REFRESH_TOKEN_AUTH mints for it; those tokens are discarded, never returned or
/// logged. A token that is unknown, expired or already revoked is reported as logged
/// out (idempotent, nothing to revoke); a token of another user is refused (AUTH_026).
/// </summary>
public class LogoutFunction
{
    private readonly ICognitoAuthService _authService;

    /// <summary>
    /// Default constructor for Lambda runtime.
    /// </summary>
    public LogoutFunction()
    {
        var clientId = Environment.GetEnvironmentVariable("COGNITO_CLIENT_ID")
                       ?? throw new InvalidOperationException("COGNITO_CLIENT_ID is not set.");

        var cognito = new AmazonCognitoIdentityProviderClient();
        _authService = new CognitoAuthService(cognito, clientId);
    }

    /// <summary>
    /// Constructor for testing.
    /// </summary>
    public LogoutFunction(ICognitoAuthService authService)
    {
        _authService = authService ?? throw new ArgumentNullException(nameof(authService));
    }

    public async Task<APIGatewayProxyResponse> FunctionHandler(
        APIGatewayProxyRequest request,
        ILambdaContext context)
    {
        context.Logger.LogInformation("Logout invoked");

        // Parse
        LogoutRequest? logoutRequest;
        try
        {
            logoutRequest = JsonSerializer.Deserialize(
                request.Body ?? "",
                AppJsonContext.Default.LogoutRequest);
        }
        catch (JsonException)
        {
            return LambdaResponse.Error(400, "AUTH_007", "Invalid JSON in request body.");
        }

        // Validate
        var validation = RequestValidator.ValidateLogoutRequest(logoutRequest);
        if (!validation.IsValid)
        {
            return LambdaResponse.ValidationError(validation);
        }

        var correlationId = AuditLogger.GetCorrelationId(request);
        var sourceIp = AuditLogger.GetSourceIp(request);

        // The route sits behind the JWT authorizer, so a missing caller identity means
        // the event did not carry the claims (never revoke on an anonymous request).
        var userId = AuthHelper.GetUserId(request);
        if (string.IsNullOrEmpty(userId))
        {
            return LambdaResponse.Error(401, "AUTH_020", "Authentication required.");
        }

        var refreshToken = logoutRequest!.RefreshToken;

        // Resolve the token's owner before revoking anything.
        var ownerLookup = await _authService.RefreshAsync(refreshToken);
        if (!ownerLookup.IsSuccess)
        {
            var lookupError = ownerLookup.Error!;
            if (lookupError.HttpStatus == 401)
            {
                // Unknown, expired or already revoked: nothing live to revoke. Reported as
                // logged out, as RevokeToken itself answers for an already revoked token.
                AuditLogger.LogSecurity("LOGOUT",
                    userId: userId, correlationId: correlationId,
                    detail: "token_inactive", outcome: "success", sourceIp: sourceIp);
                return LambdaResponse.Ok(LoggedOut(), AppJsonContext.Default.LogoutResponse);
            }

            // Throttled or Cognito unavailable: report it, revoke nothing.
            return LambdaResponse.Error(
                lookupError.HttpStatus, lookupError.Code, lookupError.Message, lookupError.Field);
        }

        var ownerSub = AuthHelper.GetSubFromJwt(ownerLookup.Value!.IdToken);
        if (string.IsNullOrEmpty(ownerSub) || !string.Equals(ownerSub, userId, StringComparison.Ordinal))
        {
            AuditLogger.LogSecurity("LOGOUT_DENIED",
                userId: userId, correlationId: correlationId,
                detail: "refresh_token_owner_mismatch", outcome: "failure", sourceIp: sourceIp);
            return LambdaResponse.Error(403, "AUTH_026",
                "The refresh token does not belong to the signed-in user.", "refreshToken");
        }

        // Cognito logout (revoke refresh token)
        var logoutResult = await _authService.LogoutAsync(refreshToken);

        if (!logoutResult.IsSuccess)
        {
            var error = logoutResult.Error!;
            return LambdaResponse.Error(error.HttpStatus, error.Code, error.Message, error.Field);
        }

        AuditLogger.LogSecurity("LOGOUT",
            userId: userId, correlationId: correlationId,
            outcome: "success", sourceIp: sourceIp);

        return LambdaResponse.Ok(LoggedOut(), AppJsonContext.Default.LogoutResponse);
    }

    private static LogoutResponse LoggedOut() => new()
    {
        LoggedOut = true,
        Message = "Successfully logged out."
    };
}
