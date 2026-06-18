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
        DateTime? created = null)
        => new()
        {
            CropType = cropType,
            PricePerKg = price,
            Quantity = qty,
            LocationName = loc,
            Latitude = lat,
            Longitude = lon,
            CreatedAt = created ?? Now,
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
        // Mirrors the Dart "perfect exact match" case → 1.0.
        Assert.Equal(1.0, MatchScoring.ScoreListing(Criteria(), Crop()), 6);
    }

    [Fact]
    public void ScoreListing_ExactOutranksSubstitute()
    {
        var exact = MatchScoring.ScoreListing(Criteria(), Crop());
        var sub = MatchScoring.ScoreListing(Criteria(), Crop(cropType: "barley"));
        Assert.True(exact > sub);
    }
}
