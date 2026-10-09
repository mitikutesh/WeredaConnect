using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using WeredaConnect.Server.Data;
using Xunit;

namespace WeredaConnect.Server.Tests.Data;

/// <summary>
/// ESC-04: the trucker leg's conditional writes. These capture the requests and check the
/// conditions; the integration suite runs the same writes against real DynamoDB, where an unused
/// expression attribute name is a ValidationException.
/// </summary>
public class EscrowRepositoryTruckerLegTests
{
    private const string Table = "escrows-test";
    private const string EscrowId = "escrow-order-ord-9";
    private const string CreatedAt = "2026-10-01T10:00:00.0000000Z";

    private readonly IAmazonDynamoDB _dynamoDb = Substitute.For<IAmazonDynamoDB>();
    private readonly EscrowRepository _repo;
    private UpdateItemRequest? _update;
    private readonly List<QueryRequest> _queries = new();

    public EscrowRepositoryTruckerLegTests()
    {
        _dynamoDb.UpdateItemAsync(Arg.Do<UpdateItemRequest>(r => _update = r), Arg.Any<CancellationToken>())
            .Returns(new UpdateItemResponse());
        _dynamoDb.QueryAsync(Arg.Do<QueryRequest>(r => _queries.Add(r)), Arg.Any<CancellationToken>())
            .Returns(new QueryResponse { Items = new List<Dictionary<string, AttributeValue>>() });
        _repo = new EscrowRepository(_dynamoDb, Table);
    }

    private static readonly List<ManualPayoutLegSeed> Legs = new()
    {
        new("farmer", "paid", "GW-FARMER-9", "gateway", "2026-10-03T10:00:00Z"),
        new("trucker", "pending", null, null, null)
    };

    /// <summary>Every #name and :value the request registers is used by one of its expressions.</summary>
    private static void AssertNoUnusedNames(UpdateItemRequest r)
    {
        var text = $"{r.UpdateExpression} {r.ConditionExpression}";
        r.ExpressionAttributeNames.Keys.Should().OnlyContain(k => text.Contains(k));
        r.ExpressionAttributeValues.Keys.Should().OnlyContain(k => text.Contains(k));
    }

    [Theory]
    [InlineData("released")]
    [InlineData("completed")]
    public async Task TruckerLegQueries_ReadOneStatusPartition_ForPendingUnqueuedRows(string status)
    {
        if (status == "released")
        {
            await _repo.QueryPendingTruckerLegsAsync(10);
        }
        else
        {
            await _repo.QueryLegacyTruckerLegsAsync(10);
        }

        var query = _queries.Single();
        query.IndexName.Should().Be("StatusExpiresIndex");
        query.KeyConditionExpression.Should().Be("#status = :status");
        query.ExpressionAttributeValues[":status"].S.Should().Be(status);
        query.FilterExpression.Should().Be("#pending = :true AND attribute_not_exists(#manualPayout)");
        query.ExpressionAttributeNames["#pending"].Should().Be("truckerPayoutPending");
        query.ExpressionAttributeValues[":true"].BOOL.Should().BeTrue();
        query.ScanIndexForward.Should().BeTrue();
    }

    [Fact]
    public async Task PendingTruckerLegQuery_KeepsPagingThroughFilteredEmptyPages()
    {
        // M31: DynamoDB applies the filter after the page limit, so pages of settled released rows
        // come back empty with a LastEvaluatedKey; the pending row behind them must still be read.
        var index = Enumerable.Range(0, 120)
            .Select(n => new Dictionary<string, AttributeValue>
            {
                ["escrowId"] = new() { S = $"escrow-plain-{n:D3}" },
                ["status"] = new() { S = "released" }
            })
            .Append(new Dictionary<string, AttributeValue>
            {
                ["escrowId"] = new() { S = "escrow-pending" },
                ["status"] = new() { S = "released" },
                ["truckerPayoutPending"] = new() { BOOL = true }
            })
            .ToList();
        var seen = new List<QueryRequest>();
        _dynamoDb.QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var request = call.Arg<QueryRequest>();
                seen.Add(request);
                var start = request.ExclusiveStartKey is { Count: > 0 } key
                    ? index.FindIndex(i => i["escrowId"].S == key["escrowId"].S) + 1
                    : 0;
                var evaluated = index.Skip(start).Take(50).ToList();
                return new QueryResponse
                {
                    Items = evaluated.Where(i => i.ContainsKey("truckerPayoutPending")).ToList(),
                    LastEvaluatedKey = start + evaluated.Count < index.Count
                        ? new Dictionary<string, AttributeValue> { ["escrowId"] = evaluated[^1]["escrowId"] }
                        : new Dictionary<string, AttributeValue>()
                };
            });

        var rows = await _repo.QueryPendingTruckerLegsAsync(10);

        rows.Select(r => (string)r["escrowId"]).Should().Equal("escrow-pending");
        seen.Should().HaveCount(3);
    }

    [Fact]
    public async Task BlockReason_IsACompareAndSet_OnAnUnlockedUnqueuedPendingReleasedRow()
    {
        var ok = await _repo.TrySetTruckerBlockReasonAsync(EscrowId, CreatedAt, "phone_unverified", "2026-10-04T10:00:00.0000000Z");

        ok.Should().BeTrue();
        _update!.UpdateExpression.Should().Be("SET #reason = :reason, #reasonAt = :reasonAt, #updated = :reasonAt");
        _update.ExpressionAttributeNames["#reasonAt"].Should().Be("truckerPayoutBlockReasonAt");
        _update.ExpressionAttributeValues[":reasonAt"].S.Should().Be("2026-10-04T10:00:00.0000000Z");
        _update.ConditionExpression.Should().Be(
            "(attribute_not_exists(#reason) OR #reason <> :reason) AND #status = :released AND #pending = :true "
            + "AND attribute_not_exists(#manualPayout) AND attribute_not_exists(#lockId)");
        _update.ExpressionAttributeNames["#reason"].Should().Be("truckerPayoutBlockReason");
        _update.ExpressionAttributeNames["#lockId"].Should().Be("truckerPayoutLockId");
        _update.ExpressionAttributeValues[":reason"].S.Should().Be("phone_unverified");
        AssertNoUnusedNames(_update);
    }

    [Fact]
    public async Task Backfill_OnlyWhenTheEscrowHasNoTruckerId()
    {
        await _repo.TryBackfillTruckerIdAsync(EscrowId, CreatedAt, "trucker-9");

        _update!.ConditionExpression.Should().Be("attribute_exists(escrowId) AND attribute_not_exists(#truckerId)");
        _update.ExpressionAttributeValues[":truckerId"].S.Should().Be("trucker-9");
        AssertNoUnusedNames(_update);
    }

    [Fact]
    public async Task AcquireLock_RequiresAReleasedPendingUnlockedUnqueuedRow_AndClearsTheBlockReason()
    {
        var lockId = await _repo.TryAcquireTruckerPayoutLockAsync(EscrowId, CreatedAt);

        lockId.Should().NotBeNullOrEmpty();
        _update!.ConditionExpression.Should().Be(
            "#status = :released AND #pending = :true AND attribute_not_exists(#lockId) AND attribute_not_exists(#manualPayout)");
        _update.UpdateExpression.Should().Be("SET #lockId = :lockId, #lockAt = :now, #updated = :now REMOVE #reason, #reasonAt");
        _update.ExpressionAttributeValues[":lockId"].S.Should().Be(lockId);
        AssertNoUnusedNames(_update);
    }

    [Fact]
    public async Task AcquireLock_ConditionFailed_OnOurOwnLostWrite_CountsAsAcquired()
    {
        string? written = null;
        _dynamoDb.UpdateItemAsync(Arg.Do<UpdateItemRequest>(r => written = r.ExpressionAttributeValues[":lockId"].S),
                Arg.Any<CancellationToken>())
            .ThrowsAsync(new ConditionalCheckFailedException("retried"));
        _dynamoDb.QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ => new QueryResponse
            {
                Items = new List<Dictionary<string, AttributeValue>>
                {
                    new() { ["escrowId"] = new() { S = EscrowId }, ["truckerPayoutLockId"] = new() { S = written! } }
                }
            });

        var lockId = await _repo.TryAcquireTruckerPayoutLockAsync(EscrowId, CreatedAt);

        lockId.Should().Be(written);
    }

    [Fact]
    public async Task AcquireLock_ConditionFailed_OnSomeoneElsesLock_ReturnsNull()
    {
        _dynamoDb.UpdateItemAsync(Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ConditionalCheckFailedException("held"));
        _dynamoDb.QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(new QueryResponse
            {
                Items = new List<Dictionary<string, AttributeValue>>
                {
                    new() { ["escrowId"] = new() { S = EscrowId }, ["truckerPayoutLockId"] = new() { S = "other" } }
                }
            });

        (await _repo.TryAcquireTruckerPayoutLockAsync(EscrowId, CreatedAt)).Should().BeNull();
    }

    [Fact]
    public async Task ReleaseLock_OnlyOurLock_AndNeverOnAQueuedRow()
    {
        await _repo.ReleaseTruckerPayoutLockAsync(EscrowId, CreatedAt, "lock-9");

        _update!.ConditionExpression.Should().Be("#lockId = :lockId AND attribute_not_exists(#manualPayout)");
        _update.UpdateExpression.Should().Be("REMOVE #lockId, #lockAt SET #updated = :now");
        AssertNoUnusedNames(_update);
    }

    [Fact]
    public async Task Record_CompletesTheEscrowUnderOurLock_AndRemovesThePendingAttributes()
    {
        await _repo.TryRecordTruckerPayoutAsync(EscrowId, CreatedAt, "lock-9", "GW-TRUCKER-9",
            "2026-10-04T10:00:00.0000000Z", "2026-10-04T10:00:00.0000001Z");

        _update!.ConditionExpression.Should().Be(
            "#status = :released AND #lockId = :lockId AND attribute_not_exists(#manualPayout)");
        _update.UpdateExpression.Should().Be(
            "SET #status = :completed, #ref = :ref, #paidAt = :paidAt, #completedAt = :completedAt, "
            + "#updated = :completedAt REMOVE #pending, #reason, #reasonAt, #lockId, #lockAt ADD #version :one");
        _update.ExpressionAttributeNames["#ref"].Should().Be("truckerTelebirrRef");
        _update.ExpressionAttributeNames["#paidAt"].Should().Be("truckerPaidAt");
        _update.ExpressionAttributeValues[":ref"].S.Should().Be("GW-TRUCKER-9");
        AssertNoUnusedNames(_update);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("park-9")]
    public async Task QueueTruckerLeg_IsFencedOnTheTruckerLock_NotTheSettlementLock(string? parkId)
    {
        await _repo.TryQueueTruckerLegAsync(EscrowId, CreatedAt, "PAYOUT_NOT_SUPPORTED", Legs,
            "2026-10-04T10:00:00.0000000Z", "lock-9", parkId);

        _update!.ConditionExpression.Should().Be("#status = :fromStatus AND #lockId = :lockId AND attribute_not_exists(#marker)");
        _update.ExpressionAttributeNames["#lockId"].Should().Be("truckerPayoutLockId");
        _update.ExpressionAttributeNames.Should().NotContainKey("#lock");
        _update.ExpressionAttributeValues[":fromStatus"].S.Should().Be("released");
        _update.ExpressionAttributeValues[":op"].S.Should().Be("release");
        _update.UpdateExpression.Should().Contain("#marker = :open");
        if (parkId != null)
        {
            _update.UpdateExpression.Should().Contain("#parkId = :parkId");
            _update.ExpressionAttributeValues[":parkId"].S.Should().Be(parkId);
        }
        else
        {
            _update.ExpressionAttributeNames.Should().NotContainKey("#parkId");
        }

        AssertNoUnusedNames(_update);
    }

    [Theory]
    [InlineData("completed")]
    [InlineData("released")]
    public async Task ParkLegacy_MovesTheRowToReleased_AndQueuesTheLeg_InOneWrite_OnlyWithoutAPendingTime(string current)
    {
        await _repo.TryParkLegacyTruckerLegAsync(EscrowId, CreatedAt, current, Legs, "2026-10-04T10:00:00.0000000Z",
            "2026-09-20T10:00:00.0000000Z");

        _update!.ConditionExpression.Should().Be(
            "#status = :current AND #pending = :true AND attribute_not_exists(#pendingSince) "
            + "AND attribute_not_exists(#tLockId) AND attribute_not_exists(#marker)");
        _update.ExpressionAttributeValues[":current"].S.Should().Be(current);
        _update.ExpressionAttributeNames["#tLockId"].Should().Be("truckerPayoutLockId");
        _update.UpdateExpression.Should().StartWith("SET #status = :fromStatus, #pendingSince = :pendingSince, ")
            .And.Contain("REMOVE #completedAt");
        _update.ExpressionAttributeValues[":reason"].S.Should().Be("GATEWAY_OUTCOME_UNKNOWN");
        _update.ExpressionAttributeValues[":fromStatus"].S.Should().Be("released");
        _update.ExpressionAttributeNames.Should().NotContainKey("#lock");
        AssertNoUnusedNames(_update);
    }

    [Fact]
    public async Task TheEsc02QueueWrites_StillRegisterTheSettlementLock_TheyUse()
    {
        await _repo.TryQueueManualPayoutAsync(EscrowId, CreatedAt, "release", "PAYOUT_NOT_SUPPORTED", "escrowed", Legs,
            "2026-10-04T10:00:00.0000000Z");
        AssertNoUnusedNames(_update!);
        _update!.ExpressionAttributeNames["#lock"].Should().Be("settlementLock");

        await _repo.TryQueueStaleSettlementAsync(EscrowId, CreatedAt, "release", "escrowed", Legs,
            "2026-10-04T10:00:00.0000000Z", "lock-1", "park-1");
        AssertNoUnusedNames(_update!);
        _update!.ExpressionAttributeNames["#lock"].Should().Be("settlementLock");
    }
}
