# F-14 Fraud Detection Architecture

## Version
1.0.0

## Status
Design — Ready for Implementation

## Date
2026-02-21

---

## 1. Overview

F-14 adds server-side fraud detection to WeredaConnect, targeting four categories of abuse:

| # | Rule | Trigger | Action |
|---|------|---------|--------|
| 1 | Duplicate Listing Detection | Crop listing creation via sync | **Reject** |
| 2 | Price Anomaly Flagging | Crop listing creation via sync | **Flag** (soft) |
| 3 | Velocity Limits | Listing creation + escrow initiation | **Reject** |
| 4 | GPS Consistency Check | Delivery verification (QR scan) | **Flag** (soft) |

**Design principles**:
- No new DynamoDB tables — reuse existing `CropsTable` (with 2 new GSIs) and `RateLimitsTable`
- AOT-compatible — no reflection, all DTOs registered in `AppJsonContext`
- Constructor-injected `IFraudDetectionService` following existing Lambda handler patterns
- Soft checks (flag) never block operations; hard checks (reject) return typed error codes

---

## 2. Fraud Rules — Detailed Design

### Rule 1 — Duplicate Listing Detection

**Trigger**: Before `_cropRepo.CreateAsync()` in `SyncFunction.ProcessCropListingAsync`

**Logic**:
```
1. Query CropsTable via FarmerIdCreatedAtIndex GSI:
     PK = farmerId
     SK >= (now - 1 hour) in ISO 8601
2. For each returned item, check:
     item.cropType == incoming.cropType
     AND |item.pricePerKg - incoming.pricePerKg| / item.pricePerKg <= 0.05  (±5%)
3. If any match found → REJECT
```

**Error**: `FRAUD_DUPLICATE_LISTING` — "A similar listing already exists. Please update your existing listing instead."

**Edge cases**:
- Deleted listings (with `deletedAt` set) are excluded via FilterExpression
- Price tolerance uses percentage, not absolute, to handle different crop price ranges (teff ~45 ETB/kg vs wheat ~20 ETB/kg)
- If `pricePerKg` is 0 (shouldn't pass validation, but defensive), skip the price comparison and match only on `cropType`

### Rule 2 — Price Anomaly Flagging

**Trigger**: Before `_cropRepo.CreateAsync()` in `SyncFunction.ProcessCropListingAsync`

**Logic**:
```
1. Query CropsTable via CropTypeCreatedAtIndex GSI:
     PK = cropType
     SK descending, Limit = 50
     FilterExpression: attribute_not_exists(deletedAt)
2. If < 5 results → SKIP (insufficient data)
3. Compute mean and standard deviation of pricePerKg values
4. If |incoming.pricePerKg - mean| > 3 * stddev → FLAG
```

**Action**: Does NOT reject. Adds `isFlagged: true` and `flagReason: "PRICE_ANOMALY"` to the crop listing data before persistence. Logs a warning via `ILambdaContext.Logger`.

**Statistical notes**:
- Standard deviation computed with Bessel's correction (N-1 denominator) for small samples
- If stddev is 0 (all prices identical), any different price triggers flagging
- Computation is done server-side in C# (no math libraries needed — just `Math.Sqrt`)

### Rule 3 — Velocity Limits

**Trigger A — Listing creation**: Before `_cropRepo.CreateAsync()` in `SyncFunction.ProcessCropListingAsync`
**Trigger B — Payment initiation**: Before `_escrowService.InitiateAsync()` in `EscrowFunction.HandleInitiateAsync`

**Logic (listings)**:
```
1. Key = "FRAUD_LISTING#{farmerId}#{currentHourBucket}"
   where currentHourBucket = DateTime.UtcNow.ToString("yyyy-MM-ddTHH")
2. Get count from RateLimitsTable
3. If count >= 5 → REJECT
4. After successful creation, increment counter (TTL = 3600s)
```

**Logic (payments)**:
```
1. Key = "FRAUD_PAYMENT#{userId}#{currentDateBucket}"
   where currentDateBucket = DateTime.UtcNow.ToString("yyyy-MM-dd")
2. Get count from RateLimitsTable
3. If count >= 10 → REJECT
4. After successful initiation, increment counter (TTL = 86400s)
```

**Error**: `FRAUD_VELOCITY_EXCEEDED` — "Too many operations. Please try again later."

**Why reuse RateLimitsTable**: The existing `RateLimitService` already implements DynamoDB atomic counters with TTL. Velocity checks use the same pattern with different key prefixes. No new repository class needed.

### Rule 4 — GPS Consistency Check

**Trigger**: During delivery status update to `delivered` in `SyncFunction.ProcessDeliveryAsync`

**Logic**:
```
1. Extract delivery GPS from incoming data:
   scanLat = data["gpsDropoff"]["latitude"]
   scanLng = data["gpsDropoff"]["longitude"]
2. If GPS coordinates are missing → SKIP (GPS is optional for rural areas)
3. Look up the associated trip (via tripId in the delivery data)
4. Extract trip's fromWoreda/toWoreda (text-based, no coordinate lookup)
5. If the delivery also has gpsPickup coordinates recorded:
   - Compute Haversine distance between gpsDropoff and gpsPickup
   - If distance < 1 km AND trip spans different woredas → FLAG
     (suspicious: pickup and dropoff at same location for inter-woreda trip)
6. Additionally, if gpsDropoff coordinates are identical to gpsPickup → FLAG
   (suspicious: GPS spoofing / not actually at delivery location)
```

**Action**: Does NOT reject. Adds `gpsAnomalyFlag: true` to the delivery data before persistence. Logs a warning.

**Note**: A full bounding-box check per woreda is deferred to a future phase since we don't have a woreda coordinate database. The existing 5km delivery validation (in F-12) already catches gross GPS mismatches. This rule catches more subtle spoofing patterns.

---

## 3. Architecture

### 3.1 Interface

```csharp
// server/src/Services/IFraudDetectionService.cs
namespace WeredaConnect.Server.Services;

/// <summary>
/// Fraud detection service for F-14.
/// Checks listing creation, payment initiation, and delivery GPS.
/// AOT-compatible — no reflection.
/// </summary>
public interface IFraudDetectionService
{
    /// <summary>
    /// Run all listing-related fraud checks (velocity, duplicate, price anomaly).
    /// Call BEFORE _cropRepo.CreateAsync().
    /// </summary>
    Task<FraudCheckResult> CheckListingCreationAsync(
        string farmerId, string cropType, decimal pricePerKg);

    /// <summary>
    /// Check payment velocity before escrow initiation.
    /// Call BEFORE _escrowService.InitiateAsync().
    /// </summary>
    Task<FraudCheckResult> CheckPaymentVelocityAsync(string userId);

    /// <summary>
    /// Check GPS consistency during delivery verification.
    /// Pure computation — no DB calls.
    /// </summary>
    FraudCheckResult CheckGpsConsistency(
        double? pickupLat, double? pickupLng,
        double? dropoffLat, double? dropoffLng,
        string fromWoreda, string toWoreda);

    /// <summary>
    /// Record a successful listing creation for velocity tracking.
    /// Call AFTER _cropRepo.CreateAsync() succeeds.
    /// </summary>
    Task RecordListingCreatedAsync(string farmerId);

    /// <summary>
    /// Record a successful payment initiation for velocity tracking.
    /// Call AFTER _escrowService.InitiateAsync() succeeds.
    /// </summary>
    Task RecordPaymentInitiatedAsync(string userId);
}
```

### 3.2 DTO

```csharp
// server/src/Models/FraudDtos.cs
namespace WeredaConnect.Server.Models;

/// <summary>
/// Result of a fraud detection check.
/// Fraud checks can either allow, flag (soft), or reject (hard).
/// </summary>
public sealed record FraudCheckResult
{
    /// <summary>
    /// Whether the operation is allowed to proceed.
    /// False = hard reject (return error to client).
    /// </summary>
    public required bool IsAllowed { get; init; }

    /// <summary>
    /// Whether the operation is flagged for review (soft check).
    /// The operation still proceeds, but metadata is added.
    /// </summary>
    public bool IsFlagged { get; init; }

    /// <summary>
    /// Error code for rejected operations (e.g., FRAUD_DUPLICATE_LISTING).
    /// Null when IsAllowed is true.
    /// </summary>
    public string? ErrorCode { get; init; }

    /// <summary>
    /// Human-readable message for the fraud check result.
    /// </summary>
    public string? Message { get; init; }

    /// <summary>
    /// Flag reason code for flagged operations (e.g., "PRICE_ANOMALY", "GPS_ANOMALY").
    /// Null when IsFlagged is false.
    /// </summary>
    public string? FlagReason { get; init; }

    public static FraudCheckResult Allowed() =>
        new() { IsAllowed = true };

    public static FraudCheckResult Flagged(string flagReason, string message) =>
        new() { IsAllowed = true, IsFlagged = true, FlagReason = flagReason, Message = message };

    public static FraudCheckResult Rejected(string errorCode, string message) =>
        new() { IsAllowed = false, ErrorCode = errorCode, Message = message };
}
```

### 3.3 Implementation

```csharp
// server/src/Services/FraudDetectionService.cs
namespace WeredaConnect.Server.Services;

using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using WeredaConnect.Server.Data;
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
    internal const int MaxPaymentsPerDay = 10;
    internal const int PaymentVelocityTtlSeconds = 86400;      // 24 hours
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
                continue;
            if (!item.TryGetValue("pricePerKg", out var ppk) || !decimal.TryParse(ppk.N, out var existingPrice))
                continue;
            if (existingPrice == 0m)
                continue;

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

            if (stdDev > 0 && Math.Abs(pricePerKg - mean) > PriceAnomalyStdDevMultiplier * stdDev)
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
        var dateBucket = DateTime.UtcNow.ToString("yyyy-MM-dd");
        var velocityKey = $"FRAUD_PAYMENT#{userId}#{dateBucket}";
        var entry = await _rateLimitRepo.GetAsync(velocityKey);

        if (entry != null && entry.Count >= MaxPaymentsPerDay)
        {
            return FraudCheckResult.Rejected(
                "FRAUD_VELOCITY_EXCEEDED",
                $"Maximum {MaxPaymentsPerDay} payment initiations per day exceeded.");
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
        var dateBucket = DateTime.UtcNow.ToString("yyyy-MM-dd");
        var key = $"FRAUD_PAYMENT#{userId}#{dateBucket}";
        await _rateLimitRepo.IncrementAsync(key, PaymentVelocityTtlSeconds);
    }

    // ── Private DynamoDB query helpers ─────────────────────

    /// <summary>
    /// Query FarmerIdCreatedAtIndex GSI for recent listings by a farmer.
    /// Returns raw DynamoDB items (not marshalled — we only need cropType + pricePerKg).
    /// </summary>
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
            // Only need cropType and pricePerKg for comparison
            ProjectionExpression = "cropType, pricePerKg, createdAt"
        };

        var response = await _dynamoDb.QueryAsync(request);
        return response.Items;
    }

    /// <summary>
    /// Query CropTypeCreatedAtIndex GSI for recent prices of a crop type.
    /// Returns just the pricePerKg values (decimal list).
    /// </summary>
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
    private static double HaversineDistanceKm(
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
```

---

## 4. DynamoDB Access Patterns

### 4.1 No New Tables

All fraud queries use existing tables:

| Query | Table | Index | Key Condition | Filter |
|-------|-------|-------|---------------|--------|
| Recent farmer listings | CropsTable | **FarmerIdCreatedAtIndex** (NEW GSI) | `farmerId = X AND createdAt >= Y` | `attribute_not_exists(deletedAt)` |
| Recent prices by crop | CropsTable | **CropTypeCreatedAtIndex** (NEW GSI) | `cropType = X` (desc, limit 50) | `attribute_not_exists(deletedAt)` |
| Listing velocity count | RateLimitsTable | Primary key | `pk = "FRAUD_LISTING#farmerId#hour"` | — |
| Payment velocity count | RateLimitsTable | Primary key | `pk = "FRAUD_PAYMENT#userId#date"` | — |

### 4.2 New GSIs on CropsTable

Both GSIs project only the attributes needed for fraud queries (to minimise storage and read costs):

#### GSI: FarmerIdCreatedAtIndex

| Property | Value |
|----------|-------|
| **Partition key** | `farmerId` (String) |
| **Sort key** | `createdAt` (String, ISO 8601) |
| **Projection** | `INCLUDE` — `cropType`, `pricePerKg`, `deletedAt` |
| **Purpose** | Duplicate listing detection (Rule 1), listing velocity alternative |

#### GSI: CropTypeCreatedAtIndex

| Property | Value |
|----------|-------|
| **Partition key** | `cropType` (String) |
| **Sort key** | `createdAt` (String, ISO 8601) |
| **Projection** | `INCLUDE` — `pricePerKg`, `deletedAt` |
| **Purpose** | Price anomaly statistical check (Rule 2) |

### 4.3 CDK Changes

Add both GSIs to the existing CropsTable definition in the infra stack:

```typescript
// In the CDK stack where CropsTable is defined
cropsTable.addGlobalSecondaryIndex({
  indexName: 'FarmerIdCreatedAtIndex',
  partitionKey: { name: 'farmerId', type: dynamodb.AttributeType.STRING },
  sortKey: { name: 'createdAt', type: dynamodb.AttributeType.STRING },
  projectionType: dynamodb.ProjectionType.INCLUDE,
  nonKeyAttributes: ['cropType', 'pricePerKg', 'deletedAt'],
});

cropsTable.addGlobalSecondaryIndex({
  indexName: 'CropTypeCreatedAtIndex',
  partitionKey: { name: 'cropType', type: dynamodb.AttributeType.STRING },
  sortKey: { name: 'createdAt', type: dynamodb.AttributeType.STRING },
  projectionType: dynamodb.ProjectionType.INCLUDE,
  nonKeyAttributes: ['pricePerKg', 'deletedAt'],
});
```

### 4.4 Velocity Key Design

Velocity keys in `RateLimitsTable` use hour/day bucketing for natural TTL expiry:

| Key Pattern | Example | TTL |
|-------------|---------|-----|
| `FRAUD_LISTING#{farmerId}#{hourBucket}` | `FRAUD_LISTING#farmer-123#2026-02-21T14` | 3600s (1 hour) |
| `FRAUD_PAYMENT#{userId}#{dateBucket}` | `FRAUD_PAYMENT#buyer-456#2026-02-21` | 86400s (24 hours) |

This follows the exact same pattern as existing rate limit keys (`SYNC#{userId}`, `LOGIN#{phoneNumber}`).

---

## 5. Integration Points

### 5.1 SyncFunction — Listing Creation

**File**: `server/src/Functions/SyncFunction.cs`
**Method**: `ProcessCropListingAsync`
**Location**: `case "create":` block, after field whitelist validation (line ~340), before `_cropRepo.CreateAsync()` (line ~341)

**Changes to SyncFunction**:

1. Add `IFraudDetectionService?` field:
```csharp
private readonly IFraudDetectionService? _fraudService;
```

2. In the default constructor, create the fraud service:
```csharp
// After _rateLimitService initialization (~line 102):
_fraudService = new FraudDetectionService(
    _dynamoDb,
    cropsTable,
    new RateLimitRepository(_dynamoDb, rateLimitTable));
```

3. In the test constructor, accept optional fraud service:
```csharp
public SyncFunction(ISyncMetadataRepository syncMetadataRepo, ICropRepository cropRepo,
    IAmazonDynamoDB dynamoDb, string tripsTable, string deliveriesTable,
    IRateLimitService? rateLimitService = null,
    IFraudDetectionService? fraudService = null)
{
    // ... existing assignments ...
    _fraudService = fraudService;
}
```

4. In `ProcessCropListingAsync`, `case "create":`, after field whitelist check:
```csharp
// ── Fraud detection (F-14) ──
if (_fraudService != null)
{
    var cropType = operation.Data.GetValueOrDefault("cropType")?.ToString() ?? "";
    var pricePerKg = 0m;
    if (operation.Data.TryGetValue("pricePerKg", out var ppkObj))
    {
        _ = decimal.TryParse(ppkObj?.ToString(), out pricePerKg);
    }

    var fraudCheck = await _fraudService.CheckListingCreationAsync(
        authenticatedUserId!, cropType, pricePerKg);

    if (!fraudCheck.IsAllowed)
    {
        return new SyncOperationResult
        {
            OperationId = operation.OperationId,
            Status = "error",
            Message = $"{fraudCheck.ErrorCode}: {fraudCheck.Message}"
        };
    }

    if (fraudCheck.IsFlagged)
    {
        operation.Data["isFlagged"] = true;
        operation.Data["flagReason"] = fraudCheck.FlagReason ?? "UNKNOWN";
        context.Logger.LogWarning(
            $"Fraud flag on listing {operation.EntityId}: {fraudCheck.FlagReason} — {fraudCheck.Message}");
    }
}
```

5. After successful `_cropRepo.CreateAsync()`:
```csharp
if (createResult.IsSuccess)
{
    // Record for velocity tracking (F-14)
    if (_fraudService != null)
    {
        await _fraudService.RecordListingCreatedAsync(authenticatedUserId!);
    }

    return new SyncOperationResult { ... };
}
```

### 5.2 SyncFunction — Delivery GPS Check

**File**: `server/src/Functions/SyncFunction.cs`
**Method**: `ProcessDeliveryAsync`
**Location**: When delivery status transitions to `delivered`, after status validation

```csharp
// In the delivery update block, when newStatus == "delivered":
if (_fraudService != null && operation.Data != null)
{
    double? pickupLat = null, pickupLng = null, dropoffLat = null, dropoffLng = null;
    string fromWoreda = "", toWoreda = "";

    // Extract GPS from existing delivery + incoming data
    // (pickup GPS was recorded at "pickedUp" transition, dropoff is incoming)
    if (operation.Data.TryGetValue("gpsPickup", out var gpObj) && gpObj is Dictionary<string, object> gp)
    {
        if (gp.TryGetValue("latitude", out var lat)) pickupLat = Convert.ToDouble(lat);
        if (gp.TryGetValue("longitude", out var lng)) pickupLng = Convert.ToDouble(lng);
    }
    if (operation.Data.TryGetValue("gpsDropoff", out var gdObj) && gdObj is Dictionary<string, object> gd)
    {
        if (gd.TryGetValue("latitude", out var lat)) dropoffLat = Convert.ToDouble(lat);
        if (gd.TryGetValue("longitude", out var lng)) dropoffLng = Convert.ToDouble(lng);
    }

    // Get trip woredas from the associated trip
    // (trip lookup is already done for delivery validation)
    fromWoreda = existingDelivery?["fromWoreda"]?.ToString() ?? "";
    toWoreda = existingDelivery?["toWoreda"]?.ToString() ?? "";

    var gpsCheck = _fraudService.CheckGpsConsistency(
        pickupLat, pickupLng, dropoffLat, dropoffLng, fromWoreda, toWoreda);

    if (gpsCheck.IsFlagged)
    {
        operation.Data["gpsAnomalyFlag"] = true;
        context.Logger.LogWarning(
            $"GPS anomaly on delivery {operation.EntityId}: {gpsCheck.Message}");
    }
}
```

### 5.3 EscrowFunction — Payment Velocity

**File**: `server/src/Functions/EscrowFunction.cs`
**Method**: `HandleInitiateAsync`
**Location**: After authentication check, before `_escrowService.InitiateAsync()`

**Changes to EscrowFunction**:

1. Add `IFraudDetectionService?` field:
```csharp
private readonly IFraudDetectionService? _fraudService;
```

2. In the default constructor, create the fraud service:
```csharp
// After creating _escrowService:
var cropsTable = Environment.GetEnvironmentVariable("CROPS_TABLE") ?? "";
var rateLimitTable = Environment.GetEnvironmentVariable("RATE_LIMITS_TABLE") ?? "";
if (!string.IsNullOrEmpty(rateLimitTable))
{
    _fraudService = new FraudDetectionService(
        dynamoDb, cropsTable,
        new RateLimitRepository(dynamoDb, rateLimitTable));
}
```

3. In `HandleInitiateAsync`, after `userId` extraction:
```csharp
// ── Fraud velocity check (F-14) ──
if (_fraudService != null)
{
    var fraudCheck = await _fraudService.CheckPaymentVelocityAsync(userId);
    if (!fraudCheck.IsAllowed)
    {
        return LambdaResponse.Error(429, fraudCheck.ErrorCode!, fraudCheck.Message!);
    }
}
```

4. After successful initiation (success response path):
```csharp
// Record for velocity tracking (F-14)
if (_fraudService != null && result.IsSuccess)
{
    await _fraudService.RecordPaymentInitiatedAsync(userId);
}
```

---

## 6. AOT Compliance

### 6.1 New DTO Registration

Add to `server/src/AppJsonContext.cs`:

```csharp
// Fraud detection DTOs (F-14)
[JsonSerializable(typeof(FraudCheckResult))]
```

### 6.2 No Reflection Used

- `FraudDetectionService` uses direct DynamoDB `QueryRequest` / `QueryResponse` (same as existing `CropRepository`)
- `FraudCheckResult` is a sealed record with `static` factory methods — no reflection
- Haversine computation uses `System.Math` — fully AOT-safe
- String formatting uses `ToString("o")` and `ToString("yyyy-MM-ddTHH")` — no reflection
- Velocity tracking reuses `IRateLimitRepository` (already proven AOT-compatible)

### 6.3 No New Regex Patterns

None needed. All comparisons are string equality or numeric.

### 6.4 No New NuGet Packages

All fraud detection logic uses:
- `AWSSDK.DynamoDBv2` (already referenced)
- `System.Math` (BCL)
- `System.Linq` (already used)

---

## 7. Mobile Integration Points

### 7.1 Error Codes

The server returns these new error codes in the `SyncOperationResult.Message` field (for sync) or `APIGatewayProxyResponse` body (for escrow):

| Error Code | HTTP Status | Trigger | User Action |
|------------|-------------|---------|-------------|
| `FRAUD_DUPLICATE_LISTING` | 400 (via sync `error` status) | Duplicate listing detected | Edit existing listing instead |
| `FRAUD_VELOCITY_EXCEEDED` | 429 (via sync `error` status or HTTP 429) | Too many listings/payments | Wait and retry |

**Note**: Flagged operations (price anomaly, GPS anomaly) do NOT return errors to the client. They succeed normally but with `isFlagged: true` in the response data. The mobile app does not need to handle flags — they are for admin review.

### 7.2 Mobile Error Handling

In the sync error handler (`SyncExecutor` / `CreateCropListingScreen`), check for the new error codes:

```dart
// When sync returns status: "error"
if (message.contains('FRAUD_DUPLICATE_LISTING')) {
  // Show localized duplicate listing message
  showError(context.l10n.fraudDuplicateListing);
} else if (message.contains('FRAUD_VELOCITY_EXCEEDED')) {
  // Show localized velocity exceeded message
  showError(context.l10n.fraudVelocityExceeded);
}
```

### 7.3 Localization Keys

Add to all three `.arb` files:

| Key | English | Amharic | Afan Oromo |
|-----|---------|---------|------------|
| `fraudDuplicateListing` | "A similar listing already exists. Please update your existing listing instead." | "ተመሳሳይ ዝርዝር አስቀድሞ አለ። እባክዎ ያለውን ዝርዝር ያዘምኑ።" | "Tarreen walfakkaataan duraan jira. Maaloo tarree jiru haaromsi." |
| `fraudVelocityExceeded` | "Too many requests. Please wait a moment and try again." | "በጣም ብዙ ጥያቄዎች። እባክዎ ትንሽ ይጠብቁ እና ደግመው ይሞክሩ።" | "Gaaffiin baay'een ergameera. Maaloo yeroo xiqqoo eegaatii irra deebi'ii yaali." |

---

## 8. File Structure

```
server/src/Services/
├── IFraudDetectionService.cs       ← NEW (interface)
├── FraudDetectionService.cs        ← NEW (implementation)
├── EscrowService.cs                ← EXISTING (no changes)
├── ...

server/src/Models/
├── FraudDtos.cs                    ← NEW (FraudCheckResult)
├── ...

server/src/Functions/
├── SyncFunction.cs                 ← MODIFIED (add fraud checks in ProcessCropListingAsync + ProcessDeliveryAsync)
├── EscrowFunction.cs               ← MODIFIED (add payment velocity check in HandleInitiateAsync)

server/src/
├── AppJsonContext.cs               ← MODIFIED (register FraudCheckResult)

infra/lib/
├── ...                             ← MODIFIED (add 2 GSIs to CropsTable)
```

**No new data layer files** — fraud queries use `IAmazonDynamoDB` directly (same pattern as `SyncFunction` for trips/deliveries) and velocity uses existing `IRateLimitRepository`.

---

## 9. Testing Strategy

### 9.1 Unit Tests

```
server/test/Services/FraudDetectionServiceTests.cs ← NEW
```

Test cases:

| Test | Method | Assertion |
|------|--------|-----------|
| Velocity under limit | `CheckListingCreationAsync` | Returns `Allowed()` |
| Velocity at limit | `CheckListingCreationAsync` | Returns `Rejected("FRAUD_VELOCITY_EXCEEDED", ...)` |
| Duplicate exact match | `CheckListingCreationAsync` | Returns `Rejected("FRAUD_DUPLICATE_LISTING", ...)` |
| Duplicate within 5% price | `CheckListingCreationAsync` | Returns `Rejected("FRAUD_DUPLICATE_LISTING", ...)` |
| Duplicate beyond 5% price | `CheckListingCreationAsync` | Returns `Allowed()` |
| Duplicate different cropType | `CheckListingCreationAsync` | Returns `Allowed()` |
| Duplicate deleted listing | `CheckListingCreationAsync` | Returns `Allowed()` (deleted excluded) |
| Price anomaly 4 sigma | `CheckListingCreationAsync` | Returns `Flagged("PRICE_ANOMALY", ...)` |
| Price anomaly within 3 sigma | `CheckListingCreationAsync` | Returns `Allowed()` |
| Price anomaly < 5 samples | `CheckListingCreationAsync` | Returns `Allowed()` (skipped) |
| Payment velocity under limit | `CheckPaymentVelocityAsync` | Returns `Allowed()` |
| Payment velocity at limit | `CheckPaymentVelocityAsync` | Returns `Rejected(...)` |
| GPS inter-woreda < 1km | `CheckGpsConsistency` | Returns `Flagged("GPS_ANOMALY", ...)` |
| GPS same woreda < 1km | `CheckGpsConsistency` | Returns `Allowed()` (same woreda is fine) |
| GPS identical coordinates | `CheckGpsConsistency` | Returns `Flagged("GPS_ANOMALY", ...)` |
| GPS missing coordinates | `CheckGpsConsistency` | Returns `Allowed()` (skipped) |
| GPS normal delivery | `CheckGpsConsistency` | Returns `Allowed()` |

### 9.2 Mock Strategy

- `IAmazonDynamoDB` — mock with NSubstitute (already used in project)
- `IRateLimitRepository` — mock with NSubstitute
- `IFraudDetectionService` — mock in `SyncFunction` and `EscrowFunction` integration tests

---

## 10. Performance Considerations

| Operation | DynamoDB Cost | Latency Impact |
|-----------|--------------|----------------|
| Velocity check (GetItem) | 0.5 RCU | ~5ms |
| Duplicate detection (GSI Query) | 1-5 RCU (depends on farmer's listing count) | ~10-20ms |
| Price anomaly (GSI Query, 50 items) | 5-10 RCU | ~20-30ms |
| GPS consistency | 0 RCU (pure computation) | <1ms |
| **Total per listing creation** | **~7-16 RCU** | **~35-55ms added latency** |
| Payment velocity check | 0.5 RCU | ~5ms |

This is acceptable for the sync endpoint which is already a batch operation (typically 1-5 operations per batch). The fraud checks run per-operation, not per-batch.

### Optimization Notes

- GSI projections are minimal (`INCLUDE` with only needed attributes) to reduce read costs
- Velocity checks run first (cheapest) — if rejected, skip the more expensive GSI queries
- `ScanIndexForward = false` on price anomaly query ensures we get the most recent prices first
- Future optimization: cache recent price statistics in-memory per Lambda invocation (Lambda container reuse)

---

## Changelog

| Version | Date | Change |
|---------|------|--------|
| 1.0.0 | 2026-02-21 | Initial architecture: 4 fraud rules, DynamoDB access patterns, integration points, mobile error codes |

---

**Last Updated**: 2026-02-21
**Author**: Architecture Agent
**Status**: Design — Ready for Implementation
