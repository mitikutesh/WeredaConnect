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
    internal const double PriceAnomalyStdDevMultiplier = 3.0;
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
    /// Sink for the risk score alarm lines. Defaults to stdout, which Lambda ships to the
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

        if (recentPrices.Count >= PriceAnomalyMinSamples)
        {
            var mean = recentPrices.Average();
            var variance = recentPrices.Sum(p => (p - mean) * (p - mean)) / (recentPrices.Count - 1);
            var stdDev = (decimal)Math.Sqrt((double)variance);

            if (stdDev > 0 && Math.Abs(pricePerKg - mean) > (decimal)PriceAnomalyStdDevMultiplier * stdDev)
            {
                return FraudCheckResult.Flagged(
                    "PRICE_ANOMALY",
                    $"Price {pricePerKg} ETB/kg is unusual for {cropType} (avg: {mean:F2}, stddev: {stdDev:F2}).");
            }
        }

        return FraudCheckResult.Allowed();
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
        }
        catch (ConditionalCheckFailedException)
        {
            // This user already has a row of this type in this hour: the write limit, not a failure.
        }
    }

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
        _ => 10
    };

    /// <summary>
    /// Soft class of the signal catalogue (section 15): flag-only rules whose weight an active
    /// whitelist skips. Every other type is hard and always counts. FRD-08 adds the rejects an
    /// honest client can also produce; LT-07 the check-in signals, which refuse nothing.
    /// </summary>
    internal static bool IsSoftSignal(string signalType) =>
        signalType is "GPS_ANOMALY" or "PRICE_ANOMALY"
            or QrHandoffFailureSignal or TelebirrPayerMismatchSignal or TelebirrPayerRefusedSignal
            or StaleVersionBurstSignal or SuspendedTokenUseSignal
            or GpsJumpSignal or GpsMockedSignal;

    /// <summary>
    /// FRD-08 types the scorer counts once per (type, entityId) over the whole window, not once per
    /// hour: the mobile queue sends a refused operation again on every reconnect, so one honest
    /// refusal would otherwise add its weight in every hour it is retried. Probes of different ids
    /// still add up, and an approved row clears its entity. A row without an entityId falls back to
    /// the hourly bucket. <c>IDOR_CREATE_CONFLICT</c> is not one of them: a conflict is final for the
    /// client (marked synced) and web makes a new id per create, so no honest retry repeats it.
    /// LT-07: the check-in signals count once per trip, since a trip with a mock-location app on (or
    /// a phone with a bad GPS) writes a row in every hour it drives.
    /// </summary>
    internal static bool CountsOncePerEntity(string signalType) =>
        signalType is RoleForbiddenSignal or ImmutableFieldChangeSignal
            or QrHandoffFailureSignal or TelebirrPayerMismatchSignal or TelebirrPayerRefusedSignal
            or StaleVersionBurstSignal or SuspendedTokenUseSignal
            or GpsJumpSignal or GpsMockedSignal;

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
