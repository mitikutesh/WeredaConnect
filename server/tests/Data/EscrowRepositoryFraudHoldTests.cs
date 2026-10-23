using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using WeredaConnect.Server.Data;
using Xunit;

namespace WeredaConnect.Server.Tests.Data;

/// <summary>
/// FRD-03 (fraud-detection-architecture.md 18.2, 18.4): the hold write and the settlement lock's admin-only
/// <c>held</c>, as DynamoDB requests. <c>FraudHoldDynamoDbTests</c> runs the same conditions for real.
/// </summary>
public class EscrowRepositoryFraudHoldTests
{
    private const string Table = "escrows-test";
    private const string EscrowId = "escrow-order-ord-7";
    private const string CreatedAt = "2026-10-01T10:00:00.0000000Z";
    private const string HeldAt = "2026-10-20T10:00:00.0000000Z";

    private readonly IAmazonDynamoDB _dynamoDb = Substitute.For<IAmazonDynamoDB>();
    private readonly EscrowRepository _repo;
    private readonly List<UpdateItemRequest> _updates = new();
    private readonly List<TransactWriteItemsRequest> _transactions = new();

    public EscrowRepositoryFraudHoldTests()
    {
        _dynamoDb.UpdateItemAsync(Arg.Do<UpdateItemRequest>(_updates.Add), Arg.Any<CancellationToken>())
            .Returns(new UpdateItemResponse());
        _dynamoDb.TransactWriteItemsAsync(Arg.Do<TransactWriteItemsRequest>(_transactions.Add), Arg.Any<CancellationToken>())
            .Returns(new TransactWriteItemsResponse());
        _repo = new EscrowRepository(_dynamoDb, Table);
    }

    private static readonly EscrowHoldRequest Hold = new("escrowed", "refund", "account_suspended", HeldAt);

    private void LatestRowIs(params (string Key, string Value)[] attributes)
    {
        var row = new Dictionary<string, AttributeValue>
        {
            ["escrowId"] = new() { S = EscrowId },
            ["createdAt"] = new() { S = CreatedAt }
        };
        foreach (var (key, value) in attributes)
        {
            row[key] = new AttributeValue { S = value };
        }

        _dynamoDb.QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(new QueryResponse { Items = new List<Dictionary<string, AttributeValue>> { row } });
    }

    private static TransactionCanceledException Cancelled(string escrowCode, string orderCode) => new("cancelled")
    {
        CancellationReasons = new List<CancellationReason> { new() { Code = escrowCode }, new() { Code = orderCode } }
    };

    [Fact]
    public async Task Lock_RefusesHeld_UnlessTheAdminExitAsksForIt()
    {
        (await _repo.TryAcquireSettlementLockAsync(EscrowId, CreatedAt, "release", "held")).Should().BeFalse();
        _updates.Should().BeEmpty();

        (await _repo.TryAcquireSettlementLockAsync(EscrowId, CreatedAt, "release", "held", allowHeld: true)).Should().BeTrue();
        _updates.Should().ContainSingle();
        _updates[0].ExpressionAttributeValues[":expected"].S.Should().Be("held");
        _updates[0].ConditionExpression.Should().Contain("attribute_not_exists(settlementLock)")
            .And.Contain("attribute_not_exists(#manualPayout)").And.Contain("#status = :expected");
    }

    [Theory]
    [InlineData("disputed")]
    [InlineData("released")]
    public async Task Lock_TheAdminFlag_AddsHeldAndNothingElse(string status)
    {
        (await _repo.TryAcquireSettlementLockAsync(EscrowId, CreatedAt, "refund", status, allowHeld: true)).Should().BeFalse();
        _updates.Should().BeEmpty();
    }

    [Fact]
    public async Task Lock_StillTakesDisputeResolved_ForEveryCaller()
    {
        (await _repo.TryAcquireSettlementLockAsync(EscrowId, CreatedAt, "refund", "dispute_resolved")).Should().BeTrue();
    }

    [Fact]
    public async Task Hold_WithoutAFence_IsOneConditionalUpdate_ThatASettlementWins()
    {
        (await _repo.TryHoldAsync(EscrowId, CreatedAt, Hold)).Should().Be(EscrowHoldWrite.Held);

        var update = _updates.Should().ContainSingle().Subject;
        update.TableName.Should().Be(Table);
        update.UpdateExpression.Should().Be("SET #status = :held, #reason = :reason, #heldAt = :heldAt, #heldBy = :system, "
                                           + "#heldFrom = :from, #heldFor = :for, #updated = :heldAt ADD #version :one");
        update.ConditionExpression.Should().Be(
            "#status = :from AND attribute_not_exists(#lock) AND attribute_not_exists(#manualPayout)");
        update.ExpressionAttributeNames["#reason"].Should().Be("holdReason");
        update.ExpressionAttributeNames["#heldFrom"].Should().Be("heldFromStatus");
        update.ExpressionAttributeNames["#heldFor"].Should().Be("heldFor");
        update.ExpressionAttributeNames["#heldBy"].Should().Be("heldBy");
        update.ExpressionAttributeNames["#lock"].Should().Be("settlementLock");
        update.ExpressionAttributeNames["#manualPayout"].Should().Be("manualPayout");
        update.ExpressionAttributeValues[":held"].S.Should().Be("held");
        update.ExpressionAttributeValues[":from"].S.Should().Be("escrowed");
        update.ExpressionAttributeValues[":for"].S.Should().Be("refund");
        update.ExpressionAttributeValues[":reason"].S.Should().Be("account_suspended");
        update.ExpressionAttributeValues[":system"].S.Should().Be("system");
        update.ExpressionAttributeValues[":heldAt"].S.Should().Be(HeldAt);
        _transactions.Should().BeEmpty();
    }

    [Fact]
    public async Task Hold_WithAFence_IsOneTransaction_WithAConditionCheckOnTheOrdersVersionAndStatus()
    {
        var fence = new OrderFence("orders-test", "ord-7", "2026-10-01T09:00:00.0000000Z", 6, "funded");

        (await _repo.TryHoldAsync(EscrowId, CreatedAt, Hold, fence)).Should().Be(EscrowHoldWrite.Held);

        _updates.Should().BeEmpty();
        var items = _transactions.Should().ContainSingle().Subject.TransactItems;
        items.Should().HaveCount(2);
        items[0].Update.TableName.Should().Be(Table);
        items[0].Update.ConditionExpression.Should().Be(
            "#status = :from AND attribute_not_exists(#lock) AND attribute_not_exists(#manualPayout)");
        var check = items[1].ConditionCheck;
        check.TableName.Should().Be("orders-test");
        check.Key["orderId"].S.Should().Be("ord-7");
        check.Key["createdAt"].S.Should().Be("2026-10-01T09:00:00.0000000Z");
        check.ConditionExpression.Should().Be("#status = :status AND #v = :version");
        check.ExpressionAttributeValues[":status"].S.Should().Be("funded");
        check.ExpressionAttributeValues[":version"].N.Should().Be("6");
    }

    [Fact]
    public async Task Hold_ConditionFailedOnOurOwnHold_CountsAsHeld()
    {
        // The SDK retried a write whose answer was lost; the retry fails its condition on our hold.
        _dynamoDb.UpdateItemAsync(Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ConditionalCheckFailedException("held"));
        LatestRowIs(("status", "held"), ("heldAt", HeldAt));

        (await _repo.TryHoldAsync(EscrowId, CreatedAt, Hold)).Should().Be(EscrowHoldWrite.Held);
    }

    [Fact]
    public async Task Hold_ConditionFailedOnALock_IsEscrowChanged()
    {
        _dynamoDb.UpdateItemAsync(Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ConditionalCheckFailedException("locked"));
        LatestRowIs(("status", "escrowed"), ("settlementLock", "release"));

        (await _repo.TryHoldAsync(EscrowId, CreatedAt, Hold)).Should().Be(EscrowHoldWrite.EscrowChanged);
    }

    [Fact]
    public async Task Hold_AnotherSweepsHold_IsNotOurs()
    {
        _dynamoDb.UpdateItemAsync(Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ConditionalCheckFailedException("held"));
        LatestRowIs(("status", "held"), ("heldAt", "2026-10-20T09:00:00.0000000Z"));

        (await _repo.TryHoldAsync(EscrowId, CreatedAt, Hold)).Should().Be(EscrowHoldWrite.EscrowChanged);
    }

    [Fact]
    public async Task Hold_FenceCancelledByTheOrder_IsOrderChanged()
    {
        _dynamoDb.TransactWriteItemsAsync(Arg.Any<TransactWriteItemsRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(Cancelled("None", "ConditionalCheckFailed"));
        LatestRowIs(("status", "escrowed"));

        var outcome = await _repo.TryHoldAsync(EscrowId, CreatedAt, Hold, new OrderFence("orders-test", "ord-7", CreatedAt, 6, "funded"));

        outcome.Should().Be(EscrowHoldWrite.OrderChanged);
    }

    [Fact]
    public async Task Hold_FenceCancelledByTheEscrow_IsEscrowChanged()
    {
        _dynamoDb.TransactWriteItemsAsync(Arg.Any<TransactWriteItemsRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(Cancelled("ConditionalCheckFailed", "None"));
        LatestRowIs(("status", "escrowed"), ("settlementLock", "refund"));

        var outcome = await _repo.TryHoldAsync(EscrowId, CreatedAt, Hold, new OrderFence("orders-test", "ord-7", CreatedAt, 6, "funded"));

        outcome.Should().Be(EscrowHoldWrite.EscrowChanged);
    }

    [Theory]
    [InlineData("TransactionConflict", "None")]
    [InlineData("None", "ThrottlingError")]
    public async Task Hold_AConcurrentTransactionOrAThrottle_IsATransientConflict_NotAFailure(string escrowCode, string orderCode)
    {
        _dynamoDb.TransactWriteItemsAsync(Arg.Any<TransactWriteItemsRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(Cancelled(escrowCode, orderCode));

        var outcome = await _repo.TryHoldAsync(EscrowId, CreatedAt, Hold, new OrderFence("orders-test", "ord-7", CreatedAt, 6, "funded"));

        outcome.Should().Be(EscrowHoldWrite.Conflict);
    }

    public static TheoryData<Exception> Throttles => new()
    {
        new ProvisionedThroughputExceededException("throttled"),
        new RequestLimitExceededException("account limit")
    };

    [Theory]
    [MemberData(nameof(Throttles))]
    public async Task Hold_AThrottleThatOutlivesTheSdkRetries_IsATransientConflict_FencedOrNot(Exception throttle)
    {
        _dynamoDb.UpdateItemAsync(Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>()).ThrowsAsync(throttle);
        _dynamoDb.TransactWriteItemsAsync(Arg.Any<TransactWriteItemsRequest>(), Arg.Any<CancellationToken>()).ThrowsAsync(throttle);

        (await _repo.TryHoldAsync(EscrowId, CreatedAt, Hold)).Should().Be(EscrowHoldWrite.Conflict);
        (await _repo.TryHoldAsync(EscrowId, CreatedAt, Hold, new OrderFence("orders-test", "ord-7", CreatedAt, 6, "funded")))
            .Should().Be(EscrowHoldWrite.Conflict);
        // Nothing is re-read: a throttled write never landed.
        await _dynamoDb.DidNotReceiveWithAnyArgs().QueryAsync(default!, default);
    }

    [Fact]
    public async Task Hold_AValidationError_IsABug_AndThrows()
    {
        _dynamoDb.TransactWriteItemsAsync(Arg.Any<TransactWriteItemsRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(Cancelled("ValidationError", "None"));

        var act = () => _repo.TryHoldAsync(EscrowId, CreatedAt, Hold, new OrderFence("orders-test", "ord-7", CreatedAt, 6, "funded"));

        await act.Should().ThrowAsync<TransactionCanceledException>();
    }
}
