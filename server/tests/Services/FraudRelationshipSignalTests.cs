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

    private static RiskScoreResult ScoreOf(params (int HoursAgo, string Type, string EntityId)[] rows)
    {
        var now = DateTime.UtcNow;
        var calculator = new RiskScoreCalculator(RiskScoreSettings.Default, now);
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
                ExclusiveStartKey = r.ExclusiveStartKey
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

    [Fact]
    public async Task HasSignal_RowsLeftAfterThePageCap_IsTrue()
    {
        var (service, requests) = WithPages(Page(0, more: true), Page(0, more: true), Page(0, more: true));

        (await service.HasSignalAsync("user-1", "SELF_TRADE", "ord-1")).Should().BeTrue();

        requests.Should().HaveCount(2);
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
