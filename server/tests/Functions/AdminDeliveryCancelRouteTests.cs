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
/// FUP-19 (spec E9): <c>POST /admin/deliveries/{id}/cancel</c> through the admin Lambda over a substituted DynamoDB
/// client, with its one transaction captured: who may cancel (the admin group), the request checks, which deliveries,
/// orders and escrows allow it, the delivery write and its audit row with the conditions on what was read, the replay,
/// and the races. The same transaction against real DynamoDB is in AdminDeliveryCancelDynamoDbTests.
/// </summary>
public class AdminDeliveryCancelRouteTests
{
    private const string UsersTable = "users-test";
    private const string OrdersTable = "orders-test";
    private const string EscrowsTable = "escrows-test";
    private const string DeliveriesTable = "deliveries-test";
    private const string TxAuditTable = "transaction-audit-test";
    private const string DeliveryId = "del-19";
    private const string TripId = "trip-19";
    private const string OrderId = "order-19";
    private const string OrderEscrowId = "escrow-order-order-19";
    private const string Reason = "Truck broke down; the farmer took the goods back.";
    private const string ForbiddenBody = "{\"error\":{\"code\":\"ADMIN_001\",\"message\":\"Admin access required.\"}}";

    private static readonly DateTime Now = new(2026, 11, 13, 9, 30, 0, DateTimeKind.Utc);

    private readonly IAmazonDynamoDB _db = Substitute.For<IAmazonDynamoDB>();
    private readonly TestLambdaContext _context = new();
    private readonly List<TransactWriteItemsRequest> _transactions = new();
    private readonly Dictionary<string, Dictionary<string, AttributeValue>> _escrows = new();
    private readonly List<string> _orderIndex = new() { OrderEscrowId };
    private string? _deliveryIndex;
    private Dictionary<string, AttributeValue>? _delivery = Delivery("pickedUp");
    private Dictionary<string, AttributeValue>? _order = Order("in_transit");

    public AdminDeliveryCancelRouteTests()
    {
        _escrows[OrderEscrowId] = Escrow(OrderEscrowId, "refunded");
        _db.QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>()).Returns(ci => Answer(ci.Arg<QueryRequest>()));
        _db.TransactWriteItemsAsync(Arg.Do<TransactWriteItemsRequest>(_transactions.Add), Arg.Any<CancellationToken>())
            .Returns(new TransactWriteItemsResponse());
    }

    private QueryResponse Answer(QueryRequest request)
    {
        IEnumerable<Dictionary<string, AttributeValue>?> rows = request switch
        {
            { TableName: DeliveriesTable } => new[] { _delivery },
            { TableName: OrdersTable } => new[] { _order },
            { TableName: EscrowsTable, IndexName: "OrderIndex" } => _orderIndex.Select(id => _escrows.GetValueOrDefault(id)),
            { TableName: EscrowsTable, IndexName: "DeliveryIndex" } =>
                new[] { _deliveryIndex == null ? null : _escrows.GetValueOrDefault(_deliveryIndex) },
            { TableName: EscrowsTable } => new[] { _escrows.GetValueOrDefault(request.ExpressionAttributeValues[":id"].S) },
            _ => Array.Empty<Dictionary<string, AttributeValue>?>()
        };
        return new QueryResponse { Items = rows.Where(r => r != null).Select(r => new Dictionary<string, AttributeValue>(r!)).ToList() };
    }

    private AdminFunction Function(string transactionAuditTable = TxAuditTable) =>
        new(_db, UsersTable, OrdersTable, EscrowsTable, DeliveriesTable, "", transactionAuditTable) { Clock = () => Now };

    private static AttributeValue S(string value) => new() { S = value };

    private static AttributeValue N(int value) => new() { N = value.ToString() };

    private static Dictionary<string, AttributeValue> Delivery(string status, int version = 3, string? orderId = OrderId)
    {
        var row = new Dictionary<string, AttributeValue>
        {
            ["deliveryId"] = S(DeliveryId),
            ["tripId"] = S(TripId),
            ["status"] = S(status),
            ["version"] = N(version),
            ["buyerId"] = S("buyer-19"),
            ["farmerId"] = S("farmer-19"),
            ["truckerId"] = S("trucker-19"),
            ["createdAt"] = S("2026-11-10T08:00:00Z"),
            ["pickedUpAt"] = S("2026-11-11T07:00:00Z")
        };
        if (orderId != null)
        {
            row["orderId"] = S(orderId);
        }

        return row;
    }

    private static Dictionary<string, AttributeValue> Order(string status) => new()
    {
        ["orderId"] = S(OrderId),
        ["createdAt"] = S("2026-11-09T08:00:00Z"),
        ["status"] = S(status),
        ["version"] = N(7),
        ["refundOverrideAt"] = S("2026-11-12T10:00:00Z")
    };

    private static Dictionary<string, AttributeValue> Escrow(string escrowId, string status) => new()
    {
        ["escrowId"] = S(escrowId),
        ["createdAt"] = S("2026-11-09T09:00:00Z"),
        ["status"] = S(status),
        ["orderId"] = S(OrderId)
    };

    private static APIGatewayCustomAuthorizerContext AdminGroup()
        => new() { { "sub", "admin-1" }, { "cognito:groups", "admin" } };

    private static APIGatewayProxyRequest Request(
        APIGatewayCustomAuthorizerContext? authorizer, string? body, string action = "cancel") => new()
        {
            HttpMethod = "POST",
            Path = $"/admin/deliveries/{DeliveryId}/{action}",
            Resource = "/admin/deliveries/{id}/{action}",
            PathParameters = new Dictionary<string, string> { ["id"] = DeliveryId, ["action"] = action },
            Body = body,
            RequestContext = new APIGatewayProxyRequest.ProxyRequestContext { Authorizer = authorizer ?? AdminGroup() }
        };

    private static string Body(string? reason = Reason, int? expectedVersion = 3)
        => JsonSerializer.Serialize(new Dictionary<string, object?> { ["reason"] = reason, ["expectedVersion"] = expectedVersion });

    private Task<APIGatewayProxyResponse> Cancel(string? body = null, APIGatewayCustomAuthorizerContext? authorizer = null)
        => Function().FunctionHandler(Request(authorizer, body ?? Body()), _context);

    private static JsonElement Json(APIGatewayProxyResponse response) => JsonDocument.Parse(response.Body).RootElement;

    private static string ErrorCode(APIGatewayProxyResponse response) => Json(response).GetProperty("error").GetProperty("code").GetString()!;

    private void TransactionCancelled(params string[] codes)
        => _db.TransactWriteItemsAsync(Arg.Any<TransactWriteItemsRequest>(), Arg.Any<CancellationToken>())
            .Returns<TransactWriteItemsResponse>(_ => throw new TransactionCanceledException("cancelled")
            {
                CancellationReasons = codes.Select(c => new CancellationReason { Code = c }).ToList()
            });

    // ===== The cancel =====

    [Theory]
    [InlineData("pickedUp")]
    [InlineData("inTransit")]
    public async Task AnAdmin_CancelsAPickedUpDelivery_WhoseMoneyWasRefunded(string status)
    {
        _delivery = Delivery(status);

        var response = await Cancel();

        response.StatusCode.Should().Be(200, response.Body);
        var body = Json(response);
        body.GetProperty("deliveryId").GetString().Should().Be(DeliveryId);
        body.GetProperty("orderId").GetString().Should().Be(OrderId);
        body.GetProperty("fromStatus").GetString().Should().Be(status);
        body.GetProperty("status").GetString().Should().Be("cancelled");
        body.GetProperty("cancelReason").GetString().Should().Be("admin_cancelled");
        body.GetProperty("cancelledAt").GetString().Should().Be(Now.ToString("o"));
        body.GetProperty("version").GetInt32().Should().Be(4);
        body.GetProperty("alreadyCancelled").GetBoolean().Should().BeFalse();

        var items = _transactions.Should().ContainSingle().Subject.TransactItems;
        items.Should().HaveCount(4, "the delivery, its audit row, the order check and the escrow check");

        var update = items[0].Update;
        update.TableName.Should().Be(DeliveriesTable);
        update.Key["deliveryId"].S.Should().Be(DeliveryId);
        update.Key["tripId"].S.Should().Be(TripId);
        update.UpdateExpression.Should().Contain("#status = :cancelled").And.Contain("ADD #v :one");
        update.ConditionExpression.Should().Be("#status = :from AND #v = :version AND attribute_not_exists(#deletedAt)");
        update.ExpressionAttributeValues[":from"].S.Should().Be(status);
        update.ExpressionAttributeValues[":version"].N.Should().Be("3");
        update.ExpressionAttributeValues[":reason"].S.Should().Be("admin_cancelled");
        update.ExpressionAttributeValues[":now"].S.Should().Be(Now.ToString("o"));
        update.UpdateExpression.Should().NotContain("deletedAt", "the cancelled row stays in every party's list");

        var audit = items[1].Put;
        audit.TableName.Should().Be(TxAuditTable);
        audit.ConditionExpression.Should().Be("attribute_not_exists(transactionId)");
        audit.Item["transactionId"].S.Should().Be(OrderId);
        audit.Item["timestamp"].S.Should().Be(Now.ToString("o"));
        audit.Item["entityType"].S.Should().Be("delivery");
        audit.Item["entityId"].S.Should().Be(DeliveryId);
        audit.Item["action"].S.Should().Be("admin_delivery_cancelled");
        audit.Item["fromState"].S.Should().Be(status);
        audit.Item["toState"].S.Should().Be("cancelled");
        audit.Item["actor"].S.Should().Be("admin-1");
        audit.Item["actorRole"].S.Should().Be("admin");
        audit.Item["auditId"].S.Should().StartWith($"txaudit-{OrderId}-admin_delivery_cancelled-");
        audit.Item["metadata"].S.Should().Be($"orderStatus=in_transit;escrows={OrderEscrowId}:refunded;reason={Reason}");

        var order = items[2].ConditionCheck;
        order.TableName.Should().Be(OrdersTable);
        order.Key["orderId"].S.Should().Be(OrderId);
        order.Key["createdAt"].S.Should().Be("2026-11-09T08:00:00Z");
        order.ExpressionAttributeValues[":status"].S.Should().Be("in_transit");

        var escrow = items[3].ConditionCheck;
        escrow.TableName.Should().Be(EscrowsTable);
        escrow.Key["escrowId"].S.Should().Be(OrderEscrowId);
        escrow.ExpressionAttributeValues[":status"].S.Should().Be("refunded");

        // Nothing but the transaction is written: no order, escrow, trip or listing row.
        await _db.DidNotReceive().UpdateItemAsync(Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>());
        await _db.DidNotReceive().PutItemAsync(Arg.Any<PutItemRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EveryEscrowBehindTheDelivery_IsCheckedOnce()
    {
        // The order index names the order's own escrow and an older one; the delivery index a legacy one and the
        // order's own again. Each is read once and conditioned in the transaction.
        _escrows["escrow-old"] = Escrow("escrow-old", "cancelled");
        _escrows["escrow-legacy"] = Escrow("escrow-legacy", "completed");
        _orderIndex.Add("escrow-old");
        _deliveryIndex = "escrow-legacy";

        var response = await Cancel();

        response.StatusCode.Should().Be(200, response.Body);
        var checks = _transactions.Single().TransactItems.Skip(3).Select(i => i.ConditionCheck).ToList();
        checks.Select(c => c.Key["escrowId"].S).Should().Equal(OrderEscrowId, "escrow-old", "escrow-legacy");
        checks.Select(c => c.ExpressionAttributeValues[":status"].S).Should().Equal("refunded", "cancelled", "completed");
    }

    [Fact]
    public async Task ADeliveryFromBeforeOrders_WithNoEscrow_IsCancelledUnderItsOwnId()
    {
        _delivery = Delivery("inTransit", orderId: null);
        _orderIndex.Clear();

        var response = await Cancel();

        response.StatusCode.Should().Be(200, response.Body);
        var items = _transactions.Single().TransactItems;
        items.Should().HaveCount(2, "nothing to check besides the delivery");
        items[1].Put.Item["transactionId"].S.Should().Be(DeliveryId);
        items[1].Put.Item["metadata"].S.Should().Be($"orderStatus=none;escrows=none;reason={Reason}");
        await _db.DidNotReceive().QueryAsync(
            Arg.Is<QueryRequest>(r => r.TableName == OrdersTable), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TheReason_IsTrimmed()
    {
        var response = await Cancel(Body($"   {Reason}   "));

        response.StatusCode.Should().Be(200, response.Body);
        _transactions.Single().TransactItems[1].Put.Item["metadata"].S.Should().EndWith($";reason={Reason}");
    }

    // ===== Replays and races =====

    [Fact]
    public async Task TheSameRequestAgain_AnswersAlreadyCancelled_AndWritesNothing()
    {
        _delivery = Delivery("cancelled", version: 4);
        _delivery["cancelReason"] = S("admin_cancelled");
        _delivery["cancelledAt"] = S("2026-11-13T09:00:00.0000000Z");

        var response = await Cancel();

        response.StatusCode.Should().Be(200, response.Body);
        var body = Json(response);
        body.GetProperty("alreadyCancelled").GetBoolean().Should().BeTrue();
        body.GetProperty("cancelledAt").GetString().Should().Be("2026-11-13T09:00:00.0000000Z");
        body.GetProperty("version").GetInt32().Should().Be(4);
        body.TryGetProperty("fromStatus", out _).Should().BeFalse();
        _transactions.Should().BeEmpty();
    }

    [Fact]
    public async Task ALostRaceToTheSameCancel_AnswersAlreadyCancelled()
    {
        var landed = Delivery("cancelled", version: 4);
        landed["cancelReason"] = S("admin_cancelled");
        _db.TransactWriteItemsAsync(Arg.Any<TransactWriteItemsRequest>(), Arg.Any<CancellationToken>())
            .Returns<TransactWriteItemsResponse>(_ =>
            {
                _delivery = landed;
                throw new TransactionCanceledException("cancelled")
                {
                    CancellationReasons = new List<CancellationReason> { new() { Code = "ConditionalCheckFailed" } }
                };
            });

        var response = await Cancel();

        response.StatusCode.Should().Be(200, response.Body);
        Json(response).GetProperty("alreadyCancelled").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task ADeliveryThatMovedUnderTheWrite_Is409Changed()
    {
        _db.TransactWriteItemsAsync(Arg.Any<TransactWriteItemsRequest>(), Arg.Any<CancellationToken>())
            .Returns<TransactWriteItemsResponse>(_ =>
            {
                _delivery = Delivery("delivered", version: 4);
                throw new TransactionCanceledException("cancelled")
                {
                    CancellationReasons = new List<CancellationReason> { new() { Code = "ConditionalCheckFailed" } }
                };
            });

        var response = await Cancel();

        response.StatusCode.Should().Be(409);
        ErrorCode(response).Should().Be("DELIVERY_ADMIN_CANCEL_CHANGED");
        Json(response).GetProperty("error").GetProperty("message").GetString().Should().Contain("The delivery changed");
    }

    [Theory]
    [InlineData("None,None,ConditionalCheckFailed,None")]
    [InlineData("None,None,None,ConditionalCheckFailed")]
    [InlineData("None,TransactionConflict,None,None")]
    public async Task AnOrderOrEscrowThatMovedUnderTheWrite_Is409Changed(string codes)
    {
        TransactionCancelled(codes.Split(','));

        var response = await Cancel();

        response.StatusCode.Should().Be(409);
        ErrorCode(response).Should().Be("DELIVERY_ADMIN_CANCEL_CHANGED");
        Json(response).GetProperty("error").GetProperty("message").GetString().Should().Contain("The order or its payment changed");
    }

    [Fact]
    public async Task AStalePage_Is409Changed_AndNothingIsWritten()
    {
        _delivery = Delivery("inTransit", version: 4);

        var response = await Cancel();

        response.StatusCode.Should().Be(409);
        ErrorCode(response).Should().Be("DELIVERY_ADMIN_CANCEL_CHANGED");
        _transactions.Should().BeEmpty();
    }

    // ===== Refusals =====

    [Theory]
    [InlineData("pending")]
    [InlineData("delivered")]
    [InlineData("disputed")]
    public async Task ADeliveryNotOnTheRoad_IsRefused(string status)
    {
        _delivery = Delivery(status);

        var response = await Cancel();

        response.StatusCode.Should().Be(409);
        ErrorCode(response).Should().Be("DELIVERY_ADMIN_CANCEL_NOT_ALLOWED");
        _transactions.Should().BeEmpty();
    }

    [Fact]
    public async Task ADeliveryAVerdictCancelled_IsNotAReplay_AndIsRefused()
    {
        _delivery = Delivery("cancelled", version: 4);
        _delivery["cancelReason"] = S("dispute_buyer_wins");

        var response = await Cancel(Body(expectedVersion: 4));

        response.StatusCode.Should().Be(409);
        ErrorCode(response).Should().Be("DELIVERY_ADMIN_CANCEL_NOT_ALLOWED");
        Json(response).GetProperty("error").GetProperty("message").GetString().Should().Contain("already cancelled");
    }

    [Theory]
    [InlineData("delivered")]
    [InlineData("settled")]
    [InlineData("disputed")]
    public async Task AnOrderWhoseGoodsArrived_IsRefused(string status)
    {
        _order = Order(status);

        var response = await Cancel();

        response.StatusCode.Should().Be(409);
        ErrorCode(response).Should().Be("DELIVERY_ADMIN_CANCEL_NOT_ALLOWED");
        _transactions.Should().BeEmpty();
    }

    [Fact]
    public async Task AMissingOrderRow_IsRefused()
    {
        _order = null;

        var response = await Cancel();

        response.StatusCode.Should().Be(409);
        ErrorCode(response).Should().Be("DELIVERY_ADMIN_CANCEL_NOT_ALLOWED");
        _transactions.Should().BeEmpty();
    }

    [Theory]
    [InlineData("escrowed")]
    [InlineData("disputed")]
    [InlineData("dispute_resolved")]
    [InlineData("held")]
    [InlineData("released")]
    public async Task MoneyStillHeld_IsRefused_AndNamesTheEscrow(string status)
    {
        _escrows[OrderEscrowId] = Escrow(OrderEscrowId, status);

        var response = await Cancel();

        response.StatusCode.Should().Be(409);
        ErrorCode(response).Should().Be("DELIVERY_ADMIN_CANCEL_MONEY_HELD");
        Json(response).GetProperty("error").GetProperty("message").GetString()
            .Should().Contain($"escrow {OrderEscrowId} is {status}");
        _transactions.Should().BeEmpty();
    }

    [Fact]
    public async Task ALegacyEscrowStillHeld_IsRefused_EvenWhenTheOrderEscrowIsRefunded()
    {
        _escrows["escrow-legacy"] = Escrow("escrow-legacy", "escrowed");
        _deliveryIndex = "escrow-legacy";

        var response = await Cancel();

        response.StatusCode.Should().Be(409);
        ErrorCode(response).Should().Be("DELIVERY_ADMIN_CANCEL_MONEY_HELD");
        _transactions.Should().BeEmpty();
    }

    // ===== The request =====

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("too short")]
    [InlineData("         padded         ")]
    public async Task AReasonOutsideTenTo500Characters_Is400(string? reason)
    {
        var response = await Cancel(Body(reason));

        response.StatusCode.Should().Be(400);
        ErrorCode(response).Should().Be("DELIVERY_ADMIN_CANCEL_INVALID");
        Json(response).GetProperty("error").GetProperty("field").GetString().Should().Be("reason");
        await _db.DidNotReceiveWithAnyArgs().QueryAsync(default!, default);
    }

    [Fact]
    public async Task AReasonOver500Characters_Is400()
    {
        var response = await Cancel(Body(new string('x', 501)));

        response.StatusCode.Should().Be(400);
        Json(response).GetProperty("error").GetProperty("field").GetString().Should().Be("reason");
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task NoExpectedVersion_Is400(int? expectedVersion)
    {
        var response = await Cancel(Body(expectedVersion: expectedVersion));

        response.StatusCode.Should().Be(400);
        ErrorCode(response).Should().Be("DELIVERY_ADMIN_CANCEL_INVALID");
        Json(response).GetProperty("error").GetProperty("field").GetString().Should().Be("expectedVersion");
    }

    [Fact]
    public async Task InvalidJson_Is400()
    {
        var response = await Cancel("{not json");

        response.StatusCode.Should().Be(400);
        ErrorCode(response).Should().Be("ADMIN_005");
    }

    [Fact]
    public async Task AnUnknownDelivery_Is404()
    {
        _delivery = null;

        var response = await Cancel();

        response.StatusCode.Should().Be(404);
        ErrorCode(response).Should().Be("ADMIN_018");
    }

    [Fact]
    public async Task ASoftDeletedDelivery_Is404()
    {
        _delivery!["deletedAt"] = S("2026-10-01T08:00:00Z");

        var response = await Cancel();

        response.StatusCode.Should().Be(404);
        ErrorCode(response).Should().Be("ADMIN_018");
    }

    [Fact]
    public async Task AnyOtherAction_Is404()
    {
        var response = await Function().FunctionHandler(Request(null, Body(), action: "delete"), _context);

        response.StatusCode.Should().Be(404);
        ErrorCode(response).Should().Be("ADMIN_002");
        _transactions.Should().BeEmpty();
    }

    [Fact]
    public async Task WithoutTheTransactionAuditTable_Is503_SoNoCancelGoesUnaudited()
    {
        var response = await Function(transactionAuditTable: "").FunctionHandler(Request(null, Body()), _context);

        response.StatusCode.Should().Be(503);
        ErrorCode(response).Should().Be("ADMIN_004");
        _transactions.Should().BeEmpty();
    }

    // ===== Who may cancel =====

    [Fact]
    public async Task ARoleClaimAdminOutsideTheAdminGroup_GetsTheAdminRefusal_AndNothingIsRead()
    {
        var response = await Cancel(authorizer: new APIGatewayCustomAuthorizerContext { { "sub", "admin-1" }, { "role", "admin" } });

        response.StatusCode.Should().Be(403);
        response.Body.Should().Be(ForbiddenBody);
        await _db.DidNotReceiveWithAnyArgs().QueryAsync(default!, default);
        _transactions.Should().BeEmpty();
    }

    [Theory]
    [InlineData("buyer")]
    [InlineData("farmer")]
    [InlineData("trucker")]
    public async Task AMarketplaceUser_GetsTheAdminRefusal(string role)
    {
        var response = await Cancel(authorizer: new APIGatewayCustomAuthorizerContext { { "sub", $"{role}-19" }, { "role", role } });

        response.StatusCode.Should().Be(403);
        response.Body.Should().Be(ForbiddenBody);
        _transactions.Should().BeEmpty();
    }
}
