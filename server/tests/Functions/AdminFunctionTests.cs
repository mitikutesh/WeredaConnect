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
                && r.ConditionExpression == "attribute_exists(userId)"),
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

    [Fact]
    public async Task ResolveDispute_FarmerWins_RecordsVerdictForTheSweep()
    {
        _dynamoDb.QueryAsync(Arg.Any<QueryRequest>()).Returns(new QueryResponse
        {
            Items =
            [
                new Dictionary<string, AttributeValue>
                {
                    { "escrowId", new AttributeValue { S = "escrow-9" } },
                    { "createdAt", new AttributeValue { S = "2026-09-20T10:00:00Z" } },
                    { "status", new AttributeValue { S = "disputed" } },
                    { "orderId", new AttributeValue { S = "order-9" } }
                }
            ]
        });
        var authorizer = new APIGatewayCustomAuthorizerContext { { "sub", "admin-1" }, { "role", "admin" } };
        var request = new APIGatewayProxyRequest
        {
            HttpMethod = "POST",
            Path = "/admin/disputes/escrow-9/resolve",
            Body = JsonSerializer.Serialize(new { resolution = "farmer_wins", action = "release", notes = "goods fine" }),
            RequestContext = new APIGatewayProxyRequest.ProxyRequestContext { Authorizer = authorizer },
        };

        var response = await _function.FunctionHandler(request, _context);

        response.StatusCode.Should().Be(200);
        response.Body.Should().Contain("dispute_resolved");
        response.Body.Should().Contain("settlement sweep releases");
        await _dynamoDb.Received(1).UpdateItemAsync(
            Arg.Is<UpdateItemRequest>(u => u.ExpressionAttributeValues[":resolution"].S == "farmer_wins"
                                           && u.ConditionExpression.Contains("attribute_not_exists(#manualPayout)")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResolveDispute_ManualPayoutOpen_Returns409Admin013()
    {
        // ESC-02: a verdict cannot contradict a settlement already waiting in the queue.
        _dynamoDb.QueryAsync(Arg.Any<QueryRequest>()).Returns(new QueryResponse
        {
            Items =
            [
                new Dictionary<string, AttributeValue>
                {
                    { "escrowId", new AttributeValue { S = "escrow-9" } },
                    { "createdAt", new AttributeValue { S = "2026-09-20T10:00:00Z" } },
                    { "status", new AttributeValue { S = "disputed" } },
                    { "manualPayout", new AttributeValue { S = "open" } },
                    { "manualPayoutOp", new AttributeValue { S = "release" } }
                }
            ]
        });
        _dynamoDb.UpdateItemAsync(Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ConditionalCheckFailedException("queued"));
        var authorizer = new APIGatewayCustomAuthorizerContext { { "sub", "admin-1" }, { "role", "admin" } };
        var request = new APIGatewayProxyRequest
        {
            HttpMethod = "POST",
            Path = "/admin/disputes/escrow-9/resolve",
            Body = JsonSerializer.Serialize(new { resolution = "buyer_wins", action = "refund" }),
            RequestContext = new APIGatewayProxyRequest.ProxyRequestContext { Authorizer = authorizer },
        };

        var response = await _function.FunctionHandler(request, _context);

        response.StatusCode.Should().Be(409);
        response.Body.Should().Contain("ADMIN_013");
        await _dynamoDb.DidNotReceive().PutItemAsync(Arg.Any<PutItemRequest>(), Arg.Any<CancellationToken>());
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
