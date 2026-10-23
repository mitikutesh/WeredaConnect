using System.Text.Json;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.TestUtilities;
using Amazon.SimpleNotificationService;
using FluentAssertions;
using NSubstitute;
using WeredaConnect.Server.Functions;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.Services;
using WeredaConnect.Server.Tests.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Functions;

/// <summary>ACC-02: the HTTP contract of <c>DELETE /user/account</c> and the sweep entry point.</summary>
public class AccountDeletionFunctionTests
{
    private const string UserId = "buyer-1";

    private readonly FakeAccountDeletionRepository _repo = new();
    private readonly FakeTransactionAuditRepository _audit = new();
    private readonly ICognitoAuthService _cognito = Substitute.For<ICognitoAuthService>();
    private readonly TestLambdaContext _context = new();
    private readonly AccountDeletionFunction _function;

    public AccountDeletionFunctionTests()
    {
        _cognito.AdminGlobalSignOutAsync(Arg.Any<string>()).Returns(AuthResult<bool>.Success(true));
        _cognito.AdminDeleteUserAsync(Arg.Any<string>()).Returns(AuthResult<bool>.Success(true));
        _function = new AccountDeletionFunction(new AccountDeletionService(
            _repo, _cognito, Substitute.For<IAmazonSimpleNotificationService>(), _audit));
        _repo.AddUser(new UserDto
        {
            UserId = UserId,
            PhoneNumber = "+251911000003",
            Role = "buyer",
            Woreda = "Adama",
            IsPhoneVerified = true,
            CreatedAt = "2026-01-01T00:00:00Z",
            UpdatedAt = "2026-01-01T00:00:00Z",
            Version = 1
        });
    }

    private static APIGatewayProxyRequest Req(
        string? body = "{\"confirm\":true}", string method = "DELETE", string? userId = UserId, long? authTime = 0)
    {
        var authorizer = new APIGatewayCustomAuthorizerContext();
        if (userId != null)
        {
            authorizer["sub"] = userId;
            authorizer["custom:role"] = "buyer";
        }

        if (authTime != null)
        {
            authorizer["auth_time"] = (authTime == 0 ? DateTimeOffset.UtcNow.ToUnixTimeSeconds() : authTime.Value).ToString();
        }

        return new APIGatewayProxyRequest
        {
            HttpMethod = method,
            Path = "/user/account",
            Resource = "/user/account",
            Body = body,
            RequestContext = new APIGatewayProxyRequest.ProxyRequestContext { Authorizer = authorizer }
        };
    }

    private static JsonElement Json(APIGatewayProxyResponse response) => JsonDocument.Parse(response.Body).RootElement;

    private void AddFundedOrder(string orderId) => _repo.Orders.Add(new Dictionary<string, object>
    {
        ["orderId"] = orderId,
        ["createdAt"] = "2026-10-01T00:00:00Z",
        ["status"] = "funded",
        ["buyerId"] = UserId,
        ["farmerId"] = "farmer-1",
        ["version"] = "1"
    });

    [Fact]
    public async Task Deleted_Returns200_WithTheContractBody()
    {
        var response = await _function.FunctionHandler(Req(), _context);

        response.StatusCode.Should().Be(200);
        var body = Json(response);
        body.GetProperty("deleted").GetBoolean().Should().BeTrue();
        body.GetProperty("userId").GetString().Should().Be(UserId);
        body.GetProperty("deletionState").GetString().Should().Be("completed");
        body.GetProperty("deletedAt").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task ARepeatedRequest_IsAReplayOfTheSameBody()
    {
        var first = await _function.FunctionHandler(Req(), _context);
        var second = await _function.FunctionHandler(Req(authTime: DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeSeconds()), _context);

        second.StatusCode.Should().Be(200);
        Json(second).GetProperty("deletedAt").GetString().Should().Be(Json(first).GetProperty("deletedAt").GetString());
        await _cognito.Received(1).AdminDeleteUserAsync(UserId);
    }

    [Fact]
    public async Task Blocked_Returns409_ListsAtMost20Blockers_AndTheFullCount()
    {
        for (var i = 0; i < 25; i++)
        {
            AddFundedOrder($"order-{i:D2}");
        }

        var response = await _function.FunctionHandler(Req(), _context);

        response.StatusCode.Should().Be(409);
        var error = Json(response).GetProperty("error");
        error.GetProperty("code").GetString().Should().Be("ACCOUNT_002");
        error.GetProperty("blockerCount").GetInt32().Should().Be(25);
        var blockers = error.GetProperty("blockers");
        blockers.GetArrayLength().Should().Be(20);
        var first = blockers[0];
        first.GetProperty("type").GetString().Should().Be("order");
        first.GetProperty("id").GetString().Should().StartWith("order-");
        first.GetProperty("status").GetString().Should().Be("funded");
        first.GetProperty("role").GetString().Should().Be("buyer");
    }

    [Fact]
    public async Task StartedButNotFinished_Returns503_WithTheStateAndARetryDelay()
    {
        _repo.FailOnce(nameof(_repo.TombstoneAsync));

        var response = await _function.FunctionHandler(Req(), _context);

        response.StatusCode.Should().Be(503);
        response.Headers["Retry-After"].Should().Be("2");
        var body = Json(response);
        body.GetProperty("deletionState").GetString().Should().Be("in_progress");
        var error = body.GetProperty("error");
        error.GetProperty("code").GetString().Should().Be("ACCOUNT_005");
        error.GetProperty("retryAfterSeconds").GetInt32().Should().Be(2);

        // The client retries the same request (its session is already revoked: no step-up asked).
        var retry = await _function.FunctionHandler(Req(authTime: null), _context);
        retry.StatusCode.Should().Be(200);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("{\"confirm\":false}")]
    [InlineData("{\"confirm\":\"true\"}")]
    [InlineData("{\"confirm\":1}")]
    [InlineData("{\"Confirm\":true}")]
    [InlineData("{\"confirm\":true,\"reason\":\"bye\"}")]
    [InlineData("[true]")]
    [InlineData("null")]
    [InlineData("not json")]
    public async Task AnyBodyButConfirmTrue_Returns400_Account001(string? body)
    {
        var response = await _function.FunctionHandler(Req(body), _context);

        response.StatusCode.Should().Be(400);
        Json(response).GetProperty("error").GetProperty("code").GetString().Should().Be("ACCOUNT_001");
        _repo.UserRow(UserId).Should().NotContainKey("deletionState");
    }

    [Fact]
    public async Task Suspended_Returns403_Account003()
    {
        _repo.UserRow(UserId)["suspended"] = new Amazon.DynamoDBv2.Model.AttributeValue { BOOL = true };

        var response = await _function.FunctionHandler(Req(), _context);

        response.StatusCode.Should().Be(403);
        Json(response).GetProperty("error").GetProperty("code").GetString().Should().Be("ACCOUNT_003");
    }

    [Fact]
    public async Task NoUsersRow_Returns404_Auth013()
    {
        var response = await _function.FunctionHandler(Req(userId: "ghost"), _context);

        response.StatusCode.Should().Be(404);
        Json(response).GetProperty("error").GetProperty("code").GetString().Should().Be("AUTH_013");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PasswordOlderThanFiveMinutes_OrNoAuthTime_Returns403_Auth040(bool hasAuthTime)
    {
        var response = await _function.FunctionHandler(
            Req(authTime: hasAuthTime ? DateTimeOffset.UtcNow.AddSeconds(-301).ToUnixTimeSeconds() : null), _context);

        response.StatusCode.Should().Be(403);
        Json(response).GetProperty("error").GetProperty("code").GetString().Should().Be("AUTH_040");
        _repo.UserRow(UserId).Should().NotContainKey("deletionState");
        await _cognito.DidNotReceiveWithAnyArgs().AdminGlobalSignOutAsync(default!);
    }

    [Fact]
    public async Task WithoutTheCaller_Returns401()
    {
        (await _function.FunctionHandler(Req(userId: null), _context)).StatusCode.Should().Be(401);
    }

    [Fact]
    public async Task AnotherMethod_Returns405()
    {
        (await _function.FunctionHandler(Req(method: "POST"), _context)).StatusCode.Should().Be(405);
    }

    [Fact]
    public async Task AnApiRequest_NeverRunsTheSweep()
    {
        var response = await _function.FunctionHandler(Req("{\"action\":\"sweep\"}"), _context);

        response.StatusCode.Should().Be(400);
        _repo.Count(nameof(_repo.ScanIncompleteAsync)).Should().Be(0);
    }

    [Fact]
    public async Task ScheduledSweep_WithoutRequestContext_RunsTheSweep()
    {
        var response = await _function.FunctionHandler(
            new APIGatewayProxyRequest { Body = "{\"action\":\"sweep\"}" }, _context);

        response.StatusCode.Should().Be(200);
        _repo.Count(nameof(_repo.ScanIncompleteAsync)).Should().Be(1);
        Json(response).GetProperty("finished").GetString().Should().Be("0");
    }

    [Fact]
    public async Task Reapply_TakesTheUserIds_AndSkipsALiveAccount()
    {
        var response = await _function.FunctionHandler(
            new APIGatewayProxyRequest { Body = "{\"action\":\"reapply\",\"userIds\":[\"buyer-1\",\"\"]}" }, _context);

        response.StatusCode.Should().Be(200);
        Json(response).GetProperty("skipped").GetString().Should().Be("1");
        _repo.UserRow(UserId).Should().ContainKey("phoneNumber");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("{\"action\":\"delete_everyone\"}")]
    [InlineData("{oops")]
    public async Task UnknownJob_Returns400(string? body)
    {
        var response = await _function.FunctionHandler(new APIGatewayProxyRequest { Body = body }, _context);

        response.StatusCode.Should().Be(400);
        _repo.Count(nameof(_repo.ScanIncompleteAsync)).Should().Be(0);
    }
}
