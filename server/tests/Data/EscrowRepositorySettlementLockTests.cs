using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using WeredaConnect.Server.Data;
using Xunit;

namespace WeredaConnect.Server.Tests.Data;

/// <summary>
/// ESC-05: the settlement lock carries its time and a random id; a lost answer on the lock write
/// is recognised by the id; a lock is never released from a queued row; the recovery step finds
/// stale locks and parks them only on the same lock and status. These tests check the DynamoDB
/// requests (the LocalStack suite runs the same conditions for real).
/// </summary>
public class EscrowRepositorySettlementLockTests
{
    private const string Table = "escrows-test";
    private const string EscrowId = "escrow-order-ord-7";
    private const string CreatedAt = "2026-10-01T10:00:00.0000000Z";

    private readonly IAmazonDynamoDB _dynamoDb = Substitute.For<IAmazonDynamoDB>();
    private readonly EscrowRepository _repo;
    private readonly List<UpdateItemRequest> _updates = new();
    private readonly List<QueryRequest> _queries = new();

    public EscrowRepositorySettlementLockTests()
    {
        _dynamoDb.UpdateItemAsync(Arg.Do<UpdateItemRequest>(_updates.Add), Arg.Any<CancellationToken>())
            .Returns(new UpdateItemResponse());
        _dynamoDb.QueryAsync(Arg.Do<QueryRequest>(_queries.Add), Arg.Any<CancellationToken>())
            .Returns(new QueryResponse { Items = new List<Dictionary<string, AttributeValue>>() });
        _repo = new EscrowRepository(_dynamoDb, Table);
    }

    private static Dictionary<string, AttributeValue> Row(params (string Key, string Value)[] attributes)
    {
        var row = new Dictionary<string, AttributeValue>
        {
            ["escrowId"] = new() { S = EscrowId },
            ["createdAt"] = new() { S = CreatedAt },
            ["status"] = new() { S = "escrowed" }
        };
        foreach (var (key, value) in attributes)
        {
            row[key] = new AttributeValue { S = value };
        }

        return row;
    }

    [Fact]
    public async Task AcquireLock_StampsTheLockTimeAndARandomId()
    {
        (await _repo.TryAcquireSettlementLockAsync(EscrowId, CreatedAt, "release", "escrowed")).Should().BeTrue();
        (await _repo.TryAcquireSettlementLockAsync(EscrowId, CreatedAt, "release", "escrowed")).Should().BeTrue();

        var first = _updates[0];
        first.UpdateExpression.Should().Be("SET settlementLock = :op, #lockAt = :now, #lockId = :lockId, #updated = :now");
        first.ExpressionAttributeNames["#lockAt"].Should().Be("settlementLockAt");
        first.ExpressionAttributeNames["#lockId"].Should().Be("settlementLockId");
        DateTime.TryParse(first.ExpressionAttributeValues[":now"].S, out _).Should().BeTrue();
        first.ExpressionAttributeValues[":lockId"].S.Should().NotBe(_updates[1].ExpressionAttributeValues[":lockId"].S);
    }

    [Fact]
    public async Task AcquireLock_ConditionFailed_OnOurOwnLock_CountsAsAcquired()
    {
        // The SDK retried a write whose answer was lost; the retry fails its condition on our lock.
        UpdateItemRequest? sent = null;
        _dynamoDb.UpdateItemAsync(Arg.Do<UpdateItemRequest>(r => sent = r), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ConditionalCheckFailedException("exists"));
        _dynamoDb.QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>()).Returns(_ => new QueryResponse
        {
            Items = new List<Dictionary<string, AttributeValue>>
            {
                Row(("settlementLock", "release"), ("settlementLockId", sent!.ExpressionAttributeValues[":lockId"].S))
            }
        });

        (await _repo.TryAcquireSettlementLockAsync(EscrowId, CreatedAt, "release", "escrowed")).Should().BeTrue();
        await _dynamoDb.Received(1).QueryAsync(Arg.Is<QueryRequest>(q => q.ConsistentRead == true), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("release", "another-id")]
    [InlineData("refund", null)]
    public async Task AcquireLock_ConditionFailed_OnAnotherLock_IsLost(string heldOp, string? heldId)
    {
        UpdateItemRequest? sent = null;
        _dynamoDb.UpdateItemAsync(Arg.Do<UpdateItemRequest>(r => sent = r), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ConditionalCheckFailedException("exists"));
        _dynamoDb.QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>()).Returns(_ => new QueryResponse
        {
            Items = new List<Dictionary<string, AttributeValue>>
            {
                Row(("settlementLock", heldOp), ("settlementLockId", heldId ?? sent!.ExpressionAttributeValues[":lockId"].S))
            }
        });

        (await _repo.TryAcquireSettlementLockAsync(EscrowId, CreatedAt, "release", "escrowed")).Should().BeFalse();
    }

    [Fact]
    public async Task ReleaseLock_RemovesTimeAndId_AndNeverTouchesAQueuedRow()
    {
        await _repo.ReleaseSettlementLockAsync(EscrowId, CreatedAt);

        var update = _updates.Single();
        update.UpdateExpression.Should().Be("REMOVE settlementLock, #lockAt, #lockId SET #updated = :now");
        update.ConditionExpression.Should().Be("attribute_exists(escrowId) AND attribute_not_exists(#manualPayout)");
        update.ExpressionAttributeNames["#manualPayout"].Should().Be("manualPayout");
    }

    [Fact]
    public async Task ReleaseLock_OnAQueuedRow_IsANoOp()
    {
        _dynamoDb.UpdateItemAsync(Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ConditionalCheckFailedException("queued"));

        var act = () => _repo.ReleaseSettlementLockAsync(EscrowId, CreatedAt);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task StaleLockQuery_FiltersLockedUnqueuedRowsByLockTimeOrLegacyUpdatedAt_AndPages()
    {
        var page = 0;
        _dynamoDb.QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>()).Returns(_ => ++page switch
        {
            1 => new QueryResponse
            {
                Items = new List<Dictionary<string, AttributeValue>>(),
                LastEvaluatedKey = new Dictionary<string, AttributeValue> { ["escrowId"] = new() { S = "k1" } }
            },
            _ => new QueryResponse
            {
                Items = new List<Dictionary<string, AttributeValue>> { Row(("settlementLock", "refund")) },
                LastEvaluatedKey = new Dictionary<string, AttributeValue>()
            }
        });

        var rows = await _repo.QueryStaleSettlementLocksAsync("dispute_resolved", "2026-10-09T11:45:00.0000000Z");

        rows.Should().ContainSingle();
        _queries.Should().HaveCount(2);
        var query = _queries[0];
        query.IndexName.Should().Be("StatusExpiresIndex");
        query.KeyConditionExpression.Should().Be("#status = :status");
        query.ExpressionAttributeValues[":status"].S.Should().Be("dispute_resolved");
        query.ExpressionAttributeValues[":before"].S.Should().Be("2026-10-09T11:45:00.0000000Z");
        query.FilterExpression.Should().Be(
            "attribute_exists(#lock) AND attribute_not_exists(#manualPayout) "
            + "AND (#lockAt <= :before OR (attribute_not_exists(#lockAt) AND #updated <= :before))");
        _queries[1].ExclusiveStartKey["escrowId"].S.Should().Be("k1");
    }

    [Fact]
    public async Task QueueStale_IsFencedOnTheSameLockIdAndStatus()
    {
        var legs = new List<ManualPayoutLegSeed> { new("buyer", "pending", null, null, null) };

        (await _repo.TryQueueStaleSettlementAsync(EscrowId, CreatedAt, "refund", "escrowed", legs, "2026-10-09T12:00:00.0000000Z", "id-7", "park-7"))
            .Should().BeTrue();

        var update = _updates.Single();
        update.ConditionExpression.Should().Be(
            "#lock = :op AND attribute_not_exists(#marker) AND #status = :fromStatus AND #lockId = :lockId");
        update.ExpressionAttributeValues[":lockId"].S.Should().Be("id-7");
        update.ExpressionAttributeValues[":reason"].S.Should().Be("GATEWAY_OUTCOME_UNKNOWN");
        update.ExpressionAttributeValues[":fromStatus"].S.Should().Be("escrowed");
        update.ExpressionAttributeNames["#lockId"].Should().Be("settlementLockId");
        update.UpdateExpression.Should().StartWith("SET #parkId = :parkId, ").And.NotContain("#lock =").And.NotContain("REMOVE");
        update.ExpressionAttributeNames["#parkId"].Should().Be("manualPayoutParkId");
        update.ExpressionAttributeValues[":parkId"].S.Should().Be("park-7");
    }

    [Fact]
    public async Task QueueStale_LegacyLock_RequiresNoLockId_AndAFailedConditionIsFalse()
    {
        _dynamoDb.UpdateItemAsync(Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ConditionalCheckFailedException("moved"));
        var legs = new List<ManualPayoutLegSeed> { new("farmer", "pending", null, null, null) };

        (await _repo.TryQueueStaleSettlementAsync(EscrowId, CreatedAt, "release", "disputed", legs, "2026-10-09T12:00:00.0000000Z", null, "park-8"))
            .Should().BeFalse();

        _updates.Single().ConditionExpression.Should().EndWith("AND attribute_not_exists(#lockId)");
    }
}
