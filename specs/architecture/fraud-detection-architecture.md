# Fraud Detection System Architecture (F-14)

## Version
3.2.0 | 2026-10-23

## Status
Rules 1 to 4, signal persistence and the risk score (sections 14 and 16, FRD-02) implemented. Nothing calls the score yet: enforcement and the rest of the risk engine (sections 17 to 23) are specified, not built (EPIC FRD).

---

## 1. Overview & Threat Model

F-14 adds fraud detection to WeredaConnect, targeting five categories of abuse that threaten the platform's trust model, which matters most in Ethiopian rural communities where users have limited legal recourse. Rules 1 to 4 are live. The account-level risk engine (T6) is specified in sections 14 to 23 and tracked as EPIC FRD in `TASK.md`. Its score (section 14) is built and not yet enforced.

### 1.1 Threat Scenarios

| # | Threat | Actor | Impact | Detection |
|---|--------|-------|--------|-----------|
| T1 | **Listing spam** | Malicious farmer | Floods marketplace, degrades browse UX | Velocity limits (Rule 3) |
| T2 | **Duplicate listings** | Farmer (accidental or intentional) | Confuses buyers, inflates supply | Duplicate detection (Rule 1) |
| T3 | **Price manipulation** | Bad actor | Distorts market prices, enables scams | Price anomaly flagging (Rule 2) |
| T4 | **GPS spoofing** | Trucker or colluding parties | Fake deliveries to claim escrow funds | GPS consistency check (Rule 4) |
| T5 | **Payment flooding** | Bot or compromised account | Drain escrow system, DoS on Telebirr | Payment velocity limits (Rule 3) |
| T6 | **Account abuse patterns** | Repeat offenders | Sustained low-level fraud across rules | Risk score and tiers (sections 14 to 17; score built in FRD-02, enforcement planned in FRD-03 and FRD-04) |

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

### Rule 5: Account-Level Risk Score (score built in FRD-02, not enforced; see sections 14 to 17)

Version 2.0.0 of this document specified a weighted 30-day score with four tiers and a
`UserRiskScores` attribute on `UsersTable`. That design was never built. The score is now
specified in section 14 (formula, bounded read, failure modes), section 16 (tiers) and section 17
(enforcement). FRD-02 built sections 14 and 16: `GetUserRiskScoreAsync` reads the signal rows
back. No request path calls it until FRD-04. It is computed on read from the fraud-signals table and never stored on the user row. The signal
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
│  │  GetUserRiskScoreAsync()  (FRD-02, no caller yet)    │      │
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

    // FRD-02. Score, enforcement score, tier (LOW, MEDIUM, HIGH, CRITICAL), Truncated and
    // Source (Computed, TableNotConfigured, Unavailable). Never throws. Section 14.
    Task<RiskScoreResult> GetUserRiskScoreAsync(string userId);
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

// FRD-02 (section 14). Server-internal: never serialized, never returned to clients (section 17).
public enum RiskTier { Low, Medium, High, Critical }
public enum RiskScoreSource { Computed, TableNotConfigured, Unavailable }

public sealed record RiskScoreResult
{
    public required int Score { get; init; }            // includes AI advisory weight
    public required int EnforcementScore { get; init; } // without it; decides CRITICAL
    public required RiskTier Tier { get; init; }
    public bool Truncated { get; init; }
    public RiskScoreSource Source { get; init; } = RiskScoreSource.Computed;
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

    // GetUserRiskScoreAsync (FRD-02) queries FraudSignalsTable:
    //   PK = userId, SK >= ISO(now minus RISK_WINDOW_DAYS), newest first,
    //   hourly dedup per type, early exit, page cap. Section 14.
    //   RiskScoreCalculator.cs holds the pure scoring and RiskScoreSettings;
    //   RiskScoreMemo.cs reads each user once per batch or sweep.

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
escrow initiation velocity rejections in `EscrowFunction`
(`FRAUD_VELOCITY_EXCEEDED`), and since ESC-48 a buyer's transport-failure cancel
in `SyncFunction` (`CONTACT_THEN_TRANSPORT_CANCEL`, next paragraph).

**`CONTACT_THEN_TRANSPORT_CANCEL` (ESC-48, order-lifecycle.md 1.18.0)**: the
parties of an order may reach each other once the farmer accepted the paid
order. A buyer who then cancels after a transport failure (no pickup by the
pickup deadline) gets a full refund, which is also how a trade agreed off the
platform would end. Each such cancel of an order whose contact had opened writes
one signal on the buyer (`entityType: order`, `entityId` the order id, the
farmer id in the description, never a phone number), with the default weight
10. One cancel is not fraud; a buyer who repeats it, or one farmer who appears
in many of them, is what the review is for. The weight stays the default until
the risk engine (section 14) is built and tuned.

**Why a separate table instead of an attribute on UsersTable**: Individual fraud signals need a time-series query pattern (PK=userId, SK=timestamp) that doesn't fit the UsersTable schema (PK=userId, no SK). Storing signals as a list attribute would require read-modify-write which is not atomic. A separate table allows clean TTL-based expiry and efficient range queries.

**The risk score is computed on read** (section 14) by summing the counted weights in the window with the dedup and caps of section 14.2. No separate risk score storage: that avoids stale scores and keeps the model simple. Built in FRD-02; no request path calls it until FRD-04.

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
| Risk read (FRD-02; one Query, normally one page, memoized per user) | 1-5 RCU | ~10-20ms |

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
| Risk score (FRD-02, `FraudRiskScoreTests.cs`) | `GetUserRiskScoreAsync` | The list in section 14.7 |
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
| Multiple signals accumulate risk (FRD-02, `Integration/FraudRiskScoreDynamoDbTests.cs`, LocalStack) | Rows from the writer are scored; window, partition and newest-first order (whitelist row first); a burst past two 1 MB pages is truncated to CRITICAL |

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

## 14. Risk Score (FRD-02, built)

**Status on main**: built in FRD-02, with no caller yet (FRD-04 wires the gates). `IFraudDetectionService.GetUserRiskScoreAsync` returns a `RiskScoreResult`: score, enforcement score, tier, `Truncated`, and `Source` (`Computed`, `TableNotConfigured` or `Unavailable`). `RiskScoreCalculator` holds the pure scoring of section 14.2, `RiskScoreSettings` the configuration of section 14.5, and `RiskScoreMemo` the per-invocation memo of section 14.3. Sections 15 to 23 are the specification that the rest of EPIC FRD implements; each section names the task that owns it. Where this document and the code disagree, the code describes what is deployed and this document describes what is planned.

### 14.1 Inputs

Rows of the fraud-signals table in one partition (`userId`). A signal row carries `weight` (written by `FraudDetectionService.SignalWeight`), `status` (`pending`, or `reviewed` with `resolution` set by the admin action: `approve`, `reject` or `whitelist`, but any string today, section 19), `signalKey` (`{ISO timestamp}#{signalType}`) and the TTL attribute `expiresAt`. Section 20 adds `recordType` and the credit and whitelist rows.

### 14.2 Formula

```
window        = rows whose signalKey >= ISO(now minus RISK_WINDOW_DAYS)
counted(row)  = row is a signal row
                AND row.resolution is not approve and not whitelist   (pending rows have none)
                AND NOT (whitelist active AND class(row.signalType) = soft)
dedup         = at most one counted row per (signalType, UTC hour bucket of signalKey);
                the first one read (the newest) wins
advisory      = sum of counted AI_ADVISORY weights, capped at RISK_ADVISORY_CAP
credits       = sum of credit-row weights in the window, floored at minus RISK_CREDIT_CAP
score         = max(0, sum of counted non-advisory weights after dedup + advisory + credits)
enforcement   = max(0, score minus advisory)      (the CRITICAL test never uses AI weight, section 21)
tier          = CRITICAL if enforcement >= RISK_TIER_CRITICAL or the read is truncated
                else HIGH   if score >= RISK_TIER_HIGH
                else MEDIUM if score >= RISK_TIER_MEDIUM
                else LOW
```

Rules behind the formula:

- **Every signal counts unless an admin cleared it.** A `reject` resolution is the admin agreeing the signal was fraud, so the score must not fall when the admin rejects. Only `approve` (false positive) and `whitelist` stop the row from counting; any other resolution counts, which fails closed while the action handler still accepts any string (section 19). Rows without `recordType` are legacy signal rows.
- **Two scores, one tier.** MEDIUM and HIGH come from the full score, so AI advisory weight can add a step-up. CRITICAL comes from the enforcement score, which leaves the advisory weight out. A full score of 100 or more with an enforcement score below 100 is therefore HIGH.
- **Burst dedup.** An offline farmer syncing 12 queued listings gets 7 `FRAUD_VELOCITY_EXCEEDED` rejections in one batch (section 11.5). A plain sum gives 105, CRITICAL for an honest user; the hourly dedup counts 15. Every row is still written and shown to the admin.
- **Floor at 0.** Credits never produce a negative score.
- The window is capped at `SignalTtlDays` (30), because the table's TTL deletes older rows anyway. The key condition, not the lazy TTL, is what bounds the read.

Implementation choices (FRD-02), where the formula leaves room:

- The scorer repeats the key condition's string comparison, so a row the Query should not have returned never counts.
- `resolution` is compared exactly: `Approve` counts. The row's `status` is not read.
- A signal row's weight is its stored `weight`, or `SignalWeight(signalType)` when that is missing. A negative weight on a signal row counts as 0, so only credit rows lower the score, and only within the cap. A missing `signalType` is read from the key.
- A row whose `recordType` is none of `signal`, `credit` and `whitelist` is not counted.
- The dedup also covers `AI_ADVISORY` rows. A key whose timestamp does not parse is a bucket of its own.
- The whitelist is active while `whitelistUntil` is after now; a missing or unparsable value means no whitelist.

### 14.3 Bounded read

One `Query` on partition key `userId` with key condition `signalKey >= :since`, `ScanIndexForward = false`, eventually consistent. The `~whitelist` row (section 20) sorts after every ISO key and is therefore read first. The scorer walks newest to oldest, applies the dedup as it goes, and stops early once the counted non-credit, non-advisory sum reaches `RISK_TIER_CRITICAL + RISK_CREDIT_CAP`: the tier is CRITICAL whatever remains unread, because credits cannot offset more than the cap. It follows `LastEvaluatedKey` for at most `RISK_MAX_PAGES` pages (default 2; a page is at most 1 MB, roughly 1,500 rows at about 600 bytes each).

If rows remain after the page cap the result is `truncated`, the tier is CRITICAL and the log carries the alarm token `RISK_SCORE_TRUNCATED`. This fails closed on purpose: reading newest first, a burst of same-type rows in the current hour (deduplicated to one) could otherwise push older weight out of the read. Honest users cannot reach the cap, since every row needs a rejected or flagged action and the mobile client never retries `FRAUD_*` sync errors. FRD-08's per-user, per-type write rate limit makes the cap unreachable short of deliberate abuse, so FRD-08 is not a prerequisite for FRD-02.

The result is memoized per user per invocation: a sync batch or a settlement sweep reads each user once. The memo is a `RiskScoreMemo` the caller creates per batch or sweep, not a cache on the service, which lives as long as the Lambda container. It remembers a failed read too, so one batch treats a user the same way throughout. The Query projects only the attributes the scorer reads.

### 14.4 Failure modes

| Condition | At a user-facing gate | In a settlement sweep |
|-----------|-----------------------|-----------------------|
| Signals table not configured (`FRAUD_SIGNALS_TABLE` empty) | LOW (today's no-op) | LOW (suspension triggers still apply). While `FRAUD_HOLD_ENABLED` is on, the sweep logs the alarm token `RISK_TABLE_UNCONFIGURED` once per run: the escrow cron has no `FRAUD_SIGNALS_TABLE` today (section 23), so a flag turned on before that infra change would otherwise fail open without a trace |
| `Query` throws | LOW, log alarm token `RISK_SCORE_UNAVAILABLE`; the baseline payment step-up (section 17) still applies | Skip this escrow for this run: no hold, no money moves; log `RISK_SETTLEMENT_SKIPPED` with `reason=score_unavailable`; retried next hour |
| A party's user row cannot be read (suspension check, section 17.2) | n/a | Skip this escrow for this run: no hold, no money moves; log `RISK_SETTLEMENT_SKIPPED` with `reason=user_read_failed`; retried next hour |
| Read truncated (section 14.3) | CRITICAL | CRITICAL (hold, section 18) |

`RiskScoreResult.Source` tells the two LOW cases apart (`TableNotConfigured`, `Unavailable`), so a sweep can skip instead of settling. The service logs `RISK_SCORE_UNAVAILABLE userId=<id> error=<exception type>` on every failed read, at a gate or in a sweep; the sweep's `RISK_SETTLEMENT_SKIPPED` line belongs to FRD-03 and FRD-04. A failure on a later page discards the partial score. An empty user id is `Unavailable` with no Query (`error=EmptyUserId`). A truncated read logs `RISK_SCORE_TRUNCATED userId=<id> pages=<n>`. User ids go through `MoneyAlarmLog.LogId`.

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

FRD-02 reads the first seven on every call (`RiskScoreSettings.FromEnvironment`). A value that is not an integer in range keeps its default: the window at least 1 and at most 30, tier bounds at least 1, caps at least 0, pages at least 1. Tier bounds that are not strictly increasing all fall back to the defaults. No Lambda sets them today, so the defaults apply. `FRAUD_WHITELIST_DAYS` (FRD-05) and `FRAUD_HOLD_ENABLED` (FRD-03) are read by the tasks that use them.

### 14.6 Cost

One `Query`, normally one page, eventually consistent: about 1 to 5 RCU and 10 to 20 ms per user per invocation. No new table, no write on read, no cached score to go stale.

### 14.7 Tests FRD-02 owns

In `server/tests/Services/FraudRiskScoreTests.cs`, and against LocalStack in `server/tests/Integration/FraudRiskScoreDynamoDbTests.cs`.

Tier boundaries 29/30, 59/60 and 99/100; burst dedup; whitelist row read first and soft weights skipped; rows before the window excluded; credit cap; a `reject` resolution and an unknown resolution still count, `approve` and `whitelist` do not; a score of 100 or more with an enforcement score below 100 is HIGH; early exit; page cap and truncation to CRITICAL; table unset gives LOW; `Query` error gives LOW with the alarm token; one `Query` per user per batch.

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

| Tier | Score that decides it | Effect (section 17) |
|------|-----------------------|---------------------|
| LOW | full score 0 to 29 | No change from today |
| MEDIUM | full score 30 to 59 | Fresh password on every payment, after the Fayda login above 10,000 ETB as today |
| HIGH | full score 60 and above, enforcement score below 100 | Fresh Fayda login and then a fresh password on every payment; FRD-13 payout delay |
| CRITICAL | enforcement score 100 and above, or a truncated read | New listings, trips, orders and payments refused with `FRAUD_ACCOUNT_HELD`; settlement of the party's escrows held for an admin |

Bounds are inclusive and configurable (section 14.5). The full score includes AI advisory weight; the enforcement score leaves it out (section 14.2), so a full score of 100 or more with an enforcement score below 100 is HIGH. The risk tier is unrelated to the reputation tiers of the three-party escrow design (`specs/features/three-party-escrow-commitments-reputation.md`) and to the Telebirr hold; this document says "risk tier" throughout.

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

In every sweep of `EscrowCronFunction` the risk gate runs before the first money-related side effect of that escrow. The gate reads each party's tier (section 14, memoized) and the party's user row (`suspended` and `suspendReason`, memoized). On a trigger the `held` write happens at that point (section 18) and the sweep moves on to the next escrow. If a party's tier or user row cannot be read, the sweep settles nothing for that escrow in this run and logs `RISK_SETTLEMENT_SKIPPED` with the reason (section 14.4).

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

For an order escrow in the auto-refund sweep the hold must also fence the order, as today's cancel-then-refund does with the order's version CAS (`TryCancelRefundedOrderAsync`): otherwise a farmer can accept between the sweep's order read and the hold write, which leaves an `accepted` order behind an escrow held for refund. FRD-03 writes that hold with `TransactWriteItems` (already used in `EscrowService`): the escrow update with the condition above plus a `ConditionCheck` on the order, `version` and `status` equal to what the sweep read. A cancelled transaction holds nothing; the next sweep re-reads the order and, now `accepted`, pushes the expiry as today. Fallback if a transaction is not possible: re-read the order right after the hold and, when it has moved to a progressing status, revert the escrow `held` to `escrowed` (conditional on `status = held` and the `heldAt` just written, audited `fraud_hold_reverted`) and push the expiry. Once the hold is written, the accept refusal of section 18.5 closes the window.

### 18.3 Exits

Admin group only, through the existing admin routes: `held` to `released` (`POST /escrow/release`) and `held` to `refunded` (`POST /escrow/refund`). There is no resume endpoint: with settlement-only entry a held escrow is always one the system was about to settle, so release or refund is the whole decision. A release runs `ResolvePayoutAsync` as today. What a refund runs depends on `heldFor`:

| `heldFor` | Order at hold time | Admin refund |
|-----------|--------------------|--------------|
| `refund` | stale `funded` or terminal (auto-refund sweep), `disputed` (dispute sweep, `buyer_wins`), or no order (a delivery-initiated escrow after its 7-day delivery timeout) | The steps the sweep skipped, in its order: the auto-refund order rules (cancel a still `funded` order by version CAS and restore its listing stock first; refuse with `422 ESCROW_INVALID_TRANSITION` while the order is progressing), the `buyer_wins` refund as the dispute sweep would run it, or for a delivery-initiated escrow the refund alone |
| `release` | `delivered` or left behind its confirmed delivery (auto-release sweep), `disputed` (dispute sweep, `farmer_wins`), or no order (a delivery-initiated escrow; the auto-release sweep does not filter by source) | Allowed, and the progressing-order rule does not apply: refusing it would leave a payout to the flagged party as the only exit (threat T4, a faked delivery by a trucker or colluding parties). The order side follows the list below |

Order side of a `heldFor: release` refund, by the order's status when the admin refunds:

- `delivered`: `delivered` to `disputed` to `dispute_resolved`.
- `disputed`: `disputed` to `dispute_resolved`.
- `accepted`, `pickup_confirmed` or `in_transit`: the best-effort `TryAdvanceOrderForDeliveryAsync` left the order behind its delivery, and `OrderTransitions` has no hop from these to `disputed`. If the order's active delivery is `delivered` (or `disputed`, which it can only be after it was delivered), the order is first advanced to `delivered` the way that advance would have done it (system actor, forward only; a self-pickup order goes from `accepted` straight to `delivered`), and then takes the `delivered` path. If it is not, the refund is refused with `422 ESCROW_INVALID_TRANSITION`, nothing moves, and the alarm token `RISK_HOLD_ORDER_MISMATCH` asks for manual reconciliation.
- `dispute_resolved` with this escrow's `disputeResolution: buyer_wins` already recorded: a retry after the refund itself failed; the order is left as it is and only the refund runs.
- Any other status, or an order escrow whose order is missing: refused the same way (`422`, `RISK_HOLD_ORDER_MISMATCH`).
- No order (a delivery-initiated escrow, no `orderId`): the escrow alone is refunded, with its escrow-audit and transaction-audit rows.

Every order hop is a system-actor write with a version CAS and one `transaction-audit` row, and the hops run before the refund (as cancel-then-refund does today), so a failed refund is retried with the order already in place. With the order hops, and still before the refund, the escrow records `disputeResolution: buyer_wins`, `disputeResolvedBy` (the admin) and `disputeResolvedAt`, the fields `HandleResolveDisputeAsync` writes, while it stays `held`; the refund then moves it `held` to `refunded` directly, never back through `dispute_resolved` (the dispute sweep would hold it again while the party stays CRITICAL). Listing stock is not restored, because the goods left the farmer; ratings stay blocked as after any dispute. ESC-03 built these order hops as `DisputeVerdictMirror` (`server/src/Services/DisputeVerdictMirror.cs`), which the admin verdict and the settlement of a `dispute_resolved` escrow already run; ESC-03 already refuses to settle a `dispute_resolved` verdict when `IsMismatch` is true (`422 ESCROW_INVALID_TRANSITION`, `DISPUTE_VERDICT_ORDER_MISMATCH` alarm); FRD-03 calls it with `buyer_wins` and refuses the refund the same way (both legs are decided before any write, so nothing has moved then), with its own `RISK_HOLD_ORDER_MISMATCH` token. The mirror also stamps the verdict on the delivery, and its mismatch also covers a delivery that cannot follow.

### 18.4 Settlement lock

`EscrowRepository.TryAcquireSettlementLockAsync(escrowId, createdAt, op, expectedStatus)` locks on the exact status the caller validated (ESC-03, escrow-protocol 1.9.0). The settleable statuses are `escrowed` and `dispute_resolved` for every caller (`disputed` left the set with ESC-03: a dispute settles only through a verdict), plus `held` for the admin release and refund only (FRD-03 adds `held` to the set behind a flag that only the admin route sets). ESC-03 fixed the earlier defect where the lock refused `dispute_resolved`; LocalStack tests in `server/tests/Integration/DisputeSettlementDynamoDbTests.cs` evaluate the real condition.

### 18.5 Active set, crons, stamps

- `held` joins `EscrowService.ActiveEscrowStatuses` (one active escrow per order). This is defence in depth, not the only double-charge guard: since ESC-01, `/escrow/initiate` already refuses an order that is funded or carries a Telebirr payment order id (`ESCROW_024`), and the order escrow's deterministic `escrow-order-{orderId}` key with a conditional create prevents a second row. Without `held` in the set, a held order escrow still reads as missing to the lookups that use it, so the `LinkDeliveryToOrderEscrowAsync` self-heal would try to mint an escrow and the active-escrow answers would be wrong. FRD-03 also extends the second, hardcoded active set: the per-delivery check in `EscrowService.InitiateAsync` (`existingStatus is "initiated" or "escrowed" or "released" or "disputed"`), which lacks `dispute_resolved` as well. `LinkDeliveryToOrderEscrowAsync` and `ScheduleRefundForOrderAsync` keep requiring `escrowed`; on `held` they return false and write nothing.
- The crons never select `held`: their queries are status-keyed (`QueryByStatusForCronAsync` on `StatusExpiresIndex`).
- No logistics step meets a held order escrow: a hold happens only once the delivery is confirmed (auto-release; the order is `delivered`, or behind it when the best-effort advance lagged), the order is `disputed` (dispute sweep), or the order is terminal or a stale `funded` order (auto-refund), see section 18.3. A delivery-initiated escrow (no order) is held either by the auto-release sweep, which selects by delivery confirmation and not by source (`heldFor: release`), or by the auto-refund sweep after its 7-day delivery timeout, when today's sweep would already refund it (`heldFor: refund`). The delivery linkage (`LinkDeliveryToOrderEscrowAsync`) and the delivery-confirmation stamps (`SyncFunction.StampEscrowDeliveryConfirmedAsync`, the cron's `IsDeliveryConfirmedAndStampAsync`) keep requiring `escrowed`. The one gap is a stale `funded` order whose escrow is held for refund: the farmer could still accept it, and a delivery created after that could never link to the held escrow or get its stamp. FRD-03 therefore refuses the farmer's `accepted` on an order whose escrow is `held`, with `ORDER_VALIDATION: This order is under review and cannot be accepted. Contact support.` (an existing code; it names no party). Reject and cancel stay allowed; the admin refund then finds a terminal order.
- A dispute on a held escrow is refused (`ESCROW_INVALID_TRANSITION`).

### 18.6 Visibility and audit

Parties (buyer, farmer, trucker) see `status: held` only, labelled "Under review" in all three languages. `holdReason`, `heldBy`, `heldFromStatus` and `heldFor` are admin-only and absent from party responses. Party scoping of escrow reads is unchanged.

Audit actions, written to escrow-audit and transaction-audit with tier and score in the metadata: `fraud_hold` (system), `fraud_hold_release` and `fraud_hold_refund` (admin), and `fraud_hold_reverted` (system) only if the fallback of section 18.2 is used. Not `hold`: escrow-audit already uses `hold_success`, `hold_pending` and `hold_failed` for the Telebirr hold, and the admin UI labels `action_hold` as "Funds held".

### 18.7 Flag and client compatibility

`FRAUD_HOLD_ENABLED` (default false). With the flag off, a sweep that finds a trigger skips the escrow (status unchanged, no money moves, alarm token `RISK_SETTLEMENT_SKIPPED` with `reason=flag_off`) and retries next hour, which every shipped client tolerates.

Escrow `status` is an open set for clients: a client must not fail on a value it does not know; it shows a neutral state with no actions and keeps the raw value. Today's clients do not meet that rule. The mobile `EscrowRepository` (three response mappers) and `EscrowSyncService` parse the status with `PaymentStatus.values.byName`, which throws `ArgumentError` on an unknown value (not `ApiException`, so a history refresh does not fall back to the cache). `PaymentStatus` in `core/database/schemas/payment.dart` and `core/models/web/payment.dart` has neither `held` nor `dispute_resolved`. `WebEscrowRepository._parsePaymentStatus` maps an unknown value to `initiated`. Pre-existing consequence: every party of a `dispute_resolved` escrow already gets a failing escrow detail and history on mobile.

FRD-06 (mobile and web together) appends `held` to `PaymentStatus` in both files (append only; Isar stores the enum by name), replaces `byName` with a tolerant parser in the repository and the sync service, stops the web parser from defaulting to `initiated`, covers `held` in the exhaustive switches of `escrow_status_badge.dart` and `escrow_timeline.dart`, and adds "Under review" in en, am and om.

Ordering: FRD-03 may merge with `FRAUD_HOLD_ENABLED=false`. The flag is turned on only after FRD-06 is deployed on web and released to the stores, and after one of these is live for clients older than FRD-06: a minimum app version gate that also covers escrow reads (`GET /escrow/{id}`, `GET /escrow/by-delivery/{deliveryId}`, `GET /escrow/history`), or a server-side mapping that answers those clients with a status they parse (`escrowed`) instead of `held`. ESC-25 as written gates order commands only, so on its own it does not protect the escrow reads where un-updated installs fail (they already fail the same way on `dispute_resolved`).

Naming: escrow `status: held` (frozen for review) is unrelated to the three-party design's `legStatus` value `held` (money still in escrow), to the Telebirr hold, and to reputation tiers. Until per-leg status lands, a hold covers the whole escrow.

### 18.8 Tests FRD-03 owns

A held escrow is never selected by any cron; `held` is in the active set (`ESCROW_024` still fires); the lock accepts `dispute_resolved` and, for the admin only, `held`; admin release from `held`; admin refund from `held` for `heldFor: refund` with the order rules and for `heldFor: release` with the order moving to `dispute_resolved` (from `delivered` and from `disputed`), no stock restore and `disputeResolution: buyer_wins`; the conditional write loses to a settlement lock; an accept that lands between the auto-refund sweep's order read and the hold write cancels the hold transaction, nothing is held and the next sweep pushes the expiry; the `heldFor: release` refund from an order left at `in_transit` advances it when the delivery is `delivered` and is refused with `RISK_HOLD_ORDER_MISMATCH` when it is not; a retry after a failed refund on a `dispute_resolved` order refunds only; a delivery-initiated escrow is refunded with no order write; flag off skips with the alarm token; a party read failure skips with the alarm token; the farmer's accept of a funded order whose escrow is `held` is refused with `ORDER_VALIDATION`; a dispute on `held` is refused; order and delivery views show "Under review" for the buyer, the farmer and the trucker.

---

## 19. Admin Actions (planned, FRD-05)

`POST /admin/fraud-signals/{id}/action` (`AdminFunction.HandleFraudActionAsync`) today stamps `status: reviewed`, `resolution`, `reviewedBy`, `reviewedAt` and `reviewNotes` and writes a transaction-audit row (`fraud_{action}`), with no other side effect. FRD-05 validates the action against `approve`, `reject` and `whitelist` and refuses anything else with `400 ADMIN_009` (the unknown-action answer of `HandleUserActionAsync`; today any string is stored as the resolution, and the score counts it, section 14.2). FRD-05 also adds the side effects:

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
- MEDIUM and HIGH come from the full score, which includes the advisory weight; CRITICAL comes from the enforcement score, which excludes it (sections 14.2 and 16). AI can therefore raise a user by at most `RISK_ADVISORY_CAP` points into MEDIUM or HIGH, that is, add a step-up, and never makes a user CRITICAL. It never holds, releases, refunds or blocks.
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
| `CONTACT_THEN_TRANSPORT_CANCEL` | signal (weight 10, default) | ESC-48 | `SyncFunction.ProcessTransportFailureCancelAsync` (live, best-effort) | admin Fraud Review (live, raw type); the risk score of section 14 (FRD-02, default weight, hard) | order-lifecycle 1.18.0 |
| `RISK_SCORE_UNAVAILABLE`, `RISK_SCORE_TRUNCATED`, `RISK_SETTLEMENT_SKIPPED` (`reason=flag_off`, `score_unavailable` or `user_read_failed`), `RISK_TABLE_UNCONFIGURED`, `RISK_HOLD_ORDER_MISMATCH` | log alarm tokens | FRD-02 (first two); new (others) | FRD-02 (first two, `FraudDetectionService.RiskScoreUnavailableToken` and `RiskScoreTruncatedToken`; no caller until FRD-04), FRD-03 (the sweep and hold-exit tokens) | FRD-16 metrics and alarms; `RISK_TABLE_UNCONFIGURED` and `RISK_SETTLEMENT_SKIPPED` alarm from the day `FRAUD_HOLD_ENABLED` is turned on | this document only |

Contract numbers are the ones taken by FRD-01 and may be renumbered by the lead in merge order. Ordering: FRD-02 first; FRD-03 merges with the flag off; FRD-06 ships on web and in both stores; then the flag is turned on (section 18.7).

---

## 23. Implementation Map

| Task | Touches |
|------|---------|
| FRD-02 (built) | `IFraudDetectionService.GetUserRiskScoreAsync` and its result type (score, enforcement score, tier, `truncated`), constants and env overrides in `FraudDetectionService`, memoization helper, unit tests (section 14.7). Delivered as `FraudDetectionService.GetUserRiskScoreAsync`, `RiskScoreResult` in `FraudDtos.cs`, `RiskScoreCalculator.cs` (with `RiskScoreSettings`), `RiskScoreMemo.cs`; no infra change, since the read uses the table's own keys and the Lambdas that have `FRAUD_SIGNALS_TABLE` already hold `dynamodb:Query` through `grantReadWriteData` |
| FRD-03 | `held` write in the three sweeps of `EscrowCronFunction`, admin exits in `EscrowFunction` and `EscrowService` (`ReleaseAsync`, `RefundAsync` with the order rules), `TryAcquireSettlementLockAsync` set and admin flag (and the `dispute_resolved` fix), `ActiveEscrowStatuses` and the per-delivery active check in `InitiateAsync`, the order-version fence on the auto-refund hold, the `heldFor: release` refund with the order hops to `dispute_resolved`, the accept refusal on a funded order whose escrow is held, dispute refusal, audit actions, the sweep alarm tokens, `FRAUD_HOLD_ENABLED`; admin status vocabulary; infra (next table) |
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

Ties to EPIC ESC: ESC-33 writes `NO_SHOW` and `DELIVERY_OVERDUE` signals and re-attributes `GPS_ANOMALY`; a minimum app version gate that covers escrow reads (ESC-25 extended, or its own gate) is one way to meet the client condition for turning the hold flag on (section 18.7); design cases H1 and H4 of the three-party escrow design (a party suspended mid-order, a buyer suspended after funding) resolve to this `held` status. The account deletion contract (`specs/contracts/account-deletion.md`, ACC-01, not built yet) refuses deletion while the user is a party to a `held` escrow, because any status it does not list blocks, and refuses self-deletion of a suspended user; its open point on ban evasion stays with EPIC FRD, since a CRITICAL user who is not suspended and has no open escrow can still delete the account.

---

## Changelog

| Version | Date | Change |
|---------|------|--------|
| 3.2.0 | 2026-10-23 | FRD-02: risk score built (sections 14 and 16), no caller yet. `GetUserRiskScoreAsync` and `RiskScoreResult` with `Source`, `RiskScoreCalculator`, `RiskScoreSettings`, `RiskScoreMemo`; implementation choices listed in 14.2, the logging and `Source` in 14.4, env parsing in 14.5; status markers updated |
| 3.1.0 | 2026-10-17 | New signal `CONTACT_THEN_TRANSPORT_CANCEL` (ESC-48): a buyer's transport-failure cancel of an order whose contact had opened, best-effort, default weight; listed with the producers and in section 22 |
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

**Last Updated**: 2026-10-23
**Author**: Architecture Agent
**Status**: Rules 1 to 4, signal persistence and the risk score (FRD-02) implemented. Enforcement and the rest of the risk engine (sections 17 to 23) specified, not built (EPIC FRD).
