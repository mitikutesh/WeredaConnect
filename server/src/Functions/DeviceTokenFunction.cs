using System.Text.RegularExpressions;
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
/// FUP-121: a shared phone keeps one FCM token, so one SNS endpoint, for every user
/// who signs in on it. When the endpoint is still bound to another user (their
/// logout's DELETE never reached the server), registration removes that user's row
/// and rebinds the endpoint to the caller (push-alerts-api.md section 1).
///
/// Error codes:
/// - 400: DEVICE_TOKEN_VALIDATION, missing/empty token, bad body, or a platform the
///   push pipeline cannot deliver to (SIM-60: only "android" today)
/// - 401: DEVICE_TOKEN_UNAUTHORIZED, no authenticated user
/// - 410: ACCOUNT_004, POST by an account that is being or has been deleted (ACC-02)
/// - 500: DEVICE_TOKEN_SERVER_ERROR, unexpected failure
/// </summary>
public partial class DeviceTokenFunction
{
    /// <summary>
    /// Platforms the SNS platform application can deliver to. It is configured with the
    /// FCM credential, so only Android tokens are accepted; add "ios" here once an APNs
    /// application exists. Compared after trim + lower-case.
    /// </summary>
    private static readonly HashSet<string> AllowedPlatforms = new(StringComparer.Ordinal) { "android" };

    /// <summary>
    /// FUP-121: create, then claim, at most this many times. A second try only happens
    /// when the endpoint was deleted between the two steps (SNS NotFound).
    /// </summary>
    private const int MaxEndpointAttempts = 2;

    /// <summary>
    /// The ARN in the InvalidParameter message SNS returns when CreatePlatformEndpoint
    /// finds an endpoint for the same token with other attributes, here another user's
    /// CustomUserData: "Endpoint arn:aws:sns:... already exists with the same Token,
    /// but different attributes."
    /// </summary>
    [GeneratedRegex(@"Endpoint (arn:aws:sns:\S+) already exists with the same [Tt]oken")]
    private static partial Regex ExistingEndpointArn();

    private readonly IAmazonSimpleNotificationService _sns;
    private readonly IDeviceTokenRepository _repo;
    private readonly string _platformAppArn;
    // ACC-02: a deleting or deleted account registers no device.
    private readonly IUserRepository? _users;

    /// <summary>Default constructor for the Lambda runtime.</summary>
    public DeviceTokenFunction()
    {
        _platformAppArn = Environment.GetEnvironmentVariable("PLATFORM_APP_ARN")
                          ?? throw new InvalidOperationException("PLATFORM_APP_ARN is not set.");
        var table = Environment.GetEnvironmentVariable("DEVICE_TOKENS_TABLE")
                    ?? throw new InvalidOperationException("DEVICE_TOKENS_TABLE is not set.");
        var usersTable = Environment.GetEnvironmentVariable("USERS_TABLE")
                         ?? throw new InvalidOperationException("USERS_TABLE is not set.");

        var dynamoDb = new AmazonDynamoDBClient();
        _sns = new AmazonSimpleNotificationServiceClient();
        _repo = new DeviceTokenRepository(dynamoDb, table);
        _users = new UserRepository(dynamoDb, usersTable);
    }

    /// <summary>Constructor for testing with mocked dependencies.</summary>
    public DeviceTokenFunction(
        IAmazonSimpleNotificationService sns,
        IDeviceTokenRepository repo,
        string platformAppArn,
        IUserRepository? users = null)
    {
        _sns = sns ?? throw new ArgumentNullException(nameof(sns));
        _repo = repo ?? throw new ArgumentNullException(nameof(repo));
        _platformAppArn = platformAppArn ?? throw new ArgumentNullException(nameof(platformAppArn));
        _users = users;
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
            if (_users != null && await AccountDeletionGuard.RefuseIfDeletingAsync(_users, userId) is { } gone)
            {
                return gone;
            }

            var endpointArn = await BindEndpointAsync(userId, body.Token!, context);

            await _repo.PutAsync(userId, endpointArn, platform, body.Token!);

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

    /// <summary>
    /// FUP-121: the endpoint for this token, bound to <paramref name="userId"/> and enabled.
    /// AWS's token-management pattern: create the endpoint (or take the existing one's ARN
    /// from SNS's refusal), read its attributes, then rebind it to the caller when it
    /// belongs to someone else, is disabled or holds another token. An endpoint deleted
    /// between the steps is created again, once.
    /// </summary>
    private async Task<string> BindEndpointAsync(string userId, string token, ILambdaContext context)
    {
        for (var attempt = 1; ; attempt++)
        {
            var endpointArn = await CreateOrFindEndpointAsync(userId, token);
            try
            {
                await ClaimEndpointAsync(endpointArn, userId, token, context);
                return endpointArn;
            }
            catch (NotFoundException) when (attempt < MaxEndpointAttempts)
            {
                // Deleted meanwhile (a logout or an account deletion): create it again.
            }
        }
    }

    /// <summary>
    /// CreatePlatformEndpoint is idempotent only for identical attributes. For a token whose
    /// endpoint is bound to another user (CustomUserData) SNS refuses with InvalidParameter
    /// and names the existing endpoint; that ARN is used. Any other refusal propagates.
    /// </summary>
    private async Task<string> CreateOrFindEndpointAsync(string userId, string token)
    {
        try
        {
            var created = await _sns.CreatePlatformEndpointAsync(new CreatePlatformEndpointRequest
            {
                PlatformApplicationArn = _platformAppArn,
                Token = token,
                // The endpoint's owner: the only link from the token back to a user's row,
                // read by ClaimEndpointAsync when another user registers the same token.
                CustomUserData = userId,
            });
            return created.EndpointArn;
        }
        catch (InvalidParameterException ex)
            when (ExistingEndpointArn().Match(ex.Message ?? "") is { Success: true } existing)
        {
            return existing.Groups[1].Value;
        }
    }

    /// <summary>
    /// Makes <paramref name="userId"/> the endpoint's owner. A previous owner's row is
    /// deleted BEFORE the endpoint is rebound: the endpoint's CustomUserData is the only
    /// way back to that row (the table has no index by token or endpoint), so rebinding
    /// first and then failing the delete would lose the row for good and keep routing the
    /// previous owner's pushes, with names and amounts, to this phone. When the delete
    /// fails the call fails closed: the endpoint is disabled (best effort, so the previous
    /// owner's pushes stop and their publishers prune the row) and still names the previous
    /// owner, so the app's next registration repeats the whole claim.
    /// </summary>
    private async Task ClaimEndpointAsync(
        string endpointArn, string userId, string token, ILambdaContext context)
    {
        var current = await _sns.GetEndpointAttributesAsync(
            new GetEndpointAttributesRequest { EndpointArn = endpointArn });
        var attributes = current.Attributes ?? new Dictionary<string, string>();
        var owner = attributes.GetValueOrDefault("CustomUserData");
        var enabled = string.Equals(attributes.GetValueOrDefault("Enabled"), "true", StringComparison.OrdinalIgnoreCase);
        var sameToken = string.Equals(attributes.GetValueOrDefault("Token"), token, StringComparison.Ordinal);

        if (owner == userId && enabled && sameToken)
        {
            return;
        }

        var previousOwner = !string.IsNullOrEmpty(owner) && owner != userId ? owner : null;
        if (previousOwner != null)
        {
            try
            {
                await _repo.DeleteAsync(previousOwner, endpointArn);
            }
            catch (Exception ex)
            {
                context.Logger.LogError(
                    $"Could not remove the previous user's registration of a shared device: [{ex.GetType().Name}]");
                await DisableEndpointAsync(endpointArn, context);
                throw;
            }
        }

        await _sns.SetEndpointAttributesAsync(new SetEndpointAttributesRequest
        {
            EndpointArn = endpointArn,
            Attributes = new Dictionary<string, string>
            {
                ["CustomUserData"] = userId,
                ["Token"] = token,
                ["Enabled"] = "true",
            },
        });

        if (previousOwner != null)
        {
            context.Logger.LogInformation(
                "Shared device: push endpoint rebound to the registering user, the previous user's registration removed.");
        }
    }

    /// <summary>Stops delivery to the endpoint and keeps its owner. Best effort.</summary>
    private async Task DisableEndpointAsync(string endpointArn, ILambdaContext context)
    {
        try
        {
            await _sns.SetEndpointAttributesAsync(new SetEndpointAttributesRequest
            {
                EndpointArn = endpointArn,
                Attributes = new Dictionary<string, string> { ["Enabled"] = "false" },
            });
        }
        catch (Exception ex)
        {
            context.Logger.LogError($"Could not disable a shared device's push endpoint: [{ex.GetType().Name}]");
        }
    }
}
