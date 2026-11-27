# GPS unavailable spike (SPK-01)

**Date:** 2026-11-13. **Ticket:** SPK-01 (`TASK.md`, Spikes). **Feeds:** FRD-11, FRD-14, ESC-33, ESC-34, ESC-36, FUP-138.
**Code read at:** `017a8e28` on main. **Method:** code and contract reading only, no device or dev API runs.
Every `path:line` below points at that commit. Lines of the location plugin (`geolocator_web-4.1.3/...`) are in the
pub cache, at the version `mobile/pubspec.lock` pins. Nothing in this spike changes code.

The light scan of 2026-10-11 in the ticket is out of date: the handoff GPS rules live in `/sync`
(`server/src/Functions/SyncFunction.cs`), not in `DeliveryFunction.cs`, which only reads; the DTO fields are at
`server/src/Models/DeliveryDtos.cs:223` and `:228`; and the LT epic has since built live location sharing.

## Flags that change the answers

| Flag | State | What GPS decides once it is on |
|---|---|---|
| `pickupClassification` (ESC-34) | off in every environment (`specs/contracts/delivery-verification-api.md:1506`) | the trucker's (or self-pickup buyer's) `arrived_pickup` check-in, which needs a fix, is their only defence at T7 when no scan happened |
| `dropoffClassification` (ESC-36) | off in every environment (`specs/contracts/delivery-verification-api.md:1860`); FUP-372 turns it on | the trucker's `arrived_dropoff` check-in, which needs a fix and a photo, decides T8 (overdue dispute) and T9 (confirm for a silent buyer) |
| `reputationPenalties` | off (`specs/architecture/reputation-architecture.md:74`) | the trucker's own `GPS_ANOMALY` row, judged from the trucker's check-ins (FUP-203, FUP-245) |

Live trip check-ins (`location_ping`) have no flag. They run wherever the current app is installed.

## Summary

Top findings:

1. **A GPS refusal from the server is a silent, endless failure on Android.** `DELIVERY_GPS_PROXIMITY` is not a
   permanent refusal on the phone. The queue sends it again on every reconnect, never reverts the local delivery
   and never says why. Web says "Delivery operation failed". It hits the self-pickup confirm without a fix
   (FUP-138), a pickup scan more than 5 km from the listing point and a drop-off confirm more than 5 km from both
   drop-off points (section 1.3). On a self-pickup it can cost the farmer both the crops and the pay: the
   delivery stays `pending`, so the order ends cancelled with a full refund to a buyer who holds the goods, and a
   dishonest buyer can get there on purpose by refusing GPS (sections 1.3 and 4).
2. **Nothing records why a fix is missing.** The reason stays on the phone. The delivery row has no field for it,
   the audit row says only `handoff=qr` or `handoff=code`, no fraud signal is written, and the admin dispute page
   says "No GPS fix at the handoff". An admin cannot tell "refused" from "never asked" (section 2).
3. **Driver tracking is live, opt-in and hourly.** Since the LT epic a sharing trucker's phone checks in every hour
   (every 15 minutes near a drop-off) through an Android foreground service that needs only the "while in use"
   permission; web checks in while the page is open. Buyer and farmer see a map from pickup to delivery, admins see
   the trail with its gaps. A phone that shares but gets no fix still reads as `live`, and the buyer is told
   "Likely no network on this road" (section 3).
4. **Refusing GPS never releases money to a trucker. It removes evidence.** Every GPS rule skips a missing
   point. Money moves only on a QR scan, an admin verdict or a timer, and the one timer that pays without the
   buyer (T9) needs a measured GPS check-in. With the ESC-34 and ESC-36 flags on, refusing GPS costs the trucker
   their own defences, so the incentive points the right way. Once `reputationPenalties` is on, the skipped
   `GPS_ANOMALY` row is also 25 points missing from the risk score the payout risk gate reads, so skipping it can
   skip a payout hold. One
   inversion: a listing whose point is a picked town far from the farm refuses an honest party with GPS on and
   passes one with GPS off (section 4).
5. **Web location has two plugin traps.** `geolocator_web` 4.1.3 passes the time limit in microseconds (15 s
   becomes about 4.2 hours) and calls any error during the permission request "denied for good" (section 1.1).
6. No path sends (0, 0) any more (the SIM-43 fix holds for crops). The server still has no bounds check on handoff
   GPS (section 1.4).

**Recommendation** (section 6): keep the QR scan as the proof of a handoff and GPS as evidence; record every missing
fix with its reason, time and accuracy; close the refusal, self-pickup and web gaps; keep live tracking opt-in
through the foreground service already built; add no background location. Follow-ups FUP-439 to FUP-448 and a new
text for FUP-138 (section 7).

## 1. GPS off or refused

### 1.1 The capture code

Crop create, every handoff screen and every check-in card call `GpsCaptureService.captureCurrentPosition`
(`mobile/lib/features/delivery_verification/data/services/gps_capture_service.dart:25-69`): high accuracy, a 15 s
limit (`mobile/lib/features/delivery_verification/data/delivery_constants.dart:20`), one attempt, and the permission
asked when it is undecided. Live trip has its own source
(`mobile/lib/features/live_trip/data/geolocator_location_source.dart:50-65`): medium accuracy, a 60 s limit, the
permission asked only for a driver's own tap.

| Failure | Android (`geolocator_android` 5.0.4) | Web (`geolocator_web` 4.1.3) |
|---|---|---|
| Location services off | `unavailable('Location services disabled')` (`gps_capture_service.dart:26-31`) | no such state: `isLocationServiceEnabled` is always true (`geolocator_web-4.1.3/lib/geolocator_web.dart:48`). The browser's error comes later as "GPS signal not available", or as "denied for good" when it happens during the permission request (next row) |
| Permission denied | asked, then `unavailable('Location permission denied')` (`gps_capture_service.dart:33-41`) | `requestPermission` answers `deniedForever` for any error of its position call (`geolocator_web-4.1.3/lib/geolocator_web.dart:58-64`), so a dismissed prompt, or location off before the first grant, reads as denied for good |
| Denied for good | `unavailable(permanentlyDeniedReason)` (`gps_capture_service.dart:42-46`) | the site state `denied` maps to `deniedForever` (`geolocator_web-4.1.3/lib/src/utils.dart:35-36`) |
| 15 s limit passes | `TimeoutException`, then `unavailable('GPS signal not available')` (`gps_capture_service.dart:64-65`) | the limit goes to the browser as `timeout?.inMicroseconds` in an option read as milliseconds (`geolocator_web-4.1.3/lib/src/html_geolocation_manager.dart:35-36`): 15 s becomes 15,000,000 ms, about 4.2 hours, and the permission request's own call waits up to a day. The plugin adds no Dart side bound |
| No fix indoors, slow phone | the same as the limit; the fused provider may return a coarse network fix instead, whose accuracy the handoffs drop (1.4) | browser dependent, bounded only as above |
| Approximate location (Android 12 and later) | granted as `whileInUse` with a coarse fix; nothing in `mobile/lib` calls `getLocationAccuracy`, so it is never noticed | not applicable |

The reason is an English string that is never shown. Only the two check-in cards read it, and only to tell "denied
for good" apart (`mobile/lib/features/order/presentation/widgets/pickup_check_in_card.dart:341`,
`mobile/lib/features/order/presentation/widgets/dropoff_check_in_card.dart:443`).

### 1.2 Each action, by role and platform

| Action | Role | Without a fix (Android) | Web difference | What the user is told |
|---|---|---|---|---|
| Crop create | Farmer | the fix is taken when the form opens (`mobile/lib/features/crop_listing/presentation/screens/create_crop_listing_screen.dart:93`). Save uses it, else the picked place's point, else blocks (`create_crop_listing_screen.dart:171-180`). Never (0, 0) | an unanswered prompt keeps "Capturing GPS..." spinning; Save still works with a picked place | a "GPS unavailable" pill with retry (`create_crop_listing_screen.dart:615-631`), the hint `detailGpsPickFromList` (`:537`), the block `cropLocationCoordinatesRequired`. No reason, no settings link |
| "Crops are ready" check-in | Farmer | waits at most 5 s (`pickup_check_in_card.dart:23`, `:330-336`), then goes without a fix and is stored unmeasured | same | nothing; optional by design |
| Pickup scan (QR) | Trucker | the fix starts when the QR reads (`mobile/lib/features/delivery_verification/presentation/screens/qr_scan_screen.dart:261`). Confirm waits for it, at most 30 s (`qr_scan_screen.dart:59`, `:270-289`, `:319-320`), then confirms without GPS (`qr_scan_screen.dart:340-354`). The server takes it: the 5 km check runs only when `gpsPickup` is sent (`SyncFunction.cs:5627-5668`) | same | "GPS unavailable" (`qr_scan_screen.dart:838-841`). No retry button for a scanned QR, no reason |
| Pickup or drop-off by typed code | Trucker, Buyer | Confirm stays disabled until a fix (`qr_scan_screen.dart:323`, `:470`); the server refuses a paid delivery's code without one, `DELIVERY_CODE_GPS_REQUIRED` (`SyncFunction.cs:9154-9156`) | same | `qrTypedCodeNeedsGps` with "Try location again" (`qr_scan_screen.dart:849-860`) |
| "I have arrived" at the farm | Trucker; the buyer on a self-pickup | blocked on the phone (`pickup_check_in_card.dart:291-310`); the server refuses an arrival without GPS, `CHECKIN_VALIDATION` (`server/src/Helpers/PickupJudgement.cs:324-327`) | a dismissed prompt is told "blocked for this site" (1.1); the card can spin far past 15 s | `checkInNeedsLocation`, or `checkInLocationBlocked` with Open settings (web: `checkInLocationBlockedWeb`) |
| Live trip check-ins | Trucker | a slot without a fix is skipped and nothing is sent (`mobile/lib/features/live_trip/domain/check_in_engine.dart:666-671`); a tap gets a snackbar. Sharing started without the permission stays "shared" (`check_in_engine.dart:504-516`) | only while the page is visible and with no queue (`mobile/lib/features/live_trip/web_check_in_engine.dart:15-30`, `mobile/lib/features/live_trip/data/web_location_ping_repository.dart:11-17`); a fix may wait hours (1.1) while the engine's actions run one at a time | `tripModeNoFix`, "Check that location is on", also for a denied permission (`mobile/lib/features/live_trip/presentation/share_consent_listener.dart:107`). No settings link |
| "Arrived at drop-off" | Trucker | the photo first, then the fix; without a fix it is blocked and the photo kept for the next tap (`dropoff_check_in_card.dart:364-404`); the server refuses it without GPS | as the farm card | `dropoffCheckInNeedsLocation`, `dropoffCheckInLocationBlocked` (web: `dropoffCheckInLocationBlockedWeb`) |
| Drop-off confirm (QR) | Buyer | the device lock first, then a confirm without GPS (`qr_scan_screen.dart:326-366`); the server takes it, the 5 km check runs only when `gpsDropoff` is sent (`SyncFunction.cs:5691-5767`) | same | "GPS unavailable" |
| Self-pickup confirm (QR) | Buyer | the scan screen does not know this leg needs GPS (only a typed code waits, `qr_scan_screen.dart:323`), so it confirms without one. The server refuses: `DELIVERY_GPS_PROXIMITY: Location is required to confirm self-pickup at the farm.` (`SyncFunction.cs:5672-5689`). See 1.3 | the refusal comes back at once as "Delivery operation failed" (`mobile/lib/features/delivery_verification/presentation/bloc/delivery_bloc.dart:73-86`, `mobile/lib/features/delivery_verification/presentation/delivery_error_l10n.dart:26`) | Android: nothing at the farm, later only the failed sync count |

The farmer's own handoff step (showing the pickup QR) takes no GPS.

### 1.3 The server's GPS refusal is lost on the client

`DELIVERY_GPS_PROXIMITY` covers five refusals: a pickup more than 5 km from the listing (`SyncFunction.cs:5665`), a
self-pickup without a fix (`SyncFunction.cs:5687`), a self-pickup on a listing without coordinates
(`SyncFunction.cs:5733`), a self-pickup more than 5 km from the farm (`SyncFunction.cs:5745`) and a drop-off more
than 5 km from both drop-off points (`SyncFunction.cs:5763`). The code is in
neither the client's permanent list (`mobile/lib/core/sync/sync_executor.dart:1305-1328`) nor the contract's
(`specs/contracts/sync-protocol.md:217-231`). On Android:

- the operation is marked failed and sent again, three tries (`mobile/lib/core/sync/sync_queue.dart:224-236`), then
  reset on every reconnect (`sync_executor.dart:536`, `sync_queue.dart:327-345`). It can never pass;
- nothing reverts the local row. It keeps `pickedUp` or `delivered` with its version raised
  (`mobile/lib/features/delivery_verification/data/repositories/delivery_repository.dart:462-470`, `:537-545`), and
  the delivery pull does not replace it with the server's older version
  (`mobile/lib/core/sync/isar_marketplace_sync.dart:243-244`);
- no refusal is announced: only operations marked rejected are (`sync_executor.dart:1069-1074`). Were it announced,
  the text would be the generic `errorChangeNotSaved` (`mobile/lib/core/errors/app_error.dart:644`).

Effect on a trucker delivery: the trucker drives off with the goods while the server still says `pending`. With
`pickupClassification` on, T7 then judges as if no scan happened (`PickupJudgement.cs:426-445`): a trucker no-show
without the trucker's arrival check-in; with it but without the farmer's "crops ready", a farmer no-show, cancelled
and refunded while the trucker holds the goods; the admin queue when both checked in.

Effect on a self-pickup, where the farmer can lose both the crops and the pay. The buyer leaves the farm with the
goods and the delivery stays `pending` on the server. The buyer's arrival check-in needs a fix too (1.2), so a
buyer whose confirm was refused for having none has no `arrived_pickup` either:

- with `pickupClassification` on (FUP-326), T7 judges a self-pickup buyer no-show
  (`server/src/Helpers/PickupJudgement.cs:436`) and cancels the order with the full refund armed
  (`specs/contracts/delivery-verification-api.md:1555-1560`) while the buyer holds the goods;
- with the flag off, the buyer may cancel for a full refund once the pickup deadline has passed, because the
  delivery is still `pending` (`specs/contracts/order-lifecycle.md:495-496`, `:548`).

This is the refund-while-the-goods-left case of FUP-331, reached without a late scan. A dishonest self-pickup buyer
can reach it on purpose by refusing GPS at the farm (section 4). Web shows "Delivery operation failed" with no
reason for every case.

### 1.4 Coordinates sent

No path sends (0, 0): crop create falls back or blocks (`create_crop_listing_screen.dart:171-180`) and the server
refuses a crop point outside Ethiopia (`SyncFunction.cs:1291-1304`); the handoffs send no `gpsPickup` or
`gpsDropoff` without a fix (`delivery_repository.dart:479-484`, `:554-559`); check-ins and pings drop a fix outside
the box (`mobile/lib/core/models/order_check_in.dart:250-263`,
`mobile/lib/features/live_trip/domain/check_in_rules.dart:90-104`).

A handoff fix goes up as latitude and longitude only. The capture result has no time
(`gps_capture_service.dart:136-156`) and the accuracy it has is not sent. The server bounds a handoff point only by
the 5 km distance, and only when a reference point exists (`SyncFunction.cs:5634-5644`, `:5698-5709`): a crafted
client could still store (0, 0) or a point abroad on a delivery with no reference point. The readers treat (0, 0) as
no fix (`server/src/Services/TransactionPatternRules.cs:442`,
`server/src/Functions/AdminFunction.DisputeEvidence.cs:336`).

The phone never checks the 5 km line before it sends: `GpsCaptureService.checkProximity` and
`isWithinEthiopianBounds` have no caller.

### 1.5 Languages

Every text named in 1.2 exists in en, am and om. The check-in texts were machine drafted for am and om and owe a
native review (their `@` descriptions, for example `mobile/lib/shared/l10n/app_en.arb:5510`).

| Key | en | am | om |
|---|---|---|---|
| `deliveryGpsUnavailable` | `app_en.arb:1040` | `app_am.arb:1010` | `app_om.arb:1000` |
| `cropLocationCoordinatesRequired` | `app_en.arb:588` | `app_am.arb:588` | `app_om.arb:582` |
| `qrTypedCodeNeedsGps` | `app_en.arb:5073` | `app_am.arb:3215` | `app_om.arb:3208` |
| `checkInNeedsLocation` | `app_en.arb:5471` | `app_am.arb:3414` | `app_om.arb:3407` |
| `checkInLocationBlocked` | `app_en.arb:5509` | `app_am.arb:3433` | `app_om.arb:3426` |
| `dropoffCheckInNeedsLocation` | `app_en.arb:5539` | `app_am.arb:3448` | `app_om.arb:3441` |
| `tripModeNoFix` | `app_en.arb:4489` | `app_am.arb:2923` | `app_om.arb:2916` |

(All in `mobile/lib/shared/l10n/`.) What is missing is the reason: only the check-in cards tell "blocked" from
"location off", the scan screen and crop create say "GPS unavailable" for every cause, and no screen but the
check-in cards offers the settings page. `deliveryGpsPermissionDenied` (`app_en.arb:1064`) has no caller. The
self-pickup refusal and the 5 km refusals have no text of their own on either platform (1.3).

## 2. How a missing fix is tracked

| Place | What it holds without a fix |
|---|---|
| Delivery row | `gpsPickup` or `gpsDropoff` absent (`DeliveryDtos.cs:223`, `:228`), nothing about why. The `requires_review` flag the contract planned was never built (`specs/contracts/delivery-verification-api.md:988`) |
| Phone time of the handoff | the pickup stores `pickupCapturedAt`, the pickup operation's phone time, when plausible (`SyncFunction.cs:5940-5952`); the drop-off stores none, its `confirmedAt` is the server's receipt time (`SyncFunction.cs:5955-5961`) |
| Transaction audit | `handoff=qr` or `handoff=code` only (`SyncFunction.cs:6037-6039`) |
| Fraud signals | none. Rule 4 skips a missing point (`server/src/Services/FraudDetectionService.cs:236-241`); the trucker's own row needs `gpsPickup` (`server/src/Functions/SyncFunction.TruckerGpsEvidence.cs:88-95`) and a fresh check-in (`SyncFunction.TruckerGpsEvidence.cs:119-123`); `IMPOSSIBLE_DELIVERY_TIME` skips (`TransactionPatternRules.cs:290-299`) |
| Arrival check-ins | an arrival without GPS is refused, so nothing is stored; a farmer's "crops ready" without one is stored with `distanceChecked: false`, which its audit row carries (`server/src/Functions/SyncFunction.HandoffCheckIn.cs:213`) |
| Live trip | a skipped slot sends nothing. A decline and a pause are events (decline: `check_in_engine.dart:524-534`, and `:377-388` when the driver stops sharing; pause: `check_in_engine.dart:293-298`). The admin trail shows a stretch of more than 90 minutes without a fix as a gap (`specs/contracts/live-trip-api.md:158`) |
| Admin dispute page | "No GPS fix at the handoff" (`admin/src/components/disputes/EvidenceTabs.tsx:89-91`, `admin/src/i18n/en.json:234`), the pickup's phone time when stored (`EvidenceTabs.tsx:74-78`), no distance (`AdminFunction.DisputeEvidence.cs:253`, `:261-266`) |
| Phone logs | `debugPrint` only (`qr_scan_screen.dart:280`, `geolocator_location_source.dart:62`) |

Can an admin tell the cases apart?

- **"GPS refused" from "GPS never asked":** no. Both are an absent point, and the reason never leaves the phone.
- **"Offline, synced later":** partly. A pickup's `pickupCapturedAt` earlier than its `pickedUpAt` shows it; a
  check-in has `capturedAt`, `receivedAt` and `late`; a ping has `late` (`live-trip-api.md:155`). A drop-off confirm
  shows nothing.
- **"Sharing on but no location" from "no network":** no. Both are a gap in the trail.

## 3. Driver tracking today

Tracking is no longer only the two scan points:

- **Scan points.** `gpsPickup` at the trucker's pickup scan, `gpsDropoff` at the buyer's drop-off scan, each
  optional.
- **Check-ins (LT epic).** At the trip's first pickup scan the trucker is asked once per trip whether to share
  (`check_in_engine.dart:337-372`; the default is to ask, `mobile/lib/features/live_trip/domain/sharing_state.dart:83`).
  "Not now, scans only" records `declined`, and then only the scans send a fix. When shared: a fix at once, then
  every 60 minutes, every 15 minutes within 10 km of an undelivered drop-off, and at each scan, Check in now and
  I've arrived (`check_in_rules.dart:31-40`). Medium accuracy with 60 s, skipped when none comes. Each is a
  `location_ping` in the sync queue with its capture time and the platform's mock flag
  (`live-trip-api.md:15-52`).
- **Android.** `geolocator`'s foreground service with a visible notification, asking for a low power position every
  5 minutes only to wake the cadence (`geolocator_location_source.dart:39`, `:68-95`). It is started from the
  consent tap in the foreground, so the "while in use" permission is enough and the manifest has no
  `ACCESS_BACKGROUND_LOCATION` (`mobile/android/app/src/main/AndroidManifest.xml:12-21`).
- **Web.** Only while the page is visible, with no queue; a reload drops waiting pings.
- **Server effects.** A fix 2 km or more from the pickup moves `pickedUp` to `inTransit`; one within 5 km of the
  drop-off pushes `truck_nearby` once; `GPS_JUMP` and `GPS_MOCKED` signals. Only a fix with accuracy at most 1,000 m
  acts (`live-trip-api.md:189`).

What the buyer sees while the goods travel (the farmer of the order sees the same): the live trip screen, polled at
most every 5 minutes (`mobile/lib/features/live_trip/domain/track_poller.dart:17`), with the trail at 3 decimals,
the last fix, the arrival window, the distance left, the pauses, and a freshness chip (fresh under 20 minutes, late
over 75: `mobile/lib/features/live_trip/domain/track_freshness.dart:28-32`). A declined trip shows "Scans only" and
the pickup point. Before pickup and after delivery there is no position (`live-trip-api.md:101-110`). When the
trucker shares but the phone gets no fix, the server still answers `live` (`server/src/Helpers/TripTrack.cs:154-168`),
the age counts from the pickup (`track_freshness.dart:37-42`), and after 75 minutes the buyer reads
`liveTripLateNote`, "Likely no network on this road" (`app_en.arb:4589`), which is not the reason.

What the admin dispute page gets (FRD-14 with LT-13): a Trip trail tab from `GET /admin/deliveries/{id}/trail` (the
scan points at 4 decimals, every fix and event with `coarse` and `late`, legs with `jump`, pauses, gaps over 90
minutes, stops away from the stop list, and a summary with `sharedWholeWay` and `declined`:
`live-trip-api.md:118-164`); a Timeline tab with each handoff's method, who, the GPS point and its distance to the
planned point (`AdminFunction.DisputeEvidence.cs:234-300`); Photos and Messages.

## 4. When the trucker's GPS cannot be recorded

| Area | Effect |
|---|---|
| Delivery state | the pickup scan goes through (1.2); a typed code on a paid delivery is refused. The system never moves the delivery to `inTransit` without a fix (`server/src/Helpers/CheckInEffects.cs:61-66`), which changes nothing: the buyer may confirm from `pickedUp` |
| Escrow release and payout legs | no escrow or payout code reads GPS (`gps` matches nothing in `server/src/Functions/EscrowFunction.cs` or `EscrowCronFunction.cs`). On a trucker delivery money moves only on the buyer's confirm scan, an admin verdict or a timer (`live-trip-api.md:187`). A self-pickup is different: a refused confirm can end in a refund (below) |
| T7, pickup judged (flag on) | no `arrived_pickup` without a fix, so with no scan by T7 the trucker is a no-show (`PickupJudgement.cs:426-437`): `awaiting_transport` with `faultParty: trucker`. A scan before T7 makes this moot |
| T8 and T9, drop-off judged (flag on) | no `arrived_dropoff` without a fix, so no T9 confirm for a silent buyer, and at T8 (pickup plus 72 hours) a `delivery_overdue` dispute freezes the escrow (`server/src/Helpers/DropoffJudgement.cs:372-383`), with `DELIVERY_OVERDUE` on the trucker under `reputationPenalties`. A buyer's confirm before T8 makes this moot |
| Dispute evidence | "No GPS fix at the handoff" and no distance; the trail is empty or has gaps; the pickup photo is optional and the arrival photo goes with the check-in, so neither may exist |
| Fraud GPS rule | a missing point skips: no `GPS_ANOMALY` (`FraudDetectionService.cs:236-241`). The trucker's own row is never judged without `gpsPickup` (`SyncFunction.TruckerGpsEvidence.cs:88-95`) or check-ins (`:119-123`). `IMPOSSIBLE_DELIVERY_TIME` skips. No pings means no `GPS_JUMP` or `GPS_MOCKED` |
| Payout risk gate | once `reputationPenalties` is on, the trucker's `GPS_ANOMALY` row weighs 25 points (`FraudDetectionService.cs:745`) in the risk score the payout risk gate reads before every automatic payout, holding a CRITICAL payee (`server/src/Services/EscrowService.PayoutRiskGate.cs:8-12`, `:74-76`, FUP-29). A row that is never judged can therefore also be a hold that never happens |
| 5 km threshold | checked only when a point is sent (`SyncFunction.cs:5627-5668`, `SyncFunction.cs:5691-5767`) |

**Can a trucker game it by refusing GPS?** Not to get paid. The pickup still needs the farmer's server-held QR or
code, and a typed code needs GPS on a paid delivery. On a trucker delivery the buyer's scan releases the money with
or without anyone's GPS, and the only automatic release without the buyer (T9) needs the trucker's measured check-in
and photo. What a refusing trucker does avoid is evidence: every GPS-based fraud rule, the 5 km checks and, with
`reputationPenalties` on, the risk points of the trucker's `GPS_ANOMALY` row that could hold a payout (table
above). A trucker and buyer who collude leave no GPS-based signal, which is accepted: the QR proves the handoff, and
the buyer releases their own money.

**A self-pickup buyer can game it.** Here GPS is mandatory, so refusing it changes where the money goes. A buyer who
scans the farmer's QR with location off takes the goods, the server refuses the confirm (1.3), and the delivery
stays `pending`. The buyer cannot check in at the farm without a fix either. The order then ends in a full refund
to the buyer: by the T7 no-show judgement with `pickupClassification` on, or by the buyer's own deadline cancel
with it off (1.3). The farmer has handed over the crops and is not paid. Nothing on the delivery or in the audit
shows that the QR was scanned: the nonce is consumed only after a successful write
(`SyncFunction.cs:5369-5371`), and the refused confirm writes nothing. This is FUP-331's case (a refund while the
goods left the farm), reached without the late scan FUP-331 relies on to notice it. With `pickupClassification` on,
the refund needs no act of the buyer at all, so even an honest buyer whose phone had no fix is refunded. FUP-439 and
FUP-138 close the gap on the client and should ship before FUP-326 turns that flag on.

**The inversion.** A listing's point may be a picked place (`create_crop_listing_screen.dart:173-174`) and nothing
records which. An honest trucker at the real farm with GPS on is refused more than 5 km from that point (silently on
Android, 1.3), and so are the `arrived_pickup` check-in (`CHECKIN_TOO_FAR`) and a self-pickup buyer, while a trucker
with GPS off passes the pickup scan.

**A residual.** A trucker who turns GPS off only for the pickup scan and shares afterwards is never judged by the
trucker's `GPS_ANOMALY` row, which needs `gpsPickup`. The order's stamped farm point (with `pickupClassification`,
`specs/contracts/delivery-verification-api.md:1513`) or the listing point could stand in.

## 5. Offline

The table is Android, where the sync queue holds every write. Web is below it.

| Fix | Taken | Queued with | How the server judges its age |
|---|---|---|---|
| Handoff (pickup, drop-off, self-pickup) | at the scan (`qr_scan_screen.dart:261`), kept in Isar with the delivery | latitude and longitude only, without the fix's own time or accuracy (`gps_capture_service.dart:136-156`, `delivery_repository.dart:479-484`), in an operation stamped at the confirm tap | by distance only, whatever its age. The pickup's operation time becomes `pickupCapturedAt` when it is at most 5 minutes ahead and 30 days old (`TransactionPatternRules.cs:148-151`, `:401-411`), read by `IMPOSSIBLE_DELIVERY_TIME`. The drop-off keeps no phone time |
| Arrival check-in | at the tap | `capturedAt` set when queued (`mobile/lib/features/order/data/repositories/order_repository.dart:798`, `:861`) | only the receipt time is judged against the deadlines; `capturedAt` is kept as evidence when plausible (`PickupJudgement.cs:144-147`); one received late is kept and never counted |
| Live trip ping | at the slot | the platform's own fix time (`geolocator_location_source.dart:122`); when more than 24 wait, one fix per hour is kept (`mobile/lib/core/sync/location_ping_rules.dart:57-60`); web keeps them in memory only | accepted up to 7 days old (`server/src/Helpers/LocationPingRules.cs:67-68`) and flagged `late` after 15 minutes; its effects need a capture after the pickup (`CheckInEffects.cs:50-51`); the trucker's evidence needs the last check-in within 90 minutes of the confirm (`server/src/Helpers/TruckerGpsEvidence.cs:86`, `:152-153`) |

**Web has no offline path for handoffs and check-ins.** The web sync queue refuses to enqueue
(`mobile/lib/core/sync/sync_queue_web.dart:14-22`), and the web repositories post each handoff and check-in to
`/sync` at once: the pickup (`mobile/lib/features/delivery_verification/data/repositories/web_delivery_repository.dart:296-297`),
the drop-off and self-pickup confirm (`web_delivery_repository.dart:346-347`) and both arrival check-ins
(`mobile/lib/features/order/data/repositories/web_order_repository.dart:599-600`). So on web nothing is taken
offline and sent later: a handoff or check-in made without a network fails at once, its fix with it, and has to be
made again online. Live trip pings on web wait in memory only while the page stays open
(`web_location_ping_repository.dart:11-17`).

So only pings are judged by their age. A handoff fix carries no age at all. On Android the fix a phone takes offline
at the scan is the right one (it belongs to the scan), so the gap is in evidence, not correctness: an admin cannot tell how
old a drop-off point was when it reached the server.

## 6. Options

| Option | Proves | With GPS off | Battery | Data on 3G | RAM and APK | Policy and privacy | Verdict |
|---|---|---|---|---|---|---|---|
| QR scan as the proof, GPS as evidence (today) | the other party's server-held QR at the handoff | works | none | none extra | none | none | keep as the base |
| Photo proof (built: pickup and drop-off optional, arrival required for the drop-off check-in) | what the goods looked like; no place, EXIF is stripped (`mobile/lib/features/delivery_verification/data/services/photo_manager_io.dart:20`) | works | camera only | one 800 px WebP at 75% per photo (`photo_manager_io.dart:12`), sent when online | nothing new | photos stay admin and party only | keep as evidence |
| Network or cell location | a rough place; often several km in rural areas | needs location on, works without a GPS signal | low | none extra | none | same permission | already mixed in: the high accuracy request uses the fused provider. Record a coarse fix with its accuracy; never trust it at 5 km |
| Picked place or woreda centroid | nothing about presence | works | none | none | the place list is in the APK already | none | fine as a planned point (listing, drop-off), never as handoff evidence; record which one a listing used |
| Periodic in-transit pings (built, LT) | where the truck was, about hourly | no | one medium accuracy fix an hour, every 15 minutes in the last 10 km, a low power wake every 5 minutes | about 7 KB on the wire per hourly ping, about 170 KB a day (`specs/features/live-trip.md:104`) | in process, no new package | foreground service of type location, Play Console declaration; consent sheet; opt-in per trip | keep opt-in |
| Full background tracking | a continuous trail | no | high on 2 GB phones | high | a second permission flow | `ACCESS_BACKGROUND_LOCATION` (Android 10 and later; from Android 11 "Allow all the time" is granted only on the settings page); Play's background location declaration and review; a continuous record of a person's movements | reject |

### Recommended: QR proof with recorded GPS evidence

1. The QR scan (or the typed code) stays the only proof of a handoff. GPS stays evidence, required only where it is
   today: a typed code on a paid delivery, the self-pickup confirm, and the arrival check-ins.
2. Record every missing or weak fix: each handoff sends why there is no fix (location off, denied, denied for good,
   timed out, approximate only), and with a fix its capture time and accuracy. Server only, shown to admins on the
   dispute page. No fraud weight for now (FUP-440).
3. Close the client holes: the server's GPS refusals become permanent, reverted and explained (FUP-439); the
   self-pickup screen waits for a fix as a typed code does (FUP-138); web captures get a real bound and a correct
   permission state (FUP-441); every screen says why and offers the settings page (FUP-447).
4. Record whether a listing's point came from GPS or a picked place, and judge a picked point accordingly (FUP-442).
5. Keep live tracking as built: opt-in, foreground service, no background permission. Record a "no fix" event so a
   phone without location is not shown as a network gap (FUP-443).

| Cost | Of the recommendation |
|---|---|
| Battery | no new fixes; the reason and accuracy come from the attempt already made. Live trip unchanged |
| Data | a few dozen bytes on each handoff operation already sent; no new request |
| RAM on 2 GB phones, APK | no new package (`geolocator` is already in `mobile/pubspec.yaml:59`); no maps or tiles |
| 3G and offline | no new round trip; the fields travel in the queued operation |
| Android 8 to 16, Play | no `ACCESS_BACKGROUND_LOCATION`. Android 8 already limits an app in the background to a few positions an hour, which is why the hourly cadence runs in the foreground service. The app targets SDK 36 (`mobile/android/app/build.gradle:48`), so the location foreground service needs the Play Console foreground service declaration, and the Data safety form must list precise location shared with the order's parties (FUP-446) |
| Consent and privacy | the reason, time and accuracy are not a location; server only and admin only, like `gpsPickup`. Live sharing keeps its per-trip consent sheet |

Not recommended: making GPS mandatory on every handoff (it blocks honest parties in no-signal areas and breaks
offline first), treating a missing fix as a fraud signal before there is data on how often honest phones lack one,
and background location.

## 7. Follow-ups

The full rows go into `TASK.md` with this spike. In short:

| ID | Sev | Gap |
|---|---|---|
| FUP-138 (new text) | P2 | Self-pickup fix rule decided: the server keeps GPS mandatory; the scan screen waits for a fix and says why there is none. Lands with FUP-439, before FUP-326 |
| FUP-439 | P1 | `DELIVERY_GPS_PROXIMITY` refusals: permanent, reverted, one text per case, mobile and web. Must land (with FUP-138) before FUP-326 turns `pickupClassification` on; companion of FUP-442 |
| FUP-440 | P2 | Record a missing fix's reason, and a fix's time and accuracy, on each handoff; show them on the dispute page |
| FUP-441 | P2 | Web location: bound every capture in Dart; tell a dismissed prompt from a blocked site |
| FUP-442 | P2 | Listing point source (`gps` or `place`) and the rule for judging a picked point at 5 km |
| FUP-443 | P3 | Live trip: a "no fix" event so the buyer and the admin do not read a dead GPS as no network |
| FUP-444 | P3 | Server bounds check on `gpsPickup` and `gpsDropoff` |
| FUP-445 | P3 | A check-in fix coarser than 1,000 m counts as unmeasured |
| FUP-446 | P2 | Play Console foreground service declaration and Data safety form before a release with live trip |
| FUP-447 | P3 | Reason texts and the settings action on the scan screen, crop create and trip mode |
| FUP-448 | P3 | The trucker's `GPS_ANOMALY` row without `gpsPickup`: decide a stand-in point |

## 8. For the tickets this feeds

- **FRD-11:** the pickup proof stands without GPS. The missing fix the fraud spec still lists as unrecorded is
  FUP-440.
- **FRD-14:** the dispute page shows "No GPS fix at the handoff" without a cause; FUP-440 adds it.
- **ESC-33:** the trucker's `GPS_ANOMALY` row is never judged without `gpsPickup`; FUP-448.
- **ESC-34:** the arrival needs GPS, which is the right incentive for a trucker; the risks are the picked listing
  point (FUP-442) and the web permission state (FUP-441). On a self-pickup the T7 judgement refunds a buyer whose
  confirm was refused for GPS while the buyer holds the goods (section 4), so FUP-439 and FUP-138 come before
  FUP-326 turns the flag on.
- **ESC-36:** before FUP-372 turns the flag on, a trucker without location loses T9 and meets T8; the texts already
  say why. A coarse fix counting as measured is FUP-445.
