namespace WeredaConnect.Server.Models;

/// <summary>
/// ESC-28: one row of the <c>{prefix}-reputation-events</c> table (reputation-architecture.md 3.2)
/// as the policy and the service read it. Penalty and completion rows, and the system rows
/// (<c>RESTRICTION#</c>, <c>OVERRIDE#</c>, <c>TIER#</c>, <c>HEAL#</c>) share the shape; a system
/// row leaves the penalty fields at their defaults.
/// </summary>
public sealed record ReputationEvent
{
    public required string UserId { get; init; }

    /// <summary>The slot (<c>REQUEST#{orderId}</c>, <c>CANCEL#...</c>) or the system row key.</summary>
    public required string EventId { get; init; }

    public string Code { get; init; } = string.Empty;

    /// <summary><c>farmer</c> or <c>trucker</c>: the user's role in the order.</summary>
    public string Party { get; init; } = string.Empty;

    /// <summary>Points before the caps.</summary>
    public int BasePoints { get; init; }

    /// <summary>Points after the caps.</summary>
    public int Points { get; init; }

    public string? ZeroReason { get; init; }

    /// <summary>Points taken back by completed orders.</summary>
    public int EarnedBack { get; init; }

    /// <summary><c>active</c>, <c>expired</c> or <c>excused</c> (system rows: <c>active</c>, <c>ended</c>, <c>lifted</c>, <c>replaced</c>).</summary>
    public string Status { get; init; } = ReputationStatuses.Active;

    public string? OrderId { get; init; }
    public string? TripId { get; init; }

    /// <summary>The order's buyer: the pair cap and the earn-back limit. Admin-only on reads.</summary>
    public string? BuyerId { get; init; }

    /// <summary>On <c>ORDER_COMPLETED</c>: the order total, for the earn-back minimum.</summary>
    public decimal? OrderTotal { get; init; }

    /// <summary>The order's <c>pickupDate</c> as read (the lateness reference).</summary>
    public string? PickupDate { get; init; }

    /// <summary>Server time of the order change (or of the system action).</summary>
    public DateTime OccurredAt { get; init; }

    public DateTime RecordedAt { get; init; }

    /// <summary><c>occurredAt</c> + the points life (90 days), on penalty and completion rows.</summary>
    public DateTime? PointsExpireAt { get; init; }

    /// <summary>On an <c>ORDER_COMPLETED</c> row that earned a point back: the penalty it reduced.</summary>
    public string? EarnBackEventId { get; init; }

    /// <summary><c>pending</c> or <c>review</c> while an appeal or a review is open (sparse).</summary>
    public string? AppealStatus { get; init; }

    public string? AppealedAt { get; init; }

    /// <summary>ESC-29: the party's appeal (reputation-api.md section 6); the text is never logged.</summary>
    public string? AppealText { get; init; }

    public string? AppealPhotoRef { get; init; }

    /// <summary>When the party's appeal text was received (on a review row <c>appealedAt</c> stays the queue key).</summary>
    public string? AppealTextAt { get; init; }

    /// <summary>The admin's decision (<c>excused</c> or <c>upheld</c>), when and the note to the party (ESC-32 writes them).</summary>
    public string? AppealDecision { get; init; }

    public string? DecidedAt { get; init; }
    public string? DecisionNote { get; init; }

    /// <summary>ESC-32: the admin who decided (admin-only on reads).</summary>
    public string? DecidedBy { get; init; }

    /// <summary>The <c>DueIndex</c> keys while the row will still change state.</summary>
    public string? DueDay { get; init; }

    public string? DueAt { get; init; }

    /// <summary>The row's own compare-and-set counter (earn-back and decisions).</summary>
    public int Version { get; init; } = 1;

    /// <summary>On a penalty row: the user's active points right after it was recorded (the push param).</summary>
    public int? ActivePointsAfter { get; init; }

    /// <summary>The stream sequence number of the order change, for support.</summary>
    public string? SourceSeq { get; init; }

    /// <summary>
    /// FUP-204: the other party whose own report decided this event (the arrival check-in behind a farmer
    /// no-show, rule R11), so its fraud signal goes through the attribution guard. Carried from the
    /// candidate to the hooks only; not stored on the row.
    /// </summary>
    public string? ReportedBy { get; init; }

    // ----- system rows -----

    /// <summary>RESTRICTION: <c>points</c>, <c>delivery_overdue</c> or <c>admin</c>.</summary>
    public string? Cause { get; init; }

    public DateTime? Since { get; init; }

    /// <summary>RESTRICTION and OVERRIDE: the end; absent on a restriction while an admin review is open.</summary>
    public DateTime? Until { get; init; }

    /// <summary>OVERRIDE: <c>low</c> or <c>clear</c>.</summary>
    public string? Tag { get; init; }

    public string? Reason { get; init; }
    public string? By { get; init; }

    /// <summary>ESC-32, RESTRICTION with status <c>lifted</c>: when it was lifted and by which admin.</summary>
    public string? LiftedAt { get; init; }

    public string? LiftedBy { get; init; }

    /// <summary>TIER_CHANGED: the tiers before and after.</summary>
    public string? FromTier { get; init; }

    public string? ToTier { get; init; }

    /// <summary>TIER_CHANGED to <c>restricted</c>: when the restriction ends (absent while a review is open).</summary>
    public DateTime? RestrictedUntil { get; init; }

    /// <summary>The TTL (epoch seconds).</summary>
    public long PurgeAt { get; init; }
}

/// <summary>
/// ESC-28: one event the stream derived from an order change (reputation-architecture.md 7.2),
/// before the policy gives it points.
/// </summary>
public sealed record ReputationCandidate
{
    public required string UserId { get; init; }

    /// <summary>The slot: one event per user and cause.</summary>
    public required string EventId { get; init; }

    /// <summary>The code as derived; a cancel becomes <c>LATE_CANCEL</c> in the policy when late.</summary>
    public required string Code { get; init; }

    public required string Party { get; init; }

    public string? OrderId { get; init; }
    public string? TripId { get; init; }
    public string? BuyerId { get; init; }
    public decimal? OrderTotal { get; init; }

    /// <summary>The order's <c>pickupDate</c> as read, or null.</summary>
    public string? PickupDate { get; init; }

    public DateTime OccurredAt { get; init; }

    /// <summary>
    /// For an expiry: how long the request gave the party to answer, or null when a timestamp is
    /// missing (counts as a short window).
    /// </summary>
    public TimeSpan? RequestWindow { get; init; }

    /// <summary>For an expiry: whether the party had a registered push device (null: not read).</summary>
    public bool? Reachable { get; init; }

    public string? SourceSeq { get; init; }

    /// <summary>
    /// FUP-204: the other party whose own report decided this event: on R11 (a farmer no-show) the trucker,
    /// or the buyer of a self-pickup order, whose arrival check-in it rests on. Null when the event rests
    /// on the party's own conduct alone.
    /// </summary>
    public string? ReportedBy { get; init; }
}

/// <summary>
/// ESC-28: what <see cref="Helpers.ReputationPolicy.Aggregate"/> computes from a user's rows, the
/// rating sum and count and <c>now</c> (user-entity.md 2.0.0 section 2, the <c>reputation</c> map).
/// </summary>
public sealed record ReputationAggregate
{
    public int ActivePoints { get; init; }

    /// <summary>The internal tier: <c>good</c>, <c>warning</c>, <c>low</c> or <c>restricted</c>.</summary>
    public string Tier { get; init; } = ReputationTiers.Good;

    public int CompletedOrders90d { get; init; }
    public int FaultCancels90d { get; init; }
    public int NoShows90d { get; init; }

    /// <summary>Whole percent; null below the minimum order count.</summary>
    public int? ReliabilityPct { get; init; }

    /// <summary>One decimal; null with no ratings.</summary>
    public decimal? EffectiveRating { get; init; }

    public ReputationEvent? Restriction { get; init; }
    public ReputationEvent? Override { get; init; }

    /// <summary>The earliest time a stored count or the tier changes with no new event.</summary>
    public DateTime? NextChangeAt { get; init; }

    /// <summary><c>occurredAt</c> of the newest event counted.</summary>
    public DateTime? LastEventAt { get; init; }

    /// <summary>The tier buyers see: <c>good</c> for good and warning, <c>low</c> for low and restricted.</summary>
    public string PublicTier => ReputationTiers.Public(Tier);
}

/// <summary>
/// ESC-28: the users row as <c>ReputationService</c> reads it (strongly consistent, a projection):
/// the account state, the stored <c>reputation</c> map and the rating sum and count.
/// </summary>
public sealed record ReputationUserState
{
    public required string UserId { get; init; }
    public string Role { get; init; } = string.Empty;
    public string? DeletionState { get; init; }
    public bool Suspended { get; init; }

    /// <summary>The map's version; null when the user has no <c>reputation</c> map yet.</summary>
    public int? MapVersion { get; init; }

    public string? StoredTier { get; init; }
    public decimal? StoredEffectiveRating { get; init; }
    public int? StoredReliabilityPct { get; init; }
    public int? StoredActivePoints { get; init; }

    /// <summary>The newest event row written with the map, for the read-lag check (6.1).</summary>
    public string? LastEventId { get; init; }

    public DateTime? LastEventRecordedAt { get; init; }

    public long RatingSum { get; init; }
    public long RatingCount { get; init; }
    public string? RatingsAggregatedAt { get; init; }

    /// <summary>ESC-29: the whole stored map, for the reads (null when the user has no map yet).</summary>
    public ReputationSnapshot? Snapshot { get; init; }
}

/// <summary>Event codes (reputation-api.md 1.1) and system row codes (architecture doc 3.2).</summary>
public static class ReputationCodes
{
    public const string DeclinedRequest = "DECLINED_REQUEST";
    public const string ResponseExpired = "RESPONSE_EXPIRED";
    public const string CancelledAfterAccept = "CANCELLED_AFTER_ACCEPT";
    public const string LateCancel = "LATE_CANCEL";
    public const string TripCancelledWithBookings = "TRIP_CANCELLED_WITH_BOOKINGS";
    public const string NoShow = "NO_SHOW";
    public const string DeliveryOverdue = "DELIVERY_OVERDUE";
    public const string CancelledTransportFailed = "CANCELLED_TRANSPORT_FAILED";
    public const string OrderCompleted = "ORDER_COMPLETED";

    public const string Restriction = "RESTRICTION";
    public const string AdminTag = "ADMIN_TAG";
    public const string AdminUntag = "ADMIN_UNTAG";
    public const string TierChanged = "TIER_CHANGED";
    public const string Heal = "HEAL";

    /// <summary>Codes that can carry points (the ones a party can appeal and that notify).</summary>
    public static readonly IReadOnlySet<string> Penalties = new HashSet<string>(StringComparer.Ordinal)
    {
        DeclinedRequest, ResponseExpired, CancelledAfterAccept, LateCancel, TripCancelledWithBookings, NoShow, DeliveryOverdue
    };

    /// <summary>The two request codes the griefing caps apply to (5.2).</summary>
    public static readonly IReadOnlySet<string> Requests = new HashSet<string>(StringComparer.Ordinal)
    {
        DeclinedRequest, ResponseExpired
    };

    /// <summary>Rows derived from an order change: the penalties, the no-fault cancel and the completion.</summary>
    public static readonly IReadOnlySet<string> OrderEvents = new HashSet<string>(StringComparer.Ordinal)
    {
        DeclinedRequest, ResponseExpired, CancelledAfterAccept, LateCancel, TripCancelledWithBookings, NoShow, DeliveryOverdue,
        CancelledTransportFailed, OrderCompleted
    };

    /// <summary>Broken commitments for reliability (5.6).</summary>
    public static readonly IReadOnlySet<string> FaultCancels = new HashSet<string>(StringComparer.Ordinal)
    {
        CancelledAfterAccept, LateCancel, TripCancelledWithBookings
    };

    public static readonly IReadOnlySet<string> NoShows = new HashSet<string>(StringComparer.Ordinal)
    {
        NoShow, DeliveryOverdue
    };
}

/// <summary>Slot and system row key prefixes (architecture doc 3.2).</summary>
public static class ReputationSlots
{
    public const string Request = "REQUEST#";
    public const string Cancel = "CANCEL#";
    public const string TripCancel = "TRIP_CANCEL#";
    public const string NoShow = "NOSHOW#";
    public const string Overdue = "OVERDUE#";
    public const string Completed = "COMPLETED#";
    public const string Restriction = "RESTRICTION#";
    public const string Override = "OVERRIDE#";
    public const string Tier = "TIER#";
    public const string Heal = "HEAL#";
}

/// <summary>Why a penalty got 0 points (reputation-api.md section 4).</summary>
public static class ReputationZeroReasons
{
    public const string Suspended = "suspended";
    public const string Restricted = "restricted";
    public const string ShortWindow = "short_window";
    public const string PairCap = "pair_cap";
    public const string DailyCap = "daily_cap";
    public const string RequestCap = "request_cap";
    public const string NotReachable = "not_reachable";
    public const string TimeoutPenaltiesOff = "timeout_penalties_off";
    public const string NoFault = "no_fault";
}

/// <summary>Row statuses (architecture doc 3.2).</summary>
public static class ReputationStatuses
{
    public const string Active = "active";
    public const string Expired = "expired";
    public const string Excused = "excused";
    public const string Ended = "ended";
    public const string Lifted = "lifted";
    public const string Replaced = "replaced";
}

/// <summary>Tiers (reputation-api.md 1.2).</summary>
public static class ReputationTiers
{
    public const string Good = "good";
    public const string Warning = "warning";
    public const string Low = "low";
    public const string Restricted = "restricted";

    /// <summary>The public tier: <c>good</c> for good and warning, <c>low</c> for low and restricted.</summary>
    public static string Public(string tier) => tier is Low or Restricted ? Low : Good;
}

/// <summary>Restriction causes and override tags (architecture doc 3.2).</summary>
public static class ReputationCauses
{
    public const string Points = "points";
    public const string DeliveryOverdue = "delivery_overdue";
    public const string Admin = "admin";

    public const string TagLow = "low";
    public const string TagClear = "clear";
}

/// <summary>
/// ESC-29: the stored <c>reputation</c> map of a users row (user-entity.md section 2) as the gates,
/// the owner stamps and the reads see it. Stored values only: whether a restriction still holds at
/// a given moment is decided by <see cref="IsRestrictedAt"/>, never by the stored tier.
/// </summary>
public sealed record ReputationSnapshot
{
    public int Version { get; init; }
    public string Tier { get; init; } = ReputationTiers.Good;
    public int ActivePoints { get; init; }
    public int CompletedOrders90d { get; init; }
    public int FaultCancels90d { get; init; }
    public int NoShows90d { get; init; }
    public int? ReliabilityPct { get; init; }
    public decimal? EffectiveRating { get; init; }

    /// <summary>True when the map carries a <c>restriction</c> (it may have ended since it was written).</summary>
    public bool HasRestriction { get; init; }

    public string? RestrictionCause { get; init; }
    public DateTime? RestrictionSince { get; init; }

    /// <summary>Absent while an admin review is open (no end date yet).</summary>
    public DateTime? RestrictionUntil { get; init; }

    public string? OverrideTag { get; init; }
    public string? OverrideReason { get; init; }
    public DateTime? OverrideUntil { get; init; }

    /// <summary>The earliest time a stored value changes with no new event (a read past it recomputes).</summary>
    public DateTime? NextChangeAt { get; init; }

    /// <summary>A stored restriction that has started and not ended at <paramref name="now"/>.</summary>
    public bool IsRestrictedAt(DateTime now)
        => HasRestriction && (RestrictionUntil == null || RestrictionUntil > now);
}

/// <summary>
/// ESC-29 (reputation-api.md section 7): the owner's public reputation as copied onto their crop
/// and trip rows. <see cref="Tier"/> is the internal tier (stored); reads map it to the public one.
/// </summary>
public sealed record OwnerReputationFields
{
    public const string TierField = "ownerTier";
    public const string EffectiveRatingField = "ownerEffectiveRating";
    public const string RatingCountField = "ownerRatingCount";
    public const string ReliabilityPctField = "ownerReliabilityPct";
    public const string AtField = "ownerReputationAt";

    /// <summary>The five attribute names, never taken from a client.</summary>
    public static readonly IReadOnlyList<string> Names = new[]
    {
        TierField, EffectiveRatingField, RatingCountField, ReliabilityPctField, AtField
    };

    public required string Tier { get; init; }
    public decimal? EffectiveRating { get; init; }
    public long RatingCount { get; init; }
    public int? ReliabilityPct { get; init; }
    public required DateTime At { get; init; }
}
