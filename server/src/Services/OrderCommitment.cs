using System.Globalization;

namespace WeredaConnect.Server.Services;

/// <summary>
/// ESC-15: the three-party commitment rules of a paid order, as pure decisions. The buyer's
/// money is held first (<c>funded</c>). Then the FARMER is asked; only once the farmer accepted
/// is the TRUCKER asked (the user's sequential rule: a trucker can never accept before the
/// farmer). The order becomes <c>accepted</c> when both agreed, or when the farmer agreed on a
/// self-pickup order. A failed transport (declined, expired, trip cancelled, trip full) parks a
/// funded order in <c>awaiting_transport</c>, where the buyer cancels for a full refund.
///
/// Everything here is deterministic on the row and the clock so the sync handler stays a thin
/// dispatch and every (status, farmer response, trucker response, actor, command) cell is
/// table-tested. Side effects (trip bookings, deliveries, refunds) are named by
/// <see cref="CommitmentEffect"/> and run by the caller.
/// </summary>
public static class OrderCommitment
{
    /// <summary>Order status while the transport is not settled and the buyer must decide.</summary>
    public const string AwaitingTransportStatus = "awaiting_transport";

    public const string FulfillmentField = "fulfillment";
    public const string PickupDateField = "pickupDate";
    public const string FarmerResponseField = "farmerResponse";
    public const string FarmerRespondedAtField = "farmerRespondedAt";
    public const string FarmerDeadlineAtField = "farmerDeadlineAt";
    public const string TruckerResponseField = "truckerResponse";
    public const string TruckerAskedAtField = "truckerAskedAt";
    public const string TruckerRespondedAtField = "truckerRespondedAt";
    public const string TruckerDeadlineAtField = "truckerDeadlineAt";
    public const string BuyerChoiceDeadlineAtField = "buyerChoiceDeadlineAt";
    public const string TransportFailureField = "transportFailure";
    public const string DeclineReasonField = "declineReason";
    public const string CancelledByField = "cancelledBy";
    public const string FaultPartyField = "faultParty";
    public const string FarmerReminderAtField = "farmerReminderAt";
    public const string TruckerReminderAtField = "truckerReminderAt";
    public const string BuyerChoiceReminderAtField = "buyerChoiceReminderAt";
    public const string PickupOverdueNotifiedAtField = "pickupOverdueNotifiedAt";

    /// <summary>Order fields only the server writes (stripped from every client payload).</summary>
    public static readonly string[] ServerOnlyFields =
    {
        FulfillmentField, PickupDateField,
        FarmerRespondedAtField, FarmerDeadlineAtField,
        TruckerAskedAtField, TruckerRespondedAtField, TruckerDeadlineAtField,
        BuyerChoiceDeadlineAtField, TransportFailureField,
        CancelledByField, FaultPartyField,
        FarmerReminderAtField, TruckerReminderAtField, BuyerChoiceReminderAtField, PickupOverdueNotifiedAtField
    };

    /// <summary>Values of <see cref="FarmerResponseField"/> and <see cref="TruckerResponseField"/>.</summary>
    public static class Response
    {
        public const string Pending = "pending";
        public const string Accepted = "accepted";
        public const string Declined = "declined";
        public const string Expired = "expired";

        /// <summary>The trucker cancelled after accepting, or cancelled the whole trip.</summary>
        public const string Cancelled = "cancelled";

        /// <summary>Trip order whose farmer has not accepted yet: the trucker is not asked.</summary>
        public const string NotAsked = "not_asked";

        /// <summary>The trip was full or gone when the money was held.</summary>
        public const string FailedCapacity = "failed_capacity";

        /// <summary>Self-pickup: there is no trucker.</summary>
        public const string None = "none";
    }

    public static class Fulfillment
    {
        public const string Trucker = "trucker";
        public const string SelfPickup = "self_pickup";
    }

    /// <summary>Values of <see cref="TransportFailureField"/>.</summary>
    public static class TransportFailure
    {
        public const string TruckerDeclined = "trucker_declined";
        public const string TruckerExpired = "trucker_expired";
        public const string TruckerCancelled = "trucker_cancelled";
        public const string TripCancelled = "trip_cancelled";
        public const string TripFull = "trip_full";
    }

    /// <summary>Values of <see cref="CancelledByField"/> and <see cref="FaultPartyField"/>.</summary>
    public static class Party
    {
        public const string Buyer = "buyer";
        public const string Farmer = "farmer";
        public const string Trucker = "trucker";
        public const string System = "system";
        public const string None = "none";
    }

    public static readonly IReadOnlySet<string> DeclineReasons = new HashSet<string>(StringComparer.Ordinal)
    {
        "date", "capacity", "price", "route", "crop_not_ready", "other"
    };

    /// <summary>
    /// Codes only the server writes into <c>cancelReason</c> (order-lifecycle.md 2.0.0). Sync moves a
    /// client text that spells one to <c>cancelNote</c>, and the notification rules trust only these,
    /// so no client cancel can pass for a server decision.
    /// </summary>
    public static readonly IReadOnlySet<string> ServerCancelReasons = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        Helpers.PickupDeadline.CancelReason, "escrow_refunded", "buyer_withdrew", "unpaid_expired",
        "farmer_response_expired", "farmer_cancelled", "farmer_no_show", "transport_not_arranged",
        "listing_out_of_stock", "buyer_no_show", AccountDeletionPolicy.CancelReason
    };

    /// <summary>Start of the sentence the unpaid-order cron writes into <c>cancelReason</c> (no code yet).</summary>
    public const string LegacyAutoCancelPrefix = "Auto-cancelled";

    public const string AcceptCommand = "accept";
    public const string DeclineCommand = "decline";

    /// <summary>Permanent refusal: the request is not open for this party (not asked yet, or already answered).</summary>
    public const string ResponseNotPendingCode = "ORDER_RESPONSE_NOT_PENDING";

    /// <summary>Permanent refusal: the party's response window closed (the cron settles the order).</summary>
    public const string DeadlinePassedCode = "ORDER_DEADLINE_PASSED";

    /// <summary>Audit actions of the commands.</summary>
    public const string FarmerAuditAction = "farmer_responded";
    public const string TruckerAuditAction = "trucker_responded";

    public static readonly TimeSpan ResponseWindow = TimeSpan.FromHours(24);
    public static readonly TimeSpan DepartureMargin = TimeSpan.FromHours(12);
    public static readonly TimeSpan MinWindow = TimeSpan.FromHours(1);
    public static readonly TimeSpan ReminderLead = TimeSpan.FromHours(6);
    public static readonly TimeSpan MinReminderWindow = TimeSpan.FromHours(12);
    public static readonly TimeSpan BuyerChoiceWindow = TimeSpan.FromHours(48);

    /// <summary>
    /// Env flag <c>COMMITMENTS_ENABLED</c>. Off, the farmer's accept moves the order straight to
    /// <c>accepted</c> as before and no trucker is asked. Read once per process.
    /// </summary>
    public static bool IsEnabled { get; } = string.Equals(
        Environment.GetEnvironmentVariable("COMMITMENTS_ENABLED"), "true", StringComparison.OrdinalIgnoreCase);

    public static string Iso(DateTime utc) => utc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);

    /// <summary>Reads a stored ISO time as UTC; false when missing or unreadable.</summary>
    public static bool TryParseUtc(string? raw, out DateTime utc)
    {
        utc = default;
        return !string.IsNullOrWhiteSpace(raw)
               && DateTime.TryParse(raw, CultureInfo.InvariantCulture,
                   DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out utc);
    }

    /// <summary>
    /// When a party's answer is due: 24 hours, but at least 12 hours before the trip leaves, and
    /// never less than one hour from now (an order funded close to departure still gets a window).
    /// </summary>
    public static DateTime ResponseDeadline(DateTime nowUtc, DateTime? departureUtc)
    {
        var deadline = nowUtc + ResponseWindow;
        if (departureUtc is { } departure && departure - DepartureMargin < deadline)
        {
            deadline = departure - DepartureMargin;
        }

        return deadline < nowUtc + MinWindow ? nowUtc + MinWindow : deadline;
    }

    public static DateTime BuyerChoiceDeadline(DateTime nowUtc) => nowUtc + BuyerChoiceWindow;

    /// <summary>A trucker order: the server binds <c>tripId</c> and <c>truckerId</c> together, but a row from before that rule may carry only the trucker.</summary>
    public static bool HasTrip(IReadOnlyDictionary<string, object> order)
        => Str(order, "tripId").Length > 0 || Str(order, "truckerId").Length > 0;

    /// <summary>The farmer's response; a funded row written before this feature counts as pending.</summary>
    public static string FarmerResponseOf(IReadOnlyDictionary<string, object> order)
    {
        var stored = Str(order, FarmerResponseField);
        if (stored.Length > 0)
        {
            return stored;
        }

        var status = Str(order, "status");
        return status is "funded" or AwaitingTransportStatus ? Response.Pending : Response.Accepted;
    }

    /// <summary>The trucker's response; a row written before this feature derives it from the trip and status.</summary>
    public static string TruckerResponseOf(IReadOnlyDictionary<string, object> order)
    {
        var stored = Str(order, TruckerResponseField);
        if (stored.Length > 0)
        {
            return stored;
        }

        if (!HasTrip(order))
        {
            return Response.None;
        }

        // Spec K2: an accepted trip order from before this feature counts as trucker-accepted.
        return Str(order, "status") is "funded" or AwaitingTransportStatus ? Response.NotAsked : Response.Accepted;
    }

    /// <summary>
    /// The fields every funded order gets in the funding write (mock and live alike): who is
    /// asked first and until when, and that the trucker waits for the farmer.
    /// </summary>
    public static Dictionary<string, object> FundingStamps(
        IReadOnlyDictionary<string, object> order, DateTime nowUtc, DateTime? departureUtc)
    {
        var hasTrip = HasTrip(order);
        var stamps = new Dictionary<string, object>
        {
            { FulfillmentField, hasTrip ? Fulfillment.Trucker : Fulfillment.SelfPickup },
            { FarmerResponseField, Response.Pending },
            { FarmerDeadlineAtField, Iso(ResponseDeadline(nowUtc, departureUtc)) },
            { TruckerResponseField, hasTrip ? Response.NotAsked : Response.None }
        };
        if (departureUtc is { } departure)
        {
            stamps[PickupDateField] = Iso(departure);
        }

        return stamps;
    }

    /// <summary>The farmer's deadline, or <c>paymentVerifiedAt + 24h</c> for a row without the stamp.</summary>
    public static DateTime? FarmerDeadlineOf(IReadOnlyDictionary<string, object> order)
    {
        if (TryParseUtc(Str(order, FarmerDeadlineAtField), out var stamped))
        {
            return stamped;
        }

        return TryParseUtc(Str(order, "paymentVerifiedAt"), out var funded) ? funded + ResponseWindow : null;
    }

    /// <summary>The trucker's deadline, or <c>truckerAskedAt + 24h</c> for a row without the stamp.</summary>
    public static DateTime? TruckerDeadlineOf(IReadOnlyDictionary<string, object> order)
    {
        if (TryParseUtc(Str(order, TruckerDeadlineAtField), out var stamped))
        {
            return stamped;
        }

        return TryParseUtc(Str(order, TruckerAskedAtField), out var asked) ? asked + ResponseWindow : null;
    }

    /// <summary>The buyer's decision deadline, or <c>updatedAt + 48h</c> for a row without the stamp.</summary>
    public static DateTime? BuyerChoiceDeadlineOf(IReadOnlyDictionary<string, object> order)
    {
        if (TryParseUtc(Str(order, BuyerChoiceDeadlineAtField), out var stamped))
        {
            return stamped;
        }

        return TryParseUtc(Str(order, "updatedAt"), out var updated) ? updated + BuyerChoiceWindow : null;
    }

    /// <summary>
    /// Fields that move a funded or accepted order to <c>awaiting_transport</c> after the transport
    /// failed: the buyer gets 48 hours to decide, and who failed is recorded for reputation.
    /// </summary>
    public static Dictionary<string, object> TransportFailedFields(
        string failure, string truckerResponse, DateTime nowUtc, string faultParty = Party.Trucker)
        => new()
        {
            { "status", AwaitingTransportStatus },
            { TruckerResponseField, truckerResponse },
            { TransportFailureField, failure },
            { BuyerChoiceDeadlineAtField, Iso(BuyerChoiceDeadline(nowUtc)) },
            { FaultPartyField, faultParty }
        };

    /// <summary>
    /// Decide a farmer or trucker command against the row. <paramref name="departureUtc"/> is the
    /// trip's departure (null for self-pickup or an unreadable trip); it bounds the trucker's
    /// window when the farmer accepts. <paramref name="freshSignIn"/> says whether the caller
    /// authenticated recently (AUTH_040), needed to drop out of a committed order.
    /// </summary>
    public static CommitmentDecision Decide(
        IReadOnlyDictionary<string, object> order,
        CommitmentActor actor,
        string command,
        DateTime nowUtc,
        DateTime? departureUtc,
        string? declineReason,
        bool freshSignIn)
    {
        if (command is not (AcceptCommand or DeclineCommand))
        {
            return CommitmentDecision.Error("ORDER_VALIDATION", $"Unknown command '{command}'. Send accept or decline.");
        }

        if (declineReason != null && !DeclineReasons.Contains(declineReason))
        {
            return CommitmentDecision.Error("ORDER_VALIDATION",
                $"declineReason must be one of {string.Join(", ", DeclineReasons)}.");
        }

        var status = Str(order, "status");
        var farmer = FarmerResponseOf(order);
        var trucker = TruckerResponseOf(order);
        var hasTrip = HasTrip(order);
        var now = Iso(nowUtc);

        return actor switch
        {
            CommitmentActor.Farmer => DecideFarmer(status, farmer, trucker, hasTrip, command, order, nowUtc, departureUtc, declineReason, now),
            CommitmentActor.Trucker => DecideTrucker(status, farmer, trucker, command, order, nowUtc, declineReason, freshSignIn, now),
            _ => CommitmentDecision.Error("ORDER_VALIDATION", "Unknown actor.")
        };
    }

    private static CommitmentDecision DecideFarmer(
        string status, string farmer, string trucker, bool hasTrip, string command,
        IReadOnlyDictionary<string, object> order, DateTime nowUtc, DateTime? departureUtc,
        string? declineReason, string now)
    {
        if (status != "funded")
        {
            if (status is "accepted" or AwaitingTransportStatus && farmer == Response.Accepted && command == AcceptCommand)
            {
                return CommitmentDecision.NoOp();
            }

            return CommitmentDecision.Error("ORDER_VALIDATION",
                $"Invalid status transition from '{status}' to '{(command == AcceptCommand ? "accepted" : "rejected")}'.");
        }

        if (farmer == Response.Accepted)
        {
            return command == AcceptCommand
                ? CommitmentDecision.NoOp()
                : CommitmentDecision.Error(ResponseNotPendingCode, "You already accepted this order. Cancel it instead.");
        }

        if (farmer != Response.Pending)
        {
            return CommitmentDecision.Error(ResponseNotPendingCode, "This order was already answered.");
        }

        if (command == DeclineCommand)
        {
            var fields = new Dictionary<string, object>
            {
                { "status", "rejected" },
                { FarmerResponseField, Response.Declined },
                { FarmerRespondedAtField, now },
                { "respondedAt", now },
                { CancelledByField, Party.Farmer },
                { FaultPartyField, Party.Farmer }
            };
            if (declineReason != null)
            {
                fields[DeclineReasonField] = declineReason;
            }

            var release = hasTrip && trucker != Response.None && trucker != Response.FailedCapacity;
            return CommitmentDecision.Write(fields, "rejected",
                release ? CommitmentEffect.ReleaseBooking : CommitmentEffect.None, FarmerAuditAction);
        }

        // Only a stamped deadline refuses the accept; a row from before the stamps is the cron's call.
        if (TryParseUtc(Str(order, FarmerDeadlineAtField), out var stampedDeadline) && nowUtc > stampedDeadline)
        {
            return CommitmentDecision.Error(DeadlinePassedCode,
                "The time to answer this order has passed; the buyer is being refunded.");
        }

        var accept = new Dictionary<string, object>
        {
            { FarmerResponseField, Response.Accepted },
            { FarmerRespondedAtField, now },
            { "respondedAt", now }
        };

        if (!hasTrip)
        {
            accept["status"] = "accepted";
            return CommitmentDecision.Write(accept, "accepted", CommitmentEffect.None, FarmerAuditAction);
        }

        switch (trucker)
        {
            case Response.NotAsked:
                accept[TruckerResponseField] = Response.Pending;
                accept[TruckerAskedAtField] = now;
                accept[TruckerDeadlineAtField] = Iso(ResponseDeadline(nowUtc, departureUtc));
                return CommitmentDecision.Write(accept, null, CommitmentEffect.None, FarmerAuditAction);
            case Response.FailedCapacity:
            case Response.Cancelled:
                // The transport already failed while the farmer was deciding: the farmer's yes lands
                // the order with the buyer, who cancels for a full refund (or, later, picks another truck).
                // A cancelled trip is the trucker's doing; a full one is a capacity race, nobody's fault.
                var tripGone = trucker == Response.Cancelled;
                foreach (var (key, value) in TransportFailedFields(
                             tripGone ? TransportFailure.TripCancelled : TransportFailure.TripFull,
                             trucker, nowUtc, tripGone ? Party.Trucker : Party.None))
                {
                    accept[key] = value;
                }

                return CommitmentDecision.Write(accept, AwaitingTransportStatus, CommitmentEffect.None, FarmerAuditAction);
            default:
                return CommitmentDecision.Error("ORDER_VALIDATION",
                    $"The order's transport is in an unexpected state ({trucker}).");
        }
    }

    private static CommitmentDecision DecideTrucker(
        string status, string farmer, string trucker, string command,
        IReadOnlyDictionary<string, object> order, DateTime nowUtc,
        string? declineReason, bool freshSignIn, string now)
    {
        if (trucker == Response.None)
        {
            return CommitmentDecision.Error("ORDER_UNAUTHORIZED", "This order has no trucker.");
        }

        if (status == "accepted" && trucker == Response.Accepted)
        {
            if (command == AcceptCommand)
            {
                return CommitmentDecision.NoOp();
            }

            // Dropping out of a committed order: the trucker's own phone must have signed in
            // recently (PRD §8.5), and the goods must still be at the farm (the caller checks the
            // delivery). The order waits for the buyer; the trucker is at fault.
            if (!freshSignIn)
            {
                return CommitmentDecision.Error(Helpers.StepUpPolicy.ErrorCode,
                    "Please sign in again to cancel a delivery you accepted.");
            }

            var dropOut = TransportFailedFields(TransportFailure.TruckerCancelled, Response.Cancelled, nowUtc);
            dropOut[TruckerRespondedAtField] = now;
            if (declineReason != null)
            {
                dropOut[DeclineReasonField] = declineReason;
            }

            return CommitmentDecision.Write(dropOut, AwaitingTransportStatus,
                CommitmentEffect.ReleaseBookingAndCancelDelivery, TruckerAuditAction);
        }

        if (status != "funded")
        {
            return CommitmentDecision.Error("ORDER_VALIDATION",
                $"The trucker cannot {command} an order in status '{status}'.");
        }

        if (farmer != Response.Accepted)
        {
            return CommitmentDecision.Error(ResponseNotPendingCode,
                "The farmer has not accepted this order yet. You will be asked once they do.");
        }

        if (trucker != Response.Pending)
        {
            return CommitmentDecision.Error(ResponseNotPendingCode,
                trucker == Response.Accepted ? "You already accepted this order." : "This transport request is closed.");
        }

        if (command == DeclineCommand)
        {
            var decline = TransportFailedFields(TransportFailure.TruckerDeclined, Response.Declined, nowUtc);
            decline[TruckerRespondedAtField] = now;
            if (declineReason != null)
            {
                decline[DeclineReasonField] = declineReason;
            }

            return CommitmentDecision.Write(decline, AwaitingTransportStatus,
                CommitmentEffect.ReleaseBooking, TruckerAuditAction);
        }

        if (TruckerDeadlineOf(order) is { } deadline && nowUtc > deadline)
        {
            return CommitmentDecision.Error(DeadlinePassedCode,
                "The time to accept this transport request has passed.");
        }

        var accept = new Dictionary<string, object>
        {
            { "status", "accepted" },
            { TruckerResponseField, Response.Accepted },
            { TruckerRespondedAtField, now }
        };
        return CommitmentDecision.Write(accept, "accepted", CommitmentEffect.ConfirmBooking, TruckerAuditAction);
    }

    private static string Str(IReadOnlyDictionary<string, object> item, string key)
        => item.TryGetValue(key, out var value) ? value?.ToString() ?? string.Empty : string.Empty;
}

public enum CommitmentActor
{
    Farmer,
    Trucker
}

/// <summary>What the caller must do around the order write.</summary>
public enum CommitmentEffect
{
    None,

    /// <summary>Trucker accept: the trip booking is verified (and the trip written) in the same transaction.</summary>
    ConfirmBooking,

    /// <summary>The order leaves its trip: give the capacity back.</summary>
    ReleaseBooking,

    /// <summary>The trucker dropped out after accepting: cancel the pending delivery too.</summary>
    ReleaseBookingAndCancelDelivery
}

public sealed record CommitmentDecision
{
    public bool IsError => ErrorCode != null;

    public bool IsNoOp { get; private init; }

    public string? ErrorCode { get; private init; }

    /// <summary>Full sync error text, <c>CODE: message</c>.</summary>
    public string? ErrorMessage { get; private init; }

    public Dictionary<string, object>? Fields { get; private init; }

    /// <summary>The status the write sets, or null when the status does not change.</summary>
    public string? NewStatus { get; private init; }

    public CommitmentEffect Effect { get; private init; }

    public string AuditAction { get; private init; } = string.Empty;

    public static CommitmentDecision Error(string code, string message)
        => new() { ErrorCode = code, ErrorMessage = $"{code}: {message}" };

    public static CommitmentDecision NoOp() => new() { IsNoOp = true };

    public static CommitmentDecision Write(
        Dictionary<string, object> fields, string? newStatus, CommitmentEffect effect, string auditAction)
        => new() { Fields = fields, NewStatus = newStatus, Effect = effect, AuditAction = auditAction };
}
