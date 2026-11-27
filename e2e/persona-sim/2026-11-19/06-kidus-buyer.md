# 06 Kidus, buyer (abuser)

- Account: `+251900199815`, userId `f02c39fc-a031-7019-e489-26032bf64b5e`, display name `SIM-2026-11-19 Kidus`, Amharic.
- Device and network in the story: Android plus a laptop with curl, Addis Ababa, 4G.
- How it was driven: direct API calls with his ID token. Payments go through dev's mock Telebirr (auto-fund). Times are real UTC on 2026-11-19.
- Evidence: `evidence/api-log.jsonl`, `who` = `kidus`.

| Day, time (UTC) | Place, network | Wants | Call (status) | What happened | Reaction | Tag |
|---|---|---|---|---|---|---|
| D1 08:38:15 | Addis, 4G | Register | fayda-start, fayda-verify, register 201 | Account created | | |
| D2 08:40:55 | Addis, 4G | Create a crop and a trip as a buyer | `crop_listing create`: `CROP_UNAUTHORIZED: Only farmers...`; `trip create`: `TRIP_UNAUTHORIZED: Only truckers...`; crop with farmerId = Abebe: `CROP_UNAUTHORIZED` | All refused | Blocked | |
| D3 08:41:35 | Addis, 4G | His own price: teff at 1 ETB/kg | `order create` pricePerUnit 1 | `ORDER_VALIDATION: pricePerUnit must equal the listing price (95.00 ETB/kg).` | Blocked | |
| D3 08:41:35 | Addis, 4G | Send his own totals | orderTotal 1, cropCost 1, commission 0 | `success`, but stored cropCost 4750, commission 237.5, total 4987.5 | Ignored, server computes | |
| D3 08:41:36 | Addis, 4G | Order more than the stock; negative quantity | 5000 kg: `ORDER_VALIDATION` (stock 1000 kg); -5: `ORDER_VALIDATION` | Refused | Blocked | |
| D3 08:41:36 | Addis, 4G | Tiny quantity | quantity 0.001 kg | `success`, stored 0.001 kg, total 0.11 ETB. Contract minimum is 0.01 | Junk order accepted | [BUG-10] |
| D3 08:41:36 | Addis, 4G | Free transport; order in Meron's name; start as funded; start as farmer-accepted | transportCost 0: `ORDER_VALIDATION`; buyerId = Meron: `ORDER_UNAUTHORIZED`; status funded: stored as `created`; farmerResponse: `ORDER_VALIDATION` | None works | Blocked | |
| D3 08:42:07 | Addis, 4G | Set his order to funded, accepted, delivered, settled, disputed, payment_failed | 6 x `order update` | All `ORDER_VALIDATION: Invalid status transition` | Blocked | |
| D3 08:42:10 | Addis, 4G | Change quantity and price after create | `immutable` twice | | Blocked | |
| D3 08:42:11 | Addis, 4G | Read, cancel, dispute, refund Meron's order | `GET /orders/{id}` 403; cancel `ORDER_UNAUTHORIZED`; `GET /escrow/{id}` 403 `ESCROW_022`; dispute 403 `ESCROW_019`; refund 401 | All refused. His `GET /orders` and `/escrow/history` list only his own | Blocked | |
| D3 08:42:14 | Laptop | Forge a Telebirr callback | `POST /webhooks/telebirr` unsigned | 404 Not Found (route is not deployed while dev runs the mock gateway) | Blocked here. The signed live path was not testable | |
| D3 08:42:14 | Laptop | Fund with his own amount | `POST /escrow/initiate` amount 1 | 400 `ESCROW_002` deliveryId required | Blocked | |
| D3 08:42:48 | Addis, 4G | Race for the last 700 kg of Abebe's teff with two orders paid at once | 2 x `order create` 700 kg; Fayda step-up (`AUTH_041`); 2 concurrent `payment_submitted` | Both payments `success`. K2 `funded`; K1 `cancelled`, `listing_out_of_stock`, refund armed. Stock 0, listing `reserved` | One winner only. Correct | |
| D3 08:42:53 | Addis, 4G | Triple-submit one payment | 3 concurrent `payment_submitted` on the 50 kg order | 1 `success`, 2 `duplicate`. One escrow of 4,987.5 | Blocked | |
| D3 08:42:53 | Addis, 4G | (side effect) pay an order whose listing just sold out | same call | Payment accepted, order then `cancelled` `listing_out_of_stock`, escrow `escrowed` with refund armed (hourly sweep) | "It took my money for something that was already gone." | [BUG-9] |
| D3 08:43:29 | Addis, 4G | Pay Dawit's wheat #2 after Dawit raised the price | `payment_submitted` | `funded` at the ordered 75 ETB/kg, total 7,875 | Fair to the buyer | |
| D4 08:44:48 | Addis, 4G | Confirm delivery on Meron's delivery | `GET /deliveries/{id}` 403; delivered (pending): `DELIVERY_INVALID_TRANSITION: Cannot transition from 'pending' to 'delivered'`; delivered (in transit): `DELIVERY_UNAUTHORIZED` | Refused. The first answer tells a stranger the delivery's status | Blocked, small leak | [BUG-12] |
| D4 08:46:03 | Addis, 4G | Book a Debre Birhan crop on Henok's Dire Dawa to Mekelle trip | `order create` with that trip | `success` | Route is not checked against the listing | [BUG-7] |
| D4 08:46:47 | Addis, 4G | Cancel after pickup for a refund | order cancelled: `ORDER_VALIDATION`; delivery cancelled: `DELIVERY_CANCEL_NOT_ALLOWED` | Refused | Blocked | |
| D4 08:46:48 | Addis, 4G | Claim "not delivered" 30 seconds after pickup | `POST /escrow/dispute` not_delivered | 200, escrow `disputed`. Order stays `pickup_confirmed` | Money frozen while the truck is still driving | [BUG-8] |
| D5 08:47:34 | Addis, 4G | Rating abuse | rate Abebe on Meron's order: `RATING_FORBIDDEN`; rate an undelivered order: `RATING_FORBIDDEN`; 6 stars: `RATING_VALIDATION` | Refused | Blocked | |
| D5 08:47:42 | Addis (not at the farm) | Confirm self-pickup of K2 (700 kg teff, 69,825 ETB) without the farmer | set qrNonce himself: `DELIVERY_UNAUTHORIZED`; confirm with own nonce: `deferred`; real nonce from Addis: `self_pickup_far` 112.7 km; no GPS: `self_pickup_no_fix` | All refused | Blocked. Needs the farmer's QR and a GPS claim near the farm | |
| D5 08:47:45 | "At the farm" | Confirm properly | delivered + nonce + farm GPS: success | Delivery and order `delivered` | | |
| D6 08:48:16 | Addis, 4G | Cancel the delivered order | `ORDER_VALIDATION` | Refused | Blocked | |
| D6 08:48:18 | Addis, 4G | Claim "not received" after confirming by QR and GPS | `POST /escrow/dispute` not_delivered | 200. Escrow, delivery and order all `disputed`. Abebe's 66,500 ETB payout frozen until an admin decides. Second dispute: 409 `ESCROW_020` | Costs him nothing. The honest farmer waits | [BUG-6] |
| D6 08:48:19 | Addis, 4G | 1 star for Abebe on the disputed order | `RATING_FORBIDDEN` | Correct: disputed orders cannot be rated | Blocked | |
| D6 08:48:22 | Addis, 4G | Confirm Henok's delivery although he disputed it mid-transit | delivered + drop-off nonce: success | Delivery `delivered`, order `delivered`, escrow still `disputed`, `deliveryConfirmedAt` null | Inconsistent state | [BUG-1] |
| D6 08:48:42 | Addis, 4G | Rate Dawit 1 star on that order | `rating create`: success | Rating stored while the money is in dispute. Contract says ratings are blocked while disputed | Works | [BUG-1] |
| D6 08:48:25 | Addis, 4G | Keep ordering | `order create`: success | No hold after about 20 refused probes and 2 disputes on 3 paid orders | | [BUG-15] |

## What worked for Kidus
1. Confirm-then-dispute: 69,825 ETB of an honest farmer's money frozen with one call after a QR and GPS confirmed handoff.
2. Dispute during transit, then confirm, then rate: order `delivered`, escrow `disputed`, rating accepted.
3. Order on a trip that does not pass the farm.
4. A 0.001 kg order.

## What did not work
Creating listings or trips, own price or totals, ordering above stock, winning the stock race twice, double payment, forged callback, cancel after pickup, reading or touching other buyers' orders, deliveries and escrows, rating orders he does not own.

## Not tested
A second Kidus account (the registration cap of 10 per hour per IP was nearly used; the multi-account path is shown by Dawit's sock). A signed Telebirr callback replay (needs the live gateway). Read-back of his fraud signals and risk tier: the admin read was not permitted in this session.
