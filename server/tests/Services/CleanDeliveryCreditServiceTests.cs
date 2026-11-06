using System.Globalization;
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
/// FRD-10 (fraud-detection-architecture.md sections 15 and 20): the clean-delivery credit writer. One
/// credit row per party of a clean trade, idempotent per escrow and party, never written when a party's
/// partition holds a counting signal on the trade, and read by the score as a credit.
/// </summary>
public class CleanDeliveryCreditServiceTests
{
    private const string Table = "test-fraud-signals";
    private const string EscrowId = "esc-1";
    private const string OrderId = "ord-1";
    private const string DeliveryId = "del-1";
    private const string BuyerId = "buyer-1";
    private const string FarmerId = "farmer-1";
    private const string TruckerId = "trucker-1";

    private static readonly DateTime Now = new(2026, 11, 7, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime ReleasedAt = Now.AddMinutes(-1);

    private readonly IAmazonDynamoDB _ddb = Substitute.For<IAmazonDynamoDB>();
    private readonly List<QueryRequest> _queries = new();
    private readonly List<PutItemRequest> _puts = new();

    // Per partition: the pages a Query returns (empty when none is queued) or the exception it throws.
    private readonly Dictionary<string, Queue<QueryResponse>> _pages = new();
    private readonly Dictionary<string, Exception> _readFailures = new();

    // Per user: the exception a put throws; _putFailure applies to every user.
    private readonly Dictionary<string, Exception> _writeFailures = new();
    private Exception? _putFailure;

    public CleanDeliveryCreditServiceTests()
    {
        _ddb.QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>()).Returns(ci =>
        {
            var request = ci.Arg<QueryRequest>();
            _queries.Add(request);
            var userId = request.ExpressionAttributeValues[":uid"].S;
            if (_readFailures.TryGetValue(userId, out var failure))
            {
                return Task.FromException<QueryResponse>(failure);
            }

            return Task.FromResult(_pages.TryGetValue(userId, out var queue) && queue.Count > 0 ? queue.Dequeue() : Page());
        });
        _ddb.PutItemAsync(Arg.Any<PutItemRequest>(), Arg.Any<CancellationToken>()).Returns(ci =>
        {
            var request = ci.Arg<PutItemRequest>();
            _puts.Add(request);
            var failure = _writeFailures.GetValueOrDefault(request.Item["userId"].S) ?? _putFailure;
            return failure != null ? Task.FromException<PutItemResponse>(failure) : Task.FromResult(new PutItemResponse());
        });
    }

    private CleanDeliveryCreditService Service(int maxPages = 2) => new(_ddb, Table) { MaxPages = maxPages };

    private static string Iso(DateTime at) => at.ToString("o", CultureInfo.InvariantCulture);

    private static QueryResponse Page(Dictionary<string, AttributeValue>? lastKey = null, params Dictionary<string, AttributeValue>[] rows) =>
        new() { Items = rows.ToList(), LastEvaluatedKey = lastKey ?? new Dictionary<string, AttributeValue>() };

    private static Dictionary<string, AttributeValue> KeyOnly(string signalKey) => new()
    {
        ["signalKey"] = new() { S = signalKey }
    };

    private static CleanDeliveryTrade Trade(
        string escrowId = EscrowId, string? releasedAt = null, params string?[] parties) =>
        new(escrowId, OrderId, DeliveryId,
            parties.Length == 0 ? new[] { BuyerId, FarmerId, TruckerId } : parties,
            releasedAt ?? Iso(ReleasedAt));

    private void PartitionReturns(string userId, params QueryResponse[] pages) =>
        _pages[userId] = new Queue<QueryResponse>(pages);

    private static string CreditKey(string escrowId = EscrowId) => $"{Iso(ReleasedAt)}#CLEAN_DELIVERY#{escrowId}";

    // ── the escrow row (section 20: no dispute, no hold) ──────────

    [Fact]
    public void EscrowNotCleanReason_PlainEscrowedRowIsClean()
    {
        var escrow = new Dictionary<string, object> { { "escrowId", EscrowId }, { "status", "escrowed" }, { "disputedBy", "" } };

        CleanDeliveryCreditService.EscrowNotCleanReason(escrow).Should().BeNull();
    }

    [Theory]
    [InlineData("disputedBy", "buyer-1", "dispute")]
    [InlineData("disputeResolution", "farmer_wins", "dispute")]
    [InlineData("heldAt", "2026-11-01T00:00:00.0000000Z", "hold")]
    [InlineData("holdReason", "risk_critical", "hold")]
    public void EscrowNotCleanReason_DisputeOrHoldFieldRulesTheCreditOut(string field, string value, string expected)
    {
        var escrow = new Dictionary<string, object> { { "escrowId", EscrowId }, { "status", "escrowed" }, { field, value } };

        CleanDeliveryCreditService.EscrowNotCleanReason(escrow).Should().Be(expected);
    }

    // ── the credit rows ────────────────────────────────────────────

    [Fact]
    public async Task Credit_CleanTradeWritesOneCreditRowPerParty()
    {
        var outcome = await Service().CreditAsync(Trade(), Now);

        outcome.NotCleanReason.Should().BeNull();
        outcome.Credited.Should().Equal(BuyerId, FarmerId, TruckerId);
        outcome.AlreadyCredited.Should().Be(0);
        outcome.Failed.Should().BeEmpty();
        _puts.Select(p => p.Item["userId"].S).Should().Equal(BuyerId, FarmerId, TruckerId);

        var put = _puts[0];
        put.TableName.Should().Be(Table);
        put.ConditionExpression.Should().Be("attribute_not_exists(signalKey)");
        put.Item["signalKey"].S.Should().Be(CreditKey());
        put.Item["recordType"].S.Should().Be("credit");
        put.Item["signalType"].S.Should().Be("CLEAN_DELIVERY");
        put.Item["weight"].N.Should().Be("-5");
        put.Item["entityType"].S.Should().Be("escrow");
        put.Item["entityId"].S.Should().Be(EscrowId);
        put.Item["createdAt"].S.Should().Be(Iso(Now));
        put.Item["expiresAt"].N.Should().Be(new DateTimeOffset(ReleasedAt).AddDays(30).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
        // Not a signal: no status, so Fraud Review, the dashboard count and the review action never see it.
        put.Item.Should().NotContainKey("status");
        put.Item.Should().NotContainKey("signalId");
    }

    [Fact]
    public async Task Credit_ReadsEachPartyOnceForSignalsOnTheOrderDeliveryOrEscrow()
    {
        await Service().CreditAsync(Trade(), Now);

        _queries.Select(q => q.ExpressionAttributeValues[":uid"].S).Should().Equal(BuyerId, FarmerId, TruckerId);
        var query = _queries[0];
        query.TableName.Should().Be(Table);
        query.KeyConditionExpression.Should().Be("#u = :uid AND #k >= :since");
        query.ExpressionAttributeValues[":since"].S.Should().Be(Iso(Now.AddDays(-30)));
        query.FilterExpression.Should().StartWith("#e IN (:e0, :e1, :e2)");
        new[] { ":e0", ":e1", ":e2" }.Select(k => query.ExpressionAttributeValues[k].S)
            .Should().Equal(EscrowId, OrderId, DeliveryId);
        // Signal rows only, and an admin-cleared signal (approve = false positive, whitelist) does not count.
        query.FilterExpression.Should().Contain("(attribute_not_exists(#rt) OR #rt = :signal)");
        query.FilterExpression.Should().Contain("NOT (#r IN (:approve, :whitelist))");
        query.ExpressionAttributeValues[":approve"].S.Should().Be("approve");
        query.ExpressionAttributeValues[":whitelist"].S.Should().Be("whitelist");
        query.ProjectionExpression.Should().Be("#k");
    }

    [Fact]
    public async Task Credit_RepeatedAndMissingPartiesAreCreditedOnce()
    {
        // A self-pickup has no trucker; a user on two sides of one trade earns one credit.
        var outcome = await Service().CreditAsync(Trade(parties: new[] { BuyerId, BuyerId, null, "" }), Now);

        outcome.Credited.Should().Equal(BuyerId);
        _queries.Should().ContainSingle();
        _puts.Should().ContainSingle();
    }

    [Fact]
    public async Task Credit_TwoEscrowsReleasedInOneHourAreTwoCredits()
    {
        // FUP-62: an hourly key would fold these into one credit. The escrow id keeps them apart.
        await Service().CreditAsync(Trade(escrowId: "esc-a", parties: new[] { FarmerId }), Now);
        await Service().CreditAsync(Trade(escrowId: "esc-b", parties: new[] { FarmerId }), Now);

        _puts.Select(p => p.Item["signalKey"].S).Should().Equal(CreditKey("esc-a"), CreditKey("esc-b"));
    }

    // ── idempotency ────────────────────────────────────────────────

    [Fact]
    public async Task Credit_ReplayOfTheSameEscrowWritesTheSameKeyAndCountsAsAlreadyCredited()
    {
        await Service().CreditAsync(Trade(), Now);
        var firstKeys = _puts.Select(p => (p.Item["userId"].S, p.Item["signalKey"].S)).ToList();
        _puts.Clear();
        _putFailure = new ConditionalCheckFailedException("exists");

        // A replay an hour later (another run, a retried sweep) builds the same keys from the stored release time.
        var replay = await Service().CreditAsync(Trade(), Now.AddHours(1));

        replay.NotCleanReason.Should().BeNull();
        replay.Credited.Should().BeEmpty();
        replay.AlreadyCredited.Should().Be(3);
        replay.Failed.Should().BeEmpty();
        _puts.Select(p => (p.Item["userId"].S, p.Item["signalKey"].S)).Should().Equal(firstKeys);
        _puts.Should().OnlyContain(p => p.ConditionExpression == "attribute_not_exists(signalKey)");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-time")]
    public async Task Credit_WithoutAReleaseTimeWritesNothing(string? releasedAt)
    {
        // No stable key can be built, and a key from "now" would credit again on a replay.
        var trade = new CleanDeliveryTrade(EscrowId, OrderId, DeliveryId, new[] { BuyerId, FarmerId }, releasedAt);

        var outcome = await Service().CreditAsync(trade, Now);

        outcome.NotCleanReason.Should().Be(CleanDeliveryCreditService.NotCleanNoReleaseTime);
        _queries.Should().BeEmpty();
        _puts.Should().BeEmpty();
    }

    // ── not clean ──────────────────────────────────────────────────

    [Fact]
    public async Task Credit_ASignalOnTheTradeInAnyPartitionCreditsNobody()
    {
        // The trucker's partition holds a signal on the delivery (say IMPOSSIBLE_DELIVERY_TIME).
        PartitionReturns(TruckerId, Page(null, KeyOnly($"{Iso(Now.AddDays(-2))}#IMPOSSIBLE_DELIVERY_TIME")));

        var outcome = await Service().CreditAsync(Trade(), Now);

        outcome.NotCleanReason.Should().Be(CleanDeliveryCreditService.NotCleanSignal);
        outcome.Credited.Should().BeEmpty();
        _puts.Should().BeEmpty();
    }

    [Fact]
    public async Task Credit_ASignalOnAnInnerPageStopsTheRead()
    {
        // The filter runs after the read: an empty page with more to read is not the end.
        PartitionReturns(BuyerId,
            Page(KeyOnly($"{Iso(Now.AddDays(-1))}#X")),
            Page(null, KeyOnly($"{Iso(Now.AddDays(-3))}#AMOUNT_ANOMALY")));

        var outcome = await Service().CreditAsync(Trade(), Now);

        outcome.NotCleanReason.Should().Be(CleanDeliveryCreditService.NotCleanSignal);
        _queries.Should().HaveCount(2);
        _queries[1].ExclusiveStartKey["signalKey"].S.Should().Be($"{Iso(Now.AddDays(-1))}#X");
        _puts.Should().BeEmpty();
    }

    [Fact]
    public async Task Credit_APartitionPastThePageCapFailsClosed()
    {
        PartitionReturns(FarmerId, Page(KeyOnly("k1")), Page(KeyOnly("k2")), Page());

        var outcome = await Service(maxPages: 2).CreditAsync(Trade(), Now);

        outcome.NotCleanReason.Should().Be(CleanDeliveryCreditService.NotCleanUnread);
        _queries.Count(q => q.ExpressionAttributeValues[":uid"].S == FarmerId).Should().Be(2);
        _puts.Should().BeEmpty();
    }

    [Fact]
    public async Task Credit_AFailedReadThrowsAndWritesNothing()
    {
        _readFailures[FarmerId] = new ProvisionedThroughputExceededException("throttled");

        var act = () => Service().CreditAsync(Trade(), Now);

        await act.Should().ThrowAsync<ProvisionedThroughputExceededException>();
        _puts.Should().BeEmpty();
    }

    // ── a failed write ─────────────────────────────────────────────

    [Fact]
    public async Task Credit_AFailedWriteIsReportedAndTheOtherPartiesAreStillCredited()
    {
        _writeFailures[FarmerId] = new ProvisionedThroughputExceededException("throttled");

        var outcome = await Service().CreditAsync(Trade(), Now);

        outcome.NotCleanReason.Should().BeNull();
        outcome.Credited.Should().Equal(BuyerId, TruckerId);
        outcome.Failed.Should().Equal((FarmerId, nameof(ProvisionedThroughputExceededException)));
    }

    // ── decay: the score reads the row as written (section 14.2) ───

    [Fact]
    public async Task Decay_TheWrittenCreditLowersThePartysScoreByFive()
    {
        await Service().CreditAsync(Trade(parties: new[] { FarmerId }), Now);
        var credit = _puts.Single().Item;

        var score = await ScoreAsync(credit, Signal("FRAUD_DUPLICATE_LISTING", Now.AddDays(-2)));

        score.Score.Should().Be(15);
        score.Tier.Should().Be(RiskTier.Low);
    }

    [Fact]
    public async Task Decay_CreditsAreCappedAndTheScoreNeverGoesBelowZero()
    {
        // Eight clean deliveries are 40 of credit, capped at 30; against 20 of signal weight the score is
        // 0, never negative.
        var service = Service();
        for (var i = 0; i < 8; i++)
        {
            await service.CreditAsync(Trade(escrowId: $"esc-{i}", parties: new[] { FarmerId }), Now);
        }

        var rows = _puts.Select(p => p.Item).Append(Signal("FRAUD_DUPLICATE_LISTING", Now.AddDays(-2))).ToArray();
        var score = await ScoreAsync(rows);

        score.Score.Should().Be(0);
        score.EnforcementScore.Should().Be(0);
        score.Tier.Should().Be(RiskTier.Low);
    }

    [Fact]
    public async Task Decay_CreditsOffsetAtMostTheCap()
    {
        var service = Service();
        for (var i = 0; i < 8; i++)
        {
            await service.CreditAsync(Trade(escrowId: $"esc-{i}", parties: new[] { FarmerId }), Now);
        }

        // 70 of signal weight minus 40 of credits would be 30 (MEDIUM); the cap of 30 leaves 40.
        var rows = _puts.Select(p => p.Item)
            .Append(Signal("FRAUD_DUPLICATE_LISTING", Now.AddDays(-2), weight: 70))
            .ToArray();
        var score = await ScoreAsync(rows);

        score.Score.Should().Be(40);
        score.Tier.Should().Be(RiskTier.Medium);
    }

    [Fact]
    public async Task Decay_ACreditOlderThanTheWindowNoLongerCounts()
    {
        var oldRelease = Now.AddDays(-31);
        await Service().CreditAsync(Trade(releasedAt: Iso(oldRelease), parties: new[] { FarmerId }), Now);

        var score = await ScoreAsync(_puts.Single().Item, Signal("FRAUD_DUPLICATE_LISTING", Now.AddDays(-2)));

        score.Score.Should().Be(20);
    }

    private static Dictionary<string, AttributeValue> Signal(string type, DateTime at, int? weight = null)
    {
        var row = new Dictionary<string, AttributeValue>
        {
            ["signalKey"] = new() { S = $"{Iso(at)}#{type}" },
            ["signalType"] = new() { S = type }
        };
        if (weight != null)
        {
            row["weight"] = new AttributeValue { N = weight.Value.ToString(CultureInfo.InvariantCulture) };
        }

        return row;
    }

    /// <summary>The score of the rows, newest first as the Query returns them.</summary>
    private static Task<RiskScoreResult> ScoreAsync(params Dictionary<string, AttributeValue>[] rows)
    {
        var ddb = Substitute.For<IAmazonDynamoDB>();
        var ordered = rows.OrderByDescending(r => r["signalKey"].S, StringComparer.Ordinal).ToList();
        ddb.QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(new QueryResponse { Items = ordered, LastEvaluatedKey = new Dictionary<string, AttributeValue>() });
        var service = new FraudDetectionService(ddb, "test-crops", Table, Substitute.For<IRateLimitRepository>());
        return service.GetUserRiskScoreAsync(FarmerId, Now, RiskScoreSettings.Default);
    }
}
