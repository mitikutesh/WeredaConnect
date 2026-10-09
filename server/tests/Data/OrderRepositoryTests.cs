using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using NSubstitute;
using WeredaConnect.Server.Data;
using Xunit;

namespace WeredaConnect.Server.Tests.Data;

/// <summary>
/// Verifies the status filter is actually applied. Regression for the web bug
/// where <see cref="OrderRepository.QueryOrdersAsync"/> dropped the status
/// parameter whenever a role id (buyerId/farmerId/truckerId) was present, so
/// every status tab returned the user's full order list.
/// </summary>
public class OrderRepositoryTests
{
    private readonly IAmazonDynamoDB _mockDynamoDb;
    private readonly OrderRepository _repository;

    private const string TableName = "test-orders";

    public OrderRepositoryTests()
    {
        _mockDynamoDb = Substitute.For<IAmazonDynamoDB>();
        _repository = new OrderRepository(_mockDynamoDb, TableName);

        _mockDynamoDb.QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(new QueryResponse
            {
                Items = new List<Dictionary<string, AttributeValue>>(),
                LastEvaluatedKey = new Dictionary<string, AttributeValue>(),
            });
    }

    // SIM-39: a replayed create used to add a second row with the same orderId.
    [Fact]
    public async Task CreateAsync_ExistingOrderId_Returns409WithoutWriting()
    {
        _mockDynamoDb.QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(new QueryResponse
            {
                Items = new List<Dictionary<string, AttributeValue>>
                {
                    new() { { "orderId", new AttributeValue { S = "order-1" } } }
                }
            });

        var result = await _repository.CreateAsync(
            "order-1", new Dictionary<string, object> { { "buyerId", "buyer-1" } });

        Assert.False(result.IsSuccess);
        Assert.Equal(409, result.Error!.HttpStatus);
        await _mockDynamoDb.DidNotReceive().PutItemAsync(
            Arg.Any<PutItemRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateAsync_NewOrderId_Writes()
    {
        var result = await _repository.CreateAsync(
            "order-2", new Dictionary<string, object> { { "buyerId", "buyer-1" } });

        Assert.True(result.IsSuccess);
        await _mockDynamoDb.Received(1).PutItemAsync(
            Arg.Is<PutItemRequest>(r => r.Item["orderId"].S == "order-2"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task QueryOrdersAsync_BuyerIdWithStatus_AppliesStatusFilterOnBuyerIndex()
    {
        await _repository.QueryOrdersAsync(buyerId: "buyer-1", status: "accepted");

        await _mockDynamoDb.Received(1).QueryAsync(
            Arg.Is<QueryRequest>(r =>
                r.IndexName == "BuyerIdCreatedAtIndex" &&
                r.ExpressionAttributeValues[":val"].S == "buyer-1" &&
                r.FilterExpression == "#status = :status" &&
                r.ExpressionAttributeNames["#status"] == "status" &&
                r.ExpressionAttributeValues[":status"].S == "accepted"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task QueryOrdersAsync_FarmerIdWithStatus_AppliesStatusFilterOnFarmerIndex()
    {
        await _repository.QueryOrdersAsync(farmerId: "farmer-1", status: "funded");

        await _mockDynamoDb.Received(1).QueryAsync(
            Arg.Is<QueryRequest>(r =>
                r.IndexName == "FarmerIdCreatedAtIndex" &&
                r.ExpressionAttributeValues[":val"].S == "farmer-1" &&
                r.FilterExpression == "#status = :status" &&
                r.ExpressionAttributeValues[":status"].S == "funded"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task QueryOrdersAsync_TruckerIdWithStatus_AppliesStatusFilterOnTruckerIndex()
    {
        await _repository.QueryOrdersAsync(truckerId: "trucker-1", status: "inTransit");

        await _mockDynamoDb.Received(1).QueryAsync(
            Arg.Is<QueryRequest>(r =>
                r.IndexName == "TruckerIdCreatedAtIndex" &&
                r.ExpressionAttributeValues[":val"].S == "trucker-1" &&
                r.FilterExpression == "#status = :status" &&
                r.ExpressionAttributeValues[":status"].S == "inTransit"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task QueryOrdersAsync_BuyerIdWithoutStatus_DoesNotApplyFilter()
    {
        await _repository.QueryOrdersAsync(buyerId: "buyer-1");

        await _mockDynamoDb.Received(1).QueryAsync(
            Arg.Is<QueryRequest>(r =>
                r.IndexName == "BuyerIdCreatedAtIndex" &&
                r.FilterExpression == null &&
                !r.ExpressionAttributeValues.ContainsKey(":status")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task QueryOrdersAsync_StatusOnly_QueriesStatusIndexWithoutFilter()
    {
        // No role id → status IS the partition key, so no FilterExpression.
        await _repository.QueryOrdersAsync(status: "delivered");

        await _mockDynamoDb.Received(1).QueryAsync(
            Arg.Is<QueryRequest>(r =>
                r.IndexName == "StatusCreatedAtIndex" &&
                r.FilterExpression == null &&
                r.ExpressionAttributeValues[":val"].S == "delivered"),
            Arg.Any<CancellationToken>());
    }

    // --- ESC-01: live payment funding ---

    private void StubStoredOrder(string status)
        => _mockDynamoDb.QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(new QueryResponse
            {
                Items = new List<Dictionary<string, AttributeValue>>
                {
                    new()
                    {
                        { "orderId", new AttributeValue { S = "order-1" } },
                        { "createdAt", new AttributeValue { S = "2026-09-30T08:00:00.0000000Z" } },
                        { "status", new AttributeValue { S = status } },
                        { "version", new AttributeValue { N = "3" } }
                    }
                }
            });

    [Fact]
    public async Task TryMarkFundedAsync_WritesFundedOnlyWhilePaymentSubmitted()
    {
        StubStoredOrder("payment_submitted");
        _mockDynamoDb.UpdateItemAsync(Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>())
            .Returns(new UpdateItemResponse
            {
                Attributes = new Dictionary<string, AttributeValue>
                {
                    { "orderId", new AttributeValue { S = "order-1" } },
                    { "status", new AttributeValue { S = "funded" } },
                    { "telebirrPaymentOrderId", new AttributeValue { S = "TB-1" } }
                }
            });

        var funded = await _repository.TryMarkFundedAsync("order-1", new Dictionary<string, object>
        {
            { "telebirrPaymentOrderId", "TB-1" },
            { "status", "cancelled" }
        });

        Assert.NotNull(funded);
        Assert.Equal("funded", funded!["status"]);
        await _mockDynamoDb.Received(1).QueryAsync(
            Arg.Is<QueryRequest>(r => r.ConsistentRead == true), Arg.Any<CancellationToken>());
        await _mockDynamoDb.Received(1).UpdateItemAsync(
            Arg.Is<UpdateItemRequest>(r =>
                r.ConditionExpression == "#status = :submitted"
                && r.ExpressionAttributeValues[":submitted"].S == "payment_submitted"
                && r.ExpressionAttributeValues[":funded"].S == "funded"
                && r.UpdateExpression.Contains("ADD #v :one")
                && r.ExpressionAttributeValues.Values.Any(v => v.S == "TB-1")
                && !r.ExpressionAttributeValues.Values.Any(v => v.S == "cancelled")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TryMarkFundedAsync_OrderNoLongerPaymentSubmitted_ReturnsNull()
    {
        StubStoredOrder("cancelled");
        _mockDynamoDb.UpdateItemAsync(Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>())
            .Returns<UpdateItemResponse>(_ => throw new ConditionalCheckFailedException("status moved"));

        var funded = await _repository.TryMarkFundedAsync("order-1", new Dictionary<string, object>
        {
            { "telebirrPaymentOrderId", "TB-1" }
        });

        Assert.Null(funded);
    }

    [Fact]
    public async Task TryMarkFundedAsync_MissingOrder_ReturnsNullWithoutWriting()
    {
        var funded = await _repository.TryMarkFundedAsync("order-1", new Dictionary<string, object>());

        Assert.Null(funded);
        await _mockDynamoDb.DidNotReceive().UpdateItemAsync(Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetByIdAsync_ConsistentRead_QueriesConsistently()
    {
        StubStoredOrder("payment_submitted");

        var order = await _repository.GetByIdAsync("order-1", consistentRead: true);

        Assert.NotNull(order);
        await _mockDynamoDb.Received(1).QueryAsync(
            Arg.Is<QueryRequest>(r => r.ConsistentRead == true), Arg.Any<CancellationToken>());
    }

    // --- ESC-08: the delivery slot claim and the delivery row are one transaction ---

    private static Dictionary<string, AttributeValue> DeliveryRow() => new()
    {
        { "deliveryId", new AttributeValue { S = "del-1" } },
        { "tripId", new AttributeValue { S = "trip-1" } },
        { "status", new AttributeValue { S = "pending" } }
    };

    private void StubTransactionCancelled(params string[] codes)
        => _mockDynamoDb.TransactWriteItemsAsync(Arg.Any<TransactWriteItemsRequest>(), Arg.Any<CancellationToken>())
            .Returns<TransactWriteItemsResponse>(_ => throw new TransactionCanceledException("cancelled")
            {
                CancellationReasons = codes.Select(c => new CancellationReason { Code = c }).ToList()
            });

    [Fact]
    public async Task TryCreateDeliveryWithSlot_ClaimsAndWritesInOneTransaction()
    {
        StubStoredOrder("accepted");

        var result = await _repository.TryCreateDeliveryWithSlotAsync("order-1", "test-deliveries", DeliveryRow());

        Assert.Equal(DeliverySlotWriteResult.Written, result);
        await _mockDynamoDb.Received(1).TransactWriteItemsAsync(
            Arg.Is<TransactWriteItemsRequest>(r =>
                r.TransactItems.Count == 2
                && r.TransactItems[0].Update.TableName == TableName
                && r.TransactItems[0].Update.Key["createdAt"].S == "2026-09-30T08:00:00.0000000Z"
                && r.TransactItems[0].Update.UpdateExpression == "SET #slot = :did, #claimedAt = :now"
                && r.TransactItems[0].Update.ConditionExpression
                    == "attribute_exists(orderId) AND (attribute_not_exists(#slot) OR #slot = :did)"
                && r.TransactItems[0].Update.ExpressionAttributeNames["#slot"] == "activeDeliveryId"
                && r.TransactItems[0].Update.ExpressionAttributeNames["#claimedAt"] == "activeDeliveryClaimedAt"
                && r.TransactItems[0].Update.ExpressionAttributeValues[":did"].S == "del-1"
                && r.TransactItems[1].Put.TableName == "test-deliveries"
                && r.TransactItems[1].Put.Item["deliveryId"].S == "del-1"
                && r.TransactItems[1].Put.ConditionExpression == "attribute_not_exists(deliveryId)"),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("2026-10-09T08:00:00.0000000Z", "#slot = :stale AND #claimedAt = :seen")]
    [InlineData(null, "#slot = :stale AND attribute_not_exists(#claimedAt)")]
    public async Task TryCreateDeliveryWithSlot_Replacing_OnlyTakesTheSlotFromTheClaimThatWasRead(
        string? claimedAt, string slotCondition)
    {
        StubStoredOrder("accepted");

        await _repository.TryCreateDeliveryWithSlotAsync(
            "order-1", "test-deliveries", DeliveryRow(), new DeliverySlotHolder("del-dead", claimedAt));

        await _mockDynamoDb.Received(1).TransactWriteItemsAsync(
            Arg.Is<TransactWriteItemsRequest>(r =>
                r.TransactItems[0].Update.ConditionExpression == $"attribute_exists(orderId) AND ({slotCondition})"
                && r.TransactItems[0].Update.ExpressionAttributeValues[":stale"].S == "del-dead"
                && (claimedAt == null
                    ? !r.TransactItems[0].Update.ExpressionAttributeValues.ContainsKey(":seen")
                    : r.TransactItems[0].Update.ExpressionAttributeValues[":seen"].S == claimedAt)),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(DeliverySlotWriteResult.SlotTaken, "ConditionalCheckFailed", "None")]
    [InlineData(DeliverySlotWriteResult.DeliveryExists, "None", "ConditionalCheckFailed")]
    [InlineData(DeliverySlotWriteResult.DeliveryExists, "ConditionalCheckFailed", "ConditionalCheckFailed")]
    public async Task TryCreateDeliveryWithSlot_CancelledTransaction_ReportsWhichConditionFailed(
        DeliverySlotWriteResult expected, string slotCode, string rowCode)
    {
        StubStoredOrder("accepted");
        StubTransactionCancelled(slotCode, rowCode);

        var result = await _repository.TryCreateDeliveryWithSlotAsync("order-1", "test-deliveries", DeliveryRow());

        Assert.Equal(expected, result);
    }

    [Fact]
    public async Task TryCreateDeliveryWithSlot_ConflictingTransaction_Throws()
    {
        // Nothing was written, so the caller's failure is a plain retryable error.
        StubStoredOrder("accepted");
        StubTransactionCancelled("TransactionConflict", "None");

        await Assert.ThrowsAsync<TransactionCanceledException>(() =>
            _repository.TryCreateDeliveryWithSlotAsync("order-1", "test-deliveries", DeliveryRow()));
    }

    [Fact]
    public async Task TryCreateDeliveryWithSlot_MissingOrder_WritesNothing()
    {
        var result = await _repository.TryCreateDeliveryWithSlotAsync("order-1", "test-deliveries", DeliveryRow());

        Assert.Equal(DeliverySlotWriteResult.OrderNotFound, result);
        await _mockDynamoDb.DidNotReceive().TransactWriteItemsAsync(
            Arg.Any<TransactWriteItemsRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReleaseDeliverySlot_RemovesTheClaimAndItsTime_OnlyForTheHolder()
    {
        StubStoredOrder("accepted");

        await _repository.ReleaseDeliverySlotAsync("order-1", "del-1");

        await _mockDynamoDb.Received(1).UpdateItemAsync(
            Arg.Is<UpdateItemRequest>(r =>
                r.UpdateExpression == "REMOVE #slot, #claimedAt"
                && r.ConditionExpression == "#slot = :did"
                && r.ExpressionAttributeNames["#claimedAt"] == "activeDeliveryClaimedAt"
                && r.ExpressionAttributeValues[":did"].S == "del-1"),
            Arg.Any<CancellationToken>());
    }
}
