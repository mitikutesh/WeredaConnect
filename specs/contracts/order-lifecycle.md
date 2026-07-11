# Order Lifecycle Contract (Status Levels & Escrow Semantics)

## Version
1.0.0

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
| 6 | `pickup_confirmed` | Pickup from the farmer confirmed (QR scan; trucker delivery or buyer self-pickup). | In escrow. | Buyer or farmer (participants) |
| 7 | `in_transit` | Crops en route to the buyer. | In escrow. | Participants |
| 8 | `delivered` | Delivery confirmed by the buyer. Starts the dispute window (see `escrow-protocol.md`: 48h; auto-release after it). | In escrow, release pending. | Participants |
| 9 | `disbursing` | Escrow release to the farmer's account is in progress (minus commission). | Leaving escrow. | **System only** |
| 10 | `settled` | Farmer received the money. Order complete. | Paid out. Terminal. | **System only** |
| — | `cancelled` | Order cancelled (buyer action, or auto-cancel — see below). Escrowed money, if any, refunds to the buyer. | Refunded/none. Terminal. | Buyer / system cron |
| — | `rejected` | Farmer declined the funded order; escrow refunds to the buyer. | Refunded. Terminal. | Farmer |
| — | `payment_failed` | Payment could not be verified. Buyer may retry (`→ pending_payment`) or cancel. | No confirmed money. | System |
| — | `disputed` | Dispute raised within the window; escrow keeps holding the money. | Frozen in escrow. | Buyer (via escrow API) |
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
- **Either participant**: `pickup_confirmed`, `in_transit`, `delivered`.

---

## Side Effects & Automation

| Trigger | Effect | Where |
|---|---|---|
| `→ funded` | Crop listing quantity decreases by the ordered amount; at 0 the listing becomes `reserved` (off the market). Best-effort — failure logs a warning, never blocks the order. | `TryDecreaseListingQuantityAsync`, `SyncFunction.cs` |
| `payment_submitted` + `TELEBIRR_MOCK=true` | Server auto-advances to `funded` as a system actor (audited as `payment_verified`). Never active in prod / live gateway. | `TryAutoFundMockOrderAsync` |
| `created`/`pending_payment` older than 24h | Auto-cancelled by cron. | `OrderCronFunction.cs` |
| `delivered` + 48h no dispute | Escrow auto-releases to farmer (`disbursing → settled`). | escrow cron, `escrow-protocol.md` |
| escrowed + 7 days no delivery | Escrow refunds the buyer. | `escrow-protocol.md` |
| every applied transition | Append-only audit row (who/when/prior→new). | `EmitTxAuditAsync` → `transaction-audit` table |

### Known gap (deliberate, tracked)

Two orders funding concurrently can together exceed the remaining listing
quantity — the decrement clamps at 0 but both orders stay funded (oversell
risk). A quantity guard at funding time is a candidate follow-up; do not
"fix" this casually — it needs an idempotent conditional update.

---

## Invariants

1. Money-state transitions (`funded`, `disbursing`, `settled`, `dispute_resolved`) are **never** accepted from a client — they come from the gateway callback, escrow cron, or admin resolution.
2. A funded order always reserves listing stock; cancelling/rejecting a funded order refunds the buyer (escrow protocol), it does not silently drop money.
3. The mobile timeline (`_StatusTimeline` in `order_detail_screen.dart`) must render every status in this table with its `*Info` tooltip; new statuses require: this doc + `OrderTransitions` + wire mapping + timeline step + 3× l10n.
4. Wire statuses are snake_case for orders only.

## Changelog

- **1.0.0 (2026-07-11)** — Initial version, extracted from `SyncFunction.cs` / `OrderCronFunction.cs` / `escrow-protocol.md` behavior while adding the timeline step tooltips.
