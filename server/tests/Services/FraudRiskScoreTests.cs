using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.RateLimiting;
using WeredaConnect.Server.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Services;

/// <summary>
/// FRD-02: the risk score of fraud-detection-architecture.md section 14 (tests listed in 14.7).
/// The DynamoDB client is mocked; each page holds rows newest first, as the Query returns them.
/// </summary>
public class FraudRiskScoreTests
{
    private const string Table = "test-fraud-signals";
    private const string UserId = "user-risk-1";

    private static readonly DateTime Now = new(2026, 10, 23, 12, 30, 0, DateTimeKind.Utc);

    private readonly IAmazonDynamoDB _ddb = Substitute.For<IAmazonDynamoDB>();
    private readonly List<string> _alarms = new();
    private readonly List<QueryRequest> _queries = new();
    private readonly FraudDetectionService _service;

    public FraudRiskScoreTests()
    {
        _service = new FraudDetectionService(_ddb, "test-crops", Table, Substitute.For<IRateLimitRepository>())
        {
            AlarmLog = _alarms.Add
        };
    }

    // ── row builders ───────────────────────────────────────

    private static string Iso(DateTime at) => at.ToString("o", CultureInfo.InvariantCulture);

    private static Dictionary<string, AttributeValue> Signal(
        string type, DateTime at, int? weight = null, string? resolution = null, string? recordType = null,
        bool withType = true)
    {
        var row = new Dictionary<string, AttributeValue>
        {
            ["signalKey"] = new() { S = $"{Iso(at)}#{type}" }
        };
        if (withType)
        {
            row["signalType"] = new AttributeValue { S = type };
        }

        if (weight != null)
        {
            row["weight"] = new AttributeValue { N = weight.Value.ToString(CultureInfo.InvariantCulture) };
        }

        if (resolution != null)
        {
            row["status"] = new AttributeValue { S = "reviewed" };
            row["resolution"] = new AttributeValue { S = resolution };
        }

        if (recordType != null)
        {
            row["recordType"] = new AttributeValue { S = recordType };
        }

        return row;
    }

    private static Dictionary<string, AttributeValue> Credit(DateTime at, int weight = -5) => new()
    {
        ["signalKey"] = new() { S = $"{Iso(at)}#CLEAN_DELIVERY" },
        ["signalType"] = new() { S = "CLEAN_DELIVERY" },
        ["recordType"] = new() { S = "credit" },
        ["weight"] = new() { N = weight.ToString(CultureInfo.InvariantCulture) }
    };

    private static Dictionary<string, AttributeValue> Whitelist(DateTime? until)
    {
        var row = new Dictionary<string, AttributeValue>
        {
            ["signalKey"] = new() { S = "~whitelist" },
            ["recordType"] = new() { S = "whitelist" }
        };
        if (until != null)
        {
            row["whitelistUntil"] = new AttributeValue { S = Iso(until.Value) };
        }

        return row;
    }

    private static QueryResponse Page(Dictionary<string, AttributeValue>? lastKey, params Dictionary<string, AttributeValue>[] rows) =>
        new() { Items = rows.ToList(), LastEvaluatedKey = lastKey ?? new Dictionary<string, AttributeValue>() };

    private static Dictionary<string, AttributeValue> PageKey(string signalKey) => new()
    {
        ["userId"] = new() { S = UserId },
        ["signalKey"] = new() { S = signalKey }
    };

    private void Pages(params QueryResponse[] pages)
    {
        var queue = new Queue<QueryResponse>(pages);
        _ddb.QueryAsync(Arg.Do<QueryRequest>(_queries.Add), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(queue.Dequeue()));
    }

    private Task<RiskScoreResult> ScoreAsync(RiskScoreSettings? settings = null) =>
        _service.GetUserRiskScoreAsync(UserId, Now, settings ?? RiskScoreSettings.Default);

    private Task<RiskScoreResult> ScoreRowsAsync(params Dictionary<string, AttributeValue>[] rows)
    {
        Pages(Page(null, rows));
        return ScoreAsync();
    }

    // ── tiers (section 16) ─────────────────────────────────

    [Theory]
    [InlineData(0, RiskTier.Low)]
    [InlineData(29, RiskTier.Low)]
    [InlineData(30, RiskTier.Medium)]
    [InlineData(59, RiskTier.Medium)]
    [InlineData(60, RiskTier.High)]
    [InlineData(99, RiskTier.High)]
    [InlineData(100, RiskTier.Critical)]
    public async Task Tier_BoundariesAreInclusiveLowerBounds(int weight, RiskTier expected)
    {
        var result = await ScoreRowsAsync(Signal("FRAUD_DUPLICATE_LISTING", Now.AddHours(-1), weight));

        result.Score.Should().Be(weight);
        result.EnforcementScore.Should().Be(weight);
        result.Tier.Should().Be(expected);
        result.Truncated.Should().BeFalse();
        result.Source.Should().Be(RiskScoreSource.Computed);
    }

    [Theory]
    [InlineData(9, RiskTier.Low)]
    [InlineData(10, RiskTier.Medium)]
    [InlineData(20, RiskTier.High)]
    [InlineData(40, RiskTier.Critical)]
    public void Tier_FollowsConfiguredBounds(int score, RiskTier expected)
    {
        var settings = RiskScoreSettings.Default with { TierMedium = 10, TierHigh = 20, TierCritical = 40 };

        RiskScoreCalculator.TierFor(score, score, truncated: false, settings).Should().Be(expected);
    }

    [Fact]
    public void Tier_TruncatedIsCriticalWhateverTheScore()
    {
        RiskScoreCalculator.TierFor(0, 0, truncated: true, RiskScoreSettings.Default).Should().Be(RiskTier.Critical);
    }

    // ── burst dedup (section 14.2) ─────────────────────────

    [Fact]
    public async Task Dedup_OfflineBurstOfOneTypeInOneHourCountsOnce()
    {
        var rows = Enumerable.Range(0, 7)
            .Select(i => Signal("FRAUD_VELOCITY_EXCEEDED", Now.AddMinutes(-i)))
            .ToArray();

        var result = await ScoreRowsAsync(rows);

        // A plain sum would be 105 (CRITICAL for an honest offline farmer).
        result.Score.Should().Be(15);
        result.Tier.Should().Be(RiskTier.Low);
    }

    [Fact]
    public async Task Dedup_SameTypeInDifferentUtcHoursCountsEach()
    {
        var result = await ScoreRowsAsync(
            Signal("FRAUD_VELOCITY_EXCEEDED", new DateTime(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc)),
            Signal("FRAUD_VELOCITY_EXCEEDED", new DateTime(2026, 10, 23, 11, 59, 59, DateTimeKind.Utc)));

        result.Score.Should().Be(30);
        result.Tier.Should().Be(RiskTier.Medium);
    }

    [Fact]
    public async Task Dedup_DifferentTypesInOneHourCountEach()
    {
        var result = await ScoreRowsAsync(
            Signal("FRAUD_DUPLICATE_LISTING", Now.AddMinutes(-1)),
            Signal("FRAUD_VELOCITY_EXCEEDED", Now.AddMinutes(-2)));

        result.Score.Should().Be(35);
    }

    [Fact]
    public async Task Dedup_NewestRowReadTakesTheSlot()
    {
        var result = await ScoreRowsAsync(
            Signal("FRAUD_DUPLICATE_LISTING", Now.AddMinutes(-1), weight: 15),
            Signal("FRAUD_DUPLICATE_LISTING", Now.AddMinutes(-2), weight: 40));

        result.Score.Should().Be(15);
    }

    // ── whitelist (sections 14.3, 15, 20) ──────────────────

    [Fact]
    public async Task Whitelist_ReadFirstSkipsSoftWeightsAndKeepsHardAndAdvisory()
    {
        var result = await ScoreRowsAsync(
            Whitelist(Now.AddDays(1)),
            Signal("GPS_ANOMALY", Now.AddMinutes(-1)),               // soft, 25: skipped
            Signal("PRICE_ANOMALY", Now.AddMinutes(-2)),             // soft, 10: skipped
            Signal("FRAUD_DUPLICATE_LISTING", Now.AddMinutes(-3)),   // hard, 20
            Signal("CONTACT_THEN_TRANSPORT_CANCEL", Now.AddMinutes(-4)), // hard (default), 10
            Signal(FraudDetectionService.AdvisorySignalType, Now.AddMinutes(-5), weight: 10)); // advisory

        result.Score.Should().Be(40);
        result.EnforcementScore.Should().Be(30);
        result.Tier.Should().Be(RiskTier.Medium);
    }

    [Fact]
    public async Task Whitelist_ExpiredRowIsIgnored()
    {
        var result = await ScoreRowsAsync(
            Whitelist(Now.AddSeconds(-1)),
            Signal("GPS_ANOMALY", Now.AddMinutes(-1)),
            Signal("PRICE_ANOMALY", Now.AddMinutes(-2)),
            Signal("FRAUD_DUPLICATE_LISTING", Now.AddMinutes(-3)));

        result.Score.Should().Be(55);
    }

    [Fact]
    public async Task Whitelist_EndsAtWhitelistUntil()
    {
        // Active while whitelistUntil is after now; at the instant itself it no longer applies.
        var result = await ScoreRowsAsync(
            Whitelist(Now),
            Signal("GPS_ANOMALY", Now.AddMinutes(-1)));

        result.Score.Should().Be(25);
    }

    [Fact]
    public async Task Whitelist_ActiveOneTickBeforeItsEnd()
    {
        var result = await ScoreRowsAsync(
            Whitelist(Now.AddTicks(1)),
            Signal("GPS_ANOMALY", Now.AddMinutes(-1)));

        result.Score.Should().Be(0);
    }

    [Fact]
    public async Task Whitelist_RowWithoutAnEndIsIgnored()
    {
        var result = await ScoreRowsAsync(
            Whitelist(until: null),
            Signal("GPS_ANOMALY", Now.AddMinutes(-1)));

        result.Score.Should().Be(25);
    }

    [Fact]
    public async Task Whitelist_SoftTypeReadFromTheKeyWhenTheTypeAttributeIsMissing()
    {
        var result = await ScoreRowsAsync(
            Whitelist(Now.AddDays(1)),
            Signal("GPS_ANOMALY", Now.AddMinutes(-1), withType: false));

        result.Score.Should().Be(0);
    }

    // ── window (sections 14.2, 14.3) ───────────────────────

    [Fact]
    public async Task Window_RowsBeforeTheWindowAreExcludedAndTheBoundaryInstantCounts()
    {
        var since = Now.AddDays(-30);

        var result = await ScoreRowsAsync(
            Signal("FRAUD_DUPLICATE_LISTING", since),                        // exactly at the bound: counts
            Signal("FRAUD_VELOCITY_EXCEEDED", since.AddTicks(-1)),           // one tick before: excluded
            Credit(since.AddDays(-1)));                                      // credit before: excluded

        result.Score.Should().Be(20);
    }

    [Fact]
    public async Task Window_QueryIsOneBoundedNewestFirstReadOfTheUsersPartition()
    {
        Pages(Page(null));

        await ScoreAsync();

        _queries.Should().ContainSingle();
        var request = _queries[0];
        request.TableName.Should().Be(Table);
        request.IndexName.Should().BeNullOrEmpty();
        request.KeyConditionExpression.Should().Be("#u = :uid AND #k >= :since");
        request.ExpressionAttributeNames["#u"].Should().Be("userId");
        request.ExpressionAttributeNames["#k"].Should().Be("signalKey");
        request.ExpressionAttributeValues[":uid"].S.Should().Be(UserId);
        request.ExpressionAttributeValues[":since"].S.Should().Be(Iso(Now.AddDays(-30)));
        request.ScanIndexForward.Should().BeFalse();
        request.ConsistentRead.Should().BeFalse();
        request.ExclusiveStartKey.Should().BeNullOrEmpty();
        // FRD-08 adds entityId: its reject types count once per refused entity.
        request.ProjectionExpression.Should().Be("#k, #t, #w, #r, #rt, #wu, #e");
        request.ExpressionAttributeNames.Values.Should().BeEquivalentTo(
            "userId", "signalKey", "signalType", "weight", "resolution", "recordType", "whitelistUntil", "entityId");
    }

    [Fact]
    public async Task Window_ConfiguredWindowMovesTheBound()
    {
        Pages(Page(null));

        await ScoreAsync(RiskScoreSettings.Default with { WindowDays = 7 });

        _queries.Single().ExpressionAttributeValues[":since"].S.Should().Be(Iso(Now.AddDays(-7)));
    }

    // ── credits (sections 14.2, 20) ────────────────────────

    [Fact]
    public async Task Credits_LowerTheScore()
    {
        var result = await ScoreRowsAsync(
            Credit(Now.AddHours(-1)),
            Credit(Now.AddHours(-2)),
            Credit(Now.AddHours(-3)),
            Signal("GPS_ANOMALY", Now.AddHours(-4)),
            Signal("FRAUD_DUPLICATE_LISTING", Now.AddHours(-4)),
            Signal("FRAUD_VELOCITY_EXCEEDED", Now.AddHours(-4)));

        result.Score.Should().Be(45);
        result.Tier.Should().Be(RiskTier.Medium);
    }

    [Fact]
    public async Task Credits_AreCappedAtTheCreditCap()
    {
        var rows = Enumerable.Range(1, 10).Select(i => Credit(Now.AddHours(-i)))
            .Append(Signal("GPS_ANOMALY", Now.AddHours(-11)))
            .Append(Signal("FRAUD_DUPLICATE_LISTING", Now.AddHours(-11)))
            .Append(Signal("FRAUD_VELOCITY_EXCEEDED", Now.AddHours(-11)))
            .ToArray();

        var result = await ScoreRowsAsync(rows);

        // 60 minus 50 of credits would be 10; the cap of 30 leaves 30.
        result.Score.Should().Be(30);
        result.Tier.Should().Be(RiskTier.Medium);
    }

    [Fact]
    public async Task Credits_NeverMakeTheScoreNegative()
    {
        var result = await ScoreRowsAsync(
            Credit(Now.AddHours(-1)),
            Credit(Now.AddHours(-2)),
            Credit(Now.AddHours(-3)),
            Signal("PRICE_ANOMALY", Now.AddHours(-4)));

        result.Score.Should().Be(0);
        result.EnforcementScore.Should().Be(0);
        result.Tier.Should().Be(RiskTier.Low);
    }

    // ── resolutions (sections 14.2, 19) ────────────────────

    [Theory]
    [InlineData(null, 20)]
    [InlineData("reject", 20)]
    [InlineData("escalate", 20)]
    [InlineData("Approve", 20)]
    [InlineData("approve", 0)]
    [InlineData("whitelist", 0)]
    public async Task Resolution_OnlyApproveAndWhitelistStopASignalCounting(string? resolution, int expected)
    {
        var row = Signal("FRAUD_DUPLICATE_LISTING", Now.AddHours(-1), resolution: resolution);
        if (resolution == null)
        {
            // Reviewed with no resolution recorded still counts: the status is not read.
            row["status"] = new AttributeValue { S = "reviewed" };
        }

        var result = await ScoreRowsAsync(row);

        result.Score.Should().Be(expected);
    }

    [Fact]
    public async Task Resolution_AnApprovedRowDoesNotTakeTheDedupSlot()
    {
        var result = await ScoreRowsAsync(
            Signal("FRAUD_DUPLICATE_LISTING", Now.AddMinutes(-1), resolution: "approve"),
            Signal("FRAUD_DUPLICATE_LISTING", Now.AddMinutes(-2)));

        result.Score.Should().Be(20);
    }

    // ── advisory (sections 14.2, 21) ───────────────────────

    [Fact]
    public async Task Advisory_FullScoreOf100WithEnforcementBelow100IsHigh()
    {
        var result = await ScoreRowsAsync(
            Signal(FraudDetectionService.AdvisorySignalType, Now.AddMinutes(-1), weight: 10),
            Signal("FRAUD_DUPLICATE_LISTING", Now.AddMinutes(-2), weight: 95));

        result.Score.Should().Be(105);
        result.EnforcementScore.Should().Be(95);
        result.Tier.Should().Be(RiskTier.High);
    }

    [Fact]
    public async Task Advisory_RowsInOneHourAreDeduplicated()
    {
        var result = await ScoreRowsAsync(
            Signal(FraudDetectionService.AdvisorySignalType, Now.AddMinutes(-1), weight: 4),
            Signal(FraudDetectionService.AdvisorySignalType, Now.AddMinutes(-2), weight: 4));

        result.Score.Should().Be(4);
        result.EnforcementScore.Should().Be(0);
    }

    [Fact]
    public async Task Advisory_TotalIsCappedAtTheAdvisoryCap()
    {
        var result = await ScoreRowsAsync(
            Signal(FraudDetectionService.AdvisorySignalType, Now.AddHours(-1), weight: 10),
            Signal(FraudDetectionService.AdvisorySignalType, Now.AddHours(-2), weight: 10),
            Signal(FraudDetectionService.AdvisorySignalType, Now.AddHours(-3), weight: 10),
            Signal("FRAUD_DUPLICATE_LISTING", Now.AddHours(-4), weight: 50));

        result.Score.Should().Be(60);
        result.EnforcementScore.Should().Be(50);
        result.Tier.Should().Be(RiskTier.High);
    }

    // ── bounded read (section 14.3) ────────────────────────

    [Fact]
    public async Task Read_StopsEarlyOnceNoUnreadRowCanLowerTheTier()
    {
        Pages(
            Page(PageKey("k1"),
                Signal("FRAUD_DUPLICATE_LISTING", Now.AddMinutes(-1), weight: 80),
                Signal("FRAUD_VELOCITY_EXCEEDED", Now.AddMinutes(-2), weight: 50), // 130 = CRITICAL + credit cap
                Credit(Now.AddHours(-1))), // after the stop: read, it would make the score 125
            Page(null, Signal("GPS_ANOMALY", Now.AddHours(-2))));

        var result = await ScoreAsync();

        _queries.Should().ContainSingle();
        result.Tier.Should().Be(RiskTier.Critical);
        result.Score.Should().Be(130);
        result.Truncated.Should().BeFalse();
        _alarms.Should().BeEmpty();
    }

    [Fact]
    public async Task Read_EarlyExitIgnoresAdvisoryWeight()
    {
        // 125 of signals plus 10 of advisory is 135, but only the 125 counts toward the stop at
        // 130, so the credits on page 2 are read and the user ends HIGH, not CRITICAL.
        Pages(
            Page(PageKey("k1"),
                Signal(FraudDetectionService.AdvisorySignalType, Now.AddMinutes(-1), weight: 10),
                Signal("FRAUD_DUPLICATE_LISTING", Now.AddMinutes(-2), weight: 125)),
            Page(null, Enumerable.Range(1, 6).Select(i => Credit(Now.AddHours(-i))).ToArray()));

        var result = await ScoreAsync();

        _queries.Should().HaveCount(2);
        result.Score.Should().Be(105);
        result.EnforcementScore.Should().Be(95);
        result.Tier.Should().Be(RiskTier.High);
        result.Truncated.Should().BeFalse();
    }

    [Fact]
    public async Task Read_BelowTheEarlyExitBoundKeepsReadingNextPage()
    {
        Pages(
            Page(PageKey("k1"), Signal("FRAUD_DUPLICATE_LISTING", Now.AddMinutes(-1), weight: 129)),
            Page(null, Credit(Now.AddHours(-1))));

        var result = await ScoreAsync();

        _queries.Should().HaveCount(2);
        result.Score.Should().Be(124);
        result.Tier.Should().Be(RiskTier.Critical);
        result.Truncated.Should().BeFalse();
    }

    [Fact]
    public async Task Read_FollowsLastEvaluatedKeyAndSumsBothPages()
    {
        var firstPageEnd = PageKey($"{Iso(Now.AddHours(-2))}#FRAUD_DUPLICATE_LISTING");
        Pages(
            Page(firstPageEnd, Signal("FRAUD_DUPLICATE_LISTING", Now.AddHours(-2))),
            Page(null, Signal("FRAUD_VELOCITY_EXCEEDED", Now.AddHours(-3))));

        var result = await ScoreAsync();

        _queries.Should().HaveCount(2);
        _queries[0].ExclusiveStartKey.Should().BeNullOrEmpty();
        _queries[1].ExclusiveStartKey.Should().BeSameAs(firstPageEnd);
        _queries[1].ExpressionAttributeValues[":since"].S.Should().Be(_queries[0].ExpressionAttributeValues[":since"].S);
        result.Score.Should().Be(35);
        result.Tier.Should().Be(RiskTier.Medium);
        result.Truncated.Should().BeFalse();
        _alarms.Should().BeEmpty();
    }

    [Fact]
    public async Task Read_RowsLeftAfterThePageCapAreTruncatedToCritical()
    {
        Pages(
            Page(PageKey("k1"), Signal("FRAUD_VELOCITY_EXCEEDED", Now.AddMinutes(-1))),
            Page(PageKey("k2"), Signal("FRAUD_VELOCITY_EXCEEDED", Now.AddMinutes(-2))),
            Page(null, Signal("FRAUD_DUPLICATE_LISTING", Now.AddHours(-5))));

        var result = await ScoreAsync();

        _queries.Should().HaveCount(2); // RISK_MAX_PAGES default
        result.Truncated.Should().BeTrue();
        result.Tier.Should().Be(RiskTier.Critical);
        result.Score.Should().Be(15);
        result.Source.Should().Be(RiskScoreSource.Computed);
        _alarms.Should().ContainSingle()
            .Which.Should().Be($"{FraudDetectionService.RiskScoreTruncatedToken} userId=user-risk-1 pages=2");
    }

    [Fact]
    public async Task Read_ConfiguredPageCapIsHonoured()
    {
        Pages(
            Page(PageKey("k1"), Signal("FRAUD_VELOCITY_EXCEEDED", Now.AddHours(-1))),
            Page(PageKey("k2"), Signal("FRAUD_VELOCITY_EXCEEDED", Now.AddHours(-2))),
            Page(null, Signal("FRAUD_VELOCITY_EXCEEDED", Now.AddHours(-3))));

        var result = await ScoreAsync(RiskScoreSettings.Default with { MaxPages = 3 });

        _queries.Should().HaveCount(3);
        result.Truncated.Should().BeFalse();
        result.Score.Should().Be(45);
    }

    // ── failure modes (section 14.4) ───────────────────────

    [Fact]
    public async Task Failure_TableNotConfiguredIsLowWithoutARead()
    {
        var service = new FraudDetectionService(_ddb, "test-crops", "", Substitute.For<IRateLimitRepository>())
        {
            AlarmLog = _alarms.Add
        };

        var result = await service.GetUserRiskScoreAsync(UserId);

        result.Should().Be(RiskScoreResult.Unscored(RiskScoreSource.TableNotConfigured));
        await _ddb.DidNotReceiveWithAnyArgs().QueryAsync(default(QueryRequest)!, default);
        _alarms.Should().BeEmpty();
    }

    [Fact]
    public async Task Failure_QueryErrorIsLowWithTheAlarmTokenAndDoesNotThrow()
    {
        _ddb.QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ProvisionedThroughputExceededException("slow down"));

        var result = await ScoreAsync();

        result.Tier.Should().Be(RiskTier.Low);
        result.Score.Should().Be(0);
        result.Source.Should().Be(RiskScoreSource.Unavailable);
        _alarms.Should().ContainSingle().Which.Should().Be(
            $"{FraudDetectionService.RiskScoreUnavailableToken} userId=user-risk-1 error=ProvisionedThroughputExceededException");
    }

    [Fact]
    public async Task Failure_ErrorOnALaterPageDiscardsThePartialScore()
    {
        var calls = 0;
        _ddb.QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ => ++calls == 1
                ? Task.FromResult(Page(PageKey("k1"), Signal("FRAUD_DUPLICATE_LISTING", Now.AddMinutes(-1), weight: 120)))
                : throw new AmazonDynamoDBException("boom"));

        var result = await ScoreAsync();

        result.Source.Should().Be(RiskScoreSource.Unavailable);
        result.Tier.Should().Be(RiskTier.Low);
        _alarms.Should().ContainSingle().Which.Should().StartWith(FraudDetectionService.RiskScoreUnavailableToken + " ");
    }

    [Fact]
    public async Task Failure_EmptyUserIdIsUnavailableWithoutARead()
    {
        var result = await _service.GetUserRiskScoreAsync("", Now, RiskScoreSettings.Default);

        result.Source.Should().Be(RiskScoreSource.Unavailable);
        result.Tier.Should().Be(RiskTier.Low);
        await _ddb.DidNotReceiveWithAnyArgs().QueryAsync(default(QueryRequest)!, default);
        _alarms.Should().ContainSingle().Which.Should().StartWith(FraudDetectionService.RiskScoreUnavailableToken + " ");
    }

    [Fact]
    public async Task Failure_UserIdInTheAlarmLineIsDefanged()
    {
        _ddb.QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new AmazonDynamoDBException("boom"));

        await _service.GetUserRiskScoreAsync("user_RISK_SCORE_TRUNCATED", Now, RiskScoreSettings.Default);

        _alarms.Single().Should().NotContain(FraudDetectionService.RiskScoreTruncatedToken);
    }

    // ── row kinds (sections 14.1, 20) ──────────────────────

    [Fact]
    public async Task Rows_LegacyRowWithoutWeightFallsBackToTheCatalogueWeight()
    {
        var result = await ScoreRowsAsync(Signal("GPS_ANOMALY", Now.AddMinutes(-1)));

        result.Score.Should().Be(25);
    }

    [Fact]
    public async Task Rows_ExplicitSignalRecordTypeCounts()
    {
        var result = await ScoreRowsAsync(Signal("FRAUD_DUPLICATE_LISTING", Now.AddMinutes(-1), recordType: "signal"));

        result.Score.Should().Be(20);
    }

    [Fact]
    public async Task Rows_UnknownRecordTypeCountsAsASignalAndIsReported()
    {
        var result = await ScoreRowsAsync(
            Signal("FRAUD_DUPLICATE_LISTING", Now.AddMinutes(-1), recordType: "note"),
            Signal("FRAUD_VELOCITY_EXCEEDED", Now.AddMinutes(-2), recordType: "note_v2"),
            Signal("PRICE_ANOMALY", Now.AddMinutes(-3), recordType: "signal"));

        result.Score.Should().Be(45);
        result.Source.Should().Be(RiskScoreSource.Computed);
        _alarms.Should().ContainSingle().Which.Should().Be(
            $"{FraudDetectionService.RiskScoreUnknownRecordToken} userId=user-risk-1 rows=2 recordType=note");
    }

    [Fact]
    public async Task Rows_MistypedCreditCountsAsASignalAtWeightZeroAndIsReported()
    {
        var credit = Credit(Now.AddMinutes(-1));
        credit["recordType"] = new AttributeValue { S = "Credit_X" };

        var result = await ScoreRowsAsync(credit, Signal("FRAUD_DUPLICATE_LISTING", Now.AddMinutes(-2)));

        // Read as a credit it would make the score 15; as a signal its -5 counts as 0.
        result.Score.Should().Be(20);
        _alarms.Should().ContainSingle().Which.Should().Be(
            $"{FraudDetectionService.RiskScoreUnknownRecordToken} userId=user-risk-1 rows=1 recordType=Credit-X");
    }

    [Fact]
    public async Task Rows_UnknownRecordTypeIsReportedOnEarlyExitAndTruncation()
    {
        Pages(Page(PageKey("k1"), Signal("FRAUD_DUPLICATE_LISTING", Now.AddMinutes(-1), weight: 130, recordType: "note")));
        var early = await ScoreAsync();

        early.Tier.Should().Be(RiskTier.Critical);
        _alarms.Should().ContainSingle().Which.Should().StartWith(FraudDetectionService.RiskScoreUnknownRecordToken + " ");

        _alarms.Clear();
        _queries.Clear();
        Pages(
            Page(PageKey("k1"), Signal("FRAUD_DUPLICATE_LISTING", Now.AddMinutes(-1), recordType: "note")),
            Page(PageKey("k2")));
        var truncated = await ScoreAsync();

        truncated.Truncated.Should().BeTrue();
        _alarms.Should().HaveCount(2);
        _alarms.Should().Contain(line => line.StartsWith(FraudDetectionService.RiskScoreTruncatedToken + " "));
        _alarms.Should().Contain(line => line.StartsWith(FraudDetectionService.RiskScoreUnknownRecordToken + " "));
    }

    [Fact]
    public async Task Rows_NegativeWeightOnASignalRowCountsAsZero()
    {
        var result = await ScoreRowsAsync(
            Signal("FRAUD_DUPLICATE_LISTING", Now.AddMinutes(-1), weight: -50),
            Signal("FRAUD_VELOCITY_EXCEEDED", Now.AddMinutes(-2)));

        result.Score.Should().Be(15);
    }

    [Fact]
    public async Task Rows_PublicOverloadScoresRowsWrittenNow()
    {
        Pages(Page(null, Signal("FRAUD_DUPLICATE_LISTING", DateTime.UtcNow.AddMinutes(-1))));

        var result = await _service.GetUserRiskScoreAsync(UserId);

        result.Score.Should().Be(20);
        _queries.Single().TableName.Should().Be(Table);
    }

    // ── memo: one Query per user per batch (section 14.3) ─

    [Fact]
    public async Task Memo_ReadsEachUserOncePerBatch()
    {
        _ddb.QueryAsync(Arg.Do<QueryRequest>(_queries.Add), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(Page(null, Signal("FRAUD_DUPLICATE_LISTING", DateTime.UtcNow.AddMinutes(-1)))));
        var memo = new RiskScoreMemo(_service);

        var first = await memo.GetAsync("buyer-1");
        var again = await memo.GetAsync("buyer-1");
        await memo.GetAsync("farmer-1");
        await memo.GetAsync("buyer-1");

        _queries.Select(q => q.ExpressionAttributeValues[":uid"].S)
            .Should().Equal("buyer-1", "farmer-1");
        again.Should().BeSameAs(first);
    }

    [Fact]
    public async Task Memo_RemembersAFailedReadForTheBatch()
    {
        _ddb.QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new AmazonDynamoDBException("boom"));
        var memo = new RiskScoreMemo(_service);

        (await memo.GetAsync("trucker-1")).Source.Should().Be(RiskScoreSource.Unavailable);
        (await memo.GetAsync("trucker-1")).Source.Should().Be(RiskScoreSource.Unavailable);

        await _ddb.ReceivedWithAnyArgs(1).QueryAsync(default(QueryRequest)!, default);
    }

    [Fact]
    public async Task Memo_EmptyOrNullUserIdGoesToTheServiceUncachedAndDoesNotThrow()
    {
        var memo = new RiskScoreMemo(_service);

        var results = new[] { await memo.GetAsync(null), await memo.GetAsync(""), await memo.GetAsync("") };

        results.Should().OnlyContain(r => r.Source == RiskScoreSource.Unavailable && r.Tier == RiskTier.Low);
        await _ddb.DidNotReceiveWithAnyArgs().QueryAsync(default(QueryRequest)!, default);
        _alarms.Should().HaveCount(3).And.OnlyContain(line => line.StartsWith(FraudDetectionService.RiskScoreUnavailableToken + " "));
    }

    [Fact]
    public async Task Memo_EmptyUserIdIsPassedAsEmpty()
    {
        var fraud = Substitute.For<IFraudDetectionService>();
        fraud.GetUserRiskScoreAsync(Arg.Any<string>())
            .Returns(Task.FromResult(RiskScoreResult.Unscored(RiskScoreSource.Unavailable)));
        var memo = new RiskScoreMemo(fraud);

        await memo.GetAsync(null);
        await memo.GetAsync(null);

        await fraud.Received(2).GetUserRiskScoreAsync(string.Empty);
    }

    [Fact]
    public async Task Memo_NewMemoReadsAgain()
    {
        _ddb.QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(Page(null)));

        await new RiskScoreMemo(_service).GetAsync("buyer-1");
        await new RiskScoreMemo(_service).GetAsync("buyer-1");

        await _ddb.ReceivedWithAnyArgs(2).QueryAsync(default(QueryRequest)!, default);
    }

    // ── configuration (section 14.5) ───────────────────────

    private static RiskScoreSettings SettingsFrom(params (string Name, string Value)[] values)
    {
        var map = values.ToDictionary(v => v.Name, v => v.Value);
        return RiskScoreSettings.From(name => map.TryGetValue(name, out var v) ? v : null);
    }

    [Fact]
    public void Settings_DefaultsMatchTheSpec()
    {
        SettingsFrom().Should().Be(new RiskScoreSettings(
            WindowDays: 30, TierMedium: 30, TierHigh: 60, TierCritical: 100, CreditCap: 30, AdvisoryCap: 10, MaxPages: 2,
            RejectTypeCap: 50));
    }

    [Fact]
    public void Settings_EnvironmentOverridesEachValue()
    {
        SettingsFrom(
                ("RISK_WINDOW_DAYS", "14"), ("RISK_TIER_MEDIUM", "20"), ("RISK_TIER_HIGH", "50"),
                ("RISK_TIER_CRITICAL", "80"), ("RISK_CREDIT_CAP", "0"), ("RISK_ADVISORY_CAP", "5"),
                ("RISK_MAX_PAGES", "4"), ("RISK_REJECT_TYPE_CAP", "80"))
            .Should().Be(new RiskScoreSettings(14, 20, 50, 80, 0, 5, 4, 80));
    }

    [Theory]
    [InlineData("RISK_WINDOW_DAYS", "90")] // capped at the signal TTL
    [InlineData("RISK_WINDOW_DAYS", "0")]
    [InlineData("RISK_WINDOW_DAYS", "abc")]
    [InlineData("RISK_CREDIT_CAP", "-5")]
    [InlineData("RISK_ADVISORY_CAP", "")]
    [InlineData("RISK_MAX_PAGES", "0")]
    [InlineData("RISK_MAX_PAGES", "-3")]
    [InlineData("RISK_TIER_CRITICAL", "1.5")]
    [InlineData("RISK_REJECT_TYPE_CAP", "-1")] // FRD-08
    [InlineData("RISK_REJECT_TYPE_CAP", "abc")]
    [InlineData("RISK_REJECT_TYPE_CAP", "2.5")]
    public void Settings_InvalidOrOutOfRangeValueKeepsTheDefault(string name, string value)
    {
        SettingsFrom((name, value)).Should().Be(RiskScoreSettings.Default);
    }

    [Theory]
    [InlineData("10", 10)]
    [InlineData("11", 10)]
    [InlineData("1000000", 10)]
    [InlineData("1", 1)]
    public void Settings_PageCountIsCappedAtTen(string value, int expected)
    {
        SettingsFrom(("RISK_MAX_PAGES", value)).MaxPages.Should().Be(expected);
    }

    [Theory]
    [InlineData("0", 0)] // FRD-08: 0 turns the capped reject types off
    [InlineData("50", 50)]
    [InlineData("1000", 1000)]
    public void Settings_RejectTypeCapAcceptsAnyNonNegativeInteger(string value, int expected)
    {
        SettingsFrom(("RISK_REJECT_TYPE_CAP", value)).RejectTypeCap.Should().Be(expected);
    }

    [Fact]
    public void Settings_TierBoundsOutOfOrderAllFallBack()
    {
        var settings = SettingsFrom(("RISK_TIER_MEDIUM", "70"), ("RISK_TIER_CRITICAL", "200"));

        (settings.TierMedium, settings.TierHigh, settings.TierCritical).Should().Be((30, 60, 100));
    }
}
