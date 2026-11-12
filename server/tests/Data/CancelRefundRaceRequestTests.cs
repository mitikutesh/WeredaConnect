using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using WeredaConnect.Server.Data;
using Xunit;

namespace WeredaConnect.Server.Tests.Data;

/// <summary>
/// FUP-53: the requests behind the cancel-then-refund fix. The dispute freeze carries a check that its
/// order is not closed in one transaction, an order close can be fenced on an escrow still held for
/// review, and a plain escrow write (the settlement lock, the expiry push, the fraud hold) that meets a
/// transaction in flight on its row is sent again, at most three times in all. The LocalStack suite
/// (<c>CancelRefundRaceDynamoDbTests</c>) runs the same conditions for real.
/// </summary>
public class CancelRefundRaceRequestTests
{
    private const string OrdersTable = "test-orders";
    private const string EscrowsTable = "test-escrows";
    private const string EscrowId = "escrow-order-order-1";
    private const string EscrowCreatedAt = "2026-10-01T09:00:00.0000000Z";

    private readonly IAmazonDynamoDB _dynamoDb = Substitute.For<IAmazonDynamoDB>();
    private readonly EscrowRepository _escrows;
    private readonly OrderRepository _orders;
    private readonly List<UpdateItemRequest> _updates = new();
    private TransactWriteItemsRequest? _sent;

    public CancelRefundRaceRequestTests()
    {
        _escrows = new EscrowRepository(_dynamoDb, EscrowsTable);
        _orders = new OrderRepository(_dynamoDb, OrdersTable);
        _dynamoDb.TransactWriteItemsAsync(Arg.Do<TransactWriteItemsRequest>(r => _sent = r), Arg.Any<CancellationToken>())
            .Returns(new TransactWriteItemsResponse());
    }

    private static TransactionCanceledException Cancelled(params string[] codes) => new("Transaction cancelled")
    {
        CancellationReasons = codes.Select(c => new CancellationReason { Code = c }).ToList()
    };

    /// <summary>The next UpdateItem calls: a conflict for each true, then success.</summary>
    private void UpdatesConflict(params bool[] conflicts)
    {
        var call = 0;
        _dynamoDb.UpdateItemAsync(Arg.Do<UpdateItemRequest>(_updates.Add), Arg.Any<CancellationToken>())
            .Returns(_ => call < conflicts.Length && conflicts[call++]
                ? throw new TransactionConflictException("Transaction is ongoing for the item")
                : Task.FromResult(new UpdateItemResponse()));
    }

    private static Dictionary<string, object> DisputeFields() => new()
    {
        { "disputeId", "dispute-1" },
        { "disputedBy", "buyer-1" }
    };

    // ===== The dispute freeze with its order check =====

    [Fact]
    public void NotClosedCheck_RequiresTheOrderToExistAndNotBeCancelledOrRejected()
    {
        var check = _orders.BuildNotClosedCheck("order-1", "2026-10-01T08:00:00.0000000Z").ConditionCheck;

        check.TableName.Should().Be(OrdersTable);
        check.Key["orderId"].S.Should().Be("order-1");
        check.Key["createdAt"].S.Should().Be("2026-10-01T08:00:00.0000000Z");
        check.ConditionExpression.Should().Be("attribute_exists(#orderId) AND #status <> :cancelled AND #status <> :rejected");
        check.ExpressionAttributeNames.Should().Contain("#orderId", "orderId").And.Contain("#status", "status");
        check.ExpressionAttributeValues[":cancelled"].S.Should().Be("cancelled");
        check.ExpressionAttributeValues[":rejected"].S.Should().Be("rejected");
    }

    [Fact]
    public async Task FreezeWithTheOrderCheck_IsTheSameEscrowWrite_InOneTransaction()
    {
        var check = _orders.BuildNotClosedCheck("order-1", "2026-10-01T08:00:00.0000000Z");
        UpdateItemRequest? plain = null;
        _dynamoDb.UpdateItemAsync(Arg.Do<UpdateItemRequest>(r => plain = r), Arg.Any<CancellationToken>())
            .Returns(new UpdateItemResponse());
        await _escrows.UpdateStatusAsync(EscrowId, EscrowCreatedAt, "disputed", "escrowed", DisputeFields());

        var outcome = await _escrows.TryUpdateStatusWithAsync(EscrowId, EscrowCreatedAt, "disputed", "escrowed", DisputeFields(), new[] { check });

        outcome.Should().Be(EscrowLinkedWriteOutcome.Written);
        _sent!.TransactItems.Should().HaveCount(2);
        var freeze = _sent.TransactItems[0].Update;
        freeze.TableName.Should().Be(EscrowsTable);
        freeze.Key["escrowId"].S.Should().Be(EscrowId);
        freeze.UpdateExpression.Should().Be(plain!.UpdateExpression);
        // Every condition of the plain freeze: the status, the queue, the lock, a scheduled refund, the ledger leg.
        freeze.ConditionExpression.Should().Be(plain.ConditionExpression);
        freeze.ConditionExpression.Should().Contain("attribute_not_exists(#refundReason)").And.Contain("attribute_not_exists(#lock)");
        freeze.ExpressionAttributeNames.Should().BeEquivalentTo(plain.ExpressionAttributeNames);
        freeze.ExpressionAttributeValues.Keys.Should().BeEquivalentTo(plain.ExpressionAttributeValues.Keys);
        _sent.TransactItems[1].Should().BeSameAs(check);
    }

    [Theory]
    [InlineData(EscrowLinkedWriteOutcome.EscrowRefused, "ConditionalCheckFailed", "None")]
    [InlineData(EscrowLinkedWriteOutcome.EscrowRefused, "ConditionalCheckFailed", "ConditionalCheckFailed")]
    [InlineData(EscrowLinkedWriteOutcome.LinkedItemRefused, "None", "ConditionalCheckFailed")]
    public async Task FreezeWithTheOrderCheck_TheEscrowsOwnRefusalDecides(EscrowLinkedWriteOutcome expected, string escrowCode, string orderCode)
    {
        _dynamoDb.TransactWriteItemsAsync(Arg.Any<TransactWriteItemsRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(Cancelled(escrowCode, orderCode));

        var outcome = await _escrows.TryUpdateStatusWithAsync(EscrowId, EscrowCreatedAt, "disputed", "escrowed", DisputeFields(),
            new[] { _orders.BuildNotClosedCheck("order-1", "2026-10-01T08:00:00.0000000Z") });

        outcome.Should().Be(expected);
    }

    [Fact]
    public async Task FreezeWithTheOrderCheck_ACancelForAnyOtherReason_Throws_NothingWritten()
    {
        _dynamoDb.TransactWriteItemsAsync(Arg.Any<TransactWriteItemsRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(Cancelled("TransactionConflict", "None"));

        var write = () => _escrows.TryUpdateStatusWithAsync(EscrowId, EscrowCreatedAt, "disputed", "escrowed", DisputeFields(),
            new[] { _orders.BuildNotClosedCheck("order-1", "2026-10-01T08:00:00.0000000Z") });

        await write.Should().ThrowAsync<TransactionCanceledException>();
    }

    [Fact]
    public void AdminRefundArm_DropsOnlyTheConfirmedDeliveryCondition_AndItsNames()
    {
        var plain = _escrows.BuildRefundArm(EscrowId, EscrowCreatedAt, "admin_refund", "2026-10-20T10:00:00.0000000Z").Update;
        var admin = _escrows.BuildRefundArm(EscrowId, EscrowCreatedAt, "admin_refund", "2026-10-20T10:00:00.0000000Z",
            allowConfirmedDelivery: true).Update;

        plain.ConditionExpression.Should().Contain("#confirmed");
        admin.ConditionExpression.Should().Be(
            "#status = :escrowed AND attribute_not_exists(#lock) AND attribute_not_exists(#manualPayout) AND "
            + "(attribute_not_exists(#ledgerLegs) OR #ledgerLegs.#ledgerCrop = :ledgerHeld)");
        // DynamoDB refuses names and values the expressions do not use.
        admin.ExpressionAttributeNames.Should().NotContainKey("#confirmed");
        admin.ExpressionAttributeValues.Should().NotContainKey(":zero");
        admin.UpdateExpression.Should().Be(plain.UpdateExpression);
        admin.ExpressionAttributeValues[":reason"].S.Should().Be("admin_refund");
    }

    [Fact]
    public void HoldFence_IsAStatusCheckOnTheEscrow()
    {
        var check = _escrows.BuildStatusCheck(EscrowId, EscrowCreatedAt, "held").ConditionCheck;

        check.TableName.Should().Be(EscrowsTable);
        check.Key["escrowId"].S.Should().Be(EscrowId);
        check.Key["createdAt"].S.Should().Be(EscrowCreatedAt);
        check.ConditionExpression.Should().Be("#status = :status");
        check.ExpressionAttributeValues[":status"].S.Should().Be("held");
    }

    // ===== A plain escrow write that meets a transaction in flight =====

    [Fact]
    public async Task SettlementLock_ATransactionInFlight_IsSentAgain_WithTheSameLockId()
    {
        UpdatesConflict(true, true);

        (await _escrows.TryAcquireSettlementLockAsync(EscrowId, EscrowCreatedAt, "refund", "escrowed")).Should().BeTrue();

        _updates.Should().HaveCount(3);
        _updates.Select(u => u.ExpressionAttributeValues[":lockId"].S).Distinct().Should().ContainSingle();
        _updates.Should().OnlyContain(u => u.ConditionExpression == _updates[0].ConditionExpression);
    }

    [Fact]
    public async Task SettlementLock_StillInFlightAfterThreeWrites_ThrowsAsBefore()
    {
        UpdatesConflict(true, true, true);

        var acquire = () => _escrows.TryAcquireSettlementLockAsync(EscrowId, EscrowCreatedAt, "refund", "escrowed");

        await acquire.Should().ThrowAsync<TransactionConflictException>();
        _updates.Should().HaveCount(EscrowRepository.TransactionConflictAttempts);
    }

    [Fact]
    public async Task SettlementLock_AFailedCondition_IsNeverSentAgain()
    {
        _dynamoDb.UpdateItemAsync(Arg.Do<UpdateItemRequest>(_updates.Add), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ConditionalCheckFailedException("locked"));
        _dynamoDb.QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(new QueryResponse { Items = new List<Dictionary<string, AttributeValue>>() });

        (await _escrows.TryAcquireSettlementLockAsync(EscrowId, EscrowCreatedAt, "refund", "escrowed")).Should().BeFalse();

        _updates.Should().ContainSingle();
    }

    [Fact]
    public async Task ExpiryPush_ATransactionInFlight_IsSentAgain()
    {
        UpdatesConflict(true);

        (await _escrows.TryExtendExpiryAsync(EscrowId, EscrowCreatedAt, "2026-10-22T09:00:00.0000000Z")).Should().BeTrue();

        _updates.Should().HaveCount(2);
        _updates[1].ConditionExpression.Should().Contain("attribute_not_exists(#refundReason)");
    }

    [Fact]
    public async Task ExpiryPush_StillInFlightAfterThreeWrites_ThrowsAsBefore()
    {
        UpdatesConflict(true, true, true);

        var push = () => _escrows.TryExtendExpiryAsync(EscrowId, EscrowCreatedAt, "2026-10-22T09:00:00.0000000Z");

        await push.Should().ThrowAsync<TransactionConflictException>();
        _updates.Should().HaveCount(3);
    }

    private static EscrowHoldRequest Hold() => new(
        HoldReason: "risk_high", HeldAt: "2026-10-20T10:00:00.0000000Z", FromStatus: "escrowed", HeldFor: "release");

    [Fact]
    public async Task FraudHold_ATransactionInFlight_IsSentAgain()
    {
        UpdatesConflict(true);

        (await _escrows.TryHoldAsync(EscrowId, EscrowCreatedAt, Hold())).Should().Be(EscrowHoldWrite.Held);

        _updates.Should().HaveCount(2);
    }

    [Fact]
    public async Task FraudHold_StillInFlightAfterThreeWrites_IsTransient_NothingWritten()
    {
        // As a throttle: the next run tries again.
        UpdatesConflict(true, true, true);

        (await _escrows.TryHoldAsync(EscrowId, EscrowCreatedAt, Hold())).Should().Be(EscrowHoldWrite.Conflict);

        _updates.Should().HaveCount(3);
    }
}
