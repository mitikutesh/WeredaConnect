# Fraud Detection System Architecture (F-14)

## Version
3.11.0 | 2026-10-28

## Status
Rules 1 to 4, signal persistence and the risk score (sections 14 and 16, FRD-02) implemented. The enforcement of section 17 is built (FRD-04): the user-action gates are live, and the settlement gate holds or skips a party at risk tier CRITICAL. The escrow `held` status of section 18 is built for a party rejected in Fraud Review (FRD-05), a suspended party (FRD-03) and a CRITICAL party (FRD-04), behind `FRAUD_HOLD_ENABLED`: on in dev, off in staging and prod, with apps older than FRD-06 answered `escrowed` (FUP-61, section 18.7). The admin actions of section 19 and the whitelist row of section 20 are built (FRD-05). Signals from existing rejects and the per-user, per-type write limit are built (FRD-08, section 15). The payout delay of section 17.5 is built (FRD-13). The rest of the risk engine (credits, the held queue, AI advisory; sections 20 to 23) is specified, not built (EPIC FRD).

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
recordType:  absent on signal rows (still written so); "whitelist" from FRD-05, "credit" from FRD-10 (section 20)
```

**Admin review semantics**: the admin API addresses a signal by `signalId`
(base64url of `userId|signalKey`) so `POST /admin/fraud-signals/{id}/action`
can reconstruct the composite table key without a scan. The review action
sets `status = "reviewed"` (the admin UI keys on that value) and records the
chosen verb (`approve`/`reject`/`whitelist`) in `resolution`, plus
`reviewedBy`/`reviewedAt`/`reviewNotes`. Since FRD-05 it accepts only those
three verbs, exactly, and has side effects (section 19).

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

### 4.4 Mobile Isar Schema: Enum Values Only

Fraud detection adds no Isar collection or field on the mobile client:
- Fraud rejections arrive as standard sync error codes (`FRAUD_DUPLICATE_LISTING`, `FRAUD_VELOCITY_EXCEEDED`, `FRAUD_ACCOUNT_HELD`)
- Flagged operations succeed normally: `isFlagged` is server-side metadata only
- FRD-06 appended `held` and `unknown` to the `PaymentStatus` enum on mobile and web (append only, stored by name, so rows written before read the same and nothing migrates; section 18.7). `unknown` is what the client reads for an escrow status it does not know

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

### 6.1 Client Scope (FRD-06)

The server decides everything. The client (mobile and web):
1. Handles the fraud error codes from sync responses and the escrow routes (section 6.2; `FRAUD_ACCOUNT_HELD` in section 17.3)
2. Reads the escrow status as an open set and shows `held` as "Under Review" on escrow and order screens, with no action the server refuses while held (section 18.7)
3. Optionally displays client-side advisory warnings (velocity, duplicate)

No new Isar collection or field; `PaymentStatus` gained `held` and `unknown` (section 4.4).

### 6.2 Error Code Handling

The server returns these new error codes:

| Error Code | HTTP Status | User Action |
|------------|-------------|-------------|
| `FRAUD_DUPLICATE_LISTING` | 400 (via sync `error`) | Edit existing listing instead |
| `FRAUD_VELOCITY_EXCEEDED` | 429 (sync `error` or HTTP 429 as `ESCROW_011`) | Wait and retry |
| `FRAUD_ACCOUNT_HELD` | 403 (REST) or sync `error`; sent since FRD-04 | Show "account under review", do not retry; FRD-06 adds the strings (section 17.3) |

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

- **Two small reads for the hold (FRD-06)**: order detail reads its order's escrow (`GET /escrow/escrow-order-{orderId}`, only when the order's status can hold money) and the order list reads one `GET /escrow/history?status=held` page per load (not on filter taps). Mobile answers both from its Isar cache offline. Everything else piggybacks on the existing sync and escrow endpoints
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
| `FRAUD_ACCOUNT_HELD` | Account under fraud review (risk tier CRITICAL); FRD-04, sync-protocol 1.9.0 (built in 1.25.0) | Permanent; show the message; on a payment revert the local order status (section 17.3) |

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

**Status on main**: built in FRD-02; the FRD-04 gates call it (section 17: `SyncFunction` through one `RiskScoreMemo` per batch, `EscrowFunction` once per request, the escrow-cron settlement gate through one memo per run). `IFraudDetectionService.GetUserRiskScoreAsync` returns a `RiskScoreResult`: score, enforcement score, tier, `Truncated`, and `Source` (`Computed`, `TableNotConfigured` or `Unavailable`). `RiskScoreCalculator` holds the pure scoring of section 14.2, `RiskScoreSettings` the configuration of section 14.5, and `RiskScoreMemo` the per-invocation memo of section 14.3. Sections 15 to 23 are the specification that the rest of EPIC FRD implements; each section names the task that owns it. Where this document and the code disagree, the code describes what is deployed and this document describes what is planned.

### 14.1 Inputs

Rows of the fraud-signals table in one partition (`userId`). A signal row carries `weight` (written by `FraudDetectionService.SignalWeight`), `status` (`pending`, or `reviewed` with `resolution` set by the admin action: `approve`, `reject` or `whitelist`; since FRD-05 the action refuses any other value, and rows reviewed before it may hold one, section 19), `signalKey` (`{ISO timestamp}#{signalType}`) and the TTL attribute `expiresAt`. Section 20 adds `recordType` and the credit and whitelist rows.

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

- **Every signal counts unless an admin cleared it.** A `reject` resolution is the admin agreeing the signal was fraud, so the score must not fall when the admin rejects. Only `approve` (false positive) and `whitelist` stop the row from counting; any other resolution counts, which fails closed for rows reviewed before FRD-05 validated the action (section 19). Rows without `recordType` are legacy signal rows.
- **Two scores, one tier.** MEDIUM and HIGH come from the full score, so AI advisory weight can add a step-up. CRITICAL comes from the enforcement score, which leaves the advisory weight out. A full score of 100 or more with an enforcement score below 100 is therefore HIGH.
- **Burst dedup.** An offline farmer syncing 12 queued listings gets 7 `FRAUD_VELOCITY_EXCEEDED` rejections in one batch (section 11.5). A plain sum gives 105, CRITICAL for an honest user; the hourly dedup counts 15. Since FRD-08 the writer keeps one row per user, type and hour (section 14.3), so the burst is one row; before, every refusal wrote its own.
- **Floor at 0.** Credits never produce a negative score.
- The window is capped at `SignalTtlDays` (30), because the table's TTL deletes older rows anyway. The key condition, not the lazy TTL, is what bounds the read.

Implementation choices (FRD-02), where the formula leaves room:

- The scorer repeats the key condition's string comparison, so a row the Query should not have returned never counts.
- `resolution` is compared exactly: `Approve` counts. The row's `status` is not read. Since FRD-05 the action refuses `Approve` (`400 ADMIN_009`), so only older rows can hold it.
- A signal row's weight is its stored `weight`, or `SignalWeight(signalType)` when that is missing. A negative weight on a signal row counts as 0, so only credit rows lower the score, and only within the cap. A missing `signalType` is read from the key.
- A row whose `recordType` is neither `credit` nor `whitelist` counts as a signal, whatever the value, with the weight fallback above (fails closed). A value other than `signal` also logs `RISK_SCORE_UNKNOWN_RECORD`, so a new writer the scorer was not taught about shows up (section 14.4).
- The dedup also covers `AI_ADVISORY` rows. A key whose timestamp does not parse is a bucket of its own.
- FRD-08: its reject types (section 15) other than `IDOR_CREATE_CONFLICT` count once per (`signalType`, `entityId`) over the whole window, not once per hour (`FraudDetectionService.CountsOncePerEntity`). The mobile queue marks a refused operation failed and sends it again after every reconnect (`SyncQueue.retryFailed`, the Home retry button), so one honest refusal leaves a row in every hour it is retried for as long as the operation stays queued; counted per entity it adds its weight once, while probes of different ids still add up. A row of these types without an `entityId` (`ESCROW_025` at the order checkout, `SUSPENDED_TOKEN_USE`) keeps the hourly bucket. `IDOR_CREATE_CONFLICT` keeps the hourly bucket and no cap: a create conflict is final for the mobile client (marked synced) and web makes a new id per create, so no honest retry repeats it. The earlier producers keep the hourly bucket. The Query projects `entityId` for this.
- FRD-08: an approved or whitelisted row clears its whole (`signalType`, `entityId`) bucket, older and newer rows of that entity alike, so one approve clears a stuck operation. Reading newest first, a bucket already counted when its approved row is met is taken back; its later rows are skipped.
- FRD-08: `ROLE_FORBIDDEN` and `IMMUTABLE_FIELD_CHANGE` are capped per type over the window at `RISK_REJECT_TYPE_CAP` (default 50) of counted weight, still counted per entity: one type alone stops at MEDIUM, both together reach CRITICAL. The early stop of section 14.3 counts only weight no unread row can take back: entity buckets (an older approved row may still clear them) are left out, and the capped types count up to the cap.
- The whitelist is active while `whitelistUntil` is after now; a missing or unparsable value means no whitelist.

### 14.3 Bounded read

One `Query` on partition key `userId` with key condition `signalKey >= :since`, `ScanIndexForward = false`, eventually consistent. The `~whitelist` row (section 20) sorts after every ISO key and is therefore read first. The scorer walks newest to oldest, applies the dedup as it goes, and stops early once the counted non-credit, non-advisory sum reaches `RISK_TIER_CRITICAL + RISK_CREDIT_CAP`: the tier is CRITICAL whatever remains unread, because credits cannot offset more than the cap. It follows `LastEvaluatedKey` for at most `RISK_MAX_PAGES` pages (default 2; a page is at most 1 MB, roughly 1,500 rows at about 600 bytes each).

If rows remain after the page cap the result is `truncated`, the tier is CRITICAL and the log carries the alarm token `RISK_SCORE_TRUNCATED`. This fails closed on purpose: reading newest first, a burst of same-type rows in the current hour (deduplicated to one) could otherwise push older weight out of the read. Honest users cannot reach the cap, since every row needs a rejected or flagged action and the mobile client never retries `FRAUD_*` sync errors (it does retry the FRD-08 refusals, which the write limit below bounds to one row per hour). FRD-08 built the per-user, per-type write limit: `RecordFraudSignalAsync` keys a row on the start of its UTC hour (`{hour}#{type}`, `createdAt` keeps the exact time of the first refusal) and writes it with `attribute_not_exists(signalKey)`, so a user holds at most one row per type per hour, 720 per type in the window, and a refused condition is the limit working, not an error. `/sync` also tries each user and type once per batch. One write per refusal, no new table. The cap is therefore unreachable short of deliberate abuse across many types and hours. A row keyed at the hour start can leave the window up to an hour before its TTL. Two consequences of one row per hour: the row keeps the `entityId` and description of the hour's first refusal, so a refusal of another entity in that hour is not recorded; and once an admin has approved or whitelisted that row, later signals of the same type in the same hour fail the condition and are not seen until the next hour (a refusal of the approved entity would not count anyway, section 14.2). FRD-08 was not a prerequisite for FRD-02. It is a prerequisite, once FRD-04 enforces the tiers, for any producer that writes a signal onto a user other than the caller (ESC-33's re-attributed `GPS_ANOMALY`, for one). A truncated read is CRITICAL, so without a per-user write limit one party could drive another past the page cap with rows of their own making (truncation griefing). Every signal is written against the caller except the verified payer mismatch (section 15), which needs a real, signed Telebirr payment for the buyer's own order and which the write limit bounds to one row per hour.

Known edge (noted in FRD-04, not fixed): DynamoDB can return a `LastEvaluatedKey` on a page that ends exactly at the last row, so a user whose window holds exactly `RISK_MAX_PAGES` full pages reads as truncated and CRITICAL although nothing is left. It fails closed and needs about 3,000 rows in 30 days, which no honest user reaches; a one-row probe after the last page would remove it.

The result is memoized per user per invocation: a sync batch or a settlement sweep reads each user once. The memo is a `RiskScoreMemo` the caller creates per batch or sweep, not a cache on the service, which lives as long as the Lambda container. It remembers a failed read too, so one batch treats a user the same way throughout. The Query projects only the attributes the scorer reads.

### 14.4 Failure modes

| Condition | At a user-facing gate | In a settlement sweep |
|-----------|-----------------------|-----------------------|
| Signals table not configured (`FRAUD_SIGNALS_TABLE` empty) | LOW (today's no-op) | LOW (suspension triggers still apply). The sweep logs the alarm token `RISK_TABLE_UNCONFIGURED` once per run (`RISK_TABLE_UNCONFIGURED action=auto_release_and_refund`, at the start of the run), whether `FRAUD_HOLD_ENABLED` is on or off: with the flag off the `flag_off` skip is what keeps a CRITICAL party unpaid, so a missing table matters either way. escrow-cron has `FRAUD_SIGNALS_TABLE` since FRD-04 (section 23), so the line means a deploy or a stack without it, which would otherwise pay a CRITICAL party without a trace |
| `Query` throws | LOW, log alarm token `RISK_SCORE_UNAVAILABLE`; the baseline payment step-up (section 17) still applies | No money moves for this escrow on this run. When another party of the escrow is CRITICAL the escrow is held as for any `risk_critical` trigger (flag on; skipped with `reason=flag_off` when off), because that hold is certain whatever the unread score says (section 17). Otherwise it is skipped: no hold, log `RISK_SETTLEMENT_SKIPPED` with `reason=score_unavailable`, flag on or off; retried next hour |
| A party's user row cannot be read (suspension check, section 17.2) | n/a | Skip this escrow for this run: no hold, no money moves; log `RISK_SETTLEMENT_SKIPPED` with `reason=user_read_failed`; retried next hour |
| Read truncated (section 14.3) | CRITICAL | CRITICAL (hold, section 18) |

`RiskScoreResult.Source` tells the two LOW cases apart (`TableNotConfigured`, `Unavailable`), so a sweep can skip instead of settling. The service logs `RISK_SCORE_UNAVAILABLE userId=<id> error=<exception type>` on every failed read, at a gate or in a sweep; the sweep's `RISK_SETTLEMENT_SKIPPED` line belongs to FRD-03 and FRD-04. A failure on a later page discards the partial score. An empty user id is `Unavailable` with no Query (`error=EmptyUserId`); `RiskScoreMemo` passes an empty or null id straight to the service, uncached. A truncated read logs `RISK_SCORE_TRUNCATED userId=<id> pages=<n>`. A read that met rows with an unknown `recordType` logs `RISK_SCORE_UNKNOWN_RECORD userId=<id> rows=<n> recordType=<first value>` once. User ids and the record type go through `MoneyAlarmLog.LogId`.

An `Unavailable` result carries `Tier = Low`. Every caller must check `Source` before it reads `Tier`: a settlement sweep that only looks at the tier would settle an escrow whose parties could not be scored. FRD-04 owns the sweep test for it.

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
| `RISK_REJECT_TYPE_CAP` | 50 | FRD-08: maximum counted weight per window of `ROLE_FORBIDDEN`, and separately of `IMMUTABLE_FIELD_CHANGE` |
| `FRAUD_WHITELIST_DAYS` | 30 | Default whitelist duration (section 19) |
| `FRAUD_HOLD_ENABLED` | false | Whether a sweep may write `held` (section 18) |

FRD-02 reads the first seven on every call (`RiskScoreSettings.FromEnvironment`). A value that is not an integer in range keeps its default: the window at least 1 and at most 30, tier bounds at least 1, caps at least 0, pages from 1 to 10 (a larger value is capped at 10, so a typo cannot turn one score into a long read). Tier bounds that are not strictly increasing all fall back to the defaults. No Lambda sets them today, so the defaults apply. FRD-08 reads `RISK_REJECT_TYPE_CAP` the same way, an integer of at least 0 (0 turns the two capped types off), no Lambda sets it. `FRAUD_WHITELIST_DAYS` (FRD-05) and `FRAUD_HOLD_ENABLED` (FRD-03) are read by the tasks that use them. The admin action reads `FRAUD_WHITELIST_DAYS` on every whitelist (`FraudDetectionService.WhitelistDaysFrom`): an integer from 1 to 365, else 30, so a typo cannot whitelist a user for good. No Lambda sets it today.

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
| `IDOR_CREATE_CONFLICT` | 40 | hard | `SyncFunction`: a crop, trip, order or delivery `create` whose id already holds a row the caller does not own and is no party to (the conflict echoes nothing, sync-protocol 1.4.0). The owner's own retry is not a source. Counted in every hour, uncapped (no honest retry) | live (FRD-08) |
| `ROLE_FORBIDDEN` | 25 | hard | `SyncFunction`: the role gates (crop, trip, order, intent, rating), the caller id binding on create (`farmerId`, `truckerId`, `buyerId`, `raterId`, `senderId`), an update, delete, cancel or dispute of a row the caller is no party to, and the delivery and order transitions by role (`*_UNAUTHORIZED`, `RATING_FORBIDDEN: You can only rate your own orders.`); `EscrowFunction`: `ESCROW_025` on `/escrow/initiate` and `/escrow/order-checkout`. A role gate met by a token without the role claim writes nothing. Counted once per entity, at most 50 per window (`RISK_REJECT_TYPE_CAP`) | live (FRD-08) |
| `IMMUTABLE_FIELD_CHANGE` | 25 | hard | `SyncFunction`: an update that changes an identity or money field (SIM-02 crop, SIM-03 trip, SIM-09 delivery, SIM-06 order, and an order update whose echoed `orderId` names another order). An unchanged echo is not a source. Counted once per entity, at most 50 per window | live (FRD-08) |
| `QR_HANDOFF_FAILURE` | 5 | soft | `SyncFunction`: `DELIVERY_NONCE_MISMATCH`, `DELIVERY_NONCE_REPLAY`, and `DELIVERY_NONCE_MISSING` while a nonce is on record. `DELIVERY_NONCE_EXPIRED` (a genuine old code) and the deferrals are not sources | live (FRD-08) |
| `TELEBIRR_PAYER_MISMATCH` | 0 | soft | `TelebirrWebhookFunction.PayerMatchesBuyerAsync`: a verified notification paid from a wallet other than the buyer's registered phone, applied because `TELEBIRR_REQUIRE_PAYER_MATCH` is off (a relative's wallet is common): logged for review, never scored | live (FRD-08) |
| `TELEBIRR_PAYER_REFUSED` | 5 | soft | The same, with `TELEBIRR_REQUIRE_PAYER_MATCH` on: the payment is refused | live (FRD-08) |
| `STALE_VERSION_BURST` | 0 | soft | `SyncFunction.ProcessOperationAsync`: the 10th update or delete version conflict in one batch (`StaleVersionBurstThreshold`); fewer write nothing | live (FRD-08) |
| `SUSPENDED_TOKEN_USE` | 0 | soft | `RefreshFunction`: `AUTH_025` for the refresh token of a suspended profile | live (FRD-08) |
| `GPS_JUMP` | 10 | soft | `SyncFunction`, the side effects of an accepted `location_ping` `fix` (LT-07, `live-trip.md` section 6.3): the new check-in and the trip's check-in just before or just after it in capture order are more than 1 km apart (great circle) at over 130 km/h. On the trip's trucker, the trip as the entity. A GPS glitch can do it too. Counted once per trip | live (LT-07) |
| `GPS_MOCKED` | 25 | soft | The same, for a check-in the phone flagged `mocked: true` (a location-faking app is in use). Counted once per trip | live (LT-07) |
| Transaction patterns: amount above 3 sigma of the user's history, new account with a large amount, repeated amounts just under 10,000 ETB, cancel-after-fund rate, per-buyer dispute rate, delivery time impossible for the distance | per rule | soft or hard per rule | FRD-09 | planned FRD-09 |
| `NO_SHOW`, `DELIVERY_OVERDUE`, buyer griefing (three declined paid orders in 30 days) | per type | hard | ESC-33 | planned ESC-33 |
| Relationship patterns: repeated farmer and buyer pairs, same device or IP on both sides, confirm-then-dispute, self-trading | per type | hard | FRD-15 hourly Lambda | planned FRD-15 |

Class semantics: **soft** is a flag-only rule whose weight an active whitelist skips; **hard** is a rejection or a money signal and always counts; **credit** is negative and capped; **advisory** is AI output, capped and excluded from the enforcement score. Each planned task adds its rows to this table, with weight and class, in the same PR as the producer.

Weights of the FRD-08 types. Only a tampered client produces the three hard types: every create id is a UUID made on the device, create screens and actions are shown by role and by the row's own party ids, and update payloads echo only the caller's own ids (the client code checked is listed in the FRD-08 PR). `ROLE_FORBIDDEN` and `IMMUTABLE_FIELD_CHANGE` count once per refused entity and at most 50 per type (section 14.2), so one refused operation is LOW (25) however often the queue retries it, and one of these types alone never passes MEDIUM. `IDOR_CREATE_CONFLICT` has no honest retry and counts in every hour, uncapped: one probe is MEDIUM (40), probes in three hours are CRITICAL. The three types in one hour are HIGH. The soft types come from honest clients too: version conflicts after offline edits, a QR code regenerated offline and scanned before it synced, a relative's wallet, the app's own refresh after a suspension. They weigh 0 or 5, count once per delivery or order escrow, and an FRD-05 whitelist skips them. The payer phone Telebirr sends (`251XXXXXXXXX`) is put into the users table's form (`+251XXXXXXXXX`) before the comparison. Residual risks, kept: a hard refusal from honest software adds its weight once per distinct stuck operation (an admin role change with operations of the old role still queued, a client bug), at most 50 per capped type, so the worst honest case is MEDIUM for one type and CRITICAL only when both capped types are stuck; an admin approve on any row of the stuck operation clears it; a web tab still holding an account the shared token no longer belongs to can send that account's ids under the token's user (a client follow-up). Not sources, by decision: a webhook whose signature, body, source IP or merchant fails (anyone can post to the route, so it is logged and written onto no user), a missing payer field, the REST read 403s, `MESSAGE_FORBIDDEN` (thread membership follows the order), `RATING_FORBIDDEN` for an order not yet delivered (the local status can lag), match event refusals (background analytics the user never made).

Attribution: every signal is written against the authenticated caller of the operation, with one exception: the webhook has no caller, so `TELEBIRR_PAYER_MISMATCH` or `TELEBIRR_PAYER_REFUSED` is written on the buyer that our own escrow or checkout row names, only after the signature verified, and only when that buyer's row and phone were read. ESC-33 changes `GPS_ANOMALY` to be attributed to the trucker who moved the goods rather than to whoever sent the confirming update.

---

## 16. Risk Tiers

| Tier | Score that decides it | Effect (section 17) |
|------|-----------------------|---------------------|
| LOW | full score 0 to 29 | No change from today, apart from the payout delay of a payee who is not trusted yet: a score above 0, a young account or little history (section 17.5) |
| MEDIUM | full score 30 to 59 | Fresh password on every payment, after the Fayda login above 10,000 ETB as today; the new-payee payout delay (section 17.5) |
| HIGH | full score 60 and above, enforcement score below 100 | Fresh Fayda login and then a fresh password on every payment; the longest payout delay (section 17.5, FRD-13) |
| CRITICAL | enforcement score 100 and above, or a truncated read | New listings, trips, orders and payments refused with `FRAUD_ACCOUNT_HELD`; settlement of the party's escrows held for an admin |

Bounds are inclusive and configurable (section 14.5). The full score includes AI advisory weight; the enforcement score leaves it out (section 14.2), so a full score of 100 or more with an enforcement score below 100 is HIGH. The risk tier is unrelated to the reputation tiers of the three-party escrow design (`specs/features/three-party-escrow-commitments-reputation.md`) and to the Telebirr hold; this document says "risk tier" throughout.

---

## 17. Enforcement Matrix and Gate Order (FRD-04, built)

Subject of the check: the caller at a user action; every party (buyer, farmer, and the trucker when present) at settlement. The tier and the score are never returned to clients.

**Built by FRD-04 (2026-10-23)**, with these implementation choices:

- `StepUpPolicy.CheckPayment` takes the tier (`PaymentNeedsFayda` tells a caller when to read the buyer's last Fayda login); `RiskGatePolicy` holds `FRAUD_ACCOUNT_HELD`, its text and `TierAtUserGate` (Source before Tier: anything but a computed score is LOW at a user gate). The `AUTH_041` text is now `Confirm it's you with Fayda to continue with this payment.`; the app string was already amount-neutral (FRD-06).
- The user-action gates go live on deploy, not behind `FRAUD_HOLD_ENABLED`: they write nothing, and every shipped client already treats `FRAUD_*` as permanent. Signals written in the last 30 days count from the first request.
- `EscrowFunction` without a fraud service (no `RATE_LIMITS_TABLE`) has no tier: LOW, like an unconfigured signals table (section 14.4), so the baseline step-up applies. Not taken: refusing payments (503) while the service is missing.
- On `/sync` creates the CRITICAL check sits after the field rules and before the F-14 listing checks (crop) and the listing and trip reads (order), so a held account costs no reads and writes no velocity signal.
- The settlement gate is FRD-03's `PartySuspensionGate` with a fraud service: the tier is read only when no party is suspended (so `account_suspended` keeps its precedence), through one `RiskScoreMemo` per run. A CRITICAL party holds the escrow even when another party could not be scored (both outcomes move no money; the hold is certain). Any result that is neither computed nor unconfigured skips the escrow (`score_unavailable`, fail closed). A `risk_critical` hold carries `tier`, `score`, `enforcementScore` and `truncated` in its `fraud_hold` audit metadata.
- With the flag off a CRITICAL party's settlement is skipped (`RISK_SETTLEMENT_SKIPPED reason=flag_off holdReason=risk_critical`, alarmed), as FRD-03 decided for any trigger (section 18.7): a CRITICAL party is never paid by a sweep. Not taken: settling CRITICAL parties as today while the flag is off.
- Not built (follow-up): the tier in the audit metadata of MEDIUM and HIGH cron settlements and of the admin release and refund (matrix rows below). HIGH at payout keeps today's money behaviour (rules below).

| Action | LOW | MEDIUM | HIGH | CRITICAL |
|--------|-----|--------|------|----------|
| Buyer payment up to and including 10,000 ETB (`/sync` order `payment_submitted`, `POST /escrow/initiate`) | `AUTH_040` when the password is not fresh (today) | same as LOW | `AUTH_041`, then `AUTH_040` | `FRAUD_ACCOUNT_HELD` |
| Buyer payment above 10,000 ETB | `AUTH_041` (today) | `AUTH_041`, then `AUTH_040` | `AUTH_041`, then `AUTH_040` | `FRAUD_ACCOUNT_HELD` |
| `POST /escrow/order-checkout` (ESC-01) | The gates already ran at `payment_submitted` | same | same | `403 FRAUD_ACCOUNT_HELD`, no Telebirr preOrder call |
| Create crop (farmer), trip (trucker), order (buyer) through `/sync` | allowed | allowed | allowed | `FRAUD_ACCOUNT_HELD` |
| Cron release, refund and dispute settlement | refund and dispute settlement as today; the auto-release waits for the new-payee payout delay unless the payee is trusted (score 0, an old enough account, enough completed escrows; section 17.5) | refund and dispute settlement as today, tier in the audit metadata (not built yet); the auto-release waits for the new-payee payout delay (a MEDIUM payee is never trusted, section 17.5) | refund and dispute settlement as today, tier in the audit metadata (not built yet); the auto-release waits for the flagged payout delay (section 17.5) | escrow moves to `held` (section 18); no money moves |
| Admin `POST /escrow/release` and `POST /escrow/refund` | not gated (the admin decides); tier in the audit metadata (not built yet) | same | same | same |

Rules:

- **A gate holds nothing.** At a user action CRITICAL only refuses that action. The only place the system writes `held` is a settlement sweep (section 18).
- **Fayda before the password at HIGH.** The Fayda round trip takes minutes and the password seconds, so asking for Fayda first keeps both proofs inside the 300 second window. The client already handles the pair: one prompt per batch, Fayda first, then the password-tier operations ask again on retry.
- **`AUTH_041` text is amount-neutral** (`StepUpPolicy.FaydaErrorMessage` since FRD-04, the app string `faydaStepUpMessage` since FRD-06). Clients key on the code, never on the text.
- **Not gated**: disputes (the existing `AUTH_040` rule is unchanged), delivery and logistics updates, messages, ratings, order accept and reject, profile updates.
- **Known gap, kept**: the counterparty's tier is not checked at order create. The settlement hold covers the money, and revealing a counterparty's tier would leak a fraud flag.
- **HIGH at payout keeps today's money behaviour** because no user is present to step up, apart from the payout delay of section 17.5 (FRD-13): the auto-release of a HIGH payee's escrow waits longer. The reviewer of FRD-04 may tighten HIGH at payout to `held`.

### 17.1 Gate order at user actions

| Path | Order |
|------|-------|
| `/sync`, every operation (`SyncFunction`) | ACC-02 first, before any operation of the batch: the caller's users row, read strongly consistent; a `deletionState` answers the whole request `410 ACCOUNT_004` and nothing is applied (a failed read `503 SYNC_SERVER_ERROR`). The rows below run after it |
| `/sync` order `payment_submitted` (`SyncFunction`) | transition auth (`ValidateOrderTransitionAuth`), risk read, CRITICAL check, the deleted party (ACC-02: the order's buyer, farmer or trucker row carries `deletionState`, read strongly consistent: `ORDER_VALIDATION: A party to this order has deleted their account.`, nothing written), KYC (`CheckPaymentKycAsync`, unchanged), tiered step-up (`StepUpPolicy.CheckPayment` extended with the tier), the live-gateway order total check (`ORDER_PAYMENT_TOTAL_OUT_OF_RANGE` since FUP-22, then the payment method check), then the write and the mock auto-fund |
| `POST /escrow/initiate` (`EscrowFunction.HandleInitiateAsync`) | the deleting caller first (ACC-02: `410 ACCOUNT_004`, before any other check or read; a failed read `503 ESCROW_012`), role check (`ESCROW_025`), risk read, CRITICAL check, tiered step-up, velocity (`ESCROW_011`), then `EscrowService.InitiateAsync`: at the start of its user validation, once the delivery and its order named the parties, a deleted buyer, farmer or trucker (ACC-02: `422 ESCROW_030`, nothing held; a failed read `500 ESCROW_012`) |
| `POST /escrow/order-checkout` (`EscrowFunction.HandleOrderCheckoutAsync`) | the deleting caller first (ACC-02: `410 ACCOUNT_004`; a failed read `503 ESCROW_012`), role check (`ESCROW_025`) and body validation, CRITICAL check, velocity (`ESCROW_011`), then `EscrowService.CreateOrderCheckoutAsync`: the order checks (`ValidateOrderForCheckout`), a deleted buyer, farmer or trucker (ACC-02: `422 ESCROW_030`), then the checkout slot and the Telebirr preOrder |
| `/sync` crop, trip and order `create` | after field validation and before the write: CRITICAL check |

`POST /escrow/dispute` is not risk-gated (section 17); ACC-02's deleting-caller `410 ACCOUNT_004` applies to it as to the two payment routes. The ACC-02 checks are specified in `account-deletion.md` 1.2.0 (Effects on Other Endpoints), `escrow-protocol.md` 1.28.0 and `sync-protocol.md` 1.26.0.

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

Built in `server/tests/Functions/SyncFunctionRiskGateTests.cs`, `EscrowFunctionRiskGateTests.cs` and `EscrowCronRiskGateTests.cs` (each sweep for a CRITICAL buyer, farmer and trucker with the flag on and off, and for an `Unavailable` source), `server/tests/Services/PartySuspensionGateTests.cs`, `server/tests/Helpers/StepUpAndKycPolicyTests.cs` (the tier matrix of `CheckPayment`), and `infra/test/unit/stack.test.ts` (escrow-cron env and read grant). All run in CI (no `Integration` category).


### 17.5 Payout delay (FRD-13, built)

**Built by FRD-13 (2026-10-23)**, escrow-protocol 1.33.0. A new or flagged payee waits longer for the automatic payout, so a signal, a late review or a FRD-05 reject has time to land before the money leaves; a trusted payee is paid when the dispute window ends, as before. Code: `PayoutDelayPolicy` (with `PayoutDelaySettings`), the stamp in `SyncFunction.StampEscrowDeliveryConfirmedAsync` and `EscrowCronFunction.StampIfDeliveredAsync`, the filter in `AutoReleaseAsync`, the deferral in `EscrowService.RetryTruckerLegAsync`.

**When.** At delivery confirm, in the same conditional write that stamps `deliveryConfirmedAt` and `disputeWindowEndsAt` (the sync `delivered` update, and the escrow-cron stamp repair): `releaseNotBefore = max(disputeWindowEndsAt, deliveryConfirmedAt + delay)`, so it never shortens the dispute window. An escrow stamped before FRD-13 has no field and settles as before. Not taken: deciding at the sweep (the date could not be shown before it and would move under the payee).

**Whose risk.** The payees: the farmer, and the trucker when the escrow has a trucker leg (the trucker on the escrow, else the one on the delivery). One release pays both legs, so the escrow waits for the later payee and both see the same date. Not taken: per-leg dates (the trucker leg cannot be paid before the farmer's, and per-leg status is ESC-20).

**Classes**, from data the server already has, each payee classed once per sync batch (`PayoutDelayBatch`, with the batch's `RiskScoreMemo`), so a batch of deliveries reads a payee's score, users row and history once:

| Class | Rule | Delay (hours after confirm, env, default) |
|-------|------|-------------------------------------------|
| trusted | computed score 0 (no counted signal), the users row's `createdAt` at least `PAYOUT_TRUSTED_MIN_ACCOUNT_DAYS` (30) days ago, and at least `PAYOUT_TRUSTED_MIN_COMPLETED` (3) `completed` escrows as that party among its newest 50 (one Query on `FarmerStatusIndex` or `TruckerStatusIndex`, the status a filter; 0 skips the read) | `PAYOUT_DELAY_TRUSTED_HOURS`, 24 |
| new | any other computed score: a young account, too little history, a score above 0 at LOW or MEDIUM, or a history read that failed | `PAYOUT_DELAY_NEW_HOURS`, 72 |
| flagged | computed tier HIGH or CRITICAL (a CRITICAL payee's escrow is held by the gate at settlement anyway, section 17.2) | `PAYOUT_DELAY_FLAGGED_HOURS`, 72 |
| unscored | Source before Tier (section 14.4): `Unavailable` or `TableNotConfigured`, a users row that is missing or cannot be read, or no payee to read | the longest of the three |

Each delay is an integer from 0 to 168 (else its default); delays not ordered trusted, new, flagged all fall back to the defaults; read on every stamp, like `RiskScoreSettings`. No Lambda sets them, so with the 48 hour window a trusted payee is paid at the window end and every other payee a day later. A stored value is never read later than 168 hours after `deliveryConfirmedAt` (else `disputeWindowEndsAt`, else the escrow's `createdAt`), and one that does not parse is read as that cap, so a bad row can hold neither the auto-release nor the trucker overdue alarm for good. The policy never throws; a read that fails gives the longer delay.

**Sweeps.** `AutoReleaseAsync` skips a row before its `releaseNotBefore` next to the window filter, before the trade check, the gate, the payout resolution and the lock (run summary `payoutsDelayed`; the row does not count against the sweep's row budget). The trucker retry defers a pending leg before it (reason `payout_delay`), after the stale lock park, the legacy park and the payout gate, and counts the leg as owed from the later of `truckerPayoutPendingSince` and `releaseNotBefore`, so `TRUCKER_PAYOUT_OVERDUE` never counts the delay. Not delayed: the admin release (a held exit included), the dispute sweep (a `farmer_wins` verdict is an admin decision), every refund, the stale lock recovery and the manual payout queue.

**Display.** The escrow reads carry `releaseNotBefore` for the farmer and the trucker while the escrow is `escrowed` (FUP-61 shows a `held` escrow as `escrowed` to an app without the build header, so a held escrow carries no date), for admins always, and never for the buyer (the date can hint at the counterparty's risk, rule of section 17). Mobile and web show "Payout expected on (date)", the day in Ethiopia, while the escrow is `escrowed`, not refund pending and the date is still ahead (once it has passed nothing is shown until the hourly payout lands). The class, the tier and the score are never returned; the log line `PAYOUT_DELAY_SET escrowId=.. releaseNotBefore=.. hours=.. farmer=<class> trucker=<class|->` is server-side only.

**Known limits, kept.** A payee who turns HIGH after the confirm keeps the date stamped then (CRITICAL still holds at the sweep). A trusted farmer waits for a new trucker, and since both see the same date, a trusted payee who sees confirm + 72 hours can infer the other payee is not trusted (per-leg dates need ESC-20). The trucker retry pays a pending leg of an early admin release or of a `farmer_wins` verdict only after the date, since the row does not record who released.

Tests (all in CI): `server/tests/Services/PayoutDelayPolicyTests.cs` (settings, classes, the later payee, the window floor, the stored value), `server/tests/Functions/EscrowCronPayoutDelayTests.cs` (the auto-release before and after, an older escrow, an unreadable value, the row budget, the dispute sweep, the trucker retry and its overdue age, the stamp repair), `server/tests/Services/EscrowServicePayoutDelayTests.cs` (admin release, the trucker retry and the gate, the reads per party), `server/tests/Functions/SyncFunctionPayoutDelayTests.cs` (the sync stamp, one read per payee per batch), `server/tests/Functions/PayoutDelayWiringTests.cs` (the Lambda constructors wire the policy), and the mobile model, repository, widget and l10n tests.

---

## 18. Escrow `held` (FRD-03; the tier trigger FRD-04; the reject trigger FRD-05)

A new escrow status for money frozen pending an admin decision. Contract: `specs/contracts/escrow-protocol.md` 1.7.0, `specs/contracts/order-lifecycle.md` 1.9.0; built in escrow-protocol 1.22.0 and order-lifecycle 1.22.0, the tier trigger in escrow-protocol 1.27.0 and order-lifecycle 1.25.0.

Built by FRD-03 (2026-10-23): the gate seam in the three sweeps (`ISettlementRiskGate`, sections 17.2 and 18.1) with the `account_suspended` trigger (`PartySuspensionGate`, the party's user row, memoized per run, a read failure skips the escrow for the run), the hold write with the order fence (18.2, the transaction; the fallback is not used, so there is no `fraud_hold_reverted`), the admin exits (18.3, admin group and a required `reason` of 10 to 500 characters, audited `fraud_hold_release` and `fraud_hold_refund` before the lock), the lock (18.4), the active sets, the accept refusal and the dispute refusal (18.5), the admin-only fields and the audit rows (18.6), the flag and its skip token with its alarm (18.7). `held` also joins the stale lock recovery and the statuses a manual payout may close from. FRD-04 (2026-10-23) added `risk_critical`, the `score_unavailable` skip and `RISK_TABLE_UNCONFIGURED` to the same gate (section 17); FRD-05 (2026-10-23) added `fraud_confirmed` (section 18.1), escrow-protocol 1.29.0.

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

`suspendReason` is admin free text (`POST /admin/users/{id}/action` stores the request's `reason` as typed). Built by FRD-05: `fraud_confirmed` is a reserved value of it. Only the Fraud Review reject action writes it (section 19); the user action route refuses it as a suspend reason (`400 ADMIN_017`, compared trimmed and case-insensitively), and its `unsuspend` removes `suspendReason`, so the value of a lifted suspension never labels a later one that gives no reason. Not taken: a separate field that only the reject action sets. The gate (`PartySuspensionGate`) compares the value exactly and gives `fraud_confirmed` to the escrow when any party has it, whatever the order of the parties; the other suspended parties are `account_suspended`. Known edge, kept: a later free-text suspend of a user who is still suspended as `fraud_confirmed` replaces the reason, so that user's escrows are held as `account_suspended`; the hold and the money are the same.

Why settlement only: all system money out goes through the three sweeps (`AutoReleaseAsync`, `AutoRefundAsync`, `SettleResolvedDisputesAsync`), so a hold at settlement freezes every system payout and refund of the party without enumerating the party's escrows. Eager holding would page three party GSIs keyed on party plus `createdAt` (`BuyerStatusIndex`, `FarmerStatusIndex`, `TruckerStatusIndex`, status only a filter), add latency and escrow writes to `/sync` or the admin Lambda, and still miss escrows created later. The cost: a party's escrows show "Under review" only once they are due to settle. FRD-05 (built, the reject response, section 19) and FRD-07 show the admin the user's open escrows read-only (the party GSIs with a status filter on `escrowed`, `disputed`, `dispute_resolved` and `held`, paged, admin side).

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
| `release` | `delivered` or left behind its confirmed delivery (auto-release sweep), or no order (a delivery-initiated escrow; the auto-release sweep does not filter by source); an escrow the dispute sweep held from a `farmer_wins` verdict is only released, see below | Allowed, and the progressing-order rule does not apply: refusing it would leave a payout to the flagged party as the only exit (threat T4, a faked delivery by a trucker or colluding parties). The order side follows the list below |

Order side of a `heldFor: release` refund, by the order's status when the admin refunds:

- `delivered`: `delivered` to `disputed` to `dispute_resolved`.
- `disputed`: `disputed` to `dispute_resolved`.
- `accepted`, `pickup_confirmed` or `in_transit`: the best-effort `TryAdvanceOrderForDeliveryAsync` left the order behind its delivery, and `OrderTransitions` has no hop from these to `disputed`. If the order's active delivery is `delivered` (or `disputed`, which it can only be after it was delivered), the order is first advanced to `delivered` the way that advance would have done it (system actor, forward only; a self-pickup order goes from `accepted` straight to `delivered`), and then takes the `delivered` path. If it is not, the refund is refused with `422 ESCROW_INVALID_TRANSITION`, nothing moves, and the alarm token `RISK_HOLD_ORDER_MISMATCH` asks for manual reconciliation.
- `dispute_resolved` with this escrow's `disputeResolution: buyer_wins` already recorded: a retry after the refund itself failed; the order is left as it is and only the refund runs.
- Any other status, or an order escrow whose order is missing: refused the same way (`422`, `RISK_HOLD_ORDER_MISMATCH`).
- No order (a delivery-initiated escrow, no `orderId`): the escrow alone is refunded, with its escrow-audit and transaction-audit rows.

An escrow held from `dispute_resolved` keeps its verdict's direction (decided 2026-10-23 in the FRD-03 review): a `farmer_wins` verdict is only released and a `buyer_wins` verdict only refunded; the other exit is refused with `422 ESCROW_INVALID_TRANSITION`, as for any verdict settlement since ESC-03. The `heldFor: release` refund above is therefore for escrows held from `escrowed` (the auto-release sweep), not for the dispute sweep's `farmer_wins` row of the table. Residual risk (threat T4): a suspended farmer who won a dispute stays `held` until an admin releases the escrow; the admin cannot refund the buyer from `held`. Not taken: overturning the verdict through the verdict mirror (a `buyer_wins` refund over a recorded `farmer_wins`), which would need its own decision on reopening a verdict.

Every order hop is a system-actor write with a version CAS and one `transaction-audit` row, and the hops run before the refund (as cancel-then-refund does today), so a failed refund is retried with the order already in place. With the order hops, and still before the refund, the escrow records `disputeResolution: buyer_wins`, `disputeResolvedBy` (the admin) and `disputeResolvedAt`, the fields `HandleResolveDisputeAsync` writes, while it stays `held`; the refund then moves it `held` to `refunded` directly, never back through `dispute_resolved` (the dispute sweep would hold it again while the party stays CRITICAL). Listing stock is not restored, because the goods left the farmer; ratings stay blocked as after any dispute. ESC-03 built these order hops as `DisputeVerdictMirror` (`server/src/Services/DisputeVerdictMirror.cs`), which the admin verdict and the settlement of a `dispute_resolved` escrow already run; ESC-03 already refuses to settle a `dispute_resolved` verdict when `IsMismatch` is true (`422 ESCROW_INVALID_TRANSITION`, `DISPUTE_VERDICT_ORDER_MISMATCH` alarm); FRD-03 calls it with `buyer_wins` and refuses the refund the same way (both legs are decided before any write, so nothing has moved then), with its own `RISK_HOLD_ORDER_MISMATCH` token. The mirror also stamps the verdict on the delivery, and its mismatch also covers a delivery that cannot follow.

### 18.4 Settlement lock

`EscrowRepository.TryAcquireSettlementLockAsync(escrowId, createdAt, op, expectedStatus)` locks on the exact status the caller validated (ESC-03, escrow-protocol 1.9.0). The settleable statuses are `escrowed` and `dispute_resolved` for every caller (`disputed` left the set with ESC-03: a dispute settles only through a verdict), plus `held` for the admin release and refund only (FRD-03 adds `held` to the set behind a flag that only the admin route sets). ESC-03 fixed the earlier defect where the lock refused `dispute_resolved`; LocalStack tests in `server/tests/Integration/DisputeSettlementDynamoDbTests.cs` evaluate the real condition.

### 18.5 Active set, crons, stamps

- `held` joins `EscrowService.ActiveEscrowStatuses` (one active escrow per order). This is defence in depth, not the only double-charge guard: since ESC-01, `/escrow/initiate` already refuses an order that is funded or carries a Telebirr payment order id (`ESCROW_024`), and the order escrow's deterministic `escrow-order-{orderId}` key with a conditional create prevents a second row. Without `held` in the set, a held order escrow still reads as missing to the lookups that use it, so the `LinkDeliveryToOrderEscrowAsync` self-heal would try to mint an escrow and the active-escrow answers would be wrong. FRD-03 also extends the second, hardcoded active set: the per-delivery check in `EscrowService.InitiateAsync` (`existingStatus is "initiated" or "escrowed" or "released" or "disputed"`), which lacks `dispute_resolved` as well. `LinkDeliveryToOrderEscrowAsync` and `ScheduleRefundForOrderAsync` keep requiring `escrowed`; on `held` they return false and write nothing.
- The crons never select `held`: their queries are status-keyed (`QueryByStatusForCronAsync` on `StatusExpiresIndex`).
- No logistics step meets a held order escrow: a hold happens only once the delivery is confirmed (auto-release; the order is `delivered`, or behind it when the best-effort advance lagged), the order is `disputed` (dispute sweep), or the order is terminal or a stale `funded` order (auto-refund), see section 18.3. A delivery-initiated escrow (no order) is held either by the auto-release sweep, which selects by delivery confirmation and not by source (`heldFor: release`), or by the auto-refund sweep after its 7-day delivery timeout, when today's sweep would already refund it (`heldFor: refund`). The delivery linkage (`LinkDeliveryToOrderEscrowAsync`) and the delivery-confirmation stamps (`SyncFunction.StampEscrowDeliveryConfirmedAsync`, the cron's `IsDeliveryConfirmedAndStampAsync`) keep requiring `escrowed`. The one gap is a stale `funded` order whose escrow is held for refund: the farmer could still accept it, and a delivery created after that could never link to the held escrow or get its stamp. FRD-03 therefore refuses the farmer's `accepted` on an order whose escrow is `held`, with `ORDER_VALIDATION: This order is under review and cannot be accepted. Contact support.` (an existing code; it names no party). Reject and cancel stay allowed; the admin refund then finds a terminal order. With `FRAUD_HOLD_ENABLED` on (the sync Lambda reads the same flag), the accept is one `TransactWriteItems`: the versioned order update and a `ConditionCheck` that the order escrow is not `held`, so a sweep that holds it between the sync Lambda's read and its write wins and nothing is written; with the flag off nothing is ever held and the accept keeps its plain write.
- A dispute on a held escrow is refused (`ESCROW_INVALID_TRANSITION`).

### 18.6 Visibility and audit

Parties (buyer, farmer, trucker) see `status: held` only, labelled "Under review" in all three languages. `holdReason`, `heldBy`, `heldFromStatus` and `heldFor` are admin-only and absent from party responses. Party scoping of escrow reads is unchanged.

Audit actions, written to escrow-audit and transaction-audit with tier and score in the metadata: `fraud_hold` (system), `fraud_hold_release` and `fraud_hold_refund` (admin), and `fraud_hold_reverted` (system) only if the fallback of section 18.2 is used. Not `hold`: escrow-audit already uses `hold_success`, `hold_pending` and `hold_failed` for the Telebirr hold, and the admin UI labels `action_hold` as "Funds held".

### 18.7 Flag and client compatibility

`FRAUD_HOLD_ENABLED` (default false). With the flag off, a sweep that finds a trigger skips the escrow (status unchanged, no money moves, alarm token `RISK_SETTLEMENT_SKIPPED` with `reason=flag_off`) and retries next hour, which every shipped client tolerates. Decided 2026-10-23 in the FRD-03 review: the skip stays (a suspended party is never paid by a sweep), and the token is alarmed from the FRD-03 deploy (`risk-settlement-skipped`, any line in a 75 minute window), so a settlement that waits is seen; the exits are to unsuspend the party or to settle by hand as an admin in the admin group. Not taken: log only and settle as today until FRD-06, FRD-07 and FRD-16 ship.

Escrow `status` is an open set for clients: a client must not fail on a value it does not know; it shows a neutral state with no actions and keeps the raw value. Today's clients do not meet that rule. The mobile `EscrowRepository` (three response mappers) and `EscrowSyncService` parse the status with `PaymentStatus.values.byName`, which throws `ArgumentError` on an unknown value (not `ApiException`, so a history refresh does not fall back to the cache). `PaymentStatus` in `core/database/schemas/payment.dart` and `core/models/web/payment.dart` has neither `held` nor `dispute_resolved`. `WebEscrowRepository._parsePaymentStatus` maps an unknown value to `initiated`. Pre-existing consequence: every party of a `dispute_resolved` escrow already gets a failing escrow detail and history on mobile.

FRD-06 (mobile and web together) appends `held` to `PaymentStatus` in both files (append only; Isar stores the enum by name), replaces `byName` with a tolerant parser in the repository and the sync service, stops the web parser from defaulting to `initiated`, covers `held` in the exhaustive switches of `escrow_status_badge.dart` and `escrow_timeline.dart`, and adds "Under review" in en, am and om.

Ordering: FRD-03 may merge with `FRAUD_HOLD_ENABLED=false`. The flag is turned on only after FRD-06 is deployed on web and released to the stores, and after one of these is live for clients older than FRD-06: a minimum app version gate that also covers escrow reads (`GET /escrow/{id}`, `GET /escrow/by-delivery/{deliveryId}`, `GET /escrow/history`), or a server-side mapping that answers those clients with a status they parse (`escrowed`) instead of `held`. ESC-25 as written gates order commands only, so on its own it does not protect the escrow reads where un-updated installs fail (they already fail the same way on `dispute_resolved`).

Built by FUP-61 (2026-10-23, escrow-protocol 1.31.0): the server-side mapping. No app sent its version before, and `mobile/pubspec.yaml` had said `0.1.0+1` since the first build, so an FRD-06 install could not be told from an older one. The server reads `X-App-Build` (the pubspec build number); the apps send it on every request from build 2, a separate FUP-61 mobile change that also moves the pubspec to `+2`. Until that build is out, every client (FRD-06 installs and the deployed web app included) reads the mapped values. On the party escrow routes (`GET /escrow/{id}`, `GET /escrow/by-delivery/{deliveryId}`, `GET /escrow/history`, and the initiate and dispute replays that re-read the row) an app with no header, an unreadable one or a build below 2 reads `held` as `escrowed` and `dispute_resolved` as `disputed` (the value FRD-06 already shows for it); build 2 and later read the stored value. Server: `ClientAppBuild` and `EscrowStatusCompat` (`server/src/Helpers`), applied in `EscrowFunction` to the answer only, so the stored row, the history `status=` filter, the audit, the crons and the admin routes keep the stored value. Decided: a missing header counts as old (fail safe), so FRD-06 installs without the header also read `escrowed` until they update; `dispute_resolved` is mapped too. Accepted: an escrow the dispute sweep holds from `dispute_resolved` reads `disputed` to an old app before the hold and `escrowed` after it (the money stays frozen; an old app's dispute is refused and release and refund are admin-only). Left raw (follow-ups): the `POST /escrow/dispute` refusal text, the `/sync` delivery dispute refusal text and the account deletion `409` blockers name the stored status. Not taken: a minimum version gate on the reads (it would lock un-updated installs out of their payments instead of showing them a status they parse). `FRAUD_HOLD_ENABLED` is on in dev for sync and escrow-cron (`fraudHoldEnabled` in `infra/lib/config/environments.ts`), off in staging and prod; with it on, a dev sweep holds the escrows of suspended, fraud-confirmed or CRITICAL parties instead of skipping them. The API's CORS allow-list carries `X-App-Build`, and must be deployed before a web build that sends it.

Naming: escrow `status: held` (frozen for review) is unrelated to the three-party design's `legStatus` value `held` (money still in escrow), to the Telebirr hold, and to reputation tiers. Until per-leg status lands, a hold covers the whole escrow.

### 18.8 Tests FRD-03 owns

A held escrow is never selected by any cron; `held` is in the active set (`ESCROW_024` still fires); the lock accepts `dispute_resolved` and, for the admin only, `held`; admin release from `held`; admin refund from `held` for `heldFor: refund` with the order rules and for `heldFor: release` with the order moving to `dispute_resolved` (from `delivered` and from `disputed`), no stock restore and `disputeResolution: buyer_wins`; the conditional write loses to a settlement lock; an accept that lands between the auto-refund sweep's order read and the hold write cancels the hold transaction, nothing is held and the next sweep pushes the expiry; the `heldFor: release` refund from an order left at `in_transit` advances it when the delivery is `delivered` and is refused with `RISK_HOLD_ORDER_MISMATCH` when it is not; a retry after a failed refund on a `dispute_resolved` order refunds only; a delivery-initiated escrow is refunded with no order write; flag off skips with the alarm token; a party read failure skips with the alarm token; the farmer's accept of a funded order whose escrow is `held` is refused with `ORDER_VALIDATION`; a dispute on `held` is refused; order and delivery views show "Under review" for the buyer, the farmer and the trucker.

---

## 19. Admin Actions (FRD-05, built)

`GET /admin/fraud-signals` (`AdminFunction.HandleListFraudSignalsAsync`) is how Fraud Review reads the signals. Query parameters: `status` (optional; filters on the row's `status`, e.g. `pending` or `reviewed`), `limit` (default 20) and `nextToken` (opaque, the base64url of the DynamoDB last-evaluated key from the previous page). Response `AdminFraudSignalListResponse`: `{"signals": [FraudSignalDto, ...], "nextToken": "<token, omitted on the last page>", "total": <count on this page>}`, each row carrying its `resolution`. The scan returns signal rows only (`attribute_not_exists(recordType) OR recordType = signal`), so the credit and whitelist rows of section 20 never show, and a `status` filter that empties a page still reads on past pages, as the other filtered admin lists do since FUP-16. Without the table configured it answers an empty list. FRD-07 replaces the scan with a sparse `status` GSI.

`POST /admin/fraud-signals/{id}/action` (`AdminFunction.HandleFraudActionAsync`), body `{"action": "approve" | "reject" | "whitelist", "notes": "..."}` (`notes` optional), `id` the base64url of `userId|signalKey`. Before FRD-05 it stamped `status: reviewed`, `resolution`, `reviewedBy`, `reviewedAt` and `reviewNotes` and wrote a transaction-audit row (`fraud_{action}`), with no other side effect, and stored any string as the resolution (which the score counts, section 14.2).

Built by FRD-05 (2026-10-23), in this order:

1. The body (`400 ADMIN_005`), then the action: anything but `approve`, `reject` or `whitelist`, exactly (`Approve` included), is `400 ADMIN_009` (the unknown-action answer of the user action route) and nothing is written. Then the id (`400 ADMIN_005`).
2. A consistent read of the row: a missing row, a credit row or the whitelist row (anything but no `recordType` or `recordType = signal`) is `404 ADMIN_006` and nothing is written.
3. The action's side effect: the suspension for `reject`, the whitelist row for `whitelist`, none for `approve`.
4. The review stamp, still conditional on `attribute_exists(userId) AND (attribute_not_exists(recordType) OR recordType = signal)` (a row gone since the read is `404 ADMIN_006`; the side effect stands), then its transaction-audit row (`fraud_approve`, `fraud_reject`, `fraud_whitelist`; entity `fraud`, the signal id; metadata the notes, for `whitelist` `whitelistUntil=<ISO>` then `;notes=<notes>`) and the success log.

The side effect comes before the stamp so a failure fails closed and stays retryable: a throttle or a timeout in the suspension or the whitelist write answers `500 ADMIN_500` with the signal still `pending`, so Fraud Review offers the action again, and the retry repeats the side effect (the same suspension, the same whitelist row written in place) and then stamps. The other order would leave a reviewed signal with a user who is not suspended, whose escrows the next sweep pays, or a cleared signal without its whitelist row.

| Action | Effect on the score | Other side effects |
|--------|---------------------|--------------------|
| `approve` | The signal stops counting (section 14.2) | none |
| `reject` | The signal keeps counting | Before the stamp, suspends the user through the one suspend path of the user action route (`ApplyUserActionAsync`) with `suspendReason: fraud_confirmed`: the profile row (`suspended: true`), Cognito disable and global sign-out, `AUTH_025` on the next refresh, and that path's `user_suspend` row (metadata `fraud_confirmed`), then the stamp and `fraud_reject`. Holds nothing; the user's escrows are held by the next sweep that would settle them (section 18.1). The response lists the user's open escrows read-only, read after the stamp |
| `whitelist` | The signal stops counting, whatever its class (section 14.2). Before the stamp, writes the `~whitelist` row (section 20) with `whitelistUntil` now plus `FRAUD_WHITELIST_DAYS` (section 14.5); soft-class weights stop counting while it is active, and count again after it. A new whitelist replaces the old row | none |

Response (`200`), the fields before FRD-05 (`signalId`, `status: reviewed`, `reviewedAt`, `message`) plus:

| Field | When | Meaning |
|-------|------|---------|
| `resolution` | always | The action applied |
| `userId` | always | The user the signal belongs to |
| `suspended`, `suspendReason` | `reject` | `true`, `fraud_confirmed` |
| `sessionsEnded` | `reject` | `false` when Cognito could not be updated: the suspension stands and a refresh is refused (`AUTH_025`), but live tokens work until they expire |
| `openEscrows` | `reject` | `[{escrowId, status, party, amount, orderId, createdAt}]`, `party` the user's side (`buyer`, `farmer`, `trucker`) |
| `openEscrowsComplete` | `reject` | `false` when the read stopped at a bound or failed (log `FRAUD_REJECT_OPEN_ESCROWS_UNREAD`); the suspension stands either way |
| `whitelistUntil` | `whitelist` | ISO 8601 |

The open escrows are read from the three party GSIs (`BuyerStatusIndex`, `FarmerStatusIndex`, `TruckerStatusIndex`, every one whatever the user's role, so a changed role keeps older escrows in view) with `status IN (escrowed, disputed, dispute_resolved, held)`, 100 rows a page, at most 5 pages per index and 50 escrows, an escrow that names the user twice listed once.

Other answers: `503 ADMIN_006` without the table; `404 ADMIN_007` on `reject` when the user row is missing: nothing is written and the signal stays `pending` (it counts all the same); `approve` or `whitelist` can still close it. Reviewing a reviewed signal again is allowed (the admin app offers no buttons on reviewed rows); an `approve` after a `reject` does not lift the suspension, the user page does.

`POST /admin/users/{id}/action` (FRD-05): `suspend` with the reason `fraud_confirmed` is `400 ADMIN_017`; `unsuspend` removes `suspendReason` (section 18.1).

Known limits, kept:

- A whitelist whose row was written but whose stamp then failed leaves the `~whitelist` row in place with the signal pending. If the admin then approves or rejects that signal instead of retrying the whitelist, the soft-signal bypass still lasts until `whitelistUntil`; nothing removes the row early (a later whitelist replaces it).
- A `reject` whose stamp finds the row gone (the TTL deleted it between the read and the stamp) answers `404 ADMIN_006` although the user is already suspended. The suspension stands and shows on the user page; it fails closed.

Admin surfaces until FRD-07: the dashboard count (`HandleDashboardStatsAsync`, `FraudSignalsCount`) and the list (`HandleListFraudSignalsAsync`, with its optional `status` filter) exclude credit and whitelist rows with `attribute_not_exists(recordType) OR recordType = signal` (built by FRD-05; the filtered list reads on past pages the filter emptied, as the other filtered admin lists do since FUP-16). The list rows carry `resolution`. FRD-07 replaces the Scans with a sparse GSI on `status`, which excludes those rows by construction because they carry no `status`.

The admin app's Fraud Review page (FRD-05): `reject` asks once more (it suspends the account); `whitelist` is offered on soft-class signals only (`GPS_ANOMALY`, `PRICE_ANOMALY`, section 15), because it clears the signal it is given for good and skips only soft signals until its end, so on a hard signal it would quietly erase hard weight (the server still accepts it there; approve is the admin's tool for a hard false positive); after each action a panel shows the result: approve no longer counts, the user suspended (with a warning when `sessionsEnded` is `false`) and the open escrows with the note that the next sweep holds them (and a hint when `openEscrowsComplete` is `false`), or that the whitelisted signal no longer counts and the soft signals do not count until the end; reviewed rows show the decision. Its error texts for `ADMIN_006` (404) and `ADMIN_007` are its own (`fraud.error_*`), so the shared `errors.*` keys of other pages keep their wording. English, Amharic and Afan Oromo.

No IAM change: the admin Lambda already reads the escrows table and its indexes, writes the fraud-signals, users and transaction-audit tables, and holds `AdminDisableUser` and `AdminUserGlobalSignOut` on the mobile pool (pinned in `infra/test/unit/stack.test.ts`).

---

## 20. Data Model Additions

The fraud-signals table keeps its keys (`userId`, `signalKey`) and its TTL attribute. The TTL attribute name `expiresAt` (Number, epoch seconds) is never renamed: the table is live, and a changed TTL attribute name rolls the deploy back.

| Row kind | `recordType` | `signalKey` | `status` | Other fields |
|----------|--------------|-------------|----------|--------------|
| Signal | absent (legacy, still written so) or `signal` | `{ISO}#{signalType}` | `pending` or `reviewed` | as section 4.3 |
| Credit (FRD-10) | `credit` | `{ISO}#CLEAN_DELIVERY` | absent | `weight: -5`, `entityType: escrow`, `entityId` |
| Whitelist (FRD-05, built) | `whitelist` | `~whitelist` (one per user; sorts after every ISO key) | absent | `whitelistUntil` (ISO), `expiresAt` set to the same instant in epoch seconds, `reviewedBy`, `reviewedAt`, `reviewNotes` (when given) |

Credits (FRD-10): when an escrow completes through auto-release with no dispute, no hold and no signal on its order, delivery or escrow, each party gets a credit row: the buyer, the farmer, and the trucker when present. The buyer is included because a paid, received and undisputed trade is the same evidence for every party. Self-trade farming is bounded by `RISK_CREDIT_CAP` and later caught by FRD-15.

FRD-07 adds a sparse GSI on `status` (partition `status`, sort `createdAt`) so the admin list and the held queue stop scanning.

Escrows table (FRD-13, built): `releaseNotBefore` (ISO 8601), stamped with `deliveryConfirmedAt` and `disputeWindowEndsAt` at delivery confirm, the earliest time the auto-release may pay the escrow out (section 17.5). The class behind it is not stored.

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
| `AUTH_040` | step-up | auth-api 2.1.0 | `StepUpPolicy` (live); the MEDIUM and HIGH use built by FRD-04 | step-up dialog, mobile and web (live) | auth-api 2.6.0, built 2.10.0 |
| `AUTH_041` | step-up | auth-api 2.4.0 | `StepUpPolicy.CheckPayment` (live); the HIGH use and the amount-neutral text built by FRD-04 | Fayda step-up flow (live); FRD-06 rewords `faydaStepUpMessage` | auth-api 2.6.0, built 2.10.0 |
| `FRAUD_ACCOUNT_HELD` | rejection | FRD-04 | FRD-04 (server, built: `RiskGatePolicy`, `SyncFunction`, `EscrowFunction`) | FRD-06 (mobile and web, three languages; `SyncExecutor` revert hook for `FRAUD_ACCOUNT_HELD` on `payment_submitted`); mobile sync already treats it as permanent | sync-protocol 1.9.0 (built 1.25.0), escrow-protocol 1.7.0 (built 1.27.0), order-lifecycle 1.9.0 (built 1.25.0), auth-api 2.6.0 (built 2.10.0), crop-listing-api 1.5.0, trip-listing-api 1.6.0 |
| escrow `held` | status | escrow-protocol 1.22.0 | FRD-03 (server, infra; behind `FRAUD_HOLD_ENABLED`, shipped off; built with the `account_suspended` trigger); the tier trigger built by FRD-04 (`risk_critical`, escrow-protocol 1.27.0); the reject trigger built by FRD-05 (`fraud_confirmed`, escrow-protocol 1.29.0) | FRD-06 tolerant parser and badge (must ship before the flag is on); apps before build 2 read `escrowed` (FUP-61, built; flag on in dev); admin vocabulary with FRD-03 (next row); held queue FRD-07 | escrow-protocol 1.7.0, order-lifecycle 1.9.0, escrow-protocol 1.31.0 (old apps) |
| escrow `held` in the admin app (`admin/src/lib/transaction.ts` `escrowStatusVariant`, `admin/src/pages/EscrowsPage.tsx` `statusVariant`, i18n en, am, om: `escrows.held`, a new `transactions.escrow_held`, `action_fraud_hold`, `action_fraud_hold_release`, `action_fraud_hold_refund`) | admin UI | FRD-03 | FRD-03 (the admin app is where held escrows are resolved, so it ships with the status) | FRD-07 adds the held queue and a dashboard held count | none |
| `fraud_confirmed` | `suspendReason` and `holdReason` value | FRD-05 | the Fraud Review reject action (`AdminFunction`, built); refused as free text on the user action route | `PartySuspensionGate` (built, first in precedence); `risk-settlement-skipped` alarm text | escrow-protocol 1.29.0 |
| `ADMIN_009` and `ADMIN_006` on `POST /admin/fraud-signals/{id}/action`, `ADMIN_017` on `POST /admin/users/{id}/action` | rejection | FRD-05 | `AdminFunction` (built) | admin app, en, am, om (built) | this document (section 19) |
| `FRAUD_DUPLICATE_LISTING`, `FRAUD_VELOCITY_EXCEEDED`, `ESCROW_011` | rejection | F-14 | live | live | sync-protocol, escrow-protocol |
| `IDOR_CREATE_CONFLICT`, `ROLE_FORBIDDEN`, `IMMUTABLE_FIELD_CHANGE`, `QR_HANDOFF_FAILURE`, `TELEBIRR_PAYER_MISMATCH`, `TELEBIRR_PAYER_REFUSED`, `STALE_VERSION_BURST`, `SUSPENDED_TOKEN_USE` | signals (weights in section 15) | FRD-08 | FRD-08 (server, built: `SyncFunction`, `EscrowFunction`, `TelebirrWebhookFunction`, `RefreshFunction`; types and weights in `FraudDetectionService`) | admin Fraud Review (live, raw type); the risk score of section 14 | sync-protocol 1.27.0, escrow-protocol 1.32.0 |
| `CONTACT_THEN_TRANSPORT_CANCEL` | signal (weight 10, default) | ESC-48 | `SyncFunction.ProcessTransportFailureCancelAsync` (live, best-effort) | admin Fraud Review (live, raw type); the risk score of section 14 (FRD-02, default weight, hard) | order-lifecycle 1.18.0 |
| escrow `releaseNotBefore`, `PAYOUT_DELAY_SET` (log line, not alarmed), the `payout_delay` trucker retry reason, `payoutsDelayed` in the run summary | field, log, reason | FRD-13 | FRD-13 (built: `PayoutDelayPolicy`, the sync stamp and the escrow-cron stamp repair) | the auto-release and the trucker retry (built); mobile and web "Payout expected on (date)" for the farmer and the trucker (built); never the buyer | escrow-protocol 1.33.0, order-lifecycle 1.27.0, delivery-verification-api 1.19.0 |
| `RISK_SCORE_UNAVAILABLE`, `RISK_SCORE_TRUNCATED`, `RISK_SCORE_UNKNOWN_RECORD`, `RISK_SETTLEMENT_SKIPPED` (`reason=flag_off`, `score_unavailable` or `user_read_failed`), `RISK_TABLE_UNCONFIGURED`, `RISK_HOLD_ORDER_MISMATCH` | log alarm tokens | FRD-02 (first three); new (others) | FRD-02 (first three, `FraudDetectionService.RiskScoreUnavailableToken`, `RiskScoreTruncatedToken` and `RiskScoreUnknownRecordToken`; called since FRD-04), FRD-03 (built: `RISK_SETTLEMENT_SKIPPED` with `flag_off` and `user_read_failed`, and `RISK_HOLD_ORDER_MISMATCH`), FRD-04 (built: `RISK_SETTLEMENT_SKIPPED` with `score_unavailable`, and `RISK_TABLE_UNCONFIGURED`, `MoneyAlarmLog.RiskTableUnconfigured`, logged flag on or off) | `RISK_SETTLEMENT_SKIPPED`: metric filter and alarm `risk-settlement-skipped` on escrow-cron from the FRD-03 deploy, flag on or off; the others FRD-16 metrics and alarms (`RISK_TABLE_UNCONFIGURED` is logged flag on or off since FRD-04, so its alarm can count from FRD-16's deploy rather than from the day `FRAUD_HOLD_ENABLED` is turned on) | this document only |

Contract numbers are the ones taken by FRD-01 and may be renumbered by the lead in merge order. Ordering: FRD-02 first; FRD-03 merges with the flag off; FRD-06 ships on web and in both stores; then the flag is turned on (section 18.7). FUP-61 answers older apps `escrowed` and turns the flag on in dev; staging and prod follow once FRD-06 is in both stores there.

---

## 23. Implementation Map

| Task | Touches |
|------|---------|
| FRD-02 (built) | `IFraudDetectionService.GetUserRiskScoreAsync` and its result type (score, enforcement score, tier, `truncated`), constants and env overrides in `FraudDetectionService`, memoization helper, unit tests (section 14.7). Delivered as `FraudDetectionService.GetUserRiskScoreAsync`, `RiskScoreResult` in `FraudDtos.cs`, `RiskScoreCalculator.cs` (with `RiskScoreSettings`), `RiskScoreMemo.cs`; no infra change, since the read uses the table's own keys and the Lambdas that have `FRAUD_SIGNALS_TABLE` already hold `dynamodb:Query` through `grantReadWriteData` |
| FRD-03 | `held` write in the three sweeps of `EscrowCronFunction`, admin exits in `EscrowFunction` and `EscrowService` (`ReleaseAsync`, `RefundAsync` with the order rules), `TryAcquireSettlementLockAsync` set and admin flag (and the `dispute_resolved` fix), `ActiveEscrowStatuses` and the per-delivery active check in `InitiateAsync`, the order-version fence on the auto-refund hold, the `heldFor: release` refund with the order hops to `dispute_resolved`, the accept refusal on a funded order whose escrow is held, dispute refusal, audit actions, the sweep alarm tokens, `FRAUD_HOLD_ENABLED`; admin status vocabulary; infra (next table) |
| FRD-04 (built) | Risk gate in `SyncFunction` (order `payment_submitted`, crop, trip and order create), `EscrowFunction` (`HandleInitiateAsync`, `HandleOrderCheckoutAsync`), the settlement gate of section 17.2, amount-neutral `AUTH_041` text, `FRAUD_ACCOUNT_HELD`. Delivered as `RiskGatePolicy` (new), `StepUpPolicy.CheckPayment` with the tier and `PaymentNeedsFayda`, the tier pass of `PartySuspensionGate` (`ISettlementRiskGate.ReadsRiskTier`, `SkipScoreUnavailable`), the score in `SettlementGateDecision` and the `fraud_hold` metadata (`EscrowService.HoldForReviewAsync`), `MoneyAlarmLog.RiskTableUnconfigured`; infra: escrow-cron `FRAUD_SIGNALS_TABLE` with `grantReadData`, the `risk-settlement-skipped` alarm text |
| FRD-05 (built) | `HandleFraudActionAsync` side effects, `~whitelist` row, `suspendReason: fraud_confirmed`, dashboard and list filters, transaction-audit rows, admin UI. Delivered as the action validation, the consistent signal-row read (`IsSignalRowAsync`) before the side effect and the conditional stamp after it, `ApplyUserActionAsync` (the suspend path the user route and the reject share), the reserved reason (`ADMIN_017`) and the `unsuspend` cleanup, `WriteWhitelistAsync`, `ReadOpenEscrowsAsync`, `ScanFilter.SignalRowsOnly` on the list and the count, `FraudSignalDto.Resolution` and the `FraudActionResponse` fields, `UserDto.SuspendReason` (server side only), `fraud_confirmed` in `PartySuspensionGate`, `FraudDetectionService.WhitelistDaysFrom`; the Fraud Review page; no infra change but the `risk-settlement-skipped` alarm text |
| FRD-06 | `PaymentStatus` `held` on mobile and web, tolerant status parsers, badge and timeline, "Under review" and `FRAUD_ACCOUNT_HELD` strings in en, am, om, `SyncExecutor` revert hook for `FRAUD_ACCOUNT_HELD` on `payment_submitted` (and its message in the batch result), `AUTH_041` re-auth retry on web repositories |
| FUP-61 (server built) | Old apps on the escrow reads (section 18.7): `ClientAppBuild` and `EscrowStatusCompat` applied in `EscrowFunction`; infra: `fraudHoldEnabled` per environment (dev on) for sync and escrow-cron, `X-App-Build` in the CORS allow-list. Separate mobile change: `X-App-Build` from `ApiConfig.appBuild` in the `ApiClient` base headers (mobile and web), pubspec `+2`; until it ships every client reads the mapped values |
| FRD-07 | Sparse `status` GSI on fraud-signals, grouped Fraud Review with score and tier, held queue, transaction timeline link |
| FRD-08 (built) | Signals from existing rejects plus the per-user, per-type write rate limit. Delivered as the types, weights and soft classes in `FraudDetectionService` (`SignalWeight`, `IsSoftSignal`), the hour-start key and conditional put in `RecordFraudSignalAsync`, `SyncFunction.RefuseWithSignalAsync` and `RoleGateSignal`, the per-batch memo in `TryRecordFraudSignalAsync`, the stale-version counter in `ProcessOperationAsync`, `EscrowFunction.TryRecordRoleForbiddenAsync`, `TelebirrWebhookFunction.TryRecordPayerMismatchAsync` and `NormalizePayerPhone`, `RefreshFunction.TryRecordSuspendedTokenUseAsync`, the per-entity count in `RiskScoreCalculator` (`FraudDetectionService.CountsOncePerEntity`, `entityId` in the score Query's projection); infra (next table) |
| FRD-09 | Transaction pattern rules with thresholds in config |
| FRD-10 | `CLEAN_DELIVERY` credit rows from the auto-release sweep |
| FRD-13 (built) | The payout delay of section 17.5. Delivered as `PayoutDelayPolicy` and `PayoutDelaySettings` (new), `releaseNotBefore` in `SyncFunction.StampEscrowDeliveryConfirmedAsync` and `EscrowCronFunction.StampIfDeliveredAsync`, the filter in `AutoReleaseAsync`, the deferral in `EscrowService.RetryTruckerLegAsync` and the overdue age in `RetryPendingTruckerLegsAsync`, `EscrowDto.ReleaseNotBefore` and `EscrowSummaryDto.ReleaseNotBefore` (payees and admins), the mobile and web "Payout expected on (date)"; no infra change (the defaults live in code; the sync Lambda and escrow-cron already read the users, fraud-signals and escrows tables with their indexes) |

Infrastructure needed (`infra/lib/stacks/api-stack.ts`):

| Lambda | Today | Needed |
|--------|-------|--------|
| `EscrowCronFunction` | `USERS_TABLE` read; `TRANSACTION_AUDIT_TABLE` write and `FRAUD_HOLD_ENABLED` (FRD-03); `FRAUD_SIGNALS_TABLE` with read (FRD-04 gate, built) | `FRAUD_SIGNALS_TABLE` write (FRD-10 credits) |
| `EscrowFunction` | `FRAUD_SIGNALS_TABLE` read and write | `TRANSACTION_AUDIT_TABLE` with write (FRD-03 admin exits) |
| `SyncFunction`, `AdminFunction` | `FRAUD_SIGNALS_TABLE` read and write, `TRANSACTION_AUDIT_TABLE` | no change for FRD-02 to FRD-05 |
| `RefreshFunction` | `USERS_TABLE` read, `RATE_LIMITS_TABLE` | `FRAUD_SIGNALS_TABLE` with `dynamodb:PutItem` only (FRD-08, built) |
| `TelebirrWebhookFunction` (only with `enableTelebirrLive`) | escrows, escrow audit, idempotency, users, orders, crops, transaction audit | `FRAUD_SIGNALS_TABLE` with `dynamodb:PutItem` only (FRD-08, built) |
| fraud-signals table | keys and TTL only | FRD-07 sparse GSI on `status` |

Notes for FRD-04, from the FRD-02 review (all handled by FRD-04):

- `EscrowCronFunction` had no `FRAUD_SIGNALS_TABLE` environment variable, no grant on the table and no `FraudDetectionService`. FRD-04 adds all three (the service built only when the variable is set, with a rate-limit repository that names no table because the cron never runs the velocity checks) and a `RiskScoreMemo` per sweep run.
- `EscrowFunction` builds its fraud service only when `RATE_LIMITS_TABLE` is set (`_fraudService` is nullable). FRD-04 decided: no service, no tier, LOW (section 17).
- A `Source = Unavailable` result carries `Tier = Low` (section 14.4). Every caller checks `Source` first; FRD-04 owns the sweep test that an unscored party skips the escrow (`server/tests/Functions/EscrowCronRiskGateTests.cs`).

Ties to EPIC ESC: ESC-33 writes `NO_SHOW` and `DELIVERY_OVERDUE` signals and re-attributes `GPS_ANOMALY`; the client condition for turning the hold flag on is met by FUP-61's server mapping rather than a minimum app version gate (section 18.7), and ESC-25 can reuse its `X-App-Build` header; design cases H1 and H4 of the three-party escrow design (a party suspended mid-order, a buyer suspended after funding) resolve to this `held` status. The account deletion contract (`specs/contracts/account-deletion.md`, ACC-01, not built yet) refuses deletion while the user is a party to a `held` escrow, because any status it does not list blocks, and refuses self-deletion of a suspended user; its open point on ban evasion stays with EPIC FRD, since a CRITICAL user who is not suspended and has no open escrow can still delete the account.

---

## Changelog

| Version | Date | Change |
|---------|------|--------|
| 3.11.0 | 2026-10-28 | LT-07 built (live-trip-api 1.0.3, notifications-api 1.3.0, delivery-verification-api 1.20.0): two check-in signal types in section 15, `GPS_JUMP` (10, soft) and `GPS_MOCKED` (25, soft), written best effort on the trip's trucker with the trip as the entity and counted once per (type, trip) like the FRD-08 types (a trip with a mock-location app on, or a phone with a bad GPS, writes a row in every hour it drives), so one trip alone stays LOW; flag-only, never a refusal: the trail is supporting evidence and the QR scans prove the handoffs |
| 3.10.0 | 2026-10-23 | FRD-13 built (escrow-protocol 1.33.0, order-lifecycle 1.27.0, delivery-verification-api 1.19.0): new section 17.5, the payout delay. Delivery confirm stamps `releaseNotBefore = max(disputeWindowEndsAt, deliveryConfirmedAt + delay)`, the longer of the farmer's and the trucker's delay; classes trusted (score 0, account 30 days, 3 completed escrows), new, flagged (HIGH or CRITICAL) and unscored (Source before Tier, a failed or missing read; the longest delay); delays 24, 72 and 72 hours by default, env from 0 to 168; the auto-release and the trucker retry wait for it, the overdue age counts from it; admin release, the dispute sweep and refunds are not delayed; the escrow reads carry the date for the payees (while `escrowed`, so the FUP-61 old-app mapping of `held` never shows one) and admins, never the buyer; sections 16, 17 (matrix and rules), 20, 22 and 23 updated |
| 3.9.0 | 2026-10-23 | FRD-08 built (sync-protocol 1.27.0, escrow-protocol 1.32.0): eight signal types from existing rejects in section 15 with weights and classes (hard: `IDOR_CREATE_CONFLICT` 40, `ROLE_FORBIDDEN` 25, `IMMUTABLE_FIELD_CHANGE` 25; soft: `QR_HANDOFF_FAILURE` 5, `TELEBIRR_PAYER_REFUSED` 5, `TELEBIRR_PAYER_MISMATCH` 0, `STALE_VERSION_BURST` 0 at 10 conflicts per batch, `SUSPENDED_TOKEN_USE` 0), the honest-client reasoning, residual risks and what is not a source; the scorer counts these types once per (type, entityId) over the window (14.2, a retried refusal counts once), except `IDOR_CREATE_CONFLICT` (hourly, uncapped); an approved row clears its whole entity; `ROLE_FORBIDDEN` and `IMMUTABLE_FIELD_CHANGE` capped per type at `RISK_REJECT_TYPE_CAP` (50, 14.5) with the early stop counting only weight that cannot be taken back; the per-user, per-type write limit (one row per hour, hour-start key, conditional put, every producer) in 14.3, its effect on the burst rows in 14.2 and on approved rows; the payer phone in Telebirr's `251` form normalized; the webhook attribution rule (a verified payer mismatch on our own buyer only, a failed signature writes nothing); sections 22 and 23, refresh and webhook grants |
| 3.8.0 | 2026-10-23 | FUP-61 built (escrow-protocol 1.31.0): section 18.7, the server mapping for apps older than FRD-06. The server reads `X-App-Build` (pubspec build number; the apps send it from build 2, a separate mobile change, and until then every client reads the mapped values); an app without it or below build 2 reads `held` as `escrowed` and `dispute_resolved` as `disputed` on the party escrow reads and the initiate and dispute replays; answers only, stored values everywhere else. Decided: a missing header is old, `dispute_resolved` mapped too (accepted: a hold from `dispute_resolved` reads `disputed` then `escrowed` to an old app); not taken: a minimum version gate on the reads; left raw: dispute refusal texts and the account deletion blockers. `FRAUD_HOLD_ENABLED` on in dev for sync and escrow-cron, off in staging and prod; CORS allows the header. Status lines and sections 22 and 23 updated |
| 3.6.0 | 2026-10-23 | FRD-05 built (escrow-protocol 1.29.0): section 19 marked built, with the request, the order of the checks (`400 ADMIN_009` for anything but `approve`, `reject` or `whitelist` exactly, `404 ADMIN_006` for a non-signal row on a consistent read, both before any write; the side effect before the review stamp, so a failure leaves the signal pending and retryable), the whitelist offered on soft signals only in the admin app, the side effects (reject suspends through the user route's one suspend path with `suspendReason: fraud_confirmed` and lists the open escrows read-only, bounded, holding nothing; whitelist writes the `~whitelist` row), the response fields and the transaction-audit rows; section 18.1: `fraud_confirmed` is a reserved `suspendReason` value (`400 ADMIN_017` on the user route, `unsuspend` removes the reason; not taken: a separate field), first in the gate's precedence across parties, with the known edge of a later free-text suspend; 14.1, 14.2, 14.5 (`FRAUD_WHITELIST_DAYS` from 1 to 365, else 30); section 20 whitelist row built (`reviewedAt` added); list and dashboard exclude credit and whitelist rows; section 19 known limits (a leftover whitelist row after a failed stamp, a 404 after a suspension when the TTL removed the row); 17.1 names ACC-02's checks (the `/sync` batch and escrow-route `410 ACCOUNT_004`, the deleted-party `ORDER_VALIDATION` and `ESCROW_030`); sections 4.3, 22 and 23 updated |
| 3.5.0 | 2026-10-23 | FRD-04 built (escrow-protocol 1.27.0, order-lifecycle 1.25.0, sync-protocol 1.25.0, auth-api 2.10.0, crop-listing-api 1.5.0, trip-listing-api 1.6.0): section 17 marked built, with its implementation choices (user-action gates live on deploy and not behind the flag, no fraud service at the escrow Lambda is LOW, the create check before the F-14 listing checks, the tier read after the suspension pass, a certain hold beats an unscored party, tier and score in the `fraud_hold` metadata; not built: the tier in the audit metadata of MEDIUM and HIGH settlements and of the admin routes); 14.3 known edge of two exactly full pages; 14.4: `RISK_TABLE_UNCONFIGURED` is logged flag on or off (the flag-off skip is the live CRITICAL enforcement) now that escrow-cron has the table, and an unreadable score holds instead of skipping when another party is CRITICAL; sections 18, 22 and 23 updated |
| 3.4.0 | 2026-10-23 | FRD-06 client handling built: section 4.4 (no new Isar collection or field; `PaymentStatus` gains `held` and `unknown`, append only), section 6.1 (client scope: fraud codes, escrow status as an open set, "Under Review" on escrow and order screens) and section 6.5 (the two hold reads are the only new network calls) |
| 3.3.0 | 2026-10-23 | FRD-03 built (escrow-protocol 1.22.0, order-lifecycle 1.22.0): section 18 marked built for the `account_suspended` trigger behind `FRAUD_HOLD_ENABLED` (shipped off); the gate seam the three sweeps share, before any side effect (for an `/escrow/initiate` refund before its pending delivery is cancelled), and the trucker taken as the payout takes it; the order fence as one transaction (no revert fallback); admin exits need a `reason` of 10 to 500 characters and record `fraud_hold_release` or `fraud_hold_refund` before the order or the money moves; `held` joins the stale lock recovery and the manual payout close; review decisions: the flag-off skip stays and `RISK_SETTLEMENT_SKIPPED` is alarmed from the FRD-03 deploy (18.7, 22), a held escrow keeps its verdict's direction with the T4 residual risk recorded (18.3), and with the flag on the farmer's accept is fenced on the escrow in one transaction (18.5); `fraud_confirmed` (FRD-05) and `risk_critical` (FRD-04) stay planned |
| 3.2.0 | 2026-10-23 | FRD-02: risk score built (sections 14 and 16), no caller yet. `GetUserRiskScoreAsync` and `RiskScoreResult` with `Source`, `RiskScoreCalculator`, `RiskScoreSettings`, `RiskScoreMemo`; implementation choices listed in 14.2 (an unknown `recordType` counts as a signal and logs `RISK_SCORE_UNKNOWN_RECORD`), the logging and `Source` in 14.4, env parsing in 14.5 (`RISK_MAX_PAGES` capped at 10); FRD-08 as a prerequisite for signals written onto another user (14.3); notes for FRD-04 in section 23; status markers updated |
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
- Sync protocol: `specs/contracts/sync-protocol.md` (1.9.0: `FRAUD_ACCOUNT_HELD:` operation error; built in 1.25.0)
- Escrow protocol: `specs/contracts/escrow-protocol.md` (1.7.0: `held`, fraud hold and risk tiers; the tier gates built in 1.27.0; `fraud_confirmed` built in 1.29.0; the payout delay in 1.33.0)
- Order lifecycle: `specs/contracts/order-lifecycle.md` (1.9.0: fraud hold and payment gates; the tiers built in 1.25.0)
- Auth API: `specs/contracts/auth-api.md` (2.6.0: `AUTH_040`, `AUTH_041`, `FRAUD_ACCOUNT_HELD`; built in 2.10.0)
- Crop and trip listing: `specs/contracts/crop-listing-api.md` 1.5.0 and `specs/contracts/trip-listing-api.md` 1.6.0 (`FRAUD_ACCOUNT_HELD` on create)
- Step-up decision record: `specs/decisions/auth-step-up-plan.md`
- Three-party escrow design: `specs/features/three-party-escrow-commitments-reputation.md` (ESC-25, ESC-33, H1, H4)
- Task list: `TASK.md`, EPIC FRD

**Last Updated**: 2026-10-28
**Author**: Architecture Agent
**Status**: Rules 1 to 4, signal persistence and the risk score (FRD-02) implemented. The enforcement of section 17 is built (FRD-04), with the payout delay of section 17.5 (FRD-13). Signals from existing rejects and the write limit are built (FRD-08). The escrow `held` status of section 18 is built for a party rejected in Fraud Review (FRD-05), a suspended party (FRD-03) and a CRITICAL party (FRD-04), behind `FRAUD_HOLD_ENABLED` (on in dev, off in staging and prod; older apps read `escrowed`, FUP-61). The admin actions of section 19 and the whitelist row are built (FRD-05). The rest of the risk engine (sections 20 to 23) specified, not built (EPIC FRD).
