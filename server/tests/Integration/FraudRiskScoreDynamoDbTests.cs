using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using FluentAssertions;
using NSubstitute;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.RateLimiting;
using WeredaConnect.Server.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Integration;

/// <summary>
/// FRD-02 against real DynamoDB (LocalStack in CI): the risk score's key condition
/// (<c>signalKey &gt;= since</c>), the newest-first order that reads the <c>~whitelist</c> row
/// first, the partition boundary, the writer's key format, and the 1 MB page cap.
/// </summary>
[Collection(LocalDynamoDbCollection.Name)]
[Trait("Category", "Integration")]
public sealed class FraudRiskScoreDynamoDbTests
{
    private readonly LocalDynamoDbFixture _db;
    private readonly string _userId = $"user-frd02-{Guid.NewGuid():N}";
    private readonly List<string> _alarms = new();

    public FraudRiskScoreDynamoDbTests(LocalDynamoDbFixture db)
    {
        _db = db;
    }

    private IAmazonDynamoDB Ddb => _db.Client;

    private FraudDetectionService Service() =>
        new(Ddb, _db.Crops, _db.FraudSignals, Substitute.For<IRateLimitRepository>()) { AlarmLog = _alarms.Add };

    private static string Iso(DateTime at) => at.ToString("o", CultureInfo.InvariantCulture);

    private static Dictionary<string, AttributeValue> Row(
        string userId, string signalKey, string? signalType, int? weight, string? recordType = null, string padding = "")
    {
        var item = new Dictionary<string, AttributeValue>
        {
            ["userId"] = new() { S = userId },
            ["signalKey"] = new() { S = signalKey },
            ["description"] = new() { S = padding.Length > 0 ? padding : "test" }
        };
        if (signalType != null)
        {
            item["signalType"] = new AttributeValue { S = signalType };
            item["status"] = new AttributeValue { S = "pending" };
        }

        if (weight != null)
        {
            item["weight"] = new AttributeValue { N = weight.Value.ToString(CultureInfo.InvariantCulture) };
        }

        if (recordType != null)
        {
            item["recordType"] = new AttributeValue { S = recordType };
        }

        return item;
    }

    private Task PutAsync(Dictionary<string, AttributeValue> item) =>
        Ddb.PutItemAsync(new PutItemRequest { TableName = _db.FraudSignals, Item = item });

    private Task PutSignalAsync(string userId, DateTime at, string type, int weight) =>
        PutAsync(Row(userId, $"{Iso(at)}#{type}", type, weight));

    [LocalDynamoDbFact]
    public async Task Score_ReadsOnlyTheWindowOfOnePartitionWithTheWhitelistFirst()
    {
        var now = DateTime.UtcNow;
        var whitelist = Row(_userId, "~whitelist", signalType: null, weight: null, recordType: "whitelist");
        whitelist["whitelistUntil"] = new AttributeValue { S = Iso(now.AddDays(1)) };
        await PutAsync(whitelist);
        await PutSignalAsync(_userId, now.AddMinutes(-5), "GPS_ANOMALY", 25);              // soft: whitelisted
        await PutSignalAsync(_userId, now.AddMinutes(-10), "FRAUD_DUPLICATE_LISTING", 20);
        await PutSignalAsync(_userId, now.AddDays(-31), "FRAUD_VELOCITY_EXCEEDED", 15);    // before the window
        await PutAsync(Row(_userId, $"{Iso(now.AddHours(-2))}#CLEAN_DELIVERY", "CLEAN_DELIVERY", -5, recordType: "credit"));
        await PutSignalAsync($"other-{_userId}", now.AddMinutes(-1), "FRAUD_DUPLICATE_LISTING", 100);

        var result = await Service().GetUserRiskScoreAsync(_userId);

        result.Should().Be(new RiskScoreResult { Score = 15, EnforcementScore = 15, Tier = RiskTier.Low });
        _alarms.Should().BeEmpty();
    }

    [LocalDynamoDbFact]
    public async Task Score_CountsRowsAsTheWriterStoresThem()
    {
        var service = Service();
        await service.RecordFraudSignalAsync(_userId, "FRAUD_DUPLICATE_LISTING", "crop-1", "crop", "duplicate");
        await service.RecordFraudSignalAsync(_userId, "FRAUD_VELOCITY_EXCEEDED", "crop-2", "crop", "velocity");

        var result = await service.GetUserRiskScoreAsync(_userId);

        result.Score.Should().Be(35);
        result.Tier.Should().Be(RiskTier.Medium);
        result.Source.Should().Be(RiskScoreSource.Computed);
    }

    [LocalDynamoDbFact]
    public async Task Writer_KeepsOneRowPerUserTypeAndHour()
    {
        // FRD-08: the real condition. A burst of one type is one row; another type and another user
        // each get their own.
        var service = Service();
        for (var i = 0; i < 5; i++)
        {
            await service.RecordFraudSignalAsync(_userId, "ROLE_FORBIDDEN", $"crop-{i}", "crop", "burst");
        }

        await service.RecordFraudSignalAsync(_userId, "IDOR_CREATE_CONFLICT", "crop-9", "crop", "probe");
        await service.RecordFraudSignalAsync($"other-{_userId}", "ROLE_FORBIDDEN", "crop-0", "crop", "burst");

        var rows = await Ddb.QueryAsync(new QueryRequest
        {
            TableName = _db.FraudSignals,
            KeyConditionExpression = "userId = :u",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue> { [":u"] = new() { S = _userId } }
        });

        rows.Items.Select(r => r["signalType"].S).Should().BeEquivalentTo("ROLE_FORBIDDEN", "IDOR_CREATE_CONFLICT");
        rows.Items.Single(r => r["signalType"].S == "ROLE_FORBIDDEN")["entityId"].S.Should().Be("crop-0");
        (await service.GetUserRiskScoreAsync(_userId)).Score.Should().Be(65);
    }

    [LocalDynamoDbFact]
    public async Task Score_BurstBeyondTwoPagesIsTruncatedToCritical()
    {
        // About 2.6 MB of one burst in one hour: DynamoDB returns at most 1 MB per page, so two
        // pages leave rows unread. Deduplicated, the burst is worth 15; unread, it fails closed.
        var hour = DateTime.UtcNow.AddHours(-1);
        var start = new DateTime(hour.Year, hour.Month, hour.Day, hour.Hour, 0, 0, DateTimeKind.Utc);
        var padding = new string('x', 10_000);
        var rows = Enumerable.Range(0, 260)
            .Select(i => Row(_userId, $"{Iso(start.AddSeconds(i))}#FRAUD_VELOCITY_EXCEEDED", "FRAUD_VELOCITY_EXCEEDED", 15, padding: padding))
            .ToList();
        foreach (var batch in rows.Chunk(25))
        {
            var response = await Ddb.BatchWriteItemAsync(new BatchWriteItemRequest
            {
                RequestItems = new Dictionary<string, List<WriteRequest>>
                {
                    [_db.FraudSignals] = batch.Select(r => new WriteRequest(new PutRequest(r))).ToList()
                }
            });
            response.UnprocessedItems.Should().BeEmpty();
        }

        var result = await Service().GetUserRiskScoreAsync(_userId);

        result.Truncated.Should().BeTrue();
        result.Tier.Should().Be(RiskTier.Critical);
        result.Score.Should().Be(15);
        _alarms.Should().ContainSingle().Which.Should().StartWith(FraudDetectionService.RiskScoreTruncatedToken + " ");
    }
}
