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
/// CHAIN-01 regression tests for the admin change_role action: an admin must not be able
/// to assign an arbitrary role string (notably "admin", which EscrowService trusts for
/// fund release). Only the three marketplace roles are assignable.
/// </summary>
public class AdminFunctionTests
{
    private const string UsersTable = "users-test";

    private readonly IAmazonDynamoDB _dynamoDb = Substitute.For<IAmazonDynamoDB>();
    private readonly AdminFunction _function;
    private readonly TestLambdaContext _context = new();

    public AdminFunctionTests()
    {
        _dynamoDb.UpdateItemAsync(Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>())
            .Returns(new UpdateItemResponse());
        _function = new AdminFunction(_dynamoDb, UsersTable);
    }

    private static APIGatewayProxyRequest ChangeRoleRequest(string userId, string newRole)
    {
        // IsAdmin() accepts a "role"/"custom:role" authorizer claim equal to "admin".
        var authorizer = new APIGatewayCustomAuthorizerContext
        {
            { "sub", "admin-1" },
            { "role", "admin" },
        };

        return new APIGatewayProxyRequest
        {
            HttpMethod = "POST",
            Path = $"/admin/users/{userId}/action",
            Body = JsonSerializer.Serialize(new { action = "change_role", newRole }),
            RequestContext = new APIGatewayProxyRequest.ProxyRequestContext { Authorizer = authorizer },
        };
    }

    [Fact]
    public async Task ChangeRole_ToAdmin_IsRejected400_AndDoesNotWrite()
    {
        var response = await _function.FunctionHandler(
            ChangeRoleRequest("user-1", "admin"), _context);

        response.StatusCode.Should().Be(400);
        response.Body.Should().Contain("ADMIN_011");
        await _dynamoDb.DidNotReceive().UpdateItemAsync(
            Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ChangeRole_ToInvalidString_IsRejected400_AndDoesNotWrite()
    {
        var response = await _function.FunctionHandler(
            ChangeRoleRequest("user-1", "superuser"), _context);

        response.StatusCode.Should().Be(400);
        response.Body.Should().Contain("ADMIN_011");
        await _dynamoDb.DidNotReceive().UpdateItemAsync(
            Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ChangeRole_ToValidRole_Succeeds_AndStoresLowercaseRole()
    {
        var response = await _function.FunctionHandler(
            ChangeRoleRequest("user-1", "trucker"), _context);

        response.StatusCode.Should().Be(200);
        await _dynamoDb.Received(1).UpdateItemAsync(
            Arg.Is<UpdateItemRequest>(r =>
                r.TableName == UsersTable
                && r.ExpressionAttributeValues[":newRole"].S == "trucker"),
            Arg.Any<CancellationToken>());
    }

    // ──────────────────────────────────────────────
    // Transaction monitor (GET /admin/transactions[/{orderId}])
    // ──────────────────────────────────────────────

    private const string OrdersTable = "orders-test";
    private const string EscrowsTable = "escrows-test";
    private const string DeliveriesTable = "deliveries-test";
    private const string AuditTable = "escrow-audit-test";
    private const string TxAuditTable = "transaction-audit-test";

    private static AttributeValue S(string v) => new() { S = v };
    private static AttributeValue Num(string v) => new() { N = v };

    private static APIGatewayProxyRequest AdminGet(string path)
    {
        var authorizer = new APIGatewayCustomAuthorizerContext
        {
            { "sub", "admin-1" },
            { "role", "admin" },
        };
        return new APIGatewayProxyRequest
        {
            HttpMethod = "GET",
            Path = path,
            RequestContext = new APIGatewayProxyRequest.ProxyRequestContext { Authorizer = authorizer },
        };
    }

    private AdminFunction TransactionFunction(IAmazonDynamoDB db) =>
        new(db, UsersTable, OrdersTable, EscrowsTable, DeliveriesTable, AuditTable);

    [Fact]
    public async Task ListTransactions_ReturnsOrders_WithDerivedStage()
    {
        var db = Substitute.For<IAmazonDynamoDB>();
        db.ScanAsync(Arg.Any<ScanRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ScanResponse
            {
                Items = new List<Dictionary<string, AttributeValue>>
                {
                    new()
                    {
                        ["orderId"] = S("order-1"),
                        ["buyerId"] = S("buyer-1"),
                        ["buyerName"] = S("Almaz"),
                        ["farmerId"] = S("farmer-1"),
                        ["farmerName"] = S("Bekele"),
                        ["cropType"] = S("teff"),
                        ["quantity"] = Num("100"),
                        ["orderTotal"] = Num("5000"),
                        ["status"] = S("in_transit"),
                        ["createdAt"] = S("2026-07-01T00:00:00Z"),
                        ["updatedAt"] = S("2026-07-02T00:00:00Z"),
                    },
                },
                LastEvaluatedKey = new Dictionary<string, AttributeValue>(),
            });

        var response = await TransactionFunction(db)
            .FunctionHandler(AdminGet("/admin/transactions"), _context);

        response.StatusCode.Should().Be(200);
        response.Body.Should().Contain("order-1");
        response.Body.Should().Contain("Almaz");
        // in_transit collapses to the "in_progress" lifecycle stage.
        response.Body.Should().Contain("\"stage\":\"in_progress\"");
    }

    [Fact]
    public async Task GetTransaction_OrderMissing_Returns404()
    {
        var db = Substitute.For<IAmazonDynamoDB>();
        db.QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(new QueryResponse
            {
                Items = new List<Dictionary<string, AttributeValue>>(),
                LastEvaluatedKey = new Dictionary<string, AttributeValue>(),
            });

        var response = await TransactionFunction(db)
            .FunctionHandler(AdminGet("/admin/transactions/order-x"), _context);

        response.StatusCode.Should().Be(404);
        response.Body.Should().Contain("ADMIN_012");
    }

    [Fact]
    public async Task GetTransaction_StitchesOrderEscrowDeliveryAndOrdersTimeline()
    {
        var db = Substitute.For<IAmazonDynamoDB>();

        var orderItem = new Dictionary<string, AttributeValue>
        {
            ["orderId"] = S("order-1"),
            ["buyerId"] = S("buyer-1"),
            ["buyerName"] = S("Almaz"),
            ["farmerId"] = S("farmer-1"),
            ["farmerName"] = S("Bekele"),
            ["cropType"] = S("teff"),
            ["quantity"] = Num("100"),
            ["orderTotal"] = Num("5000"),
            ["status"] = S("settled"),
            ["createdAt"] = S("2026-07-01T08:00:00Z"),
            ["updatedAt"] = S("2026-07-05T00:00:00Z"),
            ["paymentVerifiedAt"] = S("2026-07-01T09:00:00Z"),
        };
        var escrowItem = new Dictionary<string, AttributeValue>
        {
            ["escrowId"] = S("escrow-1"),
            ["deliveryId"] = S("delivery-1"),
            ["orderId"] = S("order-1"),
            ["buyerId"] = S("buyer-1"),
            ["farmerId"] = S("farmer-1"),
            ["amount"] = Num("5000"),
            ["status"] = S("completed"),
            ["createdAt"] = S("2026-07-01T09:05:00Z"),
            ["updatedAt"] = S("2026-07-05T00:00:00Z"),
        };
        var deliveryItem = new Dictionary<string, AttributeValue>
        {
            ["deliveryId"] = S("delivery-1"),
            ["tripId"] = S("trip-1"),
            ["listingId"] = S("listing-1"),
            ["farmerId"] = S("farmer-1"),
            ["farmerName"] = S("Bekele"),
            ["truckerId"] = S("trucker-1"),
            ["truckerName"] = S("Chala"),
            ["buyerId"] = S("buyer-1"),
            ["buyerName"] = S("Almaz"),
            ["qrToken"] = S("qr"),
            ["status"] = S("delivered"),
            ["pickedUpAt"] = S("2026-07-02T10:00:00Z"),
            ["confirmedAt"] = S("2026-07-03T14:00:00Z"),
            ["createdAt"] = S("2026-07-01T09:10:00Z"),
            ["updatedAt"] = S("2026-07-03T14:00:00Z"),
            ["version"] = Num("3"),
        };
        var auditItem = new Dictionary<string, AttributeValue>
        {
            ["auditId"] = S("audit-1"),
            ["escrowId"] = S("escrow-1"),
            ["action"] = S("release"),
            ["fromState"] = S("escrowed"),
            ["toState"] = S("released"),
            ["actor"] = S("system"),
            ["timestamp"] = S("2026-07-03T14:05:00Z"),
        };

        db.QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var req = ci.Arg<QueryRequest>();
                List<Dictionary<string, AttributeValue>> items =
                    req.IndexName == "OrderIndex" ? new() { escrowItem }
                    : req.TableName == OrdersTable ? new() { orderItem }
                    : req.TableName == DeliveriesTable ? new() { deliveryItem }
                    : req.TableName == AuditTable ? new() { auditItem }
                    : new();
                return new QueryResponse
                {
                    Items = items,
                    LastEvaluatedKey = new Dictionary<string, AttributeValue>(),
                };
            });

        // Name enrichment hits BatchGetItem; order already carries names, so return nothing.
        db.BatchGetItemAsync(Arg.Any<BatchGetItemRequest>(), Arg.Any<CancellationToken>())
            .Returns(new BatchGetItemResponse
            {
                Responses = new Dictionary<string, List<Dictionary<string, AttributeValue>>>(),
                UnprocessedKeys = new Dictionary<string, KeysAndAttributes>(),
            });

        var response = await TransactionFunction(db)
            .FunctionHandler(AdminGet("/admin/transactions/order-1"), _context);

        response.StatusCode.Should().Be(200);
        response.Body.Should().Contain("escrow-1");
        response.Body.Should().Contain("delivery-1");
        response.Body.Should().Contain("Chala"); // trucker joined from delivery
        response.Body.Should().Contain("order_created");
        response.Body.Should().Contain("picked_up");
        response.Body.Should().Contain("\"action\":\"release\""); // escrow audit event
        // Timeline is chronological: order_created (08:00) precedes the release (14:05).
        response.Body.IndexOf("order_created", StringComparison.Ordinal)
            .Should().BeLessThan(response.Body.IndexOf("\"action\":\"release\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetTransaction_UsesUnifiedAuditEvents_WhenPresent()
    {
        var db = Substitute.For<IAmazonDynamoDB>();

        var orderItem = new Dictionary<string, AttributeValue>
        {
            ["orderId"] = S("order-1"),
            ["buyerId"] = S("buyer-1"),
            ["buyerName"] = S("Almaz"),
            ["farmerId"] = S("farmer-1"),
            ["farmerName"] = S("Bekele"),
            ["cropType"] = S("teff"),
            ["quantity"] = Num("100"),
            ["orderTotal"] = Num("5000"),
            ["status"] = S("accepted"),
            ["createdAt"] = S("2026-07-01T08:00:00Z"),
            ["updatedAt"] = S("2026-07-01T09:00:00Z"),
        };
        // A real unified audit event carrying who + where (ipAddress).
        var unifiedEvent = new Dictionary<string, AttributeValue>
        {
            ["auditId"] = S("txaudit-1"),
            ["transactionId"] = S("order-1"),
            ["entityType"] = S("order"),
            ["entityId"] = S("order-1"),
            ["action"] = S("farmer_responded"),
            ["fromState"] = S("funded"),
            ["toState"] = S("accepted"),
            ["actor"] = S("farmer-1"),
            ["ipAddress"] = S("203.0.113.7"),
            ["timestamp"] = S("2026-07-01T09:00:00Z"),
        };

        db.QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var req = ci.Arg<QueryRequest>();
                List<Dictionary<string, AttributeValue>> items =
                    req.IndexName == "OrderIndex" ? new() // no escrow yet
                    : req.TableName == OrdersTable ? new() { orderItem }
                    : req.TableName == TxAuditTable ? new() { unifiedEvent }
                    : new();
                return new QueryResponse
                {
                    Items = items,
                    LastEvaluatedKey = new Dictionary<string, AttributeValue>(),
                };
            });

        var fn = new AdminFunction(
            db, UsersTable, OrdersTable, EscrowsTable, DeliveriesTable, AuditTable, TxAuditTable);
        var response = await fn.FunctionHandler(AdminGet("/admin/transactions/order-1"), _context);

        response.StatusCode.Should().Be(200);
        response.Body.Should().Contain("farmer_responded");   // from the unified event
        response.Body.Should().Contain("203.0.113.7");         // the audit "where"
        // Unified order events supersede the synthesised reconstruction — the
        // fallback "order_created" is NOT added when unified order events exist.
        response.Body.Should().NotContain("order_created");
    }

    [Fact]
    public async Task ListTransactions_Search_FindsMatchBeyondFirstScanPage()
    {
        // Regression for the "search silently misses matches" bug: DynamoDB applies
        // a filter per page, so the handler must page through — not stop at page 1.
        var db = Substitute.For<IAmazonDynamoDB>();

        var page1 = new ScanResponse
        {
            Items = new List<Dictionary<string, AttributeValue>>(), // no matches this page
            ScannedCount = 1,
            LastEvaluatedKey = new Dictionary<string, AttributeValue>
            {
                ["orderId"] = S("order-a"), // non-empty → a second page exists
            },
        };
        var page2 = new ScanResponse
        {
            Items = new List<Dictionary<string, AttributeValue>>
            {
                new()
                {
                    ["orderId"] = S("order-z"),
                    ["buyerId"] = S("buyer-z"),
                    ["buyerName"] = S("Zewde"),
                    ["farmerId"] = S("farmer-1"),
                    ["cropType"] = S("maize"),
                    ["status"] = S("delivered"),
                    ["createdAt"] = S("2026-07-02T00:00:00Z"),
                    ["updatedAt"] = S("2026-07-02T00:00:00Z"),
                },
            },
            ScannedCount = 1,
            LastEvaluatedKey = new Dictionary<string, AttributeValue>(),
        };

        db.ScanAsync(Arg.Any<ScanRequest>(), Arg.Any<CancellationToken>())
            .Returns(page1, page2);

        var request = AdminGet("/admin/transactions");
        request.QueryStringParameters = new Dictionary<string, string> { ["search"] = "Zewde" };
        var response = await TransactionFunction(db).FunctionHandler(request, _context);

        response.StatusCode.Should().Be(200);
        response.Body.Should().Contain("order-z"); // found on page 2, not dropped
        response.Body.Should().Contain("Zewde");
    }

    [Fact]
    public async Task GetTransaction_SynthesisesOrderLeg_WhenOnlyDeliveryHasUnifiedEvents()
    {
        // Per-entity gating (M2): a transaction that straddles the Phase-2 deploy —
        // unified events for the delivery leg but none for the order leg — must still
        // synthesise the order history rather than dropping it.
        var db = Substitute.For<IAmazonDynamoDB>();

        var orderItem = new Dictionary<string, AttributeValue>
        {
            ["orderId"] = S("order-1"),
            ["buyerId"] = S("buyer-1"),
            ["buyerName"] = S("Almaz"),
            ["farmerId"] = S("farmer-1"),
            ["cropType"] = S("teff"),
            ["quantity"] = Num("100"),
            ["orderTotal"] = Num("5000"),
            ["status"] = S("in_transit"),
            ["createdAt"] = S("2026-07-01T08:00:00Z"),
            ["updatedAt"] = S("2026-07-02T10:00:00Z"),
        };
        var deliveryItem = new Dictionary<string, AttributeValue>
        {
            ["deliveryId"] = S("delivery-1"),
            ["tripId"] = S("trip-1"),
            ["listingId"] = S("listing-1"),
            ["farmerId"] = S("farmer-1"),
            ["truckerId"] = S("trucker-1"),
            ["truckerName"] = S("Chala"),
            ["buyerId"] = S("buyer-1"),
            ["qrToken"] = S("qr"),
            ["status"] = S("inTransit"),
            ["pickedUpAt"] = S("2026-07-02T10:00:00Z"),
            ["createdAt"] = S("2026-07-01T09:10:00Z"),
            ["updatedAt"] = S("2026-07-02T10:00:00Z"),
            ["version"] = Num("2"),
        };
        // Only a delivery unified event (surfaces the deliveryId too — no escrow).
        var deliveryEvent = new Dictionary<string, AttributeValue>
        {
            ["auditId"] = S("txaudit-1"),
            ["transactionId"] = S("order-1"),
            ["entityType"] = S("delivery"),
            ["entityId"] = S("delivery-1"),
            ["action"] = S("picked_up"),
            ["toState"] = S("pickedUp"),
            ["actor"] = S("trucker-1"),
            ["timestamp"] = S("2026-07-02T10:00:00Z"),
        };

        db.QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var req = ci.Arg<QueryRequest>();
                List<Dictionary<string, AttributeValue>> items =
                    req.IndexName == "OrderIndex" ? new()               // no escrow
                    : req.IndexName == "EntityIndex" ? new()            // dedupe query
                    : req.TableName == OrdersTable ? new() { orderItem }
                    : req.TableName == DeliveriesTable ? new() { deliveryItem }
                    : req.TableName == TxAuditTable ? new() { deliveryEvent }
                    : new();
                return new QueryResponse
                {
                    Items = items,
                    LastEvaluatedKey = new Dictionary<string, AttributeValue>(),
                };
            });

        var fn = new AdminFunction(
            db, UsersTable, OrdersTable, EscrowsTable, DeliveriesTable, AuditTable, TxAuditTable);
        var response = await fn.FunctionHandler(AdminGet("/admin/transactions/order-1"), _context);

        response.StatusCode.Should().Be(200);
        response.Body.Should().Contain("picked_up");      // unified delivery event
        response.Body.Should().Contain("order_created");  // synthesised order leg survives
        response.Body.Should().Contain("delivery-1");     // delivery recovered without escrow
    }

    [Fact]
    public async Task AuditLog_ReturnsRecentUnifiedEntries()
    {
        var db = Substitute.For<IAmazonDynamoDB>();
        db.ScanAsync(Arg.Any<ScanRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ScanResponse
            {
                Items = new List<Dictionary<string, AttributeValue>>
                {
                    new()
                    {
                        ["auditId"] = S("txaudit-1"),
                        ["transactionId"] = S("order-1"),
                        ["entityType"] = S("order"),
                        ["entityId"] = S("order-1"),
                        ["action"] = S("order_created"),
                        ["actor"] = S("buyer-1"),
                        ["ipAddress"] = S("203.0.113.7"),
                        ["timestamp"] = S("2026-07-01T08:00:00Z"),
                    },
                },
                LastEvaluatedKey = new Dictionary<string, AttributeValue>(),
            });

        var fn = new AdminFunction(
            db, UsersTable, OrdersTable, EscrowsTable, DeliveriesTable, AuditTable, TxAuditTable);
        var response = await fn.FunctionHandler(AdminGet("/admin/audit"), _context);

        response.StatusCode.Should().Be(200);
        response.Body.Should().Contain("txaudit-1");
        response.Body.Should().Contain("order_created");
        response.Body.Should().Contain("203.0.113.7");
    }

    // Regression: role counts filtered on capitalized 'Farmer'/'Trucker'/'Buyer'
    // (stored lowercase) so every per-role count was 0. Pin the lowercase values.
    [Fact]
    public async Task DashboardStats_FiltersUserRoles_UsingLowercaseValues()
    {
        var db = Substitute.For<IAmazonDynamoDB>();
        var scans = new List<ScanRequest>();
        db.ScanAsync(Arg.Do<ScanRequest>(scans.Add), Arg.Any<CancellationToken>())
            .Returns(new ScanResponse
            {
                Count = 0,
                Items = new List<Dictionary<string, AttributeValue>>(),
                LastEvaluatedKey = new Dictionary<string, AttributeValue>(),
            });

        var fn = new AdminFunction(
            db, UsersTable, OrdersTable, EscrowsTable, DeliveriesTable, AuditTable, TxAuditTable);
        var response = await fn.FunctionHandler(AdminGet("/admin/dashboard/stats"), _context);

        response.StatusCode.Should().Be(200);
        var roleValues = scans
            .Where(s => s.TableName == UsersTable
                && s.ExpressionAttributeNames.TryGetValue("#field", out var f)
                && f == "role")
            .Select(s => s.ExpressionAttributeValues[":value"].S)
            .ToList();
        roleValues.Should().Contain(new[] { "farmer", "trucker", "buyer" });
        roleValues.Should().NotContain("Farmer");
        roleValues.Should().NotContain("Buyer");
    }

    // Regression: revenue analytics counted the transient 'released' state; the
    // terminal settled state is 'completed'. Pin the escrow filter value.
    [Fact]
    public async Task RevenueAnalytics_CountsCompletedEscrows_NotReleased()
    {
        var db = Substitute.For<IAmazonDynamoDB>();
        var scans = new List<ScanRequest>();
        db.ScanAsync(Arg.Do<ScanRequest>(scans.Add), Arg.Any<CancellationToken>())
            .Returns(new ScanResponse
            {
                Count = 0,
                Items = new List<Dictionary<string, AttributeValue>>(),
                LastEvaluatedKey = new Dictionary<string, AttributeValue>(),
            });

        var fn = new AdminFunction(
            db, UsersTable, OrdersTable, EscrowsTable, DeliveriesTable, AuditTable, TxAuditTable);
        var response = await fn.FunctionHandler(AdminGet("/admin/analytics/revenue"), _context);

        response.StatusCode.Should().Be(200);
        var escrowStatusValues = scans
            .Where(s => s.TableName == EscrowsTable
                && s.ExpressionAttributeNames.ContainsValue("status"))
            .Select(s => s.ExpressionAttributeValues[":value"].S)
            .ToList();
        escrowStatusValues.Should().Contain("completed");
        escrowStatusValues.Should().NotContain("released");
    }
}
