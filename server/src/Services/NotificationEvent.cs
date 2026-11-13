namespace WeredaConnect.Server.Services;

/// <summary>
/// ESC-21: one notification to one user, derived by <c>NotificationRules</c> from a change in the
/// orders, deliveries or escrows table. The stream function turns it into an inbox row (always) and
/// an FCM push (when <see cref="Push"/>). Text is rendered later: on the server for the push body
/// (<c>PushText</c>, in the recipient's language) and on the clients for the inbox (from
/// <see cref="Type"/> and <see cref="Params"/>, so a language switch re-renders).
/// </summary>
/// <param name="RecipientUserId">Who is told. Never the party whose own action caused the change.</param>
/// <param name="Type">One of <see cref="NotificationTypes"/>.</param>
/// <param name="Entity"><c>order</c>, <c>delivery</c> or <c>escrow</c>: the table the change came from.</param>
/// <param name="EntityId">The row's id in that table.</param>
/// <param name="OrderId">The order the event is about (deep-link target), when known.</param>
/// <param name="Params">Plain-text parameters for the text: names, quantities, amounts, deadlines (ISO), reason codes. Never a phone number.</param>
/// <param name="Push">False for inbox-only events (the disputer's own receipt, an escrow under review).</param>
/// <param name="DeadlineUtc">For "act" events, when the request closes: sets the push's time to live.</param>
public sealed record NotificationEvent(
    string RecipientUserId,
    string Type,
    string Entity,
    string EntityId,
    string? OrderId,
    IReadOnlyDictionary<string, string> Params,
    bool Push = true,
    DateTime? DeadlineUtc = null);

/// <summary>
/// ESC-21: every notification type the pipeline can emit. Each has push text in en, am and om
/// (<c>PushText</c>, parity-tested) and a client inbox rendering (ARB keys, PR 3).
/// </summary>
public static class NotificationTypes
{
    // Order commitments (orders table).
    public const string PaymentConfirmed = "payment_confirmed";
    public const string OrderRequest = "order_request";
    public const string FarmerAccepted = "farmer_accepted";
    public const string TransportRequest = "transport_request";
    public const string OrderConfirmed = "order_confirmed";
    public const string OrderRejected = "order_rejected";
    public const string TransportFailed = "transport_failed";
    public const string TransportFailedKeepCrops = "transport_failed_keep_crops";
    public const string RequestExpired = "request_expired";
    public const string RequestOnHold = "request_on_hold";
    public const string OrderCancelled = "order_cancelled";
    public const string OrderExpiredUnpaid = "order_expired_unpaid";
    public const string PaymentFailed = "payment_failed";
    public const string RequestReminder = "request_reminder";
    public const string PickupOverdue = "pickup_overdue";
    public const string PickupMissed = "pickup_missed";

    // Logistics (orders table, mirrored from the delivery; deliveries table for the create and the nearby stamp).
    public const string DeliveryScheduled = "delivery_scheduled";
    public const string BuyerCollecting = "buyer_collecting";
    public const string PickedUp = "picked_up";
    public const string HandoffRecorded = "handoff_recorded";
    public const string InTransit = "in_transit";

    /// <summary>LT-07: a check-in put the truck within 5 km of the drop-off (deliveries table, the <c>nearbyNotifiedAt</c> stamp).</summary>
    public const string TruckNearby = "truck_nearby";

    // Arrival check-ins of the pickup (FUP-286, ESC-34; orders table: the checkIns entries and the reminder marker).

    /// <summary>The check-in window closes soon and the recipient has not checked in (<c>pickupReminderAt</c> stamped).</summary>
    public const string PickupReminder = "pickup_reminder";

    /// <summary>To the farmer: the trucker's "I have arrived" at the farm was received.</summary>
    public const string TruckerArrived = "trucker_arrived";

    /// <summary>To the farmer of a self-pickup: the buyer's "I have arrived" at the farm was received.</summary>
    public const string BuyerArrived = "buyer_arrived";

    /// <summary>To the trucker (or the buyer collecting): the farmer's "Crops are ready" was received.</summary>
    public const string CropsReady = "crops_ready";

    // The drop-off check-in (FUP-338, ESC-36; orders table: dropoffCheckIn and the reminder marker).

    /// <summary>To the buyer: the trucker checked in at the drop-off; carries T9, when the delivery may be confirmed for them.</summary>
    public const string DeliveryArrived = "delivery_arrived";

    /// <summary>To the buyer: T9 comes soon and the delivery is neither confirmed nor disputed (<c>dropoffReminderAt</c> stamped).</summary>
    public const string DeliveryConfirmReminder = "delivery_confirm_reminder";

    public const string Delivered = "delivered";
    public const string DeliveredPayoutPending = "delivered_payout_pending";

    // Money: the escrows table tells the payees (it moves the money); the order's settled mirror
    // tells only the buyer.
    public const string OrderSettled = "order_settled";
    public const string RefundSent = "refund_sent";
    public const string PayoutSent = "payout_sent";
    public const string PayoutBlocked = "payout_blocked";
    public const string PayoutProcessing = "payout_processing";
    public const string DisputeOpened = "dispute_opened";
    public const string DisputeReceived = "dispute_received";
    public const string DisputeResolved = "dispute_resolved";
    public const string UnderReview = "under_review";

    // Reputation (ESC-28, reputation-api.md section 10): from the reputation-events table, to the party only.
    /// <summary>A penalty row with points above 0 was recorded for the recipient.</summary>
    public const string PenaltyRecorded = "penalty_recorded";

    /// <summary>The recipient's tier changed (a <c>TIER_CHANGED</c> row); to <c>warning</c> it is the private warning.</summary>
    public const string TierChanged = "tier_changed";

    /// <summary>Every type, for the parity tests (push text in three languages, client ARB keys).</summary>
    public static readonly IReadOnlyList<string> All = new[]
    {
        PaymentConfirmed, OrderRequest, FarmerAccepted, TransportRequest, OrderConfirmed, OrderRejected,
        TransportFailed, TransportFailedKeepCrops, RequestExpired, RequestOnHold, OrderCancelled, OrderExpiredUnpaid,
        PaymentFailed, RequestReminder, PickupOverdue, PickupMissed,
        DeliveryScheduled, BuyerCollecting, PickedUp, HandoffRecorded, InTransit, TruckNearby, Delivered, DeliveredPayoutPending,
        OrderSettled, RefundSent, PayoutSent, PayoutBlocked, PayoutProcessing,
        DisputeOpened, DisputeReceived, DisputeResolved, UnderReview,
        PenaltyRecorded, TierChanged,
        PickupReminder, TruckerArrived, BuyerArrived, CropsReady, DeliveryArrived, DeliveryConfirmReminder
    };

    /// <summary>Types that ask the recipient to do something; their push carries a deadline-bound time to live.</summary>
    public static readonly IReadOnlySet<string> ActionRequired = new HashSet<string>(StringComparer.Ordinal)
    {
        OrderRequest, TransportRequest, TransportFailed, PaymentFailed, Delivered, PickupOverdue, PayoutBlocked, RequestReminder,
        PickupReminder, DeliveryArrived, DeliveryConfirmReminder
    };
}

/// <summary>Param keys <c>NotificationRules</c> fills and <c>PushText</c> reads. Values are plain text.</summary>
public static class NotificationParams
{
    public const string BuyerName = "buyerName";
    public const string FarmerName = "farmerName";
    public const string TruckerName = "truckerName";
    public const string CropType = "cropType";
    public const string QuantityKg = "quantityKg";
    public const string AmountEtb = "amountEtb";
    public const string DeadlineAt = "deadlineAt";
    public const string PickupDate = "pickupDate";

    /// <summary>
    /// A code, never free text. FUP-286 and FUP-338: also the judgement behind a judged pickup's or drop-off's event
    /// (<c>trucker_no_show</c> on <c>pickup_missed</c>, a pickup outcome sent to the admin queue on
    /// <c>pickup_overdue</c> and <c>pickup_missed</c>, <c>auto_confirmed</c> on <c>delivered</c> and
    /// <c>delivered_payout_pending</c>, the system's dispute reason on <c>dispute_opened</c>); <c>PushText</c> then
    /// renders that outcome's own words.
    /// </summary>
    public const string Reason = "reason";
    public const string CancelledBy = "cancelledBy";
    public const string Resolution = "resolution";
    public const string Party = "party";

    /// <summary>LT-07, <c>truck_nearby</c>: the truck's distance to the drop-off in km, one decimal (<c>3.2</c>).</summary>
    public const string DistanceKm = "distanceKm";

    /// <summary>LT-07, <c>truck_nearby</c>: minutes to the start of the arrival window, a multiple of 5; absent when 0 or unknown.</summary>
    public const string Minutes = "minutes";

    /// <summary>ESC-28, <c>penalty_recorded</c>: the event's slot id, code, points and the party's active points after it.</summary>
    public const string EventId = "eventId";
    public const string Code = "code";
    public const string Points = "points";
    public const string ActivePoints = "activePoints";

    /// <summary>ESC-28, <c>tier_changed</c>: the tiers before and after, and the restriction's end when restricted.</summary>
    public const string FromTier = "fromTier";
    public const string ToTier = "toTier";
    public const string RestrictedUntil = "restrictedUntil";
}
