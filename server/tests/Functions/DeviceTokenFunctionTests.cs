using System.Text.Json;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.TestUtilities;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using NSubstitute;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Functions;
using WeredaConnect.Server.Models;
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

    // --- SIM-60: platform is an allow-list, not free text ---

    [Theory]
    [InlineData("ios")]
    [InlineData("web")]
    [InlineData("<script>alert(1)</script>")]
    public async Task Register_UnknownPlatform_Returns400AndCreatesNoEndpoint(string platform)
    {
        var response = await _fn.FunctionHandler(
            Req("buyer-1", new { token = "fcm-token-xyz", platform }), _ctx);

        Assert.Equal(400, response.StatusCode);
        Assert.Contains("DEVICE_TOKEN_VALIDATION", response.Body);
        Assert.Contains("platform must be", response.Body);
        await _sns.DidNotReceive().CreatePlatformEndpointAsync(
            Arg.Any<CreatePlatformEndpointRequest>(), Arg.Any<CancellationToken>());
        await _repo.DidNotReceive().PutAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task Register_OverlongPlatform_Returns400()
    {
        var response = await _fn.FunctionHandler(
            Req("buyer-1", new { token = "fcm-token-xyz", platform = new string('a', 300) }), _ctx);

        Assert.Equal(400, response.StatusCode);
        await _repo.DidNotReceive().PutAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task Register_PlatformIsCaseInsensitive_StoredLowercase()
    {
        var response = await _fn.FunctionHandler(
            Req("buyer-1", new { token = "fcm-token-xyz", platform = " Android " }), _ctx);

        Assert.Equal(200, response.StatusCode);
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

    // ===== DELETE /device-tokens (logout on a shared phone) =====

    private static APIGatewayProxyRequest DeleteReq(string? userId, object? body)
    {
        var request = Req(userId, body);
        request.HttpMethod = "DELETE";
        return request;
    }

    [Fact]
    public async Task Unregister_KnownToken_RemovesRowAndEndpoint()
    {
        _repo.FindEndpointArnByTokenAsync("buyer-1", "fcm-token-xyz").Returns(EndpointArn);

        var response = await _fn.FunctionHandler(DeleteReq("buyer-1", new { token = "fcm-token-xyz" }), _ctx);

        Assert.Equal(204, response.StatusCode);
        await _repo.Received(1).DeleteAsync("buyer-1", EndpointArn);
        await _sns.Received(1).DeleteEndpointAsync(
            Arg.Is<DeleteEndpointRequest>(r => r.EndpointArn == EndpointArn), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Unregister_UnknownToken_IsANoOp204()
    {
        _repo.FindEndpointArnByTokenAsync("buyer-1", "nope").Returns((string?)null);

        var response = await _fn.FunctionHandler(DeleteReq("buyer-1", new { token = "nope" }), _ctx);

        Assert.Equal(204, response.StatusCode);
        await _repo.DidNotReceive().DeleteAsync(Arg.Any<string>(), Arg.Any<string>());
        await _sns.DidNotReceive().DeleteEndpointAsync(Arg.Any<DeleteEndpointRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Unregister_OnlyLooksUpTheCallersOwnRegistration()
    {
        await _fn.FunctionHandler(DeleteReq("buyer-1", new { token = "fcm-token-xyz" }), _ctx);

        await _repo.Received(1).FindEndpointArnByTokenAsync("buyer-1", "fcm-token-xyz");
    }

    [Fact]
    public async Task Unregister_MissingToken_Returns400()
    {
        var response = await _fn.FunctionHandler(DeleteReq("buyer-1", new { token = "" }), _ctx);

        Assert.Equal(400, response.StatusCode);
    }

    [Fact]
    public async Task Unregister_Unauthenticated_Returns401()
    {
        var response = await _fn.FunctionHandler(DeleteReq(null, new { token = "fcm-token-xyz" }), _ctx);

        Assert.Equal(401, response.StatusCode);
    }

    // ===== ACC-02: a deleting or deleted account registers no device =====

    private DeviceTokenFunction WithAccount(string? deletionState, out IUserRepository users)
    {
        users = Substitute.For<IUserRepository>();
        users.GetUserByIdAsync("buyer-1", true).Returns(new UserDto
        {
            UserId = "buyer-1",
            PhoneNumber = "+251911000003",
            Role = "buyer",
            Woreda = "Adama",
            IsPhoneVerified = true,
            DeletionState = deletionState,
            CreatedAt = "2026-01-01T00:00:00Z",
            UpdatedAt = "2026-01-01T00:00:00Z",
            Version = 1
        });
        return new DeviceTokenFunction(_sns, _repo, PlatformAppArn, users);
    }

    [Theory]
    [InlineData("in_progress")]
    [InlineData("completed")]
    public async Task Register_ByADeletingAccount_Returns410_AndCreatesNoEndpoint(string state)
    {
        var fn = WithAccount(state, out _);

        var response = await fn.FunctionHandler(Req("buyer-1", new { token = "fcm-token-xyz" }), _ctx);

        Assert.Equal(410, response.StatusCode);
        Assert.Contains("ACCOUNT_004", response.Body);
        await _sns.DidNotReceive().CreatePlatformEndpointAsync(
            Arg.Any<CreatePlatformEndpointRequest>(), Arg.Any<CancellationToken>());
        await _repo.DidNotReceive().PutAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task Register_ByALiveAccount_StillWorks()
    {
        var fn = WithAccount(null, out _);

        var response = await fn.FunctionHandler(Req("buyer-1", new { token = "fcm-token-xyz" }), _ctx);

        Assert.Equal(200, response.StatusCode);
    }

    [Fact]
    public async Task Unregister_ByADeletingAccount_StillRemovesTheToken()
    {
        var fn = WithAccount("in_progress", out var users);
        _repo.FindEndpointArnByTokenAsync("buyer-1", "fcm-token-xyz").Returns(EndpointArn);

        var response = await fn.FunctionHandler(DeleteReq("buyer-1", new { token = "fcm-token-xyz" }), _ctx);

        Assert.Equal(204, response.StatusCode);
        await _repo.Received(1).DeleteAsync("buyer-1", EndpointArn);
        await users.DidNotReceive().GetUserByIdAsync(Arg.Any<string>(), Arg.Any<bool>());
    }
}
