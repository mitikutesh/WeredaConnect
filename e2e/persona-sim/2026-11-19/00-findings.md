# Persona simulation 2026-11-19: findings

Six personas, one simulated marketplace week, dev only (`https://vhhm8gh9c1.execute-api.eu-north-1.amazonaws.com`, `https://dev-app.weredaconnect.com`). Mock Telebirr, mock Fayda. Nothing in the repo was changed apart from this folder.

## Verdict
- **Critical: none found.** No way to take money was found, and role visibility held on every path tried (API, web, Android): farmers and truckers see only their own, the buyer sees all.
- **High: 10.** Three break the honest core flow (Android sync after reconnect, own listings missing on a fresh install, blank page after paying on web). Seven are abuses that work.
- The server refused about 90 abuse attempts. The list of what held is at the end.

## How each persona was driven

| Persona | Real UI | API | Not possible |
|---|---|---|---|
| Abebe | Android emulator (om), web (om) | yes | real 2 GB Android 8 device |
| Dawit | no | yes | |
| Tolosa | web (am) | yes, sync-queue shaped | Android |
| Henok | no | yes | iPhone: the repo has no iOS build |
| Meron | web (en, am, om), video | yes | Android |
| Kidus | no | yes | |

The week was compressed into about 45 minutes of real time (08:37 to 09:18 UTC), so anything that needs real waiting was not observed: payout after the 48 hour window, the hourly refund sweep, the 24 hour deadlines.

## Evidence
- `evidence/api-log.jsonl`: every API call of the six personas (386 calls, tokens and passwords redacted). `who` is the persona.
- `evidence/day*.py`, `simlib.py`: the scripts that made the calls.
- `screenshots/web-*.png`, `screenshots/android-*.png`.
- `video/web-meron-order-flow.webm` (97 s), `video/web-abebe-farmer-view.webm` (55 s).
- Diaries `01` to `06`.

## Findings

Status: Confirmed = reproduced in this run. Suspected = seen once or inferred from code.

### High

| ID | Persona | Step | Expected | Actual | Evidence | Status | Known / New | Fix area |
|---|---|---|---|---|---|---|---|---|
| BUG-32 | Abebe | D2, reconnect after offline create (Android) | Pending listing uploads soon after signal returns | `[SYNC] health check: false` once, then no attempt for 2+ min. Needed manual "sync now". Twice | logcat 08:59:54, 09:08:46; `mobile/lib/core/sync/sync_worker.dart:42`; `android-31` | Confirmed (emulator) | New | Mobile sync: retry with backoff after a failed health check |
| BUG-33 | Abebe | D2, sync on EDGE / UMTS (Android) | 95 KB and 44 KB photos upload on slow network | Both photo PUTs hit the 30 s receive timeout every time, so the listing cannot sync. 2 s unthrottled | logcat `[CropPhotoUploader] ... receive timeout 0:00:30`; `crop_photo_uploader.dart:42` | Confirmed on emulator throttle, not on a real SIM | New (FUP-140 asks for a real 3G upload check) | Mobile: upload timeout, resumable or smaller photos |
| BUG-31 | Abebe | D2, My Crops on a new install (Android) | Shows his teff and maize from the server | Empty list, "Midhaan Koo 0", chip says synced. Never appears | `android-09, -10, -43, -44`; `mobile/lib/core/sync/isar_marketplace_sync.dart:258-271` (`if (candidates.isEmpty) return false`, never inserts server rows) | Confirmed | Related FUP-520 (own listings' state never read back); the empty-phone case is New | Mobile sync: pull own listings |
| BUG-17 | Meron | D3, pay from a deep-linked or reloaded order page (web) | Order detail after paying | Payment succeeds, app goes blank white, no semantics, no console error, until manual reload. Back button on that page also dead | `web-meron-20-paid-5s.png`, `-20-paid-15s.png`, `-42-o3-deeplink-paid.png`; orders `1fd6a372`, `3fafa226` | Confirmed (2x) | New | Web navigation after payment (pop on an empty stack) |
| BUG-1 | Kidus, Henok, Dawit | D4 to D6, dispute during transit, then confirm, then rate | Order shows `disputed`; ratings blocked while disputed (`order-lifecycle.md` status table) | Escrow `disputed`, delivery `delivered`, order `delivered`, `deliveryConfirmedAt` null. `rating create` success | log 08:46:48 `POST /escrow/dispute` 200; 08:48:22 delivered success; 08:48:42 rating success; order `20f8171e` | Confirmed | New | Server: mirror a pre-delivery escrow dispute onto the order, block rating on escrow status |
| BUG-2 | Dawit | D2 to D3, same stock listed 4 times | Duplicate refused | 50 quintals of wheat listed at 60, 75, 90, 110: all success (61, 62, 63 refused). Two buyers funded orders on two of them | log 08:38:55 to 08:38:57; orders `4ccb6d42`, `d49a52f9` | Confirmed | New | Server fraud rule: same farmer, crop, quantity, place regardless of price |
| BUG-3 | Dawit | D3 to D5, second account self-order and 5-star rating | Blocked or flagged | Buyer account on the same `X-Device-Id` registered, ordered, self-picked up, rated 5 stars. Rating visible to buyers | log 08:43:24 register 201; 08:47:38 delivered; 08:47:41 rating success, `GET /ratings` 200 | Confirmed | Known in part: FUP-141 (pair rules) | Server fraud: device and pair linkage before a rating counts |
| BUG-4 | Henok | D2, overlapping trips | One truck, one trip per time window | 3 trips same route same morning at 100/150/200, plus Dire Dawa to Mekelle the same morning: all success | log 08:38:58 | Confirmed (listings only; no second paid load) | New | Server: trip overlap rule per trucker |
| BUG-5 | Henok | D4, pickup without being there | GPS required at pickup | With GPS 112.7 km away: `DELIVERY_GPS_PROXIMITY`. Same call without `gpsPickup`: success. Mocked and 100 km in 20 s fixes also accepted (flag only) | log 08:46:16, 08:46:17, 08:46:52 | Confirmed (needs the farmer's QR) | Known: FUP-448, and documented "as built" in `delivery-verification-api.md` | Server: require a fix for a paid pickup |
| BUG-6 | Kidus, Abebe | D6, "not received" after a QR and GPS confirmed handoff | Some friction or evidence check | `POST /escrow/dispute` 200, 69,825 ETB frozen, farmer waits for an admin. No cost to the buyer | log 08:47:45 delivered success; 08:48:18 dispute 200; order `4b42c185` | Confirmed (verdict not observed) | Known: FUP-141 (confirm-then-dispute rule) | Server and admin: auto-evidence in the dispute, rule for repeat disputers |

### Medium

| ID | Persona | Step | Expected | Actual | Evidence | Status | Known / New | Fix area |
|---|---|---|---|---|---|---|---|---|
| BUG-8 | Kidus | D4, "not_delivered" 30 s after pickup | Not possible before the delivery window | Accepted, money frozen while the truck drives | log 08:46:48 | Confirmed | Known decision pending: FUP-60 (`ESCROW_041` rule) | PO decision, then server |
| BUG-7 | Kidus | D4, order on a trip that does not pass the farm | Trip origin must match the listing | Debre Birhan crop booked on a Dire Dawa to Mekelle trip: success | log 08:46:03, order `ec73eb37` | Confirmed | New | Server order create: route check |
| BUG-9 | Kidus | D3, pay an order whose listing sold out | Refused at payment | Payment accepted, then `cancelled listing_out_of_stock`, refund by the hourly sweep | log 08:42:53, order `aa5a521a` | Confirmed | By design (ESC-18, `order-lifecycle.md` Stock at funding) | Server: stock check at the payment gate |
| BUG-14 | Dawit | D5, off-platform payment in order chat | Phone number or solicitation flagged | "send 7000 birr to telebirr 0911223344": success | log 08:47:36 | Confirmed | New (contact is open after accept by design, ESC-48) | Server: phone detector or warning in chat |
| BUG-34 | Abebe | D2, failed sync (Android) | "Still waiting, will retry" | Sheet says "Walsimsiisuun Xumurameera" (finished) with 1 waiting, 0 failed | `android-34, -35`; log `success (0/1 ok, 0 failed)` | Confirmed | New | Mobile sync status |
| BUG-35 | Abebe | D1, login on GSM speed (Android) | Slow login or "network too slow" | 35 s, then "log in online first to enable offline mode" | `android-06-login-failed-gsm-throttle`; `auth_service.dart:589`, `connectivity_monitor.dart:69` | Confirmed (emulator) | New | Mobile auth: timeout and message |
| BUG-36 | Abebe | D2, pending listing (Android) | Pending mark on the card | Card says available, info box says buyers see it. Only a 4 s snackbar and the bell | `android-22, -23, -24, -38`; `notification_bell.dart:158-185` | Confirmed | New | Mobile: per-row pending icon |
| BUG-38 | Abebe | D2, location field (Android) | Suggestions visible | Hidden behind the keyboard, half of the wrong town visible | `android-19` | Confirmed | Related FUP-55 (suggestion overlap) | Mobile form layout |
| BUG-40 | Abebe | D1, first launch (Android) | Language choice first | English splash and first screen before any language choice | `android-01` | Confirmed | New | Mobile onboarding |
| BUG-20 | Meron | D3, order form (web) | Quantity empty or small | Defaults to the whole stock (2,000 kg, 58,800 ETB) | `web-meron-10-order-form.png` | Confirmed | By design (`order-lifecycle.md` Units) | UX decision |
| BUG-22 | Meron | D3, order detail (web) | Crop, transport, commission split | Only a total | `web-meron-16`, `-21` | Confirmed | New | Mobile and web order detail |
| BUG-18 | Meron | D3, drop-off field (web) | Blocked while the field shows an error | Order placed with a red "Pick a place from the list."; detail then shows "The truck's destination" | `web-meron-13-form-filled.png`, order `1fd6a372` | Suspected (once) | New | Web form validation |
| BUG-19 | Meron | D3, network drop at payment (web) | "No internet, try again", retry when back | "An error occurred", no retry. Recovery by hand works, no duplicate | `web-meron-32-o2-offline-1s.png` | Suspected (once) | Related SIM-57 (web is not offline-capable) | Web error mapping |
| BUG-26 | Abebe | D2, price field (web, om) | Warning for an unlikely price | 2,800 Birr/kg (a per-quintal price) accepted with only a total line | `web-abebe-21-create-bigvalues.png` | Confirmed | New | Form: sanity warning |
| BUG-L1 | Tolosa | delete dialogs (am) | Two different words | "Cancel" and "Delete" are both "ሰርዝ" | arb keys `cancel`, `delete`; `crop_listing_detail_screen.dart:689-699` | Confirmed in code | New | l10n am |
| BUG-L2 | Abebe | all dates (om) | Oromo dates | English month and day names | `mobile/lib/core/utils/date_locale.dart:11-12`; `android-12`, `web-abebe-03-home.png` | Confirmed | New | l10n om date symbols |
| BUG-L4 | Tolosa, Abebe | trip date picker | Localized picker | English for om, Gregorian grid for am; no Ethiopian calendar anywhere | `fallback_material_localizations.dart:29-33`, `create_trip_screen.dart:235`, `ethiopian_clock.dart:174-178` | Confirmed in code | New | l10n, calendar |
| BUG-L5 | Abebe | order timeline | Icon per step | 4 px colour bar and 11 pt text; created and cancelled chips share one icon | `order_detail_screen.dart:2158-2176`; `android-12, -28` | Confirmed | New | Mobile UI |
| BUG-L9 | Abebe | server validation refusal | Says what to fix | `CROP_VALIDATION` / `TRIP_VALIDATION` show "could not be saved on the server" | `mobile/lib/core/errors/app_error.dart:671-673` | Confirmed in code | New | Error mapping |
| BUG-L12 | Meron | trip price (om) | Clear unit | "ETB/k" reads as per kilo, a 100x error | arb `detailEtbPerQuintalShort`; `suggested_trip_card.dart:108` | Confirmed in code | New | l10n om |
| BUG-L17 | Henok | iPhone | iOS app | No `mobile/ios`; iPhone users get the web app only | repo tree | Confirmed | New | Product decision |

### Low

| ID | Persona | Step | Expected | Actual | Evidence | Status | Known / New | Fix area |
|---|---|---|---|---|---|---|---|---|
| BUG-10 | Kidus | D3, tiny order | Min 0.01 kg | 0.001 kg accepted, total 0.11 ETB | log 08:41:36, order `fad9ba27` | Confirmed | New | Server order validation |
| BUG-11 | Dawit, Henok | no-op updates | Refused or no version move | Order update with only a stripped field, and a trucker's `qrNonce`, answer `success` and bump the version | log 08:43:39, 08:46:11 | Confirmed | New | Server sync |
| BUG-12 | Kidus, Tolosa | update on a stranger's delivery | `DELIVERY_UNAUTHORIZED` | `DELIVERY_INVALID_TRANSITION: Cannot transition from 'pending' to 'delivered'` tells the status | log 08:44:48, 08:46:53 | Confirmed | New | Server: party check first |
| BUG-13 | Abebe | re-create with a new key | `conflict` | `FRAUD_DUPLICATE_LISTING` | log 08:38:54 | Confirmed (whether it writes a fraud signal: not checked) | New | Server: id check before the fraud rule |
| BUG-15 | all abusers | D6, after the probes | Step-up or hold | Kidus, Dawit, Henok could still create an order, listing, trip | log 08:48:25, 08:48:26 | Suspected (risk tiers not read) | Related FRD-04 | Fraud scoring |
| BUG-16 | Kidus | buyer sets `qrNonce` | Clear reason | "You can only update your own deliveries." to the delivery's own buyer | log 08:47:42 | Confirmed | New | Server message |
| BUG-21 | Meron | after paying (web) | Order detail | Lands on the market list | video at 1:10 | Confirmed (2x) | New | Web navigation |
| BUG-23 | Meron | pay with step-up (web) | One payment request | `payment_submitted` sent before the password dialog and again after (3 `/sync`), one escrow | scratchpad `web/log-meron.txt` 08:51:49 to 08:52:44 | Suspected | New | Web payment flow |
| BUG-24 | Meron | language switch (web) | Stay in Settings, all text switches | Thrown to Home; "Recent" line keeps the old language until reload | `web-meron-06-am-settings.png` | Confirmed (3x) | New | Web settings |
| BUG-25 | Abebe, Tolosa | leftovers (om, am) | Fully translated | "Delivery Delivered", "ETB" on profile, "Back" in om, "offline" in om text, raw ids in payments | `web-abebe-03-home.png`, `web-tolosa-03-home.png`, `android-49, -50` | Confirmed | Partly known: FUP-86 | l10n |
| BUG-27 | Abebe, Meron | phone width | Button clear of content | Floating button covers the last order card | `web-abebe-ph-orders.png`, `android-12` | Confirmed | New | UI padding |
| BUG-28 | Meron | listing map | Right waypoint | "Gimbi" between Debre Birhan and Addis | `web-meron-08-maize-detail-b.png` | Confirmed | New | Places data |
| BUG-29 | Meron | payment screen (web) | Telebirr default | CBE default with account "1000XXXXXXXXXX", reference is the raw order UUID | `web-meron-14-after-place-5s.png` | Suspected (dev config) | New | Config |
| BUG-30 | Tolosa | forbidden order URL (web) | "Not your order" | Generic "an error occurred" | `web-tolosa-10-orders-d0d2ad9e-d29c-.png` | Suspected (once) | New | Error mapping |
| BUG-37 | Abebe | dashboard counts (Android) | Match the order list | 0 for 15 minutes while orders listed | `android-07, -13` vs `-12, -37` | Confirmed | New | Mobile dashboard |
| BUG-39 | Abebe | My Crops tabs (Android) | Plain empty state | "No listing matches your search" with no search | `android-44` | Confirmed | New | l10n copy |
| BUG-L11 | Abebe | units and currency (om) | One spelling | Quintal spelled 4 ways; Birrii / ETB / qarshii | arb `quantityInQuintals`, `capacity`, `marketQuintalsLeft`, `pricePerQuintal` | Confirmed | Partly known: FUP-86 | l10n om |
| BUG-L10 | Abebe, Tolosa | "escrow" wording | One plain word | 2 spellings in am, 4 in om | arb | Confirmed | Partly known: FUP-86 | l10n |
| BUG-L16 | Abebe | icon-only buttons | Tooltip or label | 14 without one; crop tiles text-only; no read-aloud | `login_screen.dart:428`, `voice_note_widget.dart:413`, `star_rating.dart:43` | Confirmed in code | New | Accessibility |

## Top 5 to fix first

1. **BUG-32 + BUG-33: an offline listing does not reach the server by itself.** After reconnect the sync gives up on one failed health check, and photo uploads time out on slow networks. This is the product's main promise to the farmer. Confirm BUG-33 on a real 3G SIM first.
2. **BUG-31: a farmer who logs in on a new phone sees none of his crops.** One early return in `isar_marketplace_sync.dart:270`.
3. **BUG-17: blank page after a successful payment on web.** An honest buyer cannot tell whether she paid.
4. **BUG-1 (with BUG-8 and BUG-6): disputes.** A buyer can freeze the money mid-transit or after a confirmed handoff at no cost, and the order then says `delivered` and takes ratings while the escrow is `disputed`.
5. **BUG-2 + BUG-3: fake supply and fake reputation.** One stock listed four times at different prices and sold twice; a second account on the same device buys from itself and leaves a public 5-star rating.

Next after these: BUG-5 (pickup without a GPS fix, FUP-448).

## What held (no finding)
- Role visibility for all three roles on API, web and Android; all ID-guessing reads and writes on crops, trips, orders, deliveries, escrows, messages, ratings.
- Buyer cannot create a crop or trip. Client price, totals, status, commitment fields are ignored or refused.
- Order above stock refused. Race for the last 700 kg: one funded, one cancelled with refund armed. Triple payment submit: one escrow.
- Price or quantity edits after an order do not change the order. Trip price and capacity locked after a booking. Listing with a funded order cannot be deleted.
- Farmer cannot mark delivered or release escrow. Trucker cannot confirm delivery. Cancel after pickup refused for buyer and trucker.
- Pickup and drop-off need the right QR; wrong, forged and reused codes fail. Far GPS refused when sent.
- Sync: same key resend is `duplicate`, same key with another payload refused, another user's id in the key has no effect, future and old timestamps refused or ignored, backwards transitions refused.
- Same phone number cannot register twice (`FAYDA_004`).
- Offline create plus app kill on Android: exactly once on the server, three variants.
- l10n: 1,721 keys in each language, no missing keys or placeholder mismatches; sync refusals are localized.

## Not tested, and why
- Payout, refund sweep, deadlines: need days of real time.
- Forged or replayed signed Telebirr callback: dev runs the mock gateway, `/webhooks/telebirr` is 404.
- The abusers' fraud signals and risk tiers: the admin read was blocked in this session. `python3 scripts/e2e_fraud_personas.py --recheck` style admin reads can show them.
- iOS (no build). Real low-end Android device. Amharic UI on Android. Buyer intents (`buyer_intent`) for the "read other buyers' intents" item.
- A second paid load on Henok's overlapping trips; a second Kidus account (registration cap of 10 per hour per IP).

## Cleanup
Everything is tagged: display names start with `SIM-2026-11-19`, listing descriptions and order drop-off notes start with `SIM-2026-11-19`. Exceptions: trips have no note field (find them by trucker `40dc293c...` and `905cb9ac...`), and two web self-pickup orders (`d0d2ad9e`, `3fafa226`) have no note because that form has none.

Accounts (7): `+251900198331` Abebe, `+251900192210` Dawit, `+251900195076` Tolosa, `+251900194686` Henok, `+251900191288` Meron, `+251900199815` Kidus, `+251900192535` Dawit's sock buyer. User ids are in the diaries.

Left open on dev: 2 escrows in dispute (`escrow-order-4b42c185...`, `escrow-order-20f8171e...`) in the admin dispute queue; funded or accepted orders `d49a52f9`, `1fd6a372`, `d0d2ad9e`, `3fafa226`, `64ad1f63`; delivered orders `81c5e034`, `4ccb6d42` that will auto-release; several `created` orders that auto-cancel in 24 hours; 9 listings and 6 trips.

## Fix status (added 2026-11-19, after the run)
The findings were fixed in five pull requests, grouped by area. None is merged yet.

| PR | Group | Findings |
|---|---|---|
| #445 | Mobile offline sync | BUG-31, 32, 33, 34, 35, 36, 37 |
| #447 | Buyer payment flow and forms | BUG-17, 18, 19, 20, 21, 22, 23, 24, 26, 28, 30, 38, 39, 40 (BUG-27 not reproducible in code) |
| #444 | Localization and low-literacy aids | BUG-L1, L2, L4 (picker), L5, L8, L9, L10, L11, L12, L14, L16, BUG-25 |
| #448 | Server order, delivery, dispute, rating | BUG-1, 9, 10, 11, 12, 13, 16 |
| #446 | Server duplicate lot and trip rules | BUG-2, 4 (BUG-3 already detected by the hourly scan; BUG-15 matches the design) |

Left for a decision or a later build (TASK.md FUP-535 to FUP-547): BUG-5, BUG-6, BUG-7, BUG-8, BUG-14, BUG-29, the rating of a same-device pair (BUG-3), the Ethiopian calendar, read-aloud and iOS (BUG-L4 rest, BUG-L17), a real device and SIM check, native review of the am and om strings, and the cleanup of the simulation data on dev.
