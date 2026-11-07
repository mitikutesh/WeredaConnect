using Amazon.Lambda.TestUtilities;
using FluentAssertions;
using NSubstitute;
using WeredaConnect.Server.Functions;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Services;

/// <summary>
/// ESC-28: the daily reputation job (reputation-architecture.md section 8): points expire, an
/// earned-back penalty counts what is left of it until it expires, restrictions and overrides end,
/// heal markers are consumed, the run stops at its time budget leaving the rest due, a catch-up
/// reads further back, and the manual recompute rewrites every active user's map.
/// </summary>
public class ReputationDailyJobTests
{
    private static readonly DateTime T0 = new(2026, 11, 7, 9, 0, 0, DateTimeKind.Utc);

    private readonly FakeReputationRepository _repo = new();
    private readonly ManualClock _clock = new(T0);
    private readonly TestLambdaContext _context = new();

    public ReputationDailyJobTests()
    {
        _repo.AddUser("farmer-1");
    }

    private ReputationService Service()
        => new(_repo, ReputationPolicySettings.Defaults, Substitute.For<IFraudDetectionService>(), _clock, _ => Task.CompletedTask);

    private ReputationDailyJob Job() => new(Service(), _repo);

    private static CronRunSlice Slice(int rowsAllowed = int.MaxValue)
    {
        // The slice reads the clock once when it is made, then once per row it is asked about.
        var calls = -1;
        return new CronRunBudget(() => calls++ < rowsAllowed ? TimeSpan.Zero : TimeSpan.FromHours(1)).Slice(TimeSpan.FromMinutes(4));
    }

    private Task<ReputationRecordResult> Record(string code, string slot, DateTime at, string user = "farmer-1", string buyer = "buyer-1", decimal? total = null)
        => Service().RecordAsync(new ReputationCandidate
        {
            UserId = user,
            EventId = slot,
            Code = code,
            Party = "farmer",
            OrderId = slot[(slot.IndexOf('#') + 1)..],
            BuyerId = buyer,
            OccurredAt = at,
            OrderTotal = total
        }, timeoutPenaltiesOn: true, new TestLambdaLogger());

    [Fact]
    public async Task PointsExpire_TheRowLeavesTheIndex_TheTierFalls()
    {
        _clock.Now = T0;
        await Record(ReputationCodes.CancelledAfterAccept, "CANCEL#o1", T0);
        _repo.Users["farmer-1"].Map!.Aggregate.Tier.Should().Be(ReputationTiers.Warning);

        _clock.Now = T0.AddDays(90).AddMinutes(30);
        var summary = await Job().RunDailyAsync(_clock.Now, ReputationDailyJob.DefaultLookbackDays, Slice(), _context);

        var row = _repo.Row("farmer-1", "CANCEL#o1");
        row.Status.Should().Be(ReputationStatuses.Expired);
        row.DueDay.Should().BeNull();
        var map = _repo.Users["farmer-1"].Map!;
        map.Aggregate.ActivePoints.Should().Be(0);
        map.Aggregate.Tier.Should().Be(ReputationTiers.Good);
        _repo.RowsOf("farmer-1").Should().Contain(r => r.Code == ReputationCodes.TierChanged && r.FromTier == ReputationTiers.Warning && r.ToTier == ReputationTiers.Good);
        summary["expired"].Should().Be("1");
        summary["users"].Should().Be("1");
        ((TestLambdaLogger)_context.Logger).Buffer.ToString().Should().Contain("REPUTATION_DAILY users=1 expired=1 ended=0");
    }

    [Fact]
    public async Task EarnedBackPoints_StayOff_AndTheRestExpireWithThePenalty()
    {
        _clock.Now = T0;
        await Record(ReputationCodes.LateCancel, "CANCEL#o1", T0);
        _clock.Now = T0.AddDays(10);
        await Record(ReputationCodes.OrderCompleted, "COMPLETED#o2", T0.AddDays(10), buyer: "buyer-2", total: 900m);
        _repo.Row("farmer-1", "CANCEL#o1").EarnedBack.Should().Be(1);
        _repo.Users["farmer-1"].Map!.Aggregate.ActivePoints.Should().Be(3);

        // Day 90: the penalty expires; the completion (due at day 100) stays, its earn-back with it.
        _clock.Now = T0.AddDays(90).AddMinutes(1);
        await Job().RunDailyAsync(_clock.Now, ReputationDailyJob.DefaultLookbackDays, Slice(), _context);

        _repo.Row("farmer-1", "CANCEL#o1").Status.Should().Be(ReputationStatuses.Expired);
        _repo.Row("farmer-1", "CANCEL#o1").EarnedBack.Should().Be(1);
        _repo.Row("farmer-1", "COMPLETED#o2").Status.Should().Be(ReputationStatuses.Active);
        _repo.Users["farmer-1"].Map!.Aggregate.ActivePoints.Should().Be(0);

        // Day 100: the completion expires too; nothing is given back.
        _clock.Now = T0.AddDays(100).AddMinutes(1);
        await Job().RunDailyAsync(_clock.Now, ReputationDailyJob.DefaultLookbackDays, Slice(), _context);
        _repo.Row("farmer-1", "COMPLETED#o2").Status.Should().Be(ReputationStatuses.Expired);
        _repo.Users["farmer-1"].Map!.Aggregate.ActivePoints.Should().Be(0);
    }

    [Fact]
    public async Task ARestriction_EndsAtItsUntil_PointsStillAtTen_LeaveTheTierLow()
    {
        _clock.Now = T0;
        for (var i = 0; i < 4; i++)
        {
            await Record(ReputationCodes.CancelledAfterAccept, $"CANCEL#o{i}", T0.AddMinutes(i));
        }

        _repo.Users["farmer-1"].Map!.Aggregate.Tier.Should().Be(ReputationTiers.Restricted);

        _clock.Now = T0.AddDays(14).AddHours(1);
        var summary = await Job().RunDailyAsync(_clock.Now, ReputationDailyJob.DefaultLookbackDays, Slice(), _context);

        var restriction = _repo.RowsOf("farmer-1").Single(r => r.Code == ReputationCodes.Restriction);
        restriction.Status.Should().Be(ReputationStatuses.Ended);
        restriction.DueDay.Should().BeNull();
        var map = _repo.Users["farmer-1"].Map!;
        map.Aggregate.Restriction.Should().BeNull();
        map.Aggregate.Tier.Should().Be(ReputationTiers.Low, "12 points are still active; the next penalty restricts again");
        _repo.RowsOf("farmer-1").Should().Contain(r => r.Code == ReputationCodes.TierChanged && r.FromTier == ReputationTiers.Restricted && r.ToTier == ReputationTiers.Low);
        summary["ended"].Should().Be("1");
        _repo.RowsOf("farmer-1").Count(r => r.Code == ReputationCodes.Restriction).Should().Be(1, "the daily job never starts a restriction");
    }

    [Fact]
    public async Task AnOverride_EndsAtItsUntil()
    {
        _repo.Put(new ReputationEvent
        {
            UserId = "farmer-1",
            EventId = "OVERRIDE#2026-11-01T00:00:00.000Z",
            Code = ReputationCodes.AdminTag,
            Tag = ReputationCauses.TagLow,
            Until = T0.AddDays(1),
            Status = ReputationStatuses.Active,
            OccurredAt = T0.AddDays(-6),
            DueDay = ReputationPolicy.Day(T0.AddDays(1)),
            DueAt = ReputationPolicy.Iso(T0.AddDays(1))
        });

        _clock.Now = T0.AddDays(1).AddHours(1);
        await Job().RunDailyAsync(_clock.Now, ReputationDailyJob.DefaultLookbackDays, Slice(), _context);

        _repo.Row("farmer-1", "OVERRIDE#2026-11-01T00:00:00.000Z").Status.Should().Be(ReputationStatuses.Ended);
        _repo.Users["farmer-1"].Map!.Aggregate.Override.Should().BeNull();
    }

    [Fact]
    public async Task HealMarkers_AreConsumed_AndTheAggregateRewritten()
    {
        _repo.Users["farmer-1"].RatingSum = 9;
        _repo.Users["farmer-1"].RatingCount = 2;
        _repo.Put(new ReputationEvent
        {
            UserId = "farmer-1",
            EventId = "HEAL#2026-11-06",
            Code = ReputationCodes.Heal,
            DueDay = "2026-11-06",
            DueAt = "2026-11-06T10:00:00.000Z"
        });

        _clock.Now = T0;
        var summary = await Job().RunDailyAsync(_clock.Now, ReputationDailyJob.DefaultLookbackDays, Slice(), _context);

        _repo.RowsOf("farmer-1").Should().NotContain(r => r.Code == ReputationCodes.Heal);
        _repo.Users["farmer-1"].Map!.Aggregate.EffectiveRating.Should().Be(4.5m);
        summary["healed"].Should().Be("1");
    }

    [Fact]
    public async Task TheTimeBudget_LeavesTheRestDue_ForTheNextRun()
    {
        foreach (var user in new[] { "farmer-2", "farmer-3" })
        {
            _repo.AddUser(user);
        }

        foreach (var user in new[] { "farmer-1", "farmer-2", "farmer-3" })
        {
            _repo.Put(new ReputationEvent
            {
                UserId = user,
                EventId = "HEAL#2026-11-06",
                Code = ReputationCodes.Heal,
                DueDay = "2026-11-06",
                DueAt = "2026-11-06T10:00:00.000Z"
            });
        }

        _clock.Now = T0;
        var summary = await Job().RunDailyAsync(_clock.Now, ReputationDailyJob.DefaultLookbackDays, Slice(rowsAllowed: 1), _context);

        summary["users"].Should().Be("1");
        summary["leftover"].Should().Be("2");
        summary["outcome"].Should().Be("partial");
        (await _repo.QueryDueAsync("2026-11-06", ReputationPolicy.Iso(T0))).Should().HaveCount(2);

        var next = await Job().RunDailyAsync(_clock.Now, ReputationDailyJob.DefaultLookbackDays, Slice(), _context);
        next["users"].Should().Be("2");
        (await _repo.QueryDueAsync("2026-11-06", ReputationPolicy.Iso(T0))).Should().BeEmpty();
    }

    [Fact]
    public async Task ACatchUp_ReadsFurtherBackThanSevenDays()
    {
        _repo.Put(new ReputationEvent
        {
            UserId = "farmer-1",
            EventId = "HEAL#2026-10-25",
            Code = ReputationCodes.Heal,
            DueDay = "2026-10-25",
            DueAt = "2026-10-25T10:00:00.000Z"
        });
        _clock.Now = T0;

        (await Job().RunDailyAsync(_clock.Now, ReputationDailyJob.DefaultLookbackDays, Slice(), _context))["users"].Should().Be("0");
        (await Job().RunDailyAsync(_clock.Now, 14, Slice(), _context))["users"].Should().Be("1");
        _repo.RowsOf("farmer-1").Should().BeEmpty();
    }

    [Fact]
    public async Task OpenReviewsAndAppeals_OlderThanSevenDays_AreLogged()
    {
        _repo.Put(new ReputationEvent
        {
            UserId = "farmer-1",
            EventId = "OVERDUE#o1",
            Code = ReputationCodes.DeliveryOverdue,
            AppealStatus = "review",
            AppealedAt = ReputationPolicy.Iso(T0.AddDays(-8))
        });
        _repo.Put(new ReputationEvent
        {
            UserId = "farmer-1",
            EventId = "CANCEL#o2",
            Code = ReputationCodes.CancelledAfterAccept,
            AppealStatus = "pending",
            AppealedAt = ReputationPolicy.Iso(T0.AddDays(-1))
        });

        await Job().RunDailyAsync(T0, ReputationDailyJob.DefaultLookbackDays, Slice(), _context);

        var log = ((TestLambdaLogger)_context.Logger).Buffer.ToString();
        log.Should().Contain("REPUTATION_REVIEW_OVERDUE count=1");
        log.Should().NotContain("REPUTATION_APPEAL_OVERDUE");
    }

    [Fact]
    public async Task RecomputeAll_RewritesEveryActiveUsersMap_WithTheLiveSettings()
    {
        _clock.Now = T0;
        await Record(ReputationCodes.CancelledAfterAccept, "CANCEL#o1", T0);
        _repo.Users["farmer-1"].Map!.Aggregate.Tier.Should().Be(ReputationTiers.Warning);

        var stricter = ReputationPolicySettings.Defaults with { WarningAt = 2, LowAt = 3, RestrictAt = 10 };
        var job = new ReputationDailyJob(
            new ReputationService(_repo, stricter, Substitute.For<IFraudDetectionService>(), _clock, _ => Task.CompletedTask), _repo);

        var summary = await job.RecomputeAllAsync(T0.AddDays(1), Slice(), _context);

        summary["written"].Should().Be("1");
        _repo.Users["farmer-1"].Map!.Aggregate.Tier.Should().Be(ReputationTiers.Low);
        _repo.RowsOf("farmer-1").Should().NotContain(r => r.Code == ReputationCodes.Restriction);
    }
}
