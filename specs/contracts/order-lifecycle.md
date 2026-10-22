# Order Lifecycle Contract (Status Levels & Escrow Semantics)

## Version
1.19.0

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
| 3 | `payment_submitted` | Buyer initiated the transfer (Telebirr/CBE) and submitted the reference. **Not yet verified.** The server stamps `paymentSubmittedAt`, which starts the payment clock (1.7.0). | Money claimed, unconfirmed. | Buyer |
| 4 | `funded` | Payment **confirmed held in the platform escrow account**. In dev/staging (`TELEBIRR_MOCK=true` on the sync Lambda) the server auto-verifies instantly after `payment_submitted`; with the live gateway the buyer pays through `POST /escrow/order-checkout` and the Telebirr webhook funds the order (1.7.0, `escrow-protocol.md` 1.5.0). | Money safely in escrow. | **System only** |
| 5 | `accepted` | Farmer accepted the funded order and prepares the crops. | In escrow. | Farmer |
| 6 | `pickup_confirmed` | Pickup from the farmer confirmed (QR scan; trucker delivery or buyer self-pickup). | In escrow. | **System** when the delivery becomes `pickedUp` (1.4.0). A participant (incl. the trucker) may request it only once the order's active delivery is `pickedUp` or later; an order without an active delivery is refused (1.10.0) |
| 7 | `in_transit` | Crops en route to the buyer. | In escrow. | **System** when the delivery becomes `inTransit`. A participant (incl. the trucker) may request it only once the active delivery is `inTransit` or later (1.10.0) |
| 8 | `delivered` | Delivery confirmed by the buyer. Starts the dispute window (see `escrow-protocol.md`: 48h; auto-release after it). | In escrow, release pending. | **System** when the delivery becomes `delivered` (self-pickup jumps here from `accepted`); a client request is refused unless the order's active delivery is already `delivered` |
| 9 | `disbursing` | The release is committed: the farmer leg is paid or waits in the manual payout queue (also an unknown outcome or a parked release lock); the trucker leg may still be owed (1.17.0). With the live gateway this can last days. | Leaving escrow. | **System only**, when the escrow becomes `released` or its release is queued (1.17.0) |
| 10 | `settled` | Every leg is paid: the farmer and, when there is one, the trucker. Order complete. | Paid out. Terminal. | **System only**, when the escrow becomes `completed` with no leg owed (1.17.0) |
| — | `cancelled` | Order cancelled (buyer action, or auto-cancel, see below). Escrowed money, if any, refunds to the buyer. An `accepted` order only after a transport failure (1.15.0, see Transport failure cancel). | Refunded/none. Terminal. | Buyer / system cron |
| — | `rejected` | Farmer declined the funded order; escrow refunds to the buyer. | Refunded. Terminal. | Farmer |
| — | `payment_failed` | Payment could not be verified: the order cron sets it 24 hours after `paymentSubmittedAt` when no payment was confirmed (1.7.0). Buyer may retry (`→ payment_submitted` directly since 1.13.0, or `→ pending_payment`) or cancel. | No confirmed money. | **System only** (enforced since 1.7.0) |
| — | `disputed` | Dispute raised within the window; escrow keeps holding the money. Ratings are blocked while disputed. | Frozen in escrow. | **System only** (1.14.0): from an escrow dispute (`POST /escrow/dispute`, buyer or farmer) or a delivery dispute via sync (1.4.1), each only after the escrow is frozen. A client order update to `disputed` is refused (`ORDER_UNAUTHORIZED`) |
| — | `dispute_resolved` | Dispute decided; money released to farmer or refunded to buyer per outcome. The order stays here after the verdict is settled (it never moves to `disbursing` or `settled`; the escrow status says whether the money moved, 1.17.0). | Released per verdict. Terminal. | **System only**: when the admin records the verdict (`POST /admin/disputes/{id}/resolve`), and again before the escrow is settled if that failed (1.12.0), and by the hourly order-money sweep after a settled verdict whose order hop failed (1.17.0) |

### The three "payment" levels, in one line each

- **Pending Payment** = "the buyer hasn't paid yet".
- **Payment Submitted** = "the buyer says they paid — we're checking".
- **Funded** = "the money is verified and locked in escrow — safe for the farmer to act".

---

## Allowed Transitions (server-enforced)

```
created ──► pending_payment ──► payment_submitted ──► funded ──► accepted ──► pickup_confirmed ──► in_transit ──► delivered ──► disbursing ──► settled
   │   └────────────────────────────► (direct hop: buyers usually pay immediately)                                    │
   │                                payment_submitted ──► payment_failed ──► payment_submitted | pending_payment | cancelled ├──► disputed ──► dispute_resolved
   └──► cancelled            funded ──► rejected | cancelled
                             accepted ──► cancelled   (buyer, only after a transport failure, 1.15.0)
```

Source of truth: `OrderTransitions` in `SyncFunction.cs`. Anything not in that
map is rejected as an invalid transition (version-checked, conflict on
mismatch — see `trip-transition` gotchas: send the **pre-increment** version).

### Actor authorization (`ValidateOrderTransitionAuth`)

- **System-managed** (client sync is rejected): `funded`, `disbursing`, `settled`, `dispute_resolved`, since 1.7.0 `payment_failed`, and since 1.14.0 `disputed`. Before 1.7.0 any participant (the farmer too) could set `payment_failed` while a Telebirr payment was in flight, which let the buyer resubmit and pay twice. Before 1.14.0 the buyer or the farmer could set `delivered → disputed` on the order itself: no escrow was frozen, the delivery stayed `delivered`, the 48-hour auto-release still paid the farmer, and the admin never saw the dispute (ESC-07). A dispute now comes only from the delivery (sync) or `POST /escrow/dispute`. No shipped app ever sent the order update.
- **Buyer-only**: `cancelled`, `pending_payment`, `payment_submitted`. From `accepted`, `cancelled` is allowed only after a transport failure and runs its own checks (1.15.0, Transport failure cancel).
- **Farmer-only**: `accepted`, `rejected`.
- **Either participant**: `pickup_confirmed`, `in_transit`, `delivered`. The order's matched trucker counts as a participant for these three only (1.4.0). Each is a catch-up of what the delivery already says: `delivered` requires the order's active delivery to be `delivered` (1.4.0; orders without an active delivery keep the old behaviour), `pickup_confirmed` requires it to be `pickedUp` or later and `in_transit` requires `inTransit` or later, and an order with no active delivery is refused for both (`ORDER_VALIDATION`, 1.10.0). Before 1.10.0 any participant could mark pickup or transit with no delivery at all.

---

## Side Effects & Automation

| Trigger | Effect | Where |
|---|---|---|
| `→ funded` | Crop listing quantity decreases by the ordered amount; at 0 the listing becomes `reserved` (off the market). Idempotent per order since 1.7.0 (the listing records `stockDecrementedFor` order ids; version CAS with retry), so a payment redelivery can repeat it safely. Best-effort: a failure logs the alarmable `ORDER_STOCK_DECREMENT_FAILED` token, never blocks the order. | `OrderFundingService.DecrementStockAsync` → `ICropRepository.TryDecrementStockAsync` (mock auto-fund in `SyncFunction.cs`, live payments in `TelebirrWebhookFunction.cs`) |
| `→ funded` | An escrow record is materialized for the held money (`escrow-order-{orderId}`, status `escrowed`, `source: order`). Best-effort with alarmable `ORDER_ESCROW_CREATE_FAILED` token; delivery-time linkage self-heals, and a payment redelivery repairs it. | `OrderFundingService.EnsureFundedSideEffectsAsync` → `EscrowService.CreateFromFundedOrderAsync`; `escrow-protocol.md` §Order-Funded Escrow |
| live Telebirr payment for an order checkout | One write conditioned on `status = payment_submitted` sets `funded`, `paymentVerifiedAt` and the server-only `telebirrMerchOrderId`, `telebirrPaymentOrderId`, `telebirrTransId` (clients can never set them; create and update strip them), then runs the two side effects above and a transaction-audit `payment_verified` row (actor `telebirr`). A second payment, a payment for an order no longer waiting, or one made in a checkout from an earlier `payment_submitted` (the checkout records the order's `paymentSubmittedAt`) changes nothing and is alarmed for a manual refund. | `TelebirrWebhookFunction.cs`; `escrow-protocol.md` §Additions in 1.5.0 |
| `funded → rejected\|cancelled` | The order's escrow is expired NOW (+`refundReason`) so the hourly auto-refund cron pays the buyer back (≤1h). The listing gets its stock back: `quantity / 100` quintals, idempotent per order (the listing records restored order ids), race-safe (version CAS), `reserved → available` (1.4.2, SIM-13). | `TryScheduleOrderEscrowRefundAsync`, `TryRestoreListingStockAsync` → `ICropRepository.TryRestoreStockAsync`; `escrow-protocol.md` |
| `funded → accepted` | The server stamps `pickupDeadlineAt` in the same version-checked write as `respondedAt` (1.15.0). A trip whose row cannot be read refuses the accept for now with an unprefixed error (the client retries), so no accepted order goes without its deadline. | `ProcessOrderUpdateAsync`, `PickupDeadline` |
| `accepted → cancelled` (buyer, after a transport failure) | The pending delivery is cancelled first, then the order (`cancelReason: transport_failed_buyer_cancelled`, the buyer's text in `cancelNote`), the delivery slot is released, the listing stock restored and the escrow refund scheduled (`refundReason: order_transport_failed`); audited (1.15.0). | `ProcessTransportFailureCancelAsync`; Transport failure cancel below |
| delivery created | The delivery (which now REQUIRES `orderId`) is linked to the order's escrow, activating confirmation-stamping / auto-release / the self-pickup QR gate. The slot claim requires the order to be `accepted`, `pickup_confirmed` or `in_transit` in the same transaction (1.15.0): a create racing a cancel is refused (`DELIVERY_VALIDATION: The order no longer takes a delivery ...`). | `TryLinkOrderEscrowAsync`; `delivery-verification-api.md` |
| `payment_submitted` + `TELEBIRR_MOCK=true` | Server auto-advances to `funded` as a system actor (audited as `payment_verified`). Never active in prod / live gateway. | `TryAutoFundMockOrderAsync` |
| `created`/`pending_payment` older than 24h | Auto-cancelled by cron. | `OrderCronFunction.cs` |
| `payment_submitted` for 24h (from `paymentSubmittedAt`; `createdAt` for orders from before 1.7.0) with no confirmed payment | Moved to `payment_failed` by the same hourly cron, version-checked so a funding that lands at the same time wins; audited as `order_updated` (`payment_submitted → payment_failed`). Since 1.16.0 (ESC-45) never while a verified Telebirr payment for the order is pending in the webhook inbox: the cron skips orders whose server-only `paymentHoldUntil` is in the future and orders with a row in the inbox's `pending` partition (if that partition cannot be read, no order is failed that run), and its write is also conditional on the hold, so a hold written after the cron read the order wins. The hold is set by the webhook for every verified `Completed` notification of the order's current payment attempt (extend-only, without changing `version` or `updatedAt`) and lapses on its own once the inbox stops replaying the payment (at most about 75 minutes after the last replay; `escrow-protocol.md` §Additions in 1.16.0). The order checkout stops opening new attempts 20 hours after `paymentSubmittedAt` (`escrow-protocol.md` 1.5.0), so none is still open then. A retry goes `payment_failed → payment_submitted` in one change (1.13.0, ESC-47 item 1): the payment gates run as for any payment, `paymentSubmittedAt` is stamped again, and the `pending_payment` auto-cancel above (which counts from `createdAt`) never sees the order. The older `payment_failed → pending_payment → payment_submitted` path is still accepted, but an order older than 24h left at `pending_payment` is cancelled at the next run (1.7.0). | `OrderCronFunction.FailStalePaymentsAsync` |
| `delivered` + 48h no dispute | Escrow auto-releases to farmer; the order follows the money (`disbursing`, then `settled`), see "The order follows the money" below (1.17.0). | escrow cron, `escrow-protocol.md` |
| release committed (escrow `released`, or the release queued for a manual payout) | `delivered → disbursing`, after the escrow write (1.17.0). | `OrderMoneyMirror`; below |
| escrow `completed` (every leg paid) | `→ settled` (through `disbursing`), after the escrow write (1.17.0). | `OrderMoneyMirror`; below |
| admin refund of a plain `escrowed` order escrow (`POST /escrow/refund`, not the system sweep) | `funded`, or `accepted` with no delivery or a `pending` one: the order is cancelled first (pending delivery first, `cancelReason: escrow_refunded`, slot freed, listing stock restored) and the refund is scheduled on the escrow, then refunded. `cancelled` or `rejected`: refunded as is (stock given back first after an `escrow_refunded` or transport-failure cancel). Goods moving or `delivered`: `422` unless `overrideOrderMismatch` with a reason (admin group), which refunds, flags the order (`refundOverride*`, server-only) and leaves its status. Anything else: `422` (1.17.0, ESC-09). | `EscrowService.PrepareAdminRefundOrderAsync`; `escrow-protocol.md` §Additions in 1.17.0 |
| admin release of a plain `escrowed` order escrow | Only an order `delivered` or `disbursing` (an order left behind a delivered delivery is advanced to `delivered` first); anything else `422`, no override (1.17.0). | `EscrowService.CheckAdminReleaseOrderAsync` |
| escrowed + 7 days no delivery | Escrow refunds the buyer. For order-funded escrows the sweep is order-aware: it refunds only stale-`funded` or terminal orders (then cancels a stale-funded order, `cancelReason: escrow_refunded`, and restores its listing stock, 1.4.2); progressing orders get the expiry pushed +48h, never past funding + 21 days (1.15.0). | `escrow-protocol.md` §Order-Funded Escrow; `EscrowCronFunction.TryRestoreListingStockAsync` |
| funded + 21 days, order not done | No more extension: the order enters the admin stuck-order queue (server-only `adminQueue`, `adminQueuedAt`, `adminQueueReason: hold_cap`, `adminQueueEscrowId`, written without bumping `version`), audited `admin_queue_entered`; the hourly report alarms `ADMIN_QUEUE_OVERDUE` once an entry is due for 24 hours and clears it when the escrow shows the money moved on. Nothing moves the money by itself (1.15.0). | `EscrowCronFunction`; `escrow-protocol.md` §Additions in 1.13.0 |
| escrow due to settle (auto-release, auto-refund, dispute settlement) while a party is suspended or at risk tier CRITICAL | The escrow moves to `held` instead of paying out; the order status does not change (a stale-funded order is not cancelled and its stock not restored until the admin decides). Planned FRD-03, FRD-04, behind `FRAUD_HOLD_ENABLED` (1.9.0). | `EscrowCronFunction`; `escrow-protocol.md` §Additions in 1.7.0 |
| delivery `pickedUp` / `inTransit` / `delivered` | The linked order advances to `pickup_confirmed` / `in_transit` / `delivered` as a system actor, forward only (never out of `disputed`, `cancelled` or a later status). Self-pickup: `accepted → delivered` in one hop. Best-effort: a failure is logged, the delivery update still succeeds. This is what unblocks ratings without any manual stepping (1.4.0, SIM-11). | `TryAdvanceOrderForDeliveryAsync`, `SyncFunction.cs` |
| escrow or delivery `disputed` | Every dispute freezes the escrow **first** (1.14.0, ESC-07); only a frozen (or already `disputed`) escrow lets the delivery and the order move, and a refused freeze moves nothing. A delivery dispute that cannot freeze (manual payout queued, settlement in flight, escrow not holding the money, no escrow for a funded order) is refused with `DELIVERY_DISPUTE_REFUSED` (`sync-protocol.md` 1.12.0). Then the order moves `delivered → disputed` (blocks ratings); an order left at `accepted`, `pickup_confirmed` or `in_transit` behind a `delivered` or `disputed` delivery is first advanced to `delivered`. System actor, version-checked from a consistent read, the third conflict gives up, one transaction-audit row per hop (`order_updated`, `dispute`). An escrow dispute also marks the delivery `disputed` (transaction-audit `dispute`). A failed hop leaves the money frozen and is repaired by the verdict mirror before any settlement (1.4.1 SIM-12, 1.14.0). | `EscrowService.FreezeForDeliveryDisputeAsync` + `MirrorDisputeAsync`; `FreezeBeforeDeliveryDisputeAsync` (`SyncFunction.cs`) |
| admin dispute verdict (`farmer_wins` or `buyer_wins`) | The order moves to `dispute_resolved`: `delivered → disputed → dispute_resolved` or `disputed → dispute_resolved`; an order left at `accepted`, `pickup_confirmed` or `in_transit` behind a `delivered` or `disputed` delivery is first advanced to `delivered`. System actor, version-checked (the third conflict of a run gives up), one transaction-audit row per hop (`order_updated`, `dispute`, `dispute_resolved`, metadata `resolution=<verdict>`). The delivery stays `disputed` and gets the verdict. Runs when the verdict is recorded and again before the escrow is settled (sweep or admin), so a failed hop is repaired before the money moves. `buyer_wins` also settles an order that never got going: a `cancelled` or `rejected` order is refunded as it is, and an order before pickup (`funded`, or `accepted` with no delivery or a `pending` one) is cancelled by the refund settlement (its pending delivery first, then the order with `cancelReason: escrow_refunded`, listing stock restored), then refunded; if the cancel or the restore does not land, the refund waits for the next run (fail closed), which retries the restore first. The order's active delivery is the one checked for pickup. Any other order (goods moving for `buyer_wins`, before delivery for `farmer_wins`) is left as it is, and **the verdict is not settled**: release and refund answer `422`, the escrow stays `dispute_resolved`, the refusal is alarmed (`DISPUTE_VERDICT_ORDER_MISMATCH`) and audited on the escrow once (`dispute_verdict_mismatch`). The admin brings the order to a status the verdict can follow (a `farmer_wins` settles by itself once the order reaches `delivered`), or, for `buyer_wins` only, refunds with the admin-group override (`overrideOrderMismatch` and a reason), which leaves the order status alone and flags it (`disputeOverrideAt`, `disputeOverrideBy`, `disputeOverrideReason`, `disputeOverrideEscrowId`, server-only). A failed order read refuses the settlement for that run. A hop whose write failed does not block the money and is repaired later. Ratings stay blocked. Listing stock is not restored (1.12.0, ESC-03). | `DisputeVerdictMirror` (`AdminFunction.HandleResolveDisputeAsync`, `EscrowService.ReleaseAsync`/`RefundAsync`); `escrow-protocol.md` §Additions in 1.9.0 |
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

Client previews of an order total (place order, the listing detail price breakdown, the match "Estimated Total", on mobile and web) use this same formula through `mobile/lib/core/models/order_pricing.dart`. Commission is never charged on transport (1.7.2, SIM-53).

`orderTotal` is what the buyer pays: `POST /escrow/initiate` rejects any other amount (`ESCROW_028`, escrow-protocol.md 1.2.0).

### Units (1.2.0)

`order.quantity` is **kilograms** on the wire (mobile and web send `listing.quantity × 100` by default) and `pricePerUnit` is ETB per kg. Listing `quantity` and trip `capacity` are quintals; `pricePerQuintal` is ETB per quintal. Every conversion between the two divides or multiplies by 100: stock check at create, transport pricing, and the stock decrement at funding (`TryDecreaseListingQuantityAsync` subtracts `quantity / 100` quintals, SIM-07). A 500 kg order on a 60-quintal listing leaves 55.

## Order Update Rules (server-enforced, 1.3.0)

`POST /sync` with `entityType: order`, `action: update` (SIM-06):

- **Updatable**: `status` and its companions `rejectionReason`, `cancelReason`, `paymentMethod`, `cbeTransactionId` (length limits as on create). Status transitions and actor rules are unchanged (see above).
- **Echo keys** the clients send: `orderId` must equal the operation `entityId` (`orderId does not match the operation entityId.`), `updatedAt` is dropped (the repository stamps it), `version` feeds the optimistic-concurrency check. Client-sent `cancelledAt` / `respondedAt` / `paymentVerifiedAt` / `paymentSubmittedAt` (1.7.0) are dropped and re-stamped by the server, and the server-only Telebirr references (`telebirrMerchOrderId`, `telebirrPaymentOrderId`, `telebirrTransId`) are dropped.
- **Immutable after create**: `listingId`, `buyerId`, `farmerId`, `buyerName`, `farmerName`, `cropType`, `quantity`, `pricePerUnit`, `tripId`, `truckerId`, `truckerName`, `transportCost`. An unchanged echo is dropped; a changed value is rejected with `ORDER_VALIDATION: <field> is immutable and cannot be changed on update.` This holds in every status, including after `funded`.
- **Server-owned money** (`cropCost`, `commissionRate`, `commissionAmount`, `orderTotal`) is always dropped from the payload, so `orderTotal` can never drift from its inputs.
- **Server-only since 1.15.0**: `pickupDeadlineAt`, `cancelNote` and the admin queue fields (`adminQueue`, `adminQueuedAt`, `adminQueueReason`, `adminQueueEscrowId`) are dropped from create and update payloads. **Since 1.16.0** also `paymentHoldUntil` (ESC-45). `GET /orders` returns `pickupDeadlineAt`, `adminQueueReason` and `adminQueuedAt`.
- **`cancelReason`** is the buyer's free text on every cancel except the transport-failure cancel, where the server writes the code `transport_failed_buyer_cancelled` and keeps the buyer's text in `cancelNote` (1.15.0). The sweeps write the code `escrow_refunded`. Server codes are reserved: a client `cancelReason` that spells one (`transport_failed_buyer_cancelled`, `escrow_refunded`, or a spec 4.6 code such as `buyer_withdrew`; case and surrounding spaces ignored) is stored as `cancelNote` instead, on create and on every update.
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
| buyer's risk tier CRITICAL, or the score read truncated; checked before KYC (1.9.0, planned FRD-04) | `FRAUD_ACCOUNT_HELD: Your account is under review. Some actions are paused. Contact support.` |
| buyer's risk tier MEDIUM and the password not fresh, at any amount; above 10,000 ETB after the Fayda row above (1.9.0, planned FRD-04) | `AUTH_040: ...` |
| buyer's risk tier HIGH: Fayda login not fresh, then password not fresh, at any amount (1.9.0, planned FRD-04) | `AUTH_041: ...` first, then `AUTH_040: ...` on the retry |
| live gateway (`TELEBIRR_MOCK` off) and `orderTotal` outside 100 to 500,000 ETB, what one Telebirr payment can take (1.7.0; bank transfers are ESC-46) | `ORDER_VALIDATION: This order total (X ETB) is outside what one Telebirr payment can take (100 to 500000 ETB).` |
| live gateway (`TELEBIRR_MOCK` off) and the update's `paymentMethod` present and not `telebirr` (1.19.0, FUP-20; checked last, after the total; bank transfers are ESC-46) | `ORDER_PAYMENT_METHOD_UNAVAILABLE: Bank transfer (cbe_transfer) is not available yet. Pay with Telebirr.`; for any other value `ORDER_PAYMENT_METHOD_UNAVAILABLE: Payment method '<value>' is not available yet. Pay with Telebirr.` |

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

The payment method gate (1.19.0, FUP-20) reads only the update's own `paymentMethod`, never
the stored one (the mobile order create writes `cbe_transfer` as a default on every order), so
an update that carries no method is left to the checkout. It is decided before any write (no
`paymentSubmittedAt`, no transaction-audit row, no idempotency key) and is permanent under the
`ORDER_*` rule (`sync-protocol.md` 1.16.0): the client reverts the local order and shows
`paymentMethodUnavailable`. Mock mode funds every method as before.

### Payment reference (1.7.1)

The payment screen shows the full `orderId`, exactly as stored, as the transfer reference
(CBE and Telebirr), and Copy copies it. It is unique per order and it is the id every
server path keys the order payment on (`escrow-order-{orderId}`, `ORDER-FUND-{orderId}`,
the order checkout mapping). In demo mode a blank transaction id is sent as
`cbeTransactionId` `MOCK-CBE-{orderId}` / `MOCK-TBR-{orderId}` (under the 100-character
limit); an entered one is sent trimmed. With the live gateway the payment is matched only
by the server-generated `merch_order_id` of `POST /escrow/order-checkout`
(`escrow-protocol.md` 1.5.0), never by a reference the buyer types. No wire change.

### Live checkout in the app (1.13.0, ESC-44)

Live builds (`--dart-define=TELEBIRR_LIVE=true`, and every `prod`/`production` build) show
the Telebirr payment only: no demo notice, no CBE option (bank transfers are ESC-46), no
reference fields, and a total outside 100 to 500,000 ETB cannot be submitted. The buyer's
`payment_submitted` carries `paymentMethod: telebirr` and no `cbeTransactionId`. Demo builds
are unchanged. What happens next never depends on the build: the app reads the order from
the server, a `funded` (or later) order is paid, and a `payment_submitted` order is paid
through `POST /escrow/order-checkout` (`escrow-protocol.md` §Additions in 1.5.0, client
notes). Order detail offers the buyer "Continue payment" on a `payment_submitted` order,
and "Pay Now" / "Pay again" on `pending_payment` / `payment_failed`. A retry after
`payment_failed` is one `payment_submitted` change. The live
`ORDER_VALIDATION: This order total ... is outside what one Telebirr payment can take`
refusal has its own message in en, am and om (web inline, mobile through the ESC-12
rejection snackbar).

Since 1.19.0 (FUP-20) the server refuses a live `payment_submitted` whose `paymentMethod` is
not `telebirr` (`ORDER_PAYMENT_METHOD_UNAVAILABLE`, see Payment gates), so a demo build
pointed at a live server cannot record a bank transfer that nothing would fund. Its message
(`paymentMethodUnavailable`, en, am and om) says to pay with Telebirr; the local order is
reverted like any `ORDER_*` refusal.

Once a delivery exists for an accepted order (a trucker's, or the buyer's self-pickup), the
order detail screen offers every party "Open delivery" instead of a second create, and the
farmer of a pending delivery "Show QR Code" (the code the trucker or the self-pickup buyer
scans). A create the server refuses with `DELIVERY_VALIDATION: The order already has an
active delivery.` opens the existing delivery.

---

## Fraud hold and risk tiers (1.9.0, planned FRD-03, FRD-04)

Design: `specs/architecture/fraud-detection-architecture.md` 3.0.0, sections 14 to 18; escrow side
in `escrow-protocol.md` 1.7.0. Nothing here is deployed yet.

- **No new order status.** `OrderTransitions` and the actor rules are unchanged; the only new rule
  is the accept refusal below. The hold lives on the escrow (`status: held`), so invariant 3 does
  not apply.
- **What the parties see.** Order detail and the order list show "Under review" for the buyer, the
  farmer and the trucker when the order's escrow is `held`.
- **When it happens.** Only when the order's escrow is due to settle (auto-release after the dispute
  window, the 7-day refund sweep, or a dispute verdict) and a party is suspended or at risk tier
  CRITICAL. At that point no logistics step is left: the delivery is confirmed (the order is
  `delivered`, or behind it when the best-effort advance lagged), or the order is `disputed`,
  terminal or a stale `funded` order. The refund sweep checks risk before it cancels a stale
  `funded` order or restores its listing stock, so the order keeps its status until the admin
  releases or refunds. That hold is written together with a check that the order's `version` and
  status are unchanged, the same fence cancel-then-refund uses today, so a farmer's accept that
  lands in between cancels the hold and the next sweep pushes the expiry.
- **Accept while held**: the farmer's `accepted` on a `funded` order whose escrow is `held` is
  refused with `ORDER_VALIDATION: This order is under review and cannot be accepted. Contact
  support.` (planned FRD-03), because a delivery created after it could never link to the held
  escrow. Reject and cancel stay allowed.
- **Admin refund, order side.** For an escrow held before a refund (`heldFor: refund`) the order is
  cancelled (`cancelReason: escrow_refunded`) and restocked as the sweep would have done, and a
  progressing order is not refunded. For an escrow held before a payout (`heldFor: release`) the
  progressing rule does not apply. Before the refund the order goes `delivered` to `disputed` to
  `dispute_resolved` (or `disputed` to `dispute_resolved`), system actor, version-checked, one
  transaction-audit row per hop, with the escrow's `disputeResolution: buyer_wins`; the listing is
  not restocked because the goods were delivered. An order left at `accepted`, `pickup_confirmed`
  or `in_transit` behind its delivery is first advanced to `delivered` when its active delivery is
  `delivered`; otherwise, and for any other status, the refund is refused (`422
  ESCROW_INVALID_TRANSITION`, alarm `RISK_HOLD_ORDER_MISMATCH`). Admin release keeps today's order
  behaviour.
- **Order create by a CRITICAL buyer** is refused with `FRAUD_ACCOUNT_HELD` (permanent, nothing
  written). Crop create (farmer) and trip create (trucker) get the same code; see
  `sync-protocol.md` 1.9.0.
- **Gate order on `payment_submitted`**: transition auth, risk read, CRITICAL check, KYC
  (unchanged), tiered step-up (`AUTH_041` before `AUTH_040` at HIGH), the live-gateway order total
  check (`ORDER_VALIDATION`, unchanged), then the write and the mock auto-fund. LOW keeps today's
  behaviour exactly. The tier is never returned to the client.
- **Client action on `FRAUD_ACCOUNT_HELD`**: revert the local order status as for
  `ORDER_KYC_REQUIRED`, show the message, do not retry. Since 1.11.0 the mobile `SyncExecutor`
  reverts every permanently rejected order operation (ESC-12), this code included; FRD-06 adds
  its message.

## Refused order changes (1.11.0, ESC-12 part A)

Every `ORDER_*` error the server returns for an order operation, except
`ORDER_KYC_REQUIRED`, is permanent: the same command gets the same answer. That
includes the transition refusals above (`ORDER_VALIDATION`, `ORDER_UNAUTHORIZED`), the
held-escrow refusal of an accept, the live payment range check and the live payment method
refusal (`ORDER_PAYMENT_METHOD_UNAVAILABLE`, 1.19.0). The mobile client
stops retrying, puts the order back to the server's view (a refused create removes the
local order) and tells the user, in their language, that the change was not allowed;
web shows the same message from the refused call. A transient order failure must not
use the `ORDER_` prefix. Details: `sync-protocol.md` 1.10.0. The KYC path above is
unchanged.

The mobile client also refuses locally, before anything is written or queued: a missing
order, a farmer response from anyone but the order's farmer or on an order that is not
`funded`, and a cancel from anyone but the buyer or farmer. Repeating what the order
already shows (accept on `accepted`, pay on `payment_submitted`) is a no-op.

## Transport failure cancel (1.15.0, ESC-06)

A stopgap before the `awaiting_transport` flow (ESC-19): an `accepted` order that nobody picked up
in time counts as a transport failure, and its buyer may cancel it for a full refund (crop cost,
transport and commission).

**The deadline** (`pickupDeadlineAt`, stamped when the farmer accepts, server clock):

| Order | `pickupDeadlineAt` |
|---|---|
| Trip order (`tripId`) | 48 hours after the later of the trip's `departureDate` and the acceptance |
| Self-pickup order (no `tripId`), or a trip that is missing or soft-deleted | 7 days after the acceptance |

The stamp never moves: a trucker who postpones the trip after the acceptance does not push the
buyer's exit back. An order accepted before 1.15.0 has no stamp; the server computes the same rule
at cancel time from the live trip and `respondedAt` (else `paymentVerifiedAt`, else `createdAt`).
Clients show no Cancel for such an order (the 21-day hold cap still ends it).

**The cancel** (`POST /sync`, order `update` `{status: cancelled, version}`, buyer only; the farmer
and the trucker still get `ORDER_UNAUTHORIZED`). Decided before any write, in this order:
1. The client's version against a strongly consistent read: a stale command gets `conflict` with
   the server's row and changes nothing.
2. The server clock is at or past the deadline, else `ORDER_CANCEL_NOT_ALLOWED: The crops can still
   be collected until <ISO time>.`
3. The order's deliveries (strongly consistent) are absent, `pending` or `cancelled`, else
   `ORDER_CANCEL_NOT_ALLOWED: The crops were already picked up.` (the order can lag its delivery).
   That is the active delivery (the slot holder), or for an order whose delivery predates the slot
   claim, every delivery of its listing that names the order (a lookup that cannot read them all is
   a transient error).
4. Every escrow of the order that still holds money is plain `escrowed` (no settlement lock, no open
   manual payout, no confirmed delivery), and the order's own escrow has not settled, else
   `ORDER_CANCEL_NOT_ALLOWED: The payment is being settled or reviewed.` When the held money has no
   escrow record (the funding-time create failed), it is created first, as delivery linkage does.

A read or write that fails, or an escrow record that cannot be created, answers an unprefixed error
("... Please try again later.") and writes nothing: mobile retries it. Then, in the dispute verdict's
order so a pickup racing in leaves the order alone:
1. each pending delivery is cancelled (condition `status = pending`); if that fails and a re-read
   shows it picked up, the cancel is refused as in step 3 and the order stays `accepted`;
2. the order is cancelled (version-checked; a conflict re-reads and retries while the order is still
   `accepted`, at most 3 writes, else a transient error; an order that moved on answers `conflict`;
   a delivery that took the slot meanwhile and was picked up refuses the cancel as in step 3);
3. the delivery slot is released, and a pending delivery created between steps 1 and 2 is cancelled
   too; any other holder (picked up in that window, or unreadable) is logged with the alarmed
   `ORDER_TRANSPORT_CANCEL_LATE_DELIVERY orderId=.. deliveryId=.. status=..` (sync log group,
   `order-escrow-problems` alarm);
4. the listing stock is restored (best-effort here; the refund sweep makes sure of it before paying);
5. the escrow refund is scheduled for the hourly sweep (`refundReason: order_transport_failed`).

Transaction-audit rows `delivery_cancelled` (metadata `reason=transport_failed_buyer_cancelled`) and
`order_cancelled` (metadata `cancelReason=transport_failed_buyer_cancelled`), actor the buyer.

`ORDER_CANCEL_NOT_ALLOWED` is permanent like every `ORDER_*` refusal (sync-protocol.md 1.13.0), but
it depends on the server clock: the client undoes its local cancel and never resends that operation;
the buyer may cancel again once it is allowed.

**Clients (mobile and web, order detail):** an `accepted` order with a `pickupDeadlineAt` shows a
"Collect by" row to all three parties. Once the device clock is past it, the buyer gets a notice
and Cancel (the dialog says everything paid comes back); the farmer and the trucker get a notice
that the buyer can now cancel, and no Cancel. Mobile refuses the cancel locally before the
deadline. Strings in en, am and om.

**Known limits:** the reverse offline race: a trucker who picks up offline while the buyer's cancel
syncs first finds the delivery cancelled and the pickup refused, so the goods are with the trucker
on a refunded order. The refused pickup logs the alarmed `ORDER_TRANSPORT_CANCEL_PICKUP_REFUSED
orderId=.. deliveryId=.. status=..` (sync log group, `order-escrow-problems` alarm) so an admin calls
the parties; nothing reverts the refund by itself. Only the party who could have made that step
counts (the delivery's trucker for `pickedUp` and `inTransit`, its buyer for `delivered`), so no
other caller can page the alarm.
A self-pickup buyer who does not come also gets a full refund (charging is ESC-40). ESC-19 replaces
this cancel with `awaiting_transport` and the buyer's three options; ESC-15 then refuses a buyer
cancel of `accepted` with the same code.

---

## The order follows the money (1.17.0, ESC-09)

Before 1.17.0 nothing wrote `disbursing` or `settled`: every order the escrow paid out stayed
`delivered` forever (the Escrow tile counted it as held, the admin stage stayed Delivered).

**Target.** The escrow's state decides where its order goes:

| Escrow | Order |
|---|---|
| any escrow with a dispute verdict (`disputeResolution`) | owned by the verdict: `dispute_resolved` (repaired after a settled verdict whose order hop failed), never `disbursing` or `settled` |
| `completed`, no `truckerPayoutPending`, no open manual payout | `settled` |
| `completed` still owing a leg (a legacy row before the ESC-04 park), `released` (with or without a trucker leg owed or queued), `escrowed` with a release in the manual payout queue | `disbursing` |
| `refunded` | not moved; the order must already be `cancelled`, `rejected` or `dispute_resolved` (or flagged by an override) |
| anything else (`escrowed`, `disputed`, a refund still queued, `initiated`, `cancelled`, `held`) | not moved |

**Moves.** Forward only, one hop at a time: `delivered → disbursing`, `disbursing → settled` (the two
system edges of `OrderTransitions`), and an order left at `accepted`, `pickup_confirmed` or
`in_transit` behind a confirmed delivery is first advanced to `delivered` (the system catch-up of
1.4.0 and 1.12.0). Each hop is one write conditioned on the order's status (not its version) that
adds 1 to `version` and stamps `updatedAt`, with one transaction-audit row `order_updated`
(system actor, metadata `escrowId`, `escrowStatus`, `source`). A hop lost to another writer re-reads
and decides again. `settled` is never written while the escrow is still `escrowed`.

**When.** Right after every escrow money write: the release (its queued, pending-trucker and completed
answers), the hourly trucker retry that pays the last leg, a manual payout leg or close (also a
replay), the stale release-lock park, the legacy trucker park and the crash recovery. Best-effort: it
never changes the settlement's answer; a failure logs `ORDER_MONEY_FOLLOW_DEFERRED`.

**Self-heal and backfill.** The hourly escrow-cron run ends with the order-money sweep: it pages the
orders `StatusCreatedAtIndex` partitions `delivered`, `disbursing`, `disputed`, `accepted`,
`pickup_confirmed`, `in_transit` (oldest first, within a 30 second slice of the run and 2,000
orders; a run that stops early saves a cursor and the next one goes on from there), finds each order's escrow (`escrow-order-{orderId}`, else the order or delivery index) and
applies the same rules. Its first run moves every order left `delivered` by releases before 1.17.0.
An order past delivery with no escrow, a refund behind a closed or flagged order, or a mismatch it
alarmed past delivery is marked with the server-only
`moneySweepSkip` (its status then) and `moneySweepSkipAt`, without a `version` or `updatedAt` change;
later runs skip it until its status moves. Alarm `ORDER_MONEY_MISMATCH` (`escrow-protocol.md`
§Additions in 1.17.0).

**Clients.** No new status and no wire change: shipped apps already know `disbursing` and `settled`.
A device that still shows an older status and queues a status change (for example the `delivered`
catch-up) gets `ORDER_VALIDATION: Invalid status transition` from the transition check, which runs
before the version check; it is permanent and the client reverts to the server row. The buyer may rate
at `delivered`, `disbursing` and `settled` (`rating-api.md` 1.1.0). The Escrow tile and "In Escrow"
keep counting only `funded` to `delivered` and `disputed`.

## Contact between the parties (1.18.0, ESC-48)

Before 1.18.0 every buyer saw every farmer's and trucker's phone in browse and could message them
from crop and trip detail, so a buyer and a farmer or trucker could agree to trade off the platform
and skip the commission. Now the parties of an order reach each other only through the order, and
only while its **contact is open** (`ContactReveal`, one rule for the phones and the messages):

| Contact | Order |
|---|---|
| **open** | `accepted`, `pickup_confirmed`, `in_transit`, `delivered`, `disbursing`, `settled`, `disputed` |
| closed (not yet) | `created`, `pending_payment`, `payment_submitted`, `payment_failed`, `funded` |
| closed again (the money went back, or the verdict decided) | `cancelled`, `rejected`, `dispute_resolved`, and any order flagged by an admin override refund (`refundOverrideAt`, `disputeOverrideAt`) |

Contact opens at the farmer's accept, not at `funded`: a funded order can still be cancelled by the
buyer or rejected by the farmer for a full refund, which would let either side read the other's
number and walk away. From `accepted` that takes a transport failure (the pickup deadline, 1.15.0),
which is friction, not a guarantee: a colluding buyer and farmer can still let the deadline pass.
That cancel therefore records a fraud signal `CONTACT_THEN_TRANSPORT_CANCEL` on the buyer
(best-effort, farmer id in the description, never a phone) for the admin fraud review. A buyer can
name any open trip at order create (there is no trucker consent before ESC-17), so the trucker's
phone is revealed to the buyer and the farmer at the farmer's accept.

**Reads.** `GET /orders` and `GET /orders/{id}` return `contactOpen` (boolean) on every order, and to
a party of an open order also `buyerPhone`, `farmerPhone` and `truckerPhone` (the last only when the
order has a trucker). The numbers are read from the users table at request time, one batched read
per page (orders store none); a failed read leaves them out and never fails the order read. The
admin transaction view carries `contactOpen` without phones. Nobody else returns a counterpart's
phone: crop and trip reads only to their owner (`crop-listing-api.md` 1.3.0, `trip-listing-api.md`
1.4.0), deliveries never (`delivery-verification-api.md` 1.15.0), messages never (`messaging.md`
1.2.0).

**Messages.** Order and delivery threads take new messages only while the order is open; crop and
trip threads never (`MESSAGE_CONTACT_LOCKED`, `sync-protocol.md` 1.15.0).

**Clients.** The order detail shows a Contact section (Call, Copy, Message per counterparty) when
`contactOpen` is true, a note before (`created` to `funded`), and nothing after a cancel, reject or
refund. `contactOpen` decides; the client falls back to the open status set only when the row has
none (a server before 1.18.0, a local change not yet pulled), and a local cancel or reject closes it
and clears the phones at once. Offline, the last pulled numbers stay on the device until the next
pull: an order closed on the server while a party is offline keeps its numbers there until then.

## Changelog

- **1.19.0 (2026-10-22)**: Live payment method refusal (FUP-20): with the live gateway a `payment_submitted` whose `paymentMethod` is present and not `telebirr` is refused with `ORDER_PAYMENT_METHOD_UNAVAILABLE` (naming the method; bank transfers wait for ESC-46), decided after the other payment gates and before any write (no `paymentSubmittedAt`, no transaction-audit row, no idempotency key); only the update's own value is checked and an update without a method is left to the checkout; mock mode still funds every method; permanent under the `ORDER_*` rule, with its own text in en, am and om (`paymentMethodUnavailable`) on mobile and web.
- **1.18.0 (2026-10-17)**: Contact between the parties (ESC-48): `contactOpen` on every order read and the parties' phones to a party of an open order (`accepted` to `settled` and `disputed`, no admin override refund), resolved from the users table at read time; contact closed before the farmer's accept and again after `cancelled`, `rejected` or `dispute_resolved`; a buyer's transport-failure cancel of an order whose contact had opened records the fraud signal `CONTACT_THEN_TRANSPORT_CANCEL`; order detail gets the Contact section.

- **1.17.0 (2026-10-10)**: The order follows the money (ESC-09): `disbursing` when the release is committed (the escrow `released` or its release queued), `settled` only when the escrow is `completed` with no leg owed; a lagging logistics order is advanced to `delivered` first; status-conditioned hops with `ADD version`, one `order_updated` row each, after the escrow write; a verdict escrow keeps its order at `dispute_resolved`; the hourly order-money sweep repairs and backfills, with the server-only skip marker `moneySweepSkip`; an admin refund of a plain escrowed order escrow cancels a pre-pickup order first (refund scheduled, stock restored) and refuses goods on the move unless overridden (`refundOverride*`); an admin release pays only a delivered order; ratings allowed on `disbursing`; a client status change on a mirrored order is a permanent `ORDER_VALIDATION`.
- **1.16.0 (2026-10-10)**: Pending payments hold the order (ESC-45): the order cron does not move a `payment_submitted` order to `payment_failed` while a verified Telebirr payment for it is pending in the webhook inbox (server-only `paymentHoldUntil`, written extend-only without a version bump, and the inbox's pending partition; the payment-failed write is conditional on the hold; an unreadable inbox skips the step for that run). No client change.
- **1.15.0 (2026-10-10)**: Transport failure cancel (ESC-06): the farmer's accept stamps `pickupDeadlineAt` (trip orders 48 hours after the later of departure and acceptance, self-pickup or a gone trip 7 days after acceptance; an unreadable trip refuses the accept for now); the buyer may cancel an `accepted` order once the deadline passed and nothing was picked up, for a full refund (pending delivery first, then the order with `cancelReason: transport_failed_buyer_cancelled` and `cancelNote`, slot, stock, refund; `ORDER_CANCEL_NOT_ALLOWED` otherwise; unprefixed transient errors write nothing; a missing escrow record is created first); a delivery can no longer be created on an order that does not take one; order escrows are never extended past funding + 21 days and the order enters the admin stuck-order queue (`adminQueue*`, not versioned); `GET /orders` returns `pickupDeadlineAt`, `adminQueueReason`, `adminQueuedAt`; server `cancelReason` codes are reserved (a client text spelling one goes to `cancelNote`); a pickup refused on a delivery that cancel cancelled logs the alarmed `ORDER_TRANSPORT_CANCEL_PICKUP_REFUSED`; mobile and web show Cancel only in that case, with notices for all three roles.
- **1.14.0 (2026-10-10)**: Disputes always freeze money (ESC-07): `disputed` is system-managed (a client order update to it gets `ORDER_UNAUTHORIZED`, which clients since 1.10.0 revert); every dispute freezes the escrow before the delivery or order moves, and a refused freeze moves nothing; the order follows a dispute through the escrow service's mirror (advance a lagging order to `delivered` first, consistent reads, retries, one transaction-audit row per hop, `dispute` action). No client change for the order path: no shipped app sent it.
- **1.13.0 (2026-10-09)**: Retry and live checkout (ESC-44, takes ESC-47 item 1): `payment_failed → payment_submitted` is allowed in one change (gates run, `paymentSubmittedAt` restamped; `payment_failed → pending_payment` still accepted); the app pays live orders through the order checkout, sends `paymentMethod: telebirr` with no `cbeTransactionId` in live builds, offers Continue payment / Pay Now / Pay again to the buyer, and localizes the live total refusal.
- **1.12.0 (2026-10-09)**: Dispute verdicts move the order (ESC-03): the admin verdict moves the order to `dispute_resolved` (`delivered → disputed → dispute_resolved` or `disputed → dispute_resolved`, advancing an order left behind its `delivered` or `disputed` delivery first), system actor, version-checked, one transaction-audit row per hop; repeated before the escrow is settled to repair a failed hop; `buyer_wins` refunds a cancelled or rejected order as it is and cancels an order before pickup (stock restored) first; any other order that cannot follow is left as it is, and the verdict is not settled until the admin brings the order to a status it can follow, or overrides a `buyer_wins` (order flagged with `disputeOverride*`); alarmed, audited once. No client change: the clients already show `dispute_resolved`.
- **1.11.0 (2026-10-09)**: Refused order changes (ESC-12 part A): every `ORDER_*` operation error but `ORDER_KYC_REQUIRED` is permanent; the client reverts the local order and explains the refusal in three languages; local guards refuse instead of reporting success; a transient order failure uses no `ORDER_` prefix. No server change.

- **1.10.0 (2026-10-06)**: Order follows its delivery for pickup too (SIM-60): a client `pickup_confirmed` / `in_transit` is honoured only when the order's active delivery is already `pickedUp` or later / `inTransit` or later; an order without an active delivery is refused. The delivery update remains the normal path (it advances the order as a system actor).
- **1.9.0 (2026-10-06)**: Fraud hold and risk tiers (planned FRD-03, FRD-04): no new order status; an order whose escrow is `held` shows "Under review" to all three roles; the farmer's accept of a funded order whose escrow is held is refused (`ORDER_VALIDATION`); an admin refund of an escrow held before a payout moves the order to `dispute_resolved` (`buyer_wins`), advancing an order left behind its confirmed delivery first; the refund-sweep hold is fenced on the order version; the refund sweep checks risk before cancelling or restocking a stale-funded order; `payment_submitted` gains the CRITICAL (`FRAUD_ACCOUNT_HELD`), MEDIUM and HIGH step-up rows and its gate order is stated; order create by a CRITICAL buyer is refused.
- **1.7.2 (2026-10-06)**: Client order previews use the server formula, commission on the crop cost only (SIM-53). The place-order preview charged it on crop plus transport and showed more than the server charges (18375 instead of 18350 ETB for 17000 of crop and 500 of transport). Doc-only, no wire change.
- **1.7.1 (2026-10-06)**: The payment screen shows the full order id as the transfer reference (SIM-54); it was the first 8 characters upper-cased, which repeated across orders (`ORD_1790` for every legacy web order). Demo references are `MOCK-CBE-{orderId}` / `MOCK-TBR-{orderId}`. No wire change; live payments are still matched by the server's `merch_order_id`.
- **1.7.0 (2026-09-30)**: Live order funding (ESC-01): with the live gateway the buyer pays a `payment_submitted` order through `POST /escrow/order-checkout`, and the Telebirr webhook funds it exactly once with the same side effects as the mock path; the funding stock decrement is idempotent per order; the Telebirr references are server-only order fields; `payment_failed` is system-only. The server stamps `paymentSubmittedAt` on `payment_submitted`; the order cron moves an order that stayed `payment_submitted` for 24 hours to `payment_failed`; with the live gateway `payment_submitted` refuses an `orderTotal` outside 100 to 500,000 ETB.
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
