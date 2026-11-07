using FluentAssertions;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Models;
using Xunit;

namespace WeredaConnect.Server.Tests.Helpers;

/// <summary>
/// ESC-28: the reputation policy, rule by rule (reputation-architecture.md section 5, test spec of
/// section 17): points per code, every cap of 5.2 with windows on both sides, tier boundaries,
/// overrides, restriction start and non-extension, effective rating bounds, reliability, expiry at
/// the boundary, every earn-back condition, and the settings parser.
/// </summary>
public class ReputationPolicyTests
{
    private static readonly ReputationPolicySettings S = ReputationPolicySettings.Defaults;
    private static readonly DateTime T0 = new(2026, 11, 7, 9, 0, 0, DateTimeKind.Utc);

    private static ReputationCandidate Candidate(
        string code,
        DateTime? at = null,
        string buyer = "buyer-1",
        string slot = "REQUEST#order-new",
        TimeSpan? window = null,
        bool? reachable = true,
        string? pickupDate = null,
        decimal? total = null)
        => new()
        {
            UserId = "farmer-1",
            EventId = slot,
            Code = code,
            Party = "farmer",
            BuyerId = buyer,
            OrderId = slot[(slot.IndexOf('#') + 1)..],
            OccurredAt = at ?? T0,
            RequestWindow = window ?? TimeSpan.FromHours(24),
            Reachable = reachable,
            PickupDate = pickupDate,
            OrderTotal = total
        };

    private static ReputationEvent Row(
        string code,
        DateTime at,
        int points = 1,
        string buyer = "buyer-x",
        string? slot = null,
        string status = ReputationStatuses.Active,
        int earnedBack = 0,
        string? earnBackEventId = null)
        => new()
        {
            UserId = "farmer-1",
            EventId = slot ?? $"{code}#{at.Ticks}#{buyer}",
            Code = code,
            Party = "farmer",
            BasePoints = points,
            Points = points,
            BuyerId = buyer,
            OccurredAt = at,
            RecordedAt = at,
            PointsExpireAt = at.AddDays(90),
            Status = status,
            EarnedBack = earnedBack,
            EarnBackEventId = earnBackEventId
        };

    private static ReputationEvent Restriction(DateTime since, DateTime? until, string status = ReputationStatuses.Active)
        => new()
        {
            UserId = "farmer-1",
            EventId = "RESTRICTION#" + ReputationPolicy.Iso(since),
            Code = ReputationCodes.Restriction,
            Cause = ReputationCauses.Points,
            Since = since,
            Until = until,
            Status = status,
            OccurredAt = since
        };

    private static ReputationEvent Override(string tag, DateTime until, DateTime? at = null)
        => new()
        {
            UserId = "farmer-1",
            EventId = "OVERRIDE#" + ReputationPolicy.Iso(at ?? T0.AddDays(-1)),
            Code = tag == ReputationCauses.TagLow ? ReputationCodes.AdminTag : ReputationCodes.AdminUntag,
            Tag = tag,
            Until = until,
            Status = ReputationStatuses.Active,
            OccurredAt = at ?? T0.AddDays(-1)
        };

    private static ReputationScore Score(ReputationCandidate c, IReadOnlyList<ReputationEvent>? rows = null, bool suspended = false, bool timeouts = true)
        => ReputationPolicy.Score(c, rows ?? Array.Empty<ReputationEvent>(), suspended, S, timeouts);

    // ===== 5.1 points =====

    [Theory]
    [InlineData(ReputationCodes.DeclinedRequest, 1, null)]
    [InlineData(ReputationCodes.ResponseExpired, 1, null)]
    [InlineData(ReputationCodes.CancelledAfterAccept, 3, null)]
    [InlineData(ReputationCodes.TripCancelledWithBookings, 3, null)]
    [InlineData(ReputationCodes.NoShow, 5, null)]
    [InlineData(ReputationCodes.DeliveryOverdue, 5, null)]
    [InlineData(ReputationCodes.CancelledTransportFailed, 0, ReputationZeroReasons.NoFault)]
    [InlineData(ReputationCodes.OrderCompleted, 0, null)]
    public void PointsPerCode(string code, int points, string? zeroReason)
    {
        var score = Score(Candidate(code, pickupDate: ReputationPolicy.Iso(T0.AddDays(5))));

        score.Points.Should().Be(points);
        score.BasePoints.Should().Be(points);
        score.ZeroReason.Should().Be(zeroReason);
    }

    [Fact]
    public void Cancel_AtOrAfter24HoursBeforePickup_IsLate_FourPoints()
    {
        var pickup = T0.AddHours(24);
        var late = Score(Candidate(ReputationCodes.CancelledAfterAccept, at: T0, pickupDate: ReputationPolicy.Iso(pickup)));
        late.Code.Should().Be(ReputationCodes.LateCancel);
        late.Points.Should().Be(4);

        var early = Score(Candidate(ReputationCodes.CancelledAfterAccept, at: T0.AddMinutes(-1), pickupDate: ReputationPolicy.Iso(pickup)));
        early.Code.Should().Be(ReputationCodes.CancelledAfterAccept);
        early.Points.Should().Be(3);
    }

    [Fact]
    public void Cancel_WithoutPickupDate_IsNotLate()
        => Score(Candidate(ReputationCodes.CancelledAfterAccept, pickupDate: null)).Points.Should().Be(3);

    [Fact]
    public void TripCancel_Late_FourPoints_KeepsItsCode()
    {
        var score = Score(Candidate(ReputationCodes.TripCancelledWithBookings, pickupDate: ReputationPolicy.Iso(T0.AddHours(2))));

        score.Code.Should().Be(ReputationCodes.TripCancelledWithBookings);
        score.Points.Should().Be(4);
    }

    [Fact]
    public void Expiry_TimeoutPenaltiesOff_ZeroPoints()
    {
        var score = Score(Candidate(ReputationCodes.ResponseExpired), timeouts: false);

        score.Points.Should().Be(0);
        score.ZeroReason.Should().Be(ReputationZeroReasons.TimeoutPenaltiesOff);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    public void Expiry_NotReachable_ZeroPoints(bool? reachable)
        => Score(Candidate(ReputationCodes.ResponseExpired, reachable: reachable)).ZeroReason.Should().Be(ReputationZeroReasons.NotReachable);

    // ===== 5.2 caps =====

    [Fact]
    public void Suspended_DeclineGetsZero_ButACancelStillCounts()
    {
        Score(Candidate(ReputationCodes.DeclinedRequest), suspended: true).ZeroReason.Should().Be(ReputationZeroReasons.Suspended);
        Score(Candidate(ReputationCodes.CancelledAfterAccept), suspended: true).Points.Should().Be(3);
    }

    [Fact]
    public void RestrictedWhenItHappened_ZeroPoints_EvenIfTheRestrictionEndedSince()
    {
        var rows = new[] { Restriction(T0.AddDays(-2), T0.AddDays(1), ReputationStatuses.Ended) };

        Score(Candidate(ReputationCodes.DeclinedRequest), rows).ZeroReason.Should().Be(ReputationZeroReasons.Restricted);
        Score(Candidate(ReputationCodes.DeclinedRequest, at: T0.AddDays(2)), rows).Points.Should().Be(1);
    }

    [Fact]
    public void ShortWindow_11h59_IsZero_12h_Counts_Missing_IsShort()
    {
        Score(Candidate(ReputationCodes.ResponseExpired, window: TimeSpan.FromHours(12) - TimeSpan.FromMinutes(1)))
            .ZeroReason.Should().Be(ReputationZeroReasons.ShortWindow);
        Score(Candidate(ReputationCodes.ResponseExpired, window: TimeSpan.FromHours(12))).Points.Should().Be(1);
        Score(Candidate(ReputationCodes.ResponseExpired) with { RequestWindow = null })
            .ZeroReason.Should().Be(ReputationZeroReasons.ShortWindow);
        // Declines have no short-window rule.
        Score(Candidate(ReputationCodes.DeclinedRequest, window: TimeSpan.FromHours(1))).Points.Should().Be(1);
    }

    [Theory]
    [InlineData(-29)]
    [InlineData(29)]
    public void PairCap_SameBuyerWithin30Days_BeforeOrAfter(int days)
    {
        var rows = new[] { Row(ReputationCodes.DeclinedRequest, T0.AddDays(days), buyer: "buyer-1") };

        Score(Candidate(ReputationCodes.DeclinedRequest), rows).ZeroReason.Should().Be(ReputationZeroReasons.PairCap);
    }

    [Fact]
    public void PairCap_Ignores_31DaysAway_OtherBuyers_ZeroPointAndExcusedRows()
    {
        var rows = new[]
        {
            Row(ReputationCodes.DeclinedRequest, T0.AddDays(-31), buyer: "buyer-1"),
            Row(ReputationCodes.DeclinedRequest, T0.AddDays(-1), buyer: "buyer-2"),
            Row(ReputationCodes.DeclinedRequest, T0.AddDays(-1), buyer: "buyer-1", points: 0, slot: "a"),
            Row(ReputationCodes.DeclinedRequest, T0.AddDays(-1), buyer: "buyer-1", status: ReputationStatuses.Excused, slot: "b")
        };

        Score(Candidate(ReputationCodes.DeclinedRequest), rows).Points.Should().Be(1);
    }

    [Fact]
    public void DailyCap_SixInTheDayBefore_SeventhIsZero()
    {
        var rows = Enumerable.Range(1, 6)
            .Select(i => Row(ReputationCodes.DeclinedRequest, T0.AddHours(-i), buyer: $"b{i}"))
            .ToList();

        Score(Candidate(ReputationCodes.DeclinedRequest), rows).ZeroReason.Should().Be(ReputationZeroReasons.DailyCap);
    }

    [Fact]
    public void DailyCap_LooksAfterTheCandidateToo()
    {
        // Six already recorded in the 23 hours after the candidate (it arrived late).
        var rows = Enumerable.Range(1, 6)
            .Select(i => Row(ReputationCodes.ResponseExpired, T0.AddHours(i * 3.5), buyer: $"b{i}"))
            .ToList();

        Score(Candidate(ReputationCodes.ResponseExpired), rows).ZeroReason.Should().Be(ReputationZeroReasons.DailyCap);
    }

    [Fact]
    public void DailyCap_WindowsOnBothSides_ThatNeverHoldSevenPass()
    {
        // Three 20 hours before and three 20 hours after: no 24 hour window holds them and the candidate together beyond 4.
        var rows = new List<ReputationEvent>();
        for (var i = 0; i < 3; i++)
        {
            rows.Add(Row(ReputationCodes.DeclinedRequest, T0.AddHours(-20).AddMinutes(i), buyer: $"before{i}"));
            rows.Add(Row(ReputationCodes.DeclinedRequest, T0.AddHours(20).AddMinutes(i), buyer: $"after{i}"));
        }

        Score(Candidate(ReputationCodes.DeclinedRequest), rows).Points.Should().Be(1);
    }

    [Fact]
    public void RequestCap_AtNine_TheTenthIsZero()
    {
        // Nine counted points over nine days (spaced past the daily cap, distinct buyers).
        var rows = Enumerable.Range(1, 9)
            .Select(i => Row(ReputationCodes.DeclinedRequest, T0.AddDays(-i), buyer: $"b{i}"))
            .ToList();

        Score(Candidate(ReputationCodes.DeclinedRequest), rows).ZeroReason.Should().Be(ReputationZeroReasons.RequestCap);
        Score(Candidate(ReputationCodes.DeclinedRequest), rows.Take(8).ToList()).Points.Should().Be(1);
    }

    [Fact]
    public void RequestCap_CountsEarnedBackAndExpiredPointsOut()
    {
        var rows = Enumerable.Range(1, 9)
            .Select(i => Row(ReputationCodes.DeclinedRequest, T0.AddDays(-i), buyer: $"b{i}"))
            .ToList();
        rows[0] = rows[0] with { EarnedBack = 1 };
        Score(Candidate(ReputationCodes.DeclinedRequest), rows).Points.Should().Be(1);

        var old = Enumerable.Range(1, 9)
            .Select(i => Row(ReputationCodes.DeclinedRequest, T0.AddDays(-89 - i), buyer: $"o{i}"))
            .ToList();
        Score(Candidate(ReputationCodes.DeclinedRequest), old).Points.Should().Be(1);
    }

    [Fact]
    public void Caps_ApplyOnlyToDeclinesAndExpiries()
    {
        var rows = Enumerable.Range(1, 9)
            .Select(i => Row(ReputationCodes.DeclinedRequest, T0.AddHours(-i), buyer: "buyer-1"))
            .ToList();

        Score(Candidate(ReputationCodes.CancelledAfterAccept), rows).Points.Should().Be(3);
    }

    [Fact]
    public void MaxWindowSum_FindsTheBusiestWindowThatContainsThePoint()
    {
        var rows = new[] { (T0.AddHours(-10), 1), (T0.AddHours(5), 1), (T0.AddHours(13), 1), (T0.AddHours(20), 1), (T0.AddHours(30), 1) };

        // [T0-10h, T0+14h) holds three (-10h, +5h, +13h), and so does [T0-4h, T0+20h]; no window that
        // contains T0 holds -10h and +20h together, and +30h is out of reach.
        ReputationPolicy.MaxWindowSum(rows, T0, ReputationPolicy.DailyWindow).Should().Be(3);
        ReputationPolicy.MaxWindowSum(rows, T0.AddHours(-11), ReputationPolicy.DailyWindow).Should().Be(2);
    }

    // ===== 5.3 tier =====

    [Theory]
    [InlineData(0, ReputationTiers.Good)]
    [InlineData(2, ReputationTiers.Good)]
    [InlineData(3, ReputationTiers.Warning)]
    [InlineData(5, ReputationTiers.Warning)]
    [InlineData(6, ReputationTiers.Low)]
    [InlineData(9, ReputationTiers.Low)]
    [InlineData(10, ReputationTiers.Low)]
    public void TierBoundaries(int points, string tier)
        => ReputationPolicy.Tier(points, null, null, suspended: false, S).Should().Be(tier);

    [Fact]
    public void Tier_RestrictionAndSuspension_AreRestricted_PublicLow()
    {
        var restriction = Restriction(T0.AddDays(-1), T0.AddDays(13));
        ReputationPolicy.Tier(0, restriction, null, false, S).Should().Be(ReputationTiers.Restricted);
        ReputationPolicy.Tier(0, null, null, suspended: true, S).Should().Be(ReputationTiers.Restricted);
        ReputationTiers.Public(ReputationTiers.Restricted).Should().Be(ReputationTiers.Low);
        ReputationTiers.Public(ReputationTiers.Warning).Should().Be(ReputationTiers.Good);
    }

    [Fact]
    public void Overrides_TagShowsLow_UntagShowsWarningAtLowPoints_NeverLiftsARestriction()
    {
        ReputationPolicy.Tier(0, null, Override(ReputationCauses.TagLow, T0.AddDays(5)), false, S).Should().Be(ReputationTiers.Low);
        ReputationPolicy.Tier(7, null, Override(ReputationCauses.TagClear, T0.AddDays(5)), false, S).Should().Be(ReputationTiers.Warning);
        ReputationPolicy.Tier(4, null, Override(ReputationCauses.TagClear, T0.AddDays(5)), false, S).Should().Be(ReputationTiers.Warning);
        ReputationPolicy.Tier(7, Restriction(T0.AddDays(-1), T0.AddDays(1)), Override(ReputationCauses.TagClear, T0.AddDays(5)), false, S)
            .Should().Be(ReputationTiers.Restricted);
    }

    [Fact]
    public void ActiveOverride_EndsAtItsUntil_TheLatestWins()
    {
        var rows = new[]
        {
            Override(ReputationCauses.TagLow, T0.AddDays(5), at: T0.AddDays(-3)),
            Override(ReputationCauses.TagClear, T0.AddDays(5), at: T0.AddDays(-1))
        };

        ReputationPolicy.ActiveOverride(rows, T0)!.Tag.Should().Be(ReputationCauses.TagClear);
        ReputationPolicy.ActiveOverride(rows, T0.AddDays(5)).Should().BeNull();
    }

    // ===== 5.4 restriction =====

    [Fact]
    public void Restriction_StartsWhenAPenaltyCrossesTen_NotWhenOneIsActive_NotForZeroPoints()
    {
        var none = Array.Empty<ReputationEvent>();
        ReputationPolicy.StartsPointsRestriction(ReputationCodes.CancelledAfterAccept, 3, 10, none, T0, S).Should().BeTrue();
        ReputationPolicy.StartsPointsRestriction(ReputationCodes.CancelledAfterAccept, 3, 9, none, T0, S).Should().BeFalse();
        ReputationPolicy.StartsPointsRestriction(ReputationCodes.DeclinedRequest, 0, 12, none, T0, S).Should().BeFalse();
        ReputationPolicy.StartsPointsRestriction(ReputationCodes.OrderCompleted, 0, 12, none, T0, S).Should().BeFalse();

        // Not extended while one is active (proposed decision R5).
        var active = new[] { Restriction(T0.AddDays(-3), T0.AddDays(11)) };
        ReputationPolicy.StartsPointsRestriction(ReputationCodes.LateCancel, 4, 14, active, T0, S).Should().BeFalse();

        // After it ended with points still at 10 or more, the next penalty restricts again.
        var ended = new[] { Restriction(T0.AddDays(-15), T0.AddDays(-1), ReputationStatuses.Ended) };
        ReputationPolicy.StartsPointsRestriction(ReputationCodes.DeclinedRequest, 1, 11, ended, T0, S).Should().BeTrue();
    }

    [Fact]
    public void ActiveRestriction_EndsAtItsUntil_ReviewHasNoEnd()
    {
        var rows = new[] { Restriction(T0.AddDays(-14), T0) };
        ReputationPolicy.ActiveRestriction(rows, T0.AddTicks(-1)).Should().NotBeNull();
        ReputationPolicy.ActiveRestriction(rows, T0).Should().BeNull();

        var review = new[] { Restriction(T0.AddDays(-100), null) with { Cause = ReputationCauses.DeliveryOverdue } };
        ReputationPolicy.ActiveRestriction(review, T0).Should().NotBeNull();
    }

    // ===== 5.5 effective rating =====

    [Fact]
    public void EffectiveRating_Bounds()
    {
        ReputationPolicy.EffectiveRating(0, 0, 4, S).Should().BeNull();
        ReputationPolicy.EffectiveRating(48, 10, 0, S).Should().Be(4.8m);
        ReputationPolicy.EffectiveRating(48, 10, 4, S).Should().Be(4.4m);
        // At most 1.0 off.
        ReputationPolicy.EffectiveRating(48, 10, 25, S).Should().Be(3.8m);
        // Never below 1.0.
        ReputationPolicy.EffectiveRating(12, 10, 25, S).Should().Be(1.0m);
        // Half up to one decimal: 4.25 is 4.3.
        ReputationPolicy.EffectiveRating(17, 4, 0, S).Should().Be(4.3m);
    }

    // ===== 5.6 reliability =====

    [Fact]
    public void Reliability_NoneBelowThreeOrders_RoundedHalfUp()
    {
        ReputationPolicy.ReliabilityPct(1, 1, 0, S).Should().BeNull();
        ReputationPolicy.ReliabilityPct(2, 1, 0, S).Should().Be(67);
        ReputationPolicy.ReliabilityPct(1, 0, 1, S).Should().BeNull();
        ReputationPolicy.ReliabilityPct(7, 1, 0, S).Should().Be(88); // 87.5 rounds up
    }

    [Fact]
    public void Aggregate_ReliabilityCountsTheLast90Days_NotDeclinesOrExcused()
    {
        var rows = new[]
        {
            Row(ReputationCodes.OrderCompleted, T0.AddDays(-1), points: 0, slot: "COMPLETED#1"),
            Row(ReputationCodes.OrderCompleted, T0.AddDays(-2), points: 0, slot: "COMPLETED#2"),
            Row(ReputationCodes.OrderCompleted, T0.AddDays(-95), points: 0, slot: "COMPLETED#old"),
            Row(ReputationCodes.CancelledAfterAccept, T0.AddDays(-3), points: 3, slot: "CANCEL#1"),
            Row(ReputationCodes.NoShow, T0.AddDays(-4), points: 5, slot: "NOSHOW#1", status: ReputationStatuses.Excused),
            Row(ReputationCodes.DeclinedRequest, T0.AddDays(-5), slot: "REQUEST#1")
        };

        var aggregate = ReputationPolicy.Aggregate(rows, 0, 0, false, T0, S);

        aggregate.CompletedOrders90d.Should().Be(2);
        aggregate.FaultCancels90d.Should().Be(1);
        aggregate.NoShows90d.Should().Be(0);
        aggregate.ReliabilityPct.Should().Be(67);
        aggregate.ActivePoints.Should().Be(4);
    }

    // ===== 5.7 expiry =====

    [Fact]
    public void Points_ExpireExactlyAtPointsExpireAt()
    {
        var row = Row(ReputationCodes.CancelledAfterAccept, T0, points: 3);
        var rows = new[] { row };

        ReputationPolicy.ActivePoints(rows, T0.AddDays(90).AddTicks(-1)).Should().Be(3);
        ReputationPolicy.ActivePoints(rows, T0.AddDays(90)).Should().Be(0);
        ReputationPolicy.Aggregate(rows, 0, 0, false, T0, S).NextChangeAt.Should().Be(T0.AddDays(90));
    }

    [Fact]
    public void ActivePoints_SubtractsEarnedBack_SkipsExcusedAndExpiredStatus()
    {
        var rows = new[]
        {
            Row(ReputationCodes.LateCancel, T0.AddDays(-1), points: 4, earnedBack: 1, slot: "a"),
            Row(ReputationCodes.CancelledAfterAccept, T0.AddDays(-1), points: 3, status: ReputationStatuses.Excused, slot: "b"),
            Row(ReputationCodes.DeclinedRequest, T0.AddDays(-1), points: 1, status: ReputationStatuses.Expired, slot: "c")
        };

        ReputationPolicy.ActivePoints(rows, T0).Should().Be(3);
    }

    // ===== 5.8 earn-back =====

    private static IReadOnlyList<ReputationEvent> Penalties() => new[]
    {
        Row(ReputationCodes.CancelledAfterAccept, T0.AddDays(-20), points: 3, slot: "CANCEL#older"),
        Row(ReputationCodes.DeclinedRequest, T0.AddDays(-10), points: 1, slot: "REQUEST#newer")
    };

    private static ReputationCandidate Completion(decimal total = 600m, string buyer = "buyer-1", DateTime? at = null)
        => Candidate(ReputationCodes.OrderCompleted, at: at, buyer: buyer, slot: "COMPLETED#order-c", total: total);

    [Fact]
    public void EarnBack_TargetsTheOldestPenaltyWithPointsLeft()
        => ReputationPolicy.EarnBackTarget(Completion(), Penalties(), T0, S)!.EventId.Should().Be("CANCEL#older");

    [Fact]
    public void EarnBack_NeedsAnOrderOf500Etb()
    {
        ReputationPolicy.EarnBackTarget(Completion(total: 499.99m), Penalties(), T0, S).Should().BeNull();
        ReputationPolicy.EarnBackTarget(Completion(total: 500m), Penalties(), T0, S).Should().NotBeNull();
    }

    [Fact]
    public void EarnBack_AtMostTwoInAny30Days_BothWays()
    {
        var rows = Penalties().Concat(new[]
        {
            Row(ReputationCodes.OrderCompleted, T0.AddDays(-10), points: 0, buyer: "b2", slot: "COMPLETED#a", earnBackEventId: "CANCEL#older"),
            Row(ReputationCodes.OrderCompleted, T0.AddDays(15), points: 0, buyer: "b3", slot: "COMPLETED#b", earnBackEventId: "CANCEL#older")
        }).ToList();

        // A window [T0-10d, T0+20d) holds both and the candidate.
        ReputationPolicy.EarnBackTarget(Completion(), rows, T0, S).Should().BeNull();

        // One 25 days before, one 25 days after: no 30 day window holds both, so each window holds the candidate and one.
        var spread = Penalties().Concat(new[]
        {
            Row(ReputationCodes.OrderCompleted, T0.AddDays(-25), points: 0, buyer: "b2", slot: "COMPLETED#a", earnBackEventId: "CANCEL#older"),
            Row(ReputationCodes.OrderCompleted, T0.AddDays(25), points: 0, buyer: "b3", slot: "COMPLETED#b", earnBackEventId: "CANCEL#older")
        }).ToList();
        ReputationPolicy.EarnBackTarget(Completion(), spread, T0, S).Should().NotBeNull();
    }

    [Theory]
    [InlineData(-89)]
    [InlineData(89)]
    public void EarnBack_OnePerBuyerIn90Days_BothWays(int days)
    {
        var rows = Penalties().Append(
            Row(ReputationCodes.OrderCompleted, T0.AddDays(days), points: 0, buyer: "buyer-1", slot: "COMPLETED#x", earnBackEventId: "CANCEL#older"))
            .ToList();

        ReputationPolicy.EarnBackTarget(Completion(), rows, T0, S).Should().BeNull();
    }

    [Fact]
    public void EarnBack_NothingBanked_WithNoActivePoints()
    {
        var spent = new[] { Row(ReputationCodes.DeclinedRequest, T0.AddDays(-1), points: 1, earnedBack: 1, slot: "r") };
        ReputationPolicy.EarnBackTarget(Completion(), spent, T0, S).Should().BeNull();

        var expired = new[] { Row(ReputationCodes.CancelledAfterAccept, T0.AddDays(-91), points: 3, slot: "c") };
        ReputationPolicy.EarnBackTarget(Completion(), expired, T0, S).Should().BeNull();
    }

    // ===== 5.9 settings =====

    [Fact]
    public void Settings_ParseTheCdkDefaults_WithNothingInvalid()
    {
        const string json =
            "{\"declinePoints\":1,\"expiryPoints\":1,\"cancelPoints\":3,\"lateCancelPoints\":4,\"tripCancelPoints\":3,"
            + "\"tripLateCancelPoints\":4,\"noShowPoints\":5,\"overduePoints\":5,\"lateCancelHours\":24,\"pointsLifeDays\":90,"
            + "\"pairCapDays\":30,\"dailyCapPoints\":6,\"expiryMinWindowHours\":12,\"requestPointsMax\":9,\"warningAt\":3,"
            + "\"lowAt\":6,\"restrictAt\":10,\"restrictionDays\":14,\"repeatWindowDays\":365,\"ratingDeductionPerPoint\":0.1,"
            + "\"ratingDeductionMax\":1.0,\"reliabilityMinOrders\":3,\"earnBackMinOrderEtb\":500,\"earnBackPer30Days\":2,"
            + "\"earnBackBuyerRepeatDays\":90,\"appealWindowDays\":7,\"tagMaxDays\":90,\"adminRestrictMaxDays\":365,"
            + "\"buyerDeclineSignalCount\":3,\"buyerDeclineSignalDays\":30,\"eventsReadCap\":2000}";
        var invalid = new List<string>();

        var settings = ReputationPolicySettings.Parse(json, invalid.Add);

        invalid.Should().BeEmpty();
        settings.Should().Be(ReputationPolicySettings.Defaults);
    }

    [Fact]
    public void Settings_BadKeysKeepTheirDefaults_AndAreReported()
    {
        var invalid = new List<string>();

        var settings = ReputationPolicySettings.Parse("{\"declinePoints\":2,\"cancelPoints\":\"three\",\"noShowPoints\":99}", invalid.Add);

        settings.DeclinePoints.Should().Be(2);
        settings.CancelPoints.Should().Be(3);
        settings.NoShowPoints.Should().Be(5);
        invalid.Should().Contain(new[] { "cancelPoints", "noShowPoints", "lateCancelPoints" });
        invalid.Should().NotContain("declinePoints");
    }

    [Fact]
    public void Settings_ThresholdsOutOfOrder_AllFallBack()
    {
        var invalid = new List<string>();

        var settings = ReputationPolicySettings.Parse("{\"warningAt\":7,\"lowAt\":6,\"restrictAt\":10,\"requestPointsMax\":9}", invalid.Add);

        settings.WarningAt.Should().Be(3);
        invalid.Should().Contain("warningAt");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    public void Settings_UnreadableParameter_GivesTheDefaults(string? json)
    {
        var invalid = new List<string>();

        ReputationPolicySettings.Parse(json, invalid.Add).Should().Be(ReputationPolicySettings.Defaults);
        invalid.Should().Equal("parameter");
    }
}
