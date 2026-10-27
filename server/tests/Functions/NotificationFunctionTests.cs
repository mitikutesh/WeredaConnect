using System.Text.Json;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.TestUtilities;
using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Functions;
using WeredaConnect.Server.Helpers;
using Xunit;

namespace WeredaConnect.Server.Tests.Functions;

/// <summary>
/// ESC-21: <c>GET /notifications</c> reads only the caller's inbox, newest first, with a signed
/// cursor and an incremental <c>since</c>.
/// </summary>
public class NotificationFunctionTests
{
    private readonly INotificationRepository _repo = Substitute.For<INotificationRepository>();
    private readonly TestLambdaContext _context = new();
    private readonly NotificationFunction _function;

    private static readonly DateTime Now = new(2026, 10, 26, 14, 0, 0, DateTimeKind.Utc);

    public NotificationFunctionTests()
    {
        _function = new NotificationFunction(_repo, new FixedClock(Now));
        _repo.QueryByUserAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<Dictionary<string, AttributeValue>?>(), Arg.Any<string?>())
            .Returns((new List<NotificationRow> { Row() }, (Dictionary<string, AttributeValue>?)null));
    }

    private static NotificationRow Row() => new(
        "farmer-1",
        "2026-10-26T12:00:00.0000000Z#order_request#order-1",
        "order_request",
        "order",
        "order-1",
        "order-1",
        new Dictionary<string, string> { ["buyerName"] = "Meron Alemu" },
        "2026-10-26T12:00:00.0000000Z",
        1_800_000_000);

    private static APIGatewayProxyRequest Request(string? userId = "farmer-1", Dictionary<string, string>? query = null, string method = "GET")
    {
        var authorizer = new APIGatewayCustomAuthorizerContext();
        if (userId != null)
        {
            authorizer["userId"] = userId;
        }

        return new APIGatewayProxyRequest
        {
            HttpMethod = method,
            Path = "/notifications",
            QueryStringParameters = query,
            RequestContext = new APIGatewayProxyRequest.ProxyRequestContext { Authorizer = authorizer }
        };
    }

    [Fact]
    public async Task ReturnsTheCallersInbox_WithTheRowKeyAsId()
    {
        var response = await _function.FunctionHandler(Request(), _context);

        response.StatusCode.Should().Be(200);
        await _repo.Received(1).QueryByUserAsync("farmer-1", NotificationFunction.DefaultLimit, null, null);
        var body = JsonDocument.Parse(response.Body).RootElement;
        var first = body.GetProperty("notifications")[0];
        first.GetProperty("notificationId").GetString().Should().Be("2026-10-26T12:00:00.0000000Z#order_request#order-1");
        first.GetProperty("type").GetString().Should().Be("order_request");
        first.GetProperty("orderId").GetString().Should().Be("order-1");
        first.GetProperty("params").GetProperty("buyerName").GetString().Should().Be("Meron Alemu");
        body.TryGetProperty("nextToken", out _).Should().BeFalse();
        body.GetProperty("cursor").GetString().Should().Be("2026-10-26T14:00:00.0000000Z");
    }

    [Fact]
    public async Task WithoutAUser_IsUnauthorized()
    {
        var response = await _function.FunctionHandler(Request(userId: null), _context);

        response.StatusCode.Should().Be(401);
        response.Body.Should().Contain("NOTIFICATION_UNAUTHORIZED");
        await _repo.DidNotReceive().QueryByUserAsync(Arg.Any<string>(), Arg.Any<int>(),
            Arg.Any<Dictionary<string, AttributeValue>?>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task OtherMethods_AreRefused()
    {
        var response = await _function.FunctionHandler(Request(method: "POST"), _context);

        response.StatusCode.Should().Be(405);
    }

    [Theory]
    [InlineData("500", 50)]
    [InlineData("0", 1)]
    [InlineData("7", 7)]
    public async Task Limit_IsClamped(string limit, int expected)
    {
        await _function.FunctionHandler(Request(query: new() { ["limit"] = limit }), _context);

        await _repo.Received(1).QueryByUserAsync("farmer-1", expected, null, null);
    }

    [Fact]
    public async Task Limit_NotANumber_IsAValidationError()
    {
        var response = await _function.FunctionHandler(Request(query: new() { ["limit"] = "lots" }), _context);

        response.StatusCode.Should().Be(400);
        response.Body.Should().Contain("NOTIFICATION_VALIDATION");
    }

    [Fact]
    public async Task TamperedToken_IsAValidationError()
    {
        var response = await _function.FunctionHandler(Request(query: new() { ["nextToken"] = "bm90LWEtdG9rZW4" }), _context);

        response.StatusCode.Should().Be(400);
        response.Body.Should().Contain("NOTIFICATION_VALIDATION");
    }

    [Fact]
    public async Task NextToken_RoundTrips()
    {
        var lastKey = new Dictionary<string, AttributeValue>
        {
            ["userId"] = new() { S = "farmer-1" },
            ["sortKey"] = new() { S = "k-2" }
        };
        _repo.QueryByUserAsync("farmer-1", Arg.Any<int>(), null, Arg.Any<string?>())
            .Returns((new List<NotificationRow> { Row() }, lastKey));

        var first = await _function.FunctionHandler(Request(), _context);
        var token = JsonDocument.Parse(first.Body).RootElement.GetProperty("nextToken").GetString();
        await _function.FunctionHandler(Request(query: new() { ["nextToken"] = token! }), _context);

        await _repo.Received(1).QueryByUserAsync("farmer-1", Arg.Any<int>(),
            Arg.Is<Dictionary<string, AttributeValue>?>(k => k != null && k["sortKey"].S == "k-2"), Arg.Any<string?>());
    }

    [Fact]
    public async Task AnotherUsersToken_IsAValidationError()
    {
        var token = PaginationTokenHelper.EncodeNextToken(new Dictionary<string, AttributeValue>
        {
            ["userId"] = new() { S = "buyer-9" },
            ["sortKey"] = new() { S = "k-2" }
        });

        var response = await _function.FunctionHandler(Request(query: new() { ["nextToken"] = token }), _context);

        response.StatusCode.Should().Be(400);
        response.Body.Should().Contain("NOTIFICATION_VALIDATION");
    }

    [Fact]
    public async Task ATokenFromAnotherEndpoint_IsAValidationError()
    {
        var token = PaginationTokenHelper.EncodeNextToken(new Dictionary<string, AttributeValue>
        {
            ["userId"] = new() { S = "farmer-1" },
            ["sortKey"] = new() { S = "k-2" },
            ["messageId"] = new() { S = "m-1" }
        });

        var response = await _function.FunctionHandler(Request(query: new() { ["nextToken"] = token }), _context);

        response.StatusCode.Should().Be(400);
    }

    [Fact]
    public async Task IncrementalPull_PagesWithSinceAndAnIndexToken_ButRefusesATokenOutsideTheWindow()
    {
        var since = "2026-10-26T12:00:00.0000000Z";
        var after = PaginationTokenHelper.EncodeNextToken(new Dictionary<string, AttributeValue>
        {
            ["userId"] = new() { S = "farmer-1" },
            ["sortKey"] = new() { S = "2026-10-26T09:00:00.0000000Z#x#z" },
            ["writtenAt"] = new() { S = "2026-10-26T12:30:00.0000000Z" }
        });
        var before = PaginationTokenHelper.EncodeNextToken(new Dictionary<string, AttributeValue>
        {
            ["userId"] = new() { S = "farmer-1" },
            ["sortKey"] = new() { S = "2026-10-26T09:00:00.0000000Z#x#z" },
            ["writtenAt"] = new() { S = "2026-10-26T11:00:00.0000000Z" }
        });
        var tableToken = PaginationTokenHelper.EncodeNextToken(new Dictionary<string, AttributeValue>
        {
            ["userId"] = new() { S = "farmer-1" },
            ["sortKey"] = new() { S = "2026-10-26T13:00:00.0000000Z#x#z" }
        });

        var ok = await _function.FunctionHandler(Request(query: new() { ["since"] = since, ["nextToken"] = after }), _context);
        var outside = await _function.FunctionHandler(Request(query: new() { ["since"] = since, ["nextToken"] = before }), _context);
        var wrongQuery = await _function.FunctionHandler(Request(query: new() { ["since"] = since, ["nextToken"] = tableToken }), _context);

        ok.StatusCode.Should().Be(200);
        await _repo.Received(1).QueryByUserAsync("farmer-1", NotificationFunction.DefaultLimit,
            Arg.Is<Dictionary<string, AttributeValue>?>(k => k != null && k["writtenAt"].S == "2026-10-26T12:30:00.0000000Z"),
            "2026-10-26T11:58:00.0000000Z");
        outside.StatusCode.Should().Be(400);
        wrongQuery.StatusCode.Should().Be(400);
    }

    [Theory]
    [InlineData("2026-10-26T12:00:00Z#order_request#order-1")]
    [InlineData("yesterday")]
    public async Task Since_NotACursor_IsAValidationError(string since)
    {
        var response = await _function.FunctionHandler(Request(query: new() { ["since"] = since }), _context);

        response.StatusCode.Should().Be(400);
        response.Body.Should().Contain("NOTIFICATION_VALIDATION");
    }

    [Fact]
    public async Task OverlongSince_IsAValidationError()
    {
        var response = await _function.FunctionHandler(
            Request(query: new() { ["since"] = new string('x', NotificationFunction.MaxSinceLength + 1) }), _context);

        response.StatusCode.Should().Be(400);
        await _repo.DidNotReceive().QueryByUserAsync(Arg.Any<string>(), Arg.Any<int>(),
            Arg.Any<Dictionary<string, AttributeValue>?>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task Since_ReadsRowsWrittenAfterTheCursor_LessTheOverlap()
    {
        // A row stamped just before the client's last read, or by a Lambda whose clock runs a little
        // behind, is still returned; the client drops the ids it already has.
        await _function.FunctionHandler(Request(query: new() { ["since"] = "2026-10-26T12:00:00.0000000Z" }), _context);

        await _repo.Received(1).QueryByUserAsync("farmer-1", NotificationFunction.DefaultLimit, null, "2026-10-26T11:58:00.0000000Z");
    }

    [Fact]
    public async Task RepositoryFailure_IsAServerError()
    {
        _repo.QueryByUserAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<Dictionary<string, AttributeValue>?>(), Arg.Any<string?>())
            .ThrowsAsync(new InvalidOperationException("dynamo down"));

        var response = await _function.FunctionHandler(Request(), _context);

        response.StatusCode.Should().Be(500);
        response.Body.Should().Contain("NOTIFICATION_SERVER_ERROR");
    }

    private sealed class FixedClock : TimeProvider
    {
        private readonly DateTimeOffset _now;

        public FixedClock(DateTime nowUtc) => _now = new DateTimeOffset(nowUtc);

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
