using System.Globalization;
using Amazon.DynamoDBv2.Model;
using WeredaConnect.Server.Helpers;

namespace WeredaConnect.Server.Services;

/// <summary>
/// FRD-09 thresholds (fraud-detection-architecture.md section 15.1). Each environment variable overrides
/// one default and is read on every check, like <see cref="RiskScoreSettings"/>. A value that is not a
/// number in its range keeps the default, so a typo can neither switch a rule to fire on everyone nor
/// turn one check into a long read.
/// </summary>
internal sealed record TransactionPatternSettings(
    int WindowDays,
    int MaxPages,
    decimal AmountSigma,
    int AmountMinSamples,
    decimal AmountMinMultiple,
    int NewAccountDays,
    decimal NewAccountLargeEtb,
    int KycBandPercent,
    int KycBandRepeats,
    int KycBandWindowDays,
    int CancelRatePercent,
    int CancelRateMinOrders,
    int DisputeRatePercent,
    int DisputeRateMinOrders,
    int MaxDeliveryKmh,
    decimal DeliveryMinKm)
{
    /// <summary>Rows per history page: one Query of a user's GSI partition, newest first.</summary>
    public const int PageSize = 50;

    public static TransactionPatternSettings Default { get; } = new(
        WindowDays: 90,
        MaxPages: 2,
        AmountSigma: 3m,
        AmountMinSamples: 5,
        AmountMinMultiple: 2m,
        NewAccountDays: 7,
        NewAccountLargeEtb: 5_000m,
        KycBandPercent: 90,
        KycBandRepeats: 3,
        KycBandWindowDays: 30,
        CancelRatePercent: 30,
        CancelRateMinOrders: 5,
        DisputeRatePercent: 30,
        DisputeRateMinOrders: 5,
        MaxDeliveryKmh: 120,
        DeliveryMinKm: 5m);

    public static TransactionPatternSettings FromEnvironment() => From(Environment.GetEnvironmentVariable);

    internal static TransactionPatternSettings From(Func<string, string?> env)
    {
        var window = Read(env, "FRAUD_PATTERN_WINDOW_DAYS", Default.WindowDays, 1, 365);
        return new TransactionPatternSettings(
            window,
            Read(env, "FRAUD_PATTERN_MAX_PAGES", Default.MaxPages, 1, FraudDetectionService.RiskMaxPagesCap),
            ReadDecimal(env, "FRAUD_AMOUNT_SIGMA", Default.AmountSigma, 1m, 10m),
            Read(env, "FRAUD_AMOUNT_MIN_SAMPLES", Default.AmountMinSamples, 2, 100),
            ReadDecimal(env, "FRAUD_AMOUNT_MIN_MULTIPLE", Default.AmountMinMultiple, 1m, 100m),
            Read(env, "FRAUD_NEW_ACCOUNT_DAYS", Default.NewAccountDays, 0, 90),
            ReadDecimal(env, "FRAUD_NEW_ACCOUNT_LARGE_ETB", Default.NewAccountLargeEtb, 1m, 500_000m),
            Read(env, "FRAUD_KYC_BAND_PERCENT", Default.KycBandPercent, 50, 99),
            Read(env, "FRAUD_KYC_BAND_REPEATS", Default.KycBandRepeats, 2, 50),
            // The band counts payments from the one history read, so it never looks further back than it.
            Math.Min(Read(env, "FRAUD_KYC_BAND_WINDOW_DAYS", Default.KycBandWindowDays, 1, 365), window),
            Read(env, "FRAUD_CANCEL_RATE_PERCENT", Default.CancelRatePercent, 1, 100),
            Read(env, "FRAUD_CANCEL_RATE_MIN_ORDERS", Default.CancelRateMinOrders, 2, 100),
            Read(env, "FRAUD_DISPUTE_RATE_PERCENT", Default.DisputeRatePercent, 1, 100),
            Read(env, "FRAUD_DISPUTE_RATE_MIN_ORDERS", Default.DisputeRateMinOrders, 2, 100),
            Read(env, "FRAUD_MAX_DELIVERY_KMH", Default.MaxDeliveryKmh, 10, 1000),
            ReadDecimal(env, "FRAUD_DELIVERY_MIN_KM", Default.DeliveryMinKm, 0m, 1000m));
    }

    /// <summary>Lower edge of the structuring band: a share of the Fayda threshold (<see cref="KycPolicy.FaydaThresholdEtb"/>).</summary>
    public decimal KycBandLowEtb => KycPolicy.FaydaThresholdEtb * KycBandPercent / 100m;

    private static int Read(Func<string, string?> env, string name, int fallback, int min, int max) =>
        int.TryParse(env(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
        && value >= min && value <= max
            ? value
            : fallback;

    private static decimal ReadDecimal(Func<string, string?> env, string name, decimal fallback, decimal min, decimal max) =>
        decimal.TryParse(env(name), NumberStyles.Number, CultureInfo.InvariantCulture, out var value)
        && value >= min && value <= max
            ? value
            : fallback;
}

/// <summary>FRD-09: one rule that fired, the signal row it writes. The description names no person and no phone.</summary>
internal readonly record struct PatternHit(string SignalType, string Description);

/// <summary>FRD-09: one earlier order of the buyer with a payment (submitted, or verified when <paramref name="Verified"/>).</summary>
internal readonly record struct PaymentSample(decimal AmountEtb, DateTime PaidAtUtc, bool Verified);

/// <summary>
/// FRD-09: what the delivery row says about the trip between the two QR scans (the pickup side as stored before
/// the confirm, the drop-off side as written, FUP-246). The server times
/// are when the scans reached the server; <see cref="PickupCapturedAtUtc"/> is when the trucker's phone
/// made the pickup scan (the pickup operation's <c>timestamp</c>), which is earlier when it was offline.
/// The confirming operation's own <c>timestamp</c> is not used: it comes from the buyer's phone.
/// <see cref="DropoffSource"/> says where the drop-off point comes from (FUP-265): the buyer's drop-off scan
/// as written, or the point that bounds it (<see cref="TransactionPatternRules.BoundedDropoff"/>).
/// </summary>
internal readonly record struct DeliveryTimeFacts(
    double? PickupLat,
    double? PickupLng,
    double? DropoffLat,
    double? DropoffLng,
    DateTime? PickedUpAtUtc,
    DateTime? ConfirmedAtUtc,
    DateTime? PickupCapturedAtUtc,
    DeliveryDropoffSource DropoffSource = DeliveryDropoffSource.BuyerScan);

/// <summary>
/// FUP-265: where the drop-off point of the impossible delivery time rule comes from. The buyer's scan is the
/// buyer's phone; the two others are points the trucker sent or set, which bound it.
/// </summary>
internal enum DeliveryDropoffSource
{
    /// <summary>The buyer's <c>gpsDropoff</c>, sent with the confirm.</summary>
    BuyerScan,

    /// <summary>The trucker's own drop-off check-in of ESC-36 (with <c>dropoffClassification</c> on).</summary>
    TruckerCheckIn,

    /// <summary>The trip's destination point, which the trucker set on the trip.</summary>
    TripDestination
}

/// <summary>
/// FRD-09 transaction pattern rules as pure decisions over data the caller read (no I/O, deterministic
/// on the inputs and the clock), so every hit and miss is table-tested. A rule that lacks data does not
/// fire. <see cref="TransactionPatternService"/> does the bounded reads.
/// </summary>
internal static class TransactionPatternRules
{
    /// <summary>
    /// Server-only delivery attribute: when the phone made the pickup scan (the pickup operation's
    /// <c>timestamp</c>, ISO 8601 UTC), stamped with <c>pickedUpAt</c> when plausible (delivery-verification-api.md 1.23.0).
    /// </summary>
    internal const string PickupCapturedAtField = "pickupCapturedAt";

    /// <summary>A client capture time later than its arrival by more than this is a fast clock, not a capture.</summary>
    internal static readonly TimeSpan CaptureMaxAhead = TimeSpan.FromMinutes(5);

    /// <summary>A client capture time older than this is not trusted as one (no offline queue is that old).</summary>
    internal static readonly TimeSpan CaptureMaxAge = TimeSpan.FromDays(30);

    /// <summary>
    /// Amount above 3 sigma (escrow-protocol.md, Fraud Prevention): the payment is above the mean plus
    /// <c>FRAUD_AMOUNT_SIGMA</c> sample standard deviations of the buyer's verified payments, with at least
    /// <c>FRAUD_AMOUNT_MIN_SAMPLES</c> of them, and also at least <c>FRAUD_AMOUNT_MIN_MULTIPLE</c> times the
    /// mean, so a history of near-equal amounts (a deviation near 0) does not flag a slightly larger order.
    /// Only amounts above the history count; a small payment is no risk.
    /// </summary>
    public static PatternHit? AmountAnomaly(
        decimal amountEtb, IReadOnlyList<PaymentSample> history, TransactionPatternSettings settings)
    {
        var amounts = history.Where(p => p.Verified && p.AmountEtb > 0).Select(p => p.AmountEtb).ToList();
        if (amountEtb <= 0 || amounts.Count < settings.AmountMinSamples)
        {
            return null;
        }

        var mean = amounts.Average();
        var variance = amounts.Sum(a => (a - mean) * (a - mean)) / (amounts.Count - 1);
        var stdDev = (decimal)Math.Sqrt((double)variance);
        var limit = mean + settings.AmountSigma * stdDev;
        if (amountEtb <= limit || amountEtb < settings.AmountMinMultiple * mean)
        {
            return null;
        }

        return new PatternHit(FraudDetectionService.AmountAnomalySignal,
            $"Payment of {Etb(amountEtb)} ETB is above {Etb(limit)} ETB, the mean plus {Num(settings.AmountSigma)} "
            + $"standard deviations of the buyer's {amounts.Count} paid orders (mean {Etb(mean)} ETB).");
    }

    /// <summary>
    /// New account with a large amount: a payment of at least <c>FRAUD_NEW_ACCOUNT_LARGE_ETB</c> from an
    /// account created less than <c>FRAUD_NEW_ACCOUNT_DAYS</c> ago (0 turns the rule off). An unknown
    /// creation time does not fire.
    /// </summary>
    public static PatternHit? NewAccountLargeAmount(
        decimal amountEtb, DateTime? accountCreatedAtUtc, DateTime nowUtc, TransactionPatternSettings settings)
    {
        if (settings.NewAccountDays == 0 || accountCreatedAtUtc is not { } createdAt
            || amountEtb < settings.NewAccountLargeEtb
            || nowUtc - createdAt >= TimeSpan.FromDays(settings.NewAccountDays))
        {
            return null;
        }

        var ageDays = Math.Max(0, (int)Math.Floor((nowUtc - createdAt).TotalDays));
        return new PatternHit(FraudDetectionService.NewAccountLargeAmountSignal,
            $"Payment of {Etb(amountEtb)} ETB from an account {ageDays} days old (large is {Etb(settings.NewAccountLargeEtb)} ETB "
            + $"or more from an account under {settings.NewAccountDays} days).");
    }

    /// <summary>Whether an amount sits in the band just under the Fayda threshold (up to and including it, which needs no Fayda).</summary>
    public static bool InKycBand(decimal amountEtb, TransactionPatternSettings settings) =>
        amountEtb >= settings.KycBandLowEtb && amountEtb <= KycPolicy.FaydaThresholdEtb;

    /// <summary>
    /// Repeated amounts just under the KYC threshold: this payment is in the band and, with the buyer's
    /// other verified (funded) payments in the band over the last <c>FRAUD_KYC_BAND_WINDOW_DAYS</c>, makes
    /// at least <c>FRAUD_KYC_BAND_REPEATS</c>. A checkout that was submitted and never verified on another
    /// order does not count. This payment counts as the one that triggers: the rule runs when it is
    /// submitted, before any gateway can verify it. A buyer who is already Fayda-verified has no gate to
    /// dodge, so the rule does not run for them.
    /// </summary>
    public static PatternHit? KycThresholdStructuring(
        decimal amountEtb, IReadOnlyList<PaymentSample> history, bool buyerFaydaVerified, DateTime nowUtc,
        TransactionPatternSettings settings)
    {
        if (buyerFaydaVerified || !InKycBand(amountEtb, settings))
        {
            return null;
        }

        var since = nowUtc.AddDays(-settings.KycBandWindowDays);
        var count = 1 + history.Count(p => p.Verified && p.PaidAtUtc >= since && InKycBand(p.AmountEtb, settings));
        if (count < settings.KycBandRepeats)
        {
            return null;
        }

        return new PatternHit(FraudDetectionService.KycThresholdStructuringSignal,
            $"Payment of {Etb(amountEtb)} ETB makes {count} payments between {Etb(settings.KycBandLowEtb)} and "
            + $"{Etb(KycPolicy.FaydaThresholdEtb)} ETB in {settings.KycBandWindowDays} days; above "
            + $"{Etb(KycPolicy.FaydaThresholdEtb)} ETB a Fayda ID is required.");
    }

    /// <summary>
    /// Cancel-after-fund rate: of the party's funded orders in the window (this cancel included), the share
    /// the party cancelled reaches <c>FRAUD_CANCEL_RATE_PERCENT</c>, over at least
    /// <c>FRAUD_CANCEL_RATE_MIN_ORDERS</c> funded orders. Rates count orders: a new user's cancel of one order
    /// never fires, but a trip cancel counts every paid order it took the transport from, so one cancel of
    /// a trip carrying at least the minimum of paid orders can fire on its own (each of those buyers lost
    /// the transport they paid for).
    /// </summary>
    public static PatternHit? CancelAfterFundRate(
        string party, int fundedOrders, int cancelledAfterFund, TransactionPatternSettings settings)
    {
        if (!RateReached(fundedOrders, cancelledAfterFund, settings.CancelRateMinOrders, settings.CancelRatePercent))
        {
            return null;
        }

        return new PatternHit(FraudDetectionService.CancelAfterFundRateSignal,
            $"The {party} cancelled {cancelledAfterFund} of {fundedOrders} funded orders in {settings.WindowDays} days "
            + $"({Percent(cancelledAfterFund, fundedOrders)}%; the limit is {settings.CancelRatePercent}% over at least "
            + $"{settings.CancelRateMinOrders} orders).");
    }

    /// <summary>
    /// Per-buyer dispute rate: of the buyer's escrows that held money in the window (this one included),
    /// the share the buyer disputed reaches <c>FRAUD_DISPUTE_RATE_PERCENT</c>, over at least
    /// <c>FRAUD_DISPUTE_RATE_MIN_ORDERS</c> escrows.
    /// </summary>
    public static PatternHit? DisputeRate(int heldEscrows, int disputedByBuyer, TransactionPatternSettings settings)
    {
        if (!RateReached(heldEscrows, disputedByBuyer, settings.DisputeRateMinOrders, settings.DisputeRatePercent))
        {
            return null;
        }

        return new PatternHit(FraudDetectionService.DisputeRateSignal,
            $"The buyer disputed {disputedByBuyer} of {heldEscrows} paid orders in {settings.WindowDays} days "
            + $"({Percent(disputedByBuyer, heldEscrows)}%; the limit is {settings.DisputeRatePercent}% over at least "
            + $"{settings.DisputeRateMinOrders} orders).");
    }

    /// <summary>
    /// Delivery time impossible for the distance: the great circle distance between the pickup and the
    /// drop-off scans, at least <c>FRAUD_DELIVERY_MIN_KM</c>, needs more time at <c>FRAUD_MAX_DELIVERY_KMH</c>
    /// than the trip took. The trip ends at the server's <c>confirmedAt</c> and starts at the earlier of the
    /// pickup's arrival (<c>pickedUpAt</c>) and the trucker's capture time (<c>pickupCapturedAt</c>): a phone
    /// offline at the farm syncs the pickup late, but its capture time keeps the real start, and a trucker's
    /// clock set behind only makes the trip look longer (one set ahead is not stored, beyond 5 minutes). No
    /// time from the buyer's phone is used, so a buyer's clock or client cannot shorten the trip. Missing
    /// coordinates or times skip, and so does a point at (0, 0), which is a phone with no fix, not a place.
    /// The rule judges these facts as given; the hook writes a row only from facts whose drop-off side is
    /// bounded by a point the trucker controls (<see cref="BoundedDropoff"/>, FUP-265).
    /// </summary>
    public static PatternHit? ImpossibleDeliveryTime(DeliveryTimeFacts facts, TransactionPatternSettings settings)
    {
        if (facts.PickupLat is not { } pickupLat || facts.PickupLng is not { } pickupLng
            || facts.DropoffLat is not { } dropoffLat || facts.DropoffLng is not { } dropoffLng
            || IsNoFix(pickupLat, pickupLng) || IsNoFix(dropoffLat, dropoffLng)
            || facts.PickedUpAtUtc is not { } pickedUpAt || facts.ConfirmedAtUtc is not { } confirmedAt
            || facts.PickupCapturedAtUtc is not { } pickupCaptured)
        {
            return null;
        }

        var km = FraudDetectionService.HaversineDistanceKm(pickupLat, pickupLng, dropoffLat, dropoffLng);
        if (km < (double)settings.DeliveryMinKm)
        {
            return null;
        }

        var tripTime = Max(confirmedAt - pickedUpAt, confirmedAt - pickupCaptured);
        var fastest = TimeSpan.FromHours(km / settings.MaxDeliveryKmh);
        if (tripTime >= fastest)
        {
            return null;
        }

        var minutes = Math.Max(0, (int)Math.Floor(tripTime.TotalMinutes));
        return new PatternHit(FraudDetectionService.ImpossibleDeliveryTimeSignal,
            $"Delivered {minutes} minutes after pickup with the pickup scan and {DropoffName(facts.DropoffSource)} "
            + $"{km.ToString("0.0", CultureInfo.InvariantCulture)} km apart; at {settings.MaxDeliveryKmh} km/h that "
            + $"takes at least {(int)Math.Ceiling(fastest.TotalMinutes)} minutes.");
    }

    /// <summary>
    /// FUP-265: the facts with the buyer's drop-off point bounded by <paramref name="bound"/>, a point the
    /// trucker controls (<paramref name="boundSource"/>: the trucker's drop-off check-in or the trip's
    /// destination). The drop-off side becomes whichever of the two is nearer to the stored pickup, so the
    /// buyer's point can only shorten the distance, never make it longer than the trucker's own point says.
    /// Null when there is nothing to bound by (no point, or one at (0, 0)) or nothing to bound (no pickup or no
    /// buyer's point, or one at (0, 0)): the rule is not judged, so no row ever rests on the buyer's point alone.
    /// </summary>
    public static DeliveryTimeFacts? BoundedDropoff(
        DeliveryTimeFacts facts, TrackPoint? bound, DeliveryDropoffSource boundSource)
    {
        if (bound == null || IsNoFix(bound.Latitude, bound.Longitude)
            || facts.PickupLat is not { } pickupLat || facts.PickupLng is not { } pickupLng
            || facts.DropoffLat is not { } dropoffLat || facts.DropoffLng is not { } dropoffLng
            || IsNoFix(pickupLat, pickupLng) || IsNoFix(dropoffLat, dropoffLng))
        {
            return null;
        }

        var toBuyerScan = FraudDetectionService.HaversineDistanceKm(pickupLat, pickupLng, dropoffLat, dropoffLng);
        var toBound = FraudDetectionService.HaversineDistanceKm(pickupLat, pickupLng, bound.Latitude, bound.Longitude);
        return toBound < toBuyerScan
            ? facts with { DropoffLat = bound.Latitude, DropoffLng = bound.Longitude, DropoffSource = boundSource }
            : facts;
    }

    private static string DropoffName(DeliveryDropoffSource source) => source switch
    {
        DeliveryDropoffSource.TruckerCheckIn => "the trucker's drop-off check-in",
        DeliveryDropoffSource.TripDestination => "the trip's destination",
        _ => "the drop-off scan"
    };

    /// <summary>
    /// ESC-33, buyer griefing (three-party spec 4.11, reputation-architecture.md section 13): the buyer's
    /// paid orders that a farmer or a trucker declined or let expire in the last
    /// <paramref name="windowDays"/> days, the one just recorded included, reach
    /// <paramref name="minOrders"/>. Both numbers come from the reputation policy
    /// (<c>buyerDeclineSignalCount</c>, <c>buyerDeclineSignalDays</c>). The declines must come from at least
    /// <see cref="BuyerDeclineMinParties"/> different farmers or truckers: one party who keeps declining one
    /// buyer says something about that party (or about the two of them), not about the buyer.
    /// </summary>
    public static PatternHit? BuyerDeclinePattern(int declinedPaidOrders, int distinctParties, int minOrders, int windowDays)
    {
        if (minOrders < 1 || declinedPaidOrders < minOrders || distinctParties < BuyerDeclineMinParties)
        {
            return null;
        }

        return new PatternHit(FraudDetectionService.BuyerDeclinePatternSignal,
            $"{distinctParties} farmers or truckers declined or did not answer {declinedPaidOrders} of the buyer's "
            + $"paid orders in {windowDays} days (the limit is {minOrders}).");
    }

    /// <summary>ESC-33: different farmers or truckers whose declines the buyer decline pattern needs.</summary>
    internal const int BuyerDeclineMinParties = 2;

    /// <summary>
    /// The facts of a confirmed delivery. The pickup side (<c>gpsPickup</c>, <c>pickedUpAt</c> and
    /// <see cref="PickupCapturedAtField"/>) comes from <paramref name="stored"/>, the row as it was before the
    /// confirm wrote it, so nothing the confirm sends can move the pickup (FUP-246); the drop-off side
    /// (<c>gpsDropoff</c>, <c>confirmedAt</c>) from <paramref name="written"/>, the row the confirm wrote.
    /// </summary>
    public static DeliveryTimeFacts DeliveryTimeFactsFrom(
        Dictionary<string, AttributeValue> stored, Dictionary<string, AttributeValue> written)
    {
        var (pickupLat, pickupLng) = Point(stored, "gpsPickup");
        var (dropoffLat, dropoffLng) = Point(written, "gpsDropoff");
        return new DeliveryTimeFacts(
            pickupLat, pickupLng, dropoffLat, dropoffLng,
            ParseUtc(Text(stored, "pickedUpAt")),
            ParseUtc(Text(written, "confirmedAt")),
            ParseUtc(Text(stored, PickupCapturedAtField)));
    }

    /// <summary>
    /// A client capture time (the sync operation's <c>timestamp</c>) when it is plausible for an operation
    /// that reached the server at <paramref name="arrivedAtUtc"/>: not after it by more than
    /// <see cref="CaptureMaxAhead"/>, not older than <see cref="CaptureMaxAge"/>. Otherwise null.
    /// </summary>
    public static DateTime? PlausibleCaptureTime(string? clientIso, DateTime arrivedAtUtc)
    {
        if (ParseUtc(clientIso) is not { } captured
            || captured > arrivedAtUtc + CaptureMaxAhead
            || captured < arrivedAtUtc - CaptureMaxAge)
        {
            return null;
        }

        return captured;
    }

    /// <summary>An ISO 8601 time as UTC (no zone is read as UTC), or null.</summary>
    public static DateTime? ParseUtc(string? iso) =>
        !string.IsNullOrWhiteSpace(iso)
        && DateTime.TryParse(iso, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var at)
            ? at
            : null;

    private static string? Text(Dictionary<string, AttributeValue> row, string name) =>
        row.TryGetValue(name, out var value) ? value.S : null;

    private static (double? Lat, double? Lng) Point(Dictionary<string, AttributeValue> row, string name)
    {
        if (!row.TryGetValue(name, out var value) || value.M is not { Count: > 0 } map)
        {
            return (null, null);
        }

        return (Coordinate(map, "latitude"), Coordinate(map, "longitude"));
    }

    private static double? Coordinate(Dictionary<string, AttributeValue> map, string name) =>
        map.TryGetValue(name, out var value)
        && double.TryParse(value.N, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
        && double.IsFinite(number)
            ? number
            : null;

    /// <summary>(0, 0) is what a phone without a GPS fix reports, in the Gulf of Guinea: not a place.</summary>
    private static bool IsNoFix(double lat, double lng) => lat == 0 && lng == 0;

    private static bool RateReached(int total, int hits, int minTotal, int percent) =>
        total >= minTotal && (long)hits * 100 >= (long)percent * total;

    private static int Percent(int part, int total) => total == 0 ? 0 : (int)Math.Round(100.0 * part / total);

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;

    private static string Etb(decimal amount) =>
        Math.Round(amount, 2, MidpointRounding.AwayFromZero).ToString("#,0.##", CultureInfo.InvariantCulture);

    private static string Num(decimal value) => value.ToString("0.##", CultureInfo.InvariantCulture);
}
