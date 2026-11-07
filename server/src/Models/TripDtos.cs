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
    /// Pagination token to retrieve next page. Null, and so omitted, when no trip follows this
    /// page (FUP-126: it never leads to an empty last page).
    /// </summary>
    public string? NextToken { get; init; }

    /// <summary>
    /// Whether a trip follows this page; true exactly when <see cref="NextToken"/> is present
    /// (trip-listing-api 2.5.0, FUP-126).
    /// </summary>
    public bool HasMore { get; init; }

    /// <summary>
    /// The number of trips on this page (<see cref="Trips"/>), not a count of every match
    /// (trip-listing-api 2.5.0). Kept because shipped clients may read it; a client that needs a
    /// count follows <see cref="NextToken"/> to the end.
    /// </summary>
    public int Total { get; init; }

    /// <summary>
    /// FUP-122 (trip-listing-api 2.3.0): an order create refuses a trip departing sooner than
    /// this many hours from now (24 while the server runs the commitments, 0 when it does not).
    /// The apps cache it and leave such trips out of the match suggestions.
    /// </summary>
    public int OrderLeadHours { get; init; }
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
