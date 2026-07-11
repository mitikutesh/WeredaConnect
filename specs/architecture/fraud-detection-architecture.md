# Fraud Detection System Architecture (F-14)

## Version
2.0.0 | 2026-02-23

## Status
Design — Ready for Implementation

---

## 1. Overview & Threat Model

F-14 adds fraud detection to WeredaConnect, targeting five categories of abuse that threaten the platform's trust model — especially critical in Ethiopian rural communities where users have limited legal recourse.

### 1.1 Threat Scenarios

| # | Threat | Actor | Impact | Detection |
|---|--------|-------|--------|-----------|
| T1 | **Listing spam** | Malicious farmer | Floods marketplace, degrades browse UX | Velocity limits (Rule 3) |
| T2 | **Duplicate listings** | Farmer (accidental or intentional) | Confuses buyers, inflates supply | Duplicate detection (Rule 1) |
| T3 | **Price manipulation** | Bad actor | Distorts market prices, enables scams | Price anomaly flagging (Rule 2) |
| T4 | **GPS spoofing** | Trucker or colluding parties | Fake deliveries to claim escrow funds | GPS consistency check (Rule 4) |
| T5 | **Payment flooding** | Bot or compromised account | Drain escrow system, DoS on Telebirr | Payment velocity limits (Rule 3) |
| T6 | **Account abuse patterns** | Repeat offenders | Sustained low-level fraud across rules | Risk scoring (Rule 5) |

### 1.2 Design Principles

- **Server-side enforcement**: All fraud rules run on the server — mobile is untrusted
- **No new DynamoDB tables** for rules 1-4: Reuse `CropsTable` (2 new GSIs) and `RateLimitsTable`
- **One new table** for Rule 5: `FraudSignalsTable` for persistent signal tracking
- **AOT-compatible**: No reflection, all DTOs in `AppJsonContext`
- **Soft vs hard checks**: Soft checks (flag) never block operations; hard checks (reject) return typed error codes
- **Offline-first compatible**: Mobile pre-checks are advisory only; server is authoritative
- **Minimal mobile impact**: No Isar schema changes; fraud detection is transparent to users

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
- Deleted listings (`deletedAt` set) excluded via FilterExpression
- Price tolerance uses percentage (not absolute) to handle different crop price ranges (teff ~45 ETB/kg vs wheat ~20 ETB/kg)
- If `pricePerKg` is 0, skip price comparison and match only on `cropType`

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

**Action**: Does NOT reject. Adds `isFlagged: true` and `flagReason: "PRICE_ANOMALY"` to the crop listing before persistence. Logs a warning.

**Statistical notes**:
- Standard deviation uses Bessel's correction (N-1 denominator) for small samples
- If stddev is 0 (all prices identical), any different price triggers flagging
- Computed in C# using `Math.Sqrt` — no external libraries

### Rule 3 — Velocity Limits

**Trigger A — Listing creation**: Before `_cropRepo.CreateAsync()`
**Trigger B — Payment initiation**: Before `_escrowService.InitiateAsync()`

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

**Reuse**: Existing `IRateLimitRepository` with `GetAsync` / `IncrementAsync` — same atomic counter pattern as auth rate limiting.

### Rule 4 — GPS Consistency Check

**Trigger**: During delivery status update to `delivered` in `SyncFunction.ProcessDeliveryAsync`

**Logic**:
```
1. Extract GPS from incoming data (gpsPickup, gpsDropoff)
2. If GPS coordinates missing → SKIP (optional for rural areas)
3. If trip spans different woredas but GPS points < 1km apart → FLAG
   (suspicious: pickup and dropoff at same location for inter-woreda trip)
4. If gpsDropoff coordinates identical to gpsPickup → FLAG
   (suspicious: GPS spoofing)
```

**Action**: Does NOT reject. Adds `gpsAnomalyFlag: true` to the delivery. Logs a warning.

**Note**: Full bounding-box per woreda deferred — the existing 5km delivery validation (F-12) already catches gross mismatches. This rule catches more subtle spoofing patterns.

### Rule 5 — Account-Level Risk Scoring

**Trigger**: After any fraud check returns a flag or rejection, and on a periodic batch basis.

**Logic**:
```
1. Maintain a FraudSignal record per user per event in FraudSignalsTable
2. Compute risk score as weighted sum of signals in the last 30 days:
   - FRAUD_DUPLICATE_LISTING rejection:  +20 points
   - FRAUD_VELOCITY_EXCEEDED rejection:  +15 points
   - PRICE_ANOMALY flag:                 +10 points
   - GPS_ANOMALY flag:                   +25 points
   - Successful delivery (no flags):     -5 points (decay, min 0)
3. Risk tiers:
   - 0-29:   LOW    (no action)
   - 30-59:  MEDIUM (enhanced monitoring — logged, no user impact)
   - 60-99:  HIGH   (all operations flagged for admin review)
   - 100+:   CRITICAL (operations soft-blocked — require admin approval)
4. Risk score stored in UserRiskScores (attribute on UsersTable)
```

**Why not a separate table for risk scores**: The risk score is a single numeric attribute per user. Adding it to the existing UsersTable avoids a new table and allows the score to be checked during any user-authenticated operation without a cross-table lookup.

**Decay mechanism**: Scores naturally decay because signals have a 30-day sliding window. Additionally, successful deliveries earn negative points to reward good behavior.

---

## 3. Architecture

### 3.1 Component Overview

```
┌──────────────────────────────────────────────────────────────┐
│                       MOBILE (Flutter)                        │
│                                                               │
│  ┌──────────────────┐  ┌────────────────┐                    │
│  │ CropListingBloc   │  │ SyncExecutor   │                    │
│  │ (advisory pre-    │  │ (handles fraud │                    │
│  │  check: velocity  │  │  error codes   │                    │
│  │  + dup warning)   │  │  from server)  │                    │
│  └──────────────────┘  └──────┬─────────┘                    │
│                               │                               │
└───────────────────────────────┼──────────────────────────────┘
                                │ POST /sync (crop + delivery ops)
                                │ POST /escrow/initiate
                                ▼
┌──────────────────────────────────────────────────────────────┐
│                   SERVER (.NET 10 AOT)                         │
│                                                               │
│  ┌────────────────────────────────────────────────────┐      │
│  │              IFraudDetectionService                  │      │
│  │  CheckListingCreationAsync()                         │      │
│  │  CheckPaymentVelocityAsync()                         │      │
│  │  CheckGpsConsistency()                               │      │
│  │  RecordListingCreatedAsync()                         │      │
│  │  RecordPaymentInitiatedAsync()                       │      │
│  │  RecordFraudSignalAsync()                            │      │
│  │  GetUserRiskScoreAsync()                             │      │
│  └──┬─────────────────────────────────────────────┬────┘      │
│     │                                             │            │
│     ▼                                             ▼            │
│  ┌────────────────┐  ┌────────────────┐  ┌──────────────┐    │
│  │ CropsTable     │  │ RateLimitsTable│  │ FraudSignals │    │
│  │ (2 new GSIs)   │  │ (velocity keys)│  │ Table (NEW)  │    │
│  └────────────────┘  └────────────────┘  └──────────────┘    │
│                                                               │
│  Integration points:                                          │
│  ├── SyncFunction.ProcessCropListingAsync (Rules 1,2,3)       │
│  ├── SyncFunction.ProcessDeliveryAsync (Rule 4)               │
│  └── EscrowFunction.HandleInitiateAsync (Rule 3)              │
└──────────────────────────────────────────────────────────────┘
```

### 3.2 Interface

```csharp
// server/src/Services/IFraudDetectionService.cs
namespace WeredaConnect.Server.Services;

public interface IFraudDetectionService
{
    /// <summary>
    /// Run all listing-related fraud checks (velocity → duplicate → price anomaly).
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

    /// <summary>
    /// Record a fraud signal for risk scoring.
    /// Call when a check flags or rejects.
    /// </summary>
    Task RecordFraudSignalAsync(string userId, string signalType, string entityId);

    /// <summary>
    /// Get the current risk score for a user (0-100+).
    /// </summary>
    Task<int> GetUserRiskScoreAsync(string userId);
}
```

### 3.3 DTO

```csharp
// server/src/Models/FraudDtos.cs
namespace WeredaConnect.Server.Models;

public sealed record FraudCheckResult
{
    public required bool IsAllowed { get; init; }
    public bool IsFlagged { get; init; }
    public string? ErrorCode { get; init; }
    public string? Message { get; init; }
    public string? FlagReason { get; init; }

    public static FraudCheckResult Allowed() =>
        new() { IsAllowed = true };

    public static FraudCheckResult Flagged(string flagReason, string message) =>
        new() { IsAllowed = true, IsFlagged = true, FlagReason = flagReason, Message = message };

    public static FraudCheckResult Rejected(string errorCode, string message) =>
        new() { IsAllowed = false, ErrorCode = errorCode, Message = message };
}
```

### 3.4 Implementation

```csharp
// server/src/Services/FraudDetectionService.cs
namespace WeredaConnect.Server.Services;

public sealed class FraudDetectionService : IFraudDetectionService
{
    private readonly IAmazonDynamoDB _dynamoDb;
    private readonly string _cropsTable;
    private readonly string _fraudSignalsTable;
    private readonly IRateLimitRepository _rateLimitRepo;

    // Thresholds (internal for testing)
    internal const int MaxListingsPerHour = 5;
    internal const int ListingVelocityTtlSeconds = 3600;
    internal const int MaxPaymentsPerDay = 10;
    internal const int PaymentVelocityTtlSeconds = 86400;
    internal const double DuplicateListingWindowHours = 1.0;
    internal const double PriceTolerancePercent = 0.05;
    internal const int PriceAnomalyMinSamples = 5;
    internal const int PriceAnomalySampleSize = 50;
    internal const double PriceAnomalyStdDevMultiplier = 3.0;
    internal const double GpsSpoofingThresholdKm = 1.0;

    // Risk score weights
    internal const int WeightDuplicateRejection = 20;
    internal const int WeightVelocityRejection = 15;
    internal const int WeightPriceAnomaly = 10;
    internal const int WeightGpsAnomaly = 25;
    internal const int WeightSuccessfulDelivery = -5;
    internal const int SignalRetentionDays = 30;

    public FraudDetectionService(
        IAmazonDynamoDB dynamoDb,
        string cropsTable,
        string fraudSignalsTable,
        IRateLimitRepository rateLimitRepo)
    {
        _dynamoDb = dynamoDb;
        _cropsTable = cropsTable;
        _fraudSignalsTable = fraudSignalsTable;
        _rateLimitRepo = rateLimitRepo;
    }

    // CheckListingCreationAsync runs checks in order:
    // velocity → duplicate → price anomaly
    // Returns on first rejection; flags accumulate.

    // CheckGpsConsistency is pure computation — no DB calls.

    // RecordFraudSignalAsync writes to FraudSignalsTable:
    //   PK: userId, SK: timestamp#signalType
    //   TTL: 30 days from now

    // GetUserRiskScoreAsync queries FraudSignalsTable:
    //   PK = userId, SK >= (now - 30 days)
    //   Sum weights by signalType, clamp min to 0

    // See full implementation in specs/decisions/f14-fraud-detection-architecture.md
}
```

### 3.5 Haversine Distance (GPS Check)

```csharp
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
```

AOT-safe: uses only `System.Math`.

---

## 4. Data Model

### 4.1 No Changes to Existing Tables (Rules 1-4)

All fraud queries for Rules 1-4 reuse existing tables:

| Query | Table | Index | Key Condition | Filter |
|-------|-------|-------|---------------|--------|
| Recent farmer listings | CropsTable | **FarmerIdCreatedAtIndex** (NEW GSI) | `farmerId = X AND createdAt >= Y` | `attribute_not_exists(deletedAt)` |
| Recent prices by crop | CropsTable | **CropTypeCreatedAtIndex** (NEW GSI) | `cropType = X` (desc, limit 50) | `attribute_not_exists(deletedAt)` |
| Listing velocity count | RateLimitsTable | Primary key | `pk = "FRAUD_LISTING#farmerId#hour"` | — |
| Payment velocity count | RateLimitsTable | Primary key | `pk = "FRAUD_PAYMENT#userId#date"` | — |

### 4.2 New GSIs on CropsTable

#### GSI: FarmerIdCreatedAtIndex

| Property | Value |
|----------|-------|
| **Partition key** | `farmerId` (String) |
| **Sort key** | `createdAt` (String, ISO 8601) |
| **Projection** | `INCLUDE` — `cropType`, `pricePerKg`, `deletedAt` |
| **Purpose** | Duplicate listing detection (Rule 1) |

#### GSI: CropTypeCreatedAtIndex

| Property | Value |
|----------|-------|
| **Partition key** | `cropType` (String) |
| **Sort key** | `createdAt` (String, ISO 8601) |
| **Projection** | `INCLUDE` — `pricePerKg`, `deletedAt` |
| **Purpose** | Price anomaly statistical check (Rule 2) |

### 4.3 New DynamoDB Table: FraudSignalsTable (Rule 5)

| Property | Value |
|----------|-------|
| **Table name** | `wereda-connect-{env}-fraud-signals` |
| **Partition key** | `userId` (String) |
| **Sort key** | `signalKey` (String) — format: `{ISO timestamp}#{signalType}` |
| **TTL attribute** | `expiresAt` — auto-deletes signals after 30 days |
| **Billing** | PAY_PER_REQUEST |

**Attributes per item** (as written by `FraudDetectionService.RecordFraudSignalAsync`):
```
userId:      "farmer-123"
signalKey:   "2026-02-23T14:00:00Z#FRAUD_DUPLICATE_LISTING"
signalId:    base64url("{userId}|{signalKey}")   — opaque single id for the admin API path
signalType:  "FRAUD_DUPLICATE_LISTING"
severity:    "high" | "medium" | "low"           — derived from weight (≥20 high, ≥10 medium)
description: "A similar listing already exists…" — human-readable check message
status:      "pending"                           — becomes "reviewed" after admin action
entityId:    "crop-456"                          (omitted when unknown)
entityType:  "crop" | "delivery" | "escrow"
weight:      20
createdAt:   "2026-02-23T14:00:00Z"
expiresAt:   1743868800  (Unix epoch, 30 days from creation)
```

**Admin review semantics**: the admin API addresses a signal by `signalId`
(base64url of `userId|signalKey`) so `POST /admin/fraud-signals/{id}/action`
can reconstruct the composite table key without a scan. The review action
sets `status = "reviewed"` (the admin UI keys on that value) and records the
chosen verb (`approve`/`reject`/`whitelist`) in `resolution`, plus
`reviewedBy`/`reviewedAt`/`reviewNotes`.

**Producers**: signals are recorded best-effort (never blocking the underlying
operation) at three sites — listing creation in `SyncFunction` (hard rejects
`FRAUD_VELOCITY_EXCEEDED`/`FRAUD_DUPLICATE_LISTING` and the soft
`PRICE_ANOMALY` flag), delivery GPS checks in `SyncFunction` (`GPS_ANOMALY`),
and escrow initiation velocity rejections in `EscrowFunction`
(`FRAUD_VELOCITY_EXCEEDED`).

**Why a separate table instead of an attribute on UsersTable**: Individual fraud signals need a time-series query pattern (PK=userId, SK=timestamp) that doesn't fit the UsersTable schema (PK=userId, no SK). Storing signals as a list attribute would require read-modify-write which is not atomic. A separate table allows clean TTL-based expiry and efficient range queries.

**Risk score is computed on-read** by summing signal weights in the 30-day window. No separate risk score storage needed — this avoids stale score problems and keeps the model simple.

### 4.4 No Isar Schema Changes on Mobile

Fraud detection is transparent to the mobile client:
- Fraud rejections arrive as standard sync error codes (`FRAUD_DUPLICATE_LISTING`, `FRAUD_VELOCITY_EXCEEDED`)
- Flagged operations succeed normally — `isFlagged` is server-side metadata only
- No new Isar collections or fields needed

### 4.5 Velocity Key Design

Velocity keys in `RateLimitsTable` use hour/day bucketing for natural TTL expiry:

| Key Pattern | Example | TTL |
|-------------|---------|-----|
| `FRAUD_LISTING#{farmerId}#{hourBucket}` | `FRAUD_LISTING#farmer-123#2026-02-21T14` | 3600s |
| `FRAUD_PAYMENT#{userId}#{dateBucket}` | `FRAUD_PAYMENT#buyer-456#2026-02-21` | 86400s |

---

## 5. Server-Side Integration

### 5.1 SyncFunction — Listing Creation (Rules 1, 2, 3)

**File**: `server/src/Functions/SyncFunction.cs`
**Method**: `ProcessCropListingAsync` — `case "create":` block

**Changes**:

1. Add `IFraudDetectionService?` field to `SyncFunction`:
```csharp
private readonly IFraudDetectionService? _fraudService;
```

2. Initialize in default constructor (after `_rateLimitService`):
```csharp
var fraudSignalsTable = Environment.GetEnvironmentVariable("FRAUD_SIGNALS_TABLE") ?? "";
_fraudService = new FraudDetectionService(
    _dynamoDb, cropsTable, fraudSignalsTable,
    new RateLimitRepository(_dynamoDb, rateLimitTable));
```

3. Add optional parameter to test constructor:
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
        decimal.TryParse(ppkObj?.ToString(), out pricePerKg);

    var fraudCheck = await _fraudService.CheckListingCreationAsync(
        authenticatedUserId!, cropType, pricePerKg);

    if (!fraudCheck.IsAllowed)
    {
        await _fraudService.RecordFraudSignalAsync(
            authenticatedUserId!, fraudCheck.ErrorCode!, operation.EntityId);
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
        await _fraudService.RecordFraudSignalAsync(
            authenticatedUserId!, fraudCheck.FlagReason!, operation.EntityId);
        context.Logger.LogWarning(
            $"Fraud flag on listing {operation.EntityId}: {fraudCheck.FlagReason}");
    }
}
```

5. After successful `_cropRepo.CreateAsync()`:
```csharp
if (createResult.IsSuccess && _fraudService != null)
{
    await _fraudService.RecordListingCreatedAsync(authenticatedUserId!);
}
```

### 5.2 SyncFunction — Delivery GPS Check (Rule 4)

**File**: `server/src/Functions/SyncFunction.cs`
**Method**: `ProcessDeliveryAsync`
**Location**: When delivery status transitions to `delivered`

```csharp
if (_fraudService != null && operation.Data != null)
{
    double? pickupLat = null, pickupLng = null;
    double? dropoffLat = null, dropoffLng = null;
    string fromWoreda = "", toWoreda = "";

    // Extract GPS coordinates
    if (operation.Data.TryGetValue("gpsPickup", out var gpObj)
        && gpObj is Dictionary<string, object> gp)
    {
        if (gp.TryGetValue("latitude", out var lat)) pickupLat = Convert.ToDouble(lat);
        if (gp.TryGetValue("longitude", out var lng)) pickupLng = Convert.ToDouble(lng);
    }
    if (operation.Data.TryGetValue("gpsDropoff", out var gdObj)
        && gdObj is Dictionary<string, object> gd)
    {
        if (gd.TryGetValue("latitude", out var lat)) dropoffLat = Convert.ToDouble(lat);
        if (gd.TryGetValue("longitude", out var lng)) dropoffLng = Convert.ToDouble(lng);
    }

    fromWoreda = existingDelivery?["fromWoreda"]?.ToString() ?? "";
    toWoreda = existingDelivery?["toWoreda"]?.ToString() ?? "";

    var gpsCheck = _fraudService.CheckGpsConsistency(
        pickupLat, pickupLng, dropoffLat, dropoffLng, fromWoreda, toWoreda);

    if (gpsCheck.IsFlagged)
    {
        operation.Data["gpsAnomalyFlag"] = true;
        await _fraudService.RecordFraudSignalAsync(
            authenticatedUserId!, "GPS_ANOMALY", operation.EntityId);
        context.Logger.LogWarning(
            $"GPS anomaly on delivery {operation.EntityId}: {gpsCheck.Message}");
    }
}
```

### 5.3 EscrowFunction — Payment Velocity (Rule 3)

**File**: `server/src/Functions/EscrowFunction.cs`
**Method**: `HandleInitiateAsync`

**Changes**:

1. Add `IFraudDetectionService?` field:
```csharp
private readonly IFraudDetectionService? _fraudService;
```

2. Initialize in default constructor (after `_escrowService`):
```csharp
var cropsTable = Environment.GetEnvironmentVariable("CROPS_TABLE") ?? "";
var fraudSignalsTable = Environment.GetEnvironmentVariable("FRAUD_SIGNALS_TABLE") ?? "";
var rateLimitTable = Environment.GetEnvironmentVariable("RATE_LIMITS_TABLE") ?? "";
if (!string.IsNullOrEmpty(rateLimitTable))
{
    _fraudService = new FraudDetectionService(
        dynamoDb, cropsTable, fraudSignalsTable,
        new RateLimitRepository(dynamoDb, rateLimitTable));
}
```

3. In `HandleInitiateAsync`, after `userId` extraction:
```csharp
if (_fraudService != null)
{
    var fraudCheck = await _fraudService.CheckPaymentVelocityAsync(userId);
    if (!fraudCheck.IsAllowed)
    {
        await _fraudService.RecordFraudSignalAsync(
            userId, fraudCheck.ErrorCode!, "escrow-velocity");
        return LambdaResponse.Error(429, fraudCheck.ErrorCode!, fraudCheck.Message!);
    }
}
```

4. After successful initiation:
```csharp
if (_fraudService != null && result.IsSuccess)
{
    await _fraudService.RecordPaymentInitiatedAsync(userId);
}
```

---

## 6. Mobile-Side Design

### 6.1 No Isar Schema Changes

Fraud detection is server-side only. The mobile app needs only:
1. Handle new error codes from sync responses
2. Optionally display client-side advisory warnings (velocity, duplicate)

### 6.2 Error Code Handling

The server returns these new error codes:

| Error Code | HTTP Status | User Action |
|------------|-------------|-------------|
| `FRAUD_DUPLICATE_LISTING` | 400 (via sync `error`) | Edit existing listing instead |
| `FRAUD_VELOCITY_EXCEEDED` | 429 (sync `error` or HTTP 429) | Wait and retry |

In the sync error handler (`SyncExecutor` or per-screen error handling):

```dart
// When sync returns status: "error"
if (message.contains('FRAUD_DUPLICATE_LISTING')) {
  showError(context.l10n.fraudDuplicateListing);
} else if (message.contains('FRAUD_VELOCITY_EXCEEDED')) {
  showError(context.l10n.fraudVelocityExceeded);
}
```

**Flagged operations** (price anomaly, GPS anomaly) do NOT return errors. They succeed normally. The mobile app does not need to handle flags.

### 6.3 Client-Side Advisory Pre-Checks (Optional Enhancement)

For better UX, the mobile app can add lightweight pre-checks in `CropListingRepository.createListing()`:

```dart
// Advisory duplicate check (before enqueuing sync)
final recentListings = await _isar.cropListings
    .filter()
    .farmerIdEqualTo(farmerId)
    .cropTypeEqualTo(cropType)
    .createdAtGreaterThan(DateTime.now().subtract(const Duration(hours: 1)))
    .findAll();

if (recentListings.isNotEmpty) {
  // Show warning dialog — user can proceed anyway
  // Server is authoritative, this is just UX
}
```

This is advisory only — server-side checks are the enforcement layer.

### 6.4 Localization Keys

Add to all three `.arb` files:

| Key | English | Amharic | Afan Oromo |
|-----|---------|---------|------------|
| `fraudDuplicateListing` | "A similar listing already exists. Please update your existing listing instead." | "ተመሳሳይ ዝርዝር አስቀድሞ አለ። እባክዎ ያለውን ዝርዝር ያዘምኑ።" | "Tarreen walfakkaataan duraan jira. Maaloo tarree jiru haaromsi." |
| `fraudVelocityExceeded` | "Too many requests. Please wait a moment and try again." | "በጣም ብዙ ጥያቄዎች። እባክዎ ትንሽ ይጠብቁ እና ደግመው ይሞክሩ።" | "Gaaffiin baay'een ergameera. Maaloo yeroo xiqqoo eegaatii irra deebi'ii yaali." |

### 6.5 Battery/Data Impact

- **Zero additional network calls**: Fraud detection piggybacks on existing sync and escrow endpoints
- **No background processing**: No new timers, workers, or periodic tasks on mobile
- **Minimal Isar impact**: Optional advisory check is a single indexed query (<1ms)

---

## 7. Infrastructure Changes

### 7.1 CDK Changes — New GSIs on CropsTable

Add to `infra/lib/stacks/database-stack.ts` after existing `StatusCreatedAtIndex`:

```typescript
// F-14: Fraud detection — duplicate listing detection
this.cropsTable.addGlobalSecondaryIndex({
  indexName: 'FarmerIdCreatedAtIndex',
  partitionKey: { name: 'farmerId', type: dynamodb.AttributeType.STRING },
  sortKey: { name: 'createdAt', type: dynamodb.AttributeType.STRING },
  projectionType: dynamodb.ProjectionType.INCLUDE,
  nonKeyAttributes: ['cropType', 'pricePerKg', 'deletedAt'],
});

// F-14: Fraud detection — price anomaly statistical check
this.cropsTable.addGlobalSecondaryIndex({
  indexName: 'CropTypeCreatedAtIndex',
  partitionKey: { name: 'cropType', type: dynamodb.AttributeType.STRING },
  sortKey: { name: 'createdAt', type: dynamodb.AttributeType.STRING },
  projectionType: dynamodb.ProjectionType.INCLUDE,
  nonKeyAttributes: ['pricePerKg', 'deletedAt'],
});
```

### 7.2 CDK Changes — New FraudSignalsTable

Add to `infra/lib/stacks/database-stack.ts`:

```typescript
// F-14: Fraud signals for risk scoring
public readonly fraudSignalsTable: dynamodb.Table;

// In constructor:
const fraudSignals = new WeredaDynamoTable(this, 'FraudSignalsTable', {
  tableName: `${prefix}-fraud-signals`,
  partitionKey: { name: 'userId', type: dynamodb.AttributeType.STRING },
  sortKey: { name: 'signalKey', type: dynamodb.AttributeType.STRING },
  isProd: envConfig.isProd,
});
this.fraudSignalsTable = fraudSignals.table;

// Enable TTL for 30-day auto-cleanup
(this.fraudSignalsTable.node.defaultChild as dynamodb.CfnTable).addPropertyOverride(
  'TimeToLiveSpecification',
  { AttributeName: 'expiresAt', Enabled: true },
);

// Output
new cdk.CfnOutput(this, 'FraudSignalsTableName', {
  value: this.fraudSignalsTable.tableName,
  description: 'DynamoDB Fraud Signals Table (F-14)',
});
```

### 7.3 CDK Changes — Lambda Environment Variables

Add to `api-stack.ts` for SyncFunction and EscrowFunction Lambda definitions:

```typescript
environment: {
  // ... existing env vars ...
  FRAUD_SIGNALS_TABLE: databaseStack.fraudSignalsTable.tableName,
}
```

Grant DynamoDB permissions:
```typescript
databaseStack.fraudSignalsTable.grantReadWriteData(syncFunction);
databaseStack.fraudSignalsTable.grantReadWriteData(escrowFunction);
```

### 7.4 No New Lambda Functions

All fraud detection runs inline within existing Lambda functions:
- `SyncFunction` — listing and delivery fraud checks
- `EscrowFunction` — payment velocity check

No EventBridge rules or scheduled jobs needed for MVP. Risk score is computed on-read.

---

## 8. AOT Compliance

### 8.1 DTO Registration

Add to `server/src/AppJsonContext.cs`:

```csharp
[JsonSerializable(typeof(FraudCheckResult))]
```

### 8.2 No Reflection Used

- `FraudDetectionService` uses direct DynamoDB `QueryRequest` / `QueryResponse`
- `FraudCheckResult` is a sealed record with static factory methods
- Haversine uses `System.Math`
- Velocity tracking reuses `IRateLimitRepository` (already AOT-safe)
- Risk scoring uses manual DynamoDB marshalling (same as `CropRepository`)

### 8.3 No New NuGet Packages

All logic uses:
- `AWSSDK.DynamoDBv2` (already referenced)
- `System.Math` (BCL)
- `System.Linq` (already used)

---

## 9. API Contract Changes

### 9.1 Sync Response — New Error Codes

The sync response `SyncOperationResult.Message` field can now contain:

| Error Code | Meaning | Client Action |
|------------|---------|---------------|
| `FRAUD_DUPLICATE_LISTING` | Same farmer+crop+similar price within 1h | Show message, suggest editing |
| `FRAUD_VELOCITY_EXCEEDED` | Too many listings per hour | Show message, suggest waiting |

These use the existing `status: "error"` response format — no schema changes.

### 9.2 Escrow Endpoint — New Error

`POST /escrow/initiate` can now return HTTP 429:

```json
{
  "error": "FRAUD_VELOCITY_EXCEEDED",
  "message": "Maximum 10 payment initiations per day exceeded."
}
```

### 9.3 No New Endpoints

Fraud detection does not expose any new REST endpoints. All checks are internal to existing endpoints. Risk scores are internal server-side data — not exposed to clients.

---

## 10. Performance Considerations

| Operation | DynamoDB Cost | Latency Impact |
|-----------|--------------|----------------|
| Velocity check (GetItem) | 0.5 RCU | ~5ms |
| Duplicate detection (GSI Query) | 1-5 RCU | ~10-20ms |
| Price anomaly (GSI Query, 50 items) | 5-10 RCU | ~20-30ms |
| GPS consistency | 0 RCU (pure computation) | <1ms |
| Record fraud signal (PutItem) | 1 WCU | ~5ms |
| **Total per listing creation** | **~7-16 RCU + 1 WCU** | **~40-60ms added** |
| Payment velocity check | 0.5 RCU | ~5ms |

Acceptable for the sync endpoint which processes 1-5 operations per batch typically.

### Optimizations

- GSI projections are minimal (`INCLUDE`) to reduce read costs
- Velocity checks run first (cheapest) — if rejected, skip expensive GSI queries
- `ScanIndexForward = false` on price anomaly ensures newest prices first
- Future: cache recent price statistics in Lambda container memory (reuse)

---

## 11. Testing Strategy

### 11.1 Unit Tests

```
server/tests/Services/FraudDetectionServiceTests.cs ← NEW
```

| Test | Method | Assertion |
|------|--------|-----------|
| Velocity under limit | `CheckListingCreationAsync` | Returns `Allowed()` |
| Velocity at limit | `CheckListingCreationAsync` | Returns `Rejected("FRAUD_VELOCITY_EXCEEDED")` |
| Duplicate exact match | `CheckListingCreationAsync` | Returns `Rejected("FRAUD_DUPLICATE_LISTING")` |
| Duplicate within 5% price | `CheckListingCreationAsync` | Returns `Rejected("FRAUD_DUPLICATE_LISTING")` |
| Duplicate beyond 5% price | `CheckListingCreationAsync` | Returns `Allowed()` |
| Duplicate different cropType | `CheckListingCreationAsync` | Returns `Allowed()` |
| Duplicate deleted listing | `CheckListingCreationAsync` | Returns `Allowed()` (excluded) |
| Price anomaly 4 sigma | `CheckListingCreationAsync` | Returns `Flagged("PRICE_ANOMALY")` |
| Price anomaly within 3 sigma | `CheckListingCreationAsync` | Returns `Allowed()` |
| Price anomaly < 5 samples | `CheckListingCreationAsync` | Returns `Allowed()` (skipped) |
| Payment velocity under limit | `CheckPaymentVelocityAsync` | Returns `Allowed()` |
| Payment velocity at limit | `CheckPaymentVelocityAsync` | Returns `Rejected(...)` |
| GPS inter-woreda < 1km | `CheckGpsConsistency` | Returns `Flagged("GPS_ANOMALY")` |
| GPS same woreda < 1km | `CheckGpsConsistency` | Returns `Allowed()` |
| GPS identical coordinates | `CheckGpsConsistency` | Returns `Flagged("GPS_ANOMALY")` |
| GPS missing coordinates | `CheckGpsConsistency` | Returns `Allowed()` (skipped) |
| GPS normal delivery | `CheckGpsConsistency` | Returns `Allowed()` |
| Risk score computation | `GetUserRiskScoreAsync` | Correct weighted sum |
| Risk score decay | `GetUserRiskScoreAsync` | Expired signals excluded |
| Record fraud signal | `RecordFraudSignalAsync` | DynamoDB PutItem called |

### 11.2 Integration Tests

```
server/tests/Functions/SyncFunctionFraudTests.cs ← NEW
```

| Test | Scenario |
|------|----------|
| Listing creation with fraud rejection | Full sync flow → error response |
| Listing creation with price flag | Full sync flow → success with isFlagged |
| Payment with velocity rejection | Escrow initiate → 429 response |
| Delivery with GPS anomaly | Delivery sync → success with gpsAnomalyFlag |
| Multiple signals accumulate risk | Create signals → compute score |

### 11.3 Mobile Widget Tests

```
mobile/test/features/crop_listing/presentation/widgets/fraud_error_test.dart ← NEW
```

| Test | Scenario |
|------|----------|
| Duplicate listing error displayed | Mock sync error → verify localized message shown |
| Velocity exceeded error displayed | Mock sync error → verify localized message shown |
| Normal creation unaffected | No fraud error → success flow unchanged |

### 11.4 Mock Strategy

- `IAmazonDynamoDB` — mock with NSubstitute (existing pattern)
- `IRateLimitRepository` — mock with NSubstitute
- `IFraudDetectionService` — mock in `SyncFunction` and `EscrowFunction` tests
- DynamoDB GSI queries — mock `QueryAsync` responses

### 11.5 Edge Cases

| Scenario | Expected Behavior |
|----------|-------------------|
| Offline farmer creates 10 listings, syncs all at once | Server rejects after 5th (velocity), remaining get error status |
| Two devices sync same listing simultaneously | Idempotency key prevents double-processing; fraud check runs once |
| Price database is empty (new crop type) | Price anomaly check skipped (< 5 samples) |
| GPS unavailable in rural area | GPS check skipped, operation proceeds |
| Farmer with HIGH risk creates listing | Listing still created but flagged for admin review |

---

## 12. File Structure

### Server (New Files)

```
server/src/Services/
├── IFraudDetectionService.cs       ← NEW (interface)
├── FraudDetectionService.cs        ← NEW (implementation)

server/src/Models/
├── FraudDtos.cs                    ← NEW (FraudCheckResult)

server/tests/Services/
├── FraudDetectionServiceTests.cs   ← NEW

server/tests/Functions/
├── SyncFunctionFraudTests.cs       ← NEW
```

### Server (Modified Files)

```
server/src/Functions/SyncFunction.cs     ← ADD fraud checks
server/src/Functions/EscrowFunction.cs   ← ADD payment velocity
server/src/AppJsonContext.cs             ← REGISTER FraudCheckResult
```

### Infrastructure (Modified Files)

```
infra/lib/stacks/database-stack.ts       ← ADD 2 GSIs + FraudSignalsTable
infra/lib/stacks/api-stack.ts            ← ADD FRAUD_SIGNALS_TABLE env var
```

### Mobile (Modified Files)

```
mobile/lib/l10n/app_en.arb               ← ADD 2 fraud error strings
mobile/lib/l10n/app_am.arb               ← ADD 2 fraud error strings
mobile/lib/l10n/app_om.arb               ← ADD 2 fraud error strings
```

### Mobile (New Files — Optional)

```
mobile/test/features/crop_listing/presentation/widgets/fraud_error_test.dart ← NEW
```

---

## 13. Implementation Effort Estimate

| Component | Effort | Priority |
|-----------|--------|----------|
| `FraudDetectionService` + interface + DTOs | ~4h | P0 (core) |
| SyncFunction integration (Rules 1-4) | ~3h | P0 |
| EscrowFunction integration (Rule 3) | ~1h | P0 |
| CDK: 2 GSIs + FraudSignalsTable | ~1h | P0 |
| Unit tests (FraudDetectionServiceTests) | ~3h | P0 |
| Integration tests (SyncFunctionFraudTests) | ~2h | P1 |
| Mobile: error handling + l10n strings | ~1h | P1 |
| Mobile: advisory pre-checks (optional) | ~1h | P2 |
| Risk scoring (Rule 5) | ~2h | P2 |
| **Total** | **~18h** | |

---

## Changelog

| Version | Date | Change |
|---------|------|--------|
| 2.0.0 | 2026-02-23 | Full architecture: 5 rules, risk scoring, FraudSignalsTable, mobile integration, testing strategy |
| 1.0.0 | 2026-02-21 | Initial design: 4 rules, interface + DTO |

---

**Cross-references**:
- Decision record: `specs/decisions/f14-fraud-detection-architecture.md`
- Crop listing architecture: `specs/architecture/crop-listing-architecture.md`
- Delivery verification: `specs/architecture/delivery-verification-architecture.md`
- Escrow payment: `specs/architecture/escrow-payment-architecture.md`
- Sync protocol: `specs/contracts/sync-protocol.md`

**Last Updated**: 2026-02-23
**Author**: Architecture Agent
**Status**: Design — Ready for Implementation
