# Order Lifecycle Contract (Status Levels & Escrow Semantics)

## Version
2.18.0

## Parties
- **Mobile / Web** (client) — displays the status timeline, requests buyer/farmer/logistics transitions via `POST /sync`
- **Backend** (server): owns the state machine (`server/src/Functions/SyncFunction.cs`, `OrderTransitions` + `ValidateOrderTransitionAuth`), performs all money-linked transitions and, since 2.0.0, decides the farmer's and the trucker's commitment commands (`server/src/Services/OrderCommitment.cs`)
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
| 2 | `pending_payment` | Waiting for the buyer to start paying. The server stamps `pendingPaymentAt` each time the order enters it, which starts the 24h auto-cancel (1.20.0). | No money paid. | Buyer |
| 3 | `payment_submitted` | Buyer initiated the transfer (Telebirr/CBE) and submitted the reference. **Not yet verified.** The server stamps `paymentSubmittedAt`, which starts the payment clock (1.7.0). | Money claimed, unconfirmed. | Buyer |
| 4 | `funded` | Payment **confirmed held in the platform escrow account**. In dev/staging (`TELEBIRR_MOCK=true` on the sync Lambda) the server auto-verifies instantly after `payment_submitted`; with the live gateway the buyer pays through `POST /escrow/order-checkout` and the Telebirr webhook funds the order (1.7.0, `escrow-protocol.md` 1.5.0). Since 2.0.0 the order also waits here for the commitments: the farmer is asked first (`farmerResponse: pending`), and on a trip order the trucker once the farmer accepted (`truckerResponse: pending`); see Commitments. | Money safely in escrow. | **System only** |
| 5 | `accepted` | Everyone committed: the farmer accepted the funded order and prepares the crops, and on a trip order the trucker accepted the transport too (2.0.0; before, the farmer's accept alone). The farmer's accept on a trip order keeps the order `funded` until the trucker answers. | In escrow. | Farmer (self-pickup order); the trucker's accept on a trip order (2.0.0) |
| 6 | `pickup_confirmed` | Pickup from the farmer confirmed (QR scan; trucker delivery or buyer self-pickup). | In escrow. | **System** when the delivery becomes `pickedUp` (1.4.0). A participant (incl. the trucker) may request it only once the order's active delivery is `pickedUp` or later; an order without an active delivery is refused (1.10.0) |
| 7 | `in_transit` | Crops en route to the buyer. | In escrow. | **System** when the delivery becomes `inTransit`. A participant (incl. the trucker) may request it only once the active delivery is `inTransit` or later (1.10.0) |
| 8 | `delivered` | Delivery confirmed by the buyer. Starts the dispute window (see `escrow-protocol.md`: 48h; auto-release after it, or after a longer payout delay for a new or flagged farmer or trucker, 1.27.0). | In escrow, release pending. | **System** when the delivery becomes `delivered` (self-pickup jumps here from `accepted`); a client request is refused unless the order's active delivery is already `delivered` |
| 9 | `disbursing` | The release is committed: the farmer leg is paid or waits in the manual payout queue (also an unknown outcome or a parked release lock); the trucker leg may still be owed (1.17.0). With the live gateway this can last days. | Leaving escrow. | **System only**, when the escrow becomes `released` or its release is queued (1.17.0) |
| 10 | `settled` | Every leg is paid: the farmer and, when there is one, the trucker. Order complete. | Paid out. Terminal. | **System only**, when the escrow becomes `completed` with no leg owed (1.17.0) |
|   | `awaiting_transport` | The transport is not settled and the buyer must decide (2.0.0, ESC-15). Entered while the money is held: the trucker declined, let the request expire or dropped out after accepting, or the trip was cancelled or had no room. The farmer's commitment stands. Exits: the buyer cancels (full refund), the farmer cancels (no penalty), or the 48 hour buyer-choice deadline cancels it (full refund). Replacement truck and the switch to self-pickup are ESC-19/20. Tooltip key `statusAwaitingTransportInfo`. | In escrow, held for the buyer's decision. | **System only** (a client cannot set it) |
| - | `cancelled` | Order cancelled (buyer action, or auto-cancel, see below). Escrowed money, if any, refunds to the buyer. An `accepted` order only after a transport failure (1.15.0, see Transport failure cancel) or, since 2.0.0, by its farmer with a fresh sign-in. Since 2.0.0 also from `awaiting_transport` (buyer or farmer) and from `funded` when the farmer did not answer in time; since 2.4.0 from `funded` by the system when the funding found the listing's stock gone (`listing_out_of_stock`, see Stock at funding); `cancelReason`, `cancelledBy` and `faultParty` say who and why. | Refunded/none. Terminal. | Buyer / farmer (2.0.0) / system cron |
| - | `rejected` | Farmer declined the funded order (`farmerResponse: declined`, `cancelledBy: farmer`, `faultParty: farmer` since 2.0.0); escrow refunds to the buyer. | Refunded. Terminal. | Farmer |
| — | `payment_failed` | Payment could not be verified: the order cron sets it 24 hours after `paymentSubmittedAt` when no payment was confirmed (1.7.0). Buyer may retry (`→ payment_submitted` directly since 1.13.0, or `→ pending_payment`) or cancel. | No confirmed money. | **System only** (enforced since 1.7.0) |
| — | `disputed` | Dispute raised within the window; escrow keeps holding the money. Ratings are blocked while disputed. | Frozen in escrow. | **System only** (1.14.0): from an escrow dispute (`POST /escrow/dispute`, buyer or farmer) or a delivery dispute via sync (1.4.1), each only after the escrow is frozen. A client order update to `disputed` is refused (`ORDER_UNAUTHORIZED`) |
| — | `dispute_resolved` | Dispute decided; money released to farmer or refunded to buyer per outcome. The order stays here after the verdict is settled (it never moves to `disbursing` or `settled`; the escrow status says whether the money moved, 1.17.0). | Released per verdict. Terminal. | **System only**: when the admin records the verdict (`POST /admin/disputes/{id}/resolve`), and again before the escrow is settled if that failed (1.12.0), and by the hourly order-money sweep after a settled verdict whose order hop failed (1.17.0) |

Since 1.21.0 an admin's dispute disposition also sets `dispute_resolved`, straight from `funded`,
`accepted`, `pickup_confirmed` or `in_transit` (system only; see "Dispute dispositions").

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
                             funded ──► cancelled              (system: the listing's stock was gone at funding, listing_out_of_stock, 2.4.0)
                             accepted ──► cancelled   (buyer, only after a transport failure, 1.15.0; farmer with a fresh sign-in, 2.0.0)
                             funded ──► awaiting_transport     (system: trucker declined or expired, trip full or cancelled, 2.0.0)
                             accepted ──► awaiting_transport   (system: trucker dropped out, trip cancelled, 2.0.0)
                             awaiting_transport ──► cancelled  (buyer, farmer, or the 48h buyer-choice deadline, 2.0.0)
   funded | accepted | pickup_confirmed | in_transit ──► dispute_resolved   (system, an admin dispute disposition, 1.21.0)
                  pickup_confirmed | in_transit ──► disputed   (system: T8 delivery_overdue or T9 silent_buyer, 2.18.0)
                  pickup_confirmed | in_transit ──► delivered  (system: T9 confirm for a silent buyer, 2.18.0)
```

Source of truth: `OrderTransitions` in `SyncFunction.cs`; the farmer's and the trucker's
commands are decided by `OrderCommitment.Decide` before that map (2.0.0). Anything not in that
map is rejected as an invalid transition (version-checked, conflict on
mismatch — see `trip-transition` gotchas: send the **pre-increment** version).

### Actor authorization (`ValidateOrderTransitionAuth`)

- **System-managed** (client sync is rejected): `funded`, `disbursing`, `settled`, `dispute_resolved`, since 1.7.0 `payment_failed`, since 1.14.0 `disputed`, and since 2.0.0 `awaiting_transport`. Before 1.7.0 any participant (the farmer too) could set `payment_failed` while a Telebirr payment was in flight, which let the buyer resubmit and pay twice. Before 1.14.0 the buyer or the farmer could set `delivered → disputed` on the order itself: no escrow was frozen, the delivery stayed `delivered`, the 48-hour auto-release still paid the farmer, and the admin never saw the dispute (ESC-07). A dispute now comes only from the delivery (sync) or `POST /escrow/dispute`. No shipped app ever sent the order update.
- **Buyer-only**: `pending_payment`, `payment_submitted`, and `cancelled` from `created`, `pending_payment` and `payment_failed` (`cancelReason: buyer_withdrew` when none is sent, `cancelledBy: buyer`, 2.0.0) and, since 2.0.0, from `awaiting_transport` (`transport_failed_buyer_cancelled`, full refund). From `accepted`, `cancelled` is allowed only after a transport failure and runs its own checks (1.15.0, Transport failure cancel). Since 2.0.0 (flag on) a buyer's `funded → cancelled` is refused with `ORDER_CANCEL_NOT_ALLOWED: The farmer and the trucker are deciding; you will be told as soon as they answer.`: the deadlines end the wait either way.
- **Farmer-only**: `accepted`, `rejected`; since 2.0.0 sent as the commands `farmerResponse: accept | decline` (the farmer's legacy `status: accepted | rejected` is mapped to them), plus `cancelled` from `accepted` before pickup with a fresh sign-in (`AUTH_040`; `farmer_cancelled`, `faultParty: farmer`, full refund) and from `awaiting_transport` without one (`farmer_cancelled`, `faultParty: none`). See Commitments.
- **Trucker-only** (2.0.0): `truckerResponse: accept | decline` on the request made to the order's own trucker (`funded`, farmer accepted, `truckerResponse: pending`), and `decline` from `accepted` before pickup as the drop-out (fresh sign-in). A trucker never sets a status by name; the server derives it. See Commitments.
- **Either participant**: `pickup_confirmed`, `in_transit`, `delivered`. The order's matched trucker counts as a participant for these three only (1.4.0). Each is a catch-up of what the delivery already says: `delivered` requires the order's active delivery to be `delivered` (1.4.0; orders without an active delivery keep the old behaviour), `pickup_confirmed` requires it to be `pickedUp` or later and `in_transit` requires `inTransit` or later, and an order with no active delivery is refused for both (`ORDER_VALIDATION`, 1.10.0). Before 1.10.0 any participant could mark pickup or transit with no delivery at all.

---

## Side Effects & Automation

| Trigger | Effect | Where |
|---|---|---|
| `→ funded` | Crop listing quantity decreases by the ordered amount; at 0 the listing becomes `reserved` (off the market). Idempotent per order since 1.7.0 (the listing records `stockDecrementedFor` order ids; version CAS with retry), so a payment redelivery can repeat it safely. Since 2.4.0 the write requires `quantity >= q`: a funding that finds less ends the order instead (next row but one, ESC-18). Best-effort: a failure logs the alarmable `ORDER_STOCK_DECREMENT_FAILED` token, never blocks the order. | `OrderFundingService.DecrementStockAsync` → `ICropRepository.TryDecrementStockAsync` (mock auto-fund in `SyncFunction.cs`, live payments in `TelebirrWebhookFunction.cs`) |
| `→ funded` | An escrow record is materialized for the held money (`escrow-order-{orderId}`, status `escrowed`, `source: order`). Best-effort with alarmable `ORDER_ESCROW_CREATE_FAILED` token; delivery-time linkage self-heals, and a payment redelivery repairs it. | `OrderFundingService.EnsureFundedSideEffectsAsync` → `EscrowService.CreateFromFundedOrderAsync`; `escrow-protocol.md` §Order-Funded Escrow |
| `→ funded`, the listing's stock gone (2.4.0, ESC-18) | Nothing is taken from the listing (it records the order in `stockRefusedFor`). The escrow is still created (once more when its create failed), then the order moves `funded → cancelled` in one write conditioned on its status and on `farmerResponse` not being `accepted` (`awaiting_transport → cancelled` for a repair of an order parked there), with `cancelReason: listing_out_of_stock`, `cancelledBy: system`, `faultParty: none` and `cancelledAt`; audited `order_cancelled` (metadata `cancelReason=listing_out_of_stock`). The escrow refund is scheduled (`refundReason: listing_out_of_stock`, the full amount with commission, through the hourly refund sweep), no trip capacity is reserved and any booking an earlier attempt made is released. No stock is restored, and a later restore for the order gives nothing back. The buyer and the farmer get `order_cancelled` (`notifications-api.md`, reason `listing_out_of_stock`). The mock auto-fund answers the buyer's payment with the ended row. An order whose farmer already accepted (on the row read or by the time of the write), or that has moved on, is left alone and logs `ORDER_STOCK_DECREMENT_FAILED ... reason=insufficient_stock` for a person to decide; a refund that cannot be armed logs `ORDER_ESCROW_REFUND_SCHEDULE_FAILED` (`reason=no_escrow` when no escrow record exists). Since 2.13.0 (FUP-210) the end (conditioned on the version and status read) and the refund arm are one transaction (an escrow held for review is not armed, as before); money a dispute froze or a settlement owns leaves the order `funded` with `ORDER_STOCK_DECREMENT_FAILED ... status=escrow_busy` (`escrow-protocol.md` 2.13.0). | `OrderFundingService.EnsureFundedSideEffectsAsync` (mock auto-fund in `SyncFunction.cs`, live payments and redeliveries in `TelebirrWebhookFunction.cs`); see Stock at funding |
| live Telebirr payment for an order checkout | One write conditioned on `status = payment_submitted` sets `funded`, `paymentVerifiedAt` and the server-only `telebirrMerchOrderId`, `telebirrPaymentOrderId`, `telebirrTransId` (clients can never set them; create and update strip them), then runs the two side effects above and a transaction-audit `payment_verified` row (actor `telebirr`). That row is keyed by the order's `paymentVerifiedAt` and written with a conditional put, so it exists once; a redelivery of the same payment writes it when the first delivery did not (1.20.0, ESC-47; `escrow-protocol.md` 1.19.0). A second payment, a payment for an order no longer waiting, or one made in a checkout from an earlier `payment_submitted` (the checkout records the order's `paymentSubmittedAt`) changes nothing and is alarmed for a manual refund. | `TelebirrWebhookFunction.cs`; `escrow-protocol.md` §Additions in 1.5.0 |
| `funded → rejected\|cancelled` | The order's escrow is expired NOW (+`refundReason`) so the hourly auto-refund cron pays the buyer back (≤1h). The listing gets its stock back: `quantity / 100` quintals, idempotent per order (the listing records restored order ids), race-safe (version CAS), `reserved → available` (1.4.2, SIM-13). Since 2.0.0 the trip capacity reserved for the order is released as well, and the row records `farmerResponse: declined` (reject) or `cancelReason: farmer_response_expired` (cron), each with `cancelledBy` and `faultParty: farmer`. Since 2.12.0 (FUP-53) the reject or cancel through sync and the refund arm on each escrow holding the money are one transaction: money a dispute froze, or that is being settled, paid out by an admin or past a confirmed delivery, refuses it with `ORDER_CANCEL_NOT_ALLOWED: The payment is being settled or reviewed.` and nothing is written; an escrow `held` for review is not armed (its admin refunds it) and the order closes only while it is still held. | `TryCloseOrderWithRefundAsync` (`EscrowService.PlanOrderRefundAsync`, `CloseOrderWithRefundAsync`), `TryRestoreListingStockAsync` → `ICropRepository.TryRestoreStockAsync`; `escrow-protocol.md` 2.11.0 |
| `funded → accepted` | The server stamps `pickupDeadlineAt` in the same version-checked write as `respondedAt` (1.15.0). A trip whose row cannot be read refuses the accept for now with an unprefixed error (the client retries), so no accepted order goes without its deadline. Since 2.0.0 (flag on) a trip order reaches `accepted` at the trucker's accept and the stamp moves there (`truckerRespondedAt`, the trip booking confirmed in the same transaction); the farmer's accept alone keeps the order `funded` and asks the trucker (`truckerResponse: pending`, `truckerAskedAt`, `truckerDeadlineAt`). A self-pickup order still reaches `accepted` at the farmer's accept. | `ProcessOrderUpdateAsync`, `PickupDeadline`, `OrderCommitment.Decide` |
| `→ funded` (2.0.0, `COMMITMENTS_ENABLED`) | The funding write also stamps the commitment fields: `fulfillment` (`trucker` with a `tripId`, else `self_pickup`), `pickupDate` (the trip's departure, trip orders only), `farmerResponse: pending`, `farmerDeadlineAt` = min(now + 24h, departure - 12h) with a 1 hour floor, and `truckerResponse: not_asked` (trip order) or `none` (self-pickup). The trip capacity is reserved for the order (atomic on the trip's `remainingQuintals`, `trip-listing-api.md` 2.0.0). A trip that is full or cancelled at that moment sends the order straight to `awaiting_transport` (`transportFailure: trip_full` / `trip_cancelled`, `truckerResponse: failed_capacity` / `cancelled`, `buyerChoiceDeadlineAt` = now + 48h) and the farmer is not asked. | `OrderCommitment.FundingStamps`, `OrderFundingService` |
| trucker `decline` on a pending request, or `truckerDeadlineAt` passed (cron) | `funded → awaiting_transport` with `truckerResponse: declined` / `expired`, `transportFailure: trucker_declined` / `trucker_expired`, `truckerRespondedAt` (decline), `declineReason` when sent, `buyerChoiceDeadlineAt` = now + 48h, `faultParty: trucker`; the trip capacity is released. No money moves. | `OrderCommitment.Decide`, `OrderCronFunction` (`commitment_deadlines`) |
| trucker drop-out (`decline` from `accepted` before pickup, fresh sign-in), or the trip cancelled under an `accepted` order | `accepted → awaiting_transport` with `truckerResponse: cancelled`, `transportFailure: trucker_cancelled` / `trip_cancelled`, `buyerChoiceDeadlineAt`, `faultParty: trucker`; the pending delivery is cancelled and the capacity released. Not possible once the goods move (`pickup_confirmed`, `in_transit`): the drop-out gets `ORDER_CANCEL_NOT_ALLOWED`, the trip cancel `TRIP_HAS_BOOKINGS`. | `OrderCommitment.Decide`, trip cascade in `SyncFunction.cs` |
| `awaiting_transport → cancelled` | Buyer: `cancelReason: transport_failed_buyer_cancelled`, `cancelledBy: buyer` (the trucker's fault stays recorded). Farmer: `farmer_cancelled`, `cancelledBy: farmer`, `faultParty: none` (no penalty). Cron after `buyerChoiceDeadlineAt`: `transport_not_arranged`, `cancelledBy: system`, `faultParty: none`. Each schedules the escrow refund (`refundReason: order_transport_failed` for the buyer's and the farmer's cancel, `order_cancelled` for the cron's) and restores the listing stock. | `ProcessOrderUpdateAsync`, `OrderCronFunction`; `escrow-protocol.md` 1.34.0 |
| `accepted → cancelled` (farmer, 2.0.0, fresh sign-in) | Before pickup only: the pending delivery is cancelled, the order cancelled (`farmer_cancelled`, `cancelledBy: farmer`, `faultParty: farmer`), the refund scheduled (`refundReason: order_cancelled`), the trip capacity released and the listing stock restored. Without a fresh sign-in `AUTH_040`; once the goods were picked up `ORDER_CANCEL_NOT_ALLOWED`. | `ProcessOrderUpdateAsync`; Commitments |
| cron action `commitment_deadlines` (2.0.0; every 15 minutes over the `DeadlineIndex` since 2.8.0, see Deadlines) | `farmerDeadlineAt` passed on a `funded` order: `cancelled` (`farmer_response_expired`, `farmerResponse: expired`, `cancelledBy: system`, `faultParty: farmer`), refund, stock and capacity released. `truckerDeadlineAt` passed: `awaiting_transport` (`trucker_expired`). `buyerChoiceDeadlineAt` passed: `cancelled` (`transport_not_arranged`), refund. Reminder markers `farmerReminderAt` / `truckerReminderAt` / `buyerChoiceReminderAt` stamped 6 hours before a deadline, only for windows of 12 hours or more. `pickupOverdueNotifiedAt` stamped when `pickupDeadlineAt` passes on an `accepted` order. Funded or accepted orders of a cancelled trip moved to `awaiting_transport` (`trip_cancelled`) when the inline cascade missed them (since 2.8.0 checked when the order is next due). Every write is version-checked; deadlines are server time. Since 2.13.0 (FUP-210) the `farmer_response_expired` and `transport_not_arranged` cancels and the refund arm on each escrow holding the money are one transaction (an escrow held for review is not armed and the cancel lands only while it is still held); money a dispute froze or that is being settled leaves the order as it was, looked at again an hour later (`ORDER_DEADLINE_REFUND_WAITING`). | `OrderCronFunction`; `escrow-protocol.md` 2.13.0 |
| `accepted → cancelled` (buyer, after a transport failure) | The pending delivery is cancelled first, then the order (`cancelReason: transport_failed_buyer_cancelled`, the buyer's text in `cancelNote`) in one transaction with the escrow refund arm (`refundReason: order_transport_failed`, 1.23.0: a dispute that froze the money since the check refuses the cancel), then the delivery slot is released and the listing stock restored; audited (1.15.0). | `ProcessTransportFailureCancelAsync`; Transport failure cancel below |
| delivery created | The delivery (which now REQUIRES `orderId`) is linked to the order's escrow, activating confirmation-stamping / auto-release / the self-pickup QR gate. The slot claim requires the order to be `accepted`, `pickup_confirmed` or `in_transit` in the same transaction (1.15.0): a create racing a cancel is refused (`DELIVERY_VALIDATION: The order no longer takes a delivery ...`). | `TryLinkOrderEscrowAsync`; `delivery-verification-api.md` |
| `payment_submitted` + `TELEBIRR_MOCK=true` | Server auto-advances to `funded` as a system actor (audited as `payment_verified`). Never active in prod / live gateway. | `TryAutoFundMockOrderAsync` |
| `created` for 24h (from `createdAt`), or `pending_payment` for 24h (from `pendingPaymentAt`, the last time the order entered it; `createdAt` for orders from before 1.20.0) | Auto-cancelled by the hourly cron, version-checked; audited as `order_cancelled`. The 24 hours is T1, read from SSM since 2.8.0 (Deadlines, Timings). Counting `pending_payment` from its own step (1.20.0, ESC-47) means a buyer who retries a failed payment on an old order through `pending_payment` gets a full day, not a cancel at the next run. | `OrderCronFunction.CancelExpiredByStatusAsync` |
| `payment_submitted` for 24h (from `paymentSubmittedAt`; `createdAt` for orders from before 1.7.0) with no confirmed payment | Moved to `payment_failed` by the same hourly cron (the 24 hours is T2, read from SSM since 2.8.0, never below 22h10m), version-checked so a funding that lands at the same time wins; audited as `order_updated` (`payment_submitted → payment_failed`). Since 1.16.0 (ESC-45) never while a verified Telebirr payment for the order is pending in the webhook inbox: the cron skips orders whose server-only `paymentHoldUntil` is in the future and orders with a row in the inbox's `pending` partition (if that partition cannot be read, no order is failed that run), and its write is also conditional on the hold, so a hold written after the cron read the order wins. The hold is set by the webhook for every verified `Completed` notification of the order's current payment attempt (extend-only, without changing `version` or `updatedAt`) and lapses on its own once the inbox stops replaying the payment (at most about 75 minutes after the last replay; `escrow-protocol.md` §Additions in 1.16.0). The order checkout stops opening new attempts 20 hours after `paymentSubmittedAt` (`escrow-protocol.md` 1.5.0), so none is still open then. A retry goes `payment_failed → payment_submitted` in one change (1.13.0, ESC-47 item 1): the payment gates run as for any payment, `paymentSubmittedAt` is stamped again, and the `pending_payment` auto-cancel above never sees the order. The older `payment_failed → pending_payment → payment_submitted` path is still accepted; since 1.20.0 (ESC-47) an order left at `pending_payment` is cancelled 24h after that step (`pendingPaymentAt`), no longer 24h after `createdAt` (1.7.0 cancelled an order older than 24h at the next run). | `OrderCronFunction.FailStalePaymentsAsync` |
| `delivered` + 48h no dispute (later when the payout delay applies: the escrow's `releaseNotBefore`, 1.27.0, FRD-13) | Escrow auto-releases to farmer; the order follows the money (`disbursing`, then `settled`), see "The order follows the money" below (1.17.0). | escrow cron, `escrow-protocol.md` |
| release committed (escrow `released`, or the release queued for a manual payout) | `delivered → disbursing`, after the escrow write (1.17.0). | `OrderMoneyMirror`; below |
| escrow `completed` (every leg paid) | `→ settled` (through `disbursing`), after the escrow write (1.17.0). | `OrderMoneyMirror`; below |
| admin refund of a plain `escrowed` order escrow (`POST /escrow/refund`, not the system sweep) | `funded`, or `accepted` with no delivery or a `pending` one: the order is cancelled first (pending delivery first, `cancelReason: escrow_refunded`, slot freed, listing stock restored) and the refund is scheduled on the escrow, then refunded. Since 2.12.0 (FUP-53) the cancel and the refund arm are one transaction: an escrow a dispute froze or a settlement took since the read refuses both (`409 ESCROW_INVALID_TRANSITION`, the order left as it was). `cancelled` or `rejected`: refunded as is (stock given back first after an `escrow_refunded` or transport-failure cancel). Since 2.13.0 (FUP-211) a restore that fails after the admin's cancel (`503`, refund armed) is retried by the hourly sweep before it refunds (the "escrowed + 7 days" row). Goods moving or `delivered`: `422` unless `overrideOrderMismatch` with a reason (admin group), which refunds, flags the order (`refundOverride*`, server-only) and leaves its status. Since 2.17.0 (FUP-19) an admin may then cancel the delivery left `pickedUp` or `inTransit` (`delivery-verification-api.md` 2.9.0): the order keeps its status and takes no new delivery. Anything else: `422` (1.17.0, ESC-09). | `EscrowService.PrepareAdminRefundOrderAsync`; `escrow-protocol.md` §Additions in 1.17.0 |
| admin release of a plain `escrowed` order escrow | Only an order `delivered` or `disbursing` (an order left behind a delivered delivery is advanced to `delivered` first); anything else `422`, no override (1.17.0). | `EscrowService.CheckAdminReleaseOrderAsync` |
| escrowed + 7 days no delivery | Escrow refunds the buyer. For order-funded escrows the sweep is order-aware: it refunds only stale-`funded` or terminal orders (then cancels a stale-funded order, `cancelReason: escrow_refunded`, and restores its listing stock, 1.4.2; since 2.12.0 the cancel and a refund arm `refundReason: delivery_timeout` are one transaction, and an escrow a dispute froze since the read leaves the order alone); progressing orders get the expiry pushed +48h, never past funding + 21 days (1.15.0). Since 2.13.0 (FUP-211) an order found `cancelled` with `cancelReason: escrow_refunded` (an admin refund, or this sweep's own cancel whose refund failed) gets its listing stock back before the refund, fail closed, as after a transport-failure cancel. Since 2.13.1 (FUP-230) so does the sweep's own cancel, in the same run: a restore that fails refunds nothing this run (the trip booking is still released), and the next run restores first, then refunds. | `escrow-protocol.md` §Order-Funded Escrow, §Additions in 2.14.0; `EscrowCronFunction.EnsureListingStockRestoredAsync` |
| funded + 21 days, order not done | No more extension: the order enters the admin stuck-order queue (server-only `adminQueue`, `adminQueuedAt`, `adminQueueReason: hold_cap`, `adminQueueEscrowId`, written without bumping `version`), audited `admin_queue_entered`; the hourly report alarms `ADMIN_QUEUE_OVERDUE` once an entry is due for 24 hours and clears it when the escrow shows the money moved on. Nothing moves the money by itself (1.15.0). | `EscrowCronFunction`; `escrow-protocol.md` §Additions in 1.13.0 |
| escrow due to settle (auto-release, auto-refund, dispute settlement) while a party is suspended or at risk tier CRITICAL | The escrow moves to `held` instead of paying out; the order status does not change (a stale-funded order is not cancelled and its stock not restored until the admin decides). Built for a suspended party (FRD-03, 1.22.0) and a party at risk tier CRITICAL (FRD-04, 1.25.0) behind `FRAUD_HOLD_ENABLED`, shipped off: the sweep then only skips the settlement for the run. A party whose risk score cannot be read skips it too, flag on or off. | `EscrowCronFunction`; `escrow-protocol.md` §Additions in 1.22.0 and 1.27.0 |
| delivery `pickedUp` / `inTransit` / `delivered` | The linked order advances to `pickup_confirmed` / `in_transit` / `delivered` as a system actor, forward only (never out of `disputed`, `cancelled` or a later status). Self-pickup: `accepted → delivered` in one hop. Best-effort: a failure is logged, the delivery update still succeeds. This is what unblocks ratings without any manual stepping (1.4.0, SIM-11). | `TryAdvanceOrderForDeliveryAsync`, `SyncFunction.cs` |
| escrow or delivery `disputed` | Every dispute freezes the escrow **first** (1.14.0, ESC-07); only a frozen (or already `disputed`) escrow lets the delivery and the order move, and a refused freeze moves nothing. A delivery dispute that cannot freeze (manual payout queued, settlement in flight, escrow not holding the money, no escrow for a funded order) is refused with `DELIVERY_DISPUTE_REFUSED` (`sync-protocol.md` 1.12.0). Then the order moves `delivered → disputed` (blocks ratings); an order left at `accepted`, `pickup_confirmed` or `in_transit` behind a `delivered` or `disputed` delivery is first advanced to `delivered`. System actor, version-checked from a consistent read, the third conflict gives up, one transaction-audit row per hop (`order_updated`, `dispute`). An escrow dispute also marks the delivery `disputed` (transaction-audit `dispute`). A failed hop leaves the money frozen and is repaired by the verdict mirror before any settlement (1.4.1 SIM-12, 1.14.0). | `EscrowService.FreezeForDeliveryDisputeAsync` + `MirrorDisputeAsync`; `FreezeBeforeDeliveryDisputeAsync` (`SyncFunction.cs`) |
| admin dispute verdict (`farmer_wins` or `buyer_wins`) | The order moves to `dispute_resolved`: `delivered → disputed → dispute_resolved` or `disputed → dispute_resolved`; an order left at `accepted`, `pickup_confirmed` or `in_transit` behind a `delivered` or `disputed` delivery is first advanced to `delivered`. System actor, version-checked (the third conflict of a run gives up), one transaction-audit row per hop (`order_updated`, `dispute`, `dispute_resolved`, metadata `resolution=<verdict>`). The delivery stays `disputed` and gets the verdict. Runs when the verdict is recorded and again before the escrow is settled (sweep or admin), so a failed hop is repaired before the money moves. `buyer_wins` also settles an order that never got going: a `cancelled` or `rejected` order is refunded as it is, and an order before pickup (`funded`, or `accepted` with no delivery or a `pending` one) is cancelled by the refund settlement (its pending delivery first, then the order with `cancelReason: escrow_refunded`, listing stock restored), then refunded; if the cancel or the restore does not land, the refund waits for the next run (fail closed), which retries the restore first. The order's active delivery is the one checked for pickup. Any other order (goods moving for `buyer_wins`, before delivery for `farmer_wins`) is left as it is, and **the verdict is not settled**: release and refund answer `422`, the escrow stays `dispute_resolved`, the refusal is alarmed (`DISPUTE_VERDICT_ORDER_MISMATCH`) and audited on the escrow once (`dispute_verdict_mismatch`). The admin brings the order to a status the verdict can follow (a `farmer_wins` settles by itself once the order reaches `delivered`), or, for `buyer_wins` only, refunds with the admin-group override (`overrideOrderMismatch` and a reason), which leaves the order status alone and flags it (`disputeOverrideAt`, `disputeOverrideBy`, `disputeOverrideReason`, `disputeOverrideEscrowId`, server-only). A failed order read refuses the settlement for that run. A hop whose write failed does not block the money and is repaired later. Ratings stay blocked. Listing stock is not restored (1.12.0, ESC-03). | `DisputeVerdictMirror` (`AdminFunction.HandleResolveDisputeAsync`, `EscrowService.ReleaseAsync`/`RefundAsync`); `escrow-protocol.md` §Additions in 1.9.0 |
| every applied transition | Append-only audit row (who/when/prior→new). | `EmitTxAuditAsync` → `transaction-audit` table |

### Stock at funding (2.4.0, ESC-18; was the "known gap")

Listing `quantity` is the free stock: what is still for sale. A funding takes the order's
quintals out of it, so stock held by funded and later orders is never part of `quantity`.

- **The race.** Two orders funding at once can no longer take more than the listing holds
  (spec A2). The decrement is one conditional write: the version read, the order not yet in
  `stockDecrementedFor`, and `quantity >= q` (a listing still storing the old string quantity
  relies on the version alone). Amounts are exact decimals and the remainder is written
  unrounded (at least 2 decimals for reading): orders carry kilograms with 2 decimals, so
  quintals have up to 4, and a 1.00-quintal listing covers a 33.6 kg order and then a 66.4 kg
  one (the restore adds back the same exact amount). The funding that loses (or one for a listing that never had
  enough) takes nothing: the order is added to the listing's server-only `stockRefusedFor` set,
  in a write that is itself conditioned on the version read, and the order ends as in Side
  Effects (`cancelled`, `listing_out_of_stock`, full refund, no farmer penalty). An order for
  exactly the quantity left (`q == quantity`) still wins.
- **Restores.** A restore gives nothing back for an order in `stockRefusedFor` and not in
  `stockDecrementedFor` (it took nothing), whichever path asks (sync, crons, dispute verdicts).
- **Deleting a listing** with orders that still depend on it is refused (`crop-listing-api.md`
  1.7.0, `CROP_HAS_ACTIVE_ORDERS`): an order `payment_submitted`, `funded`,
  `awaiting_transport` or `accepted`. Behind `COMMITMENTS_ENABLED`.
- **Quantity edits.** Because funded stock has already left `quantity`, a farmer's edit only
  changes the free stock and cannot drop below what active orders hold: the minimum stays
  0.01 (`CROP_VALIDATION`), and a stale edit from before a funding fails the version check
  instead of putting the taken stock back.
- **Not covered:** a payment for an order whose listing was deleted while the order was still
  `created` or `pending_payment` is not refused at the payment gate; its funding takes the
  stock as before.

---

## Order Create Validation (server-enforced, 1.2.0)

`POST /sync` with `entityType: order`, `action: create` is bound to the server's own listing and trip records; the client's copy is checked, never trusted (SIM-05). Every failure is `ORDER_VALIDATION` (except `TRIP_CAPACITY_EXCEEDED`, 2.0.0, and `ORDER_TRIP_NOT_ELIGIBLE`, 2.3.0) and nothing is written. Client-sent money fields (`cropCost`, `commissionRate`, `commissionAmount`, `orderTotal`) are stripped before validation.

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
| Trip must not have left (2.3.0, L-7): its departure day in Ethiopia is not over (`trip-listing-api.md` 2.2.0). Checked after the owner, whatever `COMMITMENTS_ENABLED`; a trip with no readable `departureDate` is not refused here | `ORDER_TRIP_NOT_ELIGIBLE: This trip has already left; choose another trip.` (its own code, not `ORDER_VALIDATION`) |
| `transportCost`, if sent, == `quantity` / 100 × trip `pricePerQuintal` (within 0.01); without a trip it must be absent or 0 | `transportCost must equal quantity/100 x the trip pricePerQuintal (X ETB).` / `transportCost requires a tripId.` |

The server then overwrites `farmerName` (from the listing) and `truckerName` (from the trip). Since 2.11.0 (FUP-130) the client's value is never kept: a listing without a real name (missing, blank, the farmer's id or any UUID) stores `farmerName` as the empty string (the field stays on the row, released apps read it as a string), and a trip without one leaves `truckerName` off the row. An id is never stored as a name; clients show their own localized role label for an empty name. The server then computes:

```
cropCost         = round2(quantity × pricePerUnit)
commissionAmount = round2(cropCost × commissionRate)    (rate from SSM, default 0.05)
transportCost    = round2(quantity / 100 × pricePerQuintal)   (0 without a trip)
orderTotal       = cropCost + transportCost + commissionAmount
```

`round2` (2.7.0, SIM-30) is to the santim, 2 decimals, halves away from zero, computed on exact decimals so float noise never moves a santim (170 kg at 18.99 ETB/kg: crop 3228.30, commission 161.415 so 161.42; a double gave 161.41499999999999). `orderTotal` is the exact sum, so it equals the order escrow's `amount` and splits into `farmerPayout` + `truckerPayout` + `commission` with nothing left over (`escrow-protocol.md` Commission Calculation, 1.39.0). Orders created before 2.7.0 keep their unrounded doubles; the escrow rounds them when it is created.

Client previews of an order total (place order, the listing detail price breakdown, the match "Estimated Total", on mobile and web) use this same formula through `mobile/lib/core/models/order_pricing.dart`, rounding included (its `roundEtb` drops float noise below a millionth first). Commission is never charged on transport (1.7.2, SIM-53).

`orderTotal` is what the buyer pays: `POST /escrow/initiate` rejects any other amount (`ESCROW_028`, escrow-protocol.md 1.2.0).

### Units (1.2.0)

`order.quantity` is **kilograms** on the wire (mobile and web send `listing.quantity × 100` by default) and `pricePerUnit` is ETB per kg. Listing `quantity` and trip `capacity` are quintals; `pricePerQuintal` is ETB per quintal. Every conversion between the two divides or multiplies by 100: stock check at create, transport pricing, and the stock decrement at funding (`ICropRepository.TryDecrementStockAsync` subtracts `quantity / 100` quintals, SIM-07). A 500 kg order on a 60-quintal listing leaves 55.

## Order Update Rules (server-enforced, 1.3.0)

`POST /sync` with `entityType: order`, `action: update` (SIM-06):

- **Updatable**: `status` and its companions `rejectionReason`, `cancelReason`, `paymentMethod`, `cbeTransactionId` (length limits as on create). Status transitions and actor rules are unchanged (see above). Since 2.5.0 also `dropoffNote`, and since 2.8.0 `deliveryLatitude` with `deliveryLongitude`, by the buyer only and only before pickup (Drop-off landmark and Drop-off place, below).
- **Echo keys** the clients send: `orderId` must equal the operation `entityId` (`orderId does not match the operation entityId.`), `updatedAt` is dropped (the repository stamps it), `version` feeds the optimistic-concurrency check. Client-sent `cancelledAt` / `respondedAt` / `paymentVerifiedAt` / `paymentSubmittedAt` (1.7.0) / `pendingPaymentAt` (1.20.0) are dropped and re-stamped by the server, and the server-only Telebirr references (`telebirrMerchOrderId`, `telebirrPaymentOrderId`, `telebirrTransId`) are dropped.
- **Immutable after create**: `listingId`, `buyerId`, `farmerId`, `buyerName`, `farmerName`, `cropType`, `quantity`, `pricePerUnit`, `tripId`, `truckerId`, `truckerName`, `transportCost`. An unchanged echo is dropped; a changed value is rejected with `ORDER_VALIDATION: <field> is immutable and cannot be changed on update.` This holds in every status, including after `funded`.
- **Server-owned money** (`cropCost`, `commissionRate`, `commissionAmount`, `orderTotal`) is always dropped from the payload, so `orderTotal` can never drift from its inputs.
- **Server-only since 1.15.0**: `pickupDeadlineAt`, `cancelNote` and the admin queue fields (`adminQueue`, `adminQueuedAt`, `adminQueueReason`, `adminQueueEscrowId`) are dropped from create and update payloads. **Since 1.16.0** also `paymentHoldUntil` (ESC-45). `GET /orders` returns `pickupDeadlineAt`, `adminQueueReason` and `adminQueuedAt`.
- **Commands since 2.0.0**: `farmerResponse` and `truckerResponse` take `accept` or `decline`, and `declineReason` one of `date`, `capacity`, `price`, `route`, `crop_not_ready`, `other`. They are the only new update keys a client may send, each with `version`. The farmer's legacy `status: accepted | rejected` is mapped to the farmer command. The stored response values (`pending`, `accepted`, `declined`, ...) are server-written and never taken from a client. See Commitments.
- **Server-only since 2.0.0**: `fulfillment`, `pickupDate`, `farmerResponse` (stored value), `farmerRespondedAt`, `farmerDeadlineAt`, `truckerResponse` (stored value), `truckerAskedAt`, `truckerRespondedAt`, `truckerDeadlineAt`, `buyerChoiceDeadlineAt`, `transportFailure`, `cancelledBy`, `faultParty`, `farmerReminderAt`, `truckerReminderAt`, `buyerChoiceReminderAt` and `pickupOverdueNotifiedAt` are dropped from create and update payloads (`declineReason` is taken only with a `decline` command). `GET /orders` and `GET /orders/{id}` return all of them.
- **Server-only since 2.8.0**: `nextDeadlineAt`, `nextDeadlineKind` and `deadlineShard` (the order's next deadline, see Deadlines) are dropped from create and update payloads (a client that sends them gets the rest of its update applied). They are not part of the `GET /orders` and `GET /orders/{id}` answers, but the `serverData` of a sync create or update result echoes the stored row and may carry them; clients ignore them.
- **Server-only since 2.15.0** (ESC-34): `pickupJudgement`, `pickupJudgedAt`, `checkIns`, `pickupLatitude` and `pickupLongitude` are dropped from create and update payloads. `GET /orders`, `GET /orders/{id}` and the `/sync` order rows return `pickupJudgement`, `pickupJudgedAt` and `checkIns` (per kind `{receivedAt, capturedAt?, distanceChecked, late}`, no coordinates and no sender); the stamped `pickupLatitude` and `pickupLongitude` are never returned. See Pickup judgement.
- **Server-only since 2.2.0**: `pickupPlace` (the listing's `locationName`) and `dropoffPlace` (the buyer's registered woreda) are stamped at create from the listing and buyer profile reads the create already makes, dropped from client payloads, and returned by `GET /orders`, `GET /orders/{id}` and `/sync` rows. Either is absent when its source is empty or on an order from before 2.2.0.
- **`cancelReason`** is the buyer's free text on every cancel except the transport-failure cancel, where the server writes the code `transport_failed_buyer_cancelled` and keeps the buyer's text in `cancelNote` (1.15.0). The sweeps write the code `escrow_refunded`. Server codes are reserved: a client `cancelReason` that spells one (`transport_failed_buyer_cancelled`, `escrow_refunded`, or a spec 4.6 code such as `buyer_withdrew`; case and surrounding spaces ignored) is stored as `cancelNote` instead, on create and on every update. Since 2.0.0 the server also writes `buyer_withdrew` (a buyer's cancel of an unpaid order that sends no reason), `transport_failed_buyer_cancelled` (the buyer's cancel in `awaiting_transport`), `farmer_cancelled`, `farmer_response_expired` and `transport_not_arranged`, each with `cancelledBy`, since 2.4.0 `listing_out_of_stock` (a funding that lost the listing's stock, `cancelledBy: system`), and since 2.15.0 `farmer_no_show` and `buyer_no_show` (the pickup judgement, `cancelledBy: system`); the reserved list covers them.
- **Unknown keys** are rejected: `ORDER_VALIDATION: Unknown fields: a, b`.

## Drop-off landmark (2.5.0, LT-12)

The place list has towns, not neighbourhoods, so the buyer may name a landmark for the driver
("Merkato, near Anwar Mosque", `specs/features/live-trip.md` section 10). `dropoffNote` is an
optional order field the buyer sends on create and may change, add or clear later with an order
`update` (`{"dropoffNote": "...", "version": n}`; an empty string clears it).

| Rule | Answer (nothing written) |
|---|---|
| Text or null; trimmed; one line (no control character, C0, DEL or C1, and no U+2028 line or U+2029 paragraph separator; the clients turn each into a space, collapse runs of spaces and trim before they send, so a pasted tab or line break never meets this permanent refusal); at most **80 user-visible characters**, counted as Unicode extended grapheme clusters (an Amharic syllable such as `ሰ` is one, an emoji with its modifiers is one), as the clients' `TextField.maxLength` counts them | `ORDER_VALIDATION: dropoffNote must be text.` / `... must be one line of text.` / `... must not exceed 80 characters.` |
| No phone number: the detector of listing text (`crop-listing-api.md` rule 6b), since the farmer reads the note before contact opens (ESC-48) | `ORDER_CONTACT_IN_TEXT: Remove the phone number from dropoffNote. Contact opens after payment is held in escrow.` |
| Update only, first of all: a party who may not read the note (see the table below: a trucker not committed) sends `dropoffNote` at all, whatever the value | `ORDER_UNAUTHORIZED: Only the buyer can change the drop-off note.`, before the stored note is looked at, so the answer never tells whether a note exists or what it says |
| Update only: from a party who reads it, a value equal to the stored note (after trimming) is an unchanged echo, dropped from the payload wherever the order is | (no refusal) |
| Update only: a change from anyone but the order's buyer | the same `ORDER_UNAUTHORIZED` text (both carry the `ROLE_FORBIDDEN` fraud signal, as the other party refusals) |
| Update only: the edit window. The order is `created`, `pending_payment`, `payment_submitted`, `payment_failed`, `funded`, `awaiting_transport` or `accepted`, and its active delivery (read strongly consistent, since the order follows its delivery on a best-effort write) is not `pickedUp`, `inTransit`, `delivered` or `disputed` | `ORDER_DROPOFF_NOTE_LOCKED: The drop-off note can only be changed before pickup.`; a delivery that cannot be read refuses for now with the unprefixed, retryable `The order's delivery could not be read. Please try again later.` |

The checks run in that order (on update the refusal of a party who may not read the note comes
first), on create after the other `ORDER_VALIDATION` field rules, on update right after the
participant check and before every other order rule (so a command or a status
write cannot carry a note change past them). Both codes are permanent under the `ORDER_*` rule (`sync-protocol.md` 1.36.0). A blank note
on create is not stored. A note change is not a transition: no transaction-audit row, like the
other non-status order edits.

Who reads it (the order's visibility itself is unchanged):

| Reader | `dropoffNote` |
|---|---|
| The buyer, the farmer | always |
| The order's trucker | only once committed (`truckerResponse: accepted`; a trip order from before 2.0.0 counts from `accepted` on) on an order whose money is held (`funded`, `awaiting_transport`, `accepted`, `pickup_confirmed`, `in_transit`, `delivered`, `disbursing`, `settled`, `disputed`); so not while only asked, after a decline, an expiry or a drop-out, nor once the money went back |
| Admins | the transaction detail (`GET /admin/transactions/{orderId}`); the admin app does not show it yet |
| Anyone else | never |

`GET /orders` and `GET /orders/{id}` fill it per caller, and the order row a `/sync` result
carries back (`serverData`) leaves it out for a caller who may not read it. The live track
(`GET /deliveries/{id}/track`, `live-trip-api.md` 1.0.7) returns it as `dropoff.note` by the same
rule. The clients offer the field only on a trip order (a self-pickup order has no drop-off), show
it on the order detail to whoever received it, let the buyer add or change it there until pickup,
and show it on the trucker's stop list (trip mode) and on the buyer's live map.

## Drop-off place (2.8.0, LT-14)

The order's own drop-off point, so a truck with several buyers knows where each order goes
(`specs/features/live-trip.md` section 10; the "Delivery location" of `delivery-verification-api.md`
P.4, built here). `deliveryLatitude` and `deliveryLongitude` are optional order fields, the names of
the buyer intent's point. The buyer picks a place from the bundled place list when ordering; the
clients start from the place of the intent the order fills (its place id, else its woreda name),
else the buyer's registered place, and send that place's centroid. The buyer may change it with an
order `update` (`{"deliveryLatitude": 8.5872, "deliveryLongitude": 39.1213, "version": n}`).

| Rule | Answer (nothing written) |
|---|---|
| Both or neither; each a JSON number; latitude 3 to 15, longitude 32.5 to 48.5 (the box of a `location_ping` and of a trip's destination point). A pair of nulls is no point: not stored on create, and on update it clears the point (the trip's destination is the drop-off again) | `ORDER_VALIDATION: deliveryLatitude and deliveryLongitude must be sent together.` / `... deliveryLatitude must be a number between 3 and 15.` / `... deliveryLongitude must be a number between 32.5 and 48.5.` |
| Update only, first of all: a party who may not read the point (below: the trucker of an order whose money is not held) sends either field at all, whatever the value | `ORDER_UNAUTHORIZED: Only the buyer can change the drop-off place.`, before the stored point is looked at, so the answer never tells where it is |
| Update only: from a party who reads it, the stored point again is an unchanged echo, dropped from the payload wherever the order is | (no refusal) |
| Update only: a change from anyone but the order's buyer | the same `ORDER_UNAUTHORIZED` text (with the `ROLE_FORBIDDEN` fraud signal) |
| Update only: the edit window of the drop-off note (the order status and its active delivery, read strongly consistent) | `ORDER_DELIVERY_LOCATION_LOCKED: The drop-off place can only be changed before pickup.`; a delivery that cannot be read refuses for now with the retryable `The order's delivery could not be read. Please try again later.` |

The checks run in that order, on update right after the drop-off note's and before every other
order rule. The new code is permanent under the `ORDER_*` rule (`sync-protocol.md` 1.42.0). A
change is not a transition: no transaction-audit row.

Only a trucked order has a drop-off point: on an order without a trip (no `tripId` and no
`truckerId`, or `fulfillment: self_pickup`) the fields are ignored, dropped on create before the
field rules and on update before any rule, never refused and never stored.

Who reads it, its own rule, wider than the note's: the buyer and the farmer always; the order's trucker (`truckerId`) while the money is held (`funded`, `awaiting_transport`, `accepted`, `pickup_confirmed`, `in_transit`, `delivered`, `disbursing`, `settled`, `disputed`) and the trucker is still on the order (`truckerResponse`, read as the commitments read it so an order from before 2.0.0 counts by its status: `not_asked`, `pending` or `accepted`), so the trucker answers the transport request knowing where the goods go; a trucker who declined, let the request expire, dropped out (`cancelled`) or failed on capacity keeps the `truckerId` while the order waits in `awaiting_transport` but reads nothing more, also no new place the buyer picks; admins in the transaction detail; nobody else. An asked trucker
reads the place (a town centroid from the public place list, the same detail as `dropoffPlace`)
but not the landmark, which still waits for the commitment. `GET /orders`, `GET /orders/{id}` and
the `/sync` row fill it per caller, as numbers.

What reads it, each before the trip's destination point (`TripTrack.PlannedDropoff`):
- the live track's `dropoff` for a caller who may read it, and the truck's other stops, each from
  its own order (`live-trip-api.md` 1.1.0);
- the check-in effects: `truck_nearby` within 5 km of it, and the arrival minutes it quotes;
- the delivered GPS check of a trucker delivery, which passes within 5 km of EITHER the order's point
  or the trip's destination point (the point defaults to the registered place, so a buyer who meets
  the truck at its destination is never stranded; `delivery-verification-api.md` 2.1.0);
- the admin trail's planned stops (an order that cannot be read gives that stop the trip's point)
  and the dispute evidence's planned drop-off (the nearer of the two points to the buyer's fix).

The server has no coordinates for a registered woreda, so an order placed by an older client, or
whose place is off the list, has no point and keeps the trip's destination. `dropoffPlace` stays
the registered woreda (2.2.0); the clients name the drop-off after the place nearest to the point
(the bundled list) and fall back to `dropoffPlace`. Deploy order: a server before 2.8.0 refuses
the fields (`ORDER_VALIDATION: Unknown fields`, permanent), so the server ships before clients
that send them.

## List Scoping (GET /orders, 1.4.3)

`GET /orders` takes `buyerId`, `farmerId`, `truckerId` (each must equal the caller, else `403 ORDER_FORBIDDEN`), `status`, `listingId`, `limit` and `nextToken`. With no party filter the list is scoped to the caller by the token's role claim: farmer → `farmerId`, trucker → `truckerId`, buyer (or a token without a role claim) → `buyerId` (SIM-16). It used to assume buyer, so an unfiltered farmer or trucker read returned `[]`.

## Invariants

1. Money-state transitions (`funded`, `disbursing`, `settled`, `dispute_resolved`) are **never** accepted from a client — they come from the gateway callback, escrow cron, or admin resolution.
2. A funded order always reserves listing stock; cancelling/rejecting a funded order refunds the buyer (escrow protocol) and returns the stock to the listing (1.4.2), it does not silently drop money or goods.
3. The mobile timeline (`_StatusTimeline` in `order_detail_screen.dart`) must render every status in this table with its `*Info` tooltip; new statuses require: this doc + `OrderTransitions` + wire mapping + timeline step + 3× l10n. `awaiting_transport` (2.0.0) has its server side behind `COMMITMENTS_ENABLED`; its client pieces (`statusAwaitingTransportInfo`, wire mapping, timeline step) land in the client PRs before the flag is turned on.
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
| `orderTotal` above 10,000 ETB and the buyer's last Fayda login (`POST /user/fayda-link`) older than the step-up window; at risk tier LOW a fresh password does not count | `AUTH_041: Confirm it's you with Fayda to continue with this payment.` (amount-neutral since 1.25.0) |
| buyer's risk tier CRITICAL, or the score read truncated; checked before KYC (1.9.0; built in 1.25.0, FRD-04) | `FRAUD_ACCOUNT_HELD: Your account is under review. Some actions are paused. Contact support.` |
| the order's buyer, farmer or trucker is being or has been deleted (`deletionState` on the users row, read strongly consistent); checked after the risk tier and before the KYC rows, nothing written (1.26.0, ACC-02, `account-deletion.md` 1.2.0) | `ORDER_VALIDATION: A party to this order has deleted their account.` |
| buyer's risk tier MEDIUM and the password not fresh, at any amount; above 10,000 ETB after the Fayda row above (1.9.0; built in 1.25.0, FRD-04) | `AUTH_040: ...` |
| buyer's risk tier HIGH: Fayda login not fresh, then password not fresh, at any amount (1.9.0; built in 1.25.0, FRD-04) | `AUTH_041: ...` first, then `AUTH_040: ...` on the retry |
| live gateway (`TELEBIRR_MOCK` off) and `orderTotal` outside 100 to 500,000 ETB, what one Telebirr payment can take (1.7.0; its own code since 1.24.0, FUP-22, it was `ORDER_VALIDATION` with the same sentence; bank transfers are ESC-46) | `ORDER_PAYMENT_TOTAL_OUT_OF_RANGE: This order total (X ETB) is outside what one Telebirr payment can take (100 to 500000 ETB).` |
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

Live builds (`--dart-define=TELEBIRR_LIVE=true`, every `prod`/`production` build, and since
1.24.0 a release build with an unknown `ENVIRONMENT`, which gets the production settings) show
the Telebirr payment only: no demo notice, no CBE option (bank transfers are ESC-46), no
reference fields, and a total outside 100 to 500,000 ETB cannot be submitted. The buyer's
`payment_submitted` carries `paymentMethod: telebirr` and no `cbeTransactionId`. Demo builds
are unchanged. What happens next never depends on the build: the app reads the order from
the server, a `funded` (or later) order is paid, and a `payment_submitted` order is paid
through `POST /escrow/order-checkout` (`escrow-protocol.md` §Additions in 1.5.0, client
notes). Order detail offers the buyer "Continue payment" on a `payment_submitted` order,
and "Pay Now" / "Pay again" on `pending_payment` / `payment_failed`. A retry after
`payment_failed` is one `payment_submitted` change. The live
`ORDER_PAYMENT_TOTAL_OUT_OF_RANGE` refusal (1.24.0; servers before it send
`ORDER_VALIDATION: This order total ... is outside what one Telebirr payment can take`,
which clients still match for one release) has its own message in en, am and om (web inline,
mobile through the ESC-12 rejection snackbar).

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

## Fraud hold and risk tiers (1.9.0; the hold built in 1.22.0, FRD-03; the tiers built in 1.25.0, FRD-04)

Design: `specs/architecture/fraud-detection-architecture.md` 3.0.0, sections 14 to 18; escrow side
in `escrow-protocol.md` 1.7.0, 1.22.0 and 1.27.0. 1.22.0 builds the hold for a suspended buyer, farmer or
trucker, behind `FRAUD_HOLD_ENABLED` (shipped off), the accept refusal and the admin refund's order
side below. 1.25.0 builds the risk tiers: `FRAUD_ACCOUNT_HELD` on creates and on `payment_submitted`,
the tiered step-up, and a party at risk tier CRITICAL as a hold trigger (`risk_critical`, behind the
same flag). The user-action gates write nothing and are not behind the flag. The tier is the
caller's, read once per `/sync` batch; a score that could not be read is LOW at these gates.

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
- **Accept while held**: the farmer's `accepted` on a `funded` order whose escrow
  (`escrow-order-{orderId}`) is `held` is refused with `ORDER_VALIDATION: This order is under review
  and cannot be accepted. Contact support.` (FRD-03, 1.22.0), because a delivery created after it
  could never link to the held escrow. Reject and cancel stay allowed (the refund scheduling does
  nothing on a held escrow; the admin refund then finds a terminal order). An order escrow that cannot
  be read refuses the accept for now with an unprefixed, transient error, so the client retries. With
  `FRAUD_HOLD_ENABLED` on, the accept is written in one transaction with a check that the order escrow is
  not `held`: a sweep that holds it after the read wins, and the accept gets the same refusal.
- **Admin refund, order side** (1.22.0). For an escrow held before a refund (`heldFor: refund`) a
  still `funded` order is cancelled (`cancelReason: escrow_refunded`) and restocked as the sweep would
  have done, a `rejected` or `cancelled` order is refunded as it is (a cancelled one restocked first when
  its cancel promised it), and any other status is refused (`422 ESCROW_INVALID_TRANSITION`, alarm
  `RISK_HOLD_ORDER_MISMATCH`). For an escrow held before a payout (`heldFor: release`) the
  progressing rule does not apply. Before the refund the order goes `delivered` to `disputed` to
  `dispute_resolved` (or `disputed` to `dispute_resolved`), system actor, version-checked, one
  transaction-audit row per hop, with the escrow's `disputeResolution: buyer_wins`; the listing is
  not restocked because the goods were delivered. An order left at `accepted`, `pickup_confirmed`
  or `in_transit` behind its delivery is first advanced to `delivered` when its active delivery is
  `delivered`; otherwise, and for any other status, the refund is refused (`422
  ESCROW_INVALID_TRANSITION`, alarm `RISK_HOLD_ORDER_MISMATCH`). An order already `dispute_resolved`
  is taken as a retry only when the escrow or its delivery already carries `buyer_wins`. Admin release
  keeps today's order behaviour (1.17.0: a delivered order).
- **Order create by a CRITICAL buyer** is refused with `FRAUD_ACCOUNT_HELD` (permanent, nothing
  written), after the field rules and before the listing and trip are read. Crop create (farmer)
  and trip create (trucker) get the same code; see `sync-protocol.md` 1.9.0 and 1.25.0. Only the
  buyer's tier is read: a CRITICAL farmer or trucker does not stop the order (their escrows are held
  at settlement instead), and no counterparty's tier is revealed.
- **Gate order on `payment_submitted`**: transition auth, risk read, CRITICAL check, KYC
  (unchanged), tiered step-up (`AUTH_041` before `AUTH_040` at HIGH), the live-gateway order total
  check (`ORDER_PAYMENT_TOTAL_OUT_OF_RANGE` since 1.24.0, otherwise unchanged), then the write and the mock auto-fund. LOW keeps today's
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
refusal (`ORDER_PAYMENT_METHOD_UNAVAILABLE`, 1.19.0), and since 2.0.0 the commitment refusals
`ORDER_RESPONSE_NOT_PENDING` and `ORDER_DEADLINE_PASSED` (`sync-protocol.md` 1.29.0). The mobile client
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

A stopgap before the full `awaiting_transport` flow (ESC-19; 2.0.0 builds the status for the trucker's
decline, expiry and drop-out and for a cancelled or full trip, see Commitments): an `accepted` order that
nobody picked up in time counts as a transport failure, and its buyer may cancel it for a full refund
(crop cost, transport and commission). This cancel stays until ESC-19 adds the buyer's choices.

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
2. the order is cancelled and the escrow refund scheduled for the hourly sweep
   (`refundReason: order_transport_failed`) in one transaction (1.23.0, FUP-32; `escrow-protocol.md`
   1.25.0): the order is version-checked and must still be `accepted` (a conflict re-reads and retries
   while the order is still `accepted`, at most 3 writes, else a transient error; an order that moved
   on answers `conflict`; a delivery that took the slot meanwhile and was picked up refuses the cancel
   as in step 3), and each escrow the escrow check above found holding the money must still be plain
   `escrowed`: one that a dispute froze (or a settlement took) since then refuses the cancel with that
   check's answer, and nothing is written;
3. the delivery slot is released, and a pending delivery created between steps 1 and 2 is cancelled
   too; any other holder (picked up in that window, or unreadable) is logged with the alarmed
   `ORDER_TRANSPORT_CANCEL_LATE_DELIVERY orderId=.. deliveryId=.. status=..` (sync log group,
   `order-escrow-problems` alarm);
4. the listing stock is restored (best-effort here; the refund sweep makes sure of it before paying).

Once the refund is scheduled, no dispute can freeze that escrow (`409 ESCROW_INVALID_TRANSITION` on
`POST /escrow/dispute`, `DELIVERY_DISPUTE_REFUSED` on a delivery dispute), so a cancelled order never
sits over frozen money.

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
this cancel with `awaiting_transport` and the buyer's three options. 2.0.0 (ESC-15) already refuses a
buyer cancel of a `funded` order with the same code; `accepted` keeps this deadline cancel for now.

---

## Commitments (2.0.0, ESC-15)

Design: `specs/features/three-party-escrow-commitments-reputation.md` sections 4.2 to 4.7 and 14
(D10 to D15, 2026-10-26: sequential commitments). A paid order is now committed by its parties one
after the other: the farmer first, then, on a trip order, the trucker. Server side only in this
version, behind the env flag `COMMITMENTS_ENABLED` on the sync, order-cron and webhook Lambdas (off
everywhere until the clients ship; off, the 1.27.0 behaviour: the farmer's accept makes the order
`accepted` and no trucker is asked). Replacement truck and the switch to self-pickup are ESC-19 and
ESC-20, not in 2.0.0; `transportAttempts` is not added.

### The flow

1. **Funding** stamps who is asked and until when (side-effects table, `→ funded` 2.0.0): the farmer
   is `pending` with `farmerDeadlineAt`; the trucker is `not_asked` (trip order) or `none`
   (self-pickup); `fulfillment` and `pickupDate` are fixed; the trip capacity is reserved. A trip that
   is already full or cancelled sends the order straight to `awaiting_transport` (`trip_full` /
   `trip_cancelled`) and the farmer is not asked.
2. **The farmer answers** (`farmerResponse: accept | decline`). Accept on a self-pickup order:
   `accepted`, as before. Accept on a trip order: the status stays `funded`, `farmerResponse:
   accepted`, `farmerRespondedAt` and `respondedAt` are stamped, and the trucker is asked
   (`truckerResponse: pending`, `truckerAskedAt`, `truckerDeadlineAt` = min(now + 24h, departure -
   12h), at least one hour). Decline: `rejected` (`farmerResponse: declined`, `cancelledBy: farmer`,
   `faultParty: farmer`, `declineReason` when sent), refund scheduled, stock restored, capacity
   released.
3. **The trucker answers** (`truckerResponse: accept | decline`), only the order's trucker, only while
   the order is `funded` with `farmerResponse: accepted` and `truckerResponse: pending`. Accept:
   `accepted`, `truckerResponse: accepted`, `truckerRespondedAt`, `pickupDeadlineAt` stamped now
   (1.15.0 stamped it at the farmer's accept), the trip booking verified and confirmed in the same
   transaction. When the trip refuses that confirmation because funding never managed to reserve
   the capacity (the reservation write threw), the server reserves it then and retries the accept
   once. A trip that turns out full parks the order in `awaiting_transport` (`transportFailure:
   trip_full`, `truckerResponse: failed_capacity`); a trip that is cancelled or gone parks it with
   `trip_cancelled` and `truckerResponse: cancelled`. Both answer the trucker's command with
   `success` and the `awaiting_transport` row as `serverData`, not an error. Decline: `awaiting_transport` (`truckerResponse: declined`, `transportFailure:
   trucker_declined`, `buyerChoiceDeadlineAt` = now + 48h, `faultParty: trucker`), capacity released.
4. **Drop-out.** From `accepted` before pickup a trucker `decline` needs a fresh sign-in (`AUTH_040:
   Please sign in again to cancel a delivery you accepted.`) and moves the order to
   `awaiting_transport` (`transportFailure: trucker_cancelled`, `truckerResponse: cancelled`),
   cancelling the pending delivery. A farmer `status: cancelled` from `accepted` before pickup needs
   the same fresh sign-in (`farmer_cancelled`, `faultParty: farmer`, refund).
5. **`awaiting_transport`** waits for the buyer for 48 hours: buyer `status: cancelled` (full refund,
   `transport_failed_buyer_cancelled`, `cancelledBy: buyer`), farmer `status: cancelled` (no penalty,
   `farmer_cancelled`, `faultParty: none`), or the cron cancels it after `buyerChoiceDeadlineAt`
   (`transport_not_arranged`, `cancelledBy: system`, `faultParty: none`, refund). The 7 day escrow
   sweep refunds it as a backstop (`escrow-protocol.md` 1.34.0).

### Commands (`POST /sync`, `entityType: order`, `action: update`)

| Command (`data`) | Who | Allowed when | Refusals (permanent under the `ORDER_*` rule) |
|---|---|---|---|
| `{"farmerResponse": "accept", "version": n}` | the order's farmer | `funded`, `farmerResponse: pending`, before `farmerDeadlineAt` | after the deadline `ORDER_DEADLINE_PASSED: The time to answer this order has passed; the buyer is being refunded.` (an order funded before 2.0.0 has no stamp and is never refused for it); already answered `ORDER_RESPONSE_NOT_PENDING: This order was already answered.`; a repeated accept, also on an order already `accepted` or `awaiting_transport` by this farmer, is an idempotent `success` with no write |
| `{"farmerResponse": "decline", "declineReason"?: code, "version": n}` | the order's farmer | `funded`, `farmerResponse: pending`; taken after the deadline too, until the cron cancels (the buyer is refunded either way) | after accepting `ORDER_RESPONSE_NOT_PENDING: You already accepted this order. Cancel it instead.` |
| legacy `{"status": "accepted" \| "rejected", "version": n}` from the farmer | the order's farmer | mapped to the two commands above (`rejectionReason` kept) | as above |
| `{"truckerResponse": "accept", "version": n}` | the order's trucker | `funded`, `farmerResponse: accepted`, `truckerResponse: pending`, before `truckerDeadlineAt` | before the farmer accepted `ORDER_RESPONSE_NOT_PENDING: The farmer has not accepted this order yet. You will be asked once they do.`; after the deadline `ORDER_DEADLINE_PASSED: The time to accept this transport request has passed.`; a closed request `ORDER_RESPONSE_NOT_PENDING: This transport request is closed.`; a repeated accept is an idempotent `success`; an order with no trucker `ORDER_UNAUTHORIZED`. A reservation the funding step lost is not a refusal: the server reserves and retries once, and a full or gone trip parks the order in `awaiting_transport` with a `success` answer (flow, step 3) |
| `{"truckerResponse": "decline", "declineReason"?: code, "version": n}` | the order's trucker | as the accept; from `accepted` before pickup it is the drop-out and needs a fresh sign-in | `AUTH_040` without the fresh sign-in (the client asks for the password and retries, as on a payment; not a permanent rejection); goods already picked up `ORDER_CANCEL_NOT_ALLOWED` |

A command value other than `accept` or `decline` is `ORDER_VALIDATION: Unknown command '<x>'. Send
accept or decline.`; a `declineReason` outside `date`, `capacity`, `price`, `route`, `crop_not_ready`,
`other` is `ORDER_VALIDATION` too. Every command is version-checked (send the pre-increment version)
and audited (transaction-audit `farmer_responded` / `trucker_responded`). Only the order's own farmer
or trucker may send theirs; anyone else gets `ORDER_UNAUTHORIZED`.

### Fields (server-only: clients never send them; all returned by `GET /orders` and `GET /orders/{id}`)

| Field | Values | Written |
|---|---|---|
| `fulfillment` | `trucker`, `self_pickup` | at funding, from `tripId` |
| `pickupDate` | ISO 8601 UTC | at funding: the trip's `departureDate` (trip orders only) |
| `farmerResponse` | `pending`, `accepted`, `declined`, `expired` | funding, the farmer's command, the cron; a `funded` row from before 2.0.0 reads as `pending`, any later status as `accepted` |
| `farmerRespondedAt`, `farmerDeadlineAt` | ISO 8601 UTC | the farmer's command; funding |
| `truckerResponse` | `none` (self-pickup), `not_asked` (waiting for the farmer), `pending`, `accepted`, `declined`, `expired`, `cancelled` (drop-out, or the trip cancelled), `failed_capacity` (the trip had no room) | funding, the farmer's accept, the trucker's command, the cron, the trip cascade; a trip order from before 2.0.0 reads as `not_asked` while `funded` and as `accepted` after it (spec K2) |
| `truckerAskedAt`, `truckerRespondedAt`, `truckerDeadlineAt` | ISO 8601 UTC | the farmer's accept (asked, deadline); the trucker's command |
| `buyerChoiceDeadlineAt` | ISO 8601 UTC | on entering `awaiting_transport` (now + 48h) |
| `transportFailure` | `trucker_declined`, `trucker_expired`, `trucker_cancelled`, `trip_cancelled`, `trip_full`; `trucker_no_show` since 2.15.0 (the pickup judgement) | on entering `awaiting_transport` |
| `declineReason` | `date`, `capacity`, `price`, `route`, `crop_not_ready`, `other` | the farmer's or the trucker's decline, when sent |
| `cancelledBy` | `buyer`, `farmer`, `trucker`, `system` | every cancel and reject |
| `faultParty` | `farmer`, `trucker`, `buyer`, `none` | reject, transport failure, cancel; feeds reputation (ESC-26 and later) |
| `farmerReminderAt`, `truckerReminderAt`, `buyerChoiceReminderAt` | ISO 8601 UTC | the cron, 6 hours before the deadline (windows of 12 hours or more) |
| `pickupOverdueNotifiedAt` | ISO 8601 UTC | the cron, when `pickupDeadlineAt` passed on an `accepted` order |
| `pickupJudgement` (2.15.0) | `pending`, then `scanned`, `trucker_no_show`, `farmer_no_show`, `buyer_no_show`, `both_present_no_scan`, `unchecked_check_in` | the accept with `pickupClassification` on (`pending`); the cron's judgement (the outcome). A `farmerResponse` or `truckerResponse` of `no_show` names the absent party |
| `pickupJudgedAt` (2.15.0) | ISO 8601 UTC | the cron's judgement |
| `checkIns` (2.15.0) | map per kind (`arrived_pickup`, `crops_ready`) | `handoff_check_in` (`sync-protocol.md` 1.55.0); the accept stamps it empty |
| `pickupReopen` (2.16.0, stored only: never in `GET /orders` or a `/sync` result) | map: `at`, `exit` (`pending`, `handoff_confirmed`), `fromJudgement`, `fromVersion`, `deadlineAt` (with `pending`) | an admin's re-open of the judgement (Pickup judgement, "Admin re-open"); no admin id, no reason |

### Deadlines (server time only)

| # | Waiting for | Deadline | When it passes (cron `commitment_deadlines`, every 15 minutes since 2.8.0) |
|---|---|---|---|
| T3 | the farmer's answer (`funded`) | `farmerDeadlineAt` = min(funded + 24h, departure - 12h), at least funded + 1h; self-pickup: funded + 24h | `cancelled` (`farmer_response_expired`, `farmerResponse: expired`, `cancelledBy: system`, `faultParty: farmer`), refund, stock and capacity released |
| T4 | the trucker's answer (`funded`, farmer accepted) | `truckerDeadlineAt` = min(asked + 24h, departure - 12h), at least asked + 1h | `awaiting_transport` (`truckerResponse: expired`, `transportFailure: trucker_expired`, `faultParty: trucker`), capacity released |
| T5 | the buyer's decision (`awaiting_transport`) | `buyerChoiceDeadlineAt` = entered + 48h | `cancelled` (`transport_not_arranged`, `cancelledBy: system`, `faultParty: none`), refund, stock restored |
| reminders | T3, T4, T5 | 6 hours before the deadline, only when the window is 12 hours or more | marker stamped (`farmerReminderAt`, `truckerReminderAt`, `buyerChoiceReminderAt`); the push itself is ESC-21 |
| pickup overdue | pickup (`accepted`) | `pickupDeadlineAt` (1.15.0; on a trip order now stamped at the trucker's accept) | `pickupOverdueNotifiedAt` stamped once; the buyer's transport-failure cancel stays the exit until ESC-19 |

The cron also moves funded or accepted orders of a cancelled trip to `awaiting_transport`
(`trip_cancelled`) when the inline cascade missed them; since 2.8.0 it checks that when the order is
next due (its reminder, its deadline or its pickup deadline), no longer every hour. Every cron write
is version-checked, so a party's answer that lands first wins. Client clocks play no part: the
deadlines are the server's and the clients only display them.

#### The deadline index (2.8.0, ESC-16)

Every order write keeps the order's next deadline on the row, derived from its status and its own
stamps (`OrderDeadlines.Next`): `nextDeadlineAt` (ISO 8601 UTC), `nextDeadlineKind` and
`deadlineShard` (`0`, the partition the sweep reads; `watch` for `pickup_waiting` since 2.14.0), all
three set together, or all three removed when nothing in the table below is pending. The kinds:

| `nextDeadlineKind` | Status | Due at |
|---|---|---|
| `farmer_reminder` | `funded`, farmer pending, no `farmerReminderAt`, window of 12 hours or more | `farmerDeadlineAt` - 6h |
| `farmer_response` | `funded`, farmer pending, `farmerDeadlineAt` stamped (a funded order from before 2.0.0 has none and is left to the escrow's 7-day refund, as before) | `farmerDeadlineAt` |
| `trucker_reminder` | `funded`, trucker pending, no `truckerReminderAt`, window of 12 hours or more | `truckerDeadlineAt` - 6h |
| `trucker_response` | `funded`, trucker pending | `truckerDeadlineAt` (or `truckerAskedAt` + 24h without it) |
| `buyer_choice_reminder` | `awaiting_transport`, no `buyerChoiceReminderAt` | `buyerChoiceDeadlineAt` - 6h |
| `buyer_choice` | `awaiting_transport` | `buyerChoiceDeadlineAt` (or `updatedAt` + 48h without it) |
| `pickup_overdue` | `accepted`, no `pickupOverdueNotifiedAt`, no `pickupJudgement: pending` | `pickupDeadlineAt` |
| `pickup_judgement` (2.15.0) | `accepted`, `pickupJudgement: pending` (swept, whether or not a notice was stamped) | `pickupDeadlineAt` + 1 minute |
| `pickup_waiting` (2.14.0, FUP-170; `deadlineShard = watch`) | `accepted`, `pickupOverdueNotifiedAt` stamped, no `pickupJudgement: pending` (since 2.15.0 also an order still `accepted` after its pickup judgement, which stamps the notice) | `pickupDeadlineAt` |
| `delivery_overdue` (2.18.0, T8) | `pickup_confirmed` or `in_transit`, `dropoffJudgement: pending`, no counted drop-off check-in | `dropoffDeadlineAt` + 1 minute |
| `silent_buyer` (2.18.0, T9) | `pickup_confirmed` or `in_transit`, `dropoffJudgement: pending`, a drop-off check-in received by `dropoffDeadlineAt` | its `receivedAt` + 48h (SSM `silent-buyer-hours`) + 1 minute |

`pickup_waiting` is not a deadline anything acts on: the order waits for its pickup or its cancel
after the overdue notice went out, and the entry lets the admin's Stuck Orders list it with a Query
(`escrow-protocol.md` 2.17.0, `GET /admin/orders/queue?source=deadline`). It sits in its own
partition, `watch`, which the sweep never reads, so the waits never hold up the sweep's oldest-first
read and the sweep never refunds, cancels or notifies on them. It goes with the order's next write
that leaves `accepted` (the pickup, a cancel, the drop to `awaiting_transport`), like any other kind.

The earliest pending one is stored. The write that sets it is the write that changes the order (the
versioned update, the linked and fenced updates, the funding, the status advance, the marker stamp),
in the same request. When that write returns a row whose stored deadline is not the one its state
gives (a stale read before the write), one more write conditioned on the version just written
corrects it; a failure there never fails the order write (`ORDER_DEADLINE_STAMP_FAILED`, the repair
below fixes the row).

**The sweep.** `commitment_deadlines` runs every 15 minutes. It reads the sparse `DeadlineIndex` on
the orders table (partition `deadlineShard = 0`, sort `nextDeadlineAt < now`, oldest first, keys only;
a Query, never a Scan), at most 10 pages of 100 within 3 minutes of the run, and reads each order
again (strongly consistent) before it decides it with the rules of the table above. A decision that
writes moves the deadline with its write; a lost race leaves it to the write that won; a row with
nothing to do gets its correct deadline back; a row that must wait (an unreadable delivery, goods on
the move under a cancelled trip) comes back an hour later; a row still due by its own state that
nothing could act on is logged `ORDER_DEADLINE_STUCK` and looked at again an hour later; a row whose
only pending kind is `pickup_waiting` (an index copy that lagged behind the notice) is moved to the
`watch` partition, never retried or logged stuck (2.14.0); a row whose
decision throws is logged `ORDER_DEADLINE_ROW_FAILED` and the run goes on. The summary adds `due`,
`restamped`, `retried`, `rowErrors` and `stopReason` (`time`, `lambda`, `pages`; outcome `partial`).

**The repair.** The daily action `deadline_repair` walks the `funded`, `awaiting_transport` and
`accepted` partitions of `StatusCreatedAtIndex` (the walk the sweep replaced) and stamps the next
deadline on every order whose stored one is missing or wrong, conditioned on the version read. It
decides nothing; the next sweep acts on what it stamped. It backfills the orders already in flight
when the index was added (and, since 2.14.0, gives the `accepted` orders notified before it their
`pickup_waiting` entry). Since 2.14.0 it then reads the whole `watch` partition of `DeadlineIndex` (a
Query) and each order again, and corrects or removes an entry its order no longer gives (an order
that left `accepted` while its write could not keep the deadline in step), conditioned on the version
read; those rows count in `read` and `stamped`. `commitment_deadlines` also runs a repair pass itself (1 minute share, after
the sweep and the departed-trip close) when its container has not finished one in 24 hours, so the
first run of a new deploy stamps the orders in flight without anyone invoking `deadline_repair`; a
partial or failed pass (`ORDER_DEADLINE_REPAIR_FAILED`) is not counted and the next run goes on. The
summary adds `repairRead` and `repairStamped`. Both actions are gated on `COMMITMENTS_ENABLED`.

**Timings.** The windows are SSM String parameters (hours) under `/{prefix}/orders/`, read once per
container by every Lambda that writes orders (`ORDER_TIMINGS_PARAM_PATH`); a missing or invalid value
(outside 1 to 720) keeps its default and logs `ORDER_TIMINGS_DEFAULTED`. The read is bounded (2 seconds,
one retry); an SSM failure keeps every default:

| Parameter | Default | Used for |
|---|---|---|
| `unpaid-expiry-hours` | 24 | T1 |
| `payment-verification-hours` | 24 | T2. Never below 22h10m: the order checkout opens new attempts until 20 hours after `paymentSubmittedAt` and the last one stays payable 2 hours and 10 minutes more (`escrow-protocol.md` 1.5.0), so a lower value is raised to it (`ORDER_TIMINGS_CLAMPED`). Spec 4.5 says 6 hours; that needs the checkout window shortened first. |
| `response-window-hours` | 24 | T3, T4, and the order-create lead: a trip departing within it is refused (`This trip leaves within 24 hours; choose a later trip.`, the number follows the parameter) |
| `departure-margin-hours` | 12 | T3 and T4 end this long before departure |
| `buyer-choice-window-hours` | 48 | T5 |
| `reminder-lead-hours` | 6 | the reminders |
| `pickup-grace-hours` | 48 | T7, trip order: after the later of departure and acceptance |
| `self-pickup-window-hours` | 168 | T7, self-pickup: after acceptance |
| `delivery-overdue-hours` (2.18.0) | 72 | T8: after the delivery's `pickedUpAt` (stamped on the order at pickup). Never below 24 (`ORDER_TIMINGS_CLAMPED`): T8 restricts a trucker |
| `silent-buyer-hours` (2.18.0) | 48 | T9: after the drop-off check-in's receipt. Never below 24 (`ORDER_TIMINGS_CLAMPED`): T9 confirms a delivery for a buyer who said nothing |

**The escrow sweep (2.8.0).** With `COMMITMENTS_ENABLED` on, the escrow auto-refund sweep no longer
extends a live order escrow 48 hours at a time: it holds it to the 21-day cap (T12) in one write, and
at the cap the order enters the admin queue as before (`escrow-protocol.md` 2.1.0). A delivered order
whose escrow missed its delivery stamp at confirmation is stamped by the escrow cron's hourly
order-money sweep, alarmed `ESCROW_DELIVERY_STAMP_FAILED`, so the auto-release pays it after the
dispute window instead of waiting for the cap.

### Buyer cancel rules (2.0.0)

| Order status | Buyer `status: cancelled` | Result |
|---|---|---|
| `created`, `pending_payment`, `payment_failed` | allowed, unchanged | `cancelled`, `cancelReason: buyer_withdrew` when none is sent, `cancelledBy: buyer`; nothing to refund |
| `payment_submitted` | refused, unchanged | wait for the verification or the 24h failure |
| `funded` | **refused** (flag on) | `ORDER_CANCEL_NOT_ALLOWED: The farmer and the trucker are deciding; you will be told as soon as they answer.` T3 and T4 end the wait either way |
| `awaiting_transport` | **allowed** | `cancelled` (`transport_failed_buyer_cancelled`, `cancelledBy: buyer`), full refund including commission (`refundReason: order_transport_failed`), stock restored |
| `accepted` | after the pickup deadline only (1.15.0) | unchanged until ESC-19; refused for an order stamped `pickupJudgement` while `pickupClassification` is on (2.15.0): `ORDER_CANCEL_NOT_ALLOWED: The pickup is judged from the arrival check-ins; you will be told the outcome.` |
| later | refused | a dispute after delivery is the only exit |

The farmer may cancel from `accepted` before pickup (fresh sign-in, `farmer_cancelled`, `faultParty:
farmer`, refund) and from `awaiting_transport` (no sign-in, `faultParty: none`).

### Trips under an order (`trip-listing-api.md` 2.0.0)

- **Order create** refuses a trip departing in under 24 hours (`ORDER_VALIDATION`) and a quantity
  above the trip's `remainingQuintals` (`TRIP_CAPACITY_EXCEEDED`); nothing is reserved yet. A trip
  that has left is refused before both, flag on or off, with `ORDER_TRIP_NOT_ELIGIBLE` (2.3.0).
- **Payment gate**: `payment_submitted` on a trip order is refused when the trip is no longer `open`
  or `matched`, has less room than the order, or departs in under 12 hours: `ORDER_VALIDATION: This
  trip is no longer available; place a new order.` Permanent (the client reverts the order), decided
  with the other payment gates before any write.
- **Reservation** at funding (atomic on `remainingQuintals`), verified at the trucker's accept,
  released when the order leaves the trip (reject, decline, expiry, drop-out, cancel). `matched` now
  means fully booked and is set by the system.
- **Trip cancelled or edited**: cancelling a trip (update to `cancelled` or delete) is refused with
  `TRIP_HAS_BOOKINGS` while an order on it is `pickup_confirmed` or `in_transit`; otherwise its
  `funded` and `accepted` orders move to `awaiting_transport` (`trip_cancelled`, pending deliveries
  cancelled) and unpaid orders are left to the payment gate. Once booked, `departureDate`,
  `pricePerQuintal`, `fromWoreda` and `toWoreda` are frozen and `capacity` may not drop below
  `bookedQuintals` (`TRIP_HAS_BOOKINGS`).

### Contact per party (the ESC-48 rule, 2.0.0)

Contact opens per party, no longer for the whole order at the farmer's accept: the buyer and the
farmer get each other's phone and chat once `farmerResponse: accepted`; anyone gets the trucker's
phone and chat only once `truckerResponse: accepted`. A trucker who declined, expired, dropped out,
or whose trip was cancelled is scrubbed from the phone map and the chat gate. `truckerPhone` is
therefore returned only after the trucker's accept, and an order or delivery thread with the trucker
takes messages only then (`MESSAGE_CONTACT_LOCKED` before). Behind the flag; off, the 1.18.0 rule
applies unchanged.

### Clients (2.1.0, app build 3: ESC-22, ESC-23)

- Mobile and web parse `awaiting_transport` and every commitment field, and read a status they do
  not know as `unknown` ("Updating", no actions; spec K1). The timeline has an
  `awaiting_transport` branch (created, funded, transport fell through) with its tooltip in en, am
  and om.
- **The server decides, the client mirrors.** `farmerResponseOf` / `truckerResponseOf` derive an
  unstamped row's answers as the server does (K2). A `fulfillment` stamp, written at funding only
  with `COMMITMENTS_ENABLED` on, tells the client the server runs the commitments. Without it the
  client keeps the rules from before 2.0.0: the farmer's accept is final, the buyer may cancel a
  `funded` order, and there is no trucker command, drop-out or farmer committed cancel. A staging
  or production server with the flag off therefore sees the same requests as from build 2, except
  that the farmer's answer is sent as the command (`sync-protocol.md` 1.30.0).
- Order detail: a "Who has agreed" section on `funded`, `awaiting_transport` and `accepted` (one row
  per party with its state and deadline in Ethiopia time) and a note for the viewer: the answer
  they owe and by when, why a paid order cannot be cancelled while the parties decide, or what to do
  after the transport failed. The farmer's Accept and Reject show only while the farmer's answer is
  pending; the trucker's Accept and Decline (with a reason sheet) only once the farmer accepted;
  the committed trucker's "I can't make this delivery" on `accepted`; the farmer's "Cancel without
  penalty" on `awaiting_transport` and the warned cancel on `accepted`; the buyer's Cancel on
  `awaiting_transport` (full refund) and not on `funded`.
- The local guards refuse what the server would: a trucker answer from anyone but the order's
  trucker or before the farmer accepted, and the cancels above. The optimistic row shows the
  server's result (a trip order stays `funded` with the trucker asked) until the next pull.
- Contact per party: the trucker appears in an order's contact list, and sees the parties, only
  once committed (`truckerResponse: accepted`); a funded order opens contact at the farmer's accept.
- Home shows "Action needed" cards per role (answer, keep the crops, set up the delivery, pay, pay
  again, decide after a failed transport, check a delivery), each opening the order.
- Trips: the owner sees the trip's booked orders; cards show the room left
  (`remainingQuintals`); match suggestions skip trips without room for the intent; with several
  suggested trips the buyer chooses one, and place order names the trucker, route, day and room.
- Older clients keep working while the flag is off. With the flag on, a client that sends
  `status: cancelled` on a `funded` order gets `ORDER_CANCEL_NOT_ALLOWED` and reverts (1.11.0); a
  farmer's legacy `status: accepted` on a trip order is taken as `farmerResponse: accept` and the
  order stays `funded`, which such a client shows as not yet accepted until the trucker answers.

### Not in 2.0.0

- Replacement truck and the switch to self-pickup (ESC-19, ESC-20), `transportAttempts`, partial
  refunds through the ledger.
- Reputation points for a decline, an expiry or a drop-out (`faultParty` is recorded for them;
  ESC-26 and later).
- Push notifications for the asks, reminders and failures (ESC-21, `notifications-api.md`).
- The fleet acceptance run (PR 4 of this change).

---

## The order follows the money (1.17.0, ESC-09)

Before 1.17.0 nothing wrote `disbursing` or `settled`: every order the escrow paid out stayed
`delivered` forever (the Escrow tile counted it as held, the admin stage stayed Delivered).

**Target.** The escrow's state decides where its order goes:

| Escrow | Order |
|---|---|
| any escrow with a dispute verdict (`disputeResolution`) | owned by the verdict: `dispute_resolved` (repaired after a settled verdict whose order hop failed), never `disbursing` or `settled` |
| `completed`, no `truckerPayoutPending`, no open manual payout | `settled` |
| `completed` still owing a leg (a legacy row before the ESC-04 park), `released` (with or without a trucker leg owed or queued), `escrowed` with a release in the manual payout queue, or (2.10.0, ESC-20) an `escrowed` ledger escrow whose crop leg is `released` (its release rows are open: being paid, waiting for a status check or in the manual queue) | `disbursing` |
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
replay), the stale release-lock park, the legacy trucker park and the crash recovery. For a ledger
escrow (2.10.0, `escrow-protocol.md` 2.2.0): the decision of a release (or a split verdict) and every
ledger row of `amount` that becomes `done`. A split verdict (`partial_refund`) owns its order like
`farmer_wins`: the order goes to `dispute_resolved`, as for the other verdicts. Best-effort: it
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
catch-up) sent it with a version behind the stored one: since 2.6.0 (SIM-28, `sync-protocol.md`
1.38.0) the transition check answers `conflict` with the server row, which the client adopts, when
the caller may ask for that status at all. At the current version, or for a status the caller may
never set (a system-managed one, another party's), it is still `ORDER_VALIDATION: Invalid status
transition`, permanent, and the client reverts to the server row. The buyer may rate
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

**Per party since 2.0.0** (`COMMITMENTS_ENABLED`): the buyer and the farmer open at the farmer's accept,
the trucker only at the trucker's accept, and a trucker who declined, expired or dropped out is
scrubbed from the phones and the chat gate. See Commitments, Contact per party.

## Dispute dispositions (1.21.0, FUP-16)

The admin verdict (`escrow-protocol.md` 1.21.0) no longer records a verdict the order cannot follow as
it is. Instead the admin picks a disposition, and the order moves with it as a system actor, in one
status-conditioned hop (`ADD version`, one transaction-audit row `dispute_resolved`, metadata
`resolution=<verdict>;disposition=<disposition>`), after the delivery was closed:

| Verdict | Order | Disposition | Order becomes | Delivery | Listing stock |
|---------|-------|-------------|---------------|----------|---------------|
| `buyer_wins` | goods moving: `pickup_confirmed`, `in_transit`, or `accepted` with a picked-up delivery | `return_to_farmer` | `dispute_resolved` | `cancelled` (`cancelReason: dispute_buyer_wins`) | not restored: the goods may come back damaged; the farmer relists |
| `buyer_wins` | same | `delivered_anyway` | `dispute_resolved` | `disputed` with the verdict, as after any delivered dispute | not restored (the buyer has the goods) |
| `farmer_wins` | after pickup and before delivery | `settle_now` | `dispute_resolved` | `cancelled` (`cancelReason: dispute_farmer_wins`) | not restored: the farmer is paid for goods already handed over |
| `farmer_wins` | `funded`, `accepted`, `pickup_confirmed` or `in_transit` | `wait_for_delivery` | unchanged; `dispute_resolved` once it is delivered, as in 1.12.0 | unchanged | unchanged |

The money follows through the escrow as for any verdict: `buyer_wins` refunds the buyer in full,
`farmer_wins` releases. The order stays at `dispute_resolved` (terminal, 1.17.0). The hop is conditional
on the status the verdict read, so an order that moved meanwhile (a handoff) is left alone and the admin
is told to send the verdict again. Until it does, the order is never taken for one before pickup: its
delivery was cancelled by the disposition, so the `buyer_wins` refund does not cancel the order or restore
its stock. Ratings stay blocked, and contact closes at `dispute_resolved`
(1.18.0). The clients already show `dispute_resolved`; no client change.

## Pickup judgement (2.15.0, ESC-34)

Spec 4.8 and 4.5 (T7), built behind `pickupClassification` (env `PICKUP_CLASSIFICATION_ENABLED` on the
sync and order-cron Lambdas, off in every environment, only with `COMMITMENTS_ENABLED`). The full rules,
the check-in and the late evidence are in `delivery-verification-api.md` 2.6.0 P.2 ("As built"); this
section names what changes on the order.

- **Stamp.** The write that makes an order `accepted` with the flag on stamps `pickupJudgement: pending`,
  an empty `checkIns` map and the farm's point. Orders accepted before the flag (or while it is off)
  keep the 1.15.0 rules: the `pickup_overdue` marker and the buyer's deadline cancel.
- **Check-ins** (`handoff_check_in`, `sync-protocol.md` 1.55.0) add to `checkIns` without moving
  `version` or `updatedAt`.
- **Judgement** at `pickupDeadlineAt` plus one minute (deadline kind `pickup_judgement`), in
  `commitment_deadlines`; each outcome also stamps `pickupJudgement`, `pickupJudgedAt` and
  `pickupOverdueNotifiedAt`:

| Outcome | Order | Money | Reputation |
|---|---|---|---|
| `scanned` (the delivery left `pending`) | unchanged (it lagged its delivery) | unchanged | none |
| `trucker_no_show` | `awaiting_transport`, `transportFailure: trucker_no_show`, `truckerResponse: no_show`, `faultParty: trucker`, `buyerChoiceDeadlineAt` now + 48h; pending delivery cancelled, trip capacity released | held; the buyer chooses (T5) | `NO_SHOW` on the trucker (R10) |
| `farmer_no_show` | `cancelled`, `cancelReason: farmer_no_show`, `farmerResponse: no_show`, `cancelledBy: system`, `faultParty: farmer`; pending delivery cancelled, stock and capacity back | full refund armed in the cancel's transaction (as the 2.13.0 deadline cancels) | `NO_SHOW` on the farmer (R11), its fraud signal through the attribution guard |
| `buyer_no_show` (self-pickup) | `cancelled`, `cancelReason: buyer_no_show`, `faultParty: buyer` | full refund, as above | none in P3 |
| `both_present_no_scan`, `unchecked_check_in` | stays `accepted`; admin queue with that `adminQueueReason` | held | none automatic |

A judged no-show whose absent party has a late check-in on record also enters the admin queue as
`late_pickup_evidence`; so does a late check-in or a real QR scan that reaches the server after the
judgement. On a `cancelled` (farmer or buyer no-show) order that entry clears itself once the refund
leaves, like every entry; only the late QR scan logs an alarmed line, and the late check-in stays as
evidence on the order and in the audit trail for the party's reputation appeal. With the flag turned
off while orders wait, the cron stamps the P1 marker and clears `pickupJudgement`.

- **Buyer's deadline cancel** refused for a stamped order while the flag is on (Buyer cancel rules).
- **Late pickup scan.** A pickup on a judged order (not `scanned`) is refused `ORDER_DEADLINE_PASSED:`,
  nothing written (`delivery-verification-api.md` 2.6.0).
- **Admin queue reasons** gain `both_present_no_scan`, `unchecked_check_in` and `late_pickup_evidence`
  (`AdminQueueIndex`, written without a version bump, cleared like every entry when the escrow shows the
  money moved on). The stuck-order list from the deadline index shows `pickup_judgement` for an order
  the sweep left past its judgement.
- **Deadline index.** A pending order's `pickup_judgement` is in the sweep's partition (`0`) whether or
  not a notice was stamped, so the judgement is never parked in `watch`. The judgement stamps the notice,
  so an order still `accepted` after it (both present, an unmeasured arrival, a delivery the order
  lagged) and one whose stamp the flag turned off then wait in the `watch` partition as `pickup_waiting`
  (2.14.0, FUP-170). With the flag off nothing here applies and the 2.14.0 rules stand unchanged.
- **Admin re-open (2.16.0, FUP-287).** `POST /admin/orders/{orderId}/reopen-pickup` (admin group,
  `delivery-verification-api.md` 2.8.0 P.2 "The admin side") re-opens only `both_present_no_scan` and
  `unchecked_check_in` on an order still `accepted` whose escrow still simply holds the money, with a
  reason of 10 to 500 characters and the order version the admin read:

  | Exit | Order | Deadline index |
  |---|---|---|
  | `pending` | stays `accepted`, `version` + 1; `pickupJudgement: pending`, `pickupDeadlineAt` now plus 1 to 168 hours, `pickupJudgedAt` and `pickupOverdueNotifiedAt` removed, `checkIns` kept | `pickup_judgement` at the new deadline plus one minute, in the swept partition (`0`): judged again |
  | `handoff_confirmed` (not self-pickup) | `pickup_confirmed`, `version` + 1, `pickupJudgement: scanned`; its pending delivery `pickedUp` in the same transaction (the order follows its delivery, as after a scan) | none |

  Both remove the judgement's admin queue entry (`both_present_no_scan`, `unchecked_check_in`,
  `late_pickup_evidence`; a `hold_cap` entry stays), except when the order escrow reached its hold cap
  while that entry stood: the escrow cron then stamped `holdCapReachedAt` instead of writing its own
  entry, so the entry is kept as `adminQueueReason: hold_cap` (`delivery-verification-api.md` 2.8.0). An
  order read without an entry takes none that lands before the write. Both stamp `pickupReopen`, in one
  transaction conditioned on the version, the status, the judgement and the queue entry read, the
  delivery read and the escrow still holding the money (without a hold cap, when the entry goes). No money moves, no reputation event follows (these two judgements have none). A
  trucker, farmer or buyer no-show is never re-opened: it cancelled a delivery and released a trip
  booking, or cancelled the order with its refund armed.

## Drop-off judgement (2.18.0, ESC-36)

Spec 4.5 (T8, T9), 4.8 "Silent buyer", E3 and E4, built behind `dropoffClassification` (env
`DROPOFF_CLASSIFICATION_ENABLED` on the sync, deliveries, order-cron and admin Lambdas, off in every environment,
only with `COMMITMENTS_ENABLED`; before it goes on, the client FUP-336 and the buyer's pushes FUP-338). The full rules, the drop-off check-in and the arrival photo are in
`delivery-verification-api.md` 2.10.0 P.4 ("As built"); this section names what changes on the order.

- **Stamp.** The order's first move out of `accepted` after a trucker's pickup (the system follow of its
  delivery) also stamps, with the flag on, `dropoffJudgement: pending`, `dropoffDeadlineAt` (T8: the
  delivery's `pickedUpAt` + 72h, SSM `delivery-overdue-hours`, never below 24, and never before the stamp + 24h)
  and the server-only drop-off points `dropoffOrderLatitude`/`dropoffOrderLongitude` and
  `dropoffTripLatitude`/`dropoffTripLongitude` (with `dropoffTripRead`). An admin's `handoff_confirmed` re-open
  (2.16.0) stamps the same in its transaction. Orders picked up before the flag (or while it is off), and
  self-pickups, never take it.
- **The drop-off check-in** (`handoff_check_in` kind `arrived_dropoff`, `sync-protocol.md` 1.59.0) is stored
  as the server-only `dropoffCheckIn` without moving `version` or `updatedAt`; read as
  `checkIns.arrived_dropoff`. Received by T8 it moves the order's deadline from `delivery_overdue` to
  `silent_buyer`. A photo-backed attempt more than 5 km from every point, before T8, is kept as the server-only
  `dropoffAttempt` (no deadline change).
- **Judgement** in `commitment_deadlines`; each outcome stamps `dropoffJudgement` and `dropoffJudgedAt`:

| Outcome | When | Order | Money | Reputation |
|---|---|---|---|---|
| `delivery_overdue` | T8, no drop-off check-in | `disputed`, `disputeReason: delivery_overdue` (version CAS); the delivery `disputed` (`disputedBy: system`) | the escrow frozen first; nothing moves | `DELIVERY_OVERDUE` on the trucker (R12) and the restriction pending review; alarm `ORDER_DELIVERY_OVERDUE` |
| `auto_confirmed` | T9, the evidence complete (measured check-in, arrival photo), the trucker neither low nor restricted, the money plainly held | `delivered`; the delivery `delivered` with `autoConfirmedAt` | the escrow stamped as a buyer's confirm (48h window, payout delay); the escrow cron releases as for any confirm | none |
| `silent_buyer` | T9, anything of the above missing | `disputed`, `disputeReason: silent_buyer`; the delivery `disputed` | frozen first | none |
| `queued` | T8 or T9, the money can be neither frozen nor confirmed; or T8 with a `dropoffAttempt` (the trucker came out of range of every point, FUP-204) | unchanged; admin queue `delivery_overdue`, `silent_buyer` or `dropoff_out_of_range` (the entry first, then the stamp; `DROPOFF_QUEUE_NOT_WRITTEN` when the order was queued already for another reason) | unchanged | none |
| `closed` | the buyer confirmed (or a party disputed) before the judgement | follows its delivery (`delivered`), or unchanged | as the confirm stamped it | none |

- **Admin queue reasons** gain `delivery_overdue`, `silent_buyer` and `dropoff_out_of_range`; the stuck-order list from the deadline
  index shows the kinds `delivery_overdue` and `silent_buyer` for an order the sweep left past them.
- **Order fields read by the clients** (`GET /orders`, `GET /orders/{id}`, the order rows of `/sync`
  results): `dropoffJudgement`, `dropoffJudgedAt`, `dropoffDeadlineAt`, `disputeReason`; the stamped points
  and the raw check-in never.
- **Deadline index and repair.** Both kinds are in the sweep's partition (`0`). A judged order has no
  drop-off deadline. With the flag on, the daily `deadline_repair` also walks `pickup_confirmed` and `in_transit`. With the
  flag turned off while orders wait, a pending order's stamp is cleared when it falls due (stored as a null
  attribute) and the order leaves the index. With the flag off nothing here applies.

## Changelog

- **2.18.0 (2026-11-13)**: Drop-off judgement (ESC-36, `delivery-verification-api.md` 2.10.0, `sync-protocol.md` 1.59.0), behind `dropoffClassification` (off everywhere): the order's first move out of `accepted` after a trucker's pickup stamps `dropoffJudgement: pending`, `dropoffDeadlineAt` (T8) and the drop-off points; the drop-off check-in fills `dropoffCheckIn` and moves the deadline to T9; `commitment_deadlines` judges T8 (`disputed`, `disputeReason: delivery_overdue`, the escrow frozen first, R12) and T9 (`delivered` for a silent buyer with the escrow stamped as a buyer's confirm, else `disputed` with `silent_buyer`); money neither freezable nor confirmable goes to the admin queue (`delivery_overdue`, `silent_buyer`). New system edges `pickup_confirmed | in_transit → disputed | delivered`, deadline kinds `delivery_overdue` and `silent_buyer`, SSM timings `delivery-overdue-hours` and `silent-buyer-hours` (never below 24), order fields `dropoffJudgement`, `dropoffJudgedAt`, `dropoffDeadlineAt`, `disputeReason`, the repair walk over `pickup_confirmed` and `in_transit` (with the flag on). FUP-204: a trucker's out-of-range attempt by T8 sends the order to the admin queue (`dropoff_out_of_range`) instead of the overdue dispute; the admin's `handoff_confirmed` re-open stamps the order too. 2.16.0 is FUP-287 and 2.17.0 FUP-19.
- **2.17.0 (2026-11-13)**: Admin cancel after pickup (FUP-19, spec E9, `delivery-verification-api.md` 2.9.0): once every escrow of the order is `completed`, `cancelled` or `refunded`, an admin may cancel its delivery left `pickedUp` or `inTransit` (after the override refund of goods on the move, or a verdict recorded before dispositions). The order is not written: it keeps its status, its money flags and its delivery slot, so it takes no new delivery; the cancel's transaction only checks that the order's status is still the one read (`accepted`, `pickup_confirmed`, `in_transit`, `dispute_resolved`, `cancelled` or `rejected`). No client change.
- **2.16.0 (2026-11-13)**: Admin re-open of a judged pickup (FUP-287, `delivery-verification-api.md` 2.8.0): `POST /admin/orders/{orderId}/reopen-pickup` takes an `accepted` order judged `both_present_no_scan` or `unchecked_check_in` whose escrow still simply holds the money back to `pickupJudgement: pending` with a new `pickupDeadlineAt` (judged again a minute after it, `pickupJudgedAt` and `pickupOverdueNotifiedAt` removed, `checkIns` kept), or confirms the handoff (`pickup_confirmed`, `scanned`, the pending delivery `pickedUp` in the same transaction); both remove the judgement's admin queue entry (kept as `hold_cap` when the escrow's hold cap rides on it) and stamp the new stored-only `pickupReopen`. No-show judgements, `scanned`, unjudged orders and orders that moved on are refused. No money, status of the money, reputation or client change. Companion: `data-models.md` 1.16.0.
- **2.15.0 (2026-11-12)**: Pickup judgement (ESC-34, `delivery-verification-api.md` 2.6.0, `sync-protocol.md` 1.55.0), behind `pickupClassification` (off everywhere): the accept of a flagged order stamps `pickupJudgement: pending`, `checkIns` and the farm's point; arrival check-ins fill `checkIns` without a version move; `commitment_deadlines` judges at the pickup deadline plus one minute (deadline kind `pickup_judgement`): `scanned`, `trucker_no_show` (`awaiting_transport`), `farmer_no_show` and `buyer_no_show` (`cancelled`, full refund in the cancel's transaction), `both_present_no_scan` and `unchecked_check_in` (admin queue, money held), plus `late_pickup_evidence` in the admin queue; new values `truckerResponse` / `farmerResponse` `no_show`, `transportFailure` `trucker_no_show`, `cancelReason` `farmer_no_show` and `buyer_no_show`; the buyer's deadline cancel is refused for a flagged order; a pickup after the judgement is refused `ORDER_DEADLINE_PASSED`. Off, nothing changes. Companion: `data-models.md` 1.14.0.
- **2.14.0 (2026-11-12)**: The pickup wait stays indexed (FUP-170, `escrow-protocol.md` 2.17.0): an `accepted` order whose `pickupOverdueNotifiedAt` is stamped keeps a `DeadlineIndex` entry, kind `pickup_waiting` at its `pickupDeadlineAt`, in a second partition (`deadlineShard = watch`) that the `commitment_deadlines` sweep never reads, until the write that takes it out of `accepted` (pickup, cancel, `awaiting_transport`). The admin's Stuck Orders lists it under Missed deadlines with a Query; before, it left the index with its notice and showed up only at the 21 day hold cap. The sweep's reads, decisions, refunds and notices are unchanged; a lagging entry of such an order in the sweep's partition is moved, not retried or logged `ORDER_DEADLINE_STUCK`. `deadline_repair` stamps the entry on the orders notified before this version and then reads the `watch` partition to correct or remove the entries their orders no longer give. No status, money, client or field change (the three deadline attributes stay server-only).
- **2.13.1 (2026-11-12)**: The 7 day refund sweep's own cancel of a stale `funded` or `awaiting_transport` order (`escrow_refunded`) gives the listing stock back fail closed before its refund (FUP-230, `escrow-protocol.md` 2.14.0): a restore that keeps conflicting or throws defers the refund to the next run, which restores first; the trip booking release still runs with the cancel. No status, field or client change.
- **2.13.0 (2026-11-12)**: The last cancel-then-arm writers (FUP-210, FUP-211, `escrow-protocol.md` 2.13.0): the order cron's `farmer_response_expired` and `transport_not_arranged` cancels and the funding's `listing_out_of_stock` end write the order and the escrow refund arm in one transaction, so a fraud hold or a settlement can no longer leave a cancelled order with its refund unarmed. Money a dispute froze or that is being settled writes nothing: the cron's order waits an hour (`ORDER_DEADLINE_REFUND_WAITING`), the funding's order stays `funded` for a person (`ORDER_STOCK_DECREMENT_FAILED ... status=escrow_busy`). The hourly refund sweep gives an `escrow_refunded` cancel's listing stock back before the refund, fail closed, so an admin refund whose restore failed is never refunded ahead of the stock. With nothing in the way the statuses, audit rows, stock, trip capacity, notifications and refund are as before. No client change.
- **2.12.0 (2026-11-12)**: Cancel-then-refund races (FUP-53, `escrow-protocol.md` 2.11.0, `sync-protocol.md` 1.51.0): the reject or cancel of a `funded` order through sync, the admin refund's cancel before pickup and the 7 day sweep's cancel of a stale order write the order and the escrow refund arm in one transaction, as the transport-failure cancel does since 1.23.0. Money a dispute froze or that is being settled refuses the close and nothing is written (sync: `ORDER_CANCEL_NOT_ALLOWED: The payment is being settled or reviewed.`, permanent; admin: `409 ESCROW_INVALID_TRANSITION`; sweep: skipped for the run). `POST /escrow/dispute` is refused once the order is `cancelled` or `rejected`, also when that happens between its read and its freeze. With nothing in the way the statuses, audit rows, stock, notifications and refund are as before. No client change.
- **2.11.0 (2026-11-11)**: No id stored as a name (FUP-130): on create `farmerName` is always the listing's name and `truckerName` the trip's, never the client's copy. Without a real name on the listing `farmerName` is the empty string; without one on the trip `truckerName` is left off. Older apps sent the farmer's id as `farmerName` for an unnamed listing and the server kept it; rows written before still hold ids and clients hide them (SIM-33). Clients send an empty `farmerName` instead of the id.
- **2.10.0 (2026-11-07)**: The escrow ledger (ESC-20, `escrow-protocol.md` 2.2.0): an order escrow born on the ledger (behind `ESCROW_LEDGER_ENABLED`, dev on) settles leg by leg. The order follows its money as before, plus one case: an `escrowed` ledger escrow whose crop leg is `released` (the release is decided, its rows still open) moves the order `delivered -> disbursing`; it becomes `settled` when the escrow becomes `completed` (every row of the amount done). A split verdict (`partial_refund`, admin `action: split`) needs the goods delivered and moves the order like `farmer_wins` (`dispute_resolved`). A cancel or rejection still schedules the refund as before (the hourly refund sweep writes the ledger's `refund#all`). No status, field or client change.
- **2.9.0 (2026-11-07)**: Drop-off place (LT-14, `specs/features/live-trip.md` section 10; builds the "Delivery location" of `delivery-verification-api.md` P.4): the optional `deliveryLatitude` and `deliveryLongitude` on create and, by the buyer only, on update until pickup (the drop-off note's window) on a trucked order (ignored without a trip); both or neither, numbers inside Ethiopia's box, a pair of nulls clears it; new permanent code `ORDER_DELIVERY_LOCATION_LOCKED`; returned as numbers to the buyer, the farmer and the order's trucker while the money is held and the trucker is still on the order (not asked yet, asked or committed; the note keeps its narrower rule). The live track, the truck's other stops, `truck_nearby`, the admin trail and the dispute evidence read it before the trip's destination; the delivered GPS check passes within 5 km of either point. The clients pick a place, starting from the intent's place or the registered place. Companions: `data-models.md` 1.7.0, `sync-protocol.md` 1.42.0, `live-trip-api.md` 1.1.0, `delivery-verification-api.md` 2.1.0, `escrow-protocol.md` 2.1.1.
- **2.8.0 (2026-11-07)**: Order deadlines (ESC-16, spec 4.5): every order write keeps the order's next deadline on the row (server-only `nextDeadlineAt`, `nextDeadlineKind`, `deadlineShard`: dropped from client payloads, not in the `GET /orders` answers, echoed only in a sync result's raw `serverData`), and `commitment_deadlines` runs every 15 minutes over the sparse `DeadlineIndex` (Query, oldest first, each row read again before it is decided) instead of walking the `funded`, `awaiting_transport` and `accepted` partitions every hour; a daily `deadline_repair`, and a repair pass `commitment_deadlines` runs itself once a day per container (so the first run after a deploy stamps the orders in flight), stamp rows that lack the deadline. The windows of T1 to T7 and the order-create lead are SSM parameters under `/{prefix}/orders/`, actually read, with the old values as defaults; T2 never goes below 22h10m, the close of the last checkout (spec 4.5's 6 hours is not adopted). With `COMMITMENTS_ENABLED` on, the escrow sweep holds a live order escrow to its 21-day cap in one write instead of extending it 48 hours at a time, and its hourly order-money sweep stamps a delivered order's escrow that missed its delivery stamp (alarm `escrow-delivery-stamp-failures`; `escrow-protocol.md` 2.1.0). The decisions, codes and statuses are unchanged; the cancelled-trip catch-up runs when the order is next due. No client change. Companion: `data-models.md` 1.6.0.
- **2.7.0 (2026-11-06)**: Order money to the santim (SIM-30, `escrow-protocol.md` 1.39.0): order create rounds `cropCost`, `transportCost` and `commissionAmount` to 2 decimals (halves away from zero, exact decimals) and stores `orderTotal` as their exact sum, so a commission is no longer stored as an unrounded double (0.9500000000000001) and the order's commission equals its escrow's. The rule is unchanged (5 % of the crop cost, on top, never on transport); an order whose crop or transport cost has more than 2 decimals (a fractional kg) can see its total move by a santim. The mobile and web previews and the apps' local copy of a new order round the same way. No field, status or endpoint change.
- **2.6.0 (2026-11-06)**: Race losers (SIM-28, `sync-protocol.md` 1.38.0): a client status change that is not a valid transition from the stored status, sent with a version behind the stored one by a caller who may ask for that status (`ValidateOrderTransitionAuth` passes), answers `conflict` with the order row instead of `ORDER_VALIDATION: Invalid status transition`; nothing is written. At the current version, or for a system-managed status or another party's, the error stands. The ESC-09 note under "The order follows the money" is updated. No client change: a conflict is already resolved server-wins.
- **2.5.0 (2026-11-06)**: Drop-off landmark (LT-12, `specs/features/live-trip.md` section 10): the optional `dropoffNote` on create and, by the buyer only, on update until pickup (the order status and the active delivery decide); one line of at most 80 user-visible characters (extended grapheme clusters); the listing text phone screen; new permanent codes `ORDER_CONTACT_IN_TEXT` and `ORDER_DROPOFF_NOTE_LOCKED`; returned to the buyer, the farmer and the committed trucker of a held order only (the order's visibility is unchanged), and as `dropoff.note` on the live track. The deliveries Lambda reads the orders table (Query) for it. Companions: `data-models.md` 1.5.0, `sync-protocol.md` 1.36.0, `live-trip-api.md` 1.0.7.
- **2.4.0 (2026-11-06)**: Stock at funding (ESC-18, spec A2, G1, G2): the funding decrement requires `quantity >= q` in its conditional write, computes in exact decimals and writes the remainder unrounded (restores alike); a funding that finds less takes nothing, is recorded in the listing's server-only `stockRefusedFor`, and the order is cancelled by the system (`cancelReason: listing_out_of_stock`, `cancelledBy: system`, `faultParty: none`) with its escrow refund scheduled (`refundReason: listing_out_of_stock`, full amount) and any trip booking released; never a stock restore for it. On every funding path (mock auto-fund, Telebirr webhook, redelivery repair), flag on or off. A funding loser whose farmer already accepted (also checked in the cancel's condition) is left for a person (`ORDER_STOCK_DECREMENT_FAILED ... reason=insufficient_stock`); an escrow whose create failed is created once more before the refund is armed (`ORDER_ESCROW_REFUND_SCHEDULE_FAILED reason=no_escrow` when it still fails). With `COMMITMENTS_ENABLED`, a listing with orders `payment_submitted`, `funded`, `awaiting_transport` or `accepted` cannot be deleted (`CROP_HAS_ACTIVE_ORDERS`, `crop-listing-api.md` 1.7.0, `sync-protocol.md` 1.35.0). The "Known gap" is closed. No client change: clients show the cancelled order and already handle the code.
- **2.3.0 (2026-11-04)**: Orders on a trip that left (L-7, `trip-listing-api.md` 2.2.0): an order create naming a trip whose departure day is over in Ethiopia (Africa/Addis_Ababa) is refused with `ORDER_TRIP_NOT_ELIGIBLE: This trip has already left; choose another trip.`, nothing written, whatever `COMMITMENTS_ENABLED` (with the flag on, the 24-hour lead already refused such trips with `ORDER_VALIDATION`; with it off nothing did). The code is the one clients treat as permanent since `sync-protocol.md` 1.10.0, with its own text since 1.17.0 ("That truck is no longer available. Choose again."), so a stale offline cache cannot book a departed truck and no client change is needed (`sync-protocol.md` 1.34.0). The apps also check the chosen truck when the order is placed (open, not left, room for the quantity) and say why offline. Attaching a trip to an order that is already paid is still not built (ESC-19, ESC-20)
- **2.2.0 (2026-10-31)**: Order places for the trip bookings row (Fieldway Trip detail board: "Almaz Bekele, Meki → Selam Traders, Merkato"): order create stamps the server-only `pickupPlace` (the listing's `locationName`, from the listing read `BindOrderToListing` already makes) and `dropoffPlace` (the buyer's registered woreda, from the profile read `StampOrderBuyerNameAsync` already makes); a client-sent value is dropped. No new reads, no status change. Older orders have neither and the clients fall back to the trip's route. Companion: `data-models.md` 1.2.0.
- **2.1.0 (2026-10-27)**: Clients for the commitments (ESC-22, ESC-23, app build 3): `awaiting_transport` and `unknown` on mobile and web, the commitment fields parsed, the farmer's answer sent as `farmerResponse`, the trucker's command and drop-out, the cancel rules mirrored, per-party contact, the "Who has agreed" section, Action-needed cards, trip bookings and room left. The `fulfillment` stamp is the client's signal that the server runs the commitments; without it the client keeps the rules from before 2.0.0. `COMMITMENTS_ENABLED` is on in dev, off in staging and production. No server change.

- **2.0.0 (2026-10-26)**: Sequential commitments (ESC-15, server slice, behind `COMMITMENTS_ENABLED`, off everywhere until the clients ship): funding stamps `fulfillment`, `pickupDate`, `farmerResponse: pending`, `farmerDeadlineAt` and `truckerResponse: not_asked | none` and reserves the trip capacity; the farmer answers with `farmerResponse: accept | decline` (legacy `status: accepted | rejected` mapped), and an accept on a trip order keeps the order `funded` and asks the trucker (`truckerResponse: pending`, `truckerAskedAt`, `truckerDeadlineAt`); the trucker answers with `truckerResponse: accept | decline`, the accept makes the order `accepted`, stamps `pickupDeadlineAt` and confirms the booking in one transaction, the decline moves it to the new system-managed status `awaiting_transport` (`transportFailure`, `buyerChoiceDeadlineAt` = now + 48h, `faultParty: trucker`); a trip full or cancelled while the money is held goes there too without asking the farmer, as does a trucker drop-out from `accepted` (fresh sign-in, `AUTH_040`); the exits are the buyer's cancel (full refund), the farmer's cancel (no penalty) and the 48 hour deadline (`transport_not_arranged`); a buyer `funded → cancelled` is refused with `ORDER_CANCEL_NOT_ALLOWED`, and the farmer may cancel from `accepted` before pickup with a fresh sign-in (`farmer_cancelled`); the hourly cron action `commitment_deadlines` cancels a farmer's no-answer (`farmer_response_expired`), expires the trucker's (`trucker_expired`), stamps the 6 hour reminder markers and `pickupOverdueNotifiedAt`, and catches orders of a cancelled trip; the payment gate refuses a trip that is no longer open, too small or departing in under 12 hours, and order create a trip departing in under 24 hours or a quantity above `remainingQuintals` (`TRIP_CAPACITY_EXCEEDED`); contact opens per party (buyer and farmer at the farmer's accept, the trucker at theirs); `ORDER_RESPONSE_NOT_PENDING` and `ORDER_DEADLINE_PASSED` are permanent; 18 new server-only order fields, all returned by `GET /orders`. Companion contracts: `trip-listing-api.md` 2.0.0, `sync-protocol.md` 1.29.0, `escrow-protocol.md` 1.34.0, `data-models.md` 1.1.0. Replacement truck and the self-pickup switch stay ESC-19/20.
- **1.27.0 (2026-10-23)**: Payout delay (FRD-13, `escrow-protocol.md` 1.33.0): a `delivered` order whose farmer or trucker is new or flagged stays `delivered` until its escrow's `releaseNotBefore` (at most 72 hours after delivery with the defaults, never before the 48 hour dispute window ends), then follows the money as before. The dispute window is unchanged. No status, transition or client change on the order.
- **1.26.0 (2026-10-23)**: Deleted parties (ACC-02, `account-deletion.md` 1.2.0): a `payment_submitted` for an order whose buyer, farmer or trucker is being or has been deleted is refused with `ORDER_VALIDATION: A party to this order has deleted their account.`, read strongly consistent, after the risk tier check and before KYC, nothing written; permanent under the `ORDER_*` rule (the client reverts the order). The same refusal on `POST /escrow/order-checkout` and `POST /escrow/initiate` is `422 ESCROW_030` (`escrow-protocol.md` 1.28.0). A user's deletion also cancels their unpaid orders with `cancelReason = account_deleted`. No status or client change.
- **1.25.0 (2026-10-23)**: Risk tiers built (FRD-04, the 1.9.0 rules; `escrow-protocol.md` 1.27.0, `sync-protocol.md` 1.25.0, `auth-api.md` 2.10.0): a buyer at risk tier CRITICAL (a truncated score read included) gets `FRAUD_ACCOUNT_HELD` on order create and on `payment_submitted` (before KYC), nothing written; MEDIUM adds the fresh password at any amount, HIGH the Fayda login and then the password at any amount; the buyer's tier only, read once per batch, LOW when it cannot be read; the user-action gates go live on deploy, not behind `FRAUD_HOLD_ENABLED`; a party at CRITICAL is a settlement hold trigger like a suspended one (behind the flag, shipped off: the sweep skips the settlement), and an unscored party skips it; `AUTH_041` text names no amount. No order status, field or client change.
- **1.24.0 (2026-10-23)**: Live payment total code (FUP-22): with the live gateway a `payment_submitted` whose total (rounded to the cent) is outside 100 to 500,000 ETB is refused with `ORDER_PAYMENT_TOTAL_OUT_OF_RANGE` and the same sentence (it was `ORDER_VALIDATION`), so clients match a code, not English text; same gate, bounds and place among the payment gates, nothing written; permanent under the `ORDER_*` rule (`sync-protocol.md` 1.24.0). Clients match the code and, for one release, the old text; clients before this release revert the order and show the generic refusal. App builds now read CI's `ENVIRONMENT=prod` as production (it fell back to development), and a release build with an unknown value gets the production settings, so it is live.
- **1.23.0 (2026-10-23)**: Transport cancel and dispute race (FUP-32): the transport-failure cancel writes the order and the escrow refund arm in one transaction, the escrow conditioned on a plain hold, so a dispute that froze the money after the check refuses the cancel (`ORDER_CANCEL_NOT_ALLOWED: The payment is being settled or reviewed.`, nothing written) instead of leaving a cancelled order over frozen money; once armed, no dispute can freeze the refund (`escrow-protocol.md` 1.25.0). No order status, field or code change.
- **1.22.0 (2026-10-23)**: Fraud hold built for a suspended party (FRD-03, `escrow-protocol.md` 1.22.0): the settlement sweeps hold the escrow of a suspended buyer, farmer or trucker behind `FRAUD_HOLD_ENABLED` (shipped off), with no order status change; the refund-sweep hold is fenced on the order's version and status; the farmer's accept of an order whose escrow is held is refused with `ORDER_VALIDATION` (an unreadable escrow refuses for now, transient; with the flag on the accept is fenced on the escrow in one transaction), reject and cancel stay allowed; the admin refund's order side is built (`heldFor: refund` cancels and restocks a still `funded` order and refuses anything but `funded`, `rejected` or `cancelled`; `heldFor: release` takes the order to `dispute_resolved` as `buyer_wins`); no client change. The risk tiers stay planned (FRD-04).
- **1.21.0 (2026-10-22)**: Dispute dispositions (FUP-16; 1.19.0 and 1.20.0 belong to FUP-20 and ESC-47, in flight): an admin's dispute disposition moves an order from `funded`, `accepted`, `pickup_confirmed` or `in_transit` straight to `dispute_resolved` (system, status-conditioned, one `dispute_resolved` transaction-audit row with the disposition), after its delivery is cancelled (`return_to_farmer`, `settle_now`) or disputed (`delivered_anyway`); `wait_for_delivery` leaves the order to the 1.12.0 rules; listing stock is never restored by a disposition; a verdict the order cannot follow is no longer recorded without one, so no order waits on a mismatched verdict any more (`escrow-protocol.md` 1.21.0).
- **1.20.0 (2026-10-23)**: Payment retry clock and the lost payment row (ESC-47 item 1 rest, item 2): the server stamps the server-only `pendingPaymentAt` each time an order enters `pending_payment`, and the order cron cancels a `pending_payment` order 24 hours after it (`createdAt` for orders from before), so a buyer who retries a failed payment on an old order through `pending_payment` is not cancelled at the next run; `created` still counts from `createdAt`. The Telebirr funding's transaction-audit `payment_verified` row is keyed by `paymentVerifiedAt` and written once, and a redelivery of the same payment writes it when the first delivery did not, in every order status. No client change: the apps ignore the new field, and the current apps never send `pending_payment` (older ones do). (1.19.0 is FUP-20's, in flight.)
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
