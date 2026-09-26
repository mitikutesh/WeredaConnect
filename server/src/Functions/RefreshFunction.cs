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
/// POST /auth/refresh Lambda handler.
/// Flow: parse → validate → Cognito refresh → suspension check → 200 response with new tokens.
/// A suspended profile cannot refresh: the refresh token is revoked and AUTH_025 returned,
/// so an admin suspension ends the session instead of surviving for the 7-day token life.
/// </summary>
public class RefreshFunction
{
    internal const int IpMaxPer5Min = 60;
    private const int IpWindowSeconds = 300;

    private readonly ICognitoAuthService _authService;
    private readonly IUserRepository? _userRepository;
    private readonly IRateLimitService? _rateLimitService;

    /// <summary>
    /// Default constructor for Lambda runtime.
    /// </summary>
    public RefreshFunction()
    {
        var clientId = Environment.GetEnvironmentVariable("COGNITO_CLIENT_ID")
                       ?? throw new InvalidOperationException("COGNITO_CLIENT_ID is not set.");
        // Both tables are mandatory: without them the suspension check and the
        // per-IP cap would silently disappear.
        var usersTable = Environment.GetEnvironmentVariable("USERS_TABLE")
                         ?? throw new InvalidOperationException("USERS_TABLE is not set.");
        var rateLimitTable = Environment.GetEnvironmentVariable("RATE_LIMITS_TABLE")
                             ?? throw new InvalidOperationException("RATE_LIMITS_TABLE is not set.");

        var cognito = new AmazonCognitoIdentityProviderClient();
        var dynamoDb = new AmazonDynamoDBClient();
        _authService = new CognitoAuthService(cognito, clientId);
        _userRepository = new UserRepository(dynamoDb, usersTable);
        _rateLimitService = new RateLimitService(new RateLimitRepository(dynamoDb, rateLimitTable));
    }

    /// <summary>
    /// Constructor for testing.
    /// </summary>
    public RefreshFunction(ICognitoAuthService authService, IUserRepository? userRepository = null,
        IRateLimitService? rateLimitService = null)
    {
        _authService = authService ?? throw new ArgumentNullException(nameof(authService));
        _userRepository = userRepository;
        _rateLimitService = rateLimitService;
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

        if (_rateLimitService != null)
        {
            var sourceIp = AuditLogger.GetSourceIp(request);
            var ipCheck = await _rateLimitService.CheckIpAllowedAsync(sourceIp, "refresh", IpMaxPer5Min, IpWindowSeconds);
            if (!ipCheck.IsSuccess)
            {
                return LambdaResponse.RateLimited(ipCheck.Error!.Code, ipCheck.Error.Message, ipCheck.Error.RetryAfterSeconds);
            }
            await _rateLimitService.RecordIpAsync(sourceIp, "refresh", IpWindowSeconds);
        }

        // Cognito refresh
        var refreshResult = await _authService.RefreshAsync(refreshRequest!.RefreshToken);

        if (!refreshResult.IsSuccess)
        {
            var error = refreshResult.Error!;
            return LambdaResponse.Error(error.HttpStatus, error.Code, error.Message, error.Field);
        }

        var tokens = refreshResult.Value!;

        if (_userRepository != null)
        {
            var sub = AuthHelper.GetSubFromJwt(tokens.IdToken);
            var user = sub != null ? await _userRepository.GetUserByIdAsync(sub) : null;
            if (user?.Suspended == true)
            {
                await _authService.LogoutAsync(refreshRequest.RefreshToken);
                AuditLogger.LogSecurity("REFRESH_BLOCKED", userId: user.UserId, detail: "suspended",
                    correlationId: AuditLogger.GetCorrelationId(request), outcome: "failure",
                    sourceIp: AuditLogger.GetSourceIp(request));
                return LambdaResponse.Error(403, "AUTH_025",
                    "This account has been suspended. Please contact support.");
            }
        }

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
