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
