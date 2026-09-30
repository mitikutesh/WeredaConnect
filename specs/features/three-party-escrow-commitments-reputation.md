# Feature: Three-Party Escrow, Commitments and Reputation

**Status**: APPROVED 2026-09-30 (decisions D1 to D9 answered in §14, task ESC-00 done); implementation starts with P0
**Tasks**: EPIC ESC in `TASK.md` (ESC-00 to ESC-43, grouped by the phases in §13)
**Code survey**: `specs/reports/escrow-flow-survey-2026-09-29.md` (every finding with file:line and its owning task)
**Author**: AI-assisted
**Date**: 2026-09-29
**Phase**: R5 production hardening (money flow)
**Supersedes**: PRD `docs/project-background.md` §4.3 (staged buyer-cancel refunds) and the "Trucker never shows up" row of §9.1; the buyer `funded -> cancelled` path in `order-lifecycle.md` 1.6.0
**Related**: EPIC FRD in `TASK.md` (FRD-02, FRD-03, FRD-09, FRD-11, FRD-13, FRD-15), SIM-47, SIM-48, SIM-53, SIM-57

---

## 1. What

One payment, three parties, explicit commitments with deadlines, and a reputation system that makes broken commitments costly.

1. **Fund first.** The buyer pays once. The payment is verified and held before the farmer or the trucker is asked to do anything. The hold has three legs: crop (farmer), transport (trucker) and commission (platform).
2. **Both suppliers commit.** The farmer and the trucker each accept or decline the paid order before a deadline. The order becomes `accepted` (ready for pickup) only when the farmer has accepted and the transport is settled: the trucker accepted, or the order is a self-pickup.
3. **A failed trucker gives the buyer a choice.** If the farmer accepted but the trucker declines, lets the request expire, cancels or does not show up, the order moves to a new status, `awaiting_transport`. The buyer then picks another truck, collects the crops themselves, or cancels for a full refund. This is the only case in which a buyer can cancel a paid order.
4. **Every state has a deadline.** When one passes, the server applies a fixed outcome or parks the order in an admin queue with an alarm. No escrow is ever extended indefinitely, and no money moves on a timeout after pickup.
5. **Money moves only on evidence.** QR handoff at pickup (farmer shows, trucker scans) and at drop-off (trucker shows, buyer scans), GPS check-ins, a delivery photo, or an admin verdict. Every refund and payout is a ledger row with one deterministic reference, settled exactly once.
6. **Reputation.** A farmer or trucker who declines, ignores, cancels or misses a paid order gets penalty points. Points lower the rating buyers see, move the party down in browse and matching, add a public "Low reputation" tag at 6 points, and block new commitments at 10. Points expire after 90 days, completed orders earn them back, and an admin can excuse them on appeal.

## 2. Why

- **Crops are perishable and seasonal.** A farmer who harvests and bags for a buyer who then walks away loses money, storage and sometimes the crop. Fund-first means nobody asks the farmer to commit before the money is held, and the no-cancel rule means the buyer cannot walk away after the farmer has committed.
- **The buyer carries the payment risk.** Today a paid order whose trucker disappears keeps the buyer's money held with no end date. A rural buyer who sees that once will not pay upfront again.
- **Transport is the weakest link at harvest.** Trucks are scarce, roads close, phones die. Replacing a failed trucker keeps the farmer's sale alive instead of cancelling it.
- **Rural trade already runs on reputation** (word of mouth, known brokers). Showing reliability with plain reasons, and a way to recover, is familiar and builds trust in a platform that holds people's money.
- **The PRD already expects this**: both parties accept within 24h (PRD §3 step 3), a trucker no-show is penalized, and so is a farmer whose goods are not ready (PRD §9.1).
- **Telebirr live money is close,** and several money paths are not safe yet (section 3.1). They must be fixed before real funds flow, whatever else gets built.

## 3. Current state

Surveyed 2026-09-29 on `fix/payment-fayda-stepup-pickup-qr` (bc06971) by four read-only code surveys. Rows marked **checked** were re-read in code for this plan. Re-check the rest when implementing, and expect line numbers to drift.

### 3.1 Money-safety bugs (Phase 0, before live money)

| # | Finding | Evidence |
|---|---|---|
| M1 | **Live order funding does not exist.** Only the mock auto-fund sets an order to `funded` and creates the order escrow. The Telebirr webhook only updates escrows created by `/escrow/initiate`. In production no order can reach `funded`, so nothing after it works. **checked** | `SyncFunction.cs:5409, 5672-5724`; `TelebirrWebhookFunction.cs:160-224` |
| M2 | **Live payouts and refunds are fake.** The live gateway's release and refund return success with a made-up reference and move no money, so escrows would show `completed` or `refunded` while nobody was paid. **checked** | `TelebirrSandboxGateway.cs:146-160` |
| M3 | **Admin dispute verdicts cannot settle.** A verdict moves the escrow to `dispute_resolved`, but the settlement lock only accepts `escrowed` or `disputed`. Both the hourly settlement and a manual admin release or refund fail with 409. The tests mock the lock to always succeed. **checked** | `EscrowRepository.cs:141-142`; `AdminFunction.cs:401`; `EscrowCronFunction.cs:238-323` |
| M4 | **An unpaid trucker leg is never retried.** `truckerPayoutPending` is written and read nowhere, and the escrow still moves to `completed`. **checked** | `EscrowService.cs:1130-1166` |
| M5 | **Money is held forever after the farmer accepts.** `accepted` has one exit (`pickup_confirmed`), and the refund sweep pushes `expiresAt` out by 48h on every run with no cap. The only way out is a full admin refund, which leaves the order status unchanged. **checked** | `SyncFunction.cs:260`; `EscrowCronFunction.cs:44-47, 460-470` |
| M6 | **Double-pay risk once payouts are real.** A failed or timed-out gateway call releases the lock, and the next sweep pays again unless the provider dedupes the reference. Neither gateway does. | `EscrowService.cs:1100-1110, 1624-1634` |
| M7 | **Crash windows leave the settlement lock held.** This happens after the farmer is paid but before `released` is written, and when a refund's status write fails (it has no retry). There is no recovery sweep for either. | `EscrowService.cs:1177-1194, 1641` |
| M8 | **An order-level dispute does not freeze the escrow.** A buyer or farmer can set the order to `disputed` through sync, and the escrow still auto-releases. `/escrow/dispute` cannot write orders or deliveries because it has read-only grants (SIM-47). | `SyncFunction.cs:263`; `api-stack.ts:~1028` |
| M9 | **Delivery delete skips the state machine.** The trucker can soft-cancel a delivery from any status, including `pickedUp`, `inTransit` and `delivered`, with no audit. | `SyncFunction.cs:4658-4751` |
| M10 | **Orders never reach `disbursing` or `settled`.** Nothing sets them. | code survey |
| M11 | **Sweeps can starve.** Each sweep takes the same oldest 100 rows. Rows skipped without being changed (KYC-blocked releases, unknown order states) stay at the front forever. | `EscrowRepository.cs:373-435` |
| M12 | **Idempotency keys are global.** Another user can claim your key first, so your operation is dropped as a duplicate (SIM-48). | `SyncFunction.cs:~636` |
| M13 | **Mobile never reverts a rejected order update.** `ORDER_VALIDATION` and `ORDER_FORBIDDEN` keep the optimistic status in Isar and retry forever, and they block the pull for that order. | `sync_executor.dart:685-698, 845-850`; `isar_marketplace_sync.dart:117-121` |

### 3.2 Flow gaps

| # | Finding | Evidence |
|---|---|---|
| F1 | **No trucker accept or decline.** The buyer binds a trip at order time, and the trucker is never asked or notified. The trucker may only send logistics statuses. | `SyncFunction.cs:5190-5202` |
| F2 | **Trips are not booked.** Capacity is never checked or reserved. Nothing sets `matched`, so "Start trip" can't be reached. Trips past departure still take orders. Date, price and capacity stay editable after orders bind. | `SyncFunction.cs:2576-2644, 6090-6135`; `TripRepository.cs:40-59` |
| F3 | **Cancelling a trip does nothing to its orders**, and the trucker can still create a delivery for them. The orders `TripIdIndex` exists but nothing queries it. | `SyncFunction.cs:2487-2800`; `OrderRepository.cs:175-182`; `database-stack.ts:495-500` |
| F4 | **The buyer can cancel a paid order** (`funded -> cancelled`). The new rule removes this. **checked** | `SyncFunction.cs:259` |
| F5 | **Listings can be oversold.** Two orders funding at once can take more than the listing's stock (a documented known gap). | `order-lifecycle.md`, "Known gap" |
| F6 | **Drop-off proof is weak for trucker deliveries.** The buyer's `delivered` step re-checks the farmer's pickup nonce, but no screen shows a QR after pickup (reported; verify on a device). The drop-off geofence is dead code because trips never store coordinates. | `SyncFunction.cs:4012-4133, 4325-4352`; `delivery_detail_screen.dart:320, 423-436` |
| F7 | **No notifications for order, trip, delivery or escrow events.** Push only covers chat and buyer match alerts, and tapping a push opens nothing. | `MessageStreamFunction.cs`; `CropMatchStreamFunction.cs`; `push_notification_service.dart` |
| F8 | **Old app versions show unknown order statuses as `created`**, with the buyer's Pay Now and Cancel buttons. | `web_order_repository.dart:396-403`; `isar_marketplace_sync.dart:471-478`; `conflict_resolver.dart:357-362` |
| F9 | **Timing settings in SSM are ignored.** The dispute-window and escrow-timeout parameters are passed to the Lambdas but never read; 48h and 7 days are hardcoded. | `api-stack.ts:687-700`; `EscrowService.cs:60-61` |
| F10 | **The buyer never sees which trucker was chosen.** One-tap "Order" from match results silently picks the first suggested trip, and the place-order screen never names the trucker. | `match_results_screen.dart:273-300`; `place_order_screen.dart:283-290` |

### 3.3 Reputation gaps

| # | Finding | Evidence |
|---|---|---|
| R1 | **Nothing records misbehaviour.** No record of who cancelled, no penalty data, and no counters on user rows. | `OrderDtos.cs:137-146`; `UserRepository.cs:205-296` |
| R2 | **`farmerReliability` is always null or 1.0.** The `expired` crop status never happens, so matching scores almost every farmer at the neutral 0.7. There is no trucker reliability at all. | `SyncFunction.cs:1848-1910` |
| R3 | **Ratings are costly to read and half shown.** Summaries are recomputed on every read (up to 50 index pages) and fetched once per farmer on each browse screen. Trucker ratings are never shown, and Profile shows "-". | `RatingRepository.cs:138-184`; `browse_listings_screen.dart:63-71`; `profile_screen.dart:866` |
| R4 | **Fraud signals can't hold long-lived penalty points.** They expire after 30 days, have no index, and only `pending` ones would count toward a score. | `database-stack.ts:436-452` |
| R5 | **Suspension blocks sign-in only.** A suspended farmer's listings can still be ordered. | `LoginFunction.cs:194-202` |

## 4. Design

### 4.1 Rules that make the flow hard to manipulate

1. **No money, no request.** A farmer or trucker is only asked to commit after the payment is verified and held. A request without money behind it cannot cost anyone reputation, which makes penalty griefing expensive.
2. **The server clock decides.** Deadlines are stored on the order and evaluated by the server. Client timestamps are never used for decisions.
3. **Money follows evidence, never a claim.** Evidence means a QR handoff at pickup and drop-off, GPS check-ins, a delivery photo, or an admin verdict.
4. **Every non-terminal state has a deadline and a defined outcome.** Nothing is extended indefinitely. A case with no safe automatic outcome goes to an admin queue with an alarm, and the money stays where it is.
5. **One reference per money movement.** Each refund or payout is a ledger row written before the gateway call, with a deterministic reference, and settled exactly once. An unknown outcome is never retried blindly.
6. **The server computes every amount** from its own listing, trip and order rows (existing). Payouts go only to the registered, verified phone (existing).
7. **Every change is version-checked and audited.** Changes use a version compare-and-set (CAS) and are written to the transaction-audit and escrow-audit tables.
8. **Penalties come only from system-observed events.** No party can file a penalty against another. Admin excuses are audited.
9. **Restrictions block new commitments only.** Existing orders of a penalized party continue, so buyers are never hurt by someone else's lockout.

### 4.2 Order state machine v2

```
created -> pending_payment -> payment_submitted -> funded
payment_submitted -> payment_failed -> pending_payment | cancelled
created | pending_payment | payment_failed -> cancelled          (buyer withdraws; nothing paid)

funded -> accepted             farmer accepted AND (trucker accepted OR self-pickup)
funded -> awaiting_transport   farmer accepted AND transport failed
funded -> rejected             farmer declined                               [full refund]
funded -> cancelled            farmer deadline passed                        [full refund]

awaiting_transport -> accepted     replacement trucker accepted, or buyer chose self-pickup
awaiting_transport -> cancelled    buyer cancelled, buyer deadline passed, or farmer cancelled [full refund]

accepted -> awaiting_transport     trucker cancelled or did not show up
accepted -> cancelled              farmer cancelled or did not show up           [full refund]
accepted -> pickup_confirmed -> in_transit -> delivered     (driven by the delivery)
accepted -> delivered                                        (self-pickup, one scan)
pickup_confirmed | in_transit -> disputed                    delivery deadline passed
delivered -> disbursing -> settled                           48h with no dispute, then payout
delivered -> disputed -> dispute_resolved -> settled
```

| Status | Meaning | Money | Set by |
|---|---|---|---|
| `funded` | Money held; waiting for the farmer and the trucker to respond | held | system |
| `accepted` | Everyone committed; ready for pickup | held | system, derived from the responses |
| `awaiting_transport` (new) | The farmer committed but the transport is not settled. Waiting for the buyer's choice or a replacement trucker's answer. | held | system |
| `rejected` | The farmer declined | refunded in full | farmer |
| `cancelled` | See `cancelReason` (4.6) | refunded in full, or nothing if unpaid | buyer (unpaid or `awaiting_transport` only), farmer, system |
| `disbursing`, `settled` | Payout in progress, then paid out. Now actually set by the payout path. | leaving escrow, then paid | system |

The other statuses keep their current meaning. One change: `disputed` can now only come from a delivery dispute or `/escrow/dispute`, and both of those freeze the escrow (M8).

### 4.3 Who may do what

Clients send commands, not raw status values (section 10.1). The server checks the actor, the status and the deadline, then derives the new status. A farmer app that still sends `status: accepted` or `rejected` is mapped to the matching command, so older clients keep working until the minimum app version is raised.

| Order status | Buyer | Farmer | Trucker |
|---|---|---|---|
| `created`, `pending_payment`, `payment_failed` | Pay, Withdraw | see it | see it |
| `payment_submitted` | wait | see it | see it |
| `funded` | see who has answered, and the deadlines | Accept, Decline (before T3) | Accept, Decline (before T4) |
| `awaiting_transport` | Choose another truck, Collect it myself, Cancel (full refund) | Cancel (no penalty) | replacement trucker: Accept, Decline; failed trucker: closed |
| `accepted` (truck) | see it | Show QR, Cancel (penalty, fresh sign-in); P3: Crops are ready | Create delivery, Scan QR, Cancel (penalty, fresh sign-in); P3: I have arrived |
| `accepted` (self-pickup) | Scan the farmer's QR; P3: I have arrived | Show QR, Cancel (penalty, fresh sign-in) | none |
| `pickup_confirmed`, `in_transit` | confirm delivery (existing flow; P3: scan the trucker's QR) | see it | Mark in transit; P3: Show drop-off QR, Arrived at drop-off |
| `delivered` | Dispute (48h), Rate | Dispute (48h) | Dispute (48h) |
| `disputed` | add evidence | add evidence | add evidence |
| `settled`, `cancelled`, `rejected` | Rate (settled only) | see it | see it |

"Fresh sign-in" is the existing `AUTH_040` step-up. It protects against someone else using a farmer's phone to cancel (PRD §8.5).

### 4.4 New order fields

| Field | Values | Notes |
|---|---|---|
| `fulfillment` | `trucker`, `self_pickup` | Set at create from `tripId`. It changes only through the buyer's transport choice, and it replaces guessing self-pickup from empty ids. |
| `pickupDate` | ISO date | Trip orders: the trip's `departureDate`. Self-pickup: chosen by the buyer, 1 to 7 days ahead. |
| `farmerResponse` | `pending`, `accepted`, `declined`, `expired`, `cancelled`, `no_show` | Comes with `farmerRespondedAt` and `farmerDeadlineAt`. |
| `truckerResponse` | `pending`, `accepted`, `declined`, `expired`, `cancelled`, `no_show`, `none` | Comes with `truckerRespondedAt` and `truckerDeadlineAt`. `none` for self-pickup. |
| `transportAttempts` | list, at most 3 | `{tripId, truckerId, transportCost, outcome, requestedAt, closedAt}`; the first entry is the original trip. |
| `buyerChoiceDeadlineAt` | ISO | Only in `awaiting_transport` with no open request. |
| `nextDeadlineAt`, `nextDeadlineKind`, `deadlineShard` | | Drive the sweep through a sparse index (4.5); removed when nothing is pending. |
| `cancelledBy` | `buyer`, `farmer`, `trucker`, `system` | |
| `cancelReason` | code (4.6) | Free text moves to `cancelNote`. |
| `faultParty` | `farmer`, `trucker`, `buyer`, `none` | Written by the server; feeds reputation. |
| `refundedTotal` | ETB | Sum of refunds so far. `orderTotal` stays what the buyer paid. |
| `deliveryLatitude`, `deliveryLongitude` | numbers | P3. Set by the buyer at order time, defaulting to the buyer intent or woreda centroid. |
| `adminQueue` | reason code | Sparse index for the admin queue. |

`tripId`, `truckerId`, `truckerName` and `transportCost` stay immutable to client edits. Only the buyer's transport-choice command changes them, and the server computes the new values.

### 4.5 Deadlines

Server time only. The defaults below live in SSM (`/{prefix}/orders/...`) and are actually read by the code. Today the SSM timing parameters are ignored (F9).

| # | Waiting for | Deadline | When it passes |
|---|---|---|---|
| T1 | Buyer to pay (`created`, `pending_payment`) | created + 24h | `cancelled` (`unpaid_expired`). This exists today. |
| T2 | Payment verification (`payment_submitted`, live gateway) | submitted + 6h | `payment_failed`; the buyer is told to retry. If money arrives late and the order is gone, reconciliation refunds it. |
| T3 | Farmer's answer (`funded`) | min(funded + 24h, pickupDate - 12h) | `cancelled` (`farmer_response_expired`), full refund. Farmer +1 point if reachable by push (D8). |
| T4 | Trucker's answer (`funded`) | min(funded + 24h, pickupDate - 12h) | `truckerResponse = expired`, trucker +1. If the farmer already accepted, the order moves to `awaiting_transport`. |
| T5 | Buyer's transport choice (`awaiting_transport`, no open request) | entered + 48h | `cancelled` (`transport_not_arranged`), full refund. Nobody is penalized. |
| T6 | Replacement trucker's answer (`awaiting_transport`, open request) | min(requested + 24h, new pickupDate - 12h) | The attempt fails (trucker +1) and the buyer gets T5 again with a fresh 48h. |
| T7 | Pickup (`accepted`) | pickupDate + 24h, plus 24h grace for offline sync | P1: `awaiting_transport` (the buyer may choose again or cancel; nobody penalized). P3: judged by check-ins (4.8). |
| T8 | Delivery (`pickup_confirmed`, `in_transit`) | pickup + 72h | `disputed` (`delivery_overdue`), trucker restricted pending review, alarm. No money moves. |
| T9 | Buyer confirmation after the trucker's drop-off check-in (P3) | check-in + 48h | Auto-confirm if the evidence is complete, else `disputed`. |
| T10 | Dispute window (`delivered`) | confirmedAt + 48h | Release. This exists today. |
| T11 | Admin verdict (`disputed`) | + 7 days | Alarm only. Money never moves on this timer. |
| T12 | Any non-terminal order | funded + 21 days | Admin queue and alarm. The escrow sweep never extends past this. |

**The sweep.** `OrderCronFunction` runs every 15 minutes (hourly today).
- It queries a sparse `DeadlineIndex` on the orders table for rows that are due now, oldest first. The partition key `deadlineShard` is the day of `nextDeadlineAt`, and the sort key is `nextDeadlineAt`.
- Each handler re-reads the order and applies its outcome with a version CAS. It then sets the next deadline or removes the attribute.
- A row that needs a person leaves the deadline index and enters `AdminQueueIndex`. Skipped rows can no longer starve the sweep (M11).
- The escrow sweep stops extending order escrows. It only settles ledger rows and raises an alarm on order escrows past T12.

**Order create lead time.** The chosen trip must depart at least 24h ahead (configurable). The existing 30-day maximum on trip departure still applies.

### 4.6 Buyer cancel rules and reason codes

| Order status | Can the buyer cancel? | Result |
|---|---|---|
| `created`, `pending_payment`, `payment_failed` | Yes, withdraw an unpaid order (D1) | `cancelled` (`buyer_withdrew`); nothing to refund |
| `payment_submitted` | No | Wait for verification or failure (T2) |
| `funded` | **No (new)** | The farmer and trucker are deciding; T3 and T4 end it either way |
| `awaiting_transport` | **Yes** | `cancelled` (`transport_failed_buyer_cancelled`); full refund including commission; stock returned |
| `accepted` and later | **No** | Only a dispute after delivery |

The server refuses every other buyer cancel with `ORDER_CANCEL_NOT_ALLOWED`.

**Cancel reason codes:**
- `buyer_withdrew`, `unpaid_expired`
- `farmer_response_expired`, `farmer_cancelled`, `farmer_no_show`
- `transport_failed_buyer_cancelled`, `transport_not_arranged`
- `listing_out_of_stock`, `buyer_no_show` (P3), `admin`

**Rejection reason:** `farmer_declined`.

### 4.7 When the transport fails

**What counts as a transport failure:**
- The trucker declines, or lets the request expire (T4).
- The trucker cancels after accepting, or cancels the whole trip.
- The trip turns out to be full at funding.
- In P1, no pickup happens by T7.

**On entering `awaiting_transport`:**
- The trip capacity held for the order is released, and the failed attempt is closed in `transportAttempts`.
- The buyer gets a push and an "Action needed" card. The farmer is told: the buyer is arranging another truck, keep the crops ready.
- If the farmer has not answered yet, the order stays `funded` and moves to `awaiting_transport` the moment the farmer accepts. If the farmer declines instead, the order is `rejected` with a full refund, and the transport failure no longer matters.

**The buyer's three options:**
1. **Another truck.**
   - The trip must be `open` or `matched`.
   - It must depart at least 24h from now, and no more than 7 days after the original pickup date.
   - It must have enough free capacity.
   - Its trucker must not be restricted and must not have already failed this order.
   - Its transport cost (order quintals × new price per quintal) may be higher than the transport money held, but the extra must be no more than the commission still held on the order (D4).

   The server recomputes the cost. If it is lower, the difference is refunded to the buyer through the ledger. If it is higher, the difference moves from the commission leg to the transport leg, so the buyer pays nothing extra and the platform absorbs it. It then moves the order and the escrow's transport leg to the new trucker, reserves the capacity, and sends the new trucker a request (T6). At most 3 trucks per order, counting the original. Each later replacement can only use the commission that is left.
2. **Collect it myself.** `fulfillment` becomes `self_pickup` and the whole transport leg is refunded. The buyer picks a pickup date 1 to 7 days ahead, and the order becomes `accepted` because the farmer already committed. The existing self-pickup delivery flow takes over.
3. **Cancel.** Everything still held is refunded in full, including commission, and the stock returns to the listing. Nobody is penalized for this step; the trucker's failure was already recorded.

**If nobody acts:**
- If the buyer does nothing for 48h (T5), the order is cancelled with a full refund.
- While the order is in `awaiting_transport`, the farmer may cancel without penalty, because the disruption is not the farmer's fault.
- Once a replacement trucker accepts and the order is `accepted` again, the normal farmer penalties apply.

**Example.**
1. The farmer accepts a funded order for 20 quintals. The trucker declines, and gets 1 penalty point.
2. The buyer sees "The trucker can't make it" and three buttons. She picks another trip that is 50 ETB per quintal cheaper, so 1,000 ETB of transport money goes back to her Telebirr.
3. The new trucker accepts within 24h, and the order is `accepted` again.

### 4.8 Evidence at pickup and drop-off

**Pickup QR (exists today).** The farmer shows a code backed by a nonce held on the server. The trucker, or the self-pickup buyer, scans it. The code is single use, fails closed, and works offline.

**New in P3:**
- **Arrival check-ins.** The trucker taps "I have arrived" (GPS within 5 km of the listing, from pickupDate - 24h). The farmer taps "Crops are ready" (GPS optional). Both are queued offline, and the server judges them by the time it receives them.
- **Pickup judgement at T7**, after the 24h offline grace, using only check-ins the server has received:

  | Trucker check-in | Farmer check-in | Pickup scan | Outcome |
  |---|---|---|---|
  | any | any | yes | normal flow |
  | no | any | no | trucker no-show: `awaiting_transport`, trucker +5 |
  | yes | no | no | farmer no-show: `cancelled` (`farmer_no_show`), full refund, farmer +5 |
  | yes | yes | no | admin queue (`both_present_no_scan`), money held, no automatic penalty |

  Self-pickup uses the same table with the buyer in the trucker's column. A buyer no-show cancels the order (`buyer_no_show`) with a full refund and no points in P3. Fees are decision D5.
- **Drop-off QR (PRD step 6).**
  - After pickup, the trucker's app shows a drop-off code. It is backed by a nonce the trucker's app issues, mirroring the farmer's pickup nonce and stored in the same secret store.
  - The buyer scans it to confirm delivery. This replaces the buyer re-scanning the farmer's pickup nonce.
- **Silent buyer.**
  - The trucker's "Arrived at drop-off" check-in needs GPS within 5 km of the order's delivery location and a delivery photo (existing presigned upload).
  - If the buyer neither scans nor disputes within 48h (T9), the server confirms delivery and the normal 48h dispute window starts.
  - Missing evidence, or a trucker with a low or restricted tier, sends the order to `disputed` instead.
- **Manual code fallback.** A 6-digit code derived from the same nonce can be typed instead of scanned when a camera or screen fails. The server checks it the same way as the QR.
- **Late evidence.** A valid pickup scan that arrives after T7 was already judged is rejected (`ORDER_DEADLINE_PASSED`). It goes to the admin queue (`late_pickup_evidence`) so an admin can reconcile a real handoff.

### 4.9 Money: one hold, three legs, one ledger

The buyer still pays one `orderTotal` = crop cost + transport cost + commission (5% of crop cost). The order escrow (`escrow-order-{orderId}`) keeps its per-leg amounts (`farmerPayout`, `truckerPayout`, `commission`) and gains:
- `legStatus` = `{crop, transport, commission}`, each `held`, `released`, `refunded` or `kept`.
- `refundedTotal` and `releasedTotal`.
- An invariant, checked on every write and by tests: `farmerPayout + truckerPayout + commission + refundedTotal = amount`.

A new table `escrow-ledger` (PK `escrowId`, SK `ref`) holds one row per money movement:

| ref | Movement | Amount |
|---|---|---|
| `refund#transport-delta#{n}` | to the buyer, after a cheaper replacement truck | old transport cost minus new |
| `refund#transport` | to the buyer, after switching to self-pickup | the whole transport leg |
| `refund#all` | to the buyer, on any full cancel or rejection | everything still held |
| `release#farmer` | farmer payout | crop leg |
| `release#trucker#{truckerId}` | trucker payout | transport leg |
| `verdict#{leg}` | admin partial verdict | per leg, entered by the admin |
| `refund#duplicate#{gatewayRef}` | a second payment for the same order | that payment |
| `shift#commission-to-transport#{n}` | internal: a pricier replacement truck, covered by the platform (D4); no gateway call | new transport cost minus old, at most the commission left |
| `topup#{n}` (P4) | the buyer pays extra for a truck whose extra cost is more than the commission left | the difference beyond the commission |

**How every movement runs.** This replaces the single whole-escrow settlement lock.
1. One `TransactWriteItems` call puts the ledger row as `pending` with `attribute_not_exists(ref)` and updates the escrow row (version CAS, leg status, totals).
2. Call the gateway with `TransactionRef = {escrowId}:{ref}`.
3. **Success:** the row becomes `done`, with the gateway reference.
4. **Definite failure:** the row becomes `failed` and is retried later with the same ref. After 3 failures it becomes `manual`.
5. **Unknown outcome** (timeout, or a crash after sending): the row becomes `sent`. It is retried only after a status check on the same ref shows it never happened. A gateway with no status API sends it to `manual`.
6. A recovery sweep picks up `pending` and `sent` rows older than 15 minutes.
7. `manual` rows form the admin payout queue. The admin pays through the merchant portal and records the external reference, which closes the row.

**What this fixes.**
- M3: the ref is the lock, so `dispute_resolved` escrows settle.
- M4: each trucker leg has its own row and retries.
- M6: nothing is retried blindly.
- M7: write-ahead rows survive crashes.
- M10: the order status follows the money. Once all legs are settled the order moves `delivered -> disbursing -> settled`.

**Transport rebind.**
- The server updates the order's `tripId`, `truckerId`, `truckerName` and `transportCost`, and the escrow's `truckerId` and `truckerPayout`.
- These are CAS writes, conditioned on the previous trucker and on the transport leg still being `held`.
- It then writes `refund#transport-delta#{n}` if the new cost is lower, or `shift#commission-to-transport#{n}` if it is higher (D4): `truckerPayout` rises and `commission` falls by the same amount, so the invariant still holds and nothing reaches the gateway.
- Earlier truckers move to `previousTruckerIds` for history.
- The trucker escrow index follows `truckerId`, so a failed trucker stops seeing an escrow they will not be paid from.

**Commission.** The platform keeps it on completed orders and refunds it on any full cancellation, because the buyer is not at fault in any of them. The platform absorbs gateway refund fees.

**Live Telebirr (M1, M2).**
- The webhook funds the order (`payment_submitted -> funded`) through the same `CreateFromFundedOrderAsync` path the mock uses.
- The live gateway's release and refund return `PAYOUT_NOT_SUPPORTED` instead of a fake success. That sends them to the manual payout queue until the Telebirr B2B payout API is integrated.

**Capacity and stock at funding (F2, F5).**
- Funding atomically decrements the listing (`quantity >= q`) and reserves trip capacity (`bookedQuintals + q <= capacity`).
- If the stock loses the race, the order is `rejected` (`listing_out_of_stock`) with a full refund and no farmer penalty.
- If the trip loses, the transport counts as failed, with no trucker penalty.
- Reservations are idempotent per order, using the same pattern as `stockRestoredFor`. They are released whenever the order leaves the trip.

### 4.10 Trips and listings under an order

- **Trips hold bookings.** A trip gets `bookedQuintals` and `bookedOrderIds`, and its DTO gets a computed `remainingQuintals`. The first accepted booking moves it `open -> matched` (system).
- **Browse** shows `open` and `matched` trips that still have room and depart at least 24h from now.
- **Booked trips are frozen.** Once any funded order holds capacity on a trip, its `departureDate`, `pricePerQuintal` and route cannot change. Its capacity cannot drop below `bookedQuintals` (`TRIP_HAS_BOOKINGS`).
- **Cancelling a booked trip** runs the transport-failure flow for each affected order (found through the existing `TripIdIndex`) and records one penalty event for the trip (4.11).
- **Unbooked trips close themselves** once their departure passes (`cancelled`, reason `expired`), and leave browse.
- **Trip progress.**
  - The trucker can start the trip (`matched -> inTransit`) once at least one of its orders has been picked up.
  - The trip becomes `delivered` when all its deliveries are done or cancelled.
- **Listings with active orders are protected.** A listing with funded or accepted orders cannot be deleted (`CROP_HAS_ACTIVE_ORDERS`), and its quantity cannot drop below what those orders hold. The farmer must first cancel those orders explicitly, which is penalized.
- **Delivery delete follows the state machine** (M9). After pickup, only an admin can cancel a delivery.

### 4.11 Reputation

#### Penalty events (defaults in SSM)

| Code | Party | When | Points |
|---|---|---|---|
| `DECLINED_REQUEST` | farmer, trucker | declined a paid order | 1 |
| `RESPONSE_EXPIRED` | farmer, trucker | let a paid request expire | 1, only if reachable by push (D8) |
| `CANCELLED_AFTER_ACCEPT` | farmer, trucker | cancelled a committed order more than 24h before pickup | 3 |
| `LATE_CANCEL` | farmer, trucker | cancelled 24h or less before pickup | 4 |
| `TRIP_CANCELLED_WITH_BOOKINGS` | trucker | cancelled a trip that had accepted orders | 3, or 4 when late; one event per trip |
| `NO_SHOW` (P3) | farmer, trucker | judged a no-show at pickup | 5 |
| `DELIVERY_OVERDUE` (P3) | trucker | no delivery by T8 | 5, plus a restriction pending admin review |
| `ORDER_COMPLETED` | farmer, trucker | order settled without a lost dispute | 0 (counts for reliability and earn-back) |

**Not penalized:**
- races the system caused (stock or capacity lost at funding)
- a farmer cancelling during `awaiting_transport`
- requests from a buyer later found fraudulent (admin excuse)
- anything that happened before the feature flag was switched on

**Griefing caps:**
- Declines and expiries count at most 1 point per party and buyer pair per 30 days.
- They count at most 6 points per party in any 24 hours.
- A buyer whose paid orders are declined 3 times in 30 days gets a fraud signal (feeds FRD-09).

#### Aggregate on the user row (`reputation` map)

- `activePoints`: active events in the last 90 days, minus earn-back.
- `tier`.
- `ratingSum` and `ratingCount`: updated when a rating is created, so a summary no longer costs a paged scan per card.
- `completedOrders90d`, `faultCancels90d`, `noShows90d`.
- `reliabilityPct` = completed / (completed + fault cancels + no-shows); null below 3 orders.
- `effectiveRating`.
- `restrictedUntil`.
- `override`: `{tag, reason, by, until}`.
- `version`, `updatedAt`.

#### Tiers and what they do

| Tier | Points | Buyers see | Ranking | New listings, trips, accepts |
|---|---|---|---|---|
| Good | 0 to 2 | stars and reliability | normal | allowed |
| Warning | 3 to 5 | same as Good; the party gets a private warning | slight drop through the reliability signal | allowed |
| Low reputation | 6 to 9, or admin tag | red "Low reputation" chip with an icon, the lowered rating, and a confirm dialog before ordering | lower | allowed |
| Restricted | 10 or more, or admin | listings and trips hidden | excluded | blocked for 14 days (`REPUTATION_RESTRICTED`); existing orders continue |

A second restriction within 12 months puts the account in the admin review queue for suspension (the existing suspend action).

#### How the rating drops

- **On cards and detail screens** buyers see `effectiveRating`: the buyer star average minus 0.1 per active point. It drops by at most 1.0 and never goes below 1.0.
- **On the party's profile** the breakdown is shown, for example "4.8 from buyers, 0.4 deducted for cancellations". The raw buyer average stays visible and honest.
- **With no buyer ratings yet**, only the tag and reliability show.
- **Why this is harder to game:** completion outcomes come from events the system verified, which are much harder to fake than stars.

#### Recovery

- **Expiry:** each event expires after 90 days.
- **Earn-back:** each completed order removes 1 active point, oldest first. The limit is 2 per 30 days, and only orders of at least 500 ETB count, from distinct buyers and with no lost dispute.
- **Appeal:** the party can appeal within 7 days of an event, with text and an optional photo. An admin excuses it (points removed) or upholds it; both are audited.
- **Admin tools:** excuse any event, add or remove the "Low reputation" tag with a reason and an expiry, and restrict or lift. Bulk excuse by woreda and date range, for region-wide events such as a road closure, flood or unrest, is P4.

#### Where it lives

- **Events table.** A new `reputation-events` table (PK `userId`, SK `eventId`). Ids are deterministic, such as `DECLINED_REQUEST#{orderId}` or `TRIP_CANCELLED#{tripId}`, so a replay never double counts. It has a sparse `AppealStatusIndex`.
- **How events are recorded.** They come from the orders table stream through `ReputationStreamFunction`, which sees each change to the response, cancel and fault fields. No event depends on a best-effort write inside the sync request.
- **Copied onto listings and trips.** When a party's tier or effective rating changes, their active crop and trip rows get `ownerTier`, `ownerEffectiveRating`, `ownerRatingCount` and `ownerReliabilityPct`. Browse and match cards, and the offline Isar cache, then need no extra calls. This also retires the per-farmer rating fetch on browse.
- **Matching.** `reliabilityPct` replaces the broken `farmerReliability` in `MatchScorer` (Dart) and `MatchScoring` (C#), parity-tested. It also ranks trucker trips in the trip suggestions.
- **Daily job.** It expires events, recomputes aggregates, lifts ended restrictions and refreshes the copied fields.
- **Visibility.** Reputation is shown only to buyers (who are the only ones who see farmer and trucker listings) and to the party themselves. The role visibility rules are unchanged.

#### Ties to EPIC FRD

- `NO_SHOW` and `DELIVERY_OVERDUE` also write fraud signals.
- The buyer-griefing signal feeds FRD-09.
- FRD-13's payout delay reads the tier: low and restricted parties wait longer.
- Reputation stays separate from the fraud risk score. Reputation is public and about reliability; the risk score is private and about fraud.

### 4.12 Notifications

**Push.**
- A second consumer on the orders table stream, `OrderNotifyStreamFunction`, compares the old and new rows. It sends pushes through the existing SNS and FCM device tokens (Android only), with a deep link to the order.
- Web users see the same events as in-app cards.
- The server builds push text in the user's `preferredLanguage` (en, am, om) using `Helpers/PushText.cs`. This depends on SIM-37, since today that setting does not reach the server when changed.

**New push types:**
- `order_request`, `request_reminder` (6h before T3, T4 or T6), `request_expired`
- `transport_failed`, `replacement_request`
- `order_confirmed` (everyone committed), `order_cancelled`, `refund_sent`
- `pickup_reminder`, `delivery_arrived`
- `penalty_recorded`, `tier_changed`

There is no SMS fallback (D8). A party without a registered push device sees requests in the app only, and is not penalized when a request expires.

**In-app:**
- A "Requests" card on Home for farmers and truckers, with the count and the nearest deadline.
- A bookings list with Accept and Decline on the trucker's trip detail screen.
- An "Action needed" card for buyers in `awaiting_transport`.
- A `/orders/:id` route for deep links.

## 5. Edge cases

"+n" means n penalty points.

### A. Payment and funding

| # | Case | Rule |
|---|---|---|
| A1 | Payment sent, webhook never arrives | T2 moves to `payment_failed` after 6h. Reconciliation matches late money by gateway ref and funds the order, or refunds it if the order is gone. |
| A2 | Two buyers fund the last stock | Atomic decrement. The loser is `rejected` (`listing_out_of_stock`) with a full refund and no farmer penalty. |
| A3 | Two orders fund the last trip capacity | Atomic reservation. The loser's transport counts as failed, with no trucker penalty. |
| A4 | Buyer pays twice | `ESCROW_024` plus idempotency. A second real payment found by the webhook is refunded automatically. |
| A5 | Webhook amount differs from `orderTotal` | Rejected (exists today); goes to the admin queue. |
| A6 | Trip departs in under 24h, or already departed | Order create is rejected. |
| A7 | Trip or listing owner is restricted | Hidden from browse; order create is rejected (`REPUTATION_RESTRICTED`). |
| A8 | Funding lands less than 12h before the trip departs | The transport counts as failed (no penalty). The buyer chooses once the farmer accepts. |

### B. Commitments (after funding, before pickup)

| # | Case | Rule |
|---|---|---|
| B1 | Farmer declines | `rejected`, full refund, trucker capacity freed, farmer +1 |
| B2 | Farmer silent | T3: `cancelled`, full refund, farmer +1 if reachable |
| B3 | Trucker declines or is silent, farmer accepted | `awaiting_transport`, trucker +1 |
| B4 | Trucker declines, farmer still deciding | Stays `funded`. Moves to `awaiting_transport` when the farmer accepts, or `rejected` if the farmer declines. |
| B5 | Both decline | `rejected`, full refund, both +1 |
| B6 | Trucker accepted, then the farmer declines | `rejected`, full refund, trucker freed, nothing against the trucker |
| B7 | Farmer cancels after accepting | `cancelled`, full refund, farmer +3 (+4 within 24h of pickup). Needs a reason and a fresh sign-in. |
| B8 | Trucker cancels after accepting | `awaiting_transport`, trucker +3 (+4 if late). Same reason and fresh sign-in rule. |
| B9 | Trucker cancels a trip with several orders | Every accepted order moves to `awaiting_transport`, pending requests close as declined, and one trip event is recorded (+3 or +4). |
| B10 | An accept arrives after the deadline (queued offline) | `ORDER_DEADLINE_PASSED`. The app reverts and explains. The expiry point stands but can be appealed. |
| B11 | Double tap, retry, or two devices | Idempotent command plus version CAS; the loser gets the server's copy. |
| B12 | Someone uses the farmer's phone | Accepting is harmless. Cancelling or declining after accepting needs a fresh sign-in. |

### C. Transport failure and the buyer's choice

| # | Case | Rule |
|---|---|---|
| C1 | Buyer picks another truck | Eligibility rules in 4.7; the difference is refunded; the new trucker gets a request (T6). |
| C2 | Replacement trucker declines or is silent | Back to the choice with a fresh 48h; that trucker +1. |
| C3 | Third truck fails | Only self-pickup or cancel remain. |
| C4 | Only pricier trucks exist | Offered when the extra cost is no more than the commission left; the platform covers it from the commission (D4). Trucks beyond that are not offered (the buyer sees "No other trucks available"); a buyer top-up for them comes in P4. |
| C5 | Replacement departs more than 7 days after the original pickup date | Not offered, so the farmer's commitment stays reasonable. |
| C6 | Buyer picks self-pickup | Transport leg refunded, pickup date 1 to 7 days ahead, order `accepted`. |
| C7 | Buyer cancels | Full refund including commission, stock returned, no penalties. |
| C8 | Buyer silent for 48h | `cancelled`, full refund. |
| C9 | Farmer cancels during `awaiting_transport` | `cancelled`, full refund, no farmer penalty (recorded as a zero-point event). |
| C10 | Buyer's choice was queued offline and the trip filled meanwhile | `ORDER_TRIP_NOT_ELIGIBLE`; the app reverts and shows the choice again. |
| C11 | The failed trucker wants back in | Not possible on this order. |

### D. Pickup

| # | Case | Rule |
|---|---|---|
| D1 | Scan works | `pickup_confirmed` (exists today). |
| D2 | P1: no pickup by T7 plus grace | `awaiting_transport`; the buyer chooses again or cancels. Nobody is penalized until check-ins exist. |
| D3 | P3: trucker absent | Trucker no-show, `awaiting_transport`, trucker +5. |
| D4 | P3: farmer absent, trucker checked in | Farmer no-show, `cancelled`, full refund, farmer +5. Paying the trucker for the wasted trip is D5. |
| D5 | P3: both checked in, no scan | Admin queue, money held, no automatic penalty. |
| D6 | Camera or screen broken | 6-digit manual code from the same nonce. |
| D7 | Farmer offline, nonce not synced | Existing `deferred` handling; completes when the farmer syncs, within the grace period. |
| D8 | Farmer has less than ordered | P1 to P3: the trucker may refuse (farmer cancel path, +3), or take it and the buyer disputes after delivery. P4: weigh-in at pickup with buyer approval and an automatic partial refund. |
| D9 | Self-pickup buyer finds bad quality | The buyer refuses and opens a dispute with photos. |
| D10 | A valid scan arrives after the pickup was judged | Rejected; goes to the admin queue as `late_pickup_evidence`. |
| D11 | One trip, several farms | Each order is judged on its own. |

### E. Transit and drop-off

| # | Case | Rule |
|---|---|---|
| E1 | Normal | The trucker shows the drop-off QR, the buyer scans, `delivered`, 48h, release. |
| E2 | Buyer refuses the goods | Dispute; money frozen. |
| E3 | Buyer silent | T9 auto-confirm only with GPS at the delivery location, a photo, and a trucker who is not low or restricted. Otherwise a dispute. |
| E4 | Trucker never delivers | T8: `disputed`, trucker restricted pending review, fraud signal, alarm. |
| E5 | Goods lost or damaged after a valid pickup | Dispute. Default guidance: the farmer is paid (they delivered their part), transport is refunded to the buyer, and crop loss follows the platform terms (D6). |
| E6 | Short delivery | Admin partial verdict per leg. |
| E7 | Truck breaks down | The trucker reports it; an admin may extend T8 once (audited). |
| E8 | Buyer confirms by mistake | 48h dispute window (exists today). |
| E9 | Delivery deleted after pickup | Not allowed, except by an admin. |

### F. Settlement

| # | Case | Rule |
|---|---|---|
| F1 | Farmer payout blocked by KYC | The leg stays held; the farmer is told what to verify; alarm after 7 days. |
| F2 | Trucker payout blocked | The farmer leg is paid; the trucker row waits and retries once they are verified. |
| F3 | Gateway timeout | Row `sent`; status check before any retry. No status API means manual. |
| F4 | Live gateway has no payout API | Manual payout queue. Never a fake success. |
| F5 | Admin partial verdict | The per-leg amounts must add up to what is still held. |
| F6 | Crash during settlement | Write-ahead rows and the recovery sweep. |
| F7 | Refund phone not verified | Manual queue. |
| F8 | Rounding | 2 decimals. Commission stays the remainder, and ledger rows use the stored leg amounts. |

### G. Listings and trips changing underneath an order

| # | Case | Rule |
|---|---|---|
| G1 | Listing edited after orders | Order prices stay immutable (exists today); quantity cannot drop below the held stock. |
| G2 | Listing deleted with active orders | Blocked. |
| G3 | Trip date, price or route edited with bookings | Blocked; capacity cannot drop below what is booked. |
| G4 | Trip departed with no bookings | Closes itself. |
| G5 | Nothing sets a trip to `matched` today | The first accepted booking sets it. |

### H. Accounts

| # | Case | Rule |
|---|---|---|
| H1 | Farmer or trucker suspended for fraud mid-order | Pending requests expire with no extra points; accepted orders go to the admin queue; money held. |
| H2 | Party restricted by reputation | Existing orders continue; only new commitments are blocked. |
| H3 | Account deletion with open orders | Blocked until every order is terminal (ACC tasks). |
| H4 | Buyer suspended after funding | Escrow `held` (FRD-03); admin decides. |
| H5 | Admin changes a role while orders are open | Blocked. |

### I. Reputation integrity

| # | Case | Rule |
|---|---|---|
| I1 | A buyer places orders to force declines | Only paid requests count; 1 point per pair per 30 days; the buyer gets a fraud signal. |
| I2 | A fake buyer account to earn back points | Distinct buyers only, 500 ETB minimum, 2 per 30 days, plus FRD-15 relationship checks. |
| I3 | Farmer and trucker collude | Points follow the party at fault; money follows evidence. |
| I4 | Wrong attribution (an offline trucker judged absent) | Appeal with the synced evidence; admin excuse. |
| I5 | Region-wide event | Admin bulk excuse (P4). |
| I6 | Stale tag in the offline cache | The tag shows an "as of" date; the server re-checks at order create. |
| I7 | New user | No tag, neutral reliability. |
| I8 | Who can see whose reputation | Buyers see farmer and trucker reputation, and parties see their own. Farmers never see other farmers' reputation; truckers never see other truckers'. |

### J. Offline, clocks and races

| # | Case | Rule |
|---|---|---|
| J1 | Wrong device clock | Decisions use server time. Screens show absolute local (EAT) time plus a countdown. |
| J2 | Action queued offline | Shown as "Waiting to send"; it counts only when the server receives it. |
| J3 | Evidence synced late | 24h grace before the pickup is judged. |
| J4 | Sweep races a user action | Version CAS; the sweep re-reads and re-evaluates. |
| J5 | Sweep backlog | Deadline index plus admin queue; blocked rows leave the index. |
| J6 | Someone claims your idempotency key first | Keys scoped to the caller (SIM-48). |
| J7 | Rejected update stuck on mobile | `ORDER_*` rejections are permanent: revert the local change, show the reason, unblock the pull. |

### K. Rollout

| # | Case | Rule |
|---|---|---|
| K1 | An old app shows an unknown status as `created` (with Pay Now and Cancel) | Ship client support first: unknown statuses show as "Updating" with no actions. Then switch the server on. `/sync` rejects order commands from app versions below the minimum (`APP_UPDATE_REQUIRED`). |
| K2 | Orders already in flight at deploy | `funded` orders get deadlines counted from the deploy time. `accepted` orders with a trucker count as trucker-accepted, with a pickup deadline from deploy. No points for anything before the flag. |
| K3 | Rollback | One SSM feature flag per phase: `threePartyCommitments`, `reputationPenalties`, `reputationPublicTags`, `timeoutPenalties`, `pickupClassification`. |

## 6. Files to create or modify

The phase is in brackets. "(new)" marks new files.

### Server (`server/src`)

| File | Change |
|---|---|
| `Functions/TelebirrWebhookFunction.cs` | [P0] fund orders on verified payment through `CreateFromFundedOrderAsync` |
| `Services/TelebirrSandboxGateway.cs` | [P0] release and refund return `PAYOUT_NOT_SUPPORTED`, never a fake success |
| `Data/EscrowRepository.cs` | [P0] the lock accepts `dispute_resolved`; sweep cursor fix. [P1] ledger transact writes |
| `Services/EscrowService.cs` | [P0] trucker leg retry, no lock release on unknown outcomes, order follows payout. [P1] ledger execution, partial refunds, transport rebind, full refund on cancel |
| `Functions/EscrowCronFunction.cs` | [P0] cap the order-escrow extension at T12 and alarm. [P1] stop extending; ledger recovery sweep |
| `Functions/SyncFunction.cs` | [P0] remove the delivery delete bypass after pickup; order `disputed` only through a delivery or escrow dispute; idempotency keys per caller. [P1] order commands, v2 transitions and actors, buyer cancel rules, trip bookings, freeze, cascade and auto-close, listing delete guard, atomic stock. [P3] check-ins, drop-off nonce |
| `Services/OrderCommitmentService.cs` (new) | [P1] farmer and trucker responses, transport choice, cancels, derived status; keeps `SyncFunction.cs` from growing further |
| `Helpers/OrderDeadlines.cs` (new) | [P1] pure deadline math from config |
| `Data/OrderRepository.cs` | [P1] `QueryByTripAsync` (existing `TripIdIndex`), deadline and admin-queue queries |
| `Data/EscrowLedgerRepository.cs` + `IEscrowLedgerRepository.cs` (new) | [P1] ledger rows |
| `Functions/OrderCronFunction.cs` | [P1] deadline sweep every 15 minutes. [P2] daily reputation job |
| `Functions/OrderNotifyStreamFunction.cs`, `Helpers/PushText.cs` (new) | [P1] order stream to push, with text in 3 languages |
| `Functions/ReputationStreamFunction.cs` (new) | [P2] order stream to reputation events |
| `Services/ReputationService.cs`, `Helpers/ReputationPolicy.cs` (new) | [P2] events, caps, tiers, effective rating, expiry, earn-back, copying to listings and trips |
| `Data/ReputationEventRepository.cs` + interface (new) | [P2] |
| `Helpers/ReputationGate.cs` (new) | [P2] restriction check on listing and trip create and on accept (same pattern as `KycPolicy`) |
| `Functions/ReputationFunction.cs` (new) | [P2] `GET /reputation/{userId}`, `GET /reputation/me/events`, `POST /reputation/events/{eventId}/appeal` |
| `Functions/RatingFunction.cs`, `Data/RatingRepository.cs` | [P2] stored aggregate; reputation block in the summary |
| `Data/UserRepository.cs`, `Models/UserDto.cs` | [P2] `reputation` map (server-owned, excluded from profile updates) |
| `Functions/CropFunction.cs`, `TripFunction.cs`, `MatchFunction.cs`, `Helpers/MatchScoring.cs` | [P1] trip remaining capacity; `matched` trips in browse. [P2] owner reputation fields, hide restricted parties, new reliability source |
| `Functions/AdminFunction.cs` | [P0] manual payout queue, stuck orders. [P1] per-leg verdicts; `DeriveStage` for new statuses. [P2] reputation panel, appeals, tag and restrict |
| `Models/OrderDtos.cs`, `EscrowDtos.cs`, `CropDtos.cs`, `TripDtos.cs`, `AdminDtos.cs`, `ReputationDtos.cs` (new) | new fields and types |
| `AppJsonContext.cs`, `Program.cs` | register the new types and functions |
| `server/tests/**` | table-driven tests per service and function (section 11) |

### Infra (`infra/lib`)

| File | Change |
|---|---|
| `stacks/database-stack.ts` | [P1] orders stream (new and old images); `DeadlineIndex` and `AdminQueueIndex`; `escrow-ledger` table with a sparse `LedgerOpenIndex`. [P2] `reputation-events` table with `AppealStatusIndex` |
| `stacks/api-stack.ts` | [P0] escrow Lambda write grants on orders and deliveries (SIM-47). [P1] notify stream Lambda; cron every 15 minutes; SSM deadline parameters read by code; grants. [P2] reputation Lambdas, routes, daily schedule, SSM reputation parameters, feature flags |
| `stacks/monitoring-stack.ts` | [P0] alarms for admin queue age, manual payouts older than 24h, ledger rows `sent` for over 1h, and a settlement-failure log metric. [P1] stream iterator age |
| `infra/test/**` | synth assertions for the new resources |

### Mobile and web (`mobile/lib`)

| File | Change |
|---|---|
| `core/sync/sync_executor.dart`, `core/sync/isar_marketplace_sync.dart`, `core/sync/conflict_resolver.dart` | [P0] permanent `ORDER_*` rejections revert and show a reason; rejected ops no longer block the pull; nullable parsing for new fields |
| `core/network/api_exception.dart`, `core/errors/app_error.dart` | [P0 and P1] new codes mapped to localized messages |
| `core/database/schemas/order.dart` + `core/models/web/order.dart` (+ barrel) | [P1] new fields, `awaitingTransport`, safe `unknown` fallback (regenerate `order.g.dart`) |
| `core/database/schemas/trip.dart`, `crop_listing.dart`, `delivery.dart`, `payment.dart` + web models | [P1] bookings, refund totals, `dispute_resolved` on payments. [P2] owner reputation. [P3] check-ins |
| `core/models/order_status_wire.dart`, `order_status_sets.dart` | [P1] new status in the sets and docs |
| `features/order/**` (`order_detail_screen.dart`, `order_card.dart`, `orders_tab_screen.dart`, `place_order_screen.dart`, bloc, both repositories) | [P1] action matrix (4.3), commitment rows with deadlines, trucker Accept and Decline, buyer transport-choice sheet, cancel rules, show and change the chosen trucker |
| `features/order/presentation/widgets/transport_choice_sheet.dart`, `deadline_countdown.dart`, `commitment_status_row.dart` (new) | [P1] |
| `features/trip_listing/**` | [P1] bookings list with Accept and Decline, remaining capacity, cancel warning that shows the penalty. [P2] trucker reputation on cards |
| `features/crop_listing/**`, `shared/widgets/listing_card.dart`, `features/buyer/**` (cards, `match_scorer.dart`, both matching services) | [P2] tag, effective rating, reliability signal |
| `features/delivery_verification/**` | [P3] drop-off QR, check-ins, manual code, silent-buyer notice |
| `features/reputation/**` (new: mobile and web repositories, bloc, `reputation_badge.dart`, `my_reputation_screen.dart`, `appeal_dialog.dart`) | [P2] |
| `core/notifications/push_notification_service.dart`, router | [P1] new push types, open-to-order routing, `/orders/:id` |
| `screens/home_screen.dart`, `screens/profile_screen.dart` | [P1] Requests and Action-needed cards; localized sync line. [P2] own reputation instead of "-" |
| `main.dart` and `main_web.dart` | register the new blocs and repositories in both web provider trees |
| `shared/l10n/app_en.arb`, `app_am.arb`, `app_om.arb` | all keys in section 9 |
| `mobile/test/**` | section 11 |

### Admin (`admin/src`)

| File | Change |
|---|---|
| `lib/transaction.ts` | statuses (`rejected` is missing today, plus `awaiting_transport`) and `deriveStage`, mirrored in `AdminFunction.DeriveStage` |
| `pages/ManualPayoutsPage.tsx`, `pages/StuckOrdersPage.tsx` (new) | [P0] |
| `pages/DisputesPage.tsx`, `EscrowDetailPage.tsx`, `TransactionDetailPage.tsx` | [P1] per-leg verdict, ledger view, trucker payout (empty today) |
| `pages/UserDetailPage.tsx`, `pages/ReputationAppealsPage.tsx` (new) | [P2] reputation panel, excuse, tag, restrict, appeals |
| `App.tsx`, `components/Sidebar.tsx`, `i18n/{en,am,om}.json` | routes, navigation, strings |

### Contracts and docs

| File | Change |
|---|---|
| `specs/contracts/order-lifecycle.md` | 2.0.0: statuses, commands, actors, deadlines, cancel rules |
| `specs/contracts/escrow-protocol.md` | 2.0.0: ledger, per-leg settlement, partial refunds, rebind, manual payouts, no extension, live funding |
| `specs/contracts/trip-listing-api.md` | 2.0.0: bookings, capacity, freeze, cascade, auto-close; also fix the header (1.0.0) vs footer (1.2.0) drift |
| `specs/contracts/delivery-verification-api.md` | 2.0.0: drop-off QR, check-ins, auto-confirm, cancel rules; fix the header drift |
| `specs/contracts/sync-protocol.md` | order commands, new permanent error codes, minimum app version |
| `specs/contracts/rating-api.md` | 1.1.0: stored aggregate, effective rating |
| `specs/contracts/reputation-api.md` (new) | 1.0.0 |
| `specs/contracts/user-entity.md` | 2.0.0: match the real row, add `reputation` |
| `specs/contracts/push-alerts-api.md`, `data-models.md` | new push types, new fields |
| `specs/architecture/escrow-payment-architecture.md`, `reputation-architecture.md` (new), `fraud-detection-architecture.md` | design docs |
| `docs/project-background.md` | mark §4.3 and the §9.1 trucker row as superseded |
| `TASK.md`, `STATE.md`, `CLAUDE.md` (reference index), `.ai/deployment.md` | new EPIC; runbook for the manual payout queue and for the new tables, streams and flags |

## 7. Offline behavior

- **Queued actions.** Accept, decline, cancel and the buyer's transport choice go through the sync queue with idempotency keys. The app shows them as "Waiting to send", and they only count once the server receives them.
- **Requests work offline.** Pending requests, deadlines and commitment rows come from orders already pulled into Isar, so farmers and truckers can see what is waiting for them without a connection.
- **Deadlines that pass while queued.** The server rejects the action; the app reverts the local change and explains why. Deadlines are generous (24h) for this reason.
- **Choosing a replacement truck offline.** The buyer can browse cached trips; the server re-checks eligibility when the choice arrives.
- **Handoffs.** Both QR handoffs work offline (nonce model, `deferred` until the other side syncs). Check-ins store GPS offline and count from the time the server receives them, with a 24h grace before a pickup is judged.
- **Reputation.** Tags and effective ratings travel inside the cached crop and trip rows and show an "as of" date. The party's own reputation is cached locally.
- **Web** has no offline queue today (SIM-57). On web these actions need a connection and fail with a clear message.

## 8. Sync strategy

- **Commands.** An order update carries exactly one command in `data`:
  - `farmerResponse`, `truckerResponse`
  - `transportChoice`: `{kind: trip | self_pickup | cancel, tripId?, pickupDate?}`
  - `cancelCommitted`: `{reason}`
  - `withdraw`
  - the legacy `status`, mapped to a command for older clients

  The server rejects mixed payloads, derives the status, and returns the full order as `serverData`.
- **Versioning is unchanged.** Clients send the pre-increment version, and conflicts resolve server-wins.
- **New permanent rejection codes.** These are never retried; the client reverts the local change:
  - `ORDER_DEADLINE_PASSED`, `ORDER_CANCEL_NOT_ALLOWED`, `ORDER_ACTION_NOT_ALLOWED`
  - `ORDER_TRIP_NOT_ELIGIBLE`, `ORDER_TRANSPORT_ATTEMPTS_EXCEEDED`
  - `REPUTATION_RESTRICTED`, `TRIP_HAS_BOOKINGS`, `TRIP_CAPACITY_EXCEEDED`, `CROP_HAS_ACTIVE_ORDERS`
  - `APP_UPDATE_REQUIRED`
- **Pulls** carry the new order, trip and crop fields. The party's own reputation is read from `GET /reputation/me` at start-up and on pull-to-refresh, at most every 30 minutes.
- **Server-only effects.** Money movements, deadlines and penalties happen on the server only; clients never send them.

## 9. Localization

English is below. Amharic and Afan Oromo go through `/l10n` and are then reviewed by a native speaker before release, because money and penalty wording must be exact. Admin strings go in `admin/src/i18n/{en,am,om}.json`. Push text is built on the server in the same 3 languages (`Helpers/PushText.cs`).

| Key | English |
|---|---|
| `statusAwaitingTransport` | Finding transport |
| `statusAwaitingTransportInfo` | The farmer accepted but the trucker can't make it. The buyer can choose another truck, collect the crops, or cancel for a full refund. |
| `statusWaitingForResponses` | Waiting for farmer and trucker |
| `statusWaitingForResponsesInfo` | The money is held safely. The farmer and the trucker each have until {deadline} to accept. |
| `orderStatusUpdating` | Updating |
| `orderAcceptRequest` | Accept order |
| `orderDeclineRequest` | Decline |
| `orderDeclineConfirmTitle` | Decline this paid order? |
| `orderDeclinePenaltyNote` | Declining a paid order adds 1 penalty point. |
| `orderRespondBy` | Respond by {time} |
| `orderRequestExpired` | This request has expired. |
| `orderWaitingForFarmer` | Waiting for the farmer |
| `orderWaitingForTrucker` | Waiting for the trucker |
| `orderFarmerAcceptedLabel` | Farmer accepted |
| `orderTruckerAcceptedLabel` | Trucker accepted |
| `transportFailedTitle` | The trucker can't make it |
| `transportFailedBody` | Choose before {deadline}. If you don't choose, the order is cancelled and you get a full refund. |
| `transportChooseAnotherTruck` | Choose another truck |
| `transportCollectMyself` | Collect it myself |
| `transportCollectMyselfRefund` | {amount} ETB transport cost comes back to you. |
| `transportCancelFullRefund` | Cancel and get a full refund |
| `transportNoOtherTrucks` | No other trucks are available for this route and date. |
| `transportTrucksLeft` | {count, plural, =1{1 more truck can be tried} other{{count} more trucks can be tried}} |
| `transportDifferenceRefund` | {amount} ETB of the transport cost will be refunded. |
| `transportPickupDate` | Pickup date |
| `transportFarmerNotice` | The trucker can't make it. The buyer is arranging another truck. Keep the crops ready. |
| `orderCancelNotAllowed` | A paid order can't be cancelled. If something goes wrong, open a dispute after delivery. |
| `orderCancelCommittedTitle` | Cancel this order? |
| `orderCancelCommittedPenalty` | Cancelling after you accepted adds {points} penalty points and lowers your rating. |
| `orderCancelReasonLabel` | Tell the buyer why |
| `tripCancelWithBookingsWarning` | This trip has {count} booked orders. Cancelling adds {points} penalty points. |
| `tripBookings` | Booked orders |
| `tripRemainingCapacity` | {quintals} quintals free |
| `requestsTitle` | Requests |
| `requestsEmpty` | Nothing is waiting for you. |
| `actionNeededTitle` | Action needed |
| `reputationLowTag` | Low reputation |
| `reputationLowInfo` | Recently cancelled or missed several paid orders. |
| `reputationLowOrderConfirm` | This seller has a low reputation. Order anyway? |
| `reputationRatingBreakdown` | {stars} from buyers, {deduction} deducted for cancellations |
| `reputationCompletedOrders` | {completed} of {total} orders completed |
| `reputationMyTitle` | My reputation |
| `reputationMyPoints` | {points} penalty points |
| `reputationWarning` | At {threshold} points, buyers will see a Low reputation label. |
| `reputationRestricted` | You can't take new orders until {date}. Your current orders continue. |
| `reputationPointExpires` | Expires {date} |
| `reputationEventDeclined` | Declined a paid order |
| `reputationEventExpired` | Didn't respond in time |
| `reputationEventCancelled` | Cancelled after accepting |
| `reputationEventLateCancel` | Cancelled less than a day before pickup |
| `reputationEventTripCancelled` | Cancelled a trip with booked orders |
| `reputationEventNoShow` | Missed the pickup |
| `reputationEventOverdue` | Delivery overdue |
| `reputationAppeal` | Appeal |
| `reputationAppealHint` | Explain what happened. An admin will review it. |
| `reputationAppealSent` | Appeal sent. |
| `reputationAsOf` | As of {date} |
| `errorDeadlinePassed` | Too late. The time for this step has passed. |
| `errorReputationRestricted` | Your account can't take new orders until {date}. |
| `errorTripHasBookings` | This trip has booked orders, so its date, price and route can't change. |
| `errorCropHasActiveOrders` | This listing has open orders. Handle them first. |
| `errorTripNotEligible` | That truck is no longer available. Choose again. |
| `errorAppUpdateRequired` | Please update the app to continue. |
| `deliveryImHere` (P3) | I have arrived |
| `deliveryCropsReady` (P3) | Crops are ready |
| `deliveryShowDropoffQr` (P3) | Show delivery QR |
| `deliveryScanTruckerQr` (P3) | Scan the trucker's QR |
| `deliveryEnterCode` (P3) | Enter the 6-digit code |
| `deliveryArrivedBuyerNotice` (P3) | The trucker says the crops have arrived. Confirm or open a dispute before {deadline}. |

## 10. API contract

### 10.1 Sync commands (entityType `order`, action `update`)

| Command | Who | Allowed when | Effect |
|---|---|---|---|
| `withdraw` | buyer | `created`, `pending_payment`, `payment_failed` | `cancelled` (`buyer_withdrew`) |
| `farmerResponse: accept` | farmer | `funded`, before T3 | `accepted`, `awaiting_transport`, or stays `funded` |
| `farmerResponse: decline` (optional reason) | farmer | `funded`, before T3 | `rejected`, refund |
| `truckerResponse: accept` | the requested trucker | `funded`, or `awaiting_transport` with their open request, before T4 or T6 | booking confirmed; `accepted` if the farmer accepted |
| `truckerResponse: decline` | the requested trucker | same | transport failed |
| `transportChoice` | buyer | `awaiting_transport`, no open request | see 4.7 |
| `cancelCommitted` (reason, fresh sign-in) | farmer | `accepted`, `awaiting_transport` | `cancelled`, refund |
| `cancelCommitted` (reason, fresh sign-in) | trucker | `accepted`, on their own order | `awaiting_transport` |

### 10.2 REST

| Endpoint | Who | Purpose |
|---|---|---|
| `GET /reputation/{userId}` | buyers (for farmers and truckers), self, admin | tier, effective rating, rating count, reliability, completed orders |
| `GET /reputation/me/events` | self | events with reason, points, expiry and appeal state |
| `POST /reputation/events/{eventId}/appeal` | self, within 7 days | `{text, photoRef?}` |
| `GET /admin/orders/queue` | admin | stuck orders, by reason |
| `GET /admin/payouts/manual` and `POST /admin/payouts/{escrowId}/{ref}/complete` | admin | manual payout queue; the POST body is `{externalRef}` |
| `POST /admin/disputes/{id}/resolve` | admin | gains per-leg amounts `{farmerAmount, truckerAmount, buyerRefund}` |
| `GET /admin/reputation/appeals` and `POST /admin/reputation/events/{eventId}/decision` | admin | decision body `{excuse or uphold, note}` |
| `POST /admin/users/{id}/reputation` | admin | `{tag, untag, restrict or lift, reason, until}` |

### 10.3 DTO additions

- `OrderDto`: the fields in 4.4.
- `TripDto`: `bookedQuintals`, `remainingQuintals`, owner reputation fields.
- `CropDto`: owner reputation fields.
- `EscrowDto`: `legStatus`, `refundedTotal`, `releasedTotal`, and `truckerPayout` in the admin mapper (it is missing there today).
- New types: `ReputationSummaryDto`, `ReputationEventDto`, `AppealRequest`, and the admin request and response types.

All of them are registered in `AppJsonContext.cs`.

## 11. Testing

### Server (xUnit, NSubstitute, FluentAssertions)

- **State machine:** table-driven, covering every (status, command, actor) combination, allowed and refused, for all 3 roles.
- **Deadlines:** pure tests for T1 to T12, including the caps by pickup date and the lead-time rejections.
- **Sweep:**
  - each deadline kind
  - re-run idempotency
  - a CAS conflict with a concurrent user action
  - blocked rows leaving the index
- **Money:**
  - The ledger invariant holds after every path: full cancel, difference refund, self-pickup switch, partial verdict, trucker retry.
  - An unknown outcome is never retried without a status check, and a duplicate ref is rejected.
  - The lock condition includes `dispute_resolved`. Assert the real condition expression, not a mocked lock.
- **Live gateway:** release and refund never report success without a reference; the webhook funds an order exactly once.
- **Races:** two concurrent fundings on the last stock, and on the last trip capacity.
- **Reputation:**
  - points per event, caps, tiers
  - effective rating bounds
  - expiry and earn-back limits
  - excuse
  - deterministic event ids replayed twice
- **Gates:** a restricted party is blocked on create and accept, but their existing orders continue.
- **Streams:** the notify and reputation handlers, fed from recorded stream images (AOT-safe raw JSON).

### Mobile and web

- **Bloc tests:** each new event, for success and for each rejection code.
- **Widget tests:**
  - order detail actions for every status and role (farmer, trucker, buyer)
  - the transport-choice sheet
  - the deadline countdown
  - the reputation badge and the low-reputation confirm dialog
- **Repository tests:**
  - web envelope parsing of the new fields
  - Isar pull and conflict resolver with missing optional fields
  - an unknown status shows as "Updating" with no actions
- **Matching:** match scorer parity golden vectors with the new reliability source.
- **Builds:** `flutter analyze`, `flutter test`, and `flutter build web --no-tree-shake-icons`.

### Admin

- Vitest for the new pages.
- Admin e2e for the manual payout, verdict and appeal flows.

### End to end

- Extend the synthetic fleet with these personas:
  - a flaky trucker who declines, then cancels a trip
  - a no-show farmer (P3)
  - a silent buyer (P3)
  - a griefing buyer
  - the happy path with a replacement truck
- Run against dev after deploy with the three test users.

### Offline cases

- The trucker accepts offline and reconnects before the deadline: accepted.
- The trucker accepts offline and reconnects after it: rejected; the local order reverts, a message shows, and the queue does not get stuck.
- The buyer's transport choice is queued offline and the trip fills meanwhile: rejected, and the choice shows again.
- Both sides are offline at pickup: `deferred`, and it completes within the grace period.
- A reputation tag shows from the cache with its "as of" date.

## 12. Risks

| Risk | Mitigation |
|---|---|
| AOT | New DTOs go in `AppJsonContext`. The stream Lambdas parse raw stream JSON with `JsonDocument`, the pattern `CropMatchStreamFunction` already uses. `TransactWriteItems` is plain SDK. No reflection. |
| Cold start | New services are built lazily and only on write paths. Browse reads copied fields, with no extra queries. |
| DynamoDB cost | Two tables, three sparse indexes, and one stream with two consumers, all on-demand. Small at current volume. |
| Data usage on 3G | About 60 bytes of reputation per card. It removes the per-farmer rating GET on browse. Pushes are a few hundred bytes. |
| Fairness in low-connectivity areas | Generous deadlines, a 24h evidence grace, timeout points only for reachable users, appeals, expiry, earn-back and bulk excuse. Wording reviewed by native speakers. |
| Manual payout queue workload | Needed until the Telebirr B2B payout API is integrated. Alarms keep it from being forgotten. |
| Legal | Penalties, withheld refunds and the no-cancel rule need a terms-of-service update, checked against Ethiopian consumer-protection and NBE payment rules before launch. |
| Solo maintainability | Phases behind flags, pure policy helpers, table-driven tests, and logic moved out of `SyncFunction.cs`. |
| Rollout to old clients | K1: client support first, then a minimum app version gate. |
| Push language | Push text uses the server-side `preferredLanguage`, which is stale until SIM-37 is fixed. |

## 13. Phases and estimates

| Phase | Scope | Days (solo) |
|---|---|---|
| P0 Money safety | M1 to M13. Stopgap for M5: an `accepted` order with no pickup 48h after the trip departs (or 7 days after acceptance for self-pickup) counts as a transport failure, so the buyer can cancel for a full refund. Admin queues for stuck orders and manual payouts. | 5 to 8 |
| P1 Commitments | Fields; v2 state machine; trucker accept and decline; deadlines and the sweep; bookings and trip rules; listing guards; buyer cancel rules; `awaiting_transport` with the 3 choices (cheaper trucks, or pricier ones whose extra is covered by the commission, D4); ledger and partial refunds; notifications; UI for 3 roles on mobile and web; admin statuses and verdicts; contracts; l10n. | 9 to 12 |
| P2 Reputation | Events, policy, aggregate and stored ratings, tiers, effective rating, expiry and earn-back, copied owner fields, badges, My reputation and appeals, restriction gates, matching reliability, admin panels, FRD hooks. | 6 to 8 |
| P3 Evidence | Check-ins, drop-off QR, manual code, pickup judgement and no-show points, delivery overdue, silent-buyer auto-confirm, order delivery location. | 5 to 7 |
| P4 Extras (each optional) | Buyer top-up for trucks whose extra cost is more than the commission left, mutual cancel, weigh-in partial fulfilment, wasted-trip and no-show fees, bulk excuse. SMS fallback is dropped (D8). | 2 to 4 each |

P0 ships on its own and is needed before live money regardless of the rest. P1 and P2 can ship together or separately; P2 depends on P1's fields and stream.

## 14. Decisions (answered by the user on 2026-09-30)

| # | Question | Decision |
|---|---|---|
| D1 | May a buyer withdraw an order they have not paid for yet? | **Yes.** No money is held and nobody has committed. |
| D2 | Does declining a paid order cost a point? | **Yes**, 1 point, with the griefing caps. |
| D3 | How should the rating drop? | **Yes:** effective rating on cards (0.1 per point, at most 1.0), with the raw buyer average kept on the profile. |
| D4 | The replacement truck costs more than the transport money held | **Allowed when the extra is no more than the commission** the buyer paid. Read as: the platform covers the difference from the commission leg (`shift#commission-to-transport`), so the buyer pays nothing extra. Larger differences are not offered; a buyer top-up for them stays P4. Confirm this reading with the user when ESC-19 starts. |
| D5 | Money penalties: pay the trucker for a wasted trip when the farmer is absent; charge a self-pickup buyer who does not come | **Later** (P4). Points and refunds only until then. |
| D6 | Crop lost or damaged after a valid pickup | **Legal decides.** Until legal rules, an admin applies the E5 guidance case by case. |
| D7 | Mutual cancel (the buyer asks, the farmer and trucker agree, no penalty) | **Later** (P4). |
| D8 | Timeout points and SMS | **Yes:** timeout points only for users with a registered push device, and no SMS fallback. |
| D9 | Thresholds | **Yes:** low at 6, restricted at 10 for 14 days, 90-day window, all in SSM. |
