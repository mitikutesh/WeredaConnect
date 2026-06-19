using System.Text.Json;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.TestUtilities;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using NSubstitute;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Functions;
using Xunit;

namespace WeredaConnect.Server.Tests.Functions;

public class DeviceTokenFunctionTests
{
    private readonly IAmazonSimpleNotificationService _sns = Substitute.For<IAmazonSimpleNotificationService>();
    private readonly IDeviceTokenRepository _repo = Substitute.For<IDeviceTokenRepository>();
    private readonly DeviceTokenFunction _fn;
    private readonly TestLambdaContext _ctx = new();

    private const string PlatformAppArn = "arn:aws:sns:eu-north-1:123:app/GCM/wereda-connect-dev-fcm";
    private const string EndpointArn = "arn:aws:sns:eu-north-1:123:endpoint/GCM/wereda-connect-dev-fcm/abc";

    public DeviceTokenFunctionTests()
    {
        _sns.CreatePlatformEndpointAsync(
                Arg.Any<CreatePlatformEndpointRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CreatePlatformEndpointResponse { EndpointArn = EndpointArn });
        _fn = new DeviceTokenFunction(_sns, _repo, PlatformAppArn);
    }

    private static APIGatewayProxyRequest Req(string? userId, object? body)
    {
        var authorizer = new APIGatewayCustomAuthorizerContext();
        if (userId != null)
        {
            authorizer["sub"] = userId;
        }

        return new APIGatewayProxyRequest
        {
            HttpMethod = "POST",
            Body = body == null ? null : JsonSerializer.Serialize(body),
            RequestContext = new APIGatewayProxyRequest.ProxyRequestContext { Authorizer = authorizer },
        };
    }

    [Fact]
    public async Task Register_CreatesEndpoint_AndStoresIt()
    {
        var response = await _fn.FunctionHandler(
            Req("buyer-1", new { token = "fcm-token-xyz", platform = "android" }), _ctx);

        Assert.Equal(200, response.StatusCode);
        await _sns.Received(1).CreatePlatformEndpointAsync(
            Arg.Is<CreatePlatformEndpointRequest>(r =>
                r.PlatformApplicationArn == PlatformAppArn && r.Token == "fcm-token-xyz"),
            Arg.Any<CancellationToken>());
        await _repo.Received(1).PutAsync("buyer-1", EndpointArn, "android", "fcm-token-xyz");
    }

    [Fact]
    public async Task MissingToken_Returns400()
    {
        var response = await _fn.FunctionHandler(Req("buyer-1", new { platform = "android" }), _ctx);

        Assert.Equal(400, response.StatusCode);
        Assert.Contains("DEVICE_TOKEN_VALIDATION", response.Body);
        await _repo.DidNotReceive().PutAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task Unauthenticated_Returns401()
    {
        var response = await _fn.FunctionHandler(
            Req(null, new { token = "fcm-token-xyz" }), _ctx);

        Assert.Equal(401, response.StatusCode);
        Assert.Contains("DEVICE_TOKEN_UNAUTHORIZED", response.Body);
    }

    [Fact]
    public async Task DefaultsPlatformToAndroid()
    {
        await _fn.FunctionHandler(Req("buyer-1", new { token = "fcm-token-xyz" }), _ctx);

        await _repo.Received(1).PutAsync("buyer-1", EndpointArn, "android", "fcm-token-xyz");
    }

    [Fact]
    public async Task MalformedJsonBody_Returns400()
    {
        var req = new APIGatewayProxyRequest
        {
            HttpMethod = "POST",
            Body = "{ not valid json",
            RequestContext = new APIGatewayProxyRequest.ProxyRequestContext
            {
                Authorizer = new APIGatewayCustomAuthorizerContext { { "sub", "buyer-1" } },
            },
        };

        var response = await _fn.FunctionHandler(req, _ctx);

        Assert.Equal(400, response.StatusCode);
        Assert.Contains("DEVICE_TOKEN_VALIDATION", response.Body);
        await _repo.DidNotReceive().PutAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task SnsFailure_Returns500()
    {
        _sns.CreatePlatformEndpointAsync(
                Arg.Any<CreatePlatformEndpointRequest>(), Arg.Any<CancellationToken>())
            .Returns<CreatePlatformEndpointResponse>(_ => throw new Exception("sns unavailable"));

        var response = await _fn.FunctionHandler(
            Req("buyer-1", new { token = "fcm-token-xyz" }), _ctx);

        Assert.Equal(500, response.StatusCode);
        Assert.Contains("DEVICE_TOKEN_SERVER_ERROR", response.Body);
        await _repo.DidNotReceive().PutAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
    }
}
