using System.Globalization;
using Amazon.DynamoDBv2.Model;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.Services;

namespace WeredaConnect.Server.Helpers;

/// <summary>
/// FUP-330 (delivery-verification-api.md 2.11.0 P.2 "Re-open of a trucker no-show", order-lifecycle.md 2.20.0): an
/// admin re-opens a judged trucker no-show after reconciling a real late scan, while the buyer has not chosen yet and
/// the escrow still simply holds the money. The rules, as pure functions of the rows read; <c>AdminFunction</c> does
/// the reads, the one transaction and the excuse of the trucker's <c>NO_SHOW</c>.
/// <para>
/// The judgement left the order in <c>awaiting_transport</c> (<c>transportFailure: trucker_no_show</c>,
/// <c>truckerResponse: no_show</c>, <c>faultParty: trucker</c>, <c>buyerChoiceDeadlineAt</c>), its pending delivery
/// <c>cancelled</c> (no <c>cancelReason</c>), its <c>activeDeliveryId</c> slot and its trip booking released. The
/// re-open brings the transport back in one transaction: <c>handoff_confirmed</c> moves that delivery
/// <c>cancelled → pickedUp</c>, claims the slot for it and takes the order to <c>pickup_confirmed</c> (judged
/// <c>scanned</c>, and with <c>dropoffClassification</c> on stamped for the drop-off judgement as a pickup is);
/// <c>pending</c> takes the order back to <c>accepted</c> with <c>pickupJudgement: pending</c>, a new deadline and
/// empty check-ins, the state right after the trucker's accept, so the trucker's own delivery create makes the new
/// pending delivery (the create claims the slot and links the escrow, as after any cancelled delivery) and the new
/// window counts fresh check-ins (the cleared ones go to the audit row, <see cref="CheckInsNote"/>). Both take the
/// trip booking back (refused when the trip is gone, cancelled, departed or full).
/// </para>
/// </summary>
public static class NoShowReopen
{
    /// <summary>The <c>{action}</c> of <c>POST /admin/orders/{orderId}/{action}</c>.</summary>
    public const string Action = "reopen-no-show";

    /// <summary>409: the order's trip cannot take the booking back (gone, cancelled, departed or full).</summary>
    public const string TripUnavailableCode = "PICKUP_REOPEN_TRIP_UNAVAILABLE";

    /// <summary>The log token of an applied no-show re-open.</summary>
    public const string LogToken = "PICKUP_NO_SHOW_REOPENED";

    /// <summary>
    /// The note of the excuse the re-open makes on the trucker's <c>NO_SHOW</c> (ESC-32's decision path). The trucker
    /// reads it in their event list; the admin's reason stays in the audit trail only.
    /// </summary>
    public const string ExcuseNote = "Excused: support re-opened this pickup after checking a scan that reached the server late.";

    /// <summary>What happened to the trucker's <c>NO_SHOW</c> event (the answer's <c>noShowEvent</c>).</summary>
    public static class NoShowEvent
    {
        /// <summary>Excused by this request (audited <c>reputation_excused</c>).</summary>
        public const string Excused = "excused";

        /// <summary>Already excused (an earlier re-open or an admin on the trucker's reputation page).</summary>
        public const string AlreadyExcused = "already_excused";

        /// <summary>No such event: the stream has not recorded one for this order (none is while penalties are off).</summary>
        public const string NotRecorded = "not_recorded";

        /// <summary>The event no longer counts (its points expired): nothing to excuse.</summary>
        public const string NotCounted = "not_counted";

        /// <summary>An admin upheld it before: it stays (an upheld event cannot be excused).</summary>
        public const string Upheld = "upheld";

        /// <summary><c>reputationPenalties</c> is off here (or the table is not configured): nothing is recorded.</summary>
        public const string Off = "off";

        /// <summary>The excuse could not be written: the same request again retries it.</summary>
        public const string Failed = "failed";
    }

    /// <summary>What the transaction does on the trip (<see cref="TripPlanOf"/>).</summary>
    public enum TripPlan
    {
        /// <summary>The release took the booking: book the order's quintals again, conditioned on the room left.</summary>
        Reserve,

        /// <summary>The release never landed (best effort): the booking stands, only the accept is marked again.</summary>
        KeepBooking
    }

    private const double KgPerQuintal = 100.0;

    /// <summary>Why the order's no-show cannot be re-opened (the 409's message), or null when it can.</summary>
    public static string? RefusalOf(IReadOnlyDictionary<string, object> order)
    {
        var judgement = Str(order, PickupJudgement.Field);
        if (judgement != PickupJudgement.Outcome.TruckerNoShow || !PickupJudgement.IsJudged(order))
        {
            return PickupReopen.IsReopenableOutcome(judgement)
                ? "This judgement left the order accepted: use Re-open the pickup instead."
                : "Only a pickup judged a trucker no-show can be re-opened here.";
        }

        var status = Str(order, "status");
        if (status != OrderCommitment.AwaitingTransportStatus)
        {
            return $"The order is {status} now: the buyer already chose, or the order moved on, so the no-show cannot be re-opened.";
        }

        if (Str(order, OrderCommitment.TransportFailureField) != PickupJudgement.TruckerNoShowFailure
            || Str(order, OrderCommitment.TruckerResponseField) != PickupJudgement.NoShowResponse)
        {
            return "The order's transport changed since the judgement, so the no-show cannot be re-opened.";
        }

        if (PickupJudgement.IsSelfPickup(order) || Str(order, "tripId").Length == 0 || Str(order, "truckerId").Length == 0)
        {
            return "The order names no trip and trucker to bring back.";
        }

        return QuintalsOf(order) > 0 ? null : "The order has no quantity to book on the trip again.";
    }

    /// <summary>
    /// The delivery a confirmed handoff brings back: the one the order escrow links (the order's delivery on the admin
    /// page, linked at its create and never unlinked by a cancel), else the one the order's slot still names (a release
    /// that never landed). Empty when there is none.
    /// </summary>
    public static string DeliveryIdOf(IReadOnlyDictionary<string, object> order, IReadOnlyDictionary<string, object>? escrow)
        => escrow != null && Str(escrow, "deliveryId") is { Length: > 0 } linked ? linked : Str(order, "activeDeliveryId");

    /// <summary>
    /// Why the admin cannot confirm the handoff (the 409's message), or null when it can. <paramref name="delivery"/>
    /// is the delivery of <see cref="DeliveryIdOf"/> as read: it must be this order's, on its trip, and the one the
    /// judgement cancelled: <c>cancelled</c> with no <c>cancelReason</c> (an admin cancel after pickup and a verdict
    /// disposition write one), no verdict and no soft delete, cancelled at or after <c>pickupJudgedAt</c> (the judgement
    /// cancels the pending delivery first, in the same run, so a party's cancel before it is never brought back). No
    /// other delivery may hold the order's slot.
    /// </summary>
    public static string? HandoffRefusalOf(IReadOnlyDictionary<string, object> order, IReadOnlyDictionary<string, object>? delivery)
    {
        if (delivery == null)
        {
            return "The order has no delivery the judgement cancelled, so there is nothing to mark picked up.";
        }

        if (Str(delivery, "orderId") != Str(order, "orderId") || Str(delivery, "tripId") != Str(order, "tripId"))
        {
            return "The order's delivery is not this order's, or not on its trip.";
        }

        var status = Str(delivery, "status");
        if (status != "cancelled")
        {
            return $"The order's delivery is {status}: only the delivery the judgement cancelled can be marked picked up.";
        }

        if (Str(delivery, "cancelReason").Length > 0 || Str(delivery, "disputeResolution").Length > 0 || Str(delivery, "deletedAt").Length > 0)
        {
            return "The order's delivery was closed by support or a dispute, not by the judgement.";
        }

        if (!OrderCommitment.TryParseUtc(Str(delivery, "updatedAt"), out var cancelledAt)
            || !OrderCommitment.TryParseUtc(Str(order, PickupJudgement.JudgedAtField), out var judgedAt)
            || cancelledAt < judgedAt)
        {
            return "The order's delivery was cancelled before the judgement, not by it.";
        }

        var slot = Str(order, "activeDeliveryId");
        return slot.Length == 0 || slot == Str(delivery, "deliveryId")
            ? null
            : "Another delivery holds the order's slot.";
    }

    /// <summary>The exits the action takes for the order as read: none when its no-show cannot be re-opened.</summary>
    public static List<string> ExitsOf(IReadOnlyDictionary<string, object> order, IReadOnlyDictionary<string, object>? delivery)
    {
        var exits = new List<string>();
        if (RefusalOf(order) != null)
        {
            return exits;
        }

        exits.Add(PickupReopen.Exit.Pending);
        if (HandoffRefusalOf(order, delivery) == null)
        {
            exits.Add(PickupReopen.Exit.HandoffConfirmed);
        }

        return exits;
    }

    /// <summary>
    /// The trip as the transaction needs it, from its raw row (the booking sets are string sets, which the plain
    /// dictionary form drops). Null for a trip that is not there.
    /// </summary>
    public static ReopenTripState? TripStateOf(Dictionary<string, AttributeValue>? trip, string orderId)
    {
        if (trip == null)
        {
            return null;
        }

        return new ReopenTripState(
            Status: Raw(trip, "status"),
            CreatedAt: Raw(trip, "createdAt"),
            TruckerId: Raw(trip, "truckerId"),
            CancelReason: Raw(trip, "cancelReason"),
            Deleted: trip.ContainsKey("deletedAt"),
            Booked: trip.TryGetValue("bookedOrderIds", out var booked) && booked.SS != null && booked.SS.Contains(orderId),
            RemainingQuintals: Number(trip, "remainingQuintals") ?? Number(trip, "capacity") ?? 0.0);
    }

    /// <summary>
    /// What the transaction does on the trip, or why it cannot (the 409 <see cref="TripUnavailableCode"/>'s message).
    /// Only an <c>open</c> or <c>matched</c> trip of the order's trucker, not deleted, takes the booking back; one
    /// <c>inTransit</c> or <c>delivered</c> has departed. A trip that still holds the order's booking keeps it;
    /// otherwise its room must cover the order's quintals, compared exactly as the transaction's condition compares
    /// them, so a trip short by any amount is refused here rather than by the write.
    /// </summary>
    public static (TripPlan? Plan, string? Refusal) TripPlanOf(IReadOnlyDictionary<string, object> order, ReopenTripState? trip)
    {
        if (trip == null)
        {
            return (null, "The order's trip is gone.");
        }

        if (trip.Deleted || trip.Status == "cancelled")
        {
            return (null, trip.CancelReason == TripLifecycle.ExpiredCancelReason
                ? "The order's trip was closed: it left with no booking once the no-show released this one."
                : "The order's trip was cancelled.");
        }

        if (trip.Status is "inTransit" or "delivered")
        {
            return (null, $"The order's trip has departed ({trip.Status}).");
        }

        if (trip.Status is not ("open" or "matched"))
        {
            return (null, $"The order's trip is {trip.Status}.");
        }

        if (trip.TruckerId.Length > 0 && trip.TruckerId != Str(order, "truckerId"))
        {
            return (null, "The order's trip belongs to another trucker.");
        }

        if (trip.Booked)
        {
            return (TripPlan.KeepBooking, null);
        }

        var quintals = QuintalsOf(order);
        return trip.RemainingQuintals < quintals
            ? (null, string.Format(CultureInfo.InvariantCulture,
                "The order's trip is full: {0:0.##} quintals left, the order needs {1:0.##}.", trip.RemainingQuintals, quintals))
            : (TripPlan.Reserve, null);
    }

    /// <summary>The order's quantity in quintals (<c>quantity</c> is kilograms), 0 when missing or unreadable.</summary>
    public static double QuintalsOf(IReadOnlyDictionary<string, object> order)
        => order.TryGetValue("quantity", out var value)
           && double.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Any, CultureInfo.InvariantCulture, out var kg)
           && kg > 0
            ? kg / KgPerQuintal
            : 0.0;

    /// <summary>Whether the order already carries this re-open: a <see cref="PickupReopen.Stamp"/> from this judgement (a replay).</summary>
    public static bool IsReplay(IReadOnlyDictionary<string, object> order, int expectedVersion, string exit)
        => PickupReopen.IsReplay(order, expectedVersion, exit)
           && PickupReopen.LastReopenOf(order)?.FromJudgement == PickupJudgement.Outcome.TruckerNoShow;

    /// <summary>The fields of a check-in entry the audit row keeps, in this order.</summary>
    private static readonly string[] NotedEntryFields =
    {
        PickupJudgement.EntryReceivedAt, PickupJudgement.EntryCapturedAt, PickupJudgement.EntryBy, PickupJudgement.EntryLatitude,
        PickupJudgement.EntryLongitude, PickupJudgement.EntryAccuracyM, PickupJudgement.EntryDistanceKm,
        PickupJudgement.EntryDistanceChecked, PickupJudgement.EntryLate, PickupJudgement.EntryOutOfRange
    };

    /// <summary>
    /// The check-ins a <c>pending</c> re-open clears, for its audit row's <c>metadata</c>:
    /// <c>arrived_pickup:receivedAt=...,by=...,late=true|crops_ready:...</c> with every field the entry kept (the
    /// order's copy is gone after the write). Null when the order has none.
    /// </summary>
    public static string? CheckInsNote(IReadOnlyDictionary<string, object> order)
    {
        if (!order.TryGetValue(PickupJudgement.CheckInsField, out var raw) || raw is not IReadOnlyDictionary<string, object> map || map.Count == 0)
        {
            return null;
        }

        var kinds = new List<string>();
        foreach (var (kind, value) in map.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            if (value is not IReadOnlyDictionary<string, object> entry)
            {
                continue;
            }

            var fields = NotedEntryFields
                .Where(entry.ContainsKey)
                .Select(field => entry[field] is bool flag
                    ? $"{field}={(flag ? "true" : "false")}"
                    : $"{field}={Convert.ToString(entry[field], CultureInfo.InvariantCulture)}");
            kinds.Add($"{kind}:{string.Join(",", fields)}");
        }

        return kinds.Count == 0 ? null : string.Join("|", kinds);
    }

    /// <summary>The <c>NO_SHOW</c> slot the reputation stream records for the order's trucker (rule R10).</summary>
    public static string NoShowEventId(string orderId) => ReputationSlots.NoShow + orderId;

    private static string Str(IReadOnlyDictionary<string, object> item, string key) => PickupJudgement.Str(item, key);

    private static string Raw(Dictionary<string, AttributeValue> row, string key)
        => row.TryGetValue(key, out var value) ? value.S ?? string.Empty : string.Empty;

    private static double? Number(Dictionary<string, AttributeValue> row, string key)
        => row.TryGetValue(key, out var value)
           && double.TryParse(value.N ?? value.S, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
}

/// <summary>FUP-330: the trip of a no-show re-open as read (<see cref="NoShowReopen.TripStateOf"/>).</summary>
public sealed record ReopenTripState(
    string Status, string CreatedAt, string TruckerId, string CancelReason, bool Deleted, bool Booked, double RemainingQuintals);
