# Order Lifecycle Contract (Status Levels & Escrow Semantics)

## Version
1.6.0

## Parties
- **Mobile / Web** (client) — displays the status timeline, requests buyer/farmer/logistics transitions via `POST /sync`
- **Backend** (server) — owns the state machine (`server/src/Functions/SyncFunction.cs` → `OrderTransitions` + `ValidateOrderTransitionAuth`), performs all money-linked transitions
- **Admin** — reads statuses in `/admin/transactions` timeline stitching

This document is the human- and agent-readable explanation of what every order
status **means**, who may set it, and what side effects firing it has. The
in-app timeline tooltips (l10n keys `status*Info` / `order*Info`) are the
user-facing rendering of the same table — keep both in sync.

Related contracts: `escrow-protocol.md` (escrow state machine behind
funded → settled), `sync-protocol.md` (how transitions travel),
`delivery-verification-api.md` (QR handoff behind the logistics states).

---

## Status Reference

Wire format is `snake_case` (client enum is camelCase; mapping lives in
`mobile/lib/core/models/order_status_wire.dart` — order-only, trip/delivery
statuses are camelCase on the wire).

| # | Status (wire) | Meaning | Money state | Set by |
|---|---------------|---------|-------------|--------|
| 1 | `created` | Buyer placed the order. | No money paid. | Buyer (order create) |
| 2 | `pending_payment` | Waiting for the buyer to start paying. | No money paid. | Buyer |
| 3 | `payment_submitted` | Buyer initiated the transfer (Telebirr/CBE) and submitted the reference. **Not yet verified.** | Money claimed, unconfirmed. | Buyer |
| 4 | `funded` | Payment **confirmed held in the platform escrow account**. In dev/staging (`TELEBIRR_MOCK=true` on the sync Lambda) the server auto-verifies instantly after `payment_submitted`; in production only the real Telebirr gateway callback can confirm. | Money safely in escrow. | **System only** |
| 5 | `accepted` | Farmer accepted the funded order and prepares the crops. | In escrow. | Farmer |
| 6 | `pickup_confirmed` | Pickup from the farmer confirmed (QR scan; trucker delivery or buyer self-pickup). | In escrow. | **System** when the delivery becomes `pickedUp` (1.4.0); any participant incl. the trucker may also set it |
| 7 | `in_transit` | Crops en route to the buyer. | In escrow. | **System** when the delivery becomes `inTransit`; any participant incl. the trucker |
| 8 | `delivered` | Delivery confirmed by the buyer. Starts the dispute window (see `escrow-protocol.md`: 48h; auto-release after it). | In escrow, release pending. | **System** when the delivery becomes `delivered` (self-pickup jumps here from `accepted`); a client request is refused unless the order's active delivery is already `delivered` |
| 9 | `disbursing` | Escrow release to the farmer's account is in progress (minus commission). | Leaving escrow. | **System only** |
| 10 | `settled` | Farmer received the money. Order complete. | Paid out. Terminal. | **System only** |
| — | `cancelled` | Order cancelled (buyer action, or auto-cancel — see below). Escrowed money, if any, refunds to the buyer. | Refunded/none. Terminal. | Buyer / system cron |
| — | `rejected` | Farmer declined the funded order; escrow refunds to the buyer. | Refunded. Terminal. | Farmer |
| — | `payment_failed` | Payment could not be verified. Buyer may retry (`→ pending_payment`) or cancel. | No confirmed money. | System |
| — | `disputed` | Dispute raised within the window; escrow keeps holding the money. Ratings are blocked while disputed. | Frozen in escrow. | **System** from an escrow dispute (`POST /escrow/dispute`, buyer or farmer) or a delivery dispute via sync (1.4.1) |
| — | `dispute_resolved` | Dispute decided; money released to farmer or refunded to buyer per outcome. | Released per verdict. Terminal. | **System only** (admin resolution) |

### The three "payment" levels, in one line each

- **Pending Payment** = "the buyer hasn't paid yet".
- **Payment Submitted** = "the buyer says they paid — we're checking".
- **Funded** = "the money is verified and locked in escrow — safe for the farmer to act".

---

## Allowed Transitions (server-enforced)

```
created ──► pending_payment ──► payment_submitted ──► funded ──► accepted ──► pickup_confirmed ──► in_transit ──► delivered ──► disbursing ──► settled
   │   └────────────────────────────► (direct hop: buyers usually pay immediately)                                    │
   │                                payment_submitted ──► payment_failed ──► pending_payment | cancelled              ├──► disputed ──► dispute_resolved
   └──► cancelled            funded ──► rejected | cancelled
```

Source of truth: `OrderTransitions` in `SyncFunction.cs`. Anything not in that
map is rejected as an invalid transition (version-checked, conflict on
mismatch — see `trip-transition` gotchas: send the **pre-increment** version).

### Actor authorization (`ValidateOrderTransitionAuth`)

- **System-managed** (client sync is rejected): `funded`, `disbursing`, `settled`, `dispute_resolved`.
- **Buyer-only**: `cancelled`, `pending_payment`, `payment_submitted`.
- **Farmer-only**: `accepted`, `rejected`.
- **Either participant**: `pickup_confirmed`, `in_transit`, `delivered`. The order's matched trucker counts as a participant for these three only (1.4.0); `delivered` additionally requires the order's active delivery to be `delivered`.

---

## Side Effects & Automation

| Trigger | Effect | Where |
|---|---|---|
| `→ funded` | Crop listing quantity decreases by the ordered amount; at 0 the listing becomes `reserved` (off the market). Best-effort — failure logs a warning, never blocks the order. | `TryDecreaseListingQuantityAsync`, `SyncFunction.cs` |
| `→ funded` | An escrow record is materialized for the held money (`escrow-order-{orderId}`, status `escrowed`, `source: order`). Best-effort with alarmable `ORDER_ESCROW_CREATE_FAILED` token; delivery-time linkage self-heals. | `TryCreateOrderEscrowAsync` → `EscrowService.CreateFromFundedOrderAsync`; `escrow-protocol.md` §Order-Funded Escrow |
| `funded → rejected\|cancelled` | The order's escrow is expired NOW (+`refundReason`) so the hourly auto-refund cron pays the buyer back (≤1h). The listing gets its stock back: `quantity / 100` quintals, idempotent per order (the listing records restored order ids), race-safe (version CAS), `reserved → available` (1.4.2, SIM-13). | `TryScheduleOrderEscrowRefundAsync`, `TryRestoreListingStockAsync` → `ICropRepository.TryRestoreStockAsync`; `escrow-protocol.md` |
| delivery created | The delivery (which now REQUIRES `orderId`) is linked to the order's escrow, activating confirmation-stamping / auto-release / the self-pickup QR gate. | `TryLinkOrderEscrowAsync`; `delivery-verification-api.md` |
| `payment_submitted` + `TELEBIRR_MOCK=true` | Server auto-advances to `funded` as a system actor (audited as `payment_verified`). Never active in prod / live gateway. | `TryAutoFundMockOrderAsync` |
| `created`/`pending_payment` older than 24h | Auto-cancelled by cron. | `OrderCronFunction.cs` |
| `delivered` + 48h no dispute | Escrow auto-releases to farmer (`disbursing → settled`). | escrow cron, `escrow-protocol.md` |
| escrowed + 7 days no delivery | Escrow refunds the buyer. For order-funded escrows the sweep is order-aware: it refunds only stale-`funded` or terminal orders (then cancels a stale-funded order, `cancelReason: escrow_refunded`, and restores its listing stock, 1.4.2); progressing orders get the expiry pushed +48h. | `escrow-protocol.md` §Order-Funded Escrow; `EscrowCronFunction.TryRestoreListingStockAsync` |
| delivery `pickedUp` / `inTransit` / `delivered` | The linked order advances to `pickup_confirmed` / `in_transit` / `delivered` as a system actor, forward only (never out of `disputed`, `cancelled` or a later status). Self-pickup: `accepted → delivered` in one hop. Best-effort: a failure is logged, the delivery update still succeeds. This is what unblocks ratings without any manual stepping (1.4.0, SIM-11). | `TryAdvanceOrderForDeliveryAsync`, `SyncFunction.cs` |
| escrow or delivery `disputed` | The order moves `delivered → disputed` (blocks ratings). An escrow dispute also marks the delivery `disputed`; a delivery dispute also freezes the escrow. Best-effort, audited (1.4.1, SIM-12). | `TryPropagateDisputeAsync` (`EscrowService`), `TryAdvanceOrderForDeliveryAsync` + `TryFreezeEscrowForDisputedDeliveryAsync` (`SyncFunction.cs`) |
| every applied transition | Append-only audit row (who/when/prior→new). | `EmitTxAuditAsync` → `transaction-audit` table |

### Known gap (deliberate, tracked)

Two orders funding concurrently can together exceed the remaining listing
quantity — the decrement clamps at 0 but both orders stay funded (oversell
risk). A quantity guard at funding time is a candidate follow-up; do not
"fix" this casually — it needs an idempotent conditional update.

---

## Order Create Validation (server-enforced, 1.2.0)

`POST /sync` with `entityType: order`, `action: create` is bound to the server's own listing and trip records; the client's copy is checked, never trusted (SIM-05). Every failure is `ORDER_VALIDATION` and nothing is written. Client-sent money fields (`cropCost`, `commissionRate`, `commissionAmount`, `orderTotal`) are stripped before validation.

| Rule | Rejection message |
|---|---|
| `quantity` (kg) between 0.01 and 1,000,000 (a whole listing at its 10,000-quintal maximum) | `quantity must be between 0.01 and 1000000 kg.` |
| Listing must exist and be `available` | `Listing not found.` / `Listing is not available for orders (status: x).` |
| `farmerId` == listing `farmerId`; buyer != farmer | `farmerId must be the farmer on the listing.` / `Self-trading not allowed. ...` |
| `cropType` == listing `cropType` | `cropType must match the listing.` |
| `pricePerUnit` == listing `pricePerKg` (within 0.01) | `pricePerUnit must equal the listing price (X ETB/kg).` |
| `quantity` (kg) <= listing `quantity` (quintals) × 100 | `quantity (X kg) exceeds the listing stock (Y kg).` |
| `tripId` and `truckerId` both present or both absent | `tripId and truckerId must be provided together.` |
| Trip must exist, be `open` or `matched`, belong to `truckerId`, and have a positive `pricePerQuintal` | `Trip not found.` / `Trip is not open for orders (status: x).` / `truckerId must be the trucker on the trip.` / `Trip has no valid pricePerQuintal.` |
| `transportCost`, if sent, == `quantity` / 100 × trip `pricePerQuintal` (within 0.01); without a trip it must be absent or 0 | `transportCost must equal quantity/100 x the trip pricePerQuintal (X ETB).` / `transportCost requires a tripId.` |

The server then overwrites `farmerName` (from the listing) and `truckerName` (from the trip) and computes:

```
cropCost         = quantity × pricePerUnit
commissionAmount = cropCost × commissionRate            (rate from SSM, default 0.05)
transportCost    = quantity / 100 × pricePerQuintal     (0 without a trip)
orderTotal       = cropCost + transportCost + commissionAmount
```

`orderTotal` is what the buyer pays: `POST /escrow/initiate` rejects any other amount (`ESCROW_028`, escrow-protocol.md 1.2.0).

### Units (1.2.0)

`order.quantity` is **kilograms** on the wire (mobile and web send `listing.quantity × 100` by default) and `pricePerUnit` is ETB per kg. Listing `quantity` and trip `capacity` are quintals; `pricePerQuintal` is ETB per quintal. Every conversion between the two divides or multiplies by 100: stock check at create, transport pricing, and the stock decrement at funding (`TryDecreaseListingQuantityAsync` subtracts `quantity / 100` quintals, SIM-07). A 500 kg order on a 60-quintal listing leaves 55.

## Order Update Rules (server-enforced, 1.3.0)

`POST /sync` with `entityType: order`, `action: update` (SIM-06):

- **Updatable**: `status` and its companions `rejectionReason`, `cancelReason`, `paymentMethod`, `cbeTransactionId` (length limits as on create). Status transitions and actor rules are unchanged (see above).
- **Echo keys** the clients send: `orderId` must equal the operation `entityId` (`orderId does not match the operation entityId.`), `updatedAt` is dropped (the repository stamps it), `version` feeds the optimistic-concurrency check. Client-sent `cancelledAt` / `respondedAt` / `paymentVerifiedAt` are dropped and re-stamped by the server.
- **Immutable after create**: `listingId`, `buyerId`, `farmerId`, `buyerName`, `farmerName`, `cropType`, `quantity`, `pricePerUnit`, `tripId`, `truckerId`, `truckerName`, `transportCost`. An unchanged echo is dropped; a changed value is rejected with `ORDER_VALIDATION: <field> is immutable and cannot be changed on update.` This holds in every status, including after `funded`.
- **Server-owned money** (`cropCost`, `commissionRate`, `commissionAmount`, `orderTotal`) is always dropped from the payload, so `orderTotal` can never drift from its inputs.
- **Unknown keys** are rejected: `ORDER_VALIDATION: Unknown fields: a, b`.

## List Scoping (GET /orders, 1.4.3)

`GET /orders` takes `buyerId`, `farmerId`, `truckerId` (each must equal the caller, else `403 ORDER_FORBIDDEN`), `status`, `listingId`, `limit` and `nextToken`. With no party filter the list is scoped to the caller by the token's role claim: farmer → `farmerId`, trucker → `truckerId`, buyer (or a token without a role claim) → `buyerId` (SIM-16). It used to assume buyer, so an unfiltered farmer or trucker read returned `[]`.

## Invariants

1. Money-state transitions (`funded`, `disbursing`, `settled`, `dispute_resolved`) are **never** accepted from a client — they come from the gateway callback, escrow cron, or admin resolution.
2. A funded order always reserves listing stock; cancelling/rejecting a funded order refunds the buyer (escrow protocol) and returns the stock to the listing (1.4.2), it does not silently drop money or goods.
3. The mobile timeline (`_StatusTimeline` in `order_detail_screen.dart`) must render every status in this table with its `*Info` tooltip; new statuses require: this doc + `OrderTransitions` + wire mapping + timeline step + 3× l10n.
4. Wire statuses are snake_case for orders only.
5. `order.quantity` is kilograms; listing stock and trip capacity are quintals (see Units). Money fields are computed by the server from the listing and trip at create and never taken from the client.

## Payment gates (1.5.0)

The buyer's `payment_submitted` transition is the real money path (the order-funded escrow
leg), so it carries the checks `POST /escrow/initiate` already made:

| Check | Error (operation `status: "error"`) |
|-------|--------------------------------------|
| buyer profile missing or phone not verified | `ORDER_KYC_REQUIRED: buyer_phone: ...` |
| farmer profile missing or phone not verified | `ORDER_KYC_REQUIRED: farmer_phone: ...` |
| `orderTotal` above 10,000 ETB and buyer not Fayda-verified | `ORDER_KYC_REQUIRED: buyer_fayda: Verify your Fayda ID ...` |
| `orderTotal` above 10,000 ETB and farmer not Fayda-verified | `ORDER_KYC_REQUIRED: farmer_fayda: The farmer must verify ...` |
| `orderTotal` up to and including 10,000 ETB and the token's `auth_time` older than the step-up window | `AUTH_040: Please sign in again ...` |
| `orderTotal` above 10,000 ETB and the buyer's last Fayda login (`POST /user/fayda-link`) older than the step-up window; a fresh password does not count | `AUTH_041: Confirm it's you with Fayda ...` |

The gates fail closed when the profiles cannot be read. Mock auto-funding (`TELEBIRR_MOCK`)
runs only after they pass. The client reverts the local order to its previous status on
`ORDER_KYC_REQUIRED`. For a buyer-side reason it offers Fayda verification, which must
return the account owner's identity (`POST /user/fayda-link`, `auth-api.md` 2.3.0); on
success it sends the same payment again (for `buyer_phone`, only when the link also
proved the phone, `phoneVerified: true`), on failure the payment stays unsent. For a
farmer-side reason it only explains. On `AUTH_040` it asks for the password and retries; on
`AUTH_041` it runs the Fayda link flow as the account owner and retries. A buyer who is not
Fayda-verified yet gets `buyer_fayda` first; that link is also the fresh Fayda login, so the
resubmitted payment passes both gates.

Once a delivery exists for an accepted order (a trucker's, or the buyer's self-pickup), the
order detail screen offers every party "Open delivery" instead of a second create, and the
farmer of a pending delivery "Show QR Code" (the code the trucker or the self-pickup buyer
scans). A create the server refuses with `DELIVERY_VALIDATION: The order already has an
active delivery.` opens the existing delivery.

---

## Changelog

- **1.6.0 (2026-09-29)**: Payment step-up is tiered: a fresh password (`AUTH_040`) on every payment up to 10,000 ETB, a fresh Fayda login (`AUTH_041`) above it instead of the password. Order detail links every party to the order's delivery (and the farmer to the pickup QR) once one exists, so a second create is never offered.
- **1.5.2 (2026-09-29)**: The order quantity cap is 1,000,000 kg, the kg size of a full 10,000-quintal listing. It was 100,000, which rejected full orders on any listing above 1,000 quintals. The place-order screen now blocks a quantity above the listing stock before sending.
- **1.5.1 (2026-09-29)**: KYC errors name whose verification is missing (`buyer_phone`, `buyer_fayda`, `farmer_phone`, `farmer_fayda`); the buyer's payment is resubmitted after a successful Fayda link and not otherwise.
- **1.5.0 (2026-09-26)**: Payment gates on `payment_submitted`: both parties phone-verified and, above 10,000 ETB, Fayda-verified (`ORDER_KYC_REQUIRED`); fresh password (`AUTH_040`) at or above the step-up threshold; mock auto-fund runs only after the gates.
- **1.4.3 (2026-09-25)**: Default list scoping (SIM-16): an unfiltered `GET /orders` is scoped by the caller's role claim instead of always the buyer.
- **1.4.2 (2026-09-25)**: Stock restore (SIM-13): a funded order that is rejected or cancelled (by a participant or by the refund sweep) gives its listing the quintals back, idempotent per order and race-safe; a `reserved` listing returns to `available`. The funding decrement now stores `quantity` as a number.
- **1.4.1 (2026-09-25)**: Dispute propagation (SIM-12): an escrow dispute or a delivery dispute moves a `delivered` order to `disputed`; the escrow dispute marks the delivery disputed and a delivery dispute freezes the escrow; ratings stay blocked while disputed.
- **1.4.0 (2026-09-25)**: Order follows its delivery (SIM-11): delivery `pickedUp`/`inTransit`/`delivered` advance the order server-side (forward only, best-effort, system-audited); the order's trucker may set the three logistics statuses; a client `delivered` is refused unless the active delivery is `delivered`.
- **1.3.0 (2026-09-25)**: Order update allow-list (SIM-06): only status and its companions may change; identity and pricing fields are immutable in every status; echoed bookkeeping keys are checked or dropped; unknown keys rejected.
- **1.2.0 (2026-09-25)**: Server-bound order create (SIM-05): listing and trip existence, ownership, cropType, price, stock and transport-cost binding; server computes every money field and overwrites farmer/trucker names. Units documented; funding decrements listing stock in quintals (SIM-07).
- **1.1.0 (2026-07-12)** — Order-funded escrow leg: funding materializes an escrow record; reject/cancel of a funded order schedules the buyer's refund (invariant 2 is now implemented, not aspirational); deliveries require `orderId`; order-aware 7-day refund cancels stale-funded orders.
- **1.0.0 (2026-07-11)** — Initial version, extracted from `SyncFunction.cs` / `OrderCronFunction.cs` / `escrow-protocol.md` behavior while adding the timeline step tooltips.
