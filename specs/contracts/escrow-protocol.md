# Escrow Protocol Contract (Telebirr Integration)

## Version
1.32.0

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
| `held` | Frozen for fraud review by a settlement sweep; only an admin release or refund leaves it (1.7.0; written from 1.22.0, FRD-03) | No |

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
| `escrowed` | `held` | Settlement sweep finds a party suspended (1.22.0, FRD-03) or at risk tier CRITICAL (1.27.0, FRD-04) | System |
| `dispute_resolved` | `held` | Same trigger at dispute settlement (1.22.0, FRD-03) | System |
| `held` | `released` | Admin decision, admin group only, with a reason (1.22.0, FRD-03) | Admin |
| `held` | `refunded` | Admin decision, admin group only, with a reason (1.22.0, FRD-03) | Admin |

Invalid transitions return `422 Unprocessable Entity` with error code `ESCROW_INVALID_TRANSITION`.
Since 1.9.0 a `disputed` escrow is never released or refunded directly: the admin records a
verdict first, and the verdict decides the direction (see Additions in 1.9.0).

**Clients treat `status` as an open set** (1.7.0). A client must not fail on a status value it does
not know: it shows a neutral state with no actions and keeps the raw value. Shipped mobile clients
throw on an unknown value and the web client maps it to `initiated`; FRD-06 fixes both before `held`
is enabled (see Additions in 1.7.0). Apps that do not send `X-App-Build: 2` or higher read `held` as
`escrowed` and `dispute_resolved` as `disputed` on the party escrow routes (see Additions in 1.31.0).

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
  (`accepted` … `disbursing`) gets `expiresAt` pushed +48h instead, never past funding +
  21 days (1.13.0, ESC-06: at the cap the order enters the admin stuck-order queue; see
  §Additions in 1.13.0). Fail-safe: if
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

Since 1.16.0 this `/escrow/initiate` path is **not usable with live Telebirr**: its merch_order_id
is the client idempotency key (`{userId}:{entityType}:{entityId}:{action}:{timestamp}`), which breaks
Telebirr's documented `^[A-Za-z0-9]+$` (ESC-45). Since 1.18.0 the server **refuses it up front with
`422 ESCROW_039`** whenever its gateway is live Telebirr (FUP-36, §Additions in 1.18.0): nothing is
written, and the buyer pays the order through the order checkout (§Additions in 1.5.0). The mock
gateway and the local simulator are unchanged. The webhook still serves this path: only `Failed`,
`Failure` or `Expired` cancel the escrow (a `Pending` or `Paying` notification changes nothing), and
only a notification at most 300 s old is processed.

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
| `422` | `ESCROW_039` | Live Telebirr: this path is refused before any read or write; pay the order through `POST /escrow/order-checkout` (1.18.0, FUP-36) |
| `429` | `ESCROW_011` | Rate limit exceeded |
| `403` | `AUTH_040` / `AUTH_041` | Step-up required (1.4.0 payment tiers; from 1.7.0 also raised by the caller's risk tier, built in 1.27.0, FRD-04). See Additions in 1.3.0, 1.7.0 and 1.27.0 |
| `403` | `FRAUD_ACCOUNT_HELD` | Caller's risk tier is CRITICAL; nothing held (1.7.0; built in 1.27.0, FRD-04) |
| `410` | `ACCOUNT_004` | The caller's account is being or has been deleted; carries `error.deletionState`; nothing read or held (1.28.0, ACC-02) |
| `422` | `ESCROW_030` | The buyer, farmer or trucker of the delivery's order is being or has been deleted (1.28.0, ACC-02) |
| `500` | `ESCROW_012` | Internal server error |
| `503` | `ESCROW_012` | Buyer profile could not be read for the Fayda step-up check (amount above 10,000 ETB, 1.4.0; from 1.27.0 also at any amount when the caller's risk tier is HIGH); nothing held, safe to retry |
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
| `reason` | string | For a `held` escrow | 10-500 chars (1.22.0) | Why the admin releases a held escrow; audited with the decision, ignored otherwise |

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
| `400` | `ESCROW_VALIDATION` | A `held` escrow without a `reason` of 10 to 500 characters (1.22.0) |
| `403` | `ESCROW_015` | Unauthorized to release (not system/admin), or a `held` escrow from outside the admin group (1.22.0) |
| `404` | `ESCROW_016` | Escrow not found |
| `503` | `ESCROW_012` | The order or delivery behind a verdict could not be read; nothing moved, retry (1.9.0); from 1.22.0 also: the decision on a `held` escrow could not be audited |
| `422` | `ESCROW_INVALID_TRANSITION` | Escrow is not `escrowed` or `dispute_resolved`, or its verdict is `buyer_wins` (1.9.0; before 1.9.0 `disputed` was accepted too); from 1.22.0 an admin in the admin group may also settle `held`, checked on the status it was held from, and the system caller never (FRD-03); from 1.17.0 an admin release of a plain `escrowed` order escrow whose order is not `delivered` or `disbursing` (nor left behind a delivered delivery), no override |
| `503` | `ESCROW_012` | From 1.17.0 also: the order behind an admin release could not be read, or changed while it was advanced to `delivered`; nothing moved, retry |
| `202` | `ESCROW_034` | The release is waiting in the manual payout queue and this call made no payout (1.8.0); with reason `GATEWAY_OUTCOME_UNKNOWN` an earlier gateway call may have paid (1.10.0) |
| `502` | `ESCROW_013` | Telebirr gateway error (a definite refusal, retried by the next sweep); from 1.10.0 also an unknown outcome whose queue write failed: not retried, the lock stays |
| `503` | `ESCROW_014` | Telebirr service unavailable |

---

### 3. POST /escrow/dispute

Buyer or farmer raises a dispute. Must be within 48 hours of delivery confirmation.

From pickup onward only (1.21.0, FUP-16): before the goods are picked up the dispute is refused with
`422 ESCROW_041`; see "Additions in 1.21.0".

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
`order:conflict`, `delivery:[InvalidOperationException]`); since 1.26.0 that line is alarmed
(see "Dispute tokens" in "Additions in 1.8.1"). The dispute still answers
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

No dispute on a scheduled refund (1.25.0, FUP-32): an `escrowed` escrow that carries `refundReason`
(its order was cancelled or rejected, or an admin refund is armed) answers `409
ESCROW_INVALID_TRANSITION` and writes nothing, whether the request reads it so or the refund is armed
between its read and its write (the freeze write requires `attribute_not_exists(refundReason)`). A
freeze write that meets a transport cancel's transaction in flight (`TransactionConflictException`)
is answered like a lost race. See "Additions in 1.25.0".

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
| `422` | `ESCROW_041` | The goods are not picked up yet: no `deliveryConfirmedAt`, the order before `pickup_confirmed` and its delivery before `pickedUp` (1.21.0) |
| `503` | `ESCROW_012` | The order or delivery behind the escrow could not be read for the pickup check; nothing was written, retry (1.21.0) |
| `409` | `ESCROW_INVALID_TRANSITION` | A release or refund holds the settlement lock (1.9.0), or the escrow changed while the dispute was written and is still `escrowed`: try again (1.12.0), or a refund is scheduled on it (`refundReason`), also when it was armed between the read and the write (1.25.0) |
| `422` | `ESCROW_INVALID_TRANSITION` | Escrow is in any state but `escrowed` or `disputed` (a `held` escrow is refused too, 1.7.0), also when it moved there between the read and the write (1.12.0) |
| `500` | `ESCROW_012` | Internal server error |
| `503` | `ESCROW_012` | The escrow (or an admin caller's profile) could not be read for the step-up check; the dispute did not run and nothing was written, safe to retry (1.24.0; before, a Lambda error that API Gateway answered `502` without a code) |
| `410` | `ACCOUNT_004` | The caller's account is being or has been deleted; carries `error.deletionState`; nothing written (1.28.0, ACC-02) |

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
| `reason` | string | No | 0-500 chars; 10-500 with `overrideOrderMismatch` and, required, for a `held` escrow (1.22.0) | Explanation for audit trail |
| `idempotencyKey` | string | Yes | Format: `{actor}:{escrowId}:{action}:{timestamp}` | Prevents duplicate refunds |
| `overrideOrderMismatch` | boolean | No | Admin group only (1.9.0) | Refund a `buyer_wins` verdict although its order cannot follow it (goods already moving); audited with the admin and the reason, the order is flagged. Deprecated for verdicts from 1.21.0 (record `return_to_farmer` on the verdict instead); still the tool for an ESC-09 Part B refund of a plain `escrowed` escrow |

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
| `400` | `ESCROW_VALIDATION` | `overrideOrderMismatch` without a `reason` of 10 to 500 characters (1.9.0), or a `held` escrow without one (1.22.0) |
| `403` | `ESCROW_021` | Unauthorized to refund (not admin/system), or `overrideOrderMismatch` from outside the admin group (1.9.0), or a `held` escrow from outside the admin group (1.22.0) |
| `404` | `ESCROW_016` | Escrow not found |
| `503` | `ESCROW_012` | The order or delivery behind a verdict could not be read; nothing moved, retry (1.9.0) |
| `422` | `ESCROW_INVALID_TRANSITION` | Escrow is not `escrowed` or `dispute_resolved`, its verdict is `farmer_wins`, or its order cannot follow the verdict and no override was given (1.9.0; before 1.9.0 `disputed` was accepted too); from 1.22.0 an admin in the admin group may also settle `held`, except when its order cannot follow the refund (`RISK_HOLD_ORDER_MISMATCH`, §Additions in 1.22.0), and the system caller never (FRD-03); from 1.17.0 an admin refund of a plain `escrowed` order escrow whose goods are moving or delivered (no `overrideOrderMismatch`), or whose order is anything but `funded`, `accepted`, `cancelled`, `rejected`, `pickup_confirmed`, `in_transit` or `delivered` (no override) |
| `503` | `ESCROW_012` | From 1.17.0 also: before an admin refund of a plain `escrowed` order escrow, its order could not be read, or a pre-pickup order could not be cancelled or its stock given back; nothing moved, retry |
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
| `status` | enum | Yes | See State Machine; clients tolerate unknown values (1.7.0) | Current escrow state; `held` written from 1.22.0 (FRD-03) |
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
| `holdReason` | string | No | Admin responses only (1.7.0; written from 1.22.0, FRD-03) | `fraud_confirmed` (1.29.0, FRD-05), `account_suspended` (1.22.0), `risk_critical` (1.27.0, FRD-04) |
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
| `disposition` | string | No | Admin responses only (1.21.0); `return_to_farmer`, `delivered_anyway`, `settle_now`, `wait_for_delivery` | The admin's disposition recorded on the verdict |
| `dispositionReason` | string | No | Admin responses only (1.21.0) | The admin's reason for it |
| `dispositionBy` | string | No | Admin responses only (1.21.0) | Who recorded it |
| `dispositionAt` | string (ISO 8601) | No | Admin responses only (1.21.0) | When it was recorded |
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
| **Risk tier gate** | 1.27.0 (FRD-04): MEDIUM and HIGH add step-up on initiate at any amount; CRITICAL refuses initiate and order checkout with `FRAUD_ACCOUNT_HELD`. `specs/architecture/fraud-detection-architecture.md` section 17 |
| **Fraud hold** | 1.22.0 (FRD-03): a settlement sweep moves the escrow of a suspended party to `held` instead of paying out, behind `FRAUD_HOLD_ENABLED`; a CRITICAL party too from 1.27.0 (FRD-04). Same document, section 18 |

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
3. If yes, call POST /escrow/release with `trigger = delivery_confirmed`; from 1.15.0 only when the
   delivery is `delivered` and the order agrees (§Additions in 1.15.0), and the sweep pages through every
   escrowed row
4. Log audit entry with `actor = system`
5. From 1.22.0 (FRD-03): between steps 2 and 3 the sweep checks every party's suspension (and from 1.27.0 each party's risk tier, FRD-04); on a trigger the escrow becomes `held` and no money moves (§Additions in 1.22.0 and 1.27.0)

### Auto-Refund (Cron)

A scheduled Lambda runs every hour:
1. Query `StatusExpiresIndex` for escrows with `status = escrowed` AND `expiresAt < now`
2. For each expired escrow, call POST /escrow/refund with `trigger = delivery_timeout`; order escrows
   follow their order (1.1.0, 1.13.0), and from 1.15.0 an `/escrow/initiate` escrow follows its delivery
   and order and is never refunded while its goods move (§Additions in 1.15.0)
3. Log audit entry with `actor = system`
4. Notify buyer and farmer via push notification
5. From 1.22.0 (FRD-03; the risk tier from 1.27.0, FRD-04): the same risk gate runs after the order-aware checks and before a stale `funded` order is cancelled or restocked

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
When the escrow cannot be read for that check the response is `503 ESCROW_012` (nothing
written; safe to retry; 1.24.0, FUP-44). See auth-api.md 2.4.0.

### Payout destination and identity at release
Release and refund send money to the **registered phone** of the party, read from the
profile at settlement time, never to a client-supplied number. Before the settlement lock:

| Condition | Result |
|-----------|--------|
| farmer profile missing | `422 ESCROW_029` |
| farmer phone not verified | `403 ESCROW_029` |
| amount above 10,000 ETB and farmer not Fayda-verified | `403 ESCROW_029` |

Every blocked release writes a `release_blocked_kyc` audit entry whose metadata names the reason (since
1.15.0 `code=ESCROW_029;reason=<reason>`, once per reason for the hourly sweeps, on every attempt for an
admin).
| trucker leg with an unverified trucker | farmer leg proceeds, trucker leg stays `truckerPayoutPending`; since 1.11.0 the escrow stays `released` and the hourly trucker retry pays the leg once the trucker passes the gate |

A blocked release leaves the escrow untouched for an admin.

### Webhook hardening
Since 1.16.0 (ESC-45) the notification is read in the shape Ethio Telecom documents: one flat
JSON object (`notify_url, appid, notify_time, merch_code, merch_order_id, payment_order_id,
total_amount, trans_id, trans_currency, trade_status, trans_end_time, callback_info, sign,
sign_type`), see §Additions in 1.16.0. A notification with a missing or unparseable
`notify_time` is rejected. `merch_code` must equal the configured merchant code. The documented
notify carries **no payer phone**, so `TELEBIRR_REQUIRE_PAYER_MATCH` must stay off; if a payer
field (`msisdn`) ever appears it is compared with the buyer's registered phone: a mismatch is
audited as `webhook_payer_mismatch` and, with `TELEBIRR_REQUIRE_PAYER_MATCH=true`, rejected; a
missing payer while a match is required is audited as `webhook_payer_unbound` and rejected.
Since 1.32.0 (FRD-08) the payer phone is compared in the users table's form: Telebirr's
`251XXXXXXXXX` is read as `+251XXXXXXXXX`, as are `+251...`, `00251...` and `09...`. A mismatch also
writes a fraud signal on the buyer that the escrow or checkout row names, only for a verified
notification and only when that buyer's row and phone were read: `TELEBIRR_PAYER_MISMATCH`
(weight 0, logged for review) when the payment is applied, `TELEBIRR_PAYER_REFUSED` (soft, weight
5, counted once per order escrow) when `TELEBIRR_REQUIRE_PAYER_MATCH` refuses it. A notification that fails its signature, body,
source IP or merchant check is logged and writes no signal on anyone, since anyone can post to the
route; a missing payer writes none either. The webhook's answer and the payment outcome are the
same whether the signal write works or fails. The phone normalization does change one outcome with
`TELEBIRR_REQUIRE_PAYER_MATCH` on: a payment from the buyer's own wallet in the `251XXXXXXXXX` form
was refused as a mismatch before 1.32.0 and is applied now (the documented notify carries no payer
field, so this applies only if Telebirr adds one). `ESCROW_025` on `POST /escrow/initiate` and
`POST /escrow/order-checkout` writes `ROLE_FORBIDDEN` (weight 25) on the caller when the token
names another role (none without a role claim); the 403 is unchanged. Signals and weights:
`specs/architecture/fraud-detection-architecture.md` 3.9.0, section 15.

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
| `422` | `ESCROW_030` | The order cannot be paid online (missing farmer, or buyer and farmer are the same account; from 1.28.0 also a buyer, farmer or trucker who is being or has been deleted, checked after the other order checks and before the slot, so an open checkout is not returned either) |
| `410` | `ACCOUNT_004` | The caller's account is being or has been deleted; carries `error.deletionState` (1.28.0, ACC-02) |
| `400` | `ESCROW_001` | `orderTotal` is outside what Telebirr takes in one payment (100 to 500,000 ETB) |
| `409` | `ESCROW_031` | No hosted checkout in this environment (mock payments) |
| `409` | `ESCROW_032` | A checkout for this order is already open but cannot be returned (Telebirr's answer was lost, or another request is opening it); retry after `openUntil` or in a moment |
| `429` | `ESCROW_011` | Payment rate limit exceeded (same velocity check as initiate). Every attempt that reaches Telebirr counts (a new checkout, or `ESCROW_013`); a returned open checkout does not |
| `502` | `ESCROW_013` | Telebirr refused the payment (retry now), or did not answer (the checkout stays blocked until its window ends) |
| `503` | `ESCROW_012` | Payments are temporarily unavailable |
| `403` | `FRAUD_ACCOUNT_HELD` | Caller's risk tier is CRITICAL; checked after the role and body checks and before the velocity check, the order read and any Telebirr call (1.7.0; built in 1.27.0, FRD-04) |

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
| `webhook-inbox:{merchOrderId}:{paymentOrderId}:{tradeStatus}` | inbox | one verified notification and what became of it (30-day TTL); since 1.16.0 also `orderId`, and while not final the replay queue (`inboxReplay`, `inboxReplayAt`, `replayAttempts`) and the verified payment fields (`notificationBiz`), see §Additions in 1.16.0 |

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
| Already records this `payment_order_id` | Redelivery: stock and escrow are repaired if an earlier delivery did not finish them (both idempotent per order); nothing is funded twice, and the payment is not judged again (the buyer may have changed phone since). A closed order (`rejected`, `cancelled`, `payment_failed`, `disbursing`, `settled`, `dispute_resolved`) gets no repair: no new escrow, no second stock decrement. In every status the transaction-audit `payment_verified` row is written if the order has none (1.19.0, see below) | `applied_again` |
| Records a different `payment_order_id` | `ORDER_PAYMENT_DUPLICATE`: a second payment; audited as `order_payment_duplicate`, no state change | `duplicate` |
| No reference and not `payment_submitted` | `ORDER_PAYMENT_UNMATCHED` (for example a cancelled order); audited as `order_payment_unmatched` | `unmatched:status_{status}` |
| `payment_submitted`, but the payment does not fit | `ORDER_PAYMENT_UNMATCHED`: `trans_currency` is not `ETB`, `total_amount` differs from the checkout amount (also audited as `webhook_amount_mismatch`), the payer check (§Webhook hardening) against the checkout's buyer refuses it, the order's buyer or `orderTotal` no longer match the checkout, or the checkout was opened in an earlier payment attempt (its `paymentSubmittedAt` differs from the order's) | `unmatched:currency`, `:amount`, `:payer`, `:order_changed`, `:stale_checkout` |
| `payment_submitted` and the payment fits | Funded: one write conditioned on `status = payment_submitted` sets `funded`, `paymentVerifiedAt` and the server-only `telebirrMerchOrderId`, `telebirrPaymentOrderId`, `telebirrTransId`; then the listing stock is taken (idempotent per order), the order escrow is created with the Telebirr payment as its hold reference, a transaction-audit `payment_verified` row is written (actor `telebirr`, keyed by `paymentVerifiedAt`, 1.19.0), and the checkout is closed as `paid` | `applied` |

If the conditional write loses a race, the order is read again (strongly consistent) and
classified once more; a conflict is never treated as done. A payment that is not applied
stays with the platform for a manual refund (automatic duplicate refunds are ESC-20); since
1.20.0 the admin records that refund with `POST /admin/payments/{orderId}/refund-manual`. Every
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

Since 1.19.0 (ESC-47) the redelivery also writes a missing `payment_verified` row. The row's key
is the order id and the order's `paymentVerifiedAt` (the time of the funding write), and it is
written with a conditional put (`attribute_not_exists`), so the funding delivery and every
redelivery write the same row and only the first lands. A redelivery does this after its stock
and escrow repair, so a slow audit table never takes the time that repair needs. Before writing, it
reads the order's transaction-audit trail (a `Query` on the base table, granted to the webhook
Lambda) and writes nothing if a `payment_verified` row is there under any timestamp (servers before
1.19.0 wrote it at the time of the write) or if the trail cannot be read. A failed read or write,
on any delivery, logs `ORDER_PAYMENT_BOOKKEEPING_FAILED step=payment_verified_audit` with the order
and payment ids (`reason=write_failed` for the write), and the next redelivery tries again. This
runs in every order status, closed ones included, because the row records a funding that happened.
A payment that is never redelivered keeps the gap; its escrow-audit trail still has the payment.

Since 1.16.0 the webhook answers `500` when a verified notification is not durably recorded or
not applied for a reason a retry can fix, and replays inbox rows left pending (§Additions in
1.16.0). The lasting record of a payment that was not applied is its escrow-audit row
(`order_payment_unmatched`, `order_payment_duplicate`, `order_payment_unprocessed`); the inbox
row expires after 30 days, and a payment whose mapping or inbox row could not be read has only
its log line (and was answered `500`).

---

## Additions in 1.7.0 (fraud hold and risk tiers; built in 1.22.0, FRD-03, and 1.27.0, FRD-04)

Design: `specs/architecture/fraud-detection-architecture.md` 3.0.0, sections 14 to 23. This section is
the contract the FRD tasks implement. 1.22.0 builds the `held` status for a suspended party (FRD-03,
§Additions in 1.22.0); 1.27.0 builds the risk tier and `FRAUD_ACCOUNT_HELD` (FRD-04, §Additions in
1.27.0); 1.29.0 builds `fraud_confirmed` (FRD-05, §Additions in 1.29.0). "The sweep" below means the
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
  logs the same token with its reason. With no `FRAUD_SIGNALS_TABLE` configured the sweep logs
  `RISK_TABLE_UNCONFIGURED` once per run, flag on or off (1.27.0).
- **Client compatibility**: `held` is a value shipped clients do not parse (mobile throws, web shows
  `initiated`), as is `dispute_resolved` already. FRD-06 ships the tolerant parser and the
  "Under review" badge for mobile and web; the flag is turned on only after FRD-06 is on web and in
  both stores, and after older clients are protected on the escrow reads (`GET /escrow/{id}`,
  `GET /escrow/by-delivery/{deliveryId}`, `GET /escrow/history`): either a minimum app version gate
  that covers those reads (ESC-25 as written gates order commands only) or a server-side mapping
  that answers older clients with `escrowed` instead of `held`. Built as the server-side mapping in
  1.31.0 (FUP-61); the flag is on in dev from then, off in staging and prod.

---

## Additions in 1.23.0 (client handling of `held` and `FRAUD_ACCOUNT_HELD`, FRD-06)

Mobile and web together, no server change. This is the client half of the `held` rollout condition
in §Additions in 1.7.0 ("Client compatibility"); protecting installs older than FRD-06 on the escrow
reads was still owed before `FRAUD_HOLD_ENABLED` is turned on (built in 1.31.0).

- **Status is an open set.** Every escrow the client reads (`GET /escrow/{id}`,
  `GET /escrow/by-delivery/{deliveryId}`, `GET /escrow/history`, the initiate answers and the
  payment rows in `/sync` conflict data) goes through one tolerant parser: a known name maps to
  itself, `dispute_resolved` reads as `disputed` (the money is still frozen and the party has
  nothing to do), and any other value reads as a neutral "Status Unknown" with no actions and a hint
  to update the app. Mobile no longer fails the history refresh or the detail on such a value, and
  web no longer shows it as `initiated`. Payment rows are never sent back, so the server's value is
  never overwritten.
- **`held` reads "Under Review"** (en, am, om) on the escrow badge, detail and timeline, with no
  actions (a dispute needs `escrowed`). Order detail and the order list show the same badge to the
  buyer, the farmer and the trucker when the order's escrow is `held`: the detail reads
  `GET /escrow/escrow-order-{orderId}` (statuses that can hold money only), the list reads
  `GET /escrow/history?status=held&role=<caller's role>&limit=50` once per load. The server filters
  within its page, so a held escrow older than the party's 50 newest is flagged on the order detail
  but not in the list. While held, the farmer is not offered Accept on a funded order and the buyer
  is not offered the transport-failure cancel (the server refuses both); Reject and the cancel of a
  funded order stay. A read that fails shows no badge; the server still refuses what `held` forbids.
- **`FRAUD_ACCOUNT_HELD`**: the `403` on `/escrow/initiate` and `/escrow/order-checkout` keeps its
  code (it was shown as "sign in again" and as "not authorized") and shows "Your account is under
  review, so this action is paused. Contact support." A queued `/escrow/initiate` intent (mobile,
  offline payment) fails at once instead of being retried three times, and the same text is shown.
- **`AUTH_041` on `POST /escrow/dispute`** opens the Fayda login and sends the dispute again (the
  server sends only `AUTH_040` there today). The Fayda prompt no longer names an amount.

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
metadata `paymentOrderId=<Telebirr payment_order_id>;amount=<n>`, actor = the admin id.
Built in 1.20.0 (FUP-02): the admin queue, the endpoint that writes this row and the exact
matching rule are in §Additions in 1.20.0.

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
  (`attribute_not_exists(settlementLock) AND attribute_not_exists(manualPayout)`) never holds it,
  and the cron queries already skip it. The reverse case is closed in 1.22.0: an admin release or
  refund of a `held` escrow that the gateway queues closes from `held` too. Any other later sweep
  that moves escrows by status must skip rows with `manualPayout = open` the same way.
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
DISPUTE_PROPAGATION_FAILED escrowId=<id> orderId=<id> deliveryId=<id> legs=<legs>   (1.26.0)
DELIVERY_DISPUTE_FROZEN escrowId=<id> orderId=<id> deliveryId=<id>   (1.26.0, a rate, not a failure)
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
(ESC-13)" in 1.8.0). `ADMIN_QUEUE_OVERDUE` is logged by the same run for each admin stuck-order
queue entry due for 24 hours, and for a capped escrow whose order is missing (1.13.0, ESC-06; due
time and `ageHours` in §Additions in 1.13.0).
Both must be logged from the escrow-cron Lambda, because their metric filters sit on its log
group; a producer in another Lambda (order-cron, for example) adds its own filter on that
Lambda's log group publishing the same metric.

The overdue line is a `LogWarning`: a log level above Warning on escrow-cron silences the
manual payout alarm.

### Dispute tokens (1.26.0, FUP-27)

`DISPUTE_PROPAGATION_FAILED` is logged by the dispute mirror (`EscrowService.MirrorDisputeAsync`,
through the money alarm sink, before the `dispute_propagation_failed` audit row) once per call in
which the delivery or the order did not follow a frozen escrow. Three Lambdas run the mirror, and
a filter on each of their log groups counts the line: escrow (`POST /escrow/dispute`, and its
repair of a dispute already frozen), sync (a delivery dispute) and escrow-cron (a sweep that freezes
a trade disputed before 1.12.0). `legs` is the failed-legs text through `MoneyAlarmLog.LogLegs`:
the characters `LogId` keeps, `_` as `-`, `,` as `.`, brackets dropped, cut at 200 characters (not
64, so three legs with long exception names survive): `delivery:[TimeoutException],order:conflict`
logs `delivery:TimeoutException.order:conflict`; the audit row keeps it raw. The money is frozen, so
nothing pays out; the order may still show `delivered` and stay open to ratings until a party's
repeated dispute (skipped while a manual payout or a settlement lock is open) or the admin verdict
(1.9.0, the operator's lever) moves it. Before 1.26.0 no filter counted the line.

`DELIVERY_DISPUTE_FROZEN` is logged by the sync Lambda (`LogWarning`) once a delivery dispute
froze its escrow (outcome `Frozen` in 1.12.0's freeze), before the delivery write. It is counted
into the existing `EscrowDispute` metric, so `${prefix}-escrow-dispute-rate` counts delivery
disputes through sync as well as `POST /escrow/dispute` requests (the escrow Lambda's
`ESCROW`/`DISPUTE` audit line, any outcome). Not logged: an escrow already `disputed` (counted when
it froze), a refused or undecided freeze (clients before sync-protocol 1.12.0 resend a refused
dispute on every reconnect), the repeat of a dispute on a delivery already `disputed`, and
escrow-cron's sweep freeze (both repair disputes from before 1.12.0). Both tokens contain `_` and
every id on both lines goes through `LogId`.

### Alarms (`infra/lib/stacks/monitoring-stack.ts`, all on the `${prefix}-alarms` topic)

| Alarm | Counts | Fires |
|---|---|---|
| `${prefix}-escrow-settlement-failures` | `ESCROW_SETTLEMENT_FAILED` in the escrow and escrow-cron log groups | any line in a 15 minute period, evaluated over 5 periods (75 minutes, so an hourly producer never leaves a gap); stays in ALARM while the hourly sweep keeps failing and clears by itself 75 to 90 minutes after the last line |
| `${prefix}-escrow-cron-errors` | AWS/Lambda `Errors` of `${prefix}-escrow-cron` | one error in a 5 minute period, evaluated over 4 periods (a crash and its two async retries stay one episode; clears 20 to 25 minutes after the last error): escrow-cron was killed (5 minute timeout, out of memory) or crashed. A run killed mid-settlement logs no token, so this alarm is the only signal; treat the escrows it was processing as unknown outcomes. The 1.10.0 recovery step parks an escrow whose lock is older than 15 minutes |
| `${prefix}-escrow-settlement-unknown-outcomes` (1.10.0) | `ESCROW_SETTLEMENT_FAILED` lines with `outcome=unknown` in the escrow and escrow-cron log groups | same as settlement failures; the escrow is or will be in Manual Payouts as `GATEWAY_OUTCOME_UNKNOWN`: never release or refund it by hand |
| `${prefix}-order-escrow-problems` | `ORDER_ESCROW_CREATE_FAILED`, `ORDER_ESCROW_LINK_FAILED`, `ORDER_ESCROW_REFUND_SCHEDULE_FAILED` in the sync log group (replaces `${prefix}-order-escrow-create-failed`) | any line in 15 minutes |
| `${prefix}-manual-payouts-overdue` | `MANUAL_PAYOUT_OVERDUE` in the escrow-cron log group | any line in 15 minutes, same hold and clear as settlement failures; a leg that stays blocked keeps it in ALARM (1.8.0 known limits). The admin acts on the Manual Payouts page (`/payouts`) |
| `${prefix}-trucker-payouts-overdue` (1.11.0) | `TRUCKER_PAYOUT_OVERDUE` in the escrow-cron log group | same as manual payouts overdue: a trucker leg still owed (not queued) more than 24 hours after it went pending; see Additions in 1.11.0 |
| `${prefix}-admin-queue-overdue` | `ADMIN_QUEUE_OVERDUE` in the escrow-cron log group | same; an entry that stays undecided keeps it in ALARM (1.13.0, runbook in `.ai/deployment.md`) |
| `${prefix}-dispute-propagation-failed` (1.26.0) | `DISPUTE_PROPAGATION_FAILED` in the escrow, sync and escrow-cron log groups | any line in 15 minutes, same hold and clear as settlement failures; runbook in `.ai/deployment.md` |
| `${prefix}-escrow-dispute-rate` (not a money alarm; sync counted since 1.26.0) | the escrow Lambda's `ESCROW`/`DISPUTE` JSON audit line and `DELIVERY_DISPUTE_FROZEN` in the sync log group, one metric | more than 5 in one hour |

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

From 1.21.0 (FUP-16) the endpoint decides the order and the delivery before it writes, and a verdict
they cannot follow is recorded only with a disposition (`ADMIN_015`, `ADMIN_016`); see
"Additions in 1.21.0".

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
  the goods moving needs the override. (Resolved in 1.21.0 by dispositions on the verdict.)
- `POST /escrow/dispute` still accepts a dispute before the delivery is confirmed (no
  `deliveryConfirmedAt`, so no window applies). Whether it should be refused is an open policy
  question. (Decided in 1.21.0: refused before pickup, `ESCROW_041`.)
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
- `held` joins the recovery statuses in 1.22.0 (FRD-03): an admin release or refund of a held escrow
  that died under its lock is parked like any other.
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
- Done in 1.17.0 (ESC-09): the order stays `disbursing` while the escrow is `released` and moves to
  `settled` only on `completed` (see Additions in 1.17.0).
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
   is an unknown outcome (retry). 1.25.0: an `escrowed` row with a scheduled refund (`refundReason`)
   is refused here instead (audit `dispute_refused_refund_scheduled`, metadata
   `deliveryId=<id>;refundReason=<reason>`), and the write also requires
   `attribute_not_exists(refundReason)`.
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
| manual payout, settlement in flight, not held, other delivery, refund scheduled (1.25.0) | `error` `DELIVERY_DISPUTE_REFUSED: <why> Contact support.` (permanent) | nothing (one escrow-audit refusal row) |
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

- Done in 1.15.0 (ESC-10, FUP-28): the auto-release sweep reads the order and delivery before it pays,
  and freezes a disputed trade it finds behind an `escrowed` escrow.
- `POST /escrow/dispute` before delivery confirmation freezes the money but leaves the order and
  delivery as they are (`disputed` only follows `delivered`); the decision to refuse it is FUP-16.

---

## Additions in 1.13.0 (21-day hold cap and the admin stuck-order queue, ESC-06)

Before 1.13.0 the auto-refund sweep pushed an order escrow's `expiresAt` 48 hours forward on every
run while its order was `accepted` to `disbursing`, with no end: when the trucker never came, the
buyer's money was held forever (M5). Now no order escrow is held past funding + 21 days by the
sweep; an admin queue entry and an alarm take over, and nothing moves the money by itself.

### The cap (auto-refund sweep, `source=order` escrows)

`capAt = escrow.createdAt + 21 days` (the escrow's `createdAt` is the order's `paymentVerifiedAt`);
an unreadable `createdAt` counts as capped.

| Order | Before `capAt` | From `capAt` |
|---|---|---|
| `funded`, `rejected`, `cancelled` (refundable) | refunded as before | refunded as before; the cap never blocks a refund |
| `accepted` ... `disbursing` (progressing) | `expiresAt = min(now + 48h, capAt)`, written only while the escrow is still a plain hold: `escrowed`, no `refundReason`, no settlement lock, no open manual payout (`TryExtendExpiryAsync`), so a refund scheduled meanwhile keeps its expiry | not extended; the order enters the admin queue |
| any other status (`disputed`, `payment_submitted`, ...) | skipped as before ("needs reconciliation") | the order enters the admin queue |
| missing order | skipped as before | no row to queue: the escrow is not stamped and stays in the sweep windows; from `capAt + 24h` every run logs `ADMIN_QUEUE_OVERDUE orderId=<id> reason=order_missing ageHours=<hours since capAt>` |

Entering the queue, in this order (each run repeats the idempotent writes, so a failed step is
repaired on the next run):
1. the order marker (below), conditional on the status the sweep read;
2. transaction-audit row `admin_queue_entered` on the order (system actor, only when the marker
   write landed) and the info line `ORDER_ESCROW_HOLD_CAPPED orderId=<id> escrowId=<id>` (not an
   alarm token);
3. escrow-audit row `hold_cap_reached` keyed on the entry's `adminQueuedAt` (`AppendIfAbsentAsync`,
   one row per entry however often the run repeats);
4. LAST, the escrow stamp `holdCapReachedAt` (escrowed to escrowed). It takes the escrow out of both
   sweep windows, so it is written only once the queue owns the order. When the marker write did
   not land and the order is not queued (it moved on since the read), nothing is stamped and the
   next run decides again.

`StatusExpiresIndex` queries for `escrowed` rows (release and refund sweeps) skip an escrow that
carries `holdCapReachedAt` unless it also carries `refundReason` (the buyer cancelled after a
transport failure, or the queue re-armed a refund) or `deliveryConfirmedAt` (the release sweep pays
it), next to the 1.8.0 manual payout filter. Paging goes on through filtered pages, so 150 capped
rows at the head of the window no longer starve the escrows behind them. Other statuses are not
filtered: a capped escrow that later moved on (`disputed`, `released`) stays visible to its sweep.

`EscrowDto.refundPending` / `EscrowSummaryDto.refundPending` stay false on a capped hold until a
refund is scheduled: its expiry is past, but the money is not on its way back.

### The queue (orders table)

Server-only order fields, written and removed without bumping `version` or `updatedAt` (like the
delivery slot claim), so a buyer's queued change never conflicts with them:
`adminQueue = "open"` (partition key of the sparse `AdminQueueIndex`), `adminQueuedAt` (sort key,
oldest first), `adminQueueReason` (`hold_cap`), `adminQueueEscrowId`. `GET /orders` and
`GET /admin/transactions/{orderId}` return `adminQueueReason` and `adminQueuedAt`; the admin
transaction detail shows them. ESC-16 adds more reasons to the same index; ESC-24 adds the admin
page and decision actions.

### The report (escrow-cron, every `auto_release_and_refund` run)

`ReportAdminQueueAsync` runs right after the manual payout report and before the sweeps. It reads
every entry (pages of 50, at most 20 pages) with its escrow (`adminQueueEscrowId`, else
`escrow-order-{orderId}`), both strongly consistent:
- **Cleared** only on escrow evidence: the escrow is gone or not `escrowed` (refunded, released,
  disputed), it waits in the manual payout queue, or it has `deliveryConfirmedAt`. Never on the
  order status alone. The clear is conditional on the `adminQueuedAt` that was read, and audited
  `admin_queue_cleared`.
- **Re-armed** when the order ended without its exit: `cancelled` or `rejected` with no
  `refundReason` on the escrow gets `ScheduleRefundForOrderAsync` (the refund sweep then sees it);
  otherwise an escrow linked to a delivery that is `delivered` gets the delivery stamp repair,
  whatever the order status says (it can lag the delivery).
  An entry whose exit is armed (a `refundReason`, or a repaired stamp) logs nothing.
- **Overdue** otherwise, once it has been due for 24 hours (`MoneyAlarmLog.QueueAlarmAfter`): one
  `ADMIN_QUEUE_OVERDUE orderId=<id> reason=<reason> ageHours=<n>` line per entry per run. An entry
  is due from its `adminQueuedAt`, or from the order's `pickupDeadlineAt` when the order is still
  `accepted` and that is later (a trip that departs after day 21 has not failed yet).
  **`ageHours` is the whole number of hours since the entry became due** (so it is at least 24 on
  every line). For `reason=order_missing` it counts from `capAt`.
- An entry that cannot be reviewed (a read throws) is never cleared, and logs the overdue line once
  its `adminQueuedAt` is 24 hours old. A failed index query logs `ESCROW_SETTLEMENT_FAILED
  escrowId=- action=sweep_failed outcome=failed code=admin_queue_query:<type>`.

The run summary gains `adminQueueEntered`, `adminQueueOverdue` and `adminQueueCleared`.

### Refund of a transport-failure cancel (order-lifecycle.md 1.15.0)

The buyer's cancel of an `accepted` order after a transport failure schedules the refund like any
cancel (`refundReason: order_transport_failed`; since 1.25.0 in the order cancel's own transaction,
see "Additions in 1.25.0"). Before refunding an order `cancelled` with
`cancelReason: transport_failed_buyer_cancelled`, the sweep makes sure the order's quintals are back
on its listing (`RestoreStockAsync`, idempotent per order), with the dispute verdict's rules: no
listing or a quantity that is not positive counts as done (never blocks the refund), a listing that
is gone counts as done, and a restore that keeps conflicting or throws logs
`ORDER_STOCK_RESTORE_FAILED` and skips this run's refund (the next run retries). Other cancel
reasons are unchanged.

### Not in 1.13.0 (known limits)
- No admin decision action yet (ESC-24): an entry for an `accepted` order past its pickup deadline
  waits for the buyer's in-app cancel; when the buyer is unreachable the alarm stays in ALARM.
  An admin must not refund an `accepted` order by hand (the order would stay `accepted` and a later
  delivery could not be paid).
- Goods moving at the cap (`pickup_confirmed`, `in_transit`) leave the queue only through a
  dispute and its verdict, or a confirmed delivery.
- ESC-16 replaces the cap with the T12 deadline and stops the sweep extending order escrows at all.

---

## Additions in 1.15.0 (sweep hygiene, ESC-10)

Before 1.15.0 every status sweep took the first 100 rows of its `StatusExpiresIndex` partition that
passed the filter, oldest `expiresAt` first, and skipped the ones it could not act on in memory. 100
such rows at the head (unconfirmed trades in flight, a farmer the payout gate blocks, an order in an
unexpected status) hid every row behind them for good, and a blocked release wrote a
`release_blocked_kyc` row every hour. An escrow paid through `POST /escrow/initiate` was refunded at
day 7 while its goods were on the road, and the auto-release never looked at the order or delivery.

### Paged sweeps (escrow-cron)

The auto-release, auto-refund and dispute-settlement sweeps (hourly) and the reconciliation (every 6
hours) page through their whole partition, one Query of 100 evaluated rows at a time. Skips that need
no read are part of the filter, next to the 1.8.0, 1.11.0 and 1.13.0 ones:

| Sweep | Query | Filter added in 1.15.0 |
|---|---|---|
| Auto-release | `status = escrowed` | `deliveryConfirmedAt` present, no `settlementLock` |
| Auto-refund | `status = escrowed`, `expiresAt <= now` | no `deliveryConfirmedAt`, no `settlementLock` |
| Dispute settlement | `status = dispute_resolved` | no `settlementLock` |
| Reconciliation | `status = initiated` | none |

A locked row belongs to the 1.10.0 recovery step. The window check and every other skip stay in the
sweep; a row it skips no longer hides the rows behind it.

Budget per sweep and run: at most 50 pages, at most 1,000 rows that needed a read or a write (rows
skipped on their own attributes do not count), and no new row once the run is 90 s old (auto-release),
135 s (auto-refund), 165 s (dispute settlement) or 180 s (reconciliation), or once the Lambda has less
than the trucker retry's 90 s reserve plus 30 s left. The limits count from the start of the run, so
the reports and the recovery step before the sweeps count too and the trucker retry (last) keeps its
time. A sweep a limit stops logs `ESCROW_SETTLEMENT_FAILED escrowId=- action=sweep_truncated
outcome=failed code=<sweep>:<pages|rows|time>` (the settlement-failures alarm) and is named in the run
summary's `sweepsTruncated`; the rows it did not reach wait for a later run (no cursor; ESC-16's deadline
index replaces the partition walk). A failed page logs `sweep_failed` with the sweep's code as before
(`release_query`, `refund_query`, `dispute_query`) and ends that sweep only; the reconciliation still
answers 500. In the reconciliation, one row no longer stops the others: a cancel that lost to the
webhook (the escrow moved on) is an Information line, any other failed cancel logs
`ESCROW_SETTLEMENT_FAILED .. action=reconciliation_exception outcome=failed`.

### A block is stored and audited once

Server-only escrow attributes:

| Attribute | Meaning |
|---|---|
| `settlementBlockReason` | why the hourly sweeps cannot settle this escrow: `release:payout:<reason>` (the 1.3.0 farmer payout gate: `profile_missing`, `phone_unverified`, `fayda_required`), `release:trade:<reason>` or `refund:trade:<reason>` (the order and delivery check below) |
| `settlementBlockReasonAt` | ISO time the reason was first stored: the timestamp, and so the key, of its one audit row |

The reason is written by compare-and-set (only when missing or different, on the status the sweep read,
unlocked, not queued); the version and `updatedAt` do not move. The audit row is written with
`AppendIfAbsent` at `settlementBlockReasonAt`, so later runs that see the same reason write nothing new
(a row a crash lost is written then). A reason that changes gets one row per change. The `released` and
`refunded` status writes and a closed manual payout remove both attributes; a gate that passes removes a
reason of its own gate (`<op>:<gate>:` prefix) only.

- `release_blocked_kyc`: from the sweeps once per reason, metadata `code=ESCROW_029;reason=<reason>`. An
  admin's own `POST /escrow/release` that the gate blocks is audited on every attempt, with the same
  metadata. The answer is unchanged (`403` or `422 ESCROW_029`). The cron logs
  `ESCROW_RELEASE_BLOCKED escrowId=<id> code=ESCROW_029 sweep=<sweep>` at Information (was an error
  line) and counts `releaseBlocked`.
- `auto_release_blocked`, `auto_refund_blocked` (new audit actions, `actor: system`, metadata
  `reason=<reason>;orderStatus=<status>;deliveryStatus=<status>`): a block of the check below. The run
  whose write creates the block's audit row also logs one `ESCROW_SETTLEMENT_FAILED escrowId=<id>
  action=auto_release_blocked|auto_refund_blocked outcome=failed code=<op>:trade:<reason>` line (a
  person must look), never again for the same reason; a reason whose write landed but whose answer was
  lost gets its row and its line on the next run. Every run logs `ESCROW_SETTLEMENT_BLOCKED
  escrowId=<id> op=<op> reason=<reason>` at Warning (not an alarm token) and counts `releaseBlocked` or
  `refundBlocked`.
- A definite gateway refusal (`release_failed`, `refund_failed`, `trucker_release_failed`) is still one
  row per attempt: each is a real gateway call.

### The auto-release reads the order and delivery (FUP-28)

Before a release the hourly sweep reads the escrow's delivery and, when it names one, its order, both strongly
consistent. A read that throws fails the row (`auto_release_exception`, alarm) and nothing is paid. In
order:

| Delivery | Order | Result |
|---|---|---|
| no delivery repository, or an `orderId` and no order repository | | blocked `repo_unavailable` |
| | the escrow is not the order's own and `escrow-order-{orderId}` holds or paid the money (`initiated`, `escrowed`, `released`, `disputed`, `dispute_resolved`) | blocked `double_escrow`, before any other read or a freeze |
| the escrow names none / the row is missing | | blocked `no_delivery` / `delivery_missing` |
| any | `dispute_resolved`, or a `disputed` delivery with `disputeResolution` | blocked `dispute_resolved` (never frozen) |
| `disputed` | any | frozen (below) |
| `delivered` | `disputed` | frozen (below) |
| `pending`, `pickedUp`, `inTransit`, `cancelled` | | blocked `delivery_<status>` |
| `delivered` | the escrow names one and it is missing | blocked `order_missing` |
| `delivered` | `created`, `pending_payment`, `payment_submitted`, `payment_failed`, `funded`, `rejected`, `cancelled`, `settled`, or a status the sweep does not know | blocked `order_<status>` |
| `delivered` | none, `accepted`, `pickup_confirmed`, `in_transit`, `delivered`, `disbursing` (the order may lag the delivery) | released as before (then the 1.3.0 payout gate) |

This check belongs to the hourly sweeps. The admin `POST /escrow/release` and `POST /escrow/refund`
have their own order checks (ESC-09, §Additions in 1.17.0). Every order status of the order state machine
has a class in the sweep (a server test enforces it, so a new status fails the build until it is classified).

**Frozen.** A disputed delivery or order behind an `escrowed` escrow is a row the 1.12.0 freeze never
reached. The sweep freezes it through the 1.12.0 delivery dispute path (`escrowed -> disputed`, the
`dispute` audit row, `disputedBy` the delivery's or order's, else `system`) and, like every dispute path,
moves the delivery and order to `disputed` after it. Only a freeze of this very escrow counts: that path
prefers `escrow-order-{orderId}`, so when it lands on another escrow, or refuses
(`freeze_other_escrow`, `freeze_other_delivery`, `freeze_not_held`, `freeze_no_escrow`), the escrow is
blocked with that reason, and a stored refusal of these is not asked again. A lock or queue entry that
appeared since the read defers to the next run. Without a delivery the escrow is blocked
`order_disputed`. Counted in the run summary's `disputesFrozen`.

### Refunds of `/escrow/initiate` escrows

An escrow without `source: order` pays for its delivery. When it expires the sweep reads its delivery
and, when it names one, its order, both strongly consistent (a read that throws fails the row,
`auto_refund_exception`). Order escrows keep the 1.13.0 order rules unchanged. In order:

1. No repository: blocked `repo_unavailable`. The order's own escrow (`escrow-order-{orderId}`) holds or
   paid the money too (`initiated`, `escrowed`, `released`, `disputed`, `dispute_resolved`): blocked
   `double_escrow`.
2. The escrow names an order that is missing: the 1.13.0 missing-order rule (skipped, never refunded;
   from the cap plus 24 hours `ADMIN_QUEUE_OVERDUE orderId=<id> reason=order_missing` every run).
3. A resolved dispute: blocked `dispute_resolved`. A disputed delivery or order: frozen, as above. No
   delivery row: blocked `delivery_missing`. An order status the sweep does not know: blocked
   `order_<status>`.
4. Then, with the order **live** when it is `accepted` to `disbursing`:

| Delivery | Live order | No order | Order not live |
|---|---|---|---|
| `delivered` | stamped (`deliveryConfirmedAt`, window), not refunded; the release sweep pays it | same | same |
| `pickedUp`, `inTransit` | held | held | blocked `delivery_moving` |
| `pending` | held | cancelled, then refunded | cancelled, then refunded |
| `cancelled` | held | refunded | refunded |
| anything else | blocked `delivery_<status>` | same | same |

**Held**: like an order escrow before its cap (1.13.0), `expiresAt = min(now + 48h, createdAt + 21
days)`, written only while the escrow is a plain hold. At `createdAt + 21 days` an escrow with an order
enters the admin queue (`hold_cap`, the entry's `adminQueueEscrowId` is this escrow); without an order
there is no queue entry, so from the cap plus 24 hours every run logs `ADMIN_QUEUE_OVERDUE orderId=-
reason=no_order ageHours=<hours since the cap> escrowId=<id>` (the admin-queue-overdue alarm), and the
block `hold_cap_no_order` is audited once without the settlement-failure line.

**Cancelled, then refunded**: the delivery is cancelled first, conditional on `pending`
(`delivery-verification-api.md` 1.12.0), audited in transaction-audit as `delivery_cancelled` with
`reason=escrow_expired`, and counted in `pendingDeliveriesCancelled`; a pickup that won the race keeps
the money held (the next run sees the delivery moving). The refund is the 1.1.0 one (`delivery_timeout`,
the stable `refund:<id>` reference). An `/escrow/initiate` escrow never cancels or restocks the order: the
order's own escrow and the sync cancel own that. A refund scheduled by an order cancel
(`refundReason`) passes the same check, so goods on the road are never refunded.

### Admin queue: one entry per order

An order has one admin queue entry. An escrow is stamped `holdCapReachedAt` and audited
`hold_cap_reached` only for an entry it owns (`adminQueueEscrowId`, else `escrow-order-{orderId}`).

### Run summary

New keys of the `auto_release_and_refund` summary: `releaseBlocked`, `refundBlocked`, `disputesFrozen`,
`pendingDeliveriesCancelled`, `sweepsTruncated` (`-` or a comma list). The reconciliation summary gains
`sweepsTruncated`; its `total` counts the rows it read.

### Deploying 1.15.0

The first run settles what the 100-row window hid, within the budget: on a live environment each
release or refund goes to Manual Payouts (`PAYOUT_NOT_SUPPORTED`) and the overdue alarm 24 hours later,
and the `dispute_resolved` backlog (FUP-17) drains faster than 100 rows an hour. Every escrow a gate now
blocks writes one more audit row and, for the order and delivery check, one alarm line, then none.
Legacy `/escrow/initiate` escrows whose order is `created` or `pending_payment` no longer auto-release
(blocked `order_<status>`; an admin settles them).

### Not in 1.15.0 (known limits)

- No persistent cursor: a sweep a limit stops every run never reaches its tail (it alarms every run).
- A farmer the payout gate blocks has no overdue alarm (only the audit row and the summary count).
- A legacy double payment (an `/escrow/initiate` escrow next to the order's own escrow) is blocked, not
  settled, by both sweeps: an admin decides.

---

## Additions in 1.16.0 (Telebirr live dialect, webhook answers and inbox replay, ESC-45)

Decision record: `specs/decisions/telebirr-webhook-delivery-and-recovery.md` (sources, findings,
sandbox checklist).

### Reading a notification
- **One map is verified and read.** The body is read into one flat map of strings: the top level,
  or its `data` object when the top level has no `merch_order_id` and no `biz_content` but `data`
  has one (then only `data` is read; `sign` from it, or from the top level when it has none). A
  `biz_content` object is flattened one level (the earlier simulator shape). Any other object or
  array value, and any key seen twice, refuses the body; JSON null is absent. Numbers keep their
  raw text, booleans become `1`/`0`. A key containing `&` or `=`, or a value containing `&`, refuses
  the body (`separator_in_field`, `200`, nothing written): the signed string escapes nothing, so such
  a field could re-split a captured notification into a different map with the same signed string.
  Keys that are never signed (`header`, `refund_info`, `openType`, `raw_request`,
  `wallet_reference_data`) are ignored whatever they hold and are exempt from that rule (a URL query
  in `raw_request` must not refuse a real notification); nothing reads them.
  Outbound, an order title drops `&` and `=`, and a key that appears twice once `biz_content` is
  merged up refuses to sign.
- **Signature** over every field of that map except `sign`, `sign_type`, `header`, `refund_info`,
  `openType`, `raw_request`, `wallet_reference_data`, sorted, `key=value&...`, SHA-256 with RSA-PSS
  or PKCS#1 v1.5 (both checked against Telebirr's public key; the log names the padding that
  verified). When the map has `transId` and no `trans_id`, a copy with the key renamed `trans_id` is
  verified and then read (Telebirr signs `trans_id`, and has been seen sending `transId`).
- **Freshness** on `notify_time` (else `timestamp`): 10 to 11 digits are epoch seconds, 13 to 14
  milliseconds, anything else is refused. Fresh is `|now - notify_time| <= 300 s`. On the order
  path a verified notification up to **48 hours** old is still processed (a retry may keep its
  original `notify_time`); a late redelivery of a notification whose inbox row is final changes
  nothing; older than 48 h, or more than 300 s in the future, is refused, and alarmed
  (`ORDER_PAYMENT_UNPROCESSED reason=stale_notify_time`, plus an `order_payment_unprocessed` audit
  row) only when it is a `Completed` payment the inbox has never seen. The `/escrow/initiate` path keeps 300 s.
- **Statuses**: only `Completed` funds; `Failed` and `Failure` are a failed try
  (`order_payment_failed`); others are `order_payment_status_ignored`.
- Field names of a body that did not verify are logged at most 20, each defanged (`MoneyAlarmLog.LogId`).

### Answers
| Case | Answer |
|---|---|
| Before verification (IP, empty or refused body, bad signature, bad `notify_time`, foreign merchant) | `200` |
| Mapping read fails; inbox read or write fails | `500`, `ORDER_PAYMENT_UNPROCESSED` |
| Malformed mapping; orders not configured | `200`, `ORDER_PAYMENT_UNPROCESSED` |
| Less than 4 s left after the inbox write; apply throws; funding conflict | `500`, `ORDER_PAYMENT_REPLAY_PENDING` (not alarmed), row pending |
| A final verdict, an earlier refusal kept, a late redelivery of a handled payment | `200` |
| `/escrow/initiate` path, an exception after verification | `500` |

Bodies: `200` `{"code":"0","msg":"success"}`, `500` `{"code":"1","msg":"error"}`.

### Inbox row and replay
- An order notification is written as a pending row before it is applied: status `received`,
  `orderId`, `notificationBiz` (the verified payment fields), `inboxReplay = pending`,
  `inboxReplayAt` = receipt + 2 minutes, `replayAttempts = 0`. A final outcome (`applied`,
  `applied_again`, `duplicate`, `unmatched:*`, `payment_failed`, `ignored`) removes `inboxReplay`,
  `inboxReplayAt` and `notificationBiz`. A non-final outcome sets `unprocessed` and keeps the row
  pending. Sparse GSI `InboxReplayIndex` (PK `inboxReplay`, SK `inboxReplayAt`) on the escrow
  idempotency table; all times one ISO-8601 UTC form.
- **Sweep**: the webhook Lambda invoked by EventBridge every 5 minutes with
  `{"action":"webhook_inbox_replay"}` (only without an API Gateway request context, so never from the
  public route). Per run at most 100 due rows, oldest first, no claim with less than 5 s left. A row is
  claimed with one conditional write (still pending with the replay time and attempt count read), which
  counts the attempt and sets the next replay 5, 10, 20, 40, then 60 minutes later, and is then replayed
  like a redelivery (the funding write is the fence against a redelivery at the same moment).
- **Give-up**: after the 8th failed attempt, or for a due row with every attempt used, the row moves to
  `inboxReplay = exhausted` (status `unprocessed`, stored fields kept, `exhaustedAt` stamped and never
  removed), conditional on still pending with the attempt count read; only then `ORDER_PAYMENT_UNPROCESSED ... reason=replay_exhausted cause=<why>
  attempts=<n>` and an `order_payment_unprocessed` escrow-audit row. A malformed or missing mapping or an
  unreadable stored notification is given up at once. A row with `exhaustedAt` that later applies (a
  redelivery, or the sweep after a requeue) logs "applied after its inbox row was given up; stop any
  manual refund".
- One `TELEBIRR_INBOX_REPLAY_RUN due= claimed= applied= pending= exhausted=` line per run, none when the
  index query failed; alarm `{prefix}-telebirr-inbox-replay-stalled` after 30 minutes without one.
- The rule in §Webhook: order payments about not refunding an `ORDER_PAYMENT_UNPROCESSED` payment while
  its order is in the same attempt now means: the sweep applies it; refund only after
  `replay_exhausted` and only once the order has left that attempt (or after a requeue, per the runbook).

### Order hold (order-lifecycle.md 1.16.0)
Before the inbox write of a verified `Completed`, the webhook sets the server-only order field
`paymentHoldUntil` to now + 4 h 47 min (the replay horizon): only while the order is
`payment_submitted` in the mapping's payment attempt, extend-only, without changing `version` or
`updatedAt`. Each sweep claim extends it to the next replay + 15 minutes. The order cron does not move
an order to `payment_failed` while its hold is in the future or while its payment is pending in the
inbox.

### Gateway calls
Every live Telebirr HTTP call ends at the invocation's deadline: the escrow Lambda's remaining time minus
5 s, escrow-cron's minus 30 s. With less than 2 s left no request is sent and the call fails with
`TELEBIRR_NO_TIME` (definite: a checkout slot becomes `failed` and the buyer can retry at once); a call cut
at the deadline is `TELEBIRR_TIMEOUT` (unknown, as in 1.10.0). A failed fabric-token call is the definite
`TELEBIRR_TOKEN` (no payment request was sent). The token is requested at `/payment/v1/token`; request
and checkout-URL timestamps are epoch seconds. Release and refund still make no HTTP call; a future
payout call must keep the same deadline rule.

## Additions in 1.17.0 (the order follows the money, ESC-09)

Before 1.17.0 no escrow writer touched the order after the money moved: every paid-out order stayed
`delivered` (`order-lifecycle.md` 1.17.0 has the order side).

### Every money writer and the order

| Writer | Escrow after | Order after |
|---|---|---|
| Release, farmer leg queued (`PAYOUT_NOT_SUPPORTED`, no reference, unknown outcome) | `escrowed` + queue op=release | `disbursing` |
| Release, farmer paid, trucker leg queued | `escrowed` + queue op=release | `disbursing` |
| Release, farmer paid, trucker leg pending or blocked | `released` + `truckerPayoutPending` | `disbursing` |
| Release, every leg paid | `completed` | `settled` |
| Hourly trucker retry pays the last leg | `completed` | `settled` |
| Manual payout leg recorded, others pending | queue still open | `disbursing` |
| Manual payout close (release, or a released trucker queue), and its replay | `completed` | `settled` |
| Stale release-lock park (ESC-05) | `escrowed` + queue | `disbursing` |
| Legacy trucker park (ESC-04 deploy action) | `released` + queue | `disbursing` |
| Crash recovery `released -> completed` | `completed` | `settled` |
| Any of the above on a verdict escrow | as above | stays `dispute_resolved` (the verdict owns it) |
| Refund (sweeps, verdict, admin) | `refunded` | not moved: `cancelled`, `rejected`, `dispute_resolved`, or flagged |

The order is written after the escrow write, never between the settlement lock and the gateway
call, by `OrderMoneyMirror` (status-conditioned hop, `ADD version 1`, one transaction-audit
`order_updated` per hop). The settlement's answer never depends on it: a failure logs
`ORDER_MONEY_FOLLOW_DEFERRED` (not an alarm token) and the hourly sweep repairs it.

### The hourly order-money sweep

Last step of `auto_release_and_refund` (it moves no money). One run budget, `CronRunBudget`, for every
timed sweep of the run (anchored when the handler starts; each sweep takes a slice with its own end and
its own Lambda reserve, and starts no new row once `0 < RemainingTime < reserve`). ESC-10's paged sweeps
(§Additions in 1.15.0) take theirs from it with the limits they had: no new row once the run is 90, 135,
165 or 180 seconds old, reserve 120 seconds, a stop still logged as `time`. This sweep: slice 30
seconds from its start, reserve 30 seconds, at most 2,000 orders. It reads the orders
`StatusCreatedAtIndex` partitions `delivered`, `disbursing`, `disputed`, then `accepted`,
`pickup_confirmed`, `in_transit`, with the filter `attribute_not_exists(moneySweepSkip) OR
moneySweepSkip <> :status`. Per order, strongly consistent: the order escrow
(`escrow-order-{orderId}`), and when it is missing or refunded the `OrderIndex` (and the active
delivery's `DeliveryIndex` row); the escrow furthest along wins. Then the same rules as the inline
write. A verdict escrow whose money moved is repaired through the ESC-03 mirror (no cancel) toward
`dispute_resolved`; a verdict whose money has not moved is left to its settlement.

| Finding | Action |
|---|---|
| order moved | counted (`ordersMoved`, `ordersSettled`) |
| money held, order waiting | nothing, read again next run |
| no escrow at all (only past delivery: an order before it may still get one through linkage), a refund behind a closed order or a flagged override (`refundOverride*`, ESC-03 `disputeOverride*`; a flag only ever excuses a refund, since it is written before the money moves) | order marked `moneySweepSkip` (its status) and `moneySweepSkipAt`, unversioned |
| cannot follow (a refund behind a `delivered` order, a payout behind an order not delivered) or order ahead (`disbursing`/`settled` over held money), still so after a fresh read of the escrow | escrow-audit row `order_money_mismatch` at `createdAt + 7` ticks, or `order_money_ahead` at `createdAt + 8` (`AppendIfAbsentAsync`); only when the row is new: `ORDER_MONEY_MISMATCH ... reason=cannot_follow|order_ahead` and a transaction-audit row on the order; then the skip mark, only for an order past delivery (an order before it can still be fixed by a later delivery confirmation, so it is read every run, alarmed once) |
| the hops failed (throttle, permissions, lost races) | `ORDER_MONEY_MISMATCH ... reason=write_failed` every run once the money moved more than 2 hours ago, an info line before; no mark |
| slice or row cap used up | the cursor `cursor:esc-09:order_money_sweep` (escrow idempotency table, 30-day TTL) saves the partition and the index key of the last order decided, and the next run goes on from there; the cycle's start is the row's `createdAt` and the row is deleted when a cycle has read every partition. The update is conditional on the row existing (`TryUpdateExistingAsync`), so a cursor another run deleted at its cycle end comes back only through a new store with its cycle start and TTL. Info `ORDER_MONEY_SWEEP_PAUSED`; `ORDER_MONEY_MISMATCH orderId=- ... reason=sweep_truncated` only once a cycle is unfinished 24 hours after it started |
| a page query failed | `ORDER_MONEY_MISMATCH orderId=- ... reason=sweep_failed`, the next partition goes on |

Run summary keys: `ordersMoved`, `ordersSettled`, `orderMoneyMismatches`, `orderMoneyFailed`,
`ordersWithoutEscrow`. Alarm `${prefix}-order-money-mismatch` (metric `OrderMoneyMismatch`, escrow-cron
log group only, 15 minute periods, 5 periods, any line alarms).

Escrow-audit slots in use (the table's sort key is the timestamp): `disputeResolvedAt + 1/2/3`
(ESC-03), `queuedAt - 1` (ESC-05, ESC-04), manual close `paidAt` and `paidAt + 1` (ESC-02), trucker
retry `paidAt + 1` (ESC-04), `createdAt + 7` and `+ 8` (ESC-09).

### Part B: an admin release or refund of a plain escrowed order escrow checks the order first

Only for a caller other than the system sweep, only on `escrowed` (verdicts keep 1.9.0), only when the
escrow resolves to an order (its `orderId`, else its delivery's). Before the payout gate and the lock.

- Release: the order must be `delivered` or `disbursing`; an order at `accepted`, `pickup_confirmed`
  or `in_transit` whose active delivery is `delivered` is advanced to `delivered` first (`503
  ESCROW_012` if that write loses). Anything else `422 ESCROW_INVALID_TRANSITION`, no override. Audited
  `admin_settlement_order_refused` per attempt.
- Refund: `rejected` as is; `cancelled` as is, after the listing stock is given back first when the
  cancel was `escrow_refunded` or `transport_failed_buyer_cancelled` (`503` if not); `funded`, or
  `accepted` with no delivery or a `pending` one: cancelled first through the ESC-03 path (pending
  delivery, order `cancelReason: escrow_refunded`, slot, stock; `503` if any of it does not land; when
  a concurrent cancel won the race, that order's stock is given back first as above) and
  the refund scheduled on the escrow (`expiresAt = now`, `refundReason: admin_refund`, audit
  `refund_scheduled`) so a gateway refusal leaves it for the hourly sweep. Goods moving or
  `delivered`: `422` unless `overrideOrderMismatch` with a reason of 10 to 500 characters (admin group):
  audited `refund_order_mismatch_overridden` before the money (`503` if that write fails), the order
  flagged `refundOverrideAt`, `refundOverrideBy`, `refundOverrideReason`, `refundOverrideEscrowId`
  (server-only, version-checked, status left). Anything else (`disputed`, `disbursing`, `settled`,
  `dispute_resolved`, a payment status, a missing order): `422`, no override.

This resolves the 1.13.0 limit "An admin must not refund an accepted order by hand": the refund now
cancels the order first.

### Roles and clients

Buyer: sees Disbursing then Settled and may rate from Disbursing. Farmer: Disbursing while paid or in
Manual Payouts, Settled once every leg is paid. Trucker: Disbursing until the transport leg is paid.
Admin: stages `paying_out` and `resolved`, `rejected` shown as cancelled. No new wire field; the skip
and override fields are server-only.

### Not in 1.17.0 (known limits)
- An admin release before the delivery is confirmed is refused with no override: pay such a farmer
  through the dispute path, or the admin actions of FUP-16.
- A mismatch in a status the sweep does not read (`funded`, `cancelled`, `rejected`) after money moved
  is logged only by the inline call (`ORDER_MONEY_FOLLOW_DEFERRED`); with Part B only legacy rows get there.
- A hop whose answer was lost and retried by the SDK lands without its transaction-audit row.
- Rows still waiting for their money (held escrows of orders in flight, a release blocked by the payout
  gate) are read once per cycle; at volume a cycle spans several runs, so a repair can take as many hours.
  ESC-16's deadline index is the scale path.
- Part B: when the order's cancel lands but its stock restore does not, the refund answers `503` and is
  not scheduled; the admin's retry gives the stock back first and refunds (or the escrow's expiry does).
- ESC-16's deadline index replaces the partition walk.

---

## Additions in 1.18.0 (legacy delivery initiate refused on live Telebirr, FUP-36)

Before 1.18.0 a buyer who reached `POST /escrow/initiate` with the live gateway got a gateway
refusal (`502 ESCROW_013`) after the idempotency, escrow and audit rows were written, or, when
Telebirr's answer was lost, an `initiated` escrow that could never be paid: the path's
merch_order_id is the client idempotency key, and live Telebirr takes `^[A-Za-z0-9]+$` only.

### The rule

- `POST /escrow/initiate` answers `422 ESCROW_039` ("Online escrow payment for a delivery is not
  available here. Pay this order from its order page instead.") as its first check whenever the
  gateway is live Telebirr. No idempotency row, escrow row, escrow-audit row or payment-velocity
  count is written, and Telebirr is not called; the handler's audit log line (outcome `failure`,
  code `ESCROW_039`) is the only trace. The role, step-up and velocity gates in front of the
  service still run first, as for every other initiate answer.
- "Live Telebirr" (`ITelebirrGateway.IsLiveTelebirr`): false for the mock gateway
  (`TELEBIRR_MOCK=true`); for the real gateway class false only when the configured `baseUrl` host
  is loopback (`localhost`, `127.0.0.0/8`, `::1`) or `host.docker.internal`, which is how the local
  simulator (`tools/telebirr-sandbox`) is reached. Every other host, a `baseUrl` that does not
  parse, and a simulator exposed through a tunnel count as live (fail closed).
- `422`, not `409`: the mobile intent queue completes any `409` on this path as "funds already
  held" (SIM-24), which a refusal must never read as. A queued intent that meets `ESCROW_039` is
  retried like any other failure (3 attempts) and then shown as failed.
- Unchanged: the mock and the simulator (`initiate` holds synchronously, or returns `toPayUrl` for
  the simulator's checkout), `POST /escrow/order-checkout`, the webhook (it still serves this path
  for escrows that exist), the escrow cron.

### Client

Mobile and web show `ESCROW_039` with its own message in en, am and om
(`escrowErrorDeliveryPaymentUnavailable`). A `422` whose body carries an `ESCROW_*` code now keeps
that code in both repositories (before, `ESCROW_010` and this code fell to the generic text). The
mobile delivery payment flow itself is not removed: mock deployments still use it. Hiding it when
the server is live is ESC-46 / ESC-23.

### Not in 1.18.0 (known limits)

- `.ai/telebirr-testing.md` Tier 2d option 1 (a deployed server with a tunnel-exposed simulator)
  and `scripts/e2e_full_flow.py --telebirr-sim-url` drive `/escrow/initiate` and are refused under
  the host rule; the order checkout works there. Either move them to the checkout or give the
  simulator an explicit marker in its credentials.
- Retiring the path once ESC-46 / ESC-23 remove the client flow.

---

## Additions in 1.20.0 (manual refund of an unapplied payment, FUP-02)

Numbering: 1.18.0 (FUP-36) and 1.19.0 (ESC-47) landed before this section; it does not depend
on them.

A captured Telebirr payment the webhook did not apply (§Webhook: order payments) stays with the
platform until an admin refunds it in the Telebirr merchant portal. Before 1.20.0 nothing recorded
that refund: the payment stayed open for good, and a buyer with one could never delete the account
(`account-deletion.md`, `payment_review`). 1.20.0 adds the admin queue of these payments and the
record of their refund (the row reserved in 1.8.0). Recording moves no money and changes no order or
escrow: the payment was never applied.

### Which payments wait for a refund

The webhook's escrow-audit rows under `escrow-order-{orderId}`: `order_payment_unmatched`,
`order_payment_duplicate`, `order_payment_unprocessed`.

- A payment is identified by its Telebirr payment_order_id, the `telebirrRef` of its rows. Rows
  without one (`reason=no_payment_order_id`, or an inbox row given up before its body was read) are
  identified by the checkout's merch_order_id in their metadata; the admin finds the payment in the
  portal by that id.
- A payment is resolved by an `order_payment_refunded_manual` row under the same key whose metadata
  names its payment_order_id or, for a payment without one, its merch_order_id. The time of the rows
  does not matter: a later row about the same payment is not new money, and a refund row resolves
  only the payment it names (a second payment on the same order stays open).
- A payment that funded the order is not a refund case: the order's `telebirrPaymentOrderId` is its
  payment_order_id (for a nameless payment, the order's `telebirrMerchOrderId` is its
  merch_order_id). This is the `unprocessed` row whose payment the inbox replay applied later; its
  money is in the order's escrow.
- A payment is decided for good only when it is named and has an `unmatched` or `duplicate` row:
  the inbox keeps that verdict per payment_order_id. A payment with only `unprocessed` rows can
  still be applied by a redelivery or the inbox replay, and a nameless payment can still arrive again
  as a notice that names it, which is a new inbox row and can fund the order. Neither is refunded
  while its order is still `payment_submitted`.
- Captured amount: the rows' `amount` (the checkout amount, which is what Telebirr charges); for
  `reason=amount` the `webhook=` value of the `webhook_amount_mismatch` row written just before;
  unknown for `reason=currency`, for a give-up row (it carries no amount) and when the rows disagree.
- The server keeps this rule in one helper (`UnappliedPayments`); the admin queue, the record call
  and the account-deletion blocker (`account-deletion.md` 1.1.0) all use it.

### GET /admin/payments/unapplied
Admin pool. Query `limit` (1 to 50, default 20) and `nextToken`. The escrow-audit table has no index
on the action, so the call pages through a filtered Scan: at most 4 pages of 500 rows per call
(2,000 rows, the bound of the F-35 audit feed) and the trails of at most 50 orders (one Query each,
sequential, so a page dense with unapplied rows cannot run the Lambda out of time). It stops early
once it has `limit` payments; the last page read can add a few more. Every order found is re-read
with a Query, so its answer is exact. A response can be empty while `nextToken` is set: keep loading
until `nextToken` is null. Items come in table scan order. The signed token carries the scan position
and the order already read in full, which the next call skips; when the 50-order cap stops a call,
the position is the last row of the last order read, so the next call resumes right after it.

```json
{
  "payments": [
    {
      "orderId": "...",
      "escrowId": "escrow-order-...",
      "paymentOrderId": "...",
      "merchOrderId": "WCO...",
      "actions": ["order_payment_unmatched"],
      "reasons": ["status_cancelled"],
      "firstSeenAt": "2026-10-20T08:00:00.0000000Z",
      "lastSeenAt": "2026-10-20T08:00:00.0000000Z",
      "capturedAmount": 1500,
      "currency": "ETB",
      "orderStatus": "cancelled",
      "buyerId": "...",
      "mayStillApply": false
    }
  ],
  "nextToken": null,
  "total": 1
}
```

`paymentOrderId` and `capturedAmount` are left out when unknown, `orderStatus` and `buyerId` when the
order is missing. Resolved payments and payments that funded their order are not listed.
`mayStillApply` is true when the payment is not decided for good (only `order_payment_unprocessed`
rows, or a payment the webhook could not name) and its order is still `payment_submitted`: Telebirr
may still deliver it to the order, so it is not refunded yet (the record call refuses it). Errors: `400 ESCROW_023` invalid token (also a token of another
admin list), `503 ADMIN_004` tables not configured.

### POST /admin/payments/{orderId}/refund-manual
Admin pool **and** the `admin` group (`cognito:groups`); a role claim alone gets `403 ESCROW_015`.
Body:

```json
{
  "paymentOrderId": "<Telebirr payment_order_id>",
  "telebirrRef": "<merchant-portal transaction id of the refund>",
  "amount": 1500,
  "reason": "Buyer paid after cancelling; refunded in the portal",
  "merchOrderId": "WCO..."
}
```

- `paymentOrderId`, `telebirrRef` and, when sent, `merchOrderId`: trimmed, 4 to 64 characters, a
  letter or digit first, then letters, digits, `.`, `_`, `:`, `/` or `-` (the 1.8.0 reference rule).
- `amount`: ETB, more than 0, at most two decimals, at most 100,000,000.
- `reason`: trimmed, 10 to 500 characters.
- `merchOrderId`: only for a payment the webhook could not name (its rows carry no
  payment_order_id): the listed merch_order_id. `paymentOrderId` is then the id the admin found in
  the portal.

The call reads the order's escrow-audit trail and the order strongly consistent, then answers:

| Status | Code | When |
|---|---|---|
| `200` | | refund recorded; `alreadyRecorded: true` on a replay with the same `telebirrRef` (nothing written) |
| `400` | `ESCROW_040` | a field is missing or malformed (`field` names it) |
| `400` | `ADMIN_005` | body is not JSON |
| `403` | `ESCROW_015` | caller not in the admin group |
| `404` | `ESCROW_042` | no captured payment with this payment_order_id on this order (with `merchOrderId`: no nameless payment of that checkout either), or a malformed order id |
| `409` | `ESCROW_036` | this payment's refund was already recorded with another `telebirrRef`, this `telebirrRef` was recorded for another payment of the order, or another admin is recording this payment at this moment (the 1.8.0 code for a reference conflict) |
| `409` | `ESCROW_043` | `amount` differs from the captured amount (the message names it, field `amount`) |
| `409` | `ESCROW_044` | the payment funded the order (its money is in the order's escrow), or it is not decided for good (only `unprocessed` rows, or nameless) while the order is still `payment_submitted` |
| `503` | `ADMIN_004` | escrow-audit, orders or (for this call) escrow-idempotency table not configured |

`ESCROW_041` is not used here (reserved by FUP-16). When the captured amount is unknown, the admin's
amount is recorded without a check and the answer says `amountChecked: false`: refusing would leave the
payment open for good.

```json
{
  "orderId": "...",
  "escrowId": "escrow-order-...",
  "paymentOrderId": "...",
  "merchOrderId": "WCO...",
  "telebirrRef": "...",
  "amount": 1500,
  "amountChecked": true,
  "recordedAt": "2026-10-23T09:00:00.0000000Z",
  "recordedBy": "<admin id>",
  "message": "Refund recorded.",
  "alreadyRecorded": false
}
```

### Rows written
- One `TransactWriteItems` of two puts, each only if absent; nothing is written unless both are:
  - escrow-audit, under `escrow-order-{orderId}` (`attribute_not_exists(escrowId)`, still
    append-only; a timestamp collision tries the next instant): action
    `order_payment_refunded_manual`, `fromState` and `toState` empty (as the webhook's rows),
    `actor` = the admin id, `timestamp` = now, `telebirrRef` = the refund's portal transaction id,
    `metadata` = `paymentOrderId=<id>;amount=<n>` as reserved in 1.8.0, then `;merchOrderId=<id>`
    when the payment's checkout is known, plus the attributes `reason` (the admin's text),
    `ipAddress` and `correlationId`. `amount` is invariant with at most two decimals (`1500`,
    `1500.5`).
  - the claim row `refund-claim#{orderId}#{payment}` in escrow-idempotency (`{payment}` is the
    payment_order_id, or `merch:{merch_order_id}` for a nameless payment) with `escrowId`,
    `status = refund_recorded`, `paymentOrderId`, `telebirrRef`, `claimedBy` and `claimedAt`, and
    `expiresAt` = now + 400 days (the table's TTL, epoch seconds, as its checkout rows; no buyer data).
    Its expiry cannot reopen a double record: every later attempt first reads the escrow-audit refund
    row, which is permanent, strongly consistent. It makes the record exclusive: of two admins recording
    one payment at once, exactly one writes. The other answers from the row it then reads strongly
    consistent: `alreadyRecorded` with the same `telebirrRef`, else `409 ESCROW_036`, also while the
    winner's transaction is still in flight.
- transaction-audit (best effort, like the other admin rows): `admin_payment_refunded_manual`,
  `transactionId` = the order id, `entityType` = `escrow`, `entityId` = `escrow-order-{orderId}`,
  metadata `paymentOrderId=..;telebirrRef=..;amount=..;amountChecked=true|false;reason=..`.
- A replay writes neither row.
- Log line from the admin Lambda (informational; no alarm counts it), every client value through
  `MoneyAlarmLog.LogId`:
  `ORDER_PAYMENT_REFUNDED_MANUAL orderId=<id> paymentOrderId=<id> telebirrRef=<id> amount=<n> amountChecked=<bool> admin=<id>`.
- Nothing else: no order, escrow, delivery or stock write, and no Telebirr call.

### Roles and clients
Admin only. The farmer, trucker and buyer apps (mobile and web) are unchanged. The admin app has an
"Unapplied Payments" page (en, am, om) with the record form, and the transaction timeline labels both
new actions.

### Infrastructure
Two routes on the admin authorizer. The admin Lambda already reads and writes escrow-audit, reads
orders and writes transaction-audit; it gains `ESCROW_IDEMPOTENCY_TABLE` and `dynamodb:PutItem` (only)
on escrow-idempotency for the claim row. IAM has no separate transaction action: each Put of a
`TransactWriteItems` needs `PutItem` on its table, and `ConditionCheckItem` is needed only for
condition-check items, which this transaction does not use.

### Not in 1.20.0 (known limits)
- `ESCROW_036` catches a refund reference reused within one order only; the same portal reference
  recorded on two orders is not detected (there is no index on it).
- The list is a table Scan; at volume a full pass takes many calls. A sparse index written by the
  webhook is the scale path (a webhook change and a backfill).
- No check of the references against Telebirr (no API).
- The transaction detail shows escrow-audit rows only for an order with an escrow record, so on an
  order that was never funded the refund shows there only as its `admin_payment_refunded_manual` row.
- Automatic duplicate refunds stay ESC-20.

---

## Additions in 1.21.0 (dispute dispositions and the pickup rule, FUP-16)

1.18.0 (FUP-36), 1.19.0 (ESC-47) and 1.20.0 (FUP-02) are other tickets' numbers, in flight when this
was written.

Before 1.21.0 a verdict whose order could not follow it was recorded anyway and then refused by every
settlement: a `buyer_wins` with the goods moving needed the `overrideOrderMismatch` refund (the order
kept its status, flagged), and a `farmer_wins` before delivery waited for a delivery that might never
come. `POST /escrow/dispute` also accepted a dispute before anything had left the farm.

### The verdict checks the order first
`POST /admin/disputes/{id}/resolve` decides the order and the delivery before it writes anything (the
1.9.0 mirror's rules, strongly consistent reads). A verdict they can follow as they are is recorded as
before. One they cannot follow is recorded only with a disposition that resolves it; otherwise nothing
is written:

| Order and delivery | Verdict | Without `disposition` | Dispositions that fit |
|--------------------|---------|-----------------------|-----------------------|
| follow the verdict (the 1.9.0 table: delivered, disputed, resolved; for `buyer_wins` also closed or before pickup) | either | `200`, recorded as before | none needed: a `disposition` sent anyway is not recorded, and the message says so |
| goods moving: order `pickup_confirmed` or `in_transit`, or a delivery `pickedUp` or `inTransit` (no verdict on it) | `buyer_wins` | `422 ADMIN_015`, field `disposition` | `return_to_farmer`, `delivered_anyway` |
| before delivery: order `funded`, `accepted`, `pickup_confirmed` or `in_transit`, delivery none, `pending`, `pickedUp` or `inTransit` (no verdict on it) | `farmer_wins` | `422 ADMIN_015`, field `disposition` | `wait_for_delivery`; `settle_now` only once the goods were handed over (order `pickup_confirmed`/`in_transit`, or delivery `pickedUp`/`inTransit`) |
| anything else: a missing order or delivery, a delivery carrying another verdict, a `farmer_wins` on a cancelled or rejected order, an order at `disbursing` or `settled` | either | `422 ADMIN_016` (a new verdict: fix the order or choose the other verdict; a recorded one cannot change, see "Known limits") | none |

A read that fails answers `503 ESCROW_012`; nothing is recorded.

Request, in addition to 1.9.0:

| Field | Rule |
|-------|------|
| `disposition` | Optional. `return_to_farmer` or `delivered_anyway` with `buyer_wins`; `settle_now` or `wait_for_delivery` with `farmer_wins`. Any other value, or one of the other verdict: `400 ADMIN_014`, field `disposition` |
| `reason` | Required with a `disposition`: 10 to 500 characters after trimming, else `400 ADMIN_014`, field `reason`. Stored in the audit trail |

```json
{ "resolution": "buyer_wins", "action": "refund", "disposition": "return_to_farmer",
  "reason": "Truck turned back at Adama with the bags." }
```

A disposition that does not fit (for example `settle_now` before pickup) answers `422 ADMIN_016`,
field `disposition`, and writes nothing. The endpoint stays admin group only (`403 ESCROW_015`).
The response gains `disposition`: the one recorded on the verdict, else absent.

### What a disposition does
The verdict write records the disposition in the same conditional write as the verdict, on the escrow
row: `disputeDisposition`, `disputeDispositionReason`, `disputeDispositionBy`, `disputeDispositionAt`.
Then:

1. Escrow audit: one row, actor the admin, from = to = `dispute_resolved`, metadata
   `disposition=<d>;reason=<r>;legs=<the legs that could not follow>`, at `disputeDispositionAt` plus
   four ticks with a conditional put (one row per escrow and choice, re-ensured on a replay). When the
   row is new, a transaction-audit row with the same action (entity `escrow`, the admin as actor) and
   the log line `DISPUTE_DISPOSITION_RECORDED escrowId=.. admin=.. disposition=..` (not an alarm). If
   the escrow-audit row cannot be written, nothing moves: `503 ESCROW_012`.
2. The legs, as a system actor, each write conditional on the status it was decided on, with one
   transaction-audit row each (metadata `resolution=<r>;disposition=<d>`):

| Disposition | Audit action | Delivery | Order | Money (the hourly dispute sweep, unchanged) |
|-------------|--------------|----------|-------|---------------------------------------------|
| `return_to_farmer` | `dispute_resolved_goods_returned` | `pending`, `pickedUp` or `inTransit` to `cancelled`, `cancelReason: dispute_buyer_wins`, `cancelledAt`, the verdict stamped (`delivery_cancelled`) | to `dispute_resolved` (`dispute_resolved`) | refund to the buyer in full, as every `buyer_wins` |
| `delivered_anyway` | `dispute_resolved_delivered_anyway` | to `disputed` with the verdict stamped and `confirmedAt` set when missing (`dispute_resolved`): the end state of a verdict on delivered goods, in one write | to `dispute_resolved` | refund to the buyer in full |
| `settle_now` | `dispute_resolved_settled_early` | to `cancelled`, `cancelReason: dispute_farmer_wins`, `cancelledAt`, the verdict stamped | to `dispute_resolved` | release as every `farmer_wins`: the farmer leg, and the trucker leg when the escrow carries one (1.11.0) |
| `wait_for_delivery` | `dispute_resolved_waiting_for_delivery` | unchanged | unchanged | refused (`422`) until the order reaches `delivered`, then released; audited once (`dispute_verdict_mismatch`) but not alarmed: the sweep logs `DISPUTE_VERDICT_WAITING_FOR_DELIVERY escrowId=.. op=.. legs=..` instead of the alarm token. A wait whose order moved where it cannot follow (cancelled, say) alarms again |

The delivery moves first, so a handoff racing in fails the delivery's condition and the order is left
alone. The order goes straight to `dispute_resolved` (status-conditioned, `ADD version`). Listing stock
is never restored: returned goods may come back damaged, so the farmer relists what is still sellable;
with `settle_now` the farmer is paid for goods already handed over. No trucker is paid for a wasted
trip (ESC-40, D5).

A leg that does not move (a race, an error) answers `503 ESCROW_012` with the legs: the verdict and its
disposition stand, and the same request again finishes the job (log `DISPUTE_DISPOSITION_INCOMPLETE`).
Until then the settlement refuses the verdict as in 1.9.0.

### The settlement
The dispute sweep and the admin settlement routes never apply a disposition; they only find the legs
following the verdict. The mirror's delivery rule gains one row: a delivery `cancelled` with this
verdict stamped follows it (one cancelled under the other verdict is still a mismatch). A delivery a
disposition cancelled (`cancelReason: dispute_*`, or a verdict on a cancelled row) counts as picked up:
its order is never taken for one before pickup, so the `buyer_wins` refund never cancels it or restores
its stock. A half-applied disposition (delivery closed, order not) stays refused and alarmed until the
admin sends the verdict again. The refusal message of a mismatched settlement now names the
disposition. The only refusal that is not alarmed is a recorded `wait_for_delivery` whose order still
waits for its delivery (the mismatch alarm stays clear, so a really stuck verdict notifies).

### The same verdict again
On an escrow already `dispute_resolved` (not settled) whose legs cannot follow it:

- A `disposition` different from the recorded one (or with none recorded) is recorded first,
  conditional on `status = dispute_resolved`, the same verdict, no manual payout, no settlement lock and
  the escrow still carrying the disposition this request read (none, or that one), so two admins
  choosing at once cannot overwrite each other unseen (else `409 ADMIN_010`); then audited and applied.
  This is how `wait_for_delivery` becomes `settle_now` once the trucker stops.
- The recorded one (sent again, or not sent) is applied again: a repair, no second audit row.
- No disposition at all, on a verdict recorded before 1.21.0: `422 ADMIN_015`, so the admin chooses.

A replay of a settled verdict, or of one whose legs follow, is answered as in 1.9.0
(`200 "Verdict already recorded."`), with the recorded `disposition`.

### `overrideOrderMismatch` deprecated for verdicts
`POST /escrow/refund` still accepts `overrideOrderMismatch` on a `dispute_resolved` escrow exactly as
in 1.9.0 (the order keeps its status and is flagged) for one more version; record `return_to_farmer` on
the verdict instead. It is not deprecated for ESC-09 Part B (an admin refund of a plain `escrowed`
order escrow whose goods are moving), which has no verdict to carry a disposition.

### Dispute before pickup (`ESCROW_041`)
`POST /escrow/dispute` is refused with `422 ESCROW_041` ("A dispute can be opened only after the goods
are picked up ...") unless the goods were picked up:

- the escrow's `deliveryConfirmedAt` is set (no read), or
- the order is at `pickup_confirmed` or later (`in_transit`, `delivered`, `disputed`,
  `dispute_resolved`, `disbursing`, `settled`), or
- the order's active delivery (else the escrow's `deliveryId`) is `pickedUp`, `inTransit`, `delivered`
  or `disputed`.

The order and the delivery are read strongly consistent after the request is validated and before the
idempotency record. A read that fails answers `503 ESCROW_012` (log `ESCROW_DISPUTE_PICKUP_UNREAD`) and
nothing is written. An escrow with no readable order and no readable delivery is allowed, as before.
The 48-hour window after `deliveryConfirmedAt` is unchanged.

Why: before pickup nothing has moved and the order has its own exits (the farmer's reject, the buyer's
cancel of a funded order, the transport-failure cancel of 1.13.0). A dispute there only freezes the
money and produces a `farmer_wins` with nothing to settle. Decision record:
`specs/decisions/dispute-before-pickup.md` (the user may overturn it; the gate is one condition in
`EscrowService.DisputeAsync`). A delivery dispute through `POST /sync` already starts only from
`delivered` (`delivery-verification-api.md`), so that path needs no change.

The apps show `ESCROW_041` with its own message in en, am and om (mobile and web,
`escrowErrorDisputeBeforePickup`).

### Admin
The Disputes page has two tabs: open disputes, and recorded verdicts awaiting settlement (the existing
`GET /admin/escrows?status=dispute_resolved`), each with "Load more". The awaiting tab shows the recorded
disposition and its reason (`DisputeDto` gains `disposition`, `dispositionReason`, `dispositionBy`,
`dispositionAt` on admin responses only). A verdict answered with `ADMIN_015`, or a recorded one, opens
a choice: the case, the two dispositions of the verdict, and the reason. `ADMIN_015`, `ADMIN_016` and
the four audit actions are labelled in en, am and om. (The admin app never had an override checkbox; the
override was an API-only flag.)

Status-filtered admin lists (`GET /admin/disputes`, `GET /admin/escrows?status=..`,
`GET /admin/fraud-signals?status=..`) scan and then filter, so before 1.21.0 one page of `limit`
scanned rows could hold few matches or none and the rest was never offered. Now the list keeps reading
pages of `limit` rows until `limit` rows matched, the table ends, or 25 pages were read, and returns
everything matched (up to twice `limit` less one) with a `nextToken` that resumes after the last page
read, so no row is skipped or repeated. An unfiltered list still reads one page.

### Roles and clients
Admin only. Farmer, trucker and buyer see the result through their usual pulls: the order at
`dispute_resolved`, the delivery at `cancelled` (with `cancelReason`) or `disputed`. Both statuses were
known before, and the apps ignore the new keys. No role-visibility change.

### Not in 1.21.0 (known limits)
- A recorded verdict that no disposition fits (`ADMIN_016` on a replay: a missing order or delivery
  row, another verdict on the delivery, an order closed or paid out meanwhile) has no exit in the admin
  page. For `buyer_wins` only the deprecated `overrideOrderMismatch` refund settles it; for `farmer_wins`
  there is none: the order must be repaired by hand. A new verdict in that state is simply not recorded.
- A `farmer_wins` recorded before pickup (a dispute raised before 1.21.0) can only `wait_for_delivery`.
  If its delivery never comes it waits; if the order is then cancelled, it becomes a no-fit case as
  above.
- A recorded `wait_for_delivery` is never alarmed while its order waits for the delivery; there is no
  deadline after which it alarms again.
- No payment for a trucker whose trip was wasted (`return_to_farmer`, `delivered_anyway`), and no
  withholding of the trucker leg on `settle_now` (ESC-40, D5).
- Per-leg verdicts and `partial_refund` (ESC-20); reputation effects (ESC-28).

---

## Additions in 1.22.0 (the fraud hold, FRD-03)

Builds the `held` status of 1.7.0 for one trigger and leaves the others to their tasks. Design:
`specs/architecture/fraud-detection-architecture.md` section 18. It ships with
`FRAUD_HOLD_ENABLED=false` on escrow-cron: with the flag off no escrow is written `held`.

### Entry: the risk gate in the sweeps
- Each of the three settlement sweeps asks the gate at the 1.7.0 position, before the first
  money-related side effect: auto-release after its order and delivery check (1.15.0) and before the
  payout resolution, the lock and Telebirr; auto-refund of an order escrow after the order read, the
  progressing-order extension and the not-refundable skip, and before the stock repair, the order
  cancel, the restock and the refund; auto-refund of an `/escrow/initiate` escrow after its delivery
  and order check and before its pending delivery is cancelled; dispute settlement after reading the
  verdict and before its order and delivery hops.
- The gate reads the user row of the buyer, the farmer, and the trucker, once per user per run. An
  escrow with a trucker leg but no `truckerId` takes its trucker the way the release does (the order's,
  when the delivery names the same one). Built trigger: a party with `suspended = true`
  (`holdReason: account_suspended`). Not built here: `fraud_confirmed` (FRD-05, built in 1.29.0) and
  `risk_critical` (FRD-04 reads the tier; built in 1.27.0). A party without a user row is
  not suspended.
- Flag on, a trigger writes the hold. Flag off, the sweep skips the escrow for this run and logs
  `RISK_SETTLEMENT_SKIPPED escrowId=<id> sweep=<auto-release|auto-refund|dispute> reason=flag_off holdReason=<reason>`.
  A user row that cannot be read skips it with `reason=user_read_failed`, flag on or off. Either way
  nothing is settled for that escrow on that run, and the next hourly run decides again. Every skip line
  is alarmed (`risk-settlement-skipped`, escrow-cron, from this deploy): the exits are to unsuspend the
  party or to settle the escrow by hand as an admin in the admin group.
- The hold write is conditional on `status = heldFromStatus AND attribute_not_exists(settlementLock)
  AND attribute_not_exists(manualPayout)` and adds 1 to `version`. For an order escrow in the
  auto-refund sweep it is one `TransactWriteItems` with a `ConditionCheck` that the order's `status`
  and `version` are still what the sweep read. A lost condition holds nothing (`ESCROW_FRAUD_HOLD_LOST`
  in the log); after an accept in between, the next run pushes the expiry as before. A concurrent
  transaction or a throttle on the fenced write also holds nothing and is retried next run, without a
  settlement failure line. A failed condition whose consistent re-read shows this very hold (same
  `heldAt`) counts as held.
- Audit: escrow-audit `fraud_hold` at `heldAt` (written once), metadata
  `holdReason=<r>;heldFor=<release|refund>;party=<buyer|farmer|trucker>;sweep=<sweep>`, and one
  transaction-audit row `fraud_hold` (entity `escrow`; the transaction is the order, else the escrow).
  Run summary keys `fraudHolds` and `riskSettlementsSkipped`.

### Exits: `POST /escrow/release` and `POST /escrow/refund`
- Admin group only. A marketplace admin role is refused with `403 ESCROW_015` (release) or
  `403 ESCROW_021` (refund); the system caller with `422 ESCROW_INVALID_TRANSITION`.
- `reason` is required, 10 to 500 characters (`400 ESCROW_VALIDATION`, field `reason`); the release
  request gains the optional `reason` field for it.
- The checks run on the status the escrow was held from: a verdict still decides the direction (held
  from `dispute_resolved` with `farmer_wins` only releases, with `buyer_wins` only refunds; the other exit
  is `422`) and its order and delivery are repaired first as for any verdict settlement (1.9.0); held from
  `escrowed`, a release needs a delivered order as any admin release does (1.17.0). A suspended farmer
  who won a dispute therefore stays held until an admin releases (fraud spec 18.3, threat T4).
- A refund of an escrow held from `escrowed` follows `heldFor`:
  - `refund`, order escrow: a still `funded` order is cancelled first (`cancelReason: escrow_refunded`,
    pending delivery, slot, stock); `rejected` refunds as it is; `cancelled` gets its stock back first when
    its cancel promised it. Any other order status, or a missing order, is refused
    (`422 ESCROW_INVALID_TRANSITION`, alarm token `RISK_HOLD_ORDER_MISMATCH`). The refund is not armed on
    the escrow for the sweeps, which never select `held`: a gateway refusal waits for the admin's retry.
  - `refund`, `/escrow/initiate` escrow: the refund alone.
  - `release`: the dispute path. The order goes `delivered` to `disputed` to `dispute_resolved` (or
    `disputed` to `dispute_resolved`; an order left at `accepted`, `pickup_confirmed` or `in_transit`
    behind a delivered delivery is advanced first) and the delivery takes the verdict; then the escrow
    records `disputeResolution: buyer_wins`, `disputeResolvedBy` and `disputeResolvedAt` while still
    `held`, and the refund moves it `held` to `refunded`. No stock is restored. A leg that cannot follow
    is refused (`422`, `RISK_HOLD_ORDER_MISMATCH`, nothing written); an order already `dispute_resolved`
    is a retry when this escrow or its delivery already carries `buyer_wins`. A failed read, hop or
    verdict write refuses with `503 ESCROW_012` (hops already made stay, and the retry goes on from
    there). No order behind the escrow: the refund alone.
- Before the order is checked or moved and before the lock, the decision is audited: escrow-audit
  `fraud_hold_release` or `fraud_hold_refund` (`held` to `held`, metadata
  `reason=<text>;holdReason=<r>;heldFor=<f>;heldFromStatus=<s>`; a failed write refuses with
  `503 ESCROW_012`, nothing moved) and the same action in transaction-audit. The usual `release` or
  `refund` row, from `held`, follows the money.
- The lock takes `held` for these two admin routes only; the settled write is `held` to `released` or
  `refunded`. A gateway that cannot pay out queues the settlement as for any other (`202 ESCROW_034`,
  the escrow stays `held`), and the manual payout close accepts `held`. A lock on a `held` row older than
  15 minutes is parked by the recovery step like any other.

### Other paths
- `held` counts as active: `/escrow/initiate` for a delivery whose escrow is held gets `ESCROW_024`
  (order escrow) or `ESCROW_009`; delivery linkage and refund scheduling do nothing on `held`; the sweeps'
  double-escrow check counts it.
- The farmer's `accepted` on an order whose `escrow-order-{orderId}` is `held` is refused with
  `ORDER_VALIDATION: This order is under review and cannot be accepted. Contact support.`
  (`order-lifecycle.md` 1.22.0); an order escrow that cannot be read refuses the accept for now with an
  unprefixed, transient error. With `FRAUD_HOLD_ENABLED` on (the sync Lambda reads it too), the accept
  is one `TransactWriteItems` with a `ConditionCheck` that the order escrow is not `held`, so a hold
  written after the read wins and the order is not written.
- `POST /escrow/dispute` and a sync delivery dispute on `held` are refused as before (1.7.0, 1.12.0).
- Parties read `status: held` only. The admin escrow list and detail add `holdReason`, `heldAt`,
  `heldBy`, `heldFromStatus` and `heldFor`; the admin app labels `held` "Under review" and the three
  audit actions, in en, am and om.

### Not in 1.22.0
- `fraud_confirmed` (FRD-05, built in 1.29.0); `risk_critical` and `RISK_TABLE_UNCONFIGURED` (FRD-04, built in 1.27.0);
  the alarm on `RISK_HOLD_ORDER_MISMATCH` (FRD-16); the held queue and release or refund actions in the
  admin app (FRD-07).
- The client parser and badge for `held` (FRD-06). The flag stays off until FRD-06 ships (1.7.0).
- The hourly trucker leg retry (1.11.0) still pays a released escrow's trucker leg to a suspended
  trucker: `held` never applies to a released escrow (FUP-29).

---

## Additions in 1.25.0 (transport cancel and dispute race, FUP-32)

Before 1.25.0 the buyer's transport-failure cancel (ESC-06, `order-lifecycle.md` 1.15.0) checked the
escrow, cancelled the order and only then scheduled the refund, in separate writes. A freeze that
landed after the check (`POST /escrow/dispute`, a delivery dispute, the escrow-cron legacy freeze)
left a cancelled order over frozen money, and the refund step logged only an info line. A freeze in
the hour between the refund schedule and the sweep's refund did the same.

### One transaction for the cancel and its refund

The order write and the refund arm are one `TransactWriteItems` (sync Lambda):

| Item | Write | Condition |
|---|---|---|
| the order | the cancel fields, `version + 1`, `updatedAt` | `version = <expected> AND status = accepted` |
| each escrow the check found holding the money (normally `escrow-order-{orderId}` alone) | `expiresAt = now`, `refundReason = order_transport_failed`, `updatedAt` | `status = escrowed AND attribute_not_exists(settlementLock) AND attribute_not_exists(manualPayout) AND (attribute_not_exists(deliveryConfirmedAt) OR size(deliveryConfirmedAt) = 0)` |

- An escrow condition that fails refuses the cancel with `ORDER_CANCEL_NOT_ALLOWED: The payment is
  being settled or reviewed.` (the answer the check gives), also when the order condition failed too.
  Nothing is written. The pending delivery the cancel already cancelled stays cancelled, as on every
  refusal after that step (a replacement can be created). Info line: `Transport cancel of order <id>
  refused: its escrow changed after the check.`
- The order condition alone failing is the 1.13.0 version retry (re-read, at most 3 writes).
- A transaction cancelled for any other reason (a conflicting transaction, throttling) writes nothing
  and answers the unprefixed transient error, which the client retries.
- After the commit, one escrow-audit `refund_scheduled` row per armed escrow (metadata the reason, as
  before). A failed row is logged `ORDER_ESCROW_REFUND_AUDIT_FAILED orderId=<id> escrowId=<id>
  reason=[<type>]`, not an alarm token: the refund is armed, and the sweep's refund writes its own
  rows. The cancel's separate refund schedule step is gone, so `ORDER_ESCROW_REFUND_SCHEDULE_FAILED`
  no longer comes from this path.
- No new IAM: the sync Lambda's read-write grants on orders and escrows cover `UpdateItem` items in a
  transaction.

### No freeze on a scheduled refund

Every freeze write (`escrowed -> disputed`) also requires `attribute_not_exists(refundReason)`. A
scheduled refund is a settlement decided: the order was cancelled or rejected, or an admin refund is
armed, and the money is on its way back to the buyer. This holds for every refund arm
(`order_rejected`, `order_cancelled`, `order_transport_failed`, `admin_refund`, the admin queue's
re-arm).

| Path | `escrowed` escrow with `refundReason` |
|---|---|
| `POST /escrow/dispute` | `409 ESCROW_INVALID_TRANSITION` ("A refund to the buyer is scheduled for this escrow ..."), before the idempotency record is stored. Armed between the read and the write: the same answer after the condition fails, key freed. A freeze write that meets a cancel's transaction in flight (`TransactionConflictException`) is answered from the escrow as it is then, key freed, never a `500`. |
| Delivery dispute (`POST /sync`) | `DELIVERY_DISPUTE_REFUSED: The payment for this delivery is being refunded to the buyer, so it can no longer be disputed. Contact support.` (permanent, nothing written); escrow-audit `dispute_refused_refund_scheduled` (once a day per escrow and delivery, metadata `deliveryId=<id>;refundReason=<reason>`); line `ESCROW_DISPUTE_REFUSED_REFUND_SCHEDULED escrowId=<id>` (not alarmed) |
| escrow-cron freeze of a legacy disputed trade (1.15.0) | refused, so the settlement blocks with `freeze_refund_scheduled` (`<op>:trade:freeze_refund_scheduled`), audited and alarmed once; like the other permanent freeze refusals it is not asked again on later runs (`refundReason` is never removed) |

An escrow that is already `disputed` keeps its 1.12.0 answers (`409 ESCROW_020`, the mirror repair),
with or without `refundReason`.

### Interleavings

| First to reach the escrow | Cancel | Freeze | End state |
|---|---|---|---|
| the freeze, before the cancel's check | refused at the check | frozen | order `accepted`, escrow `disputed` |
| the freeze, between the check and the order write | refused by the transaction | frozen | order `accepted`, escrow `disputed` |
| the cancel, before the freeze reads | committed | refused before any write | order `cancelled`, escrow `escrowed`, refund armed |
| the cancel, between the freeze's read and its write | committed | refused by its condition | the same |

Never a cancelled order over a `disputed` escrow, and never a cancelled order without its refund arm.
Clients: no new code; `ESCROW_INVALID_TRANSITION`, `ORDER_CANCEL_NOT_ALLOWED` and
`DELIVERY_DISPUTE_REFUSED` keep their texts in en, am and om.

### Not in 1.25.0 (known limits)
- A funded order's reject or cancel, the admin refund's cancel before pickup, and the auto-refund
  sweep's cancel of a stale funded order still cancel the order first and arm or take the refund
  after. `POST /escrow/dispute` reads no order status, so a freeze in that short window still leaves
  a closed order over frozen money; once the refund is armed, no freeze lands. The same transaction
  fits them.
- A plain write to the escrow (a settlement lock, a sweep write) that meets a transport cancel's
  transaction in flight fails with `TransactionConflictException`, like any DynamoDB error on that
  path (the SDK does not retry it).

---

## Additions in 1.27.0 (the risk tier gates, FRD-04)

Builds the risk tier of 1.7.0. Design: `specs/architecture/fraud-detection-architecture.md` sections
14 to 18. The tier and the score are never on the escrow row and never in a response.

### User actions
- `POST /escrow/initiate`: the role check (`ESCROW_025`), the caller's risk tier, CRITICAL (a score read
  cut at its page cap included) `403 FRAUD_ACCOUNT_HELD` with the message
  `Your account is under review. Some actions are paused. Contact support.`, then the step-up with the
  tier (`auth-api.md` 2.10.0: MEDIUM adds the fresh password at any amount, HIGH the Fayda login and then
  the password at any amount; the buyer's profile is read for the Fayda login above 10,000 ETB and,
  from HIGH, at any amount, and a profile that cannot be read answers `503 ESCROW_012` with nothing held;
  without the users table the Fayda check fails closed), then velocity
  (`ESCROW_011`). A refused request writes nothing, calls no gateway and is not counted for velocity.
- `POST /escrow/order-checkout`: the role and body checks (`ESCROW_025`, `ESCROW_033`), then CRITICAL
  `403 FRAUD_ACCOUNT_HELD` before the velocity check, the order read and any Telebirr call. No step-up
  here: it ran at the order's `payment_submitted` (`order-lifecycle.md` 1.25.0).
- One score read per request, for the caller only. A score that could not be read (the server logs
  `RISK_SCORE_UNAVAILABLE`), a server without the fraud-signals table, or an escrow Lambda without its
  fraud service (no `RATE_LIMITS_TABLE`) is LOW: the checks of 1.26.0 apply unchanged.
- `AUTH_041` reads `Confirm it's you with Fayda to continue with this payment.` (no amount).
- Admin `POST /escrow/release` and `POST /escrow/refund` are not gated.

### Settlement sweeps
- The 1.22.0 gate also reads each party's risk tier (the buyer, the farmer and the trucker, the legacy
  trucker found as for the suspension), once per party per run and only when no party is suspended,
  so the `holdReason` precedence stays `fraud_confirmed`, `account_suspended`, `risk_critical`.
- A party at CRITICAL (a truncated read included) is the `risk_critical` trigger: with
  `FRAUD_HOLD_ENABLED` on the escrow is held as in 1.22.0; with it off the sweep skips it and logs
  `RISK_SETTLEMENT_SKIPPED ... reason=flag_off holdReason=risk_critical`. A CRITICAL party holds the
  escrow even when another party's score could not be read.
- Otherwise a party whose score could not be read skips the escrow for this run, flag on or off
  (`RISK_SETTLEMENT_SKIPPED ... reason=score_unavailable holdReason=-`): an unread score carries tier
  LOW and must not settle. A failed read is remembered for the run, so every escrow of that party waits.
  The next hourly run reads again.
- A missing fraud-signals table is LOW (suspended parties still hold). Without tier reading, escrow-cron
  logs `RISK_TABLE_UNCONFIGURED action=auto_release_and_refund` once per run, flag on or off: with the flag
  off the `flag_off` skip is what keeps a CRITICAL party unpaid, so a missing table matters either way.
  escrow-cron now gets `FRAUD_SIGNALS_TABLE` with read access only.
- MEDIUM and HIGH settle as before.
- The `fraud_hold` metadata (escrow-audit and transaction-audit) of a `risk_critical` hold adds
  `;tier=critical;score=<n>;enforcementScore=<n>;truncated=<true|false>` after the 1.22.0 fields.
- Every `RISK_SETTLEMENT_SKIPPED` line is counted by the existing `risk-settlement-skipped` alarm.

### Not in 1.27.0
- The tier in the audit metadata of a MEDIUM or HIGH settlement and of the admin release and refund
  (fraud spec section 17 matrix).
- `fraud_confirmed` (FRD-05, built in 1.29.0); HIGH's payout delay (FRD-13); the `RISK_TABLE_UNCONFIGURED` alarm (FRD-16).
- The hourly trucker leg retry (`RetryPendingTruckerLegsAsync`, 1.11.0) is not risk-gated: a trucker who
  turns CRITICAL after the release still gets a pending leg paid, as a suspended one does (1.22.0).
  `held` never applies to a released escrow; FUP-29 covers both.

---

## Additions in 1.28.0 (account deletion, ACC-02)

Design: `account-deletion.md` 1.2.0 (Effects on Other Endpoints). No escrow status, field or client change.

- **Caller refused**: `POST /escrow/initiate`, `POST /escrow/order-checkout` and `POST /escrow/dispute` answer
  `410 ACCOUNT_004` (`{"error":{"code":"ACCOUNT_004","message":"This account has been deleted.","deletionState":"..."}}`)
  when the caller's users row, read strongly consistent, carries `deletionState`; checked first, before any other
  check or read. A row that cannot be read answers `503 ESCROW_012`, nothing written. Reads (`GET /escrow/*`) stay
  open; the admin routes are not affected.
- **Counterparty refused**: an order (or, on initiate, the delivery's order) whose buyer, farmer or trucker is being
  or has been deleted cannot be paid: `422 ESCROW_030` `This order cannot be paid online.`, the code clients already
  show for an order that cannot be paid online. On `order-checkout` it is one of the order checks, after the 1.27.0
  CRITICAL and velocity checks and before the checkout slot and any Telebirr call (an open checkout is not returned).
  On `initiate` it runs in the user validation, after the 1.27.0 gates, once the delivery and its order named the
  parties (the trucker from the order, else the delivery). A failed read answers `500 ESCROW_012`, nothing held.
- **Deleted payees**: a refund to a buyer whose row is a tombstone (no phone) goes to the buyer id, as for a buyer
  without a profile; the manual payout queue shows no `payeePhone` for a deleted payee, who stays not payable
  (`ESCROW_029`: no verified phone).

---

## Additions in 1.29.0 (the Fraud Review reject trigger, FRD-05)

Builds `fraud_confirmed` of 1.7.0. Design: `specs/architecture/fraud-detection-architecture.md` 3.6.0,
sections 18.1 and 19.

### The trigger
- The admin's `reject` on a fraud signal (`POST /admin/fraud-signals/{id}/action`) suspends the signal's
  user with `suspendReason: fraud_confirmed`, through the same path as `POST /admin/users/{id}/action`
  `suspend` (Cognito disable, global sign-out, `AUTH_025` on the next refresh). It holds no escrow and
  writes none: its response only lists the user's open escrows (`escrowed`, `disputed`,
  `dispute_resolved`, `held`) read-only.
- `fraud_confirmed` is reserved: the user action route answers `400 ADMIN_017` to a `suspend` with that
  reason (trimmed, any case), and its `unsuspend` removes `suspendReason`.

### Settlement sweeps
- The gate of 1.22.0 reads `suspendReason` with `suspended` from each party's user row (same read, once
  per party per run). A party suspended with `suspendReason = fraud_confirmed` (exact value) is the
  `fraud_confirmed` trigger; it takes precedence over a party suspended for any other reason
  (`account_suspended`), whatever the party order, and over `risk_critical` (no score is read while a
  party is suspended).
- Flag on: the escrow is held as in 1.22.0 with `holdReason: fraud_confirmed`; the `fraud_hold` audit
  metadata reads `holdReason=fraud_confirmed;heldFor=..;party=..;sweep=..`. Flag off: the sweep skips it
  and logs `RISK_SETTLEMENT_SKIPPED ... reason=flag_off holdReason=fraud_confirmed`, counted by the
  existing `risk-settlement-skipped` alarm (its text now names the value).
- The exits are those of 1.22.0 (admin release or refund in the admin group; unsuspend the party).
- Parties see `status: held` only, as before. No client change.

### Not in 1.29.0
- A later free-text suspend of a user who is still suspended as `fraud_confirmed` replaces the reason; that
  user's escrows are then held as `account_suspended` (same hold, same money).
- The held queue in the admin app (FRD-07).

---

## Additions in 1.31.0 (old apps read statuses they parse, FUP-61)

Builds the server-side mapping of §Additions in 1.7.0 ("Client compatibility"), option 2 of
`specs/architecture/fraud-detection-architecture.md` 18.7 (3.8.0).

### The app build header
- The server reads `X-App-Build: <n>`, `n` being the build number of `mobile/pubspec.yaml`
  (`version: x.y.z+n`). The mobile and web apps send it on every request from build 2 on (FUP-61,
  the mobile change that follows this one). No build before 2 sends it, so until build 2 is out
  every client, FRD-06 installs and the deployed web app included, reads the mapped values below.
  The API's CORS allow-list carries the header.
- The server reads it case-insensitively and accepts only a positive whole number (ASCII digits).
  A missing, empty or unreadable header is an old app.

### What an old app reads
An app with no header, or a build below 2, gets these values on the party escrow routes:

| Stored `status` | Answered to an old app | Why |
|-----------------|------------------------|-----|
| `held` | `escrowed` | the money is still in escrow; mobile before FRD-06 throws on `held` and web shows `initiated` |
| `dispute_resolved` | `disputed` | the verdict waits for its settlement; the same failure as `held` on old apps, and FRD-06 already reads it as `disputed` |
| anything else | unchanged | |

Build 2 and later get the stored value.

- Routes: `GET /escrow/{id}`, `GET /escrow/by-delivery/{deliveryId}`, `GET /escrow/history` (each
  row), and the idempotent replays of `POST /escrow/initiate` and `POST /escrow/dispute`, which answer
  the row as it is now. The same for the buyer, the farmer and the trucker; party scoping is
  unchanged.
- Only the answer changes. The stored row, the `status=` filter of `GET /escrow/history` (it matches
  the stored value, so `status=held` lists held rows, answered `escrowed` to an old app), the audit
  rows, the crons, the settlement lock and every server decision keep the stored value.
- Not mapped: the admin routes (`/admin/*`, and `POST /escrow/release` and `POST /escrow/refund` on
  the admin authorizer). The `GET /escrow/*` routes sit on the marketplace authorizer, so no admin
  token reaches them.
- `holdReason`, `heldBy`, `heldFromStatus` and `heldFor` stay out of party answers, as since 1.22.0.
- Accepted: an escrow the dispute sweep holds from `dispute_resolved` reads `disputed` to an old app
  before the hold and `escrowed` after it. The money stays frozen either way, and nothing an old app
  can do on `escrowed` moves it: its dispute is refused (`ESCROW_INVALID_TRANSITION`) and release
  and refund are admin-only.

### Rollout
- `FRAUD_HOLD_ENABLED` is on in dev for sync and escrow-cron, off in staging and prod (an
  `infra/lib/config/environments.ts` value per environment). A staging or prod switch is a config
  change once FRD-06 is in both stores there.
- Until build 2 is the only one in use, every app before it reads a held escrow as `escrowed` ("Funds
  held", the old wording), never "Under review". A dispute from such an app on a held escrow is refused
  as before (`ESCROW_INVALID_TRANSITION`).
- Deploy order: the CORS allow-list must accept `X-App-Build` before a web build that sends it is
  live, or the browser preflight fails every call. Deploy the server stack first.

### Not in 1.31.0
- A minimum app version gate (ESC-25); it can reuse the header.
- Raw stored values left in party answers (follow-ups): the refusal text of `POST /escrow/dispute`
  (`Cannot dispute escrow in 'held' state. Must be 'escrowed'.`), the delivery dispute refusal text
  on `/sync` (`... (status: held) ...`), and the `409` blockers of the account deletion
  (`account-deletion.md`), which list each blocking escrow with its stored `status`. The texts are
  free text, not parsed values.

---

## Changelog

| Version | Date | Change |
|---------|------|--------|
| 1.32.0 | 2026-10-23 | Fraud signals (FRD-08, fraud spec 3.9.0): a verified webhook payer mismatch writes `TELEBIRR_PAYER_MISMATCH` (weight 0) on the buyer of our own escrow or checkout row when the payment is applied and `TELEBIRR_PAYER_REFUSED` (soft, 5) when the payer match refuses it; the payer phone in Telebirr's `251XXXXXXXXX` form is compared as `+251XXXXXXXXX`; an unverified notification writes no signal on anyone; `ESCROW_025` writes `ROLE_FORBIDDEN` on the caller when the token names another role; answers unchanged and a failed signal write only logged; one payment outcome changes with `TELEBIRR_REQUIRE_PAYER_MATCH` on: a payment from the buyer's own wallet sent as `251XXXXXXXXX` was refused before and is applied now; no client change |
| 1.31.0 | 2026-10-23 | Old apps read statuses they parse (FUP-61, fraud spec 3.8.0 section 18.7): the server reads `X-App-Build` (the apps send it from build 2, a separate mobile change; until then every client reads the mapped values; CORS allows it); an app without it or below build 2 reads `held` as `escrowed` and `dispute_resolved` as `disputed` on `GET /escrow/{id}`, `GET /escrow/by-delivery/{deliveryId}`, `GET /escrow/history` and the initiate and dispute replays, for every party; stored rows, filters, audit, admin and server decisions keep the stored value; `FRAUD_HOLD_ENABLED` on in dev for sync and escrow-cron, off in staging and prod |
| 1.29.0 | 2026-10-23 | Fraud Review reject trigger built (FRD-05, the 1.7.0 rules): the admin's `reject` on a fraud signal suspends its user with the reserved `suspendReason: fraud_confirmed` through the user route's suspend path and holds nothing (the response lists the user's open escrows read-only); the settlement sweeps hold (flag on) or skip (flag off, `RISK_SETTLEMENT_SKIPPED ... holdReason=fraud_confirmed`) an escrow with such a party as `holdReason: fraud_confirmed`, ahead of `account_suspended` and `risk_critical`; `POST /admin/users/{id}/action` refuses that reason (`400 ADMIN_017`) and its `unsuspend` removes `suspendReason`; `risk-settlement-skipped` alarm text names the value; no client change |
| 1.28.0 | 2026-10-23 | Account deletion (ACC-02, `account-deletion.md` 1.2.0): `POST /escrow/initiate`, `/escrow/order-checkout` and `/escrow/dispute` answer `410 ACCOUNT_004` to a caller whose account is being or has been deleted (a row that cannot be read: `503 ESCROW_012`); initiate and order-checkout answer `422 ESCROW_030` for an order whose buyer, farmer or trucker is being or has been deleted, after the 1.27.0 gates and before any slot, hold or Telebirr call; a refund to a deleted buyer goes to the buyer id, never an empty phone; the manual payout queue shows no phone for a deleted payee; no client change |
| 1.27.0 | 2026-10-23 | Risk tier gates built (FRD-04, the 1.7.0 rules): `POST /escrow/initiate` refuses a caller at risk tier CRITICAL with `403 FRAUD_ACCOUNT_HELD` after the role check and before the step-up and velocity, and the step-up takes the tier (MEDIUM the password at any amount, HIGH Fayda and then the password at any amount); `POST /escrow/order-checkout` refuses CRITICAL before velocity and any Telebirr call; an unreadable score or a missing fraud service is LOW at these gates; the settlement sweeps hold (flag on) or skip (flag off) the escrow of a party at CRITICAL (`holdReason: risk_critical`, tier and score in the `fraud_hold` audit metadata) and skip, flag on or off, one whose score cannot be read (`reason=score_unavailable`); escrow-cron reads the fraud-signals table and logs `RISK_TABLE_UNCONFIGURED` once per run without it, flag on or off; `AUTH_041` text amount-neutral; admin routes not gated; no client change |
| 1.26.0 | 2026-10-23 | Dispute alarms (FUP-27): `DISPUTE_PROPAGATION_FAILED` (a dispute mirror leg that failed) is counted in the escrow, sync and escrow-cron log groups, the Lambdas that run the mirror, by the new alarm `dispute-propagation-failed` (money alarm shape); the line is written through the money alarm sink with its `legs` defanged like the verdict lines (the `dispute_propagation_failed` audit row keeps them raw); the sync Lambda logs `DELIVERY_DISPUTE_FROZEN escrowId=.. orderId=.. deliveryId=..` once a delivery dispute froze its escrow, counted into the `escrow-dispute-rate` metric with the escrow Lambda's `POST /escrow/dispute` audit line (an escrow already disputed, a refused or undecided freeze and the repairs of pre-1.12.0 disputes are not counted); no API, error code or client change (1.18.0 and 1.20.0 to 1.25.0 are in flight) |
| 1.25.0 | 2026-10-23 | Transport cancel and dispute race (FUP-32): the buyer's transport-failure cancel writes the order and the refund arm (`expiresAt`, `refundReason`) on each held escrow in one `TransactWriteItems`, the escrow item conditioned on a plain hold, so a freeze that landed after the check refuses the cancel (`ORDER_CANCEL_NOT_ALLOWED`) and nothing is written; every freeze write requires `attribute_not_exists(refundReason)`: `POST /escrow/dispute` on an `escrowed` escrow with a scheduled refund answers `409 ESCROW_INVALID_TRANSITION` (also after a lost race or a `TransactionConflictException`, key freed), a delivery dispute is refused (`DELIVERY_DISPUTE_REFUSED`, audit `dispute_refused_refund_scheduled`), the escrow-cron legacy freeze blocks with the permanent reason `freeze_refund_scheduled` (alarmed once, not asked again); `refund_scheduled` audit row after the commit, a failed row logged `ORDER_ESCROW_REFUND_AUDIT_FAILED` (not alarmed); no client change (`order-lifecycle.md` 1.23.0) |
| 1.24.0 | 2026-10-23 | The dispute step-up read fails closed with a typed answer (FUP-44): when `POST /escrow/dispute` cannot read the escrow (or an admin caller's profile) to decide the step-up, it answers `503 ESCROW_012` with nothing written (no escrow write, no escrow-audit row, no idempotency record), so the same request can be retried; before, the exception escaped the handler and API Gateway answered `502` without a code; no new read or write (1.20.0 to 1.23.0 belong to other tickets still in flight) |
| 1.23.0 | 2026-10-23 | Client handling (FRD-06): one tolerant status parser on mobile and web (`dispute_resolved` reads as `disputed`, any unknown value as a neutral "Status Unknown" with no actions); `held` shown as "Under Review" on escrow and order screens for all three parties (order detail reads the order escrow, the order list one `status=held` history page), farmer Accept and the transport-failure cancel hidden while held; `FRAUD_ACCOUNT_HELD` keeps its code and its own text on initiate and order checkout, a queued intent fails at once; `AUTH_041` on dispute opens the Fayda login; no server change |
| 1.22.0 | 2026-10-23 | Fraud hold (FRD-03): the three settlement sweeps ask a risk gate before the first money side effect; a suspended buyer, farmer or trucker holds the escrow (`held`, `holdReason: account_suspended`, `heldAt`, `heldBy: system`, `heldFromStatus`, `heldFor`) behind `FRAUD_HOLD_ENABLED`, shipped off (the sweep only skips: `RISK_SETTLEMENT_SKIPPED reason=flag_off`); an unreadable party row skips (`reason=user_read_failed`); every skip is alarmed (`risk-settlement-skipped`); the hold write loses to a lock or a queued payout, and the auto-refund hold of an order escrow is fenced on the order's version and status in one transaction; admin-group exits with a required `reason` (10 to 500; `reason` added to the release request), checked on the held-from status, the refund by `heldFor` (order cancel and restock, or the dispute path to `dispute_resolved` with `buyer_wins`; `RISK_HOLD_ORDER_MISMATCH` and `422` when the order cannot follow); audit `fraud_hold`, `fraud_hold_release`, `fraud_hold_refund` in escrow-audit and transaction-audit; `held` in the active set, the lock (admin only), the manual payout close and the stale lock recovery; the farmer's accept of an order whose escrow is held refused (`ORDER_VALIDATION`; with the flag on, fenced on the escrow in one transaction); a held verdict keeps its direction; admin-only hold fields on the admin escrow reads; no client change |
| 1.21.0 | 2026-10-22 | Dispute dispositions and the pickup rule (FUP-16; 1.18.0, 1.19.0 and 1.20.0 belong to FUP-36, ESC-47 and FUP-02, in flight): the verdict endpoint decides the order and delivery before writing and records a verdict they cannot follow only with a `disposition` and a `reason` (`422 ADMIN_015` without one, `422 ADMIN_016` when none fits or the chosen one does not, `400 ADMIN_014` for a bad value or reason, `503 ESCROW_012` on a failed read); `return_to_farmer` and `delivered_anyway` for `buyer_wins` with the goods moving, `settle_now` (after pickup) and `wait_for_delivery` for `farmer_wins` before delivery; the disposition is stored on the escrow in the verdict write (`disputeDisposition*`), audited once (`dispute_resolved_goods_returned`, `dispute_resolved_delivered_anyway`, `dispute_resolved_settled_early`, `dispute_resolved_waiting_for_delivery`, escrow audit at the choice time plus four ticks, plus transaction audit; shown on the admin `DisputeDto`), then closes the delivery (`cancelled` with `cancelReason: dispute_<verdict>`, or `disputed`) and the order (`dispute_resolved`) as a system actor, without restoring stock; a replay records a new choice (conditional on the one it read, `409 ADMIN_010`) or repairs the recorded one; the settlement mirror accepts a delivery cancelled under the same verdict and never treats its order as before pickup; a recorded `wait_for_delivery` is refused without the alarm (`DISPUTE_VERDICT_WAITING_FOR_DELIVERY`); status-filtered admin lists read on past filtered-out pages (25-page budget, `nextToken`); `overrideOrderMismatch` deprecated for verdicts; `POST /escrow/dispute` refused before pickup (`422 ESCROW_041`, `503 ESCROW_012` on a failed read), localized in the apps; admin Disputes page with an awaiting-settlement tab and the disposition choice |
| 1.20.0 | 2026-10-23 | Manual refund of an unapplied payment (FUP-02; 1.18.0 FUP-36 and 1.19.0 ESC-47 are in flight): `GET /admin/payments/unapplied` (filtered Scan of escrow-audit, at most 4 pages of 500 rows per call, signed token that skips the order already read) and `POST /admin/payments/{orderId}/refund-manual` (admin group only) write the reserved `order_payment_refunded_manual` row (metadata `paymentOrderId=..;amount=..` then `;merchOrderId=..`, attribute `reason`) and transaction-audit `admin_payment_refunded_manual`; a payment is matched by its payment_order_id, a nameless one by its merch_order_id, whenever the rows were written; a payment that funded the order, or may still fund it (only `unprocessed` rows, or nameless, while the order is `payment_submitted`), is refused (`409 ESCROW_044`) and not listed (funded) or flagged `mayStillApply`; the record is exclusive per payment (one `TransactWriteItems` with a claim row `refund-claim#{orderId}#{payment}` in escrow-idempotency, 400-day TTL; the admin Lambda gains `PutItem` there only); the list reads at most 50 order trails per call and resumes after the last one; the amount is checked against the captured amount when the rows record it (`409 ESCROW_043`, else `amountChecked: false`); `400 ESCROW_040`, `404 ESCROW_042`, `409 ESCROW_036` reused for a reference conflict; a replay, or a second admin with the same reference, answers `alreadyRecorded` and writes nothing; `ESCROW_041` left to FUP-16; `account-deletion.md` 1.1.0 uses the same rule; admin page in en/am/om; no client change |
| 1.19.0 | 2026-10-23 | The funding's transaction-audit row survives a failed write (ESC-47 item 2): the `payment_verified` row of a Telebirr order funding is keyed by the order's `paymentVerifiedAt` and written with a conditional put on the funding delivery; a redelivery of the same payment (fresh, or an inbox replay) writes it when it is missing, in every order status, after its stock and escrow repair and after reading the order's trail (a row under any timestamp counts; the webhook Lambda gets `dynamodb:Query` on the transaction-audit base table); an unreadable trail writes nothing, and a failed read or write logs `ORDER_PAYMENT_BOOKKEEPING_FAILED step=payment_verified_audit` with the order and payment ids; no client change (1.18.0 is FUP-36's, in flight) |
| 1.18.0 | 2026-10-22 | Legacy delivery initiate refused on live Telebirr (FUP-36): `POST /escrow/initiate` answers `422 ESCROW_039` before any read or write whenever the gateway is live Telebirr (`ITelebirrGateway.IsLiveTelebirr`: not the mock, and the real gateway's `baseUrl` host not loopback or `host.docker.internal`); mock and local simulator unchanged; clients show `ESCROW_039` in en, am, om and keep any `ESCROW_*` code on an untyped `422` |
| 1.17.0 | 2026-10-10 | The order follows the money (ESC-09): every escrow money writer moves the order after its write (`disbursing` once the release is committed, `settled` once `completed` with no leg owed, a verdict escrow's order stays `dispute_resolved`), status-conditioned with `ADD version`; the hourly order-money sweep (`CronRunBudget`, now also the budget of ESC-10's paged sweeps with their limits unchanged; skip marker `moneySweepSkip`, escrow-audit `order_money_mismatch`/`order_money_ahead` once at `createdAt + 7/8` ticks) repairs and backfills; alarm `order-money-mismatch` (`ORDER_MONEY_MISMATCH`); Part B: an admin release of a plain escrowed order escrow needs a delivered order (`422`, no override), an admin refund cancels a pre-pickup order first and schedules the refund, refuses goods on the move unless overridden (`refundOverride*`) |
| 1.16.0 | 2026-10-10 | Telebirr live dialect, webhook answers and inbox replay (ESC-45): the flat documented notify (one map verified and read, `data` envelope, nested `biz_content`, `transId` alias only without `trans_id`, duplicate keys and nested values refused), PSS or PKCS#1 accepted inbound, `notify_time` freshness by digit range with a 48 h late window on the order path only; `500` for verified notifications not recorded or not applied for a retryable reason, small JSON bodies; inbox rows carry `orderId`, the stored payment fields and a replay queue (sparse `InboxReplayIndex`); a 5-minute sweep replays them with claim, backoff and a conditional give-up after 8 attempts (`replay_exhausted`), heartbeat and stalled alarm; `ORDER_PAYMENT_REPLAY_PENDING` not alarmed; order `paymentHoldUntil` (order-lifecycle 1.16.0); every gateway call bounded by the run's deadline (`TELEBIRR_NO_TIME`, `TELEBIRR_TOKEN` definite); token path `/payment/v1/token`, timestamps in seconds; `/escrow/initiate` not usable live and cancels only on `Failed`, `Failure`, `Expired`; no payer field in the documented notify |
| 1.15.0 | 2026-10-10 | Sweep hygiene (ESC-10): the auto-release, auto-refund, dispute-settlement and reconciliation sweeps page through their whole partition (pages of 100) within a budget per sweep (50 pages, 1,000 rows that needed I/O, a time limit counted from the run start, the trucker reserve kept), with confirmation and lock skips in the filter and `sweep_truncated` when a limit stops one; a blocked settlement stores `settlementBlockReason`/`settlementBlockReasonAt` by compare-and-set and is audited once per reason (`release_blocked_kyc` from the sweeps, metadata `code=ESCROW_029;reason=<reason>`; admin attempts each time; new `auto_release_blocked`, `auto_refund_blocked` with one alarm line when first stored); the released and refunded writes clear it; the auto-release reads the delivery and order first (FUP-28): delivered and an order in flight or none pays, a disputed trade is frozen through the 1.12.0 path (this escrow only) and its delivery and order follow, a resolved dispute and every mismatch block; an expired `/escrow/initiate` escrow follows its delivery and order: goods moving or an order in flight hold it (extended to the 21-day cap, then the admin queue, or `ADMIN_QUEUE_OVERDUE reason=no_order` without an order), a pending delivery is cancelled before the refund, a double escrow or missing row blocks; an admin queue entry stamps only the escrow it owns; reconciliation rows are isolated; new run summary keys; no client change |
| 1.14.0 | 2026-10-17 | Not released: number reserved for ESC-09 and left unused when the merge order changed; ESC-09 is 1.17.0. |
| 1.13.0 | 2026-10-10 | 21-day hold cap and the admin stuck-order queue (ESC-06): the auto-refund sweep never extends an order escrow past funding + 21 days, and the extension is conditional (no `refundReason`, lock or manual payout); at the cap the order enters the admin queue (`adminQueue`, sparse `AdminQueueIndex` on orders, not versioned), audited `admin_queue_entered` and `hold_cap_reached`, and the escrow is stamped `holdCapReachedAt` last; capped escrows leave both sweep windows until a refund is scheduled or the delivery is confirmed; the hourly report produces `ADMIN_QUEUE_OVERDUE` (due from the queue time, or the pickup deadline of an accepted order; `ageHours` since due), clears an entry on escrow evidence only and re-arms a lost refund or delivery stamp; a capped escrow with a missing order logs `reason=order_missing`; `refundPending` stays false on a capped hold; the refund of a transport-failure cancel restores the listing stock first (fail closed) |
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

**Last Updated**: 2026-10-23
**Version**: 1.32.0
**Status**: Draft (Phase R4)
