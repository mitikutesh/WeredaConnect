# Escrow Protocol Contract (Telebirr Integration)

## Version
1.12.0

## Parties
- **Mobile** (client) -- initiates payment intents, displays escrow status, submits disputes
- **Backend** (server) -- manages escrow state machine, integrates with Telebirr, enforces idempotency and audit trail
- **Telebirr Gateway** (external) -- holds/releases/refunds funds via Ethio Telecom's mobile money API

---

## Overview

The Escrow Protocol enables secure payment between buyers and farmers on WeredaConnect using Telebirr, Ethiopia's dominant mobile money platform. Funds are held in a platform-managed Telebirr escrow account until delivery is verified (F-12), at which point the platform releases payment to the farmer minus a configurable commission.

**This is the highest-priority feature in the backlog (F-13, score 19)** because it directly enables revenue, prevents payment fraud, and satisfies Ethiopian Fayda ID compliance requirements.

**Key Design Decisions**:
- **Server-authoritative**: Escrow operations are NOT synced via POST /sync. All payment mutations go through dedicated `/escrow/*` endpoints because money movement requires server coordination.
- **Idempotent operations**: Every escrow action uses an idempotency key to prevent double-charges on flaky 3G networks.
- **Platform-held escrow**: Funds are held in WeredaConnect's Telebirr business account (simplest model for MVP).
- **Offline intent, online execution**: Payment intent can be queued offline; actual fund movement requires connectivity.
- **Telebirr abstraction**: Integration is behind an `ITelebirrGateway` interface to allow mock implementations during development.

---

## Escrow Lifecycle (State Machine)

```
                    +-----------+
                    | initiated |
                    +-----+-----+
                          |
              +-----------+-----------+
              |                       |
        [funds held]          [buyer cancels]
              |                       |
              v                       v
        +-----------+          +-----------+
        | escrowed  |          | cancelled |
        +-----+-----+          +-----------+
              |
    +---------+---------+
    |                   |
[delivery         [dispute raised
 confirmed]        within 48h]
    |                   |
    v                   v
+-----------+     +-----------+
| released  |     | disputed  |
+-----+-----+     +-----+-----+
      |                 |
      v           +-----+-----+
+-----------+     |           |
| completed |  [farmer     [buyer
+-----------+   wins]       wins]
                  |           |
                  v           v
            +-----------+ +-----------+
            | released  | | refunded  |
            +-----+-----+ +-----------+
                  |
                  v
            +-----------+
            | completed |
            +-----------+

Also:
  escrowed --[7-day timeout, no delivery]--> refunded
  escrowed --[no dispute within 48h of delivery]--> released (auto-release)
```

### State Definitions

| State | Description | Terminal |
|-------|-------------|----------|
| `initiated` | Buyer submitted payment intent; Telebirr HoldFunds call pending or in progress | No |
| `escrowed` | Funds successfully held in platform Telebirr account | No |
| `released` | Funds released to farmer's Telebirr (minus commission). With `truckerPayoutPending` the trucker leg is still owed and the escrow stays here until it is paid (1.11.0) | No |
| `completed` | Every leg is paid (farmer and, when there is one, trucker); transaction fully settled | Yes |
| `cancelled` | Buyer cancelled before funds were held | Yes |
| `disputed` | Buyer or farmer raised a dispute within 48 hours of delivery | No |
| `refunded` | Funds returned to buyer's Telebirr account | Yes |
| `dispute_resolved` | Admin verdict recorded (`disputeResolution`); the hourly sweep settles it (1.3.0) | No |
| `held` | Frozen for fraud review by a settlement sweep; only an admin release or refund leaves it (1.7.0, planned FRD-03) | No |

An escrow waiting in the manual payout queue (1.8.0) keeps the status it had when it was
queued (`escrowed`, `disputed` or `dispute_resolved`); the queue is a flag on the row, not a
state.

### Valid State Transitions

| From | To | Trigger | Actor |
|------|----|---------|-------|
| `initiated` | `escrowed` | Telebirr HoldFunds succeeds | System |
| `initiated` | `cancelled` | Buyer cancels before escrow completes | Buyer |
| `escrowed` | `released` | Delivery confirmed (F-12) AND 48-hour dispute window passed OR no dispute | System (auto-release) |
| `escrowed` | `released` | Delivery confirmed AND dispute resolved in farmer's favor | Admin/System |
| `escrowed` | `disputed` | Buyer or farmer raises dispute within 48 hours of delivery confirmation | Buyer or Farmer |
| `escrowed` | `refunded` | Delivery not completed within 7 days of escrow | System (auto-refund) |
| `released` | `completed` | Every leg paid: at release, by the hourly trucker retry, or by the manual payout close (1.11.0; never while `truckerPayoutPending`) | System/Admin |
| `completed` | `released` | One-off `park_legacy_trucker_legs` migration only: a pre-1.11.0 escrow completed with its trucker leg unpaid (1.11.0) | System |
| *(created as)* `escrowed` | — | Order funding verified (order-funded escrow, below) | System |
| `disputed` | `dispute_resolved` | Admin verdict (`POST /admin/disputes/{id}/resolve`, 1.3.0) | Admin |
| `dispute_resolved` | `released` | `farmer_wins`, settled by the hourly sweep (1.3.0) or an admin release (1.9.0) | System/Admin |
| `dispute_resolved` | `refunded` | `buyer_wins`, settled by the hourly sweep (1.3.0) or an admin refund (1.9.0) | System/Admin |
| `escrowed` | `held` | Settlement sweep finds a party suspended or at risk tier CRITICAL (1.7.0, planned FRD-03) | System |
| `dispute_resolved` | `held` | Same trigger at dispute settlement (1.7.0, planned FRD-03) | System |
| `held` | `released` | Admin decision (1.7.0, planned FRD-03) | Admin |
| `held` | `refunded` | Admin decision (1.7.0, planned FRD-03) | Admin |

Invalid transitions return `422 Unprocessable Entity` with error code `ESCROW_INVALID_TRANSITION`.
Since 1.9.0 a `disputed` escrow is never released or refunded directly: the admin records a
verdict first, and the verdict decides the direction (see Additions in 1.9.0).

**Clients treat `status` as an open set** (1.7.0). A client must not fail on a status value it does
not know: it shows a neutral state with no actions and keeps the raw value. Shipped mobile clients
throw on an unknown value and the web client maps it to `initiated`; FRD-06 fixes both before `held`
is enabled (see Additions in 1.7.0).

---

## Order-Funded Escrow (v1.1.0)

The order-payment leg (order `payment_submitted` → `funded`, see
`order-lifecycle.md`) verifies and holds the buyer's money BEFORE any delivery
exists. The server materializes an escrow record at that moment so
`GET /escrow/history`, refunds, and release all flow through this protocol:

- **Creation**: applied by the funding path (`EscrowService.CreateFromFundedOrderAsync`
  through `OrderFundingService`, called from the sync Lambda's mock auto-fund and, since
  1.5.0, from the Telebirr webhook when a live order checkout is paid; see §Additions in
  1.5.0). The record is born `escrowed`: the hold already happened on the
  payment leg, so no Telebirr HoldFunds call is made. The hold reference is the order's
  server-only `telebirrPaymentOrderId` when present, else `cbeTransactionId`, else
  `ORDER-FUND-{orderId}`.
- **Identity & idempotency**: `escrowId = "escrow-order-{orderId}"` and
  `createdAt = order.paymentVerifiedAt` — a deterministic (PK, SK) pair, so retries
  and races collapse onto one conditional put.
- **Fields**: `source: "order"`, `orderId`, NO `deliveryId` (the sparse
  DeliveryIndex gains the record only at linkage), `telebirrRef`/`telebirrHoldRef` =
  the order's payment reference (`cbeTransactionId`, else `ORDER-FUND-{orderId}`),
  `amount = orderTotal`, `farmerPayout = cropCost`, `truckerPayout = transportCost`,
  `commission = amount − farmerPayout − truckerPayout` (exact remainder — the split
  always reconciles), `expiresAt = createdAt + 7 days`.
- **Delivery linkage**: creating a delivery REQUIRES `orderId`
  (see `delivery-verification-api.md`); after the delivery write the server
  backfills `escrow.deliveryId`, which activates every by-delivery mechanism
  (confirmation stamping, auto-release, the self-pickup fail-closed QR gate,
  `GET /escrow/by-delivery`). Linkage re-points only when the previously linked
  delivery is `cancelled`; it refuses terminal orders; and it SELF-HEALS a missing
  record while the order is still `funded`/`accepted`.
- **Refund on reject/cancel**: `funded → rejected|cancelled` stamps the escrow
  `expiresAt = now` (+`refundReason`); the hourly auto-refund cron then pays the
  buyer through the standard gateway path (≤1h latency, mock and live alike).
- **Order-aware auto-refund**: for `source == "order"` escrows the 7-day sweep
  loads the order first. It refunds only a stale-`funded` (farmer never responded)
  or terminal (`rejected`/`cancelled`) order; a progressing order
  (`accepted` … `disbursing`) gets `expiresAt` pushed +48h instead. Fail-safe: if
  the orders table isn't wired, order-sourced escrows are skipped, never refunded
  blind. A stale-funded order is cancelled FIRST via version-CAS
  (`cancelReason: escrow_refunded`) — the CAS fences out a concurrent farmer
  accept (on conflict the refund is skipped and the next sweep re-classifies) —
  and only then is the money refunded, so money and order state agree.
- **Double-charge guard**: `POST /escrow/initiate` returns `409 ESCROW_024`
  ("Funds for this order are already held in escrow") when the delivery's order
  already carries an active escrow. The order-funded record is recognised by
  `source=order` (or its deterministic `escrow-order-{orderId}` id) and the
  guard is decided before the per-delivery duplicate check, so it also wins
  once linkage has stamped the delivery on that record (SIM-24). `ESCROW_009`
  is reserved for a second delivery-initiated escrow on the same delivery.
- **Documented divergences**: order-funded records carry
  `buyerFaydaVerified/farmerFaydaVerified = false` unless a users table is wired
  (KYC enforcement belongs on the order-payment leg — follow-up); amounts outside
  the initiate API's 100–500,000 ETB band are still recorded (they reflect money
  actually held). Listing-stock restore on refund is implemented (1.2.2, SIM-13): when
  the sweep cancels a stale-funded order, or a participant rejects/cancels a funded
  order, the listing gets its quintals back (idempotent per order).

---

## Endpoints

**Base path**: `/escrow`
**Authentication**: All endpoints require Cognito JWT (Bearer token).
**Content-Type**: `application/json`

### 1. POST /escrow/initiate

Buyer initiates escrow payment for a delivery. Queues a Telebirr HoldFunds call.

#### Request Body

```json
{
  "deliveryId": "delivery-001",
  "buyerId": "buyer-456",
  "farmerId": "farmer-123",
  "amount": 15000.00,
  "currency": "ETB",
  "idempotencyKey": "buyer-456:delivery-001:initiate:1708084800000"
}
```

| Field | Type | Required | Constraints | Description |
|-------|------|----------|-------------|-------------|
| `deliveryId` | string (UUID) | Yes | Must reference an existing delivery | The delivery this payment covers |
| `buyerId` | string (UUID) | Yes | Must match JWT `sub` claim | Buyer initiating payment |
| `farmerId` | string (UUID) | Yes | Must be a verified user AND the delivery's `farmerId` | Farmer receiving payment |
| `amount` | number | Yes | 100.00 - 500,000.00 ETB; must equal the linked order's `orderTotal` | Total payment amount in ETB |
| `currency` | string | Yes | Fixed: `"ETB"` | Ethiopian Birr |
| `idempotencyKey` | string | Yes | Format: `{buyerId}:{deliveryId}:{action}:{timestamp}` | Prevents duplicate charges |

#### Success Response

**Status**: `201 Created`

```json
{
  "escrowId": "escrow-789",
  "deliveryId": "delivery-001",
  "buyerId": "buyer-456",
  "farmerId": "farmer-123",
  "amount": 15000.00,
  "commission": 375.00,
  "farmerPayout": 14625.00,
  "currency": "ETB",
  "status": "initiated",
  "telebirrRef": null,
  "toPayUrl": null,
  "createdAt": "2026-02-17T10:00:00Z",
  "expiresAt": "2026-02-24T10:00:00Z",
  "message": "Escrow initiated. Waiting for Telebirr confirmation."
}
```

| Field | Type | Description |
|-------|------|-------------|
| `toPayUrl` | string \| null | **Async gateways only.** The Telebirr H5 checkout URL the buyer opens to complete payment. Present when the real gateway is used; `null` for the synchronous mock (which returns `status: "escrowed"` directly). **Transient — never persisted; never returned by `GET /escrow/{id}`.** |

#### Asynchronous Payment (real Telebirr gateway)

With the real Telebirr gateway, fund collection is **asynchronous** (C2B H5 checkout):

1. `POST /escrow/initiate` creates the escrow in `initiated` and returns a
   `toPayUrl` (funds are **not** yet held; `telebirrRef` stays `null`).
2. The client opens `toPayUrl`; the buyer pays in Telebirr.
3. Telebirr calls the server webhook (`POST /webhooks/telebirr`), which — after
   signature + timestamp + replay + amount checks — transitions the escrow
   `initiated → escrowed` and stamps the real `telebirrRef`.
4. The client polls `GET /escrow/{id}` until `status` is `escrowed`.

If the buyer never completes payment, the escrow stays `initiated` and the
reconciliation cron cancels it only **after** the Telebirr payment window
(`timeout_express`, 120m) plus a safety buffer has elapsed — so a slow-but-valid
payment is never stranded in `cancelled`.

With the **mock** gateway (`TELEBIRR_MOCK=true`), `HoldFunds` is synchronous:
`initiate` returns `status: "escrowed"` immediately and no `toPayUrl`.

#### Idempotent Duplicate Response

If the same `idempotencyKey` is received again, return the cached result with `200 OK` (not an error).

```json
{
  "escrowId": "escrow-789",
  "status": "escrowed",
  "telebirrRef": "TBR-2026021710-ABC123",
  "message": "Duplicate request. Returning cached result.",
  "isDuplicate": true
}
```

#### Error Responses

| Status | Error Code | Description |
|--------|-----------|-------------|
| `400` | `ESCROW_001` | Invalid amount (below 100 ETB or above 500,000 ETB) |
| `400` | `ESCROW_002` | Missing or invalid deliveryId |
| `400` | `ESCROW_003` | Missing or invalid idempotency key format |
| `400` | `ESCROW_004` | Currency must be ETB |
| `401` | `ESCROW_005` | Invalid or expired JWT token |
| `403` | `ESCROW_006` | buyerId does not match authenticated user |
| `403` | `ESCROW_007` | Fayda ID verification required (amount > 10,000 ETB) |
| `403` | `ESCROW_025` | Caller's role claim is not `buyer` (only buyers initiate escrow; missing claim fails closed) |
| `403` | `ESCROW_026` | Caller is not the buyer on this delivery |
| `400` | `ESCROW_027` | `farmerId` does not match the farmer on this delivery |
| `400` | `ESCROW_028` | `amount` does not equal the linked order's `orderTotal` |
| `422` | `ESCROW_VALIDATION` | Linked order not found, so the amount cannot be verified |
| `404` | `ESCROW_008` | Delivery not found |
| `409` | `ESCROW_009` | Escrow already exists for this delivery (a second delivery-initiated escrow) |
| `409` | `ESCROW_024` | Funds for this order are already held in escrow (order-funded escrow, see §Order-Funded Escrow; takes precedence over `ESCROW_009` when that record is already linked to this delivery) |
| `422` | `ESCROW_010` | Delivery is not in a valid state for payment (must be pending or pickedUp) |
| `429` | `ESCROW_011` | Rate limit exceeded |
| `403` | `AUTH_040` / `AUTH_041` | Step-up required (1.4.0 payment tiers; from 1.7.0 also raised by the caller's risk tier, planned FRD-04). See Additions in 1.3.0 and 1.7.0 |
| `403` | `FRAUD_ACCOUNT_HELD` | Caller's risk tier is CRITICAL; nothing held (1.7.0, planned FRD-04) |
| `500` | `ESCROW_012` | Internal server error |
| `503` | `ESCROW_012` | Buyer profile could not be read for the Fayda step-up check (amount above 10,000 ETB); nothing held, safe to retry (1.4.0) |
| `502` | `ESCROW_013` | Telebirr gateway error (upstream failure) |
| `503` | `ESCROW_014` | Telebirr service unavailable |

**Error body format**:

```json
{
  "error": {
    "code": "ESCROW_007",
    "message": "Fayda ID verification required for transactions above 10,000 ETB. Please verify your identity in Settings.",
    "field": "buyerId"
  }
}
```

---

### 2. POST /escrow/release

Triggers release of escrowed funds to the farmer. This is an **internal trigger** called by the system when delivery is confirmed (F-12) and the 48-hour dispute window has passed. Can also be called by admin to settle a `farmer_wins` dispute verdict (`dispute_resolved`, 1.9.0).

#### Request Body

```json
{
  "escrowId": "escrow-789",
  "trigger": "delivery_confirmed",
  "deliveryId": "delivery-001",
  "idempotencyKey": "system:escrow-789:release:1708171200000"
}
```

| Field | Type | Required | Constraints | Description |
|-------|------|----------|-------------|-------------|
| `escrowId` | string (UUID) | Yes | Must reference an existing escrow | Escrow to release |
| `trigger` | string | Yes | Enum: `delivery_confirmed`, `dispute_resolved`, `admin_override` | What triggered the release |
| `deliveryId` | string (UUID) | Yes | Must match the escrow's delivery | Delivery verification reference |
| `idempotencyKey` | string | Yes | Format: `{actor}:{escrowId}:{action}:{timestamp}` | Prevents duplicate releases |

#### Success Response

**Status**: `200 OK`

```json
{
  "escrowId": "escrow-789",
  "status": "released",
  "farmerPayout": 14625.00,
  "commission": 375.00,
  "telebirrRef": "TBR-2026021810-DEF456",
  "releasedAt": "2026-02-18T10:00:00Z",
  "message": "Funds released to farmer's Telebirr account."
}
```

#### Error Responses

| Status | Error Code | Description |
|--------|-----------|-------------|
| `400` | `ESCROW_003` | Invalid idempotency key format |
| `401` | `ESCROW_005` | Invalid or expired JWT token |
| `403` | `ESCROW_015` | Unauthorized to release (not system/admin) |
| `404` | `ESCROW_016` | Escrow not found |
| `503` | `ESCROW_012` | The order or delivery behind a verdict could not be read; nothing moved, retry (1.9.0) |
| `422` | `ESCROW_INVALID_TRANSITION` | Escrow is not `escrowed` or `dispute_resolved`, or its verdict is `buyer_wins` (1.9.0; before 1.9.0 `disputed` was accepted too); from 1.7.0 the admin may also settle `held` (planned FRD-03) |
| `202` | `ESCROW_034` | The release is waiting in the manual payout queue and this call made no payout (1.8.0); with reason `GATEWAY_OUTCOME_UNKNOWN` an earlier gateway call may have paid (1.10.0) |
| `502` | `ESCROW_013` | Telebirr gateway error (a definite refusal, retried by the next sweep); from 1.10.0 also an unknown outcome whose queue write failed: not retried, the lock stays |
| `503` | `ESCROW_014` | Telebirr service unavailable |

---

### 3. POST /escrow/dispute

Buyer or farmer raises a dispute. Must be within 48 hours of delivery confirmation.

Propagation (1.2.1, SIM-12): a successful dispute also moves the linked order from
`delivered` to `disputed` (which blocks ratings) and the delivery from `delivered` to
`disputed`, best-effort as a system side effect; a failure is written to the escrow
audit ledger as `dispute_propagation_failed`. The reverse holds too: a delivery
disputed through `POST /sync` freezes its `escrowed` escrow the same way (reason
`other`, the delivery's free-text reason as the description, the same audit entry, no
extra idempotency record), so the 48-hour auto-release can never pay out a disputed
handoff.

Propagation fix (1.5.1, SIM-47): the escrow Lambda holds `dynamodb:UpdateItem` on the
orders and deliveries tables (before this every mirror write was denied, so order and
delivery stayed `delivered` and ratings stayed open). The delivery leg and the order leg
run independently, so one failing leg never skips the other. The order write reads the
order with a consistent read and retries a version conflict (3 attempts in all); a
missing order, or one that is no longer `delivered`, is nothing to do. A failed leg is
logged as `DISPUTE_PROPAGATION_FAILED` and audited as `dispute_propagation_failed` with
`metadata` `legs=<leg>:<reason>` (for example `order:[AmazonDynamoDBException]`,
`order:conflict`, `delivery:[InvalidOperationException]`). The dispute still answers
`200`: the escrow is already `disputed` and audited (money frozen) before propagation
runs, and failing the request would make the client retry into `ESCROW_020`.

Freeze first, everywhere (1.12.0, ESC-07): a dispute only ever comes from this endpoint or
from a delivery dispute through `POST /sync`; a client order update to `disputed` is refused
(`order-lifecycle.md` 1.14.0). Both freeze the escrow before anything else moves, and both move
the delivery and the order through the same mirror (see "Additions in 1.12.0"). The mirror audits
every hop in transaction-audit (`dispute`, `order_updated` for an order first advanced to
`delivered`), as a system actor with `metadata` `escrowId=<id>`, and the delivery's version is
added to (`ADD`), never written from a read. When the escrow carries no `deliveryId` (an order
escrow not linked yet) the order's `activeDeliveryId` is used. A dispute whose escrow write fails
its condition is never a `500`: the idempotency key is freed and the answer follows the escrow as
it is then (manual payout `409 ESCROW_038`, settlement lock `409 ESCROW_INVALID_TRANSITION`,
another dispute first `409 ESCROW_020`, any other status `422 ESCROW_INVALID_TRANSITION`). An escrow
that is already `disputed` when the request reads it answers `409 ESCROW_020` too.

#### Request Body

```json
{
  "escrowId": "escrow-789",
  "disputedBy": "buyer-456",
  "reason": "damaged_goods",
  "description": "Half the teff bags were wet and moldy on arrival.",
  "evidenceRefs": ["photos/delivery-001/damage-1.jpg", "photos/delivery-001/damage-2.jpg"],
  "idempotencyKey": "buyer-456:escrow-789:dispute:1708171200000"
}
```

| Field | Type | Required | Constraints | Description |
|-------|------|----------|-------------|-------------|
| `escrowId` | string (UUID) | Yes | Must reference an existing escrow in `escrowed` state | Escrow to dispute |
| `disputedBy` | string (UUID) | Yes | Must be buyer or farmer on this escrow | Who is raising the dispute |
| `reason` | string | Yes | Enum: `damaged_goods`, `wrong_quantity`, `wrong_crop`, `not_delivered`, `quality_mismatch`, `other` | Dispute category |
| `description` | string | No | 0-1000 chars, supports Amharic/Oromo/English | Free-text description |
| `evidenceRefs` | string[] | No | Max 5 items, each max 200 chars | Photo/document references in S3 |
| `idempotencyKey` | string | Yes | Format: `{userId}:{escrowId}:{action}:{timestamp}` | Prevents duplicate disputes |

#### Success Response

**Status**: `200 OK`

```json
{
  "escrowId": "escrow-789",
  "status": "disputed",
  "disputeId": "dispute-101",
  "disputedBy": "buyer-456",
  "reason": "damaged_goods",
  "disputeWindowEndsAt": "2026-02-20T10:00:00Z",
  "createdAt": "2026-02-18T14:00:00Z",
  "message": "Dispute registered. Platform will review within 48 hours."
}
```

#### Error Responses

| Status | Error Code | Description |
|--------|-----------|-------------|
| `400` | `ESCROW_003` | Invalid idempotency key format |
| `400` | `ESCROW_017` | Invalid dispute reason |
| `400` | `ESCROW_018` | Dispute window expired (>48 hours since delivery confirmation) |
| `401` | `ESCROW_005` | Invalid or expired JWT token |
| `403` | `ESCROW_019` | User is not buyer or farmer on this escrow |
| `404` | `ESCROW_016` | Escrow not found |
| `409` | `ESCROW_020` | Dispute already exists for this escrow: the escrow is already `disputed` when read, or another dispute (this endpoint or a delivery dispute) froze it between the read and the write (both 1.12.0; before, an already `disputed` escrow answered `422`) |
| `409` | `ESCROW_038` | The escrow's settlement is waiting in the manual payout queue; it can no longer be disputed (1.8.0) |
| `409` | `ESCROW_INVALID_TRANSITION` | A release or refund holds the settlement lock (1.9.0), or the escrow changed while the dispute was written and is still `escrowed`: try again (1.12.0) |
| `422` | `ESCROW_INVALID_TRANSITION` | Escrow is in any state but `escrowed` or `disputed` (a `held` escrow is refused too, 1.7.0), also when it moved there between the read and the write (1.12.0) |
| `500` | `ESCROW_012` | Internal server error |

---

### 4. POST /escrow/refund

Refunds escrowed funds to the buyer. Triggered by admin (a `buyer_wins` dispute verdict, 1.9.0) or system (7-day delivery timeout, or the dispute sweep).

#### Request Body

```json
{
  "escrowId": "escrow-789",
  "trigger": "dispute_resolved",
  "reason": "Buyer's dispute upheld: damaged goods confirmed by photo evidence.",
  "idempotencyKey": "admin:escrow-789:refund:1708257600000"
}
```

| Field | Type | Required | Constraints | Description |
|-------|------|----------|-------------|-------------|
| `escrowId` | string (UUID) | Yes | Must reference an existing escrow | Escrow to refund |
| `trigger` | string | Yes | Enum: `dispute_resolved`, `delivery_timeout`, `admin_override` | What triggered the refund |
| `reason` | string | No | 0-500 chars; 10-500 with `overrideOrderMismatch` | Explanation for audit trail |
| `idempotencyKey` | string | Yes | Format: `{actor}:{escrowId}:{action}:{timestamp}` | Prevents duplicate refunds |
| `overrideOrderMismatch` | boolean | No | Admin group only (1.9.0) | Refund a `buyer_wins` verdict although its order cannot follow it (goods already moving); audited with the admin and the reason, the order is flagged |

#### Success Response

**Status**: `200 OK`

```json
{
  "escrowId": "escrow-789",
  "status": "refunded",
  "refundAmount": 15000.00,
  "telebirrRef": "TBR-2026021910-GHI789",
  "refundedAt": "2026-02-19T10:00:00Z",
  "message": "Funds refunded to buyer's Telebirr account."
}
```

#### Error Responses

| Status | Error Code | Description |
|--------|-----------|-------------|
| `400` | `ESCROW_003` | Invalid idempotency key format |
| `401` | `ESCROW_005` | Invalid or expired JWT token |
| `400` | `ESCROW_VALIDATION` | `overrideOrderMismatch` without a `reason` of 10 to 500 characters (1.9.0) |
| `403` | `ESCROW_021` | Unauthorized to refund (not admin/system), or `overrideOrderMismatch` from outside the admin group (1.9.0) |
| `404` | `ESCROW_016` | Escrow not found |
| `503` | `ESCROW_012` | The order or delivery behind a verdict could not be read; nothing moved, retry (1.9.0) |
| `422` | `ESCROW_INVALID_TRANSITION` | Escrow is not `escrowed` or `dispute_resolved`, its verdict is `farmer_wins`, or its order cannot follow the verdict and no override was given (1.9.0; before 1.9.0 `disputed` was accepted too); from 1.7.0 the admin may also settle `held` (planned FRD-03), except a `heldFor: refund` order escrow whose order is progressing |
| `202` | `ESCROW_034` | The refund is waiting in the manual payout queue and this call made no payout (1.8.0); with reason `GATEWAY_OUTCOME_UNKNOWN` an earlier gateway call may have paid (1.10.0) |
| `502` | `ESCROW_013` | Telebirr gateway error (a definite refusal, retried by the next sweep); from 1.10.0 also an unknown outcome whose queue write failed: not retried, the lock stays |
| `503` | `ESCROW_014` | Telebirr service unavailable |

---

### 5. GET /escrow/{id}

Get the current status of an escrow transaction.

#### Path Parameters

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `id` | string (UUID) | Yes | Escrow ID |

#### Success Response

**Status**: `200 OK`

```json
{
  "escrowId": "escrow-789",
  "deliveryId": "delivery-001",
  "buyerId": "buyer-456",
  "farmerId": "farmer-123",
  "amount": 15000.00,
  "commission": 375.00,
  "farmerPayout": 14625.00,
  "currency": "ETB",
  "status": "escrowed",
  "telebirrRef": "TBR-2026021710-ABC123",
  "dispute": null,
  "createdAt": "2026-02-17T10:00:00Z",
  "updatedAt": "2026-02-17T10:01:00Z",
  "expiresAt": "2026-02-24T10:00:00Z",
  "deliveryConfirmedAt": null,
  "disputeWindowEndsAt": null,
  "releasedAt": null,
  "refundedAt": null
}
```

When a dispute exists:

```json
{
  "escrowId": "escrow-789",
  "status": "disputed",
  "dispute": {
    "disputeId": "dispute-101",
    "disputedBy": "buyer-456",
    "reason": "damaged_goods",
    "description": "Half the teff bags were wet and moldy.",
    "evidenceRefs": ["photos/delivery-001/damage-1.jpg"],
    "createdAt": "2026-02-18T14:00:00Z",
    "resolution": null
  }
}
```

#### Error Responses

| Status | Error Code | Description |
|--------|-----------|-------------|
| `401` | `ESCROW_005` | Invalid or expired JWT token |
| `403` | `ESCROW_022` | User is not a party to this escrow (buyer, farmer, the trucker the order was bound to, or admin) |
| `404` | `ESCROW_016` | Escrow not found |

---

### 6. GET /escrow/by-delivery/{deliveryId}

Get the escrow associated with a specific delivery (escrow is 1:1 with a delivery, resolved via the `DeliveryIndex` GSI). Only the buyer, the farmer, the trucker the order was bound to (it carries the transport leg), or an admin may read it. **F-33.**

#### Path Parameters

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `deliveryId` | string (UUID) | Yes | Delivery ID |

#### Success Response

`200 OK` — identical `EscrowDto` body as `GET /escrow/{id}` (see above).

#### Error Responses

| Status | Error Code | Description |
|--------|-----------|-------------|
| `401` | `ESCROW_005` | Invalid or expired JWT token |
| `403` | `ESCROW_022` | User is not a party to this escrow (buyer, farmer, the trucker the order was bound to, or admin) |
| `404` | `ESCROW_016` | No escrow exists for the given delivery |

---

### 7. GET /escrow/history

Get the authenticated user's payment history.

#### Query Parameters

| Parameter | Type | Required | Description | Example |
|-----------|------|----------|-------------|---------|
| `status` | string | No | Filter by escrow status | `status=escrowed` |
| `role` | string | No | The caller's role in the transaction: `buyer`, `farmer` or `trucker`. Selects the GSI (`BuyerStatusIndex` / `FarmerStatusIndex` / `TruckerStatusIndex`) keyed by the caller's user id. Defaults to the token's role claim (1.2.4); a token without one reads as `buyer` | `role=trucker` |
| `fromDate` | string | No | Start date (ISO 8601) | `fromDate=2026-01-01T00:00:00Z` |
| `toDate` | string | No | End date (ISO 8601) | `toDate=2026-02-28T23:59:59Z` |
| `limit` | number | No | Results per page (default: 20, max: 50) | `limit=20` |
| `nextToken` | string | No | Pagination token | `nextToken=base64...` |

> **Note**: `deliveryId` is **not** a supported filter here. To fetch the escrow for a specific delivery, use `GET /escrow/by-delivery/{deliveryId}`.

#### Success Response

**Status**: `200 OK`

```json
{
  "escrows": [
    {
      "escrowId": "escrow-789",
      "deliveryId": "delivery-001",
      "orderId": "order-123",
      "buyerId": "buyer-456",
      "farmerId": "farmer-123",
      "counterpartyName": "Alemayehu Bekele",
      "amount": 15000.00,
      "commission": 375.00,
      "farmerPayout": 13625.00,
      "truckerPayout": 1000.00,
      "truckerId": "trucker-789",
      "currency": "ETB",
      "status": "completed",
      "createdAt": "2026-02-17T10:00:00Z",
      "completedAt": "2026-02-19T10:00:00Z",
      "expiresAt": "2026-02-24T10:00:00Z",
      "refundPending": false,
      "refundReason": null
    }
  ],
  "nextToken": "base64-encoded-pagination-token",
  "hasMore": false,
  "total": 1
}
```

Money legs (SIM-15): `amount = farmerPayout + truckerPayout + commission` on every row. `truckerPayout` and `truckerId` are `null` when the order carried no trucker split. Each client renders only the caller's own leg in its Payments and Profile views (buyer: `amount`, farmer: `farmerPayout`, trucker: `truckerPayout`); the escrow detail shows the full breakdown. `counterpartyName` is the farmer for a buyer and the buyer for a farmer or trucker.

Refund pending (SIM-20): `refundPending` is `true` while a row is still `escrowed` but the money is on its way back to the buyer: the order was cancelled or rejected (the server scheduled the refund, `refundReason` says why, `expiresAt` is set to that moment) or the hold passed its `expiresAt`; the hourly sweep refunds it. Clients label such rows "Refund pending" and never "Funds held safely"; a row without a delivery shows its `orderId` as the reference. Both fields are also on `EscrowDto`.

#### Error Responses

| Status | Error Code | Description |
|--------|-----------|-------------|
| `400` | `ESCROW_023` | Invalid query parameter |
| `401` | `ESCROW_005` | Invalid or expired JWT token |

---

## Data Models

### EscrowDto

| Field | Type | Required | Constraints | Description |
|-------|------|----------|-------------|-------------|
| `escrowId` | string (UUID) | Yes | Server-generated | Unique escrow identifier |
| `deliveryId` | string (UUID) | Yes | References Delivery entity | The delivery this escrow covers |
| `buyerId` | string (UUID) | Yes | References User entity | Buyer making payment |
| `farmerId` | string (UUID) | Yes | References User entity | Farmer receiving payment |
| `amount` | number | Yes | 100.00 - 500,000.00 | Total payment amount (ETB) |
| `commission` | number | Yes | Calculated: amount * rate | Platform commission (ETB) |
| `commissionRate` | number | Yes | Server-configured, default 0.025 | Commission percentage (2.5%) |
| `farmerPayout` | number | Yes | Crop leg of amount | Net amount farmer receives (ETB); with a trucker split amount = farmerPayout + truckerPayout + commission |
| `truckerPayout` | number | No | Transport leg of amount; null without a trucker split | Net amount the trucker receives on release (ETB) |
| `truckerId` | string (UUID) | No | References User entity; stamped at creation from the order, or backfilled at delivery linkage | Trucker paid out of this escrow (the truck the order was bound to) |
| `currency` | string | Yes | Fixed: `"ETB"` | Ethiopian Birr |
| `status` | enum | Yes | See State Machine; clients tolerate unknown values (1.7.0) | Current escrow state; `held` from 1.7.0 (planned FRD-03) |
| `telebirrRef` | string | No | Null until Telebirr confirms | Telebirr transaction reference |
| `telebirrHoldRef` | string | No | Null until hold succeeds | Reference for the hold operation |
| `telebirrReleaseRef` | string | No | Null until release succeeds | Reference for the release operation |
| `telebirrRefundRef` | string | No | Null until refund succeeds | Reference for the refund operation |
| `dispute` | DisputeDto | No | Null if no dispute | Embedded dispute details |
| `buyerFaydaVerified` | boolean | Yes | Checked at initiation | Whether buyer's Fayda ID was verified |
| `farmerFaydaVerified` | boolean | Yes | Checked at initiation | Whether farmer's Fayda ID was verified |
| `createdAt` | string (ISO 8601) | Yes | Server-generated | When escrow was initiated |
| `updatedAt` | string (ISO 8601) | Yes | Auto-updated | Last state change timestamp |
| `expiresAt` | string (ISO 8601) | Yes | createdAt + 7 days | Auto-refund deadline |
| `deliveryConfirmedAt` | string (ISO 8601) | No | Set by F-12 | When delivery was confirmed |
| `disputeWindowEndsAt` | string (ISO 8601) | No | deliveryConfirmedAt + 48h | Dispute deadline |
| `releasedAt` | string (ISO 8601) | No | Set on release | When funds were released |
| `refundedAt` | string (ISO 8601) | No | Set on refund | When funds were refunded |
| `refundPending` | boolean | Yes | `status = escrowed` and (`refundReason` set or `expiresAt` passed) | Money still held but going back to the buyer; label "Refund pending" (SIM-20) |
| `refundReason` | string | No | Set when a cancelled/rejected order schedules its refund | Why the refund was scheduled |
| `holdReason` | string | No | Admin responses only (1.7.0, planned FRD-03) | `fraud_confirmed`, `account_suspended` or `risk_critical` |
| `heldAt` | string (ISO 8601) | No | Admin responses only | When the sweep held the escrow |
| `heldBy` | string | No | Admin responses only | `system` |
| `heldFromStatus` | string | No | Admin responses only | `escrowed` or `dispute_resolved` |
| `heldFor` | string | No | Admin responses only | `release` or `refund`: what the sweep was about to do |
| `completedAt` | string (ISO 8601) | No | Set on completion | When transaction settled |
| `version` | number | Yes | >= 1 | Optimistic concurrency control |

### DisputeDto

| Field | Type | Required | Constraints | Description |
|-------|------|----------|-------------|-------------|
| `disputeId` | string (UUID) | Yes | Server-generated | Unique dispute identifier |
| `escrowId` | string (UUID) | Yes | References parent escrow | Parent escrow |
| `disputedBy` | string (UUID) | Yes | Must be buyer or farmer | Who raised the dispute |
| `reason` | enum | Yes | See Dispute Reasons | Dispute category |
| `description` | string | No | 0-1000 chars | Free-text explanation |
| `evidenceRefs` | string[] | No | Max 5 items | S3 references to photos/documents |
| `resolution` | string | No | Enum: `farmer_wins`, `buyer_wins`; `partial_refund` only on rows recorded before 1.9.0 (refused since, until per-leg settlement, ESC-20) | How dispute was resolved |
| `resolvedBy` | string | No | Admin user ID | Who resolved it |
| `resolvedAt` | string (ISO 8601) | No | Set on resolution | When dispute was resolved |
| `createdAt` | string (ISO 8601) | Yes | Server-generated | When dispute was raised |

### Dispute Reasons (Enum)

| Value | Description |
|-------|-------------|
| `damaged_goods` | Crops arrived damaged or spoiled |
| `wrong_quantity` | Quantity delivered does not match listing |
| `wrong_crop` | Different crop type than ordered |
| `not_delivered` | Delivery was not completed |
| `quality_mismatch` | Crop quality lower than described |
| `other` | Other reason (requires description) |

### EscrowAuditEntry

Every state change produces an immutable audit record.

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `auditId` | string (UUID) | Yes | Unique audit entry ID |
| `escrowId` | string (UUID) | Yes | References parent escrow |
| `action` | string | Yes | The action taken (e.g., `initiate`, `hold_success`, `release`, `dispute`, `refund`) |
| `fromState` | string | Yes | Previous escrow state |
| `toState` | string | Yes | New escrow state |
| `actor` | string | Yes | Who performed the action (userId, `system`, or `admin`) |
| `telebirrRef` | string | No | Telebirr reference if applicable |
| `metadata` | object | No | Additional context (idempotency key, error details, etc.) |
| `timestamp` | string (ISO 8601) | Yes | When the action occurred |
| `ipAddress` | string | No | Client IP (for fraud detection) |

---

## Telebirr Integration

### ITelebirrGateway Interface

All Telebirr interactions are abstracted behind this interface to enable mock implementations during development.

```
ITelebirrGateway:
  HoldFunds(request: HoldFundsRequest) -> HoldFundsResponse
  ReleaseFunds(request: ReleaseFundsRequest) -> ReleaseFundsResponse
  RefundFunds(request: RefundFundsRequest) -> RefundFundsResponse
  GetTransactionStatus(transactionRef: string) -> TransactionStatus
```

### HoldFundsRequest

| Field | Type | Description |
|-------|------|-------------|
| `transactionRef` | string | Idempotency key for Telebirr (maps to escrow idempotencyKey) |
| `buyerPhone` | string | Buyer's Telebirr-registered phone (+251...) |
| `amount` | number | Amount to hold (ETB) |
| `description` | string | "WeredaConnect escrow for delivery {deliveryId}" |

### HoldFundsResponse

| Field | Type | Description |
|-------|------|-------------|
| `success` | boolean | Whether the hold succeeded |
| `telebirrRef` | string | Telebirr's reference ID for this hold |
| `errorCode` | string | Telebirr error code (if failed) |
| `errorMessage` | string | Human-readable error (if failed) |

### ReleaseFundsRequest

| Field | Type | Description |
|-------|------|-------------|
| `transactionRef` | string | Idempotency key |
| `holdRef` | string | Original hold reference (from HoldFundsResponse) |
| `farmerPhone` | string | Farmer's Telebirr-registered phone |
| `amount` | number | Amount to release (after commission deduction) |
| `platformCommission` | number | Commission amount retained by platform |

### RefundFundsRequest

| Field | Type | Description |
|-------|------|-------------|
| `transactionRef` | string | Idempotency key |
| `holdRef` | string | Original hold reference |
| `buyerPhone` | string | Buyer's Telebirr phone for refund |
| `amount` | number | Full amount to refund (no commission deducted) |

### Mock Implementation (Development)

For development and testing, `MockTelebirrGateway` returns success responses with deterministic reference IDs:
- Hold: `MOCK-HOLD-{escrowId}-{timestamp}`
- Release: `MOCK-RELEASE-{escrowId}-{timestamp}`
- Refund: `MOCK-REFUND-{escrowId}-{timestamp}`

The mock simulates a 500ms delay to mimic real API latency. It also supports a `SIMULATE_FAILURE` flag in metadata to test error handling paths.

### Real Implementation Pattern

The real `TelebirrGateway` wraps Ethio Telecom's B2B API:
1. Authenticate with Telebirr merchant credentials (stored in AWS Secrets Manager)
2. Submit transaction with `transactionRef` as the idempotency key
3. Poll or receive webhook for confirmation
4. Map Telebirr-specific error codes to `ESCROW_*` error codes
5. All calls have a 30-second timeout with 2 retries (exponential backoff: 5s, 15s)
6. Log all requests/responses (with PII redacted) for audit

---

## Idempotency Requirements

Idempotency is **critical** for payment operations. Ethiopian 3G networks frequently drop connections mid-request, so clients will retry.

### Idempotency Key Format

```
{actor}:{entityId}:{action}:{timestamp}
```

Examples:
- Buyer initiates: `buyer-456:delivery-001:initiate:1708084800000`
- System releases: `system:escrow-789:release:1708171200000`
- Buyer disputes: `buyer-456:escrow-789:dispute:1708171200000`
- Admin refunds: `admin:escrow-789:refund:1708257600000`

### Server Processing Flow

```
1. Receive escrow request with idempotencyKey
2. Check EscrowIdempotency table (DynamoDB) for existing key
3. If key exists:
   a. Return cached result (200 OK, isDuplicate: true)
   b. Do NOT re-process
4. If key does not exist:
   a. Begin processing
   b. Write idempotency record BEFORE calling Telebirr
   c. Call Telebirr gateway
   d. Update idempotency record with result
   e. Return result (201 Created or 200 OK)
5. If processing fails midway:
   a. Idempotency record exists with "processing" status
   b. Next retry detects this and checks Telebirr status
   c. Reconcile state based on Telebirr's response
```

### Idempotency Table Schema

| Field | Type | Description |
|-------|------|-------------|
| `idempotencyKey` | String (PK) | Composite key |
| `escrowId` | String | Associated escrow |
| `status` | String | `processing`, `completed`, `failed` |
| `result` | String | JSON-encoded response (for cache return) |
| `telebirrRef` | String | Telebirr reference (if applicable) |
| `createdAt` | Number | Unix timestamp (ms) |
| `expiresAt` | Number (TTL) | Auto-delete after 30 days |

---

## DynamoDB Key Design

### EscrowsTable

| Key | Field | Description |
|-----|-------|-------------|
| **Partition Key** | `escrowId` | Unique escrow identifier |
| **Sort Key** | `createdAt` | ISO 8601 timestamp |

**GSI: DeliveryIndex**:
- Partition key: `deliveryId`
- Sort key: `createdAt`
- Use case: Look up escrow by delivery (1:1 relationship)

**GSI: BuyerStatusIndex**:
- Partition key: `buyerId`
- Sort key: `createdAt`
- Use case: Buyer's payment history, sorted by recency

**GSI: FarmerStatusIndex**:
- Partition key: `farmerId`
- Sort key: `createdAt`
- Use case: Farmer's payment history, sorted by recency

**GSI: TruckerStatusIndex** (sparse):
- Partition key: `truckerId`
- Sort key: `createdAt`
- Use case: Trucker's payout history (`GET /escrow/history?role=trucker`); only escrows whose order was bound to a truck carry `truckerId`

**GSI: StatusExpiresIndex**:
- Partition key: `status`
- Sort key: `expiresAt`
- Use case: Find escrowed items nearing expiry (for auto-refund cron)

**GSI: OrderIndex** (sparse):
- Partition key: `orderId`
- Sort key: `createdAt`
- Use case: the admin transaction monitor join; only order-funded escrows carry `orderId`

**GSI: ManualPayoutIndex** (sparse, 1.8.0):
- Partition key: `manualPayout` (always `open`)
- Sort key: `manualPayoutQueuedAt`
- Use case: the manual payout queue, oldest first; the marker is removed when the last leg is paid

### EscrowAuditTable

| Key | Field | Description |
|-----|-------|-------------|
| **Partition Key** | `escrowId` | Parent escrow |
| **Sort Key** | `timestamp` | When the audit event occurred |

No GSIs needed. Audit entries are always queried by escrowId and sorted by time.

### EscrowIdempotencyTable

| Key | Field | Description |
|-----|-------|-------------|
| **Partition Key** | `idempotencyKey` | Composite idempotency key |

TTL field: `expiresAt` (30 days). No GSIs needed.

### Query Patterns

| Query | Table/Index | Key Condition | Filter |
|-------|------------|---------------|--------|
| Get escrow by ID | EscrowsTable (PK) | `escrowId = X` | -- |
| Get escrow for delivery | DeliveryIndex | `deliveryId = X` | -- |
| Buyer's history | BuyerStatusIndex | `buyerId = X`, SK between dates | `status = Y` (optional) |
| Farmer's history | FarmerStatusIndex | `farmerId = X`, SK between dates | `status = Y` (optional) |
| Trucker's history | TruckerStatusIndex | `truckerId = X`, SK between dates | `status = Y` (optional) |
| Expiring escrows | StatusExpiresIndex | `status = escrowed`, SK < now + 24h | `attribute_not_exists(manualPayout)` (1.8.0, every cron status query) |
| Manual payout queue | ManualPayoutIndex | `manualPayout = open`, oldest first | -- |
| Audit trail | EscrowAuditTable | `escrowId = X` | -- |
| Idempotency check | EscrowIdempotencyTable (PK) | `idempotencyKey = X` | -- |

---

## Validation Rules

### Amount Validation

| Rule | Constraint | Rationale |
|------|-----------|-----------|
| Minimum transaction | 100.00 ETB | Below this, Telebirr fees make transactions uneconomical |
| Maximum transaction | 500,000.00 ETB | Regulatory limit; amounts above this require additional KYC |
| Precision | 2 decimal places | ETB sub-unit (santim) is 1/100 |
| Currency | ETB only | Ethiopian Birr is the only supported currency |

### Fayda ID Verification

| Rule | Constraint | Rationale |
|------|-----------|-----------|
| Required for amounts > 10,000 ETB | Both buyer and farmer must have verified Fayda ID | Ethiopian Jan 2026 digital transaction policy |
| Verified during registration (F-03) | Stored as `custom:faydaId` in Cognito | One-time verification, not per-transaction |
| Check at escrow initiation | Server validates Fayda status from user profile | Prevents initiating a transaction that will be rejected |

### Delivery State Validation

Escrow can only be initiated when the associated delivery is in a valid state:
- `pending` -- delivery created, awaiting pickup
- `pickedUp` -- trucker has picked up goods

Escrow CANNOT be initiated when delivery is in:
- `delivered` -- too late; delivery already confirmed without escrow
- `disputed` -- delivery itself is in dispute
- `inTransit` -- allowed only if escrow was initiated before transit

### User Validation

1. `buyerId` must match the JWT `sub` claim (buyers can only pay for their own purchases)
2. `farmerId` must be a registered user with role `farmer`
3. Both users must be phone-verified (`isPhoneVerified: true`)
4. For amounts > 10,000 ETB, both users must have `custom:faydaId` set in Cognito
5. The caller's role claim must be `buyer` (`ESCROW_025`, checked before anything else); the delivery's `buyerId` must be the caller (`ESCROW_026`) and `farmerId` must be the delivery's farmer (`ESCROW_027`). Nothing is written when any of these fail.
6. When the delivery (or, for a delivery without one, the request) names an order, `amount` must equal that order's `orderTotal` (`ESCROW_028`), the order must belong to the caller, and a client `orderId` may only echo the delivery's own. The payout split (farmer / trucker / commission) is then taken from that order, exactly as on the order-funded leg.

### Commission Calculation

```
commission = amount * commissionRate
farmerPayout = amount - commission
```

- `commissionRate` is server-configured (stored in AWS Systems Manager Parameter Store)
- Default: 0.025 (2.5%)
- Minimum commission: 5 ETB (floor)
- Commission is deducted at release time, not at hold time (full amount is escrowed)

---

## Offline Behavior

### What Works Offline

| Action | Offline Behavior | Sync When Online |
|--------|-----------------|-----------------|
| View escrow status | Cached in local Isar (last known state) | Auto-refresh on connectivity |
| Initiate payment | **Payment INTENT** queued locally with `pending_sync` flag | POST /escrow/initiate sent when online |
| Raise dispute | Dispute INTENT queued locally | POST /escrow/dispute sent when online |
| View payment history | Cached list from last sync | Auto-refresh on connectivity |

### What Requires Connectivity

| Action | Why |
|--------|-----|
| Actual fund hold (Telebirr HoldFunds) | Money movement requires Telebirr API |
| Fund release | Server-triggered after delivery confirmation |
| Fund refund | Server-triggered by admin or timeout |

### Offline Payment Intent Flow

```
1. Buyer taps "Pay" on delivery screen
2. Mobile creates local EscrowIntent in Isar:
   - status: "pending_sync"
   - amount, deliveryId, buyerId, farmerId
   - idempotencyKey generated client-side
3. UI shows: "Payment queued. Will process when you have internet."
4. When connectivity detected:
   a. SyncExecutor picks up pending escrow intents
   b. POST /escrow/initiate with queued data
   c. On success: update local Isar with server response
   d. On failure: show notification to buyer
5. Buyer can cancel a pending_sync intent before it syncs
```

### Local Isar Cache (Payment Entity)

The mobile Payment entity (defined in `data-models.md`) caches escrow state locally:
- `paymentId` maps to `escrowId`
- `status` mirrors server state (updated on sync)
- `telebirrRef` populated after server confirms hold
- Stale data detection: if local `updatedAt` is > 1 hour old, show "may be outdated" indicator

---

## Error Handling

### Partial Failure Scenarios

| Scenario | Handling |
|----------|---------|
| **Telebirr hold times out** | Idempotency record set to `processing`. Next retry queries Telebirr for status. If held, mark as `escrowed`. If not, allow re-initiation. |
| **Telebirr hold succeeds but DB write fails** | Idempotency record exists with `processing` status. Retry detects this, queries Telebirr, and reconciles. |
| **Telebirr release fails** | A definite refusal releases the settlement lock and the next hourly sweep retries; a timeout or any answer that does not say whether money moved keeps the lock and parks the escrow for an admin, never a blind retry (Additions in 1.10.0). Funds remain escrowed. |
| **Telebirr refund fails** | Same rules as release (1.10.0). Buyer sees "refund pending" status. |
| **Double-charge prevention** | Idempotency key checked BEFORE calling Telebirr. Duplicate requests return cached result. |
| **Network drops during initiation** | Client retries with same idempotency key. Server returns cached result if already processed. |

### Reconciliation

A scheduled Lambda (every 6 hours) runs reconciliation:
1. Query all escrows with `status = initiated` older than 30 minutes
2. Check Telebirr for actual hold status
3. Update escrow state if Telebirr confirms hold
4. Cancel escrow if Telebirr has no record of the hold

### Timeout Handling

| Timeout | Duration | Action |
|---------|----------|--------|
| Escrow expiry | 7 days from creation | Auto-refund if delivery not completed |
| Dispute window | 48 hours from delivery confirmation | Auto-release if no dispute raised |
| Telebirr API timeout | 30 seconds per call | Not retried: an unknown outcome, parked for an admin (1.10.0) |
| Stale settlement lock | 15 minutes | The hourly recovery step parks the escrow for an admin (1.10.0) |
| Initiated → escrowed timeout | 30 minutes | Reconciliation marks as failed |

---

## Security Considerations

### Payment Data Encryption

- All payment data encrypted at rest in DynamoDB (AWS-managed encryption)
- Telebirr credentials stored in AWS Secrets Manager (not environment variables)
- Phone numbers and Fayda IDs encrypted in transit (TLS 1.2+)
- Audit log entries are immutable (no update/delete operations on EscrowAuditTable)

### Fraud Prevention

| Check | Implementation |
|-------|---------------|
| **Velocity check** | Max 5 escrow initiations per buyer per hour |
| **Amount anomaly** | Planned (FRD-09): flag transactions above 3 standard deviations from the user's own average |
| **Self-trading** | buyerId and farmerId must be different users |
| **Duplicate delivery payment** | One escrow per deliveryId (enforced by DeliveryIndex) |
| **Fayda ID check** | Mandatory for high-value transactions (> 10,000 ETB) |
| **IP geolocation** | Log client IP for forensic analysis (not blocking) |
| **Risk tier gate** | Planned (FRD-04): MEDIUM and HIGH add step-up on initiate at any amount; CRITICAL refuses with `FRAUD_ACCOUNT_HELD`. `specs/architecture/fraud-detection-architecture.md` section 17 |
| **Fraud hold** | Planned (FRD-03): a settlement sweep moves the escrow of a suspended or CRITICAL party to `held` instead of paying out. Same document, section 18 |

### Authorization Matrix

| Action | Buyer | Farmer | Admin | System |
|--------|-------|--------|-------|--------|
| Initiate escrow | Yes | No | No | No |
| Cancel (before hold) | Yes | No | Yes | No |
| Raise dispute | Yes | Yes | No | No |
| Release funds | No | No | Yes | Yes (auto) |
| Refund funds | No | No | Yes | Yes (auto) |
| View own escrow | Yes | Yes | Yes | -- |
| View any escrow | No | No | Yes | -- |
| View audit trail | No | No | Yes | -- |

### Audit Trail

Every escrow state change MUST produce an EscrowAuditEntry. The audit trail is:
- **Append-only**: No updates or deletes
- **Timestamped**: Server-generated UTC timestamps
- **Actor-tracked**: Records who performed the action
- **Telebirr-linked**: Includes Telebirr reference when applicable
- **Retained for 7 years**: Per Ethiopian financial record-keeping practice

---

## Ethiopian Context

### Telebirr

- **Operator**: Ethio Telecom (state-owned, monopoly telecom provider)
- **Users**: 40+ million (as of 2026)
- **Dominance**: Primary mobile money platform in Ethiopia, ahead of M-Pesa and HelloCash
- **USSD access**: Many rural users access Telebirr via USSD (`*127#`) on basic phones
- **B2B API**: Available for merchant integration (used by WeredaConnect)
- **Trust**: Government-backed, higher trust than private payment providers

### Fayda ID (National Digital ID)

- **Mandate**: January 2026 Ethiopian policy requires Fayda ID for digital financial transactions above 10,000 ETB
- **Enrollment**: Growing but not universal; rural adoption still ramping up
- **Integration**: Verified during user registration (F-03), stored in Cognito as `custom:faydaId`
- **Graceful degradation**: Transactions below 10,000 ETB do not require Fayda ID

### Currency: Ethiopian Birr (ETB)

- **Code**: ETB
- **Sub-unit**: Santim (1 ETB = 100 santim)
- **Exchange rate**: Approximately 1 USD = 55 ETB (as of 2026, volatile)
- **Typical transaction sizes on WeredaConnect**:
  - Small: 500-2,000 ETB (single farmer, small load)
  - Medium: 5,000-20,000 ETB (multiple quintals of teff)
  - Large: 50,000-200,000 ETB (bulk wholesale purchase)

### Mobile Money Patterns in Rural Ethiopia

- **Cash-dominant economy**: Mobile money is growing but cash remains king in rural areas
- **Trust barrier**: First-time mobile money users need strong safety signals (escrow status messages, Telebirr confirmation receipts)
- **Shared devices**: Payment screens should show minimal sensitive data; require PIN or biometric confirmation
- **USSD fallback**: Some users may initiate Telebirr payments via USSD rather than in-app; the platform must handle both flows
- **Network interruptions**: Payment confirmations may arrive minutes or hours after initiation; UI must handle async confirmation gracefully

### Low-Literacy Considerations for Payment UI

- Use color-coded status indicators (green = paid, yellow = pending, red = disputed)
- Show amounts prominently with ETB symbol
- Use icons for actions (shield = escrow, checkmark = released, exclamation = disputed)
- Voice confirmation for payment actions ("Are you sure you want to pay 15,000 Birr?")
- Minimise text; maximise visual confirmation flows

---

## Automated Processes

### Auto-Release (Cron)

A scheduled Lambda runs every hour:
1. Query `StatusExpiresIndex` for escrows with `status = escrowed`
2. For each, check if `deliveryConfirmedAt` is set AND `now > disputeWindowEndsAt`
3. If yes, call POST /escrow/release with `trigger = delivery_confirmed`
4. Log audit entry with `actor = system`
5. From 1.7.0 (planned FRD-03, FRD-04): between steps 2 and 3 the sweep checks every party's risk tier and suspension; on a trigger the escrow becomes `held` and no money moves

### Auto-Refund (Cron)

A scheduled Lambda runs every hour:
1. Query `StatusExpiresIndex` for escrows with `status = escrowed` AND `expiresAt < now`
2. For each expired escrow, call POST /escrow/refund with `trigger = delivery_timeout`
3. Log audit entry with `actor = system`
4. Notify buyer and farmer via push notification
5. From 1.7.0 (planned FRD-03, FRD-04): the same risk gate runs after the order-aware checks and before a stale `funded` order is cancelled or restocked

### Reconciliation (Cron)

A scheduled Lambda runs every 6 hours:
1. Query escrows with `status = initiated` AND `createdAt < now - 30 minutes`
2. For each, query Telebirr GetTransactionStatus
3. If Telebirr confirms hold: update to `escrowed`
4. If Telebirr has no record: mark as `cancelled` with reason "hold_failed"
5. Log all reconciliation actions in audit trail

---

## Performance Considerations

### Rate Limiting

| Endpoint | Rate Limit | Burst | Per |
|----------|-----------|-------|-----|
| `POST /escrow/initiate` | 10 req/s | 20 | Per user (JWT sub) |
| `POST /escrow/dispute` | 5 req/s | 10 | Per user |
| `POST /escrow/release` | 50 req/s | 100 | Per system (internal) |
| `POST /escrow/refund` | 10 req/s | 20 | Per admin |
| `GET /escrow/{id}` | 50 req/s | 100 | Per user |
| `GET /escrow/history` | 20 req/s | 40 | Per user |

### Pagination

- Default limit: 20 escrows per page
- Maximum limit: 50 escrows per page
- Pagination token: Base64-encoded DynamoDB ExclusiveStartKey
- Token expiration: 24 hours

### Caching

- **Mobile**: Escrow status cached in local Isar database for offline viewing
- **Server**: No CDN caching for escrow endpoints (financial data must be fresh)
- **Idempotency cache**: 30-day TTL in DynamoDB (longer than sync protocol's 7 days because financial records require longer retention)

---

## Future Enhancements

### Phase R5+
- **Partial refund**: Dispute resolution with partial refund (e.g., 50% refund for partially damaged goods)
- **Multi-party escrow**: Support cooperative sales where multiple farmers are paid from a single escrow
- **Telebirr webhook**: Replace polling with real-time webhook notifications from Telebirr
- **USSD payment flow**: Support buyers who initiate payment via Telebirr USSD (`*127#`) instead of in-app
- **Insurance integration (F-27)**: Link escrow to AICE insurance for high-value shipments
- **Transaction analytics**: Dashboard showing platform transaction volume, commission revenue, dispute rates
- **Trucker payment**: Separate escrow flow for transport payment (currently out of scope -- farmer-buyer only)
- **Escrow expiry notifications**: Push notification 24 hours before auto-refund deadline

### Performance
- Implement DynamoDB Streams for real-time escrow state change events
- Add CloudWatch custom metrics: escrow initiation rate, release latency, dispute rate
- Optimize reconciliation Lambda with parallel Telebirr queries

---

## Additions in 1.3.0 (settlement authority, identity gates)

### Who may release or refund
`POST /escrow/release` and `POST /escrow/refund` are served on the **admin** authorizer. The
caller must be a member of the admin pool's `admin` group (`cognito:groups`); a marketplace
token, or a token that merely claims an admin role, gets `ESCROW_015` / `ESCROW_021`. The
cron (system actor) releases and refunds in-process.

### Dispute verdicts are settled by the sweep
`POST /admin/disputes/{id}/resolve` records `disputeResolution`, `disputeResolvedBy` and
`disputeResolvedAt`, moves the escrow to `dispute_resolved` and writes an
`admin_dispute_resolved` audit entry. The hourly cron then settles: `farmer_wins` releases
(trigger `dispute_resolved`), `buyer_wins` refunds; `partial_refund` has no automatic leg
and needs a manual admin release or refund. `dispute_resolved` is therefore a valid source
state for release and refund. Until 1.9.0 the settlement lock refused `dispute_resolved`, so
no verdict ever settled; since 1.9.0 `partial_refund` is refused at the verdict (ESC-03).

### Step-up
`POST /escrow/initiate` is tiered at 10,000 ETB (1.4.0): up to and including it, `403 AUTH_040`
when the caller's `auth_time` is older than the step-up window; above it, `403 AUTH_041` unless
the buyer's last Fayda login (`POST /user/fayda-link`) is within the window. A fresh password
does not satisfy the Fayda tier. When the buyer's profile cannot be read for that check the
response is `503 ESCROW_012` (nothing held; safe to retry). `POST /escrow/dispute` returns `403 AUTH_040` when the
escrow's amount is at or above the step-up threshold and `auth_time` is older than the window.
See auth-api.md 2.4.0.

### Payout destination and identity at release
Release and refund send money to the **registered phone** of the party, read from the
profile at settlement time, never to a client-supplied number. Before the settlement lock:

| Condition | Result |
|-----------|--------|
| farmer profile missing | `422 ESCROW_029` |
| farmer phone not verified | `403 ESCROW_029` |
| amount above 10,000 ETB and farmer not Fayda-verified | `403 ESCROW_029` |

Every blocked release writes a `release_blocked_kyc` audit entry whose metadata names the reason.
| trucker leg with an unverified trucker | farmer leg proceeds, trucker leg stays `truckerPayoutPending`; since 1.11.0 the escrow stays `released` and the hourly trucker retry pays the leg once the trucker passes the gate |

A blocked release leaves the escrow untouched for an admin.

### Webhook hardening
A notification with a missing or unparseable `timestamp` is rejected. `biz_content.merch_code`
must equal the configured merchant code. When the notification carries the paying wallet's
phone (`biz_content.msisdn`, field name to be confirmed on the live payload) it is compared
with the buyer's registered phone: a mismatch is audited as `webhook_payer_mismatch` and,
with `TELEBIRR_REQUIRE_PAYER_MATCH=true`, rejected; a missing payer while a match is
required is audited as `webhook_payer_unbound` and rejected.

---

## Additions in 1.5.0 (live order payments, ESC-01)

Before 1.5.0 only the mock auto-fund could move an order to `funded`, so with the live
gateway no order could ever be paid. An order is now paid through a hosted Telebirr
checkout that the server opens for it, and the webhook funds the order.

> **Deploy gate:** live release and refund fail closed since 1.8.0 (ESC-02); the remaining
> P0 money-safety gates (ESC-03, ESC-05 at least) still apply before `enableTelebirrLive`.

### POST /escrow/order-checkout

The order's buyer opens (or gets back) the live Telebirr checkout for a `payment_submitted`
order. The payment gates (KYC and step-up) already ran when the order moved to
`payment_submitted` (`order-lifecycle.md` §Payment gates); the checkout only binds a Telebirr
payment to the order.

Request body:

```json
{ "orderId": "b1c2d3e4-f5a6-7890-bcde-f12345678901" }
```

Success (`200`):

```json
{
  "orderId": "b1c2d3e4-f5a6-7890-bcde-f12345678901",
  "amount": 15000.00,
  "currency": "ETB",
  "toPayUrl": "https://.../checkout?appid=...&prepay_id=...&sign=...",
  "openUntil": "2026-09-30T10:00:00.0000000Z",
  "isExisting": false
}
```

- `amount` is the order's server-side `orderTotal` (rounded to 2 decimals). The client sends
  no amount, no reference and no idempotency key.
- The server generates the Telebirr `merch_order_id`: `WCO` + the first 24 upper-case hex
  characters of SHA-256 over the order id (UTF-8) + a 3-digit attempt number. It is 30 ASCII
  letters and digits, and two different order ids never share one.
- **One open checkout per order.** While a checkout is open, a new request returns the same
  payment with a freshly signed URL and `isExisting: true`. It never creates a second payable
  preOrder. `openUntil` is 130 minutes after Telebirr answered: the preOrder's 120-minute
  `timeout_express` plus a 10-minute margin for clock skew and a payment accepted in the last
  minute.
- **Payment time.** No Telebirr payment is started more than 20 hours after the order
  entered `payment_submitted` (`paymentSubmittedAt`, `order-lifecycle.md`; `createdAt` for
  orders from before it was stamped, as the order cron does). This is checked right before
  every Telebirr call, also when an interrupted attempt is finished late. A checkout that is
  already payable is returned as usual. The order cron marks the order `payment_failed` 24
  hours after the same clock start, so no checkout can still be open when that happens.
- **Bound to the payment attempt.** Each checkout records the order's `paymentSubmittedAt`
  in its mapping. After `payment_failed` and a new `payment_submitted`, a payment made in an
  earlier checkout never funds the order (see the webhook table below).
- `toPayUrl` is transient: it is never persisted and appears in no other response. The
  `prepay_id` behind it is stored server-side only (the checkout slot row) so the URL can be
  signed again.
- Mock payments fund orders at `payment_submitted`, so there is nothing to open:
  `409 ESCROW_031`.

| Status | Error Code | Description |
|--------|-----------|-------------|
| `401` | `ESCROW_005` | Not authenticated |
| `403` | `ESCROW_025` | Caller's role claim is not `buyer` |
| `403` | `ESCROW_026` | Caller is not the order's buyer |
| `400` | `ESCROW_033` | `orderId` missing, or the body is not valid JSON |
| `404` | `ESCROW_033` | Order not found |
| `409` | `ESCROW_024` | The order is already funded |
| `409` | `ESCROW_030` | The order is not waiting for payment (status other than `payment_submitted`), or its payment time ran out (20 hours after `paymentSubmittedAt`) |
| `422` | `ESCROW_030` | The order cannot be paid online (missing farmer, or buyer and farmer are the same account) |
| `400` | `ESCROW_001` | `orderTotal` is outside what Telebirr takes in one payment (100 to 500,000 ETB) |
| `409` | `ESCROW_031` | No hosted checkout in this environment (mock payments) |
| `409` | `ESCROW_032` | A checkout for this order is already open but cannot be returned (Telebirr's answer was lost, or another request is opening it); retry after `openUntil` or in a moment |
| `429` | `ESCROW_011` | Payment rate limit exceeded (same velocity check as initiate). Every attempt that reaches Telebirr counts (a new checkout, or `ESCROW_013`); a returned open checkout does not |
| `502` | `ESCROW_013` | Telebirr refused the payment (retry now), or did not answer (the checkout stays blocked until its window ends) |
| `503` | `ESCROW_012` | Payments are temporarily unavailable |
| `403` | `FRAUD_ACCOUNT_HELD` | Caller's risk tier is CRITICAL; checked before any Telebirr call (1.7.0, planned FRD-04) |

#### How the app uses it (1.10.1, ESC-44)

- The app reads the order from the server first (`GET /orders/{id}`; on mobile only once its
  queued `payment_submitted` has synced). `funded` or later means paid and no checkout is
  opened. That is the normal mock result: mock payments fund the order during the sync.
- The checkout is requested only for a `payment_submitted` order. At most one automatic
  request per screen visit (the buyer has just paid or tapped "Continue payment"); it may
  happen on the first read after a queued payment syncs, which can be a poll, an app
  resume or a return link, and never a second time. After that only from a button (start
  a new payment after `openUntil`, retry after an error) or one retry when the network
  comes back after a request failed offline, so velocity only counts what the buyer asked
  for. Before starting a new payment the order is read again.
- "Open Telebirr again" while `openUntil` is ahead opens the URL the app already holds.
  Neither `toPayUrl` nor `openUntil` is stored on the device; after a restart the next
  request returns the open checkout (`isExisting: true`). The time left is `openUntil`
  minus the response's `Date` header, and the page ends at the first of a monotonic
  deadline and a device-clock deadline. Browsers hide `Date` on cross-origin responses, so
  web uses the device clock: a new page counts as open for at most the full window, a page
  returned again for at most 10 minutes (then a new request returns it again while it is
  open).
- Android opens Telebirr on its own once; web opens it only from a tap (a real link), as
  browsers block pop-ups after a network wait. The return link
  `weredaconnect://payment/complete` (Android) or coming back to the tab refreshes at once;
  a cold start from the return link checks the buyer's waiting orders.
- The order is polled with backoff (5 s rising to 30 s, at most 10 minutes after the last
  trigger, paused in the background) until it is `funded`, `payment_failed`, `cancelled`
  or `rejected`.
- `409 ESCROW_030` is told apart by reading the order again: still `payment_submitted`
  means the payment time ran out. `ESCROW_031` keeps polling for the mock funding.
  `ESCROW_032` and `ESCROW_013` keep polling (a payment may still land) and offer a retry;
  `ESCROW_032` can last until `openUntil` (up to about 130 minutes). Each code has its own
  message in en, am and om. No server change.

### How a checkout is recorded

Rows in the escrow idempotency table. Slot and mapping rows keep a 400-day TTL, far past any
checkout window, so a late notification still finds its order and a merch_order_id is never
reused:

| Key | Kind | Holds |
|---|---|---|
| `order-checkout:{orderId}` | slot | attempt, merch_order_id, amount, buyer, `outcome`, `openUntil`, `prepayId` |
| the merch_order_id | mapping | `escrowId = escrow-order-{orderId}`, orderId, buyerId, amount, `paymentSubmittedAt` (the order's payment attempt; absent for orders from before it was stamped), `status` |
| `webhook-inbox:{merchOrderId}:{paymentOrderId}:{tradeStatus}` | inbox | one verified notification and what became of it (30-day TTL) |

Slot outcomes: `opening` (written, Telebirr not answered yet), `url_issued`, `unknown`
(timeout, network error, or a non-success HTTP status: the payment may exist), `failed`
(Telebirr refused to create the payment), `paid`. A slot is open while its outcome is
`opening`, `url_issued` or `unknown` and `openUntil` has not passed. `openUntil` is written
with the slot and moved to 130 minutes after Telebirr's answer once it arrives.

The mapping is written **before** Telebirr is called and is never deleted, so a payment that
Telebirr confirms late still reaches its order. When Telebirr's answer is lost, the checkout
stays blocked until `openUntil` rather than risk a second payable payment.

### Webhook: order payments

A verified notification (signature, timestamp, merchant) whose merch_order_id has an order
checkout mapping takes the order path; any other merch_order_id keeps the `/escrow/initiate`
behaviour above.

1. The notification is written to its inbox row first. If the mapping lookup itself fails,
   or the mapping row is missing its order, buyer or amount, the notification is not
   dropped: `ORDER_PAYMENT_UNPROCESSED` is logged with the merch_order_id and
   payment_order_id, and it never falls through to the escrow path. That log line is then
   its only record. When processing fails after the mapping was read, the same token is
   logged and a best-effort `order_payment_unprocessed` escrow-audit row is written.
   A redelivery of a notification whose inbox row already says `unmatched:*` or `duplicate`
   keeps that verdict and is not applied again: the payment may already be refunded by hand.
2. Only `Completed` can fund. `Failed` is only audited (`order_payment_failed`): the checkout
   stays open until `openUntil`, because Telebirr may still accept the same payment (a wrong
   PIN or a low balance can be retried), and the buyer keeps getting the same URL back. The
   order stays `payment_submitted`, and a late `Failed` after the order was paid changes
   nothing. Other statuses are audited as `order_payment_status_ignored`.
3. `Completed` must carry a `payment_order_id`.
4. The order is then classified by payment reference first, in any status:

| Order state | Result | Inbox outcome |
|---|---|---|
| Order missing | `ORDER_PAYMENT_UNMATCHED`; audited as `order_payment_unmatched` | `unmatched:order_missing` |
| Already records this `payment_order_id` | Redelivery: stock and escrow are repaired if an earlier delivery did not finish them (both idempotent per order); nothing is funded twice, and the payment is not judged again (the buyer may have changed phone since). A closed order (`rejected`, `cancelled`, `payment_failed`, `disbursing`, `settled`, `dispute_resolved`) gets no repair: no new escrow, no second stock decrement | `applied_again` |
| Records a different `payment_order_id` | `ORDER_PAYMENT_DUPLICATE`: a second payment; audited as `order_payment_duplicate`, no state change | `duplicate` |
| No reference and not `payment_submitted` | `ORDER_PAYMENT_UNMATCHED` (for example a cancelled order); audited as `order_payment_unmatched` | `unmatched:status_{status}` |
| `payment_submitted`, but the payment does not fit | `ORDER_PAYMENT_UNMATCHED`: `trans_currency` is not `ETB`, `total_amount` differs from the checkout amount (also audited as `webhook_amount_mismatch`), the payer check (§Webhook hardening) against the checkout's buyer refuses it, the order's buyer or `orderTotal` no longer match the checkout, or the checkout was opened in an earlier payment attempt (its `paymentSubmittedAt` differs from the order's) | `unmatched:currency`, `:amount`, `:payer`, `:order_changed`, `:stale_checkout` |
| `payment_submitted` and the payment fits | Funded: one write conditioned on `status = payment_submitted` sets `funded`, `paymentVerifiedAt` and the server-only `telebirrMerchOrderId`, `telebirrPaymentOrderId`, `telebirrTransId`; then the listing stock is taken (idempotent per order), the order escrow is created with the Telebirr payment as its hold reference, a transaction-audit `payment_verified` row is written (actor `telebirr`), and the checkout is closed as `paid` | `applied` |

If the conditional write loses a race, the order is read again (strongly consistent) and
classified once more; a conflict is never treated as done. A payment that is not applied
stays with the platform for a manual refund (automatic duplicate refunds are ESC-20). Every
such case logs an alarmable token (`ORDER_PAYMENT_UNMATCHED`, `ORDER_PAYMENT_DUPLICATE`,
`ORDER_PAYMENT_UNPROCESSED`, plus `ORDER_ESCROW_CREATE_FAILED` and
`ORDER_STOCK_DECREMENT_FAILED` from the funding steps), watched by the
`{prefix}-order-payment-problems` alarm on the webhook log group. The two funding-step
tokens mean the order is funded and needs repair, never a refund. Before refunding any
payment, read the order: if its `telebirrPaymentOrderId` is that payment, the payment funded
the order. Do not refund an `ORDER_PAYMENT_UNPROCESSED` payment while its order is still
`payment_submitted` in the same attempt (the mapping's `paymentSubmittedAt` equals the
order's): a redelivery can still apply it. Wait until the order is funded by it, funded by
another payment, or out of that attempt.

`/escrow/initiate` also answers `409 ESCROW_024` when the delivery's order is in a funded
status (`funded` through `dispute_resolved`) or records a `telebirrPaymentOrderId`, even if
the order's escrow record is missing, so a buyer can never pay twice for one order.

Once the order is funded, a failure in the follow-up bookkeeping (the transaction-audit row,
closing the slot, marking the mapping, updating the inbox row) logs
`ORDER_PAYMENT_BOOKKEEPING_FAILED` instead. It is outside the alarm on purpose: refunding a
funded order would be wrong. A redelivery finishes the stock and escrow steps.

The webhook still answers `200` to every request. Telebirr's redelivery rules (retry policy,
whether a retry keeps the original timestamp, a query API) are not confirmed yet (ESC-45).
Until they are, the lasting record of a payment that was not applied is its escrow-audit row
(`order_payment_unmatched`, `order_payment_duplicate`, `order_payment_unprocessed`); the
inbox row expires after 30 days, and a payment whose mapping could not be read has only its
log line.

---

## Additions in 1.7.0 (fraud hold and risk tiers; planned FRD-03, FRD-04)

Design: `specs/architecture/fraud-detection-architecture.md` 3.0.0, sections 14 to 23. Nothing in
this section is deployed; it is the contract the FRD tasks implement. "The sweep" below means the
three hourly sweeps of `EscrowCronFunction` (`AutoReleaseAsync`, `AutoRefundAsync`,
`SettleResolvedDisputesAsync`), the only system paths that move money out.

### Risk tier on initiate and checkout
`POST /escrow/initiate` reads the caller's risk tier after the role check and before the step-up
and velocity checks. CRITICAL (or a truncated score read) returns `403 FRAUD_ACCOUNT_HELD` with
nothing held. HIGH requires a fresh Fayda login (`AUTH_041`) and then a fresh password
(`AUTH_040`) at any amount; MEDIUM requires the fresh password at any amount, after the Fayda login
above 10,000 ETB as today; LOW is unchanged.
`POST /escrow/order-checkout` applies only the CRITICAL check, before any Telebirr call (the
step-up gates already ran at the order's `payment_submitted`). The tier and the score are never
returned. The `AUTH_041` message becomes amount-neutral; clients key on the code.

### The `held` status
- **Entry** only by a settlement sweep, from `escrowed` or `dispute_resolved`, with `heldBy: system`,
  when a party (buyer, farmer, or the trucker when present) is suspended with
  `suspendReason: fraud_confirmed` (`holdReason: fraud_confirmed`), suspended for another reason
  (`account_suspended`), or at risk tier CRITICAL (`risk_critical`), in that precedence. Never at a
  user gate and never synchronously from an admin action.
- **Position in the sweep**: after the eligibility filters and before the first money-related side
  effect. For the auto-refund of an order escrow that is before the order is cancelled and its
  listing stock restored, so a held escrow never sits behind a cancelled order. For the auto-release
  it is before the payout resolution and its `release_blocked_kyc` audit entry.
- **Write**: conditional on `status = heldFromStatus AND attribute_not_exists(settlementLock)`; a
  settlement already in progress wins. Fields: `holdReason`, `heldAt`, `heldBy`, `heldFromStatus`,
  `heldFor` (`release` or `refund`). For an order escrow in the auto-refund sweep the hold also
  fences the order, as cancel-then-refund does today: one `TransactWriteItems` with a
  `ConditionCheck` that the order's `version` and `status` are still what the sweep read, so a
  farmer's accept in between cancels the hold and the next sweep pushes the expiry instead.
- **Exits**: admin group only, `POST /escrow/release` or `POST /escrow/refund`. No resume. A release
  resolves the payout as today. A refund depends on `heldFor`:
  - `heldFor: refund`: the steps the sweep skipped. For the auto-refund of an order escrow it cancels
    a still `funded` order and restores its stock first, and is refused with
    `422 ESCROW_INVALID_TRANSITION` while the order is progressing; a `buyer_wins` dispute escrow is
    refunded as the dispute sweep would; a delivery-initiated escrow is just refunded.
  - `heldFor: release`: the progressing-order rule does not apply, because otherwise a payout to the
    flagged party would be the only exit. The order takes the dispute path before the refund:
    `delivered` to `disputed` to `dispute_resolved`, or `disputed` to `dispute_resolved`. An order
    left at `accepted`, `pickup_confirmed` or `in_transit` behind its delivery is first advanced to
    `delivered` when its active delivery is `delivered`; otherwise, and for any other status or a
    missing order, the refund is refused with `422 ESCROW_INVALID_TRANSITION` and the alarm token
    `RISK_HOLD_ORDER_MISMATCH`. An order already `dispute_resolved` by this escrow's earlier attempt
    is left as it is. Each hop is a system-actor write, version-checked, with a transaction-audit
    row. Still before the refund, the escrow records `disputeResolution: buyer_wins`,
    `disputeResolvedBy` and `disputeResolvedAt` while it stays `held`; the refund then moves it
    `held` to `refunded` directly. Listing stock is not restored
    (the goods were delivered). A delivery-initiated escrow (no `orderId`) is refunded with no order
    write.
- **Settlement lock**: the lock requires the exact status the caller validated, from the settleable
  set `escrowed` and `dispute_resolved` (1.9.0, ESC-03: `dispute_resolved` added, `disputed` removed
  because a dispute settles only through a verdict), plus `held` for the admin routes only.
- **One active escrow per order**: `held` counts as active, so a second `/escrow/initiate` on the
  order still gets `ESCROW_024` and delivery linkage does not mint a second escrow. Linkage and
  refund scheduling require `escrowed` and do nothing on `held`.
- **Crons** never select `held`. Delivery linkage and the delivery-confirmation stamp keep requiring
  `escrowed`: a hold happens only once no logistics step is left (delivery confirmed, order
  `disputed`, terminal, or a stale `funded` order). Delivery-initiated escrows are held too, at
  auto-release (the sweep does not filter by source) or after their 7-day timeout. On a stale
  `funded` order whose escrow is held, the farmer's `accepted` is refused with `ORDER_VALIDATION`
  (`order-lifecycle.md` 1.9.0), since a delivery created after it could never link to the held
  escrow.
- `POST /escrow/dispute` on a held escrow is refused (`ESCROW_INVALID_TRANSITION`).
- **Visibility**: parties see `status: held` ("Under review"); `holdReason`, `heldBy`,
  `heldFromStatus` and `heldFor` appear in admin responses only. Party scoping is unchanged.
- **Audit**: `fraud_hold` (system), `fraud_hold_release` and `fraud_hold_refund` (admin) in
  escrow-audit and transaction-audit, tier and score in the metadata. `hold_success`,
  `hold_pending` and `hold_failed` remain the Telebirr hold.
- **Flag**: `FRAUD_HOLD_ENABLED`, default false. Off, a sweep that finds a trigger skips the escrow
  (status unchanged, no money, alarm token `RISK_SETTLEMENT_SKIPPED`) and retries next hour. A sweep
  that cannot read a party's tier or user row also skips the escrow for that run (no settlement) and
  logs the same token with its reason. With the flag on and no `FRAUD_SIGNALS_TABLE` configured the
  sweep logs `RISK_TABLE_UNCONFIGURED` once per run.
- **Client compatibility**: `held` is a value shipped clients do not parse (mobile throws, web shows
  `initiated`), as is `dispute_resolved` already. FRD-06 ships the tolerant parser and the
  "Under review" badge for mobile and web; the flag is turned on only after FRD-06 is on web and in
  both stores, and after older clients are protected on the escrow reads (`GET /escrow/{id}`,
  `GET /escrow/by-delivery/{deliveryId}`, `GET /escrow/history`): either a minimum app version gate
  that covers those reads (ESC-25 as written gates order commands only) or a server-side mapping
  that answers older clients with `escrowed` instead of `held`.

---

## Additions in 1.8.0 (fail-closed payouts and the manual payout queue, ESC-02)

### Gateway rule
A gateway without a payout API returns `Success = false` with `ErrorCode = PAYOUT_NOT_SUPPORTED`
from `ReleaseFundsAsync` and `RefundFundsAsync` and makes no call. It never reports a payout
it did not make, and a success always carries a non-empty `TelebirrRef`. The live H5/C2B
gateway (`TelebirrSandboxGateway`, used whenever `enableTelebirrLive` is set) does exactly
this until the B2B disbursement API is integrated; the mock gateway (`TELEBIRR_MOCK=true`)
is unchanged.

### What the service does with such an answer
Two gateway answers send a settlement to the **manual payout queue**: `PAYOUT_NOT_SUPPORTED`,
and `Success = true` with an empty `TelebirrRef` (reason `GATEWAY_REF_MISSING`, fail closed
instead of completing with `""`). Every other failure keeps the retryable failure path (1.10.0:
except an unknown outcome, which is queued as `GATEWAY_OUTCOME_UNKNOWN`).

- The escrow keeps its status (`escrowed`, `disputed` or `dispute_resolved`); clients see no
  new state. The only client change is the `ESCROW_038` message below.
- The settlement lock is **kept on purpose**: nothing else may release or refund the escrow
  until an admin has paid every leg. The stale-lock recovery step (1.10.0) skips a row whose
  `manualPayout` is `open`.
- The call returns `202 ESCROW_034` in the standard error envelope ("waiting in the manual
  payout queue"; this call made no payout, and for these two reasons nothing moved: 1.10.0 adds
  `GATEWAY_OUTCOME_UNKNOWN`, where an earlier gateway call may have paid). Later release or refund calls on that escrow return the same
  `202` at once, with no gateway call, no lock attempt and no audit row, so the hourly sweeps
  stay quiet. The cron counts them as `manualPayoutsWaiting` in its run summary and logs
  `ESCROW_MANUAL_PAYOUT_WAITING escrowId=...` at Information.
- If the queue write fails or throws, the service re-reads the row strongly consistent. A
  queue entry for the same operation means the write committed (for example a timeout after
  the commit) and counts as queued. Otherwise, for `PAYOUT_NOT_SUPPORTED`, nothing has moved and today's retryable failure
  applies (1.10.0: for `GATEWAY_REF_MISSING` the write is retried up to 4 times and the lock is never released) (`502 ESCROW_013`, lock released, audit `release_failed` or `refund_failed` with
  metadata `MANUAL_QUEUE_FAILED:<reason>`).
- Guards on the row: acquiring the settlement lock requires `attribute_not_exists(manualPayout)`,
  and any status write to `released` or `refunded` other than the queue close requires it too,
  so no gateway settlement can land on a queued escrow.
- Every cron status query on `StatusExpiresIndex` filters out rows with `manualPayout` and keeps
  paging through pages the filter emptied. Queued rows keep `escrowed` and their old
  `expiresAt`, so without the filter they filled the oldest-first window (100 rows) and no
  newer escrow was ever released or refunded. The auto-refund sweep also skips a queued
  escrow before the delivery stamp and the order cancel and restock.
- Disputes: `POST /escrow/dispute` refuses an escrow with an open manual payout
  (`409 ESCROW_038`), and the status write to `disputed` carries the same guard, so a dispute
  cannot race in after the queue entry. A delivery dispute through sync does not freeze a
  queued escrow and does not mirror the order to `disputed` either (the delivery itself keeps
  its `disputed` status); it writes `dispute_refused_manual_payout` (metadata
  `deliveryId=...;op=...`) to escrow-audit and logs `ESCROW_MANUAL_PAYOUT_DISPUTE_REFUSED`, so
  the admin sees it. The app (mobile and web) shows `ESCROW_038` with its own message in en,
  am and om.
- KYC-blocked payouts (`ESCROW_029`, `release_blocked_kyc`) do NOT enter the queue: paying an
  unverified party by hand would bypass the gate.
- The trucker leg follows the same rule: `PAYOUT_NOT_SUPPORTED`, or a success without a
  reference, queues it. When the farmer leg was paid by the gateway and only the trucker leg
  needs a manual payout, the farmer leg is seeded `paid` with its gateway reference
  (`paidBy = gateway`). If that
  queue write keeps failing after the farmer was paid, the escrow advances with
  `truckerPayoutPending = true` instead of stranding under a held lock (1.11.0: it stays
  `released`, and the hourly trucker retry pays or queues the leg).

### Row attributes (escrows table, flat)

| Attribute | Value |
|---|---|
| `manualPayout` | `open` while waiting; removed on close. Partition key of the sparse `ManualPayoutIndex` |
| `manualPayoutQueuedAt` | ISO time; sort key of the index (oldest first). Kept after close |
| `manualPayoutOp` | `release` or `refund`; equals the held `settlementLock` |
| `manualPayoutReason` | `PAYOUT_NOT_SUPPORTED`, `GATEWAY_REF_MISSING` or (1.10.0) `GATEWAY_OUTCOME_UNKNOWN`: the gateway may have paid (a timeout, a lost or unreadable answer, a call that threw, or a stale lock); the admin checks the portal before paying |
| `manualPayoutFromStatus` | escrow status when queued |
| `manualFarmerStatus`, `manualTruckerStatus`, `manualBuyerStatus` | `pending` or `paid`; only the legs of the operation exist |
| `manual{Leg}Ref`, `manual{Leg}PaidBy`, `manual{Leg}PaidAt` | portal or gateway reference, admin id (or `gateway`), time |

Legs and amounts: `release` pays `farmer` (`farmerPayout`) and, when `truckerPayout > 0`,
`trucker` (`truckerPayout`); `refund` pays `buyer` (`amount`). The commission has no leg.
Phones are never stored on the row; they are read from the profile when listing and completing.

### GET /admin/payouts/manual
Admin pool. Query `limit` (1 to 50, default 20) and `nextToken`. Response:

```json
{
  "payouts": [
    {
      "escrowId": "escrow-order-...",
      "orderId": "...",
      "deliveryId": "...",
      "op": "release",
      "reason": "PAYOUT_NOT_SUPPORTED",
      "escrowStatus": "escrowed",
      "fromStatus": "escrowed",
      "queuedAt": "2026-10-06T08:00:00Z",
      "currency": "ETB",
      "pendingAmount": 19500,
      "legs": [
        {
          "ref": "farmer",
          "payeeRole": "farmer",
          "payeeId": "...",
          "payeeName": "...",
          "payeePhone": "+2519...",
          "payeeVerified": true,
          "blockReason": null,
          "amount": 16500,
          "status": "pending",
          "externalRef": null,
          "paidBy": null,
          "paidAt": null
        }
      ]
    }
  ],
  "nextToken": null,
  "total": 1
}
```

`payeeVerified` applies the same identity gate as the gateway release: farmer and trucker
need a verified phone and, above 10,000 ETB (judged on the whole held amount for the
farmer, on the transport leg for the trucker), a verified Fayda; the buyer only needs a
profile with a phone. Errors: `400 ESCROW_023` invalid token, `503 ADMIN_004` tables not
configured.

### POST /admin/payouts/{escrowId}/{ref}/complete
Admin pool **and** the `admin` group (`cognito:groups`); a role claim alone gets
`403 ESCROW_015`. `ref` is `farmer`, `trucker` or `buyer`. Body `{"externalRef": "<Telebirr
merchant-portal transaction id>"}`: trimmed, 4 to 64 characters, a letter or digit first,
then letters, digits, `.`, `_`, `:`, `/` or `-`.

The call records one paid leg. The last pending leg closes the queue in one conditional
write: the escrow moves to `released` with `telebirrReleaseRef` (farmer reference) and
`truckerTelebirrRef` (when a trucker leg exists), then to `completed`; or to `refunded`
with `telebirrRefundRef`. The gateway is never called. A crash between `released` and
`completed` is recovered by the cron's `CompleteReleasedAsync` sweep or by replaying the
same call. Response:

```json
{
  "escrowId": "...",
  "orderId": "...",
  "ref": "trucker",
  "legStatus": "paid",
  "escrowStatus": "completed",
  "externalRef": "TB2026...",
  "paidAt": "2026-10-06T09:00:00Z",
  "remainingLegs": [],
  "message": "Payout recorded. The escrow is completed.",
  "alreadyRecorded": false
}
```

`alreadyRecorded` is `true` on a replay of a leg already recorded with the same reference:
nothing new was paid or settled, and no second transaction-audit row is written.

| Status | Code | When |
|---|---|---|
| `200` | | leg recorded; a replay with the same reference is idempotent |
| `400` | `ESCROW_037` | `externalRef` missing or malformed (field `externalRef`) |
| `400` | `ADMIN_005` | body is not JSON |
| `403` | `ESCROW_015` | caller not in the admin group |
| `404` | `ESCROW_016` | escrow not found |
| `404` | `ESCROW_035` | unknown leg, leg not part of this operation, or no open manual payout |
| `409` | `ESCROW_036` | this leg already carries a different reference, the reference was used for another leg of this escrow, or the row changed three times while recording |
| `409` | `ESCROW_029` | the payee fails the identity gate (409, not 403, so the admin app keeps the session) |
| `409` | `ESCROW_INVALID_TRANSITION` | the escrow is no longer in a settleable status |
| `503` | `ADMIN_004` | escrow tables not configured |

Two admins recording the two release legs at the same time are safe: the second write fails
its condition, the handler re-reads (strongly consistent), sees it is now the last leg, and
closes.

Each list item also carries `deliveryConfirmedAt` (null when the delivery was never confirmed).
On a refund queued before the delivery was confirmed, the admin page warns that the goods
arrived after the refund was queued. The page reads 50 items per page and loads the next page
(`nextToken`) on demand.

A `GATEWAY_REF_MISSING` leg is different from a `PAYOUT_NOT_SUPPORTED` one: the gateway said
it paid, so the money has probably moved. The admin page says so on the row ("find it in the
portal and record its ID; pay only if it is missing") and asks the admin to confirm the
payment is in the portal, not to pay it.

### POST /admin/disputes/{id}/resolve
Refuses with `409 ADMIN_013` when the escrow has an open manual payout, so a verdict cannot
contradict a settlement already waiting for the admin. The full endpoint is in Additions in 1.9.0.

### Audit
escrow-audit: `manual_payout_queued` (from = to = current status, metadata
`op=...;legs=...;reason=...`), `manual_payout_paid` (telebirrRef = the entered reference,
metadata `leg=...;amount=...;payee=...`), then `release` + `complete` or `refund` with
metadata `manual`. These rows sit at deterministic keys, written with a conditional put
(`attribute_not_exists(escrowId)`, still append-only): `manual_payout_queued` at
`manualPayoutQueuedAt`, `manual_payout_paid` at the leg's `paidAt`, `release` or `refund` at
the closing leg's `paidAt` plus one 100 ns tick, and `complete` at `completedAt`. The escrow-cron
crash recovery writes its `complete` row under the same `completedAt` key, so whichever writer
completes the escrow, there is one `complete` row. A replay of the same reference re-ensures all
of them, so a crash between the money write and its audit row is repaired without a duplicate.
A failed `complete` audit write never changes the answer: the response reports the escrow's
real status. A refused completion on an existing escrow (`ESCROW_035`, `ESCROW_036`,
`ESCROW_029`, `ESCROW_INVALID_TRANSITION`) writes `manual_payout_refused` (metadata
`leg=...;code=...;attemptedRef=...`); a failed write of that row never changes the answer.
transaction-audit: `admin_manual_payout` (metadata `leg=...;ref=...`).

Log lines: `ESCROW_MANUAL_PAYOUT_QUEUED`, `ESCROW_MANUAL_PAYOUT_PAID` and
`ESCROW_MANUAL_PAYOUT_QUEUE_FAILED` from the service, `ESCROW_MANUAL_PAYOUT_WAITING` from the
cron (informational, no alarm counts it).

### Overdue alarm producer (ESC-13)
The hourly escrow-cron run first reads `ManualPayoutIndex` (oldest first, 50 per page, at
most 20 pages) and logs one line per pending leg of every manual payout queued more than 24
hours ago, in the ESC-13 format:

```
MANUAL_PAYOUT_OVERDUE escrowId=<id> ref=<op>:<leg> ageHours=<n>
```

for example `MANUAL_PAYOUT_OVERDUE escrowId=escrow-order-ord-1 ref=release:farmer ageHours=26`.
An open row with no pending leg left (it cannot close from the admin page) logs
`ref=<op>:no_pending_leg`. Paging stops at the first row younger than 24 hours. The report runs
before the settlement sweeps and on its own, so a failing sweep cannot skip it; a failed
index query logs `ESCROW_SETTLEMENT_FAILED escrowId=- action=sweep_failed outcome=failed
code=manual_payout_query:<exception type>`. The run summary carries `manualPayoutsOverdue`
(lines logged).

### Reserved: manual refund of an unapplied payment
`account-deletion.md` keeps a buyer's `payment_review` blocker until a manual refund is
recorded for a captured payment the webhook did not apply (`order_payment_unmatched`,
`order_payment_duplicate`, `order_payment_unprocessed` under `escrow-order-{orderId}`). The
resolution row is reserved here: escrow-audit action `order_payment_refunded_manual` under
the same `escrow-order-{orderId}` key, `telebirrRef` = the merchant-portal transaction id,
metadata `paymentOrderId=<Telebirr payment_order_id>;amount=<n>`, actor = the admin id. 1.8.0
has no endpoint that writes it; a follow-up task adds it (or ESC-20, which owns automatic
duplicate refunds).

### Not in 1.8.0 (known limits)
- No cancel or reassign of a queued payout, no cross-escrow uniqueness of references and no
  verification of the reference against Telebirr (no API).
- No claim step: two admins can both pay the same leg in the merchant portal before either
  records it. The second record is refused (`ESCROW_036`, audited as
  `manual_payout_refused`), but the second payment has already left; it is recovered by hand.
  One admin works the queue at a time until a claim step exists.
- A leg the identity gate blocks (`ESCROW_029`: an unverified trucker, an escrow without
  `truckerId`, a buyer without a profile or phone) cannot be recorded and has no unqueue path.
  It stays in the queue and keeps `${prefix}-manual-payouts-overdue` in ALARM until the payee
  verifies. 1.11.0: a trucker leg recorded on an escrow without `truckerId` takes the order's
  trucker when the delivery names the same one, and the list shows that trucker.
- FRD-03 (`held`, 1.7.0): a queued escrow keeps its settlement lock, so the hold write
  (`attribute_not_exists(settlementLock)`) never holds it, and the cron queries already skip
  it. The reverse case is open: when FRD-03 lets an admin settle a `held` escrow and the live
  gateway queues that settlement, `held` must join the statuses a queued payout may close from
  (`escrowed`, `disputed`, `dispute_resolved`), or the queue close fails its condition and the
  leg can never be recorded. Any other later sweep that moves escrows by status must skip rows
  with `manualPayout = open` the same way.
- A leg that stays blocked keeps `${prefix}-manual-payouts-overdue` in ALARM for as long as it
  waits. While the alarm is already in ALARM, a new overdue payout does not change its state,
  so it is not announced separately: read the `MANUAL_PAYOUT_OVERDUE` lines or the admin page
  (oldest first) rather than relying on a fresh alarm transition.
- ESC-20 (ledger) maps the legs to `release#farmer`, `release#trucker#{id}` and `refund#all`
  and replaces this row-level queue.

---

## Additions in 1.8.1 (money alarms, ESC-13)

Before 1.8.1 a failed release, refund or trucker leg was only an escrow-audit row, a crashed
sweep only a Lambda log line, and the only alarm over the money paths counted Lambda errors,
which the sweeps never raise because their catches answer 500. Nothing told an operator that
money had stopped moving. The server now logs one plain-text token per failure and the
monitoring stack counts the tokens.

### Log tokens

One line each, written by `MoneyAlarmLog` (`server/src/Helpers/MoneyAlarmLog.cs`). Fields are
reduced to `[A-Za-z0-9-_.:]`, cut at 64 characters, and an empty field is `-`, so a value
cannot add a second field or break the line. Ids (`escrowId`, `orderId`) also turn `_` into
`-`; action, outcome, code and ref are server values.

Spoofing: the filters count any line of their log group that contains a token, so no client
text may spell one. Every token contains `_`, and every client-influenced value logged in the
escrow, escrow-cron, sync and telebirr-webhook log groups (ids, path parameters, headers,
error and exception messages that may echo input, the ids in the JSON audit line) has its `_`
turned into `-` (`MoneyAlarmLog.LogId`, `LogText`, `Defang`). The escrow Lambda logs the route
template, not the request path, and defangs it too. Two sources stay unsanitized because no
client reaches them: escrow-cron's `Cron action` line (escrow-cron has only EventBridge
triggers, so only an IAM principal picks the action) and the message the Lambda runtime prints
for an unhandled exception (the handlers catch client-reachable exceptions and log a type name
or `LogText`).

```
ESCROW_SETTLEMENT_FAILED escrowId=<id> action=<action> outcome=<failed|unknown> code=<code>
MANUAL_PAYOUT_OVERDUE escrowId=<id> ref=<ref> ageHours=<n>
TRUCKER_PAYOUT_OVERDUE escrowId=<id> reason=<reason> ageHours=<n>   (1.11.0)
ADMIN_QUEUE_OVERDUE orderId=<id> reason=<code> ageHours=<n>
```

`outcome=failed` is a definite gateway refusal: no money moved. `outcome=unknown` means
Telebirr may already have paid (a timeout, a lost or unreadable answer, an exception after
the gateway call): check the merchant portal before any manual release or refund.

`ESCROW_SETTLEMENT_FAILED` sources:

| Where | action | outcome | code |
|---|---|---|---|
| `EscrowService`, with the `release_failed`, `refund_failed` and `trucker_release_failed` audit rows | the audit action | `unknown` when the code is `TELEBIRR_TIMEOUT`, `TELEBIRR_NETWORK`, `TELEBIRR_PREORDER`, `GATEWAY_EXCEPTION:<type>` (1.10.0) or `MANUAL_QUEUE_FAILED:<reason>` for any reason but `PAYOUT_NOT_SUPPORTED` (1.10.0; before: only `GATEWAY_REF_MISSING`), else `failed` | the gateway error code; `MANUAL_QUEUE_FAILED:<reason>` when the manual payout queue write failed (1.8.0); before 1.11.0 `-` when the trucker leg was skipped for an unverified trucker (since 1.11.0 that is `trucker_release_blocked_kyc`, not counted) |
| escrow-cron, exception while settling one escrow | `auto_release_exception`, `auto_refund_exception`, `dispute_settlement_exception` | `unknown` when the exception escaped after the settlement lock, `failed` before it (1.10.0) | exception type name |
| escrow-cron, the `dispute_resolved` query failed (run skipped) | `sweep_failed` | `failed` | `dispute_query:<exception type>` |
| escrow-cron, the `ManualPayoutIndex` query of the overdue report failed (1.8.0) | `sweep_failed` | `failed` | `manual_payout_query:<exception type>` |
| escrow-cron, the whole run died | `sweep_failed` | `unknown` after a settlement lock, else `failed` (1.10.0) | `<action>:<exception type>` |
| `POST /escrow/release`, `POST /escrow/refund`, unhandled exception | `release_exception`, `refund_exception` | `unknown` after the settlement lock, `failed` before it (1.10.0) | exception type name |
| escrow-cron, the release, refund or crash-recovery query failed (1.10.0) | `sweep_failed` | `failed` | `release_query`, `refund_query` or `complete_query:<exception type>` |
| escrow-cron recovery step (1.10.0) | `stale_lock_parked`, `stale_lock_recovery_failed`; `sweep_failed` for its query | `unknown`; `failed` for the query | the locked op; exception type; `lock_recovery_query:<status>:<type>` |

Who retries (1.10.0): after a line with `outcome=failed` the lock is released and the next hourly
sweep retries (`release_failed`, `refund_failed`), or the escrow completes with
`truckerPayoutPending` (`trucker_release_failed`): since 1.11.0 it stays `released` and the hourly
trucker retry pays the leg with the same reference; do not pay the trucker by hand. After a line with `outcome=unknown` nothing retries: the escrow keeps its
lock and waits in Manual Payouts as `GATEWAY_OUTCOME_UNKNOWN` (or `GATEWAY_REF_MISSING`), at once or
through the recovery step; check the portal before paying (Additions in 1.10.0).

Not counted on purpose: `release_blocked_kyc` and (1.11.0) `trucker_release_blocked_kyc` (a known
wait for the farmer or the trucker to verify; the trucker's is reported by `TRUCKER_PAYOUT_OVERDUE`
after 24 hours), a lock-held
`ESCROW_INVALID_TRANSITION` answer (no gateway call was made), a failed `complete`
step (the money already moved) and the reconciliation of `initiated` escrows (funding side).
A settlement queued for a manual payout (1.8.0) is not a failure either: `202 ESCROW_034`,
audit `manual_payout_queued`, log lines `ESCROW_MANUAL_PAYOUT_QUEUED` and, on later sweeps,
`ESCROW_MANUAL_PAYOUT_WAITING`; nor are `manual_payout_refused` and the admin-side
`ESCROW_MANUAL_PAYOUT_*` bookkeeping lines. The line is written before the audit row, so a
failed audit write still alarms. Exception messages are never logged, only the type name.

### Queue tokens

`MANUAL_PAYOUT_OVERDUE` is logged by the hourly escrow-cron run, once per run for each
pending leg of a manual payout (1.8.0) queued more than 24 hours ago
(`MoneyAlarmLog.QueueAlarmAfter`), with `ref=<op>:<leg>` (see "Overdue alarm producer
(ESC-13)" in 1.8.0). `ADMIN_QUEUE_OVERDUE` is logged the same way for each admin stuck-order
queue entry once ESC-06 builds that queue; until then its alarm is wired and stays at zero.
Both must be logged from the escrow-cron Lambda, because their metric filters sit on its log
group; a producer in another Lambda (order-cron, for example) adds its own filter on that
Lambda's log group publishing the same metric.

The overdue line is a `LogWarning`: a log level above Warning on escrow-cron silences the
manual payout alarm.

### Alarms (`infra/lib/stacks/monitoring-stack.ts`, all on the `${prefix}-alarms` topic)

| Alarm | Counts | Fires |
|---|---|---|
| `${prefix}-escrow-settlement-failures` | `ESCROW_SETTLEMENT_FAILED` in the escrow and escrow-cron log groups | any line in a 15 minute period, evaluated over 5 periods (75 minutes, so an hourly producer never leaves a gap); stays in ALARM while the hourly sweep keeps failing and clears by itself 75 to 90 minutes after the last line |
| `${prefix}-escrow-cron-errors` | AWS/Lambda `Errors` of `${prefix}-escrow-cron` | one error in a 5 minute period, evaluated over 4 periods (a crash and its two async retries stay one episode; clears 20 to 25 minutes after the last error): escrow-cron was killed (5 minute timeout, out of memory) or crashed. A run killed mid-settlement logs no token, so this alarm is the only signal; treat the escrows it was processing as unknown outcomes. The 1.10.0 recovery step parks an escrow whose lock is older than 15 minutes |
| `${prefix}-escrow-settlement-unknown-outcomes` (1.10.0) | `ESCROW_SETTLEMENT_FAILED` lines with `outcome=unknown` in the escrow and escrow-cron log groups | same as settlement failures; the escrow is or will be in Manual Payouts as `GATEWAY_OUTCOME_UNKNOWN`: never release or refund it by hand |
| `${prefix}-order-escrow-problems` | `ORDER_ESCROW_CREATE_FAILED`, `ORDER_ESCROW_LINK_FAILED`, `ORDER_ESCROW_REFUND_SCHEDULE_FAILED` in the sync log group (replaces `${prefix}-order-escrow-create-failed`) | any line in 15 minutes |
| `${prefix}-manual-payouts-overdue` | `MANUAL_PAYOUT_OVERDUE` in the escrow-cron log group | any line in 15 minutes, same hold and clear as settlement failures; a leg that stays blocked keeps it in ALARM (1.8.0 known limits). The admin acts on the Manual Payouts page (`/payouts`) |
| `${prefix}-trucker-payouts-overdue` (1.11.0) | `TRUCKER_PAYOUT_OVERDUE` in the escrow-cron log group | same as manual payouts overdue: a trucker leg still owed (not queued) more than 24 hours after it went pending; see Additions in 1.11.0 |
| `${prefix}-admin-queue-overdue` | `ADMIN_QUEUE_OVERDUE` in the escrow-cron log group | same |

`scripts/money_alarms_synthetic.sh` checks that the deployed filters match these exact lines
and, with `--fire`, that every alarm reaches ALARM from synthetic lines plus one failed
escrow-cron invocation, without moving money (see `.ai/deployment.md`, Monitoring).

## Additions in 1.9.0 (dispute verdicts settle, ESC-03)

Before 1.9.0 no dispute verdict ever settled: the settlement lock accepted only `escrowed` and
`disputed`, so the hourly sweep and every admin release or refund of a `dispute_resolved`
escrow stopped at the lock. The verdict endpoint also accepted any string, left the order
`disputed` for good, and an admin could settle a verdict the wrong way.

### Settlement lock
`POST /escrow/release`, `POST /escrow/refund` and the three sweeps lock with a condition on the
exact status the service validated (`#status = :expected`), never a set of statuses, so a stale
read cannot lock a row that moved on. The settleable statuses are `escrowed` and
`dispute_resolved`. A `disputed` escrow is refused with `422 ESCROW_INVALID_TRANSITION`
("record the verdict first"): no caller settles a dispute without a verdict any more. A queued
payout that an older release or refund left on a `disputed` row still closes (the queue close
keeps `disputed` in its set).

### The verdict binds the settlement
On a `dispute_resolved` escrow, `farmer_wins` may only be released and `buyer_wins` only
refunded; the other call gets `422 ESCROW_INVALID_TRANSITION`, before any idempotency record,
lock or gateway call. The sweep already settled that way. A row recorded before 1.9.0 with
another value (`partial_refund`) has no direction: the admin may release or refund it.

### POST /admin/disputes/{id}/resolve
Admin group only (`cognito:groups` contains `admin`), like the manual payout queue, because the
sweep settles a verdict within the hour.

```json
{ "resolution": "buyer_wins", "action": "refund", "notes": "optional, not stored" }
```

| Field | Rule |
|-------|------|
| `resolution` | `farmer_wins` or `buyer_wins`, exact. `partial_refund` is refused until per-leg settlement (ESC-20) |
| `action` | `release` for `farmer_wins`, `refund` for `buyer_wins` |

The write is conditional on `status = disputed`, no open manual payout and no settlement lock.
When the condition fails the handler re-reads the escrow (strongly consistent):

| Escrow after the failed write | Response |
|-------------------------------|----------|
| No longer `disputed` and carries the same `disputeResolution` | `200`, a replay: the escrow's current `status`, the recorded `resolvedAt`, message "Verdict already recorded."; no new audit rows; the order and delivery are mirrored again |
| Open manual payout | `409 ADMIN_013` (1.8.0) |
| `disputed` with a settlement lock | `409 ADMIN_010` "A settlement is in progress for this escrow." |
| Another verdict recorded | `409 ADMIN_010` "A different verdict is already recorded for this escrow." |
| Anything else | `409 ADMIN_010` "Escrow is not in a disputed state." |

Errors: `400 ADMIN_005` (body is not JSON or misses `resolution` or `action`), `400 ADMIN_014`
(`resolution` or `action` invalid, field named), `403 ESCROW_015` (not in the admin group),
`404 ADMIN_003`, the `409` rows above. The response body is unchanged.

### The order and the delivery follow the verdict
After the verdict, and again before a `dispute_resolved` escrow is released or refunded (sweep
or admin), the server moves the order and the delivery behind the escrow to the verdict
(`DisputeVerdictMirror`). The second run repairs a leg the first could not write and is a no-op
otherwise. The order id comes from the escrow, else from its delivery; the delivery id from the
escrow, else the order's `activeDeliveryId`.

- Order (`order-lifecycle.md` 1.12.0): `delivered` to `disputed` to `dispute_resolved`, or
  `disputed` to `dispute_resolved`. An order left at `accepted`, `pickup_confirmed` or
  `in_transit` behind a delivery that is `delivered` or `disputed` is first advanced to
  `delivered`. Each hop is a version-checked system write; a version conflict re-reads
  (strongly consistent) and retries, and the third conflict of a run gives up.
- Delivery (`delivery-verification-api.md` 1.8.0): a `delivered` delivery is marked `disputed`;
  a `disputed` delivery gets `disputeResolution` and `disputeResolvedAt`, conditional on no
  verdict yet. Its status stays `disputed`.
- The delivery that counts is the order's `activeDeliveryId` when it has one (the escrow may
  still name an earlier, cancelled delivery), else the escrow's `deliveryId`.
- Both legs are decided before anything is written. A read that fails before both legs are
  decided fails closed: release and refund answer `503 ESCROW_012` with nothing locked or paid,
  and the sweep skips the escrow until its next run (so does a `buyer_wins` cancel or restock that
  did not land, below). Each blocked attempt logs `DISPUTE_VERDICT_SETTLEMENT_BLOCKED escrowId=..
  op=.. legs=..` (legs `read:[Error]`, `cancel:<reason>` or `restock:<reason>`), counted on the
  escrow-cron log group by the `${prefix}-dispute-verdict-settlement-blocked` alarm, which fires
  only when the line is in three of four hourly periods (period 1 hour, 3 of 4 datapoints, so the
  hourly sweep's jitter leaving one clock-hour empty does not hide a lasting block; missing
  data not breaching), and writes one escrow-audit row `dispute_settlement_blocked` (metadata
  `legs=...;op=...;at=...`, timestamp `disputeResolvedAt` plus three ticks, conditional put), so
  the admin timeline shows why the settlement waits. A write that fails after both legs were
  decided (`conflict`, an error code) does not block the money; a later run or an admin replay of
  the verdict repairs it (`DISPUTE_VERDICT_PROPAGATION_FAILED`, no alarm).
- What each verdict needs from the order:

  | Verdict | Order (and its delivery) | Settlement |
  |---------|--------------------------|------------|
  | either | `delivered`, `disputed`, `dispute_resolved`, or left at `accepted`/`pickup_confirmed`/`in_transit` behind a `delivered` or `disputed` delivery | the order moves to `dispute_resolved`, then the money moves |
  | `buyer_wins` | `cancelled` or `rejected` | nothing to move: the settlement makes sure the listing stock is back (idempotent per order; a restore that does not land refuses the refund for that run), then refunds |
  | `buyer_wins` | before pickup: `funded`, or `accepted` with no delivery or a `pending` one | the refund settlement (sweep or admin) first cancels the pending delivery, then the order (`cancelReason: escrow_refunded`, version-checked), restores its listing stock (idempotent per order), then refunds, as the auto-refund sweep's cancel-then-refund does; a cancel or stock restore that does not land (an error, three version conflicts, an exception) refuses the refund for that run with `503 ESCROW_012`, and the next run, finding the order closed, retries the restore before refunding; the admin verdict itself leaves all of this to the settlement |
  | `buyer_wins` | goods moving: `pickup_confirmed`/`in_transit`, or a delivery picked up but not delivered | refused (`422`), unless an admin in the admin group refunds with `overrideOrderMismatch: true` and a `reason` of 10 to 500 characters; the sweep never overrides |
  | `farmer_wins` | before delivery (`funded`, `accepted`, `pickup_confirmed`, `in_transit` behind an undelivered delivery) or closed | refused (`422`), no override; it settles by itself once the order reaches `delivered` |
  | either | anything else, a missing order or delivery, or a delivery with another verdict | refused (`422`) |

  A refusal answers `422 ESCROW_INVALID_TRANSITION` ("The order or delivery behind this verdict
  cannot follow it (legs). Nothing was settled ...") before any lock, idempotency record or gateway
  call; the escrow stays `dispute_resolved` and the sweep tries again every hour. **The admin
  brings the order (and its delivery) to a status the verdict can follow, or, for `buyer_wins`,
  overrides.** Each refusal logs `DISPUTE_VERDICT_ORDER_MISMATCH escrowId=<id> op=<release|refund>
  legs=<legs>` (legs defanged, `,` as `.`), counted by the `${prefix}-dispute-verdict-order-mismatch`
  alarm on the escrow and escrow-cron log groups (15 minute periods, 5 periods, any line alarms).
- The override writes, before any money moves, the escrow-audit row
  `dispute_order_mismatch_overridden` (actor = the admin, metadata `legs=...;reason=...`; if this
  write fails the refund is refused with `503 ESCROW_012`), a transaction-audit row
  `dispute_mismatch_override` (entity `escrow`, the admin as actor) and the log line
  `DISPUTE_VERDICT_MISMATCH_OVERRIDDEN escrowId=.. admin=.. legs=..`; the order keeps its status and
  is flagged with `disputeOverrideAt`, `disputeOverrideBy`, `disputeOverrideReason` and
  `disputeOverrideEscrowId` (best-effort, version-checked).
- Escrow audit for a leg that does not reach the verdict, from = to = `dispute_resolved`:
  `dispute_verdict_mismatch` (it cannot follow) or `dispute_verdict_propagation_failed` (a read or
  write failed), metadata `legs=order:<reason>,delivery:<reason>` (reasons `mismatch:<status>`,
  `verdict:<other verdict>`, `missing`, `conflict`, `read:[ExceptionType]`, an error code or
  `[ExceptionType]`), then `;op=<op>` on a settlement and `;at=<time>`. One row per escrow and
  action: the timestamp is the verdict's `disputeResolvedAt` plus one tick (mismatch) or two
  (propagation), written with a conditional put, so it sorts right after the verdict's own row and
  the hourly sweep never adds another.
- transaction-audit, actor `system`, metadata `resolution=<verdict>`: order `order_updated`
  (the advance to `delivered`), `dispute`, `dispute_resolved`; delivery `dispute`,
  `dispute_resolved` (from = to = `disputed`). The escrow and escrow-cron Lambdas now write
  transaction-audit (`TRANSACTION_AUDIT_TABLE`, `PutItem` only); the admin Lambda, the escrow-cron
  Lambda and the escrow Lambda may `UpdateItem` orders and deliveries.

### Disputes and a settlement in flight
`escrowed` to `disputed` (`POST /escrow/dispute`, and a delivery dispute through sync) now also
requires that no settlement holds the lock. The settlement wins: `POST /escrow/dispute` answers
`409 ESCROW_INVALID_TRANSITION` "A settlement is in progress for this escrow; it can no longer be
disputed." and frees its idempotency key. A delivery dispute through sync is recorded on the
delivery, but the escrow is not frozen and the order is not moved to `disputed` (as for a queued
payout, 1.8.0); the escrow audit gets `dispute_refused_settlement_in_flight` (metadata
`deliveryId=<id>`) and the sync Lambda logs `ESCROW_DISPUTE_REFUSED_SETTLEMENT_IN_FLIGHT escrowId=<id>`.
Before 1.9.0 the dispute could land between the lock and the release's status write, which then
failed after the money had moved.

### Active escrows
`dispute_resolved` counts as active, like `escrowed` and `disputed`: the money is still held until
the verdict is settled. A second `/escrow/initiate` on the delivery gets `409 ESCROW_009` (or
`ESCROW_024` for the order-funded record), the order-level double-charge guard answers
`ESCROW_024`, and delivery linkage does not mint a second escrow for the order.

### Deploying 1.9.0
Every `dispute_resolved` escrow with `farmer_wins` or `buyer_wins` that piled up before 1.9.0 is
settled by the first hourly sweep after the deploy (100 rows per run, oldest `expiresAt` first).
With the live gateway each one enters the manual payout queue and, after 24 hours, the
`MANUAL_PAYOUT_OVERDUE` alarm. A verdict whose order cannot follow it is refused instead and raises
the new mismatch alarm every hour until the admin fixes the order. Count them per environment
before deploying.

### Not in 1.9.0 (known limits)
- No admin action advances an order whose delivery is stuck behind it, or cancels an order whose
  goods are moving; a `farmer_wins` before delivery waits for the delivery, and a `buyer_wins` with
  the goods moving needs the override.
- `POST /escrow/dispute` still accepts a dispute before the delivery is confirmed (no
  `deliveryConfirmedAt`, so no window applies). Whether it should be refused is an open policy
  question.
- The dispute sweep reads the oldest 100 `dispute_resolved` rows per run with no paging past rows it
  cannot settle (mismatched or identity-blocked verdicts); ESC-10 owns that.

---

## Additions in 1.10.0 (no double pay, no stuck lock, ESC-05)

Before 1.10.0 a release or refund whose gateway call timed out, lost its answer or threw released
the settlement lock (or left it held forever), and the next hourly sweep called the gateway
again: a blind retry that pays twice unless Telebirr dedupes the reference. A crash after the
lock left the lock held for good; every later sweep answered 409 and nobody was told. The refund
status write had no retry at all.

### The rule
Every outcome that may have moved money ends in the manual payout queue (1.8.0) with the
settlement lock held: at once when the queue write lands, otherwise through the recovery step
below within about 75 minutes. No path releases the lock after the gateway may have paid, and
no sweep calls the gateway for a locked or queued escrow. Only a definite refusal releases the
lock, and only then does the next sweep retry.

### Gateway answers

| Answer | What the service does |
|---|---|
| Success with a reference | settles (unchanged) |
| `PAYOUT_NOT_SUPPORTED` | queues (1.8.0); a failed queue write still releases the lock, nothing moved |
| Success without a reference | queues `GATEWAY_REF_MISSING` (1.8.0); the queue write is retried up to 4 times and the lock is never released (FUP-03) |
| `TELEBIRR_TIMEOUT`, `TELEBIRR_NETWORK`, `TELEBIRR_PREORDER`, no answer at all (`TELEBIRR_NETWORK`), or the call threw (`GATEWAY_EXCEPTION:<exception type>`) | audit `release_failed`, `refund_failed` or `trucker_release_failed` with that code (alarm line `outcome=unknown`; the audit write is best-effort, the queue write follows anyway), then queue with reason `GATEWAY_OUTCOME_UNKNOWN`, lock kept, `202 ESCROW_034` |
| Any other failure code | a definite refusal: the lock is released first, then the idempotency record, then the audit row (`outcome=failed`); `502 ESCROW_013`; the next sweep retries |

Rule for every gateway (`TelebirrGatewayErrors`): a failure that may have moved money (no answer,
a lost, unreadable or incomplete answer) returns `TELEBIRR_TIMEOUT` or `TELEBIRR_NETWORK`, or
throws. The live gateway's preOrder now does so for an unreadable 200 answer or a success without
`prepay_id` (before: `TELEBIRR_ERR` or `TELEBIRR_0`, read as a definite refusal by the checkout).

Release legs: an unknown farmer outcome queues the farmer leg (and a trucker leg when
`truckerPayout > 0`) as `pending`; the trucker is not called. An unknown trucker outcome after
the farmer was paid queues the farmer leg `paid` (`paidBy = gateway`, its reference) and the
trucker leg `pending`. If that queue write keeps failing, a `PAYOUT_NOT_SUPPORTED` trucker leg
still falls back to `truckerPayoutPending` (1.8.0, nothing moved for the trucker); any other
reason keeps the lock and never completes, so `truckerPayoutPending` only ever means a definite
refusal or an unverified trucker, which is why 1.11.0 may retry it. Refund: the buyer leg.

When an unknown outcome cannot be queued after 4 attempts, the call answers `502 ESCROW_013`
("the gateway may already have paid ... not retried"), the lock and the idempotency record stay,
the audit row carries `MANUAL_QUEUE_FAILED:<reason>` (`outcome=unknown`), and the recovery step
queues it later.

The `202 ESCROW_034` of an unknown outcome says "The gateway did not confirm this payout, so it
may already have been made ...". `ESCROW_034` means the settlement waits in the queue and this
call made no payout; whether an earlier gateway call did depends on the reason.

### Nothing that can fail between the lock and the call
The farmer's and the trucker's payout destinations and the buyer's refund phone are read before
the lock, and the idempotency record is written after the gateway answered (best-effort, like the
other idempotency writes after the lock). An exception before the lock is a plain failure; one
that escapes after it is an unknown outcome (see Alarms).

### Status writes
The `released` and the `refunded` write share one helper: up to 4 attempts, 150 ms times the
attempt apart. A failed condition is not retried: an earlier attempt, or the SDK's own retry after
a lost answer (so even on the service's first attempt), may have landed although the client saw an
error, so a strongly consistent re-read that shows the new status (or `completed` for a release)
counts as written. If the write never lands, the service
logs `ESCROW_SETTLEMENT_PAID_UNRECORDED escrowId=.. op=.. <leg>Ref=..` (the references for the
operator, no alarm of its own), keeps the lock and lets the exception escape. The trucker leg's
audit row is written after the `released` write, so a failed audit write cannot leave a paid
escrow unrecorded. Once the settled status is written the payout is on record: an exception after
it (a leg audit row, the `release` or `refund` row, the `completed` write or its audit row) logs
`outcome=failed`, never `unknown`, and no queue row follows; the crash-recovery sweep completes a
`released` escrow.

### The settlement lock
`TryAcquireSettlementLockAsync` now also writes `settlementLockAt` (ISO time) and `settlementLockId`
(a random id) with `settlementLock`. When the write fails its condition, a strongly consistent
re-read that shows the same op and this lock id counts as acquired: the SDK retried a write whose
answer was lost. `ReleaseSettlementLockAsync` removes all three, and refuses (as a no-op) a row
with an open manual payout: the queue owns its lock even when the caller missed that its queue
write committed. The lock attributes stay on settled rows, as before.

| Attribute | Value |
|---|---|
| `settlementLockAt` | ISO time the lock was taken; from then on the gateway may have been called. Missing on a lock taken before 1.10.0 |
| `settlementLockId` | random id of this lock (two locks can carry the same time) |
| `manualPayoutReason` | gains `GATEWAY_OUTCOME_UNKNOWN` |
| `manualPayoutParkId` | random id of the recovery write that parked the row (recovery only) |

### Recovery step (escrow-cron, hourly)
First in `auto_release_and_refund`, right after the overdue report and before the sweeps:

- Reads `StatusExpiresIndex` for `escrowed`, `dispute_resolved` and `disputed` (a lock on a
  `disputed` row was taken before 1.9.0) with the filter `attribute_exists(settlementLock) AND
  attribute_not_exists(manualPayout) AND (settlementLockAt <= now - 15 min OR (no settlementLockAt
  AND updatedAt <= now - 15 min))`, paging through filtered-empty pages, at most 50 rows per status
  per run. No new index: it reads the whole status partitions hourly; ESC-20's ledger index
  replaces this.
- 15 minutes is far above the longest settlement run (escrow Lambda 30 s, escrow-cron 5 min; an
  infra test keeps both at or under 5 minutes), so such a lock has no living owner. Any of them may
  have called the gateway, so each is parked, never unlocked: one pending leg per payee of the
  locked operation, reason `GATEWAY_OUTCOME_UNKNOWN`, conditional on the same lock
  (`settlementLockId`, or no lock id at all for a pre-1.10.0 lock), the same status, the lock still
  held and no queue entry.
- On a parked row: the alarm line `ESCROW_SETTLEMENT_FAILED escrowId=.. action=stale_lock_parked
  outcome=unknown code=<op>`, `ESCROW_MANUAL_PAYOUT_QUEUED`, escrow-audit `stale_lock_parked`
  (metadata `op=..;lockedAt=..;ageMinutes=..;legacyLock=..`, one tick before the queued row so the
  two never share the table's sort key) and `manual_payout_queued` (actor `system`).
- A condition that fails is checked with a strongly consistent re-read: the row is ours only if it
  carries this run's `manualPayoutParkId` on the same lock (a write whose answer was lost).
  Anything else (another run parked it first, a new lock) is left alone without a log line, audit
  row or alarm.
- Never calls the gateway and never releases a lock. A failed query logs `sweep_failed
  outcome=failed code=lock_recovery_query:<status>:<type>`. A park write that throws, on a row the
  re-read shows still unqueued under the same lock, logs `stale_lock_recovery_failed
  outcome=unknown code=<exception type>` (so does a re-read that throws), and the next run tries
  again. The run summary carries `staleLocksParked`.
- `stale_lock_parked` metadata also carries `disputeResolution`. A lock taken before ESC-03 can go
  against the verdict of a `dispute_resolved` row (a `release` lock under `buyer_wins`); it is
  parked like any other, and the admin page warns (see Admin).
- **Legacy `disputed` rows**: a queue close from `disputed` settles with no verdict and no order or
  delivery mirror (the 1.8.0 close keeps `disputed` in its set). Only pre-1.9.0 locks can land
  there. A verdict on such a row answers `409 ADMIN_013`; the admin brings the order and the
  delivery to the right status by hand.

The auto-refund sweep skips any `escrowed` row that carries a settlement lock (an in-flight
settlement, or one the recovery step just parked while the index copy lacks the queue marker),
before the delivery stamp and the order cancel and restock, and logs
`ESCROW_SETTLEMENT_IN_FLIGHT_SKIPPED escrowId=.. sweep=auto-refund`. Because the index copy can
lag, it then reads the base row strongly consistent and skips (`ESCROW_AUTO_REFUND_SKIPPED
escrowId=.. reason=missing|status|queued|locked|confirmed`) unless the row is still `escrowed`,
unlocked, unqueued and unconfirmed. This narrows the race with an admin release that takes the lock
to the few milliseconds between that read and the order cancel; it does not close it. The auto-release,
auto-refund and crash-recovery queries are wrapped like the dispute query: a failure logs
`sweep_failed outcome=failed code=release_query|refund_query|complete_query:<type>` and only that
sweep skips the run.

There is no automated status check: the live gateway has no payout status API
(`GetTransactionStatusAsync` answers `unknown` without a call; B2B payouts are not integrated)
and the mock's status is not truthful. Unknown outcomes go to the admin. A status check belongs
with the B2B integration or the ESC-20 ledger (`sent` rows).

### Alarms
`outcome=unknown` is now logged only when money may have moved: the uncertain codes above,
`GATEWAY_EXCEPTION:*`, `MANUAL_QUEUE_FAILED:<reason>` for any reason but `PAYOUT_NOT_SUPPORTED`,
`stale_lock_parked`, `stale_lock_recovery_failed`, and an exception that escaped a release or
refund after it took the lock and before its settled status was written (`release_exception`, `refund_exception`, `auto_release_exception`,
`auto_refund_exception`, `dispute_settlement_exception`, `sweep_failed` for a whole run). An
exception before the lock, and a definite refusal, log `outcome=failed`.

New alarm `${prefix}-escrow-settlement-unknown-outcomes` (FUP-06): metric
`EscrowSettlementUnknownOutcomes` from two filters (escrow and escrow-cron log groups, pattern
`"ESCROW_SETTLEMENT_FAILED" "outcome=unknown"`), 15 minute periods, 5 periods, any line alarms. The
general `${prefix}-escrow-settlement-failures` alarm still counts every line. Runbook: do not release
or refund by hand; open Manual Payouts, search the portal for the leg's gateway reference, record it
if found, pay and record only if it is still missing.

### Admin
`GET /admin/payouts/manual`: each leg gains `gatewayRef` (`release:<escrowId>`,
`release:<escrowId>:trucker` or `refund:<escrowId>`), each item `settlementLockedAt` (null for a
pre-1.10.0 lock) and `disputeResolution` (null without a verdict). When a `release` waits under a
`buyer_wins` verdict, or a `refund` under `farmer_wins`, the page warns not to pay it and to check
the escrow history first. The Manual Payouts page treats `GATEWAY_OUTCOME_UNKNOWN` like
`GATEWAY_REF_MISSING` (check the portal first, "Record reference", confirm the payment is in the
portal), shows the gateway reference to search for, says to search again later before paying
(a payment can show up late), and shows how long ago the gateway was called. Strings in en, am and om.
Clients (mobile and web) see no change: the escrow keeps its status.

### Not in 1.10.0 (known limits)
- A Lambda killed between the lock and the gateway call (no I/O there any more) is parked like
  any other stale lock: the admin finds nothing in the portal and pays. No double pay.
- The admin can still pay twice by hand: two admins on one leg, or a payment made without checking
  the portal (1.8.0, no claim step).
- Audit rows after a settled status (`release`, `complete`, `refund`) can still be lost in a crash;
  the escrow row keeps the references.
- A locked row still sits in the auto-release window until the next recovery run (at most an hour).
- `held` (FRD-03) is not a recovery status; when FRD-03 lets an admin settle a held escrow it joins
  the list.
- ESC-20 (ledger) replaces the whole-escrow lock, this recovery step and the reason with per-leg
  rows (`sent`, status check, `manual`).

---

## Additions in 1.11.0 (trucker leg retry, ESC-04)

Before 1.11.0 a release whose trucker leg was refused, or whose trucker failed the payout gate,
still moved the escrow to `completed` with `truckerPayoutPending = true`, nothing ever read that
flag, and the block reason was lost (the audit row said `code=-`). An escrow could be completed
with a leg unpaid, silently.

### The rule
An escrow is `completed` only once every leg is paid. `released` means the farmer is paid; with
`truckerPayoutPending = true` the trucker leg is still owed and the escrow stays `released` until the
hourly trucker retry pays it, or an admin records it in Manual Payouts. The repository refuses a move
to `completed` while `truckerPayoutPending` or `manualPayout` is set, so no writer (the release, the
crash recovery, a manual close) can complete an escrow with a leg owed.

`truckerPayoutPending` is only set where nothing moved for the trucker (1.10.0): a definite gateway
refusal, a `PAYOUT_NOT_SUPPORTED` leg whose queue write failed, or a trucker the gate blocks (no
call). That is what makes a retry with the same reference safe.

### Release
`POST /escrow/release` (and the sweeps) with a pending trucker leg answer `200` with
`"status": "released"`, `truckerTelebirrRef: null` and the message "Farmer paid. The transport payout
is pending and is retried every hour." No `completed` write follows. A trucker blocked by the payout
gate is audited `trucker_release_blocked_kyc` (metadata `code=ESCROW_029;reason=<reason>`, no money
alarm, like `release_blocked_kyc`); a definite refusal stays `trucker_release_failed` with its code.

### Row attributes (escrows table)

| Attribute | Value |
|---|---|
| `truckerPayoutPending` | `true` while the trucker leg is owed; removed when the leg is recorded (before 1.11.0 never removed) |
| `truckerPayoutPendingSince` | ISO time the leg went pending; the overdue alarm counts from it (fallback `releasedAt`). Written with every pending flag since 1.11.0, so a pending leg without it is legacy (see Legacy rows) |
| `truckerPayoutBlockReason` | `no_trucker`, `profile_missing`, `phone_unverified`, `fayda_required` or `trucker_mismatch`; written as a compare-and-set, so each value is audited once; removed when the gate passes |
| `truckerPayoutBlockReasonAt` | ISO time the reason was set: the key of its `trucker_release_blocked_kyc` row (written with a conditional put), which every run that sees the same reason re-ensures, so a row lost after the reason was stored comes back |
| `truckerPayoutLockId`, `truckerPayoutLockAt` | the trucker retry's lock (random id, ISO time); the release's `settlementLock` stays on a released row and is not reused |
| `truckerPaidAt` | ISO time the trucker leg was recorded |

### Trucker retry (escrow-cron, hourly)
Last in `auto_release_and_refund`, after the crash recovery:

1. Reads `StatusExpiresIndex` for `released` rows with `truckerPayoutPending = true` and no
   `manualPayout`, oldest `expiresAt` first, up to 500 rows (more logs `ESCROW_SETTLEMENT_FAILED
   escrowId=- action=sweep_truncated outcome=failed code=trucker_retry_query:500`; a failed query logs
   `sweep_failed outcome=failed code=trucker_retry_query:<type>`). The crash recovery's query leaves
   these rows out. A leg that went pending in this run waits for the next one.
2. Per row, a strongly consistent read of the base row; a row no longer released, pending and
   unqueued is skipped. A pending leg without `truckerPayoutPendingSince` was left by the code before
   1.11.0 (a crash between the released and completed writes) and is parked as a legacy row (see
   Legacy rows), never retried.
3. A trucker lock younger than 15 minutes is left to its owner. An older one has no living owner and
   its gateway call may have paid: the leg is parked in Manual Payouts as `GATEWAY_OUTCOME_UNKNOWN`
   (fenced on the same lock id, `manualPayoutParkId`, silent when another run parked it first), alarm
   `stale_lock_parked outcome=unknown code=release:trucker`, audit `stale_lock_parked` (metadata
   `op=release;leg=trucker;lockedAt=..;ageMinutes=..`) and `manual_payout_queued`. Never released.
4. An escrow without `truckerId` takes the order's `truckerId`, only when the escrow's delivery
   names the same trucker (the delivery's `truckerId` never changes); audit `trucker_backfilled`
   (`source=order;truckerId=..`). An order without one is `no_trucker`; a delivery that cannot be
   read or names another trucker is `trucker_mismatch`, and nothing is written.
5. The payout gate (phone, and Fayda above 10,000 ETB on the transport leg); without a profile
   repository the retry fails closed (`gate_unavailable`). A blocked trucker is not called: the reason
   is stored and audited once per value.
6. A trucker lock (`#status = released AND truckerPayoutPending AND no lock AND no manualPayout`; a
   lost answer is recognised by the lock id) is taken only with more than 90 seconds of the run left
   (the gateway's 30 second timeout plus the writes after it) and below 25 gateway calls per run.
   The gateway is called at once with `release:<escrowId>:trucker`, the hold reference, the trucker's
   registered phone and the transport leg; an exception from here on is an unknown outcome
   (`trucker_retry_exception outcome=unknown`).

| Answer | What happens |
|---|---|
| success with a reference | one write fenced on the lock id: `completed`, `truckerTelebirrRef`, `truckerPaidAt`, `completedAt`, the pending flag, block reason and lock removed (4 attempts; a failed condition whose consistent re-read shows our reference counts as written; otherwise `ESCROW_SETTLEMENT_PAID_UNRECORDED` and the lock stays for step 3). Audit `trucker_release` (metadata `source=retry`) at `truckerPaidAt` and `complete` (`trucker_retry`) at `completedAt`, one 100 ns tick later, both at deterministic keys |
| `PAYOUT_NOT_SUPPORTED`, success without a reference | the trucker leg goes to Manual Payouts (op `release`, from status `released`, the farmer leg seeded `paid` by `gateway` with `telebirrReleaseRef` at `releasedAt`), fenced on the trucker lock, which stays. If the queue write fails 4 times: `PAYOUT_NOT_SUPPORTED` releases the lock (nothing moved) and audits `MANUAL_QUEUE_FAILED:PAYOUT_NOT_SUPPORTED`; `GATEWAY_REF_MISSING` keeps it for step 3 |
| unknown (`TELEBIRR_TIMEOUT`, `TELEBIRR_NETWORK`, `TELEBIRR_PREORDER`, no answer, `GATEWAY_EXCEPTION:*`) | audit `trucker_release_failed` with the code (`outcome=unknown`), queued as `GATEWAY_OUTCOME_UNKNOWN`, lock kept, never retried |
| any other code | a definite refusal: the lock goes first, then `trucker_release_failed` with the code (`outcome=failed`); the next run retries with the same reference |

Every leg still owed and not queued more than 24 hours after `truckerPayoutPendingSince` logs, once
per run, `TRUCKER_PAYOUT_OVERDUE escrowId=<id> reason=<reason> ageHours=<n>` (a `LogWarning`), with
the block reason or why the run did not pay it (`gateway_refused`, `queue_failed`, `in_flight`,
`deferred`, `error`). The run summary gains `truckerLegsPaid`, `truckerLegsQueued`,
`truckerLegsBlocked`, `truckerLegsFailed`, `truckerLegsDeferred` and `truckerLegsOverdue`.

### Manual Payouts on a released escrow
A trucker leg queued on a `released` escrow (from status `released`) closes too: the close condition
accepts `released` when `manualPayoutFromStatus` is `released`. Recording it moves the escrow straight
to `completed` in the closing write (`truckerTelebirrRef`, `truckerPaidAt`, `completedAt`, the
pending flag, block reason and lock removed); `releasedAt` and `telebirrReleaseRef` stay as the release
wrote them. Audit `manual_payout_paid` (from = to = `released`) and `complete` (`manual`) at
`completedAt`, no `release` row; a replay re-ensures the same rows. `GET /admin/payouts/manual` shows
such an item with `escrowStatus` and `fromStatus` `released` and `settlementLockedAt` set to the trucker
lock time. A trucker leg of an escrow without `truckerId` shows (and, when recorded, keeps) the order's
trucker under the rule of step 4; `trucker_mismatch` or `no_trucker` answer `409 ESCROW_029`. No admin
page or string change.

### Legacy rows (`park_legacy_trucker_legs`)
A trucker leg left pending before 1.11.0 has no `truckerPayoutPendingSince`, and the 1.8.1 runbook said
to pay such a leg by hand, so it is never retried. A legacy row is moved to `released` with the leg queued
as `GATEWAY_OUTCOME_UNKNOWN` in one write conditional on the same status, the leg pending without a
pending time or trucker lock, and no queue entry (`completedAt` removed, `truckerPayoutPendingSince` =
the old `completedAt` or `releasedAt`; the order's trucker is backfilled first under step 4). Alarm
`legacy_trucker_parked outcome=unknown code=release:trucker`, audit `legacy_trucker_parked` (from the
old status to `released`, metadata `legacy=true;fromStatus=..;completedAt=..`) and `manual_payout_queued`.
Never calls the gateway.

- A `released` legacy row (a crash between the two writes) is parked by the hourly trucker retry
  (step 2), whose run summary counts it as queued.
- A `completed` legacy row (a release that completed without the leg) is parked by the escrow-cron
  action `park_legacy_trucker_legs` (no schedule; `deploy-server.yml` invokes it after every successful
  deploy with `{"body": "{\"action\":\"park_legacy_trucker_legs\"}"}`, retrying with backoff on
  throttling or a grant that has not propagated yet). It reads the `completed` partition for such rows (up
  to 5000), answers `200` with `found`, `parked`, `failed`, `truncated`, `marker` and `skipped`, or `500`
  when a row could not be parked (`legacy_trucker_park_failed`). Idempotent. The CI deploy role may
  invoke only escrow-cron (`InvokeEscrowCronAfterDeploy`).
- Bounded by a marker in the escrow idempotency table (`migration:esc-04:park_legacy_trucker_legs`,
  30-day TTL): the first run that finds nothing writes `clean`; a later run that finds nothing at least
  15 minutes after that writes `done`; a `done` marker skips the read. The two clean runs apart catch a
  release that a Lambda instance still running the old code completed with the leg unpaid after the
  deploy's first run. Until the marker is done such a row waits for the next deploy's run: the hourly
  retry and the overdue report read only `released` rows. Deleting the marker makes the next deploy read
  again.

### Alarms
New `${prefix}-trucker-payouts-overdue` (metric `TruckerPayoutsOverdue` from the `TRUCKER_PAYOUT_OVERDUE`
filter on escrow-cron, 15 minute periods, 5 periods, any line alarms). Runbook: `phone_unverified` or
`fayda_required`: ask the trucker to verify, the next hourly run pays (with the live gateway it moves to
Manual Payouts); `no_trucker`, `profile_missing` or `trucker_mismatch`: no payable trucker; the hourly
run copies the truckerId from the order when the delivery agrees, otherwise the leg has no payee until
ESC-20: escalate, never edit the escrow row by hand; `gateway_refused`, `queue_failed`: see
settlement failures. A `trucker_release_failed outcome=failed` line no longer means "pay the trucker by
hand".

### Roles and clients
Farmer and buyer: unaffected (the farmer is paid at release; `released` already reads "Funds sent to
farmer"). Trucker: paid by the retry; while the leg waits the escrow reads `released` instead of a false
`completed`. No wire, model or string change for mobile, web or admin. Account deletion is unchanged:
a `released` escrow already blocks every party (account-deletion 1.0.0).

### Not in 1.11.0 (known limits)
- No attempt cap on definite refusals: the leg is retried every hour and alarms each time, like the
  farmer leg (ESC-20 moves a leg to `manual` after 3 failures).
- A trucker who never verifies keeps the leg owed and `trucker-payouts-overdue` in ALARM; the buyer
  cannot get the transport leg back until ESC-20 (`refund#transport`).
- A two-leg Manual Payouts entry (the live gateway queues farmer and trucker at once) still holds a
  blocked trucker leg as in 1.8.0, and recording the farmer leg alone leaves the escrow `escrowed`
  until the trucker leg is recorded.
- Pending trucker legs past the first 500 of the `released` partition are neither retried nor
  reported overdue in that run (the truncation line alarms instead).
- ESC-09 (order follows the money) should keep the order `disbursing` while the escrow is `released`
  and move it to `settled` only on `completed`.
- The `trucker_release` and `complete` rows after a retry payment sit at deterministic keys, but a row
  lost after the record write is not re-ensured: the completed escrow leaves the retry query. The escrow
  row keeps `truckerTelebirrRef`, `truckerPaidAt` and `completedAt` (as for the 1.10.0 settled rows).
- O4: a retry can run days after the release; a trucker suspended since then is still paid if verified.
  Blocking suspended payees belongs to FRD and would change the release gate too.
- O7: a future B2B gateway may answer "duplicate reference" on a reused `release:{id}:trucker` after an
  earlier refusal; that answer must be classified as maybe-paid (queued, never retried) when B2B or
  ESC-45 lands.
- ESC-20 (ledger) replaces the trucker lock, the retry and the legacy action with a
  `release#trucker#{id}` row.

---

## Additions in 1.12.0 (disputes always freeze money, ESC-07)

### Every dispute freezes the escrow first

Before 1.12.0 three paths could mark a trade disputed, and only one froze the money first:

| Path | Before | Since 1.12.0 |
|------|--------|--------------|
| Client order update `delivered -> disputed` (buyer or farmer) | Order disputed, escrow still `escrowed`, delivery `delivered`: the 48-hour auto-release paid the farmer and the admin never saw the dispute | Refused, `ORDER_UNAUTHORIZED` (system-managed, `order-lifecycle.md` 1.14.0) |
| Delivery dispute through `POST /sync` | Delivery written first; then the freeze. A refused freeze (manual payout, lock) left the delivery `disputed`; a failed or missed freeze (exception, `DeliveryIndex` miss) still mirrored the order to `disputed` over escrowed money | Freeze first; a refused or undecided freeze writes nothing (below) |
| `POST /escrow/dispute` | Freeze first, then the delivery and order | Unchanged order; every hop now audited, a lost race is never a `500` |

### The freeze behind a delivery dispute

`EscrowService.FreezeForDeliveryDisputeAsync` runs after the delivery handler's own checks
(window, parties, step-up, reason) and after the client version matched a strongly consistent
read of the delivery (a stale version is a `conflict` and freezes nothing). It finds the escrow
and decides on the base row, never on an index copy:

1. A delivery with an `orderId`: the order escrow `escrow-order-{orderId}`, strongly consistent,
   any status. Otherwise, or when the order has none (an order paid through `POST /escrow/initiate`),
   the escrow on the `DeliveryIndex`, re-read strongly consistent by id.
2. An order escrow linked to another delivery: refused (audit `dispute_refused_other_delivery`).
3. A manual payout queued (`manualPayout` open), on any status, even `disputed`: refused (audit
   `dispute_refused_manual_payout`, ESC-02). A settlement lock held: refused (audit
   `dispute_refused_settlement_in_flight`, ESC-03).
4. `escrowed`: frozen with the write `#status = escrowed AND attribute_not_exists(manualPayout) AND
   attribute_not_exists(settlementLock)` (reason `other`, the delivery's text as the description;
   the version is added to with `ADD`, never written from the read). The same write stamps the
   server-only `disputeAuditAt` (equal to `disputeCreatedAt`), and the `dispute` audit row goes to
   that deterministic key, written only if absent. When the condition
   fails, the row is read again and decided by steps 3 to 5; still `escrowed` with nothing in the way
   is an unknown outcome (retry).
5. `disputed`: already frozen (no status write); when the escrow carries `disputeAuditAt`, the
   `dispute` row is written at that key if it is missing (the freeze's own audit write may have
   failed), and the delivery and order may follow. An escrow disputed before 1.12.0 has no marker:
   its row sits at the old write's own time, so no row is added (the ledger is append-only). This repairs a trade whose `POST /escrow/dispute` mirror failed, and finishes a
   delivery dispute whose audit or delivery write failed after its freeze.
6. Any other status (`initiated`, `released`, `completed`, `refunded`, `dispute_resolved`,
   `cancelled`, `held`, unknown): refused (audit `dispute_refused_not_held`, metadata
   `deliveryId=<id>`). An unlinked refunded order escrow lands here, not in "no escrow".

Every refusal row is written once per escrow, delivery and reason each UTC day: its `auditId` is
`audit-<action>-<deliveryId>-<yyyyMMdd>` and it is skipped when the escrow's trail already has it
(the row keeps the time of the day's first refusal). Clients before sync-protocol 1.12.0 resend a
refused dispute on every reconnect. The sync Lambda reads the trail for this (`dynamodb:Query` on
escrow-audit).

| Outcome | Sync answer | Written |
|---------|-------------|---------|
| frozen, already disputed | the delivery update (`success`, or `conflict` with the current row) | escrow (when frozen), delivery, order |
| manual payout, settlement in flight, not held, other delivery | `error` `DELIVERY_DISPUTE_REFUSED: <why> Contact support.` (permanent) | nothing (one escrow-audit refusal row) |
| no escrow, delivery has an `orderId` | `error` `DELIVERY_DISPUTE_REFUSED: No payment is on record for this delivery's order, ...`; log `DISPUTE_NO_ESCROW orderId=<id> deliveryId=<id>` (the order escrow problems alarm counts it) | nothing |
| no escrow, legacy delivery without an `orderId` | the delivery update, as before (no money to freeze) | delivery |
| exception, or no escrow service wired for an order's delivery | `error` `DELIVERY_DISPUTE_RETRY: ...` (transient: the client sends it again) | nothing |

After a successful delivery write the order follows through the mirror. When the delivery write
fails its version condition after a successful freeze (a concurrent update landed after the
version check), the money is frozen, so the mirror marks the delivery (status-conditioned) and the
order, and the client gets a `conflict` with the row as it is now. When the freeze's audit write or
the delivery write fails any other way (a throttle), the request errors with the escrow `disputed`
and the delivery and order still `delivered`; the client's retry finds the escrow already disputed
and finishes the audit row, the delivery and the order, with one `dispute` row on the escrow in all.
A dispute for a delivery that is already `disputed` (by the strongly consistent read too: the
retry of a request that stopped between the delivery write and the order mirror, or the other
party's dispute) is not written again: a participant's request runs the freeze (normally
`AlreadyDisputed`) and the order mirror, then answers `conflict` with the delivery row. Anything
else but `delivered` at the client's version is a `conflict` that freezes nothing.

`POST /escrow/dispute` follows the same rule: it stamps `disputeAuditAt` with the status and its
`dispute` row is at that key, and a party's request that finds the escrow already `disputed` (no
manual payout, no lock) writes a missing `dispute` row (marker present) and runs the mirror before
it answers `409 ESCROW_020` (a second dispute, as when it loses the write race); a failure of that
repair is logged and the answer stays `409 ESCROW_020`.

The repeat path can freeze an `escrowed` escrow behind a delivery that was already `disputed` before
1.12.0 (a legacy row whose freeze never happened) without the 48-hour window or the step-up checks
the first dispute passed. That is intended: it only ever freezes money behind a delivery that is
already in dispute.

### Follow-ups

- The auto-release sweep (`EscrowCronFunction.AutoReleaseAsync`) reads only the escrow: it never
  checks that the order or delivery is disputed. Since 1.12.0 a disputed order or delivery implies
  a frozen escrow, except rows left inconsistent before it (scan before deploy).
- `POST /escrow/dispute` before delivery confirmation freezes the money but leaves the order and
  delivery as they are (`disputed` only follows `delivered`); the decision to refuse it is FUP-16.

## Changelog

| Version | Date | Change |
|---------|------|--------|
| 1.12.0 | 2026-10-10 | Disputes always freeze money (ESC-07): the client order dispute is gone (`order-lifecycle.md` 1.14.0); a delivery dispute through sync freezes the escrow before the delivery or order is written (order escrow by its deterministic id, else the `DeliveryIndex`, decided on a consistent base row; manual payout, lock, any status but `escrowed`/`disputed`, another delivery: refused with `DELIVERY_DISPUTE_REFUSED` and nothing written; undecided: `DELIVERY_DISPUTE_RETRY`; no escrow for a funded order: refused and alarmed `DISPUTE_NO_ESCROW`); one dispute mirror for both paths (consistent reads, a lagging order advanced first, one transaction-audit row per hop, the delivery version added to); `POST /escrow/dispute` answers every lost race (`ESCROW_020`, `ESCROW_038`, `ESCROW_INVALID_TRANSITION`) and frees its key, never a `500`; new audit actions `dispute_refused_not_held`, `dispute_refused_other_delivery`; dispute rows at a deterministic key (repaired by a retry), refusal rows once a day per escrow, delivery and reason; the disputed write adds to the version; a dispute on an escrow already `disputed` answers `409 ESCROW_020` (was `422`) |
| 1.11.0 | 2026-10-10 | Trucker leg retry (ESC-04): an escrow is `completed` only once every leg is paid (the repository refuses `completed` while `truckerPayoutPending` or `manualPayout` is set); a release with a pending trucker leg stays `released` and answers `status: released`; a gate-blocked trucker is audited `trucker_release_blocked_kyc` with its reason (no money alarm) and the reason is stored once per value (compare-and-set); new row attributes `truckerPayoutPendingSince`, `truckerPayoutBlockReason`, `truckerPayoutLockId`, `truckerPayoutLockAt`, `truckerPaidAt`; hourly trucker retry last in the run (after the crash recovery) under its own lock, only with time left (90 s) and up to 25 gateway calls, with the stable reference `release:<id>:trucker`: paid completes in one fenced write (deterministic `trucker_release` and `complete` rows), not supported, no reference or unknown queues the leg on the released row, a definite refusal releases the lock for the next run, a stale trucker lock is parked as `GATEWAY_OUTCOME_UNKNOWN`; truckerId backfilled from the order only when the delivery names the same trucker (`trucker_mismatch` otherwise), also when an admin records the leg; Manual Payouts closes a trucker leg on a released escrow straight to `completed` without touching the release; pre-1.11.0 trucker legs (no pending time) are parked in the queue as `GATEWAY_OUTCOME_UNKNOWN`, never retried: released ones by the hourly retry, completed ones by `park_legacy_trucker_legs`, which the server deploy invokes (with retries) until a marker says two clean runs found nothing; the block reason's audit row sits at `truckerPayoutBlockReasonAt` and is re-ensured; new token `TRUCKER_PAYOUT_OVERDUE` and alarm `trucker-payouts-overdue`; `sweep_truncated`, `trucker_retry_exception`, `legacy_trucker_parked` and `legacy_trucker_park_failed` lines; the crash recovery and the cron status queries skip pending trucker legs; no client change |
| 1.10.1 | 2026-10-09 | App side of the order checkout (ESC-44): when the mobile and web apps request it, how they reuse an open checkout, poll the order and tell the `ESCROW_030` causes apart, and localized `ESCROW_030` to `ESCROW_033`; no server or wire change |
| 1.10.0 | 2026-10-09 | No double pay, no stuck lock (ESC-05): a gateway timeout, lost or unreadable answer (`TELEBIRR_NETWORK`, also for the live preOrder's unreadable 200 or missing `prepay_id`) or a call that threw (`GATEWAY_EXCEPTION:<type>`) keeps the settlement lock and parks the escrow in the manual payout queue with the new reason `GATEWAY_OUTCOME_UNKNOWN` (`202 ESCROW_034`, unknown-outcome message), never a blind retry; a `GATEWAY_REF_MISSING` queue write is retried and never releases the lock (FUP-03); only a definite refusal releases the lock (lock first, then the audit row); an unknown trucker leg is queued with the farmer leg paid, and never falls back to `truckerPayoutPending`; payout destinations and the refund phone are read before the lock and the idempotency record after the gateway answered; the `refunded` write retries like the `released` write, and a condition failure after a lost commit counts as written; the lock carries `settlementLockAt` and `settlementLockId` (a lost answer on the lock write is recognised by the id), and releasing it is a no-op on a queued row; hourly recovery step parks a lock older than 15 minutes on `escrowed`, `dispute_resolved` or (legacy) `disputed` rows, fenced on the lock and status, silent when it loses a race (`manualPayoutParkId`), audited `stale_lock_parked`, never calling the gateway; auto-refund skips a locked row before any delivery or order write; the release, refund and crash-recovery queries fail on their own (`sweep_failed outcome=failed`); `outcome=unknown` only after the lock (exceptions before it are `failed`); new alarm `escrow-settlement-unknown-outcomes` (FUP-06); manual payout list items gain `settlementLockedAt` and legs `gatewayRef`, plus `disputeResolution` with a warning when the parked operation goes against the verdict; a settled status found by a consistent re-read after any failed condition counts as written; exceptions after the settled status write log `outcome=failed`; a park write that throws on a still-unparked lock alarms `stale_lock_recovery_failed`; auto-refund re-reads the base row strongly consistent before any side effect; admin page strings in en/am/om; no client change |
| 1.9.0 | 2026-10-09 | Dispute verdicts settle (ESC-03): the settlement lock requires the exact status the caller validated, settleable `escrowed` and `dispute_resolved` (`dispute_resolved` added, `disputed` removed); release and refund of a `disputed` escrow refused (`422`, verdict first), and of a `dispute_resolved` escrow only the way its verdict says (`farmer_wins` releases, `buyer_wins` refunds; legacy verdicts either way); `POST /admin/disputes/{id}/resolve` admin group only (`403 ESCROW_015`), `resolution` `farmer_wins` or `buyer_wins` with a matching `action` (`400 ADMIN_014`; `partial_refund` refused until ESC-20), refused while a settlement holds the lock (`409 ADMIN_010`), same-verdict replay answers `200` with the current status; the order and delivery follow the verdict at the verdict and again before settlement (`DisputeVerdictMirror`; order hops to `dispute_resolved`, delivery stamped `disputeResolution`/`disputeResolvedAt`, both legs decided before any write), `buyer_wins` refunds a cancelled or rejected order as it is and cancels an order before pickup (pending delivery cancelled, stock restored) before refunding; a verdict whose order cannot follow it is not settled (`422`, `DISPUTE_VERDICT_ORDER_MISMATCH` and the `dispute-verdict-order-mismatch` alarm), with an admin-group override (`overrideOrderMismatch` and a reason) for a `buyer_wins` whose goods are moving, never for `farmer_wins`; a failed order or delivery read, or a `buyer_wins` cancel or restock that did not land, fails closed (`503 ESCROW_012`, `DISPUTE_VERDICT_SETTLEMENT_BLOCKED` and the `dispute-verdict-settlement-blocked` alarm when blocked in three of four hourly periods, one `dispute_settlement_blocked` audit row); failures audited once per escrow and action at the verdict time plus one or two ticks (`dispute_verdict_mismatch`, `dispute_verdict_propagation_failed`); transaction-audit rows for every hop; `escrowed -> disputed` refused while a settlement holds the lock (`409 ESCROW_INVALID_TRANSITION`; a sync delivery dispute is not frozen, audited `dispute_refused_settlement_in_flight`); `dispute_resolved` counts as an active escrow; escrow and escrow-cron Lambdas get `TRANSACTION_AUDIT_TABLE` (`PutItem` only); `ADMIN_010` and `ADMIN_014` localized in admin (en/am/om) |
| 1.8.1 | 2026-10-09 | Money alarms (ESC-13): `ESCROW_SETTLEMENT_FAILED` logged for every failed release, refund or trucker leg (outcome `failed` or `unknown`; `unknown` also for `MANUAL_QUEUE_FAILED:GATEWAY_REF_MISSING`), for exceptions mid-sweep and on the admin settlement routes; a payout queued for an admin (`202 ESCROW_034`) is not counted; the 1.8.0 overdue report logs `MANUAL_PAYOUT_OVERDUE` through `MoneyAlarmLog` (same line); `ADMIN_QUEUE_OVERDUE` defined for ESC-06 (24 hours); five alarms: settlement failures, escrow-cron errors, order escrow problems (create, link, refund schedule; replaces the create-failed alarm), manual payouts overdue, admin queue overdue (log alarms over 5 periods of 15 minutes, escrow-cron errors over 4 periods of 5 minutes); client-influenced values in the escrow, escrow-cron, sync and webhook log lines have `_` turned into `-` and the escrow Lambda logs the route template, so no client can spell a token; no endpoint or response change |
| 1.8.0 | 2026-10-09 | Fail-closed live payouts and the manual payout queue (ESC-02): the live gateway's release and refund return `PAYOUT_NOT_SUPPORTED`; the service queues such settlements (and gateway successes without a reference, trucker leg included) on the escrow row with the lock held, returns `202 ESCROW_034`, and stays quiet on later sweeps; a failed queue write is re-read strongly consistent; the lock and any `released`/`refunded` status write refuse a queued row; new sparse `ManualPayoutIndex`; `GET /admin/payouts/manual` and `POST /admin/payouts/{escrowId}/{ref}/complete` (admin group only) with codes `ESCROW_035` to `ESCROW_037` and `409 ESCROW_029` (`alreadyRecorded` on a replay, no second transaction-audit row); cron status queries filter out queued rows and page through filtered-empty pages; `409 ESCROW_038` on a dispute against a queued escrow (delivery disputes through sync are refused and audited, and the order is not mirrored); queue audit rows at deterministic keys, re-ensured on replay, with the cron crash recovery keying its `complete` row on `completedAt` too; `deliveryConfirmedAt` on list items; `409 ADMIN_013` on a dispute verdict against a queued payout; audit actions `manual_payout_queued`, `manual_payout_paid`, `manual_payout_refused`; hourly `MANUAL_PAYOUT_OVERDUE` producer for the ESC-13 alarm; `order_payment_refunded_manual` reserved for ACC; known limits listed; `ESCROW_038` localized in the app (mobile and web, en/am/om), no other client change |
| 1.7.0 | 2026-10-06 | Fraud hold and risk tiers (planned FRD-03, FRD-04): new status `held` with `holdReason`, `heldAt`, `heldBy`, `heldFromStatus`, `heldFor`; settlement-only entry from `escrowed` or `dispute_resolved`, admin-only exits (an admin refund of a `heldFor: release` escrow moves the order to `dispute_resolved` as `buyer_wins`; the auto-refund hold is fenced on the order's version), `held` counts as active, crons never select it, `FRAUD_HOLD_ENABLED`; `403 FRAUD_ACCOUNT_HELD` on initiate and order-checkout; `AUTH_040`/`AUTH_041` also from the risk tier; clients tolerate unknown status values; state table gains `dispute_resolved`, `held` and the missing 1.3.0 transitions; `dispute_resolved` named in the release and refund transition errors; amount anomaly marked planned (FRD-09) |
| 1.5.1 | 2026-10-01 | Dispute propagation fixed (SIM-47): the escrow Lambda can write the order and delivery it mirrors a dispute onto (UpdateItem only); legs independent, order conflicts retried, failures logged as `DISPUTE_PROPAGATION_FAILED` and audited with the failed leg; response unchanged |
| 1.5.0 | 2026-09-30 | Live order payments (ESC-01): `POST /escrow/order-checkout` (server-generated hashed merch_order_id, one open checkout per order until 130 minutes after Telebirr answered, re-signed URL on retry, mapping written before Telebirr and never deleted, 400-day TTL, no Telebirr call 20 hours after `paymentSubmittedAt` or `createdAt`, each checkout bound to the order's payment attempt); the webhook funds a paid order checkout exactly once (payment-reference classification, status-conditional funding write, idempotent stock and escrow repair except on closed orders, inbox row, alarm tokens, a failed or malformed mapping lookup alarmed instead of dropped, `Failed` only audited, a refused payment keeps its verdict on redelivery, a payment from an earlier attempt never funds); `/escrow/initiate` refuses an order that is already funded or paid (`ESCROW_024`) even without its escrow record; order escrows take the server-only `telebirrPaymentOrderId` as hold reference; new codes `ESCROW_030` to `ESCROW_033`; header version corrected (it still read 1.3.0) |
| 1.4.0 | 2026-09-29 | Tiered step-up on initiate: a fresh password up to 10,000 ETB (`AUTH_040`, now for every amount), a fresh Fayda login above it (`AUTH_041`), `503 ESCROW_012` when the buyer profile cannot be read for the Fayda check; clients keep the server's step-up code; dispute step-up unchanged |
| 1.3.1 | 2026-09-26 | Double-charge precedence (SIM-24): an initiate on a delivery whose order-funded escrow is already linked to it returns `ESCROW_024` (field `orderId`), not `ESCROW_009`; clients read the `ESCROW_*` code from the error envelope and show `ESCROW_024` its own message |
| 1.3.0 | 2026-09-26 | Settlement authority and identity gates: `/escrow/release` and `/escrow/refund` on the admin authorizer (admin group), `dispute_resolved` settled by the hourly sweep, `AUTH_040` step-up on initiate and dispute, payouts resolved to the party's registered phone with `ESCROW_029` when unverified, webhook `merch_code`, fail-closed timestamp and payer binding (`webhook_payer_mismatch`, `webhook_payer_unbound`) |
| 1.2.5 | 2026-09-25 | Refund-pending labelling (SIM-20): history rows and `EscrowDto` carry `refundPending` + `refundReason` (rows also `expiresAt`); a cancelled order's still-escrowed money reads "Refund pending" with the order as reference instead of "Funds held safely" with a blank delivery |
| 1.2.4 | 2026-09-25 | Default history scoping (SIM-16): without `role=` the history is scoped by the token's role claim instead of always the buyer GSI, so a farmer (including the farmer of a self-pickup, whose escrow is order-funded with no delivery) and a trucker read their own rows unfiltered |
| 1.2.3 | 2026-09-25 | Trucker money view (SIM-15): `EscrowDto` and history rows carry `truckerPayout` + `truckerId`, history rows carry `farmerPayout`; `role=trucker` reads the new sparse `TruckerStatusIndex` GSI; the order's trucker is a party for `GET /escrow/{id}` and `by-delivery`; delivery linkage backfills a missing `truckerId` from the order; clients show each role its own leg |
| 1.2.2 | 2026-09-25 | Stock restore (SIM-13): the refund sweep's cancel of a stale-funded order (and a participant's reject/cancel of a funded order) returns the listing's quintals, idempotent per order; the escrow cron Lambda gains `CROPS_TABLE` |
| 1.2.1 | 2026-09-25 | Dispute propagation (SIM-12): an escrow dispute mirrors onto the order (`delivered → disputed`, blocks ratings) and the delivery; a delivery-level dispute freezes the escrow; failures land in the audit ledger as `dispute_propagation_failed` |
| 1.2.0 | 2026-09-25 | Initiate hardening (SIM-04): buyer role only (`ESCROW_025`), caller must be the delivery's buyer (`ESCROW_026`), `farmerId` must be the delivery's farmer (`ESCROW_027`), `amount` must equal the linked order's `orderTotal` (`ESCROW_028`), client `orderId` may only echo the delivery's; payout split always taken from the linked order |
| 1.1.0 | 2026-07-12 | Order-funded escrow leg: record created at order funding (deterministic escrow-order-{orderId} key, source=order, no deliveryId until linkage), delivery linkage + self-heal, reject/cancel refund scheduling via expiresAt, order-aware auto-refund, ESCROW_024 double-charge guard, EscrowSummaryDto gains orderId/buyerId/farmerId |
| 1.0.0 | 2026-02-17 | Initial contract: escrow lifecycle, endpoints, data models, Telebirr integration, idempotency, DynamoDB design, security, Ethiopian context, offline behavior, error handling, automated processes |

---

**Last Updated**: 2026-10-10
**Version**: 1.12.0
**Status**: Draft (Phase R4)
