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
/// attribute, the conditional slot rows that hold the caps (in sequence, in parallel, after a claim ran
/// out), a flood attempt by one buyer and by several, the reliability signals written by a
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

    private async Task<List<Dictionary<string, AttributeValue>>> PartitionOf(string userId)
        => (await Ddb.QueryAsync(new QueryRequest
        {
            TableName = _db.FraudSignals,
            KeyConditionExpression = "userId = :u",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue> { [":u"] = new() { S = userId } },
            ConsistentRead = true
        })).Items;

    /// <summary>The user's signal rows (the guard's slot rows left out).</summary>
    private async Task<List<Dictionary<string, AttributeValue>>> SignalsOf(string userId)
        => (await PartitionOf(userId)).Where(r => r["recordType"].S != FraudDetectionService.RecordTypeAttributionSlot).ToList();

    private async Task<List<string>> SlotsOf(string userId)
        => (await PartitionOf(userId)).Where(r => r["recordType"].S == FraudDetectionService.RecordTypeAttributionSlot)
            .Select(r => r["signalKey"].S).OrderBy(k => k, StringComparer.Ordinal).ToList();

    /// <summary>A slot row as the guard writes it, claimed until <paramref name="claimedUntil"/>.</summary>
    private Task SeedSlotAsync(string userId, string slotKey, DateTime claimedUntil)
        => Ddb.PutItemAsync(_db.FraudSignals, new Dictionary<string, AttributeValue>
        {
            ["userId"] = new() { S = userId },
            ["signalKey"] = new() { S = slotKey },
            ["recordType"] = new() { S = FraudDetectionService.RecordTypeAttributionSlot },
            ["claimedUntil"] = new() { S = Iso(claimedUntil) },
            ["expiresAt"] = new() { N = new DateTimeOffset(claimedUntil).AddDays(1).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture) }
        });

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

    private Task SeedOrderAsync(
        string orderId, string buyerId, DateTime createdAt, string? farmerResponse, bool paid = true, string? farmerId = null)
    {
        var item = new Dictionary<string, AttributeValue>
        {
            ["orderId"] = new() { S = orderId },
            ["createdAt"] = new() { S = Iso(createdAt) },
            ["status"] = new() { S = "rejected" },
            ["buyerId"] = new() { S = buyerId },
            ["farmerId"] = new() { S = farmerId ?? Id("farmer") },
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
        (await PartitionOf(buyer)).Should().BeEmpty("the guard writes nothing on the source");
        // The two slot rows the write claimed sit in the trucker's partition, before every signal key.
        (await SlotsOf(trucker)).Should().Equal($"!attr#GPS_ANOMALY#slot#1", $"!attr#GPS_ANOMALY#source#{buyer}");
        // No reader keyed on the window sees them: the score, the existence check.
        var score = await Fraud().GetUserRiskScoreAsync(trucker);
        score.Score.Should().Be(25);
        score.Truncated.Should().BeFalse();
        (await Fraud().HasSignalAsync(trucker, "GPS_ANOMALY", Id("del-1"))).Should().BeTrue();
        (await Fraud().HasSignalAsync(trucker, "GPS_ANOMALY", Id("del-other"))).Should().BeFalse();
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
    public async Task ManyBuyersTogether_StopAtTwoRows_SoTheTruckerStaysAtMediumOnThem()
    {
        var trucker = Id("trucker-c");
        for (var i = 1; i <= 2; i++)
        {
            await SeedSignalAsync(trucker, "GPS_ANOMALY", Id($"del-{i}"), hoursAgo: i * 10, source: Id($"buyer-c{i}"));
        }

        var outcomes = new List<AttributedSignalOutcome>();
        for (var i = 3; i <= 12; i++)
        {
            outcomes.Add(await Fraud().RecordAttributedSignalAsync(trucker, Id($"buyer-c{i}"), "GPS_ANOMALY", Id($"del-{i}"), "delivery", "why"));
        }

        outcomes.Should().OnlyContain(o => o == AttributedSignalOutcome.TargetCapped);
        (await SignalsOf(trucker)).Should().HaveCount(2);
        var score = await Fraud().GetUserRiskScoreAsync(trucker);
        score.Score.Should().Be(50);
        score.Tier.Should().Be(RiskTier.Medium);
        score.Truncated.Should().BeFalse();
    }

    [LocalDynamoDbFact]
    public async Task ConfirmsThatAllReadBelowTheCap_AreStoppedByTheSlots()
    {
        // The straddling race, made certain: the first buyer's row is written; the second buyer's is held back by
        // the hour limit but keeps its slot; so a third buyer's read still shows ONE row, below the cap of two.
        // Only the conditional slot writes stop it.
        var trucker = Id("trucker-race");
        (await Fraud().RecordAttributedSignalAsync(trucker, Id("buyer-r1"), "GPS_ANOMALY", Id("del-1"), "delivery", "why"))
            .Should().Be(AttributedSignalOutcome.Written);
        (await Fraud().RecordAttributedSignalAsync(trucker, Id("buyer-r2"), "GPS_ANOMALY", Id("del-2"), "delivery", "why"))
            .Should().Be(AttributedSignalOutcome.HourLimited);

        (await SignalsOf(trucker)).Should().ContainSingle();
        (await Fraud().RecordAttributedSignalAsync(trucker, Id("buyer-r3"), "GPS_ANOMALY", Id("del-3"), "delivery", "why"))
            .Should().Be(AttributedSignalOutcome.TargetCapped);
        // The same in parallel: ten more buyers at once, none gets through.
        var parallel = await Task.WhenAll(Enumerable.Range(4, 10).Select(i =>
            Fraud().RecordAttributedSignalAsync(trucker, Id($"buyer-r{i}"), "GPS_ANOMALY", Id($"del-{i}"), "delivery", "why")));

        parallel.Should().OnlyContain(o => o == AttributedSignalOutcome.TargetCapped);
        (await SignalsOf(trucker)).Should().ContainSingle();
        (await Fraud().GetUserRiskScoreAsync(trucker)).Score.Should().Be(25);
    }

    [LocalDynamoDbFact]
    public async Task ParallelConfirmsOnACleanTrucker_NeverWriteMoreThanTheCap()
    {
        var trucker = Id("trucker-par");

        var outcomes = await Task.WhenAll(Enumerable.Range(1, 12).Select(i =>
            Fraud().RecordAttributedSignalAsync(trucker, Id($"buyer-p{i}"), "GPS_ANOMALY", Id($"del-{i}"), "delivery", "why")));

        // One hour bucket here (two when the test straddles the hour), so one row; what matters is that at
        // most two requests held a slot.
        outcomes.Count(o => o == AttributedSignalOutcome.Written).Should().BeInRange(1, FraudDetectionService.AttributedPerTargetCap);
        outcomes.Count(o => o is AttributedSignalOutcome.Written or AttributedSignalOutcome.HourLimited)
            .Should().BeLessThanOrEqualTo(FraudDetectionService.AttributedPerTargetCap);
        (await SignalsOf(trucker)).Count.Should().BeInRange(1, FraudDetectionService.AttributedPerTargetCap);
        (await SlotsOf(trucker)).Count(k => k.Contains("#slot#")).Should().Be(FraudDetectionService.AttributedPerTargetCap);
    }

    [LocalDynamoDbFact]
    public async Task ASlotWhoseClaimRanOut_IsClaimedAgain_AndALiveOneIsNot()
    {
        var (trucker, buyer) = (Id("trucker-exp"), Id("buyer-exp"));
        await SeedSlotAsync(trucker, "!attr#GPS_ANOMALY#slot#1", DateTime.UtcNow.AddDays(3));
        await SeedSlotAsync(trucker, "!attr#GPS_ANOMALY#slot#2", DateTime.UtcNow.AddMinutes(-1));
        await SeedSlotAsync(trucker, $"!attr#GPS_ANOMALY#source#{buyer}", DateTime.UtcNow.AddMinutes(-1));

        (await Fraud().RecordAttributedSignalAsync(trucker, buyer, "GPS_ANOMALY", Id("del-1"), "delivery", "why"))
            .Should().Be(AttributedSignalOutcome.Written);
        (await Fraud().RecordAttributedSignalAsync(trucker, Id("buyer-exp2"), "GPS_ANOMALY", Id("del-2"), "delivery", "why"))
            .Should().Be(AttributedSignalOutcome.TargetCapped);

        (await SignalsOf(trucker)).Should().ContainSingle();
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

    [LocalDynamoDbFact]
    public async Task TheCallersOwnSignal_NeedsNoGuardAndNoSlot()
    {
        var buyer = Id("buyer-self");

        await Fraud().RecordFraudSignalAsync(buyer, "GPS_ANOMALY", Id("del-1"), "delivery", "why");

        var row = (await PartitionOf(buyer)).Should().ContainSingle().Subject;
        row["signalType"].S.Should().Be("GPS_ANOMALY");
        row.Should().NotContainKey(FraudDetectionService.SourceUserIdAttribute);
        (await Fraud().GetUserRiskScoreAsync(buyer)).Score.Should().Be(25);
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
        await SeedOrderAsync(Id("o3"), buyer, now.AddHours(-1), "declined", farmerId: farmer);
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
        row["description"].S.Should().StartWith("2 farmers or truckers").And.Contain("3 of the buyer's paid orders in 30 days");
        (await SignalsOf(farmer)).Should().BeEmpty("declining costs the farmer a reputation point, not a fraud signal");
        var score = await Fraud().GetUserRiskScoreAsync(buyer);
        score.Score.Should().Be(10);
        score.Tier.Should().Be(RiskTier.Low);
    }

    [LocalDynamoDbFact]
    public async Task OneFarmerDecliningOneBuyerThreeTimes_FlagsNobody()
    {
        var (farmer, buyer) = (Id("farmer-g"), Id("buyer-g"));
        await SeedUserAsync(farmer, "farmer");
        var now = DateTime.UtcNow;
        await SeedOrderAsync(Id("g1"), buyer, now.AddDays(-12), "declined", farmerId: farmer);
        await SeedOrderAsync(Id("g2"), buyer, now.AddDays(-5), "expired", farmerId: farmer);
        await SeedOrderAsync(Id("g3"), buyer, now.AddHours(-1), "declined", farmerId: farmer);
        var orders = new OrderRepository(Ddb, _db.Orders);
        for (var i = 0; i < 20 && (await orders.QueryOrdersAsync(buyerId: buyer, limit: 50)).Items.Count < 3; i++)
        {
            await Task.Delay(100);
        }

        var candidate = Candidate(farmer, "farmer", ReputationCodes.DeclinedRequest, ReputationSlots.Request, Id("g3"), buyer);
        (await Reputation().RecordAsync(candidate, timeoutPenaltiesOn: false, _logger)).Should().Be(ReputationRecordResult.Recorded);

        (await PartitionOf(buyer)).Should().BeEmpty();
    }
}
