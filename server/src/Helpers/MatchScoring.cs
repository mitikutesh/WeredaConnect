namespace WeredaConnect.Server.Helpers;

/// <summary>
/// Inputs to <see cref="MatchScoring"/> for a single buyer query.
/// Price/quantity/woreda/coordinates are optional — absent constraints simply
/// don't penalise. Coordinates (when present) are the buyer's delivery point,
/// resolved client-side from the delivery woreda.
/// </summary>
public sealed record MatchCriteria
{
    public required string CropType { get; init; }
    public double? MaxPricePerKg { get; init; }
    public double? QuantityNeeded { get; init; }
    public string? DeliveryWoreda { get; init; }
    public double? DeliveryLatitude { get; init; }
    public double? DeliveryLongitude { get; init; }
    public required DateTime NowUtc { get; init; }
}

/// <summary>Scoring-relevant fields of a candidate crop listing.</summary>
public sealed record CropCandidate
{
    public required string CropType { get; init; }
    public double PricePerKg { get; init; }
    public double Quantity { get; init; }
    public string? LocationName { get; init; }
    public double? Latitude { get; init; }
    public double? Longitude { get; init; }
    public required DateTime CreatedAt { get; init; }
}

/// <summary>
/// Server-side relevance scoring for buyer intent matching.
///
/// ⚠️ MUST stay in lockstep with the mobile Dart scorer at
/// mobile/lib/features/buyer/domain/services/match_scorer.dart — identical
/// weights, near-miss tolerance bands, substitute map, and distance decay so
/// server-ranked and on-device-ranked results agree. A parity test
/// (MatchScoringTests) guards the headline numbers. Update BOTH sides together.
///
/// A transparent weighted sum of independent signals (NOT a learned model):
/// type(+substitutes), price near-miss, geo (haversine when coordinates are
/// available, else woreda-string proxy), partial-fill quantity, and freshness.
/// AOT-safe: pure arithmetic, no reflection.
/// </summary>
public static class MatchScoring
{
    // Weights (sum = 1.0). Keep identical to the Dart scorer.
    public const double WType = 0.35;
    public const double WPrice = 0.20;
    public const double WGeo = 0.20;
    public const double WQuantity = 0.15;
    public const double WFreshness = 0.10;

    public const double PriceTolerance = 0.15; // +15% near-miss band
    public const double MinQuantityFraction = 0.5;
    public const double SubstituteTypeScore = 0.6;

    public const int FreshFullDays = 7;
    public const int FreshFloorDays = 30;
    public const double FreshFloor = 0.3;
    public const double GeoNeutral = 0.3;
    public const double GeoHalfScoreKm = 50.0;

    private const double EarthRadiusKm = 6371.0;

    /// <summary>
    /// Accepted-substitute map (lowercase English crop names). Mirror of the
    /// Dart scorer's map — keep the two identical.
    /// </summary>
    private static readonly Dictionary<string, string[]> Substitutes =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["wheat"] = new[] { "barley" },
            ["barley"] = new[] { "wheat" },
            ["maize"] = new[] { "sorghum" },
            ["sorghum"] = new[] { "maize" },
            ["chickpea"] = new[] { "lentils" },
            ["lentils"] = new[] { "chickpea" },
        };

    /// <summary>Crop types to query: the exact type plus accepted substitutes.</summary>
    public static IReadOnlyList<string> CandidateCropTypes(string cropType)
    {
        var type = cropType.ToLowerInvariant();
        var result = new List<string> { type };
        if (Substitutes.TryGetValue(type, out var subs))
        {
            result.AddRange(subs);
        }
        return result;
    }

    public static bool IsSubstitute(string intentCropType, string listingCropType)
        => !string.Equals(intentCropType, listingCropType, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether a crop qualifies as a candidate (type membership + price/quantity
    /// near-miss bands). Centralised so the scoring path and any count path agree.
    /// </summary>
    public static bool IsCandidate(MatchCriteria criteria, CropCandidate crop)
    {
        var types = CandidateCropTypes(criteria.CropType);
        if (!types.Any(t => string.Equals(t, crop.CropType, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        if (criteria.MaxPricePerKg is double max && max > 0
            && crop.PricePerKg > max * (1 + PriceTolerance))
        {
            return false;
        }

        if (criteria.QuantityNeeded is double need && need > 0
            && crop.Quantity < need * MinQuantityFraction)
        {
            return false;
        }

        return true;
    }

    public static double TypeScore(MatchCriteria criteria, CropCandidate crop)
        => IsSubstitute(criteria.CropType, crop.CropType) ? SubstituteTypeScore : 1.0;

    public static double PriceScore(MatchCriteria criteria, CropCandidate crop)
    {
        // No budget given → price is unconstrained (server-only extension; the
        // Dart scorer always has a budget).
        if (criteria.MaxPricePerKg is not double max || max <= 0)
        {
            return 1.0;
        }

        if (crop.PricePerKg <= max)
        {
            return 1.0;
        }

        var over = (crop.PricePerKg - max) / max;
        return Math.Clamp(1.0 - over / PriceTolerance, 0.0, 1.0);
    }

    public static double QuantityScore(MatchCriteria criteria, CropCandidate crop)
    {
        if (criteria.QuantityNeeded is not double need || need <= 0)
        {
            return 1.0;
        }

        return Math.Clamp(crop.Quantity / need, 0.0, 1.0);
    }

    /// <summary>
    /// Geographic relevance: true haversine distance (decayed) when the buyer's
    /// delivery coordinates are known and the crop has a real GPS fix; otherwise
    /// a woreda-string proximity proxy.
    /// </summary>
    public static double GeoScore(MatchCriteria criteria, CropCandidate crop)
    {
        if (criteria.DeliveryLatitude is double dlat
            && criteria.DeliveryLongitude is double dlon
            && HasRealFix(crop.Latitude, crop.Longitude))
        {
            var km = HaversineKm(crop.Latitude!.Value, crop.Longitude!.Value, dlat, dlon);
            return 1.0 / (1.0 + km / GeoHalfScoreKm);
        }

        return WoredaProxyScore(criteria.DeliveryWoreda, crop.LocationName);
    }

    public static double FreshnessScore(MatchCriteria criteria, CropCandidate crop)
    {
        // Whole days, matching Dart's Duration.inDays truncation.
        var ageDays = (criteria.NowUtc - crop.CreatedAt).Days;
        if (ageDays <= FreshFullDays)
        {
            return 1.0;
        }

        if (ageDays >= FreshFloorDays)
        {
            return FreshFloor;
        }

        const double span = FreshFloorDays - FreshFullDays;
        var decayed = 1.0 - ((ageDays - FreshFullDays) / span) * (1.0 - FreshFloor);
        return Math.Clamp(decayed, FreshFloor, 1.0);
    }

    /// <summary>Overall 0..1 relevance score for a crop against the criteria.</summary>
    public static double ScoreListing(MatchCriteria criteria, CropCandidate crop)
        => WType * TypeScore(criteria, crop)
         + WPrice * PriceScore(criteria, crop)
         + WGeo * GeoScore(criteria, crop)
         + WQuantity * QuantityScore(criteria, crop)
         + WFreshness * FreshnessScore(criteria, crop);

    private static bool HasRealFix(double? lat, double? lon)
    {
        if (lat is not double la || lon is not double lo)
        {
            return false;
        }

        if (la == 0 && lo == 0)
        {
            return false; // "no GPS captured" sentinel
        }

        return Math.Abs(la) <= 90 && Math.Abs(lo) <= 180;
    }

    private static double WoredaProxyScore(string? deliveryWoreda, string? cropLocationName)
    {
        var crop = (cropLocationName ?? string.Empty).Trim().ToLowerInvariant();
        var dest = (deliveryWoreda ?? string.Empty).Trim().ToLowerInvariant();
        if (crop.Length == 0 || dest.Length == 0)
        {
            return GeoNeutral;
        }

        if (crop == dest)
        {
            return 1.0;
        }

        if (crop.Contains(dest) || dest.Contains(crop))
        {
            return 0.6;
        }

        return GeoNeutral;
    }

    /// <summary>Great-circle distance in kilometres (Haversine, WGS-84 mean radius).</summary>
    private static double HaversineKm(double lat1, double lon1, double lat2, double lon2)
    {
        var dLat = ToRadians(lat2 - lat1);
        var dLon = ToRadians(lon2 - lon1);
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
              + Math.Cos(ToRadians(lat1)) * Math.Cos(ToRadians(lat2))
              * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        var c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        return EarthRadiusKm * c;
    }

    private static double ToRadians(double degrees) => degrees * Math.PI / 180.0;
}
