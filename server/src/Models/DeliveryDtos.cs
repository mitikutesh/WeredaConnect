namespace WeredaConnect.Server.Models;

/// <summary>
/// GET /deliveries response containing paginated delivery listings.
/// </summary>
public sealed record GetDeliveriesResponse
{
    /// <summary>
    /// List of deliveries matching the query.
    /// </summary>
    public required List<DeliveryDto> Deliveries { get; init; }

    /// <summary>
    /// Pagination token to retrieve next page (null if no more results).
    /// </summary>
    public string? NextToken { get; init; }

    /// <summary>
    /// Whether more results are available beyond this page.
    /// </summary>
    public bool HasMore { get; init; }

    /// <summary>
    /// Approximate total count of matching deliveries (for UI display).
    /// </summary>
    public int Total { get; init; }
}

/// <summary>
/// GET /deliveries/{id} response for a single delivery.
/// </summary>
public sealed record GetDeliveryResponse
{
    /// <summary>
    /// The delivery details.
    /// </summary>
    public required DeliveryDto Delivery { get; init; }
}

/// <summary>
/// POST /deliveries/{id}/photos request body.
/// Client sends photo type to get a pre-signed S3 upload URL.
/// </summary>
public sealed record UploadPhotoRequest
{
    /// <summary>
    /// Photo type: "pickup" or "dropoff".
    /// </summary>
    public required string Type { get; init; }
}

/// <summary>
/// POST /deliveries/{id}/photos response with pre-signed upload URL.
/// </summary>
public sealed record UploadPhotoResponse
{
    /// <summary>
    /// Pre-signed S3 URL for uploading the photo (PUT request, 15-min expiry).
    /// </summary>
    public required string UploadUrl { get; init; }

    /// <summary>
    /// S3 key that the photo will be stored at. Use this as photoRef in delivery updates.
    /// </summary>
    public required string PhotoRef { get; init; }

    /// <summary>
    /// ISO 8601 UTC timestamp of when the upload URL was generated.
    /// </summary>
    public required string IssuedAt { get; init; }

    /// <summary>
    /// Seconds until the upload URL expires (900 = 15 minutes).
    /// </summary>
    public required int ExpiresInSeconds { get; init; }
}

/// <summary>
/// GPS coordinates data transfer object.
/// </summary>
public sealed record LatLngDto
{
    /// <summary>
    /// Latitude coordinate.
    /// </summary>
    public required double Latitude { get; init; }

    /// <summary>
    /// Longitude coordinate.
    /// </summary>
    public required double Longitude { get; init; }
}

/// <summary>
/// Delivery data transfer object.
/// Represents a delivery linking a farmer, trucker, and buyer for a crop listing.
/// </summary>
public sealed record DeliveryDto
{
    /// <summary>
    /// Unique identifier for this delivery (UUID v4).
    /// </summary>
    public required string DeliveryId { get; init; }

    /// <summary>
    /// Associated trip ID.
    /// </summary>
    public required string TripId { get; init; }

    /// <summary>
    /// Associated crop listing ID.
    /// </summary>
    public required string ListingId { get; init; }

    /// <summary>
    /// Farmer user ID.
    /// </summary>
    public required string FarmerId { get; init; }

    /// <summary>
    /// Farmer display name.
    /// </summary>
    public required string FarmerName { get; init; }

    /// <summary>
    /// Farmer phone number (E.164 format, hidden from non-participants).
    /// </summary>
    public string? FarmerPhone { get; init; }

    /// <summary>
    /// Trucker user ID.
    /// </summary>
    public required string TruckerId { get; init; }

    /// <summary>
    /// Trucker display name.
    /// </summary>
    public required string TruckerName { get; init; }

    /// <summary>
    /// Trucker phone number (E.164 format, hidden from non-participants).
    /// </summary>
    public string? TruckerPhone { get; init; }

    /// <summary>
    /// Buyer user ID.
    /// </summary>
    public required string BuyerId { get; init; }

    /// <summary>
    /// Buyer display name.
    /// </summary>
    public required string BuyerName { get; init; }

    /// <summary>
    /// Buyer phone number (E.164 format, hidden from non-participants).
    /// </summary>
    public string? BuyerPhone { get; init; }

    /// <summary>
    /// QR verification token for delivery handoff.
    /// </summary>
    public required string QrToken { get; init; }

    /// <summary>
    /// Delivery lifecycle status: pending, pickedUp, inTransit, delivered, disputed.
    /// </summary>
    public required string Status { get; init; }

    /// <summary>
    /// GPS coordinates at pickup location.
    /// </summary>
    public LatLngDto? GpsPickup { get; init; }

    /// <summary>
    /// GPS coordinates at dropoff location.
    /// </summary>
    public LatLngDto? GpsDropoff { get; init; }

    /// <summary>
    /// S3 reference for pickup photo evidence.
    /// </summary>
    public string? PickupPhotoRef { get; init; }

    /// <summary>
    /// S3 reference for dropoff photo evidence.
    /// </summary>
    public string? DropoffPhotoRef { get; init; }

    /// <summary>
    /// ISO 8601 UTC timestamp of when cargo was picked up.
    /// </summary>
    public string? PickedUpAt { get; init; }

    /// <summary>
    /// ISO 8601 UTC timestamp of when delivery was confirmed.
    /// </summary>
    public string? ConfirmedAt { get; init; }

    /// <summary>
    /// Reason for dispute if status is disputed.
    /// </summary>
    public string? DisputeReason { get; init; }

    /// <summary>
    /// ISO 8601 UTC timestamp of when this delivery was created.
    /// </summary>
    public required string CreatedAt { get; init; }

    /// <summary>
    /// ISO 8601 UTC timestamp of last update.
    /// </summary>
    public required string UpdatedAt { get; init; }

    /// <summary>
    /// ISO 8601 UTC timestamp of when this delivery was soft-deleted.
    /// </summary>
    public string? DeletedAt { get; init; }

    /// <summary>
    /// Version number for optimistic concurrency control.
    /// </summary>
    public required int Version { get; init; }
}
