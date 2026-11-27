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
/// FRD-15 (fraud-detection-architecture.md 14.2, 15, 15.2): the three relationship signal types in the risk score
/// (weight, class, once per order, the per-type cap of the two soft ones), and the existence check the hourly
/// scan runs before every write.
/// </summary>
public class FraudRelationshipSignalTests
{
    public static TheoryData<string, int, bool, bool, string> Catalogue => new()
    {
        { "SELF_TRADE", 40, false, false, "high" },
        { "SHARED_DEVICE_TRADE", 25, true, true, "high" },
        { "REPEATED_PAIR", 5, true, true, "low" }
    };

    [Theory]
    [MemberData(nameof(Catalogue))]
    public async Task RelationshipSignals_CarryTheCatalogueWeightClassAndRowSchema(
        string type, int weight, bool soft, bool capped, string severity)
    {
        var ddb = Substitute.For<IAmazonDynamoDB>();
        PutItemRequest? captured = null;
        ddb.PutItemAsync(Arg.Do<PutItemRequest>(r => captured = r), Arg.Any<CancellationToken>()).Returns(new PutItemResponse());
        var service = new FraudDetectionService(ddb, "crops", "fraud-signals", Substitute.For<IRateLimitRepository>());

        await service.RecordFraudSignalAsync("user-1", type, "order-1", "order", "why");

        FraudDetectionService.SignalWeight(type).Should().Be(weight);
        FraudDetectionService.IsSoftSignal(type).Should().Be(soft);
        FraudDetectionService.IsRelationshipSignal(type).Should().BeTrue();
        FraudDetectionService.IsPatternSignal(type).Should().BeFalse();
        FraudDetectionService.CountsOncePerEntity(type).Should().BeTrue();
        FraudDetectionService.IsCappedRelationshipType(type).Should().Be(capped);
        RiskScoreSettings.Default.CapOf(type).Should().Be(capped ? 50 : null);
        captured!.Item["signalKey"].S.Should().EndWith($"#{type}");
        captured.Item["severity"].S.Should().Be(severity);
        captured.Item["entityType"].S.Should().Be("order");
        captured.Item["entityId"].S.Should().Be("order-1");
        captured.Item["status"].S.Should().Be("pending");
    }

    private static RiskScoreResult ScoreOf(params (int HoursAgo, string Type, string EntityId)[] rows) =>
        ScoreOf(RiskScoreSettings.Default, rows);

    private static RiskScoreResult ScoreOf(RiskScoreSettings settings, params (int HoursAgo, string Type, string EntityId)[] rows)
    {
        var now = DateTime.UtcNow;
        var calculator = new RiskScoreCalculator(settings, now);
        foreach (var (hoursAgo, type, entityId) in rows.OrderBy(r => r.HoursAgo))
        {
            var hour = now.AddHours(-hoursAgo).ToString("yyyy-MM-dd'T'HH':00:00.0000000Z'", CultureInfo.InvariantCulture);
            calculator.Add(new Dictionary<string, AttributeValue>
            {
                ["signalKey"] = new() { S = $"{hour}#{type}" },
                ["signalType"] = new() { S = type },
                ["entityId"] = new() { S = entityId },
                ["weight"] = new() { N = FraudDetectionService.SignalWeight(type).ToString(CultureInfo.InvariantCulture) }
            });
        }

        return calculator.Result(truncated: false);
    }

    [Fact]
    public void OneOrderSeenInManyHours_CountsOnce()
    {
        ScoreOf((1, "SHARED_DEVICE_TRADE", "ord-1"), (2, "SHARED_DEVICE_TRADE", "ord-1"), (30, "SHARED_DEVICE_TRADE", "ord-1"))
            .Score.Should().Be(25);
    }

    [Fact]
    public void ASharedDeviceSelfTrader_IsLowAfterOneTrade_MediumAfterTwo_AndTheCapHoldsIt()
    {
        ScoreOf((1, "SHARED_DEVICE_TRADE", "ord-1")).Tier.Should().Be(RiskTier.Low);
        ScoreOf((1, "SHARED_DEVICE_TRADE", "ord-1"), (5, "SHARED_DEVICE_TRADE", "ord-2")).Tier.Should().Be(RiskTier.Medium);
        var many = Enumerable.Range(1, 6).Select(h => (h * 3, "SHARED_DEVICE_TRADE", $"ord-{h}")).ToArray();
        ScoreOf(many).Score.Should().Be(50);
    }

    [Fact]
    public void SharedDeviceAndRepeatedPair_ShareOneCap_SoTogetherTheyStopAtMedium()
    {
        // A family phone that trades often: six shared-device orders and six repeated-pair orders are 180
        // uncapped, 50 with the shared cap, never CRITICAL.
        var shared = Enumerable.Range(1, 6).Select(h => (h * 3, "SHARED_DEVICE_TRADE", $"ord-{h}"));
        var pairs = Enumerable.Range(1, 6).Select(h => (h * 3 + 1, "REPEATED_PAIR", $"ord-{h}"));

        var result = ScoreOf(shared.Concat(pairs).ToArray());

        result.Score.Should().Be(50);
        result.Tier.Should().Be(RiskTier.Medium);
        ScoreOf((1, "SHARED_DEVICE_TRADE", "ord-1"), (2, "REPEATED_PAIR", "ord-1")).Score.Should().Be(30);
        RiskScoreSettings.CapKeyOf("SHARED_DEVICE_TRADE").Should().Be(RiskScoreSettings.CapKeyOf("REPEATED_PAIR"));
        RiskScoreSettings.CapKeyOf("SELF_TRADE").Should().Be("SELF_TRADE");
        RiskScoreSettings.CapKeyOf("DISPUTE_RATE").Should().Be("DISPUTE_RATE");
    }

    [Fact]
    public void TheSharedCap_IsRiskPatternTypeCap_AndOtherTypesStillAddUp()
    {
        var soft = new[] { (1, "SHARED_DEVICE_TRADE", "ord-1"), (2, "SHARED_DEVICE_TRADE", "ord-2"), (3, "REPEATED_PAIR", "ord-2") };

        ScoreOf(RiskScoreSettings.Default with { PatternTypeCap = 30 }, soft).Score.Should().Be(30);
        // A FRD-09 pattern keeps its own cap, and SELF_TRADE is outside the group.
        ScoreOf(soft.Append((4, "DISPUTE_RATE", "esc-1")).Append((5, "SELF_TRADE", "ord-3")).ToArray())
            .Score.Should().Be(50 + 10 + 40);
    }

    [Fact]
    public void SelfTrade_IsUncapped()
    {
        ScoreOf((1, "SELF_TRADE", "ord-1"), (5, "SELF_TRADE", "ord-2"), (9, "SELF_TRADE", "ord-3"))
            .Tier.Should().Be(RiskTier.Critical);
    }

    [Fact]
    public void AWhitelist_SkipsTheSoftRelationshipTypes_NotSelfTrade()
    {
        var now = DateTime.UtcNow;
        var calculator = new RiskScoreCalculator(RiskScoreSettings.Default, now);
        calculator.Add(new Dictionary<string, AttributeValue>
        {
            ["signalKey"] = new() { S = "~whitelist" },
            ["recordType"] = new() { S = "whitelist" },
            ["whitelistUntil"] = new() { S = now.AddDays(10).ToString("o", CultureInfo.InvariantCulture) }
        });
        foreach (var type in new[] { "SELF_TRADE", "SHARED_DEVICE_TRADE", "REPEATED_PAIR" })
        {
            calculator.Add(new Dictionary<string, AttributeValue>
            {
                ["signalKey"] = new() { S = now.AddHours(-1).ToString("yyyy-MM-dd'T'HH':00:00.0000000Z'", CultureInfo.InvariantCulture) + "#" + type },
                ["signalType"] = new() { S = type },
                ["entityId"] = new() { S = "ord-1" },
                ["weight"] = new() { N = FraudDetectionService.SignalWeight(type).ToString(CultureInfo.InvariantCulture) }
            });
        }

        calculator.Result(truncated: false).Score.Should().Be(40);
    }

    // ── HasSignalAsync ───────────────────────────────────────

    private static (FraudDetectionService Service, List<QueryRequest> Requests) WithPages(params QueryResponse[] pages)
    {
        var ddb = Substitute.For<IAmazonDynamoDB>();
        var requests = new List<QueryRequest>();
        var index = 0;
        ddb.QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            // The service reuses its request object across pages: keep a copy of what each call asked for.
            var r = call.Arg<QueryRequest>();
            requests.Add(new QueryRequest
            {
                TableName = r.TableName,
                KeyConditionExpression = r.KeyConditionExpression,
                FilterExpression = r.FilterExpression,
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>(r.ExpressionAttributeValues),
                ConsistentRead = r.ConsistentRead,
                ExclusiveStartKey = r.ExclusiveStartKey,
                ScanIndexForward = r.ScanIndexForward,
                Limit = r.Limit
            });
            return pages[Math.Min(index++, pages.Length - 1)];
        });
        return (new FraudDetectionService(ddb, "crops", "fraud-signals", Substitute.For<IRateLimitRepository>()), requests);
    }

    private static QueryResponse Page(int items, bool more) => new()
    {
        Items = Enumerable.Range(0, items).Select(_ => new Dictionary<string, AttributeValue> { ["signalKey"] = new() { S = "k" } }).ToList(),
        LastEvaluatedKey = more ? new Dictionary<string, AttributeValue> { ["signalKey"] = new() { S = "next" } } : new()
    };

    [Fact]
    public async Task HasSignal_QueriesTheUsersPartitionForTheTypeAndEntity_Consistently()
    {
        var (service, requests) = WithPages(Page(1, more: false));

        (await service.HasSignalAsync("user-1", "SELF_TRADE", "ord-1")).Should().BeTrue();

        var request = requests.Single();
        request.TableName.Should().Be("fraud-signals");
        request.KeyConditionExpression.Should().Be("#u = :uid AND #k >= :since");
        request.FilterExpression.Should().Be("#t = :type AND #e = :entity");
        request.ConsistentRead.Should().BeTrue();
        request.ExpressionAttributeValues[":uid"].S.Should().Be("user-1");
        request.ExpressionAttributeValues[":type"].S.Should().Be("SELF_TRADE");
        request.ExpressionAttributeValues[":entity"].S.Should().Be("ord-1");
        DateTime.Parse(request.ExpressionAttributeValues[":since"].S, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal)
            .Should().BeCloseTo(DateTime.UtcNow.AddDays(-30), TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task HasSignal_None_IsFalse_AfterFollowingAPage()
    {
        var (service, requests) = WithPages(Page(0, more: true), Page(0, more: false));

        (await service.HasSignalAsync("user-1", "SELF_TRADE", "ord-1")).Should().BeFalse();

        requests.Should().HaveCount(2);
        requests[1].ExclusiveStartKey["signalKey"].S.Should().Be("next");
    }

    [Theory]
    [InlineData(1, false)] // the probe meets a row
    [InlineData(0, true)]  // the probe answers only a key
    public async Task HasSignal_RowsLeftAfterThePageCap_IsTrue(int probeItems, bool probeMore)
    {
        var (service, requests) = WithPages(Page(0, more: true), Page(0, more: true), Page(probeItems, probeMore));

        (await service.HasSignalAsync("user-1", "SELF_TRADE", "ord-1")).Should().BeTrue();

        // Two pages (DefaultRiskMaxPages), then one unfiltered row from the last key, in the read's order (FUP-62).
        requests.Should().HaveCount(3);
        var probe = requests[2];
        probe.ExclusiveStartKey["signalKey"].S.Should().Be("next");
        probe.Limit.Should().Be(1);
        probe.FilterExpression.Should().BeNullOrEmpty();
        probe.ConsistentRead.Should().BeTrue();
        probe.ScanIndexForward.Should().BeTrue();
        probe.ExpressionAttributeValues[":since"].S.Should().Be(requests[0].ExpressionAttributeValues[":since"].S);
    }

    [Fact]
    public async Task HasSignal_ALastPageThatEndsExactlyAtTheCap_IsFalse()
    {
        // FUP-62: the second page carried a key but nothing is left, so absence is shown and the signal is written.
        var (service, requests) = WithPages(Page(0, more: true), Page(0, more: true), Page(0, more: false));

        (await service.HasSignalAsync("user-1", "SELF_TRADE", "ord-1")).Should().BeFalse();

        requests.Should().HaveCount(3);
    }

    [Fact]
    public async Task HasSignal_AProbeThatFails_ReachesTheCaller()
    {
        // The relationship scan logs a failed check and writes nothing, as for a failed page.
        var ddb = Substitute.For<IAmazonDynamoDB>();
        var calls = 0;
        ddb.QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ => ++calls <= 2 ? Task.FromResult(Page(0, more: true)) : throw new ProvisionedThroughputExceededException("slow"));
        var service = new FraudDetectionService(ddb, "crops", "fraud-signals", Substitute.For<IRateLimitRepository>());

        await FluentActions.Awaiting(() => service.HasSignalAsync("user-1", "SELF_TRADE", "ord-1"))
            .Should().ThrowAsync<ProvisionedThroughputExceededException>();
    }

    [Fact]
    public async Task HasSignal_NoTable_IsFalse_WithoutARead()
    {
        var ddb = Substitute.For<IAmazonDynamoDB>();
        var service = new FraudDetectionService(ddb, "crops", "", Substitute.For<IRateLimitRepository>());

        (await service.HasSignalAsync("user-1", "SELF_TRADE", "ord-1")).Should().BeFalse();

        await ddb.DidNotReceiveWithAnyArgs().QueryAsync(default(QueryRequest)!, default);
    }
}
