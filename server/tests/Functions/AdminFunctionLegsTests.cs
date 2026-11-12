using System.Globalization;
using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.TestUtilities;
using FluentAssertions;
using NSubstitute;
using WeredaConnect.Server.Functions;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Functions;

/// <summary>
/// ESC-24: the admin's read-only view of the legs. The escrow DTO carries the transport leg and the ledger
/// fields; <c>GET /admin/escrows/{id}/ledger</c> pages the ledger rows; <c>GET /admin/orders/queue</c> lists the
/// stuck orders from <c>AdminQueueIndex</c> or <c>DeadlineIndex</c>. No route here writes or scans.
/// </summary>
public class AdminFunctionLegsTests
{
    private const string UsersTable = "users-test";
    private const string OrdersTable = "orders-test";
    private const string EscrowsTable = "escrows-test";
    private const string LedgerTable = "ledger-test";
    private static readonly DateTime Now = new(2026, 11, 11, 12, 0, 0, DateTimeKind.Utc);

    private readonly IAmazonDynamoDB _ddb = Substitute.For<IAmazonDynamoDB>();
    private readonly TestLambdaContext _context = new();
    private readonly Dictionary<string, Dictionary<string, AttributeValue>> _escrows = new();
    private readonly Dictionary<string, Dictionary<string, AttributeValue>> _orders = new();
    private readonly Dictionary<string, Queue<QueryResponse>> _pages = new();
    private readonly Dictionary<string, string> _names = new();

    public AdminFunctionLegsTests()
    {
        _ddb.QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>()).Returns(ci =>
        {
            var query = ci.ArgAt<QueryRequest>(0);
            var pageKey = query.IndexName ?? query.TableName;
            if (query.IndexName != null || query.TableName == LedgerTable)
            {
                return Task.FromResult(_pages.TryGetValue(pageKey, out var pages) && pages.Count > 0 ? pages.Dequeue() : Page(null));
            }

            var rows = query.TableName == EscrowsTable ? _escrows : _orders;
            return Task.FromResult(rows.TryGetValue(query.ExpressionAttributeValues[":id"].S, out var row) ? Page(null, row) : Page(null));
        });
        _ddb.BatchGetItemAsync(Arg.Any<BatchGetItemRequest>(), Arg.Any<CancellationToken>()).Returns(ci =>
        {
            var keys = ci.ArgAt<BatchGetItemRequest>(0).RequestItems[UsersTable].Keys;
            var rows = keys.Select(k => k["userId"].S).Where(_names.ContainsKey)
                .Select(id => new Dictionary<string, AttributeValue> { ["userId"] = S(id), ["displayName"] = S(_names[id]) })
                .ToList();
            return Task.FromResult(new BatchGetItemResponse
            {
                Responses = new Dictionary<string, List<Dictionary<string, AttributeValue>>> { { UsersTable, rows } },
                UnprocessedKeys = new Dictionary<string, KeysAndAttributes>()
            });
        });
    }

    private AdminFunction Function() => new(_ddb, UsersTable, OrdersTable, EscrowsTable, "deliveries-test", "escrow-audit-test")
    {
        LedgerTable = LedgerTable,
        Clock = () => Now
    };

    private static AttributeValue S(string value) => new() { S = value };

    private static AttributeValue N(string value) => new() { N = value };

    private static string Iso(DateTime at) => at.ToString("o", CultureInfo.InvariantCulture);

    private static QueryResponse Page(Dictionary<string, AttributeValue>? lastKey, params Dictionary<string, AttributeValue>[] rows) =>
        new() { Items = rows.ToList(), LastEvaluatedKey = lastKey ?? new Dictionary<string, AttributeValue>() };

    private void Pages(string indexOrTable, params QueryResponse[] pages) => _pages[indexOrTable] = new Queue<QueryResponse>(pages);

    private static APIGatewayProxyRequest Get(string path, Dictionary<string, string>? query = null, string role = "admin") => new()
    {
        HttpMethod = "GET",
        Path = path,
        QueryStringParameters = query,
        RequestContext = new APIGatewayProxyRequest.ProxyRequestContext
        {
            Authorizer = new APIGatewayCustomAuthorizerContext { { "sub", "user-1" }, { "role", role } }
        }
    };

    private Task<APIGatewayProxyResponse> GetAsync(string path, Dictionary<string, string>? query = null, string role = "admin") =>
        Function().FunctionHandler(Get(path, query, role), _context);

    private static JsonElement Json(APIGatewayProxyResponse response) => JsonDocument.Parse(response.Body).RootElement;

    private static string ErrorCode(APIGatewayProxyResponse response) =>
        Json(response).GetProperty("error").GetProperty("code").GetString()!;

    private List<QueryRequest> Queries(string indexOrTable) => _ddb.ReceivedCalls()
        .Select(c => c.GetArguments()[0]).OfType<QueryRequest>()
        .Where(q => (q.IndexName ?? q.TableName) == indexOrTable).ToList();

    private void NothingWrittenOrScanned()
    {
        var calls = _ddb.ReceivedCalls().Select(c => c.GetMethodInfo().Name).ToList();
        calls.Should().OnlyContain(name => name == "QueryAsync" || name == "BatchGetItemAsync");
    }

    private Dictionary<string, AttributeValue> Escrow(string id, bool ledger = true, params (string Key, AttributeValue Value)[] extra)
    {
        var item = new Dictionary<string, AttributeValue>
        {
            ["escrowId"] = S(id),
            ["createdAt"] = S("2026-11-01T10:00:00Z"),
            ["updatedAt"] = S("2026-11-01T10:00:00Z"),
            ["expiresAt"] = S("2026-11-08T10:00:00Z"),
            ["status"] = S("escrowed"),
            ["deliveryId"] = S("delivery-1"),
            ["orderId"] = S("order-1"),
            ["buyerId"] = S("buyer-1"),
            ["farmerId"] = S("farmer-1"),
            ["truckerId"] = S("trucker-1"),
            ["amount"] = N("17250"),
            ["farmerPayout"] = N("15000"),
            ["truckerPayout"] = N("1500"),
            ["commission"] = N("750"),
            ["commissionRate"] = N("0.05"),
            ["currency"] = S("ETB"),
            ["version"] = N("3")
        };
        if (ledger)
        {
            item["settlementModel"] = S("ledger");
            item["legStatus"] = new AttributeValue
            {
                M = new Dictionary<string, AttributeValue> { ["crop"] = S("held"), ["transport"] = S("held"), ["commission"] = S("held") }
            };
            item["refundedTotal"] = N("0");
            item["releasedTotal"] = N("0");
        }

        foreach (var (key, value) in extra)
        {
            item[key] = value;
        }

        _escrows[id] = item;
        return item;
    }

    private static Dictionary<string, AttributeValue> LedgerRow(string escrowId, string reference, string status, string payeeRole, string payeeId, string amount) => new()
    {
        ["escrowId"] = S(escrowId),
        ["ref"] = S(reference),
        ["kind"] = S(reference.Split('#')[0]),
        ["legs"] = new AttributeValue { SS = [payeeRole == "trucker" ? "transport" : "crop"] },
        ["payeeRole"] = S(payeeRole),
        ["payeeId"] = S(payeeId),
        ["amount"] = N(amount),
        ["status"] = S(status),
        ["gatewayRef"] = S("WCL0123456789ABCDEF0123456789"),
        ["attempts"] = N("1"),
        ["reason"] = S("auto_release"),
        ["actor"] = S("system"),
        ["createdAt"] = S("2026-11-05T10:00:00Z"),
        ["updatedAt"] = S("2026-11-05T10:05:00Z"),
        ["version"] = N("2")
    };

    private Dictionary<string, AttributeValue> Order(string id, string status, params (string Key, string Value)[] extra)
    {
        var item = new Dictionary<string, AttributeValue>
        {
            ["orderId"] = S(id),
            ["createdAt"] = S("2026-11-01T09:00:00Z"),
            ["updatedAt"] = S("2026-11-02T09:00:00Z"),
            ["status"] = S(status),
            ["buyerId"] = S("buyer-1"),
            ["buyerName"] = S("Buyer One"),
            ["farmerId"] = S("farmer-1"),
            ["farmerName"] = S("Farmer One"),
            ["truckerId"] = S("trucker-1"),
            ["truckerName"] = S("Trucker One"),
            ["cropType"] = S("teff"),
            ["orderTotal"] = N("17250"),
            ["quantity"] = N("300"),
            ["version"] = N("4")
        };
        foreach (var (key, value) in extra)
        {
            item[key] = S(value);
        }

        _orders[id] = item;
        return item;
    }

    // ──────────────────────────────────────────────
    // The escrow DTO
    // ──────────────────────────────────────────────

    [Fact]
    public async Task EscrowDetail_CarriesTheTransportLeg_TheLedgerFields_AndTheTruckerName()
    {
        Escrow("escrow-1");
        _names["trucker-1"] = "Trucker One";

        var response = await GetAsync("/admin/escrows/escrow-1");

        response.StatusCode.Should().Be(200);
        var escrow = Json(response).GetProperty("escrow");
        escrow.GetProperty("truckerPayout").GetDecimal().Should().Be(1500m);
        escrow.GetProperty("truckerId").GetString().Should().Be("trucker-1");
        escrow.GetProperty("truckerName").GetString().Should().Be("Trucker One");
        escrow.GetProperty("legStatus").GetProperty("transport").GetString().Should().Be("held");
        escrow.GetProperty("refundedTotal").GetDecimal().Should().Be(0m);
        escrow.GetProperty("releasedTotal").GetDecimal().Should().Be(0m);
        escrow.GetProperty("truckerLeg").GetProperty("state").GetString().Should().Be("held");
        escrow.TryGetProperty("verdictAmounts", out _).Should().BeFalse();
    }

    [Fact]
    public async Task EscrowDetail_OfALegacyEscrowWithoutATrucker_HasNoLegFields()
    {
        var item = Escrow("escrow-2", ledger: false);
        item.Remove("truckerId");
        item.Remove("truckerPayout");

        var escrow = Json(await GetAsync("/admin/escrows/escrow-2")).GetProperty("escrow");

        foreach (var field in new[] { "truckerPayout", "truckerId", "truckerName", "truckerLeg", "legStatus", "refundedTotal", "releasedTotal" })
        {
            escrow.TryGetProperty(field, out _).Should().BeFalse(field);
        }
    }

    [Fact]
    public async Task EscrowDetail_CarriesTheRecordedSplit()
    {
        Escrow("escrow-3", true, ("status", S("dispute_resolved")), ("verdictAmounts", new AttributeValue
        {
            M = new Dictionary<string, AttributeValue> { ["farmerAmount"] = N("9000.00"), ["truckerAmount"] = N("1500.00"), ["buyerRefund"] = N("6000.00") }
        }));

        var split = Json(await GetAsync("/admin/escrows/escrow-3")).GetProperty("escrow").GetProperty("verdictAmounts");

        split.GetProperty("farmerAmount").GetDecimal().Should().Be(9000m);
        split.GetProperty("truckerAmount").GetDecimal().Should().Be(1500m);
        split.GetProperty("buyerRefund").GetDecimal().Should().Be(6000m);
    }

    public static TheoryData<string, Dictionary<string, object>, string?> TruckerLegCases() => new()
    {
        { "blocked by the gate", new() { ["status"] = "released", ["truckerPayout"] = 1500m, ["truckerPayoutPending"] = true, ["truckerPayoutBlockReason"] = "phone_unverified" }, "blocked" },
        { "owed, retried", new() { ["status"] = "released", ["truckerPayout"] = 1500m, ["truckerPayoutPending"] = true }, "pending" },
        { "in Manual Payouts", new() { ["status"] = "released", ["truckerPayout"] = 1500m, ["truckerPayoutPending"] = true, ["manualPayout"] = "open", ["manualTruckerStatus"] = "pending" }, "manual_queue" },
        { "queue open for the farmer only", new() { ["status"] = "escrowed", ["truckerPayout"] = 1500m, ["manualPayout"] = "open", ["manualFarmerStatus"] = "pending", ["manualTruckerStatus"] = "paid", ["truckerPaidAt"] = "2026-11-05T10:00:00Z" }, "paid" },
        { "paid", new() { ["status"] = "completed", ["truckerPayout"] = 1500m, ["truckerTelebirrRef"] = "TB-1" }, "paid" },
        { "held (1.x)", new() { ["status"] = "disputed", ["truckerPayout"] = 1500m }, "held" },
        { "refunded (1.x)", new() { ["status"] = "refunded", ["truckerPayout"] = 1500m }, "refunded" },
        { "ledger leg released", new() { ["status"] = "released", ["truckerPayout"] = 1500m, ["settlementModel"] = "ledger", ["legStatus"] = new Dictionary<string, object> { ["crop"] = "released", ["transport"] = "released" } }, "released" },
        { "no transport money", new() { ["status"] = "escrowed", ["truckerPayout"] = 0m }, null },
        { "no transport leg at all", new() { ["status"] = "escrowed" }, null }
    };

    [Theory]
    [MemberData(nameof(TruckerLegCases))]
    public void TruckerLegOf_NamesTheStateOfTheTransportLeg(string label, Dictionary<string, object> escrow, string? expected)
    {
        var leg = AdminFunction.TruckerLegOf(escrow);

        (leg?.State).Should().Be(expected, label);
    }

    // ──────────────────────────────────────────────
    // The ledger view
    // ──────────────────────────────────────────────

    [Fact]
    public async Task Ledger_ReturnsOnePageOfRows_InRefOrder_WithPayeeNames_AndATokenForTheNext()
    {
        Escrow("escrow-1");
        _names["farmer-1"] = "Farmer One";
        var lastKey = new Dictionary<string, AttributeValue> { ["escrowId"] = S("escrow-1"), ["ref"] = S("release#trucker#trucker-1") };
        Pages(LedgerTable, Page(lastKey,
            LedgerRow("escrow-1", "release#farmer", "done", "farmer", "farmer-1", "15000"),
            LedgerRow("escrow-1", "release#trucker#trucker-1", "manual", "trucker", "trucker-1", "1500")));

        var response = await GetAsync("/admin/escrows/escrow-1/ledger");

        response.StatusCode.Should().Be(200);
        var body = Json(response);
        body.GetProperty("settlementModel").GetString().Should().Be("ledger");
        var rows = body.GetProperty("rows").EnumerateArray().ToList();
        rows.Should().HaveCount(2);
        rows[0].GetProperty("ref").GetString().Should().Be("release#farmer");
        rows[0].GetProperty("kind").GetString().Should().Be("release");
        rows[0].GetProperty("payeeName").GetString().Should().Be("Farmer One");
        rows[0].GetProperty("amount").GetDecimal().Should().Be(15000m);
        rows[0].GetProperty("status").GetString().Should().Be("done");
        rows[0].GetProperty("gatewayRef").GetString().Should().StartWith("WCL");
        rows[0].GetProperty("legs")[0].GetString().Should().Be("crop");
        rows[1].GetProperty("status").GetString().Should().Be("manual");
        var token = body.GetProperty("nextToken").GetString()!;

        var query = Queries(LedgerTable).Single();
        query.KeyConditionExpression.Should().Be("#id = :id");
        query.ExpressionAttributeValues[":id"].S.Should().Be("escrow-1");
        query.Limit.Should().Be(50);
        query.ExclusiveStartKey.Should().BeNullOrEmpty();

        // The token resumes right after the last row read.
        Pages(LedgerTable, Page(null));
        (await GetAsync("/admin/escrows/escrow-1/ledger", new() { ["nextToken"] = token, ["limit"] = "100" })).StatusCode.Should().Be(200);
        var second = Queries(LedgerTable).Last();
        second.ExclusiveStartKey["ref"].S.Should().Be("release#trucker#trucker-1");
        second.Limit.Should().Be(100);
        NothingWrittenOrScanned();
    }

    [Fact]
    public async Task Ledger_OfALegacyEscrow_AnswersLegacyWithNoRows_AndNeverReadsTheLedger()
    {
        Escrow("escrow-old", ledger: false);

        var response = await GetAsync("/admin/escrows/escrow-old/ledger");

        response.StatusCode.Should().Be(200);
        Json(response).GetProperty("settlementModel").GetString().Should().Be("legacy");
        Json(response).GetProperty("rows").GetArrayLength().Should().Be(0);
        Queries(LedgerTable).Should().BeEmpty();
    }

    [Fact]
    public async Task Ledger_RefusesATokenOfAnotherEscrowOrAnotherIndex()
    {
        Escrow("escrow-1");
        var otherEscrow = PaginationTokenHelper.EncodeNextToken(new() { ["escrowId"] = S("escrow-other"), ["ref"] = S("release#farmer") });
        var otherIndex = PaginationTokenHelper.EncodeNextToken(new() { ["orderId"] = S("order-1"), ["createdAt"] = S("x") });

        foreach (var token in new[] { otherEscrow, otherIndex, "garbage" })
        {
            var response = await GetAsync("/admin/escrows/escrow-1/ledger", new() { ["nextToken"] = token });
            response.StatusCode.Should().Be(400);
            ErrorCode(response).Should().Be("ADMIN_005");
        }

        Queries(LedgerTable).Should().BeEmpty();
    }

    [Theory]
    [InlineData("escrow-missing")]
    [InlineData("escrow%23bad")]
    public async Task Ledger_OfAnUnknownEscrow_Is404(string escrowId)
    {
        var response = await GetAsync($"/admin/escrows/{escrowId}/ledger");

        response.StatusCode.Should().Be(404);
        ErrorCode(response).Should().Be("ADMIN_003");
    }

    [Theory]
    [InlineData("/admin/escrows/escrow-1/ledger")]
    [InlineData("/admin/orders/queue")]
    public async Task TheNewRoutes_RefuseACallerWhoIsNotAnAdmin(string path)
    {
        Escrow("escrow-1");

        var response = await GetAsync(path, role: "buyer");

        response.StatusCode.Should().Be(403);
        _ddb.ReceivedCalls().Should().BeEmpty();
    }

    // ──────────────────────────────────────────────
    // Stuck orders
    // ──────────────────────────────────────────────

    [Fact]
    public async Task StuckOrders_Queue_ReadsAdminQueueIndex_WithTheReasonTheAgeAndTheParties()
    {
        var row = Order("order-1", "accepted",
            ("adminQueue", "open"), ("adminQueuedAt", Iso(Now.AddHours(-50.5))), ("adminQueueReason", "hold_cap"),
            ("adminQueueEscrowId", "escrow-order-order-1"));
        var lastKey = new Dictionary<string, AttributeValue>
        {
            ["orderId"] = S("order-1"),
            ["createdAt"] = row["createdAt"],
            ["adminQueue"] = S("open"),
            ["adminQueuedAt"] = row["adminQueuedAt"]
        };
        Pages("AdminQueueIndex", Page(lastKey, row));

        var response = await GetAsync("/admin/orders/queue", new() { ["limit"] = "100" });

        response.StatusCode.Should().Be(200);
        var body = Json(response);
        body.GetProperty("source").GetString().Should().Be("queue");
        body.GetProperty("asOf").GetString().Should().Be(Iso(Now));
        var order = body.GetProperty("orders").EnumerateArray().Single();
        order.GetProperty("orderId").GetString().Should().Be("order-1");
        order.GetProperty("reason").GetString().Should().Be("hold_cap");
        order.GetProperty("ageHours").GetInt32().Should().Be(50);
        order.GetProperty("status").GetString().Should().Be("accepted");
        order.GetProperty("stage").GetString().Should().Be("in_progress");
        order.GetProperty("buyerName").GetString().Should().Be("Buyer One");
        order.GetProperty("farmerId").GetString().Should().Be("farmer-1");
        order.GetProperty("truckerName").GetString().Should().Be("Trucker One");
        order.GetProperty("escrowId").GetString().Should().Be("escrow-order-order-1");
        order.GetProperty("orderTotal").GetDouble().Should().Be(17250);
        body.GetProperty("nextToken").GetString().Should().NotBeNullOrEmpty();

        var query = Queries("AdminQueueIndex").Single();
        query.TableName.Should().Be(OrdersTable);
        query.Limit.Should().Be(50, "the page is capped at 50");
        query.ScanIndexForward.Should().BeTrue("oldest first");
        NothingWrittenOrScanned();
    }

    [Fact]
    public async Task StuckOrders_Deadline_KeepsOnlyTheOrdersWhoseOwnDeadlinePassedTheGrace()
    {
        // The farmer never answered: 2 hours past the deadline, and the sweep pushed the index time ahead for a retry.
        Order("order-farmer", "funded",
            ("farmerResponse", "pending"), ("farmerDeadlineAt", Iso(Now.AddHours(-2))), ("paymentVerifiedAt", Iso(Now.AddHours(-26))),
            ("farmerReminderAt", Iso(Now.AddHours(-8))), ("nextDeadlineAt", Iso(Now.AddMinutes(40))));
        // Only the reminder is late; the deadline itself is 5 hours away: not stuck.
        Order("order-reminder", "funded",
            ("farmerResponse", "pending"), ("farmerDeadlineAt", Iso(Now.AddHours(5))), ("paymentVerifiedAt", Iso(Now.AddHours(-19))));
        // Due 10 minutes ago: inside the grace, the next sweep run takes it.
        Order("order-fresh", "awaiting_transport", ("buyerChoiceDeadlineAt", Iso(Now.AddMinutes(-10))), ("buyerChoiceReminderAt", Iso(Now.AddHours(-6))));
        // The buyer never chose: 3 hours past.
        Order("order-buyer", "awaiting_transport", ("buyerChoiceDeadlineAt", Iso(Now.AddHours(-3))));
        var lastKey = new Dictionary<string, AttributeValue>
        {
            ["orderId"] = S("order-buyer"),
            ["createdAt"] = S("2026-11-01T09:00:00Z"),
            ["deadlineShard"] = S("0"),
            ["nextDeadlineAt"] = S(Iso(Now))
        };
        static Dictionary<string, AttributeValue> Key(string id) => new() { ["orderId"] = S(id), ["createdAt"] = S("2026-11-01T09:00:00Z") };
        Pages("DeadlineIndex",
            Page(lastKey, Key("order-farmer"), Key("order-reminder"), Key("order-fresh"), Key("order-buyer"), Key("order-deleted")),
            Page(null));

        var response = await GetAsync("/admin/orders/queue", new() { ["source"] = "deadline" });

        response.StatusCode.Should().Be(200);
        var orders = Json(response).GetProperty("orders").EnumerateArray().ToList();
        orders.Select(o => o.GetProperty("orderId").GetString()).Should().Equal("order-farmer", "order-buyer");
        orders[0].GetProperty("reason").GetString().Should().Be("farmer_response");
        orders[0].GetProperty("stuckSince").GetString().Should().Be(Iso(Now.AddHours(-2)));
        orders[0].GetProperty("ageHours").GetInt32().Should().Be(2);
        orders[1].GetProperty("reason").GetString().Should().Be("buyer_choice");
        orders[1].GetProperty("ageHours").GetInt32().Should().Be(3);
        Json(response).TryGetProperty("nextToken", out _).Should().BeFalse("both partitions of the index ended");

        var queries = Queries("DeadlineIndex");
        queries.Should().HaveCount(3);
        queries[0].ExpressionAttributeValues[":due"].S.Should().Be(Iso(Now.AddHours(1)), "rows the sweep pushed ahead for a retry are read too");
        queries[0].ExpressionAttributeValues[":shard"].S.Should().Be(OrderDeadlines.Shard);
        queries[0].Limit.Should().Be(20);
        queries[1].Limit.Should().Be(18, "each read asks only for the rows still missing");
        queries[1].ExclusiveStartKey["orderId"].S.Should().Be("order-buyer");
        // FUP-170: then the watch partition, from its start.
        queries[2].ExpressionAttributeValues[":shard"].S.Should().Be(OrderDeadlines.WatchShard);
        queries[2].ExclusiveStartKey.Should().BeNullOrEmpty();
        queries[2].Limit.Should().Be(18);
        NothingWrittenOrScanned();
    }

    [Fact]
    public async Task StuckOrders_Deadline_ListsThePickupWaits_FromTheWatchPartition_AfterTheSweepsPartition()
    {
        // FUP-170: past its pickup deadline with the notice sent; its entry in the sweep's partition lags.
        Order("order-waiting", "accepted",
            ("pickupDeadlineAt", Iso(Now.AddHours(-30))), ("pickupOverdueNotifiedAt", Iso(Now.AddHours(-29.75))));
        // The notice went out 10 minutes after a deadline 20 minutes ago: inside the grace.
        Order("order-fresh", "accepted",
            ("pickupDeadlineAt", Iso(Now.AddMinutes(-20))), ("pickupOverdueNotifiedAt", Iso(Now.AddMinutes(-10))));
        // Picked up since: its watch entry lags.
        Order("order-picked", "pickup_confirmed",
            ("pickupDeadlineAt", Iso(Now.AddHours(-50))), ("pickupOverdueNotifiedAt", Iso(Now.AddHours(-49))));
        Order("order-farmer", "funded",
            ("farmerResponse", "pending"), ("farmerDeadlineAt", Iso(Now.AddHours(-2))), ("paymentVerifiedAt", Iso(Now.AddHours(-26))));
        static Dictionary<string, AttributeValue> Key(string id) => new() { ["orderId"] = S(id), ["createdAt"] = S("2026-11-01T09:00:00Z") };
        Pages("DeadlineIndex",
            Page(null, Key("order-waiting"), Key("order-farmer")),
            Page(null, Key("order-picked"), Key("order-waiting"), Key("order-fresh")));

        var response = await GetAsync("/admin/orders/queue", new() { ["source"] = "deadline" });

        response.StatusCode.Should().Be(200);
        var orders = Json(response).GetProperty("orders").EnumerateArray().ToList();
        orders.Select(o => o.GetProperty("orderId").GetString()).Should().Equal("order-farmer", "order-waiting");
        orders[1].GetProperty("reason").GetString().Should().Be("pickup_waiting");
        orders[1].GetProperty("stuckSince").GetString().Should().Be(Iso(Now.AddHours(-30)));
        orders[1].GetProperty("ageHours").GetInt32().Should().Be(30);
        orders[1].GetProperty("status").GetString().Should().Be("accepted");
        Json(response).TryGetProperty("nextToken", out _).Should().BeFalse();
        Queries("DeadlineIndex").Select(q => q.ExpressionAttributeValues[":shard"].S)
            .Should().Equal(OrderDeadlines.Shard, OrderDeadlines.WatchShard);
        NothingWrittenOrScanned();
    }

    [Fact]
    public async Task StuckOrders_Deadline_ThePageThatEndsTheSweepsPartition_HandsBackTheWatchPartitionsStart()
    {
        Order("order-farmer", "funded",
            ("farmerResponse", "pending"), ("farmerDeadlineAt", Iso(Now.AddHours(-2))), ("paymentVerifiedAt", Iso(Now.AddHours(-26))));
        Pages("DeadlineIndex", Page(null, new Dictionary<string, AttributeValue> { ["orderId"] = S("order-farmer") }));

        var first = await GetAsync("/admin/orders/queue", new() { ["source"] = "deadline", ["limit"] = "1" });

        Json(first).GetProperty("orders").EnumerateArray().Single().GetProperty("orderId").GetString().Should().Be("order-farmer");
        var token = Json(first).GetProperty("nextToken").GetString()!;
        var decoded = PaginationTokenHelper.DecodeNextToken(token)!;
        decoded.Keys.Should().Equal("deadlineShard");
        decoded["deadlineShard"].S.Should().Be(OrderDeadlines.WatchShard);
        Queries("DeadlineIndex").Should().ContainSingle("the page was full");

        var second = await GetAsync("/admin/orders/queue", new() { ["source"] = "deadline", ["limit"] = "1", ["nextToken"] = token });

        second.StatusCode.Should().Be(200);
        Json(second).GetProperty("orders").GetArrayLength().Should().Be(0);
        Json(second).TryGetProperty("nextToken", out _).Should().BeFalse("the watch partition ended");
        var watch = Queries("DeadlineIndex").Last();
        watch.ExpressionAttributeValues[":shard"].S.Should().Be(OrderDeadlines.WatchShard);
        watch.ExclusiveStartKey.Should().BeNullOrEmpty();
    }

    [Fact]
    public async Task StuckOrders_Deadline_AKeyOfTheWatchPartition_ResumesThere_AnUnknownPartitionIsRefused()
    {
        Dictionary<string, AttributeValue> KeyIn(string shard) => new()
        {
            ["orderId"] = S("order-x"),
            ["createdAt"] = S("2026-11-01T09:00:00Z"),
            ["deadlineShard"] = S(shard),
            ["nextDeadlineAt"] = S(Iso(Now.AddHours(-3)))
        };

        var resumed = await GetAsync("/admin/orders/queue",
            new() { ["source"] = "deadline", ["nextToken"] = PaginationTokenHelper.EncodeNextToken(KeyIn(OrderDeadlines.WatchShard)) });

        resumed.StatusCode.Should().Be(200);
        var query = Queries("DeadlineIndex").Single();
        query.ExpressionAttributeValues[":shard"].S.Should().Be(OrderDeadlines.WatchShard);
        query.ExclusiveStartKey["orderId"].S.Should().Be("order-x");

        foreach (var bad in new[]
                 {
                     KeyIn("7"),
                     new Dictionary<string, AttributeValue> { ["deadlineShard"] = S(OrderDeadlines.Shard) },
                     new Dictionary<string, AttributeValue> { ["deadlineShard"] = S(OrderDeadlines.WatchShard), ["orderId"] = S("order-x") }
                 })
        {
            var refused = await GetAsync("/admin/orders/queue",
                new() { ["source"] = "deadline", ["nextToken"] = PaginationTokenHelper.EncodeNextToken(bad) });
            refused.StatusCode.Should().Be(400);
            ErrorCode(refused).Should().Be("ADMIN_005");
        }

        Queries("DeadlineIndex").Should().ContainSingle();
    }

    [Fact]
    public async Task StuckOrders_Deadline_StopsAfterFiveIndexPages_AndHandsBackTheToken()
    {
        var lastKey = new Dictionary<string, AttributeValue>
        {
            ["orderId"] = S("order-x"),
            ["createdAt"] = S("2026-11-01T09:00:00Z"),
            ["deadlineShard"] = S("0"),
            ["nextDeadlineAt"] = S(Iso(Now))
        };
        Pages("DeadlineIndex", Enumerable.Range(0, 8).Select(_ => Page(lastKey)).ToArray());

        var response = await GetAsync("/admin/orders/queue", new() { ["source"] = "deadline" });

        Json(response).GetProperty("orders").GetArrayLength().Should().Be(0);
        Json(response).GetProperty("nextToken").GetString().Should().NotBeNullOrEmpty();
        Queries("DeadlineIndex").Should().HaveCount(5);
    }

    [Fact]
    public async Task StuckOrders_RefuseAnUnknownSource_AndATokenOfTheOtherIndex()
    {
        var badSource = await GetAsync("/admin/orders/queue", new() { ["source"] = "all" });
        badSource.StatusCode.Should().Be(400);
        ErrorCode(badSource).Should().Be("ADMIN_005");

        var queueToken = PaginationTokenHelper.EncodeNextToken(new()
        {
            ["orderId"] = S("order-1"),
            ["createdAt"] = S("x"),
            ["adminQueue"] = S("open"),
            ["adminQueuedAt"] = S("y")
        });
        var wrongIndex = await GetAsync("/admin/orders/queue", new() { ["source"] = "deadline", ["nextToken"] = queueToken });
        wrongIndex.StatusCode.Should().Be(400);
        ErrorCode(wrongIndex).Should().Be("ADMIN_005");

        (await GetAsync("/admin/orders/queue", new() { ["source"] = "queue", ["nextToken"] = queueToken })).StatusCode.Should().Be(200);
        Queries("AdminQueueIndex").Single().ExclusiveStartKey["adminQueuedAt"].S.Should().Be("y");
        Queries("DeadlineIndex").Should().BeEmpty();
    }

    [Fact]
    public void OverdueDeadline_NamesThePickupAndTheTruckerDeadline_AndIgnoresAnOrderWithNothingPending()
    {
        var pickup = new Dictionary<string, object> { ["status"] = "accepted", ["pickupDeadlineAt"] = Iso(Now.AddHours(-30)) };
        AdminFunction.OverdueDeadline(pickup, Now)!.Value.Kind.Should().Be(OrderDeadlines.Kind.PickupOverdue);

        var trucker = new Dictionary<string, object>
        {
            ["status"] = "funded",
            ["farmerResponse"] = "accepted",
            ["truckerResponse"] = "pending",
            ["truckerAskedAt"] = Iso(Now.AddHours(-30)),
            ["truckerDeadlineAt"] = Iso(Now.AddHours(-6))
        };
        AdminFunction.OverdueDeadline(trucker, Now)!.Value.Kind.Should().Be(OrderDeadlines.Kind.TruckerResponse);

        // FUP-170: the notice went out and the order still waits for its pickup: listed as waiting, from its deadline.
        pickup[OrderCommitment.PickupOverdueNotifiedAtField] = Iso(Now.AddHours(-29));
        AdminFunction.OverdueDeadline(pickup, Now).Should().Be(new OrderDeadline(Now.AddHours(-30), OrderDeadlines.Kind.PickupWaiting));
        AdminFunction.OverdueDeadline(new Dictionary<string, object> { ["status"] = "delivered" }, Now).Should().BeNull();

        // Exactly at the grace it counts; a minute inside it does not.
        var edge = new Dictionary<string, object> { ["status"] = "awaiting_transport", ["buyerChoiceDeadlineAt"] = Iso(Now - AdminFunction.StuckOrderGrace) };
        AdminFunction.OverdueDeadline(edge, Now).Should().NotBeNull();
        edge["buyerChoiceDeadlineAt"] = Iso(Now - AdminFunction.StuckOrderGrace + TimeSpan.FromMinutes(1));
        AdminFunction.OverdueDeadline(edge, Now).Should().BeNull();
    }
}
