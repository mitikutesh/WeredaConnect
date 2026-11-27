namespace WeredaConnect.Server.Models;

// ──────────────────────────────────────────────
// FUP-287: the admin's side of the pickup judgement (delivery-verification-api.md 2.8.0 P.2,
// order-lifecycle.md 2.16.0): the check-ins as evidence and the re-open of a judged pickup
// ──────────────────────────────────────────────

/// <summary>
/// FUP-287: the pickup judgement of an order as the admin transaction view reads it
/// (<c>GET /admin/transactions/{orderId}</c>, <c>pickupEvidence</c>). Unlike the parties' <c>OrderDto</c>
/// it carries the stamped farm point and, per check-in, the fix, the distance and the sender. Admin
/// routes only; never on a party route.
/// </summary>
public sealed record AdminPickupEvidenceDto
{
    /// <summary><c>pending</c>, or the judged outcome (<c>scanned</c>, <c>trucker_no_show</c>, ...).</summary>
    public string? Judgement { get; init; }

    /// <summary>When the order cron judged the pickup (ISO 8601); absent while pending.</summary>
    public string? JudgedAt { get; init; }

    /// <summary>The order's <c>pickupDeadlineAt</c>: check-ins received after it are late.</summary>
    public string? DeadlineAt { get; init; }

    /// <summary>A self-pickup order: the buyer sends <c>arrived_pickup</c>.</summary>
    public bool SelfPickup { get; init; }

    /// <summary>The farm point stamped when the order became judgeable; absent when the listing could not be read then.</summary>
    public double? PointLatitude { get; init; }

    public double? PointLongitude { get; init; }

    /// <summary>Per kind (<c>arrived_pickup</c>, <c>crops_ready</c>); absent when nobody checked in.</summary>
    public Dictionary<string, AdminCheckInEvidenceDto>? CheckIns { get; init; }

    /// <summary>
    /// The exits <c>POST /admin/orders/{orderId}/reopen-pickup</c> takes for the order as read:
    /// <c>pending</c>, <c>handoff_confirmed</c>, or none. The action reads the order, its delivery and its
    /// escrow again before it writes.
    /// </summary>
    public List<string> ReopenExits { get; init; } = new();

    /// <summary>
    /// FUP-330: the exits <c>POST /admin/orders/{orderId}/reopen-no-show</c> takes for a trucker no-show as read:
    /// <c>pending</c>, <c>handoff_confirmed</c>, or none. The action reads the order, its delivery, its trip and its
    /// escrow again before it writes (a trip that cannot take the booking back is refused then).
    /// </summary>
    public List<string> NoShowReopenExits { get; init; } = new();

    /// <summary>The last re-open, when an admin re-opened the judgement.</summary>
    public AdminPickupReopenDto? LastReopen { get; init; }
}

/// <summary>FUP-287: one stored check-in (<c>checkIns.{kind}</c>) with everything the server kept.</summary>
public sealed record AdminCheckInEvidenceDto
{
    /// <summary>When the server received it (ISO 8601): the only time the judgement reads.</summary>
    public string ReceivedAt { get; init; } = string.Empty;

    /// <summary>When the phone says it was taken, kept only when plausible: evidence only.</summary>
    public string? CapturedAt { get; init; }

    /// <summary>The user id of the sender.</summary>
    public string? By { get; init; }

    /// <summary>The fix, 4 decimals; absent for a farmer's check-in without GPS.</summary>
    public double? Latitude { get; init; }

    public double? Longitude { get; init; }

    /// <summary>The fix's accuracy in metres, when the phone sent it.</summary>
    public double? AccuracyM { get; init; }

    /// <summary>The distance in km from the fix to the farm point (2 decimals), when both were there.</summary>
    public double? DistanceKm { get; init; }

    /// <summary>The fix was measured within 5 km of the farm point.</summary>
    public bool DistanceChecked { get; init; }

    /// <summary>It reached the server after the deadline: kept as evidence, never counted.</summary>
    public bool Late { get; init; }

    /// <summary>A farmer's fix more than 5 km away: kept, counted as unmeasured.</summary>
    public bool OutOfRange { get; init; }
}

/// <summary>FUP-287: the order's <c>pickupReopen</c> stamp, as the admin reads it.</summary>
public sealed record AdminPickupReopenDto
{
    public string? At { get; init; }

    /// <summary><c>pending</c> or <c>handoff_confirmed</c>.</summary>
    public string? Exit { get; init; }

    /// <summary>The judgement it re-opened (<c>both_present_no_scan</c>, <c>unchecked_check_in</c> or, FUP-330, <c>trucker_no_show</c>).</summary>
    public string? FromJudgement { get; init; }

    /// <summary>The new pickup deadline of a <c>pending</c> re-open.</summary>
    public string? DeadlineAt { get; init; }
}

/// <summary>FUP-287: <c>POST /admin/orders/{orderId}/reopen-pickup</c>.</summary>
public sealed record PickupReopenRequest
{
    /// <summary><c>pending</c> (judged again at a new deadline) or <c>handoff_confirmed</c>.</summary>
    public string? Exit { get; init; }

    /// <summary>Why, for the audit trail: 10 to 500 characters after trimming.</summary>
    public string? Reason { get; init; }

    /// <summary>The order's <c>version</c> as the admin's page read it.</summary>
    public int? ExpectedVersion { get; init; }

    /// <summary>With <c>pending</c> only: the new pickup deadline, this many hours from now (1 to 168).</summary>
    public int? DeadlineHours { get; init; }
}

/// <summary>FUP-287: the answer of a re-open, or of its replay (<see cref="AlreadyApplied"/>).</summary>
public sealed record PickupReopenResponse
{
    public required string OrderId { get; init; }

    public required string Exit { get; init; }

    /// <summary>The judgement that was re-opened.</summary>
    public string? FromJudgement { get; init; }

    /// <summary>The order's status now (<c>accepted</c>, or <c>pickup_confirmed</c> after a confirmed handoff).</summary>
    public string? Status { get; init; }

    /// <summary>The order's <c>pickupJudgement</c> now (<c>pending</c> or <c>scanned</c>).</summary>
    public string? PickupJudgement { get; init; }

    public string? PickupDeadlineAt { get; init; }

    /// <summary>The delivery marked <c>pickedUp</c> by a confirmed handoff.</summary>
    public string? DeliveryId { get; init; }

    public int Version { get; init; }

    /// <summary>The order already carried this re-open (same exit from the same version): nothing was written.</summary>
    public bool AlreadyApplied { get; init; }

    /// <summary>
    /// FUP-330, a trucker no-show re-open only: what happened to the trucker's <c>NO_SHOW</c> event
    /// (<see cref="Helpers.NoShowReopen.NoShowEvent"/>: <c>excused</c>, <c>already_excused</c>, <c>not_recorded</c>,
    /// <c>not_counted</c>, <c>upheld</c>, <c>off</c> or <c>failed</c>). Absent on <c>reopen-pickup</c>.
    /// </summary>
    public string? NoShowEvent { get; init; }

    public required string Message { get; init; }
}
