namespace WeredaConnect.Server.Services;

using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.RateLimiting;

public sealed class FraudDetectionService : IFraudDetectionService
{
    private readonly IAmazonDynamoDB _dynamoDb;
    private readonly string _cropsTable;
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

    public FraudDetectionService(
        IAmazonDynamoDB dynamoDb,
        string cropsTable,
        IRateLimitRepository rateLimitRepo)
    {
        _dynamoDb = dynamoDb ?? throw new ArgumentNullException(nameof(dynamoDb));
        _cropsTable = cropsTable ?? throw new ArgumentNullException(nameof(cropsTable));
        _rateLimitRepo = rateLimitRepo ?? throw new ArgumentNullException(nameof(rateLimitRepo));
    }

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
