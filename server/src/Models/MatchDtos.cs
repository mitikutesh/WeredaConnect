namespace WeredaConnect.Server.Models;

/// <summary>
/// A scored crop match returned by GET /match. Mirrors the buyer-facing
/// fields of <see cref="CropDto"/> plus the relevance score and a flag for
/// substitute-crop matches.
/// </summary>
public sealed record MatchDto
{
    public required string CropId { get; init; }
    public required string FarmerId { get; init; }
    public string? FarmerName { get; init; }
    public required string CropType { get; init; }
    public required string Woreda { get; init; }

    /// <summary>
    /// Crop quantity. NOTE: despite the name this carries QUINTALS, not
    /// kilograms — it mirrors the historical "quantityKg" key misnomer
    /// documented in mobile IsarMarketplaceSync. No kg/quintal conversion
    /// happens anywhere in the stack; do NOT "correct" it by ×100.
    /// </summary>
    public required decimal QuantityKg { get; init; }
    public required decimal PricePerKg { get; init; }
    public required string Status { get; init; }
    public required string CreatedAt { get; init; }
    public double? Latitude { get; init; }
    public double? Longitude { get; init; }

    /// <summary>Relevance score in [0, 1]; results are ranked by this descending.</summary>
    public required double Score { get; init; }

    /// <summary>True when this crop matched via a substitute type, not the exact ask.</summary>
    public required bool IsSubstitute { get; init; }

    /// <summary>
    /// Farmer's reliability in [0, 1] (listing-conversion track record). Absent
    /// when the server has no history for the farmer; clients default it to a
    /// neutral value in scoring.
    /// </summary>
    public double? FarmerReliability { get; init; }
}

/// <summary>Response body for GET /match.</summary>
public sealed record GetMatchesResponse
{
    public required List<MatchDto> Matches { get; init; }
    public required int Count { get; init; }

    /// <summary>
    /// Total eligible matches BEFORE the limit cut. Clients derive match-count
    /// badges from this so counts don't saturate at the page limit.
    /// </summary>
    public int TotalMatches { get; init; }
}
