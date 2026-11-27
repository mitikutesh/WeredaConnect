namespace WeredaConnect.Server.Models;

/// <summary>
/// GET /orders query parameters for filtering orders.
/// All parameters are optional.
/// </summary>
public sealed record GetOrdersRequest
{
    /// <summary>Filter by buyer ID.</summary>
    public string? BuyerId { get; init; }

    /// <summary>Filter by farmer ID.</summary>
    public string? FarmerId { get; init; }

    /// <summary>Filter by order status.</summary>
    public string? Status { get; init; }

    /// <summary>Filter by listing ID.</summary>
    public string? ListingId { get; init; }

    /// <summary>Maximum number of results to return (1-50, defaults to 20).</summary>
    public int? Limit { get; init; }

    /// <summary>Pagination token from previous response.</summary>
    public string? NextToken { get; init; }
}

/// <summary>
/// GET /orders response containing paginated orders.
/// </summary>
public sealed record GetOrdersResponse
{
    /// <summary>List of orders matching the query.</summary>
    public required List<OrderDto> Orders { get; init; }

    /// <summary>Pagination token to retrieve next page (null if no more results).</summary>
    public string? NextToken { get; init; }
}

/// <summary>
/// GET /orders/{id} response for a single order.
/// </summary>
public sealed record GetOrderResponse
{
    /// <summary>The order details.</summary>
    public required OrderDto Order { get; init; }
}

/// <summary>
/// Order data transfer object.
/// Represents a buyer-initiated purchase order in the WeredaConnect marketplace.
///
/// State machine:
///   created → pending_payment → payment_submitted → funded → accepted →
///   pickup_confirmed → in_transit → delivered → disbursing → settled
///
/// Shortcut: buyers pay immediately after placing an order, so
///   created → payment_submitted is also allowed (skipping pending_payment).
///
/// Exception states: payment_failed, cancelled, disputed, dispute_resolved
///
/// ESC-15: a funded or accepted order whose transport failed parks in awaiting_transport (the
/// money is still held) until the buyer cancels (awaiting_transport → cancelled).
/// </summary>
public sealed record OrderDto
{
    /// <summary>Unique identifier for this order (UUID v4).</summary>
    public required string OrderId { get; init; }

    /// <summary>Crop listing this order is for.</summary>
    public required string ListingId { get; init; }

    /// <summary>Buyer who placed the order.</summary>
    public required string BuyerId { get; init; }

    /// <summary>Farmer who owns the listing.</summary>
    public required string FarmerId { get; init; }

    /// <summary>Buyer's display name.</summary>
    public required string BuyerName { get; init; }

    /// <summary>Farmer's display name.</summary>
    public required string FarmerName { get; init; }

    /// <summary>Type of crop being ordered.</summary>
    public required string CropType { get; init; }

    /// <summary>Quantity requested (in kg).</summary>
    public required double Quantity { get; init; }

    /// <summary>Price per unit (ETB/kg).</summary>
    public required double PricePerUnit { get; init; }

    /// <summary>Crop cost (quantity * pricePerUnit), calculated server-side.</summary>
    public required double CropCost { get; init; }

    /// <summary>Trip ID if a trucker trip is assigned.</summary>
    public string? TripId { get; init; }

    /// <summary>Trucker user ID (from assigned trip).</summary>
    public string? TruckerId { get; init; }

    /// <summary>Trucker display name.</summary>
    public string? TruckerName { get; init; }

    /// <summary>
    /// The listing's place name at create (order-lifecycle 2.2.0): where the goods are
    /// collected. Server-only; null on orders from before it or when the listing had none.
    /// </summary>
    public string? PickupPlace { get; init; }

    /// <summary>
    /// The buyer's registered woreda at create (order-lifecycle 2.2.0): where the goods go.
    /// Server-only; null on orders from before it.
    /// </summary>
    public string? DropoffPlace { get; init; }

    /// <summary>
    /// LT-12 (order-lifecycle 2.5.0): the buyer's drop-off landmark, at most 80 characters. Only to
    /// the buyer, the farmer and the committed trucker (OrderDropoffNote.VisibleTo); null otherwise
    /// and when there is none.
    /// </summary>
    public string? DropoffNote { get; init; }

    /// <summary>
    /// LT-14 (order-lifecycle 2.9.0): the order's own drop-off point, the place the buyer picked
    /// (both or neither, inside Ethiopia's box). Only to the readers of <see cref="DropoffNote"/>
    /// (OrderDropoffNote.VisibleTo); null otherwise and when the order has none (the trip's
    /// destination is then the drop-off).
    /// </summary>
    public double? DeliveryLatitude { get; init; }

    /// <summary>LT-14: see <see cref="DeliveryLatitude"/>.</summary>
    public double? DeliveryLongitude { get; init; }

    /// <summary>Transport cost from the assigned trip.</summary>
    public double? TransportCost { get; init; }

    /// <summary>Platform commission rate (e.g. 0.05 = 5%).</summary>
    public required double CommissionRate { get; init; }

    /// <summary>Commission amount (cropCost * commissionRate), calculated server-side.</summary>
    public required double CommissionAmount { get; init; }

    /// <summary>Total order amount (cropCost + transportCost + commissionAmount).</summary>
    public required double OrderTotal { get; init; }

    /// <summary>Payment method: "cbe_transfer".</summary>
    public string? PaymentMethod { get; init; }

    /// <summary>CBE Transaction ID entered by buyer.</summary>
    public string? CbeTransactionId { get; init; }

    /// <summary>ISO 8601 UTC timestamp of when payment was verified.</summary>
    public string? PaymentVerifiedAt { get; init; }

    /// <summary>Current order status.</summary>
    public required string Status { get; init; }

    /// <summary>ISO 8601 UTC timestamp of order creation.</summary>
    public required string CreatedAt { get; init; }

    /// <summary>ISO 8601 UTC timestamp of last update.</summary>
    public required string UpdatedAt { get; init; }

    /// <summary>Version for optimistic concurrency control.</summary>
    public required int Version { get; init; }

    /// <summary>ISO 8601 UTC timestamp of when the farmer responded.</summary>
    public string? RespondedAt { get; init; }

    /// <summary>Reason for rejection, if status is rejected.</summary>
    public string? RejectionReason { get; init; }

    /// <summary>ISO 8601 UTC timestamp of cancellation.</summary>
    public string? CancelledAt { get; init; }

    /// <summary>
    /// Reason for cancellation: the buyer's text, or a server code
    /// (<c>transport_failed_buyer_cancelled</c>, <c>escrow_refunded</c>).
    /// </summary>
    public string? CancelReason { get; init; }

    /// <summary>
    /// ESC-06: ISO 8601 UTC time after which an accepted order nobody picked up counts as a
    /// transport failure and its buyer may cancel it. Stamped at the farmer's accept.
    /// </summary>
    public string? PickupDeadlineAt { get; init; }

    /// <summary>ESC-06: why the order waits in the admin stuck-order queue (<c>hold_cap</c>); null when it does not.</summary>
    public string? AdminQueueReason { get; init; }

    /// <summary>ESC-06: ISO 8601 UTC time the order entered the admin queue.</summary>
    public string? AdminQueuedAt { get; init; }

    /// <summary>ESC-15: how the goods move, <c>trucker</c> or <c>self_pickup</c>; null before funding or on a row from before ESC-15.</summary>
    public string? Fulfillment { get; init; }

    /// <summary>ESC-15: ISO 8601 UTC pickup date of a trucker order (the trip's departure).</summary>
    public string? PickupDate { get; init; }

    /// <summary>ESC-15: the farmer's answer to the paid order: <c>pending</c>, <c>accepted</c>, <c>declined</c> or <c>expired</c>.</summary>
    public string? FarmerResponse { get; init; }

    /// <summary>ESC-15: ISO 8601 UTC time the farmer answered.</summary>
    public string? FarmerRespondedAt { get; init; }

    /// <summary>ESC-15: ISO 8601 UTC time by which the farmer must answer; after it the buyer is refunded.</summary>
    public string? FarmerDeadlineAt { get; init; }

    /// <summary>ESC-15: the trucker's answer: <c>not_asked</c>, <c>pending</c>, <c>accepted</c>, <c>declined</c>, <c>expired</c>, <c>cancelled</c>, <c>failed_capacity</c>, or <c>none</c> on self-pickup.</summary>
    public string? TruckerResponse { get; init; }

    /// <summary>ESC-15: ISO 8601 UTC time the trucker was asked (at the farmer's accept).</summary>
    public string? TruckerAskedAt { get; init; }

    /// <summary>ESC-15: ISO 8601 UTC time the trucker answered.</summary>
    public string? TruckerRespondedAt { get; init; }

    /// <summary>ESC-15: ISO 8601 UTC time by which the trucker must answer.</summary>
    public string? TruckerDeadlineAt { get; init; }

    /// <summary>ESC-15: ISO 8601 UTC time by which the buyer of an <c>awaiting_transport</c> order must decide.</summary>
    public string? BuyerChoiceDeadlineAt { get; init; }

    /// <summary>ESC-15: why the transport failed: <c>trucker_declined</c>, <c>trucker_expired</c>, <c>trucker_cancelled</c>, <c>trip_cancelled</c> or <c>trip_full</c>.</summary>
    public string? TransportFailure { get; init; }

    /// <summary>ESC-15: the reason a party gave for declining: <c>date</c>, <c>capacity</c>, <c>price</c>, <c>route</c>, <c>crop_not_ready</c> or <c>other</c>.</summary>
    public string? DeclineReason { get; init; }

    /// <summary>ESC-15: who ended the order: <c>buyer</c>, <c>farmer</c>, <c>trucker</c> or <c>system</c>.</summary>
    public string? CancelledBy { get; init; }

    /// <summary>ESC-15: the party at fault for the failed order, for reputation; <c>none</c> when nobody is.</summary>
    public string? FaultParty { get; init; }

    /// <summary>
    /// ESC-34: the pickup judgement of an order accepted with <c>pickupClassification</c> on:
    /// <c>pending</c> until the order cron judges it at <c>pickupDeadlineAt</c>, then <c>scanned</c>,
    /// <c>trucker_no_show</c>, <c>farmer_no_show</c>, <c>buyer_no_show</c>, <c>both_present_no_scan</c> or
    /// <c>unchecked_check_in</c>. Null on every other order (the P1 rules apply).
    /// </summary>
    public string? PickupJudgement { get; init; }

    /// <summary>ESC-34: ISO 8601 UTC time the pickup was judged.</summary>
    public string? PickupJudgedAt { get; init; }

    /// <summary>
    /// ESC-34: the arrival check-ins the server received, keyed by kind (<c>arrived_pickup</c>,
    /// <c>crops_ready</c>; ESC-36: <c>arrived_dropoff</c>). No coordinates and no sender. Null when there is none.
    /// </summary>
    public Dictionary<string, OrderCheckInDto>? CheckIns { get; init; }

    /// <summary>
    /// ESC-36: the drop-off judgement of an order picked up with <c>dropoffClassification</c> on:
    /// <c>pending</c> until the order cron judges it (T8 at <see cref="DropoffDeadlineAt"/>, or T9 48 hours
    /// after the trucker's drop-off check-in), then <c>closed</c>, <c>auto_confirmed</c>, <c>silent_buyer</c>,
    /// <c>delivery_overdue</c> or <c>queued</c>. Null on every other order.
    /// </summary>
    public string? DropoffJudgement { get; init; }

    /// <summary>ESC-36: ISO 8601 UTC time the drop-off was judged.</summary>
    public string? DropoffJudgedAt { get; init; }

    /// <summary>ESC-36: T8, the pickup plus 72 hours: without a drop-off check-in by then the delivery is overdue.</summary>
    public string? DropoffDeadlineAt { get; init; }

    /// <summary>
    /// FUP-370 (order-lifecycle 2.22.0): T9, the trucker's counted drop-off check-in plus the SSM
    /// <c>silent-buyer-hours</c> (48 by default): if the buyer has neither confirmed nor disputed by then, the
    /// order cron decides the delivery for them. Only to the order's parties while it is on the road and
    /// <c>dropoffJudgement</c> is <c>pending</c> with <c>dropoffClassification</c> on; computed on every read.
    /// </summary>
    public string? DropoffConfirmAt { get; init; }

    /// <summary>ESC-36: why the system disputed the order: <c>delivery_overdue</c> (T8) or <c>silent_buyer</c> (T9).</summary>
    public string? DisputeReason { get; init; }

    /// <summary>
    /// ESC-48: whether the caller may reach the other parties on this order (call, copy, message): the
    /// farmer accepted the paid order and the money has not gone back to the buyer
    /// (<c>ContactReveal</c>); for the trucker, also that they committed to the transport (ESC-15).
    /// Computed by the server on every read.
    /// </summary>
    public bool ContactOpen { get; init; }

    /// <summary>ESC-48: the buyer's phone (E.164), only to a party of an order whose contact is open.</summary>
    public string? BuyerPhone { get; init; }

    /// <summary>ESC-48: the farmer's phone (E.164), only to a party of an order whose contact is open.</summary>
    public string? FarmerPhone { get; init; }

    /// <summary>ESC-48: the trucker's phone (E.164), only to a party of an open order whose trucker committed (ESC-15).</summary>
    public string? TruckerPhone { get; init; }
}

/// <summary>ESC-34: one arrival check-in as the parties read it (delivery-verification-api.md 2.6.0).</summary>
public sealed record OrderCheckInDto
{
    /// <summary>ISO 8601 UTC time the server received it: the only time the judgement reads.</summary>
    public string ReceivedAt { get; init; } = string.Empty;

    /// <summary>ISO 8601 UTC time the phone took it, kept when plausible; evidence only.</summary>
    public string? CapturedAt { get; init; }

    /// <summary>Whether its GPS fix was measured against the pickup point (false: no fix, or no point).</summary>
    public bool DistanceChecked { get; init; }

    /// <summary>Whether it reached the server after the pickup deadline (kept as evidence, never counted).</summary>
    public bool Late { get; init; }
}
