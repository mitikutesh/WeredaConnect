using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.TestUtilities;
using FluentAssertions;
using NSubstitute;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Functions;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.RateLimiting;
using WeredaConnect.Server.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Integration;

/// <summary>
/// FRD-15 against real DynamoDB (LocalStack in CI): seeded orders and users on the deployed key schemas, the scan's
/// key-bounded status Queries and the buyer index read, the three patterns written on both parties with the order as
/// the entity, the existence check that keeps a later run from writing twice, and the score reading the rows.
/// </summary>
[Collection(RelationshipScanDynamoDbCollection.Name)]
[Trait("Category", "Integration")]
public sealed class RelationshipPatternDynamoDbTests
{
    private readonly LocalDynamoDbFixture _db;
    private readonly string _run = Guid.NewGuid().ToString("N")[..8];

    public RelationshipPatternDynamoDbTests(LocalDynamoDbFixture db)
    {
        _db = db;
    }

    private IAmazonDynamoDB Ddb => _db.Client;

    private FraudDetectionService Fraud() =>
        new(Ddb, _db.Crops, _db.FraudSignals, Substitute.For<IRateLimitRepository>()) { AlarmLog = _ => { } };

    private RelationshipPatternService Scan() => new(
        new OrderRepository(Ddb, _db.Orders), new UserRepository(Ddb, _db.Users), Fraud(), RelationshipPatternSettings.Default);

    private static CronRunSlice Slice() => CronRunBudget.StartNew().Slice(TimeSpan.FromMinutes(4));

    private static string Iso(DateTime at) => at.ToString("o", CultureInfo.InvariantCulture);

    private string Id(string name) => $"{name}-frd15-{_run}";

    private async Task SeedUserAsync(string userId, string phone, string? fayda = null, params string[] devices)
    {
        var created = await new UserRepository(Ddb, _db.Users).CreateUserAsync(new UserDto
        {
            UserId = userId,
            PhoneNumber = phone,
            Role = "buyer",
            Woreda = "Ada'a",
            FaydaId = fayda,
            IsPhoneVerified = true,
            KnownDevices = devices.ToList(),
            CreatedAt = "2026-01-01T00:00:00Z",
            UpdatedAt = "2026-01-01T00:00:00Z",
            Version = 1
        });
        created.IsSuccess.Should().BeTrue();
    }

    private async Task SeedOrderAsync(string orderId, string buyerId, string farmerId, DateTime createdAt, string status, bool funded = true)
    {
        var item = new Dictionary<string, AttributeValue>
        {
            ["orderId"] = new() { S = orderId },
            ["createdAt"] = new() { S = Iso(createdAt) },
            ["status"] = new() { S = status },
            ["buyerId"] = new() { S = buyerId },
            ["farmerId"] = new() { S = farmerId },
            ["version"] = new() { N = "3" }
        };
        if (funded)
        {
            item["paymentVerifiedAt"] = new AttributeValue { S = Iso(createdAt.AddMinutes(4)) };
        }

        await Ddb.PutItemAsync(new PutItemRequest { TableName = _db.Orders, Item = item });
    }

    private async Task<List<Dictionary<string, AttributeValue>>> SignalRowsAsync(string userId)
    {
        var response = await Ddb.QueryAsync(new QueryRequest
        {
            TableName = _db.FraudSignals,
            KeyConditionExpression = "userId = :u",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue> { [":u"] = new() { S = userId } },
            ConsistentRead = true
        });
        return response.Items;
    }

    [LocalDynamoDbFact]
    public async Task EachPatternFromItsFixture_IsWrittenOnBothParties_AndARerunWritesNothing()
    {
        var now = DateTime.UtcNow;

        // Self-trader on one install (the FRD-17 persona): a funded self-pickup between two accounts of one phone.
        await SeedUserAsync(Id("dev-farmer"), $"+2519{_run[..4]}0001", null, Id("install"));
        await SeedUserAsync(Id("dev-buyer"), $"+2519{_run[..4]}0002", null, Id("install"), Id("install-2"));
        await SeedOrderAsync(Id("ord-device"), Id("dev-buyer"), Id("dev-farmer"), now.AddHours(-2), "delivered");

        // One Fayda ID on both sides.
        await SeedUserAsync(Id("id-farmer"), $"+2519{_run[..4]}0003", Id("FAYDA"));
        await SeedUserAsync(Id("id-buyer"), $"+2519{_run[..4]}0004", Id("FAYDA"));
        await SeedOrderAsync(Id("ord-identity"), Id("id-buyer"), Id("id-farmer"), now.AddHours(-1), "funded");

        // A repeated pair: two settled orders this week (before the lookback) and a new one.
        await SeedUserAsync(Id("pair-farmer"), $"+2519{_run[..4]}0005");
        await SeedUserAsync(Id("pair-buyer"), $"+2519{_run[..4]}0006");
        await SeedOrderAsync(Id("ord-pair-1"), Id("pair-buyer"), Id("pair-farmer"), now.AddDays(-5), "settled");
        await SeedOrderAsync(Id("ord-pair-2"), Id("pair-buyer"), Id("pair-farmer"), now.AddDays(-3), "settled");
        await SeedOrderAsync(Id("ord-pair-3"), Id("pair-buyer"), Id("pair-farmer"), now.AddHours(-3), "accepted");

        // Not read: an unpaid order between the self-trader's accounts, and one older than the lookback.
        await SeedOrderAsync(Id("ord-unpaid"), Id("dev-buyer"), Id("dev-farmer"), now.AddHours(-1), "cancelled", funded: false);
        await SeedOrderAsync(Id("ord-old"), Id("id-buyer"), Id("id-farmer"), now.AddHours(-60), "settled");

        var first = await Scan().RunAsync(now, Slice(), new TestLambdaContext());

        first.Failed.Should().Be(0);
        first.Written.Should().Be(6);
        foreach (var (user, type, order) in new[]
                 {
                     (Id("dev-buyer"), "SHARED_DEVICE_TRADE", Id("ord-device")),
                     (Id("dev-farmer"), "SHARED_DEVICE_TRADE", Id("ord-device")),
                     (Id("id-buyer"), "SELF_TRADE", Id("ord-identity")),
                     (Id("id-farmer"), "SELF_TRADE", Id("ord-identity")),
                     (Id("pair-buyer"), "REPEATED_PAIR", Id("ord-pair-3")),
                     (Id("pair-farmer"), "REPEATED_PAIR", Id("ord-pair-3"))
                 })
        {
            var rows = await SignalRowsAsync(user);
            rows.Should().ContainSingle();
            rows[0]["signalType"].S.Should().Be(type);
            rows[0]["entityId"].S.Should().Be(order);
            rows[0]["entityType"].S.Should().Be("order");
            rows[0]["status"].S.Should().Be("pending");
        }

        // The next hourly run sees the same orders: every row is found, nothing is written twice.
        var second = await Scan().RunAsync(now.AddHours(1), Slice(), new TestLambdaContext());

        second.Written.Should().Be(0);
        second.AlreadyRecorded.Should().Be(6);
        (await SignalRowsAsync(Id("dev-farmer"))).Should().ContainSingle();

        // The score reads the rows as written: one shared-device trade is LOW (25), one self-trade MEDIUM (40).
        (await Fraud().GetUserRiskScoreAsync(Id("dev-farmer"))).Score.Should().Be(25);
        (await Fraud().GetUserRiskScoreAsync(Id("id-buyer"))).Tier.Should().Be(RiskTier.Medium);
    }

    [LocalDynamoDbFact]
    public async Task HasSignal_FindsAnApprovedRowToo()
    {
        var userId = Id("approved");
        await Ddb.PutItemAsync(new PutItemRequest
        {
            TableName = _db.FraudSignals,
            Item = new Dictionary<string, AttributeValue>
            {
                ["userId"] = new() { S = userId },
                ["signalKey"] = new() { S = $"{Iso(DateTime.UtcNow.AddDays(-2))}#SHARED_DEVICE_TRADE" },
                ["signalType"] = new() { S = "SHARED_DEVICE_TRADE" },
                ["entityId"] = new() { S = "ord-x" },
                ["status"] = new() { S = "reviewed" },
                ["resolution"] = new() { S = "approve" }
            }
        });

        (await Fraud().HasSignalAsync(userId, "SHARED_DEVICE_TRADE", "ord-x")).Should().BeTrue();
        (await Fraud().HasSignalAsync(userId, "SHARED_DEVICE_TRADE", "ord-y")).Should().BeFalse();
        (await Fraud().HasSignalAsync(userId, "REPEATED_PAIR", "ord-x")).Should().BeFalse();
    }
}
