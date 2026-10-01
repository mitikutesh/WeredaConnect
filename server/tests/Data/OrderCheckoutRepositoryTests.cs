using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using WeredaConnect.Server.Data;
using Xunit;

namespace WeredaConnect.Server.Tests.Data;

/// <summary>
/// ESC-01: the order checkout rows in the escrow idempotency table. The conditions are what keep
/// an order to one open checkout and a merch_order_id to one mapping, so they are pinned here.
/// </summary>
public class OrderCheckoutRepositoryTests
{
    private const string TableName = "test-escrow-idempotency";
    private const string OrderId = "order-1";
    private const string MerchOrderId = "WCO39D3159FE171BAD33894DB64001";

    private static readonly DateTime OpenedAt = new(2026, 9, 30, 8, 0, 0, DateTimeKind.Utc);

    private readonly IAmazonDynamoDB _dynamoDb = Substitute.For<IAmazonDynamoDB>();
    private readonly OrderCheckoutRepository _sut;

    public OrderCheckoutRepositoryTests()
    {
        _sut = new OrderCheckoutRepository(_dynamoDb, TableName);
    }

    private static OrderCheckoutSlot Slot(int attempt = 1)
        => new()
        {
            OrderId = OrderId,
            BuyerId = "buyer-1",
            Attempt = attempt,
            MerchOrderId = MerchOrderId,
            Amount = 15000.50m,
            Outcome = OrderCheckoutOutcome.Opening,
            OpenedAtUtc = OpenedAt,
            OpenUntilUtc = OpenedAt.AddMinutes(130)
        };

    private static Dictionary<string, AttributeValue> SlotItem(string kind = "order_checkout_slot")
        => new()
        {
            { "idempotencyKey", new AttributeValue { S = "order-checkout:order-1" } },
            { "kind", new AttributeValue { S = kind } },
            { "orderId", new AttributeValue { S = OrderId } },
            { "buyerId", new AttributeValue { S = "buyer-1" } },
            { "attempt", new AttributeValue { N = "2" } },
            { "merchOrderId", new AttributeValue { S = MerchOrderId } },
            { "amount", new AttributeValue { N = "15000.50" } },
            { "outcome", new AttributeValue { S = "url_issued" } },
            { "openedAt", new AttributeValue { S = "2026-09-30T08:00:00.0000000Z" } },
            { "openUntil", new AttributeValue { S = "2026-09-30T10:10:00.0000000Z" } },
            { "prepayId", new AttributeValue { S = "prepay-1" } }
        };

    private static bool ExpiresIn400Days(Dictionary<string, AttributeValue> item)
    {
        var expected = DateTimeOffset.UtcNow.AddDays(400).ToUnixTimeSeconds();
        var actual = long.Parse(item["expiresAt"].N, CultureInfo.InvariantCulture);
        return Math.Abs(actual - expected) < 60;
    }

    [Fact]
    public async Task TryOpenSlotAsync_FirstAttempt_RequiresThatNoSlotExists()
    {
        PutItemRequest? captured = null;
        await _dynamoDb.PutItemAsync(Arg.Do<PutItemRequest>(r => captured = r), Arg.Any<CancellationToken>());

        var actual = await _sut.TryOpenSlotAsync(Slot(), previousAttempt: null);

        Assert.True(actual);
        Assert.Equal(TableName, captured!.TableName);
        Assert.Equal("attribute_not_exists(idempotencyKey)", captured.ConditionExpression);
        Assert.Equal("order-checkout:order-1", captured.Item["idempotencyKey"].S);
        Assert.Equal("order_checkout_slot", captured.Item["kind"].S);
        Assert.Equal("1", captured.Item["attempt"].N);
        Assert.Equal("15000.50", captured.Item["amount"].N);
        Assert.Equal("opening", captured.Item["outcome"].S);
        Assert.False(captured.Item.ContainsKey("prepayId"));
        Assert.True(ExpiresIn400Days(captured.Item));
    }

    [Fact]
    public async Task TryOpenSlotAsync_NextAttempt_RequiresTheSlotToStillBeThePreviousOne()
    {
        PutItemRequest? captured = null;
        await _dynamoDb.PutItemAsync(Arg.Do<PutItemRequest>(r => captured = r), Arg.Any<CancellationToken>());

        await _sut.TryOpenSlotAsync(Slot(attempt: 2), previousAttempt: 1);

        Assert.Equal("#attempt = :previous", captured!.ConditionExpression);
        Assert.Equal("attempt", captured.ExpressionAttributeNames["#attempt"]);
        Assert.Equal("1", captured.ExpressionAttributeValues[":previous"].N);
    }

    [Fact]
    public async Task TryOpenSlotAsync_ConcurrentOpenerWon_ReturnsFalse()
    {
        _dynamoDb.PutItemAsync(Arg.Any<PutItemRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ConditionalCheckFailedException("slot exists"));

        var actual = await _sut.TryOpenSlotAsync(Slot(), previousAttempt: null);

        Assert.False(actual);
    }

    [Fact]
    public async Task GetSlotAsync_ReadsTheSlotConsistently()
    {
        GetItemRequest? captured = null;
        _dynamoDb.GetItemAsync(Arg.Do<GetItemRequest>(r => captured = r), Arg.Any<CancellationToken>())
            .Returns(new GetItemResponse { Item = SlotItem() });

        var actual = await _sut.GetSlotAsync(OrderId);

        Assert.True(captured!.ConsistentRead);
        Assert.Equal("order-checkout:order-1", captured.Key["idempotencyKey"].S);
        Assert.Equal(2, actual!.Attempt);
        Assert.Equal(MerchOrderId, actual.MerchOrderId);
        Assert.Equal(15000.50m, actual.Amount);
        Assert.Equal(OrderCheckoutOutcome.UrlIssued, actual.Outcome);
        Assert.Equal("prepay-1", actual.PrepayId);
        Assert.Equal(OpenedAt.AddMinutes(130), actual.OpenUntilUtc);
        Assert.Equal(DateTimeKind.Utc, actual.OpenUntilUtc.Kind);
    }

    [Fact]
    public async Task GetSlotAsync_RowOfAnotherKind_ReturnsNull()
    {
        _dynamoDb.GetItemAsync(Arg.Any<GetItemRequest>(), Arg.Any<CancellationToken>())
            .Returns(new GetItemResponse { Item = SlotItem(kind: "order_checkout") });

        var actual = await _sut.GetSlotAsync(OrderId);

        Assert.Null(actual);
    }

    [Fact]
    public async Task UpdateSlotOutcomeAsync_WithOpenUntil_MovesTheWindowForThatAttemptOnly()
    {
        UpdateItemRequest? captured = null;
        await _dynamoDb.UpdateItemAsync(Arg.Do<UpdateItemRequest>(r => captured = r), Arg.Any<CancellationToken>());
        var openUntil = OpenedAt.AddMinutes(140);

        await _sut.UpdateSlotOutcomeAsync(OrderId, 1, OrderCheckoutOutcome.UrlIssued, "prepay-1", openUntil);

        Assert.Equal("#attempt = :attempt", captured!.ConditionExpression);
        Assert.Equal("1", captured.ExpressionAttributeValues[":attempt"].N);
        Assert.Contains("#prepay = :prepay", captured.UpdateExpression);
        Assert.Contains("#openUntil = :openUntil", captured.UpdateExpression);
        Assert.Equal("2026-09-30T10:20:00.0000000Z", captured.ExpressionAttributeValues[":openUntil"].S);
    }

    [Fact]
    public async Task UpdateSlotOutcomeAsync_WithoutOpenUntil_LeavesTheWindow()
    {
        UpdateItemRequest? captured = null;
        await _dynamoDb.UpdateItemAsync(Arg.Do<UpdateItemRequest>(r => captured = r), Arg.Any<CancellationToken>());

        await _sut.UpdateSlotOutcomeAsync(OrderId, 1, OrderCheckoutOutcome.Failed);

        Assert.Equal("SET #outcome = :outcome", captured!.UpdateExpression);
        Assert.False(captured.ExpressionAttributeNames.ContainsKey("#openUntil"));
    }

    [Fact]
    public async Task UpdateSlotOutcomeAsync_SlotMovedToAnotherAttempt_IsANoOp()
    {
        _dynamoDb.UpdateItemAsync(Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ConditionalCheckFailedException("attempt moved"));

        var exception = await Xunit.Record.ExceptionAsync(
            () => _sut.UpdateSlotOutcomeAsync(OrderId, 1, OrderCheckoutOutcome.Unknown));

        Assert.Null(exception);
    }

    [Fact]
    public async Task CloseSlotForMerchOrderAsync_ClosesOnlyTheAttemptThatWasPaid()
    {
        UpdateItemRequest? captured = null;
        await _dynamoDb.UpdateItemAsync(Arg.Do<UpdateItemRequest>(r => captured = r), Arg.Any<CancellationToken>());

        await _sut.CloseSlotForMerchOrderAsync(OrderId, MerchOrderId, OrderCheckoutOutcome.Paid);

        Assert.Equal("#merch = :merch", captured!.ConditionExpression);
        Assert.Equal(MerchOrderId, captured.ExpressionAttributeValues[":merch"].S);
        Assert.Equal("paid", captured.ExpressionAttributeValues[":outcome"].S);
    }

    [Fact]
    public async Task StoreMappingAsync_WritesTheMappingOnlyOnce()
    {
        PutItemRequest? captured = null;
        await _dynamoDb.PutItemAsync(Arg.Do<PutItemRequest>(r => captured = r), Arg.Any<CancellationToken>());

        var actual = await _sut.StoreMappingAsync(
            MerchOrderId, OrderId, "buyer-1", 15000m, "2026-09-30T07:00:00.0000000Z");

        Assert.True(actual);
        Assert.Equal("attribute_not_exists(idempotencyKey)", captured!.ConditionExpression);
        Assert.Equal(MerchOrderId, captured.Item["idempotencyKey"].S);
        Assert.Equal("order_checkout", captured.Item["kind"].S);
        Assert.Equal("escrow-order-order-1", captured.Item["escrowId"].S);
        Assert.Equal("processing", captured.Item["status"].S);
        Assert.Equal("buyer-1", captured.Item["buyerId"].S);
        Assert.Equal("2026-09-30T07:00:00.0000000Z", captured.Item["paymentSubmittedAt"].S);
        Assert.True(ExpiresIn400Days(captured.Item));
    }

    [Fact]
    public async Task StoreMappingAsync_OrderWithoutAPaymentClock_WritesNoPaymentAttempt()
    {
        PutItemRequest? captured = null;
        await _dynamoDb.PutItemAsync(Arg.Do<PutItemRequest>(r => captured = r), Arg.Any<CancellationToken>());

        await _sut.StoreMappingAsync(MerchOrderId, OrderId, "buyer-1", 15000m, paymentSubmittedAt: null);

        Assert.False(captured!.Item.ContainsKey("paymentSubmittedAt"));
    }

    [Fact]
    public async Task StoreMappingAsync_MappingExists_ReturnsFalse()
    {
        _dynamoDb.PutItemAsync(Arg.Any<PutItemRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ConditionalCheckFailedException("exists"));

        var actual = await _sut.StoreMappingAsync(MerchOrderId, OrderId, "buyer-1", 15000m, null);

        Assert.False(actual);
    }

    [Fact]
    public async Task GetMappingAsync_OrderCheckout_ReturnsItsBinding()
    {
        GetItemRequest? captured = null;
        _dynamoDb.GetItemAsync(Arg.Do<GetItemRequest>(r => captured = r), Arg.Any<CancellationToken>())
            .Returns(new GetItemResponse
            {
                Item = new Dictionary<string, AttributeValue>
                {
                    { "idempotencyKey", new AttributeValue { S = MerchOrderId } },
                    { "kind", new AttributeValue { S = "order_checkout" } },
                    { "orderId", new AttributeValue { S = OrderId } },
                    { "buyerId", new AttributeValue { S = "buyer-1" } },
                    { "amount", new AttributeValue { N = "15000" } },
                    { "paymentSubmittedAt", new AttributeValue { S = "2026-09-30T07:00:00.0000000Z" } }
                }
            });

        var actual = await _sut.GetMappingAsync(MerchOrderId);

        Assert.True(captured!.ConsistentRead);
        Assert.Equal(
            new OrderCheckoutMapping(MerchOrderId, OrderId, "buyer-1", 15000m, "2026-09-30T07:00:00.0000000Z"), actual);
    }

    [Fact]
    public async Task GetMappingAsync_EscrowInitiateRow_ReturnsNull()
    {
        _dynamoDb.GetItemAsync(Arg.Any<GetItemRequest>(), Arg.Any<CancellationToken>())
            .Returns(new GetItemResponse
            {
                Item = new Dictionary<string, AttributeValue>
                {
                    { "idempotencyKey", new AttributeValue { S = "buyer-1:delivery-1:initiate:1700000000000" } },
                    { "escrowId", new AttributeValue { S = "escrow-1" } },
                    { "status", new AttributeValue { S = "completed" } }
                }
            });

        var actual = await _sut.GetMappingAsync("buyer-1:delivery-1:initiate:1700000000000");

        Assert.Null(actual);
    }

    [Fact]
    public async Task GetMappingAsync_MappingWithoutAnAmount_Throws()
    {
        // Returning null would send the payment down the /escrow/initiate path, where it is
        // dropped without an alarm.
        _dynamoDb.GetItemAsync(Arg.Any<GetItemRequest>(), Arg.Any<CancellationToken>())
            .Returns(new GetItemResponse
            {
                Item = new Dictionary<string, AttributeValue>
                {
                    { "kind", new AttributeValue { S = "order_checkout" } },
                    { "orderId", new AttributeValue { S = OrderId } },
                    { "buyerId", new AttributeValue { S = "buyer-1" } }
                }
            });

        await Assert.ThrowsAsync<InvalidDataException>(() => _sut.GetMappingAsync(MerchOrderId));
    }
}
