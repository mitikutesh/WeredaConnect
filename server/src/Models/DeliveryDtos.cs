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
    /// The order this delivery fulfils (required on create since 1.2.0; exposed on
    /// list and detail since 1.4.2, SIM-14, so clients can tie a delivery to its
    /// order). Null only for deliveries created before orderId became required.
    /// </summary>
    public string? OrderId { get; init; }

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
    /// Delivery type: null/absent = a normal trucker delivery; "self_pickup" = the
    /// buyer collects at the farm (no trucker). Lets a farmer's app show the pickup QR
    /// for a buyer-created self-pickup delivery.
    /// </summary>
    public string? DeliveryType { get; init; }

    /// <summary>
    /// The order's crop type key ("wheat"), stamped on create from the order (1.21.0) so the
    /// deliveries list can title each card without one order read per row. Null on deliveries
    /// created before 1.21.0.
    /// </summary>
    public string? CropType { get; init; }

    /// <summary>
    /// The order's quantity in kilograms, stamped on create from the order (1.21.0). Null on
    /// deliveries created before 1.21.0.
    /// </summary>
    public double? Quantity { get; init; }

    /// <summary>
    /// Where the crop is picked up: the listing's place name, stamped on create (1.21.0).
    /// Null when the listing has none or the delivery predates 1.21.0.
    /// </summary>
    public string? PickupPlace { get; init; }

    /// <summary>
    /// ISO 8601 UTC departure date of the trip at create time (1.21.0). Null on a self-pickup
    /// and on deliveries created before 1.21.0.
    /// </summary>
    public string? PickupPlannedAt { get; init; }

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

/// <summary>
/// LT-06: GET /deliveries/{id}/track response (live-trip-api.md section 2). Only the delivery's
/// buyer, farmer and trucker read it. Coordinates are rounded (party points at 3 decimals, other
/// stops at 2) and limited to the delivery's own window, from pickup to delivery. Nullable fields
/// are written as <c>null</c>, as the contract shows them.
/// </summary>
public sealed record DeliveryTrackResponse
{
    public required string DeliveryId { get; init; }

    /// <summary><c>not_started</c>, <c>live</c>, <c>paused</c>, <c>declined</c> or <c>ended</c>.</summary>
    public required string Sharing { get; init; }

    /// <summary>The end of the running pause, only while <c>paused</c>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? PausedUntil { get; init; }

    /// <summary>The server time of this answer.</summary>
    public required string AsOf { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public TrackLastFixDto? LastFix { get; init; }

    public required List<TrackTrailPointDto> Trail { get; init; }

    public required List<TrackPauseDto> Pauses { get; init; }

    public required TrackPickupDto Pickup { get; init; }

    public required TrackDropoffDto Dropoff { get; init; }

    public required List<TrackStopDto> OtherStops { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public TrackEtaDto? Eta { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public double? RemainingKm { get; init; }

    /// <summary>Only when <c>ended</c>; carries no coordinates.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public TrackSummaryDto? Summary { get; init; }
}

/// <summary>The latest check-in of the window.</summary>
public sealed record TrackLastFixDto
{
    public required double Latitude { get; init; }
    public required double Longitude { get; init; }
    public required string CapturedAt { get; init; }
    public required string ReceivedAt { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public double? AccuracyM { get; init; }
}

/// <summary>One check-in of the window, in capture order.</summary>
public sealed record TrackTrailPointDto
{
    public required double Latitude { get; init; }
    public required double Longitude { get; init; }
    public required string CapturedAt { get; init; }
    public required string ReceivedAt { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? Source { get; init; }
}

/// <summary>A pause of the driver inside the window.</summary>
public sealed record TrackPauseDto
{
    public required string From { get; init; }
    public required string Until { get; init; }
}

/// <summary>The pickup point (gpsPickup, else the listing location) and the pickup time.</summary>
public sealed record TrackPickupDto
{
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public double? Latitude { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public double? Longitude { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? At { get; init; }
}

/// <summary>The drop-off point and the buyer's landmark note (LT-12; null until then).</summary>
public sealed record TrackDropoffDto
{
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public double? Latitude { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public double? Longitude { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? Note { get; init; }
}

/// <summary>Another order's pickup or drop-off on the same truck: never named, 2 decimals.</summary>
public sealed record TrackStopDto
{
    /// <summary><c>pickup</c> or <c>dropoff</c>.</summary>
    public required string Kind { get; init; }
    public required double Latitude { get; init; }
    public required double Longitude { get; init; }
    public required bool Done { get; init; }
}

/// <summary>The arrival window, on 5 minute marks.</summary>
public sealed record TrackEtaDto
{
    public required string From { get; init; }
    public required string To { get; init; }
}

/// <summary>The trip after delivery: distance of the legs, pickup to delivery, number of check-ins.</summary>
public sealed record TrackSummaryDto
{
    public required double DistanceKm { get; init; }
    public required int DurationMin { get; init; }
    public required int Fixes { get; init; }
}
