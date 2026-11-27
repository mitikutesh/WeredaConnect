using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.TestUtilities;
using FluentAssertions;
using NSubstitute;
using WeredaConnect.Server.Functions;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.Tests.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Functions;

/// <summary>
/// GCR-07 (reputation-api.md 1.12.0 section 9.5): <c>GET /admin/transactions/{orderId}</c> carries the conduct stamp
/// of each party of the order, read whatever <c>conductRewards</c> says, so an admin can see why the order scored as it
/// did for the farmer, the trucker and the buyer.
/// </summary>
public class AdminConductTests
{
    private const string UsersTable = "users-test";
    private const string OrdersTable = "orders-test";
    private const string EscrowsTable = "escrows-test";
    private const string DeliveriesTable = "deliveries-test";
    private const string AuditTable = "escrow-audit-test";
    private static readonly DateTime Settled = new(2026, 11, 20, 10, 0, 0, DateTimeKind.Utc);

    private readonly FakeConductStamps _stamps = new();
    private readonly TestLambdaContext _context = new() { Logger = new TestLambdaLogger() };

    [Fact]
    public async Task GetTransaction_ShowsEachPartysStamp_FarmerTruckerBuyer()
    {
        Stamp("farmer-1", "farmer", 3, 2, new ConductStep("as_described", "earned"),
            new ConductStep("pickup_scanned", "earned"), new ConductStep("pickup_photo", "missed"));
        Stamp("trucker-1", "trucker", 2, 1, new ConductStep("pickup_scanned", "earned"),
            new ConductStep("pickup_fix", "left_out", "no_signal"), new ConductStep("dropoff_photo", "missed"));
        _stamps.Rows[("buyer-1", "order-1")] =
            Stamp("buyer-1", "buyer", 2, 2, new ConductStep("dropoff_scanned", "earned"), new ConductStep("rated_parties", "earned"))
                with
            { Counted = false, NotCountedReason = "pair_cap", ReevaluatedAt = Settled.AddDays(1) };

        var conduct = (await Detail(truckerId: "trucker-1")).GetProperty("conduct");

        var parties = conduct.GetProperty("parties").EnumerateArray().ToList();
        parties.Select(p => p.GetProperty("party").GetString()).Should().Equal("farmer", "trucker", "buyer");
        conduct.TryGetProperty("readFailed", out _).Should().BeFalse();

        var farmer = parties[0];
        farmer.GetProperty("userId").GetString().Should().Be("farmer-1");
        farmer.GetProperty("offered").GetInt32().Should().Be(3);
        farmer.GetProperty("earned").GetInt32().Should().Be(2);
        farmer.GetProperty("counted").GetBoolean().Should().BeTrue();
        farmer.TryGetProperty("notCountedReason", out _).Should().BeFalse();
        farmer.TryGetProperty("reevaluatedAt", out _).Should().BeFalse();
        farmer.GetProperty("occurredAt").GetString().Should().Be(ReputationPolicy.Iso(Settled));
        farmer.GetProperty("recordedAt").GetString().Should().Be(ReputationPolicy.Iso(Settled.AddSeconds(5)));
        farmer.GetProperty("steps").EnumerateArray().Select(s => s.GetProperty("result").GetString())
            .Should().Equal("earned", "earned", "missed");

        var leftOut = parties[1].GetProperty("steps").EnumerateArray()
            .Single(s => s.GetProperty("id").GetString() == "pickup_fix");
        leftOut.GetProperty("result").GetString().Should().Be("left_out");
        leftOut.GetProperty("reason").GetString().Should().Be("no_signal");
        parties[1].GetProperty("steps").EnumerateArray()
            .Single(s => s.GetProperty("id").GetString() == "pickup_scanned").TryGetProperty("reason", out _)
            .Should().BeFalse("only a left-out step carries a reason");

        var buyer = parties[2];
        buyer.GetProperty("counted").GetBoolean().Should().BeFalse();
        buyer.GetProperty("notCountedReason").GetString().Should().Be("pair_cap");
        buyer.GetProperty("reevaluatedAt").GetString().Should().Be(ReputationPolicy.Iso(Settled.AddDays(1)));
        _stamps.Gets.Should().Be(3, "one read per party");
    }

    [Fact]
    public async Task GetTransaction_ASelfPickup_ReadsNoTruckerStamp()
    {
        Stamp("farmer-1", "farmer", 1, 1, new ConductStep("as_described", "earned"));
        Stamp("buyer-1", "buyer", 1, 0, new ConductStep("rated_parties", "missed"));

        var conduct = (await Detail(truckerId: null)).GetProperty("conduct");

        conduct.GetProperty("parties").EnumerateArray().Select(p => p.GetProperty("party").GetString())
            .Should().Equal("farmer", "buyer");
        _stamps.Gets.Should().Be(2);
    }

    [Fact]
    public async Task GetTransaction_BeforeTheSettle_HasAnEmptyList()
    {
        var conduct = (await Detail(truckerId: "trucker-1", status: "in_transit")).GetProperty("conduct");

        conduct.GetProperty("parties").GetArrayLength().Should().Be(0);
        conduct.TryGetProperty("readFailed", out _).Should().BeFalse();
    }

    [Fact]
    public async Task GetTransaction_AFailedStampRead_SaysSo_AndStillShowsTheRest()
    {
        Stamp("farmer-1", "farmer", 1, 1, new ConductStep("as_described", "earned"));
        Stamp("buyer-1", "buyer", 1, 1, new ConductStep("rated_parties", "earned"));
        _stamps.ThrowOnGetFor.Add("trucker-1");

        var body = await Detail(truckerId: "trucker-1");

        var conduct = body.GetProperty("conduct");
        conduct.GetProperty("readFailed").GetBoolean().Should().BeTrue();
        conduct.GetProperty("parties").EnumerateArray().Select(p => p.GetProperty("party").GetString())
            .Should().Equal("farmer", "buyer");
        body.GetProperty("order").GetProperty("orderId").GetString().Should().Be("order-1");
        ((TestLambdaLogger)_context.Logger).Buffer.ToString().Should()
            .Contain(AdminFunction.ConductStampReadFailedToken).And.Contain("party=trucker").And.NotContain("trucker-1");
    }

    [Fact]
    public async Task GetTransaction_WithoutTheReputationTable_HasNoConduct()
    {
        var response = await new AdminFunction(OrderDb(null, "settled"), UsersTable, OrdersTable, EscrowsTable, DeliveriesTable, AuditTable)
        {
            ReputationEventsTable = null
        }
            .FunctionHandler(AdminGet("/admin/transactions/order-1"), _context);

        response.StatusCode.Should().Be(200, response.Body);
        JsonDocument.Parse(response.Body).RootElement.TryGetProperty("conduct", out _).Should().BeFalse();
    }

    private ConductStamp Stamp(string userId, string party, int offered, int earned, params ConductStep[] steps)
    {
        var stamp = new ConductStamp
        {
            UserId = userId,
            OrderId = "order-1",
            Party = party,
            FarmerId = "farmer-1",
            TruckerId = "trucker-1",
            BuyerId = "buyer-1",
            Offered = offered,
            Earned = earned,
            Steps = steps,
            Counted = true,
            OccurredAt = Settled,
            RecordedAt = Settled.AddSeconds(5)
        };
        _stamps.Rows[(userId, "order-1")] = stamp;
        return stamp;
    }

    private async Task<JsonElement> Detail(string? truckerId, string status = "settled")
    {
        var response = await new AdminFunction(OrderDb(truckerId, status), UsersTable, OrdersTable, EscrowsTable, DeliveriesTable, AuditTable)
        {
            ConductStamps = _stamps
        }
            .FunctionHandler(AdminGet("/admin/transactions/order-1"), _context);

        response.StatusCode.Should().Be(200, response.Body);
        return JsonDocument.Parse(response.Body).RootElement.Clone();
    }

    private static IAmazonDynamoDB OrderDb(string? truckerId, string status)
    {
        var order = new Dictionary<string, AttributeValue>
        {
            ["orderId"] = S("order-1"),
            ["buyerId"] = S("buyer-1"),
            ["farmerId"] = S("farmer-1"),
            ["cropType"] = S("teff"),
            ["quantity"] = new() { N = "100" },
            ["orderTotal"] = new() { N = "5000" },
            ["status"] = S(status),
            ["createdAt"] = S("2026-11-18T08:00:00Z"),
            ["updatedAt"] = S("2026-11-20T10:00:00Z")
        };
        if (truckerId != null)
        {
            order["truckerId"] = S(truckerId);
            order["tripId"] = S("trip-1");
        }

        var db = Substitute.For<IAmazonDynamoDB>();
        db.QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(ci => new QueryResponse
            {
                Items = ci.Arg<QueryRequest>() is { IndexName: null, TableName: OrdersTable } ? new() { order } : new(),
                LastEvaluatedKey = new Dictionary<string, AttributeValue>()
            });
        return db;
    }

    private static AttributeValue S(string v) => new() { S = v };

    private static APIGatewayProxyRequest AdminGet(string path) => new()
    {
        HttpMethod = "GET",
        Path = path,
        RequestContext = new APIGatewayProxyRequest.ProxyRequestContext
        {
            Authorizer = new APIGatewayCustomAuthorizerContext
            {
                { "sub", "admin-1" },
                { "role", "admin" },
                { "cognito:groups", "admin" }
            }
        }
    };
}
