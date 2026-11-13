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
/// alone (FUP-204): once the trucker checked in, the buyer's silence leads to T9, never to T8; and a trucker
/// who tried by T8 but was out of range of every point (<see cref="AttemptField"/>: the only point may be the
/// buyer's own pin) gets the dispute <c>dropoff_out_of_range</c> instead, which R12 does not read.</item>
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

    /// <summary>
    /// FUP-370: T9 as the parties read it (ISO 8601 UTC, <see cref="ConfirmAtFor"/>). Computed on every read and
    /// never stored, so a changed <c>silent-buyer-hours</c> shows as the order cron judges by it.
    /// </summary>
    public const string ConfirmAtField = "dropoffConfirmAt";

    /// <summary>Server-only order map: the trucker's drop-off check-in (the <see cref="PickupJudgement"/> entry fields).</summary>
    public const string CheckInField = "dropoffCheckIn";

    /// <summary>
    /// Server-only order map: the trucker's first drop-off check-in that was more than 5 km from every drop-off
    /// point, with its photo attached (the entry fields, <c>outOfRange: true</c>). It never counts as a
    /// check-in, but at T8 the dispute takes the reason <c>dropoff_out_of_range</c> instead of
    /// <c>delivery_overdue</c>, which reputation rule R12 does not read: the buyer's own pin can be the only
    /// point, and a buyer who pinned far away must not be able to put <c>DELIVERY_OVERDUE</c> on a trucker who
    /// came (FUP-204). An admin decides the dispute through the normal verdict.
    /// </summary>
    public const string AttemptField = "dropoffAttempt";

    /// <summary>
    /// Server-only order field: <c>true</c> when the stamp at pickup read the trip, whether or not it had a
    /// destination. Only a trip that could not be read then is read live at the check-in: a booked trip may
    /// gain a destination later (trip-listing-api 2.1.0), and that one the trucker would choose after the handoff.
    /// </summary>
    public const string TripReadField = "dropoffTripRead";

    /// <summary>
    /// A stamp that lands late (the order's first advance after the pickup failed and a later one stamped it)
    /// still leaves the trucker at least this long to check in: T8 is never before the stamp plus this.
    /// </summary>
    public static readonly TimeSpan LateStampGrace = TimeSpan.FromHours(24);

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

    /// <summary>
    /// FUP-373: server-only delivery field, when the arrival photo was attached as late evidence on the T8 dispute
    /// (<see cref="TakesLateArrivalPhoto"/>); absent on a photo attached on the road.
    /// </summary>
    public const string ArrivalPhotoLateAtField = "arrivalPhotoLateAt";

    /// <summary>
    /// FUP-140: server-only delivery field, when the buyer's drop-off photo was attached as late evidence on a system
    /// dispute without a confirm (<see cref="TakesLateDropoffPhoto"/>); absent on a photo of a confirmed delivery.
    /// </summary>
    public const string DropoffPhotoLateAtField = "dropoffPhotoLateAt";

    /// <summary>
    /// The delivery field an admin's verdict or disposition stamps. The verdict never changes <see cref="Field"/>,
    /// so late drop-off evidence reads this to stop once the dispute is decided (FUP-373).
    /// </summary>
    public const string DisputeResolutionField = "disputeResolution";

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

        /// <summary>
        /// T8 with the trucker's out-of-range attempt (FUP-204): the system opened a dispute with this reason, which
        /// reputation rule R12 does not read; an admin decides through the verdict.
        /// </summary>
        public const string DropoffOutOfRange = "dropoff_out_of_range";

        /// <summary>The money could be neither confirmed nor frozen: the admin queue, nothing moved.</summary>
        public const string Queued = "queued";
    }

    /// <summary>The dispute reasons of the order and its delivery (also the admin queue reasons when nothing could move).</summary>
    public const string OverdueReason = Outcome.DeliveryOverdue;

    public const string SilentBuyerReason = Outcome.SilentBuyer;

    /// <summary>The dispute reason of a T8 whose trucker checked in out of range (<see cref="AttemptField"/>): no R12.</summary>
    public const string OutOfRangeReason = Outcome.DropoffOutOfRange;

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

    /// <summary>The stored out-of-range attempt (<see cref="AttemptField"/>), or null.</summary>
    public static IReadOnlyDictionary<string, object>? AttemptOf(IReadOnlyDictionary<string, object> order)
        => order.TryGetValue(AttemptField, out var raw) && raw is IReadOnlyDictionary<string, object> entry ? entry : null;

    /// <summary>Whether a stored check-in was measured within 5 km of a drop-off point (<c>distanceChecked</c>).</summary>
    public static bool IsMeasured(IReadOnlyDictionary<string, object> entry) => Bool(entry, PickupJudgement.EntryDistanceChecked);

    /// <summary>The trucker's out-of-range attempt received by T8, or null (<see cref="AttemptField"/>).</summary>
    public static IReadOnlyDictionary<string, object>? CountedAttempt(IReadOnlyDictionary<string, object> order)
        => order.TryGetValue(AttemptField, out var raw) && raw is IReadOnlyDictionary<string, object> entry
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

        return SilentBuyerDeadline(order, timings) is { } t9
            ? new OrderDeadline(t9 + PickupJudgement.SettleMargin, OrderDeadlines.Kind.SilentBuyer)
            : new OrderDeadline(deadline + PickupJudgement.SettleMargin, OrderDeadlines.Kind.DeliveryOverdue);
    }

    /// <summary>
    /// T9 without the settle margin: the counted check-in's receipt plus <see cref="OrderTimings.SilentBuyerWindow"/>,
    /// or null when there is no counted check-in. <see cref="Due"/> adds the margin to it.
    /// </summary>
    public static DateTime? SilentBuyerDeadline(IReadOnlyDictionary<string, object> order, OrderTimings timings)
        => CountedCheckIn(order) is { } entry && ReceivedAt(entry) is { } received
            ? received + timings.SilentBuyerWindow
            : null;

    /// <summary>Whether the check-in should read the order's own point live: none was stamped at pickup.</summary>
    public static bool ReadsLiveOrderPoint(IReadOnlyDictionary<string, object> order)
        => TripTrack.PointOf(order, OrderLatitudeField, OrderLongitudeField) == null;

    /// <summary>
    /// Whether the check-in should read the trip's destination live: none was stamped and the stamp could not
    /// read the trip (<see cref="TripReadField"/>). A trip read at pickup without a destination stays without one.
    /// </summary>
    public static bool ReadsLiveTripPoint(IReadOnlyDictionary<string, object> order)
        => TripTrack.PointOf(order, TripLatitudeField, TripLongitudeField) == null && !Bool(order, TripReadField);

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
    /// pending judgement, T8 counted from <paramref name="pickedUpAt"/> (never before <paramref name="nowUtc"/>
    /// plus <see cref="LateStampGrace"/>, so a stamp that lands late still opens a check-in window), and the
    /// drop-off points known now (the order's own, the trip's destination). A point that is not known is
    /// left out; <paramref name="tripRead"/> records that the trip was read (with or without a destination).
    /// </summary>
    public static Dictionary<string, object> PickupStamp(
        DateTime pickedUpAt, TrackPoint? orderPoint, TrackPoint? tripPoint, bool tripRead, OrderTimings timings, DateTime nowUtc)
    {
        var t8 = pickedUpAt + timings.DeliveryOverdue;
        if (t8 < nowUtc + LateStampGrace)
        {
            t8 = nowUtc + LateStampGrace;
        }

        var fields = new Dictionary<string, object>
        {
            { Field, Outcome.Pending },
            { DeadlineField, OrderCommitment.Iso(t8) }
        };
        if (tripRead)
        {
            fields[TripReadField] = true;
        }

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
        /// <summary>T8: no counted drop-off check-in and no out-of-range attempt.</summary>
        DeliveryOverdue,

        /// <summary>T8 with an out-of-range attempt by the trucker (<see cref="AttemptField"/>): the dispute <c>dropoff_out_of_range</c>.</summary>
        OutOfRangeReview,

        /// <summary>T9: everything is in place to confirm for the silent buyer.</summary>
        AutoConfirm,

        /// <summary>T9: something is missing; a dispute.</summary>
        SilentBuyerDispute
    }

    /// <summary>
    /// The decision on a pending order whose delivery is still on the road, at the due time
    /// <see cref="Due"/> gave. FUP-204: with a counted check-in it is never T8, whatever the buyer did (a
    /// buyer who withholds the scan makes a T9). Without one, T8; but when the trucker tried to check in by
    /// T8 and was out of range (the only point may be the buyer's own pin), the dispute has its own reason,
    /// which R12 does not read, so the buyer's pin cannot put <c>DELIVERY_OVERDUE</c> on a trucker who came.
    /// </summary>
    public static Decision Decide(
        IReadOnlyDictionary<string, object> order, bool arrivalPhoto, bool truckerHeld, bool escrowPlain)
    {
        if (CountedCheckIn(order) is not { } entry)
        {
            return CountedAttempt(order) != null ? Decision.OutOfRangeReview : Decision.DeliveryOverdue;
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

    // ── the silent buyer's reminder and the pushes (FUP-338) ─────────────────

    /// <summary>
    /// FUP-338: server-only order field, when the order cron stamped the silent buyer's reminder (ISO 8601 UTC):
    /// <see cref="OrderTimings.ReminderLead"/> before T9, once per order (the cron stamps it only while it is absent, in a
    /// write conditioned on the status and the pending judgement). The order notifier tells the buyer only when it
    /// appears, so a second stamp would tell nobody.
    /// </summary>
    public const string ReminderAtField = "dropoffReminderAt";

    /// <summary>
    /// FUP-338: when the silent buyer's reminder is due: T9 (<see cref="SilentBuyerDeadline"/>) less
    /// <see cref="OrderTimings.ReminderLead"/>, on a pending order with a counted check-in and no reminder stamped yet.
    /// Null otherwise, and when that time is not after the check-in's receipt (a lead as long as the window: the
    /// arrival push already said when).
    /// </summary>
    public static DateTime? ReminderDueAt(IReadOnlyDictionary<string, object> order, OrderTimings timings)
    {
        if (!IsPending(order)
            || Str(order, ReminderAtField).Length > 0
            || CountedCheckIn(order) is not { } entry
            || ReceivedAt(entry) is not { } received
            || SilentBuyerDeadline(order, timings) is not { } t9)
        {
            return null;
        }

        var at = t9 - timings.ReminderLead;
        return at > received ? at : null;
    }

    /// <summary>
    /// FUP-338: the reason of a dispute the order cron opened, as its escrow carries it: the
    /// <c>disputeDescription</c> the cron writes starts with the reason code and a colon (the escrow's own
    /// <c>disputeReason</c> stays <c>other</c>). Null for any other text. The order notifier tells the three parties
    /// such a dispute from the order row, with its reason, and not a second time from the escrow row.
    /// </summary>
    public static string? DisputeReasonOfDescription(string? description)
    {
        if (string.IsNullOrEmpty(description))
        {
            return null;
        }

        foreach (var reason in new[] { OverdueReason, SilentBuyerReason, OutOfRangeReason })
        {
            if (description.StartsWith(reason + ":", StringComparison.Ordinal))
            {
                return reason;
            }
        }

        return null;
    }

    /// <summary>
    /// The system's dispute reason a delivery carries (<see cref="OverdueReason"/>, <see cref="OutOfRangeReason"/> or
    /// <see cref="SilentBuyerReason"/>), when the order cron opened it; else null.
    /// </summary>
    public static string? SystemDisputeReasonOf(IReadOnlyDictionary<string, object> delivery)
        => Str(delivery, "status") == "disputed"
           && Str(delivery, "disputedBy") == SystemActor
           && Str(delivery, "disputeReason") is OverdueReason or SilentBuyerReason or OutOfRangeReason
            ? Str(delivery, "disputeReason")
            : null;

    /// <summary>
    /// Whether the trucker's arrival photo is taken for the delivery: while the goods are on the road
    /// (<see cref="OnTheRoadDeliveryStatuses"/>), and (FUP-373) as late evidence (<see cref="TakesLateArrivalPhoto"/>).
    /// </summary>
    public static bool TakesArrivalPhoto(IReadOnlyDictionary<string, object> delivery)
        => OnTheRoadDeliveryStatuses.Contains(Str(delivery, "status")) || TakesLateArrivalPhoto(delivery);

    /// <summary>
    /// FUP-373: whether the arrival photo is taken as late evidence: on the T8 dispute the order cron opened
    /// (<see cref="OverdueReason"/> or <see cref="OutOfRangeReason"/>) that no admin has decided yet (no
    /// <c>disputeResolution</c>). A check-in the trucker made offline at the drop-off before T8 may reach the
    /// server after the T8 run, its photo first. The attach writes the photo with <see cref="ArrivalPhotoLateAtField"/>
    /// and never the status, and the admin sees it marked late in the dispute evidence: it was attached after the
    /// money froze, so it proves less than one taken on the road. T9's <see cref="SilentBuyerReason"/> takes none:
    /// a counted check-in always had its photo.
    /// </summary>
    public static bool TakesLateArrivalPhoto(IReadOnlyDictionary<string, object> delivery)
        => SystemDisputeReasonOf(delivery) is OverdueReason or OutOfRangeReason
           && Str(delivery, DisputeResolutionField).Length == 0;

    /// <summary>
    /// FUP-140: whether the buyer's drop-off photo is taken as late evidence on a delivery the buyer never confirmed:
    /// on a dispute the order cron opened (T8's <see cref="OverdueReason"/> or <see cref="OutOfRangeReason"/>, T9's
    /// <see cref="SilentBuyerReason"/>) that no admin has decided yet. A buyer who confirmed offline before the run
    /// gets the confirm refused when it syncs after, but the photo shows what arrived. The attach writes it with
    /// <see cref="DropoffPhotoLateAtField"/> and never the status, as the late arrival photo.
    /// </summary>
    public static bool TakesLateDropoffPhoto(IReadOnlyDictionary<string, object> delivery)
        => SystemDisputeReasonOf(delivery) != null && Str(delivery, DisputeResolutionField).Length == 0;

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

    /// <summary>
    /// FUP-370: <see cref="ConfirmAtField"/> as <paramref name="callerId"/> reads it: T9
    /// (<see cref="SilentBuyerDeadline"/>, ISO 8601 UTC) to the order's buyer, farmer or trucker while the order is
    /// on the road, waits for its judgement and has a counted check-in. Null to anyone else, on any other order,
    /// and with the judgement off (<paramref name="timings"/> null). The timings are the ones the order cron judges
    /// by (<see cref="Due"/>), read only when there is a T9 to show.
    /// </summary>
    public static string? ConfirmAtFor(IReadOnlyDictionary<string, object> order, string? callerId, Func<OrderTimings>? timings)
        => timings != null
           && IsParty(order, callerId)
           && IsPending(order)
           && OnTheRoadOrderStatuses.Contains(Str(order, "status"))
           && CountedCheckIn(order) != null
           && SilentBuyerDeadline(order, timings()) is { } t9
            ? OrderCommitment.Iso(t9)
            : null;

    /// <summary>
    /// FUP-370: a sync result whose order row goes back to <paramref name="callerId"/>, with
    /// <see cref="ConfirmAtField"/> added when that caller reads one (<see cref="ConfirmAtFor"/>). It reads the
    /// stored check-in, so it runs before <see cref="PickupJudgement.ForClient"/> takes the raw one out. Any other
    /// result is returned unchanged.
    /// </summary>
    public static SyncOperationResult WithConfirmAt(SyncOperationResult result, string? callerId, Func<OrderTimings>? timings)
        => result.ServerData is { } row && ConfirmAtFor(row, callerId, timings) is { } t9
            ? result with { ServerData = new Dictionary<string, object>(row) { [ConfirmAtField] = t9 } }
            : result;

    private static bool IsParty(IReadOnlyDictionary<string, object> order, string? callerId)
        => !string.IsNullOrEmpty(callerId)
           && (callerId == Str(order, "buyerId") || callerId == Str(order, "farmerId") || callerId == Str(order, "truckerId"));

    /// <summary>The server-only fields a client never reads back raw (the view replaces the check-in).</summary>
    public static readonly string[] HiddenFields =
    {
        CheckInField, AttemptField, TripReadField, OrderLatitudeField, OrderLongitudeField, TripLatitudeField, TripLongitudeField
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
