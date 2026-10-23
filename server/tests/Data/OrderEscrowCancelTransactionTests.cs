using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using FluentAssertions;
using NSubstitute;
using WeredaConnect.Server.Data;
using Xunit;

namespace WeredaConnect.Server.Tests.Data;

/// <summary>
/// FUP-32: the transport cancel writes the order and the escrow refund arm in one DynamoDB
/// transaction. These tests check the requests and how a cancelled transaction is read; the
/// LocalStack suite (<c>CancelDisputeRaceDynamoDbTests</c>) runs the same conditions for real.
/// </summary>
public class OrderEscrowCancelTransactionTests
{
    private const string OrdersTable = "test-orders";
    private const string EscrowsTable = "test-escrows";
    private const string OrderId = "order-1";
    private const string OrderCreatedAt = "2026-10-01T08:00:00.0000000Z";

    private readonly IAmazonDynamoDB _dynamoDb = Substitute.For<IAmazonDynamoDB>();
    private readonly OrderRepository _orders;
    private readonly EscrowRepository _escrows;
    private readonly List<QueryRequest> _reads = new();
    private TransactWriteItemsRequest? _sent;

    public OrderEscrowCancelTransactionTests()
    {
        _orders = new OrderRepository(_dynamoDb, OrdersTable);
        _escrows = new EscrowRepository(_dynamoDb, EscrowsTable);
        _dynamoDb.QueryAsync(Arg.Do<QueryRequest>(_reads.Add), Arg.Any<CancellationToken>())
            .Returns(new QueryResponse
            {
                Items = new List<Dictionary<string, AttributeValue>>
                {
                    new()
                    {
                        ["orderId"] = new AttributeValue { S = OrderId },
                        ["createdAt"] = new AttributeValue { S = OrderCreatedAt },
                        ["status"] = new AttributeValue { S = "accepted" },
                        ["buyerId"] = new AttributeValue { S = "buyer-1" },
                        ["version"] = new AttributeValue { N = "4" }
                    }
                }
            });
        _dynamoDb.TransactWriteItemsAsync(Arg.Do<TransactWriteItemsRequest>(r => _sent = r), Arg.Any<CancellationToken>())
            .Returns(new TransactWriteItemsResponse());
    }

    private static Dictionary<string, object> CancelFields() => new()
    {
        { "status", "cancelled" },
        { "cancelReason", "transport_failed_buyer_cancelled" },
        { "version", 99 } // never written from the caller: the version comes from expectedVersion
    };

    private TransactWriteItem Arm() => _escrows.BuildRefundArm(
        "escrow-order-order-1", "2026-10-01T09:00:00.0000000Z", "order_transport_failed", "2026-10-23T10:00:00.0000000Z");

    private void TransactionCancelled(params string[] codes)
        => _dynamoDb.TransactWriteItemsAsync(Arg.Any<TransactWriteItemsRequest>(), Arg.Any<CancellationToken>())
            .Returns<TransactWriteItemsResponse>(_ => throw new TransactionCanceledException("cancelled")
            {
                CancellationReasons = codes.Select(c => new CancellationReason { Code = c }).ToList()
            });

    [Fact]
    public async Task TryUpdateWith_WritesTheOrderAndTheLinkedItemsInOneTransaction_OrderFirst()
    {
        var arm = Arm();

        var result = await _orders.TryUpdateWithAsync(OrderId, CancelFields(), 4, "accepted", new[] { arm });

        result.Outcome.Should().Be(OrderLinkedWriteOutcome.Written);
        _sent!.TransactItems.Should().HaveCount(2);
        var order = _sent.TransactItems[0].Update;
        order.TableName.Should().Be(OrdersTable);
        order.Key["orderId"].S.Should().Be(OrderId);
        order.Key["createdAt"].S.Should().Be(OrderCreatedAt);
        order.ConditionExpression.Should().Be("#v = :expectedVersion AND #expectedStatusName = :expectedStatus");
        order.ExpressionAttributeNames["#v"].Should().Be("version");
        order.ExpressionAttributeNames["#expectedStatusName"].Should().Be("status");
        order.ExpressionAttributeValues[":expectedVersion"].N.Should().Be("4");
        order.ExpressionAttributeValues[":expectedStatus"].S.Should().Be("accepted");
        order.ExpressionAttributeValues[":newVersion"].N.Should().Be("5");
        order.UpdateExpression.Should().StartWith("SET #updated = :updated, #v = :newVersion");
        order.ExpressionAttributeNames.Values.Should().Contain(new[] { "status", "cancelReason" });
        order.ExpressionAttributeValues.Values.Select(v => v.S).Should().Contain("cancelled");
        _sent.TransactItems[1].Should().BeSameAs(arm);
    }

    [Fact]
    public async Task TryUpdateWith_ReturnsTheOrderAsWritten_FromAStronglyConsistentRead()
    {
        var result = await _orders.TryUpdateWithAsync(OrderId, CancelFields(), 4, "accepted", new[] { Arm() });

        _reads.Should().ContainSingle().Which.ConsistentRead.Should().BeTrue();
        result.Order!["status"].Should().Be("cancelled");
        result.Order["cancelReason"].Should().Be("transport_failed_buyer_cancelled");
        result.Order["version"].ToString().Should().Be("5");
        result.Order["buyerId"].Should().Be("buyer-1");
        result.Order.Should().ContainKey("updatedAt");
    }

    [Fact]
    public async Task TryUpdateWith_MissingOrder_IsOrderNotFound_AndSendsNothing()
    {
        _dynamoDb.QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(new QueryResponse { Items = new List<Dictionary<string, AttributeValue>>() });

        var result = await _orders.TryUpdateWithAsync(OrderId, CancelFields(), 4, "accepted", new[] { Arm() });

        result.Outcome.Should().Be(OrderLinkedWriteOutcome.OrderNotFound);
        await _dynamoDb.DidNotReceive().TransactWriteItemsAsync(Arg.Any<TransactWriteItemsRequest>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("None", "ConditionalCheckFailed")]
    // The escrow refused AND the version moved: the escrow decides, a retry could not succeed.
    [InlineData("ConditionalCheckFailed", "ConditionalCheckFailed")]
    public async Task TryUpdateWith_ALinkedItemRefused_IsLinkedItemRefused(string orderCode, string escrowCode)
    {
        TransactionCancelled(orderCode, escrowCode);

        var result = await _orders.TryUpdateWithAsync(OrderId, CancelFields(), 4, "accepted", new[] { Arm() });

        result.Outcome.Should().Be(OrderLinkedWriteOutcome.LinkedItemRefused);
        result.Order.Should().BeNull();
    }

    [Fact]
    public async Task TryUpdateWith_OnlyTheOrderConditionFailed_IsOrderChanged()
    {
        TransactionCancelled("ConditionalCheckFailed", "None");

        var result = await _orders.TryUpdateWithAsync(OrderId, CancelFields(), 4, "accepted", new[] { Arm() });

        result.Outcome.Should().Be(OrderLinkedWriteOutcome.OrderChanged);
    }

    [Theory]
    [InlineData("TransactionConflict", "None")]
    [InlineData("None", "ThrottlingError")]
    public async Task TryUpdateWith_CancelledForAnotherReason_Throws(string orderCode, string escrowCode)
    {
        TransactionCancelled(orderCode, escrowCode);

        var write = () => _orders.TryUpdateWithAsync(OrderId, CancelFields(), 4, "accepted", new[] { Arm() });

        await write.Should().ThrowAsync<TransactionCanceledException>();
    }

    [Fact]
    public void BuildRefundArm_SchedulesTheRefund_OnlyWhileTheEscrowIsAPlainHold()
    {
        var arm = Arm().Update;

        arm.TableName.Should().Be(EscrowsTable);
        arm.Key["escrowId"].S.Should().Be("escrow-order-order-1");
        arm.Key["createdAt"].S.Should().Be("2026-10-01T09:00:00.0000000Z");
        arm.UpdateExpression.Should().Be("SET #expiresAt = :at, #refundReason = :reason, #updated = :at");
        arm.ConditionExpression.Should().Be(
            "#status = :escrowed AND attribute_not_exists(#lock) AND attribute_not_exists(#manualPayout) "
            + "AND (attribute_not_exists(#confirmed) OR size(#confirmed) = :zero)");
        arm.ExpressionAttributeNames.Should().Contain(new Dictionary<string, string>
        {
            ["#expiresAt"] = "expiresAt",
            ["#refundReason"] = "refundReason",
            ["#updated"] = "updatedAt",
            ["#status"] = "status",
            ["#lock"] = "settlementLock",
            ["#manualPayout"] = "manualPayout",
            ["#confirmed"] = "deliveryConfirmedAt"
        });
        arm.ExpressionAttributeValues[":at"].S.Should().Be("2026-10-23T10:00:00.0000000Z");
        arm.ExpressionAttributeValues[":reason"].S.Should().Be("order_transport_failed");
        arm.ExpressionAttributeValues[":escrowed"].S.Should().Be("escrowed");
        arm.ExpressionAttributeValues[":zero"].N.Should().Be("0");
    }
}
