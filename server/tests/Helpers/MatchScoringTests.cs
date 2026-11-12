using WeredaConnect.Server.Helpers;
using Xunit;

namespace WeredaConnect.Server.Tests.Helpers;

/// <summary>
/// Unit + parity tests for <see cref="MatchScoring"/>. These mirror the Dart
/// scorer's tests (mobile match_scorer_test.dart) so server and on-device
/// ranking stay in lockstep — if a weight or band changes on one side, a test
/// here should change too.
/// </summary>
public class MatchScoringTests
{
    private static readonly DateTime Now = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    private static MatchCriteria Criteria(
        string cropType = "wheat",
        double? maxPrice = 100,
        double? qty = 10,
        string? woreda = "Adama",
        double? lat = null,
        double? lon = null)
        => new()
        {
            CropType = cropType,
            MaxPricePerKg = maxPrice,
            QuantityNeeded = qty,
            DeliveryWoreda = woreda,
            DeliveryLatitude = lat,
            DeliveryLongitude = lon,
            NowUtc = Now,
        };

    private static CropCandidate Crop(
        string cropType = "wheat",
        double price = 100,
        double qty = 10,
        string? loc = "Adama",
        double? lat = null,
        double? lon = null,
        DateTime? created = null,
        int? reliabilityPct = null)
        => new()
        {
            CropType = cropType,
            PricePerKg = price,
            Quantity = qty,
            LocationName = loc,
            Latitude = lat,
            Longitude = lon,
            CreatedAt = created ?? Now,
            OwnerReliabilityPct = reliabilityPct,
        };

    [Fact]
    public void CandidateCropTypes_IncludesExactPlusSubstitutes()
    {
        Assert.Contains("wheat", MatchScoring.CandidateCropTypes("wheat"));
        Assert.Contains("barley", MatchScoring.CandidateCropTypes("wheat"));
    }

    [Fact]
    public void CandidateCropTypes_TeffHasNoSubstitutes()
    {
        Assert.Equal(new[] { "teff" }, MatchScoring.CandidateCropTypes("teff"));
    }

    [Fact]
    public void IsCandidate_AcceptsExactAndSubstitute_RejectsUnrelated()
    {
        Assert.True(MatchScoring.IsCandidate(Criteria(), Crop()));
        Assert.True(MatchScoring.IsCandidate(Criteria(), Crop(cropType: "barley")));
        Assert.False(MatchScoring.IsCandidate(Criteria(), Crop(cropType: "teff")));
    }

    [Fact]
    public void IsCandidate_PriceNearMissBand()
    {
        Assert.True(MatchScoring.IsCandidate(Criteria(), Crop(price: 114)));  // within +15%
        Assert.False(MatchScoring.IsCandidate(Criteria(), Crop(price: 120))); // beyond +15%
    }

    [Fact]
    public void IsCandidate_QuantityFloor()
    {
        Assert.False(MatchScoring.IsCandidate(Criteria(), Crop(qty: 4)));  // < 50% of 10
        Assert.True(MatchScoring.IsCandidate(Criteria(), Crop(qty: 6)));   // >= 50%
    }

    [Fact]
    public void TypeScore_ExactBeatsSubstitute()
    {
        Assert.Equal(1.0, MatchScoring.TypeScore(Criteria(), Crop()));
        Assert.Equal(MatchScoring.SubstituteTypeScore,
            MatchScoring.TypeScore(Criteria(), Crop(cropType: "barley")));
    }

    [Fact]
    public void PriceScore_FullWithinBudget_DecaysAbove()
    {
        Assert.Equal(1.0, MatchScoring.PriceScore(Criteria(), Crop(price: 90)));
        var nearMiss = MatchScoring.PriceScore(Criteria(), Crop(price: 110));
        Assert.True(nearMiss > 0 && nearMiss < 1.0);
    }

    [Fact]
    public void QuantityScore_PartialFillRatio()
    {
        Assert.Equal(0.5, MatchScoring.QuantityScore(Criteria(), Crop(qty: 5)));
        Assert.Equal(1.0, MatchScoring.QuantityScore(Criteria(), Crop(qty: 20)));
    }

    [Fact]
    public void GeoScore_WoredaProxy_SameWoredaIsBest()
    {
        Assert.Equal(1.0, MatchScoring.GeoScore(Criteria(), Crop(loc: "Adama")));
        Assert.Equal(MatchScoring.GeoNeutral,
            MatchScoring.GeoScore(Criteria(), Crop(loc: "Mekelle")));
    }

    [Fact]
    public void GeoScore_WoredaProxy_NormalisesSpelling()
    {
        // SIM-52: picker spelling ("Ādama") vs typed "Adama, Oromia" is the
        // same place. Mirrors the Dart scorer test of the same name.
        Assert.Equal(1.0,
            MatchScoring.GeoScore(Criteria(woreda: "Ādama"), Crop(loc: "Adama, Oromia")));
        Assert.Equal(1.0,
            MatchScoring.GeoScore(Criteria(woreda: "adama "), Crop(loc: "ADAMA")));
        // Whole-word overlap keeps the partial score.
        Assert.Equal(MatchScoring.GeoPartial,
            MatchScoring.GeoScore(Criteria(woreda: "Adama"), Crop(loc: "Adama Zone")));
        // Raw-substring false positive is gone: "Ambo" is not "Gambo".
        Assert.Equal(MatchScoring.GeoNeutral,
            MatchScoring.GeoScore(Criteria(woreda: "Ambo"), Crop(loc: "Gambo")));
    }

    [Fact]
    public void GeoScore_Haversine_NearBeatsFar()
    {
        // Buyer delivering to Adama centroid; crop on Adama vs crop on Mekelle.
        var crit = Criteria(lat: 8.54, lon: 39.27);
        var near = MatchScoring.GeoScore(crit, Crop(lat: 8.54, lon: 39.27));
        var far = MatchScoring.GeoScore(crit, Crop(lat: 13.49, lon: 39.47));
        Assert.True(near > 0.9);
        Assert.True(far < near);
        Assert.True(far < MatchScoring.GeoNeutral);
    }

    [Fact]
    public void FreshnessScore_DecaysFromFullToFloor()
    {
        Assert.Equal(1.0, MatchScoring.FreshnessScore(
            Criteria(), Crop(created: Now.AddDays(-1))));
        Assert.Equal(MatchScoring.FreshFloor, MatchScoring.FreshnessScore(
            Criteria(), Crop(created: Now.AddDays(-60))));
    }

    [Fact]
    public void ScoreListing_PerfectExactMatch_IsTop()
    {
        // A perfect match from a fully-reliable farmer → 1.0.
        Assert.Equal(1.0, MatchScoring.ScoreListing(Criteria(), Crop(reliabilityPct: 100)), 6);
    }

    [Fact]
    public void ReliabilityScore_DefaultsToNeutral_WhenUnknown()
    {
        Assert.Equal(MatchScoring.ReliabilityNeutral, MatchScoring.ReliabilityScore(Crop()));
        Assert.Equal(0.4, MatchScoring.ReliabilityScore(Crop(reliabilityPct: 40)));
    }

    [Fact]
    public void ScoreListing_ExactOutranksSubstitute()
    {
        var exact = MatchScoring.ScoreListing(Criteria(), Crop());
        var sub = MatchScoring.ScoreListing(Criteria(), Crop(cropType: "barley"));
        Assert.True(exact > sub);
    }

    // ── Parity guards (MUST match mobile match_scorer_test.dart) ─────────────
    // The two scorers are independent implementations whose agreement is the
    // contract's hard invariant. These golden vectors and the weight-sum check
    // are duplicated verbatim on the Dart side; if a weight/band changes on one
    // platform, the identical assertion fails on the other.

    [Fact]
    public void Weights_SumToOne()
    {
        Assert.Equal(1.0, MatchScoring.WType + MatchScoring.WPrice + MatchScoring.WGeo
                          + MatchScoring.WQuantity + MatchScoring.WFreshness
                          + MatchScoring.WReliability, 10);
    }

    [Fact]
    public void ScoreListing_GoldenVector_SubstitutePartialDisjoint()
    {
        // type=substitute(0.6), price=within(1.0), qty=5/10(0.5),
        // geo=disjoint-proxy(0.3), freshness=fresh(1.0), reliability=neutral(0.7).
        // 0.30*0.6 + 0.20*1.0 + 0.18*0.3 + 0.12*0.5 + 0.10*1.0 + 0.10*0.7 = 0.664
        var score = MatchScoring.ScoreListing(
            Criteria(),
            Crop(cropType: "barley", price: 100, qty: 5, loc: "Mekelle"));
        Assert.Equal(0.664, score, 6);
    }

    [Fact]
    public void ScoreListing_GoldenVector_PriceNearMissBand()
    {
        // type=exact(1.0), price=+10% over → 1-(0.10/0.15)=0.333333,
        // qty=1.0, geo=same-woreda(1.0), freshness=fresh(1.0), reliability=neutral(0.7).
        // 0.30 + 0.20*0.333333 + 0.18 + 0.12 + 0.10 + 0.10*0.7 = 0.836667
        var score = MatchScoring.ScoreListing(Criteria(), Crop(price: 110));
        Assert.Equal(0.836667, score, 6);
    }

    // ESC-30 golden vectors: ownerReliabilityPct / 100 is the reliability signal, absent is the
    // neutral 0.7. The same table is in match_scorer_test.dart ("reliability golden vectors").
    // A perfect match otherwise (type, price, geo, quantity, freshness all 1.0), so the score is
    // 0.90 + 0.10 * reliability.
    [Theory]
    [InlineData(null, 0.7, 0.97)]   // absent: flag off, under 3 orders, or a row with no copy
    [InlineData(0, 0.0, 0.90)]
    [InlineData(50, 0.5, 0.95)]
    [InlineData(70, 0.7, 0.97)]     // equal to the neutral value: same score as absent
    [InlineData(83, 0.83, 0.983)]
    [InlineData(100, 1.0, 1.0)]
    [InlineData(-5, 0.0, 0.90)]     // out of range values are clamped
    [InlineData(150, 1.0, 1.0)]
    public void ScoreListing_GoldenVector_Reliability(int? reliabilityPct, double signal, double score)
    {
        Assert.Equal(signal, MatchScoring.ReliabilityFromPct(reliabilityPct), 12);
        Assert.Equal(signal, MatchScoring.ReliabilityScore(Crop(reliabilityPct: reliabilityPct)), 12);
        Assert.Equal(score, MatchScoring.ScoreListing(Criteria(), Crop(reliabilityPct: reliabilityPct)), 9);
    }

    [Fact]
    public void ScoreListing_GoldenVector_ReliabilityMovesTheScoreByItsFullWeight()
    {
        // A perfect match from a farmer at 20 against one at 100 differs by
        // WReliability * (1.0 - 0.2) = 0.08.
        var flaky = MatchScoring.ScoreListing(Criteria(), Crop(reliabilityPct: 20));
        var proven = MatchScoring.ScoreListing(Criteria(), Crop(reliabilityPct: 100));
        Assert.Equal(MatchScoring.WReliability * 0.8, proven - flaky, 9);
    }

    [Fact]
    public void Ranking_GoldenOrder_WithReliability_IsStableAcrossPlatforms()
    {
        // Duplicated verbatim on the Dart side. Four listings equal in everything but the
        // farmer's reliability, and one pricier listing from a proven farmer: reliability only
        // reorders listings that are otherwise close.
        var scores = new Dictionary<string, double>
        {
            ["unknown"] = MatchScoring.ScoreListing(Criteria(), Crop()),                                    // 0.97
            ["proven"] = MatchScoring.ScoreListing(Criteria(), Crop(reliabilityPct: 100)),                  // 1.0
            ["average"] = MatchScoring.ScoreListing(Criteria(), Crop(reliabilityPct: 50)),                  // 0.95
            ["flaky"] = MatchScoring.ScoreListing(Criteria(), Crop(reliabilityPct: 0)),                     // 0.90
            ["proven-pricey"] = MatchScoring.ScoreListing(Criteria(), Crop(price: 110, reliabilityPct: 100)), // 0.866667
        };
        var ranked = scores.OrderByDescending(kv => kv.Value).Select(kv => kv.Key).ToArray();
        Assert.Equal(new[] { "proven", "unknown", "average", "flaky", "proven-pricey" }, ranked);
        Assert.Equal(0.866667, scores["proven-pricey"], 6);
    }

    [Fact]
    public void GeoScore_Haversine_AdamaToMekelle_PinsRadiusAndUnits()
    {
        // Adama (8.54, 39.27) → Mekelle (13.49, 39.47) ≈ 550 km.
        // score = 1/(1 + km/50) ≈ 1/(1+11) ≈ 0.083. Pins WGS-84 radius + km units
        // so a radius/units regression (vs the mobile haversine) is caught.
        var score = MatchScoring.GeoScore(
            Criteria(lat: 8.54, lon: 39.27),
            Crop(lat: 13.49, lon: 39.47));
        Assert.True(score is > 0.06 and < 0.10,
            $"Adama→Mekelle geo score {score} outside expected ~0.083 band");
    }

    [Fact]
    public void Substitutes_FullMapParity()
    {
        // The COMPLETE substitute map, as exact lists — a one-sided edit to any
        // pair (not just the golden-vector pairs) fails here and on the Dart side.
        Assert.Equal(new[] { "wheat", "barley" }, MatchScoring.CandidateCropTypes("wheat"));
        Assert.Equal(new[] { "barley", "wheat" }, MatchScoring.CandidateCropTypes("barley"));
        Assert.Equal(new[] { "maize", "sorghum" }, MatchScoring.CandidateCropTypes("maize"));
        Assert.Equal(new[] { "sorghum", "maize" }, MatchScoring.CandidateCropTypes("sorghum"));
        Assert.Equal(new[] { "chickpea", "lentils" }, MatchScoring.CandidateCropTypes("chickpea"));
        Assert.Equal(new[] { "lentils", "chickpea" }, MatchScoring.CandidateCropTypes("lentils"));
    }

    [Fact]
    public void ScoreListing_GoldenVector_MaizeSorghumMirrorsWheatBarley()
    {
        // The maize↔sorghum pair must score exactly like wheat↔barley — pins the
        // one substitute pair the original golden vectors didn't exercise.
        var viaGrain = MatchScoring.ScoreListing(
            Criteria(), Crop(cropType: "barley", qty: 5, loc: "Mekelle"));
        var viaMaize = MatchScoring.ScoreListing(
            Criteria(cropType: "maize"),
            Crop(cropType: "sorghum", qty: 5, loc: "Mekelle"));
        Assert.Equal(0.664, viaMaize, 6);
        Assert.Equal(viaGrain, viaMaize, 9);
    }

    [Fact]
    public void Ranking_GoldenOrder_IsStableAcrossPlatforms()
    {
        // A fixed candidate set must produce ONE exact ranked order, duplicated
        // verbatim on the Dart side — catches formula drift that the per-signal
        // golden vectors can miss. All crops carry neutral reliability.
        var scores = new Dictionary<string, double>
        {
            ["exact-same"] = MatchScoring.ScoreListing(Criteria(), Crop()),                          // 0.97
            ["near-miss-price"] = MatchScoring.ScoreListing(Criteria(), Crop(price: 110)),           // 0.836667
            ["exact-far"] = MatchScoring.ScoreListing(Criteria(), Crop(loc: "Mekelle")),             // 0.844
            ["substitute-partial"] = MatchScoring.ScoreListing(Criteria(), Crop(cropType: "barley", qty: 5)), // 0.79
        };
        var ranked = scores.OrderByDescending(kv => kv.Value).Select(kv => kv.Key).ToArray();
        Assert.Equal(
            new[] { "exact-same", "exact-far", "near-miss-price", "substitute-partial" },
            ranked);
    }

    [Fact]
    public void Substitutes_LegumesAreReachable()
    {
        // Regression: the substitute map previously used non-existent crop types
        // ("lentil"/"fababean") so legume substitution never matched real data.
        Assert.Contains("lentils", MatchScoring.CandidateCropTypes("chickpea"));
        Assert.Contains("chickpea", MatchScoring.CandidateCropTypes("lentils"));
        Assert.True(MatchScoring.IsSubstitute("chickpea", "lentils"));
        Assert.True(MatchScoring.IsCandidate(Criteria(cropType: "chickpea"), Crop(cropType: "lentils")));
    }

    // ── MATCH-01 trip pairing parity guards (MUST match match_scorer_test.dart) ──────────────
    // The same literal coordinates (the mobile WoredaCentroids entries) and the same trip set,
    // in the same order, as the Dart group "trip pairing (MATCH-01)".

    private static readonly Dictionary<string, (double Lat, double Lon)> TripCoords = new()
    {
        ["Hawassa"] = (7.05, 38.47),
        ["Shashemene"] = (7.20, 38.60),
        ["Addis Ababa"] = (9.03, 38.74),
        ["Addis Abeba"] = (9.03, 38.74),
        ["Bishoftu"] = (8.75, 38.98),
        ["Adama"] = (8.54, 39.27),
        ["Jimma"] = (7.67, 36.83),
    };

    private static readonly DateTime TripNow = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime TripDay2 = new(2026, 1, 2, 21, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime TripDeadline = new(2026, 1, 5, 21, 0, 0, DateTimeKind.Utc);

    private static double? Lat(string name) => TripCoords.TryGetValue(name, out var c) ? c.Lat : null;

    private static double? Lon(string name) => TripCoords.TryGetValue(name, out var c) ? c.Lon : null;

    private static TripCandidate Pt(
        string id,
        string from,
        string to,
        double price,
        double? remaining = null,
        DateTime? departure = null,
        int? reliabilityPct = null,
        string status = "open")
        => new()
        {
            TripId = id,
            Status = status,
            FromWoreda = from,
            FromLatitude = Lat(from),
            FromLongitude = Lon(from),
            ToWoreda = to,
            ToLatitude = Lat(to),
            ToLongitude = Lon(to),
            DepartureUtc = departure ?? TripDay2,
            Capacity = 100,
            RemainingQuintals = remaining,
            PricePerQuintal = price,
            OwnerReliabilityPct = reliabilityPct,
        };

    // A crop in Hawassa, delivered to Addis Ababa, 10 quintals, by 2026-01-05T21:00Z.
    private static List<TripCandidate> GoldenTrips() =>
    [
        Pt("exact", "Hawassa", "Addis Ababa", 12),
        Pt("exact-twin", "Hawassa", "Addis Ababa", 12),
        Pt("exact-proven", "Hawassa", "Addis Ababa", 12, reliabilityPct: 95),
        Pt("today", "Hawassa", "Addis Ababa", 12, departure: new DateTime(2025, 12, 31, 21, 0, 0, DateTimeKind.Utc)),
        Pt("adjacent", "Shashemene", "Addis Ababa", 12, reliabilityPct: 100),
        Pt("adjacent-cheap", "Shashemene", "Addis Ababa", 10),
        Pt("fallback", "Hawassa Zuria", "Addis Ababa", 12),
        Pt("far", "Jimma", "Addis Ababa", 9),
        Pt("unknown", "Gimbo", "Addis Ababa", 9),
        Pt("to-bishoftu", "Hawassa", "Bishoftu", 9),
        Pt("full", "Hawassa", "Addis Ababa", 9, remaining: 0),
        Pt("short", "Hawassa", "Addis Ababa", 9, remaining: 9.98),
        Pt("slack", "Hawassa", "Addis Ababa", 11, remaining: 9.995),
        Pt("late", "Hawassa", "Addis Ababa", 9, departure: new DateTime(2026, 1, 6, 21, 0, 0, DateTimeKind.Utc)),
        Pt("deadline-day", "Hawassa", "Addis Ababa", 13, departure: TripDeadline),
        Pt("left", "Hawassa", "Addis Ababa", 9, departure: new DateTime(2025, 12, 30, 21, 0, 0, DateTimeKind.Utc)),
        Pt("matched", "Hawassa", "Addis Ababa", 9, status: "matched"),
    ];

    private static TripRouteQuery TripQuery(
        double? quantityNeeded = 10,
        int? orderLeadHours = null,
        string listing = "Hawassa",
        string delivery = "Addis Ababa")
        => new()
        {
            ListingWoreda = listing,
            ListingLatitude = Lat(listing),
            ListingLongitude = Lon(listing),
            DeliveryWoreda = delivery,
            DeliveryLatitude = Lat(delivery),
            DeliveryLongitude = Lon(delivery),
            QuantityNeeded = quantityNeeded,
            NowUtc = TripNow,
            OrderLeadHours = orderLeadHours,
            NeededByUtc = TripDeadline,
        };

    private static string[] SuggestedIds(TripRouteQuery query, IEnumerable<TripCandidate> trips)
        => MatchScoring.SuggestTrips(query, trips).Select(t => t.TripId).ToArray();

    // Rows: trip end, its point, wanted place, its point, km (null: a point unknown), score
    // (null: the end does not serve). Duplicated verbatim in match_scorer_test.dart.
    [Theory]
    [InlineData("Hawassa", 7.05, 38.47, "Hawassa", 7.05, 38.47, 0.0, 1.0)]                      // same place
    [InlineData("Shashemene", 7.20, 38.60, "Hawassa", 7.05, 38.47, 22.0, 0.694444444)]          // 1 / (1 + 22 / 50)
    [InlineData("Bishoftu", 8.75, 38.98, "Adama", 8.54, 39.27, 39.5, 0.558659218)]              // inside 40 km
    [InlineData("Bishoftu", 8.75, 38.98, "Addis Ababa", 9.03, 38.74, 40.8, null)]               // just outside 40 km
    [InlineData("Jimma", 7.67, 36.83, "Hawassa", 7.05, 38.47, 193.6, null)]                     // far
    [InlineData("Hawassa Zuria", null, null, "Hawassa", 7.05, 38.47, null, 0.6)]                // no centroid: whole word
    [InlineData("Gimbo", null, null, "Hawassa", 7.05, 38.47, null, null)]                       // no centroid, another name
    [InlineData("Addis Abeba", 9.03, 38.74, "Addis Ababa", 9.03, 38.74, 0.0, 1.0)]              // two spellings, 0 km
    [InlineData("Adama, Oromia", null, null, "Ādama", null, null, null, 1.0)]                    // no centroid: same name
    [InlineData("", 7.05, 38.47, "Hawassa", 7.05, 38.47, 0.0, null)]                            // a blank name never serves
    public void TripEndScore_GoldenVectors(
        string tripEnd, double? tLat, double? tLon,
        string wanted, double? wLat, double? wLon,
        double? km, double? score)
    {
        var actualKm = MatchScoring.TripEndKm(tLat, tLon, wLat, wLon);
        if (km is double expectedKm)
        {
            Assert.NotNull(actualKm);
            Assert.Equal(expectedKm, actualKm!.Value, 9);
        }
        else
        {
            Assert.Null(actualKm);
        }

        var actual = MatchScoring.TripEndScore(tripEnd, tLat, tLon, wanted, wLat, wLon);
        if (score is double expected)
        {
            Assert.NotNull(actual);
            Assert.Equal(expected, actual!.Value, 9);
        }
        else
        {
            Assert.Null(actual);
        }
    }

    [Fact]
    public void SuggestTrips_GoldenOrder_IsStableAcrossPlatforms()
    {
        // Duplicated verbatim on the Dart side ("golden order (same as the C# SuggestTrips
        // vector)"). Dropped: far (193.6 km), unknown (no centroid, other name), to-bishoftu
        // (40.8 km from Addis Ababa), full, short, late, left, matched.
        Assert.Equal(
            new[]
            {
                "adjacent-cheap", // price stays the first key
                "slack",          // 9.995 room for 10: inside the 0.01 tolerance
                "exact-proven",   // exact route, reliability 95
                "today",          // exact, neutral: the earlier departure first
                "exact",
                "exact-twin",     // same as exact in all but the id
                "adjacent",       // 22 km away ranks below exact at the same price
                "fallback",       // no centroid: the whole-word name match, 0.8
                "deadline-day",   // departs on the deadline
            },
            SuggestedIds(TripQuery(), GoldenTrips()));
    }

    [Fact]
    public void TripRouteScore_GoldenVectors()
    {
        var byId = GoldenTrips().ToDictionary(t => t.TripId);
        Assert.Equal(1.0, MatchScoring.TripRouteScore(TripQuery(), byId["exact"]));
        Assert.Equal(0.847222222, MatchScoring.TripRouteScore(TripQuery(), byId["adjacent"])!.Value, 9);
        Assert.Equal(0.8, MatchScoring.TripRouteScore(TripQuery(), byId["fallback"])!.Value, 9);
        Assert.Null(MatchScoring.TripRouteScore(TripQuery(), byId["far"]));
        Assert.Null(MatchScoring.TripRouteScore(TripQuery(), byId["unknown"]));
        Assert.Null(MatchScoring.TripRouteScore(TripQuery(), byId["to-bishoftu"]));

        // A trip ending 39.5 km from the delivery woreda serves it.
        Assert.Equal(
            0.779329609,
            MatchScoring.TripRouteScore(TripQuery(delivery: "Adama"), Pt("to-near", "Hawassa", "Bishoftu", 12))!.Value,
            9);
    }

    [Fact]
    public void SuggestTrips_WithTheOrderLead_DropsTheTripLeavingToday()
    {
        Assert.Equal(
            new[] { "adjacent-cheap", "slack", "exact-proven", "exact", "exact-twin", "adjacent", "fallback", "deadline-day" },
            SuggestedIds(TripQuery(orderLeadHours: 24), GoldenTrips()));
    }

    [Fact]
    public void SuggestTrips_NoPositiveNeed_WantsAQuintalOfRoom()
    {
        // The full trip is still dropped; the short one (9.98) now fits.
        var ids = SuggestedIds(TripQuery(quantityNeeded: 0), GoldenTrips());
        Assert.DoesNotContain("full", ids);
        Assert.Contains("short", ids);
        Assert.Equal(ids, SuggestedIds(TripQuery(quantityNeeded: null), GoldenTrips()));
    }

    [Fact]
    public void SuggestTrips_InputOrderDoesNotMatter()
    {
        var forward = SuggestedIds(TripQuery(), GoldenTrips());
        var backward = SuggestedIds(TripQuery(), Enumerable.Reverse(GoldenTrips()));
        Assert.Equal(forward, backward);
    }

    [Fact]
    public void SuggestTrips_GoldenOrder_ReliabilityAtEqualPrice()
    {
        // The ESC-30 vector of the Dart rankTripSuggestions test, on one exact route.
        TripCandidate T(string id, double price, int? pct = null)
            => Pt(id, "Hawassa", "Addis Ababa", price, reliabilityPct: pct);
        Assert.Equal(
            new[] { "cheap-flaky", "mid-proven", "mid-seventy", "mid-unknown", "mid-low", "pricey-proven" },
            SuggestedIds(TripQuery(), new[]
            {
                T("pricey-proven", 15, 100),
                T("mid-unknown", 12),
                T("mid-low", 12, 40),
                T("mid-proven", 12, 95),
                T("mid-seventy", 12, 70),
                T("cheap-flaky", 10, 20),
            }));
    }

    [Fact]
    public void TripPairingConstants_MatchTheDartScorer()
    {
        Assert.Equal(40.0, MatchScoring.TripNearRadiusKm);
        Assert.Equal(0.01, MatchScoring.TripRoomSlackQuintals);
        Assert.Equal(1.0, MatchScoring.TripMinUsefulLoadQuintals);
    }
}
