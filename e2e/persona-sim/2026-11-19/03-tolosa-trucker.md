# 03 Tolosa, trucker (honest)

- Account: `+251900195076`, userId `905cb9ac-4021-7013-306d-bb37fd63481d`, display name `SIM-2026-11-19 Tolosa`, Amharic.
- Device and network in the story: old Android, Amharic UI, Debre Birhan to Addis Ababa, no signal for hours on the road.
- How it was driven: API calls shaped like the phone's sync queue (same idempotency keys resent, queued operations with old timestamps flushed in one batch). His Amharic web UI was checked by the web agent (see `05-meron-buyer.md`, web section). No Android run for Tolosa.
- Evidence: `evidence/api-log.jsonl`, `who` = `tolosa`.

| Day, time (UTC) | Place, network | Wants | Call (status) | What happened | Reaction | Tag |
|---|---|---|---|---|---|---|
| D1 08:38:08 | Debre Birhan, 3G | Register | fayda-start 200, fayda-verify 200, register 201 | Account created | Fine | |
| D2 08:38:55 | Debre Birhan, 3G | List one trip to Addis, 40 quintals, 120 ETB/quintal, in 3 days | `/sync trip create` success v1 | Trip `ce17683c` open | Fine | |
| D2 08:40:50 | Debre Birhan, 3G | See his trips | `GET /trips` 200, 1 trip, all his | Only own trip. `?truckerId=henok` 403, Henok's trip by id 403, `GET /crops` 403 | Correct visibility | |
| D3 08:42:16 | Debre Birhan, 3G | Accept Meron's load (300 kg teff) | `order update` truckerResponse accept: success v5 | Order `accepted`, pickupDeadlineAt 2026-11-24T06:00Z | Fine | |
| D4 08:44:46 | Abebe's farm, 2G | Start the delivery | `delivery create` success | Delivery `e95ad512` pending | Fine | |
| D4 08:44:49 | Farm, 2G | Confirm pickup without scanning (camera struggles) | `delivery update` pickedUp, no nonce | `DELIVERY_NONCE_MISSING` | Has to scan or type the code. Correct | |
| D4 08:44:49 | Farm, 2G | Scan Abebe's QR | pickedUp + scannedQrNonce + gpsPickup: success v2 | Order moves to `pickup_confirmed` by itself | Fine | |
| D4 08:44:50 | Farm, 2G | Make a drop-off code | `dropoffQrNonce` without the `d.` prefix | `DELIVERY_VALIDATION: dropoffQrNonce must start with 'd.'` | Harness mistake, not an app bug. With `d.` prefix at 08:45:32: success | |
| D4 08:44:51 | Road, no signal for hours, then Addis edge 3G | Phone flushes the queue: inTransit plus 3 location fixes captured earlier | one `/sync` batch of 4 ops with old timestamps | All 4 success, delivery `inTransit` v3, order `in_transit` | Fine | |
| D4 08:44:51 | Addis edge, 3G drops | The same batch goes again (response lost) | same 4 ops, same keys | 4 x `duplicate`, nothing written twice | Correct | |
| D5 08:45:33 | Merkato, 4G | Confirm delivery himself to save time | delivered by trucker | `DELIVERY_UNAUTHORIZED: Only the buyer can confirm delivery.` | Correct | |
| D5 08:45:34 | Merkato, 4G | Meron scans his drop-off QR | (Meron) delivered: success v4 | Delivery `delivered`, order `delivered` | Fine | |
| D5 08:45:39 | Merkato, 4G | See what he will be paid | `GET /escrow/history?role=trucker` 200 | truckerPayout 360 ETB, escrow `escrowed`, releaseNotBefore 2026-11-22T08:45Z | Has to wait 3 days. The date is shown | |
| D5 08:47:33 | Addis | Gets rated | (Meron) rating 5 stars: success | | | |

## Result
The honest trucker path works end to end over the API, including an offline batch and a full replay: no duplicates, no lost operations. Payout itself was not observed (release is not before 2026-11-22, outside this run).

## Problems Tolosa would hit
- Amharic date picker for the departure date is a Gregorian grid (code review, `mobile/lib/features/trip_listing/presentation/screens/create_trip_screen.dart:235`). A wrong day is costly. [BUG-L4]
- Amharic "Cancel" and "Delete" are the same word, "ሰርዝ". [BUG-L1]
- Another trucker listed the same route on the same morning at 3 prices, which crowds his single honest listing. [BUG-4]
