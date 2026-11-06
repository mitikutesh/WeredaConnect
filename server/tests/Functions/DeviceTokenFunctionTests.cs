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
        // FUP-121: by default the endpoint is the caller's own, enabled, with the same token.
        EndpointAttributes(EndpointArn, owner: "buyer-1");
        _fn = new DeviceTokenFunction(_sns, _repo, PlatformAppArn);
    }

    private void EndpointAttributes(
        string endpointArn, string owner, string enabled = "true", string token = "fcm-token-xyz")
    {
        _sns.GetEndpointAttributesAsync(
                Arg.Is<GetEndpointAttributesRequest>(r => r.EndpointArn == endpointArn),
                Arg.Any<CancellationToken>())
            .Returns(new GetEndpointAttributesResponse
            {
                Attributes = new Dictionary<string, string>
                {
                    ["CustomUserData"] = owner,
                    ["Enabled"] = enabled,
                    ["Token"] = token,
                },
            });
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

    // ===== FUP-121: a shared phone's endpoint follows the user who registers it =====

    private const string ExistingArn = "arn:aws:sns:eu-north-1:123:endpoint/GCM/wereda-connect-dev-fcm/existing";

    private string Logs => ((TestLambdaLogger)_ctx.Logger).Buffer.ToString();

    /// <summary>SNS's refusal when the token's endpoint carries another user's CustomUserData.</summary>
    private void TokenAlreadyBoundTo(string endpointArn, string owner)
    {
        _sns.CreatePlatformEndpointAsync(
                Arg.Any<CreatePlatformEndpointRequest>(), Arg.Any<CancellationToken>())
            .Returns<CreatePlatformEndpointResponse>(_ => throw new InvalidParameterException(
                $"Invalid parameter: Token Reason: Endpoint {endpointArn} already exists with the same Token, but different attributes."));
        EndpointAttributes(endpointArn, owner);
    }

    private static bool Rebinds(SetEndpointAttributesRequest r, string arn, string userId) =>
        r.EndpointArn == arn
        && r.Attributes["CustomUserData"] == userId
        && r.Attributes["Token"] == "fcm-token-xyz"
        && r.Attributes["Enabled"] == "true";

    [Fact]
    public async Task Register_TokenBoundToAnotherUser_RemovesTheirRow_ThenRebindsTheEndpoint()
    {
        TokenAlreadyBoundTo(ExistingArn, owner: "farmer-1");

        var response = await _fn.FunctionHandler(
            Req("buyer-1", new { token = "fcm-token-xyz", platform = "android" }), _ctx);

        Assert.Equal(200, response.StatusCode);
        Received.InOrder(() =>
        {
            _repo.DeleteAsync("farmer-1", ExistingArn);
            _sns.SetEndpointAttributesAsync(
                Arg.Is<SetEndpointAttributesRequest>(r => Rebinds(r, ExistingArn, "buyer-1")),
                Arg.Any<CancellationToken>());
            _repo.PutAsync("buyer-1", ExistingArn, "android", "fcm-token-xyz");
        });
        // Nothing about the previous user leaves the server: not in the body, not in the logs.
        Assert.DoesNotContain("farmer-1", response.Body);
        Assert.DoesNotContain("farmer-1", Logs);
        Assert.DoesNotContain("fcm-token-xyz", Logs);
    }

    [Fact]
    public async Task Register_AlreadyExistsRefusal_UsesTheEndpointSnsNames()
    {
        TokenAlreadyBoundTo(ExistingArn, owner: "farmer-1");

        await _fn.FunctionHandler(Req("buyer-1", new { token = "fcm-token-xyz" }), _ctx);

        await _sns.Received(1).GetEndpointAttributesAsync(
            Arg.Is<GetEndpointAttributesRequest>(r => r.EndpointArn == ExistingArn), Arg.Any<CancellationToken>());
        await _repo.Received(1).PutAsync("buyer-1", ExistingArn, "android", "fcm-token-xyz");
        await _repo.DidNotReceive().PutAsync(
            Arg.Any<string>(), EndpointArn, Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task Register_InvalidParameterNamingNoEndpoint_Returns500_AndTouchesNoRow()
    {
        _sns.CreatePlatformEndpointAsync(
                Arg.Any<CreatePlatformEndpointRequest>(), Arg.Any<CancellationToken>())
            .Returns<CreatePlatformEndpointResponse>(_ => throw new InvalidParameterException(
                "Invalid parameter: Token Reason: malformed"));

        var response = await _fn.FunctionHandler(Req("buyer-1", new { token = "fcm-token-xyz" }), _ctx);

        Assert.Equal(500, response.StatusCode);
        await _sns.DidNotReceive().GetEndpointAttributesAsync(
            Arg.Any<GetEndpointAttributesRequest>(), Arg.Any<CancellationToken>());
        await _repo.DidNotReceive().DeleteAsync(Arg.Any<string>(), Arg.Any<string>());
        await _repo.DidNotReceive().PutAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task Register_SameUserAgain_LeavesTheEndpointAlone()
    {
        var response = await _fn.FunctionHandler(Req("buyer-1", new { token = "fcm-token-xyz" }), _ctx);

        Assert.Equal(200, response.StatusCode);
        await _sns.Received(1).GetEndpointAttributesAsync(
            Arg.Is<GetEndpointAttributesRequest>(r => r.EndpointArn == EndpointArn), Arg.Any<CancellationToken>());
        await _sns.DidNotReceive().SetEndpointAttributesAsync(
            Arg.Any<SetEndpointAttributesRequest>(), Arg.Any<CancellationToken>());
        await _repo.DidNotReceive().DeleteAsync(Arg.Any<string>(), Arg.Any<string>());
        await _repo.Received(1).PutAsync("buyer-1", EndpointArn, "android", "fcm-token-xyz");
    }

    [Theory]
    [InlineData("false", "fcm-token-xyz")] // disabled by SNS after an FCM failure
    [InlineData("true", "an-older-token")]  // holds another token
    public async Task Register_OwnEndpointDisabledOrStale_IsReEnabledWithTheToken(string enabled, string token)
    {
        EndpointAttributes(EndpointArn, owner: "buyer-1", enabled: enabled, token: token);

        var response = await _fn.FunctionHandler(Req("buyer-1", new { token = "fcm-token-xyz" }), _ctx);

        Assert.Equal(200, response.StatusCode);
        await _sns.Received(1).SetEndpointAttributesAsync(
            Arg.Is<SetEndpointAttributesRequest>(r => Rebinds(r, EndpointArn, "buyer-1")),
            Arg.Any<CancellationToken>());
        await _repo.DidNotReceive().DeleteAsync(Arg.Any<string>(), Arg.Any<string>());
        await _repo.Received(1).PutAsync("buyer-1", EndpointArn, "android", "fcm-token-xyz");
    }

    [Fact]
    public async Task Register_PreviousOwnersRowCannotBeDeleted_FailsClosed()
    {
        TokenAlreadyBoundTo(ExistingArn, owner: "farmer-1");
        _repo.DeleteAsync("farmer-1", ExistingArn).Returns<Task>(_ => throw new Exception("dynamodb unavailable"));

        var response = await _fn.FunctionHandler(Req("buyer-1", new { token = "fcm-token-xyz" }), _ctx);

        Assert.Equal(500, response.StatusCode);
        Assert.Contains("DEVICE_TOKEN_SERVER_ERROR", response.Body);
        // The endpoint keeps naming the previous owner (so the next registration finds their
        // row again) and is disabled, so their pushes stop reaching this phone meanwhile.
        await _sns.DidNotReceive().SetEndpointAttributesAsync(
            Arg.Is<SetEndpointAttributesRequest>(r => r.Attributes.ContainsKey("CustomUserData")),
            Arg.Any<CancellationToken>());
        await _sns.Received(1).SetEndpointAttributesAsync(
            Arg.Is<SetEndpointAttributesRequest>(r =>
                r.EndpointArn == ExistingArn && r.Attributes.Count == 1 && r.Attributes["Enabled"] == "false"),
            Arg.Any<CancellationToken>());
        await _repo.DidNotReceive().PutAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
        Assert.Contains("Could not remove the previous user's registration", Logs);
        Assert.DoesNotContain("farmer-1", Logs);
    }

    [Fact]
    public async Task Register_PreviousOwnersRowCannotBeDeleted_AndDisableFails_StillFailsClosed()
    {
        TokenAlreadyBoundTo(ExistingArn, owner: "farmer-1");
        _repo.DeleteAsync("farmer-1", ExistingArn).Returns<Task>(_ => throw new Exception("dynamodb unavailable"));
        _sns.SetEndpointAttributesAsync(Arg.Any<SetEndpointAttributesRequest>(), Arg.Any<CancellationToken>())
            .Returns<SetEndpointAttributesResponse>(_ => throw new Exception("sns unavailable"));

        var response = await _fn.FunctionHandler(Req("buyer-1", new { token = "fcm-token-xyz" }), _ctx);

        Assert.Equal(500, response.StatusCode);
        await _repo.DidNotReceive().PutAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
        Assert.Contains("Could not disable a shared device's push endpoint", Logs);
    }

    [Fact]
    public async Task Register_EndpointDeletedMeanwhile_IsCreatedAgain()
    {
        const string recreatedArn = "arn:aws:sns:eu-north-1:123:endpoint/GCM/wereda-connect-dev-fcm/recreated";
        _sns.CreatePlatformEndpointAsync(
                Arg.Any<CreatePlatformEndpointRequest>(), Arg.Any<CancellationToken>())
            .Returns(
                new CreatePlatformEndpointResponse { EndpointArn = EndpointArn },
                new CreatePlatformEndpointResponse { EndpointArn = recreatedArn });
        _sns.GetEndpointAttributesAsync(
                Arg.Is<GetEndpointAttributesRequest>(r => r.EndpointArn == EndpointArn), Arg.Any<CancellationToken>())
            .Returns<GetEndpointAttributesResponse>(_ => throw new NotFoundException("Endpoint does not exist"));
        EndpointAttributes(recreatedArn, owner: "buyer-1");

        var response = await _fn.FunctionHandler(Req("buyer-1", new { token = "fcm-token-xyz" }), _ctx);

        Assert.Equal(200, response.StatusCode);
        await _sns.Received(2).CreatePlatformEndpointAsync(
            Arg.Any<CreatePlatformEndpointRequest>(), Arg.Any<CancellationToken>());
        await _repo.Received(1).PutAsync("buyer-1", recreatedArn, "android", "fcm-token-xyz");
        await _repo.DidNotReceive().PutAsync(
            Arg.Any<string>(), EndpointArn, Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task Register_EndpointKeepsVanishing_Returns500AfterTwoTries()
    {
        _sns.GetEndpointAttributesAsync(Arg.Any<GetEndpointAttributesRequest>(), Arg.Any<CancellationToken>())
            .Returns<GetEndpointAttributesResponse>(_ => throw new NotFoundException("Endpoint does not exist"));

        var response = await _fn.FunctionHandler(Req("buyer-1", new { token = "fcm-token-xyz" }), _ctx);

        Assert.Equal(500, response.StatusCode);
        await _sns.Received(2).CreatePlatformEndpointAsync(
            Arg.Any<CreatePlatformEndpointRequest>(), Arg.Any<CancellationToken>());
        await _repo.DidNotReceive().PutAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
    }

    /// <summary>
    /// The ticket's end state, with SNS and the table kept in memory: a farmer signs in, logs
    /// out offline (no DELETE), a buyer signs in on the same phone. Pushes are routed by the
    /// recipient's rows (<see cref="IDeviceTokenRepository.GetEndpointArnsByUserAsync"/>), so
    /// once the farmer's row is gone none of their pushes reaches this endpoint.
    /// </summary>
    [Fact]
    public async Task SharedPhone_NextUserTakesTheEndpoint_AndThePreviousUsersPushesNoLongerRouteToIt()
    {
        var sns = new InMemorySnsEndpoints();
        var rows = new InMemoryDeviceTokens();
        var fn = new DeviceTokenFunction(sns.Substitute, rows, PlatformAppArn);

        Assert.Equal(200, (await fn.FunctionHandler(Req("farmer-1", new { token = "fcm-token-xyz" }), _ctx)).StatusCode);
        var arn = Assert.Single(await rows.GetEndpointArnsByUserAsync("farmer-1"));
        Assert.Equal("farmer-1", sns.Endpoints[arn]["CustomUserData"]);

        Assert.Equal(200, (await fn.FunctionHandler(Req("buyer-1", new { token = "fcm-token-xyz" }), _ctx)).StatusCode);

        Assert.Empty(await rows.GetEndpointArnsByUserAsync("farmer-1"));
        Assert.Equal(new[] { arn }, await rows.GetEndpointArnsByUserAsync("buyer-1"));
        Assert.Single(sns.Endpoints);
        Assert.Equal("buyer-1", sns.Endpoints[arn]["CustomUserData"]);
        Assert.Equal("true", sns.Endpoints[arn]["Enabled"]);

        // And back: the farmer signs in again on the same phone.
        Assert.Equal(200, (await fn.FunctionHandler(Req("farmer-1", new { token = "fcm-token-xyz" }), _ctx)).StatusCode);
        Assert.Empty(await rows.GetEndpointArnsByUserAsync("buyer-1"));
        Assert.Equal(new[] { arn }, await rows.GetEndpointArnsByUserAsync("farmer-1"));
    }

    /// <summary>SNS platform endpoints in memory, with CreatePlatformEndpoint's attribute check.</summary>
    private sealed class InMemorySnsEndpoints
    {
        public IAmazonSimpleNotificationService Substitute { get; } =
            NSubstitute.Substitute.For<IAmazonSimpleNotificationService>();

        public Dictionary<string, Dictionary<string, string>> Endpoints { get; } = new();

        public InMemorySnsEndpoints()
        {
            Substitute.CreatePlatformEndpointAsync(
                    Arg.Any<CreatePlatformEndpointRequest>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    var request = call.Arg<CreatePlatformEndpointRequest>();
                    foreach (var (arn, attributes) in Endpoints)
                    {
                        if (attributes["Token"] != request.Token)
                        {
                            continue;
                        }

                        if (attributes["CustomUserData"] != request.CustomUserData)
                        {
                            throw new InvalidParameterException(
                                $"Invalid parameter: Token Reason: Endpoint {arn} already exists with the same Token, but different attributes.");
                        }

                        return new CreatePlatformEndpointResponse { EndpointArn = arn };
                    }

                    var created = $"arn:aws:sns:eu-north-1:123:endpoint/GCM/wereda-connect-dev-fcm/{Endpoints.Count + 1}";
                    Endpoints[created] = new Dictionary<string, string>
                    {
                        ["CustomUserData"] = request.CustomUserData,
                        ["Token"] = request.Token,
                        ["Enabled"] = "true",
                    };
                    return new CreatePlatformEndpointResponse { EndpointArn = created };
                });
            Substitute.GetEndpointAttributesAsync(
                    Arg.Any<GetEndpointAttributesRequest>(), Arg.Any<CancellationToken>())
                .Returns(call => new GetEndpointAttributesResponse
                {
                    Attributes = new Dictionary<string, string>(Endpoints[call.Arg<GetEndpointAttributesRequest>().EndpointArn]),
                });
            Substitute.SetEndpointAttributesAsync(
                    Arg.Any<SetEndpointAttributesRequest>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    var request = call.Arg<SetEndpointAttributesRequest>();
                    foreach (var (key, value) in request.Attributes)
                    {
                        Endpoints[request.EndpointArn][key] = value;
                    }

                    return new SetEndpointAttributesResponse();
                });
        }
    }

    /// <summary>The device-tokens table in memory (PK userId, SK endpointArn).</summary>
    private sealed class InMemoryDeviceTokens : IDeviceTokenRepository
    {
        private readonly Dictionary<(string UserId, string EndpointArn), string> _rows = new();

        public Task PutAsync(string userId, string endpointArn, string platform, string token)
        {
            _rows[(userId, endpointArn)] = token;
            return Task.CompletedTask;
        }

        public Task<List<string>> GetEndpointArnsByUserAsync(string userId) =>
            Task.FromResult(_rows.Keys.Where(k => k.UserId == userId).Select(k => k.EndpointArn).ToList());

        public Task DeleteAsync(string userId, string endpointArn)
        {
            _rows.Remove((userId, endpointArn));
            return Task.CompletedTask;
        }

        public Task<string?> FindEndpointArnByTokenAsync(string userId, string token) =>
            Task.FromResult(_rows
                .Where(r => r.Key.UserId == userId && r.Value == token)
                .Select(r => (string?)r.Key.EndpointArn)
                .FirstOrDefault());
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
