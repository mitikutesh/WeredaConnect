using Amazon.DynamoDBv2;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Models;

namespace WeredaConnect.Server.Functions;

/// <summary>
/// POST /device-tokens — register an FCM device token for push alerts.
///
/// Creates an SNS platform endpoint for the token (under the SNS Platform
/// Application configured with the FCM credential) and stores the resulting
/// endpoint ARN against the authenticated user. The match-on-insert stream
/// Lambda publishes to these endpoints. See
/// specs/architecture/push-notifications-architecture.md.
///
/// Error codes:
/// - 400: DEVICE_TOKEN_VALIDATION, missing/empty token, bad body, or a platform the
///   push pipeline cannot deliver to (SIM-60: only "android" today)
/// - 401: DEVICE_TOKEN_UNAUTHORIZED, no authenticated user
/// - 500: DEVICE_TOKEN_SERVER_ERROR, unexpected failure
/// </summary>
public class DeviceTokenFunction
{
    /// <summary>
    /// Platforms the SNS platform application can deliver to. It is configured with the
    /// FCM credential, so only Android tokens are accepted; add "ios" here once an APNs
    /// application exists. Compared after trim + lower-case.
    /// </summary>
    private static readonly HashSet<string> AllowedPlatforms = new(StringComparer.Ordinal) { "android" };

    private readonly IAmazonSimpleNotificationService _sns;
    private readonly IDeviceTokenRepository _repo;
    private readonly string _platformAppArn;

    /// <summary>Default constructor for the Lambda runtime.</summary>
    public DeviceTokenFunction()
    {
        _platformAppArn = Environment.GetEnvironmentVariable("PLATFORM_APP_ARN")
                          ?? throw new InvalidOperationException("PLATFORM_APP_ARN is not set.");
        var table = Environment.GetEnvironmentVariable("DEVICE_TOKENS_TABLE")
                    ?? throw new InvalidOperationException("DEVICE_TOKENS_TABLE is not set.");

        _sns = new AmazonSimpleNotificationServiceClient();
        _repo = new DeviceTokenRepository(new AmazonDynamoDBClient(), table);
    }

    /// <summary>Constructor for testing with mocked dependencies.</summary>
    public DeviceTokenFunction(
        IAmazonSimpleNotificationService sns,
        IDeviceTokenRepository repo,
        string platformAppArn)
    {
        _sns = sns ?? throw new ArgumentNullException(nameof(sns));
        _repo = repo ?? throw new ArgumentNullException(nameof(repo));
        _platformAppArn = platformAppArn ?? throw new ArgumentNullException(nameof(platformAppArn));
    }

    public async Task<APIGatewayProxyResponse> FunctionHandler(
        APIGatewayProxyRequest request,
        ILambdaContext context)
    {
        context.Logger.LogInformation($"DeviceTokenFunction invoked: {request.HttpMethod}");

        return request.HttpMethod switch
        {
            "POST" => await HandleRegisterAsync(request, context),
            "DELETE" => await HandleUnregisterAsync(request, context),
            _ => LambdaResponse.Error(405, "DEVICE_TOKEN_VALIDATION", "Method not allowed.")
        };
    }

    /// <summary>
    /// DELETE /device-tokens {token}: called on logout so a shared phone stops
    /// receiving the previous user's pushes. Only the caller's own registration can
    /// be removed; an unknown token is a no-op (204 either way).
    /// </summary>
    private async Task<APIGatewayProxyResponse> HandleUnregisterAsync(
        APIGatewayProxyRequest request,
        ILambdaContext context)
    {
        var userId = AuthHelper.GetUserId(request);
        if (string.IsNullOrEmpty(userId))
        {
            return LambdaResponse.Error(401, "DEVICE_TOKEN_UNAUTHORIZED", "Authentication required.");
        }

        RegisterDeviceTokenRequest? body;
        try
        {
            body = JsonSerializer.Deserialize(
                request.Body ?? "", AppJsonContext.Default.RegisterDeviceTokenRequest);
        }
        catch (JsonException)
        {
            return LambdaResponse.Error(400, "DEVICE_TOKEN_VALIDATION", "Invalid request body.");
        }

        if (body == null || string.IsNullOrWhiteSpace(body.Token))
        {
            return LambdaResponse.Error(400, "DEVICE_TOKEN_VALIDATION", "token is required.");
        }

        try
        {
            var endpointArn = await _repo.FindEndpointArnByTokenAsync(userId, body.Token!);
            if (endpointArn != null)
            {
                await _repo.DeleteAsync(userId, endpointArn);
                try
                {
                    await _sns.DeleteEndpointAsync(new DeleteEndpointRequest { EndpointArn = endpointArn });
                }
                catch (Exception ex)
                {
                    // The row is gone, so no push can be routed here any more; the
                    // orphaned SNS endpoint is harmless and cleaned up by SNS itself.
                    context.Logger.LogWarning($"Could not delete SNS endpoint: [{ex.GetType().Name}]");
                }
            }

            return LambdaResponse.NoContent();
        }
        catch (Exception ex)
        {
            context.Logger.LogError($"Error unregistering device token: [{ex.GetType().Name}]");
            return LambdaResponse.Error(500, "DEVICE_TOKEN_SERVER_ERROR", "Failed to unregister device token.");
        }
    }

    private async Task<APIGatewayProxyResponse> HandleRegisterAsync(
        APIGatewayProxyRequest request,
        ILambdaContext context)
    {
        var userId = AuthHelper.GetUserId(request);
        if (string.IsNullOrEmpty(userId))
        {
            return LambdaResponse.Error(401, "DEVICE_TOKEN_UNAUTHORIZED", "Authentication required.");
        }

        RegisterDeviceTokenRequest? body;
        try
        {
            body = JsonSerializer.Deserialize(
                request.Body ?? "", AppJsonContext.Default.RegisterDeviceTokenRequest);
        }
        catch (JsonException)
        {
            return LambdaResponse.Error(400, "DEVICE_TOKEN_VALIDATION", "Invalid request body.");
        }

        if (body == null || string.IsNullOrWhiteSpace(body.Token))
        {
            return LambdaResponse.Error(400, "DEVICE_TOKEN_VALIDATION", "token is required.");
        }

        // SIM-60: the platform used to be stored verbatim (any string, any length).
        var platform = string.IsNullOrWhiteSpace(body.Platform)
            ? "android"
            : body.Platform!.Trim().ToLowerInvariant();
        if (!AllowedPlatforms.Contains(platform))
        {
            return LambdaResponse.Error(400, "DEVICE_TOKEN_VALIDATION", "platform must be 'android'.", "platform");
        }

        try
        {
            var endpoint = await _sns.CreatePlatformEndpointAsync(new CreatePlatformEndpointRequest
            {
                PlatformApplicationArn = _platformAppArn,
                Token = body.Token,
                // Associate the endpoint with the user so a stale endpoint can be traced.
                CustomUserData = userId,
            });

            await _repo.PutAsync(userId, endpoint.EndpointArn, platform, body.Token!);

            return LambdaResponse.Ok(
                new RegisterDeviceTokenResponse { Success = true },
                AppJsonContext.Default.RegisterDeviceTokenResponse);
        }
        catch (Exception ex)
        {
            context.Logger.LogError($"Error registering device token: [{ex.GetType().Name}]");
            return LambdaResponse.Error(500, "DEVICE_TOKEN_SERVER_ERROR", "Failed to register device token.");
        }
    }
}
