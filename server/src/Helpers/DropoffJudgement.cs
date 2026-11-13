using System.Globalization;
using System.Text.Json;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.Services;

namespace WeredaConnect.Server.Helpers;

/// <summary>
/// ESC-36 (spec 4.5 T8 and T9, 4.8 "Silent buyer", edge cases E3 and E4; delivery-verification-api.md
/// P.4): the trucker's drop-off check-in and the judgement of a delivery that is overdue (T8) or whose
/// buyer stays silent (T9), as pure rules on the order row, its delivery and the server clock. The sync
/// handler (<c>handoff_check_in</c> kind <c>arrived_dropoff</c>) and the order cron do the reads and the
/// writes; what they decide is here.
/// <para>
/// Behind the flag <c>dropoffClassification</c> (<see cref="EnabledEnv"/>, off everywhere), which only
/// works with <c>COMMITMENTS_ENABLED</c>. With it on, the order's first move out of <c>accepted</c> after a
/// trucker's pickup stamps <see cref="Field"/> <c>pending</c>, the T8 deadline and the drop-off points as
/// they were then: only such an order takes the drop-off check-in and is judged, so an order picked up
/// before the flag (or while it was off) keeps the rules it had (spec K2).
/// </para>
/// <para>
/// The two judgements:
/// <list type="bullet">
/// <item>T8: no drop-off check-in received by <see cref="DeadlineField"/> (pickup plus
/// <see cref="OrderTimings.DeliveryOverdue"/>, 72 hours): the order and its delivery go to
/// <c>disputed</c> with the reason <c>delivery_overdue</c> after the escrow froze, which reputation rule
/// R12 turns into <c>DELIVERY_OVERDUE</c> on the trucker. It rests on the trucker's own missing check-in
/// alone (FUP-204): once the trucker checked in, the buyer's silence leads to T9, never to T8.</item>
/// <item>T9: a drop-off check-in, and the buyer neither confirmed nor disputed within
/// <see cref="OrderTimings.SilentBuyerWindow"/> (48 hours) of its receipt: the order cron confirms the
/// delivery as the system when the evidence is complete (the check-in measured within 5 km, the arrival
/// photo attached), the trucker is neither low nor restricted and the escrow holds the money with
/// nothing else on it; otherwise <c>disputed</c> with the reason <c>silent_buyer</c>. No reputation event
/// either way.</item>
/// </list>
/// </para>
/// </summary>
public static class DropoffJudgement
{
    /// <summary>The Lambda env flag <c>dropoffClassification</c> (sync, delivery and order-cron).</summary>
    public const string EnabledEnv = "DROPOFF_CLASSIFICATION_ENABLED";

    /// <summary>Whether <c>dropoffClassification</c> is on in this Lambda. Off by default.</summary>
    public static bool IsEnabled
        => string.Equals(Environment.GetEnvironmentVariable(EnabledEnv), "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>Server-only order field: <c>pending</c> until judged, then the outcome (<see cref="Outcome"/>).</summary>
    public const string Field = "dropoffJudgement";

    /// <summary>Server-only order field: when the order cron judged the drop-off (ISO 8601 UTC).</summary>
    public const string JudgedAtField = "dropoffJudgedAt";

    /// <summary>Server-only order field: T8, the pickup plus <see cref="OrderTimings.DeliveryOverdue"/> (ISO 8601 UTC).</summary>
    public const string DeadlineField = "dropoffDeadlineAt";

    /// <summary>Server-only order map: the trucker's drop-off check-in (the <see cref="PickupJudgement"/> entry fields).</summary>
    public const string CheckInField = "dropoffCheckIn";

    /// <summary>
    /// Server-only order fields: the order's own drop-off point and the trip's destination point as they
    /// were at pickup. The check-in is measured against them, so neither a buyer who moves the order's
    /// point nor a trucker who moves the trip's destination afterwards changes where the drop-off is
    /// (FUP-204). Never returned to a client.
    /// </summary>
    public const string OrderLatitudeField = "dropoffOrderLatitude";

    public const string OrderLongitudeField = "dropoffOrderLongitude";
    public const string TripLatitudeField = "dropoffTripLatitude";
    public const string TripLongitudeField = "dropoffTripLongitude";

    /// <summary>The order field reputation rule R12 reads (<see cref="ReputationDerivation.DisputeReasonField"/>).</summary>
    public const string DisputeReasonField = ReputationDerivation.DisputeReasonField;

    /// <summary>Server-only delivery field: when the order cron confirmed the delivery for a silent buyer.</summary>
    public const string AutoConfirmedAtField = "autoConfirmedAt";

    /// <summary>The <c>disputedBy</c> of a dispute the order cron opened.</summary>
    public const string SystemActor = "system";

    /// <summary>Values of <see cref="Field"/>.</summary>
    public static class Outcome
    {
        /// <summary>Picked up with the flag on: judged at T8, or at T9 once the trucker checked in.</summary>
        public const string Pending = "pending";

        /// <summary>A party confirmed or disputed the delivery before the judgement: nothing to judge.</summary>
        public const string Closed = "closed";

        /// <summary>T9: the system confirmed the delivery; the normal dispute window runs.</summary>
        public const string AutoConfirmed = "auto_confirmed";

        /// <summary>T9: the system opened a dispute (evidence missing, the trucker low or restricted, the money not plain).</summary>
        public const string SilentBuyer = "silent_buyer";

        /// <summary>T8: no drop-off check-in by the deadline; the system opened a dispute.</summary>
        public const string DeliveryOverdue = "delivery_overdue";

        /// <summary>The money could be neither confirmed nor frozen: the admin queue, nothing moved.</summary>
        public const string Queued = "queued";
    }

    /// <summary>The dispute reasons of the order and its delivery (also the admin queue reasons when nothing could move).</summary>
    public const string OverdueReason = Outcome.DeliveryOverdue;

    public const string SilentBuyerReason = Outcome.SilentBuyer;

    /// <summary>The refusal of a drop-off check-in sent without its attached arrival photo (permanent).</summary>
    public const string PhotoRequiredCode = "CHECKIN_PHOTO_REQUIRED";

    /// <summary>The order statuses of goods on the road (the order may lag its delivery).</summary>
    public static readonly IReadOnlySet<string> OnTheRoadOrderStatuses =
        new HashSet<string>(StringComparer.Ordinal) { "pickup_confirmed", "in_transit" };

    /// <summary>The delivery statuses of goods on the road.</summary>
    public static readonly IReadOnlySet<string> OnTheRoadDeliveryStatuses =
        new HashSet<string>(StringComparer.Ordinal) { "pickedUp", "inTransit" };

    // ── the order ───────────────────────────────────────────────────────────

    /// <summary>Whether the order was stamped judgeable at pickup (any value of <see cref="Field"/>).</summary>
    public static bool IsStamped(IReadOnlyDictionary<string, object> order) => Str(order, Field).Length > 0;

    /// <summary>Whether the order waits for its drop-off judgement.</summary>
    public static bool IsPending(IReadOnlyDictionary<string, object> order) => Str(order, Field) == Outcome.Pending;

    /// <summary>T8 as stamped at pickup, or null.</summary>
    public static DateTime? DeadlineOf(IReadOnlyDictionary<string, object> order)
        => OrderCommitment.TryParseUtc(Str(order, DeadlineField), out var at) ? at : null;

    /// <summary>The stored drop-off check-in, or null.</summary>
    public static IReadOnlyDictionary<string, object>? CheckInOf(IReadOnlyDictionary<string, object> order)
        => order.TryGetValue(CheckInField, out var raw) && raw is IReadOnlyDictionary<string, object> entry ? entry : null;

    /// <summary>
    /// The drop-off check-in when it counts: received by the T8 deadline and not late. A check-in that
    /// came after it is evidence for the admin, never a reason to wait for T9.
    /// </summary>
    public static IReadOnlyDictionary<string, object>? CountedCheckIn(IReadOnlyDictionary<string, object> order)
        => CheckInOf(order) is { } entry
           && !Bool(entry, PickupJudgement.EntryLate)
           && ReceivedAt(entry) is { } received
           && DeadlineOf(order) is { } deadline
           && received <= deadline
            ? entry
            : null;

    /// <summary>The server's receipt time of a stored check-in, or null.</summary>
    public static DateTime? ReceivedAt(IReadOnlyDictionary<string, object> entry)
        => OrderCommitment.TryParseUtc(Str(entry, PickupJudgement.EntryReceivedAt), out var at) ? at : null;

    /// <summary>
    /// The judgement that is due next on a pending order and when: T9 at the counted check-in's receipt
    /// plus the silent buyer window, else T8; each plus <see cref="PickupJudgement.SettleMargin"/>, so every
    /// operation the server received before the deadline is written when the cron reads the row. Null
    /// when the order has no T8 deadline (it is not judged).
    /// </summary>
    public static OrderDeadline? Due(IReadOnlyDictionary<string, object> order, OrderTimings timings)
    {
        if (DeadlineOf(order) is not { } deadline)
        {
            return null;
        }

        return CountedCheckIn(order) is { } entry && ReceivedAt(entry) is { } received
            ? new OrderDeadline(received + timings.SilentBuyerWindow + PickupJudgement.SettleMargin, OrderDeadlines.Kind.SilentBuyer)
            : new OrderDeadline(deadline + PickupJudgement.SettleMargin, OrderDeadlines.Kind.DeliveryOverdue);
    }

    /// <summary>The order's drop-off points as stamped at pickup: its own, then the trip's destination.</summary>
    public static IReadOnlyList<TrackPoint> StampedPoints(IReadOnlyDictionary<string, object> order)
    {
        var points = new List<TrackPoint>(2);
        if (TripTrack.PointOf(order, OrderLatitudeField, OrderLongitudeField) is { } own)
        {
            points.Add(own);
        }

        if (TripTrack.PointOf(order, TripLatitudeField, TripLongitudeField) is { } trip)
        {
            points.Add(trip);
        }

        return points;
    }

    /// <summary>The distance in km from a fix to the nearest of <paramref name="points"/>, or null when there is none.</summary>
    public static double? NearestKm(double latitude, double longitude, IReadOnlyList<TrackPoint> points)
    {
        double? nearest = null;
        foreach (var point in points)
        {
            var km = GpsValidator.HaversineDistance(latitude, longitude, point.Latitude, point.Longitude);
            nearest = nearest is { } best ? Math.Min(best, km) : km;
        }

        return nearest;
    }

    /// <summary>
    /// The fields the order's first move out of <c>accepted</c> after a trucker's pickup adds: the
    /// pending judgement, T8 counted from <paramref name="pickedUpAt"/>, and the drop-off points known now
    /// (the order's own, the trip's destination). A point that is not known is left out.
    /// </summary>
    public static Dictionary<string, object> PickupStamp(
        DateTime pickedUpAt, TrackPoint? orderPoint, TrackPoint? tripPoint, OrderTimings timings)
    {
        var fields = new Dictionary<string, object>
        {
            { Field, Outcome.Pending },
            { DeadlineField, OrderCommitment.Iso(pickedUpAt + timings.DeliveryOverdue) }
        };
        if (orderPoint != null)
        {
            fields[OrderLatitudeField] = orderPoint.Latitude;
            fields[OrderLongitudeField] = orderPoint.Longitude;
        }

        if (tripPoint != null)
        {
            fields[TripLatitudeField] = tripPoint.Latitude;
            fields[TripLongitudeField] = tripPoint.Longitude;
        }

        return fields;
    }

    /// <summary>
    /// Whether the order is stamped at its first move out of <c>accepted</c>: a trucked order (a trucker
    /// bound, not self-pickup) that was never stamped, moving to <c>pickup_confirmed</c> or
    /// <c>in_transit</c>.
    /// </summary>
    public static bool TakesPickupStamp(IReadOnlyDictionary<string, object> order, string fromStatus, string toStatus)
        => fromStatus == "accepted"
           && OnTheRoadOrderStatuses.Contains(toStatus)
           && !IsStamped(order)
           && Str(order, "truckerId").Length > 0
           && !PickupJudgement.IsSelfPickup(order);

    // ── the judgement ───────────────────────────────────────────────────────

    /// <summary>What the order cron does with a pending order whose delivery is still on the road.</summary>
    public enum Decision
    {
        /// <summary>T8: no counted drop-off check-in.</summary>
        DeliveryOverdue,

        /// <summary>T9: everything is in place to confirm for the silent buyer.</summary>
        AutoConfirm,

        /// <summary>T9: something is missing; a dispute.</summary>
        SilentBuyerDispute
    }

    /// <summary>
    /// The decision on a pending order whose delivery is still on the road, at the due time
    /// <see cref="Due"/> gave. FUP-204: without a counted check-in it is always T8, whatever the buyer
    /// did; with one it is never T8, whatever the buyer did (a buyer who withholds the scan makes a T9).
    /// </summary>
    public static Decision Decide(
        IReadOnlyDictionary<string, object> order, bool arrivalPhoto, bool truckerHeld, bool escrowPlain)
    {
        if (CountedCheckIn(order) is not { } entry)
        {
            return Decision.DeliveryOverdue;
        }

        return Bool(entry, PickupJudgement.EntryDistanceChecked) && arrivalPhoto && !truckerHeld && escrowPlain
            ? Decision.AutoConfirm
            : Decision.SilentBuyerDispute;
    }

    /// <summary>
    /// Whether the trucker's reputation keeps a silent buyer's delivery from being confirmed (spec 4.8,
    /// E3): a tier of <c>low</c> or <c>restricted</c>, or a restriction active now. No reputation map (the
    /// feature off, a new user) holds nothing.
    /// </summary>
    public static bool HoldsAutoConfirm(ReputationSnapshot? reputation, DateTime nowUtc)
        => reputation != null
           && (reputation.IsRestrictedAt(nowUtc) || reputation.Tier is ReputationTiers.Low or ReputationTiers.Restricted);

    /// <summary>
    /// Whether the escrow holds the money with nothing else on it, as a buyer's confirm finds it: status
    /// <c>escrowed</c>, no refund armed, not in the manual payout queue, no settlement in flight, no
    /// delivery confirmed yet, and linked to no delivery but <paramref name="deliveryId"/>. Anything else is
    /// not the system's to confirm.
    /// </summary>
    public static bool IsPlainForConfirm(IReadOnlyDictionary<string, object>? escrow, string deliveryId)
        => escrow != null
           && Str(escrow, "status") == "escrowed"
           && Str(escrow, "refundReason").Length == 0
           && Str(escrow, ManualPayoutFields.Marker) != ManualPayoutFields.MarkerOpen
           && Str(escrow, SettlementLockFields.Lock).Length == 0
           && Str(escrow, "deliveryConfirmedAt").Length == 0
           && (Str(escrow, "deliveryId").Length == 0 || Str(escrow, "deliveryId") == deliveryId);

    /// <summary>
    /// The system's dispute reason a delivery carries (<see cref="OverdueReason"/> or
    /// <see cref="SilentBuyerReason"/>), when the order cron opened it; else null.
    /// </summary>
    public static string? SystemDisputeReasonOf(IReadOnlyDictionary<string, object> delivery)
        => Str(delivery, "status") == "disputed"
           && Str(delivery, "disputedBy") == SystemActor
           && Str(delivery, "disputeReason") is OverdueReason or SilentBuyerReason
            ? Str(delivery, "disputeReason")
            : null;

    // ── what the parties read ───────────────────────────────────────────────

    /// <summary>The drop-off check-in as the parties read it (no coordinates, no sender), or null.</summary>
    public static OrderCheckInDto? ViewOf(IReadOnlyDictionary<string, object> order)
        => CheckInOf(order) is { } entry && Str(entry, PickupJudgement.EntryReceivedAt).Length > 0
            ? new OrderCheckInDto
            {
                ReceivedAt = Str(entry, PickupJudgement.EntryReceivedAt),
                CapturedAt = Str(entry, PickupJudgement.EntryCapturedAt) is { Length: > 0 } captured ? captured : null,
                DistanceChecked = Bool(entry, PickupJudgement.EntryDistanceChecked),
                Late = Bool(entry, PickupJudgement.EntryLate)
            }
            : null;

    /// <summary>The server-only fields a client never reads back raw (the view replaces the check-in).</summary>
    public static readonly string[] HiddenFields =
    {
        CheckInField, OrderLatitudeField, OrderLongitudeField, TripLatitudeField, TripLongitudeField
    };

    // ── reading ─────────────────────────────────────────────────────────────

    private static string Str(IReadOnlyDictionary<string, object> item, string key)
        => item.TryGetValue(key, out var value) ? value?.ToString() ?? string.Empty : string.Empty;

    private static bool Bool(IReadOnlyDictionary<string, object> item, string key)
        => item.TryGetValue(key, out var value) && value switch
        {
            bool b => b,
            string s => bool.TryParse(s, out var parsed) && parsed,
            JsonElement { ValueKind: JsonValueKind.True } => true,
            _ => false
        };

    /// <summary>Invariant text of a number, for log lines.</summary>
    internal static string Km(double km) => km.ToString("F1", CultureInfo.InvariantCulture);
}
