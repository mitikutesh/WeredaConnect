# 04 Henok, trucker (abuser)

- Account: `+251900194686`, userId `40dc293c-b001-70d0-caf4-637d0ebc3136`, display name `SIM-2026-11-19 Henok`, English.
- Device in the brief: iPhone (iOS Simulator). **Not possible:** the repo has no `mobile/ios` folder, so there is no iOS build. An iPhone user can only use the web app. [BUG-L17]
- How it was driven: direct API calls with his ID token. Times are real UTC on 2026-11-19.
- Evidence: `evidence/api-log.jsonl`, `who` = `henok`.

| Day, time (UTC) | Place, network | Wants | Call (status) | What happened | Reaction | Tag |
|---|---|---|---|---|---|---|
| D1 08:38:11 | Addis, 4G | Register | fayda-start, fayda-verify, register 201 | Account created | | |
| D2 08:38:58 | Addis, 4G | Same route, same morning, three prices | 3 x `trip create` Debre Birhan to Addis, 2026-11-22 06:00Z, 100 / 150 / 200 ETB per quintal | All `success` | Buyers see three "different" trucks | [BUG-4] |
| D2 08:38:58 | Addis, 4G | A trip that cannot physically happen with the others | `trip create` Dire Dawa to Mekelle, same morning | `success` | One truck, four trips, two regions | [BUG-4] |
| D2 08:38:59 | Addis, 4G | Silly values | capacity 5000: `TRIP_VALIDATION`; departure in the past: `TRIP_VALIDATION` | Refused | Blocked | |
| D2 08:40:54 | Addis, 4G | See and edit Tolosa's trip | `GET /trips` shows only his 4; Tolosa's by id 403; update price and cancel: `TRIP_UNAUTHORIZED` | Refused, Tolosa's trip unchanged | Blocked | |
| D4 08:44:47 | Addis, 4G | Hijack Tolosa's delivery | `GET /deliveries/{id}` 403; pickedUp with a forged nonce: `DELIVERY_UNAUTHORIZED` | Refused | Blocked | |
| D4 08:46:04 | Addis, 4G | Raise the price after Kidus ordered at 100, before he pays | `trip update` pricePerQuintal 900: success | Trip now 900. Kidus's order stays at 100 (transportCost 100 for 1 quintal) | No gain, but the edit is allowed | |
| D4 08:46:08 | Addis, 4G | Accept the load | truckerResponse accept: success | Order `accepted`, trip bookedQuintals 1 | | |
| D4 08:46:09 | Addis, 4G | Raise price and cut capacity after booking | price 2000, capacity 0.1 | Both `TRIP_HAS_BOOKINGS` | Blocked | |
| D4 08:46:10 | Addis, 4G | Edit the order's transport cost; accept Meron's order | `ORDER_UNAUTHORIZED` both | | Blocked | |
| D4 08:46:11 | Addis (not at the farm) | Write the farmer's QR nonce himself, then scan it | `delivery update` qrNonce: `success` v1 to v2 (value silently dropped); pickedUp with that nonce: `deferred DELIVERY_NONCE_PENDING` | His nonce is not stored. "success" is misleading | Blocked, but confusing | [BUG-11] |
| D4 08:46:12 | Addis | Reuse the pickup QR from Tolosa's delivery | pickedUp with that nonce: `deferred` | Not accepted | Blocked | |
| D4 08:46:13 | Addis | Guess the 9-digit typed code, 6 tries | 6 x `deferred DELIVERY_NONCE_PENDING` (farmer nonce not stored yet) | No hit. Guesses made before the farmer's nonce exists are not counted | Blocked | |
| D4 08:46:16 | Addis, 112 km from the farm | Pickup with the real QR (Dawit sent a photo), honest GPS | pickedUp + nonce + gpsPickup Addis | `DELIVERY_GPS_PROXIMITY: pickup_far: Pickup GPS is 112.7km from listing location (max 5km).` | Blocked | |
| D4 08:46:17 | Addis, 112 km from the farm | Same call, location switched off | pickedUp + nonce, no gpsPickup | `success`, delivery `pickedUp`, order `pickup_confirmed` | "Turn GPS off and it goes through." | [BUG-5] |
| D4 08:46:48 | Road | Drop out after pickup | truckerResponse decline: `ORDER_VALIDATION`; trip cancel and delete: `TRIP_HAS_BOOKINGS`; delivery cancel and delete: `DELIVERY_CANCEL_NOT_ALLOWED` | All refused | Blocked | |
| D4 08:46:51 | Road | Mark it delivered himself | delivery delivered: `DELIVERY_UNAUTHORIZED`; order delivered: `ORDER_VALIDATION` | Refused | Blocked | |
| D4 08:46:52 | Road | Fake location: mocked fix, then 100 km in 20 s | 2 x `location_ping create`: success | Accepted (flag only by design). The teleport fix moved the delivery to `inTransit` by itself | Works, the track shows a fake position | [BUG-5] |
| D4 08:46:52 | Road | Timestamps 3 days ahead and 30 days old | `LOCATION_STALE` both | Refused | Blocked | |
| D4 08:46:52 | Road | Ping on Tolosa's trip | `LOCATION_NOT_TRUCKER` | Refused | Blocked | |
| D4 08:46:52 | Road | Idempotency key carrying Tolosa's user id, op timestamp 2031 | inTransit: `conflict` "already in transit" | Key is scoped to the caller, timestamp ignored | Blocked | |
| D4 08:46:53 | Road | Same key, different payload; go backwards inTransit to pickedUp | `SYNC_VALIDATION`; `DELIVERY_INVALID_TRANSITION` | Refused | Blocked | |
| D6 08:48:21 | Addis | Hand over although the buyer disputed mid-transit | dropoffQrNonce success; Kidus confirms: success | Delivery and order `delivered`, escrow stays `disputed` | State no longer agrees | [BUG-1] |
| D6 08:48:26 | Addis | Keep listing | `trip create`: success | No hold | | [BUG-15] |

## What worked for Henok
1. Four trips for one truck on one morning, three of them the same route at different prices.
2. Pickup confirmed 112 km from the farm by leaving the GPS fix out (he still needs the farmer's QR, so it takes a colluding farmer or a forwarded QR photo).
3. Mocked and teleporting location fixes are stored and drive the live track.

## What did not work
Seeing or editing other truckers' trips, changing price or capacity after a booking, cancelling after pickup, confirming delivery himself, forging or reusing a QR, replaying or reordering sync operations, tampering with timestamps or idempotency keys.

## Not tested
Any iPhone screen (no iOS build). A second paid load on an overlapping trip (only the listings overlap in this run).
