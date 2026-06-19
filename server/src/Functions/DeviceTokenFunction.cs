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
/// - 400: DEVICE_TOKEN_VALIDATION — missing/empty token or bad body
/// - 401: DEVICE_TOKEN_UNAUTHORIZED — no authenticated user
/// - 500: DEVICE_TOKEN_SERVER_ERROR — unexpected failure
/// </summary>
public class DeviceTokenFunction
{
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
            _ => LambdaResponse.Error(405, "DEVICE_TOKEN_VALIDATION", "Method not allowed.")
        };
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

        var platform = string.IsNullOrWhiteSpace(body.Platform) ? "android" : body.Platform!;

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
