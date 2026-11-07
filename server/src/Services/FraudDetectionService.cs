namespace WeredaConnect.Server.Services;

using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.RateLimiting;

public sealed class FraudDetectionService : IFraudDetectionService
{
    private readonly IAmazonDynamoDB _dynamoDb;
    private readonly string _cropsTable;
    private readonly string _fraudSignalsTable;
    private readonly IRateLimitRepository _rateLimitRepo;

    // Thresholds (internal for testing)
    internal const int MaxListingsPerHour = 5;
    internal const int ListingVelocityTtlSeconds = 3600;      // 1 hour
    // Contract (escrow-protocol.md): max 5 escrow initiations per buyer per hour.
    internal const int MaxPaymentsPerHour = 5;
    internal const int PaymentVelocityTtlSeconds = 3600;       // 1 hour
    internal const double DuplicateListingWindowHours = 1.0;
    internal const double PriceTolerancePercent = 0.05;        // ±5%
    internal const int PriceAnomalyMinSamples = 5;
    internal const int PriceAnomalySampleSize = 50;

    // Rule 2 (FUP-135): robust bounds, so a few test or absurd listings in the sample cannot widen them.
    // A price is unusual when it lies more than PriceAnomalySpreadMultiplier spreads from the sample median.

    /// <summary>Spreads from the median that flag a price (the old rule's 3 standard deviations).</summary>
    internal const decimal PriceAnomalySpreadMultiplier = 3.0m;

    /// <summary>1.4826 times the median absolute deviation estimates the standard deviation of normal prices.</summary>
    internal const decimal MadToStdDev = 1.4826m;

    /// <summary>
    /// The spread is at least this share of the median, so a sample where most farmers ask the same price (a
    /// median absolute deviation of 0) flags only prices more than 30% away from it, not every other price.
    /// </summary>
    internal const decimal PriceAnomalyMinSpreadOfMedian = 0.10m;

    internal const double GpsSpoofingThresholdKm = 1.0;
    internal const int SignalTtlDays = 30;

    // Risk score defaults (fraud-detection-architecture.md section 14.5). The environment
    // overrides are read per call by RiskScoreSettings, like StepUpPolicy's.
    internal const int DefaultRiskWindowDays = 30;
    internal const int DefaultRiskTierMedium = 30;
    internal const int DefaultRiskTierHigh = 60;
    internal const int DefaultRiskTierCritical = 100;
    internal const int DefaultRiskCreditCap = 30;
    internal const int DefaultRiskAdvisoryCap = 10;
    internal const int DefaultRiskMaxPages = 2;
    internal const int RiskMaxPagesCap = 10;
    internal const int DefaultRiskRejectTypeCap = 50;
    internal const int DefaultRiskPatternTypeCap = 50;

    /// <summary>AI reviewer output (FRD-19): capped, and left out of the enforcement score.</summary>
    internal const string AdvisorySignalType = "AI_ADVISORY";

    // Row kinds of the fraud-signals table (section 20). Rows without recordType are legacy signal rows.
    internal const string RecordTypeSignal = "signal";
    internal const string RecordTypeCredit = "credit";
    internal const string RecordTypeWhitelist = "whitelist";

    /// <summary>The key of a user's one whitelist row; sorts after every ISO key, so a newest-first read meets it first.</summary>
    internal const string WhitelistSignalKey = "~whitelist";

    // FRD-05: how long an admin whitelist lasts (section 14.5, FRAUD_WHITELIST_DAYS).
    internal const int DefaultWhitelistDays = 30;
    internal const int WhitelistDaysCap = 365;

    /// <summary>Log alarm token: the score read failed and the user was scored LOW (section 14.4).</summary>
    public const string RiskScoreUnavailableToken = "RISK_SCORE_UNAVAILABLE";

    /// <summary>Log alarm token: rows were left after the page cap and the user was scored CRITICAL.</summary>
    public const string RiskScoreTruncatedToken = "RISK_SCORE_TRUNCATED";

    /// <summary>Log alarm token: rows with an unknown <c>recordType</c> were read and counted as signals.</summary>
    public const string RiskScoreUnknownRecordToken = "RISK_SCORE_UNKNOWN_RECORD";

    /// <summary>
    /// FRD-16 log token: one signal row was written (section 23.1). Its line is JSON so a metric filter can
    /// take the type as a dimension; the token is also a plain term for the total-volume filter.
    /// </summary>
    public const string FraudSignalRecordedToken = "FRAUD_SIGNAL_RECORDED";

    public FraudDetectionService(
        IAmazonDynamoDB dynamoDb,
        string cropsTable,
        string fraudSignalsTable,
        IRateLimitRepository rateLimitRepo)
    {
        _dynamoDb = dynamoDb ?? throw new ArgumentNullException(nameof(dynamoDb));
        _cropsTable = cropsTable ?? throw new ArgumentNullException(nameof(cropsTable));
        _fraudSignalsTable = fraudSignalsTable ?? throw new ArgumentNullException(nameof(fraudSignalsTable));
        _rateLimitRepo = rateLimitRepo ?? throw new ArgumentNullException(nameof(rateLimitRepo));
    }

    /// <summary>
    /// Sink for the risk score alarm lines and the FRD-16 signal line. Defaults to stdout, which Lambda ships to the
    /// function's CloudWatch log group. Tests replace it to capture the lines.
    /// </summary>
    public Action<string> AlarmLog { get; init; } = Console.WriteLine;

    // ── CheckListingCreationAsync ──────────────────────────
    // Runs checks in order: velocity → duplicate → price anomaly
    // Returns on first rejection; flags accumulate.
    public async Task<FraudCheckResult> CheckListingCreationAsync(
        string farmerId, string cropType, decimal pricePerKg)
    {
        // 1. Velocity check (cheapest — single DynamoDB GetItem)
        var hourBucket = DateTime.UtcNow.ToString("yyyy-MM-ddTHH");
        var velocityKey = $"FRAUD_LISTING#{farmerId}#{hourBucket}";
        var velocityEntry = await _rateLimitRepo.GetAsync(velocityKey);

        if (velocityEntry != null && velocityEntry.Count >= MaxListingsPerHour)
        {
            return FraudCheckResult.Rejected(
                "FRAUD_VELOCITY_EXCEEDED",
                $"Maximum {MaxListingsPerHour} listings per hour exceeded.");
        }

        // 2. Duplicate detection (GSI query — moderate cost)
        var since = DateTime.UtcNow.AddHours(-DuplicateListingWindowHours).ToString("o");
        var duplicateItems = await QueryFarmerRecentListingsAsync(farmerId, since);

        foreach (var item in duplicateItems)
        {
            if (!item.TryGetValue("cropType", out var ct) || ct.S != cropType)
            {
                continue;
            }

            if (!item.TryGetValue("pricePerKg", out var ppk) || !decimal.TryParse(ppk.N, out var existingPrice))
            {
                continue;
            }

            if (existingPrice == 0m)
            {
                continue;
            }

            var priceDiff = Math.Abs(existingPrice - pricePerKg) / existingPrice;
            if (priceDiff <= (decimal)PriceTolerancePercent)
            {
                return FraudCheckResult.Rejected(
                    "FRAUD_DUPLICATE_LISTING",
                    "A similar listing already exists. Please update your existing listing instead.");
            }
        }

        // 3. Price anomaly (GSI query + computation — most expensive)
        var recentPrices = await QueryRecentPricesByCropTypeAsync(cropType, PriceAnomalySampleSize);

        if (PriceBand.Of(recentPrices) is { } band && band.IsUnusual(pricePerKg))
        {
            return FraudCheckResult.Flagged(
                "PRICE_ANOMALY",
                string.Create(CultureInfo.InvariantCulture,
                    $"Price {pricePerKg} ETB/kg is unusual for {cropType} (median: {band.Median:F2}, MAD: {band.Mad:F2})."));
        }

        return FraudCheckResult.Allowed();
    }

    /// <summary>
    /// Rule 2 (FUP-135): the robust band of a crop type's recent prices. The centre is the median and the spread
    /// <see cref="MadToStdDev"/> times the median absolute deviation (MAD), at least
    /// <see cref="PriceAnomalyMinSpreadOfMedian"/> of the median. Unlike the mean and the standard deviation it
    /// replaced, neither moves much when up to half the sample is extreme: the dev teff sample (listings up to
    /// 1,000 ETB/kg, mean 216.6, deviation 322.0) gave a band so wide that 999 ETB/kg passed.
    /// </summary>
    internal sealed record PriceBand(decimal Median, decimal Mad, decimal Spread)
    {
        /// <summary>The band of a sample, or null when it has fewer than <see cref="PriceAnomalyMinSamples"/> prices or no spread.</summary>
        public static PriceBand? Of(IReadOnlyList<decimal> prices)
        {
            if (prices.Count < PriceAnomalyMinSamples)
            {
                return null;
            }

            var median = MedianOf(prices);
            var mad = MedianOf(prices.Select(p => Math.Abs(p - median)).ToList());
            var spread = Math.Max(MadToStdDev * mad, PriceAnomalyMinSpreadOfMedian * Math.Abs(median));
            return spread > 0 ? new PriceBand(median, mad, spread) : null;
        }

        public bool IsUnusual(decimal price) => Math.Abs(price - Median) > PriceAnomalySpreadMultiplier * Spread;

        private static decimal MedianOf(IReadOnlyList<decimal> values)
        {
            var sorted = values.OrderBy(v => v).ToList();
            var middle = sorted.Count / 2;
            return sorted.Count % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
        }
    }

    // ── CheckPaymentVelocityAsync ──────────────────────────
    public async Task<FraudCheckResult> CheckPaymentVelocityAsync(string userId)
    {
        var hourBucket = DateTime.UtcNow.ToString("yyyy-MM-ddTHH");
        var velocityKey = $"FRAUD_PAYMENT#{userId}#{hourBucket}";
        var entry = await _rateLimitRepo.GetAsync(velocityKey);

        if (entry != null && entry.Count >= MaxPaymentsPerHour)
        {
            return FraudCheckResult.Rejected(
                "FRAUD_VELOCITY_EXCEEDED",
                $"Maximum {MaxPaymentsPerHour} payment initiations per hour exceeded.");
        }

        return FraudCheckResult.Allowed();
    }

    // ── CheckGpsConsistency ────────────────────────────────
    // Pure computation — no async DB calls.
    public FraudCheckResult CheckGpsConsistency(
        double? pickupLat, double? pickupLng,
        double? dropoffLat, double? dropoffLng,
        string fromWoreda, string toWoreda)
    {
        // Skip if GPS coordinates are missing (optional in rural areas)
        if (pickupLat == null || pickupLng == null ||
            dropoffLat == null || dropoffLng == null)
        {
            return FraudCheckResult.Allowed();
        }

        // Check 1: If trip spans different woredas but GPS points are < 1km apart
        if (!string.Equals(fromWoreda, toWoreda, StringComparison.OrdinalIgnoreCase))
        {
            var distanceKm = HaversineDistanceKm(
                pickupLat.Value, pickupLng.Value,
                dropoffLat.Value, dropoffLng.Value);

            if (distanceKm < GpsSpoofingThresholdKm)
            {
                return FraudCheckResult.Flagged(
                    "GPS_ANOMALY",
                    $"Pickup and dropoff are {distanceKm:F2}km apart for inter-woreda trip ({fromWoreda} → {toWoreda}).");
            }
        }

        // Check 2: Identical coordinates (exact GPS spoofing)
        if (Math.Abs(pickupLat.Value - dropoffLat.Value) < 0.0001 &&
            Math.Abs(pickupLng.Value - dropoffLng.Value) < 0.0001)
        {
            return FraudCheckResult.Flagged(
                "GPS_ANOMALY",
                "Pickup and dropoff GPS coordinates are identical — possible spoofing.");
        }

        return FraudCheckResult.Allowed();
    }

    // ── RecordFraudSignalAsync ─────────────────────────────
    // Writes the item the admin Fraud Review reader expects
    // (signalId/severity/description/status) on top of the spec
    // schema (userId/signalKey/weight/expiresAt).
    public async Task RecordFraudSignalAsync(
        string userId, string signalType, string? entityId,
        string entityType, string? description)
    {
        if (string.IsNullOrEmpty(_fraudSignalsTable) || string.IsNullOrEmpty(userId))
        {
            return;
        }

        var now = DateTime.UtcNow;
        var createdAt = now.ToString("o");
        // FRD-08 write limit (section 14.3): at most one row per user, type and UTC hour. The key
        // carries the start of the hour and the put is conditional, so a burst writes its first row
        // and nothing more. The score counts one row per type and hour anyway (section 14.2); without
        // the limit a burst could fill the partition up to the page cap, which reads as CRITICAL.
        var hourStart = new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0, DateTimeKind.Utc);
        var signalKey = $"{hourStart.ToString("o", CultureInfo.InvariantCulture)}#{signalType}";
        var weight = SignalWeight(signalType);
        var expiresAt = new DateTimeOffset(now).AddDays(SignalTtlDays).ToUnixTimeSeconds();

        var item = new Dictionary<string, AttributeValue>
        {
            { "userId", new AttributeValue { S = userId } },
            { "signalKey", new AttributeValue { S = signalKey } },
            { "signalId", new AttributeValue { S = BuildSignalId(userId, signalKey) } },
            { "signalType", new AttributeValue { S = signalType } },
            { "severity", new AttributeValue { S = SignalSeverity(weight) } },
            { "weight", new AttributeValue { N = weight.ToString() } },
            { "description", new AttributeValue { S = description ?? "" } },
            { "status", new AttributeValue { S = "pending" } },
            { "entityType", new AttributeValue { S = entityType } },
            { "createdAt", new AttributeValue { S = createdAt } },
            { "expiresAt", new AttributeValue { N = expiresAt.ToString() } }
        };

        if (!string.IsNullOrEmpty(entityId))
        {
            item["entityId"] = new AttributeValue { S = entityId };
        }

        try
        {
            await _dynamoDb.PutItemAsync(new PutItemRequest
            {
                TableName = _fraudSignalsTable,
                Item = item,
                ConditionExpression = "attribute_not_exists(signalKey)"
            });
            // FRD-16: one line per row written, so the signal metrics count the rows Fraud Review shows.
            AlarmLog(SignalRecordedLine(signalType, entityType));
        }
        catch (ConditionalCheckFailedException)
        {
            // This user already has a row of this type in this hour: the write limit, not a failure.
        }
    }

    // ── HasSignalAsync ─────────────────────────────────────
    // FRD-15: the scan sees an order again in every hourly run of its lookback, and the writer's key only
    // stops a second row within one hour. One Query of the user's partition over the table's 30 day life,
    // consistent, the type and the entity as a filter, at most DefaultRiskMaxPages pages.
    public async Task<bool> HasSignalAsync(string userId, string signalType, string entityId)
    {
        if (string.IsNullOrEmpty(_fraudSignalsTable) || string.IsNullOrEmpty(userId))
        {
            return false;
        }

        var since = DateTime.UtcNow.AddDays(-SignalTtlDays).ToString("o", CultureInfo.InvariantCulture);
        var request = new QueryRequest
        {
            TableName = _fraudSignalsTable,
            KeyConditionExpression = "#u = :uid AND #k >= :since",
            // Any status and resolution: an approved (false positive) row also means "already seen".
            FilterExpression = "#t = :type AND #e = :entity",
            ProjectionExpression = "#k",
            ExpressionAttributeNames = new Dictionary<string, string>
            {
                { "#u", "userId" },
                { "#k", "signalKey" },
                { "#t", "signalType" },
                { "#e", "entityId" }
            },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                { ":uid", new AttributeValue { S = userId } },
                { ":since", new AttributeValue { S = since } },
                { ":type", new AttributeValue { S = signalType } },
                { ":entity", new AttributeValue { S = entityId } }
            },
            ConsistentRead = true
        };

        var pages = 0;
        Dictionary<string, AttributeValue>? startKey = null;
        do
        {
            if (startKey != null)
            {
                request.ExclusiveStartKey = startKey;
            }

            var response = await _dynamoDb.QueryAsync(request);
            pages++;
            if (response.Items is { Count: > 0 })
            {
                return true;
            }

            startKey = response.LastEvaluatedKey is { Count: > 0 } next ? next : null;
        }
        while (startKey != null && pages < DefaultRiskMaxPages);

        // Rows left unread: absence cannot be shown, so nothing more is written. Such a partition already
        // reads as truncated, CRITICAL (section 14.3).
        return startKey != null;
    }

    /// <summary>
    /// FRD-16 (section 23.1): the line of a signal row written,
    /// <c>{"event":"FRAUD_SIGNAL_RECORDED","signalType":"&lt;type&gt;","entityType":"&lt;kind&gt;"}</c>. Server
    /// values only, no user or entity id. Both fields keep <c>[A-Za-z0-9-_.:]</c> and are cut at 64
    /// characters (empty is <c>-</c>), so the line is always valid JSON and the type, a metric dimension,
    /// cannot carry free text.
    /// </summary>
    internal static string SignalRecordedLine(string? signalType, string? entityType) =>
        $"{{\"event\":\"{FraudSignalRecordedToken}\",\"signalType\":\"{MoneyAlarmLog.Safe(signalType)}\",\"entityType\":\"{MoneyAlarmLog.Safe(entityType)}\"}}";

    // FRD-08 signal types (fraud-detection-architecture.md section 15). Each is written on the
    // caller of the refused operation; the webhook's payer mismatch on the buyer our own row names.

    /// <summary>A create that names an id another user's row already holds.</summary>
    internal const string IdorCreateConflictSignal = "IDOR_CREATE_CONFLICT";

    /// <summary>A role gate, a caller id binding or a party check refused the operation.</summary>
    internal const string RoleForbiddenSignal = "ROLE_FORBIDDEN";

    /// <summary>An update tried to change an identity or money field (SIM-02, SIM-03, SIM-06, SIM-09).</summary>
    internal const string ImmutableFieldChangeSignal = "IMMUTABLE_FIELD_CHANGE";

    /// <summary>A delivery handoff with a wrong, reused or missing QR nonce.</summary>
    internal const string QrHandoffFailureSignal = "QR_HANDOFF_FAILURE";

    /// <summary>
    /// A verified Telebirr payment from a wallet other than the buyer's registered phone, applied
    /// because the payer match is not enforced. Paying from a relative's wallet is common: never scores.
    /// </summary>
    internal const string TelebirrPayerMismatchSignal = "TELEBIRR_PAYER_MISMATCH";

    /// <summary>The same payment refused because TELEBIRR_REQUIRE_PAYER_MATCH is on.</summary>
    internal const string TelebirrPayerRefusedSignal = "TELEBIRR_PAYER_REFUSED";

    /// <summary>Many version conflicts in one /sync batch. Honest offline clients conflict too: never scores.</summary>
    internal const string StaleVersionBurstSignal = "STALE_VERSION_BURST";

    /// <summary>A suspended profile's refresh token was refused. The app does this on its own: never scores.</summary>
    internal const string SuspendedTokenUseSignal = "SUSPENDED_TOKEN_USE";

    // LT-07 check-in signals (live-trip.md section 6.3), written on the trip's trucker with the trip
    // as the entity. Flag-only: the trail is supporting evidence, the QR scans prove the handoffs.

    /// <summary>Two consecutive check-ins of a trip more than 1 km apart at over 130 km/h. A GPS glitch can do it too.</summary>
    internal const string GpsJumpSignal = "GPS_JUMP";

    /// <summary>A check-in the phone flagged as a mock location (a location-faking app is in use).</summary>
    internal const string GpsMockedSignal = "GPS_MOCKED";

    // FRD-09 transaction pattern signals (section 15.1). Flag-only: each observes a write that already
    // succeeded and refuses nothing; the risk score and its tier gates are the only enforcement.

    /// <summary>A buyer's payment above the mean plus 3 standard deviations of the buyer's own paid orders.</summary>
    internal const string AmountAnomalySignal = "AMOUNT_ANOMALY";

    /// <summary>A buyer's payment of a large amount from an account under 7 days old.</summary>
    internal const string NewAccountLargeAmountSignal = "NEW_ACCOUNT_LARGE_AMOUNT";

    /// <summary>Repeated payments just under the 10,000 ETB Fayda threshold (structuring around the KYC rule).</summary>
    internal const string KycThresholdStructuringSignal = "KYC_THRESHOLD_STRUCTURING";

    /// <summary>A farmer or trucker who cancels too large a share of the funded orders they took on.</summary>
    internal const string CancelAfterFundRateSignal = "CANCEL_AFTER_FUND_RATE";

    /// <summary>A buyer who disputes too large a share of the escrows they funded.</summary>
    internal const string DisputeRateSignal = "DISPUTE_RATE";

    /// <summary>A delivery confirmed sooner after its pickup than the distance between the two scans allows.</summary>
    internal const string ImpossibleDeliveryTimeSignal = "IMPOSSIBLE_DELIVERY_TIME";

    // FRD-15 relationship signals (section 15.2), written by the hourly order-cron scan on both the buyer and
    // the farmer of a funded order. Flag-only: the trade already happened; the score and its gates enforce.

    /// <summary>The buyer and the farmer of a funded order are one identity: the same user id, phone or Fayda ID.</summary>
    internal const string SelfTradeSignal = "SELF_TRADE";

    /// <summary>The buyer's and the farmer's accounts of a funded order have signed in on the same install (X-Device-Id).</summary>
    internal const string SharedDeviceTradeSignal = "SHARED_DEVICE_TRADE";

    /// <summary>A buyer and a farmer with several funded orders between them in a few days (a regular customer can do it too).</summary>
    internal const string RepeatedPairSignal = "REPEATED_PAIR";

    // Weights per fraud-detection-architecture.md §2 Rule 5 and section 15.
    internal static int SignalWeight(string signalType) => signalType switch
    {
        "GPS_ANOMALY" => 25,
        "FRAUD_DUPLICATE_LISTING" => 20,
        "FRAUD_VELOCITY_EXCEEDED" => 15,
        "PRICE_ANOMALY" => 10,
        IdorCreateConflictSignal => 40,
        RoleForbiddenSignal => 25,
        ImmutableFieldChangeSignal => 25,
        QrHandoffFailureSignal => 5,
        TelebirrPayerMismatchSignal => 0,
        TelebirrPayerRefusedSignal => 5,
        StaleVersionBurstSignal => 0,
        SuspendedTokenUseSignal => 0,
        GpsJumpSignal => 10,
        GpsMockedSignal => 25,
        AmountAnomalySignal => 10,
        NewAccountLargeAmountSignal => 10,
        KycThresholdStructuringSignal => 20,
        CancelAfterFundRateSignal => 15,
        DisputeRateSignal => 10,
        ImpossibleDeliveryTimeSignal => 15,
        SelfTradeSignal => 40,
        SharedDeviceTradeSignal => 25,
        RepeatedPairSignal => 5,
        _ => 10
    };

    /// <summary>
    /// Soft class of the signal catalogue (section 15): flag-only rules whose weight an active
    /// whitelist skips. Every other type is hard and always counts. FRD-08 adds the rejects an
    /// honest client can also produce; LT-07 the check-in signals, which refuse nothing; FRD-09 its
    /// pattern rules except the structuring one, which is about dodging the Fayda rule and so a money
    /// signal a whitelist does not silence. FRD-15: the shared device (a family or cooperative phone) and
    /// the repeated pair (a regular customer); <c>SELF_TRADE</c> is hard, since those identities are unique.
    /// </summary>
    internal static bool IsSoftSignal(string signalType) =>
        signalType is "GPS_ANOMALY" or "PRICE_ANOMALY"
            or QrHandoffFailureSignal or TelebirrPayerMismatchSignal or TelebirrPayerRefusedSignal
            or StaleVersionBurstSignal or SuspendedTokenUseSignal
            or GpsJumpSignal or GpsMockedSignal
            or AmountAnomalySignal or NewAccountLargeAmountSignal or CancelAfterFundRateSignal
            or DisputeRateSignal or ImpossibleDeliveryTimeSignal
            or SharedDeviceTradeSignal or RepeatedPairSignal;

    /// <summary>
    /// FRD-09: the transaction pattern types. Each is written with the order, escrow, delivery or trip
    /// it observed as the entity, counts once per entity (a retried payment of the same order is one
    /// observation) and is capped per type at <c>RISK_PATTERN_TYPE_CAP</c>, so no pattern alone takes a
    /// user past MEDIUM.
    /// </summary>
    internal static bool IsPatternSignal(string signalType) =>
        signalType is AmountAnomalySignal or NewAccountLargeAmountSignal or KycThresholdStructuringSignal
            or CancelAfterFundRateSignal or DisputeRateSignal or ImpossibleDeliveryTimeSignal;

    /// <summary>
    /// FRD-15: the relationship types. Each is written on both parties with the funded order as the entity,
    /// so it counts once per order, and an order it names gets no clean-delivery credit (section 20).
    /// </summary>
    internal static bool IsRelationshipSignal(string signalType) =>
        signalType is SelfTradeSignal or SharedDeviceTradeSignal or RepeatedPairSignal;

    /// <summary>
    /// FRD-15: the relationship types honest users can also produce (a shared phone, a regular customer),
    /// capped together, as one group, at <c>RISK_PATTERN_TYPE_CAP</c> (<see cref="RiskScoreSettings.CapKeyOf"/>),
    /// so the two alone stop at MEDIUM. <c>SELF_TRADE</c> is not capped: one user id, phone or Fayda ID on both
    /// sides is not an honest trade.
    /// </summary>
    internal static bool IsCappedRelationshipType(string signalType) =>
        signalType is SharedDeviceTradeSignal or RepeatedPairSignal;

    /// <summary>
    /// FRD-08 types the scorer counts once per (type, entityId) over the whole window, not once per
    /// hour: the mobile queue sends a refused operation again on every reconnect, so one honest
    /// refusal would otherwise add its weight in every hour it is retried. Probes of different ids
    /// still add up, and an approved row clears its entity. A row without an entityId falls back to
    /// the hourly bucket. <c>IDOR_CREATE_CONFLICT</c> is not one of them: a conflict is final for the
    /// client (marked synced) and web makes a new id per create, so no honest retry repeats it.
    /// LT-07: the check-in signals count once per trip, since a trip with a mock-location app on (or
    /// a phone with a bad GPS) writes a row in every hour it drives. FRD-09: the pattern types count
    /// once per observed order, escrow, delivery or trip (<see cref="IsPatternSignal"/>). FRD-15: the
    /// relationship types once per order (<see cref="IsRelationshipSignal"/>).
    /// </summary>
    internal static bool CountsOncePerEntity(string signalType) =>
        signalType is RoleForbiddenSignal or ImmutableFieldChangeSignal
            or QrHandoffFailureSignal or TelebirrPayerMismatchSignal or TelebirrPayerRefusedSignal
            or StaleVersionBurstSignal or SuspendedTokenUseSignal
            or GpsJumpSignal or GpsMockedSignal
            || IsPatternSignal(signalType)
            || IsRelationshipSignal(signalType);

    /// <summary>
    /// FRD-08 types whose counted weight is capped per type over the window (<c>RISK_REJECT_TYPE_CAP</c>):
    /// honest software can still repeat them on several stuck operations (an old-role queue after a
    /// role change, a client bug), so one type alone stops at MEDIUM.
    /// </summary>
    internal static bool IsCappedRejectType(string signalType) =>
        signalType is RoleForbiddenSignal or ImmutableFieldChangeSignal;

    /// <summary>
    /// FRD-05: the whitelist duration in days. A value that is not an integer from 1 to
    /// <see cref="WhitelistDaysCap"/> keeps the default, so a typo cannot whitelist a user for good.
    /// </summary>
    internal static int WhitelistDaysFrom(Func<string, string?> env) =>
        int.TryParse(env("FRAUD_WHITELIST_DAYS"), System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var days) && days is >= 1 and <= WhitelistDaysCap
            ? days
            : DefaultWhitelistDays;

    internal static string SignalSeverity(int weight) =>
        weight >= 20 ? "high" : weight >= 10 ? "medium" : "low";

    // The admin API addresses signals by a single opaque path id, but the
    // table key is composite (userId + signalKey) — encode both into the id
    // so review actions can reconstruct the key without a table scan.
    internal static string BuildSignalId(string userId, string signalKey)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes($"{userId}|{signalKey}");
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    internal static bool TryParseSignalId(string signalId, out string userId, out string signalKey)
    {
        userId = "";
        signalKey = "";
        try
        {
            var base64 = signalId.Replace('-', '+').Replace('_', '/');
            base64 = base64.PadRight(base64.Length + (4 - base64.Length % 4) % 4, '=');
            var decoded = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(base64));
            var separator = decoded.IndexOf('|');
            if (separator <= 0 || separator == decoded.Length - 1)
            {
                return false;
            }

            userId = decoded[..separator];
            signalKey = decoded[(separator + 1)..];
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    // ── GetUserRiskScoreAsync ──────────────────────────────
    public Task<RiskScoreResult> GetUserRiskScoreAsync(string userId) =>
        GetUserRiskScoreAsync(userId, DateTime.UtcNow, RiskScoreSettings.FromEnvironment());

    // Section 14.3: one Query of the user's partition, newest first, at most MaxPages pages.
    internal async Task<RiskScoreResult> GetUserRiskScoreAsync(
        string userId, DateTime nowUtc, RiskScoreSettings settings)
    {
        if (string.IsNullOrEmpty(_fraudSignalsTable))
        {
            return RiskScoreResult.Unscored(RiskScoreSource.TableNotConfigured);
        }

        if (string.IsNullOrEmpty(userId))
        {
            // A caller bug, not a clean user: a sweep must skip rather than score nobody LOW.
            AlarmLog($"{RiskScoreUnavailableToken} userId=- error=EmptyUserId");
            return RiskScoreResult.Unscored(RiskScoreSource.Unavailable);
        }

        var calculator = new RiskScoreCalculator(settings, nowUtc);
        try
        {
            var pages = 0;
            Dictionary<string, AttributeValue>? startKey = null;
            do
            {
                var response = await _dynamoDb.QueryAsync(BuildRiskQuery(userId, calculator.SinceKey, startKey));
                pages++;
                foreach (var row in response.Items ?? new List<Dictionary<string, AttributeValue>>())
                {
                    calculator.Add(row);
                    if (calculator.ReachedCritical)
                    {
                        return Finish(truncated: false);
                    }
                }

                startKey = response.LastEvaluatedKey is { Count: > 0 } next ? next : null;
            }
            while (startKey != null && pages < settings.MaxPages);

            if (startKey != null)
            {
                // Fail closed: newest-first, a burst in the current hour (deduplicated to one)
                // could otherwise push older weight out of the read.
                AlarmLog($"{RiskScoreTruncatedToken} userId={MoneyAlarmLog.LogId(userId)} pages={pages}");
                return Finish(truncated: true);
            }

            return Finish(truncated: false);
        }
        catch (Exception ex)
        {
            // A gate goes on with today's checks (the baseline step-up still applies); a
            // settlement sweep sees Source == Unavailable and skips the escrow (section 14.4).
            AlarmLog($"{RiskScoreUnavailableToken} userId={MoneyAlarmLog.LogId(userId)} error={MoneyAlarmLog.LogId(ex.GetType().Name)}");
            return RiskScoreResult.Unscored(RiskScoreSource.Unavailable);
        }

        RiskScoreResult Finish(bool truncated)
        {
            if (calculator.UnknownRecordRows > 0)
            {
                AlarmLog($"{RiskScoreUnknownRecordToken} userId={MoneyAlarmLog.LogId(userId)} rows={calculator.UnknownRecordRows} recordType={MoneyAlarmLog.LogId(calculator.FirstUnknownRecordType)}");
            }

            return calculator.Result(truncated);
        }
    }

    private QueryRequest BuildRiskQuery(
        string userId, string sinceKey, Dictionary<string, AttributeValue>? startKey)
    {
        var request = new QueryRequest
        {
            TableName = _fraudSignalsTable,
            KeyConditionExpression = "#u = :uid AND #k >= :since",
            // Only the attributes the scorer reads; descriptions stay on the table.
            ProjectionExpression = "#k, #t, #w, #r, #rt, #wu, #e",
            ExpressionAttributeNames = new Dictionary<string, string>
            {
                { "#u", "userId" },
                { "#k", "signalKey" },
                { "#t", "signalType" },
                { "#w", "weight" },
                { "#r", "resolution" },
                { "#rt", "recordType" },
                { "#wu", "whitelistUntil" },
                { "#e", "entityId" }
            },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                { ":uid", new AttributeValue { S = userId } },
                { ":since", new AttributeValue { S = sinceKey } }
            },
            ScanIndexForward = false, // newest first; "~whitelist" sorts last, so it comes first
            ConsistentRead = false
        };
        if (startKey != null)
        {
            request.ExclusiveStartKey = startKey;
        }

        return request;
    }

    // ── Record methods (post-success velocity tracking) ────
    public async Task RecordListingCreatedAsync(string farmerId)
    {
        var hourBucket = DateTime.UtcNow.ToString("yyyy-MM-ddTHH");
        var key = $"FRAUD_LISTING#{farmerId}#{hourBucket}";
        await _rateLimitRepo.IncrementAsync(key, ListingVelocityTtlSeconds);
    }

    public async Task RecordPaymentInitiatedAsync(string userId)
    {
        // Hourly bucket must match CheckPaymentVelocityAsync's key format.
        var hourBucket = DateTime.UtcNow.ToString("yyyy-MM-ddTHH");
        var key = $"FRAUD_PAYMENT#{userId}#{hourBucket}";
        await _rateLimitRepo.IncrementAsync(key, PaymentVelocityTtlSeconds);
    }

    // ── Private DynamoDB query helpers ─────────────────────

    private async Task<List<Dictionary<string, AttributeValue>>> QueryFarmerRecentListingsAsync(
        string farmerId, string sinceIso)
    {
        var request = new QueryRequest
        {
            TableName = _cropsTable,
            IndexName = "FarmerIdCreatedAtIndex",
            KeyConditionExpression = "farmerId = :fid AND createdAt >= :since",
            FilterExpression = "attribute_not_exists(deletedAt)",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                { ":fid", new AttributeValue { S = farmerId } },
                { ":since", new AttributeValue { S = sinceIso } }
            },
            ProjectionExpression = "cropType, pricePerKg, createdAt"
        };

        var response = await _dynamoDb.QueryAsync(request);
        return response.Items;
    }

    private async Task<List<decimal>> QueryRecentPricesByCropTypeAsync(
        string cropType, int limit)
    {
        var request = new QueryRequest
        {
            TableName = _cropsTable,
            IndexName = "CropTypeCreatedAtIndex",
            KeyConditionExpression = "cropType = :ct",
            FilterExpression = "attribute_not_exists(deletedAt)",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                { ":ct", new AttributeValue { S = cropType } }
            },
            ProjectionExpression = "pricePerKg",
            ScanIndexForward = false, // newest first
            Limit = limit
        };

        var response = await _dynamoDb.QueryAsync(request);
        var prices = new List<decimal>();

        foreach (var item in response.Items)
        {
            if (item.TryGetValue("pricePerKg", out var ppk) &&
                decimal.TryParse(ppk.N, out var price))
            {
                prices.Add(price);
            }
        }

        return prices;
    }

    /// <summary>
    /// Haversine formula for distance between two GPS points in km.
    /// </summary>
    internal static double HaversineDistanceKm(
        double lat1, double lon1, double lat2, double lon2)
    {
        const double R = 6371.0; // Earth radius in km
        var dLat = DegreesToRadians(lat2 - lat1);
        var dLon = DegreesToRadians(lon2 - lon1);
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(DegreesToRadians(lat1)) * Math.Cos(DegreesToRadians(lat2)) *
                Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        var c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        return R * c;
    }

    private static double DegreesToRadians(double degrees) => degrees * Math.PI / 180.0;
}
