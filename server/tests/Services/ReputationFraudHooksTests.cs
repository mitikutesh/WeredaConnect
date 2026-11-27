using System.Globalization;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.TestUtilities;
using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Services;

/// <summary>
/// ESC-33 (reputation-architecture.md section 13, fraud-detection-architecture.md section 15.3): the fraud
/// signals a recorded reputation event leads to. <c>NO_SHOW</c> and <c>DELIVERY_OVERDUE</c> with points
/// write the same-named signal on the penalised party; a decline or an expiry checks the buyer decline
/// pattern. Each runs once per event, only after the event committed, and never fails it. The producers
/// of the two P3 events are ESC-34 and ESC-36, so the events are recorded directly here.
/// </summary>
public class ReputationFraudHooksTests
{
    private static readonly DateTime T0 = new(2026, 11, 12, 9, 0, 0, DateTimeKind.Utc);

    private readonly FakeReputationRepository _repo = new();
    private readonly ManualClock _clock = new(T0.AddMinutes(5));
    private readonly IFraudDetectionService _fraud = Substitute.For<IFraudDetectionService>();
    private readonly IOrderRepository _orders = Substitute.For<IOrderRepository>();
    private readonly TestLambdaLogger _logger = new();

    public ReputationFraudHooksTests()
    {
        _repo.AddUser("farmer-1");
        _repo.AddUser("trucker-1");
        BuyerOrders();
    }

    private ReputationService Service(ReputationPolicySettings? settings = null, bool withPatterns = true, bool withFraud = true)
        => new(_repo, settings ?? ReputationPolicySettings.Defaults, withFraud ? _fraud : null, _clock, _ => Task.CompletedTask)
        {
            BuyerPatterns = withPatterns ? new TransactionPatternService(_orders, null, TransactionPatternSettings.Default) : null
        };

    private static ReputationCandidate Candidate(string code, string slot, string user = "trucker-1", string party = "trucker")
        => new()
        {
            UserId = user,
            EventId = slot,
            Code = code,
            Party = party,
            OrderId = slot[(slot.IndexOf('#') + 1)..],
            BuyerId = "buyer-1",
            OccurredAt = T0,
            RequestWindow = TimeSpan.FromHours(24),
            Reachable = true
        };

    private Task<ReputationRecordResult> Record(ReputationCandidate candidate, ReputationService? service = null)
        => (service ?? Service()).RecordAsync(candidate, timeoutPenaltiesOn: true, _logger);

    private static Dictionary<string, object> Order(
        string id, int daysAgo, string? farmerResponse = null, string? truckerResponse = null, bool paid = true,
        string farmer = "farmer-9", string trucker = "trucker-9")
    {
        var createdAt = T0.AddDays(-daysAgo).ToString("o", CultureInfo.InvariantCulture);
        var order = new Dictionary<string, object>
        {
            { "orderId", id }, { "buyerId", "buyer-1" }, { "createdAt", createdAt }, { "farmerId", farmer }, { "truckerId", trucker }
        };
        if (paid)
        {
            order["paymentVerifiedAt"] = createdAt;
        }

        if (farmerResponse != null)
        {
            order["farmerResponse"] = farmerResponse;
        }

        if (truckerResponse != null)
        {
            order["truckerResponse"] = truckerResponse;
        }

        return order;
    }

    private void BuyerOrders(params Dictionary<string, object>[] orders)
        => _orders.QueryOrdersAsync("buyer-1", null, null, null, null, 50, null)
            .Returns((orders.ToList(), (Dictionary<string, AttributeValue>?)null));

    // ===== NO_SHOW and DELIVERY_OVERDUE =====

    [Theory]
    [InlineData(ReputationCodes.NoShow, "NOSHOW#o1", "NO_SHOW", "no-show")]
    [InlineData(ReputationCodes.DeliveryOverdue, "OVERDUE#o1", "DELIVERY_OVERDUE", "did not deliver")]
    public async Task ARecordedReliabilityEvent_WritesItsSignalOnThePenalisedParty(
        string code, string slot, string signal, string text)
    {
        (await Record(Candidate(code, slot))).Should().Be(ReputationRecordResult.Recorded);

        await _fraud.Received(1).RecordFraudSignalAsync(
            "trucker-1", signal, "o1", "order", Arg.Is<string>(d => d.Contains(text) && d.Contains("trucker")));
        // Nothing lands on the buyer or anyone else.
        _fraud.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(IFraudDetectionService.RecordFraudSignalAsync))
            .Should().Be(1);
    }

    [Fact]
    public async Task AFarmerNoShow_LandsOnTheFarmer()
    {
        await Record(Candidate(ReputationCodes.NoShow, "NOSHOW#o1", user: "farmer-1", party: "farmer"));

        await _fraud.Received(1).RecordFraudSignalAsync("farmer-1", "NO_SHOW", "o1", "order", Arg.Any<string?>());
    }

    [Fact]
    public async Task FUP204_AFarmerNoShowReportedByTheTrucker_GoesThroughTheAttributionGuard()
    {
        // The judgement rests on the trucker's own check-in: a signal written at the trucker's request.
        await Record(Candidate(ReputationCodes.NoShow, "NOSHOW#o1", user: "farmer-1", party: "farmer") with { ReportedBy = "trucker-1" });

        await _fraud.Received(1).RecordAttributedSignalAsync(
            "farmer-1", "trucker-1", "NO_SHOW", "o1", "order", Arg.Is<string?>(d => d!.Contains("farmer")));
        await _fraud.DidNotReceive().RecordFraudSignalAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task FUP204_ATruckerNoShow_RestsOnTheTruckerAlone_AndIsWrittenPlainly()
    {
        await Record(Candidate(ReputationCodes.NoShow, "NOSHOW#o1"));

        await _fraud.Received(1).RecordFraudSignalAsync("trucker-1", "NO_SHOW", "o1", "order", Arg.Any<string?>());
        await _fraud.DidNotReceiveWithAnyArgs().RecordAttributedSignalAsync(default!, default!, default!, default!, default!, default);
    }

    [Fact]
    public async Task FUP204_AGuardThatThrows_NeverFailsTheEvent()
    {
        _fraud.RecordAttributedSignalAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<string?>())
            .ThrowsAsync(new ProvisionedThroughputExceededException("slow down"));

        (await Record(Candidate(ReputationCodes.NoShow, "NOSHOW#o1", user: "farmer-1", party: "farmer") with { ReportedBy = "trucker-1" }))
            .Should().Be(ReputationRecordResult.Recorded);
        _logger.Buffer.ToString().Should().Contain($"{ReputationFraudHooks.FailedToken} hook=reliability code=NO_SHOW");
    }

    [Fact]
    public async Task AReplayedEvent_WritesNoSecondSignal()
    {
        var candidate = Candidate(ReputationCodes.NoShow, "NOSHOW#o1");

        await Record(candidate);
        (await Record(candidate)).Should().Be(ReputationRecordResult.Replay);
        (await Record(candidate with { OccurredAt = T0.AddHours(3) })).Should().Be(ReputationRecordResult.Replay);

        await _fraud.Received(1).RecordFraudSignalAsync("trucker-1", "NO_SHOW", "o1", "order", Arg.Any<string?>());
    }

    [Fact]
    public async Task ANoShowWithNoPoints_WritesNoSignal()
    {
        var settings = ReputationPolicySettings.Defaults with { NoShowPoints = 0 };

        (await Record(Candidate(ReputationCodes.NoShow, "NOSHOW#o1"), Service(settings))).Should().Be(ReputationRecordResult.Recorded);

        await _fraud.DidNotReceiveWithAnyArgs().RecordFraudSignalAsync(default!, default!, default, default!, default);
    }

    [Theory]
    [InlineData(ReputationCodes.CancelledAfterAccept, "CANCEL#o1")]
    [InlineData(ReputationCodes.TripCancelledWithBookings, "TRIP_CANCEL#t1")]
    [InlineData(ReputationCodes.OrderCompleted, "COMPLETED#o1")]
    public async Task OtherEvents_WriteNoReliabilitySignal(string code, string slot)
    {
        (await Record(Candidate(code, slot))).Should().Be(ReputationRecordResult.Recorded);

        await _fraud.DidNotReceiveWithAnyArgs().RecordFraudSignalAsync(default!, default!, default, default!, default);
        await _orders.DidNotReceiveWithAnyArgs().QueryOrdersAsync();
    }

    [Fact]
    public async Task ASignalWriteThatFails_NeverFailsTheEvent()
    {
        _fraud.RecordFraudSignalAsync(default!, default!, default, default!, default)
            .ThrowsAsyncForAnyArgs(new InvalidOperationException("put failed"));

        (await Record(Candidate(ReputationCodes.NoShow, "NOSHOW#o1"))).Should().Be(ReputationRecordResult.Recorded);

        _repo.Row("trucker-1", "NOSHOW#o1").Points.Should().Be(5);
        _repo.Users["trucker-1"].Map!.Aggregate.ActivePoints.Should().Be(5);
        _logger.Buffer.ToString().Should().Contain("REPUTATION_FRAUD_HOOK_FAILED hook=reliability code=NO_SHOW [InvalidOperationException]");
    }

    [Fact]
    public async Task ADeletingAccount_GetsNoEventAndNoSignal()
    {
        _repo.Users["trucker-1"].DeletionState = "pending";

        (await Record(Candidate(ReputationCodes.NoShow, "NOSHOW#o1"))).Should().Be(ReputationRecordResult.Skipped);

        await _fraud.DidNotReceiveWithAnyArgs().RecordFraudSignalAsync(default!, default!, default, default!, default);
    }

    [Fact]
    public async Task WithoutAFraudService_TheEventIsRecordedAndNothingElseRuns()
    {
        (await Record(Candidate(ReputationCodes.NoShow, "NOSHOW#o1"), Service(withFraud: false)))
            .Should().Be(ReputationRecordResult.Recorded);
        (await Record(Candidate(ReputationCodes.DeclinedRequest, "REQUEST#o2"), Service(withFraud: false)))
            .Should().Be(ReputationRecordResult.Recorded);

        await _orders.DidNotReceiveWithAnyArgs().QueryOrdersAsync();
    }

    // ===== buyer decline pattern =====

    [Theory]
    [InlineData(ReputationCodes.DeclinedRequest)]
    [InlineData(ReputationCodes.ResponseExpired)]
    public async Task TheThirdDeclinedPaidOrderIn30Days_FlagsTheBuyer(string code)
    {
        BuyerOrders(
            Order("o3", 0, truckerResponse: "declined", trucker: "trucker-1"), // the order just recorded: counted once
            Order("o2", 3, farmerResponse: "declined"),
            Order("o1", 20, truckerResponse: "expired"),
            Order("ok", 5, farmerResponse: "accepted"));

        (await Record(Candidate(code, "REQUEST#o3"))).Should().Be(ReputationRecordResult.Recorded);

        await _fraud.Received(1).RecordFraudSignalAsync("buyer-1", "BUYER_DECLINE_PATTERN", "o3", "order",
            Arg.Is<string>(d => d.StartsWith("3 farmers or truckers") && d.Contains("3 of the buyer's paid orders in 30 days")));
        // The party who declined gets no fraud signal for declining.
        await _fraud.DidNotReceive().RecordFraudSignalAsync("trucker-1", Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task TwoDeclinedPaidOrders_AreBelowTheLimit()
    {
        BuyerOrders(Order("o3", 0, truckerResponse: "declined", trucker: "trucker-1"), Order("o2", 3, farmerResponse: "declined"));

        await Record(Candidate(ReputationCodes.DeclinedRequest, "REQUEST#o3"));

        await _fraud.DidNotReceiveWithAnyArgs().RecordFraudSignalAsync(default!, default!, default, default!, default);
    }

    [Fact]
    public async Task OnePartyDecliningOneBuyerThreeTimes_DoesNotFlagTheBuyer()
    {
        // Three declines, all by the same farmer: that says something about the farmer, not the buyer.
        BuyerOrders(
            Order("o3", 0, farmerResponse: "declined", farmer: "farmer-1"),
            Order("o2", 3, farmerResponse: "declined", farmer: "farmer-1"),
            Order("o1", 9, farmerResponse: "expired", farmer: "farmer-1"));

        await Record(Candidate(ReputationCodes.DeclinedRequest, "REQUEST#o3", user: "farmer-1", party: "farmer"));

        await _fraud.DidNotReceiveWithAnyArgs().RecordFraudSignalAsync(default!, default!, default, default!, default);
    }

    [Fact]
    public async Task ASecondPartyAmongTheDeclines_IsEnough()
    {
        BuyerOrders(
            Order("o3", 0, farmerResponse: "declined", farmer: "farmer-1"),
            Order("o2", 3, farmerResponse: "declined", farmer: "farmer-1"),
            Order("o1", 9, truckerResponse: "declined", farmer: "farmer-1", trucker: "trucker-7"));

        await Record(Candidate(ReputationCodes.DeclinedRequest, "REQUEST#o3", user: "farmer-1", party: "farmer"));

        await _fraud.Received(1).RecordFraudSignalAsync("buyer-1", "BUYER_DECLINE_PATTERN", "o3", "order",
            Arg.Is<string>(d => d.StartsWith("2 farmers or truckers")));
    }

    [Theory]
    [InlineData(false)] // the read shows the recorded order without a verified payment
    [InlineData(null)] // the read does not return the recorded order at all
    public async Task TheRecordedOrder_CountsOnlyWhenTheReadShowsItPaid(bool? paid)
    {
        var history = new List<Dictionary<string, object>>
        {
            Order("o2", 3, farmerResponse: "declined"), Order("o1", 9, truckerResponse: "expired")
        };
        if (paid != null)
        {
            history.Add(Order("o3", 0, truckerResponse: "declined", trucker: "trucker-1", paid: paid.Value));
        }

        BuyerOrders(history.ToArray());

        await Record(Candidate(ReputationCodes.DeclinedRequest, "REQUEST#o3"));

        await _fraud.DidNotReceiveWithAnyArgs().RecordFraudSignalAsync(default!, default!, default, default!, default);
    }

    [Fact]
    public async Task UnpaidOrdersAndOrdersOutsideTheWindow_DoNotCount()
    {
        BuyerOrders(
            Order("o3", 0, truckerResponse: "declined", trucker: "trucker-1"),
            Order("unpaid", 2, farmerResponse: "declined", paid: false),
            Order("accepted", 4, farmerResponse: "accepted", truckerResponse: "accepted"),
            Order("o2", 6, farmerResponse: "declined"),
            Order("old", 31, farmerResponse: "declined"));

        await Record(Candidate(ReputationCodes.DeclinedRequest, "REQUEST#o3"));

        await _fraud.DidNotReceiveWithAnyArgs().RecordFraudSignalAsync(default!, default!, default, default!, default);
    }

    [Fact]
    public async Task ThePolicyNumbers_SetTheLimitAndTheWindow()
    {
        BuyerOrders(Order("o3", 0, truckerResponse: "declined", trucker: "trucker-1"), Order("o2", 40, farmerResponse: "declined"));
        var settings = ReputationPolicySettings.Defaults with { BuyerDeclineSignalCount = 2, BuyerDeclineSignalDays = 60 };

        await Record(Candidate(ReputationCodes.DeclinedRequest, "REQUEST#o3"), Service(settings));

        await _fraud.Received(1).RecordFraudSignalAsync("buyer-1", "BUYER_DECLINE_PATTERN", "o3", "order",
            Arg.Is<string>(d => d.Contains("2 of the buyer's paid orders in 60 days")));
    }

    [Fact]
    public async Task ADeclineWithNoPoints_StillCountsForTheBuyer()
    {
        // The pair cap zeroes the second decline of one buyer: exactly the case a griefing buyer produces.
        BuyerOrders(
            Order("o3", 0, farmerResponse: "declined", farmer: "farmer-1"),
            Order("o2", 1, farmerResponse: "declined"),
            Order("o1", 2, farmerResponse: "declined", farmer: "farmer-1"));
        await Record(Candidate(ReputationCodes.DeclinedRequest, "REQUEST#o1", user: "farmer-1", party: "farmer"));
        _fraud.ClearReceivedCalls();

        await Record(Candidate(ReputationCodes.DeclinedRequest, "REQUEST#o3", user: "farmer-1", party: "farmer"));

        _repo.Row("farmer-1", "REQUEST#o3").Points.Should().Be(0);
        await _fraud.Received(1).RecordFraudSignalAsync("buyer-1", "BUYER_DECLINE_PATTERN", "o3", "order", Arg.Any<string?>());
    }

    [Fact]
    public async Task AHistoryReadThatFails_NeverFailsTheEvent()
    {
        _orders.QueryOrdersAsync("buyer-1", null, null, null, null, 50, null)
            .ThrowsAsync(new ProvisionedThroughputExceededException("busy"));

        (await Record(Candidate(ReputationCodes.DeclinedRequest, "REQUEST#o3"))).Should().Be(ReputationRecordResult.Recorded);

        await _fraud.DidNotReceiveWithAnyArgs().RecordFraudSignalAsync(default!, default!, default, default!, default);
        _logger.Buffer.ToString().Should().Contain("REPUTATION_FRAUD_HOOK_FAILED hook=buyer_decline");
    }

    [Fact]
    public async Task WithoutAnOrdersReader_TheDeclineIsRecordedAndNoHistoryIsRead()
    {
        (await Record(Candidate(ReputationCodes.DeclinedRequest, "REQUEST#o3"), Service(withPatterns: false)))
            .Should().Be(ReputationRecordResult.Recorded);

        await _orders.DidNotReceiveWithAnyArgs().QueryOrdersAsync();
        await _fraud.DidNotReceiveWithAnyArgs().RecordFraudSignalAsync(default!, default!, default, default!, default);
    }

    [Fact]
    public async Task TheHistoryRead_IsBoundedToTwoPagesOfTheBuyersIndex()
    {
        var key = new Dictionary<string, AttributeValue> { { "orderId", new AttributeValue { S = "k" } } };
        var page = Enumerable.Range(0, 50).Select(i => Order($"p-{i}", 1, farmerResponse: "accepted")).ToList();
        _orders.QueryOrdersAsync("buyer-1", null, null, null, null, 50, Arg.Any<Dictionary<string, AttributeValue>?>())
            .Returns((page, key));

        await Record(Candidate(ReputationCodes.DeclinedRequest, "REQUEST#o3"));

        await _orders.Received(2).QueryOrdersAsync("buyer-1", null, null, null, null, 50, Arg.Any<Dictionary<string, AttributeValue>?>());
    }

    // ===== the pure rule =====

    [Theory]
    [InlineData(2, 2, 3, false)]
    [InlineData(3, 2, 3, true)]
    [InlineData(7, 4, 3, true)]
    [InlineData(7, 1, 3, false)] // one party behind every decline
    [InlineData(1, 2, 0, false)]
    public void BuyerDeclinePattern_FiresAtTheLimit_WithTwoParties(int declined, int parties, int limit, bool fires)
        => (TransactionPatternRules.BuyerDeclinePattern(declined, parties, limit, 30) != null).Should().Be(fires);

    // ===== the score =====

    private static RiskScoreResult ScoreOf(bool whitelisted, params (int HoursAgo, string Type, string EntityId)[] rows)
    {
        var now = DateTime.UtcNow;
        var calculator = new RiskScoreCalculator(RiskScoreSettings.Default, now);
        if (whitelisted)
        {
            calculator.Add(new Dictionary<string, AttributeValue>
            {
                ["signalKey"] = new() { S = "~whitelist" },
                ["recordType"] = new() { S = "whitelist" },
                ["whitelistUntil"] = new() { S = now.AddDays(5).ToString("o", CultureInfo.InvariantCulture) }
            });
        }

        foreach (var (hoursAgo, type, entityId) in rows.OrderBy(r => r.HoursAgo))
        {
            var hour = now.AddHours(-hoursAgo).ToString("yyyy-MM-dd'T'HH':00:00.0000000Z'", CultureInfo.InvariantCulture);
            calculator.Add(new Dictionary<string, AttributeValue>
            {
                ["signalKey"] = new() { S = $"{hour}#{type}" },
                ["signalType"] = new() { S = type },
                ["weight"] = new() { N = FraudDetectionService.SignalWeight(type).ToString(CultureInfo.InvariantCulture) },
                ["entityId"] = new() { S = entityId }
            });
        }

        return calculator.Result(truncated: false);
    }

    [Theory]
    [InlineData("NO_SHOW", 15, false, "medium")]
    [InlineData("DELIVERY_OVERDUE", 25, false, "high")]
    [InlineData("BUYER_DECLINE_PATTERN", 10, true, "medium")]
    public void TheNewTypes_CarryTheCatalogueWeightAndClass(string type, int weight, bool soft, string severity)
    {
        FraudDetectionService.SignalWeight(type).Should().Be(weight);
        FraudDetectionService.IsSoftSignal(type).Should().Be(soft);
        FraudDetectionService.SignalSeverity(weight).Should().Be(severity);
        FraudDetectionService.CountsOncePerEntity(type).Should().BeTrue();
        RiskScoreSettings.Default.CapOf(type).Should().Be(50);
    }

    [Fact]
    public void OneOrder_CountsOnceHoweverManyHoursHoldARow()
    {
        ScoreOf(false, (1, "NO_SHOW", "o1"), (9, "NO_SHOW", "o1"), (40, "NO_SHOW", "o1")).Score.Should().Be(15);
    }

    [Fact]
    public void NoShowsAndOverdueDeliveriesTogether_StopAtMedium()
    {
        // Four overdue deliveries and four no-shows: 160 uncapped, 50 under the one cap the two share.
        var rows = Enumerable.Range(1, 4).Select(i => (i * 5, "DELIVERY_OVERDUE", $"d-{i}"))
            .Concat(Enumerable.Range(1, 4).Select(i => (i * 7, "NO_SHOW", $"n-{i}"))).ToArray();

        var result = ScoreOf(false, rows);

        result.Score.Should().Be(50);
        result.Tier.Should().Be(RiskTier.Medium);
    }

    [Fact]
    public void ReliabilitySignals_AddToOtherTypes()
    {
        var result = ScoreOf(false, (1, "DELIVERY_OVERDUE", "d-1"), (2, "DELIVERY_OVERDUE", "d-2"), (3, "SELF_TRADE", "o-9"), (4, "SELF_TRADE", "o-8"));

        result.Score.Should().Be(130);
        result.Tier.Should().Be(RiskTier.Critical);
    }

    [Fact]
    public void AWhitelist_DoesNotSilenceTheHardReliabilityTypes()
    {
        ScoreOf(true, (1, "NO_SHOW", "o1"), (2, "DELIVERY_OVERDUE", "o2")).Score.Should().Be(40);
    }

    [Fact]
    public void TheBuyerDeclinePatternAlone_StopsAtMedium_AndAWhitelistSkipsIt()
    {
        var rows = Enumerable.Range(1, 9).Select(i => (i * 3, "BUYER_DECLINE_PATTERN", $"o-{i}")).ToArray();

        var result = ScoreOf(false, rows);

        result.Score.Should().Be(50, "nine declined orders are 90 uncapped; the pattern cap keeps one buyer at MEDIUM");
        result.Tier.Should().Be(RiskTier.Medium);
        ScoreOf(true, rows).Score.Should().Be(0);
    }
}
