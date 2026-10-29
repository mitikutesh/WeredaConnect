using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.TestUtilities;
using FluentAssertions;
using NSubstitute;
using WeredaConnect.Server.Functions;
using Xunit;

namespace WeredaConnect.Server.Tests.Functions;

/// <summary>
/// LT-13: GET /admin/deliveries/{id}/trail through the real handler and repositories over a
/// substituted DynamoDB client: who may read it (the admin group only), the 404s, the response shape
/// and that the read writes nothing. The window, legs, pauses and gaps are in AdminTrailTests.
/// </summary>
public class AdminFunctionTrailTests
{
    private const string UsersTable = "users-test";
    private const string DeliveriesTable = "deliveries-test";
    private const string TripLocationsTable = "trip-locations-test";
    private const string DeliveryId = "del-101";
    private const string TripId = "trip-101";
    private const string ForbiddenBody = "{\"error\":{\"code\":\"ADMIN_001\",\"message\":\"Admin access required.\"}}";
    private const string NotFoundBody = "{\"error\":{\"code\":\"ADMIN_018\",\"message\":\"Delivery not found.\"}}";

    private static readonly DateTime Now = new(2026, 10, 27, 9, 10, 0, DateTimeKind.Utc);

    private readonly IAmazonDynamoDB _db = Substitute.For<IAmazonDynamoDB>();
    private readonly TestLambdaContext _context = new();

    public AdminFunctionTrailTests()
    {
        DeliveryReturns(Delivery("disputed"));
        _db.QueryAsync(Arg.Is<QueryRequest>(r => r.TableName == TripLocationsTable), Arg.Any<CancellationToken>())
            .Returns(new QueryResponse
            {
                Items = new List<Dictionary<string, AttributeValue>>
                {
                    FixRow("2026-10-27T06:00:00.000Z", "8.1", "35"),
                    FixRow("2026-10-27T06:30:00.000Z", "9.1", "1500"),
                    EventRow("2026-10-27T06:40:00.000Z", "paused", "2026-10-27T07:10:00.000Z"),
                    FixRow("2026-10-27T07:00:00.000Z", "8.35", "35"),
                    FixRow("2026-10-27T07:20:00.000Z", "9.35", "35")
                }
            });
    }

    private AdminFunction Function(string tripLocationsTable = TripLocationsTable) =>
        new(_db, UsersTable, "", "", DeliveriesTable, "", tripLocationsTable: tripLocationsTable) { Clock = () => Now };

    // ===== Who may read it =====

    [Fact]
    public async Task AnAdminInTheAdminGroup_GetsTheTrail()
    {
        var response = await Function().FunctionHandler(Request(AdminGroup()), _context);

        response.StatusCode.Should().Be(200);
        var trail = JsonDocument.Parse(response.Body).RootElement;
        trail.GetProperty("deliveryId").GetString().Should().Be(DeliveryId);
        trail.GetProperty("tripId").GetString().Should().Be(TripId);
        trail.GetProperty("status").GetString().Should().Be("disputed");
        trail.GetProperty("points").GetArrayLength().Should().Be(5);
    }

    [Fact]
    public async Task ARoleClaimAdminOutsideTheAdminGroup_GetsTheAdminRefusal_AndNothingIsRead()
    {
        // A role claim can be set on a marketplace profile; the group only in the admin pool.
        var response = await Function().FunctionHandler(
            Request(new APIGatewayCustomAuthorizerContext { { "sub", "admin-1" }, { "role", "admin" } }), _context);

        response.StatusCode.Should().Be(403);
        response.Body.Should().Be(ForbiddenBody);
        await _db.DidNotReceiveWithAnyArgs().QueryAsync(default!, default);
    }

    [Theory]
    [InlineData("buyer")]
    [InlineData("farmer")]
    [InlineData("trucker")]
    public async Task AMarketplaceUser_GetsTheAdminRefusal(string role)
    {
        var response = await Function().FunctionHandler(
            Request(new APIGatewayCustomAuthorizerContext { { "sub", "user-1" }, { "role", role } }), _context);

        response.StatusCode.Should().Be(403);
        response.Body.Should().Be(ForbiddenBody);
        await _db.DidNotReceiveWithAnyArgs().QueryAsync(default!, default);
    }

    [Fact]
    public async Task WithoutAToken_GetsTheAdminRefusal()
    {
        var request = Request(AdminGroup());
        request.RequestContext = null;

        var response = await Function().FunctionHandler(request, _context);

        response.StatusCode.Should().Be(403);
        response.Body.Should().Be(ForbiddenBody);
    }

    // ===== Not found and not configured =====

    [Fact]
    public async Task AnUnknownDelivery_Is404_AndNoCheckInIsRead()
    {
        DeliveryReturns(null);

        var response = await Function().FunctionHandler(Request(AdminGroup()), _context);

        response.StatusCode.Should().Be(404);
        response.Body.Should().Be(NotFoundBody);
        await _db.DidNotReceive().QueryAsync(
            Arg.Is<QueryRequest>(r => r.TableName == TripLocationsTable), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ASoftDeletedDelivery_Is404()
    {
        var deleted = Delivery("disputed");
        deleted["deletedAt"] = new AttributeValue { S = "2026-10-27T08:00:00Z" };
        DeliveryReturns(deleted);

        var response = await Function().FunctionHandler(Request(AdminGroup()), _context);

        response.StatusCode.Should().Be(404);
        response.Body.Should().Be(NotFoundBody);
    }

    [Fact]
    public async Task WithoutTheTripLocationsTable_Is503()
    {
        var response = await Function(tripLocationsTable: "").FunctionHandler(Request(AdminGroup()), _context);

        response.StatusCode.Should().Be(503);
        response.Body.Should().Contain("ADMIN_004");
    }

    // ===== What it reads and answers =====

    [Fact]
    public async Task TheTrail_IsReadOnly()
    {
        await Function().FunctionHandler(Request(AdminGroup()), _context);

        await _db.DidNotReceiveWithAnyArgs().PutItemAsync(default(PutItemRequest)!, default);
        await _db.DidNotReceiveWithAnyArgs().UpdateItemAsync(default(UpdateItemRequest)!, default);
        await _db.DidNotReceiveWithAnyArgs().DeleteItemAsync(default(DeleteItemRequest)!, default);
        await _db.DidNotReceiveWithAnyArgs().TransactWriteItemsAsync(default(TransactWriteItemsRequest)!, default);
        await _db.DidNotReceiveWithAnyArgs().BatchWriteItemAsync(default(BatchWriteItemRequest)!, default);
        // The trip's rows, by the trip's key.
        await _db.Received(1).QueryAsync(
            Arg.Is<QueryRequest>(r => r.TableName == TripLocationsTable && r.ExpressionAttributeValues[":t"].S == TripId),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TheAnswer_CarriesThePointsAt4Decimals_TheLegsWithTheJumpFlag_ThePausesAndTheSummary()
    {
        var response = await Function().FunctionHandler(Request(AdminGroup()), _context);
        var trail = JsonDocument.Parse(response.Body).RootElement;

        trail.GetProperty("from").GetString().Should().Be("2026-10-27T05:05:00.000Z");
        trail.GetProperty("until").GetString().Should().Be("2026-10-27T08:00:00.000Z");
        trail.GetProperty("open").GetBoolean().Should().BeFalse();
        trail.GetProperty("pickup").GetProperty("latitude").GetDouble().Should().Be(8.1504);
        trail.GetProperty("dropoff").GetProperty("at").GetString().Should().Be("2026-10-27T08:00:00.000Z");

        var points = trail.GetProperty("points");
        var coarse = points[1];
        coarse.GetProperty("kind").GetString().Should().Be("fix");
        coarse.GetProperty("coarse").GetBoolean().Should().BeTrue();
        coarse.GetProperty("accuracyM").GetDouble().Should().Be(1500);
        coarse.GetProperty("mocked").GetBoolean().Should().BeFalse();
        var paused = points[2];
        paused.GetProperty("kind").GetString().Should().Be("paused");
        paused.GetProperty("pausedUntil").GetString().Should().Be("2026-10-27T07:10:00.000Z");
        // An event carries no position; the nulls are written, as the contract shows them.
        paused.GetProperty("latitude").ValueKind.Should().Be(JsonValueKind.Null);
        paused.GetProperty("source").ValueKind.Should().Be(JsonValueKind.Null);

        // The coarse fix is no end of a leg: 06:00 to 07:00, then the jump 07:00 to 07:20.
        var legs = trail.GetProperty("legs");
        legs.GetArrayLength().Should().Be(2);
        legs[0].GetProperty("fromCapturedAt").GetString().Should().Be("2026-10-27T06:00:00.000Z");
        legs[0].GetProperty("toCapturedAt").GetString().Should().Be("2026-10-27T07:00:00.000Z");
        legs[0].GetProperty("distanceKm").GetDouble().Should().Be(27.8);
        legs[0].GetProperty("minutes").GetDouble().Should().Be(60);
        legs[0].GetProperty("kmh").GetDouble().Should().Be(27.8);
        legs[0].GetProperty("jump").GetBoolean().Should().BeFalse();
        legs[1].GetProperty("jump").GetBoolean().Should().BeTrue();

        var pause = trail.GetProperty("pauses").EnumerateArray().Single();
        pause.GetProperty("from").GetString().Should().Be("2026-10-27T06:40:00.000Z");
        pause.GetProperty("until").GetString().Should().Be("2026-10-27T07:10:00.000Z");

        var summary = trail.GetProperty("summary");
        summary.GetProperty("jumps").GetInt32().Should().Be(1);
        summary.GetProperty("coarseFixes").GetInt32().Should().Be(1);
        summary.GetProperty("fixes").GetInt32().Should().Be(4);
        // Sharing says nothing about the jump: the phone checked in all the way.
        summary.GetProperty("sharedWholeWay").GetBoolean().Should().BeTrue();
        summary.GetProperty("gaps").GetInt32().Should().Be(0);
        trail.GetProperty("gaps").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task BeforePickup_TheCheckInsAreNotRead()
    {
        var pending = Delivery("pending");
        pending.Remove("pickedUpAt");
        pending.Remove("confirmedAt");
        DeliveryReturns(pending);

        var response = await Function().FunctionHandler(Request(AdminGroup()), _context);

        response.StatusCode.Should().Be(200);
        var trail = JsonDocument.Parse(response.Body).RootElement;
        trail.GetProperty("from").ValueKind.Should().Be(JsonValueKind.Null);
        trail.GetProperty("points").GetArrayLength().Should().Be(0);
        await _db.DidNotReceive().QueryAsync(
            Arg.Is<QueryRequest>(r => r.TableName == TripLocationsTable), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ASelfPickup_HasNoTrip_AndNoCheckInIsRead()
    {
        var self = Delivery("delivered");
        self["deliveryType"] = new AttributeValue { S = "self_pickup" };
        self["tripId"] = new AttributeValue { S = DeliveryId };
        self.Remove("truckerId");
        DeliveryReturns(self);

        var response = await Function().FunctionHandler(Request(AdminGroup()), _context);

        response.StatusCode.Should().Be(200);
        JsonDocument.Parse(response.Body).RootElement.GetProperty("tripId").ValueKind.Should().Be(JsonValueKind.Null);
        await _db.DidNotReceive().QueryAsync(
            Arg.Is<QueryRequest>(r => r.TableName == TripLocationsTable), Arg.Any<CancellationToken>());
    }

    // ===== Helpers =====

    private static AttributeValue S(string value) => new() { S = value };

    private static AttributeValue N(string value) => new() { N = value };

    private static APIGatewayCustomAuthorizerContext AdminGroup()
        => new() { { "sub", "admin-1" }, { "cognito:groups", "admin" } };

    private static APIGatewayProxyRequest Request(APIGatewayCustomAuthorizerContext authorizer) => new()
    {
        HttpMethod = "GET",
        Path = $"/admin/deliveries/{DeliveryId}/trail",
        Resource = "/admin/deliveries/{id}/trail",
        PathParameters = new Dictionary<string, string> { ["id"] = DeliveryId },
        RequestContext = new APIGatewayProxyRequest.ProxyRequestContext { Authorizer = authorizer }
    };

    // ===== Planned stops (the board's stop list) =====

    [Fact]
    public async Task WithTheTripAndCropsTables_ThePlannedStopsJudgeThePauses()
    {
        // The pause at 06:40 is placed at the 06:00 fix (8.1; the 06:30 one is coarse), where the
        // order's listing is: it is on the stop list.
        var delivery = Delivery("disputed");
        delivery["listingId"] = S("listing-1");
        DeliveryReturns(delivery);
        _db.QueryAsync(Arg.Is<QueryRequest>(r => r.TableName == "trips-test"), Arg.Any<CancellationToken>())
            .Returns(new QueryResponse
            {
                Items = new List<Dictionary<string, AttributeValue>>
                {
                    new() { ["tripId"] = S(TripId), ["status"] = S("inTransit") }
                }
            });
        _db.QueryAsync(Arg.Is<QueryRequest>(r => r.TableName == "crops-test"), Arg.Any<CancellationToken>())
            .Returns(new QueryResponse
            {
                Items = new List<Dictionary<string, AttributeValue>>
                {
                    new() { ["cropId"] = S("listing-1"), ["latitude"] = N("8.1"), ["longitude"] = N("38.74") }
                }
            });
        var function = new AdminFunction(
            _db, UsersTable, "", "", DeliveriesTable, "", tripLocationsTable: TripLocationsTable,
            tripsTable: "trips-test", cropsTable: "crops-test") { Clock = () => Now };

        var response = await function.FunctionHandler(Request(AdminGroup()), _context);

        response.StatusCode.Should().Be(200);
        var trail = JsonDocument.Parse(response.Body).RootElement;
        trail.GetProperty("awayStops").GetArrayLength().Should().Be(0);
        trail.GetProperty("summary").GetProperty("awayStops").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task WithoutTheTripsTable_NoPlannedStopIsRead_AndTheScansAloneJudge()
    {
        var response = await Function().FunctionHandler(Request(AdminGroup()), _context);

        // The delivery's own scan points are far from the pause, so it is listed, with no
        // planned-stop distance.
        var trail = JsonDocument.Parse(response.Body).RootElement;
        var stop = trail.GetProperty("awayStops").EnumerateArray().Should().ContainSingle().Subject;
        stop.GetProperty("nearestScanKm").GetDouble().Should().BeGreaterThan(5);
        stop.GetProperty("nearestPlannedStopKm").ValueKind.Should().Be(JsonValueKind.Null);
        await _db.DidNotReceive().QueryAsync(
            Arg.Is<QueryRequest>(r => r.TableName == "trips-test"), Arg.Any<CancellationToken>());
    }

    private void DeliveryReturns(Dictionary<string, AttributeValue>? item)
        => _db.QueryAsync(Arg.Is<QueryRequest>(r => r.TableName == DeliveriesTable), Arg.Any<CancellationToken>())
            .Returns(new QueryResponse
            {
                Items = item == null
                    ? new List<Dictionary<string, AttributeValue>>()
                    : new List<Dictionary<string, AttributeValue>> { item }
            });

    private static Dictionary<string, AttributeValue> Delivery(string status) => new()
    {
        ["deliveryId"] = S(DeliveryId),
        ["tripId"] = S(TripId),
        ["orderId"] = S("order-101"),
        ["farmerId"] = S("farmer-123"),
        ["truckerId"] = S("trucker-789"),
        ["buyerId"] = S("buyer-456"),
        ["status"] = S(status),
        ["pickedUpAt"] = S("2026-10-27T05:05:00.0000000Z"),
        ["confirmedAt"] = S("2026-10-27T08:00:00.0000000Z"),
        ["gpsPickup"] = new AttributeValue
        {
            M = new Dictionary<string, AttributeValue> { ["latitude"] = N("8.15041"), ["longitude"] = N("38.8196") }
        },
        ["version"] = N("4")
    };

    private static Dictionary<string, AttributeValue> FixRow(string capturedAt, string latitude, string accuracyM) => new()
    {
        ["tripId"] = S(TripId),
        ["sortKey"] = S($"{capturedAt}#fix"),
        ["truckerId"] = S("trucker-789"),
        ["latitude"] = N(latitude),
        ["longitude"] = N("38.74"),
        ["accuracyM"] = N(accuracyM),
        ["source"] = S("hourly"),
        ["mocked"] = new AttributeValue { BOOL = false },
        ["receivedAt"] = S(capturedAt),
        ["expiresAt"] = N("1798000000")
    };

    private static Dictionary<string, AttributeValue> EventRow(string capturedAt, string kind, string pausedUntil) => new()
    {
        ["tripId"] = S(TripId),
        ["sortKey"] = S($"{capturedAt}#{kind}"),
        ["truckerId"] = S("trucker-789"),
        ["pausedUntil"] = S(pausedUntil),
        ["receivedAt"] = S(capturedAt),
        ["expiresAt"] = N("1798000000")
    };
}
