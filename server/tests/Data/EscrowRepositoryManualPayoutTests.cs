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

        var ok = await _repo.TryQueueManualPayoutAsync(EscrowId, CreatedAt, "release", "PAYOUT_NOT_SUPPORTED", "disputed", legs, "2026-10-02T09:00:00.0000000Z");

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
            new List<ManualPayoutLegSeed> { new("buyer", "pending", null, null, null) }, "2026-10-02T09:00:00.0000000Z");

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
            + " AND (#status = :escrowed OR #status = :disputed OR #status = :disputeResolved OR #status = :held"
            + " OR (#status = :released AND #fromStatus = :released))"
            + " AND #other0 = :paid");
        // FRD-03: an admin release or refund of a held escrow that the gateway queued closes from held.
        _update.ExpressionAttributeValues[":held"].S.Should().Be("held");
        Name(_update, "#legStatus").Should().Be("manualTruckerStatus");
        Name(_update, "#other0").Should().Be("manualFarmerStatus");
        Name(_update, "#fromStatus").Should().Be("manualPayoutFromStatus");
        _update.ExpressionAttributeValues[":released"].S.Should().Be("released");
        _update.ExpressionAttributeValues[":newStatus"].S.Should().Be("released");
        // ESC-04: once a release closes no trucker leg is owed, so its pending attributes go too.
        // ESC-10: nor is the settlement blocked any more.
        _update.UpdateExpression.Should().Contain("#status = :newStatus")
            .And.Contain("REMOVE #marker, #blockReason, #blockReasonAt, #tPending, #tBlock, #tBlockAt, #tLockId, #tLockAt")
            .And.Contain("ADD #version :one");
        Name(_update, "#tPending").Should().Be("truckerPayoutPending");
        Name(_update, "#tBlock").Should().Be("truckerPayoutBlockReason");
        Name(_update, "#tBlockAt").Should().Be("truckerPayoutBlockReasonAt");
        Name(_update, "#tLockId").Should().Be("truckerPayoutLockId");
        Name(_update, "#tLockAt").Should().Be("truckerPayoutLockAt");

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
    public async Task Close_Refund_RemovesTheMarkerAndTheBlockReasonOnly()
    {
        await _repo.TryCloseManualPayoutAsync(
            EscrowId, CreatedAt, "refund", "buyer", "TB-REF-0009", "admin-1", "2026-10-02T10:05:00Z",
            Array.Empty<string>(), "refunded", new Dictionary<string, object> { { "telebirrRefundRef", "TB-REF-0009" } });

        _update!.UpdateExpression.Should().Contain("REMOVE #marker, #blockReason, #blockReasonAt ADD #version :one");
        Name(_update, "#blockReason").Should().Be("settlementBlockReason");
        Name(_update, "#blockReasonAt").Should().Be("settlementBlockReasonAt");
        _update.ExpressionAttributeNames.Should().NotContainKey("#tPending");
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

    [Fact]
    public async Task UpdateStatus_ToDisputed_RequiresNoOpenManualPayoutAndNoSettlementLock()
    {
        // ESC-03: a dispute never lands on an escrow whose settlement is in flight.
        // FUP-32: nor on one whose refund is scheduled.
        await _repo.UpdateStatusAsync(EscrowId, CreatedAt, "disputed", "escrowed");

        _update!.ConditionExpression.Should().Be(
            "#status = :expectedStatus AND attribute_not_exists(#manualPayout) AND attribute_not_exists(#lock) "
            + "AND attribute_not_exists(#refundReason)");
        Name(_update, "#manualPayout").Should().Be("manualPayout");
        Name(_update, "#lock").Should().Be("settlementLock");
        Name(_update, "#refundReason").Should().Be("refundReason");
    }

    [Fact]
    public async Task UpdateStatus_ToCompleted_RequiresNoOpenManualPayoutAndNoPendingTruckerLeg()
    {
        // ESC-04: an escrow is completed only once every leg is paid.
        await _repo.UpdateStatusAsync(EscrowId, CreatedAt, "completed", "released");

        _update!.ConditionExpression.Should().Be(
            "#status = :expectedStatus AND attribute_not_exists(#manualPayout) AND attribute_not_exists(#truckerPending)");
        Name(_update, "#manualPayout").Should().Be("manualPayout");
        Name(_update, "#truckerPending").Should().Be("truckerPayoutPending");
    }

    [Fact]
    public async Task UpdateStatus_OtherStatuses_KeepTheirCondition()
    {
        await _repo.UpdateStatusAsync(EscrowId, CreatedAt, "escrowed", "escrowed");

        _update!.ConditionExpression.Should().Be("#status = :expectedStatus");
        _update.ExpressionAttributeNames.Should().NotContainKey("#manualPayout");
        _update.ExpressionAttributeNames.Should().NotContainKey("#truckerPending");
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
        var ok = await _repo.TryAcquireSettlementLockAsync(EscrowId, CreatedAt, "refund", "escrowed");

        ok.Should().BeTrue();
        _update!.ConditionExpression.Should().Contain("attribute_not_exists(settlementLock)")
            .And.Contain("attribute_not_exists(#manualPayout)");
        Name(_update, "#manualPayout").Should().Be("manualPayout");
    }

    [Theory]
    [InlineData("escrowed")]
    [InlineData("dispute_resolved")]
    public async Task AcquireLock_RequiresTheExactStatusTheCallerValidated(string status)
    {
        // ESC-03: the lock is a CAS on one status, never a set, so a stale read cannot lock a
        // row that moved on. The real expression runs against DynamoDB in the integration tests.
        var ok = await _repo.TryAcquireSettlementLockAsync(EscrowId, CreatedAt, "release", status);

        ok.Should().BeTrue();
        _update!.ConditionExpression.Should().Be(
            "attribute_not_exists(settlementLock) AND attribute_not_exists(#manualPayout) AND #status = :expected");
        Name(_update, "#status").Should().Be("status");
        _update.ExpressionAttributeValues[":expected"].S.Should().Be(status);
        _update.ExpressionAttributeValues[":op"].S.Should().Be("release");
    }

    [Theory]
    [InlineData("disputed")]
    [InlineData("initiated")]
    [InlineData("released")]
    [InlineData("completed")]
    [InlineData("refunded")]
    [InlineData("held")]
    public async Task AcquireLock_NonSettleableStatus_IsRefusedWithoutAWrite(string status)
    {
        var ok = await _repo.TryAcquireSettlementLockAsync(EscrowId, CreatedAt, "refund", status);

        ok.Should().BeFalse();
        _update.Should().BeNull();
    }

    [Fact]
    public async Task CronQuery_ExcludesQueuedRows_AndKeepsPagingThroughFilteredEmptyPages()
    {
        // 150 queued escrows (oldest expiresAt first) and one fresh escrow behind them. DynamoDB
        // applies Limit before the filter, so the first pages come back empty with a
        // LastEvaluatedKey; the fresh row must still be returned to the sweep.
        var index = Enumerable.Range(0, 150)
            .Select(n => new Dictionary<string, AttributeValue>
            {
                { "escrowId", new AttributeValue { S = $"escrow-queued-{n:D3}" } },
                { "status", new AttributeValue { S = "escrowed" } },
                { "expiresAt", new AttributeValue { S = $"2026-09-01T00:{n / 60:D2}:{n % 60:D2}Z" } },
                { "manualPayout", new AttributeValue { S = "open" } }
            })
            .Append(new Dictionary<string, AttributeValue>
            {
                { "escrowId", new AttributeValue { S = "escrow-fresh" } },
                { "status", new AttributeValue { S = "escrowed" } },
                { "expiresAt", new AttributeValue { S = "2026-10-01T00:00:00Z" } }
            })
            .ToList();
        var requests = new List<QueryRequest>();
        _dynamoDb.QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => FakeIndexPage(index, call.Arg<QueryRequest>(), requests));

        var items = await _repo.QueryByStatusForCronAsync("escrowed", limit: 100);

        items.Select(i => (string)i["escrowId"]).Should().Equal("escrow-fresh");
        requests.Should().HaveCount(2);
        requests.Should().AllSatisfy(r =>
        {
            r.IndexName.Should().Be("StatusExpiresIndex");
            // ESC-04: a released escrow with an owed trucker leg belongs to the trucker retry.
            r.FilterExpression.Should().Be("attribute_not_exists(#manualPayout) AND attribute_not_exists(#truckerPending) AND " + HoldCapFilter);
            r.ExpressionAttributeNames["#manualPayout"].Should().Be("manualPayout");
            r.ExpressionAttributeNames["#truckerPending"].Should().Be("truckerPayoutPending");
        });
    }

    /// <summary>ESC-06: the clause that hides an escrow held to its cap until a refund or a confirmed delivery.</summary>
    private const string HoldCapFilter =
        "(attribute_not_exists(#holdCap) OR attribute_exists(#capRefund) OR attribute_exists(#capConfirmed))";

    [Fact]
    public async Task CronQuery_ExcludesCappedRows_UntilARefundOrAConfirmedDelivery_AndKeepsPaging()
    {
        // ESC-06: 150 escrows held to their cap sit at the head of the oldest-first window; a
        // releasable one and a refund-scheduled capped one behind them must still be returned.
        var index = Enumerable.Range(0, 150)
            .Select(n => new Dictionary<string, AttributeValue>
            {
                { "escrowId", new AttributeValue { S = $"escrow-capped-{n:D3}" } },
                { "status", new AttributeValue { S = "escrowed" } },
                { "expiresAt", new AttributeValue { S = $"2026-09-01T00:{n / 60:D2}:{n % 60:D2}Z" } },
                { "holdCapReachedAt", new AttributeValue { S = "2026-09-01T00:00:00Z" } }
            })
            .Append(new Dictionary<string, AttributeValue>
            {
                { "escrowId", new AttributeValue { S = "escrow-capped-refund" } },
                { "status", new AttributeValue { S = "escrowed" } },
                { "expiresAt", new AttributeValue { S = "2026-09-30T00:00:00Z" } },
                { "holdCapReachedAt", new AttributeValue { S = "2026-09-01T00:00:00Z" } },
                { "refundReason", new AttributeValue { S = "order_transport_failed" } }
            })
            .Append(new Dictionary<string, AttributeValue>
            {
                { "escrowId", new AttributeValue { S = "escrow-capped-confirmed" } },
                { "status", new AttributeValue { S = "escrowed" } },
                { "expiresAt", new AttributeValue { S = "2026-09-30T01:00:00Z" } },
                { "holdCapReachedAt", new AttributeValue { S = "2026-09-01T00:00:00Z" } },
                { "deliveryConfirmedAt", new AttributeValue { S = "2026-09-29T00:00:00Z" } }
            })
            .ToList();
        var requests = new List<QueryRequest>();
        _dynamoDb.QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => FakeIndexPage(index, call.Arg<QueryRequest>(), requests));

        var items = await _repo.QueryByStatusForCronAsync("escrowed", limit: 100);

        items.Select(i => (string)i["escrowId"]).Should().Equal("escrow-capped-refund", "escrow-capped-confirmed");
        requests.Should().AllSatisfy(r =>
        {
            r.ExpressionAttributeNames["#holdCap"].Should().Be("holdCapReachedAt");
            r.ExpressionAttributeNames["#capRefund"].Should().Be("refundReason");
            r.ExpressionAttributeNames["#capConfirmed"].Should().Be("deliveryConfirmedAt");
        });
    }

    [Theory]
    [InlineData("released")]
    [InlineData("dispute_resolved")]
    public async Task CronQuery_OtherStatuses_KeepCappedRowsVisible(string status)
    {
        // A capped escrow that moved on (a verdict, a release) must stay visible to its sweep.
        await _repo.QueryByStatusForCronAsync(status);

        _query!.FilterExpression.Should().Be("attribute_not_exists(#manualPayout) AND attribute_not_exists(#truckerPending)");
        _query.ExpressionAttributeNames.Should().NotContainKey("#holdCap");
    }

    [Fact]
    public async Task CronQuery_DeliveryFilter_IsCombinedWithTheQueueFilter()
    {
        await _repo.QueryByStatusForCronAsync("escrowed", deliveryConfirmedBefore: "2026-10-01T00:00:00Z");

        _query!.FilterExpression.Should().Be(
            "attribute_not_exists(#manualPayout) AND attribute_not_exists(#truckerPending) AND " + HoldCapFilter
            + " AND #confirmedAt <= :confirmedBefore");
    }

    /// <summary>
    /// DynamoDB Query semantics on a sorted index: read up to Limit items from the start key,
    /// then apply the filter, and return a LastEvaluatedKey when more items remain.
    /// </summary>
    private static QueryResponse FakeIndexPage(
        List<Dictionary<string, AttributeValue>> index, QueryRequest request, List<QueryRequest> seen)
    {
        seen.Add(request);
        var start = request.ExclusiveStartKey is { Count: > 0 } key
            ? index.FindIndex(i => i["escrowId"].S == key["escrowId"].S) + 1
            : 0;
        var evaluated = index.Skip(start).Take(request.Limit > 0 ? request.Limit : index.Count).ToList();
        var filterQueued = request.FilterExpression?.Contains("attribute_not_exists(#manualPayout)") == true;
        var filterCapped = request.FilterExpression?.Contains(HoldCapFilter) == true;
        return new QueryResponse
        {
            Items = evaluated
                .Where(i => !filterQueued || !i.ContainsKey("manualPayout"))
                .Where(i => !filterCapped || !i.ContainsKey("holdCapReachedAt")
                    || i.ContainsKey("refundReason") || i.ContainsKey("deliveryConfirmedAt"))
                .ToList(),
            LastEvaluatedKey = start + evaluated.Count < index.Count
                ? new Dictionary<string, AttributeValue> { { "escrowId", evaluated[^1]["escrowId"] } }
                : new Dictionary<string, AttributeValue>()
        };
    }
}
