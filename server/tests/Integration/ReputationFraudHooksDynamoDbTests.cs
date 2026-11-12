using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.TestUtilities;
using FluentAssertions;
using NSubstitute;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.RateLimiting;
using WeredaConnect.Server.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Integration;

/// <summary>
/// ESC-33 against real DynamoDB (LocalStack in CI), the requests the mocked tests cannot validate:
/// the attribution guard's Query (filter, projection, consistent read) and its put with the source
/// attribute, a flood attempt by one buyer and by several, the reliability signals written by a
/// recorded reputation event on the real events, users and fraud-signals tables, the buyer decline
/// pattern read on the orders buyer index, and the score reading every new row.
/// </summary>
[Collection(LocalDynamoDbCollection.Name)]
[Trait("Category", "Integration")]
public sealed class ReputationFraudHooksDynamoDbTests
{
    private readonly LocalDynamoDbFixture _db;
    private readonly string _run = Guid.NewGuid().ToString("N")[..8];
    private readonly TestLambdaLogger _logger = new();

    public ReputationFraudHooksDynamoDbTests(LocalDynamoDbFixture db)
    {
        _db = db;
    }

    private IAmazonDynamoDB Ddb => _db.Client;

    private string Id(string name) => $"{name}-esc33-{_run}";

    private FraudDetectionService Fraud() =>
        new(Ddb, _db.Crops, _db.FraudSignals, Substitute.For<IRateLimitRepository>()) { AlarmLog = _ => { } };

    private ReputationService Reputation() => new(
        new ReputationEventRepository(Ddb, _db.ReputationEvents, _db.Users), ReputationPolicySettings.Defaults, Fraud())
    {
        BuyerPatterns = new TransactionPatternService(new OrderRepository(Ddb, _db.Orders), null, TransactionPatternSettings.Default)
    };

    private static string Iso(DateTime at) => at.ToString("o", CultureInfo.InvariantCulture);

    private async Task<List<Dictionary<string, AttributeValue>>> SignalsOf(string userId)
        => (await Ddb.QueryAsync(new QueryRequest
        {
            TableName = _db.FraudSignals,
            KeyConditionExpression = "userId = :u",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue> { [":u"] = new() { S = userId } },
            ConsistentRead = true
        })).Items;

    /// <summary>A signal row as the writer shapes it, keyed at an earlier hour (the writer keeps one row per hour).</summary>
    private Task SeedSignalAsync(string userId, string type, string entityId, int hoursAgo, string? source)
    {
        var hour = DateTime.UtcNow.AddHours(-hoursAgo);
        var key = $"{Iso(new DateTime(hour.Year, hour.Month, hour.Day, hour.Hour, 0, 0, DateTimeKind.Utc))}#{type}";
        var item = new Dictionary<string, AttributeValue>
        {
            ["userId"] = new() { S = userId },
            ["signalKey"] = new() { S = key },
            ["recordType"] = new() { S = "signal" },
            ["signalType"] = new() { S = type },
            ["weight"] = new() { N = FraudDetectionService.SignalWeight(type).ToString(CultureInfo.InvariantCulture) },
            ["status"] = new() { S = "pending" },
            ["entityId"] = new() { S = entityId },
            ["entityType"] = new() { S = "delivery" },
            ["createdAt"] = new() { S = Iso(hour) },
            ["expiresAt"] = new() { N = new DateTimeOffset(hour).AddDays(30).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture) }
        };
        if (source != null)
        {
            item[FraudDetectionService.SourceUserIdAttribute] = new AttributeValue { S = source };
        }

        return Ddb.PutItemAsync(_db.FraudSignals, item);
    }

    private async Task SeedUserAsync(string userId, string role)
    {
        var created = await new UserRepository(Ddb, _db.Users).CreateUserAsync(new UserDto
        {
            UserId = userId,
            PhoneNumber = $"+2519{Random.Shared.Next(10_000_000, 99_999_999)}",
            Role = role,
            Woreda = "Ada'a",
            IsPhoneVerified = true,
            CreatedAt = "2026-01-01T00:00:00Z",
            UpdatedAt = "2026-01-01T00:00:00Z",
            Version = 1
        });
        created.IsSuccess.Should().BeTrue();
    }

    private Task SeedOrderAsync(string orderId, string buyerId, DateTime createdAt, string? farmerResponse, bool paid = true)
    {
        var item = new Dictionary<string, AttributeValue>
        {
            ["orderId"] = new() { S = orderId },
            ["createdAt"] = new() { S = Iso(createdAt) },
            ["status"] = new() { S = "rejected" },
            ["buyerId"] = new() { S = buyerId },
            ["farmerId"] = new() { S = Id("farmer") },
            ["version"] = new() { N = "3" }
        };
        if (paid)
        {
            item["paymentVerifiedAt"] = new AttributeValue { S = Iso(createdAt.AddMinutes(4)) };
        }

        if (farmerResponse != null)
        {
            item["farmerResponse"] = new AttributeValue { S = farmerResponse };
        }

        return Ddb.PutItemAsync(_db.Orders, item);
    }

    private ReputationCandidate Candidate(string userId, string party, string code, string slot, string orderId, string buyerId) => new()
    {
        UserId = userId,
        EventId = slot + orderId,
        Code = code,
        Party = party,
        OrderId = orderId,
        BuyerId = buyerId,
        OccurredAt = DateTime.UtcNow.AddMinutes(-2),
        RequestWindow = TimeSpan.FromHours(24),
        Reachable = true
    };

    // ===== the attribution guard =====

    [LocalDynamoDbFact]
    public async Task AttributedSignal_IsWrittenOnTheTargetWithItsSource_AndTheScoreReadsIt()
    {
        var (trucker, buyer) = (Id("trucker-a"), Id("buyer-a"));

        var outcome = await Fraud().RecordAttributedSignalAsync(trucker, buyer, "GPS_ANOMALY", Id("del-1"), "delivery", "why");

        outcome.Should().Be(AttributedSignalOutcome.Written);
        var row = (await SignalsOf(trucker)).Should().ContainSingle().Subject;
        row["signalType"].S.Should().Be("GPS_ANOMALY");
        row["entityId"].S.Should().Be(Id("del-1"));
        row[FraudDetectionService.SourceUserIdAttribute].S.Should().Be(buyer);
        row["recordType"].S.Should().Be("signal");
        (await SignalsOf(buyer)).Should().BeEmpty("the buyer who confirmed carries nothing");
        var score = await Fraud().GetUserRiskScoreAsync(trucker);
        score.Score.Should().Be(25);
        score.Truncated.Should().BeFalse();
    }

    [LocalDynamoDbFact]
    public async Task AFloodByOneBuyer_LeavesOneRowOnTheTrucker()
    {
        var (trucker, buyer) = (Id("trucker-b"), Id("buyer-b"));
        // The buyer's first anomaly on this trucker, from an earlier hour.
        await SeedSignalAsync(trucker, "GPS_ANOMALY", Id("del-0"), hoursAgo: 30, source: buyer);

        var outcomes = new List<AttributedSignalOutcome>();
        for (var i = 1; i <= 25; i++)
        {
            outcomes.Add(await Fraud().RecordAttributedSignalAsync(trucker, buyer, "GPS_ANOMALY", Id($"del-{i}"), "delivery", "why"));
        }

        // The same delivery again is not a second row either.
        (await Fraud().RecordAttributedSignalAsync(trucker, buyer, "GPS_ANOMALY", Id("del-0"), "delivery", "why"))
            .Should().Be(AttributedSignalOutcome.AlreadyRecorded);
        outcomes.Should().OnlyContain(o => o == AttributedSignalOutcome.SourceCapped);
        (await SignalsOf(trucker)).Should().ContainSingle();
        var score = await Fraud().GetUserRiskScoreAsync(trucker);
        score.Score.Should().Be(25);
        score.Tier.Should().Be(RiskTier.Low);
    }

    [LocalDynamoDbFact]
    public async Task ManyBuyersTogether_StopAtThreeRows_SoTheTruckerNeverReachesCriticalOnThem()
    {
        var trucker = Id("trucker-c");
        for (var i = 1; i <= 3; i++)
        {
            await SeedSignalAsync(trucker, "GPS_ANOMALY", Id($"del-{i}"), hoursAgo: i * 10, source: Id($"buyer-c{i}"));
        }

        // A trucker's row of another type does not count toward the cap of this one.
        await SeedSignalAsync(trucker, "GPS_JUMP", Id("trip-1"), hoursAgo: 5, source: null);

        var outcome = await Fraud().RecordAttributedSignalAsync(trucker, Id("buyer-c4"), "GPS_ANOMALY", Id("del-4"), "delivery", "why");

        outcome.Should().Be(AttributedSignalOutcome.TargetCapped);
        (await SignalsOf(trucker)).Count(r => r["signalType"].S == "GPS_ANOMALY").Should().Be(3);
        var score = await Fraud().GetUserRiskScoreAsync(trucker);
        score.Score.Should().Be(85);
        score.Tier.Should().Be(RiskTier.High);
        score.Truncated.Should().BeFalse();
    }

    [LocalDynamoDbFact]
    public async Task TwoBuyersInOneHour_TheHourLimitKeepsOneRow()
    {
        var trucker = Id("trucker-d");

        (await Fraud().RecordAttributedSignalAsync(trucker, Id("buyer-d1"), "GPS_ANOMALY", Id("del-1"), "delivery", "why"))
            .Should().Be(AttributedSignalOutcome.Written);
        (await Fraud().RecordAttributedSignalAsync(trucker, Id("buyer-d2"), "GPS_ANOMALY", Id("del-2"), "delivery", "why"))
            .Should().Be(AttributedSignalOutcome.HourLimited);

        (await SignalsOf(trucker)).Should().ContainSingle();
    }

    // ===== the reliability signals of a recorded reputation event =====

    [LocalDynamoDbTheory]
    [InlineData(ReputationCodes.NoShow, ReputationSlots.NoShow, "NO_SHOW", 15)]
    [InlineData(ReputationCodes.DeliveryOverdue, ReputationSlots.Overdue, "DELIVERY_OVERDUE", 25)]
    public async Task ARecordedReliabilityEvent_WritesOneSignalRow_AndAReplayAddsNone(string code, string slot, string signal, int weight)
    {
        var trucker = Id($"trucker-{signal.ToLowerInvariant()}");
        var order = Id($"order-{signal.ToLowerInvariant()}");
        await SeedUserAsync(trucker, "trucker");
        var candidate = Candidate(trucker, "trucker", code, slot, order, Id("buyer-e"));

        (await Reputation().RecordAsync(candidate, timeoutPenaltiesOn: false, _logger)).Should().Be(ReputationRecordResult.Recorded);
        (await Reputation().RecordAsync(candidate, timeoutPenaltiesOn: false, _logger)).Should().Be(ReputationRecordResult.Replay);

        var row = (await SignalsOf(trucker)).Should().ContainSingle().Subject;
        row["signalType"].S.Should().Be(signal);
        row["weight"].N.Should().Be(weight.ToString(CultureInfo.InvariantCulture));
        row["entityId"].S.Should().Be(order);
        row["entityType"].S.Should().Be("order");
        row.Should().NotContainKey(FraudDetectionService.SourceUserIdAttribute);
        (await Fraud().GetUserRiskScoreAsync(trucker)).Score.Should().Be(weight);
        (await Fraud().HasSignalAsync(trucker, signal, order)).Should().BeTrue();
    }

    // ===== the buyer decline pattern =====

    [LocalDynamoDbFact]
    public async Task TheThirdDeclinedPaidOrder_FlagsTheBuyer_FromTheOrdersBuyerIndex()
    {
        var (farmer, buyer) = (Id("farmer-f"), Id("buyer-f"));
        await SeedUserAsync(farmer, "farmer");
        var now = DateTime.UtcNow;
        await SeedOrderAsync(Id("o1"), buyer, now.AddDays(-12), "declined");
        await SeedOrderAsync(Id("o2"), buyer, now.AddDays(-5), "expired");
        await SeedOrderAsync(Id("o3"), buyer, now.AddHours(-1), "declined");
        await SeedOrderAsync(Id("unpaid"), buyer, now.AddDays(-2), "declined", paid: false);
        await SeedOrderAsync(Id("old"), buyer, now.AddDays(-45), "declined");
        var orders = new OrderRepository(Ddb, _db.Orders);
        // The buyer index is eventually consistent: wait until it shows the seeded rows.
        for (var i = 0; i < 20 && (await orders.QueryOrdersAsync(buyerId: buyer, limit: 50)).Items.Count < 5; i++)
        {
            await Task.Delay(100);
        }

        var candidate = Candidate(farmer, "farmer", ReputationCodes.DeclinedRequest, ReputationSlots.Request, Id("o3"), buyer);
        (await Reputation().RecordAsync(candidate, timeoutPenaltiesOn: false, _logger)).Should().Be(ReputationRecordResult.Recorded);

        var row = (await SignalsOf(buyer)).Should().ContainSingle().Subject;
        row["signalType"].S.Should().Be("BUYER_DECLINE_PATTERN");
        row["entityId"].S.Should().Be(Id("o3"));
        row["description"].S.Should().Contain("3 of the buyer's paid orders in 30 days");
        (await SignalsOf(farmer)).Should().BeEmpty("declining costs the farmer a reputation point, not a fraud signal");
        var score = await Fraud().GetUserRiskScoreAsync(buyer);
        score.Score.Should().Be(10);
        score.Tier.Should().Be(RiskTier.Low);
    }
}
