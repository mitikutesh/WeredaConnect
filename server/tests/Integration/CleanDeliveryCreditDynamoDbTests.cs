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
/// FRD-10 against real DynamoDB (LocalStack in CI): the credit's conditional put (one row per escrow and
/// party, however often it is replayed), the signal filter (<c>entityId IN</c>, signal rows only, an
/// admin-cleared signal left out), and the score reading the rows as written.
/// </summary>
[Collection(LocalDynamoDbCollection.Name)]
[Trait("Category", "Integration")]
public sealed class CleanDeliveryCreditDynamoDbTests
{
    private readonly LocalDynamoDbFixture _db;
    private readonly string _run = Guid.NewGuid().ToString("N");

    public CleanDeliveryCreditDynamoDbTests(LocalDynamoDbFixture db)
    {
        _db = db;
    }

    private IAmazonDynamoDB Ddb => _db.Client;

    private string Buyer => $"buyer-frd10-{_run}";
    private string Farmer => $"farmer-frd10-{_run}";
    private string Trucker => $"trucker-frd10-{_run}";

    private CleanDeliveryCreditService Credits() => new(Ddb, _db.FraudSignals);

    private FraudDetectionService Scores() =>
        new(Ddb, _db.Crops, _db.FraudSignals, Substitute.For<IRateLimitRepository>()) { AlarmLog = _ => { } };

    private static string Iso(DateTime at) => at.ToString("o", CultureInfo.InvariantCulture);

    private CleanDeliveryTrade Trade(string escrowId, DateTime releasedAt) =>
        new(escrowId, $"ord-{escrowId}", $"del-{escrowId}", new[] { Buyer, Farmer, Trucker }, Iso(releasedAt));

    private async Task PutSignalAsync(string userId, DateTime at, string type, int weight, string? entityId,
        string? resolution = null)
    {
        var item = new Dictionary<string, AttributeValue>
        {
            ["userId"] = new() { S = userId },
            ["signalKey"] = new() { S = $"{Iso(at)}#{type}" },
            ["signalType"] = new() { S = type },
            ["weight"] = new() { N = weight.ToString(CultureInfo.InvariantCulture) },
            ["status"] = new() { S = resolution == null ? "pending" : "reviewed" }
        };
        if (entityId != null)
        {
            item["entityId"] = new AttributeValue { S = entityId };
        }

        if (resolution != null)
        {
            item["resolution"] = new AttributeValue { S = resolution };
        }

        await Ddb.PutItemAsync(new PutItemRequest { TableName = _db.FraudSignals, Item = item });
    }

    private async Task<List<Dictionary<string, AttributeValue>>> CreditRowsAsync(string userId)
    {
        var response = await Ddb.QueryAsync(new QueryRequest
        {
            TableName = _db.FraudSignals,
            KeyConditionExpression = "userId = :u",
            FilterExpression = "recordType = :credit",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                [":u"] = new() { S = userId },
                [":credit"] = new() { S = "credit" }
            },
            ConsistentRead = true
        });
        return response.Items;
    }

    [LocalDynamoDbFact]
    public async Task CleanTrade_CreditsEachPartyOnce_AReplayWritesNothing()
    {
        var now = DateTime.UtcNow;
        // A signal on another trade does not block this one.
        await PutSignalAsync(Farmer, now.AddDays(-1), "PRICE_ANOMALY", 10, "crop-1");

        var first = await Credits().CreditAsync(Trade("esc-1", now.AddMinutes(-1)), now);
        var replay = await Credits().CreditAsync(Trade("esc-1", now.AddMinutes(-1)), now.AddHours(1));

        first.Credited.Should().Equal(Buyer, Farmer, Trucker);
        replay.Credited.Should().BeEmpty();
        replay.AlreadyCredited.Should().Be(3);
        replay.Failed.Should().BeEmpty();
        (await CreditRowsAsync(Farmer)).Should().ContainSingle()
            .Which["signalKey"].S.Should().Be($"{Iso(now.AddMinutes(-1))}#CLEAN_DELIVERY#esc-1");
        (await Scores().GetUserRiskScoreAsync(Farmer)).Score.Should().Be(5);
    }

    [LocalDynamoDbFact]
    public async Task TwoTradesInOneHour_AreTwoCredits()
    {
        var now = DateTime.UtcNow;
        await PutSignalAsync(Farmer, now.AddDays(-1), "FRAUD_DUPLICATE_LISTING", 20, "crop-1");

        await Credits().CreditAsync(Trade("esc-a", now.AddMinutes(-2)), now);
        await Credits().CreditAsync(Trade("esc-b", now.AddMinutes(-1)), now);

        (await CreditRowsAsync(Farmer)).Should().HaveCount(2);
        (await Scores().GetUserRiskScoreAsync(Farmer)).Score.Should().Be(10);
    }

    [LocalDynamoDbFact]
    public async Task ASignalOnTheDeliveryInOnePartition_CreditsNobody()
    {
        var now = DateTime.UtcNow;
        await PutSignalAsync(Trucker, now.AddHours(-3), "IMPOSSIBLE_DELIVERY_TIME", 15, "del-esc-2");

        var outcome = await Credits().CreditAsync(Trade("esc-2", now.AddMinutes(-1)), now);

        outcome.NotCleanReason.Should().Be(CleanDeliveryCreditService.NotCleanSignal);
        (await CreditRowsAsync(Buyer)).Should().BeEmpty();
        (await CreditRowsAsync(Farmer)).Should().BeEmpty();
        (await CreditRowsAsync(Trucker)).Should().BeEmpty();
    }

    [LocalDynamoDbFact]
    public async Task AFalsePositiveOnTheOrder_DoesNotBlockTheCredit()
    {
        // The admin approved it (false positive): it no longer counts, so the trade is clean.
        var now = DateTime.UtcNow;
        await PutSignalAsync(Buyer, now.AddDays(-2), "AMOUNT_ANOMALY", 10, "ord-esc-3", resolution: "approve");

        var outcome = await Credits().CreditAsync(Trade("esc-3", now.AddMinutes(-1)), now);

        outcome.NotCleanReason.Should().BeNull();
        outcome.Credited.Should().Equal(Buyer, Farmer, Trucker);
        (await Scores().GetUserRiskScoreAsync(Buyer)).Score.Should().Be(0);
    }

    [LocalDynamoDbFact]
    public async Task ARejectedSignalOnTheEscrow_BlocksTheCredit()
    {
        // A reject confirms the signal: it still counts, so the trade is not clean.
        var now = DateTime.UtcNow;
        await PutSignalAsync(Farmer, now.AddDays(-1), "ROLE_FORBIDDEN", 25, "esc-4", resolution: "reject");

        var outcome = await Credits().CreditAsync(Trade("esc-4", now.AddMinutes(-1)), now);

        outcome.NotCleanReason.Should().Be(CleanDeliveryCreditService.NotCleanSignal);
        (await CreditRowsAsync(Farmer)).Should().BeEmpty();
    }
}
