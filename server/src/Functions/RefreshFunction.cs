using Amazon.CognitoIdentityProvider;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.Services;
using WeredaConnect.Server.Validation;

namespace WeredaConnect.Server.Functions;

/// <summary>
/// POST /auth/refresh Lambda handler.
/// Flow: parse → validate → Cognito refresh → 200 response with new tokens.
/// No DynamoDB interaction needed — pure token refresh.
/// </summary>
public class RefreshFunction
{
    private readonly ICognitoAuthService _authService;

    /// <summary>
    /// Default constructor for Lambda runtime.
    /// </summary>
    public RefreshFunction()
    {
        var clientId = Environment.GetEnvironmentVariable("COGNITO_CLIENT_ID")
                       ?? throw new InvalidOperationException("COGNITO_CLIENT_ID is not set.");

        var cognito = new AmazonCognitoIdentityProviderClient();
        _authService = new CognitoAuthService(cognito, clientId);
    }

    /// <summary>
    /// Constructor for testing.
    /// </summary>
    public RefreshFunction(ICognitoAuthService authService)
    {
        _authService = authService ?? throw new ArgumentNullException(nameof(authService));
    }

    public async Task<APIGatewayProxyResponse> FunctionHandler(
        APIGatewayProxyRequest request,
        ILambdaContext context)
    {
        context.Logger.LogInformation("Refresh invoked");

        // Parse
        RefreshRequest? refreshRequest;
        try
        {
            refreshRequest = JsonSerializer.Deserialize(
                request.Body ?? "",
                AppJsonContext.Default.RefreshRequest);
        }
        catch (JsonException)
        {
            return LambdaResponse.Error(400, "AUTH_007", "Invalid JSON in request body.");
        }

        // Validate
        var validation = RequestValidator.ValidateRefreshRequest(refreshRequest);
        if (!validation.IsValid)
        {
            return LambdaResponse.ValidationError(validation);
        }

        // Cognito refresh
        var refreshResult = await _authService.RefreshAsync(refreshRequest!.RefreshToken);

        if (!refreshResult.IsSuccess)
        {
            var error = refreshResult.Error!;
            return LambdaResponse.Error(error.HttpStatus, error.Code, error.Message, error.Field);
        }

        var tokens = refreshResult.Value!;

        var response = new RefreshResponse
        {
            Tokens = new TokenResponse
            {
                AccessToken = tokens.AccessToken,
                IdToken = tokens.IdToken,
                RefreshToken = tokens.RefreshToken, // null — Cognito doesn't rotate
                ExpiresIn = tokens.ExpiresIn
            }
        };

        return LambdaResponse.Ok(response, AppJsonContext.Default.RefreshResponse);
    }
}
