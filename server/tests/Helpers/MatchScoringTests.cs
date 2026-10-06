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
        double? reliability = null)
        => new()
        {
            CropType = cropType,
            PricePerKg = price,
            Quantity = qty,
            LocationName = loc,
            Latitude = lat,
            Longitude = lon,
            CreatedAt = created ?? Now,
            FarmerReliability = reliability,
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
        Assert.Equal(1.0, MatchScoring.ScoreListing(Criteria(), Crop(reliability: 1.0)), 6);
    }

    [Fact]
    public void ReliabilityScore_DefaultsToNeutral_WhenUnknown()
    {
        Assert.Equal(MatchScoring.ReliabilityNeutral, MatchScoring.ReliabilityScore(Crop()));
        Assert.Equal(0.4, MatchScoring.ReliabilityScore(Crop(reliability: 0.4)));
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
}
