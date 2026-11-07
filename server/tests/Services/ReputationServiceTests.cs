using Amazon.Lambda.TestUtilities;
using FluentAssertions;
using NSubstitute;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Services;

/// <summary>
/// ESC-28: <see cref="ReputationService.RecordAsync"/> and the transaction it plans
/// (reputation-architecture.md 6.1, 6.2), against an in-memory table with the real conditions:
/// a replay never counts twice, two writers for one user serialize on the map's version, the
/// limits of 5.2 and 5.8 hold in any arrival order, a deleting account is skipped, restrictions,
/// tier rows, earn-back and the read-lag check.
/// </summary>
public class ReputationServiceTests
{
    private static readonly DateTime T0 = new(2026, 11, 7, 9, 0, 0, DateTimeKind.Utc);

    private readonly FakeReputationRepository _repo = new();
    private readonly ManualClock _clock = new(T0.AddMinutes(5));
    private readonly IFraudDetectionService _fraud = Substitute.For<IFraudDetectionService>();
    private readonly TestLambdaLogger _logger = new();

    public ReputationServiceTests()
    {
        _repo.AddUser("farmer-1");
    }

    private ReputationService Service(ReputationPolicySettings? settings = null)
        => new(_repo, settings ?? ReputationPolicySettings.Defaults, _fraud, _clock, _ => Task.CompletedTask);

    private static ReputationCandidate Candidate(
        string code,
        string slot,
        DateTime at,
        string buyer = "buyer-1",
        string user = "farmer-1",
        decimal? total = null,
        string? pickupDate = null)
        => new()
        {
            UserId = user,
            EventId = slot,
            Code = code,
            Party = "farmer",
            OrderId = slot[(slot.IndexOf('#') + 1)..],
            BuyerId = buyer,
            OccurredAt = at,
            RequestWindow = TimeSpan.FromHours(24),
            Reachable = true,
            OrderTotal = total,
            PickupDate = pickupDate
        };

    private Task<ReputationRecordResult> Record(ReputationCandidate candidate, ReputationService? service = null)
        => (service ?? Service()).RecordAsync(candidate, timeoutPenaltiesOn: true, _logger);

    // ===== replays =====

    [Fact]
    public async Task ARecordedSlot_NeverCountsTwice()
    {
        var candidate = Candidate(ReputationCodes.CancelledAfterAccept, "CANCEL#o1", T0);

        (await Record(candidate)).Should().Be(ReputationRecordResult.Recorded);
        (await Record(candidate)).Should().Be(ReputationRecordResult.Replay);
        (await Record(candidate with { OccurredAt = T0.AddHours(1) })).Should().Be(ReputationRecordResult.Replay);

        _repo.RowsOf("farmer-1").Where(r => r.Code == ReputationCodes.CancelledAfterAccept).Should().ContainSingle();
        _repo.Committed.Should().ContainSingle();
        _repo.Users["farmer-1"].Map!.Aggregate.ActivePoints.Should().Be(3);
    }

    [Fact]
    public async Task ASlotWrittenBetweenTheReadAndTheCommit_IsAReplay()
    {
        var candidate = Candidate(ReputationCodes.DeclinedRequest, "REQUEST#o1", T0);
        var other = Service();
        _repo.BeforeCommit = async _ => await other.RecordAsync(candidate, true, _logger);

        (await Record(candidate)).Should().Be(ReputationRecordResult.Replay);

        _repo.RowsOf("farmer-1").Count(r => r.EventId == "REQUEST#o1").Should().Be(1);
        _repo.Users["farmer-1"].Map!.Version.Should().Be(1);
    }

    // ===== concurrency =====

    [Fact]
    public async Task TwoConcurrentPenalties_TheLoserRetriesAndSeesTheWinner()
    {
        var first = Candidate(ReputationCodes.DeclinedRequest, "REQUEST#o1", T0, buyer: "buyer-1");
        var second = Candidate(ReputationCodes.DeclinedRequest, "REQUEST#o2", T0.AddHours(1), buyer: "buyer-1");
        var concurrent = Service();
        _repo.BeforeCommit = async _ => (await concurrent.RecordAsync(second, true, _logger)).Should().Be(ReputationRecordResult.Recorded);

        (await Record(first)).Should().Be(ReputationRecordResult.Recorded);

        // The first commit lost the version check; its retry read the second and gave the pair cap.
        _repo.Attempts.Should().Be(3);
        _repo.Row("farmer-1", "REQUEST#o2").Points.Should().Be(1);
        var loser = _repo.Row("farmer-1", "REQUEST#o1");
        loser.Points.Should().Be(0);
        loser.ZeroReason.Should().Be(ReputationZeroReasons.PairCap);
        _repo.Users["farmer-1"].Map!.Version.Should().Be(2);
        _repo.Users["farmer-1"].Map!.Aggregate.ActivePoints.Should().Be(1);
    }

    [Fact]
    public async Task AVersionThatKeepsMoving_ThrowsAfterFiveAttempts()
    {
        var repo = Substitute.For<IReputationEventRepository>();
        repo.GetUserStateAsync("farmer-1").Returns(new ReputationUserState { UserId = "farmer-1", Role = "farmer" });
        repo.QueryEventsAsync("farmer-1", Arg.Any<int>()).Returns((Array.Empty<ReputationEvent>(), false));
        repo.CommitAsync(Arg.Any<ReputationWrite>()).Returns(ReputationCommitOutcome.Conflict);
        var service = new ReputationService(repo, ReputationPolicySettings.Defaults, _fraud, _clock, _ => Task.CompletedTask);

        var act = () => service.RecordAsync(Candidate(ReputationCodes.DeclinedRequest, "REQUEST#o1", T0), true, _logger);

        await act.Should().ThrowAsync<ReputationConflictException>();
        await repo.Received(ReputationService.MaxAttempts).CommitAsync(Arg.Any<ReputationWrite>());
    }

    // ===== skipped =====

    [Fact]
    public async Task ADeletingAccount_OrNoRow_IsSkipped()
    {
        _repo.Users["farmer-1"].DeletionState = "in_progress";

        (await Record(Candidate(ReputationCodes.CancelledAfterAccept, "CANCEL#o1", T0))).Should().Be(ReputationRecordResult.Skipped);
        (await Record(Candidate(ReputationCodes.CancelledAfterAccept, "CANCEL#o1", T0, user: "nobody"))).Should().Be(ReputationRecordResult.Skipped);

        _repo.Committed.Should().BeEmpty();
        _logger.Buffer.ToString().Should().Contain("REPUTATION_SKIPPED reason=deleting").And.Contain("REPUTATION_SKIPPED reason=no_user");
    }

    [Fact]
    public async Task ASuspendedParty_GetsNoPointsForAnExpiry()
    {
        _repo.Users["farmer-1"].Suspended = true;

        await Record(Candidate(ReputationCodes.ResponseExpired, "REQUEST#o1", T0));

        var row = _repo.Row("farmer-1", "REQUEST#o1");
        row.Points.Should().Be(0);
        row.ZeroReason.Should().Be(ReputationZeroReasons.Suspended);
    }

    // ===== the row, the map, the tier and the restriction =====

    [Fact]
    public async Task ThePenaltyRow_CarriesItsExpiry_DueKeys_TtlAndThePointsAfter()
    {
        await Record(Candidate(ReputationCodes.CancelledAfterAccept, "CANCEL#o1", T0, pickupDate: "2026-11-20T06:00:00.000Z"));

        var row = _repo.Row("farmer-1", "CANCEL#o1");
        row.PointsExpireAt.Should().Be(T0.AddDays(90));
        row.DueDay.Should().Be("2027-02-05");
        row.DueAt.Should().Be(ReputationPolicy.Iso(T0.AddDays(90)));
        row.RecordedAt.Should().Be(_clock.Now);
        row.PurgeAt.Should().Be(new DateTimeOffset(_clock.Now.AddDays(400)).ToUnixTimeSeconds());
        row.ActivePointsAfter.Should().Be(3);
        row.Status.Should().Be(ReputationStatuses.Active);

        var map = _repo.Users["farmer-1"].Map!;
        map.Aggregate.Tier.Should().Be(ReputationTiers.Warning);
        map.LastEventId.Should().Be("CANCEL#o1");
        map.Aggregate.NextChangeAt.Should().Be(T0.AddDays(90));
    }

    [Fact]
    public async Task ATierChange_WritesATierRow_AndAPublicChange_AHealMarker()
    {
        await Record(Candidate(ReputationCodes.CancelledAfterAccept, "CANCEL#o1", T0));

        var tier = _repo.RowsOf("farmer-1").Single(r => r.Code == ReputationCodes.TierChanged);
        tier.FromTier.Should().Be(ReputationTiers.Good);
        tier.ToTier.Should().Be(ReputationTiers.Warning);
        // Warning still reads as good to buyers: no public change, no heal marker.
        _repo.RowsOf("farmer-1").Should().NotContain(r => r.Code == ReputationCodes.Heal);

        await Record(Candidate(ReputationCodes.LateCancel, "CANCEL#o2", T0.AddHours(1)));
        _repo.RowsOf("farmer-1").Count(r => r.Code == ReputationCodes.TierChanged).Should().Be(2);
        _repo.Users["farmer-1"].Map!.Aggregate.Tier.Should().Be(ReputationTiers.Low);
        _repo.RowsOf("farmer-1").Should().Contain(r => r.Code == ReputationCodes.Heal && r.DueDay == ReputationPolicy.Day(_clock.Now));
    }

    [Fact]
    public async Task CrossingTen_StartsA14DayRestriction_OnlyOnce()
    {
        await Record(Candidate(ReputationCodes.CancelledAfterAccept, "CANCEL#o1", T0));
        await Record(Candidate(ReputationCodes.CancelledAfterAccept, "CANCEL#o2", T0.AddHours(1)));
        await Record(Candidate(ReputationCodes.CancelledAfterAccept, "CANCEL#o3", T0.AddHours(2)));
        _repo.RowsOf("farmer-1").Should().NotContain(r => r.Code == ReputationCodes.Restriction);

        await Record(Candidate(ReputationCodes.CancelledAfterAccept, "CANCEL#o4", T0.AddHours(3)));

        var restriction = _repo.RowsOf("farmer-1").Single(r => r.Code == ReputationCodes.Restriction);
        restriction.Cause.Should().Be(ReputationCauses.Points);
        restriction.Since.Should().Be(_clock.Now);
        restriction.Until.Should().Be(_clock.Now.AddDays(14));
        restriction.DueAt.Should().Be(ReputationPolicy.Iso(_clock.Now.AddDays(14)));
        var map = _repo.Users["farmer-1"].Map!;
        map.Aggregate.Tier.Should().Be(ReputationTiers.Restricted);
        map.Aggregate.Restriction!.Until.Should().Be(_clock.Now.AddDays(14));
        _repo.RowsOf("farmer-1").Single(r => r.Code == ReputationCodes.TierChanged && r.ToTier == ReputationTiers.Restricted)
            .RestrictedUntil.Should().Be(_clock.Now.AddDays(14));

        // Points during an active restriction do not extend it.
        await Record(Candidate(ReputationCodes.LateCancel, "CANCEL#o5", T0.AddHours(4)));
        _repo.RowsOf("farmer-1").Count(r => r.Code == ReputationCodes.Restriction).Should().Be(1);
    }

    [Fact]
    public async Task DeliveryOverdue_OpensAReview_WithARestrictionThatHasNoEnd()
    {
        await Record(Candidate(ReputationCodes.DeliveryOverdue, "OVERDUE#o1", T0));

        var row = _repo.Row("farmer-1", "OVERDUE#o1");
        row.AppealStatus.Should().Be("review");
        row.AppealedAt.Should().Be(ReputationPolicy.Iso(_clock.Now));
        var restriction = _repo.RowsOf("farmer-1").Single(r => r.Code == ReputationCodes.Restriction);
        restriction.Cause.Should().Be(ReputationCauses.DeliveryOverdue);
        restriction.Until.Should().BeNull();
        restriction.DueDay.Should().BeNull();
        _repo.Users["farmer-1"].Map!.Aggregate.Tier.Should().Be(ReputationTiers.Restricted);
    }

    // ===== earn-back =====

    [Fact]
    public async Task ACompletion_EarnsAPointBackFromTheOldestPenalty_InTheSameTransaction()
    {
        await Record(Candidate(ReputationCodes.CancelledAfterAccept, "CANCEL#o1", T0.AddDays(-10)));
        await Record(Candidate(ReputationCodes.DeclinedRequest, "REQUEST#o2", T0.AddDays(-5), buyer: "buyer-2"));

        await Record(Candidate(ReputationCodes.OrderCompleted, "COMPLETED#o3", T0, buyer: "buyer-3", total: 600m));

        _repo.Row("farmer-1", "CANCEL#o1").EarnedBack.Should().Be(1);
        _repo.Row("farmer-1", "CANCEL#o1").Version.Should().Be(2);
        var completion = _repo.Row("farmer-1", "COMPLETED#o3");
        completion.EarnBackEventId.Should().Be("CANCEL#o1");
        completion.PurgeAt.Should().Be(new DateTimeOffset(T0.AddDays(100)).ToUnixTimeSeconds());
        _repo.Users["farmer-1"].Map!.Aggregate.ActivePoints.Should().Be(3);
    }

    [Fact]
    public async Task ACompletion_WithASelfTradeSignal_EarnsNothing()
    {
        _fraud.HasSignalAsync("buyer-3", "SELF_TRADE", "o3").Returns(true);
        await Record(Candidate(ReputationCodes.CancelledAfterAccept, "CANCEL#o1", T0.AddDays(-10)));

        await Record(Candidate(ReputationCodes.OrderCompleted, "COMPLETED#o3", T0, buyer: "buyer-3", total: 600m));

        _repo.Row("farmer-1", "CANCEL#o1").EarnedBack.Should().Be(0);
        _repo.Row("farmer-1", "COMPLETED#o3").EarnBackEventId.Should().BeNull();
    }

    [Fact]
    public async Task ACompletion_WithoutTheFraudService_EarnsNothing()
    {
        var service = new ReputationService(_repo, ReputationPolicySettings.Defaults, fraud: null, _clock, _ => Task.CompletedTask);
        await Record(Candidate(ReputationCodes.CancelledAfterAccept, "CANCEL#o1", T0.AddDays(-10)), service);

        await Record(Candidate(ReputationCodes.OrderCompleted, "COMPLETED#o3", T0, buyer: "buyer-3", total: 600m), service);

        _repo.Row("farmer-1", "CANCEL#o1").EarnedBack.Should().Be(0);
    }

    // ===== the read-lag check (6.1) =====

    [Fact]
    public async Task AQueryThatLagsTheUsersRow_IsReadAgain()
    {
        await Record(Candidate(ReputationCodes.DeclinedRequest, "REQUEST#o1", T0, buyer: "buyer-1"));
        _repo.Hidden = "REQUEST#o1";
        _repo.HiddenReads = 1;

        // Without the second read the pair cap would not see REQUEST#o1.
        await Record(Candidate(ReputationCodes.DeclinedRequest, "REQUEST#o2", T0.AddHours(1), buyer: "buyer-1"));

        _repo.Row("farmer-1", "REQUEST#o2").ZeroReason.Should().Be(ReputationZeroReasons.PairCap);
    }

    [Fact]
    public async Task AQueryThatKeepsLagging_Throws_SoTheRecordIsParked()
    {
        await Record(Candidate(ReputationCodes.DeclinedRequest, "REQUEST#o1", T0));
        _repo.Hidden = "REQUEST#o1";
        _repo.HiddenReads = ReputationService.MaxLagReads;

        var act = () => Record(Candidate(ReputationCodes.DeclinedRequest, "REQUEST#o2", T0.AddHours(1)));

        await act.Should().ThrowAsync<ReputationConflictException>();
    }

    // ===== arrival order (5.2 invariants, section 17) =====

    /// <summary>
    /// One farmer's declines and expiries: a busy day (12 requests within 20 hours from 5 buyers) and
    /// 18 more spread over 40 days, buyers drawn from a pool of 6.
    /// </summary>
    private static List<ReputationCandidate> RequestSet()
    {
        var set = new List<ReputationCandidate>();
        for (var i = 0; i < 12; i++)
        {
            set.Add(Candidate(i % 2 == 0 ? ReputationCodes.DeclinedRequest : ReputationCodes.ResponseExpired,
                $"REQUEST#day-{i}", T0.AddDays(-20).AddMinutes(i * 100), buyer: $"buyer-{i % 5}"));
        }

        for (var i = 0; i < 18; i++)
        {
            set.Add(Candidate(i % 3 == 0 ? ReputationCodes.ResponseExpired : ReputationCodes.DeclinedRequest,
                $"REQUEST#spread-{i}", T0.AddDays(-40).AddHours(i * 53), buyer: $"buyer-{(i * 7) % 6}"));
        }

        return set;
    }

    private static IEnumerable<IReadOnlyList<ReputationCandidate>> Orders(List<ReputationCandidate> set)
    {
        yield return set.OrderBy(c => c.OccurredAt).ToList();
        yield return set.OrderByDescending(c => c.OccurredAt).ToList();
        var random = new Random(28);
        for (var run = 0; run < 20; run++)
        {
            yield return set.OrderBy(_ => random.Next()).ToList();
        }
    }

    private static void AssertRequestInvariants(IReadOnlyList<ReputationEvent> rows, ReputationPolicySettings s)
    {
        var counted = rows.Where(ReputationPolicy.IsCounted).OrderBy(r => r.OccurredAt).ToList();

        // 1. No two counted events of one buyer within 30 days of each other.
        foreach (var group in counted.GroupBy(r => r.BuyerId))
        {
            var times = group.Select(r => r.OccurredAt).OrderBy(t => t).ToList();
            for (var i = 1; i < times.Count; i++)
            {
                (times[i] - times[i - 1]).Should().BeGreaterThanOrEqualTo(TimeSpan.FromDays(s.PairCapDays));
            }
        }

        // 2. No 24 hour window holds more than 6 counted points (a window starting at each row is the worst case).
        foreach (var start in counted.Select(r => r.OccurredAt))
        {
            counted.Where(r => r.OccurredAt >= start && r.OccurredAt < start + TimeSpan.FromHours(24))
                .Sum(r => r.Points).Should().BeLessThanOrEqualTo(s.DailyCapPoints);
        }

        // 3. Active points from declines and expiries never pass requestPointsMax (checked where each life starts).
        foreach (var t in counted.Select(r => r.OccurredAt))
        {
            counted.Where(r => r.OccurredAt <= t && t < r.PointsExpireAt)
                .Sum(r => r.Points - r.EarnedBack).Should().BeLessThanOrEqualTo(s.RequestPointsMax);
        }
    }

    [Fact]
    public async Task Requests_InOrder_Reversed_And20Shuffles_KeepTheThreeInvariants()
    {
        var set = RequestSet();
        var totals = new List<int>();
        foreach (var order in Orders(set))
        {
            var repo = new FakeReputationRepository();
            repo.AddUser("farmer-1");
            var service = new ReputationService(repo, ReputationPolicySettings.Defaults, _fraud, _clock, _ => Task.CompletedTask);
            foreach (var candidate in order)
            {
                await service.RecordAsync(candidate, timeoutPenaltiesOn: true, _logger);
            }

            var rows = repo.RowsOf("farmer-1");
            rows.Count(r => ReputationCodes.Requests.Contains(r.Code)).Should().Be(set.Count, "every candidate is recorded, capped or not");
            AssertRequestInvariants(rows, ReputationPolicySettings.Defaults);
            totals.Add(rows.Where(ReputationPolicy.IsCounted).Sum(r => r.Points));
        }

        // The caps bind (some candidates are zeroed) in every order.
        totals.Should().OnlyContain(t => t > 0 && t < set.Count);
    }

    [Fact]
    public async Task TenExpiriesFromOneCronMinute_NewestFirst_GiveAtMostSixPoints_AndOnePerBuyer()
    {
        var expiries = Enumerable.Range(0, 10)
            .Select(i => Candidate(ReputationCodes.ResponseExpired, $"REQUEST#cron-{i}", T0.AddSeconds(i * 5), buyer: $"buyer-{i % 7}"))
            .OrderByDescending(c => c.OccurredAt)
            .ToList();

        foreach (var candidate in expiries)
        {
            await Record(candidate);
        }

        var counted = _repo.RowsOf("farmer-1").Where(ReputationPolicy.IsCounted).ToList();
        counted.Sum(r => r.Points).Should().BeLessThanOrEqualTo(6);
        counted.GroupBy(r => r.BuyerId).Should().OnlyContain(g => g.Count() == 1);
        _repo.RowsOf("farmer-1").Where(r => r.Points == 0 && ReputationCodes.Requests.Contains(r.Code))
            .Select(r => r.ZeroReason).Should().OnlyContain(z => z == ReputationZeroReasons.PairCap || z == ReputationZeroReasons.DailyCap);
    }

    [Fact]
    public async Task EarnBack_InOrder_Reversed_And20Shuffles_KeepsItsLimits()
    {
        // Enough penalties to earn back from all along (cancels, outside the request caps), and
        // 14 large completions over 60 days from 4 buyers.
        var penalties = Enumerable.Range(0, 4)
            .Select(i => Candidate(ReputationCodes.CancelledAfterAccept, $"CANCEL#p{i}", T0.AddDays(-70 + i)))
            .ToList();
        var completions = Enumerable.Range(0, 14)
            .Select(i => Candidate(ReputationCodes.OrderCompleted, $"COMPLETED#c{i}", T0.AddDays(-60 + i * 4.3), buyer: $"buyer-{i % 4}", total: 800m))
            .ToList();

        foreach (var order in Orders(completions))
        {
            var repo = new FakeReputationRepository();
            repo.AddUser("farmer-1");
            var service = new ReputationService(repo, ReputationPolicySettings.Defaults, _fraud, _clock, _ => Task.CompletedTask);
            foreach (var candidate in penalties.Concat(order))
            {
                await service.RecordAsync(candidate, timeoutPenaltiesOn: true, _logger);
            }

            var earners = repo.RowsOf("farmer-1").Where(r => r.EarnBackEventId != null).OrderBy(r => r.OccurredAt).ToList();
            earners.Should().NotBeEmpty();
            foreach (var start in earners.Select(r => r.OccurredAt))
            {
                earners.Count(r => r.OccurredAt >= start && r.OccurredAt < start + TimeSpan.FromDays(30)).Should().BeLessThanOrEqualTo(2);
            }

            foreach (var group in earners.GroupBy(r => r.BuyerId))
            {
                var times = group.Select(r => r.OccurredAt).ToList();
                for (var i = 1; i < times.Count; i++)
                {
                    (times[i] - times[i - 1]).Should().BeGreaterThanOrEqualTo(TimeSpan.FromDays(90));
                }
            }

            // Each earn-back took exactly one point from a penalty.
            repo.RowsOf("farmer-1").Where(r => r.Code == ReputationCodes.CancelledAfterAccept).Sum(r => r.EarnedBack)
                .Should().Be(earners.Count);
        }
    }

    // ===== refresh after a rating (6.4) =====

    [Fact]
    public async Task Refresh_WritesTheMapWhenTheEffectiveRatingMoves_ElseNothing()
    {
        await Record(Candidate(ReputationCodes.CancelledAfterAccept, "CANCEL#o1", T0));
        _repo.Users["farmer-1"].RatingSum = 9;
        _repo.Users["farmer-1"].RatingCount = 2;

        (await Service().RefreshAsync("farmer-1", _logger)).Should().BeTrue();
        _repo.Users["farmer-1"].Map!.Aggregate.EffectiveRating.Should().Be(4.2m);
        _repo.Users["farmer-1"].Map!.LastEventId.Should().Be("CANCEL#o1", "the last event carries over");

        (await Service().RefreshAsync("farmer-1", _logger)).Should().BeFalse();
        _repo.Users["farmer-1"].Map!.Version.Should().Be(2);
    }

    [Fact]
    public async Task Refresh_ForADeletingAccount_WritesNothing()
    {
        _repo.Users["farmer-1"].DeletionState = "in_progress";
        _repo.Users["farmer-1"].RatingCount = 1;
        _repo.Users["farmer-1"].RatingSum = 5;

        (await Service().RefreshAsync("farmer-1", _logger)).Should().BeFalse();
        _repo.Committed.Should().BeEmpty();
    }
}
