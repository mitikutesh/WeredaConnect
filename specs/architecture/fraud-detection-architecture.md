# Fraud Detection System Architecture (F-14)

## Version
3.17.0 | 2026-11-07

## Status
Rules 1 to 4, signal persistence and the risk score (sections 14 and 16, FRD-02) implemented. The enforcement of section 17 is built (FRD-04): the user-action gates are live, and the settlement gate holds or skips a party at risk tier CRITICAL. The escrow `held` status of section 18 is built for a party rejected in Fraud Review (FRD-05), a suspended party (FRD-03) and a CRITICAL party (FRD-04), behind `FRAUD_HOLD_ENABLED`: on in dev, off in staging and prod, with apps older than FRD-06 answered `escrowed` (FUP-61, section 18.7). The admin actions of section 19 and the whitelist row of section 20 are built (FRD-05). Signals from existing rejects and the per-user, per-type write limit are built (FRD-08, section 15). The payout delay of section 17.5 is built (FRD-13). The transaction pattern rules of section 15.1 are built (FRD-09). The adversarial personas run against dev of section 11.6 is built (FRD-17). The clean-delivery credits of section 20 (the score decay) are built (FRD-10). The pickup proof is built (FRD-11). Fraud Review on the sparse status index, grouped by user with the risk score, and the held queue (sections 18 and 19) are built (FRD-07). The relationship patterns of section 15.2 are built (FRD-15). The rest of the risk engine (AI advisory; sections 21 to 23) is specified, not built (EPIC FRD).

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

**Transaction pattern signals (FRD-09)**: six flag-only rules write after a
payment, a committed cancel, a buyer's dispute or a delivery confirm succeeded
(section 15.1), with the observed order, escrow, delivery or trip as the entity.

**Credit rows (FRD-10)**: not signals. escrow-cron writes one `CLEAN_DELIVERY`
row per party after the auto-release of a clean trade (section 20), never
through `RecordFraudSignalAsync`, whose hourly key would fold two clean
deliveries in one hour into one credit.

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

No EventBridge rules or scheduled jobs for the rules. The risk score is computed on read (section 14). FRD-15 adds no Lambda either: its relationship scan is a third hourly action of the existing order-cron Lambda (section 15.2).

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
| Risk score decay (FRD-10, `CleanDeliveryCreditServiceTests.cs`, `EscrowCronCleanDeliveryCreditTests.cs`, `CleanDeliveryCreditDynamoDbTests.cs`, credits in `FraudRiskScoreTests.cs`) | `CleanDeliveryCreditService.CreditAsync`, the auto-release, `GetUserRiskScoreAsync` | One credit per party of a clean trade, idempotent per escrow and party, none on a trade with a signal (an approved one excepted), a failed credit never fails the release; the score lowered by 5 per credit, capped, floored at 0 |
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

### 11.6 Adversarial personas against dev (FRD-17)

`scripts/e2e_fraud_personas.py` runs six adversarial personas against the deployed dev API. Each persona registers its own accounts through the mock Fayda provider (`POST /auth/fayda-start`, the mock's `auto=1&phone=<number>`, `POST /auth/fayda-verify`, `POST /auth/register`), from the phone range `+25190017XXXX`, never the shared test users. The script logs in as the dev admin before it creates anything, reads the signals back through `GET /admin/fraud-signals` for both statuses (the list takes one status at a time since FRD-07, and a row an admin rejected is `reviewed` and still counts), and takes each persona's score and tier from the responses' `users[]` (section 19, FRD-07). A replica of section 14.2 recomputes them from the rows as a cross-check, and a disagreement is flagged. It also checks the gates each persona meets. It writes a report to `specs/reports/fraud-personas-<UTC date>.md`.

| Persona | Accounts | Expected signals | Tier | Gate or hold checked |
|---------|----------|------------------|------|----------------------|
| Listing spammer | farmer | `FRAUD_DUPLICATE_LISTING`, `FRAUD_VELOCITY_EXCEEDED`, `PRICE_ANOMALY` when the type's sample allows | MEDIUM | the duplicate and velocity refusals; the 8th create attempt meets the velocity refusal, not `FRAUD_ACCOUNT_HELD` |
| Self-trader | a farmer and a buyer on one device | none at once; since FRD-15 the hourly scan writes `SHARED_DEVICE_TRADE` on both accounts within the hour (section 15.2), so a `--recheck` sees it | LOW (25 once the scan ran) | the payment with a fresh password passes; a 5-minute-old sign-in meets `AUTH_040` |
| IDOR prober | a buyer and a farmer | buyer: `IDOR_CREATE_CONFLICT`, `ROLE_FORBIDDEN`, `IMMUTABLE_FIELD_CHANGE`; farmer: the same plus `FRAUD_DUPLICATE_LISTING` | HIGH; CRITICAL | `AUTH_041` on the buyer's payment, then `AUTH_040` after a mock Fayda step-up with a 5-minute-old sign-in; a 403 or 404 on another buyer's order over REST (a 200 fails the persona); `FRAUD_ACCOUNT_HELD` on the farmer's next listing; a clean buyer's paid order on the CRITICAL farmer is the hold target |
| Payer mismatch | none | none reachable: dev has the mock gateway and no `/webhooks/telebirr` route | n/a | the route answers 404 |
| GPS spoofer | trucker | `GPS_MOCKED`, `GPS_JUMP`, `IMPOSSIBLE_DELIVERY_TIME`; never `GPS_ANOMALY` on the trucker (Rule 4 writes it on the confirming buyer) | MEDIUM | none below CRITICAL |
| Confirm-then-dispute buyer | buyer | `DISPUTE_RATE` (2 of 5 paid orders disputed) | LOW | none |

```bash
python3 scripts/e2e_fraud_personas.py --self-test    # offline: persona definitions, score replica, guards
python3 scripts/e2e_fraud_personas.py --dry-run      # the plan and the accounts, no network
python3 scripts/e2e_fraud_personas.py --state-file /tmp/frd17-state.json            # every persona against dev
python3 scripts/e2e_fraud_personas.py --personas idor,gps --report /tmp/frd17.md    # some personas
python3 scripts/e2e_fraud_personas.py --recheck /tmp/frd17-state.json --report specs/reports/fraud-personas-<date>.md
python3 scripts/e2e_fraud_personas.py --recheck /tmp/frd17-state.json --hold-target escrow-order-<id>=<farmerDeadlineAt>
```

Run rules. The script refuses any API but dev unless `--allow-any-api` is passed, and refuses a live or sandbox Fayda authorize host. A full run registers 9 accounts, and `RegisterFunction` allows 10 per IP in a window that opens with the first registration and closes an hour later, so a second full run waits for it. Every request of an account carries one `X-Device-Id`, so no new-device SMS is sent; registration proves the phone through the Fayda claim, so no OTP SMS is sent either. Listings carry photo ids with no upload (the server does not check them at create, `crop-listing-api.md` rule 9), so nothing goes to S3. Payments go through dev's mock gateway. Names and descriptions start with `frd17`; the spam listings and the unpaid or unanswered orders are cleaned up, and the delivered trades, the disputed escrows and the hold target stay (the report lists them). The state file holds the synthetic accounts' passwords: the script refuses a path inside a git checkout and writes it with mode 0600.

What one run can and cannot show. The write limit keeps one row per user, type and hour (section 14.3), so a rerun with the same accounts in the same hour adds nothing, and in one hour a persona reaches CRITICAL only with four types (the IDOR farmer: 40 + 25 + 25 + 20). Only a settlement sweep writes `held` (section 18.1), so the hold of the CRITICAL farmer's escrow is checked with `--recheck` after the order's `farmerDeadlineAt`, when the order cron has cancelled it and the auto-refund sweep has met the gate. `--recheck` reads the held queue (`GET /admin/escrows/held`, FRD-07) and exits 1 when a hold target is past its deadline plus `--hold-grace-hours` (default 2: the order cron, then the hourly sweep) and is not held. The run keeps the target's deadline in the state file; for an older state file, `--hold-target ESCROW_ID=DEADLINE` names it.

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
- FRD-09: the six pattern types (section 15.1) count once per (`signalType`, `entityId`) like the FRD-08 types, so a payment retried after `payment_failed` is one observation and an approved row clears its entity, and each is capped per type at `RISK_PATTERN_TYPE_CAP` (default 50) of counted weight: one pattern alone stops at MEDIUM, several together add up. `RiskScoreSettings.CapOf` gives a capped type its cap; the early stop counts the pattern types like the capped reject types.
- The whitelist is active while `whitelistUntil` is after now; a missing or unparsable value means no whitelist.
- FRD-10: a credit row's key carries the escrow id after the type (`{releasedAt}#CLEAN_DELIVERY#{escrowId}`, section 20). The window compares the whole key, so its time prefix decides, as for any row; a credit row is never deduplicated, and its weight is read as stored.
- FRD-15: the three relationship types (section 15.2) count once per (`signalType`, `entityId`), the order, like the pattern types; `SHARED_DEVICE_TRADE` and `REPEATED_PAIR` share one cap of `RISK_PATTERN_TYPE_CAP` (a cap group, `RiskScoreSettings.CapKeyOf`, not a cap each), so together they stop at MEDIUM; `SELF_TRADE` is not capped.

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
| `RISK_PATTERN_TYPE_CAP` | 50 | FRD-09: maximum counted weight per window of each transaction pattern type (section 15.1); FRD-15: also of `SHARED_DEVICE_TRADE` and `REPEATED_PAIR` together, one cap for the two (section 15.2) |
| `FRAUD_WHITELIST_DAYS` | 30 | Default whitelist duration (section 19) |
| `FRAUD_HOLD_ENABLED` | false | Whether a sweep may write `held` (section 18) |

FRD-02 reads the first seven on every call (`RiskScoreSettings.FromEnvironment`). A value that is not an integer in range keeps its default: the window at least 1 and at most 30, tier bounds at least 1, caps at least 0, pages from 1 to 10 (a larger value is capped at 10, so a typo cannot turn one score into a long read). Tier bounds that are not strictly increasing all fall back to the defaults. No Lambda sets them today, so the defaults apply. FRD-08 reads `RISK_REJECT_TYPE_CAP` the same way, an integer of at least 0 (0 turns the two capped types off), no Lambda sets it. FRD-09 reads `RISK_PATTERN_TYPE_CAP` the same way (0 turns the pattern types off in the score; their rows are still written for review), no Lambda sets it; the pattern rules' own thresholds are in section 15.1. `FRAUD_WHITELIST_DAYS` (FRD-05) and `FRAUD_HOLD_ENABLED` (FRD-03) are read by the tasks that use them. The admin action reads `FRAUD_WHITELIST_DAYS` on every whitelist (`FraudDetectionService.WhitelistDaysFrom`): an integer from 1 to 365, else 30, so a typo cannot whitelist a user for good. No Lambda sets it today.

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
| `CLEAN_DELIVERY` | minus 5 | credit | escrow-cron, after the auto-release of a trade with no dispute, no hold and no signal on its order, delivery or escrow: one row per party (section 20). Counted up to `RISK_CREDIT_CAP` in total (section 14.2) | live (FRD-10) |
| `AI_ADVISORY` | per review, capped by `RISK_ADVISORY_CAP` in total | advisory | Bedrock reviewer (section 21) | planned FRD-19 |
| `IDOR_CREATE_CONFLICT` | 40 | hard | `SyncFunction`: a crop, trip, order or delivery `create` whose id already holds a row the caller does not own and is no party to (the conflict echoes nothing, sync-protocol 1.4.0). The owner's own retry is not a source. Counted in every hour, uncapped (no honest retry) | live (FRD-08) |
| `ROLE_FORBIDDEN` | 25 | hard | `SyncFunction`: the role gates (crop, trip, order, intent, rating), the caller id binding on create (`farmerId`, `truckerId`, `buyerId`, `raterId`, `senderId`), an update, delete, cancel or dispute of a row the caller is no party to, and the delivery and order transitions by role (`*_UNAUTHORIZED`, `RATING_FORBIDDEN: You can only rate your own orders.`); `EscrowFunction`: `ESCROW_025` on `/escrow/initiate` and `/escrow/order-checkout`. A role gate met by a token without the role claim writes nothing. Counted once per entity, at most 50 per window (`RISK_REJECT_TYPE_CAP`) | live (FRD-08) |
| `IMMUTABLE_FIELD_CHANGE` | 25 | hard | `SyncFunction`: an update that changes an identity or money field (SIM-02 crop, SIM-03 trip, SIM-09 delivery, SIM-06 order, and an order update whose echoed `orderId` names another order). An unchanged echo is not a source. Counted once per entity, at most 50 per window | live (FRD-08) |
| `QR_HANDOFF_FAILURE` | 5 | soft | `SyncFunction`: `DELIVERY_NONCE_MISMATCH`, `DELIVERY_NONCE_REPLAY`, and `DELIVERY_NONCE_MISSING` while a nonce is on record. `DELIVERY_NONCE_EXPIRED` (a genuine old code) and the deferrals are not sources | live (FRD-08) |
| `TELEBIRR_PAYER_MISMATCH` | 0 | soft | `TelebirrWebhookFunction.PayerMatchesBuyerAsync`: a verified notification paid from a wallet other than the buyer's registered phone, applied because `TELEBIRR_REQUIRE_PAYER_MATCH` is off (a relative's wallet is common): logged for review, never scored | live (FRD-08) |
| `TELEBIRR_PAYER_REFUSED` | 5 | soft | The same, with `TELEBIRR_REQUIRE_PAYER_MATCH` on: the payment is refused | live (FRD-08) |
| `STALE_VERSION_BURST` | 0 | soft | `SyncFunction.ProcessOperationAsync`: the 10th update or delete version conflict in one batch (`StaleVersionBurstThreshold`); fewer write nothing | live (FRD-08) |
| `SUSPENDED_TOKEN_USE` | 0 | soft | `RefreshFunction`: `AUTH_025` for the refresh token of a suspended profile | live (FRD-08) |
| `GPS_JUMP` | 10 | soft | `SyncFunction`, the side effects of an accepted `location_ping` `fix` (LT-07, `live-trip.md` section 6.3): the new check-in and the trip's check-in just before or just after it in capture order are more than 1 km apart (great circle) at over 130 km/h, only among check-ins with `accuracyM` at most 1,000 (`live-trip-api.md` invariant 6). On the trip's trucker, the trip as the entity. A GPS glitch can do it too. Counted once per trip | live (LT-07) |
| `GPS_MOCKED` | 25 | soft | The same, for a check-in the phone flagged `mocked: true` (a location-faking app is in use). Counted once per trip | live (LT-07) |
| `AMOUNT_ANOMALY` | 10 | soft | `SyncFunction`, after an order `payment_submitted` write: the total is above 3 sigma of the buyer's own verified payments (section 15.1). On the buyer, the order as the entity. Counted once per order, at most 50 per window (`RISK_PATTERN_TYPE_CAP`) | live (FRD-09) |
| `NEW_ACCOUNT_LARGE_AMOUNT` | 10 | soft | The same payment: 5,000 ETB or more from an account under 7 days old, unless the same payment is structuring | live (FRD-09) |
| `KYC_THRESHOLD_STRUCTURING` | 20 | hard | The same payment, by a buyer without a Fayda ID: with two or more funded payments from 9,000 up to and including 10,000 ETB (the Fayda threshold) in 30 days, the third or later in the band | live (FRD-09) |
| `CANCEL_AFTER_FUND_RATE` | 15 | soft | `SyncFunction`: a farmer's committed cancel of an accepted order, a trucker's drop-out, a trip cancel that took the transport from paid orders, when the canceller has cancelled 30 percent or more of 5 or more funded orders in 90 days. On the canceller, the order (or the trip) as the entity | live (FRD-09) |
| `DISPUTE_RATE` | 10 | soft | `EscrowFunction.HandleDisputeAsync` and a `SyncFunction` delivery dispute, when the buyer raised it and it froze the money: 30 percent or more of 5 or more paid orders in 90 days disputed by the buyer. On the buyer, the escrow as the entity | live (FRD-09) |
| `IMPOSSIBLE_DELIVERY_TIME` | 15 | soft | `SyncFunction`, after a delivery `delivered` write (not a self-pickup): the trip between the pickup and drop-off scans, 5 km or more apart, was faster than 120 km/h, from the earlier of the pickup's arrival and the trucker's pickup capture time to the server's `confirmedAt`. On the delivery's trucker, the delivery as the entity | live (FRD-09) |
| `NO_SHOW`, `DELIVERY_OVERDUE`, buyer griefing (three declined paid orders in 30 days) | per type | hard | ESC-33 | planned ESC-33 |
| `SELF_TRADE` | 40 | hard | The FRD-15 hourly scan (order-cron `relationship_patterns`, section 15.2): the buyer and the farmer of a funded order are the same user id, or their users rows carry the same phone number or Fayda ID. On both, the order as the entity. Counted once per order, uncapped | live (FRD-15) |
| `SHARED_DEVICE_TRADE` | 25 | soft | The same scan: not `SELF_TRADE`, the farmer did not decline the order, and the two accounts have signed in on a common install (`knownDevices`, the `X-Device-Id` of each sign-in). On both, the order as the entity. Counted once per order; with `REPEATED_PAIR`, at most 50 per window together (`RISK_PATTERN_TYPE_CAP`) | live (FRD-15) |
| `REPEATED_PAIR` | 5 | soft | The same scan: an order the farmer accepted completes 3 or more funded orders the farmer accepted between the pair, created in the 7 days up to it. On both, the order as the entity. Counted once per order; with `SHARED_DEVICE_TRADE`, at most 50 per window together | live (FRD-15) |

Class semantics: **soft** is a flag-only rule whose weight an active whitelist skips; **hard** is a rejection or a money signal and always counts; **credit** is negative and capped; **advisory** is AI output, capped and excluded from the enforcement score. Each planned task adds its rows to this table, with weight and class, in the same PR as the producer.

Weights of the FRD-08 types. Only a tampered client produces the three hard types: every create id is a UUID made on the device, create screens and actions are shown by role and by the row's own party ids, and update payloads echo only the caller's own ids (the client code checked is listed in the FRD-08 PR). `ROLE_FORBIDDEN` and `IMMUTABLE_FIELD_CHANGE` count once per refused entity and at most 50 per type (section 14.2), so one refused operation is LOW (25) however often the queue retries it, and one of these types alone never passes MEDIUM. `IDOR_CREATE_CONFLICT` has no honest retry and counts in every hour, uncapped: one probe is MEDIUM (40), probes in three hours are CRITICAL. The three types in one hour are HIGH. The soft types come from honest clients too: version conflicts after offline edits, a QR code regenerated offline and scanned before it synced, a relative's wallet, the app's own refresh after a suspension. They weigh 0 or 5, count once per delivery or order escrow, and an FRD-05 whitelist skips them. The payer phone Telebirr sends (`251XXXXXXXXX`) is put into the users table's form (`+251XXXXXXXXX`) before the comparison. Residual risks, kept: a hard refusal from honest software adds its weight once per distinct stuck operation (an admin role change with operations of the old role still queued, a client bug), at most 50 per capped type, so the worst honest case is MEDIUM for one type and CRITICAL only when both capped types are stuck; an admin approve on any row of the stuck operation clears it; a web tab still holding an account the shared token no longer belongs to can send that account's ids under the token's user (a client follow-up). Not sources, by decision: a webhook whose signature, body, source IP or merchant fails (anyone can post to the route, so it is logged and written onto no user), a missing payer field, the REST read 403s, `MESSAGE_FORBIDDEN` (thread membership follows the order), `RATING_FORBIDDEN` for an order not yet delivered (the local status can lag), match event refusals (background analytics the user never made).

Attribution: every signal is written against the authenticated caller of the operation, with one exception: the webhook has no caller, so `TELEBIRR_PAYER_MISMATCH` or `TELEBIRR_PAYER_REFUSED` is written on the buyer that our own escrow or checkout row names, only after the signature verified, and only when that buyer's row and phone were read. ESC-33 changes `GPS_ANOMALY` to be attributed to the trucker who moved the goods rather than to whoever sent the confirming update.

### 15.1 Transaction pattern rules (FRD-09, built)

Six deterministic rules over data the server already holds. Each runs only after the business write it observes succeeded, writes its hit through `RecordFraudSignalAsync` (the section 4.3 row schema, one row per user, type and UTC hour, TTL 30 days) with the observed order, escrow, delivery or trip as the entity, and never changes the answer: a failed history read logs `Pattern check <rule> skipped for <entityType> <id>: [<exception type>]` (the id through `MoneyAlarmLog.LogId`) and writes nothing, and a failed signal write is logged like every signal write. No rule refuses or holds anything: enforcement comes only through the risk score and its tier gates (sections 16 and 17). Code: `TransactionPatternRules` (the pure decisions and `TransactionPatternSettings`), `TransactionPatternService` (the history reads), the hooks in `SyncFunction` and `EscrowFunction` (`PatternRules`, wired by the Lambda when `FRAUD_SIGNALS_TABLE` is set).

| Rule | Signal (weight, class) | Party | Hook (after the write) | Fires when | Data read |
|------|------------------------|-------|------------------------|------------|-----------|
| Amount above 3 sigma (escrow-protocol, Fraud Prevention) | `AMOUNT_ANOMALY` (10, soft) | the buyer (caller) | `/sync` order `payment_submitted`, after the payment gates and the write | the order total is above the mean plus `FRAUD_AMOUNT_SIGMA` sample standard deviations of the buyer's verified payments (orders with `paymentVerifiedAt`), with at least `FRAUD_AMOUNT_MIN_SAMPLES` of them, and at least `FRAUD_AMOUNT_MIN_MULTIPLE` times their mean (so a history of near-equal amounts, a deviation near 0, does not flag a slightly larger order) | the buyer's orders on `BuyerIdCreatedAtIndex` |
| New account with a large amount | `NEW_ACCOUNT_LARGE_AMOUNT` (10, soft) | the buyer | the same | the total is at least `FRAUD_NEW_ACCOUNT_LARGE_ETB` and the account (`createdAt` of the users row the KYC gate read) is younger than `FRAUD_NEW_ACCOUNT_DAYS`; not written when the same payment is structuring (one payment, one reason) | none beyond the KYC gate's |
| Repeated amounts just under the KYC threshold | `KYC_THRESHOLD_STRUCTURING` (20, hard) | the buyer, only one without a Fayda ID (`FaydaVerified` on the users row the KYC gate read) | the same | the total is in the band from `FRAUD_KYC_BAND_PERCENT` percent of `KycPolicy.FaydaThresholdEtb` (10,000 ETB) up to and including it (no Fayda needed), and with the buyer's other funded payments (`paymentVerifiedAt`) in the band over `FRAUD_KYC_BAND_WINDOW_DAYS` makes at least `FRAUD_KYC_BAND_REPEATS`; this payment counts as the triggering one | the payment rules' one read |
| Cancel-after-fund rate | `CANCEL_AFTER_FUND_RATE` (15, soft) | the farmer or trucker who cancelled (caller) | farmer: the committed cancel of an accepted order (`farmer_cancelled`, at fault); trucker: the drop-out from an accepted order, and a trip cancel or delete that took the transport from paid orders (once per trip, the trip as the entity) | of the party's funded orders in the window (this cancel included), the party cancelled at least `FRAUD_CANCEL_RATE_PERCENT` percent, over at least `FRAUD_CANCEL_RATE_MIN_ORDERS` | the party's orders on `FarmerIdCreatedAtIndex` or `TruckerIdCreatedAtIndex` |
| Per-buyer dispute rate | `DISPUTE_RATE` (10, soft) | the buyer (caller) | `POST /escrow/dispute` that succeeded, and a `/sync` delivery dispute that froze the escrow, when the buyer raised it | of the buyer's escrows in the window that held money (this one included), at least `FRAUD_DISPUTE_RATE_PERCENT` percent carry `disputedBy` the buyer, over at least `FRAUD_DISPUTE_RATE_MIN_ORDERS` | the buyer's escrows on `BuyerStatusIndex` |
| Delivery time impossible for the distance | `IMPOSSIBLE_DELIVERY_TIME` (15, soft) | the delivery's trucker (not the caller, usually the buyer) | `/sync` delivery `delivered`, after the write; never a self-pickup or a delivery without a trucker | the great circle distance between `gpsPickup` and `gpsDropoff` is at least `FRAUD_DELIVERY_MIN_KM` and needs more time at `FRAUD_MAX_DELIVERY_KMH` than the trip took, from the earlier of `pickedUpAt` and `pickupCapturedAt` to `confirmedAt` (below); a point at (0, 0) is no fix and skips | none: the written row |

Definitions and choices:

- **Rate bases**: a funded order has `paymentVerifiedAt`. A farmer's cancel is a `cancelled` order with `cancelledBy` and `faultParty` both `farmer` (the committed cancel of an accepted order). A farmer's decline of a funded order (`rejected`) is an answer to the request, not a cancel, and a farmer's cancel in `awaiting_transport` keeps the trucker's fault (or none), so neither counts. A trucker's cancel is `truckerResponse: cancelled` (a drop-out after accepting, or the trip cancelled under the order); a decline of a request is an answer. Escrows that held money are `escrowed`, `released`, `completed`, `disputed`, `dispute_resolved`, `held` and `refunded`; a farmer's dispute is not the buyer's. The amount rule's history and the structuring count take only funded payments (`paymentVerifiedAt`): a checkout submitted and never paid on another order is no payment. The payment being observed counts as the triggering one although it is only submitted: the rules run at `payment_submitted`, before a live gateway can fund it, and moving them to the funding would put them in the webhook and `OrderFundingService`; an attempt that is never funded therefore triggers at most once (per order, section 14.2) and does not count for later payments.
- **The event counts once**: the history read is eventually consistent and may not show the write just made, so the current order, escrow or orders are left out of the read and counted explicitly (funded and cancelled, held and disputed).
- **Minimum samples**: no rate fires below its minimum (5 orders), so a new user is never flagged for cancelling one order or for one dispute; the amount rule needs 5 verified payments. Rates count orders: a trip cancel counts every paid order it took the transport from, so a trucker who cancels a trip carrying 5 or more paid orders can be flagged on that one cancel, whatever the history. Kept on purpose: each of those buyers lost the transport they paid for, and the signal is soft, weight 15, LOW on its own.
- **The delivery trip time**: `pickedUpAt` and `confirmedAt` are when the scans reached the server. A trucker offline at the farm syncs the pickup at the destination, minutes before the confirm, and would read as an impossible trip on server time alone. The pickup therefore also stores the server-only `pickupCapturedAt`, the pickup operation's `timestamp` from the trucker's phone (delivery-verification-api 1.23.0), and the trip runs from the earlier of `pickedUpAt` and `pickupCapturedAt` to the server's `confirmedAt`. An offline pickup keeps its real start; a trucker's clock set behind only makes the trip longer; one set ahead by more than 5 minutes is not stored (nor one older than 30 days), and a delivery without `pickupCapturedAt` (picked up before FRD-09, or a client that sends no `timestamp`) is not judged. The confirming operation's own `timestamp` is not used: it comes from the buyer's phone, whose clock (often set by hand, hours behind) or a modified client would otherwise shorten an honest trucker's trip. A point at (0, 0) is what a phone without a fix reports and is treated as missing.
- **Not sources**: `POST /escrow/initiate` (the delivery escrow path; the order payment is the marketplace money path and carries the amount rules); the buyer's cancels (they follow a transport failure the buyer did not cause; ESC-48's `CONTACT_THEN_TRANSPORT_CANCEL` covers the buyer side); the farmer's cancel in `awaiting_transport`; the crons' deadline cancels; an admin's dispute disposition; a payment the gates refused. With `COMMITMENTS_ENABLED` off (staging and prod today) there is no committed farmer cancel, no trucker drop-out and no trip-cancel cascade, so the cancel rule has nothing to observe there.
- **Weights and tiers**: every pattern type counts once per entity and is capped at `RISK_PATTERN_TYPE_CAP` (section 14.2), so one pattern alone stops at MEDIUM. A buyer who pays four times in the structuring band within 30 days reaches MEDIUM (a fresh password at every payment) and the cap keeps them there; patterns together add up and can reach HIGH or CRITICAL.
- **Classes**: all soft except `KYC_THRESHOLD_STRUCTURING`, which is about avoiding the Fayda rule, a money signal a whitelist does not silence.
- **Attribution**: every pattern signal is written on the caller except `IMPOSSIBLE_DELIVERY_TIME`, written on the delivery's trucker. A buyer cannot drive it: the trip time uses only server times and the trucker's own pickup capture time, nothing the buyer's phone sends; the buyer's confirm needs the drop-off code shown by the trucker's phone, so only the trucker can let it happen early; it needs a real paid order carried by that trucker; and it counts once per delivery, at most 50, with the FRD-08 write limit of one row per hour (section 14.3).

Configuration, read on every check (`TransactionPatternSettings.FromEnvironment`). A value that is not a number in its range keeps the default, so a typo can neither make a rule fire on everyone nor turn a check into a long read. No Lambda sets them today, so the defaults apply.

| Variable | Default | Range | Meaning |
|----------|---------|-------|---------|
| `FRAUD_PATTERN_WINDOW_DAYS` | 90 | 1 to 365 | History window of the amount history and the two rates |
| `FRAUD_PATTERN_MAX_PAGES` | 2 | 1 to 10 | Pages of 50 rows one check reads at most |
| `FRAUD_AMOUNT_SIGMA` | 3 | 1 to 10 | Standard deviations above the mean |
| `FRAUD_AMOUNT_MIN_SAMPLES` | 5 | 2 to 100 | Verified payments needed before the amount rule fires |
| `FRAUD_AMOUNT_MIN_MULTIPLE` | 2 | 1 to 100 | The payment must also be at least this times the mean |
| `FRAUD_NEW_ACCOUNT_DAYS` | 7 | 0 to 90 | Account age under which an account is new (0 turns the rule off) |
| `FRAUD_NEW_ACCOUNT_LARGE_ETB` | 5,000 | 1 to 500,000 | A large payment for a new account |
| `FRAUD_KYC_BAND_PERCENT` | 90 | 50 to 99 | Lower edge of the band, in percent of the 10,000 ETB Fayda threshold |
| `FRAUD_KYC_BAND_REPEATS` | 3 | 2 to 50 | Payments in the band, this one included, that fire |
| `FRAUD_KYC_BAND_WINDOW_DAYS` | 30 | 1 to 365, at most the pattern window | Window of the band count |
| `FRAUD_CANCEL_RATE_PERCENT` | 30 | 1 to 100 | Share of funded orders cancelled that fires |
| `FRAUD_CANCEL_RATE_MIN_ORDERS` | 5 | 2 to 100 | Funded orders needed before the rate fires |
| `FRAUD_DISPUTE_RATE_PERCENT` | 30 | 1 to 100 | Share of paid orders disputed by the buyer that fires |
| `FRAUD_DISPUTE_RATE_MIN_ORDERS` | 5 | 2 to 100 | Paid orders needed before the rate fires |
| `FRAUD_MAX_DELIVERY_KMH` | 120 | 10 to 1,000 | Fastest plausible average speed between the two scans |
| `FRAUD_DELIVERY_MIN_KM` | 5 | 0 to 1,000 | Distance under which the delivery time is not judged (GPS noise) |

Data access and cost: each check is one Query of one user's partition on an existing GSI through the repositories (`IOrderRepository.QueryOrdersAsync`, `IEscrowRepository.QueryByBuyerAsync`), newest first, pages of 50 rows, at most `FRAUD_PATTERN_MAX_PAGES`, stopping at the first row older than the window: 100 rows at most by default, about 1 to 40 RCU eventually consistent, once per payment, committed cancel or buyer dispute, and no read for the delivery rule. No Scan, no new index, no new table; an empty or blank user id reads nothing (`OrderRepository.QueryOrdersAsync` falls back to a Scan when it gets no id). A user with more rows in the window than the cap is judged on the newest ones. No IAM change: the sync Lambda already reads and writes the orders, escrows and fraud-signals tables with their indexes, and the escrow Lambda the escrows and fraud-signals tables.

Admin: the Fraud Review shows the type as written (no per-type label, so nothing to add). Its `SOFT_SIGNAL_TYPES` (where the whitelist is offered) lists every soft type of section 15 since FRD-15 (before, only `GPS_ANOMALY` and `PRICE_ANOMALY`), and the whitelist result no longer names "GPS and price".

Tests: `server/tests/Services/TransactionPatternRulesTests.cs` (hit and miss per rule, configuration), `TransactionPatternServiceTests.cs` (the reads: the window, the page cap, the current event, the party), `FraudPatternSignalTests.cs` (weights, classes, the per-entity count, the cap, the whitelist), and the hooks in `server/tests/Functions/SyncFunctionPaymentPatternTests.cs`, `SyncFunctionDeliveryPatternTests.cs`, `SyncFunctionCommitmentTests.cs`, `SyncFunctionTripBookingTests.cs` and `EscrowFunctionTests.cs` (each with a failed read or write that leaves the answer unchanged).

### 15.2 Relationship patterns (FRD-15, built)

An hourly scan of recently funded orders for trades between parties who are, or act like, one person. It runs as the `relationship_patterns` action of the existing order-cron Lambda (a third hourly EventBridge rule, `{prefix}-order-cron-relationship-patterns`), reads with DynamoDB Queries and GetItems only (no Scan, no graph store, no new table or index), and writes each hit through `RecordFraudSignalAsync` (the section 4.3 row: one row per user, type and UTC hour, TTL 30 days) on both the buyer and the farmer, with the order as the entity (`entityType: order`, so Fraud Review links the order). Flag-only: it refuses, holds and cancels nothing; enforcement comes only through the score and its tier gates (sections 16 and 17). Code: `RelationshipPatternRules` (the pure decisions, with `RelationshipPatternSettings`), `RelationshipPatternService` (the reads, the existence check, the writes), `OrderCronFunction.HandleRelationshipPatternsAsync`, `FraudDetectionService.HasSignalAsync`, `OrderRepository.QueryByStatusCreatedSinceAsync`.

| Pattern | Signal (weight, class) | Fires when | Data read |
|---------|------------------------|------------|-----------|
| Self-trading by identity | `SELF_TRADE` (40, hard, uncapped) | the buyer and the farmer of a funded order are the same user id, or their users rows carry the same phone number, or the same Fayda ID | the two users rows (GetItem) |
| Same device on both sides | `SHARED_DEVICE_TRADE` (25, soft, one cap with `REPEATED_PAIR`) | not `SELF_TRADE`, the farmer did not turn the order down (`rejected`, or `farmerResponse` `declined`), and the two accounts have signed in on at least one common install: the users rows' `knownDevices` (the `X-Device-Id` of each sign-in, at most 10 per account, kept by `RegisterFunction` and `LoginFunction` since auth hardening) intersect | the same rows |
| Repeated farmer and buyer pair | `REPEATED_PAIR` (5, soft, one cap with `SHARED_DEVICE_TRADE`) | the examined order is one the farmer accepted (`OrderCommitment.FarmerResponseOf` is `accepted`, which reads a row from before the commitments as accepted once it left `funded`; not `rejected`), and it completes at least `FRAUD_PAIR_MIN_ORDERS` (3) such orders between the pair created in the `FRAUD_PAIR_WINDOW_DAYS` (7) days up to and including its own `createdAt`. An order still waiting for the farmer, or declined, never counts, so a buyer cannot flag a farmer by paying orders the farmer refuses; and of a burst only the order that completes the count (and any later one) is a hit, not every order of it | the buyer's orders on `BuyerIdCreatedAtIndex` back to the lookback plus the pair window, newest first, at most 4 pages of 50, stopping at the first one older |

Not built, because of the data or by decision:

- **Same IP on both sides.** No table stores a request's source IP per user. The auth Lambdas write it only to CloudWatch (`AuditLogger`) and to rate-limit counters (`IP#{bucket}#{ip}`, minutes of TTL, no user id). Storing it (a capped set beside `knownDevices`) is a follow-up that needs a privacy decision; carrier NAT puts many rural phones behind one address, so it would be a weak, soft signal at best.
- **Confirm-then-dispute buyers.** Covered by `DISPUTE_RATE` (FRD-09, section 15.1): it runs at the dispute itself, on the buyer, and counts every buyer dispute that froze money, after a confirmed delivery or not. A second type for the disputes raised after the buyer's own confirm would weigh the same disputes twice, so none is added. Kept gap: a buyer with fewer than `FRAUD_DISPUTE_RATE_MIN_ORDERS` (5) paid orders is not flagged however often they confirm and then dispute; each dispute still freezes its escrow for an admin.

Definitions and choices:

- **Funded**: the order has `paymentVerifiedAt` (the FRD-09 rate base). Only funded orders are checked: an order that never moved money is no trade, and leaving them out spares a shared family phone a signal for an order it abandoned.
- **Which orders**: the `StatusCreatedAtIndex` partitions of the twelve statuses a funded order can be in (`funded`, `accepted`, `pickup_confirmed`, `in_transit`, `delivered`, `disbursing`, `settled`, `awaiting_transport`, `disputed`, `dispute_resolved`, `cancelled`, `rejected`), each with the key condition `createdAt >= now minus FRAUD_RELATIONSHIP_LOOKBACK_HOURS` (48), newest first, pages of 100. The four statuses without a verified payment are not read. An order met in two partitions (it moved between two Queries) is checked once. The lookback is far longer than an hour so that an order paid up to a day after it was created (the unpaid auto-cancel is at 24 hours) is still met once it is paid, and so that a missed run is made up by the next.
- **One order, one identity reason**: an order gets `SELF_TRADE` or `SHARED_DEVICE_TRADE`, never both. `REPEATED_PAIR` is independent of them.
- **Identity**: roles are fixed at registration, so a buyer id equal to the farmer id needs a tampered client or a data change, and the money paths already refuse it (`ESCROW_001` at `/escrow/initiate`, `ESCROW_030` at the order checkout, and no order escrow is minted for it); the scan checks it anyway, at no cost. Phone numbers and Fayda IDs are unique at registration (`AUTH_005`, `FAYDA_004`), so a match means a duplicated identity or stale duplicate rows. Empty values never match (a tombstoned account, ACC-02, has no phone).
- **Classes and weights**: `SELF_TRADE` is hard and uncapped, since one identity on both sides is never an honest trade: three such orders reach CRITICAL. `SHARED_DEVICE_TRADE` is soft: a family or cooperative phone (or a shared browser for the web app, whose install id lives in the browser's storage) can carry a farmer's and a buyer's account honestly, and an FRD-05 whitelist silences it. `REPEATED_PAIR` is soft at 5, like the FRD-08 soft types honest clients also produce: a regular customer looks the same. The two soft types share one cap of `RISK_PATTERN_TYPE_CAP` (50; a cap group in the calculator, not a cap each), so on their own they stop at MEDIUM and never reach CRITICAL (`FRAUD_ACCOUNT_HELD` on creates) for an honest shared phone; other types still add to them. One shared-device trade is LOW (25), two are MEDIUM (50), and a one-device self-trader who trades three times in a week is at 50 (three shared-device orders and one repeated pair, 80, capped).
- **Effects beyond the score**: any counted weight makes the farmer a "new" payee for the payout delay (section 17.5: 72 hours instead of 24) while the row is in the window, and a relationship row on an order blocks that order's clean-delivery credit (section 20), which is what stops a self-trader from farming credits. An honest regular customer's pair pays that price until an admin approves the row or whitelists the user; this is the deliberate trade for catching wash trading between two phones.
- **Attribution**: both parties, since both took part (only funded orders are read, a pair order counts only once the farmer accepted it, and a shared install is not flagged on an order the farmer declined). A party cannot put another user's install id into their own `knownDevices` without knowing it (a random UUID kept on the other phone and returned only to its own account), cannot share a phone number or Fayda ID with another account, and a repeated pair needs real funded payments that the farmer accepted. The write limit of section 14.3 keeps one row per user, type and hour whatever the cause.
- **Idempotent across runs**: an order stays in the lookback for up to 48 hourly runs. Before each write `HasSignalAsync` Queries the user's partition (30 days, consistent, filtered on the type and the order, at most 2 pages) and writes nothing when a row is there, whatever its review status, so an approved false positive stays approved. A rerun within the same hour meets the writer's hour key. A second hit of the same user and type in one run would be dropped by that key, so the scan defers it (counted as `deferred`) and the next run writes it: up to 48 such hits per user and type are written over an order's lookback. A partition with more than 2 pages counts as already flagged (it reads as truncated, CRITICAL, anyway).
- **Not sources**: the trucker (a farmer who carries their own crops on a second account is an honest pattern; trucker pairs are a follow-up), unfunded orders, orders created before the lookback.
- **Known gaps, kept**: a truncated run is not resumed. There is no cursor: the next run starts again from the newest orders of each status, so at a steady volume above the cap the oldest orders of the lookback are never checked (`FRAUD_RELATIONSHIP_SCAN_TRUNCATED` says when to raise the cap). An order funded more than 48 hours after its creation is never scanned, since the lookback is on `createdAt` (`FRAUD_RELATIONSHIP_LOOKBACK_HOURS` widens it); likewise an order the farmer accepts more than 48 hours after its creation is never the examined order of a pair, though it still counts toward the pair count of a later order.

Bounded work: at most `FRAUD_RELATIONSHIP_MAX_ORDERS` (1,000) funded orders per run; past it the run logs `FRAUD_RELATIONSHIP_SCAN_TRUNCATED funded=<n> cap=<n> lookbackHours=<n>` and checks the ones it read. The run takes a 4 minute slice of the 5 minute Lambda (`CronRunBudget`, 30 seconds kept for the summary) and stops before the next page or order once it is used up (`stopReason` `time` or `lambda`, outcome `partial`). Users rows and buyer histories are read once per run.

Failures: a failed status Query fails the run (`500 ORDER_CRON_ERROR`, the next hour runs again); a failed users or history read skips that order (`FRAUD_RELATIONSHIP_CHECK_FAILED orderId=<id> error=<type>`); a failed existence check or write skips that signal (`FRAUD_RELATIONSHIP_SIGNAL_FAILED type=<type> orderId=<id> userId=<id> error=<type>`). The order stays in the lookback, so a later run tries again. Every run logs `FRAUD_RELATIONSHIP_SCAN ordersRead=<n> funded=<n> examined=<n> hits=<n> written=<n> alreadyRecorded=<n> deferred=<n> failed=<n> truncated=<bool> stop=<reason>`. A Lambda without `FRAUD_SIGNALS_TABLE` or `USERS_TABLE` logs `FRAUD_RELATIONSHIP_UNCONFIGURED` at error level and writes nothing. A crash or a timeout is caught by the existing `order-cron-errors` alarm (Lambda `Errors`); metric filters for the new log lines belong to FRD-16. Ids go through `MoneyAlarmLog.LogId`.

Configuration, read once per run (`RelationshipPatternSettings.FromEnvironment`). A value that is not an integer in its range keeps the default. No Lambda sets them today.

| Variable | Default | Range | Meaning |
|----------|---------|-------|---------|
| `FRAUD_RELATIONSHIP_LOOKBACK_HOURS` | 48 | 1 to 168 | Orders created this many hours ago or later are read |
| `FRAUD_RELATIONSHIP_MAX_ORDERS` | 1,000 | 1 to 10,000 | Funded orders checked per run |
| `FRAUD_PAIR_MIN_ORDERS` | 3 | 2 to 100 | Funded orders of one pair in the window that fire `REPEATED_PAIR` |
| `FRAUD_PAIR_WINDOW_DAYS` | 7 | 1 to 90 | Window of the pair count |

Infra: the order-cron Lambda gets `FRAUD_SIGNALS_TABLE`, one IAM statement with `dynamodb:Query` and `dynamodb:PutItem` on the fraud-signals table itself (no index, no update, no delete), the third hourly rule, and a reserved concurrency of 3 instead of 2 outside dev (dev sets none), so the three hourly rules fit at once. It already read the orders table with its indexes and the users table by key. The log group and its retention are the Lambda's own, unchanged; the `order-cron-errors` alarm text now names the scan.

Cost: on dev volume (tens of orders a day, so at most about 50 funded orders in the lookback) one run makes 12 status Queries (one page each, mostly empty, 0.5 to 1 read unit each), at most one GetItem per distinct user and one history Query per distinct buyer (about 150 reads of 0.5 to 2 read units), one consistent Query per hit and party, and one PutItem per new row (none on most runs): about 100 to 200 read units per run, a few thousand a day, a few cents a month on demand. The Lambda runs under 2 seconds at 256 MB, 24 times a day, inside the free tier; scheduled EventBridge rules are free. At the cap of 1,000 funded orders a run makes about 3,000 reads in under a minute, under 1 USD a month. These figures come from the request counts above; the scan has not been run against dev.

Tests: `server/tests/Services/RelationshipPatternRulesTests.cs` (each rule's hit and miss, the pair count's exclusions, the settings), `RelationshipPatternServiceTests.cs` (each pattern from a seeded fixture, the Queries made, one read per user and per buyer, the existence check and a later run, the deferred second hit, the order cap, the time slice, failed reads and writes), `FraudRelationshipSignalTests.cs` (weights, classes, once per order, the caps, the whitelist, `HasSignalAsync`), `server/tests/Functions/OrderCronRelationshipTests.cs` (the action and the Lambda wiring), `server/tests/Integration/RelationshipPatternDynamoDbTests.cs` (LocalStack: the three patterns on both parties, a rerun writes nothing, the score reads the rows) and `infra/test/unit/stack.test.ts` (the rule, the variable, the grant).

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
| `/sync` order `payment_submitted` (`SyncFunction`) | transition auth (`ValidateOrderTransitionAuth`), risk read, CRITICAL check, the deleted party (ACC-02: the order's buyer, farmer or trucker row carries `deletionState`, read strongly consistent: `ORDER_VALIDATION: A party to this order has deleted their account.`, nothing written), KYC (`CheckPaymentKycAsync`, unchanged), tiered step-up (`StepUpPolicy.CheckPayment` extended with the tier), the live-gateway order total check (`ORDER_PAYMENT_TOTAL_OUT_OF_RANGE` since FUP-22, then the payment method check), then the write, the FRD-09 payment rules (section 15.1, flag-only, after the write) and the mock auto-fund |
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

Why settlement only: all system money out goes through the three sweeps (`AutoReleaseAsync`, `AutoRefundAsync`, `SettleResolvedDisputesAsync`), so a hold at settlement freezes every system payout and refund of the party without enumerating the party's escrows. Eager holding would page three party GSIs keyed on party plus `createdAt` (`BuyerStatusIndex`, `FarmerStatusIndex`, `TruckerStatusIndex`, status only a filter), add latency and escrow writes to `/sync` or the admin Lambda, and still miss escrows created later. The cost: a party's escrows show "Under review" only once they are due to settle. FRD-05 (built, the reject response, section 19) shows the admin the user's open escrows read-only (the party GSIs with a status filter on `escrowed`, `disputed`, `dispute_resolved` and `held`, paged, admin side). FRD-07 (built) lists every held escrow, whoever the party, in the held queue (section 18.3).

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

Admin group only, through the existing admin routes: `held` to `released` (`POST /escrow/release`) and `held` to `refunded` (`POST /escrow/refund`). Built by FRD-07, the held queue in the admin app (Fraud Review, the Held escrows tab) reads `GET /admin/escrows/held` and calls these two routes with `trigger: admin_override`, the reason the admin typed (10 to 500 characters, asked again before the call) and an idempotency key `admin:{escrowId}:{op}:{epoch ms}`; a release sends the escrow's `deliveryId`, or its order or escrow id when it has none (a self-pickup order escrow), because the route requires the field but does not compare it with the row. An escrow held from `dispute_resolved` is offered the verdict's direction only (see below). A payout the live gateway cannot make answers `202 ESCROW_034` and waits in the manual payout queue as for any settlement. There is no resume endpoint: with settlement-only entry a held escrow is always one the system was about to settle, so release or refund is the whole decision. A release runs `ResolvePayoutAsync` as today. What a refund runs depends on `heldFor`:

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

`GET /admin/fraud-signals` (`AdminFunction.HandleListFraudSignalsAsync`) is how Fraud Review reads the signals. Query parameters: `status` (`pending`, the default, or `reviewed`; any other value is `400 ADMIN_005`), `limit` (default 20, at most 50) and `nextToken` (opaque, the signed last-evaluated key of the previous page). Response `AdminFraudSignalListResponse`: `{"signals": [FraudSignalDto, ...], "users": [FraudSignalUserDto, ...], "nextToken": "<token, omitted on the last page>", "total": <count on this page>}`, each row carrying its `resolution`. Without the table configured it answers an empty list.

Built by FRD-07 (2026-11-07): the list is one `Query` of a status partition of the sparse `StatusCreatedAtIndex` (partition `status`, sort `createdAt`, projection all, section 20), newest `createdAt` first; the Scan is gone. Only signal rows carry `status`, so the credit and whitelist rows of section 20 are never in the index; the signal-row filter (`attribute_not_exists(recordType) OR recordType = signal`) stays on the Query as a second guard, and a page it thinned is read on for the missing rows only (each read asks for `limit` minus the rows already found, at most 25 reads), so a page never passes `limit` and the token resumes right after the last row read. A token that does not decode or whose key belongs to another status or index is `400 ADMIN_005` (`field: nextToken`): a Query whose start key lies outside its partition fails, and starting over would answer the first page as the next. Each signal carries `createdAt`, the first refusal of its row's hour (section 14.3: one row per user, type and hour since FRD-08), and `orderId` when its entity names a transaction timeline without a read (`entityType: order`, or `escrow` with an order escrow id `escrow-order-{orderId}`). `users` holds one entry per distinct user of the page, in first appearance order: `userId`, `displayName` (when the users table has one), `riskScore` and `enforcementScore` (section 14.2), `riskTier` (`low`, `medium`, `high`, `critical`), `truncated` and `scoreAvailable` (`false` when the score read failed: the score and tier then say nothing, the page still answers). One `RiskScoreMemo` per page, one bounded score `Query` per user (section 14.3), so at most 50 per page. The score is admin-only, as section 17 requires of parties.

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

Admin surfaces: FRD-05 kept credit and whitelist rows out of the dashboard count and the list with a Scan filter on `recordType`. FRD-07 (built) replaced both Scans with the sparse `StatusCreatedAtIndex`, which excludes those rows by construction because they carry no `status`. The dashboard (`HandleDashboardStatsAsync`) counts `FraudSignalsCount` as the `pending` partition (`Select: COUNT`, every page, the signal-row filter kept), so it is the review backlog; before FRD-07 it counted every signal row, reviewed ones included. It also counts `heldEscrows`, the `held` partition of the escrows' `StatusExpiresIndex`.

`GET /admin/escrows/held` (FRD-07, `AdminFunction.HandleListHeldEscrowsAsync`), the held queue: one `Query` of the `held` partition of `StatusExpiresIndex` (a hold keeps the escrow's `expiresAt`, so every held escrow is in it), oldest `expiresAt` first, `limit` default 20 and at most 50, `nextToken` checked like the list's. Response `AdminEscrowListResponse` with the admin escrow DTO: the hold fields of section 18.2, the dispute and its verdict, the party names. It only lists; the exits are the escrow routes of section 18.3. Without the escrows table it answers `503 ADMIN_004`.

`GET /admin/users/{id}` (FRD-07) adds `suspendReason` next to `user` in the response (`AdminUserDetailResponse`), never inside it: `UserDto.SuspendReason` stays out of every party response.

The admin app's Fraud Review page (FRD-05; FRD-07 below): `reject` asks once more (it suspends the account); `whitelist` is offered on soft-class signals only (`GPS_ANOMALY`, `PRICE_ANOMALY`, section 15), because it clears the signal it is given for good and skips only soft signals until its end, so on a hard signal it would quietly erase hard weight (the server still accepts it there; approve is the admin's tool for a hard false positive); after each action a panel shows the result: approve no longer counts, the user suspended (with a warning when `sessionsEnded` is `false`) and the open escrows with the note that the next sweep holds them (and a hint when `openEscrowsComplete` is `false`), or that the whitelisted signal no longer counts and the soft signals do not count until the end; reviewed rows show the decision. Its error texts for `ADMIN_006` (404) and `ADMIN_007` are its own (`fraud.error_*`), so the shared `errors.*` keys of other pages keep their wording. English, Amharic and Afan Oromo.

FRD-07 (built) gives the page three tabs. To review (`status=pending`) and Reviewed read the list 50 a page (Load more) and group the loaded signals by user in first appearance order, so the user with the newest signal comes first; each group shows the user's name linked to the user page, the risk score and tier (or that it could not be read, and a note when a truncated read counts as CRITICAL) and a table of the user's signals with the type, severity, First seen (`createdAt`), the entity with a Transaction timeline link when the row has `orderId`, the decision and the actions above, unchanged. A note says one row stands for every refusal of its type in one hour and the time shown is the first. Held escrows lists the held queue: the escrow and its timeline, the parties, the amount, why it is held (`holdReason`), what the sweep was about to do (`heldFor`) and the status it was held from, and Release and Refund with the reason and the confirm of section 18.3. The dashboard shows the review backlog and the held count; the user page shows the suspend reason of a suspended account (`fraud_confirmed` labelled as the Fraud Review reject). English, Amharic and Afan Oromo. Admin e2e: `e2e/admin/fraud-review.spec.ts` (a seeded admin session and a mocked API, run against a local build).

No IAM change: the admin Lambda already reads the escrows table and its indexes, writes the fraud-signals, users and transaction-audit tables, and holds `AdminDisableUser` and `AdminUserGlobalSignOut` on the mobile pool (pinned in `infra/test/unit/stack.test.ts`). FRD-07 needs none either: its `grantReadWriteData` on fraud-signals covers the new index (`table/index/*`), and the escrows read grant covers `StatusExpiresIndex`. FRD-07 adds the route `GET /admin/escrows/held` on the admin authorizer.

---

## 20. Data Model Additions

The fraud-signals table keeps its keys (`userId`, `signalKey`) and its TTL attribute. The TTL attribute name `expiresAt` (Number, epoch seconds) is never renamed: the table is live, and a changed TTL attribute name rolls the deploy back.

| Row kind | `recordType` | `signalKey` | `status` | Other fields |
|----------|--------------|-------------|----------|--------------|
| Signal | absent (legacy, still written so) or `signal` | `{ISO}#{signalType}` | `pending` or `reviewed` | as section 4.3 |
| Credit (FRD-10, built) | `credit` | `{releasedAt}#CLEAN_DELIVERY#{escrowId}` | absent | `signalType: CLEAN_DELIVERY`, `weight: -5`, `entityType: escrow`, `entityId` (the escrow id), `createdAt` (the write), `expiresAt` (`releasedAt` plus 30 days) |
| Whitelist (FRD-05, built) | `whitelist` | `~whitelist` (one per user; sorts after every ISO key) | absent | `whitelistUntil` (ISO), `expiresAt` set to the same instant in epoch seconds, `reviewedBy`, `reviewedAt`, `reviewNotes` (when given) |

Credits (FRD-10): when an escrow completes through auto-release with no dispute, no hold and no signal on its order, delivery or escrow, each party gets a credit row: the buyer, the farmer, and the trucker when present. The buyer is included because a paid, received and undisputed trade is the same evidence for every party. Self-trade farming is bounded by `RISK_CREDIT_CAP`, and since FRD-15 a relationship signal on the order (section 15.2) blocks its credit: the hourly scan writes it within the hour of the funding, days before the auto-release.

Built by FRD-10 (2026-11-07) as `CleanDeliveryCreditService`, called from `EscrowCronFunction.AutoReleaseAsync`:

- **When.** Right after the auto-release's `ReleaseAsync` succeeded, that is once the farmer is paid (a trucker leg the hourly retry still owes does not hold the credit back: the trade itself was clean). A release answered as a duplicate is left to the run that released it. Nothing else credits: an admin release, the dispute sweep (`dispute_resolved`), refunds and the manual payout queue (a release waiting there is not a success) write none. One place, because only this sweep pays a trade whose dispute window closed undisputed.
- **Parties.** `buyerId`, `farmerId`, and the trucker: the escrow's `truckerId`, else, when the escrow pays a trucker leg (`truckerPayout` above 0), the trucker the payout takes from the order (`EscrowService.ResolveTruckerIdAsync`). A self-pickup has no trucker. A user named twice is credited once.
- **Clean.** The escrow row carries no `disputedBy`, `disputeResolution`, `heldAt` or `holdReason` (the sweep only pays `escrowed`, which no dispute or hold returns to; this guards older rows). Then one `Query` per party on its own partition over the table's 30 day life (`signalKey >= now minus SignalTtlDays`, eventually consistent, projecting the key only), filtered to signal rows (`recordType` absent or `signal`) whose `entityId` is the escrow, its order or its delivery and whose `resolution` is not `approve` or `whitelist`. Any such row in any party's partition, whatever its weight, means no credit for anyone. An approved (false positive) or whitelisted signal does not block, as it does not count (section 14.2); a `reject` blocks. A signal on the trip (`GPS_JUMP`, `GPS_MOCKED`) is not on the trade's entities and does not block. More than 2 pages of one partition (`FraudDetectionService.DefaultRiskMaxPages`, not read from the environment) fail closed: no credit (`signals_unread`). No Scan.
- **Idempotent.** The key is the escrow's stored `releasedAt` and its id, and the put is conditional (`attribute_not_exists(signalKey)`), so a replay of the same escrow writes nothing more, and two escrows released in the same hour are two credits. A missing or unparsable release time writes nothing (`no_release_time`), since a key from the clock would credit again on a replay.
- **Failure.** The credit runs after the money moved and catches everything: a failed read or write never fails, retries or undoes the release, and is not a settlement alarm. A failed write of one party leaves the others written. Log lines (not alarmed; a lost credit only leaves a score higher): `CLEAN_DELIVERY_CREDITED escrowId=<id> credited=<n> alreadyCredited=<n> failed=<n>`, `CLEAN_DELIVERY_NOT_CREDITED escrowId=<id> reason=<dispute|hold|signal|signals_unread|no_release_time>`, `CLEAN_DELIVERY_CREDIT_FAILED escrowId=<id> [userId=<id>] error=<type>`. The escrow is not visited again, so a lost credit is not retried.
- **Admin.** A credit row has no `status`, so it is never in the status index Fraud Review and the dashboard count read (FRD-07), and the review action refuses it (section 19, `ScanFilter.SignalRowsOnly`); the admin user page shows no fraud rows. The score is the only reader.
- **Live mode (known gap).** In live mode (`enableTelebirrLive`, and wherever `TELEBIRR_MOCK` is off) the Telebirr gateway cannot pay out yet (`PAYOUT_NOT_SUPPORTED`), so every auto-release goes to the manual payout queue (`ManualPayoutWaiting`) and no credit is written. Until B2B payouts exist, credits accrue only where the mock gateway pays (dev). Crediting when an admin closes an auto-release's manual payout (keyed on the `releasedAt` that close writes) is a follow-up.
- **Cost.** Per auto-released escrow, two or three `Query` calls of one page and two or three `PutItem` calls. Infra: escrow-cron gets `dynamodb:PutItem` on the table (section 23).

Fraud-signals index (FRD-07, built): `StatusCreatedAtIndex`, partition `status`, sort `createdAt`, projection all, the table's only GSI (CloudFormation adds one GSI per table per update, so another waits for a later deploy). Sparse: a row is indexed only when it has both attributes as strings. Every signal row has had `status` (`pending`, then `reviewed`) and `createdAt` since the writer persisted signals (2026-07-12, #48), and the 30-day TTL removed anything older, so no backfill is needed; credit rows have `createdAt` but no `status`, the whitelist row has neither. The admin list and the dashboard count read it; the held queue reads the escrows' existing `StatusExpiresIndex`, so the escrows table gets no new index.

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
| escrow `held` | status | escrow-protocol 1.22.0 | FRD-03 (server, infra; behind `FRAUD_HOLD_ENABLED`, shipped off; built with the `account_suspended` trigger); the tier trigger built by FRD-04 (`risk_critical`, escrow-protocol 1.27.0); the reject trigger built by FRD-05 (`fraud_confirmed`, escrow-protocol 1.29.0) | FRD-06 tolerant parser and badge (must ship before the flag is on); apps before build 2 read `escrowed` (FUP-61, built; flag on in dev); admin vocabulary with FRD-03 (next row); held queue FRD-07 (built) | escrow-protocol 1.7.0, order-lifecycle 1.9.0, escrow-protocol 1.31.0 (old apps) |
| escrow `held` in the admin app (`admin/src/lib/transaction.ts` `escrowStatusVariant`, `admin/src/pages/EscrowsPage.tsx` `statusVariant`, i18n en, am, om: `escrows.held`, a new `transactions.escrow_held`, `action_fraud_hold`, `action_fraud_hold_release`, `action_fraud_hold_refund`) | admin UI | FRD-03 | FRD-03 (the admin app is where held escrows are resolved, so it ships with the status) | FRD-07 (built) adds the held queue and a dashboard held count | none |
| `fraud_confirmed` | `suspendReason` and `holdReason` value | FRD-05 | the Fraud Review reject action (`AdminFunction`, built); refused as free text on the user action route | `PartySuspensionGate` (built, first in precedence); `risk-settlement-skipped` alarm text | escrow-protocol 1.29.0 |
| `ADMIN_009` and `ADMIN_006` on `POST /admin/fraud-signals/{id}/action`, `ADMIN_017` on `POST /admin/users/{id}/action` | rejection | FRD-05 | `AdminFunction` (built) | admin app, en, am, om (built) | this document (section 19) |
| `FRAUD_DUPLICATE_LISTING`, `FRAUD_VELOCITY_EXCEEDED`, `ESCROW_011` | rejection | F-14 | live | live | sync-protocol, escrow-protocol |
| `IDOR_CREATE_CONFLICT`, `ROLE_FORBIDDEN`, `IMMUTABLE_FIELD_CHANGE`, `QR_HANDOFF_FAILURE`, `TELEBIRR_PAYER_MISMATCH`, `TELEBIRR_PAYER_REFUSED`, `STALE_VERSION_BURST`, `SUSPENDED_TOKEN_USE` | signals (weights in section 15) | FRD-08 | FRD-08 (server, built: `SyncFunction`, `EscrowFunction`, `TelebirrWebhookFunction`, `RefreshFunction`; types and weights in `FraudDetectionService`) | admin Fraud Review (live, raw type); the risk score of section 14 | sync-protocol 1.27.0, escrow-protocol 1.32.0 |
| `AMOUNT_ANOMALY`, `NEW_ACCOUNT_LARGE_AMOUNT`, `KYC_THRESHOLD_STRUCTURING`, `CANCEL_AFTER_FUND_RATE`, `DISPUTE_RATE`, `IMPOSSIBLE_DELIVERY_TIME` | signals (weights in section 15) | FRD-09 | FRD-09 (server, built: `TransactionPatternRules`, `TransactionPatternService`, hooks in `SyncFunction` and `EscrowFunction`; types, weights and classes in `FraudDetectionService`) | admin Fraud Review (live, raw type); the risk score of section 14 (once per entity, capped at `RISK_PATTERN_TYPE_CAP`) | delivery-verification-api 1.23.0 (`pickupCapturedAt`), escrow-protocol 1.36.0 |
| `CONTACT_THEN_TRANSPORT_CANCEL` | signal (weight 10, default) | ESC-48 | `SyncFunction.ProcessTransportFailureCancelAsync` (live, best-effort) | admin Fraud Review (live, raw type); the risk score of section 14 (FRD-02, default weight, hard) | order-lifecycle 1.18.0 |
| escrow `releaseNotBefore`, `PAYOUT_DELAY_SET` (log line, not alarmed), the `payout_delay` trucker retry reason, `payoutsDelayed` in the run summary | field, log, reason | FRD-13 | FRD-13 (built: `PayoutDelayPolicy`, the sync stamp and the escrow-cron stamp repair) | the auto-release and the trucker retry (built); mobile and web "Payout expected on (date)" for the farmer and the trucker (built); never the buyer | escrow-protocol 1.33.0, order-lifecycle 1.27.0, delivery-verification-api 1.19.0 |
| `CLEAN_DELIVERY` credit row, `CLEAN_DELIVERY_CREDITED`, `CLEAN_DELIVERY_NOT_CREDITED`, `CLEAN_DELIVERY_CREDIT_FAILED` | row kind (section 20), log lines (not alarmed) | FRD-10 | FRD-10 (built: `CleanDeliveryCreditService`, `EscrowCronFunction.TryCreditCleanDeliveryAsync`) | the risk score of section 14 (credits, capped, floored at 0) | this document only |
| `SELF_TRADE`, `SHARED_DEVICE_TRADE`, `REPEATED_PAIR`; log lines `FRAUD_RELATIONSHIP_SCAN`, `FRAUD_RELATIONSHIP_SCAN_TRUNCATED`, `FRAUD_RELATIONSHIP_CHECK_FAILED`, `FRAUD_RELATIONSHIP_SIGNAL_FAILED`, `FRAUD_RELATIONSHIP_UNCONFIGURED` (not alarmed) | signals (weights in section 15), log lines | FRD-15 | FRD-15 (built: `RelationshipPatternRules`, `RelationshipPatternService`, `OrderCronFunction` action `relationship_patterns`; types, weights and classes in `FraudDetectionService`) | admin Fraud Review (live, raw type, the order linked); the risk score of section 14 (once per order; the two soft types share one cap of `RISK_PATTERN_TYPE_CAP`); the clean-delivery credit check of section 20; metric filters for the log lines are FRD-16's | this document only |
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
| FRD-07 (built) | Sparse `status` GSI on fraud-signals, grouped Fraud Review with score and tier, held queue, transaction timeline link. Delivered as `StatusCreatedAtIndex` in `infra/lib/stacks/database-stack.ts` and the route `GET /admin/escrows/held`; in `AdminFunction` the list as a Query (`QueryIndexPageAsync`, `TryReadIndexToken`) with the per-user scores (`ScoreSignalUsersAsync`, `FraudSignalUserDto`) and `FraudSignalDto.OrderId`, the held queue (`HandleListHeldEscrowsAsync`), the dashboard counts (`QueryCountAsync`, `HeldEscrows`), `AdminUserDetailResponse.SuspendReason`; the admin Fraud Review tabs, the held queue with Release and Refund, the dashboard card and the user page reason (en, am, om). Tests: `AdminFraudReviewQueueTests`, `FraudReviewQueueDynamoDbTests` (LocalStack), the admin unit tests and `e2e/admin/fraud-review.spec.ts`. Deploy order: infra (the index) before the server. CloudFormation does not wait for the index backfill, so for a few minutes after the deploy the Fraud Review list and the dashboard can answer 500 until the index is ACTIVE (admin only, heals itself). |
| FRD-08 (built) | Signals from existing rejects plus the per-user, per-type write rate limit. Delivered as the types, weights and soft classes in `FraudDetectionService` (`SignalWeight`, `IsSoftSignal`), the hour-start key and conditional put in `RecordFraudSignalAsync`, `SyncFunction.RefuseWithSignalAsync` and `RoleGateSignal`, the per-batch memo in `TryRecordFraudSignalAsync`, the stale-version counter in `ProcessOperationAsync`, `EscrowFunction.TryRecordRoleForbiddenAsync`, `TelebirrWebhookFunction.TryRecordPayerMismatchAsync` and `NormalizePayerPhone`, `RefreshFunction.TryRecordSuspendedTokenUseAsync`, the per-entity count in `RiskScoreCalculator` (`FraudDetectionService.CountsOncePerEntity`, `entityId` in the score Query's projection); infra (next table) |
| FRD-09 (built) | Transaction pattern rules with thresholds in config (section 15.1). Delivered as `TransactionPatternRules` and `TransactionPatternSettings` (new), `TransactionPatternService` (new), the six types with weights and classes in `FraudDetectionService` (`IsPatternSignal`, `CountsOncePerEntity`), `RISK_PATTERN_TYPE_CAP` in `RiskScoreSettings` (`CapOf`) and the calculator's per-type cap, the hooks `SyncFunction.TryRecordPatternSignalsAsync` (payment, committed farmer cancel, trucker drop-out, trip-cancel cascade) and `TryRecordDeliveryPatternSignalsAsync` (delivery dispute, delivery confirm), `pickupCapturedAt` on the pickup write, `EscrowFunction.TryRecordDisputeRateAsync`; no infra change (defaults in code; the reads use existing GSIs the Lambdas already hold) |
| FRD-10 (built) | `CLEAN_DELIVERY` credit rows from the auto-release sweep. Delivered as `CleanDeliveryCreditService` (new: the escrow-row check, the per-party signal Query, the conditional put keyed `{releasedAt}#CLEAN_DELIVERY#{escrowId}`), `EscrowCronFunction.CleanDeliveryCredits` (wired when `FRAUD_SIGNALS_TABLE` is set) and `TryCreditCleanDeliveryAsync` after a successful, non-duplicate auto-release, `PartySuspensionGate.HasTruckerLeg` made internal for it; the calculator already read credit rows (FRD-02), so no scoring change; infra: escrow-cron `dynamodb:PutItem` on the fraud-signals table |
| FRD-11 (built) | Pickup proof (delivery-verification-api 1.24.0, QR Secret Storage). Most of it predates the ticket: the farmer's nonce in the `delivery-qr-secrets` table, the scan or typed code checked on sync, single use per leg, 14-day freshness, `deferred` while the nonce has not synced (offline first), `gpsPickup` and `pickupCapturedAt` on the pickup, the self-pickup's mandatory GPS. Delivered here: the pickup legs (the trucker's `pickedUp`, the self-pickup buyer's `delivered`) fail closed whatever the money (`SyncFunction` QR gate, `AwaitFarmerNonce`; a delivery without an escrow passed with no QR before), and the scan screen's Confirm waits for the fix taken at the scan (`QrScanScreen`, at most 30 seconds). No infra change, no flag (shipped apps already send the farmer's nonce on every pickup). The cargo photo upload stays with FRD-12; a missing pickup fix is still not recorded (SPK-01) |
| FRD-13 (built) | The payout delay of section 17.5. Delivered as `PayoutDelayPolicy` and `PayoutDelaySettings` (new), `releaseNotBefore` in `SyncFunction.StampEscrowDeliveryConfirmedAsync` and `EscrowCronFunction.StampIfDeliveredAsync`, the filter in `AutoReleaseAsync`, the deferral in `EscrowService.RetryTruckerLegAsync` and the overdue age in `RetryPendingTruckerLegsAsync`, `EscrowDto.ReleaseNotBefore` and `EscrowSummaryDto.ReleaseNotBefore` (payees and admins), the mobile and web "Payout expected on (date)"; no infra change (the defaults live in code; the sync Lambda and escrow-cron already read the users, fraud-signals and escrows tables with their indexes) |
| FRD-15 (built) | Relationship patterns (section 15.2). Delivered as `RelationshipPatternRules` and `RelationshipPatternSettings` (new), `RelationshipPatternService` (new), the three types with weights and classes in `FraudDetectionService` (`IsRelationshipSignal`, `IsCappedRelationshipType`, `CountsOncePerEntity`) and their shared cap group in `RiskScoreSettings` (`CapKeyOf`, `CapOf`), `IFraudDetectionService.HasSignalAsync` (the existence check), `IOrderRepository.QueryByStatusCreatedSinceAsync` (a key-bounded page of `StatusCreatedAtIndex`), `OrderCronFunction.HandleRelationshipPatternsAsync` and its wiring; infra: order-cron `FRAUD_SIGNALS_TABLE`, `dynamodb:Query` and `dynamodb:PutItem` on the fraud-signals table, the hourly rule `{prefix}-order-cron-relationship-patterns`, reserved concurrency 3 outside dev, the `order-cron-errors` alarm text |
| FRD-17 (built) | Adversarial personas against dev (section 11.6). Delivered as `scripts/e2e_fraud_personas.py` (stdlib; `--self-test`, `--dry-run`, `--recheck` with its hold assertion) and the run report in `specs/reports/`; no server, client or infra change |

Infrastructure needed (`infra/lib/stacks/api-stack.ts`):

| Lambda | Today | Needed |
|--------|-------|--------|
| `EscrowCronFunction` | `USERS_TABLE` read; `TRANSACTION_AUDIT_TABLE` write and `FRAUD_HOLD_ENABLED` (FRD-03); `FRAUD_SIGNALS_TABLE` with read (FRD-04 gate, built) and `dynamodb:PutItem` only (FRD-10 credits, built) | no change |
| `EscrowFunction` | `FRAUD_SIGNALS_TABLE` read and write | `TRANSACTION_AUDIT_TABLE` with write (FRD-03 admin exits) |
| `SyncFunction`, `AdminFunction` | `FRAUD_SIGNALS_TABLE` read and write, `TRANSACTION_AUDIT_TABLE` | no change for FRD-02 to FRD-05; FRD-07 (built) Queries the new index through the existing read grant |
| `RefreshFunction` | `USERS_TABLE` read, `RATE_LIMITS_TABLE` | `FRAUD_SIGNALS_TABLE` with `dynamodb:PutItem` only (FRD-08, built) |
| `TelebirrWebhookFunction` (only with `enableTelebirrLive`) | escrows, escrow audit, idempotency, users, orders, crops, transaction audit | `FRAUD_SIGNALS_TABLE` with `dynamodb:PutItem` only (FRD-08, built) |
| `OrderCronFunction` | orders (with indexes), users read, escrows, escrow audit, crops, trips, deliveries, transaction audit, the idempotency inbox index | `FRAUD_SIGNALS_TABLE` with `dynamodb:Query` and `dynamodb:PutItem` on the table only, a third hourly rule (FRD-15, built) |
| fraud-signals table | keys and TTL | `StatusCreatedAtIndex` (FRD-07, built) |

Notes for FRD-04, from the FRD-02 review (all handled by FRD-04):

- `EscrowCronFunction` had no `FRAUD_SIGNALS_TABLE` environment variable, no grant on the table and no `FraudDetectionService`. FRD-04 adds all three (the service built only when the variable is set, with a rate-limit repository that names no table because the cron never runs the velocity checks) and a `RiskScoreMemo` per sweep run.
- `EscrowFunction` builds its fraud service only when `RATE_LIMITS_TABLE` is set (`_fraudService` is nullable). FRD-04 decided: no service, no tier, LOW (section 17).
- A `Source = Unavailable` result carries `Tier = Low` (section 14.4). Every caller checks `Source` first; FRD-04 owns the sweep test that an unscored party skips the escrow (`server/tests/Functions/EscrowCronRiskGateTests.cs`).

Ties to EPIC ESC: ESC-33 writes `NO_SHOW` and `DELIVERY_OVERDUE` signals and re-attributes `GPS_ANOMALY`; the client condition for turning the hold flag on is met by FUP-61's server mapping rather than a minimum app version gate (section 18.7), and ESC-25 can reuse its `X-App-Build` header; design cases H1 and H4 of the three-party escrow design (a party suspended mid-order, a buyer suspended after funding) resolve to this `held` status. The account deletion contract (`specs/contracts/account-deletion.md`, ACC-01, not built yet) refuses deletion while the user is a party to a `held` escrow, because any status it does not list blocks, and refuses self-deletion of a suspended user; its open point on ban evasion stays with EPIC FRD, since a CRITICAL user who is not suspended and has no open escrow can still delete the account.

---

## Changelog

| Version | Date | Change |
|---------|------|--------|
| 3.17.0 | 2026-11-07 | FRD-15 built: new section 15.2, an hourly relationship scan as the `relationship_patterns` action of the existing order-cron Lambda (a third hourly EventBridge rule; no new Lambda, table or index). It reads the funded orders created in the last 48 hours on the orders' `StatusCreatedAtIndex` (key-bounded, twelve status partitions, at most 1,000 orders per run, a 4 minute slice), the two users rows and the buyer's orders on `BuyerIdCreatedAtIndex`, and writes on both the buyer and the farmer, with the order as the entity: `SELF_TRADE` (40, hard, uncapped: one user id, phone or Fayda ID on both sides), `SHARED_DEVICE_TRADE` (25, soft: a common install in `knownDevices`, not on an order the farmer declined), `REPEATED_PAIR` (5, soft: an order the farmer accepted completes 3 accepted funded orders of one pair in the 7 days up to it, so a burst flags only the order that completes the count and a buyer cannot flag a farmer with orders the farmer refuses); the two soft types share one cap of `RISK_PATTERN_TYPE_CAP` (a cap group), so alone they stop at MEDIUM. Idempotent across runs through `HasSignalAsync` (an existing row of the type on the order, any review status, means no write) and the writer's hour key; a second hit of a user and type in one run is deferred to the next run. Not built: same IP on both sides (no table stores a source IP per user); confirm-then-dispute (covered by `DISPUTE_RATE`, not duplicated). Known gaps: a truncated run is not resumed; an order funded more than 48 hours after its creation is never scanned. A relationship row on an order blocks its clean-delivery credit. Four `FRAUD_*` settings, log lines, cost. Infra: order-cron `FRAUD_SIGNALS_TABLE`, Query and PutItem on the table only, reserved concurrency 3 outside dev. Sections 7.4, 11.6, 14.2, 14.5, 15, 20, 22 and 23 updated; status updated |
| 3.16.0 | 2026-11-07 | FRD-17 built: new section 11.6, the adversarial personas script (`scripts/e2e_fraud_personas.py`) with its run rules on dev (own accounts through the mock Fayda provider, 9 registrations per run, no SMS, no S3, mock gateway) and the limits of one run (one row per user, type and hour; holds only at settlement, checked with `--recheck`, which exits 1 for an overdue target that is not held); the run reads both Fraud Review statuses and asserts the server's tier from `users[]` (FRD-07), the replica only cross-checks; section 23 row; documentation only |
| 3.15.0 | 2026-11-07 | FRD-07 built: Fraud Review reads the new sparse `StatusCreatedAtIndex` (partition `status`, sort `createdAt`) of fraud-signals instead of scanning, one status at a time (`pending` by default or `reviewed`, else `400 ADMIN_005`), newest first, at most 50 a page, with a checked `nextToken`; each distinct user on the page comes with the section 14 score, tier, `truncated` and `scoreAvailable`, and an order or order-escrow signal with its `orderId`. The held queue `GET /admin/escrows/held` reads the `held` partition of `StatusExpiresIndex`; the admin app releases or refunds from it through the escrow routes with a reason. The dashboard counts pending signals (was every signal row) and held escrows on the indexes. The admin user detail returns `suspendReason` beside the user. Existing signal rows carry `status` and `createdAt`, so no backfill; credit and whitelist rows are never indexed. Sections 18.1, 18.3, 19, 20, 22 and 23; status updated |
| 3.14.0 | 2026-11-07 | FRD-11 built (delivery-verification-api 1.24.0): a pickup at the farm needs the farmer's QR whatever the money, deferred until the nonce syncs and refused 14 days after the later of the create and the scan, no escrow read; the scan screen waits for the fix taken at the scan; section 23 updated |
| 3.13.0 | 2026-11-07 | FRD-10 built: the clean-delivery credits of section 20 (the score decay). After the auto-release pays an escrow (not a duplicate answer), escrow-cron writes one `CLEAN_DELIVERY` row (weight minus 5) per party, the buyer, the farmer and the trucker when present, when the escrow row records no dispute or hold and no party's partition holds a signal on the escrow, its order or its delivery (an approved or whitelisted signal does not block, a rejected one does; one bounded `Query` per party, more than 2 pages fails closed). The key is `{releasedAt}#CLEAN_DELIVERY#{escrowId}` with a conditional put, so a replay never credits twice and two clean deliveries in one hour are two credits (FUP-62: never through `RecordFraudSignalAsync`); `expiresAt` is the release plus 30 days. A failed credit is logged (`CLEAN_DELIVERY_CREDIT_FAILED`) and never fails the release. No scoring change: the calculator already reads credits, capped at `RISK_CREDIT_CAP` and floored at 0, and an approved signal already stops counting. Admin lists still exclude credit rows. Infra: escrow-cron `dynamodb:PutItem` on the fraud-signals table. Sections 4.3, 11.1, 14.2, 15, 20, 22 and 23 updated |
| 3.12.0 | 2026-11-06 | FRD-09 built (delivery-verification-api 1.23.0, escrow-protocol 1.36.0): new section 15.1, six transaction pattern rules with signal types, weights and classes in section 15 (`AMOUNT_ANOMALY` 10 soft, `NEW_ACCOUNT_LARGE_AMOUNT` 10 soft, `KYC_THRESHOLD_STRUCTURING` 20 hard, `CANCEL_AFTER_FUND_RATE` 15 soft, `DISPUTE_RATE` 10 soft, `IMPOSSIBLE_DELIVERY_TIME` 15 soft), each written after the write it observes succeeded, flag-only and best effort, with sixteen `FRAUD_*` thresholds in configuration; the amount rule adds a minimum multiple of the mean to the 3 sigma test; rates need 5 orders; the delivery trip runs from the earlier of the pickup's arrival and the trucker's pickup capture time (the new server-only `pickupCapturedAt`) to the server's `confirmedAt`, never a buyer phone time, and a point at (0, 0) is no fix; structuring applies only to a buyer without a Fayda ID and counts only funded payments, and its payment writes no `NEW_ACCOUNT_LARGE_AMOUNT` as well; a trip cancel counts each paid order on the trip; the pattern types count once per entity (14.2) and are capped per type at `RISK_PATTERN_TYPE_CAP` (50, 14.5); `IMPOSSIBLE_DELIVERY_TIME` is written on the delivery's trucker; bounded reads on existing GSIs, no infra change; sections 4.3, 17.1, 22 and 23 updated |
| 3.11.0 | 2026-10-28 | LT-07 built (live-trip-api 1.0.4, notifications-api 1.3.0, delivery-verification-api 1.20.0): two check-in signal types in section 15, `GPS_JUMP` (10, soft; only between check-ins with `accuracyM` at most 1,000) and `GPS_MOCKED` (25, soft), written best effort on the trip's trucker with the trip as the entity and counted once per (type, trip) like the FRD-08 types (a trip with a mock-location app on, or a phone with a bad GPS, writes a row in every hour it drives), so one trip alone stays LOW; flag-only, never a refusal: the trail is supporting evidence and the QR scans prove the handoffs |
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

**Last Updated**: 2026-11-07
**Author**: Architecture Agent
**Status**: Rules 1 to 4, signal persistence and the risk score (FRD-02) implemented. The enforcement of section 17 is built (FRD-04), with the payout delay of section 17.5 (FRD-13). Signals from existing rejects and the write limit are built (FRD-08), the transaction pattern rules of section 15.1 (FRD-09) and the clean-delivery credits of section 20 (FRD-10). The adversarial personas of section 11.6 run against dev (FRD-17). The pickup proof is built (FRD-11). The escrow `held` status of section 18 is built for a party rejected in Fraud Review (FRD-05), a suspended party (FRD-03) and a CRITICAL party (FRD-04), behind `FRAUD_HOLD_ENABLED` (on in dev, off in staging and prod; older apps read `escrowed`, FUP-61). The admin actions of section 19 and the whitelist row are built (FRD-05), and Fraud Review on the sparse status index with the held queue (FRD-07). The relationship patterns of section 15.2 are built (FRD-15). The rest of the risk engine (AI advisory, sections 21 to 23) specified, not built (EPIC FRD).
