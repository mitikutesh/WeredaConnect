namespace WeredaConnect.Server.Models;

/// <summary>
/// GET /trips response containing paginated trip listings.
/// </summary>
public sealed record GetTripsResponse
{
    /// <summary>
    /// List of trip listings matching the query.
    /// </summary>
    public required List<TripDto> Trips { get; init; }

    /// <summary>
    /// Pagination token to retrieve next page (null if no more results).
    /// </summary>
    public string? NextToken { get; init; }

    /// <summary>
    /// Whether more results are available beyond this page.
    /// </summary>
    public bool HasMore { get; init; }

    /// <summary>
    /// Approximate total count of matching trips (for UI display).
    /// </summary>
    public int Total { get; init; }
}

/// <summary>
/// GET /trips/{id} response for a single trip listing.
/// </summary>
public sealed record GetTripResponse
{
    /// <summary>
    /// The trip listing details.
    /// </summary>
    public required TripDto Trip { get; init; }
}

/// <summary>
/// Trip listing data transfer object.
/// Represents a trucker's available transport capacity on a route.
/// </summary>
public sealed record TripDto
{
    /// <summary>
    /// Unique identifier for this trip listing (UUID v4).
    /// </summary>
    public required string TripId { get; init; }

    /// <summary>
    /// User ID of the trucker who posted this trip.
    /// </summary>
    public required string TruckerId { get; init; }

    /// <summary>
    /// Trucker's display name.
    /// </summary>
    public required string TruckerName { get; init; }

    /// <summary>
    /// Trucker's contact phone number (E.164 format, hidden from unauthenticated users).
    /// </summary>
    public string? TruckerPhone { get; init; }

    /// <summary>
    /// Vehicle license plate number (e.g., 3-AA-12345).
    /// </summary>
    public string? VehiclePlate { get; init; }

    /// <summary>
    /// Vehicle description (e.g., Isuzu FTR 10-ton).
    /// </summary>
    public string? VehicleDescription { get; init; }

    /// <summary>
    /// Origin woreda (Ethiopian administrative district).
    /// </summary>
    public required string FromWoreda { get; init; }

    /// <summary>
    /// Destination woreda (Ethiopian administrative district).
    /// </summary>
    public required string ToWoreda { get; init; }

    /// <summary>
    /// Scheduled departure date and time (ISO 8601 UTC).
    /// </summary>
    public required string DepartureDate { get; init; }

    /// <summary>
    /// Available capacity in quintals (100kg).
    /// </summary>
    public required double Capacity { get; init; }

    /// <summary>
    /// ESC-15: quintals reserved by funded orders. Null when the trip has no bookings yet
    /// (a row written before bookings existed); treat null as zero.
    /// </summary>
    public double? BookedQuintals { get; init; }

    /// <summary>
    /// ESC-15: quintals still free for new orders. Null when the trip has no bookings yet;
    /// treat null as <see cref="Capacity"/>. Zero means fully booked (<c>matched</c>).
    /// </summary>
    public double? RemainingQuintals { get; init; }

    /// <summary>
    /// Transport price per quintal in Ethiopian Birr.
    /// </summary>
    public required double PricePerQuintal { get; init; }

    /// <summary>
    /// Trip lifecycle status: open, matched, inTransit, delivered, cancelled.
    /// </summary>
    public required string Status { get; init; }

    /// <summary>
    /// Crop listing IDs matched to this trip.
    /// </summary>
    public required List<string> MatchedListings { get; init; }

    /// <summary>
    /// ISO 8601 UTC timestamp of when this trip was created.
    /// </summary>
    public required string CreatedAt { get; init; }

    /// <summary>
    /// ISO 8601 UTC timestamp of last update.
    /// </summary>
    public required string UpdatedAt { get; init; }

    /// <summary>
    /// ISO 8601 UTC timestamp of when this trip was soft-deleted (cancelled).
    /// </summary>
    public string? DeletedAt { get; init; }

    /// <summary>
    /// Version number for optimistic concurrency control.
    /// </summary>
    public required int Version { get; init; }
}
