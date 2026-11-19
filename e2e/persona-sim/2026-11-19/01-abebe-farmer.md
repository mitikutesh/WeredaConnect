# 01 Abebe, farmer (honest)

- Account: `+251900198331`, userId `102c398c-00e1-7014-222f-d3b9d96b9271`, display name `SIM-2026-11-19 Abebe`, Afan Oromo.
- Device and network in the story: cheap Android, 2 GB RAM, weak 2G/3G near Debre Birhan, often offline. Reads slowly, relies on icons.
- How it was driven:
  - **Android emulator** (AVD `Medium_Phone_API_36.1`, debug APK built against dev, package `et.wereda.connect`), driven with adb taps, network throttled with `adb emu network speed/delay`, offline with airplane mode. Screenshots `screenshots/android-01` to `android-53`. Not a real 2 GB Android 8 device.
  - **API** for his first two listings and his order answers (`evidence/api-log.jsonl`, `who` = `abebe`).
  - **Web** check of his Afan Oromo UI: see `05-meron-buyer.md`, and `video/web-abebe-farmer-view.webm` (55 s).
- Times are real UTC on 2026-11-19; the "day" is the simulated day.

## Diary

| Day, time (UTC) | Place, network | Wants | Tap or call (status) | What happened | Reaction | Tag |
|---|---|---|---|---|---|---|
| D1 08:37:41 | Town, 3G | Register | fayda-start 200, mock Fayda, fayda-verify 200, register 201 | Account created, no SMS needed | Fine | |
| D1 08:45 | Farm, 2G, Android | Open the app | first launch | Splash and first screen are fully English, no language choice before "Get Started". Language switch is on the login screen | Cannot read the first screen | [BUG-40] |
| D1 08:47 | Farm, GSM-speed | Log in | login, twice | 35 s spinner each time, then "Haalata offline dandeessisuuf dursa online seenaa" (log in online first to enable offline mode). He is online, just slow | "It says I am offline. I am not." | [BUG-35] |
| D1 08:50 | Farm, EDGE | Log in again | login | Success in about 6 s | | |
| D2 08:38:54 | API | List 10 quintals of teff at 95 Birr/kg | `/sync crop_listing create` success v1 | Listing `8377f01f` | | |
| D2 08:38:54 | API, signal drops | Phone resends the same queued create | same idempotency key | `duplicate`, nothing written twice | Correct | |
| D2 08:38:54 | API | Same listing id sent with a new key (a re-queued create) | `crop_listing create` | `FRAUD_DUPLICATE_LISTING: A similar listing already exists.` instead of a conflict | An honest retry is answered as fraud | [BUG-13] |
| D2 08:38:55 | API | List 20 quintals of maize at 28 Birr/kg | create success | Listing `d0740ebc` | | |
| D2 08:40:45 | API | See his crops | `GET /crops` 200: 2 rows, both his. `?farmerId=dawit` 403, Dawit's crop by id 403, `GET /trips` 403 | Only his own | Correct | |
| D2 08:50 | Farm, EDGE, Android | See his crops on the phone | My Crops | "Tarreen midhaan amma hin jiru" (no listings) while the server has teff and maize. Dashboard "Midhaan Koo 0", sync chip says synced. They never appear, to the end of the run | "Where are my crops?" | [BUG-31] |
| D2 08:50 | Android | Read the dashboard | home | "Ajaja Eeggataa 0" while the Orders tab lists orders. Correct count only at 09:05 | Confusing | [BUG-37] |
| D2 08:54 | Farm, Android | Start a third listing (barley) | Create form | Two English Android system dialogs (location permission, Google Location Accuracy) before the form works. Nine crop tiles are words only | Taps blindly | [BUG-L16] |
| D2 08:54:57 | Field, no signal | Fill the form offline | barley, 1 quintal, 40 Birr/kg, 2 gallery photos | Location suggestions sit behind the keyboard, only half of "Bishooftuu" (wrong town) visible. 2 photos mandatory, 4 taps through an English photo picker | Slow, nearly picks the wrong town | [BUG-38] |
| D2 08:57:24 | Field, no signal | Save | Save | Snackbar "saved on phone, sends when network is there", amber chip "1 walsimsisuuf eeggataa jiru". Good. But the listing card says "Ka'aa Jiru" (available) and the info box says buyers can see it | Thinks buyers already see it | [BUG-36] |
| D2 08:58 | Field, no signal | Look at orders offline | Orders, order detail | Open from cache | Fine | |
| D2 08:59:15 | Field, then EDGE | Phone dies, signal returns, he reopens | force-stop offline, network on, reopen 08:59:24 | Session kept, chip still "1 waiting". Log `[SYNC] health check: false` at 08:59:54, then no further attempt for 2 minutes | Listing is not going up and nothing tells him | [BUG-32] |
| D2 09:01:57 | Farm, EDGE | Tap "Amma Walsimsisi" (sync now) | manual sync | Both photo uploads (95 KB, 44 KB) time out at 30 s. Sheet says "Walsimsiisuun Xumurameera" (sync finished) with 1 still waiting, 0 failed | "Finished? It is still waiting." | [BUG-33] [BUG-34] |
| D2 09:03:47 | Farm, UMTS throttle | Again | manual sync | Same timeouts | | [BUG-33] |
| D2 09:05:14 | Town, good signal | Again | manual sync | `success (1/1 ok)` in 2 s. Server has barley `fe202ab1` exactly once | Works only with good signal | |
| D2 09:06 | Field, no signal | Fourth listing, sorghum, offline | create | Saved on phone | | |
| D2 09:08:20 | EDGE | Signal back | automatic | `health check: false` at 09:08:46, nothing for 2 minutes | Same as before | [BUG-32] |
| D2 09:11:11 | EDGE | App killed during the photo upload | force-stop mid-sync, reopen on good signal 09:11:21 | `success (1/1 ok)`. Server has sorghum `b6d9e517` exactly once | No duplicate, no loss | |
| D2 09:15 | EDGE | Edit barley price offline (40 to 41), kill about 1 s into the sync | force-stop with the update in flight | Server already at v2, price 41. Reopen 09:17:44: `recovered 1 stale syncing ops`, `success`. Server still v2 | No double apply | |
| D3 08:42:15 | API | Accept Meron's teff order (300 kg) | `order update` farmerResponse accept: success | Order waits for the trucker, then `accepted` | Fine | |
| D4 08:44:46 | Farm | Show the pickup QR to Tolosa | `delivery update` qrNonce: success | Tolosa scans, order `pickup_confirmed` | Fine | |
| D5 08:45:38 | Farm | See what he will get | `GET /escrow/{id}` | farmerPayout 28,500 ETB, `escrowed`, disputeWindowEndsAt 2026-11-21, releaseNotBefore 2026-11-22 | Has to wait 3 days. Dates on screen are English Gregorian ("Nov 19, 2026") | [BUG-L2] |
| D5 08:47:42 | Farm | Accept Kidus's 700 kg order, hand over, show QR | accept success, qrNonce success, Kidus confirms at the farm | Order `delivered`, 66,500 ETB due to him | Happy | |
| D6 08:48:18 | Farm | Wait for his money | (Kidus) `POST /escrow/dispute` not_delivered: 200 | Order, delivery and escrow `disputed`. His payout is frozen until an admin decides, although the handoff was confirmed by QR and GPS | Angry and helpless | [BUG-6] |

## Exactly-once verdict (Android)
- Offline create, app killed offline, reconnect: PASS. On the server once, nothing lost.
- Kill during photo upload: PASS. Kill with an update request in flight: PASS, no double apply.
- Not achieved: a kill exactly between the photo upload and the create request of a new listing.
- Final `GET /crops?farmerId=` at 09:18:16: 4 listings, each id once.

## But the listing does not get there by itself
Both offline listings reached the server only after a manual "sync now" on an unthrottled network. After reconnect the automatic sync failed its health check and did not try again within 2 minutes (the only automatic retry found in code is a 15 minute timer, `mobile/lib/core/sync/sync_worker.dart:42`; the 15 minutes were not waited out). On emulated EDGE and UMTS the photo upload timed out every time (`mobile/lib/features/crop_listing/.../crop_photo_uploader.dart:42`, 30 s receive timeout). Caveat: the emulator's shaper may be harsher than a real 2G/3G SIM.

## Low-literacy notes (Android, Afan Oromo)
- Good: offline banner with a cloud-off icon, amber dashboard chip with a count, plain "saved on phone" message.
- Order status chips "Uumame" and "Haqame" (cancelled) use the same grey circle icon. Order timeline is four text labels with no icons. [BUG-L5]
- Units: quantity in "kuuntaalii", price in "Birrii/kg", orders in "kg"; quintal spelled three ways. [BUG-L11]
- "Offline" left in English inside Oromo text; profile shows "100,040 ETB" while the rest says "Birrii"; payments list shows raw ids ("Ajaja #64ad1f63"). [BUG-25]
- "Qabamee" and "Gurguramee Jira" tabs show a search empty state ("no listing matches your search") although there is no search. [BUG-39]
- Floating "Midhaan Gurguri" button covers the last order card's status chip. [BUG-27]

## Role visibility
PASS on every screen reached on Android and web, and over the API. No other farmer's listing appeared anywhere.

## Listings left on dev
`8377f01f` teff (reserved), `d0740ebc` maize, `fe202ab1` barley (offline test), `b6d9e517` sorghum (offline test).

## Not tested
A real 2 GB Android 8 device, camera capture, voice note, accepting or rejecting an order in the Android UI, wrong-password and validation error texts, the 15 minute retry timer.
