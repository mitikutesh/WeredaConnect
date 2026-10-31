namespace WeredaConnect.Server.Models;

/// <summary>
/// GET /crops query parameters for filtering crop listings.
/// All parameters are optional.
/// </summary>
public sealed record GetCropsRequest
{
    /// <summary>
    /// Filter by administrative district (woreda name).
    /// </summary>
    public string? Woreda { get; init; }

    /// <summary>
    /// Filter by crop type (e.g., "teff", "maize", "wheat", "coffee").
    /// </summary>
    public string? CropType { get; init; }

    /// <summary>
    /// Filter by listing status: "available", "pending", "sold", "deleted".
    /// </summary>
    public string? Status { get; init; }

    /// <summary>
    /// Maximum number of results to return (1-100, defaults to 20).
    /// </summary>
    public int? Limit { get; init; }

    /// <summary>
    /// Pagination token from previous response (DynamoDB LastEvaluatedKey).
    /// </summary>
    public string? NextToken { get; init; }
}

/// <summary>
/// GET /crops response containing paginated crop listings.
/// </summary>
public sealed record GetCropsResponse
{
    /// <summary>
    /// List of crop listings matching the query.
    /// </summary>
    public required List<CropDto> Crops { get; init; }

    /// <summary>
    /// Pagination token to retrieve next page (null if no more results).
    /// </summary>
    public string? NextToken { get; init; }
}

/// <summary>
/// GET /crops/{id} response for a single crop listing.
/// </summary>
public sealed record GetCropResponse
{
    /// <summary>
    /// The crop listing details.
    /// </summary>
    public required CropDto Crop { get; init; }
}

/// <summary>
/// Crop listing data transfer object.
/// Represents a farmer's crop available for sale.
/// </summary>
public sealed record CropDto
{
    /// <summary>
    /// Unique identifier for this crop listing (UUID v4).
    /// </summary>
    public required string CropId { get; init; }

    /// <summary>
    /// User ID of the farmer who created this listing.
    /// </summary>
    public required string FarmerId { get; init; }

    /// <summary>
    /// Display name of the farmer who created this listing.
    /// May be empty for legacy listings created before this field was added.
    /// </summary>
    public string? FarmerName { get; init; }

    /// <summary>
    /// Farmer's phone number, denormalized onto the listing so a buyer can
    /// contact them (mirrors <c>TripDto.TruckerPhone</c>). Null for legacy
    /// listings created before this field was added. Only returned to
    /// authorized callers (crop reads are buyer/admin-gated).
    /// </summary>
    public string? FarmerPhone { get; init; }

    /// <summary>
    /// Type of crop (e.g., "teff", "maize", "wheat", "coffee", "chickpea").
    /// </summary>
    public required string CropType { get; init; }

    /// <summary>
    /// Administrative district where the crop is located.
    /// </summary>
    public required string Woreda { get; init; }

    /// <summary>
    /// Quantity available in kilograms. Decimal to preserve fractional
    /// quantities (contract allows 0.01–10,000, e.g. 50.5); the store
    /// persists values like "45.00".
    /// </summary>
    public required decimal QuantityKg { get; init; }

    /// <summary>
    /// Price per kilogram in Ethiopian Birr.
    /// </summary>
    public required decimal PricePerKg { get; init; }

    /// <summary>
    /// Current status: "available", "pending", "sold", "deleted".
    /// </summary>
    public required string Status { get; init; }

    /// <summary>
    /// ISO 8601 UTC timestamp of when this listing was created.
    /// </summary>
    public required string CreatedAt { get; init; }

    /// <summary>
    /// ISO 8601 UTC timestamp of last update.
    /// </summary>
    public required string UpdatedAt { get; init; }

    /// <summary>
    /// Version number for optimistic concurrency control.
    /// </summary>
    public required int Version { get; init; }

    /// <summary>
    /// Optional description of the crop quality, variety, or other details.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Optional pickup location description.
    /// </summary>
    public string? Location { get; init; }

    /// <summary>
    /// Farmer-entered place for THIS listing (e.g. "Bahir Dar") — the source of
    /// truth for display and matching. Distinct from <see cref="Woreda"/>, which
    /// is the farmer's registered home district used only as a filter proxy.
    /// Null for legacy listings created before this field was persisted.
    /// </summary>
    public string? LocationName { get; init; }

    /// <summary>
    /// ISO 8601 UTC timestamp of when this listing was soft-deleted (if status = "deleted").
    /// </summary>
    public string? DeletedAt { get; init; }

    /// <summary>
    /// Farmer's reliability in [0, 1] (listing-conversion track record),
    /// server-injected at create time. Absent for farmers with no history; the
    /// on-device matcher defaults it to a neutral value.
    /// </summary>
    public double? FarmerReliability { get; init; }

    /// <summary>
    /// Crop's GPS latitude (WGS84), captured when the farmer created the
    /// listing with a location fix. Absent when no GPS was recorded — the
    /// on-device matcher (mobile AND web) then falls back to a woreda-string
    /// geo proxy. Returning it here lets the web client score true haversine
    /// distance, matching the mobile on-device matcher.
    /// </summary>
    public double? Latitude { get; init; }

    /// <summary>
    /// Crop's GPS longitude (WGS84). See <see cref="Latitude"/>.
    /// </summary>
    public double? Longitude { get; init; }

    /// <summary>
    /// The listing's photos in display order, the first being the cover (crop-listing-api 1.6.0).
    /// GET /crops gives each a thumbUrl, GET /crops/{id} adds fullUrl. Null, and so omitted, for a
    /// listing without photos or when photo storage is not configured.
    /// </summary>
    public List<CropPhotoDto>? Photos { get; init; }
}

/// <summary>
/// One photo of a listing on a read response: presigned GET URLs of its renditions
/// (thumb 6 hours, full 1 hour). Clients cache by photoId and rendition, never by URL.
/// </summary>
public sealed record CropPhotoDto
{
    public required string PhotoId { get; init; }

    /// <summary>Presigned GET of the 320 px WebP thumb.</summary>
    public string? ThumbUrl { get; init; }

    /// <summary>Presigned GET of the 1024 px WebP rendition; GET /crops/{id} only.</summary>
    public string? FullUrl { get; init; }
}

/// <summary>POST /crops/{id}/photos request body (crop-listing-api 1.6.0).</summary>
public sealed record CropPhotoUploadRequest
{
    /// <summary>1 to 4 photos to upload.</summary>
    public List<CropPhotoUploadItem>? Photos { get; init; }
}

/// <summary>One photo the device is about to upload.</summary>
public sealed record CropPhotoUploadItem
{
    /// <summary>UUID made on the device; distinct within the request.</summary>
    public string? PhotoId { get; init; }

    /// <summary><c>image/jpeg</c>, <c>image/png</c> or <c>image/webp</c>.</summary>
    public string? ContentType { get; init; }

    /// <summary>Exact length of the bytes the device will PUT, 1 to 10485760.</summary>
    public long? SizeBytes { get; init; }
}

/// <summary>POST /crops/{id}/photos response: one presigned PUT per photo.</summary>
public sealed record CropPhotoUploadResponse
{
    public required List<CropPhotoUploadUrl> Uploads { get; init; }

    public required int ExpiresInSeconds { get; init; }
}

/// <summary>
/// A presigned PUT for one original. The device must send <c>Content-Type</c> equal to
/// <see cref="ContentType"/> and <c>Content-Length</c> equal to <see cref="SizeBytes"/>: both are signed.
/// </summary>
public sealed record CropPhotoUploadUrl
{
    public required string PhotoId { get; init; }

    public required string UploadUrl { get; init; }

    public required string ContentType { get; init; }

    public required long SizeBytes { get; init; }
}
