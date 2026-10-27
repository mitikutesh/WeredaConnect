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

    // Logistics (orders table, mirrored from the delivery; deliveries table for the create).
    public const string DeliveryScheduled = "delivery_scheduled";
    public const string BuyerCollecting = "buyer_collecting";
    public const string PickedUp = "picked_up";
    public const string HandoffRecorded = "handoff_recorded";
    public const string InTransit = "in_transit";
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

    /// <summary>Every type, for the parity tests (push text in three languages, client ARB keys).</summary>
    public static readonly IReadOnlyList<string> All = new[]
    {
        PaymentConfirmed, OrderRequest, FarmerAccepted, TransportRequest, OrderConfirmed, OrderRejected,
        TransportFailed, TransportFailedKeepCrops, RequestExpired, RequestOnHold, OrderCancelled, OrderExpiredUnpaid,
        PaymentFailed, RequestReminder, PickupOverdue, PickupMissed,
        DeliveryScheduled, BuyerCollecting, PickedUp, HandoffRecorded, InTransit, Delivered, DeliveredPayoutPending,
        OrderSettled, RefundSent, PayoutSent, PayoutBlocked, PayoutProcessing,
        DisputeOpened, DisputeReceived, DisputeResolved, UnderReview
    };

    /// <summary>Types that ask the recipient to do something; their push carries a deadline-bound time to live.</summary>
    public static readonly IReadOnlySet<string> ActionRequired = new HashSet<string>(StringComparer.Ordinal)
    {
        OrderRequest, TransportRequest, TransportFailed, PaymentFailed, Delivered, PickupOverdue, PayoutBlocked, RequestReminder
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
    public const string Reason = "reason";
    public const string CancelledBy = "cancelledBy";
    public const string Resolution = "resolution";
    public const string Party = "party";
}
