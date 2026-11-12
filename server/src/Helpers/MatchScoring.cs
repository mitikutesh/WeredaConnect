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

    /// <summary>
    /// ESC-30: the farmer's <c>ownerReliabilityPct</c> (0 to 100, reputation-api.md section 7)
    /// as the caller is allowed to read it. Null when the caller gets no value (the
    /// <c>reputationPublicTags</c> flag is off, the farmer has fewer than 3 orders, or the row
    /// has no copy yet): the score then uses <see cref="MatchScoring.ReliabilityNeutral"/>.
    /// </summary>
    public int? OwnerReliabilityPct { get; init; }
}

/// <summary>
/// MATCH-01: the route and the load a buyer wants a trip for: a matched crop at
/// <see cref="ListingWoreda"/> going to <see cref="DeliveryWoreda"/>. Coordinates are the
/// woreda centroids as the caller resolved them (the server holds no centroid table, as for
/// <see cref="MatchCriteria"/>); null when unknown, and the woreda names decide.
/// </summary>
public sealed record TripRouteQuery
{
    public string? ListingWoreda { get; init; }
    public double? ListingLatitude { get; init; }
    public double? ListingLongitude { get; init; }
    public string? DeliveryWoreda { get; init; }
    public double? DeliveryLatitude { get; init; }
    public double? DeliveryLongitude { get; init; }

    /// <summary>Quintals the buyer needs; null or not positive means any useful load.</summary>
    public double? QuantityNeeded { get; init; }

    public required DateTime NowUtc { get; init; }

    /// <summary>
    /// FUP-122: hours ahead a trip must depart for an order create to take it (24 while the
    /// commitments run); null or 0 refuses nothing.
    /// </summary>
    public int? OrderLeadHours { get; init; }

    /// <summary>The buyer's deadline (UTC); a trip departing after it is not suggested.</summary>
    public DateTime? NeededByUtc { get; init; }
}

/// <summary>MATCH-01: the pairing-relevant fields of an open trip.</summary>
public sealed record TripCandidate
{
    public required string TripId { get; init; }
    public string Status { get; init; } = "open";
    public string? FromWoreda { get; init; }
    public double? FromLatitude { get; init; }
    public double? FromLongitude { get; init; }
    public string? ToWoreda { get; init; }
    public double? ToLatitude { get; init; }
    public double? ToLongitude { get; init; }

    /// <summary>The departure instant (UTC), Ethiopia midnight of the trucker's day.</summary>
    public required DateTime DepartureUtc { get; init; }

    public double Capacity { get; init; }

    /// <summary>ESC-15: the room left once the trip has bookings; null means the whole capacity.</summary>
    public double? RemainingQuintals { get; init; }

    public double PricePerQuintal { get; init; }

    /// <summary>ESC-30: the trucker's <c>ownerReliabilityPct</c>; null counts as neutral.</summary>
    public int? OwnerReliabilityPct { get; init; }
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
/// available, else woreda-string proxy), partial-fill quantity, freshness, and
/// farmer reliability. AOT-safe: pure arithmetic, no reflection.
/// </summary>
public static class MatchScoring
{
    // Weights (sum = 1.0). Keep identical to the Dart scorer.
    public const double WType = 0.30;
    public const double WPrice = 0.20;
    public const double WGeo = 0.18;
    public const double WQuantity = 0.12;
    public const double WFreshness = 0.10;
    public const double WReliability = 0.10;

    public const double PriceTolerance = 0.15; // +15% near-miss band
    public const double MinQuantityFraction = 0.5;
    public const double SubstituteTypeScore = 0.6;

    public const int FreshFullDays = 7;
    public const int FreshFloorDays = 30;
    public const double FreshFloor = 0.3;
    public const double GeoNeutral = 0.3;

    /// <summary>Geo score for a whole-word partial match of the two woreda names.</summary>
    public const double GeoPartial = 0.6;
    public const double GeoHalfScoreKm = 50.0;

    /// <summary>
    /// Reliability assumed when the owner's <c>reliabilityPct</c> is absent (flag off, fewer than
    /// 3 orders, a row without the copy). It is the value every listing scored with before
    /// ESC-30 in practice, so a flags-off ranking is the old neutral ranking, and a known
    /// reliability only moves a listing up (above 70) or down (below 70) from there.
    /// </summary>
    public const double ReliabilityNeutral = 0.7;

    /// <summary>
    /// MATCH-01: how far (km, straight line between centroids) a trip's start may be from the
    /// crop's woreda, and its end from the delivery woreda, for the trip to be suggested. Keeps
    /// a neighbouring town (Hawassa and Shashemene 22 km, Adama and Bishoftu 39.5), leaves out
    /// the next market over (Addis Ababa and Bishoftu 40.8). Same value as the Dart
    /// <c>tripNearRadiusKm</c>.
    /// </summary>
    public const double TripNearRadiusKm = 40.0;

    /// <summary>
    /// MATCH-01: slack on the room-left check, the order create's tolerance
    /// (<c>MoneyTolerance</c> in <c>BindOrderToTrip</c>).
    /// </summary>
    public const double TripRoomSlackQuintals = 0.01;

    /// <summary>MATCH-01: the room a trip needs when the request names no positive quantity.</summary>
    public const double TripMinUsefulLoadQuintals = 1.0;

    private const double EarthRadiusKm = 6371.0;

    /// <summary>MATCH-01: the Dart <c>GpsDistanceService.earthRadiusMeters</c>, for <see cref="TripEndKm"/>.</summary>
    private const double EarthRadiusMeters = 6371000.0;

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
        // No budget given → price is unconstrained. The Dart scorer applies the
        // same rule for a non-positive budget (the max-price field is optional).
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

    /// <summary>
    /// ESC-30: an owner's <c>reliabilityPct</c> (0 to 100) as a 0..1 signal, or
    /// <see cref="ReliabilityNeutral"/> when absent. Out of range values are clamped. The Dart
    /// scorer's <c>reliabilityFromPct</c> is the same rule.
    /// </summary>
    public static double ReliabilityFromPct(int? reliabilityPct)
        => reliabilityPct is int pct ? Math.Clamp(pct / 100.0, 0.0, 1.0) : ReliabilityNeutral;

    /// <summary>
    /// Farmer reliability signal: the crop's
    /// <see cref="CropCandidate.OwnerReliabilityPct"/> through <see cref="ReliabilityFromPct"/>.
    /// </summary>
    public static double ReliabilityScore(CropCandidate crop)
        => ReliabilityFromPct(crop.OwnerReliabilityPct);

    /// <summary>Overall 0..1 relevance score for a crop against the criteria.</summary>
    public static double ScoreListing(MatchCriteria criteria, CropCandidate crop)
        => WType * TypeScore(criteria, crop)
         + WPrice * PriceScore(criteria, crop)
         + WGeo * GeoScore(criteria, crop)
         + WQuantity * QuantityScore(criteria, crop)
         + WFreshness * FreshnessScore(criteria, crop)
         + WReliability * ReliabilityScore(crop);

    /// <summary>
    /// MATCH-01: the trips to suggest for the query, best first. The Dart
    /// <c>MatchScorer.suggestTrips</c> is the same rule, used by the phone and web; this mirror
    /// keeps the rule parity-tested for a server-side pairing (match-api.md 1.5.0). A trip is kept
    /// when <see cref="IsTripEligible"/> and <see cref="TripRouteScore"/> serves the route.
    /// Order: cheapest <c>PricePerQuintal</c>; then the higher route score (the exact route above
    /// a neighbouring woreda); then the more reliable trucker; then the earlier departure; then
    /// <c>TripId</c> (ordinal), so the input order never matters.
    /// </summary>
    public static IReadOnlyList<TripCandidate> SuggestTrips(TripRouteQuery query, IEnumerable<TripCandidate> trips)
    {
        var scored = new List<(TripCandidate Trip, double Route, int Index)>();
        var index = 0;
        foreach (var trip in trips)
        {
            var position = index++;
            if (IsTripEligible(query, trip) && TripRouteScore(query, trip) is double route)
            {
                scored.Add((trip, route, position));
            }
        }

        return scored
            .OrderBy(s => s.Trip.PricePerQuintal)
            .ThenByDescending(s => s.Route)
            .ThenByDescending(s => ReliabilityFromPct(s.Trip.OwnerReliabilityPct))
            .ThenBy(s => s.Trip.DepartureUtc)
            .ThenBy(s => s.Trip.TripId, StringComparer.Ordinal)
            .ThenBy(s => s.Index)
            .Select(s => s.Trip)
            .ToList();
    }

    /// <summary>
    /// MATCH-01: whether the trip can take the buyer's load, before the route is looked at:
    /// <c>open</c>; its Ethiopia departure day not over (<see cref="TripDeparture.HasDeparted"/>);
    /// not departing within <see cref="TripRouteQuery.OrderLeadHours"/>; room left (remaining,
    /// else capacity) at least the need less <see cref="TripRoomSlackQuintals"/> (the need is
    /// <see cref="TripMinUsefulLoadQuintals"/> when not positive); departing on or before
    /// <see cref="TripRouteQuery.NeededByUtc"/> when set. Outside that window a trip is dropped.
    /// </summary>
    public static bool IsTripEligible(TripRouteQuery query, TripCandidate trip)
    {
        if (!string.Equals(trip.Status, "open", StringComparison.Ordinal))
        {
            return false;
        }

        if (TripDeparture.HasDeparted(trip.DepartureUtc, query.NowUtc))
        {
            return false;
        }

        if (query.OrderLeadHours is int lead && lead > 0
            && trip.DepartureUtc < query.NowUtc.AddHours(lead))
        {
            return false;
        }

        var need = query.QuantityNeeded is double q && q > 0 ? q : TripMinUsefulLoadQuintals;
        if ((trip.RemainingQuintals ?? trip.Capacity) < need - TripRoomSlackQuintals)
        {
            return false;
        }

        return query.NeededByUtc is not DateTime deadline || trip.DepartureUtc <= deadline;
    }

    /// <summary>
    /// MATCH-01: how well the trip's route serves the query, in 0..1: the mean of
    /// <see cref="TripEndScore"/> for the trip's start against the crop's woreda and for its end
    /// against the delivery woreda. 1.0 is the exact route. Null (not suggested) when either end
    /// does not serve, or when the trip runs the other way: scored with the crop's and the
    /// delivery woreda swapped it fits better than forward (Bishoftu to Adama for a crop in Adama
    /// going to Bishoftu). Equal scores (the crop and the delivery in one woreda) stay. The Dart
    /// <c>tripRouteScore</c> is the same rule.
    /// </summary>
    public static double? TripRouteScore(TripRouteQuery query, TripCandidate trip)
    {
        if (MeanRouteScore(trip,
                query.ListingWoreda, query.ListingLatitude, query.ListingLongitude,
                query.DeliveryWoreda, query.DeliveryLatitude, query.DeliveryLongitude) is not double forward)
        {
            return null;
        }

        var reversed = MeanRouteScore(trip,
            query.DeliveryWoreda, query.DeliveryLatitude, query.DeliveryLongitude,
            query.ListingWoreda, query.ListingLatitude, query.ListingLongitude);
        return reversed is double r && r > forward ? null : forward;
    }

    /// <summary>
    /// The mean of the trip's start against the start place and its end against the end place,
    /// or null when either end does not serve.
    /// </summary>
    private static double? MeanRouteScore(
        TripCandidate trip,
        string? startWoreda, double? startLatitude, double? startLongitude,
        string? endWoreda, double? endLatitude, double? endLongitude)
    {
        if (TripEndScore(trip.FromWoreda, trip.FromLatitude, trip.FromLongitude,
                startWoreda, startLatitude, startLongitude) is not double from)
        {
            return null;
        }

        if (TripEndScore(trip.ToWoreda, trip.ToLatitude, trip.ToLongitude,
                endWoreda, endLatitude, endLongitude) is not double to)
        {
            return null;
        }

        return (from + to) / 2;
    }

    /// <summary>
    /// MATCH-01: how well one end of a trip serves the place the buyer wants it at, in 0..1, or
    /// null when it does not. The best of what applies, whether or not the centroids are known:
    /// 1.0 for the same place after <see cref="WoredaNames.Normalize"/>;
    /// <c>1 / (1 + km / GeoHalfScoreKm)</c> when both centroids are known and at most
    /// <see cref="TripNearRadiusKm"/> apart (<see cref="TripEndKm"/>); <see cref="GeoPartial"/>
    /// for a whole-word name match. For a woreda without a centroid only the name rules can
    /// apply. A blank name never serves.
    /// </summary>
    public static double? TripEndScore(
        string? tripWoreda, double? tripLatitude, double? tripLongitude,
        string? wantedWoreda, double? wantedLatitude, double? wantedLongitude)
    {
        var tripKey = WoredaNames.Normalize(tripWoreda);
        var wantedKey = WoredaNames.Normalize(wantedWoreda);
        if (tripKey.Length == 0 || wantedKey.Length == 0)
        {
            return null;
        }

        var match = WoredaNames.CompareKeys(tripKey, wantedKey);
        if (match == WoredaMatch.Exact)
        {
            return 1.0;
        }

        double? score = match == WoredaMatch.Partial ? GeoPartial : null;
        if (TripEndKm(tripLatitude, tripLongitude, wantedLatitude, wantedLongitude) is double km
            && km <= TripNearRadiusKm)
        {
            var byDistance = 1.0 / (1.0 + km / GeoHalfScoreKm);
            if (score is not double named || byDistance > named)
            {
                score = byDistance;
            }
        }

        return score;
    }

    /// <summary>
    /// MATCH-01: the great-circle distance between two centroids in km, rounded half away from
    /// zero to 0.1 km (as Dart's <c>round()</c>), or null when either point is unknown or not a
    /// real position. The rounding keeps the platforms on the same number whatever the last
    /// digit of their sin and cos. Computed as metres (radius 6371000) over 1000, the same
    /// expression as the Dart <c>GpsDistanceService.haversineDistance / 1000.0</c>, so a
    /// distance on a 0.05 km boundary rounds the same way on both.
    /// </summary>
    public static double? TripEndKm(double? lat1, double? lon1, double? lat2, double? lon2)
    {
        if (!HasRealFix(lat1, lon1) || !HasRealFix(lat2, lon2))
        {
            return null;
        }

        var km = EarthRadiusMeters * CentralAngle(lat1!.Value, lon1!.Value, lat2!.Value, lon2!.Value) / 1000.0;
        return Math.Round(km * 10, MidpointRounding.AwayFromZero) / 10;
    }

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

    /// <summary>
    /// Woreda-string proximity proxy. Both names go through the shared
    /// <see cref="WoredaNames"/> rule, so "Ādama", "Adama, Oromia" and "adama"
    /// are the same place; the Dart MatchScorer applies the identical rule.
    /// </summary>
    private static double WoredaProxyScore(string? deliveryWoreda, string? cropLocationName)
        => WoredaNames.Compare(deliveryWoreda, cropLocationName) switch
        {
            WoredaMatch.Exact => 1.0,
            WoredaMatch.Partial => GeoPartial,
            _ => GeoNeutral,
        };

    /// <summary>Great-circle distance in kilometres (Haversine, WGS-84 mean radius).</summary>
    private static double HaversineKm(double lat1, double lon1, double lat2, double lon2)
        => EarthRadiusKm * CentralAngle(lat1, lon1, lat2, lon2);

    /// <summary>
    /// The haversine central angle in radians, operation for operation the Dart
    /// <c>GpsDistanceService.haversineDistance</c> before its radius.
    /// </summary>
    private static double CentralAngle(double lat1, double lon1, double lat2, double lon2)
    {
        var dLat = ToRadians(lat2 - lat1);
        var dLon = ToRadians(lon2 - lon1);
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
              + Math.Cos(ToRadians(lat1)) * Math.Cos(ToRadians(lat2))
              * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }

    private static double ToRadians(double degrees) => degrees * Math.PI / 180.0;
}
