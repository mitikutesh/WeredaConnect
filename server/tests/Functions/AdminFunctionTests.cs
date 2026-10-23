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
using WeredaConnect.Server.Services;
using WeredaConnect.Server.Models;

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

    private static APIGatewayProxyRequest AdminGetWithQuery(string path, Dictionary<string, string> query)
    {
        var request = AdminGet(path);
        request.QueryStringParameters = query;
        return request;
    }

    [Fact]
    public async Task GetEscrow_AHeldEscrow_CarriesTheAdminOnlyHoldFields()
    {
        // FRD-03: the admin sees why and from what the sweep held it; party reads never carry these.
        var db = Substitute.For<IAmazonDynamoDB>();
        db.QueryAsync(Arg.Is<QueryRequest>(r => r.TableName == EscrowsTable), Arg.Any<CancellationToken>())
            .Returns(new QueryResponse
            {
                Items = new List<Dictionary<string, AttributeValue>>
                {
                    new()
                    {
                        ["escrowId"] = S("escrow-order-ord-9"), ["createdAt"] = S("2026-10-01T10:00:00Z"),
                        ["updatedAt"] = S("2026-10-20T10:00:00Z"), ["expiresAt"] = S("2026-10-08T10:00:00Z"),
                        ["status"] = S("held"), ["buyerId"] = S("buyer-1"), ["farmerId"] = S("farmer-1"),
                        ["deliveryId"] = S("del-9"), ["currency"] = S("ETB"),
                        ["holdReason"] = S("account_suspended"), ["heldAt"] = S("2026-10-20T10:00:00Z"),
                        ["heldBy"] = S("system"), ["heldFromStatus"] = S("escrowed"), ["heldFor"] = S("release")
                    }
                }
            });
        db.BatchGetItemAsync(Arg.Any<BatchGetItemRequest>(), Arg.Any<CancellationToken>())
            .Returns(new BatchGetItemResponse
            {
                Responses = new Dictionary<string, List<Dictionary<string, AttributeValue>>>(),
                UnprocessedKeys = new Dictionary<string, KeysAndAttributes>(),
            });

        var response = await TransactionFunction(db).FunctionHandler(AdminGet("/admin/escrows/escrow-order-ord-9"), _context);

        response.StatusCode.Should().Be(200, response.Body);
        response.Body.Should().Contain("\"status\":\"held\"")
            .And.Contain("\"holdReason\":\"account_suspended\"")
            .And.Contain("\"heldAt\":\"2026-10-20T10:00:00Z\"")
            .And.Contain("\"heldBy\":\"system\"")
            .And.Contain("\"heldFromStatus\":\"escrowed\"")
            .And.Contain("\"heldFor\":\"release\"");
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
            ["truckerPhone"] = S("+251922000001"),
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
        response.Body.Should().Contain("251922000001");   // ESC-48: admins still see party phones
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

    // ──────────────────────────────────────────────
    // Fraud signal review (POST /admin/fraud-signals/{id}/action)
    // ──────────────────────────────────────────────

    private const string FraudSignalsTable = "fraud-signals-test";

    private static APIGatewayProxyRequest FraudActionRequest(string signalId, string action)
    {
        var authorizer = new APIGatewayCustomAuthorizerContext
        {
            { "sub", "admin-1" },
            { "role", "admin" },
        };
        return new APIGatewayProxyRequest
        {
            HttpMethod = "POST",
            Path = $"/admin/fraud-signals/{signalId}/action",
            Body = JsonSerializer.Serialize(new { action }),
            RequestContext = new APIGatewayProxyRequest.ProxyRequestContext { Authorizer = authorizer },
        };
    }

    [Fact]
    public async Task FraudAction_UpdatesByCompositeKey_AndSetsStatusReviewed()
    {
        var signalKey = "2026-07-11T10:00:00.0000000Z#PRICE_ANOMALY";
        var signalId = WeredaConnect.Server.Services.FraudDetectionService
            .BuildSignalId("farmer-1", signalKey);
        var fn = new AdminFunction(_dynamoDb, UsersTable, FraudSignalsTable);

        var response = await fn.FunctionHandler(
            FraudActionRequest(signalId, "approve"), _context);

        response.StatusCode.Should().Be(200);
        response.Body.Should().Contain("\"status\":\"reviewed\"");
        await _dynamoDb.Received(1).UpdateItemAsync(
            Arg.Is<UpdateItemRequest>(r =>
                r.TableName == FraudSignalsTable
                && r.Key["userId"].S == "farmer-1"
                && r.Key["signalKey"].S == signalKey
                && r.ExpressionAttributeValues[":status"].S == "reviewed"
                && r.ExpressionAttributeValues[":resolution"].S == "approve"
                // FRD-05: only a signal row (legacy rows carry no recordType) can be reviewed.
                && r.ConditionExpression == "attribute_exists(userId) AND (attribute_not_exists(#recordType) OR #recordType = :signalRecord)"
                && r.ExpressionAttributeValues[":signalRecord"].S == "signal"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FraudAction_MalformedSignalId_Returns400_AndDoesNotWrite()
    {
        var fn = new AdminFunction(_dynamoDb, UsersTable, FraudSignalsTable);

        var response = await fn.FunctionHandler(
            FraudActionRequest("not-a-valid-id!!", "approve"), _context);

        response.StatusCode.Should().Be(400);
        response.Body.Should().Contain("ADMIN_005");
        await _dynamoDb.DidNotReceive().UpdateItemAsync(
            Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FraudAction_UnknownSignal_Returns404()
    {
        var signalId = WeredaConnect.Server.Services.FraudDetectionService
            .BuildSignalId("farmer-1", "2026-07-11T10:00:00.0000000Z#GPS_ANOMALY");
        _dynamoDb.UpdateItemAsync(Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>())
            .Returns<UpdateItemResponse>(_ => throw new ConditionalCheckFailedException("no item"));
        var fn = new AdminFunction(_dynamoDb, UsersTable, FraudSignalsTable);

        var response = await fn.FunctionHandler(
            FraudActionRequest(signalId, "reject"), _context);

        response.StatusCode.Should().Be(404);
        response.Body.Should().Contain("ADMIN_006");
    }

    // ===== Account controls reach Cognito (suspend / unsuspend / change_role) =====

    private static APIGatewayProxyRequest UserActionRequest(string userId, object body)
    {
        var authorizer = new APIGatewayCustomAuthorizerContext
        {
            { "sub", "admin-1" },
            { "role", "admin" },
        };

        return new APIGatewayProxyRequest
        {
            HttpMethod = "POST",
            Path = $"/admin/users/{userId}/action",
            Body = JsonSerializer.Serialize(body),
            RequestContext = new APIGatewayProxyRequest.ProxyRequestContext { Authorizer = authorizer },
        };
    }

    private ICognitoAuthService CognitoWithPhone(string phone, bool succeed = true)
    {
        var cognito = Substitute.For<ICognitoAuthService>();
        var outcome = succeed
            ? AuthResult<bool>.Success(true)
            : AuthResult<bool>.Failure(503, "AUTH_008", "Unavailable.");
        cognito.AdminDisableUserAsync(Arg.Any<string>()).Returns(outcome);
        cognito.AdminEnableUserAsync(Arg.Any<string>()).Returns(outcome);
        cognito.AdminGlobalSignOutAsync(Arg.Any<string>()).Returns(outcome);
        cognito.AdminSetRoleAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(outcome);
        _dynamoDb.GetItemAsync(Arg.Any<GetItemRequest>(), Arg.Any<CancellationToken>())
            .Returns(new GetItemResponse
            {
                Item = new Dictionary<string, AttributeValue>
                {
                    { "phoneNumber", new AttributeValue { S = phone } }
                }
            });
        return cognito;
    }

    [Fact]
    public async Task Suspend_DisablesCognitoUserAndSignsOutEverywhere()
    {
        var cognito = CognitoWithPhone("+251911000001");
        var fn = new AdminFunction(_dynamoDb, UsersTable, cognitoAuth: cognito);

        var response = await fn.FunctionHandler(
            UserActionRequest("user-1", new { action = "suspend", reason = "fraud" }), _context);

        response.StatusCode.Should().Be(200);
        response.Body.Should().Contain("completed successfully");
        await cognito.Received(1).AdminDisableUserAsync("+251911000001");
        await cognito.Received(1).AdminGlobalSignOutAsync("+251911000001");
        await cognito.DidNotReceive().AdminEnableUserAsync(Arg.Any<string>());
    }

    [Fact]
    public async Task Unsuspend_ReEnablesCognitoUser()
    {
        var cognito = CognitoWithPhone("+251911000001");
        var fn = new AdminFunction(_dynamoDb, UsersTable, cognitoAuth: cognito);

        var response = await fn.FunctionHandler(
            UserActionRequest("user-1", new { action = "unsuspend" }), _context);

        response.StatusCode.Should().Be(200);
        await cognito.Received(1).AdminEnableUserAsync("+251911000001");
        await cognito.DidNotReceive().AdminGlobalSignOutAsync(Arg.Any<string>());
    }

    [Fact]
    public async Task ChangeRole_WritesCognitoRoleAndSignsOutOldTokens()
    {
        var cognito = CognitoWithPhone("+251911000001");
        var fn = new AdminFunction(_dynamoDb, UsersTable, cognitoAuth: cognito);

        var response = await fn.FunctionHandler(
            UserActionRequest("user-1", new { action = "change_role", newRole = "Buyer" }), _context);

        response.StatusCode.Should().Be(200);
        await cognito.Received(1).AdminSetRoleAsync("+251911000001", "buyer");
        await cognito.Received(1).AdminGlobalSignOutAsync("+251911000001");
    }

    [Fact]
    public async Task Suspend_WhenCognitoFails_KeepsTheFlagAndTellsTheAdmin()
    {
        var cognito = CognitoWithPhone("+251911000001", succeed: false);
        var fn = new AdminFunction(_dynamoDb, UsersTable, cognitoAuth: cognito);

        var response = await fn.FunctionHandler(
            UserActionRequest("user-1", new { action = "suspend" }), _context);

        response.StatusCode.Should().Be(200);
        response.Body.Should().Contain("sign-in service update failed");
        await _dynamoDb.Received(1).UpdateItemAsync(Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Suspend_WithoutCognitoClient_ReportsThatSessionsWereNotEnded()
    {
        var response = await _function.FunctionHandler(
            UserActionRequest("user-1", new { action = "suspend" }), _context);

        response.StatusCode.Should().Be(200);
        response.Body.Should().Contain("sign-in service was not updated");
    }

    [Fact]
    public async Task Verify_DoesNotTouchCognito()
    {
        var cognito = CognitoWithPhone("+251911000001");
        var fn = new AdminFunction(_dynamoDb, UsersTable, cognitoAuth: cognito);

        var response = await fn.FunctionHandler(
            UserActionRequest("user-1", new { action = "verify" }), _context);

        response.StatusCode.Should().Be(200);
        await cognito.DidNotReceive().AdminGlobalSignOutAsync(Arg.Any<string>());
        await cognito.DidNotReceive().AdminDisableUserAsync(Arg.Any<string>());
    }

    // ===== Dispute resolution records the verdict; the cron settles the money =====

    private static Dictionary<string, AttributeValue> DisputedEscrow(params (string Key, string Value)[] extra)
    {
        var item = new Dictionary<string, AttributeValue>
        {
            { "escrowId", S("escrow-9") },
            { "createdAt", S("2026-09-20T10:00:00Z") },
            { "status", S("disputed") },
            { "orderId", S("order-9") },
            { "disputedBy", S("buyer-9") }
        };
        foreach (var (key, value) in extra)
        {
            item[key] = S(value);
        }

        return item;
    }

    private static APIGatewayProxyRequest ResolveRequest(object body)
        => AdminGroupRequest("POST", "/admin/disputes/escrow-9/resolve", JsonSerializer.Serialize(body));

    private void EscrowReads(params Dictionary<string, AttributeValue>[] items)
    {
        var responses = items.Select(i => new QueryResponse { Items = [i] }).ToArray();
        _dynamoDb.QueryAsync(Arg.Any<QueryRequest>()).Returns(responses[0], responses.Skip(1).ToArray());
    }

    [Fact]
    public async Task ResolveDispute_FarmerWins_RecordsVerdictForTheSweep()
    {
        EscrowReads(DisputedEscrow());

        var response = await _function.FunctionHandler(
            ResolveRequest(new { resolution = "farmer_wins", action = "release", notes = "goods fine" }), _context);

        response.StatusCode.Should().Be(200);
        response.Body.Should().Contain("dispute_resolved");
        response.Body.Should().Contain("settlement sweep releases");
        // ESC-03: no verdict while a settlement holds the lock.
        await _dynamoDb.Received(1).UpdateItemAsync(
            Arg.Is<UpdateItemRequest>(u => u.ExpressionAttributeValues[":resolution"].S == "farmer_wins"
                                           && u.ConditionExpression == "#status = :disputed AND attribute_not_exists(#manualPayout) AND attribute_not_exists(#lock)"
                                           && u.ExpressionAttributeNames["#lock"] == "settlementLock"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResolveDispute_ManualPayoutOpen_Returns409Admin013()
    {
        // ESC-02: a verdict cannot contradict a settlement already waiting in the queue.
        var queued = DisputedEscrow(("manualPayout", "open"), ("manualPayoutOp", "release"));
        EscrowReads(queued, queued);
        _dynamoDb.UpdateItemAsync(Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ConditionalCheckFailedException("queued"));

        var response = await _function.FunctionHandler(
            ResolveRequest(new { resolution = "buyer_wins", action = "refund" }), _context);

        response.StatusCode.Should().Be(409);
        response.Body.Should().Contain("ADMIN_013");
        await _dynamoDb.DidNotReceive().PutItemAsync(Arg.Any<PutItemRequest>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("partial_refund", "refund")]
    [InlineData("", "release")]
    [InlineData("FARMER_WINS", "release")]
    [InlineData("seller_wins", "release")]
    public async Task ResolveDispute_ResolutionWithoutASettlement_Returns400Admin014_NothingRead(
        string resolution, string action)
    {
        var response = await _function.FunctionHandler(ResolveRequest(new { resolution, action }), _context);

        response.StatusCode.Should().Be(400);
        response.Body.Should().Contain("ADMIN_014");
        await _dynamoDb.DidNotReceive().QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>());
        await _dynamoDb.DidNotReceive().UpdateItemAsync(Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("farmer_wins", "refund")]
    [InlineData("buyer_wins", "release")]
    [InlineData("buyer_wins", "")]
    public async Task ResolveDispute_ActionContradictsTheVerdict_Returns400Admin014(string resolution, string action)
    {
        var response = await _function.FunctionHandler(ResolveRequest(new { resolution, action }), _context);

        response.StatusCode.Should().Be(400);
        response.Body.Should().Contain("ADMIN_014").And.Contain("action");
        await _dynamoDb.DidNotReceive().UpdateItemAsync(Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResolveDispute_AdminOutsideTheAdminGroup_Returns403()
    {
        // ESC-03: a verdict moves money within the hour, so a role claim is not enough.
        var authorizer = new APIGatewayCustomAuthorizerContext { { "sub", "admin-1" }, { "role", "admin" } };
        var request = new APIGatewayProxyRequest
        {
            HttpMethod = "POST",
            Path = "/admin/disputes/escrow-9/resolve",
            Body = JsonSerializer.Serialize(new { resolution = "farmer_wins", action = "release" }),
            RequestContext = new APIGatewayProxyRequest.ProxyRequestContext { Authorizer = authorizer },
        };

        var response = await _function.FunctionHandler(request, _context);

        response.StatusCode.Should().Be(403);
        response.Body.Should().Contain("ESCROW_015");
        await _dynamoDb.DidNotReceive().UpdateItemAsync(Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResolveDispute_SettlementInFlight_Returns409Admin010()
    {
        EscrowReads(DisputedEscrow(), DisputedEscrow(("settlementLock", "release")));
        _dynamoDb.UpdateItemAsync(Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ConditionalCheckFailedException("locked"));

        var response = await _function.FunctionHandler(
            ResolveRequest(new { resolution = "buyer_wins", action = "refund" }), _context);

        response.StatusCode.Should().Be(409);
        response.Body.Should().Contain("ADMIN_010").And.Contain("settlement is in progress");
        await _dynamoDb.DidNotReceive().PutItemAsync(Arg.Any<PutItemRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResolveDispute_SameVerdictAgain_ReplaysWithTheActualStatus()
    {
        // A double click: the first click recorded the verdict and the sweep already settled it.
        var settled = DisputedEscrow(("status", "completed"), ("disputeResolution", "farmer_wins"),
            ("disputeResolvedAt", "2026-09-21T08:00:00.0000000Z"));
        EscrowReads(DisputedEscrow(), settled);
        _dynamoDb.UpdateItemAsync(Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ConditionalCheckFailedException("moved"));

        var response = await _function.FunctionHandler(
            ResolveRequest(new { resolution = "farmer_wins", action = "release" }), _context);

        response.StatusCode.Should().Be(200);
        response.Body.Should().Contain("\"status\":\"completed\"")
            .And.Contain("2026-09-21T08:00:00.0000000Z")
            .And.Contain("Verdict already recorded.");
        // The re-read is strongly consistent, and a replay records nothing new.
        await _dynamoDb.Received(1).QueryAsync(Arg.Is<QueryRequest>(q => q.ConsistentRead == true), Arg.Any<CancellationToken>());
        await _dynamoDb.DidNotReceive().PutItemAsync(Arg.Any<PutItemRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResolveDispute_OtherVerdictAlreadyRecorded_Returns409Admin010()
    {
        EscrowReads(DisputedEscrow(), DisputedEscrow(("status", "dispute_resolved"), ("disputeResolution", "buyer_wins")));
        _dynamoDb.UpdateItemAsync(Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ConditionalCheckFailedException("moved"));

        var response = await _function.FunctionHandler(
            ResolveRequest(new { resolution = "farmer_wins", action = "release" }), _context);

        response.StatusCode.Should().Be(409);
        response.Body.Should().Contain("ADMIN_010").And.Contain("different verdict");
    }

    private AdminFunction WithMirrorTables()
        => new(_dynamoDb, UsersTable, OrdersTable, EscrowsTable, DeliveriesTable, AuditTable, TxAuditTable);

    private void TableReads(string table, Dictionary<string, AttributeValue> item)
        => _dynamoDb.QueryAsync(Arg.Is<QueryRequest>(q => q.TableName == table), Arg.Any<CancellationToken>())
            .Returns(new QueryResponse { Items = [item] });

    [Fact]
    public async Task ResolveDispute_MovesTheOrderToTheVerdict()
    {
        TableReads(EscrowsTable, DisputedEscrow());
        TableReads(OrdersTable, new Dictionary<string, AttributeValue>
        {
            { "orderId", S("order-9") }, { "createdAt", S("2026-09-19T10:00:00Z") },
            { "status", S("disputed") }, { "version", Num("6") }
        });
        _dynamoDb.UpdateItemAsync(Arg.Is<UpdateItemRequest>(u => u.TableName == OrdersTable), Arg.Any<CancellationToken>())
            .Returns(new UpdateItemResponse
            {
                Attributes = new Dictionary<string, AttributeValue>
                {
                    { "orderId", S("order-9") }, { "status", S("dispute_resolved") }, { "version", Num("7") }
                }
            });

        var response = await WithMirrorTables().FunctionHandler(
            ResolveRequest(new { resolution = "buyer_wins", action = "refund" }), _context);

        response.StatusCode.Should().Be(200);
        await _dynamoDb.Received(1).UpdateItemAsync(
            Arg.Is<UpdateItemRequest>(u => u.TableName == OrdersTable
                                           && u.ConditionExpression == "#v = :expectedVersion"
                                           && u.ExpressionAttributeValues[":expectedVersion"].N == "6"
                                           && u.ExpressionAttributeValues.Values.Any(v => v.S == "dispute_resolved")),
            Arg.Any<CancellationToken>());
        await _dynamoDb.Received(1).PutItemAsync(
            Arg.Is<PutItemRequest>(p => p.TableName == TxAuditTable && p.Item["entityType"].S == "order"
                                        && p.Item["action"].S == "dispute_resolved" && p.Item["actor"].S == "system"),
            Arg.Any<CancellationToken>());
        await _dynamoDb.DidNotReceive().PutItemAsync(
            Arg.Is<PutItemRequest>(p => p.Item.ContainsKey("action") && p.Item["action"].S == "dispute_verdict_propagation_failed"),
            Arg.Any<CancellationToken>());
    }

    // ===== FUP-16: filtered admin lists page past filtered-out rows =====

    private static Dictionary<string, AttributeValue> PageKey(string escrowId)
        => new() { { "escrowId", S(escrowId) }, { "createdAt", S("2026-10-01T10:00:00Z") } };

    private static ScanResponse ScanPage(Dictionary<string, AttributeValue>? lastKey, params string[] disputedIds) => new()
    {
        Items = disputedIds.Select(id => new Dictionary<string, AttributeValue>
        {
            { "escrowId", S(id) }, { "createdAt", S("2026-10-01T10:00:00Z") }, { "status", S("disputed") },
            { "buyerId", S("buyer-1") }, { "farmerId", S("farmer-1") }, { "amount", Num("100") }
        }).ToList(),
        LastEvaluatedKey = lastKey ?? new Dictionary<string, AttributeValue>()
    };

    /// <summary>A database whose user-name lookup (list enrichment) finds nobody.</summary>
    private static IAmazonDynamoDB ListDb()
    {
        var db = Substitute.For<IAmazonDynamoDB>();
        db.BatchGetItemAsync(Arg.Any<BatchGetItemRequest>(), Arg.Any<CancellationToken>())
            .Returns(new BatchGetItemResponse
            {
                Responses = new Dictionary<string, List<Dictionary<string, AttributeValue>>>(),
                UnprocessedKeys = new Dictionary<string, KeysAndAttributes>()
            });
        return db;
    }

    [Fact]
    public async Task ListDisputes_KeepsScanningPastPagesWithoutMatches_UntilTheLimitMatches()
    {
        var db = ListDb();
        var scans = new List<ScanRequest>();
        db.ScanAsync(Arg.Do<ScanRequest>(scans.Add), Arg.Any<CancellationToken>())
            .Returns(ScanPage(PageKey("e-10")), ScanPage(PageKey("e-20"), "escrow-a"), ScanPage(PageKey("e-30"), "escrow-b"),
                ScanPage(null, "escrow-c"));
        var fn = new AdminFunction(db, UsersTable, OrdersTable, EscrowsTable, DeliveriesTable, AuditTable, TxAuditTable);

        var response = await fn.FunctionHandler(
            AdminGetWithQuery("/admin/disputes", new Dictionary<string, string> { { "limit", "2" } }), _context);

        response.StatusCode.Should().Be(200, response.Body);
        response.Body.Should().Contain("escrow-a").And.Contain("escrow-b").And.NotContain("escrow-c");
        scans.Should().HaveCount(3);
        scans[0].ExclusiveStartKey.Should().BeNullOrEmpty();
        scans[1].ExclusiveStartKey["escrowId"].S.Should().Be("e-10");
        scans[2].ExclusiveStartKey["escrowId"].S.Should().Be("e-20");
        scans.Should().OnlyContain(r => r.Limit == 2 && r.ExpressionAttributeValues[":value"].S == "disputed");
        // The next page resumes after the last page read.
        response.Body.Should().Contain("\"nextToken\":\"");
    }

    [Fact]
    public async Task ListEscrows_ByStatus_StopsAtThePageBudget_AndHandsBackANextToken()
    {
        var db = ListDb();
        var scans = new List<ScanRequest>();
        db.ScanAsync(Arg.Do<ScanRequest>(scans.Add), Arg.Any<CancellationToken>())
            .Returns(_ => ScanPage(PageKey($"e-{scans.Count}")));
        var fn = new AdminFunction(db, UsersTable, OrdersTable, EscrowsTable, DeliveriesTable, AuditTable, TxAuditTable);

        var response = await fn.FunctionHandler(AdminGetWithQuery("/admin/escrows",
            new Dictionary<string, string> { { "status", "dispute_resolved" }, { "limit", "20" } }), _context);

        response.StatusCode.Should().Be(200, response.Body);
        scans.Should().HaveCount(25);
        response.Body.Should().Contain("\"escrows\":[]").And.Contain("\"nextToken\":\"");
    }

    [Fact]
    public async Task ListEscrows_WithoutAStatus_ReadsOnePage()
    {
        var db = ListDb();
        var scans = new List<ScanRequest>();
        db.ScanAsync(Arg.Do<ScanRequest>(scans.Add), Arg.Any<CancellationToken>())
            .Returns(ScanPage(PageKey("e-1"), "escrow-a"));
        var fn = new AdminFunction(db, UsersTable, OrdersTable, EscrowsTable, DeliveriesTable, AuditTable, TxAuditTable);

        var response = await fn.FunctionHandler(AdminGet("/admin/escrows"), _context);

        response.StatusCode.Should().Be(200, response.Body);
        scans.Should().ContainSingle();
    }

    // ===== FUP-16: a verdict the legs cannot follow needs a disposition =====

    private const string Reason = "Truck turned back at Adama with the bags.";

    private static Dictionary<string, AttributeValue> OrderItem(string status, string? activeDeliveryId = null)
    {
        var order = new Dictionary<string, AttributeValue>
        {
            { "orderId", S("order-9") }, { "createdAt", S("2026-09-19T10:00:00Z") },
            { "status", S(status) }, { "version", Num("3") }
        };
        if (activeDeliveryId != null)
        {
            order["activeDeliveryId"] = S(activeDeliveryId);
        }

        return order;
    }

    private static Dictionary<string, AttributeValue> DeliveryItem(string status) => new()
    {
        { "deliveryId", S("delivery-9") }, { "tripId", S("trip-9") }, { "orderId", S("order-9") },
        { "status", S(status) }, { "version", Num("2") }
    };

    private void OrderAdvanceReturns(string status)
        => _dynamoDb.UpdateItemAsync(Arg.Is<UpdateItemRequest>(u => u.TableName == OrdersTable), Arg.Any<CancellationToken>())
            .Returns(new UpdateItemResponse
            {
                Attributes = new Dictionary<string, AttributeValue>
                {
                    { "orderId", S("order-9") }, { "status", S(status) }, { "version", Num("4") }
                }
            });

    private List<T> Calls<T>(string method)
        => _dynamoDb.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == method)
            .Select(c => (T)c.GetArguments()[0]!)
            .ToList();

    private static object DispositionBody(string resolution, string disposition, string? reason = Reason)
        => new { resolution, action = resolution == "farmer_wins" ? "release" : "refund", disposition, reason };

    [Theory]
    [InlineData("buyer_wins", "refund", "in_transit", "return_to_farmer or delivered_anyway")]
    [InlineData("farmer_wins", "release", "pickup_confirmed", "wait_for_delivery or settle_now")]
    [InlineData("farmer_wins", "release", "accepted", "(wait_for_delivery)")]
    public async Task ResolveDispute_LegsThatCannotFollow_WithoutDisposition_Returns422Admin015_NothingWritten(
        string resolution, string action, string orderStatus, string offered)
    {
        TableReads(EscrowsTable, DisputedEscrow());
        TableReads(OrdersTable, OrderItem(orderStatus));

        var response = await WithMirrorTables().FunctionHandler(ResolveRequest(new { resolution, action }), _context);

        response.StatusCode.Should().Be(422);
        response.Body.Should().Contain("ADMIN_015").And.Contain("\"field\":\"disposition\"").And.Contain(offered);
        await _dynamoDb.DidNotReceive().UpdateItemAsync(Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>());
        await _dynamoDb.DidNotReceive().PutItemAsync(Arg.Any<PutItemRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResolveDispute_ReturnToFarmer_RecordsItWithTheVerdict_AuditsIt_ThenCancelsTheDeliveryBeforeTheOrder()
    {
        TableReads(EscrowsTable, DisputedEscrow());
        TableReads(OrdersTable, OrderItem("in_transit", "delivery-9"));
        TableReads(DeliveriesTable, DeliveryItem("inTransit"));
        OrderAdvanceReturns("dispute_resolved");
        var writes = new List<string>();
        _dynamoDb.When(d => d.UpdateItemAsync(Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>()))
            .Do(c => writes.Add(((UpdateItemRequest)c[0]).TableName));

        var response = await WithMirrorTables().FunctionHandler(
            ResolveRequest(DispositionBody("buyer_wins", "return_to_farmer")), _context);

        response.StatusCode.Should().Be(200, response.Body);
        response.Body.Should().Contain("\"disposition\":\"return_to_farmer\"").And.Contain("goods go back to the farmer");
        writes.Should().Equal(EscrowsTable, DeliveriesTable, OrdersTable);

        var updates = Calls<UpdateItemRequest>(nameof(IAmazonDynamoDB.UpdateItemAsync));
        var verdict = updates.Single(u => u.TableName == EscrowsTable);
        verdict.ConditionExpression.Should().Be("#status = :disputed AND attribute_not_exists(#manualPayout) AND attribute_not_exists(#lock)");
        verdict.ExpressionAttributeValues[":disposition"].S.Should().Be("return_to_farmer");
        verdict.ExpressionAttributeValues[":dispositionReason"].S.Should().Be(Reason);
        verdict.ExpressionAttributeNames["#dispositionBy"].Should().Be("disputeDispositionBy");
        var delivery = updates.Single(u => u.TableName == DeliveriesTable);
        delivery.ExpressionAttributeValues[":to"].S.Should().Be("cancelled");
        delivery.ExpressionAttributeValues[":from"].S.Should().Be("inTransit");
        delivery.ExpressionAttributeValues[":cancelReason"].S.Should().Be("dispute_buyer_wins");
        delivery.ExpressionAttributeValues[":resolution"].S.Should().Be("buyer_wins");
        delivery.ConditionExpression.Should().Be("#status = :from AND attribute_not_exists(#resolution)");
        var order = updates.Single(u => u.TableName == OrdersTable);
        order.ExpressionAttributeValues[":from"].S.Should().Be("in_transit");
        order.ExpressionAttributeValues[":to"].S.Should().Be("dispute_resolved");

        var puts = Calls<PutItemRequest>(nameof(IAmazonDynamoDB.PutItemAsync));
        var verdictRow = puts.Single(p => p.TableName == AuditTable && p.Item["action"].S == "admin_dispute_resolved");
        var choiceRow = puts.Single(p => p.TableName == AuditTable && p.Item["action"].S == "dispute_resolved_goods_returned");
        choiceRow.Item["actor"].S.Should().Be("admin-1");
        choiceRow.Item["metadata"].S.Should().Be(
            $"disposition=return_to_farmer;reason={Reason};legs=order:mismatch:in_transit,delivery:mismatch:inTransit");
        choiceRow.Item["timestamp"].S.Should().Be(
            DisputeVerdictMirror.VerdictAuditTimestamp(verdictRow.Item["timestamp"].S, "dispute_resolved_goods_returned"));
        choiceRow.ConditionExpression.Should().Be("attribute_not_exists(escrowId)");
        puts.Should().ContainSingle(p => p.TableName == TxAuditTable && p.Item["entityType"].S == "escrow"
                                         && p.Item["action"].S == "dispute_resolved_goods_returned"
                                         && p.Item["actor"].S == "admin-1");
        puts.Should().ContainSingle(p => p.TableName == TxAuditTable && p.Item["entityType"].S == "delivery"
                                         && p.Item["action"].S == "delivery_cancelled" && p.Item["actor"].S == "system");
        puts.Should().ContainSingle(p => p.TableName == TxAuditTable && p.Item["entityType"].S == "order"
                                         && p.Item["action"].S == "dispute_resolved" && p.Item["actor"].S == "system");
        puts.Should().NotContain(p => p.Item.ContainsKey("action") && p.Item["action"].S == "dispute_verdict_mismatch");
    }

    [Fact]
    public async Task ResolveDispute_HandoffLandsBetweenTheDecisionAndTheApply_FallsBackToThePlainVerdict()
    {
        TableReads(EscrowsTable, DisputedEscrow());
        QueryResponse Rows(Dictionary<string, AttributeValue> item) => new() { Items = [item] };
        // The decision reads the goods on the way; every later read finds them delivered.
        _dynamoDb.QueryAsync(Arg.Is<QueryRequest>(q => q.TableName == OrdersTable), Arg.Any<CancellationToken>())
            .Returns(Rows(OrderItem("in_transit", "delivery-9")), Rows(OrderItem("delivered", "delivery-9")));
        _dynamoDb.QueryAsync(Arg.Is<QueryRequest>(q => q.TableName == DeliveriesTable), Arg.Any<CancellationToken>())
            .Returns(Rows(DeliveryItem("inTransit")), Rows(DeliveryItem("delivered")));

        var response = await WithMirrorTables().FunctionHandler(
            ResolveRequest(DispositionBody("buyer_wins", "return_to_farmer")), _context);

        response.StatusCode.Should().Be(200, response.Body);
        response.Body.Should().Contain("disposition was not needed");
        // Never cancelled under the buyer: the plain verdict path disputes the delivered handoff instead.
        Calls<UpdateItemRequest>(nameof(IAmazonDynamoDB.UpdateItemAsync))
            .Should().NotContain(u => u.TableName == DeliveriesTable && u.ExpressionAttributeValues.ContainsKey(":cancelReason"));
    }

    [Fact]
    public async Task ResolveDispute_QueuedPayout_Answers409Admin013_BeforeAnyDispositionQuestion()
    {
        var queued = DisputedEscrow(("manualPayout", "open"), ("manualPayoutOp", "release"));
        TableReads(EscrowsTable, queued);
        TableReads(OrdersTable, OrderItem("in_transit"));
        _dynamoDb.UpdateItemAsync(Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ConditionalCheckFailedException("queued"));

        var response = await WithMirrorTables().FunctionHandler(
            ResolveRequest(new { resolution = "buyer_wins", action = "refund" }), _context);

        response.StatusCode.Should().Be(409);
        response.Body.Should().Contain("ADMIN_013");
        await _dynamoDb.DidNotReceive().QueryAsync(
            Arg.Is<QueryRequest>(q => q.TableName == OrdersTable), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("buyer_wins", "keep_the_goods", Reason, "disposition")]
    [InlineData("buyer_wins", "settle_now", Reason, "disposition")]
    [InlineData("farmer_wins", "return_to_farmer", Reason, "disposition")]
    [InlineData("buyer_wins", "return_to_farmer", null, "reason")]
    [InlineData("buyer_wins", "delivered_anyway", "too short", "reason")]
    [InlineData("farmer_wins", "wait_for_delivery", "          ", "reason")]
    public async Task ResolveDispute_InvalidDisposition_Returns400Admin014_NothingRead(
        string resolution, string disposition, string? reason, string field)
    {
        var response = await WithMirrorTables().FunctionHandler(
            ResolveRequest(DispositionBody(resolution, disposition, reason)), _context);

        response.StatusCode.Should().Be(400);
        response.Body.Should().Contain("ADMIN_014").And.Contain($"\"field\":\"{field}\"");
        await _dynamoDb.DidNotReceive().QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>());
        await _dynamoDb.DidNotReceive().UpdateItemAsync(Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResolveDispute_ReasonOver500Characters_Returns400Admin014()
    {
        var response = await WithMirrorTables().FunctionHandler(
            ResolveRequest(DispositionBody("farmer_wins", "wait_for_delivery", new string('r', 501))), _context);

        response.StatusCode.Should().Be(400);
        response.Body.Should().Contain("ADMIN_014").And.Contain("\"field\":\"reason\"");
    }

    [Fact]
    public async Task ResolveDispute_DispositionOutsideTheAdminGroup_Returns403()
    {
        var authorizer = new APIGatewayCustomAuthorizerContext { { "sub", "admin-1" }, { "role", "admin" } };
        var request = new APIGatewayProxyRequest
        {
            HttpMethod = "POST",
            Path = "/admin/disputes/escrow-9/resolve",
            Body = JsonSerializer.Serialize(DispositionBody("buyer_wins", "return_to_farmer")),
            RequestContext = new APIGatewayProxyRequest.ProxyRequestContext { Authorizer = authorizer },
        };

        var response = await WithMirrorTables().FunctionHandler(request, _context);

        response.StatusCode.Should().Be(403);
        response.Body.Should().Contain("ESCROW_015");
        await _dynamoDb.DidNotReceive().UpdateItemAsync(Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResolveDispute_SettleNowBeforePickup_Returns422Admin016_NothingWritten()
    {
        // The farmer keeps the money only for goods already handed over.
        TableReads(EscrowsTable, DisputedEscrow());
        TableReads(OrdersTable, OrderItem("accepted"));

        var response = await WithMirrorTables().FunctionHandler(
            ResolveRequest(DispositionBody("farmer_wins", "settle_now")), _context);

        response.StatusCode.Should().Be(422);
        response.Body.Should().Contain("ADMIN_016").And.Contain("wait_for_delivery");
        await _dynamoDb.DidNotReceive().UpdateItemAsync(Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResolveDispute_NoDispositionFits_Returns422Admin016_NothingWritten()
    {
        TableReads(EscrowsTable, DisputedEscrow());
        TableReads(OrdersTable, OrderItem("cancelled"));

        var response = await WithMirrorTables().FunctionHandler(
            ResolveRequest(new { resolution = "farmer_wins", action = "release" }), _context);

        response.StatusCode.Should().Be(422);
        response.Body.Should().Contain("ADMIN_016").And.Contain("order:mismatch:cancelled").And.Contain("choose the other verdict");
        await _dynamoDb.DidNotReceive().UpdateItemAsync(Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResolveDispute_ADispositionTheLegsDoNotNeed_IsNotRecorded()
    {
        TableReads(EscrowsTable, DisputedEscrow());
        TableReads(OrdersTable, OrderItem("disputed"));
        OrderAdvanceReturns("dispute_resolved");

        var response = await WithMirrorTables().FunctionHandler(
            ResolveRequest(DispositionBody("buyer_wins", "delivered_anyway")), _context);

        response.StatusCode.Should().Be(200, response.Body);
        response.Body.Should().Contain("No disposition was needed");
        var verdict = Calls<UpdateItemRequest>(nameof(IAmazonDynamoDB.UpdateItemAsync)).Single(u => u.TableName == EscrowsTable);
        verdict.ExpressionAttributeValues.Should().NotContainKey(":disposition");
        Calls<PutItemRequest>(nameof(IAmazonDynamoDB.PutItemAsync))
            .Should().NotContain(p => p.Item.ContainsKey("action") && p.Item["action"].S == "dispute_resolved_delivered_anyway");
    }

    [Fact]
    public async Task ResolveDispute_LegsUnreadable_Returns503_NothingRecorded()
    {
        TableReads(EscrowsTable, DisputedEscrow());
        _dynamoDb.QueryAsync(Arg.Is<QueryRequest>(q => q.TableName == OrdersTable), Arg.Any<CancellationToken>())
            .ThrowsAsync(new AmazonDynamoDBException("orders down"));

        var response = await WithMirrorTables().FunctionHandler(
            ResolveRequest(new { resolution = "buyer_wins", action = "refund" }), _context);

        response.StatusCode.Should().Be(503);
        response.Body.Should().Contain("ESCROW_012");
        await _dynamoDb.DidNotReceive().UpdateItemAsync(Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>());
    }

    private static Dictionary<string, AttributeValue> RecordedVerdict(string resolution, params (string Key, string Value)[] extra)
    {
        var escrow = DisputedEscrow(("status", "dispute_resolved"), ("disputeResolution", resolution),
            ("disputeResolvedAt", "2026-09-21T08:00:00.0000000Z"));
        foreach (var (key, value) in extra)
        {
            escrow[key] = S(value);
        }

        return escrow;
    }

    private void TheVerdictWriteFindsItDecided()
        => _dynamoDb.UpdateItemAsync(
                Arg.Is<UpdateItemRequest>(u => u.TableName == EscrowsTable && u.ConditionExpression.StartsWith("#status = :disputed")),
                Arg.Any<CancellationToken>())
            .ThrowsAsync(new ConditionalCheckFailedException("decided"));

    [Fact]
    public async Task ResolveDispute_ReplayOfAStuckVerdict_WithoutDisposition_Returns422Admin015()
    {
        // A verdict recorded before 1.21.0 whose goods are moving: the admin is asked to choose.
        TableReads(EscrowsTable, RecordedVerdict("buyer_wins"));
        TableReads(OrdersTable, OrderItem("in_transit"));
        TheVerdictWriteFindsItDecided();

        var response = await WithMirrorTables().FunctionHandler(
            ResolveRequest(new { resolution = "buyer_wins", action = "refund" }), _context);

        response.StatusCode.Should().Be(422);
        response.Body.Should().Contain("ADMIN_015");
        await _dynamoDb.DidNotReceive().PutItemAsync(Arg.Any<PutItemRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResolveDispute_ReplayOfAVerdictNothingFits_SaysItCannotChange()
    {
        // A recorded verdict cannot become the other one: the message must not offer it.
        TableReads(EscrowsTable, RecordedVerdict("farmer_wins"));
        TableReads(OrdersTable, OrderItem("cancelled"));
        TheVerdictWriteFindsItDecided();

        var response = await WithMirrorTables().FunctionHandler(
            ResolveRequest(DispositionBody("farmer_wins", "settle_now")), _context);

        response.StatusCode.Should().Be(422);
        response.Body.Should().Contain("ADMIN_016").And.Contain("cannot change").And.NotContain("choose the other verdict");
        Calls<UpdateItemRequest>(nameof(IAmazonDynamoDB.UpdateItemAsync))
            .Should().NotContain(u => u.TableName == EscrowsTable && u.ConditionExpression.StartsWith("#status = :resolved"));
    }

    [Fact]
    public async Task ListEscrows_ShowsTheRecordedDisposition()
    {
        var db = ListDb();
        var item = RecordedVerdict("farmer_wins", ("disputeId", "dispute-1"), ("disputeReason", "not_delivered"),
            ("disputeCreatedAt", "2026-09-20T10:00:00Z"), ("disputeDisposition", "wait_for_delivery"),
            ("disputeDispositionReason", Reason), ("disputeDispositionBy", "admin-1"),
            ("disputeDispositionAt", "2026-09-21T08:00:00.0000000Z"));
        item["buyerId"] = S("buyer-1");
        item["farmerId"] = S("farmer-1");
        item["amount"] = Num("100");
        db.ScanAsync(Arg.Any<ScanRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ScanResponse { Items = [item], LastEvaluatedKey = new Dictionary<string, AttributeValue>() });
        var fn = new AdminFunction(db, UsersTable, OrdersTable, EscrowsTable, DeliveriesTable, AuditTable, TxAuditTable);

        var response = await fn.FunctionHandler(AdminGetWithQuery("/admin/escrows",
            new Dictionary<string, string> { { "status", "dispute_resolved" } }), _context);

        response.StatusCode.Should().Be(200, response.Body);
        response.Body.Should().Contain("\"disposition\":\"wait_for_delivery\"")
            .And.Contain($"\"dispositionReason\":\"{Reason}\"")
            .And.Contain("\"dispositionBy\":\"admin-1\"");
    }

    [Fact]
    public async Task ResolveDispute_ReplayTurnsAWaitIntoSettleNow_RecordsTheNewChoiceFirst()
    {
        TableReads(EscrowsTable, RecordedVerdict("farmer_wins",
            ("disputeDisposition", "wait_for_delivery"), ("disputeDispositionAt", "2026-09-21T08:00:00.0000000Z")));
        TableReads(OrdersTable, OrderItem("in_transit", "delivery-9"));
        TableReads(DeliveriesTable, DeliveryItem("inTransit"));
        TheVerdictWriteFindsItDecided();
        OrderAdvanceReturns("dispute_resolved");

        var response = await WithMirrorTables().FunctionHandler(
            ResolveRequest(DispositionBody("farmer_wins", "settle_now")), _context);

        response.StatusCode.Should().Be(200, response.Body);
        response.Body.Should().Contain("\"disposition\":\"settle_now\"").And.Contain("\"status\":\"dispute_resolved\"");
        var updates = Calls<UpdateItemRequest>(nameof(IAmazonDynamoDB.UpdateItemAsync));
        var choice = updates.Single(u => u.TableName == EscrowsTable && u.ConditionExpression.StartsWith("#status = :resolved"));
        choice.ConditionExpression.Should().Contain("attribute_not_exists(#lock)").And.Contain("attribute_not_exists(#manualPayout)");
        choice.ExpressionAttributeValues[":disposition"].S.Should().Be("settle_now");
        choice.ExpressionAttributeValues[":resolution"].S.Should().Be("farmer_wins");
        // Never last-writer-wins: the write requires the choice this request read.
        choice.ConditionExpression.Should().EndWith("AND #disposition = :previous");
        choice.ExpressionAttributeValues[":previous"].S.Should().Be("wait_for_delivery");
        updates.Single(u => u.TableName == DeliveriesTable).ExpressionAttributeValues[":cancelReason"].S
            .Should().Be("dispute_farmer_wins");
        updates.Single(u => u.TableName == OrdersTable).ExpressionAttributeValues[":to"].S.Should().Be("dispute_resolved");
        Calls<PutItemRequest>(nameof(IAmazonDynamoDB.PutItemAsync))
            .Should().ContainSingle(p => p.TableName == AuditTable && p.Item["action"].S == "dispute_resolved_settled_early");
    }

    [Fact]
    public async Task ResolveDispute_ReplayWithADispositionWhileASettlementRuns_Returns409Admin010()
    {
        TableReads(EscrowsTable, RecordedVerdict("farmer_wins"));
        TableReads(OrdersTable, OrderItem("in_transit"));
        TheVerdictWriteFindsItDecided();
        _dynamoDb.UpdateItemAsync(
                Arg.Is<UpdateItemRequest>(u => u.TableName == EscrowsTable && u.ConditionExpression.StartsWith("#status = :resolved")),
                Arg.Any<CancellationToken>())
            .ThrowsAsync(new ConditionalCheckFailedException("locked"));

        var response = await WithMirrorTables().FunctionHandler(
            ResolveRequest(DispositionBody("farmer_wins", "settle_now")), _context);

        response.StatusCode.Should().Be(409);
        response.Body.Should().Contain("ADMIN_010").And.Contain("another admin");
        await _dynamoDb.DidNotReceive().UpdateItemAsync(
            Arg.Is<UpdateItemRequest>(u => u.TableName == OrdersTable || u.TableName == DeliveriesTable), Arg.Any<CancellationToken>());
        // Nothing was recorded when this request read the escrow: a concurrent first choice wins.
        Calls<UpdateItemRequest>(nameof(IAmazonDynamoDB.UpdateItemAsync))
            .Single(u => u.TableName == EscrowsTable && u.ConditionExpression.StartsWith("#status = :resolved"))
            .ConditionExpression.Should().EndWith("AND attribute_not_exists(#disposition)");
    }

    [Fact]
    public async Task ResolveDispute_ReplayOfTheSameDisposition_FinishesTheLegs_WithoutASecondChoiceRow()
    {
        // The first run recorded the choice and cancelled the delivery, then the order write failed.
        TableReads(EscrowsTable, RecordedVerdict("buyer_wins",
            ("disputeDisposition", "return_to_farmer"), ("disputeDispositionReason", Reason),
            ("disputeDispositionAt", "2026-09-21T08:00:00.0000000Z")));
        TableReads(OrdersTable, OrderItem("in_transit", "delivery-9"));
        var cancelled = DeliveryItem("cancelled");
        cancelled["disputeResolution"] = S("buyer_wins");
        TableReads(DeliveriesTable, cancelled);
        TheVerdictWriteFindsItDecided();
        OrderAdvanceReturns("dispute_resolved");

        var response = await WithMirrorTables().FunctionHandler(
            ResolveRequest(new { resolution = "buyer_wins", action = "refund" }), _context);

        response.StatusCode.Should().Be(200, response.Body);
        var updates = Calls<UpdateItemRequest>(nameof(IAmazonDynamoDB.UpdateItemAsync));
        updates.Should().NotContain(u => u.TableName == DeliveriesTable);
        updates.Should().NotContain(u => u.TableName == EscrowsTable && u.ConditionExpression.StartsWith("#status = :resolved"));
        updates.Single(u => u.TableName == OrdersTable).ExpressionAttributeValues[":to"].S.Should().Be("dispute_resolved");
        // The choice row is re-ensured at its deterministic key: a conditional put, never a second row.
        var choiceRow = Calls<PutItemRequest>(nameof(IAmazonDynamoDB.PutItemAsync))
            .Single(p => p.TableName == AuditTable && p.Item["action"].S == "dispute_resolved_goods_returned");
        choiceRow.Item["timestamp"].S.Should().Be("2026-09-21T08:00:00.0000004Z");
        choiceRow.ConditionExpression.Should().Be("attribute_not_exists(escrowId)");
    }

    // ──────────────────────────────────────────────
    // Manual payout queue (ESC-02)
    // ──────────────────────────────────────────────

    private static APIGatewayProxyRequest AdminGroupRequest(
        string method, string path, string? body = null, Dictionary<string, string>? query = null)
    {
        // Admin-pool tokens carry cognito:groups; money-affecting routes require the group.
        var authorizer = new APIGatewayCustomAuthorizerContext
        {
            { "sub", "admin-1" },
            { "cognito:groups", "admin" },
        };
        return new APIGatewayProxyRequest
        {
            HttpMethod = method,
            Path = path,
            Body = body,
            QueryStringParameters = query,
            RequestContext = new APIGatewayProxyRequest.ProxyRequestContext { Authorizer = authorizer },
        };
    }

    private static Dictionary<string, AttributeValue> QueuedRefundEscrowItem() => new()
    {
        ["escrowId"] = S("escrow-order-ord-7"),
        ["createdAt"] = S("2026-10-01T10:00:00Z"),
        ["orderId"] = S("ord-7"),
        ["buyerId"] = S("buyer-7"),
        ["farmerId"] = S("farmer-7"),
        ["status"] = S("escrowed"),
        ["amount"] = Num("20000"),
        ["farmerPayout"] = Num("19500"),
        ["commission"] = Num("500"),
        ["currency"] = S("ETB"),
        ["settlementLock"] = S("refund"),
        ["manualPayout"] = S("open"),
        ["manualPayoutQueuedAt"] = S("2026-10-02T09:00:00Z"),
        ["manualPayoutOp"] = S("refund"),
        ["manualPayoutReason"] = S("PAYOUT_NOT_SUPPORTED"),
        ["manualPayoutFromStatus"] = S("escrowed"),
        ["manualBuyerStatus"] = S("pending"),
    };

    private static Dictionary<string, AttributeValue> BuyerUserItem() => new()
    {
        ["userId"] = S("buyer-7"),
        ["phoneNumber"] = S("+251911000007"),
        ["displayName"] = S("Sara"),
        ["role"] = S("buyer"),
        ["woreda"] = S("w"),
        ["isPhoneVerified"] = new AttributeValue { BOOL = true },
        ["createdAt"] = S("2026-01-01T00:00:00Z"),
        ["updatedAt"] = S("2026-01-01T00:00:00Z"),
    };

    private static AdminFunction PayoutFunction(IAmazonDynamoDB db) =>
        new(db, UsersTable, OrdersTable, EscrowsTable, DeliveriesTable, AuditTable, TxAuditTable);

    [Fact]
    public async Task ManualPayouts_List_QueriesTheIndexAndMapsLegs()
    {
        var db = Substitute.For<IAmazonDynamoDB>();
        db.QueryAsync(Arg.Is<QueryRequest>(q => q.IndexName == "ManualPayoutIndex"), Arg.Any<CancellationToken>())
            .Returns(new QueryResponse { Items = new List<Dictionary<string, AttributeValue>> { QueuedRefundEscrowItem() } });
        db.GetItemAsync(Arg.Any<GetItemRequest>(), Arg.Any<CancellationToken>())
            .Returns(new GetItemResponse { Item = BuyerUserItem() });

        var response = await PayoutFunction(db).FunctionHandler(
            AdminGroupRequest("GET", "/admin/payouts/manual", query: new Dictionary<string, string> { { "limit", "10" } }),
            _context);

        response.StatusCode.Should().Be(200);
        response.Body.Should().Contain("\"payouts\"")
            .And.Contain("escrow-order-ord-7")
            .And.Contain("\"op\":\"refund\"")
            .And.Contain("\"ref\":\"buyer\"")
            .And.Contain("251911000007")
            .And.Contain("\"payeeVerified\":true");
        await db.Received(1).QueryAsync(
            Arg.Is<QueryRequest>(q => q.IndexName == "ManualPayoutIndex" && q.ScanIndexForward == true && q.Limit == 10),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ManualPayouts_Complete_ClosesRefundAndEmitsAudits()
    {
        var db = Substitute.For<IAmazonDynamoDB>();
        db.QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(new QueryResponse { Items = new List<Dictionary<string, AttributeValue>> { QueuedRefundEscrowItem() } });
        db.GetItemAsync(Arg.Any<GetItemRequest>(), Arg.Any<CancellationToken>())
            .Returns(new GetItemResponse { Item = BuyerUserItem() });
        db.UpdateItemAsync(Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>()).Returns(new UpdateItemResponse());
        db.PutItemAsync(Arg.Any<PutItemRequest>(), Arg.Any<CancellationToken>()).Returns(new PutItemResponse());

        var response = await PayoutFunction(db).FunctionHandler(
            AdminGroupRequest("POST", "/admin/payouts/escrow-order-ord-7/buyer/complete",
                JsonSerializer.Serialize(new { externalRef = "TB-REF-0009" })),
            _context);

        response.StatusCode.Should().Be(200);
        response.Body.Should().Contain("\"escrowStatus\":\"refunded\"").And.Contain("TB-REF-0009");
        await db.Received(1).UpdateItemAsync(
            Arg.Is<UpdateItemRequest>(u =>
                u.TableName == EscrowsTable
                && u.ExpressionAttributeValues[":newStatus"].S == "refunded"
                && u.ExpressionAttributeValues[":ref"].S == "TB-REF-0009"
                && u.ExpressionAttributeValues[":paidBy"].S == "admin-1"
                && u.UpdateExpression.Contains("REMOVE #marker")),
            Arg.Any<CancellationToken>());
        await db.Received().PutItemAsync(
            Arg.Is<PutItemRequest>(p => p.TableName == AuditTable && p.Item["action"].S == "manual_payout_paid"),
            Arg.Any<CancellationToken>());
        await db.Received().PutItemAsync(
            Arg.Is<PutItemRequest>(p => p.TableName == AuditTable && p.Item["action"].S == "refund"
                                        && p.Item["metadata"].S == "manual"),
            Arg.Any<CancellationToken>());
        await db.Received().PutItemAsync(
            Arg.Is<PutItemRequest>(p => p.TableName == TxAuditTable && p.Item["action"].S == "admin_manual_payout"
                                        && p.Item["transactionId"].S == "ord-7"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ManualPayouts_Complete_Replay_ReEnsuresAuditRows_NoSecondTxAudit()
    {
        // The refund already closed (marker gone, refunded) with this reference, but the audit
        // writes after the close may have failed: the replay re-ensures them with conditional
        // puts at deterministic keys and writes no second transaction-audit row.
        var closed = QueuedRefundEscrowItem();
        closed.Remove("manualPayout");
        closed["status"] = S("refunded");
        closed["manualBuyerStatus"] = S("paid");
        closed["manualBuyerRef"] = S("TB-REF-0009");
        closed["manualBuyerPaidBy"] = S("admin-1");
        closed["manualBuyerPaidAt"] = S("2026-10-02T10:00:00.0000000Z");
        var db = Substitute.For<IAmazonDynamoDB>();
        db.QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(new QueryResponse { Items = new List<Dictionary<string, AttributeValue>> { closed } });
        db.PutItemAsync(Arg.Any<PutItemRequest>(), Arg.Any<CancellationToken>()).Returns(new PutItemResponse());

        var response = await PayoutFunction(db).FunctionHandler(
            AdminGroupRequest("POST", "/admin/payouts/escrow-order-ord-7/buyer/complete",
                JsonSerializer.Serialize(new { externalRef = "TB-REF-0009" })),
            _context);

        response.StatusCode.Should().Be(200);
        response.Body.Should().Contain("\"alreadyRecorded\":true").And.Contain("\"escrowStatus\":\"refunded\"");
        await db.DidNotReceive().UpdateItemAsync(Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>());
        await db.Received(1).PutItemAsync(
            Arg.Is<PutItemRequest>(p => p.TableName == AuditTable && p.Item["action"].S == "manual_payout_paid"
                                        && p.Item["timestamp"].S == "2026-10-02T10:00:00.0000000Z"
                                        && p.ConditionExpression == "attribute_not_exists(escrowId)"),
            Arg.Any<CancellationToken>());
        await db.Received(1).PutItemAsync(
            Arg.Is<PutItemRequest>(p => p.TableName == AuditTable && p.Item["action"].S == "refund"
                                        && p.Item["timestamp"].S == "2026-10-02T10:00:00.0000001Z"
                                        && p.ConditionExpression == "attribute_not_exists(escrowId)"),
            Arg.Any<CancellationToken>());
        await db.DidNotReceive().PutItemAsync(
            Arg.Is<PutItemRequest>(p => p.TableName == TxAuditTable), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ManualPayouts_Complete_RoleClaimOnly_Returns403_NoWrite()
    {
        // A marketplace token that merely claims role=admin is not in the admin group and is
        // not an admin profile either: refused before the escrow is even read.
        var db = Substitute.For<IAmazonDynamoDB>();
        db.GetItemAsync(Arg.Any<GetItemRequest>(), Arg.Any<CancellationToken>())
            .Returns(new GetItemResponse { Item = new Dictionary<string, AttributeValue>() });
        var authorizer = new APIGatewayCustomAuthorizerContext { { "sub", "admin-1" }, { "role", "admin" } };
        var request = new APIGatewayProxyRequest
        {
            HttpMethod = "POST",
            Path = "/admin/payouts/escrow-order-ord-7/buyer/complete",
            Body = JsonSerializer.Serialize(new { externalRef = "TB-REF-0009" }),
            RequestContext = new APIGatewayProxyRequest.ProxyRequestContext { Authorizer = authorizer },
        };

        var response = await PayoutFunction(db).FunctionHandler(request, _context);

        response.StatusCode.Should().Be(403);
        response.Body.Should().Contain("ESCROW_015");
        await db.DidNotReceive().QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>());
        await db.DidNotReceive().UpdateItemAsync(Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ManualPayouts_Complete_BadJson_Returns400()
    {
        var db = Substitute.For<IAmazonDynamoDB>();

        var response = await PayoutFunction(db).FunctionHandler(
            AdminGroupRequest("POST", "/admin/payouts/escrow-order-ord-7/buyer/complete", "{not json"), _context);

        response.StatusCode.Should().Be(400);
        response.Body.Should().Contain("ADMIN_005");
    }

    [Fact]
    public async Task ManualPayouts_Complete_EmptyReference_Returns400Escrow037_NoRead()
    {
        var db = Substitute.For<IAmazonDynamoDB>();

        var response = await PayoutFunction(db).FunctionHandler(
            AdminGroupRequest("POST", "/admin/payouts/escrow-order-ord-7/buyer/complete",
                JsonSerializer.Serialize(new { externalRef = "" })),
            _context);

        response.StatusCode.Should().Be(400);
        response.Body.Should().Contain("ESCROW_037");
        await db.DidNotReceive().QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("/admin/payouts/escrow-order-ord-7/platform/complete")]
    [InlineData("/admin/payouts/bad%20id/buyer/complete")]
    public async Task ManualPayouts_Complete_UnknownLegOrBadId_Returns404(string path)
    {
        var db = Substitute.For<IAmazonDynamoDB>();

        var response = await PayoutFunction(db).FunctionHandler(
            AdminGroupRequest("POST", path, JsonSerializer.Serialize(new { externalRef = "TB-REF-0009" })), _context);

        response.StatusCode.Should().Be(404);
        response.Body.Should().Contain("ESCROW_035");
        await db.DidNotReceive().QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ManualPayouts_Complete_UnknownEscrow_Returns404Escrow016()
    {
        var db = Substitute.For<IAmazonDynamoDB>();
        db.QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(new QueryResponse { Items = new List<Dictionary<string, AttributeValue>>() });

        var response = await PayoutFunction(db).FunctionHandler(
            AdminGroupRequest("POST", "/admin/payouts/escrow-e2e-missing/farmer/complete",
                JsonSerializer.Serialize(new { externalRef = "E2E-NOT-A-REAL-REF" })),
            _context);

        response.StatusCode.Should().Be(404);
        response.Body.Should().Contain("ESCROW_016");
        await db.DidNotReceive().UpdateItemAsync(Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ManualPayouts_WithoutEscrowTables_Returns503()
    {
        var response = await _function.FunctionHandler(AdminGroupRequest("GET", "/admin/payouts/manual"), _context);

        response.StatusCode.Should().Be(503);
        response.Body.Should().Contain("ADMIN_004");
    }
}
