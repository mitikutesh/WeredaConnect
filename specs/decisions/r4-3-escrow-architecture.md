# R4.3 Telebirr Escrow System Architecture

## Version
1.0.0 | 2026-02-17

## Status
Approved (Architect)

---

## 1. Component Overview

```
┌─────────────────────────────────────────────────────────────────────┐
│                         MOBILE (Flutter)                             │
│                                                                      │
│  ┌──────────────┐  ┌─────────────────┐  ┌────────────────────────┐  │
│  │  Escrow       │  │  Escrow         │  │   Isar DB              │  │
│  │  Payment      │──│  Repository     │──│  (Payment schema)      │  │
│  │  BLoC         │  │  (online-first  │  │  + EscrowIntent cache  │  │
│  │               │  │   for mutations,│  │                        │  │
│  │  Events/States│  │   cache for     │  │  Local state only —    │  │
│  │               │  │   reads)        │  │  NO sync queue for $   │  │
│  └──────────────┘  └────────┬────────┘  └────────────────────────┘  │
│                             │                                        │
│                   ┌─────────┴──────────┐                             │
│                   │ Escrow Intent Queue │                             │
│                   │ (local queue for    │                             │
│                   │  offline intent,    │                             │
│                   │  NOT sync queue)    │                             │
│                   └─────────┬──────────┘                             │
└─────────────────────────────┼────────────────────────────────────────┘
                              │
               POST /escrow/initiate     GET /escrow/{id}
               POST /escrow/dispute      GET /escrow/history
               (Dedicated endpoints, NOT /sync)
                              │
                              ▼
┌─────────────────────────────────────────────────────────────────────┐
│                    API GATEWAY (HTTP API v2)                         │
│  Cognito JWT Authorizer on ALL /escrow/* routes                     │
│                                                                      │
│  Routes:                                                             │
│    POST /escrow/initiate  → EscrowFunction                           │
│    POST /escrow/release   → EscrowFunction (internal/admin)          │
│    POST /escrow/dispute   → EscrowFunction                           │
│    POST /escrow/refund    → EscrowFunction (internal/admin)          │
│    GET  /escrow/{id}      → EscrowFunction                           │
│    GET  /escrow/history   → EscrowFunction                           │
└──────────────────────────────┬──────────────────────────────────────┘
                               │
                               ▼
┌─────────────────────────────────────────────────────────────────────┐
│                    SERVER (.NET 10 AOT)                               │
│                                                                      │
│  ┌─────────────────────────────────────────────────────────────┐    │
│  │                    EscrowFunction                            │    │
│  │  Routes to handler based on HTTP method + path               │    │
│  └────────────────────────────┬────────────────────────────────┘    │
│                               │                                     │
│                               ▼                                     │
│  ┌─────────────────────────────────────────────────────────────┐    │
│  │                    EscrowService                             │    │
│  │  State machine + commission + Fayda check + idempotency      │    │
│  └────┬───────────────────┬────────────────────────┬───────────┘    │
│       │                   │                        │                │
│       ▼                   ▼                        ▼                │
│  ┌────────────┐  ┌────────────────┐  ┌─────────────────────┐      │
│  │ IEscrow    │  │ ITelebirr      │  │ IEscrowIdempotency  │      │
│  │ Repository │  │ Gateway        │  │ Repository          │      │
│  │ (DynamoDB) │  │ (Mock for MVP) │  │ (DynamoDB, 30d TTL) │      │
│  └────────────┘  └────────────────┘  └─────────────────────┘      │
│       │                                                             │
│       ├── EscrowsTable                                              │
│       ├── EscrowAuditTable                                          │
│       └── EscrowIdempotencyTable                                    │
│                                                                      │
│  ┌─────────────────────────────────────────────────────────────┐    │
│  │                  Scheduled Lambdas (Cron)                    │    │
│  │  AutoReleaseFunction  — hourly — release after 48h window    │    │
│  │  AutoRefundFunction   — hourly — refund after 7-day timeout  │    │
│  │  ReconciliationFunction — 6h — check stuck 'initiated'       │    │
│  └─────────────────────────────────────────────────────────────┘    │
└─────────────────────────────────────────────────────────────────────┘
```

---

## 2. Critical Architecture Decision: NOT Synced via /sync

**Why escrow uses dedicated endpoints instead of the sync queue:**

| Factor | Sync Queue (POST /sync) | Dedicated /escrow/* |
|--------|------------------------|---------------------|
| Money movement | Cannot hold funds offline | Server coordinates with Telebirr |
| Idempotency TTL | 7 days | 30 days (financial records) |
| Authorization | Entity-level (owner) | Role-based (buyer/farmer/admin/system) |
| Conflict resolution | Server-wins (overwrite) | State machine (reject invalid) |
| Audit trail | Optional | Mandatory (every state change) |
| Error handling | Retry queue | Specific error codes (ESCROW_*) |

**Offline behavior**: Payment INTENT can be queued locally. When online, the intent is sent to POST /escrow/initiate. Actual fund movement always requires server + Telebirr connectivity.

---

## 3. Escrow State Machine

```
                    +-----------+
                    | initiated |
                    +-----+-----+
                          |
              +-----------+-----------+
              |                       |
        [Telebirr hold           [buyer cancels
         succeeds]                before hold]
              |                       |
              v                       v
        +-----------+          +-----------+
        | escrowed  |          | cancelled |  (TERMINAL)
        +-----+-----+          +-----------+
              |
    +---------+---------+----------+
    |                   |          |
[delivery         [dispute    [7-day timeout,
 confirmed +       raised      no delivery]
 48h passed]       <48h]           |
    |                   |          |
    v                   v          v
+-----------+     +-----------+ +-----------+
| released  |     | disputed  | | refunded  |  (TERMINAL)
+-----+-----+     +-----+-----+ +-----------+
      |                 |
      v           +-----+-----+
+-----------+     |           |
| completed |  [farmer     [buyer
+-----------+   wins]       wins]
 (TERMINAL)      |           |
                  v           v
            +-----------+ +-----------+
            | released  | | refunded  |
            +-----+-----+ +-----------+  (TERMINAL)
                  |
                  v
            +-----------+
            | completed |
            +-----------+  (TERMINAL)
```

### 3.1 Server-Side Transition Enforcement

```csharp
private static readonly Dictionary<string, HashSet<string>> ValidEscrowTransitions = new()
{
    { "initiated", new HashSet<string> { "escrowed", "cancelled" } },
    { "escrowed", new HashSet<string> { "released", "disputed", "refunded" } },
    { "disputed", new HashSet<string> { "released", "refunded" } },
    { "released", new HashSet<string> { "completed" } },
    // Terminal states: completed, cancelled, refunded — no transitions out
};
```

---

## 4. Server Implementation

### 4.1 New Files

| File | Description |
|------|-------------|
| `server/src/Functions/EscrowFunction.cs` | Lambda handler routing all /escrow/* endpoints |
| `server/src/Services/EscrowService.cs` | State machine, commission, Fayda check, idempotency |
| `server/src/Services/ITelebirrGateway.cs` | Interface for Telebirr API abstraction |
| `server/src/Services/MockTelebirrGateway.cs` | Mock implementation for development |
| `server/src/Data/IEscrowRepository.cs` | Repository interface |
| `server/src/Data/EscrowRepository.cs` | DynamoDB implementation |
| `server/src/Data/IEscrowAuditRepository.cs` | Append-only audit trail interface |
| `server/src/Data/EscrowAuditRepository.cs` | Audit trail implementation |
| `server/src/Data/IEscrowIdempotencyRepository.cs` | 30-day TTL idempotency |
| `server/src/Data/EscrowIdempotencyRepository.cs` | Implementation |
| `server/src/Models/EscrowDtos.cs` | All escrow DTOs |
| `server/src/Functions/AutoReleaseFunction.cs` | Hourly cron — auto-release |
| `server/src/Functions/AutoRefundFunction.cs` | Hourly cron — auto-refund |
| `server/src/Functions/ReconciliationFunction.cs` | 6-hourly cron — reconcile stuck |

### 4.2 EscrowFunction Pattern

```csharp
public class EscrowFunction
{
    private readonly EscrowService _escrowService;

    public EscrowFunction()
    {
        var escrowsTable = Environment.GetEnvironmentVariable("ESCROWS_TABLE")
            ?? throw new InvalidOperationException("ESCROWS_TABLE is not set.");
        var auditTable = Environment.GetEnvironmentVariable("ESCROW_AUDIT_TABLE")
            ?? throw new InvalidOperationException("ESCROW_AUDIT_TABLE is not set.");
        var idempotencyTable = Environment.GetEnvironmentVariable("ESCROW_IDEMPOTENCY_TABLE")
            ?? throw new InvalidOperationException("ESCROW_IDEMPOTENCY_TABLE is not set.");

        var dynamoDb = new AmazonDynamoDBClient();
        var escrowRepo = new EscrowRepository(dynamoDb, escrowsTable);
        var auditRepo = new EscrowAuditRepository(dynamoDb, auditTable);
        var idempotencyRepo = new EscrowIdempotencyRepository(dynamoDb, idempotencyTable);
        var telebirrGateway = new MockTelebirrGateway(); // Replace with real in production

        _escrowService = new EscrowService(
            escrowRepo, auditRepo, idempotencyRepo, telebirrGateway);
    }

    public async Task<APIGatewayProxyResponse> FunctionHandler(
        APIGatewayProxyRequest request, ILambdaContext context)
    {
        // Extract authenticated user
        var userId = ExtractUserId(request);
        if (string.IsNullOrEmpty(userId))
            return LambdaResponse.Error(401, "ESCROW_005", "Authentication required.");

        return (request.HttpMethod, request.Path) switch
        {
            ("POST", var p) when p.EndsWith("/initiate")
                => await HandleInitiateAsync(request, userId, context),
            ("POST", var p) when p.EndsWith("/release")
                => await HandleReleaseAsync(request, userId, context),
            ("POST", var p) when p.EndsWith("/dispute")
                => await HandleDisputeAsync(request, userId, context),
            ("POST", var p) when p.EndsWith("/refund")
                => await HandleRefundAsync(request, userId, context),
            ("GET", var p) when p.EndsWith("/history")
                => await HandleHistoryAsync(request, userId, context),
            ("GET", _) when request.PathParameters?.ContainsKey("id") == true
                => await HandleGetByIdAsync(request, userId, context),
            _ => LambdaResponse.Error(405, "ESCROW_VALIDATION", "Method not allowed.")
        };
    }
}
```

### 4.3 EscrowService — Core Business Logic

```csharp
public class EscrowService
{
    private readonly IEscrowRepository _escrowRepo;
    private readonly IEscrowAuditRepository _auditRepo;
    private readonly IEscrowIdempotencyRepository _idempotencyRepo;
    private readonly ITelebirrGateway _telebirrGateway;

    private const decimal DefaultCommissionRate = 0.025m; // 2.5%
    private const decimal MinCommission = 5.0m; // 5 ETB floor
    private const decimal MinAmount = 100.0m;
    private const decimal MaxAmount = 500_000.0m;
    private const decimal FaydaThreshold = 10_000.0m;
    private const int EscrowExpiryDays = 7;
    private const int DisputeWindowHours = 48;

    public async Task<EscrowResult> InitiateAsync(
        InitiateEscrowRequest request, string authenticatedUserId)
    {
        // 1. Validate buyerId matches JWT sub
        if (request.BuyerId != authenticatedUserId)
            return EscrowResult.Forbidden("ESCROW_006");

        // 2. Check idempotency
        var cached = await _idempotencyRepo.GetAsync(request.IdempotencyKey);
        if (cached != null)
            return EscrowResult.Duplicate(cached);

        // 3. Validate amount (100-500,000 ETB)
        if (request.Amount < MinAmount || request.Amount > MaxAmount)
            return EscrowResult.BadRequest("ESCROW_001");

        // 4. Fayda ID check for high-value transactions
        if (request.Amount > FaydaThreshold)
        {
            // Check buyer and farmer Fayda verification status
            // (lookup from Cognito user attributes)
        }

        // 5. Calculate commission
        var commission = Math.Max(request.Amount * DefaultCommissionRate, MinCommission);
        var farmerPayout = request.Amount - commission;

        // 6. Create escrow record
        var escrowId = Guid.NewGuid().ToString();
        var now = DateTime.UtcNow;
        var escrow = new EscrowRecord
        {
            EscrowId = escrowId,
            DeliveryId = request.DeliveryId,
            BuyerId = request.BuyerId,
            FarmerId = request.FarmerId,
            Amount = request.Amount,
            Commission = commission,
            CommissionRate = DefaultCommissionRate,
            FarmerPayout = farmerPayout,
            Currency = "ETB",
            Status = "initiated",
            CreatedAt = now,
            ExpiresAt = now.AddDays(EscrowExpiryDays),
            Version = 1
        };

        // 7. Store idempotency record (BEFORE calling Telebirr)
        await _idempotencyRepo.StoreAsync(request.IdempotencyKey, escrowId, "processing");

        // 8. Save escrow to DynamoDB
        await _escrowRepo.CreateAsync(escrow);

        // 9. Audit log
        await _auditRepo.AppendAsync(new EscrowAuditEntry
        {
            EscrowId = escrowId,
            Action = "initiate",
            FromState = "",
            ToState = "initiated",
            Actor = authenticatedUserId,
            Timestamp = now
        });

        // 10. Call Telebirr HoldFunds (async — may fail)
        // For MVP with MockTelebirrGateway, this succeeds immediately
        var holdResult = await _telebirrGateway.HoldFunds(new HoldFundsRequest
        {
            TransactionRef = request.IdempotencyKey,
            BuyerPhone = request.BuyerPhone,
            Amount = request.Amount,
            Description = $"WeredaConnect escrow for delivery {request.DeliveryId}"
        });

        if (holdResult.Success)
        {
            // Transition: initiated → escrowed
            await _escrowRepo.UpdateStatusAsync(escrowId, "escrowed",
                holdResult.TelebirrRef, now);
            await _auditRepo.AppendAsync(new EscrowAuditEntry
            {
                EscrowId = escrowId,
                Action = "hold_success",
                FromState = "initiated",
                ToState = "escrowed",
                Actor = "system",
                TelebirrRef = holdResult.TelebirrRef,
                Timestamp = now
            });
            await _idempotencyRepo.UpdateAsync(request.IdempotencyKey, "completed");
        }

        return EscrowResult.Created(escrow);
    }
}
```

### 4.4 ITelebirrGateway Interface

```csharp
public interface ITelebirrGateway
{
    Task<HoldFundsResponse> HoldFunds(HoldFundsRequest request);
    Task<ReleaseFundsResponse> ReleaseFunds(ReleaseFundsRequest request);
    Task<RefundFundsResponse> RefundFunds(RefundFundsRequest request);
    Task<TransactionStatusResponse> GetTransactionStatus(string transactionRef);
}
```

### 4.5 MockTelebirrGateway (Development)

```csharp
public class MockTelebirrGateway : ITelebirrGateway
{
    public async Task<HoldFundsResponse> HoldFunds(HoldFundsRequest request)
    {
        await Task.Delay(500); // Simulate API latency
        return new HoldFundsResponse
        {
            Success = true,
            TelebirrRef = $"MOCK-HOLD-{request.TransactionRef}-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}"
        };
    }
    // Similar for ReleaseFunds, RefundFunds, GetTransactionStatus
}
```

### 4.6 EscrowDtos (AOT-Safe)

```csharp
// Register ALL in AppJsonContext:
[JsonSerializable(typeof(InitiateEscrowRequest))]
[JsonSerializable(typeof(InitiateEscrowResponse))]
[JsonSerializable(typeof(ReleaseEscrowRequest))]
[JsonSerializable(typeof(ReleaseEscrowResponse))]
[JsonSerializable(typeof(DisputeEscrowRequest))]
[JsonSerializable(typeof(DisputeEscrowResponse))]
[JsonSerializable(typeof(RefundEscrowRequest))]
[JsonSerializable(typeof(RefundEscrowResponse))]
[JsonSerializable(typeof(EscrowDto))]
[JsonSerializable(typeof(DisputeDto))]
[JsonSerializable(typeof(GetEscrowHistoryResponse))]
[JsonSerializable(typeof(EscrowErrorResponse))]
[JsonSerializable(typeof(EscrowError))]
```

### 4.7 AOT Compliance Review

| Check | Status |
|-------|--------|
| System.Text.Json source generators | ✅ All DTOs in AppJsonContext |
| No reflection | ✅ Manual marshalling |
| No Newtonsoft.Json | ✅ |
| No Entity Framework | ✅ DynamoDB direct |
| Explicit DI | ✅ Constructor injection |
| HMAC-SHA256 | ✅ System.Security.Cryptography (AOT-safe) |

---

## 5. DynamoDB Design

### 5.1 New Tables Required

#### EscrowsTable
```
PK: escrowId (String)
SK: createdAt (String, ISO 8601)

GSI: DeliveryIndex
  PK: deliveryId
  SK: createdAt
  Use case: Look up escrow by delivery (1:1)

GSI: BuyerStatusIndex
  PK: buyerId
  SK: createdAt
  Use case: Buyer's payment history

GSI: FarmerStatusIndex
  PK: farmerId
  SK: createdAt
  Use case: Farmer's payment history

GSI: StatusExpiresIndex
  PK: status
  SK: expiresAt
  Use case: Auto-refund cron (find expiring escrowed items)
```

#### EscrowAuditTable
```
PK: escrowId (String)
SK: timestamp (String, ISO 8601)

No GSIs needed. Always queried by escrowId, sorted by time.
Append-only. No updates or deletes allowed.
```

#### EscrowIdempotencyTable
```
PK: idempotencyKey (String)

TTL: expiresAt (30 days from creation)
No GSIs needed.
```

### 5.2 Existing Tables

The `database-stack.ts` already has a `PaymentsTable`:
```
PK: paymentId | SK: deliveryId
GSI: FarmerIdIndex (PK: farmerId, SK: status)
GSI: StatusIndex (PK: status, SK: createdAt)
```

**Decision**: The existing PaymentsTable serves as the local mobile cache. The server-side EscrowsTable is the authoritative source. The `paymentId` field in the mobile Payment schema maps to `escrowId`.

### 5.3 Infrastructure Changes Required

Add to `database-stack.ts`:
```typescript
// EscrowsTable
const escrowsTable = new WeredaDynamoTable(this, 'EscrowsTable', {
    tableName: `${prefix}-escrows`,
    partitionKey: { name: 'escrowId', type: AttributeType.STRING },
    sortKey: { name: 'createdAt', type: AttributeType.STRING },
    isProd: props.isProd,
});
escrowsTable.table.addGlobalSecondaryIndex({ /* DeliveryIndex */ });
escrowsTable.table.addGlobalSecondaryIndex({ /* BuyerStatusIndex */ });
escrowsTable.table.addGlobalSecondaryIndex({ /* FarmerStatusIndex */ });
escrowsTable.table.addGlobalSecondaryIndex({ /* StatusExpiresIndex */ });

// EscrowAuditTable
const escrowAuditTable = new WeredaDynamoTable(this, 'EscrowAuditTable', {
    tableName: `${prefix}-escrow-audit`,
    partitionKey: { name: 'escrowId', type: AttributeType.STRING },
    sortKey: { name: 'timestamp', type: AttributeType.STRING },
    isProd: props.isProd,
});

// EscrowIdempotencyTable
const escrowIdempotencyTable = new WeredaDynamoTable(this, 'EscrowIdempotencyTable', {
    tableName: `${prefix}-escrow-idempotency`,
    partitionKey: { name: 'idempotencyKey', type: AttributeType.STRING },
    isProd: props.isProd,
});
// Enable TTL on expiresAt field
```

Add to `api-stack.ts`:
```typescript
// EscrowFunction Lambda
const escrowFn = new WeredaLambda(this, 'EscrowFunction', {
    functionName: `${prefix}-escrow`,
    description: 'Escrow payment management',
    environment: {
        ESCROWS_TABLE: escrowsTable.tableName,
        ESCROW_AUDIT_TABLE: escrowAuditTable.tableName,
        ESCROW_IDEMPOTENCY_TABLE: escrowIdempotencyTable.tableName,
    },
    timeout: Duration.seconds(30), // Telebirr calls may be slow
});

// Routes
POST /escrow/initiate → escrowFn (JWT required)
POST /escrow/release → escrowFn (JWT required, system/admin only)
POST /escrow/dispute → escrowFn (JWT required)
POST /escrow/refund → escrowFn (JWT required, system/admin only)
GET /escrow/{id} → escrowFn (JWT required)
GET /escrow/history → escrowFn (JWT required)

// Scheduled Lambdas (EventBridge rules)
AutoReleaseFunction — rate(1 hour)
AutoRefundFunction — rate(1 hour)
ReconciliationFunction — rate(6 hours)
```

---

## 6. Mobile Implementation

### 6.1 Key Distinction: Online-First for Mutations

Unlike Trip and Delivery features (which use the sync queue), escrow mutations require connectivity:

```dart
class EscrowRepository {
  final Isar _isar;
  final DioClient _dio;

  /// Initiate payment — requires connectivity.
  /// Queues intent locally if offline, executes when online.
  Future<EscrowResult> initiatePayment({
    required String deliveryId,
    required String buyerId,
    required String farmerId,
    required double amount,
  }) async {
    final idempotencyKey =
        '$buyerId:$deliveryId:initiate:${DateTime.now().millisecondsSinceEpoch}';

    // Check connectivity
    if (await _isOffline()) {
      // Save intent to Isar, process when online
      final intent = PaymentIntent()
        ..deliveryId = deliveryId
        ..buyerId = buyerId
        ..farmerId = farmerId
        ..amount = amount
        ..idempotencyKey = idempotencyKey
        ..status = IntentStatus.pendingSync
        ..createdAt = DateTime.now().toUtc();

      await _isar.writeTxn(() => _isar.paymentIntents.put(intent));
      return EscrowResult.queued(intent);
    }

    // Online: call server directly
    final response = await _dio.post('/escrow/initiate', data: {
      'deliveryId': deliveryId,
      'buyerId': buyerId,
      'farmerId': farmerId,
      'amount': amount,
      'currency': 'ETB',
      'idempotencyKey': idempotencyKey,
    });

    // Cache server response in local Payment entity
    final payment = Payment()
      ..paymentId = response.data['escrowId']
      ..deliveryId = deliveryId
      ..farmerId = farmerId
      ..buyerId = buyerId
      ..amount = amount
      ..status = PaymentStatus.values.byName(response.data['status'])
      ..telebirrRef = response.data['telebirrRef']
      ..createdAt = DateTime.now().toUtc()
      ..version = 1;

    await _isar.writeTxn(() => _isar.payments.put(payment));
    return EscrowResult.success(payment);
  }
}
```

### 6.2 New Files

| File | Description |
|------|-------------|
| `mobile/lib/features/escrow_payment/data/repositories/escrow_repository.dart` | Online-first, with offline intent queue |
| `mobile/lib/features/escrow_payment/data/models/payment_intent.dart` | Isar schema for offline payment intents |
| `mobile/lib/features/escrow_payment/presentation/bloc/escrow_bloc.dart` | Events/States |
| `mobile/lib/features/escrow_payment/presentation/screens/payment_screen.dart` | Initiate payment |
| `mobile/lib/features/escrow_payment/presentation/screens/escrow_status_screen.dart` | View escrow details |
| `mobile/lib/features/escrow_payment/presentation/screens/payment_history_screen.dart` | Transaction history |
| `mobile/lib/features/escrow_payment/presentation/screens/dispute_screen.dart` | Raise dispute |
| `mobile/lib/features/escrow_payment/presentation/widgets/escrow_status_badge.dart` | Color-coded status |
| `mobile/lib/features/escrow_payment/presentation/widgets/amount_display.dart` | ETB formatting |

### 6.3 Payment Isar Schema — Already Scaffolded

The schema at `mobile/lib/core/database/schemas/payment.dart` already has:
- paymentId, deliveryId, farmerId, buyerId, amount
- telebirrRef, status, createdAt, version

Needs additions for escrow-specific fields:
```dart
// Fields to add:
late double commission;
late double farmerPayout;
late String? disputeReason;
late DateTime? expiresAt;
late DateTime? disputeWindowEndsAt;
late DateTime? releasedAt;
late DateTime? refundedAt;
```

### 6.4 PaymentIntent Isar Schema (New)

```dart
@collection
class PaymentIntent {
  Id id = Isar.autoIncrement;
  @Index(unique: true) late String idempotencyKey;
  late String deliveryId;
  late String buyerId;
  late String farmerId;
  late double amount;
  @Enumerated(EnumType.name) late IntentStatus status;
  late DateTime createdAt;
}

enum IntentStatus { pendingSync, syncing, completed, failed, cancelled }
```

### 6.5 Offline Intent Processing

```dart
/// Process queued payment intents when connectivity returns.
/// Called by ConnectivityMonitor on state change.
Future<void> processQueuedIntents() async {
  final intents = await _isar.paymentIntents
      .filter()
      .statusEqualTo(IntentStatus.pendingSync)
      .findAll();

  for (final intent in intents) {
    // Mark as syncing
    await _isar.writeTxn(() {
      intent.status = IntentStatus.syncing;
      return _isar.paymentIntents.put(intent);
    });

    try {
      final response = await _dio.post('/escrow/initiate', data: {
        'deliveryId': intent.deliveryId,
        'buyerId': intent.buyerId,
        'farmerId': intent.farmerId,
        'amount': intent.amount,
        'currency': 'ETB',
        'idempotencyKey': intent.idempotencyKey,
      });

      // Success — cache payment, mark intent as completed
      await _cachePayment(response.data);
      await _isar.writeTxn(() {
        intent.status = IntentStatus.completed;
        return _isar.paymentIntents.put(intent);
      });
    } catch (e) {
      // Failure — mark as pending for retry
      await _isar.writeTxn(() {
        intent.status = IntentStatus.pendingSync;
        return _isar.paymentIntents.put(intent);
      });
    }
  }
}
```

### 6.6 Low-Literacy UI Patterns

| Element | Implementation |
|---------|---------------|
| Status colors | Green=paid/released, Yellow=pending/escrowed, Red=disputed/refunded |
| Amount display | Large font, "ETB 15,000" (formatted with thousands separator) |
| Status icons | Shield=escrowed, Checkmark=released, Exclamation=disputed |
| Voice confirm | "Are you sure you want to pay 15,000 Birr?" (TTS) |
| Minimal text | Icon-first design, text as secondary |

---

## 7. Automated Processes

### 7.1 Auto-Release Lambda

```csharp
public class AutoReleaseFunction
{
    // Triggered by EventBridge rule: rate(1 hour)

    public async Task FunctionHandler(ScheduledEvent ev, ILambdaContext context)
    {
        // 1. Query StatusExpiresIndex: status=escrowed
        // 2. For each, check:
        //    - deliveryConfirmedAt is set (delivery was confirmed)
        //    - now > disputeWindowEndsAt (48h dispute window passed)
        // 3. If both true: call EscrowService.ReleaseAsync()
        // 4. Audit log: actor=system, trigger=delivery_confirmed
    }
}
```

### 7.2 Auto-Refund Lambda

```csharp
public class AutoRefundFunction
{
    // Triggered by EventBridge rule: rate(1 hour)

    public async Task FunctionHandler(ScheduledEvent ev, ILambdaContext context)
    {
        // 1. Query StatusExpiresIndex: status=escrowed, expiresAt < now
        // 2. For each expired escrow: call EscrowService.RefundAsync()
        // 3. Audit log: actor=system, trigger=delivery_timeout
        // 4. Notify buyer and farmer (future: push notification)
    }
}
```

### 7.3 Reconciliation Lambda

```csharp
public class ReconciliationFunction
{
    // Triggered by EventBridge rule: rate(6 hours)

    public async Task FunctionHandler(ScheduledEvent ev, ILambdaContext context)
    {
        // 1. Query escrows: status=initiated, createdAt < now - 30min
        // 2. For each, check Telebirr: GetTransactionStatus()
        // 3. If Telebirr confirms hold → update to escrowed
        // 4. If Telebirr has no record → mark as cancelled (hold_failed)
        // 5. Audit log all reconciliation actions
    }
}
```

---

## 8. Implementation Sequence

```
Phase 1: Infrastructure
  1. Add EscrowsTable, EscrowAuditTable, EscrowIdempotencyTable to database-stack
  2. Add EscrowFunction Lambda to api-stack
  3. Add /escrow/* routes to API Gateway
  4. Add EventBridge rules for cron Lambdas
  5. Deploy infrastructure

Phase 2: Server — Core Escrow Logic
  1. Create EscrowDtos (AOT-safe sealed records)
  2. Register in AppJsonContext
  3. Create ITelebirrGateway + MockTelebirrGateway
  4. Create IEscrowRepository + EscrowRepository
  5. Create IEscrowAuditRepository + EscrowAuditRepository
  6. Create IEscrowIdempotencyRepository + EscrowIdempotencyRepository
  7. Create EscrowService (state machine, commission, Fayda check)
  8. Create EscrowFunction (route to handlers)

Phase 3: Server — Automated Processes
  1. Create AutoReleaseFunction
  2. Create AutoRefundFunction
  3. Create ReconciliationFunction

Phase 4: Mobile — Escrow Feature
  1. Add escrow-specific fields to Payment schema
  2. Create PaymentIntent Isar schema
  3. Run build_runner
  4. Create EscrowRepository (online-first + offline intent)
  5. Create EscrowBloc
  6. Create payment screens (initiate, status, history, dispute)
  7. Connect intent processing to ConnectivityMonitor
  8. Add l10n strings (3 languages)

Phase 5: Integration Testing
  1. Happy path: initiate → escrowed → delivered → released → completed
  2. Dispute path: escrowed → delivered → disputed → (farmer/buyer wins)
  3. Auto-refund: escrowed → 7 days → refunded
  4. Auto-release: escrowed → delivered + 48h → released
  5. Idempotency: duplicate initiation returns cached result
  6. Offline intent: queue payment → go online → auto-send
  7. Fayda check: amount > 10,000 ETB → verify both parties
```

---

## 9. Security Checklist

| Concern | Mitigation |
|---------|-----------|
| Double-charge | Idempotency key checked before Telebirr call |
| Unauthorized release | Only system/admin can release; JWT + role check |
| Self-trading fraud | buyerId != farmerId enforced server-side |
| Amount manipulation | Server validates 100-500,000 ETB range |
| Fayda ID bypass | Server checks Cognito custom:faydaId for >10,000 ETB |
| Stale escrows | Auto-refund after 7 days; reconciliation every 6h |
| Audit tampering | EscrowAuditTable is append-only (no UpdateItem/DeleteItem) |
| PII in logs | Redact phone numbers, Fayda IDs in CloudWatch logs |
| Telebirr credentials | AWS Secrets Manager (not env vars in production) |

---

## 10. Commission Calculation

```
commission = max(amount * 0.025, 5.00)    // 2.5% with 5 ETB floor
farmerPayout = amount - commission

Examples:
  ETB 100   → commission: ETB 5.00  (floor), farmer gets ETB 95.00
  ETB 500   → commission: ETB 12.50,        farmer gets ETB 487.50
  ETB 15000 → commission: ETB 375.00,       farmer gets ETB 14,625.00
  ETB 500000→ commission: ETB 12,500.00,    farmer gets ETB 487,500.00
```

---

## 11. Risks and Mitigations

| Risk | Impact | Mitigation |
|------|--------|-----------|
| Telebirr API unavailable | Payments cannot be held | MockTelebirrGateway for dev; retry with backoff for prod; clear "payment queued" UI |
| Reconciliation misses stuck escrow | Buyer charged but escrow not recorded | Reconciliation runs every 6h + idempotency prevents double-charge |
| Dispute abuse (frivolous disputes) | Farmers lose trust | Admin resolution; future: reputation scoring |
| 3G network drops during payment | Partial state | Idempotency key stored BEFORE Telebirr call; retry returns cached result |
| StatusExpiresIndex hot partition | Many escrows in "escrowed" status | Acceptable for MVP scale; partition splitting if needed later |

---

**Last Updated**: 2026-02-17
**Author**: Architect Agent
