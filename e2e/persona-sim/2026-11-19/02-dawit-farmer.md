# 02 Dawit, farmer and crop broker (abuser)

- Account: `+251900192210`, userId `f08c49dc-9091-70f6-2d22-6d85f0763137`, display name `SIM-2026-11-19 Dawit`, Amharic.
- Second identity: `SIM-2026-11-19 Dawit sock buyer`, `+251900192535`, userId `507c995c-8071-7056-04d2-04f7a21ae439`, registered with the same `X-Device-Id` as Dawit.
- Device and network in the story: mid-range Android, good 4G, Debre Birhan market.
- How it was driven: direct API calls (`POST /sync`, REST) with his ID token. No UI. Times are real UTC on 2026-11-19; the "day" is the simulated day of the week.
- Evidence: every call is in `evidence/api-log.jsonl` (field `who` = `dawit`, `dsock`, `dawit2-samephone`).

| Day, time (UTC) | Place, network | Wants | Call (status) | What happened | Reaction | Tag |
|---|---|---|---|---|---|---|
| D1 08:38:06 | Shop, 4G | Register as a farmer | `POST /auth/fayda-start` 200, mock Fayda, `POST /auth/fayda-verify` 200, `POST /auth/register` 201 | Account created, phone confirmed from the Fayda claim, no SMS | Smooth | |
| D1 08:38:18 | Shop, 4G | A second farmer account on the same phone number | `POST /auth/fayda-verify` 409 `FAYDA_004` | "This Fayda ID is already linked to another account." | Blocked. Needs another person's ID | |
| D2 08:38:55 | Market, 4G | List the same 50 quintals of wheat at several prices to see which sells | 7 x `/sync crop_listing create` | 60, 75, 90, 110 ETB/kg: all `success`. 61, 62, 63: `FRAUD_DUPLICATE_LISTING` | "So I just need a bigger price gap." 200 quintals on the market from 50 real ones | [BUG-2] |
| D2 08:38:57 | Market, 4G | Absurd price to anchor buyers | `crop_listing create` pricePerKg 5000: `CROP_VALIDATION` | Refused, max 1000 | Blocked | |
| D2 08:40:52 | Market, 4G | Read and edit Abebe's teff by id | `GET /crops/{abebe}` 403 `CROP_FORBIDDEN`; update and delete: `CROP_UNAUTHORIZED`; create with `farmerId` = Abebe: `CROP_UNAUTHORIZED` | All refused, Abebe's row unchanged (price 95, v1) | Blocked | |
| D3 08:43:24 | Home, 4G | Buyer account for self-orders | Register `dsock` (same device id) 201 | Created. Nothing links the two accounts on one device | Easy | [BUG-3] |
| D3 08:43:25 | Home, 4G | Self-order 100 kg from wheat #1 | `order create` success, pay: `funded` (6,300 ETB mock) | "Self-trading not allowed" only compares user ids, so the sock passes | Works | [BUG-3] |
| D3 08:43:26 | Market, 4G | Raise the price after Kidus ordered wheat #2 at 75 | `crop_listing update` pricePerKg 140: success | Listing now 140. Kidus's order still pays 75 (order price is locked) | Annoyed, the order did not follow | |
| D3 08:43:35 | Market, 4G | Change price and quantity after escrow is funded | update price 300: success; quantity 0.01: success; delete: `CROP_HAS_ACTIVE_ORDERS`; status `sold`: success | Order untouched (7,875 ETB). Listing edits only affect new buyers | Blocked where it matters | |
| D3 08:43:38 | Market, 4G | Rewrite the order amounts | `order update` pricePerUnit: `immutable`; orderTotal 99999: `success` v3 to v4, value unchanged | Money field is dropped but the empty update still bumps the version and answers success | "It said success?" Confusing, no gain | [BUG-11] |
| D3 08:43:39 | Market, 4G | Mark the order delivered before any delivery | `order update` pickup_confirmed, in_transit, delivered, disbursing, settled | All `ORDER_VALIDATION` | Blocked | |
| D3 08:43:44 | Market, 4G | Ask for the payout now | `POST /escrow/release` (trigger delivery_confirmed, then admin_override) | 401 Unauthorized both times | Blocked | |
| D3 08:43:45 | Market, 4G | Forge the buyer's self-pickup delivery | `delivery create` self_pickup for Kidus's order | `DELIVERY_UNAUTHORIZED: buyerId must match authenticated user for self-pickup.` | Blocked | |
| D4 08:44:48 | Market, 4G | Overwrite Abebe's pickup QR on Tolosa's delivery | `delivery update` qrNonce | `DELIVERY_UNAUTHORIZED` | Blocked | |
| D5 08:47:35 | Market, 4G | Chat a buyer before any order | `message create` on crop thread | `MESSAGE_CONTACT_LOCKED` | Blocked | |
| D5 08:47:36 | Market, 4G | Push Kidus to pay off-platform on the accepted order | 2 x `message create` with "send 7000 birr to telebirr 0911223344" and a spelled-out number | Both `success`, Kidus reads them | Works. No phone or solicitation check in order chat | [BUG-14] |
| D5 08:47:37 | Home, 4G | Finish the self-trade for a rating | accept sock order, sock creates self-pickup delivery, Dawit stores nonce, sock confirms with farm GPS: all success; `rating create` 5 stars: success | Order `delivered`. Meron sees the 5-star rating on `GET /ratings?rateeId=dawit` | Fake reputation in 4 calls | [BUG-3] |
| D6 08:48:23 | Market, 4G | Dispute Kidus's accepted order to hold his money | `POST /escrow/dispute` 422 `ESCROW_041` | Only after pickup | Blocked | |
| D6 08:48:25 | Market, 4G | Keep listing after all of the above | `crop_listing create` barley: success | No hold after about 10 refused probes plus the self-trade | Still in business | [BUG-15] |

## What worked for Dawit
1. Four live listings for one stock, at 60, 75, 90, 110 ETB/kg. Two different buyers funded orders on two of them (`4ccb6d42` on #1, `d49a52f9` on #2): oversell across listings.
2. A second account on the same device bought his own wheat and left a public 5-star rating.
3. Off-platform payment request with a phone number in order chat.

## What did not work
Second account on the same phone number, reading or editing another farmer's crops, changing an order's price or total, marking delivered, requesting payout, forging a delivery, deleting a listing with a funded order.
