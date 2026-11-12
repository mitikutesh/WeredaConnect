using System.Globalization;
using WeredaConnect.Server.Models;

namespace WeredaConnect.Server.Helpers;

/// <summary>
/// ESC-28: the reputation policy as pure functions (reputation-architecture.md section 5): from a
/// user's event rows, rating sum and count, the account state, the settings and <c>now</c> to
/// points, tier, effective rating, reliability, a restriction start and the earn-back target. No
/// I/O and no clock, so every rule is a table-driven unit test.
///
/// Arrival order (principle 3): every window rule (the caps of 5.2, the earn-back limits of 5.8)
/// looks both ways in time from the candidate's <c>occurredAt</c> over the rows already recorded, so
/// each limit holds whatever order the stream delivers one party's orders in.
/// </summary>
public static class ReputationPolicy
{
    /// <summary>The reliability window (5.6) and the counts' <c>90d</c> suffix.</summary>
    public static readonly TimeSpan ReliabilityWindow = TimeSpan.FromDays(90);

    /// <summary>The daily cap's window (5.2 rule 5).</summary>
    public static readonly TimeSpan DailyWindow = TimeSpan.FromHours(24);

    /// <summary>The earn-back rate limit's window (5.8 rule 2).</summary>
    public static readonly TimeSpan EarnBackWindow = TimeSpan.FromDays(30);

    /// <summary>TTLs (3.1): penalty, restriction, override and tier rows from <c>recordedAt</c>.</summary>
    public static readonly TimeSpan PenaltyRetention = TimeSpan.FromDays(400);

    /// <summary>TTL of <c>ORDER_COMPLETED</c> rows, from <c>occurredAt</c>.</summary>
    public static readonly TimeSpan CompletionRetention = TimeSpan.FromDays(100);

    /// <summary>TTL of <c>HEAL</c> markers (the daily job deletes them after use).</summary>
    public static readonly TimeSpan HealRetention = TimeSpan.FromDays(7);

    // ===== points (5.1) =====

    /// <summary>
    /// True when a cancel came at or after <c>pickupDate - lateCancelHours</c>. No readable
    /// <c>pickupDate</c> (a self-pickup before ESC-20) is not late (decision R10).
    /// </summary>
    public static bool IsLate(DateTime occurredAt, string? pickupDate, ReputationPolicySettings s)
        => TryParseUtc(pickupDate, out var pickup) && occurredAt >= pickup.AddHours(-s.LateCancelHours);

    /// <summary>The code to record: a cancel after accept becomes <c>LATE_CANCEL</c> when late.</summary>
    public static string ResolveCode(ReputationCandidate candidate, ReputationPolicySettings s)
        => candidate.Code == ReputationCodes.CancelledAfterAccept && IsLate(candidate.OccurredAt, candidate.PickupDate, s)
            ? ReputationCodes.LateCancel
            : candidate.Code;

    /// <summary>
    /// The points before the caps, and why they are 0 when the code itself gives none:
    /// <c>timeout_penalties_off</c> and <c>not_reachable</c> for an expiry, <c>no_fault</c> for the
    /// farmer's cancel during <c>awaiting_transport</c>. A completion has no reason.
    /// </summary>
    public static (int Points, string? ZeroReason) BasePoints(
        string code, ReputationCandidate candidate, ReputationPolicySettings s, bool timeoutPenaltiesOn)
    {
        switch (code)
        {
            case ReputationCodes.DeclinedRequest:
                return Points(s.DeclinePoints);
            case ReputationCodes.ResponseExpired:
                if (!timeoutPenaltiesOn)
                {
                    return (0, ReputationZeroReasons.TimeoutPenaltiesOff);
                }

                return candidate.Reachable == true
                    ? Points(s.ExpiryPoints)
                    : (0, ReputationZeroReasons.NotReachable);
            case ReputationCodes.CancelledAfterAccept:
                return Points(s.CancelPoints);
            case ReputationCodes.LateCancel:
                return Points(s.LateCancelPoints);
            case ReputationCodes.TripCancelledWithBookings:
                return Points(IsLate(candidate.OccurredAt, candidate.PickupDate, s) ? s.TripLateCancelPoints : s.TripCancelPoints);
            case ReputationCodes.NoShow:
                return Points(s.NoShowPoints);
            case ReputationCodes.DeliveryOverdue:
                return Points(s.OverduePoints);
            case ReputationCodes.CancelledTransportFailed:
                return (0, ReputationZeroReasons.NoFault);
            default:
                return (0, null);
        }

        // A setting of 0 points records the event with no reason a party could act on.
        static (int, string?) Points(int points) => (points, points > 0 ? null : ReputationZeroReasons.NoFault);
    }

    /// <summary>
    /// The candidate's code, base points, points after the caps of 5.2 and the zero reason.
    /// <paramref name="events"/> are the user's rows already recorded (read strongly consistent).
    /// </summary>
    public static ReputationScore Score(
        ReputationCandidate candidate,
        IReadOnlyList<ReputationEvent> events,
        bool suspended,
        ReputationPolicySettings s,
        bool timeoutPenaltiesOn)
    {
        var code = ResolveCode(candidate, s);
        var (basePoints, baseReason) = BasePoints(code, candidate, s, timeoutPenaltiesOn);
        if (basePoints <= 0 || !ReputationCodes.Requests.Contains(code))
        {
            return new ReputationScore(code, basePoints, basePoints, basePoints > 0 ? null : baseReason);
        }

        var capReason = CapReason(code, candidate, basePoints, events, suspended, s);
        return capReason == null
            ? new ReputationScore(code, basePoints, basePoints, null)
            : new ReputationScore(code, basePoints, 0, capReason);
    }

    // ===== caps (5.2) =====

    /// <summary>
    /// "Counted": a decline or expiry of the user with points above 0 that was not excused. Expired
    /// rows still count for the windows (the caps are about when the events happened).
    /// </summary>
    public static bool IsCounted(ReputationEvent e)
        => ReputationCodes.Requests.Contains(e.Code) && e.Points > 0 && e.Status != ReputationStatuses.Excused;

    /// <summary>The first cap of 5.2 that fires for a decline or expiry with points, or null.</summary>
    internal static string? CapReason(
        string code,
        ReputationCandidate candidate,
        int points,
        IReadOnlyList<ReputationEvent> events,
        bool suspended,
        ReputationPolicySettings s)
    {
        var at = candidate.OccurredAt;

        // 1. Suspended (spec H1: a suspended party's pending requests expire with no extra points).
        if (suspended)
        {
            return ReputationZeroReasons.Suspended;
        }

        // 2. Restricted when it happened: the restriction blocked the accept.
        if (RestrictionCovering(events, at) != null)
        {
            return ReputationZeroReasons.Restricted;
        }

        // 3. Short window (expiries only); a missing timestamp counts as short.
        if (code == ReputationCodes.ResponseExpired && s.ExpiryMinWindowHours > 0
            && (candidate.RequestWindow is not { } window || window < TimeSpan.FromHours(s.ExpiryMinWindowHours)))
        {
            return ReputationZeroReasons.ShortWindow;
        }

        var counted = events.Where(e => IsCounted(e) && e.EventId != candidate.EventId).ToList();

        // 4. Pair cap: a counted event of the same buyer within pairCapDays before or after.
        if (!string.IsNullOrEmpty(candidate.BuyerId))
        {
            var pairWindow = TimeSpan.FromDays(s.PairCapDays);
            if (counted.Any(e => e.BuyerId == candidate.BuyerId && Distance(e.OccurredAt, at) < pairWindow))
            {
                return ReputationZeroReasons.PairCap;
            }
        }

        // 5. Daily cap: every 24 hour window that contains the candidate stays at or below the cap.
        if (MaxWindowSum(counted.Select(e => (e.OccurredAt, e.Points)), at, DailyWindow) + points > s.DailyCapPoints)
        {
            return ReputationZeroReasons.DailyCap;
        }

        // 6. Request cap: active points from counted declines and expiries, at every moment of the
        //    candidate's life, stay at or below requestPointsMax.
        if (MaxActiveRequestPoints(counted, at, s) + points > s.RequestPointsMax)
        {
            return ReputationZeroReasons.RequestCap;
        }

        return null;
    }

    /// <summary>
    /// The largest sum of <paramref name="weights"/> inside any window <c>[start, start + window)</c>
    /// that contains <paramref name="at"/>, that is, any start in <c>(at - window, at]</c>. The sum
    /// only changes where a row enters or leaves, so it is enough to look at the earliest start and
    /// at the starts where a later row enters (one tick after <c>row - window</c>).
    /// </summary>
    public static int MaxWindowSum(IEnumerable<(DateTime At, int Weight)> weights, DateTime at, TimeSpan window)
    {
        var rows = weights.Where(w => w.At > at - window && w.At < at + window).ToList();
        if (rows.Count == 0)
        {
            return 0;
        }

        var tick = TimeSpan.FromTicks(1);
        var starts = new List<DateTime> { at - window + tick };
        starts.AddRange(rows.Where(r => r.At > at).Select(r => r.At - window + tick));

        var max = 0;
        foreach (var start in starts)
        {
            var end = start + window;
            var sum = rows.Where(r => r.At >= start && r.At < end).Sum(r => r.Weight);
            max = Math.Max(max, sum);
        }

        return max;
    }

    /// <summary>
    /// The most active points the counted declines and expiries hold at any moment of a candidate's
    /// life <c>[at, at + pointsLife)</c>. The sum only grows where a row's life starts, so the
    /// candidate's own start and each later row's start are the moments to check.
    /// </summary>
    internal static int MaxActiveRequestPoints(IReadOnlyList<ReputationEvent> counted, DateTime at, ReputationPolicySettings s)
    {
        var life = s.PointsLife;
        var moments = new List<DateTime> { at };
        moments.AddRange(counted.Where(e => e.OccurredAt > at && e.OccurredAt < at + life).Select(e => e.OccurredAt));

        var max = 0;
        foreach (var t in moments)
        {
            var sum = counted
                .Where(e => e.OccurredAt <= t && t < (e.PointsExpireAt ?? e.OccurredAt + life))
                .Sum(e => Math.Max(0, e.Points - e.EarnedBack));
            max = Math.Max(max, sum);
        }

        return max;
    }

    // ===== restriction and override (5.3, 5.4) =====

    /// <summary>A restriction row in force at <paramref name="at"/> (any cause), or null.</summary>
    public static ReputationEvent? RestrictionCovering(IReadOnlyList<ReputationEvent> events, DateTime at)
        => events
            .Where(e => e.Code == ReputationCodes.Restriction && e.Since is { } since && since <= at
                        && (e.Status == ReputationStatuses.Active || e.Status == ReputationStatuses.Ended)
                        && (e.Until == null || e.Until > at))
            .OrderByDescending(e => e.Until ?? DateTime.MaxValue)
            .FirstOrDefault();

    /// <summary>The active restriction at <paramref name="now"/>: status active, started, not ended.</summary>
    public static ReputationEvent? ActiveRestriction(IReadOnlyList<ReputationEvent> events, DateTime now)
        => events
            .Where(e => e.Code == ReputationCodes.Restriction && e.Status == ReputationStatuses.Active
                        && e.Since is { } since && since <= now && (e.Until == null || e.Until > now))
            .OrderByDescending(e => e.Until ?? DateTime.MaxValue)
            .FirstOrDefault();

    /// <summary>The admin override in force at <paramref name="now"/> (the latest one), or null.</summary>
    public static ReputationEvent? ActiveOverride(IReadOnlyList<ReputationEvent> events, DateTime now)
        => events
            .Where(e => (e.Code == ReputationCodes.AdminTag || e.Code == ReputationCodes.AdminUntag)
                        && e.Status == ReputationStatuses.Active && e.Until is { } until && until > now)
            .OrderByDescending(e => e.OccurredAt)
            .ThenByDescending(e => e.EventId, StringComparer.Ordinal)
            .FirstOrDefault();

    /// <summary>
    /// 5.3: an active restriction (or a suspended account, stored as restricted for buyers) is
    /// <c>restricted</c>; an override <c>low</c> is <c>low</c>; <c>lowAt</c> points or more are
    /// <c>low</c>, or <c>warning</c> under an override <c>clear</c>; <c>warningAt</c> or more
    /// <c>warning</c>; else <c>good</c>.
    /// </summary>
    public static string Tier(int activePoints, ReputationEvent? restriction, ReputationEvent? overrideRow, bool suspended, ReputationPolicySettings s)
    {
        if (suspended || restriction != null)
        {
            return ReputationTiers.Restricted;
        }

        if (overrideRow?.Tag == ReputationCauses.TagLow)
        {
            return ReputationTiers.Low;
        }

        if (activePoints >= s.LowAt)
        {
            return overrideRow?.Tag == ReputationCauses.TagClear ? ReputationTiers.Warning : ReputationTiers.Low;
        }

        return activePoints >= s.WarningAt ? ReputationTiers.Warning : ReputationTiers.Good;
    }

    /// <summary>
    /// 5.4: a new penalty with points that leaves <c>activePoints</c> at <c>restrictAt</c> or more
    /// starts a <c>points</c> restriction when none is active. Only the transaction that records a
    /// penalty calls this: reads and the daily recompute never start one.
    /// </summary>
    public static bool StartsPointsRestriction(
        string code, int points, int activePointsAfter, IReadOnlyList<ReputationEvent> events, DateTime now, ReputationPolicySettings s)
        => ReputationCodes.Penalties.Contains(code) && points > 0 && activePointsAfter >= s.RestrictAt
           && ActiveRestriction(events, now) == null;

    // ===== aggregate (5.3, 5.5, 5.6, 5.7) =====

    /// <summary>
    /// 5.7: the sum of <c>points - earnedBack</c> over penalty rows with status active whose points
    /// have not expired.
    /// </summary>
    public static int ActivePoints(IReadOnlyList<ReputationEvent> events, DateTime now)
        => events
            .Where(e => ReputationCodes.Penalties.Contains(e.Code) && e.Status == ReputationStatuses.Active
                        && e.PointsExpireAt is { } expires && expires > now)
            .Sum(e => Math.Max(0, e.Points - e.EarnedBack));

    /// <summary>
    /// 5.5: <c>max(1.0, ratingSum / ratingCount - min(deductionMax, deductionPerPoint x activePoints))</c>,
    /// rounded half up to one decimal; null with no ratings (D3).
    /// </summary>
    public static decimal? EffectiveRating(long ratingSum, long ratingCount, int activePoints, ReputationPolicySettings s)
    {
        if (ratingCount <= 0)
        {
            return null;
        }

        var average = (decimal)ratingSum / ratingCount;
        var deduction = Math.Min(s.RatingDeductionMax, s.RatingDeductionPerPoint * activePoints);
        return Math.Round(Math.Max(1.0m, average - deduction), 1, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// 5.6: <c>round(100 x completed / (completed + faultCancels + noShows))</c>, half up; null while
    /// the denominator is below <c>reliabilityMinOrders</c>.
    /// </summary>
    public static int? ReliabilityPct(int completed, int faultCancels, int noShows, ReputationPolicySettings s)
    {
        var total = completed + faultCancels + noShows;
        if (total < s.ReliabilityMinOrders || total == 0)
        {
            return null;
        }

        return (int)Math.Round(100m * completed / total, 0, MidpointRounding.AwayFromZero);
    }

    /// <summary>The aggregate of a user's rows at <paramref name="now"/>.</summary>
    public static ReputationAggregate Aggregate(
        IReadOnlyList<ReputationEvent> events, long ratingSum, long ratingCount, bool suspended, DateTime now, ReputationPolicySettings s)
    {
        var activePoints = ActivePoints(events, now);
        var restriction = ActiveRestriction(events, now);
        var overrideRow = ActiveOverride(events, now);

        var windowStart = now - ReliabilityWindow;
        var recent = events
            .Where(e => ReputationCodes.OrderEvents.Contains(e.Code) && e.Status != ReputationStatuses.Excused
                        && e.OccurredAt > windowStart && e.OccurredAt <= now)
            .ToList();
        var completed = recent.Count(e => e.Code == ReputationCodes.OrderCompleted);
        var faults = recent.Count(e => ReputationCodes.FaultCancels.Contains(e.Code));
        var noShows = recent.Count(e => ReputationCodes.NoShows.Contains(e.Code));

        return new ReputationAggregate
        {
            ActivePoints = activePoints,
            Tier = Tier(activePoints, restriction, overrideRow, suspended, s),
            CompletedOrders90d = completed,
            FaultCancels90d = faults,
            NoShows90d = noShows,
            ReliabilityPct = ReliabilityPct(completed, faults, noShows, s),
            EffectiveRating = EffectiveRating(ratingSum, ratingCount, activePoints, s),
            Restriction = restriction,
            Override = overrideRow,
            NextChangeAt = NextChangeAt(events, restriction, overrideRow, now),
            LastEventAt = events.Where(e => ReputationCodes.OrderEvents.Contains(e.Code))
                .Select(e => (DateTime?)e.OccurredAt)
                .DefaultIfEmpty(null)
                .Max()
        };
    }

    /// <summary>
    /// ESC-29 (architecture doc 6.6): the aggregate of a stored <c>reputation</c> map at
    /// <paramref name="now"/>, with no event read. Valid while <paramref name="now"/> is before the
    /// map's <c>nextChangeAt</c> (past it the caller recomputes from the events); a restriction or
    /// an override whose end has passed is dropped here either way, so a gate never refuses on an
    /// ended restriction. The tier, the effective rating and the reliability come from the stored
    /// counts and the live policy. An absent map is 0 points, <c>good</c>, no reliability.
    /// </summary>
    public static ReputationAggregate FromSnapshot(
        ReputationSnapshot? snapshot, (long Sum, long Count) ratings, bool suspended, DateTime now, ReputationPolicySettings s)
    {
        var restriction = snapshot != null && snapshot.IsRestrictedAt(now)
            ? new ReputationEvent
            {
                UserId = string.Empty,
                EventId = ReputationSlots.Restriction,
                Code = ReputationCodes.Restriction,
                Cause = snapshot.RestrictionCause,
                Since = snapshot.RestrictionSince,
                Until = snapshot.RestrictionUntil
            }
            : null;
        var overrideRow = snapshot?.OverrideTag != null && snapshot.OverrideUntil is { } overrideEnd && overrideEnd > now
            ? new ReputationEvent
            {
                UserId = string.Empty,
                EventId = ReputationSlots.Override,
                Code = snapshot.OverrideTag == ReputationCauses.TagClear ? ReputationCodes.AdminUntag : ReputationCodes.AdminTag,
                Tag = snapshot.OverrideTag,
                Reason = snapshot.OverrideReason,
                Until = overrideEnd
            }
            : null;
        var points = snapshot?.ActivePoints ?? 0;
        var completed = snapshot?.CompletedOrders90d ?? 0;
        var faults = snapshot?.FaultCancels90d ?? 0;
        var noShows = snapshot?.NoShows90d ?? 0;

        return new ReputationAggregate
        {
            ActivePoints = points,
            Tier = Tier(points, restriction, overrideRow, suspended, s),
            CompletedOrders90d = completed,
            FaultCancels90d = faults,
            NoShows90d = noShows,
            ReliabilityPct = ReliabilityPct(completed, faults, noShows, s),
            EffectiveRating = EffectiveRating(ratings.Sum, ratings.Count, points, s),
            Restriction = restriction,
            Override = overrideRow,
            NextChangeAt = snapshot?.NextChangeAt
        };
    }

    /// <summary>
    /// The earliest moment a stored value changes with no new event: a point or completion expiry,
    /// a row leaving the 90 day reliability window, the restriction's or the override's end.
    /// </summary>
    internal static DateTime? NextChangeAt(
        IReadOnlyList<ReputationEvent> events, ReputationEvent? restriction, ReputationEvent? overrideRow, DateTime now)
    {
        DateTime? next = null;
        void Consider(DateTime? at)
        {
            if (at is { } value && value > now && (next == null || value < next))
            {
                next = value;
            }
        }

        foreach (var e in events)
        {
            if (!ReputationCodes.OrderEvents.Contains(e.Code) || e.Status == ReputationStatuses.Excused)
            {
                continue;
            }

            if (e.Status == ReputationStatuses.Active)
            {
                Consider(e.PointsExpireAt);
            }

            if (e.Code == ReputationCodes.OrderCompleted || ReputationCodes.FaultCancels.Contains(e.Code)
                || ReputationCodes.NoShows.Contains(e.Code))
            {
                Consider(e.OccurredAt + ReliabilityWindow);
            }
        }

        Consider(restriction?.Until);
        Consider(overrideRow?.Until);
        return next;
    }

    // ===== earn-back (5.8) =====

    /// <summary>
    /// The penalty an <c>ORDER_COMPLETED</c> candidate takes a point from, or null, checking rules 1,
    /// 2, 3 and 5 of 5.8 (rule 4, the fraud signal, is a read the service makes after this says yes).
    /// The target is the active penalty with points left and the oldest <c>occurredAt</c>. Both
    /// window limits look both ways in time from the candidate.
    /// </summary>
    public static ReputationEvent? EarnBackTarget(
        ReputationCandidate completion, IReadOnlyList<ReputationEvent> events, DateTime now, ReputationPolicySettings s)
    {
        // 1. A large enough order.
        if ((completion.OrderTotal ?? 0m) < s.EarnBackMinOrderEtb || s.EarnBackPer30Days <= 0)
        {
            return null;
        }

        var earners = events
            .Where(e => e.Code == ReputationCodes.OrderCompleted && !string.IsNullOrEmpty(e.EarnBackEventId)
                        && e.EventId != completion.EventId)
            .ToList();

        // 2. Fewer than earnBackPer30Days earn-backs in every 30 day window that contains it.
        if (MaxWindowSum(earners.Select(e => (e.OccurredAt, 1)), completion.OccurredAt, EarnBackWindow) + 1 > s.EarnBackPer30Days)
        {
            return null;
        }

        // 3. No earn-back from the same buyer within earnBackBuyerRepeatDays either way.
        if (!string.IsNullOrEmpty(completion.BuyerId) && s.EarnBackBuyerRepeatDays > 0)
        {
            var repeat = TimeSpan.FromDays(s.EarnBackBuyerRepeatDays);
            if (earners.Any(e => e.BuyerId == completion.BuyerId && Distance(e.OccurredAt, completion.OccurredAt) < repeat))
            {
                return null;
            }
        }

        // 5. Some active penalty with points left; nothing is banked.
        return events
            .Where(e => ReputationCodes.Penalties.Contains(e.Code) && e.Status == ReputationStatuses.Active
                        && e.PointsExpireAt is { } expires && expires > now && e.Points - e.EarnedBack > 0)
            .OrderBy(e => e.OccurredAt)
            .ThenBy(e => e.EventId, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    // ===== helpers =====

    private static TimeSpan Distance(DateTime a, DateTime b) => a > b ? a - b : b - a;

    /// <summary>An ISO 8601 time as UTC, or false.</summary>
    public static bool TryParseUtc(string? iso, out DateTime utc)
    {
        utc = default;
        if (string.IsNullOrWhiteSpace(iso))
        {
            return false;
        }

        if (!DateTime.TryParse(iso, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed))
        {
            return false;
        }

        utc = DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
        return true;
    }

    /// <summary>The ISO 8601 form every reputation timestamp is written in (milliseconds, Z).</summary>
    public static string Iso(DateTime utc)
        => DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    /// <summary>The <c>DueIndex</c> day partition of a time (UTC, <c>yyyy-MM-dd</c>).</summary>
    public static string Day(DateTime utc)
        => DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}

/// <summary>The policy's verdict on one candidate.</summary>
public sealed record ReputationScore(string Code, int BasePoints, int Points, string? ZeroReason);
