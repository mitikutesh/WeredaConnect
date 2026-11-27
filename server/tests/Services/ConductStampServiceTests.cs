using Amazon.Lambda.TestUtilities;
using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.Services;
using WeredaConnect.Server.Tests.Helpers;
using Xunit;

namespace WeredaConnect.Server.Tests.Services;

/// <summary>An in-memory <see cref="IConductStampRepository"/> with the table's conditions.</summary>
internal sealed class FakeConductStamps : IConductStampRepository
{
    public Dictionary<(string UserId, string OrderId), ConductStamp> Rows { get; } = new();
    public HashSet<string> Deleting { get; } = new();
    public int Gets { get; private set; }
    public int Puts { get; private set; }
    public Exception? ThrowOnPut { get; set; }
    public int ThrowTimes { get; set; } = int.MaxValue;

    /// <summary>GCR-04: the figure's reads, and a failure to throw from them.</summary>
    public List<(string UserId, DateTime Since, int Cap)> CountedQueries { get; } = new();
    public Exception? ThrowOnCountedQuery { get; set; }

    public Task<IReadOnlyList<ConductStamp>> QueryStampsAsync(string userId, int cap)
        => Task.FromResult<IReadOnlyList<ConductStamp>>(Rows.Values.Where(r => r.UserId == userId).Take(cap).ToList());

    /// <summary>The table's filter: this user's counted stamps after <paramref name="sinceUtc"/>, then the cap.</summary>
    public Task<(IReadOnlyList<ConductStamp> Rows, bool Truncated)> QueryCountedStampsAsync(string userId, DateTime sinceUtc, int cap)
    {
        CountedQueries.Add((userId, sinceUtc, cap));
        if (ThrowOnCountedQuery != null)
        {
            throw ThrowOnCountedQuery;
        }

        var matching = Rows.Values.Where(r => r.UserId == userId && r.Counted && r.OccurredAt > sinceUtc).ToList();
        return Task.FromResult<(IReadOnlyList<ConductStamp>, bool)>((matching.Take(cap).ToList(), matching.Count > cap));
    }

    /// <summary>GCR-07: the users whose one-stamp read throws.</summary>
    public HashSet<string> ThrowOnGetFor { get; } = new();

    public Task<ConductStamp?> GetStampAsync(string userId, string orderId)
    {
        Gets++;
        if (ThrowOnGetFor.Contains(userId))
        {
            throw new InvalidOperationException("The stamp read failed.");
        }

        return Task.FromResult(Rows.TryGetValue((userId, orderId), out var row) ? row : null);
    }

    public Task<ConductWriteOutcome> PutNewAsync(ConductStamp stamp)
    {
        Puts++;
        if (ThrowOnPut != null && ThrowTimes-- > 0)
        {
            throw ThrowOnPut;
        }

        if (Rows.ContainsKey((stamp.UserId, stamp.OrderId)))
        {
            return Task.FromResult(ConductWriteOutcome.Exists);
        }

        if (Deleting.Contains(stamp.UserId))
        {
            return Task.FromResult(ConductWriteOutcome.Skipped);
        }

        Rows[(stamp.UserId, stamp.OrderId)] = stamp;
        return Task.FromResult(ConductWriteOutcome.Written);
    }

    public Task<ConductWriteOutcome> ReplaceOnceAsync(ConductStamp stamp)
    {
        if (!Rows.TryGetValue((stamp.UserId, stamp.OrderId), out var existing) || existing.ReevaluatedAt != null)
        {
            return Task.FromResult(ConductWriteOutcome.Exists);
        }

        Rows[(stamp.UserId, stamp.OrderId)] = stamp;
        return Task.FromResult(ConductWriteOutcome.Written);
    }
}

/// <summary>
/// GCR-03: the conduct stamp on settle (one row per party, settling twice writes one row per party, a failed write
/// never throws) and the one re-evaluation a late rating or photo causes.
/// </summary>
public class ConductStampServiceTests
{
    private static readonly DateTime StampedAt = new(2026, 11, 20, 9, 5, 0, DateTimeKind.Utc);

    private readonly FakeConductStamps _stamps = new();
    private readonly IDeliveryRepository _deliveries = Substitute.For<IDeliveryRepository>();
    private readonly IOrderRepository _orders = Substitute.For<IOrderRepository>();
    private readonly IRatingRepository _ratings = Substitute.For<IRatingRepository>();
    private readonly ITripLocationRepository _pings = Substitute.For<ITripLocationRepository>();
    private readonly IFraudDetectionService _fraud = Substitute.For<IFraudDetectionService>();
    private readonly ManualClock _clock = new(StampedAt);
    private readonly TestLambdaLogger _logger = new();

    public ConductStampServiceTests()
    {
        _deliveries.GetByIdAsync("del-1", true).Returns(ConductScoringTests.Delivery());
        _ratings.GetByIdAsync(Arg.Any<string>(), true).Returns((Dictionary<string, object>?)null);
        // Pings every 40 minutes from the pickup (06:59) to the drop-off (14:59, phone times).
        var pickup = new DateTime(2026, 11, 7, 7, 0, 0, DateTimeKind.Utc);
        var rows = Enumerable.Range(0, 12)
            .Select(i => new TripLocationRow("trip-1", $"{LocationPingRules.FormatUtc(pickup.AddMinutes(40 * i))}#fix", "trucker-1",
                9.0, 38.7, 15, "hourly", false, null, LocationPingRules.FormatUtc(pickup.AddMinutes(40 * i)), 0))
            .ToList();
        _pings.ListBetweenAsync("trip-1", Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>())
            .Returns(new TripLocationWindow(rows, true));
    }

    private ConductStampService Service(bool withFraud = true)
        => new(_stamps, _deliveries, _orders, _ratings, _pings, withFraud ? _fraud : null, _clock, _ => Task.CompletedTask);

    private void Rated(params string[] rateeIds)
    {
        foreach (var ratee in rateeIds)
        {
            _ratings.GetByIdAsync($"order-1:{ratee}", true)
                .Returns(new Dictionary<string, object> { ["ratingId"] = $"order-1:{ratee}" });
        }
    }

    private void OrderRowReturns(ConductOrder order)
        => _orders.GetByIdAsync(order.OrderId, true).Returns(new Dictionary<string, object>
        {
            ["orderId"] = order.OrderId, ["farmerId"] = order.FarmerId, ["buyerId"] = order.BuyerId,
            ["truckerId"] = order.TruckerId ?? "", ["tripId"] = order.TripId ?? "",
            ["fulfillment"] = order.SelfPickup ? "self_pickup" : "trucker", ["activeDeliveryId"] = "del-1",
            ["orderTotal"] = "1200", ["status"] = "settled", ["updatedAt"] = "2026-11-25T00:00:00.000Z",
            ["pickupDate"] = "2026-11-07T06:00:00.000Z", ["deliveryLatitude"] = "9.0", ["deliveryLongitude"] = "38.8"
        });

    [Fact]
    public async Task Settle_WritesOneRowPerParty_WithItsSteps()
    {
        Rated("farmer-1", "trucker-1");

        var written = await Service().StampAsync(ConductScoringTests.Order(), _logger);

        written.Should().Be(3);
        _stamps.Rows.Keys.Should().BeEquivalentTo(new[] { ("farmer-1", "order-1"), ("trucker-1", "order-1"), ("buyer-1", "order-1") });
        var trucker = _stamps.Rows[("trucker-1", "order-1")];
        trucker.Party.Should().Be("trucker");
        (trucker.Offered, trucker.Earned).Should().Be((5, 5), "the pings cover the trip");
        trucker.Counted.Should().BeTrue();
        trucker.RecordedAt.Should().Be(StampedAt);
        trucker.OccurredAt.Should().Be(ConductScoringTests.Order().SettledAt);
        _stamps.Rows[("buyer-1", "order-1")].Earned.Should().Be(7);
        _logger.Buffer.ToString().Should().Contain(ConductStampService.StampedToken);
    }

    [Fact]
    public async Task SelfPickup_WritesFarmerAndBuyerRowsOnly()
    {
        (await Service().StampAsync(ConductScoringTests.Order(selfPickup: true), _logger)).Should().Be(2);
        _stamps.Rows.Keys.Select(k => k.UserId).Should().BeEquivalentTo("farmer-1", "buyer-1");
        await _pings.DidNotReceiveWithAnyArgs().ListBetweenAsync(default!, default!, default!, default);
    }

    [Fact]
    public async Task SettlingTwice_WritesOneRowPerParty()
    {
        var service = Service();
        await service.StampAsync(ConductScoringTests.Order(), _logger);
        var first = _stamps.Rows.ToDictionary(kv => kv.Key, kv => kv.Value);
        Rated("farmer-1", "trucker-1");

        (await service.StampAsync(ConductScoringTests.Order(), _logger)).Should().Be(0);
        (await service.TryStampAsync(ConductScoringTests.Order(), _logger)).Should().Be(0);

        _stamps.Rows.Should().HaveCount(3);
        _stamps.Rows.Should().BeEquivalentTo(first, "a replay never rewrites a stamp");
    }

    [Fact]
    public async Task DeletingAccount_GetsNoRow_TheOthersDo()
    {
        _stamps.Deleting.Add("buyer-1");
        (await Service().StampAsync(ConductScoringTests.Order(), _logger)).Should().Be(2);
        _stamps.Rows.Keys.Select(k => k.UserId).Should().NotContain("buyer-1");
        _logger.Buffer.ToString().Should().Contain(ConductStampService.SkippedToken);
    }

    [Fact]
    public async Task FailedWrite_NeverThrows_AndIsLogged()
    {
        _stamps.ThrowOnPut = new InvalidOperationException("throttled");

        var written = await Service().TryStampAsync(ConductScoringTests.Order(), _logger);

        written.Should().Be(0);
        _stamps.Puts.Should().Be(ConductStampService.MaxAttempts);
        _logger.Buffer.ToString().Should().Contain(ConductStampService.FailedToken).And.Contain("gaveUp=true");
    }

    [Fact]
    public async Task FailedRead_NeverThrows()
    {
        _deliveries.GetByIdAsync("del-1", true).ThrowsAsync(new InvalidOperationException("down"));
        (await Service().TryStampAsync(ConductScoringTests.Order(), _logger)).Should().Be(0);
        _stamps.Rows.Should().BeEmpty();
    }

    [Fact]
    public async Task AFailureOnce_IsRetried_AndTheRowsLand()
    {
        _stamps.ThrowOnPut = new InvalidOperationException("throttled");
        _stamps.ThrowTimes = 1;

        (await Service().TryStampAsync(ConductScoringTests.Order(), _logger)).Should().Be(3);
        _stamps.Rows.Should().HaveCount(3);
    }

    [Fact]
    public async Task FraudSignal_OnTheBuyer_NoOrderCounts_WithoutTheTableNoneCounts()
    {
        _fraud.HasSignalAsync("buyer-1", FraudDetectionService.SelfTradeSignal, "order-1").Returns(true);
        await Service().StampAsync(ConductScoringTests.Order(), _logger);
        _stamps.Rows.Values.Should().OnlyContain(r => !r.Counted && r.NotCountedReason == ConductScoring.NotCountedReasons.FraudSignal);

        _stamps.Rows.Clear();
        await Service(withFraud: false).StampAsync(ConductScoringTests.Order(), _logger);
        _stamps.Rows.Values.Should().OnlyContain(r => r.NotCountedReason == ConductScoring.NotCountedReasons.FraudUnchecked);
    }

    [Fact]
    public async Task ThirdOrderOfAPairIn30Days_IsStampedButNotCounted()
    {
        var service = Service();
        var order = ConductScoringTests.Order();
        await service.StampAsync(order with { OrderId = "order-a", SettledAt = order.SettledAt.AddDays(-10) }, _logger);
        await service.StampAsync(order with { OrderId = "order-b", SettledAt = order.SettledAt.AddDays(-5) }, _logger);

        await service.StampAsync(order, _logger);

        var third = _stamps.Rows[("farmer-1", "order-1")];
        third.Counted.Should().BeFalse();
        third.NotCountedReason.Should().Be(ConductScoring.NotCountedReasons.PairCap);
        third.Offered.Should().Be(2, "a row is written either way");
    }

    [Fact]
    public async Task LateRating_ReevaluatesOnce()
    {
        var order = ConductScoringTests.Order();
        OrderRowReturns(order);
        var service = Service();
        await service.StampAsync(order, _logger);
        var buyerRow = _stamps.Rows[("buyer-1", "order-1")];
        buyerRow.Steps.Single(s => s.Id == ConductScoring.Steps.RatedParties).Result.Should().Be(ConductScoring.Results.Missed);

        Rated("farmer-1", "trucker-1");
        _clock.Now = _clock.Now.Add(TimeSpan.FromDays(3));
        var written = await service.TryReevaluateAsync("order-1", new[] { "buyer-1", "farmer-1", "trucker-1" }, _logger);

        written.Should().Be(3);
        var after = _stamps.Rows[("buyer-1", "order-1")];
        after.Steps.Single(s => s.Id == ConductScoring.Steps.RatedParties).Result.Should().Be(ConductScoring.Results.Earned);
        after.Earned.Should().Be(buyerRow.Earned + 1);
        after.ReevaluatedAt.Should().Be(StampedAt.AddDays(3));
        after.RecordedAt.Should().Be(StampedAt, "the window stays anchored at the first stamp");
        after.OccurredAt.Should().Be(order.SettledAt, "the settle time stays, whatever the row's updatedAt says now");
        after.Version.Should().Be(2);
        _logger.Buffer.ToString().Should().Contain(ConductStampService.ReevaluatedToken);

        // A second late arrival does nothing.
        _deliveries.GetByIdAsync("del-1", true).Returns(ConductScoringTests.Delivery(("dropoffMethod", "code")));
        (await service.TryReevaluateAsync("order-1", new[] { "buyer-1", "farmer-1", "trucker-1" }, _logger)).Should().Be(0);
        _stamps.Rows[("buyer-1", "order-1")].Should().Be(after);
    }

    [Fact]
    public async Task LateEvidence_After14Days_DoesNothing()
    {
        var order = ConductScoringTests.Order();
        OrderRowReturns(order);
        var service = Service();
        await service.StampAsync(order, _logger);
        Rated("farmer-1", "trucker-1");
        _clock.Now = _clock.Now.Add(TimeSpan.FromDays(14) + TimeSpan.FromMinutes(1));

        (await service.TryReevaluateAsync("order-1", new[] { "buyer-1", "farmer-1", "trucker-1" }, _logger)).Should().Be(0);
        await _orders.DidNotReceiveWithAnyArgs().GetByIdAsync(default!, default);
    }

    [Fact]
    public async Task LateEvidence_BeforeTheSettle_ReadsOneStampOnly()
    {
        (await Service().TryReevaluateAsync("order-1", new[] { "trucker-1", "farmer-1", "buyer-1" }, _logger)).Should().Be(0);
        _stamps.Gets.Should().Be(1);
        await _orders.DidNotReceiveWithAnyArgs().GetByIdAsync(default!, default);
    }

    [Fact]
    public async Task Reevaluation_ThatFails_NeverThrows()
    {
        var order = ConductScoringTests.Order();
        var service = Service();
        await service.StampAsync(order, _logger);
        _orders.GetByIdAsync("order-1", true).ThrowsAsync(new InvalidOperationException("down"));

        (await service.TryReevaluateAsync("order-1", new[] { "buyer-1" }, _logger)).Should().Be(0);
        _logger.Buffer.ToString().Should().Contain(ConductStampService.FailedToken).And.Contain("step=reevaluate");
    }

    [Fact]
    public async Task ADeliveryOfAnotherOrder_IsNoEvidence()
    {
        _deliveries.GetByIdAsync("del-1", true).Returns(ConductScoringTests.Delivery(("orderId", "order-9")));
        await Service().StampAsync(ConductScoringTests.Order(), _logger);
        _stamps.Rows[("trucker-1", "order-1")].Offered.Should().Be(0);
    }
}
