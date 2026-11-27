using System.Text.Json;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.TestUtilities;
using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Functions;
using Xunit;

namespace WeredaConnect.Server.Tests.Functions;

/// <summary>
/// SIM-56: <c>GET /intents</c> on the match Lambda, the buyer's own requests
/// (push-alerts-api.md 1.5.0).
/// </summary>
public class MatchFunctionIntentsTests
{
    private readonly ICropRepository _crops = Substitute.For<ICropRepository>();
    private readonly IIntentRepository _intents = Substitute.For<IIntentRepository>();
    private readonly TestLambdaContext _context = new();

    private MatchFunction Function(bool withIntents = true)
        => new(_crops, withIntents ? _intents : null);

    private static APIGatewayProxyRequest Request(
        string? userId = "buyer-1",
        string? role = "buyer",
        string method = "GET",
        Dictionary<string, string>? query = null)
    {
        var authorizer = new APIGatewayCustomAuthorizerContext();
        if (userId != null)
        {
            authorizer["sub"] = userId;
        }

        if (role != null)
        {
            authorizer["custom:role"] = role;
        }

        return new APIGatewayProxyRequest
        {
            HttpMethod = method,
            Resource = "/intents",
            Path = "/intents",
            QueryStringParameters = query,
            RequestContext = new APIGatewayProxyRequest.ProxyRequestContext { Authorizer = authorizer },
        };
    }

    private static Dictionary<string, object> Row(
        string intentId,
        string buyerId = "buyer-1",
        string? status = "active",
        string createdAt = "2026-11-01T08:00:00.0000000Z")
    {
        var row = new Dictionary<string, object>
        {
            ["buyerId"] = buyerId,
            ["intentId"] = intentId,
            ["cropType"] = "teff",
            ["quantityNeeded"] = 12.5m,
            ["maxPricePerKg"] = 95m,
            ["deliveryWoreda"] = "Adama",
            ["deliveryLatitude"] = 8.54m,
            ["deliveryLongitude"] = 39.27m,
            ["neededByDate"] = "2026-12-01T00:00:00.000Z",
            ["createdAt"] = createdAt,
            ["updatedAt"] = createdAt,
            ["version"] = 2m,
        };
        if (status != null)
        {
            row["status"] = status;
        }

        return row;
    }

    private static JsonElement Body(APIGatewayProxyResponse response)
        => JsonDocument.Parse(response.Body).RootElement;

    [Fact]
    public async Task Buyer_GetsOwnActiveIntents_NewestFirst_WithEveryField()
    {
        _intents.QueryByBuyerAsync("buyer-1").Returns(new List<Dictionary<string, object>>
        {
            Row("old", createdAt: "2026-10-01T08:00:00.0000000Z"),
            Row("new", createdAt: "2026-11-05T08:00:00.0000000Z"),
        });

        var response = await Function().FunctionHandler(Request(), _context);

        response.StatusCode.Should().Be(200);
        var body = Body(response);
        body.GetProperty("count").GetInt32().Should().Be(2);
        var intents = body.GetProperty("intents");
        intents[0].GetProperty("intentId").GetString().Should().Be("new");
        intents[1].GetProperty("intentId").GetString().Should().Be("old");

        var first = intents[0];
        first.GetProperty("cropType").GetString().Should().Be("teff");
        first.GetProperty("quantityNeeded").GetDouble().Should().Be(12.5);
        first.GetProperty("maxPricePerKg").GetDouble().Should().Be(95);
        first.GetProperty("deliveryWoreda").GetString().Should().Be("Adama");
        first.GetProperty("deliveryLatitude").GetDouble().Should().Be(8.54);
        first.GetProperty("deliveryLongitude").GetDouble().Should().Be(39.27);
        first.GetProperty("neededByDate").GetString().Should().Be("2026-12-01T00:00:00.000Z");
        first.GetProperty("status").GetString().Should().Be("active");
        first.GetProperty("version").GetInt32().Should().Be(2);
        // Every row is the caller's: the id is not repeated.
        first.TryGetProperty("buyerId", out _).Should().BeFalse();
    }

    [Fact]
    public async Task ReadsOnlyTheCallersPartition_IgnoringAnyBuyerIdParameter()
    {
        _intents.QueryByBuyerAsync(Arg.Any<string>()).Returns(new List<Dictionary<string, object>>());

        var response = await Function().FunctionHandler(
            Request(query: new Dictionary<string, string> { ["buyerId"] = "buyer-2" }), _context);

        response.StatusCode.Should().Be(200);
        await _intents.Received(1).QueryByBuyerAsync("buyer-1");
        await _intents.DidNotReceive().QueryByBuyerAsync("buyer-2");
    }

    [Fact]
    public async Task RowOfAnotherBuyer_IsNeverReturned()
    {
        _intents.QueryByBuyerAsync("buyer-1").Returns(new List<Dictionary<string, object>>
        {
            Row("mine"),
            Row("theirs", buyerId: "buyer-2"),
        });

        var response = await Function().FunctionHandler(Request(), _context);

        var intents = Body(response).GetProperty("intents");
        intents.GetArrayLength().Should().Be(1);
        intents[0].GetProperty("intentId").GetString().Should().Be("mine");
    }

    [Fact]
    public async Task OnlyActiveRows_ARowWithoutStatusCountsAsActive()
    {
        _intents.QueryByBuyerAsync("buyer-1").Returns(new List<Dictionary<string, object>>
        {
            Row("active"),
            Row("no-status", status: null),
            Row("fulfilled", status: "fulfilled"),
            Row("expired", status: "expired"),
        });

        var response = await Function().FunctionHandler(Request(), _context);

        var ids = Body(response).GetProperty("intents").EnumerateArray()
            .Select(i => i.GetProperty("intentId").GetString()).ToList();
        ids.Should().BeEquivalentTo(new[] { "active", "no-status" });
    }

    [Fact]
    public async Task RowWithoutIdOrCrop_IsLeftOut()
    {
        var noCrop = Row("no-crop");
        noCrop.Remove("cropType");
        _intents.QueryByBuyerAsync("buyer-1").Returns(new List<Dictionary<string, object>> { Row("ok"), noCrop });

        var response = await Function().FunctionHandler(Request(), _context);

        Body(response).GetProperty("count").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task NoIntents_IsAnEmptyList()
    {
        _intents.QueryByBuyerAsync("buyer-1").Returns(new List<Dictionary<string, object>>());

        var response = await Function().FunctionHandler(Request(), _context);

        response.StatusCode.Should().Be(200);
        Body(response).GetProperty("intents").GetArrayLength().Should().Be(0);
    }

    [Theory]
    [InlineData("farmer")]
    [InlineData("trucker")]
    [InlineData("admin")]
    [InlineData(null)]
    public async Task NonBuyer_Is403_AndReadsNothing(string? role)
    {
        var response = await Function().FunctionHandler(Request(role: role), _context);

        response.StatusCode.Should().Be(403);
        response.Body.Should().Contain("INTENT_FORBIDDEN");
        await _intents.DidNotReceive().QueryByBuyerAsync(Arg.Any<string>());
    }

    [Fact]
    public async Task NoUser_Is401()
    {
        var response = await Function().FunctionHandler(Request(userId: null), _context);

        response.StatusCode.Should().Be(401);
        response.Body.Should().Contain("INTENT_UNAUTHORIZED");
    }

    [Fact]
    public async Task NoIntentsTable_Is503()
    {
        var response = await Function(withIntents: false).FunctionHandler(Request(), _context);

        response.StatusCode.Should().Be(503);
        response.Body.Should().Contain("INTENT_UNAVAILABLE");
    }

    [Fact]
    public async Task ReadFailure_Is500()
    {
        _intents.QueryByBuyerAsync("buyer-1").ThrowsAsync(new InvalidOperationException("boom"));

        var response = await Function().FunctionHandler(Request(), _context);

        response.StatusCode.Should().Be(500);
        response.Body.Should().Contain("INTENT_SERVER_ERROR");
    }

    [Fact]
    public async Task OtherMethod_Is405()
    {
        var response = await Function().FunctionHandler(Request(method: "POST"), _context);

        response.StatusCode.Should().Be(405);
    }

    [Fact]
    public async Task MatchRoute_StillMatches_AndNeverReadsIntents()
    {
        var request = Request(query: new Dictionary<string, string>());
        request.Resource = "/match";
        request.Path = "/match";

        var response = await Function().FunctionHandler(request, _context);

        // No cropType: the match route's own validation answers, not the intents route.
        response.StatusCode.Should().Be(400);
        response.Body.Should().Contain("MATCH_VALIDATION");
        await _intents.DidNotReceive().QueryByBuyerAsync(Arg.Any<string>());
    }
}
