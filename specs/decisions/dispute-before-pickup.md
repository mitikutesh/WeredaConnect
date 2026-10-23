# Dispute before pickup: refused (FUP-16)

Status: implemented on the lead's recommendation, 2026-10-22, pending the user's confirmation.
Contracts: `escrow-protocol.md` 1.21.0 (`ESCROW_041`), `delivery-verification-api.md` 1.16.0.

## Question

ESC-03 (#119) left this open: should `POST /escrow/dispute` be refused before the delivery is
confirmed? A dispute without `deliveryConfirmedAt` had no window check, so a buyer or a farmer could
dispute an order whose goods had never left the farm.

## Decision

A dispute is accepted from pickup onward and refused before it, with `422 ESCROW_041`:

- accepted when the escrow's `deliveryConfirmedAt` is set, or the order is at `pickup_confirmed` or
  later, or its active delivery is `pickedUp` or later;
- refused when the order is still at payment, `funded`, `accepted`, `cancelled` or `rejected` and its
  delivery (if any) is `pending` or `cancelled`;
- allowed, as before, when the escrow names no order and no delivery that can be read;
- a failed read answers `503 ESCROW_012` and writes nothing.

The 48-hour window after `deliveryConfirmedAt` is unchanged. The delivery dispute through `POST /sync`
already starts only from `delivered`, so it needs no change.

## Why

- Before pickup nothing has moved. The order has its own exits: the farmer rejects a funded order, the
  buyer cancels a funded order, and after a missed pickup deadline the buyer cancels with a refund
  (transport failure, ESC-06). Each of them refunds without an admin.
- A dispute there only freezes the money and leaves the admin a verdict with nothing to settle: a
  `farmer_wins` before pickup can only wait for a delivery (paying the farmer for goods still at the
  farm is not allowed), and a `buyer_wins` does what the cancel path already does.
- From pickup onward the goods are in someone else's hands, and the admin now has a disposition for
  every case (`return_to_farmer`, `delivered_anyway`, `settle_now`, `wait_for_delivery`).

## Alternatives not taken

- Refuse every dispute before delivery confirmation (the strict reading of "48 hours after delivery").
  Rejected: once the goods are on a truck, a buyer or farmer who sees them go wrong (a trucker who
  stops, goods diverted) needs a way to freeze the money before the auto-release.
- Keep accepting disputes at any time. Rejected for the reasons above.

## If the user disagrees

The gate is one condition in `EscrowService.DisputeAsync` (step 11, `RefuseDisputeBeforePickupAsync`).
Removing that `if` restores the old behaviour; the app keeps a localized message for `ESCROW_041`, which
the server then never sends. The contract rows for `ESCROW_041` would go with it.
