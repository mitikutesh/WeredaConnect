# Fraud Detection System Architecture (F-14)

## Version
3.0.0 | 2026-10-06

## Status
Rules 1 to 4 and signal persistence implemented. Risk engine (sections 14 to 23) specified, not built (EPIC FRD).

---

## 1. Overview & Threat Model

F-14 adds fraud detection to WeredaConnect, targeting five categories of abuse that threaten the platform's trust model, which matters most in Ethiopian rural communities where users have limited legal recourse. Rules 1 to 4 are live. The account-level risk engine (T6) is specified in sections 14 to 23 and tracked as EPIC FRD in `TASK.md`.

### 1.1 Threat Scenarios

| # | Threat | Actor | Impact | Detection |
|---|--------|-------|--------|-----------|
| T1 | **Listing spam** | Malicious farmer | Floods marketplace, degrades browse UX | Velocity limits (Rule 3) |
| T2 | **Duplicate listings** | Farmer (accidental or intentional) | Confuses buyers, inflates supply | Duplicate detection (Rule 1) |
| T3 | **Price manipulation** | Bad actor | Distorts market prices, enables scams | Price anomaly flagging (Rule 2) |
| T4 | **GPS spoofing** | Trucker or colluding parties | Fake deliveries to claim escrow funds | GPS consistency check (Rule 4) |
| T5 | **Payment flooding** | Bot or compromised account | Drain escrow system, DoS on Telebirr | Payment velocity limits (Rule 3) |
| T6 | **Account abuse patterns** | Repeat offenders | Sustained low-level fraud across rules | Risk score and tiers (sections 14 to 17; planned, FRD-02 to FRD-04) |

### 1.2 Design Principles

- **Server-side enforcement**: All fraud rules run on the server; mobile is untrusted
- **No new DynamoDB tables** for rules 1-4: Reuse `CropsTable` (2 new GSIs) and `RateLimitsTable`
- **One new table** for Rule 5: `FraudSignalsTable` for persistent signal tracking
- **AOT-compatible**: No reflection, all DTOs in `AppJsonContext`
- **Soft vs hard checks**: Soft checks (flag) never block operations; hard checks (reject) return typed error codes
- **Offline-first compatible**: Mobile pre-checks are advisory only; server is authoritative
- **Minimal mobile impact**: No Isar schema changes; fraud detection is transparent to users (FRD-06 appends one enum value, `held`, see section 18.7)

---

## 2. Fraud Rules (Detailed Design)

### Rule 1: Duplicate Listing Detection

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

**Error**: `FRAUD_DUPLICATE_LISTING` with the message "A similar listing already exists. Please update your existing listing instead."

**Edge cases**:
- Deleted listings (`deletedAt` set) excluded via FilterExpression
- Price tolerance uses percentage (not absolute) to handle different crop price ranges (teff ~45 ETB/kg vs wheat ~20 ETB/kg)
- If `pricePerKg` is 0, skip price comparison and match only on `cropType`

### Rule 2: Price Anomaly Flagging

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
- Computed in C# using `Math.Sqrt`, no external libraries

### Rule 3: Velocity Limits

**Trigger A, listing creation**: Before `_cropRepo.CreateAsync()`
**Trigger B, payment initiation**: Before `_escrowService.InitiateAsync()` in `EscrowFunction.HandleInitiateAsync`, and before the Telebirr preOrder in `EscrowFunction.HandleOrderCheckoutAsync` (escrow-protocol 1.5.0)

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
1. Key = "FRAUD_PAYMENT#{userId}#{currentHourBucket}"
   where currentHourBucket = DateTime.UtcNow.ToString("yyyy-MM-ddTHH")
2. Get count from RateLimitsTable
3. If count >= 5 → REJECT
4. After successful initiation, increment counter (TTL = 3600s)
```

**Error (listings)**: sync operation `status: "error"` with `message` prefixed `FRAUD_VELOCITY_EXCEEDED:`.

**Error (payments)**: `429 ESCROW_011` on `POST /escrow/initiate` and `POST /escrow/order-checkout` (the contract code the clients key on). Initiate also records a `FRAUD_VELOCITY_EXCEEDED` signal; order-checkout does not. Version 2.0.0 of this document said 10 per day and `FRAUD_VELOCITY_EXCEEDED` on the wire; the code (`FraudDetectionService.MaxPaymentsPerHour`) and `escrow-protocol.md` say 5 per hour and `ESCROW_011`.

**Reuse**: Existing `IRateLimitRepository` with `GetAsync` / `IncrementAsync`, the same atomic counter pattern as auth rate limiting.

### Rule 4: GPS Consistency Check

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

**Note**: Full bounding-box per woreda deferred: the existing 5km delivery validation (F-12) already catches gross mismatches. This rule catches more subtle spoofing patterns.

### Rule 5: Account-Level Risk Score (not implemented; see sections 14 to 17)

Version 2.0.0 of this document specified a weighted 30-day score with four tiers and a
`UserRiskScores` attribute on `UsersTable`. None of it was built: the service writes signal
rows (section 4.3) and nothing reads them back. The score is now specified in section 14
(formula, bounded read, failure modes), section 16 (tiers) and section 17 (enforcement). It is
computed on read from the fraud-signals table and never stored on the user row. The signal
weights are unchanged (`FraudDetectionService.SignalWeight`: `GPS_ANOMALY` 25,
`FRAUD_DUPLICATE_LISTING` 20, `FRAUD_VELOCITY_EXCEEDED` 15, `PRICE_ANOMALY` 10, any other type 10).

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
│  │  GetUserRiskScoreAsync()  (planned, FRD-02)          │      │
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
// server/src/Services/IFraudDetectionService.cs (as on main; comments abridged)
namespace WeredaConnect.Server.Services;

public interface IFraudDetectionService
{
    // Velocity, duplicate, price anomaly. Call BEFORE _cropRepo.CreateAsync().
    Task<FraudCheckResult> CheckListingCreationAsync(
        string farmerId, string cropType, decimal pricePerKg);

    // Payment velocity. Call BEFORE _escrowService.InitiateAsync().
    Task<FraudCheckResult> CheckPaymentVelocityAsync(string userId);

    // Pure computation, no DB calls.
    FraudCheckResult CheckGpsConsistency(
        double? pickupLat, double? pickupLng,
        double? dropoffLat, double? dropoffLng,
        string fromWoreda, string toWoreda);

    // Persist a signal for admin review and scoring. No-op when the signals
    // table is not configured. Best-effort: callers wrap it in try/catch so a
    // failed write never blocks the operation.
    Task RecordFraudSignalAsync(
        string userId, string signalType, string? entityId,
        string entityType, string? description);

    // Velocity counters. Call AFTER the create or initiation succeeds.
    Task RecordListingCreatedAsync(string farmerId);
    Task RecordPaymentInitiatedAsync(string userId);
}
```

Planned (FRD-02), added to the same interface:

```csharp
// Score, enforcement score, tier (LOW, MEDIUM, HIGH, CRITICAL) and Truncated. Section 14.
Task<RiskScoreResult> GetUserRiskScoreAsync(string userId);
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
    internal const int MaxPaymentsPerHour = 5;          // escrow-protocol.md: 5 initiations per buyer per hour
    internal const int PaymentVelocityTtlSeconds = 3600;
    internal const double DuplicateListingWindowHours = 1.0;
    internal const double PriceTolerancePercent = 0.05;
    internal const int PriceAnomalyMinSamples = 5;
    internal const int PriceAnomalySampleSize = 50;
    internal const double PriceAnomalyStdDevMultiplier = 3.0;
    internal const double GpsSpoofingThresholdKm = 1.0;
    internal const int SignalTtlDays = 30;

    // Signal weights live in SignalWeight(signalType):
    //   GPS_ANOMALY 25, FRAUD_DUPLICATE_LISTING 20, FRAUD_VELOCITY_EXCEEDED 15,
    //   PRICE_ANOMALY 10, anything else 10.
    // Risk tiers, credit cap and window: planned, section 14.5 (RISK_* env overrides).

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

    // CheckGpsConsistency is pure computation, no DB calls.

    // RecordFraudSignalAsync writes to FraudSignalsTable:
    //   PK: userId, SK: timestamp#signalType
    //   TTL: 30 days from now (expiresAt, epoch seconds)

    // GetUserRiskScoreAsync (planned, FRD-02) queries FraudSignalsTable:
    //   PK = userId, SK >= ISO(now minus RISK_WINDOW_DAYS), newest first,
    //   hourly dedup per type, early exit, page cap. Section 14.

    // Implementation: server/src/Services/FraudDetectionService.cs
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
| Listing velocity count | RateLimitsTable | Primary key | `pk = "FRAUD_LISTING#farmerId#hour"` | none |
| Payment velocity count | RateLimitsTable | Primary key | `pk = "FRAUD_PAYMENT#userId#hour"` | none |

### 4.2 New GSIs on CropsTable

#### GSI: FarmerIdCreatedAtIndex

| Property | Value |
|----------|-------|
| **Partition key** | `farmerId` (String) |
| **Sort key** | `createdAt` (String, ISO 8601) |
| **Projection** | `INCLUDE`: `cropType`, `pricePerKg`, `deletedAt` |
| **Purpose** | Duplicate listing detection (Rule 1) |

#### GSI: CropTypeCreatedAtIndex

| Property | Value |
|----------|-------|
| **Partition key** | `cropType` (String) |
| **Sort key** | `createdAt` (String, ISO 8601) |
| **Projection** | `INCLUDE`: `pricePerKg`, `deletedAt` |
| **Purpose** | Price anomaly statistical check (Rule 2) |

### 4.3 New DynamoDB Table: FraudSignalsTable (Rule 5)

| Property | Value |
|----------|-------|
| **Table name** | `wereda-connect-{env}-fraud-signals` |
| **Partition key** | `userId` (String) |
| **Sort key** | `signalKey` (String), format `{ISO timestamp}#{signalType}` (the whitelist row uses `~whitelist`, section 20) |
| **TTL attribute** | `expiresAt` (Number, epoch seconds): auto-deletes rows after 30 days. Never renamed on the live table (section 20) |
| **Billing** | PAY_PER_REQUEST |

**Attributes per item** (as written by `FraudDetectionService.RecordFraudSignalAsync`):
```
userId:      "farmer-123"
signalKey:   "2026-02-23T14:00:00Z#FRAUD_DUPLICATE_LISTING"
signalId:    base64url("{userId}|{signalKey}")   (opaque single id for the admin API path)
signalType:  "FRAUD_DUPLICATE_LISTING"
severity:    "high" | "medium" | "low"           (from weight: 20 and above high, 10 and above medium)
description: "A similar listing already exists…" (human-readable check message)
status:      "pending"                           (becomes "reviewed" after admin action)
entityId:    "crop-456"                          (omitted when unknown)
entityType:  "crop" | "delivery" | "escrow"
weight:      20
createdAt:   "2026-02-23T14:00:00Z"
expiresAt:   1743868800  (Unix epoch, 30 days from creation)
recordType:  absent on rows written today; "signal" | "credit" | "whitelist" from FRD-05 and FRD-10 (section 20)
```

**Admin review semantics**: the admin API addresses a signal by `signalId`
(base64url of `userId|signalKey`) so `POST /admin/fraud-signals/{id}/action`
can reconstruct the composite table key without a scan. The review action
sets `status = "reviewed"` (the admin UI keys on that value) and records the
chosen verb (`approve`/`reject`/`whitelist`) in `resolution`, plus
`reviewedBy`/`reviewedAt`/`reviewNotes`.

**Producers**: signals are recorded best-effort (never blocking the underlying
operation) at three sites: listing creation in `SyncFunction` (hard rejects
`FRAUD_VELOCITY_EXCEEDED`/`FRAUD_DUPLICATE_LISTING` and the soft
`PRICE_ANOMALY` flag), delivery GPS checks in `SyncFunction` (`GPS_ANOMALY`),
and escrow initiation velocity rejections in `EscrowFunction`
(`FRAUD_VELOCITY_EXCEEDED`).

**Why a separate table instead of an attribute on UsersTable**: Individual fraud signals need a time-series query pattern (PK=userId, SK=timestamp) that doesn't fit the UsersTable schema (PK=userId, no SK). Storing signals as a list attribute would require read-modify-write which is not atomic. A separate table allows clean TTL-based expiry and efficient range queries.

**The risk score will be computed on read** (section 14) by summing the counted weights in the window with the dedup and caps of section 14.2. No separate risk score storage: that avoids stale scores and keeps the model simple. Not implemented on main.

### 4.4 No Isar Schema Changes on Mobile

Fraud detection is transparent to the mobile client:
- Fraud rejections arrive as standard sync error codes (`FRAUD_DUPLICATE_LISTING`, `FRAUD_VELOCITY_EXCEEDED`)
- Flagged operations succeed normally: `isFlagged` is server-side metadata only
- No new Isar collections or fields needed. FRD-06 appends `held` to the `PaymentStatus` enum (append only, stored by name; section 18.7)

### 4.5 Velocity Key Design

Velocity keys in `RateLimitsTable` use hour bucketing for natural TTL expiry:

| Key Pattern | Example | TTL |
|-------------|---------|-----|
| `FRAUD_LISTING#{farmerId}#{hourBucket}` | `FRAUD_LISTING#farmer-123#2026-02-21T14` | 3600s |
| `FRAUD_PAYMENT#{userId}#{hourBucket}` | `FRAUD_PAYMENT#buyer-456#2026-02-21T14` | 3600s |

---

## 5. Server-Side Integration

The snippets in this section are the 2.0.0 design and are kept for the rationale; the code is authoritative. Since then `RecordFraudSignalAsync` takes `entityType` and `description`, the sync call sites wrap it in `SyncFunction.TryRecordFraudSignalAsync` (best-effort), and the payment velocity rejection is returned as `429 ESCROW_011`.

### 5.1 SyncFunction: Listing Creation (Rules 1, 2, 3)

**File**: `server/src/Functions/SyncFunction.cs`
**Method**: `ProcessCropListingAsync`, `case "create":` block

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

### 5.2 SyncFunction: Delivery GPS Check (Rule 4)

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

### 5.3 EscrowFunction: Payment Velocity (Rule 3)

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
| `FRAUD_VELOCITY_EXCEEDED` | 429 (sync `error` or HTTP 429 as `ESCROW_011`) | Wait and retry |
| `FRAUD_ACCOUNT_HELD` | 403 (REST) or sync `error`; planned FRD-04 | Show "account under review", do not retry; FRD-06 adds the strings (section 17.3) |

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
  // Show warning dialog, user can proceed anyway
  // Server is authoritative, this is just UX
}
```

This is advisory only: server-side checks are the enforcement layer.

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

### 7.1 CDK Changes: New GSIs on CropsTable

Add to `infra/lib/stacks/database-stack.ts` after existing `StatusCreatedAtIndex`:

```typescript
// F-14: Fraud detection: duplicate listing detection
this.cropsTable.addGlobalSecondaryIndex({
  indexName: 'FarmerIdCreatedAtIndex',
  partitionKey: { name: 'farmerId', type: dynamodb.AttributeType.STRING },
  sortKey: { name: 'createdAt', type: dynamodb.AttributeType.STRING },
  projectionType: dynamodb.ProjectionType.INCLUDE,
  nonKeyAttributes: ['cropType', 'pricePerKg', 'deletedAt'],
});

// F-14: Fraud detection: price anomaly statistical check
this.cropsTable.addGlobalSecondaryIndex({
  indexName: 'CropTypeCreatedAtIndex',
  partitionKey: { name: 'cropType', type: dynamodb.AttributeType.STRING },
  sortKey: { name: 'createdAt', type: dynamodb.AttributeType.STRING },
  projectionType: dynamodb.ProjectionType.INCLUDE,
  nonKeyAttributes: ['pricePerKg', 'deletedAt'],
});
```

### 7.2 CDK Changes: New FraudSignalsTable

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

### 7.3 CDK Changes: Lambda Environment Variables

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

The admin Lambda has the same env and grant. The escrow cron Lambda has neither today; FRD-03 and FRD-04 add them (section 23).

### 7.4 No New Lambda Functions

All fraud detection runs inline within existing Lambda functions:
- `SyncFunction`: listing and delivery fraud checks
- `EscrowFunction`: payment velocity check

No EventBridge rules or scheduled jobs for the rules. The risk score is computed on read (section 14); FRD-15 adds an hourly EventBridge Lambda for relationship patterns.

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

### 9.1 Sync Response: Error Codes

The sync response `SyncOperationResult.Message` field can now contain:

| Error Code | Meaning | Client Action |
|------------|---------|---------------|
| `FRAUD_DUPLICATE_LISTING` | Same farmer+crop+similar price within 1h | Show message, suggest editing |
| `FRAUD_VELOCITY_EXCEEDED` | Too many listings per hour | Show message, suggest waiting |
| `FRAUD_ACCOUNT_HELD` | Account under fraud review (risk tier CRITICAL); planned FRD-04, sync-protocol 1.9.0 | Permanent; show the message; on a payment revert the local order status (section 17.3) |

These use the existing `status: "error"` response format: no schema changes.

### 9.2 Escrow Endpoints: Errors

`POST /escrow/initiate` and `POST /escrow/order-checkout` return HTTP 429 on the payment velocity
limit. The contract code is `ESCROW_011`; the `FRAUD_VELOCITY_EXCEEDED` signal is recorded on
initiate only:

```json
{
  "error": {
    "code": "ESCROW_011",
    "message": "Maximum 5 payment initiations per hour exceeded."
  }
}
```

Planned (FRD-04): both endpoints return `403` with code `FRAUD_ACCOUNT_HELD` when the caller's risk
tier is CRITICAL (section 17.3), and the risk tier can also raise `AUTH_040` or `AUTH_041` on
initiate (section 17).

### 9.3 No New Endpoints

Fraud detection does not expose any new REST endpoints. All checks are internal to existing endpoints. Risk scores and tiers are internal server-side data, never exposed to clients.

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
| Risk read (planned, FRD-02; one Query, normally one page, memoized per user) | 1-5 RCU | ~10-20ms |

Acceptable for the sync endpoint which processes 1-5 operations per batch typically.

### Optimizations

- GSI projections are minimal (`INCLUDE`) to reduce read costs
- Velocity checks run first (cheapest): if rejected, skip expensive GSI queries
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
| Risk score (planned, FRD-02) | `GetUserRiskScoreAsync` | The list in section 14.7 |
| Risk score decay (planned, FRD-10) | `GetUserRiskScoreAsync` | Credits applied, cap and floor |
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
| Multiple signals accumulate risk (planned, FRD-02) | Create signals → compute score, hourly dedup |

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

- `IAmazonDynamoDB`: mock with NSubstitute (existing pattern)
- `IRateLimitRepository`: mock with NSubstitute
- `IFraudDetectionService`: mock in `SyncFunction` and `EscrowFunction` tests
- DynamoDB GSI queries: mock `QueryAsync` responses

### 11.5 Edge Cases

| Scenario | Expected Behavior |
|----------|-------------------|
| Offline farmer creates 12 listings, syncs all at once | Server rejects after 5th (velocity), remaining get error status; the hourly dedup counts those rejections once in the score (section 14.2) |
| Two devices sync same listing simultaneously | Idempotency key prevents double-processing; fraud check runs once |
| Price database is empty (new crop type) | Price anomaly check skipped (< 5 samples) |
| GPS unavailable in rural area | GPS check skipped, operation proceeds |
| Farmer with a HIGH risk tier creates a listing | Allowed (section 17); at CRITICAL the create is refused with `FRAUD_ACCOUNT_HELD` (planned, FRD-04) |

---

## 12. File Structure

Files of the delivered rules. Planned additions are listed per task in section 23.

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

### Mobile (New Files, Optional)

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
| Risk engine (sections 14 to 23) | EPIC FRD, estimated per task in `TASK.md` | P0 to P3 |
| **Total (rules 1 to 4, delivered)** | **~16h** | |

---

## 14. Risk Score (planned, FRD-02)

**Status on main**: not built. `IFraudDetectionService` has no `GetUserRiskScoreAsync`, and nothing reads a signal's `weight` back. Sections 14 to 23 are the specification that EPIC FRD implements; each section names the task that owns it. Where this document and the code disagree, the code describes what is deployed and this document describes what is planned.

### 14.1 Inputs

Rows of the fraud-signals table in one partition (`userId`). A signal row carries `weight` (written by `FraudDetectionService.SignalWeight`), `status` (`pending`, or `reviewed` with `resolution` set to `approve`, `reject` or `whitelist`), `signalKey` (`{ISO timestamp}#{signalType}`) and the TTL attribute `expiresAt`. Section 20 adds `recordType` and the credit and whitelist rows.

### 14.2 Formula

```
window        = rows whose signalKey >= ISO(now minus RISK_WINDOW_DAYS)
counted(row)  = row is a signal row
                AND (row.status = pending OR (row.status = reviewed AND row.resolution = reject))
                AND NOT (whitelist active AND class(row.signalType) = soft)
dedup         = at most one counted row per (signalType, UTC hour bucket of signalKey);
                the first one read (the newest) wins
advisory      = sum of counted AI_ADVISORY weights, capped at RISK_ADVISORY_CAP
credits       = sum of credit-row weights in the window, floored at minus RISK_CREDIT_CAP
score         = max(0, sum of counted non-advisory weights after dedup + advisory + credits)
enforcement   = max(0, score minus advisory)      (the CRITICAL test never uses AI weight, section 21)
```

Rules behind the formula:

- **Confirmed signals keep counting.** A `reject` resolution is the admin agreeing the signal was fraud, so the score must not fall when the admin rejects. `approve` (false positive) and `whitelist` stop the row from counting. Rows without `recordType` are legacy signal rows.
- **Burst dedup.** An offline farmer syncing 12 queued listings gets 7 `FRAUD_VELOCITY_EXCEEDED` rejections in one batch (section 11.5). A plain sum gives 105, CRITICAL for an honest user; the hourly dedup counts 15. Every row is still written and shown to the admin.
- **Floor at 0.** Credits never produce a negative score.
- The window is capped at `SignalTtlDays` (30), because the table's TTL deletes older rows anyway. The key condition, not the lazy TTL, is what bounds the read.

### 14.3 Bounded read

One `Query` on partition key `userId` with key condition `signalKey >= :since`, `ScanIndexForward = false`, eventually consistent. The `~whitelist` row (section 20) sorts after every ISO key and is therefore read first. The scorer walks newest to oldest, applies the dedup as it goes, and stops early once the counted non-credit, non-advisory sum reaches `RISK_TIER_CRITICAL + RISK_CREDIT_CAP`: the tier is CRITICAL whatever remains unread, because credits cannot offset more than the cap. It follows `LastEvaluatedKey` for at most `RISK_MAX_PAGES` pages (default 2; a page is at most 1 MB, roughly 1,500 rows at about 600 bytes each).

If rows remain after the page cap the result is `truncated`, the tier is CRITICAL and the log carries the alarm token `RISK_SCORE_TRUNCATED`. This fails closed on purpose: reading newest first, a burst of same-type rows in the current hour (deduplicated to one) could otherwise push older weight out of the read. Honest users cannot reach the cap, since every row needs a rejected or flagged action and the mobile client never retries `FRAUD_*` sync errors. FRD-08's per-user, per-type write rate limit makes the cap unreachable short of deliberate abuse, so FRD-08 is not a prerequisite for FRD-02.

The result is memoized per user per invocation: a sync batch or a settlement sweep reads each user once.

### 14.4 Failure modes

| Condition | At a user-facing gate | In a settlement sweep |
|-----------|-----------------------|-----------------------|
| Signals table not configured (`FRAUD_SIGNALS_TABLE` empty) | LOW (today's no-op) | LOW |
| `Query` throws | LOW, log alarm token `RISK_SCORE_UNAVAILABLE`; the baseline payment step-up (section 17) still applies | Skip this escrow for this run: no hold, no money moves; retried next hour |
| Read truncated (section 14.3) | CRITICAL | CRITICAL (hold, section 18) |

### 14.5 Configuration

Constants live in `FraudDetectionService` with environment overrides, the same pattern `StepUpPolicy` uses for `STEP_UP_THRESHOLD_ETB` and `STEP_UP_MAX_AGE_SECONDS`.

| Variable | Default | Meaning |
|----------|---------|---------|
| `RISK_WINDOW_DAYS` | 30 | Scoring window; capped at `SignalTtlDays` |
| `RISK_TIER_MEDIUM` | 30 | Lower bound of MEDIUM |
| `RISK_TIER_HIGH` | 60 | Lower bound of HIGH |
| `RISK_TIER_CRITICAL` | 100 | Lower bound of CRITICAL |
| `RISK_CREDIT_CAP` | 30 | Maximum total credit per window |
| `RISK_ADVISORY_CAP` | 10 | Maximum total AI advisory weight per window |
| `RISK_MAX_PAGES` | 2 | Query pages read before the result is `truncated` |
| `FRAUD_WHITELIST_DAYS` | 30 | Default whitelist duration (section 19) |
| `FRAUD_HOLD_ENABLED` | false | Whether a sweep may write `held` (section 18) |

### 14.6 Cost

One `Query`, normally one page, eventually consistent: about 1 to 5 RCU and 10 to 20 ms per user per invocation. No new table, no write on read, no cached score to go stale.

### 14.7 Tests FRD-02 owns

Tier boundaries 29/30, 59/60 and 99/100; burst dedup; whitelist row read first and soft weights skipped; rows before the window excluded; credit cap; a `reject` resolution still counts and an `approve` does not; early exit; page cap and truncation to CRITICAL; table unset gives LOW; `Query` error gives LOW with the alarm token; one `Query` per user per batch.

---

## 15. Signal Catalogue

| Type | Weight | Class | Producer | Status |
|------|--------|-------|----------|--------|
| `GPS_ANOMALY` | 25 | soft | `SyncFunction.TryRecordFraudSignalAsync` on a delivery `delivered` update (`CheckGpsConsistency`) | live |
| `FRAUD_DUPLICATE_LISTING` | 20 | hard | `SyncFunction.TryRecordFraudSignalAsync` on listing create | live |
| `FRAUD_VELOCITY_EXCEEDED` | 15 | hard | Listing create (`SyncFunction`) and `POST /escrow/initiate` (`EscrowFunction.HandleInitiateAsync`, returned as `429 ESCROW_011`). `POST /escrow/order-checkout` applies the same hourly limit but writes no signal | live |
| `PRICE_ANOMALY` | 10 | soft | Listing create | live |
| any other type | 10 | hard | `SignalWeight` default branch | live |
| `CLEAN_DELIVERY` | minus 5 | credit | Escrow cron, auto-release with no dispute, hold or signal (section 14.2) | planned FRD-10 |
| `AI_ADVISORY` | per review, capped by `RISK_ADVISORY_CAP` in total | advisory | Bedrock reviewer (section 21) | planned FRD-19 |
| Rejects from existing guards: create conflict on another user's id, role 403s, identity or money field mutation attempts, stale-version replays, repeated QR handoff failures, webhook signature failure or payer mismatch, requests with a suspended user's token | per type, set by the task | hard | FRD-08 (also adds the per-user, per-type write rate limit) | planned FRD-08 |
| Transaction patterns: amount above 3 sigma of the user's history, new account with a large amount, repeated amounts just under 10,000 ETB, cancel-after-fund rate, per-buyer dispute rate, delivery time impossible for the distance | per rule | soft or hard per rule | FRD-09 | planned FRD-09 |
| `NO_SHOW`, `DELIVERY_OVERDUE`, buyer griefing (three declined paid orders in 30 days) | per type | hard | ESC-33 | planned ESC-33 |
| Relationship patterns: repeated farmer and buyer pairs, same device or IP on both sides, confirm-then-dispute, self-trading | per type | hard | FRD-15 hourly Lambda | planned FRD-15 |

Class semantics: **soft** is a flag-only rule whose weight an active whitelist skips; **hard** is a rejection or a money signal and always counts; **credit** is negative and capped; **advisory** is AI output, capped and excluded from the enforcement score. Each planned task adds its rows to this table, with weight and class, in the same PR as the producer.

Attribution: today every signal is written against the authenticated caller of the operation. ESC-33 changes `GPS_ANOMALY` to be attributed to the trucker who moved the goods rather than to whoever sent the confirming update.

---

## 16. Risk Tiers

| Tier | Enforcement score | Effect (section 17) |
|------|-------------------|---------------------|
| LOW | 0 to 29 | No change from today |
| MEDIUM | 30 to 59 | Fresh password on every payment, after the Fayda login above 10,000 ETB as today |
| HIGH | 60 to 99 | Fresh Fayda login and then a fresh password on every payment; FRD-13 payout delay |
| CRITICAL | 100 and above, or a truncated read | New listings, trips, orders and payments refused with `FRAUD_ACCOUNT_HELD`; settlement of the party's escrows held for an admin |

Bounds are inclusive and configurable (section 14.5). The risk tier is unrelated to the reputation tiers of the three-party escrow design (`specs/features/three-party-escrow-commitments-reputation.md`) and to the Telebirr hold; this document says "risk tier" throughout.

---

## 17. Enforcement Matrix and Gate Order (planned, FRD-04)

Subject of the check: the caller at a user action; every party (buyer, farmer, and the trucker when present) at settlement. The tier and the score are never returned to clients.

| Action | LOW | MEDIUM | HIGH | CRITICAL |
|--------|-----|--------|------|----------|
| Buyer payment up to and including 10,000 ETB (`/sync` order `payment_submitted`, `POST /escrow/initiate`) | `AUTH_040` when the password is not fresh (today) | same as LOW | `AUTH_041`, then `AUTH_040` | `FRAUD_ACCOUNT_HELD` |
| Buyer payment above 10,000 ETB | `AUTH_041` (today) | `AUTH_041`, then `AUTH_040` | `AUTH_041`, then `AUTH_040` | `FRAUD_ACCOUNT_HELD` |
| `POST /escrow/order-checkout` (ESC-01) | The gates already ran at `payment_submitted` | same | same | `403 FRAUD_ACCOUNT_HELD`, no Telebirr preOrder call |
| Create crop (farmer), trip (trucker), order (buyer) through `/sync` | allowed | allowed | allowed | `FRAUD_ACCOUNT_HELD` |
| Cron release, refund and dispute settlement | as today | as today, tier in the audit metadata | as today, tier in the audit metadata (FRD-13 adds the payout delay) | escrow moves to `held` (section 18); no money moves |
| Admin `POST /escrow/release` and `POST /escrow/refund` | not gated (the admin decides); tier in the audit metadata | same | same | same |

Rules:

- **A gate holds nothing.** At a user action CRITICAL only refuses that action. The only place the system writes `held` is a settlement sweep (section 18).
- **Fayda before the password at HIGH.** The Fayda round trip takes minutes and the password seconds, so asking for Fayda first keeps both proofs inside the 300 second window. The client already handles the pair: one prompt per batch, Fayda first, then the password-tier operations ask again on retry.
- **`AUTH_041` text becomes amount-neutral** (`StepUpPolicy.FaydaErrorMessage` and the app string `faydaStepUpMessage` are amount-specific today). Clients key on the code, never on the text.
- **Not gated**: disputes (the existing `AUTH_040` rule is unchanged), delivery and logistics updates, messages, ratings, order accept and reject, profile updates.
- **Known gap, kept**: the counterparty's tier is not checked at order create. The settlement hold covers the money, and revealing a counterparty's tier would leak a fraud flag.
- **HIGH at payout keeps today's money behaviour** because no user is present to step up; FRD-13 owns the payout delay. The reviewer of FRD-04 may tighten HIGH at payout to `held`.

### 17.1 Gate order at user actions

| Path | Order |
|------|-------|
| `/sync` order `payment_submitted` (`SyncFunction`) | transition auth (`ValidateOrderTransitionAuth`), risk read, CRITICAL check, KYC (`CheckPaymentKycAsync`, unchanged), tiered step-up (`StepUpPolicy.CheckPayment` extended with the tier), the live-gateway order total check (`ORDER_VALIDATION`, unchanged), then the write and the mock auto-fund |
| `POST /escrow/initiate` (`EscrowFunction.HandleInitiateAsync`) | role check (`ESCROW_025`), risk read, CRITICAL check, tiered step-up, velocity (`ESCROW_011`) |
| `POST /escrow/order-checkout` (`EscrowFunction.HandleOrderCheckoutAsync`) | role check (`ESCROW_025`) and body validation, CRITICAL check, velocity (`ESCROW_011`), then `EscrowService.CreateOrderCheckoutAsync` (order checks and the Telebirr preOrder) |
| `/sync` crop, trip and order `create` | after field validation and before the write: CRITICAL check |

### 17.2 Gate order at settlement

In every sweep of `EscrowCronFunction` the risk gate runs before the first money-related side effect of that escrow. The gate reads each party's tier (section 14, memoized) and the party's user row (`suspended` and `suspendReason`, memoized). On a trigger the `held` write happens at that point (section 18) and the sweep moves on to the next escrow.

| Sweep | Gate position |
|-------|---------------|
| `AutoReleaseAsync` | After the eligibility filters (`deliveryConfirmedAt` set, `disputeWindowEndsAt` passed) and before `EscrowService.ReleaseAsync`, so before `ResolvePayoutAsync`, its `release_blocked_kyc` audit entry, the settlement lock and Telebirr |
| `AutoRefundAsync` | After the confirmed-delivery skips (including the `IsDeliveryConfirmedAndStampAsync` repair), the order read, the progressing-order expiry extension and the not-refundable skip; before `TryCancelRefundedOrderAsync`, `TryRestoreListingStockAsync` and `EscrowService.RefundAsync`. A held escrow never sits behind a cancelled, restocked order |
| `SettleResolvedDisputesAsync` | After reading `disputeResolution` (`farmer_wins`, `buyer_wins`; `partial_refund` keeps today's skip) and before `ReleaseAsync` or `RefundAsync` |

`ReleaseAsync` and `RefundAsync` keep refusing `held` for the system caller through their status check (defence in depth). The admin routes are not gated (matrix above).

### 17.3 `FRAUD_ACCOUNT_HELD`

| Surface | Shape |
|---------|-------|
| REST (`/escrow/*`) | `403 {"error":{"code":"FRAUD_ACCOUNT_HELD","message":"..."}}` |
| `/sync` operation | `status: "error"`, `message: "FRAUD_ACCOUNT_HELD: Your account is under review. Some actions are paused. Contact support."` |

The code is permanent: the mobile `SyncExecutor._isPermanentRejection` already treats every `FRAUD_` prefix as non-retryable, so permanence needs no change. The payment revert and the message do need a `SyncExecutor` change (FRD-06): today only `ORDER_KYC_REQUIRED` calls the revert hook, `FRAUD_ACCOUNT_HELD` would only be marked rejected, and the batch error message surfaces only `FRAUD_DUPLICATE_LISTING` and `FRAUD_VELOCITY_EXCEEDED`. Web has no sync executor (`sync_executor_web.dart` is a stub) and its repositories call the API directly, so FRD-06 handles the code there too. On an order `payment_submitted` the client reverts the local order status, as it does for `ORDER_KYC_REQUIRED`.

### 17.4 Tests FRD-04 owns

Each tier on each gated path, sync and REST, for the farmer (crop create), the trucker (trip create) and the buyer (order create, both payment paths, checkout); the LOW path unchanged; CRITICAL on `order-checkout` before any Telebirr call; per sweep, for a CRITICAL or suspended party: no order cancel, no stock restore, no `ResolvePayoutAsync` or `release_blocked_kyc` audit, no lock call, no Telebirr call, one `held` write.

---

## 18. Escrow `held` (planned, FRD-03)

A new escrow status for money frozen pending an admin decision. Contract: `specs/contracts/escrow-protocol.md` 1.7.0, `specs/contracts/order-lifecycle.md` 1.9.0.

```
escrowed ---------+
                  +--[settlement sweep, risk trigger]--> held --[admin release]--> released
dispute_resolved -+                                          \--[admin refund]---> refunded
```

### 18.1 Entry

Only a settlement sweep writes `held` (`heldBy: system`), at the gate position of section 17.2, from `escrowed` or `dispute_resolved`. Never at a user gate, never synchronously from an admin action. Triggers, checked for every party, in this precedence:

| `holdReason` | Trigger |
|--------------|---------|
| `fraud_confirmed` | The party's user row has `suspended = true` and `suspendReason = fraud_confirmed` (set by the FRD-05 reject action) |
| `account_suspended` | The party is suspended for any other reason |
| `risk_critical` | The party's tier is CRITICAL, including a truncated read |

`suspendReason` is admin free text today (`HandleUserActionAsync` stores the request's `reason` as typed), so `fraud_confirmed` cannot be told apart from an admin who typed the same words. FRD-05 owns the value: either a reserved token that the user action route refuses as free text, or a separate field that only the reject action sets.

Why settlement only: all system money out goes through the three sweeps (`AutoReleaseAsync`, `AutoRefundAsync`, `SettleResolvedDisputesAsync`), so a hold at settlement freezes every system payout and refund of the party without enumerating the party's escrows. Eager holding would page three party GSIs keyed on party plus `createdAt` (`BuyerStatusIndex`, `FarmerStatusIndex`, `TruckerStatusIndex`, status only a filter), add latency and escrow writes to `/sync` or the admin Lambda, and still miss escrows created later. The cost: a party's escrows show "Under review" only once they are due to settle. FRD-05 and FRD-07 show the admin the user's open escrows read-only (the party GSIs with a status filter on `escrowed`, `disputed`, `dispute_resolved` and `held`, paged, admin side).

### 18.2 Fields and write

| Field | Value |
|-------|-------|
| `status` | `held` |
| `holdReason` | one of the triggers above |
| `heldAt` | ISO 8601 |
| `heldBy` | `system` |
| `heldFromStatus` | `escrowed` or `dispute_resolved` |
| `heldFor` | `release` or `refund`: what the sweep was about to do |

The tier and score go to the audit metadata only, never onto the escrow row. The write is conditional: `status = heldFromStatus AND attribute_not_exists(settlementLock)`, so a settlement already in progress wins.

### 18.3 Exits

Admin group only, through the existing admin routes: `held` to `released` (`POST /escrow/release`) and `held` to `refunded` (`POST /escrow/refund`). There is no resume endpoint: with settlement-only entry a held escrow is always one the system was about to settle, so release or refund is the whole decision. The admin exit runs the steps the sweep skipped, in the sweep's order: a refund of an order escrow applies the auto-refund order rules (cancel a still `funded` order by version CAS and restore its listing stock first; refuse with `422 ESCROW_INVALID_TRANSITION` while the order is progressing); a release runs `ResolvePayoutAsync` as today.

### 18.4 Settlement lock

The settleable set for `EscrowRepository.TryAcquireSettlementLockAsync` becomes `escrowed`, `disputed` and `dispute_resolved` for every caller, plus `held` for the admin release and refund only (the method gains a flag that only the admin route sets). This is the intended set, not today's condition. Pre-existing defect, to be fixed in or before FRD-03: the lock condition today accepts only `escrowed` or `disputed`, while `EscrowService.ReleaseAsync` and `RefundAsync` (and escrow-protocol 1.3.0) accept `dispute_resolved`, so the dispute sweep and an admin settlement of a resolved dispute get `409 ESCROW_INVALID_TRANSITION` at the lock. Every current test mocks the lock as acquired; the fix needs a repository-level test that inspects the `UpdateItemRequest` condition.

### 18.5 Active set, crons, stamps

- `held` joins `EscrowService.ActiveEscrowStatuses` (one active escrow per order). This is defence in depth, not the only double-charge guard: since ESC-01, `/escrow/initiate` already refuses an order that is funded or carries a Telebirr payment order id (`ESCROW_024`), and the order escrow's deterministic `escrow-order-{orderId}` key with a conditional create prevents a second row. Without `held` in the set, a held order escrow still reads as missing to the lookups that use it, so the `LinkDeliveryToOrderEscrowAsync` self-heal would try to mint an escrow and the active-escrow answers would be wrong. FRD-03 also extends the second, hardcoded active set: the per-delivery check in `EscrowService.InitiateAsync` (`existingStatus is "initiated" or "escrowed" or "released" or "disputed"`), which lacks `dispute_resolved` as well. `LinkDeliveryToOrderEscrowAsync` and `ScheduleRefundForOrderAsync` keep requiring `escrowed`; on `held` they return false and write nothing.
- The crons never select `held`: their queries are status-keyed (`QueryByStatusForCronAsync` on `StatusExpiresIndex`).
- The delivery-confirmation stamp (`SyncFunction.StampEscrowDeliveryConfirmedAsync` and the cron's `IsDeliveryConfirmedAndStampAsync`) conditions on status in `escrowed`, `held` and keeps the status, so the admin sees the delivery outcome on a held escrow.
- A dispute on a held escrow is refused (`ESCROW_INVALID_TRANSITION`).

### 18.6 Visibility and audit

Parties (buyer, farmer, trucker) see `status: held` only, labelled "Under review" in all three languages. `holdReason`, `heldBy`, `heldFromStatus` and `heldFor` are admin-only and absent from party responses. Party scoping of escrow reads is unchanged.

Audit actions, written to escrow-audit and transaction-audit with tier and score in the metadata: `fraud_hold` (system), `fraud_hold_release` and `fraud_hold_refund` (admin). Not `hold`: escrow-audit already uses `hold_success`, `hold_pending` and `hold_failed` for the Telebirr hold, and the admin UI labels `action_hold` as "Funds held".

### 18.7 Flag and client compatibility

`FRAUD_HOLD_ENABLED` (default false). With the flag off, a sweep that finds a trigger skips the escrow (status unchanged, no money moves, alarm token `RISK_SETTLEMENT_SKIPPED`) and retries next hour, which every shipped client tolerates.

Escrow `status` is an open set for clients: a client must not fail on a value it does not know; it shows a neutral state with no actions and keeps the raw value. Today's clients do not meet that rule. The mobile `EscrowRepository` (three response mappers) and `EscrowSyncService` parse the status with `PaymentStatus.values.byName`, which throws `ArgumentError` on an unknown value (not `ApiException`, so a history refresh does not fall back to the cache). `PaymentStatus` in `core/database/schemas/payment.dart` and `core/models/web/payment.dart` has neither `held` nor `dispute_resolved`. `WebEscrowRepository._parsePaymentStatus` maps an unknown value to `initiated`. Pre-existing consequence: every party of a `dispute_resolved` escrow already gets a failing escrow detail and history on mobile.

FRD-06 (mobile and web together) appends `held` to `PaymentStatus` in both files (append only; Isar stores the enum by name), replaces `byName` with a tolerant parser in the repository and the sync service, stops the web parser from defaulting to `initiated`, covers `held` in the exhaustive switches of `escrow_status_badge.dart` and `escrow_timeline.dart`, and adds "Under review" in en, am and om.

Ordering: FRD-03 may merge with `FRAUD_HOLD_ENABLED=false`. The flag is turned on only after FRD-06 is deployed on web and released to the stores, and either ESC-25's minimum app version gate is live or the lead accepts that un-updated installs fail escrow detail for held escrows (as they already do for `dispute_resolved`).

Naming: escrow `status: held` (frozen for review) is unrelated to the three-party design's `legStatus` value `held` (money still in escrow), to the Telebirr hold, and to reputation tiers. Until per-leg status lands, a hold covers the whole escrow.

### 18.8 Tests FRD-03 owns

A held escrow is never selected by any cron; `held` is in the active set (`ESCROW_024` still fires); the lock accepts `dispute_resolved` and, for the admin only, `held`; admin release and refund from `held` including the order rules; the conditional write loses to a settlement lock; flag off skips with the alarm token; the stamp keeps `held`; a dispute on `held` is refused; order and delivery views show "Under review" for the buyer, the farmer and the trucker.

---

## 19. Admin Actions (planned, FRD-05)

`POST /admin/fraud-signals/{id}/action` (`AdminFunction.HandleFraudActionAsync`) today stamps `status: reviewed`, `resolution`, `reviewedBy`, `reviewedAt` and `reviewNotes` and writes a transaction-audit row (`fraud_{action}`), with no other side effect. FRD-05 adds the side effects:

| Action | Effect on the score | Other side effects |
|--------|---------------------|--------------------|
| `approve` | The signal stops counting (section 14.2) | none |
| `reject` | The signal keeps counting | Suspends the user through the existing `HandleUserActionAsync` suspend path with `suspendReason: fraud_confirmed`: Cognito disable, global sign-out, `AUTH_025` on the next refresh. Holds nothing synchronously; the user's escrows are held by the next sweep that would settle them (section 18.1). The response lists the user's open escrows read-only |
| `whitelist` | Writes the `~whitelist` row (section 20) with `whitelistUntil` now plus `FRAUD_WHITELIST_DAYS`; soft-class weights stop counting while it is active | none |

Every action keeps its transaction-audit row. The action handler refuses non-signal rows (`recordType` of `credit` or `whitelist`) with `404 ADMIN_006`.

Admin surfaces until FRD-07: the dashboard count (`HandleDashboardStatsAsync`, `FraudSignalsCount`, a `ScanCountAsync` over the whole table) and the list (`HandleListFraudSignalsAsync`, a `Scan` with an optional `status` filter) must exclude credit and whitelist rows. The task that first writes a non-signal row (FRD-05 or FRD-10, whichever lands first) adds `attribute_not_exists(recordType) OR recordType = signal` to both. FRD-07 replaces the Scans with a sparse GSI on `status`, which excludes those rows by construction because they carry no `status`.

---

## 20. Data Model Additions

The fraud-signals table keeps its keys (`userId`, `signalKey`) and its TTL attribute. The TTL attribute name `expiresAt` (Number, epoch seconds) is never renamed: the table is live, and a changed TTL attribute name rolls the deploy back.

| Row kind | `recordType` | `signalKey` | `status` | Other fields |
|----------|--------------|-------------|----------|--------------|
| Signal (today) | absent (legacy) or `signal` | `{ISO}#{signalType}` | `pending` or `reviewed` | as section 4.3 |
| Credit (FRD-10) | `credit` | `{ISO}#CLEAN_DELIVERY` | absent | `weight: -5`, `entityType: escrow`, `entityId` |
| Whitelist (FRD-05) | `whitelist` | `~whitelist` (one per user; sorts after every ISO key) | absent | `whitelistUntil` (ISO), `expiresAt` set to the same instant in epoch seconds, `reviewedBy`, `reviewNotes` |

Credits (FRD-10): when an escrow completes through auto-release with no dispute, no hold and no signal on its order, delivery or escrow, each party gets a credit row: the buyer, the farmer, and the trucker when present. The buyer is included because a paid, received and undisputed trade is the same evidence for every party. Self-trade farming is bounded by `RISK_CREDIT_CAP` and later caught by FRD-15.

FRD-07 adds a sparse GSI on `status` (partition `status`, sort `createdAt`) so the admin list and the held queue stop scanning.

---

## 21. AI Advisory Boundary (planned, FRD-19, FRD-20)

- AI output is one `AI_ADVISORY` signal per review, class advisory, and the total advisory weight per window is capped by `RISK_ADVISORY_CAP`.
- The CRITICAL test uses the enforcement score, which excludes advisory weight (section 14.2). AI can at most move a user from LOW to MEDIUM or HIGH, that is, add a step-up. It never holds, releases, refunds or blocks.
- The reviewer runs asynchronously in a separate non-AOT Lambda on HIGH and CRITICAL users only, reads the audit timeline, and writes an admin-facing explanation next to the signal. It has a kill switch in configuration.
- Rules stay authoritative. ML scoring (FRD-20) is considered only once labelled outcomes exist (FRD-18), and even then rules keep the final word on holds.

---

## 22. Codes, Statuses and Owners

| Code or status | Kind | Since | Producer (owner) | Consumers (owner) | Contract |
|----------------|------|-------|------------------|-------------------|----------|
| `AUTH_040` | step-up | auth-api 2.1.0 | `StepUpPolicy` (live); FRD-04 adds the MEDIUM and HIGH use | step-up dialog, mobile and web (live) | auth-api 2.6.0 |
| `AUTH_041` | step-up | auth-api 2.4.0 | `StepUpPolicy.CheckPayment` (live); FRD-04 adds the HIGH use and the amount-neutral text | Fayda step-up flow (live); FRD-06 rewords `faydaStepUpMessage` | auth-api 2.6.0 |
| `FRAUD_ACCOUNT_HELD` | rejection | new | FRD-04 (server) | FRD-06 (mobile and web, three languages; `SyncExecutor` revert hook for `FRAUD_ACCOUNT_HELD` on `payment_submitted`); mobile sync already treats it as permanent | sync-protocol 1.9.0, escrow-protocol 1.7.0, order-lifecycle 1.9.0, auth-api 2.6.0 |
| escrow `held` | status | new | FRD-03 (server, infra; behind `FRAUD_HOLD_ENABLED`); triggers from FRD-04 (tier) and FRD-05 (reject) | FRD-06 tolerant parser and badge (must ship before the flag is on); admin vocabulary with FRD-03 (next row); held queue FRD-07 | escrow-protocol 1.7.0, order-lifecycle 1.9.0 |
| escrow `held` in the admin app (`admin/src/lib/transaction.ts` `escrowStatusVariant`, `admin/src/pages/EscrowsPage.tsx` `statusVariant`, i18n en, am, om: `escrows.held`, a new `transactions.escrow_held`, `action_fraud_hold`, `action_fraud_hold_release`, `action_fraud_hold_refund`) | admin UI | new | FRD-03 (the admin app is where held escrows are resolved, so it ships with the status) | FRD-07 adds the held queue and a dashboard held count | none |
| `FRAUD_DUPLICATE_LISTING`, `FRAUD_VELOCITY_EXCEEDED`, `ESCROW_011` | rejection | F-14 | live | live | sync-protocol, escrow-protocol |
| `RISK_SCORE_UNAVAILABLE`, `RISK_SCORE_TRUNCATED`, `RISK_SETTLEMENT_SKIPPED` | log alarm tokens | new | FRD-02 (first two), FRD-03 | FRD-16 metrics | this document only |

Contract numbers are the ones taken by FRD-01 and may be renumbered by the lead in merge order. Ordering: FRD-02 first; FRD-03 merges with the flag off; FRD-06 ships on web and in both stores; then the flag is turned on (section 18.7).

---

## 23. Implementation Map

| Task | Touches |
|------|---------|
| FRD-02 | `IFraudDetectionService.GetUserRiskScoreAsync` and its result type (score, enforcement score, tier, `truncated`), constants and env overrides in `FraudDetectionService`, memoization helper, unit tests (section 14.7) |
| FRD-03 | `held` write in the three sweeps of `EscrowCronFunction`, admin exits in `EscrowFunction` and `EscrowService` (`ReleaseAsync`, `RefundAsync` with the order rules), `TryAcquireSettlementLockAsync` set and admin flag (and the `dispute_resolved` fix), `ActiveEscrowStatuses` and the per-delivery active check in `InitiateAsync`, the two delivery-confirmation stamps, dispute refusal, audit actions, `FRAUD_HOLD_ENABLED`; admin status vocabulary; infra (next table) |
| FRD-04 | Risk gate in `SyncFunction` (order `payment_submitted`, crop, trip and order create), `EscrowFunction` (`HandleInitiateAsync`, `HandleOrderCheckoutAsync`), the settlement gate of section 17.2, amount-neutral `AUTH_041` text, `FRAUD_ACCOUNT_HELD` |
| FRD-05 | `HandleFraudActionAsync` side effects, `~whitelist` row, `suspendReason: fraud_confirmed`, dashboard and list filters, transaction-audit rows, admin UI |
| FRD-06 | `PaymentStatus` `held` on mobile and web, tolerant status parsers, badge and timeline, "Under review" and `FRAUD_ACCOUNT_HELD` strings in en, am, om, `SyncExecutor` revert hook for `FRAUD_ACCOUNT_HELD` on `payment_submitted` (and its message in the batch result), `AUTH_041` re-auth retry on web repositories |
| FRD-07 | Sparse `status` GSI on fraud-signals, grouped Fraud Review with score and tier, held queue, transaction timeline link |
| FRD-08 | Signals from existing rejects plus the per-user, per-type write rate limit |
| FRD-09 | Transaction pattern rules with thresholds in config |
| FRD-10 | `CLEAN_DELIVERY` credit rows from the auto-release sweep |

Infrastructure needed (`infra/lib/stacks/api-stack.ts`):

| Lambda | Today | Needed |
|--------|-------|--------|
| `EscrowCronFunction` | `USERS_TABLE` read; no signals or transaction-audit access | `FRAUD_SIGNALS_TABLE` with read (FRD-04 gate) and write (FRD-10 credits), `TRANSACTION_AUDIT_TABLE` with write (FRD-03 `fraud_hold` rows), `FRAUD_HOLD_ENABLED` |
| `EscrowFunction` | `FRAUD_SIGNALS_TABLE` read and write | `TRANSACTION_AUDIT_TABLE` with write (FRD-03 admin exits) |
| `SyncFunction`, `AdminFunction` | `FRAUD_SIGNALS_TABLE` read and write, `TRANSACTION_AUDIT_TABLE` | no change for FRD-02 to FRD-05 |
| fraud-signals table | keys and TTL only | FRD-07 sparse GSI on `status` |

Ties to EPIC ESC: ESC-33 writes `NO_SHOW` and `DELIVERY_OVERDUE` signals and re-attributes `GPS_ANOMALY`; ESC-25's minimum app version gate is one of the two conditions for turning the hold flag on; design cases H1 and H4 of the three-party escrow design (a party suspended mid-order, a buyer suspended after funding) resolve to this `held` status. Account deletion (`specs/contracts/account-deletion.md`, ACC-01) already refuses while the user is a party to a `held` escrow, because any status it does not list blocks, and a suspended user cannot self-delete; its open point on ban evasion stays with EPIC FRD, since a CRITICAL user who is not suspended and has no open escrow can still delete the account.

---

## Changelog

| Version | Date | Change |
|---------|------|--------|
| 3.0.0 | 2026-10-06 | Risk engine specified (sections 14 to 23): score formula with hourly dedup, bounded newest-first read that fails closed, tiers LOW 0-29, MEDIUM 30-59, HIGH 60-99, CRITICAL 100+, enforcement matrix and gate order per money action, escrow `held` (`holdReason`, `heldAt`, `heldBy`, `heldFromStatus`, `heldFor`; settlement-only entry, admin-only exits, `FRAUD_HOLD_ENABLED`), decay credits and whitelist rows, AI advisory boundary, codes and owners table, implementation map. Corrections: payment velocity is 5 per hour and returned as `ESCROW_011`; Rule 5 and `GetUserRiskScoreAsync` marked not built; current `RecordFraudSignalAsync` signature; status header |
| 2.0.0 | 2026-02-23 | Full architecture: 5 rules, risk scoring, FraudSignalsTable, mobile integration, testing strategy |
| 1.0.0 | 2026-02-21 | Initial design: 4 rules, interface + DTO |

---

**Cross-references**:
- Decision record: `specs/decisions/f14-fraud-detection-architecture.md`
- Crop listing architecture: `specs/architecture/crop-listing-architecture.md`
- Delivery verification: `specs/architecture/delivery-verification-architecture.md`
- Escrow payment: `specs/architecture/escrow-payment-architecture.md`
- Sync protocol: `specs/contracts/sync-protocol.md` (1.9.0: `FRAUD_ACCOUNT_HELD:` operation error)
- Escrow protocol: `specs/contracts/escrow-protocol.md` (1.7.0: `held`, fraud hold and risk tiers)
- Order lifecycle: `specs/contracts/order-lifecycle.md` (1.9.0: fraud hold and payment gates)
- Auth API: `specs/contracts/auth-api.md` (2.6.0: `AUTH_040`, `AUTH_041`, `FRAUD_ACCOUNT_HELD`)
- Step-up decision record: `specs/decisions/auth-step-up-plan.md`
- Three-party escrow design: `specs/features/three-party-escrow-commitments-reputation.md` (ESC-25, ESC-33, H1, H4)
- Task list: `TASK.md`, EPIC FRD

**Last Updated**: 2026-10-06
**Author**: Architecture Agent
**Status**: Rules 1 to 4 and signal persistence implemented. Risk engine (sections 14 to 23) specified, not built (EPIC FRD).
