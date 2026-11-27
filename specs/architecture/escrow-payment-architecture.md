# Telebirr Escrow Payment System Architecture

## Version
1.1.0 | 2026-11-06 (commission rule, SIM-30)

## Status
Implemented (R4.3 — 100%, mock Telebirr gateway)

---

## 1. Component Overview

```
┌──────────────────────────────────────────────────────────────┐
│                       MOBILE (Flutter)                        │
│                                                               │
│  ┌──────────────────┐  ┌────────────────┐  ┌──────────────┐ │
│  │ EscrowBloc        │  │ Escrow         │  │   Isar DB    │ │
│  │                   │──│ Repository     │──│  (Payment +  │ │
│  │ Events/States     │  │ (online-first  │  │  Intent)     │ │
│  │                   │  │  for mutations)│  │              │ │
│  └──────────────────┘  └───────┬────────┘  └──────────────┘ │
│                                │                              │
│                         ┌──────┴────────┐                     │
│                         │ Escrow Intent │                     │
│                         │ Queue (local  │                     │
│                         │  NOT /sync)   │                     │
│                         └──────┬────────┘                     │
└────────────────────────────────┼─────────────────────────────┘
                                 │
                POST /escrow/initiate    GET /escrow/{id}
                POST /escrow/dispute     GET /escrow/history
                (Dedicated endpoints, NOT /sync)
                                 ▼
┌──────────────────────────────────────────────────────────────┐
│                   SERVER (.NET 10 AOT)                         │
│                                                               │
│  ┌──────────────────────────────────────────────────┐        │
│  │                 EscrowFunction                     │        │
│  │  Routes: initiate, release, dispute, refund,      │        │
│  │          history, getById                          │        │
│  └──────────────────────┬───────────────────────────┘        │
│                          │                                    │
│  ┌──────────────────────┴───────────────────────────┐        │
│  │                 EscrowService                      │        │
│  │  State machine + commission + Fayda + idempotency  │        │
│  └──┬──────────────────┬──────────────────┬─────────┘        │
│     │                  │                  │                    │
│     ▼                  ▼                  ▼                    │
│  IEscrowRepo    ITelebirrGateway    IIdempotencyRepo         │
│  (DynamoDB)     (Mock for MVP)      (DynamoDB, 30d TTL)      │
│                                                               │
│  ┌──────────────────────────────────────────────────┐        │
│  │              Scheduled Lambdas (Cron)              │        │
│  │  AutoRelease  — hourly — release after 48h window  │        │
│  │  AutoRefund   — hourly — refund after 7-day timeout│        │
│  │  Reconcile    — 6h — check stuck 'initiated'       │        │
│  └──────────────────────────────────────────────────┘        │
└──────────────────────────────────────────────────────────────┘
```

---

## 2. Critical Decision: NOT Synced via /sync

Escrow uses **dedicated endpoints** instead of the sync queue because:

| Factor | Sync Queue | Dedicated /escrow/* (Chosen) |
|--------|-----------|----------------------------|
| Money movement | Cannot hold funds offline | Server coordinates with Telebirr |
| Idempotency TTL | 7 days | 30 days (financial records) |
| Authorization | Entity-level (owner) | Role-based (buyer/farmer/admin/system) |
| Conflict resolution | Server-wins (overwrite) | State machine (reject invalid) |
| Audit trail | Optional | Mandatory (every state change) |

**Offline behavior**: Payment INTENT queued locally. When online, intent sent to POST /escrow/initiate. Actual fund movement always requires server + Telebirr.

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
            ▼                       ▼
      +-----------+          +-----------+
      | escrowed  |          | cancelled |  (TERMINAL)
      +-----+-----+          +-----------+
            |
  +---------+---------+----------+
  |                   |          |
[delivery +      [dispute    [7-day timeout,
 48h passed]      raised      no delivery]
  |               <48h]           |
  ▼                ▼              ▼
+-----------+ +-----------+ +-----------+
| released  | | disputed  | | refunded  |  (TERMINAL)
+-----+-----+ +-----+-----+ +-----------+
      |             |
      ▼        +----+----+
+-----------+  |         |
| completed | [farmer  [buyer
+-----------+  wins]    wins]
 (TERMINAL)    |         |
               ▼         ▼
         +-----------+ +-----------+
         | released  | | refunded  |  (TERMINAL)
         +-----+-----+ +-----------+
               |
               ▼
         +-----------+
         | completed |  (TERMINAL)
         +-----------+
```

---

## 4. Commission Calculation

The rule since order-funded escrows (SIM-30, `escrow-protocol.md` 1.39.0, Commission Calculation):
the commission is a percentage of the crop cost (SSM `/{prefix}/escrow/commission-rate`, default
0.05) that the buyer pays on top of the crop and transport cost. Every leg is rounded to the santim.

```
cropCost         = round2(quantity × pricePerUnit)
transportCost    = round2(quantity / 100 × pricePerQuintal)
commissionAmount = round2(cropCost × 0.05)
orderTotal       = cropCost + transportCost + commissionAmount   // escrow amount

farmerPayout = cropCost, truckerPayout = transportCost,
commission   = amount − farmerPayout − truckerPayout

Example: 170 kg at 18.99 ETB/kg, 1.7 quintals at 255.55 ETB/quintal
  crop ETB 3,228.30 + transport ETB 434.44 + commission ETB 161.42 = ETB 3,824.16
```

The original R4.3 rule below survives only for a `POST /escrow/initiate` on a delivery without an
order (mock gateway only):

```
commission = max(amount × 0.025, 5.00)    // 2.5% with 5 ETB floor, deducted from the amount
farmerPayout = amount - commission

Examples:
  ETB 100    → commission: ETB 5.00 (floor),     farmer: ETB 95.00
  ETB 500    → commission: ETB 12.50,             farmer: ETB 487.50
  ETB 15,000 → commission: ETB 375.00,           farmer: ETB 14,625.00
```

---

## 5. Mobile Implementation

### Online-First for Mutations

Unlike Trip and Crop features, escrow mutations require connectivity:

```
initiatePayment()
  │
  ├── IF ONLINE: POST /escrow/initiate → cache response in Isar
  │
  └── IF OFFLINE: Save PaymentIntent to Isar
       └── EscrowSyncService processes when connectivity returns
```

### Screens

| Screen | Purpose |
|--------|---------|
| InitiatePaymentScreen | Buyer starts escrow payment |
| EscrowStatusScreen | View escrow details + timeline |
| PaymentHistoryScreen | Transaction history |
| DisputeScreen | Raise dispute with reason |

### Widgets

| Widget | Purpose |
|--------|---------|
| StatusBadge | Color-coded escrow status |
| EscrowTimeline | State visualization (stepper) |
| PendingIntentCard | Queued offline payment indicator |
| AmountDisplay | Large ETB formatting with thousands separator |

### Low-Literacy UI Patterns

| Element | Implementation |
|---------|---------------|
| Status colors | Green=paid/released, Yellow=pending, Red=disputed |
| Amount display | Large font, "ETB 15,000" with thousands separator |
| Status icons | Shield=escrowed, Checkmark=released, Exclamation=disputed |
| Minimal text | Icon-first design, text secondary |

---

## 6. Server Implementation

### EscrowService — Core Business Logic

```
InitiateAsync():
  1. Validate buyerId matches JWT sub
  2. Check idempotency (30-day TTL)
  3. Validate amount (100-500,000 ETB)
  4. Fayda ID check for > 10,000 ETB (future)
  5. Take the split from the linked order (commission = 5% of crop cost); without an order, 2.5% with a 5 ETB floor
  6. Create escrow record
  7. Store idempotency BEFORE Telebirr call
  8. Save to DynamoDB
  9. Audit log
  10. Call Telebirr HoldFunds
  11. If success: transition initiated → escrowed
```

### Automated Processes

| Lambda | Schedule | Action |
|--------|----------|--------|
| AutoReleaseFunction | Hourly | Release escrowed + delivered + 48h passed |
| AutoRefundFunction | Hourly | Refund escrowed + 7-day expired |
| ReconciliationFunction | Every 6h | Check stuck 'initiated' against Telebirr |

### Mock Telebirr Gateway
Development uses `MockTelebirrGateway` which simulates 500ms latency and returns success. Replace with real gateway for production.

---

## 7. DynamoDB Design

### EscrowsTable
```
PK: escrowId | SK: createdAt
GSI: DeliveryIndex (PK: deliveryId, SK: createdAt)
GSI: BuyerStatusIndex (PK: buyerId, SK: createdAt)
GSI: FarmerStatusIndex (PK: farmerId, SK: createdAt)
GSI: StatusExpiresIndex (PK: status, SK: expiresAt)
```

### EscrowAuditTable (Append-only)
```
PK: escrowId | SK: timestamp
No updates/deletes. Every state change logged.
```

### EscrowIdempotencyTable
```
PK: idempotencyKey | TTL: 30 days
```

---

## 8. Security

| Concern | Mitigation |
|---------|-----------|
| Double-charge | Idempotency key checked before Telebirr call |
| Unauthorized release | Only system/admin can release; JWT + role check |
| Self-trading fraud | buyerId != farmerId enforced server-side |
| Amount manipulation | Server validates 100-500,000 ETB range |
| Fayda ID bypass | Server checks custom:faydaId for > 10,000 ETB |
| Stale escrows | Auto-refund after 7 days + reconciliation every 6h |
| Audit tampering | Append-only audit table (no UpdateItem/DeleteItem) |
| PII in logs | Redact phone numbers, Fayda IDs in CloudWatch |

---

## 9. File Structure

```
mobile/lib/features/escrow_payment/
├── data/
│   ├── models/
│   │   └── payment_intent.dart
│   ├── repositories/
│   │   └── escrow_repository.dart
│   └── services/
│       └── escrow_sync_service.dart
└── presentation/
    ├── bloc/
    │   ├── escrow_bloc.dart
    │   ├── escrow_event.dart
    │   └── escrow_state.dart
    ├── screens/
    │   ├── initiate_payment_screen.dart
    │   ├── escrow_status_screen.dart
    │   ├── payment_history_screen.dart
    │   └── dispute_screen.dart
    └── widgets/
        ├── escrow_status_badge.dart
        ├── escrow_timeline.dart
        ├── pending_intent_card.dart
        └── amount_display.dart
```

---

**Cross-references**:
- Detailed design: `specs/decisions/r4-3-escrow-architecture.md`
- API contract: `specs/contracts/escrow-protocol.md`

**Last Updated**: 2026-11-06
