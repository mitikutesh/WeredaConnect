using WeredaConnect.Server.Models;
using WeredaConnect.Server.Services;

namespace WeredaConnect.Server.Helpers;

/// <summary>
/// ESC-29 (reputation-api.md sections 2 to 4 and 6): how a reputation and its events are shown.
/// The party's own view carries everything about them; the public summary only what buyers may
/// see (the public tier, never points, events, reasons, appeals or restriction dates).
/// </summary>
public static class ReputationReadModels
{
    /// <summary>Section 2: the party's own reputation.</summary>
    public static ReputationDto Own(ReputationRead read, ReputationPolicySettings settings, bool penaltiesEnabled)
    {
        var a = read.Aggregate;
        var average = Average(read.Ratings);
        return new ReputationDto
        {
            UserId = read.User.UserId,
            Role = read.User.Role,
            Tier = a.Tier,
            ActivePoints = a.ActivePoints,
            Thresholds = new ReputationThresholdsDto { Warning = settings.WarningAt, Low = settings.LowAt, Restricted = settings.RestrictAt },
            RatingAverage = average,
            RatingCount = read.Ratings.Count,
            RatingDeduction = average is { } avg && a.EffectiveRating is { } effective ? Math.Max(0m, avg - effective) : null,
            EffectiveRating = a.EffectiveRating,
            ReliabilityPct = a.ReliabilityPct,
            CompletedOrders90d = a.CompletedOrders90d,
            FaultCancels90d = a.FaultCancels90d,
            NoShows90d = a.NoShows90d,
            Restriction = a.Restriction is { } r
                ? new ReputationRestrictionDto
                {
                    Since = r.Since is { } since ? ReputationPolicy.Iso(since) : null,
                    Until = r.Until is { } until ? ReputationPolicy.Iso(until) : null,
                    Cause = r.Cause ?? ReputationCauses.Points
                }
                : null,
            Override = a.Override is { } o
                ? new ReputationOverrideDto
                {
                    Tag = o.Tag ?? string.Empty,
                    Until = o.Until is { } end ? ReputationPolicy.Iso(end) : null,
                    Reason = o.Reason
                }
                : null,
            NextPointExpiryAt = read.NextPointExpiryAt is { } next ? ReputationPolicy.Iso(next) : null,
            PenaltiesEnabled = penaltiesEnabled,
            AsOf = ReputationPolicy.Iso(read.AsOf)
        };
    }

    /// <summary>
    /// Section 3: the public summary. With <c>reputationPublicTags</c> off only the raw rating
    /// average and count stay (they exist today through <c>GET /ratings</c>).
    /// </summary>
    public static ReputationDto Public(ReputationRead read, bool publicTags)
    {
        var a = read.Aggregate;
        return new ReputationDto
        {
            UserId = read.User.UserId,
            Role = read.User.Role,
            Tier = publicTags ? a.PublicTier : null,
            RatingAverage = Average(read.Ratings),
            RatingCount = read.Ratings.Count,
            EffectiveRating = publicTags ? a.EffectiveRating : null,
            ReliabilityPct = publicTags ? a.ReliabilityPct : null,
            CompletedOrders90d = publicTags ? a.CompletedOrders90d : null,
            TotalOrders90d = publicTags ? a.CompletedOrders90d + a.FaultCancels90d + a.NoShows90d : null,
            AsOf = ReputationPolicy.Iso(read.AsOf)
        };
    }

    /// <summary>The raw average at one decimal, or null with no ratings.</summary>
    public static decimal? Average((long Sum, long Count) ratings)
        => ratings.Count > 0 ? Math.Round((decimal)ratings.Sum / ratings.Count, 1, MidpointRounding.AwayFromZero) : null;

    /// <summary>
    /// Section 4: rows the party's list shows: the penalty codes, the no-fault cancel, and a
    /// completion only when it earned a point back. System rows never.
    /// </summary>
    public static bool IsListed(ReputationEvent e)
        => ReputationCodes.Penalties.Contains(e.Code)
           || e.Code == ReputationCodes.CancelledTransportFailed
           || (e.Code == ReputationCodes.OrderCompleted && !string.IsNullOrEmpty(e.EarnBackEventId));

    /// <summary>The status at <paramref name="now"/>: an active row whose points have expired reads <c>expired</c> before the daily job writes it.</summary>
    public static string StatusAt(ReputationEvent e, DateTime now)
        => e.Status == ReputationStatuses.Active && e.PointsExpireAt is { } expires && expires <= now
            ? ReputationStatuses.Expired
            : e.Status;

    /// <summary>
    /// 6.1: a penalty with points, still active, recorded within the appeal window, with no appeal
    /// text yet.
    /// </summary>
    public static bool CanAppeal(ReputationEvent e, DateTime now, ReputationPolicySettings settings)
        => ReputationCodes.Penalties.Contains(e.Code) && e.Points > 0
           && StatusAt(e, now) == ReputationStatuses.Active
           && now <= e.RecordedAt.AddDays(settings.AppealWindowDays)
           && string.IsNullOrEmpty(e.AppealText);

    /// <summary><c>pending</c> or <c>review</c> while open, the decision once made, else <c>none</c>.</summary>
    public static string AppealStatus(ReputationEvent e)
        => !string.IsNullOrEmpty(e.AppealStatus) ? e.AppealStatus!
            : !string.IsNullOrEmpty(e.AppealDecision) ? e.AppealDecision!
            : "none";

    /// <summary>One event as its party sees it: no buyer id, no base points.</summary>
    public static ReputationEventDto Event(ReputationEvent e, DateTime now, ReputationPolicySettings settings)
    {
        var status = StatusAt(e, now);
        var canAppeal = CanAppeal(e, now, settings);
        return new ReputationEventDto
        {
            EventId = e.EventId,
            Code = e.Code,
            Points = e.Points,
            ZeroReason = e.Points == 0 && ReputationCodes.Penalties.Contains(e.Code) ? e.ZeroReason : null,
            EarnedBack = e.EarnedBack,
            RemainingPoints = status == ReputationStatuses.Active ? Math.Max(0, e.Points - e.EarnedBack) : 0,
            Status = status,
            OccurredAt = ReputationPolicy.Iso(e.OccurredAt),
            RecordedAt = ReputationPolicy.Iso(e.RecordedAt),
            PointsExpireAt = e.PointsExpireAt is { } expires ? ReputationPolicy.Iso(expires) : null,
            OrderId = e.Code == ReputationCodes.TripCancelledWithBookings ? null : e.OrderId,
            TripId = e.Code == ReputationCodes.TripCancelledWithBookings ? e.TripId : null,
            EarnBackEventId = e.EarnBackEventId,
            Appeal = new ReputationEventAppealDto
            {
                Status = AppealStatus(e),
                CanAppeal = canAppeal,
                DeadlineAt = canAppeal ? ReputationPolicy.Iso(e.RecordedAt.AddDays(settings.AppealWindowDays)) : null,
                DecidedAt = e.DecidedAt,
                Note = e.DecisionNote
            }
        };
    }
}
