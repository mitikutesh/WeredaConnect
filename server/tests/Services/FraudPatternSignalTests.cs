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
/// FRD-09 (fraud-detection-architecture.md 14.2, 14.5, 15): the six transaction pattern signal types in the
/// risk score. Each carries its catalogue weight and class, counts once per observed entity, and is capped
/// per type at <c>RISK_PATTERN_TYPE_CAP</c>, so one pattern alone never takes a user past MEDIUM.
/// </summary>
public class FraudPatternSignalTests
{
    public static TheoryData<string, int, bool, string> Catalogue => new()
    {
        { "AMOUNT_ANOMALY", 10, true, "medium" },
        { "NEW_ACCOUNT_LARGE_AMOUNT", 10, true, "medium" },
        { "KYC_THRESHOLD_STRUCTURING", 20, false, "high" },
        { "CANCEL_AFTER_FUND_RATE", 15, true, "medium" },
        { "DISPUTE_RATE", 10, true, "medium" },
        { "IMPOSSIBLE_DELIVERY_TIME", 15, true, "medium" }
    };

    [Theory]
    [MemberData(nameof(Catalogue))]
    public async Task PatternSignals_CarryTheCatalogueWeightClassAndRowSchema(string type, int weight, bool soft, string severity)
    {
        var ddb = Substitute.For<IAmazonDynamoDB>();
        PutItemRequest? captured = null;
        ddb.PutItemAsync(Arg.Do<PutItemRequest>(r => captured = r), Arg.Any<CancellationToken>()).Returns(new PutItemResponse());
        var service = new FraudDetectionService(ddb, "crops", "fraud-signals", Substitute.For<IRateLimitRepository>());

        await service.RecordFraudSignalAsync("user-1", type, "order-1", "order", "why");

        FraudDetectionService.SignalWeight(type).Should().Be(weight);
        FraudDetectionService.IsSoftSignal(type).Should().Be(soft);
        FraudDetectionService.IsPatternSignal(type).Should().BeTrue();
        FraudDetectionService.CountsOncePerEntity(type).Should().BeTrue();
        // The existing row schema and TTL: one row per user, type and hour, 30 days.
        captured!.TableName.Should().Be("fraud-signals");
        captured.Item["signalKey"].S.Should().EndWith($"#{type}");
        captured.Item["weight"].N.Should().Be(weight.ToString(CultureInfo.InvariantCulture));
        captured.Item["severity"].S.Should().Be(severity);
        captured.Item["entityId"].S.Should().Be("order-1");
        long.Parse(captured.Item["expiresAt"].N, CultureInfo.InvariantCulture)
            .Should().BeCloseTo(DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeSeconds(), 60);
        captured.ConditionExpression.Should().Be("attribute_not_exists(signalKey)");
    }

    [Fact]
    public void OtherTypes_AreNotPatternSignals()
    {
        FraudDetectionService.IsPatternSignal("ROLE_FORBIDDEN").Should().BeFalse();
        FraudDetectionService.IsPatternSignal("GPS_ANOMALY").Should().BeFalse();
    }

    private static RiskScoreResult ScoreOf(RiskScoreSettings settings, params (int HoursAgo, string Type, string? EntityId, string? Resolution)[] rows)
    {
        var now = DateTime.UtcNow;
        var calculator = new RiskScoreCalculator(settings, now);
        foreach (var (hoursAgo, type, entityId, resolution) in rows.OrderBy(r => r.HoursAgo))
        {
            var hour = now.AddHours(-hoursAgo).ToString("yyyy-MM-dd'T'HH':00:00.0000000Z'", CultureInfo.InvariantCulture);
            var row = new Dictionary<string, AttributeValue>
            {
                ["signalKey"] = new() { S = $"{hour}#{type}" },
                ["signalType"] = new() { S = type },
                ["weight"] = new() { N = FraudDetectionService.SignalWeight(type).ToString(CultureInfo.InvariantCulture) }
            };
            if (entityId != null)
            {
                row["entityId"] = new AttributeValue { S = entityId };
            }

            if (resolution != null)
            {
                row["resolution"] = new AttributeValue { S = resolution };
            }

            calculator.Add(row);
        }

        return calculator.Result(truncated: false);
    }

    private static RiskScoreResult ScoreOf(params (int HoursAgo, string Type, string? EntityId)[] rows) =>
        ScoreOf(RiskScoreSettings.Default, rows.Select(r => (r.HoursAgo, r.Type, r.EntityId, (string?)null)).ToArray());

    [Fact]
    public void APaymentRetriedInSeveralHours_CountsOnce()
    {
        // payment_failed then payment_submitted again, hours apart: the same order is one observation.
        var result = ScoreOf((1, "AMOUNT_ANOMALY", "order-1"), (5, "AMOUNT_ANOMALY", "order-1"), (30, "AMOUNT_ANOMALY", "order-1"));

        result.Score.Should().Be(10);
    }

    [Fact]
    public void OnePatternAlone_StopsAtMedium()
    {
        // Structuring on six orders: 120 uncapped, 50 with the per-type cap.
        var rows = Enumerable.Range(1, 6).Select(h => (h * 24, "KYC_THRESHOLD_STRUCTURING", (string?)$"order-{h}")).ToArray();

        var result = ScoreOf(rows);

        result.Score.Should().Be(50);
        result.Tier.Should().Be(RiskTier.Medium);
    }

    [Fact]
    public void PatternsTogether_AddUpAcrossTypes()
    {
        var structuring = Enumerable.Range(1, 3).Select(h => (h * 24, "KYC_THRESHOLD_STRUCTURING", (string?)$"order-{h}"));
        var amounts = Enumerable.Range(1, 3).Select(h => (h * 24 + 1, "AMOUNT_ANOMALY", (string?)$"order-{h}"));
        var disputes = Enumerable.Range(1, 2).Select(h => (h * 24 + 2, "DISPUTE_RATE", (string?)$"escrow-{h}"));

        var result = ScoreOf(structuring.Concat(amounts).Concat(disputes).ToArray());

        result.Score.Should().Be(50 + 30 + 20);
        result.Tier.Should().Be(RiskTier.Critical);
        ScoreOf(structuring.Concat(amounts).ToArray()).Tier.Should().Be(RiskTier.High);
    }

    [Fact]
    public void ThePatternCap_IsConfigurable_ApartFromTheRejectCap()
    {
        var rows = Enumerable.Range(1, 6).Select(h => (h * 24, "CANCEL_AFTER_FUND_RATE", (string?)$"order-{h}", (string?)null)).ToArray();

        ScoreOf(RiskScoreSettings.Default with { PatternTypeCap = 75 }, rows).Score.Should().Be(75);
        ScoreOf(RiskScoreSettings.Default with { PatternTypeCap = 0 }, rows).Score.Should().Be(0);
        ScoreOf(RiskScoreSettings.Default with { RejectTypeCap = 0 }, rows).Score.Should().Be(50);
    }

    [Fact]
    public void AnApprovedRow_ClearsItsEntity()
    {
        var result = ScoreOf(RiskScoreSettings.Default,
            (1, "IMPOSSIBLE_DELIVERY_TIME", "del-1", "approve"), (2, "IMPOSSIBLE_DELIVERY_TIME", "del-2", null));

        result.Score.Should().Be(15);
    }

    [Fact]
    public void AWhitelist_SkipsTheSoftPatterns_NotStructuring()
    {
        var now = DateTime.UtcNow;
        var calculator = new RiskScoreCalculator(RiskScoreSettings.Default, now);
        calculator.Add(new Dictionary<string, AttributeValue>
        {
            ["signalKey"] = new() { S = "~whitelist" },
            ["recordType"] = new() { S = "whitelist" },
            ["whitelistUntil"] = new() { S = now.AddDays(10).ToString("o", CultureInfo.InvariantCulture) }
        });
        foreach (var type in new[] { "AMOUNT_ANOMALY", "NEW_ACCOUNT_LARGE_AMOUNT", "KYC_THRESHOLD_STRUCTURING",
                     "CANCEL_AFTER_FUND_RATE", "DISPUTE_RATE", "IMPOSSIBLE_DELIVERY_TIME" })
        {
            calculator.Add(new Dictionary<string, AttributeValue>
            {
                ["signalKey"] = new()
                {
                    S = now.AddHours(-1).ToString("yyyy-MM-dd'T'HH':00:00.0000000Z'", CultureInfo.InvariantCulture) + "#" + type
                },
                ["signalType"] = new() { S = type },
                ["entityId"] = new() { S = "order-1" },
                ["weight"] = new() { N = FraudDetectionService.SignalWeight(type).ToString(CultureInfo.InvariantCulture) }
            });
        }

        calculator.Result(truncated: false).Score.Should().Be(20);
    }

    [Theory]
    [InlineData("0", 0)]
    [InlineData("80", 80)]
    public void Settings_PatternTypeCapAcceptsAnyNonNegativeInteger(string value, int expected)
    {
        RiskScoreSettings.From(name => name == "RISK_PATTERN_TYPE_CAP" ? value : null).PatternTypeCap.Should().Be(expected);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("abc")]
    [InlineData("2.5")]
    public void Settings_InvalidPatternTypeCapKeepsTheDefault(string value)
    {
        RiskScoreSettings.From(name => name == "RISK_PATTERN_TYPE_CAP" ? value : null).Should().Be(RiskScoreSettings.Default);
        RiskScoreSettings.Default.PatternTypeCap.Should().Be(50);
    }
}
