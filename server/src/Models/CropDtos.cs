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
    /// Type of crop (e.g., "teff", "maize", "wheat", "coffee", "chickpea").
    /// </summary>
    public required string CropType { get; init; }

    /// <summary>
    /// Administrative district where the crop is located.
    /// </summary>
    public required string Woreda { get; init; }

    /// <summary>
    /// Quantity available in kilograms.
    /// </summary>
    public required int QuantityKg { get; init; }

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
    /// ISO 8601 UTC timestamp of when this listing was soft-deleted (if status = "deleted").
    /// </summary>
    public string? DeletedAt { get; init; }
}
