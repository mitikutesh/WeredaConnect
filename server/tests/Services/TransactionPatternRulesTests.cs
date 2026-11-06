using System.Globalization;
using Amazon.DynamoDBv2.Model;
using FluentAssertions;
using WeredaConnect.Server.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Services;

/// <summary>
/// FRD-09 (fraud-detection-architecture.md 15.1): each transaction pattern rule fires on its pattern and
/// stays quiet on the near miss, and every threshold comes from configuration with a safe default.
/// </summary>
public class TransactionPatternRulesTests
{
    private static readonly DateTime Now = new(2026, 11, 6, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TransactionPatternSettings Settings = TransactionPatternSettings.Default;

    private static PaymentSample Paid(decimal amount, int daysAgo = 3, bool verified = true) =>
        new(amount, Now.AddDays(-daysAgo), verified);

    // ── configuration ─────────────────────────────────────

    private static TransactionPatternSettings SettingsFrom(params (string Name, string Value)[] values)
    {
        var map = values.ToDictionary(v => v.Name, v => v.Value);
        return TransactionPatternSettings.From(name => map.TryGetValue(name, out var v) ? v : null);
    }

    [Fact]
    public void Settings_DefaultsMatchTheSpec()
    {
        SettingsFrom().Should().Be(new TransactionPatternSettings(
            WindowDays: 90, MaxPages: 2, AmountSigma: 3m, AmountMinSamples: 5, AmountMinMultiple: 2m,
            NewAccountDays: 7, NewAccountLargeEtb: 5_000m, KycBandPercent: 90, KycBandRepeats: 3, KycBandWindowDays: 30,
            CancelRatePercent: 30, CancelRateMinOrders: 5, DisputeRatePercent: 30, DisputeRateMinOrders: 5,
            MaxDeliveryKmh: 120, DeliveryMinKm: 5m));
        Settings.KycBandLowEtb.Should().Be(9_000m);
    }

    [Fact]
    public void Settings_EnvironmentOverridesEachValue()
    {
        SettingsFrom(
                ("FRAUD_PATTERN_WINDOW_DAYS", "60"), ("FRAUD_PATTERN_MAX_PAGES", "3"), ("FRAUD_AMOUNT_SIGMA", "2.5"),
                ("FRAUD_AMOUNT_MIN_SAMPLES", "8"), ("FRAUD_AMOUNT_MIN_MULTIPLE", "1.5"), ("FRAUD_NEW_ACCOUNT_DAYS", "14"),
                ("FRAUD_NEW_ACCOUNT_LARGE_ETB", "8000"), ("FRAUD_KYC_BAND_PERCENT", "95"), ("FRAUD_KYC_BAND_REPEATS", "4"),
                ("FRAUD_KYC_BAND_WINDOW_DAYS", "14"), ("FRAUD_CANCEL_RATE_PERCENT", "40"), ("FRAUD_CANCEL_RATE_MIN_ORDERS", "10"),
                ("FRAUD_DISPUTE_RATE_PERCENT", "25"), ("FRAUD_DISPUTE_RATE_MIN_ORDERS", "6"), ("FRAUD_MAX_DELIVERY_KMH", "90"),
                ("FRAUD_DELIVERY_MIN_KM", "2.5"))
            .Should().Be(new TransactionPatternSettings(60, 3, 2.5m, 8, 1.5m, 14, 8_000m, 95, 4, 14, 40, 10, 25, 6, 90, 2.5m));
    }

    [Theory]
    [InlineData("FRAUD_PATTERN_WINDOW_DAYS", "0")]
    [InlineData("FRAUD_PATTERN_WINDOW_DAYS", "366")]
    [InlineData("FRAUD_PATTERN_MAX_PAGES", "11")] // never past RiskMaxPagesCap
    [InlineData("FRAUD_PATTERN_MAX_PAGES", "0")]
    [InlineData("FRAUD_AMOUNT_SIGMA", "0.5")]
    [InlineData("FRAUD_AMOUNT_SIGMA", "abc")]
    [InlineData("FRAUD_AMOUNT_MIN_SAMPLES", "1")] // a deviation needs two samples
    [InlineData("FRAUD_AMOUNT_MIN_MULTIPLE", "0.5")]
    [InlineData("FRAUD_NEW_ACCOUNT_DAYS", "-1")]
    [InlineData("FRAUD_NEW_ACCOUNT_LARGE_ETB", "0")]
    [InlineData("FRAUD_KYC_BAND_PERCENT", "100")] // the band never reaches past the threshold
    [InlineData("FRAUD_KYC_BAND_PERCENT", "10")]
    [InlineData("FRAUD_KYC_BAND_REPEATS", "1")] // one payment is never a repeat
    [InlineData("FRAUD_CANCEL_RATE_PERCENT", "0")] // 0 would flag every cancel
    [InlineData("FRAUD_CANCEL_RATE_MIN_ORDERS", "1")] // one event never decides a rate
    [InlineData("FRAUD_DISPUTE_RATE_PERCENT", "101")]
    [InlineData("FRAUD_DISPUTE_RATE_MIN_ORDERS", "0")]
    [InlineData("FRAUD_MAX_DELIVERY_KMH", "5")]
    [InlineData("FRAUD_DELIVERY_MIN_KM", "-1")]
    [InlineData("FRAUD_DELIVERY_MIN_KM", "")]
    public void Settings_InvalidOrOutOfRangeValueKeepsTheDefault(string name, string value)
    {
        SettingsFrom((name, value)).Should().Be(Settings);
    }

    [Fact]
    public void Settings_KycBandWindowNeverLooksPastTheHistoryRead()
    {
        SettingsFrom(("FRAUD_PATTERN_WINDOW_DAYS", "20"), ("FRAUD_KYC_BAND_WINDOW_DAYS", "45")).KycBandWindowDays.Should().Be(20);
    }

    [Fact]
    public void Settings_NewAccountDaysZeroTurnsTheRuleOff()
    {
        var off = SettingsFrom(("FRAUD_NEW_ACCOUNT_DAYS", "0"));

        TransactionPatternRules.NewAccountLargeAmount(50_000m, Now.AddHours(-1), Now, off).Should().BeNull();
    }

    // ── amount above 3 sigma ──────────────────────────────

    private static readonly PaymentSample[] SteadyBuyer =
    {
        Paid(1_000m), Paid(1_200m), Paid(900m), Paid(1_100m), Paid(1_000m)
    };

    [Fact]
    public void AmountAnomaly_FarAboveTheBuyersOwnHistory_Hits()
    {
        var hit = TransactionPatternRules.AmountAnomaly(5_000m, SteadyBuyer, Settings);

        hit.Should().NotBeNull();
        hit!.Value.SignalType.Should().Be("AMOUNT_ANOMALY");
        hit.Value.Description.Should().Contain("5,000 ETB").And.Contain("5 paid orders").And.Contain("mean 1,040 ETB");
    }

    [Theory]
    [InlineData(1_300)] // inside 3 sigma (mean 1,040, sd about 114: the limit is about 1,382 ETB)
    [InlineData(2_000)] // above 3 sigma but under twice the mean (2,080 ETB)
    [InlineData(500)] // a small payment is no risk
    public void AmountAnomaly_WithinTheHistoryOrNotTwiceTheMean_Misses(int amount)
    {
        TransactionPatternRules.AmountAnomaly(amount, SteadyBuyer, Settings).Should().BeNull();
    }

    [Fact]
    public void AmountAnomaly_TooFewPaidOrders_Misses()
    {
        TransactionPatternRules.AmountAnomaly(50_000m, SteadyBuyer.Take(4).ToList(), Settings).Should().BeNull();
    }

    [Fact]
    public void AmountAnomaly_UnverifiedPaymentsAreNotHistory()
    {
        var attempts = SteadyBuyer.Select(p => p with { Verified = false }).ToList();

        TransactionPatternRules.AmountAnomaly(50_000m, attempts, Settings).Should().BeNull();
    }

    [Fact]
    public void AmountAnomaly_IdenticalHistory_FiresOnlyFromTwiceTheMean()
    {
        // A deviation of 0 would flag 1,001 ETB; the multiple guard keeps it to a real jump.
        var flat = Enumerable.Repeat(Paid(1_000m), 6).ToList();

        TransactionPatternRules.AmountAnomaly(1_500m, flat, Settings).Should().BeNull();
        TransactionPatternRules.AmountAnomaly(2_000m, flat, Settings).Should().NotBeNull();
    }

    // ── new account with a large amount ───────────────────

    [Fact]
    public void NewAccountLargeAmount_YoungAccountPayingALot_Hits()
    {
        var hit = TransactionPatternRules.NewAccountLargeAmount(6_000m, Now.AddDays(-2), Now, Settings);

        hit!.Value.SignalType.Should().Be("NEW_ACCOUNT_LARGE_AMOUNT");
        hit.Value.Description.Should().Contain("6,000 ETB").And.Contain("2 days old");
    }

    [Theory]
    [InlineData(4_999, 2)] // not large
    [InlineData(6_000, 7)] // exactly 7 days: no longer new
    [InlineData(6_000, 30)]
    public void NewAccountLargeAmount_SmallAmountOrOldAccount_Misses(int amount, int ageDays)
    {
        TransactionPatternRules.NewAccountLargeAmount(amount, Now.AddDays(-ageDays), Now, Settings).Should().BeNull();
    }

    [Fact]
    public void NewAccountLargeAmount_UnknownAccountAge_Misses()
    {
        TransactionPatternRules.NewAccountLargeAmount(50_000m, null, Now, Settings).Should().BeNull();
    }

    // ── repeated amounts just under the 10,000 ETB Fayda threshold ──

    [Fact]
    public void KycStructuring_ThirdPaymentInTheBandWithin30Days_Hits()
    {
        // This payment (submitted now, so not verified yet) is the third with two funded ones in the band.
        var history = new[] { Paid(9_800m, 10), Paid(9_500m, 20), Paid(2_000m, 5) };

        var hit = TransactionPatternRules.KycThresholdStructuring(9_990m, history, false, Now, Settings);

        hit!.Value.SignalType.Should().Be("KYC_THRESHOLD_STRUCTURING");
        hit.Value.Description.Should().Contain("3 payments between 9,000 and 10,000 ETB in 30 days");
    }

    [Fact]
    public void KycStructuring_CheckoutsThatWereNeverVerified_DoNotCount()
    {
        // An abandoned checkout on another order is no payment: only funded ones repeat.
        var history = new[] { Paid(9_800m, 10), Paid(9_500m, 20, verified: false), Paid(9_700m, 3, verified: false) };

        TransactionPatternRules.KycThresholdStructuring(9_990m, history, false, Now, Settings).Should().BeNull();
    }

    [Fact]
    public void KycStructuring_FaydaVerifiedBuyer_HasNoGateToDodge()
    {
        var history = new[] { Paid(9_800m, 1), Paid(9_700m, 2), Paid(9_600m, 3) };

        TransactionPatternRules.KycThresholdStructuring(9_900m, history, true, Now, Settings).Should().BeNull();
        TransactionPatternRules.KycThresholdStructuring(9_900m, history, false, Now, Settings).Should().NotBeNull();
    }

    [Fact]
    public void KycStructuring_TheThresholdItselfIsInTheBand()
    {
        // Up to and including 10,000 ETB needs no Fayda ID.
        var history = new[] { Paid(10_000m, 1), Paid(9_000m, 2) };

        TransactionPatternRules.KycThresholdStructuring(10_000m, history, false, Now, Settings).Should().NotBeNull();
    }

    [Theory]
    [InlineData(8_999.99)] // below the band
    [InlineData(10_000.01)] // above the threshold: the Fayda ID is asked anyway
    public void KycStructuring_OutsideTheBand_Misses(double amount)
    {
        var history = new[] { Paid(9_800m, 1), Paid(9_700m, 2), Paid(9_600m, 3) };

        TransactionPatternRules.KycThresholdStructuring((decimal)amount, history, false, Now, Settings).Should().BeNull();
    }

    [Fact]
    public void KycStructuring_TooFewOrTooOld_Misses()
    {
        TransactionPatternRules.KycThresholdStructuring(9_900m, new[] { Paid(9_800m, 1) }, false, Now, Settings).Should().BeNull();
        TransactionPatternRules.KycThresholdStructuring(9_900m, new[] { Paid(9_800m, 1), Paid(9_700m, 31) }, false, Now, Settings)
            .Should().BeNull();
    }

    // ── rates: cancel after fund, dispute ────────────────

    [Theory]
    [InlineData(5, 2)] // 40 %
    [InlineData(10, 3)] // 30 %: the limit itself
    public void CancelAfterFundRate_AtOrAboveTheLimitOverEnoughOrders_Hits(int funded, int cancelled)
    {
        var hit = TransactionPatternRules.CancelAfterFundRate("farmer", funded, cancelled, Settings);

        hit!.Value.SignalType.Should().Be("CANCEL_AFTER_FUND_RATE");
        hit.Value.Description.Should().StartWith($"The farmer cancelled {cancelled} of {funded} funded orders in 90 days");
    }

    [Theory]
    [InlineData(10, 2)] // 20 %
    [InlineData(4, 4)] // 100 % of too few orders: a new user is not flagged
    [InlineData(1, 1)] // one order cancelled by a new user
    public void CancelAfterFundRate_BelowTheLimitOrTooFewOrders_Misses(int funded, int cancelled)
    {
        TransactionPatternRules.CancelAfterFundRate("trucker", funded, cancelled, Settings).Should().BeNull();
    }

    [Theory]
    [InlineData(5, 2, true)]
    [InlineData(10, 3, true)]
    [InlineData(10, 2, false)]
    [InlineData(4, 3, false)] // too few orders
    public void DisputeRate_HitsAtTheLimitOverEnoughOrders(int held, int disputed, bool hits)
    {
        var hit = TransactionPatternRules.DisputeRate(held, disputed, Settings);

        (hit != null).Should().Be(hits);
        if (hits)
        {
            hit!.Value.SignalType.Should().Be("DISPUTE_RATE");
            hit.Value.Description.Should().StartWith($"The buyer disputed {disputed} of {held} paid orders");
        }
    }

    // ── delivery time impossible for the distance ────────

    // Jimma to Addis Ababa: about 259 km great circle, at least 130 minutes at 120 km/h. The confirm reached
    // the server at Now; the pickup reached it serverTime before, and the trucker's phone made the pickup scan
    // captureTime before Now.
    private static DeliveryTimeFacts Trip(
        TimeSpan serverTime, TimeSpan captureTime, double dropoffLat = 9.03, double dropoffLng = 38.74) => new(
        7.67, 36.83, dropoffLat, dropoffLng,
        Now - serverTime, Now,
        Now - captureTime);

    [Fact]
    public void ImpossibleDeliveryTime_PickupArrivalAndCaptureBothTooRecent_Hits()
    {
        var hit = TransactionPatternRules.ImpossibleDeliveryTime(
            Trip(TimeSpan.FromMinutes(20), TimeSpan.FromMinutes(25)), Settings);

        hit!.Value.SignalType.Should().Be("IMPOSSIBLE_DELIVERY_TIME");
        hit.Value.Description.Should().Contain("Delivered 25 minutes after pickup").And.Contain("at 120 km/h");
    }

    [Fact]
    public void ImpossibleDeliveryTime_PickupSyncedLateButCapturedEarly_Misses()
    {
        // Offline at the farm: the pickup reached the server minutes before the confirm, but the phone
        // captured it four hours earlier. An honest offline trucker is not flagged.
        TransactionPatternRules.ImpossibleDeliveryTime(Trip(TimeSpan.FromMinutes(5), TimeSpan.FromHours(4)), Settings)
            .Should().BeNull();
    }

    [Fact]
    public void ImpossibleDeliveryTime_TruckerClockBehind_OnlyLengthensTheTrip()
    {
        // A phone on a manually set local clock, six hours behind: the pickup capture looks older, never newer.
        TransactionPatternRules.ImpossibleDeliveryTime(Trip(TimeSpan.FromMinutes(20), TimeSpan.FromHours(6) + TimeSpan.FromMinutes(20)), Settings)
            .Should().BeNull();
    }

    [Fact]
    public void ImpossibleDeliveryTime_CaptureAfterArrivalButSlowServerTime_Misses()
    {
        TransactionPatternRules.ImpossibleDeliveryTime(Trip(TimeSpan.FromHours(5), TimeSpan.FromMinutes(-30)), Settings)
            .Should().BeNull();
    }

    [Theory]
    [InlineData(0.0, 0.0, 9.03, 38.74)]
    [InlineData(7.67, 36.83, 0.0, 0.0)]
    public void ImpossibleDeliveryTime_APointAtZeroZeroIsNoFix_Skips(double pickupLat, double pickupLng, double dropoffLat, double dropoffLng)
    {
        // (0, 0) to Addis Ababa is thousands of km: it would always look impossible.
        var fast = Trip(TimeSpan.FromMinutes(20), TimeSpan.FromMinutes(20)) with
        {
            PickupLat = pickupLat,
            PickupLng = pickupLng,
            DropoffLat = dropoffLat,
            DropoffLng = dropoffLng
        };

        TransactionPatternRules.ImpossibleDeliveryTime(fast, Settings).Should().BeNull();
    }

    [Fact]
    public void ImpossibleDeliveryTime_ShortHopsAreNotJudged()
    {
        // Under 5 km (GPS noise around one market town).
        TransactionPatternRules.ImpossibleDeliveryTime(
            Trip(TimeSpan.Zero, TimeSpan.Zero, dropoffLat: 7.69, dropoffLng: 36.84), Settings).Should().BeNull();
    }

    [Fact]
    public void ImpossibleDeliveryTime_MissingCoordinatesOrTimes_Skips()
    {
        var fast = Trip(TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));

        TransactionPatternRules.ImpossibleDeliveryTime(fast with { PickupLat = null }, Settings).Should().BeNull();
        TransactionPatternRules.ImpossibleDeliveryTime(fast with { DropoffLng = null }, Settings).Should().BeNull();
        TransactionPatternRules.ImpossibleDeliveryTime(fast with { PickedUpAtUtc = null }, Settings).Should().BeNull();
        TransactionPatternRules.ImpossibleDeliveryTime(fast with { PickupCapturedAtUtc = null }, Settings).Should().BeNull();
        TransactionPatternRules.ImpossibleDeliveryTime(fast with { ConfirmedAtUtc = null }, Settings).Should().BeNull();
    }

    [Fact]
    public void ImpossibleDeliveryTime_AFasterLimitLetsTheSameTripThrough()
    {
        var trip = Trip(TimeSpan.FromMinutes(110), TimeSpan.FromMinutes(110)); // about 141 km/h

        TransactionPatternRules.ImpossibleDeliveryTime(trip, Settings).Should().NotBeNull();
        TransactionPatternRules.ImpossibleDeliveryTime(trip, SettingsFrom(("FRAUD_MAX_DELIVERY_KMH", "150"))).Should().BeNull();
    }

    [Fact]
    public void DeliveryTimeFactsFrom_ReadsOnlyTheWrittenRow()
    {
        var confirmedAt = Now.ToString("o", CultureInfo.InvariantCulture);
        var row = new Dictionary<string, AttributeValue>
        {
            ["gpsPickup"] = Point(7.67, 36.83),
            ["gpsDropoff"] = Point(9.03, 38.74),
            ["pickedUpAt"] = new() { S = Now.AddMinutes(-20).ToString("o", CultureInfo.InvariantCulture) },
            ["confirmedAt"] = new() { S = confirmedAt },
            [TransactionPatternRules.PickupCapturedAtField] = new() { S = Now.AddMinutes(-30).ToString("o", CultureInfo.InvariantCulture) }
        };

        var facts = TransactionPatternRules.DeliveryTimeFactsFrom(row);

        facts.Should().Be(new DeliveryTimeFacts(7.67, 36.83, 9.03, 38.74,
            Now.AddMinutes(-20), Now, Now.AddMinutes(-30)));
    }

    private static AttributeValue Point(double lat, double lng) => new()
    {
        M = new Dictionary<string, AttributeValue>
        {
            ["latitude"] = new() { N = lat.ToString(CultureInfo.InvariantCulture) },
            ["longitude"] = new() { N = lng.ToString(CultureInfo.InvariantCulture) }
        }
    };

    [Theory]
    [InlineData(-120, true)] // two hours offline
    [InlineData(4, true)] // a phone a little fast
    [InlineData(6, false)] // too far ahead
    [InlineData(-60 * 24 * 31, false)] // older than any offline queue
    public void PlausibleCaptureTime_BoundsTheClientClock(int minutesFromArrival, bool kept)
    {
        var captured = Now.AddMinutes(minutesFromArrival).ToString("o", CultureInfo.InvariantCulture);

        (TransactionPatternRules.PlausibleCaptureTime(captured, Now) != null).Should().Be(kept);
        TransactionPatternRules.PlausibleCaptureTime("not a time", Now).Should().BeNull();
        TransactionPatternRules.PlausibleCaptureTime(null, Now).Should().BeNull();
    }
}
