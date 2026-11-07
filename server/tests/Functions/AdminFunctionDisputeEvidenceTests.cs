using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.TestUtilities;
using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using WeredaConnect.Server.Functions;
using Xunit;

namespace WeredaConnect.Server.Tests.Functions;

/// <summary>
/// FRD-14: GET /admin/disputes/{escrowId}/evidence through the real handler and repositories over a
/// substituted DynamoDB client: who may read it (the admin group only), the 404s, what it stitches
/// (timeline, handoffs with the scan method, who and the distance to the planned point, photos left
/// to FRD-12's tests), the parts it tolerates missing, and the message read: only when asked for,
/// only the disputed order's and delivery's threads, audited before it is answered.
/// </summary>
public sealed class AdminFunctionDisputeEvidenceTests
{
    private const string UsersTable = "users-test";
    private const string OrdersTable = "orders-test";
    private const string EscrowsTable = "escrows-test";
    private const string DeliveriesTable = "deliveries-test";
    private const string EscrowAuditTable = "escrow-audit-test";
    private const string TxAuditTable = "tx-audit-test";
    private const string TripsTable = "trips-test";
    private const string CropsTable = "crops-test";
    private const string MessagesTable = "messages-test";
    private const string ConversationIndex = "ConversationIdCreatedAtIndex";

    private const string EscrowId = "escrow-order-ord-1";
    private const string OrderId = "ord-1";
    private const string DeliveryId = "del-1";
    private const string Buyer = "buyer-1";
    private const string Farmer = "farmer-1";
    private const string Trucker = "trucker-1";

    private const string ForbiddenBody = "{\"error\":{\"code\":\"ADMIN_001\",\"message\":\"Admin access required.\"}}";

    private readonly IAmazonDynamoDB _db = Substitute.For<IAmazonDynamoDB>();
    private readonly TestLambdaContext _context = new();

    // What each table answers; a test changes these before it calls the handler.
    private Dictionary<string, AttributeValue>? _escrow = Escrow();
    private Dictionary<string, AttributeValue>? _order = Order();
    private Dictionary<string, AttributeValue>? _delivery = Delivery();
    private readonly Dictionary<string, List<Dictionary<string, AttributeValue>>> _threads = new();
    private readonly HashSet<string> _truncatedThreads = new();
    private bool _cropsFail;

    public AdminFunctionDisputeEvidenceTests()
    {
        _db.QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>()).Returns(ci => Answer(ci.Arg<QueryRequest>()));
        _db.BatchGetItemAsync(Arg.Any<BatchGetItemRequest>(), Arg.Any<CancellationToken>())
            .Returns(new BatchGetItemResponse
            {
                Responses = new Dictionary<string, List<Dictionary<string, AttributeValue>>>(),
                UnprocessedKeys = new Dictionary<string, KeysAndAttributes>(),
            });
        _db.PutItemAsync(Arg.Any<PutItemRequest>(), Arg.Any<CancellationToken>()).Returns(new PutItemResponse());

        _threads[$"order:{OrderId}:{Buyer}:{Farmer}"] = new()
        {
            Message("m-2", $"order:{OrderId}:{Buyer}:{Farmer}", Farmer, Buyer, "The sacks left full.", "2026-11-05T09:00:00Z"),
            Message("m-1", $"order:{OrderId}:{Buyer}:{Farmer}", Buyer, Farmer, "Is the maize ready?", "2026-11-04T08:00:00Z"),
        };
        _threads[$"delivery:{DeliveryId}:{Buyer}:{Trucker}"] = new()
        {
            Message("m-3", $"delivery:{DeliveryId}:{Buyer}:{Trucker}", Buyer, Trucker, "150 kg short.", "2026-11-05T12:20:00Z"),
            // A row of another thread that a GSI answer could carry: never shown.
            Message("m-x", "order:other:a:b", "a", "b", "Not this dispute.", "2026-11-05T12:21:00Z"),
        };
    }

    // ===== Who may read it =====

    [Fact]
    public async Task AnAdminInTheAdminGroup_GetsTheEvidence()
    {
        var response = await Function().FunctionHandler(Request(AdminGroup()), _context);

        response.StatusCode.Should().Be(200, response.Body);
        var body = JsonDocument.Parse(response.Body).RootElement;
        body.GetProperty("escrow").GetProperty("escrowId").GetString().Should().Be(EscrowId);
        body.GetProperty("escrow").GetProperty("dispute").GetProperty("reason").GetString().Should().Be("short_delivery");
        body.GetProperty("order").GetProperty("orderId").GetString().Should().Be(OrderId);
        body.GetProperty("delivery").GetProperty("deliveryId").GetString().Should().Be(DeliveryId);
    }

    [Fact]
    public async Task ARoleClaimAdminOutsideTheAdminGroup_GetsTheAdminRefusal_AndNothingIsRead()
    {
        var response = await Function().FunctionHandler(
            Request(new APIGatewayCustomAuthorizerContext { { "sub", "admin-1" }, { "role", "admin" } }, messages: true),
            _context);

        response.StatusCode.Should().Be(403);
        response.Body.Should().Be(ForbiddenBody);
        await _db.DidNotReceiveWithAnyArgs().QueryAsync(default!, default);
        await _db.DidNotReceiveWithAnyArgs().PutItemAsync(default(PutItemRequest)!, default);
    }

    [Theory]
    [InlineData("buyer")]
    [InlineData("farmer")]
    [InlineData("trucker")]
    public async Task AMarketplaceUser_GetsTheAdminRefusal(string role)
    {
        var response = await Function().FunctionHandler(
            Request(new APIGatewayCustomAuthorizerContext { { "sub", Buyer }, { "role", role } }, messages: true),
            _context);

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

    // ===== Not found =====

    [Fact]
    public async Task AnUnknownEscrow_Is404()
    {
        _escrow = null;

        var response = await Function().FunctionHandler(Request(AdminGroup(), messages: true), _context);

        response.StatusCode.Should().Be(404);
        response.Body.Should().Contain("ADMIN_003");
        await _db.DidNotReceive().QueryAsync(Arg.Is<QueryRequest>(r => r.TableName == MessagesTable), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AnEscrowNobodyDisputed_Is404_AndItsMessagesAreNeverRead()
    {
        _escrow = Escrow(disputed: false);

        var response = await Function().FunctionHandler(Request(AdminGroup(), messages: true), _context);

        response.StatusCode.Should().Be(404);
        response.Body.Should().Contain("ADMIN_019");
        await _db.DidNotReceive().QueryAsync(Arg.Is<QueryRequest>(r => r.TableName == MessagesTable), Arg.Any<CancellationToken>());
        await _db.DidNotReceiveWithAnyArgs().PutItemAsync(default(PutItemRequest)!, default);
    }

    [Fact]
    public async Task AResolvedDispute_StillHasItsEvidence()
    {
        _escrow = Escrow(status: "dispute_resolved");

        var response = await Function().FunctionHandler(Request(AdminGroup()), _context);

        response.StatusCode.Should().Be(200, response.Body);
    }

    // ===== What it stitches =====

    [Fact]
    public async Task TheTimeline_MergesTheOrderDeliveryAndEscrowLegs_InTimeOrder()
    {
        var body = await EvidenceAsync();

        var actions = body.GetProperty("timeline").EnumerateArray().Select(e => e.GetProperty("action").GetString()).ToList();
        actions.Should().ContainInOrder("order_created", "picked_up", "delivered", "dispute");
        actions.Should().Contain("escrowed");
        var pickup = body.GetProperty("timeline").EnumerateArray().Single(e => e.GetProperty("action").GetString() == "picked_up");
        pickup.GetProperty("location").GetProperty("latitude").GetDouble().Should().Be(8.5);
    }

    [Fact]
    public async Task TheHandoffs_SayHowWhoWhereAndHowFarFromThePlannedPoint()
    {
        var body = await EvidenceAsync();

        var handoffs = body.GetProperty("handoffs");
        handoffs.GetArrayLength().Should().Be(2);

        var pickup = handoffs[0];
        pickup.GetProperty("leg").GetString().Should().Be("pickup");
        pickup.GetProperty("at").GetString().Should().Be("2026-11-05T07:40:00Z");
        pickup.GetProperty("capturedAt").GetString().Should().Be("2026-11-05T07:39:30Z");
        pickup.GetProperty("actorId").GetString().Should().Be(Trucker);
        pickup.GetProperty("actorRole").GetString().Should().Be("trucker");
        pickup.GetProperty("method").GetString().Should().Be("qr");
        pickup.GetProperty("gps").GetProperty("latitude").GetDouble().Should().Be(8.5);
        pickup.GetProperty("planned").GetProperty("latitude").GetDouble().Should().Be(8.504);
        // 0.004 degrees of latitude: about 0.44 km.
        pickup.GetProperty("distanceKm").GetDouble().Should().Be(0.44);

        var dropoff = handoffs[1];
        dropoff.GetProperty("leg").GetString().Should().Be("dropoff");
        dropoff.GetProperty("at").GetString().Should().Be("2026-11-05T12:15:00Z");
        dropoff.GetProperty("actorRole").GetString().Should().Be("buyer");
        dropoff.GetProperty("method").GetString().Should().Be("code");
        dropoff.TryGetProperty("capturedAt", out _).Should().BeFalse();
        dropoff.GetProperty("planned").GetProperty("latitude").GetDouble().Should().Be(9.0);
        dropoff.GetProperty("distanceKm").GetDouble().Should().Be(0.22);
    }

    [Fact]
    public async Task TheDropoffHandoff_IsMeasuredFromTheOrdersOwnPoint_WhenItIsTheNearer()
    {
        // LT-14: the delivered GPS check passes within 5 km of the order's point or the trip's
        // destination; the evidence shows the one nearer to the buyer's fix (9.002, 38.74).
        _order!["deliveryLatitude"] = N("9.003");
        _order["deliveryLongitude"] = N("38.74");

        var body = await EvidenceAsync();

        var dropoff = body.GetProperty("handoffs")[1];
        dropoff.GetProperty("planned").GetProperty("latitude").GetDouble().Should().Be(9.003);
        dropoff.GetProperty("distanceKm").GetDouble().Should().Be(0.11);
        // The admin's order carries the point too.
        body.GetProperty("order").GetProperty("deliveryLatitude").GetDouble().Should().Be(9.003);
    }

    [Fact]
    public async Task TheDropoffHandoff_AtTheTripsDestination_IsMeasuredFromIt_NotTheFartherOrderPoint()
    {
        _order!["deliveryLatitude"] = N("9.022");
        _order["deliveryLongitude"] = N("38.74");

        var body = await EvidenceAsync();

        var dropoff = body.GetProperty("handoffs")[1];
        dropoff.GetProperty("planned").GetProperty("latitude").GetDouble().Should().Be(9.0);
        dropoff.GetProperty("distanceKm").GetDouble().Should().Be(0.22);
    }

    [Fact]
    public async Task TheDelivery_CarriesNoPhoneAndNoQrToken()
    {
        var body = await EvidenceAsync();

        var delivery = body.GetProperty("delivery");
        delivery.TryGetProperty("buyerPhone", out _).Should().BeFalse();
        delivery.TryGetProperty("farmerPhone", out _).Should().BeFalse();
        delivery.TryGetProperty("truckerPhone", out _).Should().BeFalse();
        delivery.GetProperty("qrToken").GetString().Should().BeEmpty();
    }

    [Fact]
    public async Task WithoutAskingForThem_NoMessageIsRead_AndNothingIsWritten()
    {
        var body = await EvidenceAsync();

        body.TryGetProperty("messages", out _).Should().BeFalse();
        body.TryGetProperty("messagesTruncated", out _).Should().BeFalse();
        await _db.DidNotReceive().QueryAsync(Arg.Is<QueryRequest>(r => r.TableName == MessagesTable), Arg.Any<CancellationToken>());
        await _db.DidNotReceiveWithAnyArgs().PutItemAsync(default(PutItemRequest)!, default);
        await _db.DidNotReceiveWithAnyArgs().UpdateItemAsync(default(UpdateItemRequest)!, default);
        await _db.DidNotReceiveWithAnyArgs().ScanAsync(default(ScanRequest)!, default);
    }

    // ===== Missing parts are tolerated =====

    [Fact]
    public async Task AnEscrowWithoutOrderOrDelivery_AnswersWhatThereIs()
    {
        _escrow = Escrow(withOrder: false, withDelivery: false);

        var body = await EvidenceAsync();

        body.TryGetProperty("order", out _).Should().BeFalse();
        body.TryGetProperty("delivery", out _).Should().BeFalse();
        body.GetProperty("handoffs").GetArrayLength().Should().Be(0);
        // The escrow leg is still there, from the escrow audit trail.
        body.GetProperty("timeline").EnumerateArray().Select(e => e.GetProperty("action").GetString())
            .Should().Contain("escrowed");
    }

    [Fact]
    public async Task AnOrderOrDeliveryThatIsGone_IsLeftOut()
    {
        _order = null;
        _delivery = null;

        var body = await EvidenceAsync();

        body.TryGetProperty("order", out _).Should().BeFalse();
        body.TryGetProperty("delivery", out _).Should().BeFalse();
        body.GetProperty("handoffs").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task APlannedPointThatCannotBeRead_LeavesOnlyTheDistanceOut()
    {
        _cropsFail = true;

        var body = await EvidenceAsync();

        var pickup = body.GetProperty("handoffs")[0];
        pickup.GetProperty("gps").GetProperty("latitude").GetDouble().Should().Be(8.5);
        pickup.TryGetProperty("planned", out _).Should().BeFalse();
        pickup.TryGetProperty("distanceKm", out _).Should().BeFalse();
    }

    [Fact]
    public async Task ASelfPickup_HasOneHandoffAtTheFarm()
    {
        _delivery = Delivery(selfPickup: true);

        var body = await EvidenceAsync();

        var handoff = body.GetProperty("handoffs").EnumerateArray().Should().ContainSingle().Subject;
        handoff.GetProperty("leg").GetString().Should().Be("self_pickup");
        handoff.GetProperty("actorRole").GetString().Should().Be("buyer");
        // Judged against the listing, where the buyer collected the goods.
        handoff.GetProperty("planned").GetProperty("latitude").GetDouble().Should().Be(8.504);
        await _db.DidNotReceive().QueryAsync(Arg.Is<QueryRequest>(r => r.TableName == TripsTable), Arg.Any<CancellationToken>());
    }

    // ===== The messages =====

    [Fact]
    public async Task AskingForTheMessages_ReadsOnlyTheDisputedOrdersAndDeliverysThreads_OldestFirst()
    {
        var body = await EvidenceAsync(messages: true);

        var read = _db.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(IAmazonDynamoDB.QueryAsync))
            .Select(c => (QueryRequest)c.GetArguments()[0]!)
            .Where(r => r.TableName == MessagesTable)
            .ToList();
        read.Should().OnlyContain(r => r.IndexName == ConversationIndex && r.Limit == AdminFunction.MaxMessagesPerThread);
        // Every pair of the three parties, on the order and on the delivery.
        read.Select(r => r.ExpressionAttributeValues[":val"].S).Should().BeEquivalentTo(
            $"order:{OrderId}:{Buyer}:{Farmer}", $"order:{OrderId}:{Buyer}:{Trucker}", $"order:{OrderId}:{Farmer}:{Trucker}",
            $"delivery:{DeliveryId}:{Buyer}:{Farmer}", $"delivery:{DeliveryId}:{Buyer}:{Trucker}",
            $"delivery:{DeliveryId}:{Farmer}:{Trucker}");

        var messages = body.GetProperty("messages").EnumerateArray().ToList();
        messages.Select(m => m.GetProperty("messageId").GetString()).Should().Equal("m-1", "m-2", "m-3");
        messages[0].GetProperty("body").GetString().Should().Be("Is the maize ready?");
        messages[0].GetProperty("senderId").GetString().Should().Be(Buyer);
        body.GetProperty("messagesTruncated").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task EveryMessageRead_IsAudited_UnderTheAdmin()
    {
        await EvidenceAsync(messages: true);

        await _db.Received(1).PutItemAsync(
            Arg.Is<PutItemRequest>(r =>
                r.TableName == TxAuditTable
                && r.Item["action"].S == AdminFunction.MessagesReadAction
                && r.Item["transactionId"].S == OrderId
                && r.Item["entityType"].S == "escrow"
                && r.Item["entityId"].S == EscrowId
                && r.Item["actor"].S == "admin-1"
                && r.Item["actorRole"].S == "admin"
                && r.Item["metadata"].S == "messages=3;threads=2"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AReadThatCannotBeAudited_AnswersNoMessage()
    {
        _db.PutItemAsync(Arg.Any<PutItemRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new AmazonDynamoDBException("throttled"));

        var response = await Function().FunctionHandler(Request(AdminGroup(), messages: true), _context);

        response.StatusCode.Should().Be(503);
        response.Body.Should().Contain("ADMIN_020");
        response.Body.Should().NotContain("maize");
    }

    [Fact]
    public async Task WithoutTheMessagesTable_NothingIsRead()
    {
        var response = await Function(messagesTable: "").FunctionHandler(Request(AdminGroup(), messages: true), _context);

        response.StatusCode.Should().Be(503);
        response.Body.Should().Contain("ADMIN_004");
        await _db.DidNotReceiveWithAnyArgs().QueryAsync(default!, default);
    }

    [Fact]
    public async Task WithoutTheAuditTable_NoMessageIsRead()
    {
        var function = new AdminFunction(_db, UsersTable, OrdersTable, EscrowsTable, DeliveriesTable, EscrowAuditTable,
            transactionAuditTable: "", tripsTable: TripsTable, cropsTable: CropsTable)
        { MessagesTable = MessagesTable };

        var response = await function.FunctionHandler(Request(AdminGroup(), messages: true), _context);

        response.StatusCode.Should().Be(503);
        response.Body.Should().Contain("ADMIN_004");
        await _db.DidNotReceive().QueryAsync(Arg.Is<QueryRequest>(r => r.TableName == MessagesTable), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AThreadLongerThanOneRead_IsMarkedTruncated()
    {
        _truncatedThreads.Add($"order:{OrderId}:{Buyer}:{Farmer}");

        var body = await EvidenceAsync(messages: true);

        body.GetProperty("messagesTruncated").GetBoolean().Should().BeTrue();
    }

    // ===== Helpers =====

    [Fact]
    public void ConversationIds_FollowTheMessagingFormula()
    {
        AdminFunction.ConversationIds("order", "o-1", "b", "a", null, "", "a")
            .Should().Equal("order:o-1:a:b");
        AdminFunction.ConversationIds("delivery", "d-1", "z", "a", "m")
            .Should().Equal("delivery:d-1:a:m", "delivery:d-1:a:z", "delivery:d-1:m:z");
        AdminFunction.ConversationIds("order", "o-1", "only").Should().BeEmpty();
    }

    [Theory]
    [InlineData("handoff=qr", "qr")]
    [InlineData("handoff=code", "code")]
    [InlineData("source=x;handoff=code", "code")]
    [InlineData("handoff=other", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void HandoffMethodOf_ReadsTheAuditMetadata(string? metadata, string? expected)
        => AdminFunction.HandoffMethodOf(metadata).Should().Be(expected);

    private AdminFunction Function(string messagesTable = MessagesTable) =>
        new(_db, UsersTable, OrdersTable, EscrowsTable, DeliveriesTable, EscrowAuditTable,
            transactionAuditTable: TxAuditTable, tripsTable: TripsTable, cropsTable: CropsTable)
        {
            MessagesTable = messagesTable,
        };

    private async Task<JsonElement> EvidenceAsync(bool messages = false)
    {
        var response = await Function().FunctionHandler(Request(AdminGroup(), messages), _context);
        response.StatusCode.Should().Be(200, response.Body);
        return JsonDocument.Parse(response.Body).RootElement.Clone();
    }

    private QueryResponse Answer(QueryRequest r)
    {
        List<Dictionary<string, AttributeValue>> items;
        var lastKey = new Dictionary<string, AttributeValue>();
        if (r.TableName == MessagesTable)
        {
            var conversationId = r.ExpressionAttributeValues[":val"].S;
            items = _threads.GetValueOrDefault(conversationId) ?? new();
            if (_truncatedThreads.Contains(conversationId))
            {
                lastKey = new Dictionary<string, AttributeValue> { ["messageId"] = S("m-0") };
            }
        }
        else if (r.TableName == CropsTable && _cropsFail)
        {
            throw new AmazonDynamoDBException("crops unavailable");
        }
        else
        {
            items = r.TableName switch
            {
                EscrowsTable => One(_escrow),
                OrdersTable => One(_order),
                DeliveriesTable => One(_delivery),
                EscrowAuditTable => new() { EscrowAuditRow("2026-11-04T08:30:00Z", "escrowed") },
                TxAuditTable when r.IndexName == "EntityIndex" => DeliveryEvents(),
                TxAuditTable => new() { OrderEvent("2026-11-04T08:00:00Z", "order_created") },
                CropsTable => new() { new() { ["cropId"] = S("crop-1"), ["latitude"] = N("8.504"), ["longitude"] = N("38.8") } },
                TripsTable => new()
                {
                    new() { ["tripId"] = S("trip-1"), ["destinationLatitude"] = N("9.0"), ["destinationLongitude"] = N("38.74") }
                },
                _ => new()
            };
        }

        return new QueryResponse { Items = items, LastEvaluatedKey = lastKey };
    }

    private static List<Dictionary<string, AttributeValue>> One(Dictionary<string, AttributeValue>? item)
        => item == null ? new() : new() { item };

    private static APIGatewayCustomAuthorizerContext AdminGroup()
        => new() { { "sub", "admin-1" }, { "cognito:groups", "admin" } };

    private static APIGatewayProxyRequest Request(APIGatewayCustomAuthorizerContext authorizer, bool messages = false) => new()
    {
        HttpMethod = "GET",
        Path = $"/admin/disputes/{EscrowId}/evidence",
        Resource = "/admin/disputes/{id}/evidence",
        PathParameters = new Dictionary<string, string> { ["id"] = EscrowId },
        QueryStringParameters = messages ? new Dictionary<string, string> { ["include"] = "messages" } : null,
        RequestContext = new APIGatewayProxyRequest.ProxyRequestContext { Authorizer = authorizer }
    };

    private static Dictionary<string, AttributeValue> Escrow(
        bool disputed = true, string status = "disputed", bool withOrder = true, bool withDelivery = true)
    {
        var escrow = new Dictionary<string, AttributeValue>
        {
            ["escrowId"] = S(EscrowId),
            ["buyerId"] = S(Buyer),
            ["farmerId"] = S(Farmer),
            ["truckerId"] = S(Trucker),
            ["amount"] = N("30900"),
            ["status"] = S(status),
            ["createdAt"] = S("2026-11-04T08:30:00Z"),
            ["updatedAt"] = S("2026-11-05T12:50:00Z"),
            ["version"] = N("3"),
        };
        if (withOrder)
        {
            escrow["orderId"] = S(OrderId);
        }

        if (withDelivery)
        {
            escrow["deliveryId"] = S(DeliveryId);
        }

        if (disputed)
        {
            escrow["disputeId"] = S("dispute-1");
            escrow["disputeReason"] = S("short_delivery");
            escrow["disputedBy"] = S(Buyer);
            escrow["disputeCreatedAt"] = S("2026-11-05T12:50:00Z");
        }

        return escrow;
    }

    private static Dictionary<string, AttributeValue> Order() => new()
    {
        ["orderId"] = S(OrderId),
        ["listingId"] = S("crop-1"),
        ["buyerId"] = S(Buyer),
        ["farmerId"] = S(Farmer),
        ["truckerId"] = S(Trucker),
        ["status"] = S("disputed"),
        ["createdAt"] = S("2026-11-04T08:00:00Z"),
        ["updatedAt"] = S("2026-11-05T12:50:00Z"),
    };

    private static Dictionary<string, AttributeValue> Delivery(bool selfPickup = false)
    {
        var delivery = new Dictionary<string, AttributeValue>
        {
            ["deliveryId"] = S(DeliveryId),
            ["tripId"] = S(selfPickup ? DeliveryId : "trip-1"),
            ["orderId"] = S(OrderId),
            ["listingId"] = S("crop-1"),
            ["buyerId"] = S(Buyer),
            ["farmerId"] = S(Farmer),
            ["buyerPhone"] = S("+251900100003"),
            ["farmerPhone"] = S("+251900100001"),
            ["qrToken"] = S("secret-token"),
            ["status"] = S("disputed"),
            ["confirmedAt"] = S("2026-11-05T12:15:00Z"),
            ["gpsDropoff"] = Point(selfPickup ? "8.504" : "9.002", selfPickup ? "38.8" : "38.74"),
            ["createdAt"] = S("2026-11-04T09:00:00Z"),
            ["updatedAt"] = S("2026-11-05T12:50:00Z"),
            ["version"] = N("4"),
        };
        if (selfPickup)
        {
            delivery["deliveryType"] = S("self_pickup");
        }
        else
        {
            delivery["truckerId"] = S(Trucker);
            delivery["truckerPhone"] = S("+251900100002");
            delivery["pickedUpAt"] = S("2026-11-05T07:40:00Z");
            delivery["pickupCapturedAt"] = S("2026-11-05T07:39:30Z");
            delivery["gpsPickup"] = Point("8.5", "38.8");
        }

        return delivery;
    }

    private List<Dictionary<string, AttributeValue>> DeliveryEvents()
    {
        var selfPickup = _delivery?.ContainsKey("deliveryType") == true;
        var events = new List<Dictionary<string, AttributeValue>>();
        if (!selfPickup)
        {
            events.Add(DeliveryEvent("2026-11-05T07:40:01Z", "picked_up", Trucker, "handoff=qr"));
        }

        events.Add(DeliveryEvent("2026-11-05T12:15:01Z", "delivered", Buyer, selfPickup ? "handoff=qr" : "handoff=code"));
        events.Add(DeliveryEvent("2026-11-05T12:50:00Z", "dispute", Buyer, null));
        return events;
    }

    private static Dictionary<string, AttributeValue> OrderEvent(string at, string action) => new()
    {
        ["auditId"] = S($"tx-{action}"),
        ["transactionId"] = S(OrderId),
        ["entityType"] = S("order"),
        ["entityId"] = S(OrderId),
        ["action"] = S(action),
        ["actor"] = S(Buyer),
        ["timestamp"] = S(at),
    };

    private static Dictionary<string, AttributeValue> DeliveryEvent(string at, string action, string actor, string? metadata)
    {
        var row = new Dictionary<string, AttributeValue>
        {
            ["auditId"] = S($"tx-{action}"),
            ["transactionId"] = S(OrderId),
            ["entityType"] = S("delivery"),
            ["entityId"] = S(DeliveryId),
            ["action"] = S(action),
            ["actor"] = S(actor),
            ["timestamp"] = S(at),
        };
        if (metadata != null)
        {
            row["metadata"] = S(metadata);
        }

        return row;
    }

    private static Dictionary<string, AttributeValue> EscrowAuditRow(string at, string action) => new()
    {
        ["auditId"] = S($"audit-{action}"),
        ["escrowId"] = S(EscrowId),
        ["action"] = S(action),
        ["toState"] = S(action),
        ["actor"] = S("system"),
        ["timestamp"] = S(at),
    };

    private static Dictionary<string, AttributeValue> Message(
        string id, string conversationId, string sender, string recipient, string body, string at) => new()
        {
            ["messageId"] = S(id),
            ["conversationId"] = S(conversationId),
            ["contextType"] = S(conversationId.Split(':')[0]),
            ["contextId"] = S(conversationId.Split(':')[1]),
            ["senderId"] = S(sender),
            ["recipientId"] = S(recipient),
            ["body"] = S(body),
            ["createdAt"] = S(at),
            ["version"] = N("1"),
        };

    private static AttributeValue Point(string latitude, string longitude) => new()
    {
        M = new Dictionary<string, AttributeValue> { ["latitude"] = N(latitude), ["longitude"] = N(longitude) }
    };

    private static AttributeValue S(string v) => new() { S = v };
    private static AttributeValue N(string v) => new() { N = v };
}
