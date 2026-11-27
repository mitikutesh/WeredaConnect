using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using FluentAssertions;
using NSubstitute;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.RateLimiting;
using WeredaConnect.Server.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Services;

/// <summary>
/// ESC-33 (fraud-detection-architecture.md section 14.3): the guard of a signal written onto a user other
/// than the caller (<c>GPS_ANOMALY</c> on the delivery's trucker, caused by the buyer's confirm). A truncated
/// score read is CRITICAL, so the write is bounded first: one row per target and entity, one per source and
/// target, two per target, over the table's 30 days; an unread partition writes nothing. The two caps are
/// held by conditional slot rows, so requests that all read "below the cap" still cannot pass it.
/// </summary>
public class FraudAttributedSignalTests
{
    private const string Trucker = "trucker-1";
    private const string Buyer = "buyer-1";

    private readonly IAmazonDynamoDB _ddb = Substitute.For<IAmazonDynamoDB>();
    private readonly List<string> _log = new();
    private readonly List<PutItemRequest> _puts = new();
    private readonly List<PutItemRequest> _slotPuts = new();
    private readonly List<QueryRequest> _queries = new();

    /// <summary>Slot keys another request already holds: their conditional put fails.</summary>
    private readonly HashSet<string> _heldSlots = new(StringComparer.Ordinal);

    public FraudAttributedSignalTests()
    {
        _ddb.PutItemAsync(Arg.Any<PutItemRequest>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            var put = call.Arg<PutItemRequest>();
            var key = put.Item["signalKey"].S;
            if (!key.StartsWith(FraudDetectionService.AttributionSlotPrefix, StringComparison.Ordinal))
            {
                _puts.Add(put);
                return new PutItemResponse();
            }

            _slotPuts.Add(put);
            if (!_heldSlots.Add(key))
            {
                throw new ConditionalCheckFailedException("held");
            }

            return new PutItemResponse();
        });
        Existing();
    }

    private FraudDetectionService Service(string table = "fraud-signals")
        => new(_ddb, "crops", table, Substitute.For<IRateLimitRepository>()) { AlarmLog = _log.Add };

    private static Dictionary<string, AttributeValue> Row(string entityId, string? source)
    {
        var row = new Dictionary<string, AttributeValue>
        {
            ["signalKey"] = new() { S = $"2026-11-0{entityId.Length % 9 + 1}T10:00:00.0000000Z#GPS_ANOMALY" },
            ["entityId"] = new() { S = entityId }
        };
        if (source != null)
        {
            row[FraudDetectionService.SourceUserIdAttribute] = new AttributeValue { S = source };
        }

        return row;
    }

    private void Existing(params Dictionary<string, AttributeValue>[] rows)
        => _ddb.QueryAsync(Arg.Do<QueryRequest>(_queries.Add), Arg.Any<CancellationToken>())
            .Returns(new QueryResponse { Items = rows.ToList() });

    private Task<AttributedSignalOutcome> RecordAsync(string source = Buyer, string delivery = "del-1", FraudDetectionService? service = null)
        => (service ?? Service()).RecordAttributedSignalAsync(Trucker, source, "GPS_ANOMALY", delivery, "delivery", "why");

    [Fact]
    public async Task ACleanTarget_GetsTheRowWithItsSource()
    {
        (await RecordAsync()).Should().Be(AttributedSignalOutcome.Written);

        var put = _puts.Should().ContainSingle().Subject;
        put.Item["userId"].S.Should().Be(Trucker, "the signal lands on the trucker, not on the buyer who confirmed");
        put.Item["signalType"].S.Should().Be("GPS_ANOMALY");
        put.Item["weight"].N.Should().Be("25");
        put.Item["entityId"].S.Should().Be("del-1");
        put.Item["entityType"].S.Should().Be("delivery");
        put.Item[FraudDetectionService.SourceUserIdAttribute].S.Should().Be(Buyer);
        put.ConditionExpression.Should().Be("attribute_not_exists(signalKey)", "the FRD-08 hour limit still applies");
    }

    [Fact]
    public async Task TheWrite_FirstClaimsTheSourceSlotAndATargetSlot_Conditionally()
    {
        await RecordAsync();

        _slotPuts.Select(p => p.Item["signalKey"].S).Should().Equal(
            $"!attr#GPS_ANOMALY#source#{Buyer}", "!attr#GPS_ANOMALY#slot#1");
        foreach (var slot in _slotPuts)
        {
            slot.Item["userId"].S.Should().Be(Trucker);
            slot.Item["recordType"].S.Should().Be("attribution_slot");
            slot.Item.Should().NotContainKey("status", "a slot row is never on the Fraud Review index");
            slot.ConditionExpression.Should().Be("attribute_not_exists(#k) OR #c <= :now");
            DateTime.Parse(slot.Item["claimedUntil"].S, null, System.Globalization.DateTimeStyles.RoundtripKind)
                .Should().BeCloseTo(DateTime.UtcNow.AddDays(30), TimeSpan.FromMinutes(1));
            long.Parse(slot.Item["expiresAt"].N).Should().BeCloseTo(DateTimeOffset.UtcNow.AddDays(31).ToUnixTimeSeconds(), 60);
        }

        // Before any ISO timestamp, so no read keyed "signalKey >= since" (the score among them) returns a slot.
        string.CompareOrdinal(FraudDetectionService.AttributionSlotPrefix, "1970-01-01").Should().BeNegative();
    }

    [Fact]
    public async Task TwoConfirmsThatBothReadOneRow_CannotBothPassTheCap()
    {
        // The straddling race: each request's Query showed one row (below the cap of two), in two hour
        // buckets. The slot of the first is already held when the second one asks; only one slot is left.
        Existing(Row("d-a", "buyer-2"));
        _heldSlots.Add("!attr#GPS_ANOMALY#slot#1");

        (await RecordAsync(source: "buyer-3", delivery: "del-3")).Should().Be(AttributedSignalOutcome.Written);
        (await RecordAsync(source: "buyer-4", delivery: "del-4")).Should().Be(AttributedSignalOutcome.TargetCapped);

        _puts.Should().ContainSingle();
    }

    [Fact]
    public async Task TwoConfirmsByOneBuyerThatBothReadNothing_CannotBothWrite()
    {
        (await RecordAsync(delivery: "del-1")).Should().Be(AttributedSignalOutcome.Written);
        // The second request's Query ran before the first wrote: it still sees a clean partition.
        (await RecordAsync(delivery: "del-2")).Should().Be(AttributedSignalOutcome.SourceCapped);

        _puts.Should().ContainSingle();
    }

    [Fact]
    public async Task TheGuardRead_IsOneConsistentQueryOfTheTargetsPartitionForThatType()
    {
        await RecordAsync();

        var query = _queries.Should().ContainSingle().Subject;
        query.TableName.Should().Be("fraud-signals");
        query.ConsistentRead.Should().BeTrue();
        query.KeyConditionExpression.Should().Be("#u = :uid AND #k >= :since");
        query.FilterExpression.Should().Be("#t = :type");
        query.ExpressionAttributeValues[":uid"].S.Should().Be(Trucker);
        query.ExpressionAttributeValues[":type"].S.Should().Be("GPS_ANOMALY");
        DateTime.Parse(query.ExpressionAttributeValues[":since"].S, null, System.Globalization.DateTimeStyles.RoundtripKind)
            .Should().BeCloseTo(DateTime.UtcNow.AddDays(-30), TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task ASecondConfirmOfTheSameDelivery_WritesNothing()
    {
        Existing(Row("del-1", Buyer));

        (await RecordAsync()).Should().Be(AttributedSignalOutcome.AlreadyRecorded);

        _puts.Should().BeEmpty();
    }

    [Fact]
    public async Task OneBuyer_PutsAtMostOneRowOnOneTrucker()
    {
        Existing(Row("del-0", Buyer));

        (await RecordAsync(delivery: "del-2")).Should().Be(AttributedSignalOutcome.SourceCapped);

        _puts.Should().BeEmpty();
        _log.Should().ContainSingle().Which.Should().Be("FRAUD_ATTRIBUTED_SIGNAL_SKIPPED signalType=GPS_ANOMALY reason=SourceCapped");
    }

    [Fact]
    public async Task AnotherBuyer_StillAddsTheirOwnRow()
    {
        Existing(Row("del-0", "buyer-2"));

        (await RecordAsync(delivery: "del-2")).Should().Be(AttributedSignalOutcome.Written);
    }

    [Fact]
    public async Task AllBuyersTogether_PutAtMostTwoRowsOnOneTrucker()
    {
        // Two rows are 50, MEDIUM: other users' confirms alone never make a trucker HIGH or CRITICAL.
        Existing(Row("d-a", "buyer-2"), Row("d-bb", "buyer-3"));

        (await RecordAsync(delivery: "del-2")).Should().Be(AttributedSignalOutcome.TargetCapped);

        _puts.Should().BeEmpty();
        _slotPuts.Should().BeEmpty("a request the read already stops claims no slot");
        (FraudDetectionService.AttributedPerTargetCap * FraudDetectionService.SignalWeight("GPS_ANOMALY"))
            .Should().BeLessThan(FraudDetectionService.DefaultRiskTierHigh);
    }

    [Fact]
    public async Task ARowWithoutASource_CountsTowardTheTargetCapOnly()
    {
        // A trucker's own self-reported row (or a legacy one) carries no source.
        Existing(Row("d-a", null));

        (await RecordAsync(delivery: "del-2")).Should().Be(AttributedSignalOutcome.Written);
    }

    [Fact]
    public async Task APartitionWithRowsLeftAfterTheReadCap_WritesNothing()
    {
        var key = new Dictionary<string, AttributeValue> { ["userId"] = new() { S = Trucker }, ["signalKey"] = new() { S = "k" } };
        _ddb.QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(new QueryResponse { Items = new List<Dictionary<string, AttributeValue>>(), LastEvaluatedKey = key });

        (await RecordAsync()).Should().Be(AttributedSignalOutcome.TargetUnread);

        _puts.Should().BeEmpty();
        // Two pages, then the one-row probe (FUP-62).
        await _ddb.Received(3).QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TheHourLimit_IsReportedNotThrown()
    {
        _ddb.PutItemAsync(Arg.Is<PutItemRequest>(p => !p.Item["signalKey"].S.StartsWith("!attr#")), Arg.Any<CancellationToken>())
            .Returns<PutItemResponse>(_ => throw new ConditionalCheckFailedException("exists"));

        (await RecordAsync()).Should().Be(AttributedSignalOutcome.HourLimited);
    }

    [Theory]
    [InlineData("", Buyer, "del-1")]
    [InlineData(Trucker, "", "del-1")]
    [InlineData(Trucker, Buyer, "")]
    public async Task AMissingId_ReadsAndWritesNothing(string target, string source, string entity)
    {
        var outcome = await Service().RecordAttributedSignalAsync(target, source, "GPS_ANOMALY", entity, "delivery", null);

        outcome.Should().Be(AttributedSignalOutcome.Invalid);
        _queries.Should().BeEmpty();
        _puts.Should().BeEmpty();
    }

    [Fact]
    public async Task AnUnconfiguredTable_IsTodaysNoOp()
    {
        (await RecordAsync(service: Service(table: ""))).Should().Be(AttributedSignalOutcome.NotConfigured);

        _queries.Should().BeEmpty();
        _puts.Should().BeEmpty();
    }

    [Fact]
    public async Task AFailedGuardRead_ThrowsAndWritesNothing()
    {
        _ddb.QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>())
            .Returns<QueryResponse>(_ => throw new ProvisionedThroughputExceededException("busy"));

        var act = () => RecordAsync();

        await act.Should().ThrowAsync<ProvisionedThroughputExceededException>();
        _puts.Should().BeEmpty();
    }

    [Fact]
    public async Task ASignalOnTheCaller_CarriesNoSource()
    {
        await Service().RecordFraudSignalAsync(Buyer, "GPS_ANOMALY", "del-1", "delivery", "why");

        _puts.Should().ContainSingle().Which.Item.Should().NotContainKey(FraudDetectionService.SourceUserIdAttribute);
        _queries.Should().BeEmpty("a signal on the caller needs no guard read");
    }
}
