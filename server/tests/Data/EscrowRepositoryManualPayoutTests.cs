using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using WeredaConnect.Server.Data;
using Xunit;

namespace WeredaConnect.Server.Tests.Data;

/// <summary>
/// ESC-02: the manual payout queue lives on the escrow row behind conditional writes.
/// These tests capture the real DynamoDB requests and check the conditions that make
/// the queue safe: only the lock holder queues, only once; a leg is recorded once; the
/// last leg closes the queue, sets the status and removes the marker in one write.
/// </summary>
public class EscrowRepositoryManualPayoutTests
{
    private const string Table = "escrows-test";
    private const string EscrowId = "escrow-order-ord-1";
    private const string CreatedAt = "2026-10-01T10:00:00.0000000Z";

    private readonly IAmazonDynamoDB _dynamoDb = Substitute.For<IAmazonDynamoDB>();
    private readonly EscrowRepository _repo;
    private UpdateItemRequest? _update;
    private QueryRequest? _query;

    public EscrowRepositoryManualPayoutTests()
    {
        _dynamoDb.UpdateItemAsync(Arg.Do<UpdateItemRequest>(r => _update = r), Arg.Any<CancellationToken>())
            .Returns(new UpdateItemResponse());
        _dynamoDb.QueryAsync(Arg.Do<QueryRequest>(r => _query = r), Arg.Any<CancellationToken>())
            .Returns(new QueryResponse { Items = new List<Dictionary<string, AttributeValue>>() });
        _repo = new EscrowRepository(_dynamoDb, Table);
    }

    private static string Name(UpdateItemRequest r, string alias) => r.ExpressionAttributeNames[alias];

    [Fact]
    public async Task Queue_WritesMarkerLegsAndTouchesOnlyTheLockHolder()
    {
        var legs = new List<ManualPayoutLegSeed>
        {
            new("farmer", "paid", "GW-FARMER-1", "gateway", "2026-10-02T09:00:00Z"),
            new("trucker", "pending", null, null, null)
        };

        var ok = await _repo.TryQueueManualPayoutAsync(EscrowId, CreatedAt, "release", "PAYOUT_NOT_SUPPORTED", "disputed", legs);

        ok.Should().BeTrue();
        _update.Should().NotBeNull();
        _update!.TableName.Should().Be(Table);
        _update.Key["escrowId"].S.Should().Be(EscrowId);
        _update.Key["createdAt"].S.Should().Be(CreatedAt);
        _update.ConditionExpression.Should().Be("#lock = :op AND attribute_not_exists(#marker)");
        Name(_update, "#lock").Should().Be("settlementLock");
        Name(_update, "#marker").Should().Be("manualPayout");
        _update.ExpressionAttributeValues[":op"].S.Should().Be("release");
        _update.ExpressionAttributeValues[":open"].S.Should().Be("open");
        _update.ExpressionAttributeValues[":reason"].S.Should().Be("PAYOUT_NOT_SUPPORTED");
        _update.ExpressionAttributeValues[":fromStatus"].S.Should().Be("disputed");
        _update.UpdateExpression.Should().StartWith("SET ").And.Contain("#marker = :open")
            .And.Contain("#queuedAt = :now").And.Contain("ADD #version :one");
        _update.UpdateExpression.Should().NotContain("REMOVE");
        // The settlement lock is never touched by the queue write.
        _update.UpdateExpression.Should().NotContain("#lock =");

        var names = _update.ExpressionAttributeNames.Values.ToList();
        names.Should().Contain(new[]
        {
            "manualFarmerStatus", "manualFarmerRef", "manualFarmerPaidBy", "manualFarmerPaidAt",
            "manualTruckerStatus", "manualPayoutOp", "manualPayoutReason", "manualPayoutFromStatus", "manualPayoutQueuedAt"
        });
        names.Should().NotContain("manualTruckerRef");
        _update.ExpressionAttributeValues.Values.Select(v => v.S).Should().Contain(new[] { "paid", "pending", "GW-FARMER-1", "gateway" });
    }

    [Fact]
    public async Task Queue_ConditionalCheckFailed_ReturnsFalse()
    {
        _dynamoDb.UpdateItemAsync(Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ConditionalCheckFailedException("lock not held"));

        var ok = await _repo.TryQueueManualPayoutAsync(EscrowId, CreatedAt, "refund", "PAYOUT_NOT_SUPPORTED", "escrowed",
            new List<ManualPayoutLegSeed> { new("buyer", "pending", null, null, null) });

        ok.Should().BeFalse();
    }

    [Fact]
    public async Task MarkLegPaid_RequiresOpenQueueThisLegPendingAndOtherLegStillPending()
    {
        var ok = await _repo.TryMarkManualLegPaidAsync(
            EscrowId, CreatedAt, "release", "farmer", "TB-REF-0001", "admin-1", "2026-10-02T10:00:00Z", "trucker");

        ok.Should().BeTrue();
        _update!.ConditionExpression.Should().Be(
            "#marker = :open AND #op = :op AND #legStatus = :pending AND #otherStatus = :pending");
        Name(_update, "#legStatus").Should().Be("manualFarmerStatus");
        Name(_update, "#otherStatus").Should().Be("manualTruckerStatus");
        Name(_update, "#legRef").Should().Be("manualFarmerRef");
        Name(_update, "#legPaidBy").Should().Be("manualFarmerPaidBy");
        _update.ExpressionAttributeValues[":ref"].S.Should().Be("TB-REF-0001");
        _update.ExpressionAttributeValues[":paidBy"].S.Should().Be("admin-1");
        _update.ExpressionAttributeValues[":paid"].S.Should().Be("paid");
        _update.UpdateExpression.Should().Contain("#legStatus = :paid").And.Contain("ADD #version :one");
        // The leg write never changes the escrow status or the marker.
        _update.UpdateExpression.Should().NotContain("#status").And.NotContain("REMOVE");
    }

    [Fact]
    public async Task MarkLegPaid_WithoutOtherLeg_DoesNotAddTheExtraCondition()
    {
        await _repo.TryMarkManualLegPaidAsync(
            EscrowId, CreatedAt, "refund", "buyer", "TB-REF-0009", "admin-1", "2026-10-02T10:00:00Z", null);

        _update!.ConditionExpression.Should().Be("#marker = :open AND #op = :op AND #legStatus = :pending");
        _update.ExpressionAttributeNames.Should().NotContainKey("#otherStatus");
    }

    [Fact]
    public async Task Close_SetsStatusAndSettlementFieldsRemovesMarkerAndChecksOtherLegsPaid()
    {
        var fields = new Dictionary<string, object>
        {
            { "telebirrReleaseRef", "TB-REF-0001" },
            { "truckerTelebirrRef", "TB-REF-0002" },
            { "releasedAt", "2026-10-02T10:05:00Z" },
            { "status", "ignored" },
            { "version", 99 }
        };

        var ok = await _repo.TryCloseManualPayoutAsync(
            EscrowId, CreatedAt, "release", "trucker", "TB-REF-0002", "admin-1", "2026-10-02T10:05:00Z",
            new[] { "farmer" }, "released", fields);

        ok.Should().BeTrue();
        _update!.ConditionExpression.Should().Be(
            "#marker = :open AND #op = :op AND #legStatus = :pending"
            + " AND (#status = :escrowed OR #status = :disputed OR #status = :disputeResolved)"
            + " AND #other0 = :paid");
        Name(_update, "#legStatus").Should().Be("manualTruckerStatus");
        Name(_update, "#other0").Should().Be("manualFarmerStatus");
        _update.ExpressionAttributeValues[":newStatus"].S.Should().Be("released");
        _update.UpdateExpression.Should().Contain("#status = :newStatus")
            .And.Contain("REMOVE #marker")
            .And.Contain("ADD #version :one");

        var setNames = _update.ExpressionAttributeNames.Values.ToList();
        setNames.Should().Contain(new[] { "telebirrReleaseRef", "truckerTelebirrRef", "releasedAt" });
        _update.ExpressionAttributeValues.Values.Select(v => v.S).Should().Contain(new[] { "TB-REF-0001", "TB-REF-0002" });
        // Protected keys passed in the settlement fields are ignored, not written twice.
        _update.ExpressionAttributeValues.Values.Select(v => v.S).Should().NotContain("ignored");
        _update.ExpressionAttributeValues.Values.Select(v => v.N).Should().NotContain("99");
    }

    [Fact]
    public async Task Close_ConditionalCheckFailed_ReturnsFalse()
    {
        _dynamoDb.UpdateItemAsync(Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ConditionalCheckFailedException("other leg not paid"));

        var ok = await _repo.TryCloseManualPayoutAsync(
            EscrowId, CreatedAt, "refund", "buyer", "TB-REF-0009", "admin-1", "2026-10-02T10:05:00Z",
            Array.Empty<string>(), "refunded", new Dictionary<string, object> { { "telebirrRefundRef", "TB-REF-0009" } });

        ok.Should().BeFalse();
    }

    [Fact]
    public async Task QueryOpen_UsesTheSparseIndexOldestFirstWithClampedLimit()
    {
        var startKey = new Dictionary<string, AttributeValue> { { "escrowId", new AttributeValue { S = "x" } } };

        var (items, lastKey) = await _repo.QueryOpenManualPayoutsAsync(500, startKey);

        items.Should().BeEmpty();
        lastKey.Should().BeNull();
        _query.Should().NotBeNull();
        _query!.TableName.Should().Be(Table);
        _query.IndexName.Should().Be("ManualPayoutIndex");
        _query.KeyConditionExpression.Should().Be("#marker = :open");
        _query.ExpressionAttributeNames["#marker"].Should().Be("manualPayout");
        _query.ExpressionAttributeValues[":open"].S.Should().Be("open");
        _query.ScanIndexForward.Should().BeTrue();
        _query.Limit.Should().Be(20);
        _query.ExclusiveStartKey.Should().BeSameAs(startKey);
    }

    [Fact]
    public async Task QueryOpen_ReturnsItemsAndLastKey()
    {
        var lastEvaluated = new Dictionary<string, AttributeValue> { { "escrowId", new AttributeValue { S = "escrow-2" } } };
        _dynamoDb.QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(new QueryResponse
            {
                Items = new List<Dictionary<string, AttributeValue>>
                {
                    new()
                    {
                        { "escrowId", new AttributeValue { S = "escrow-1" } },
                        { "manualPayout", new AttributeValue { S = "open" } },
                        { "farmerPayout", new AttributeValue { N = "16500" } }
                    }
                },
                LastEvaluatedKey = lastEvaluated
            });

        var (items, lastKey) = await _repo.QueryOpenManualPayoutsAsync(10, null);

        items.Should().ContainSingle();
        items[0]["escrowId"].Should().Be("escrow-1");
        lastKey.Should().BeSameAs(lastEvaluated);
        _query!.Limit.Should().Be(10);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GetById_PassesTheConsistentReadFlag(bool consistent)
    {
        await _repo.GetByIdAsync(EscrowId, consistent);

        _query!.ConsistentRead.Should().Be(consistent);
        _query.KeyConditionExpression.Should().Be("escrowId = :id");
        _query.ExpressionAttributeValues[":id"].S.Should().Be(EscrowId);
    }

    [Theory]
    [InlineData("released")]
    [InlineData("refunded")]
    public async Task UpdateStatus_ToASettledStatus_RequiresNoOpenManualPayout(string newStatus)
    {
        await _repo.UpdateStatusAsync(EscrowId, CreatedAt, newStatus, "escrowed",
            new Dictionary<string, object> { { "telebirrReleaseRef", "GW-1" } });

        _update!.ConditionExpression.Should().Be("#status = :expectedStatus AND attribute_not_exists(#manualPayout)");
        Name(_update, "#manualPayout").Should().Be("manualPayout");
    }

    [Theory]
    [InlineData("completed", "released")]
    [InlineData("escrowed", "escrowed")]
    public async Task UpdateStatus_OtherStatuses_KeepTheirCondition(string newStatus, string expected)
    {
        await _repo.UpdateStatusAsync(EscrowId, CreatedAt, newStatus, expected);

        _update!.ConditionExpression.Should().Be("#status = :expectedStatus");
        _update.ExpressionAttributeNames.Should().NotContainKey("#manualPayout");
    }

    [Fact]
    public async Task UpdateStatus_SettledStatusWithoutExpectedStatus_StillGuardsTheQueue()
    {
        await _repo.UpdateStatusAsync(EscrowId, CreatedAt, "refunded");

        _update!.ConditionExpression.Should().Be("attribute_not_exists(#manualPayout)");
    }

    [Fact]
    public async Task AcquireLock_RefusesAnEscrowWithAnOpenManualPayout()
    {
        var ok = await _repo.TryAcquireSettlementLockAsync(EscrowId, CreatedAt, "refund");

        ok.Should().BeTrue();
        _update!.ConditionExpression.Should().Contain("attribute_not_exists(settlementLock)")
            .And.Contain("attribute_not_exists(#manualPayout)");
        Name(_update, "#manualPayout").Should().Be("manualPayout");
    }
}
