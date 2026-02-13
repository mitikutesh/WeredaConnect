using Amazon.CognitoIdentityProvider;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.Services;
using WeredaConnect.Server.Validation;

namespace WeredaConnect.Server.Functions;

/// <summary>
/// POST /auth/logout Lambda handler (authenticated endpoint).
/// Flow: parse → validate → Cognito revoke token → 200 response.
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

        // Cognito logout (revoke refresh token)
        var logoutResult = await _authService.LogoutAsync(logoutRequest!.RefreshToken);

        if (!logoutResult.IsSuccess)
        {
            var error = logoutResult.Error!;
            return LambdaResponse.Error(error.HttpStatus, error.Code, error.Message, error.Field);
        }

        var response = new LogoutResponse
        {
            LoggedOut = true,
            Message = "Successfully logged out."
        };

        return LambdaResponse.Ok(response, AppJsonContext.Default.LogoutResponse);
    }
}
