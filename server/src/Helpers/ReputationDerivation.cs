using System.Globalization;
using WeredaConnect.Server.Models;
using static WeredaConnect.Server.Services.OrderCommitment;

namespace WeredaConnect.Server.Helpers;

/// <summary>
/// ESC-28: the reputation events one order change yields (reputation-architecture.md 7.2, rules
/// R1 to R13), as a pure function of the record's old and new images. Each event is derived from
/// that one record alone, so arrival order cannot change what a record means (principle 3).
///
/// Rules that hold for every record:
/// - a response field missing from the old image (an order from before <c>COMMITMENTS_ENABLED</c>)
///   never yields a penalty (decision R25);
/// - the farmer's two cancels are told apart by the old status, never by <c>faultParty</c>;
/// - not events: <c>trip_full</c> and <c>failed_capacity</c> (a race the system caused), the buyer
///   and system cancels, a trucker who was never asked, and a farmer's <c>rejected</c> through a
///   dispute verdict.
/// R10 to R12 (P3) read values only ESC-34 and ESC-36 write: ESC-34's pickup judgement writes R10's and
/// R11's (only behind <c>pickupClassification</c>); ESC-36's T8 writes R12's (only behind
/// <c>dropoffClassification</c>). ESC-36's T9 (<c>disputeReason: silent_buyer</c>, or a system confirm) is
/// no event.
/// </summary>
public static class ReputationDerivation
{
    /// <summary>The <c>cancelReason</c> of a farmer's committed cancel (order-lifecycle.md 2.0.0).</summary>
    public const string FarmerCancelledReason = "farmer_cancelled";

    /// <summary>P3 (ESC-34): the farmer judged a no-show at pickup.</summary>
    public const string FarmerNoShowReason = "farmer_no_show";

    /// <summary>P3 (ESC-34): the trucker judged a no-show at pickup.</summary>
    public const string TruckerNoShowFailure = "trucker_no_show";

    /// <summary>P3 (ESC-36): the order field naming why it went to <c>disputed</c>; ESC-36 must keep it in step.</summary>
    public const string DisputeReasonField = "disputeReason";

    public const string DeliveryOverdueReason = "delivery_overdue";

    /// <summary>
    /// The candidates of one order record. <paramref name="recordTimeUtc"/> is the record's
    /// <c>ApproximateCreationDateTime</c>, the fallback for <c>occurredAt</c> when the new image has
    /// no <c>updatedAt</c>.
    /// </summary>
    public static IReadOnlyList<ReputationCandidate> Derive(
        StreamImage old, StreamImage row, DateTime? recordTimeUtc, string? sequenceNumber)
    {
        var candidates = new List<ReputationCandidate>();
        if (!old.Exists || !row.Exists)
        {
            return candidates;
        }

        var orderId = row.String("orderId");
        if (orderId.Length == 0)
        {
            return candidates;
        }

        var occurredAt = ReputationPolicy.TryParseUtc(row.String("updatedAt"), out var updated)
            ? updated
            : recordTimeUtc;
        if (occurredAt is not { } at)
        {
            return candidates; // no server time to judge windows by: nothing is recorded
        }

        var farmerId = row.String("farmerId");
        var buyerId = row.String("buyerId");
        var pickupDate = NonEmpty(row.String(PickupDateField)) ?? NonEmpty(old.String(PickupDateField));

        ReputationCandidate Candidate(string userId, string party, string slot, string code, string? tripId = null) => new()
        {
            UserId = userId,
            EventId = slot,
            Code = code,
            Party = party,
            OrderId = orderId,
            TripId = tripId,
            BuyerId = NonEmpty(buyerId),
            PickupDate = pickupDate,
            OccurredAt = at,
            SourceSeq = sequenceNumber
        };

        void Add(ReputationCandidate? candidate)
        {
            if (candidate != null && candidate.UserId.Length > 0)
            {
                candidates.Add(candidate);
            }
        }

        var oldFarmer = old.String(FarmerResponseField);
        var newFarmer = row.String(FarmerResponseField);
        var oldTrucker = old.String(TruckerResponseField);
        var newTrucker = row.String(TruckerResponseField);
        var oldTruckerId = old.String("truckerId");
        var failure = row.String(TransportFailureField);
        var oldStatus = old.String("status");
        var newStatus = row.String("status");
        var cancelReason = row.String("cancelReason");

        // R1, R2: the farmer declined or let the request expire.
        if (old.Has(FarmerResponseField) && oldFarmer != newFarmer)
        {
            if (newFarmer == Response.Declined)
            {
                Add(Candidate(farmerId, Party.Farmer, ReputationSlots.Request + orderId, ReputationCodes.DeclinedRequest));
            }
            else if (newFarmer == Response.Expired)
            {
                Add(Candidate(farmerId, Party.Farmer, ReputationSlots.Request + orderId, ReputationCodes.ResponseExpired) with
                {
                    RequestWindow = Window(row, old, "paymentVerifiedAt", FarmerDeadlineAtField)
                });
            }
        }

        // R3 to R7: the trucker's answer or drop-out.
        if (oldTrucker == Response.Pending)
        {
            if (newTrucker == Response.Declined)
            {
                Add(Candidate(oldTruckerId, Party.Trucker, ReputationSlots.Request + orderId, ReputationCodes.DeclinedRequest));
            }
            else if (newTrucker == Response.Expired)
            {
                Add(Candidate(oldTruckerId, Party.Trucker, ReputationSlots.Request + orderId, ReputationCodes.ResponseExpired) with
                {
                    RequestWindow = Window(row, old, TruckerAskedAtField, TruckerDeadlineAtField)
                });
            }
            else if (newTrucker == Response.Cancelled && failure == TransportFailure.TripCancelled)
            {
                // Spec B9: a pending request closed by cancelling its trip is a decline.
                Add(Candidate(oldTruckerId, Party.Trucker, ReputationSlots.Request + orderId, ReputationCodes.DeclinedRequest));
            }
        }
        else if (oldTrucker == Response.Accepted && newTrucker == Response.Cancelled)
        {
            if (failure == TransportFailure.TruckerCancelled)
            {
                Add(Candidate(oldTruckerId, Party.Trucker, ReputationSlots.Cancel + orderId, ReputationCodes.CancelledAfterAccept));
            }
            else if (failure == TransportFailure.TripCancelled)
            {
                // One event per trip: the other orders of the same cancel hit the existing slot.
                var tripId = NonEmpty(old.String("tripId")) ?? NonEmpty(row.String("tripId"));
                if (tripId != null)
                {
                    Add(Candidate(oldTruckerId, Party.Trucker, ReputationSlots.TripCancel + tripId,
                        ReputationCodes.TripCancelledWithBookings, tripId));
                }
            }
        }

        // R8, R9: the farmer's cancel, told apart by the old status. R25: only committed orders.
        if (newStatus == "cancelled" && cancelReason == FarmerCancelledReason && old.Has(FarmerResponseField))
        {
            if (oldStatus == "accepted")
            {
                Add(Candidate(farmerId, Party.Farmer, ReputationSlots.Cancel + orderId, ReputationCodes.CancelledAfterAccept));
            }
            else if (oldStatus == AwaitingTransportStatus)
            {
                Add(Candidate(farmerId, Party.Farmer, ReputationSlots.Cancel + orderId, ReputationCodes.CancelledTransportFailed));
            }
        }

        // R10 (P3): the trucker was judged a no-show. It rests on the trucker's own missing check-in
        // alone (FUP-204, fraud spec 15.3): no other party's report can cause it.
        if (failure == TruckerNoShowFailure && old.String(TransportFailureField) != TruckerNoShowFailure)
        {
            Add(Candidate(oldTruckerId, Party.Trucker, ReputationSlots.NoShow + orderId, ReputationCodes.NoShow));
        }

        // R11 (P3): the farmer was judged a no-show. FUP-204: the judgement rests on the other side's
        // arrival check-in (the trucker's, or the self-pickup buyer's), so that party is the reporter.
        if (cancelReason == FarmerNoShowReason && oldStatus != "cancelled")
        {
            var reporter = PickupJudgement.IsSelfPickup(row.String(FulfillmentField), row.String("tripId"), row.String("truckerId"))
                ? buyerId
                : row.String("truckerId");
            Add(Candidate(farmerId, Party.Farmer, ReputationSlots.NoShow + orderId, ReputationCodes.NoShow) with
            {
                ReportedBy = NonEmpty(reporter)
            });
        }

        // R12 (P3): no delivery by T8. FUP-204: ESC-36's T8 rests on the trucker's own missing drop-off
        // check-in alone (a buyer who withholds the scan after the check-in makes a T9, silent_buyer, which is
        // no event), so no other party is its reporter.
        if (newStatus == "disputed" && oldStatus != "disputed" && row.String(DisputeReasonField) == DeliveryOverdueReason)
        {
            Add(Candidate(row.String("truckerId"), Party.Trucker, ReputationSlots.Overdue + orderId, ReputationCodes.DeliveryOverdue));
        }

        // R13: the order settled; the trucker too when they carried it.
        if (newStatus == "settled" && oldStatus != "settled")
        {
            var total = row.Number("orderTotal") is { } value ? (decimal?)Convert.ToDecimal(value, CultureInfo.InvariantCulture) : null;
            Add(Candidate(farmerId, Party.Farmer, ReputationSlots.Completed + orderId, ReputationCodes.OrderCompleted) with
            {
                OrderTotal = total
            });
            if (row.String(FulfillmentField) == Fulfillment.Trucker && newTrucker == Response.Accepted)
            {
                Add(Candidate(row.String("truckerId"), Party.Trucker, ReputationSlots.Completed + orderId, ReputationCodes.OrderCompleted) with
                {
                    OrderTotal = total
                });
            }
        }

        return candidates;
    }

    /// <summary>The request window <c>end - start</c> from the new image (the old one as fallback); null when either is missing.</summary>
    private static TimeSpan? Window(StreamImage row, StreamImage old, string startField, string endField)
    {
        var start = NonEmpty(row.String(startField)) ?? NonEmpty(old.String(startField));
        var end = NonEmpty(row.String(endField)) ?? NonEmpty(old.String(endField));
        return ReputationPolicy.TryParseUtc(start, out var s) && ReputationPolicy.TryParseUtc(end, out var e) ? e - s : null;
    }

    private static string? NonEmpty(string value) => value.Length > 0 ? value : null;

    /// <summary>
    /// The order attributes the derivation reads: what a parked record keeps of each image
    /// (architecture doc 7.5), well under 2 KB.
    /// </summary>
    public static readonly IReadOnlyList<string> ProjectedAttributes = new[]
    {
        "orderId", "createdAt", "status", FarmerResponseField, TruckerResponseField, TransportFailureField, "cancelReason",
        CancelledByField, FaultPartyField, "farmerId", "truckerId", "buyerId", "tripId", FulfillmentField, PickupDateField,
        "orderTotal", "paymentVerifiedAt", FarmerDeadlineAtField, TruckerAskedAtField, TruckerDeadlineAtField, "updatedAt",
        DisputeReasonField
    };
}
