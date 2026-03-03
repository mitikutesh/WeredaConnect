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
/// Exception states: payment_failed, cancelled, disputed, dispute_resolved
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

    /// <summary>Reason for cancellation.</summary>
    public string? CancelReason { get; init; }
}
