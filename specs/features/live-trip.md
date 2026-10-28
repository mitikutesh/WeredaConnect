# Feature: Live Trip (where the truck is after pickup)

**Status**: APPROVED 2026-10-28 (decisions D1 to D8 in §3, set by the user on the design canvas)
**Tasks**: EPIC LT in `TASK.md` (LT-01 to LT-13)
**Contract**: `specs/contracts/live-trip-api.md` 1.0.0 (new)
**Design**: canvas https://claude.ai/artifact/K6DUM33R6apPwa2jmdsLax (35 boards: Live trip for all roles, the Ethiopian clock, and the core marketplace screens drawn in the same look)
**Author**: AI-assisted
**Date**: 2026-10-28
**Related**: `delivery-verification-api.md` (QR handoff, GPS proximity, "Real-time GPS tracking" in Future Enhancements), `order-lifecycle.md` 2.1.0 (commitments, contact per party), `notifications-api.md` (push types, `PushText`), `specs/features/three-party-escrow-commitments-reputation.md` §1.5 ("GPS check-ins" as evidence)

---

## 1. What

After pickup, the trucker's phone reports where the truck is. The buyer and the farmer of that order see it on a map that the phone draws from data the app already ships.

1. **Check-ins, not live streaming.** The phone records a GPS fix about once an hour, at each QR scan, every 15 minutes when the truck is within 10 km of a drop-off, and when the driver taps "Check in now" or "I've arrived". With no network the fixes wait in the sync queue and go up later with their real capture time.
2. **An honest map.** The map shows the trail from the viewer's own pickup, the last check-in with a halo that grows with its age, the road ahead as a dashed line, and an arrival window ("13:20-14:00"), never a fake exact minute. States: fresh, normal, late (no network), paused, scans only, ended.
3. **Optional for the driver.** After the first pickup scan the driver is asked once per trip. A driver who declines is shown as "Scans only"; the QR scans still prove pickup and delivery.
4. **Ends by itself.** Sharing stops when every delivery on the trip is delivered, cancelled or disputed. The driver can pause for a rest stop or the night.
5. **Useful events.** The first check-in 2 km past a pickup moves that delivery to `inTransit` (the manual button stays as a fallback). A check-in within 5 km of a drop-off pushes `truck_nearby` to that buyer, once. Impossible speeds and mock-location apps raise fraud signals. Admins see the full trail as dispute evidence.
6. **Ethiopian clock.** Amharic and Afaan Oromo show times on the Ethiopian clock across the app and in push messages; English keeps the 24-hour clock.

## 2. Why

- The most common question after pickup is "where is my crop?". Today the only answer is a phone call. Buyers and farmers who can see progress call less and trust the escrow more, and drivers are interrupted less while driving.
- Hourly check-ins fit rural reality: cheap phones, patchy network, 2 GB RAM, costly data. A continuous stream would drain batteries and fail on most roads anyway.
- The trail is evidence. A dispute about a short delivery or a missed drop-off is easier to judge with the route and its stops next to the QR, GPS and photo proof.
- Times on the Ethiopian clock are how most Amharic and Oromo speakers read the day. A deadline shown as "18:00" to a farmer who reads "12 in the evening" invites missed answers.

## 3. Decisions

| # | Decision | Set by |
|---|---|---|
| D1 | The map is drawn on the phone from shipped data (the bundled place list, later an optional roads file). No map tiles, no map data cost. | User, 2026-10-27 |
| D2 | Location sharing is optional for drivers ("Scans only" when declined). | User, 2026-10-28 |
| D3 | Amharic and Afaan Oromo use the Ethiopian clock, always with the part of the day; English keeps the 24-hour clock. A Clock setting follows the language until the person picks one. | User, 2026-10-28 |
| D4 | Dates stay on the Gregorian calendar. | Default (the user was told; not asked for) |
| D5 | Cadence: hourly, at each scan, every 15 min within 10 km of a drop-off, and on tap. | Design |
| D6 | Live location is never stored on trip rows (every buyer can browse every trip). It lives only behind the delivery's track endpoint. | Design (role rule) |
| D7 | Each party sees the trail only from their own pickup until their delivery; other stops on the truck are never named. | Design (privacy) |
| D8 | The labeled create FAB floats above the bottom bar instead of docking into a 48 px gap it covers today. | Design (bug fix) |

## 4. Experience per role

Board names refer to the design canvas.

**Trucker** (Trucker · Share consent, Trip mode, Offline and pause)
- After the trip's first pickup scan: a one-time sheet per trip, "Share where the truck is?", with who sees it, how often, when it stops, and the data cost (the daily figure LT-08 measures, §5.3). "Share trip location" or "Not now, scans only".
- Trip mode: the trip header, sharing state with last and next check-in, Pause, Check in now, the next stop with I've arrived and Show QR, the stop list (pickups and drop-offs of this trip, with names: the trucker is a party to every order), and the pay for the trip.
- Pause sheet: 15 min, 30 min, 1 hour, overnight until 06:00. Sharing resumes by itself. QR scans still record their place.
- Offline banner: how many check-ins wait on the phone.
- Android shows a foreground notification while sharing is on.

**Buyer** (Buyer · Deliveries tab, Live trip in 5 states, Trip log, Arriving, Delivered; Web · Buyer deliveries)
- Deliveries tab: an "On the road now" card with a route ribbon, the freshness chip and the arrival window; waiting-for-pickup cards say the map starts at pickup.
- Live trip: map hero plus bottom sheet (arrival window, distance left, 4-step progress, escrow line, driver card with message and call, trip log link).
- Trip log: check-ins with times and places, "sent late" tags, other stops unnamed, pickup proof, price breakdown, who has agreed, help text (no cancel after pickup, 48 h to report after delivery), privacy note.
- Arriving (truck within 5 km): checklist and the Scan driver's QR button.
- Delivered: trip summary (distance, time, check-ins), "sharing ended at delivery", escrow timeline, report a problem until the window ends, Rate Farmer and Rate Trucker.

**Farmer** (Farmer · Order on the road)
- "Your wheat is on the road": map card from the farmer's pickup, freshness, arrival window, a note that grey rings are other stops, the payment steps (paid into escrow, handed over, buyer confirms, 48 h, paid out), contact rows, and a link to the order details.

**Admin** (Web · Admin dispute evidence)
- A Trip trail tab on a dispute detail page (new: the admin Disputes page is a list today, and FRD-14 plans the full evidence view): map, the check-ins with leg distance, time and speed, pauses, what the trail says (shared the whole way, no impossible jumps, stops off the stop list), and a reminder that the trail shows where the truck was, not what it carried.

**Every role**: freshness chips, the route ribbon and the map language follow the Foundations board.

## 5. Check-in model

### 5.1 Kinds

| Kind | Coordinates | When |
|---|---|---|
| `fix` | yes | hourly, at a scan, near a drop-off, Check in now, I've arrived (`source` says which) |
| `paused` | no | the driver paused; carries `pausedUntil` |
| `resumed` | no | the driver resumed early, or the pause ran out on the phone |
| `declined` | no | the driver chose "Not now, scans only" for this trip |

### 5.2 Cadence on the phone

- After an accepted consent, the phone records a fix at once and then:
  - every 60 minutes after the last fix of any source (a scan or a tap resets the clock),
  - every 15 minutes while the last fix is within 10 km of any undelivered drop-off on the trip,
  - on a pickup or drop-off scan, on Check in now and on I've arrived.
- Fixes use the medium accuracy setting with a 60 second timeout. No fix in time: skip, try at the next slot, never retry in a loop.
- Mock-location detection (`Position.isMocked`) is sent as `mocked: true`.
- While paused no fix is taken. The pause ends at `pausedUntil` or when the driver resumes.
- The phone stops sharing (and the foreground service) when its local copy shows every delivery on the trip delivered, cancelled or disputed, or when the server answers `LOCATION_SHARING_ENDED`.

### 5.3 Offline and the queue

- Each fix and event is a `location_ping` create in the existing sync queue (FIFO), so it waits offline like every other write.
- Coalescing: when more than 24 location pings wait unsent, keep at most one `fix` per clock hour plus every event, so a long dead zone does not flood the queue ahead of a delivery scan.
- About 600 bytes per ping in the sync request body with real ids (the operation envelope is most of it), so about 15 KB a day per truck at the hourly rate. A ping that goes up alone also carries the HTTP headers with the ID token, and often a new TLS connection, a few KB more. LT-08 measures the real daily figure with `DataUsageTracker` before the consent sheet quotes one.

### 5.4 Android

- A foreground service with a visible notification runs the cadence while sharing is on. It is started from the consent sheet, while the app is in the foreground, so the "while in use" location permission is enough (no `ACCESS_BACKGROUND_LOCATION`).
- The installed `geolocator_android` 5.0.4 supports the foreground notification (`ForegroundNotificationConfig`); no new package is needed.
- The manifest adds `FOREGROUND_SERVICE` and `FOREGROUND_SERVICE_LOCATION`; the Play Console foreground-service declaration (location type) is filed before release. The consent sheet is the in-app disclosure.
- Web drivers check in only while the page is open (manual and a visible-page timer); no background.

## 6. Server

### 6.1 Ingest (`location_ping` via `POST /sync`)

Accepted only when all hold (each refusal code is in the contract):
- the caller is the trip's trucker;
- the trip has at least one delivery that is `pickedUp` or `inTransit`, or that reached `delivered`, `cancelled` or `disputed` after `capturedAt` (late offline pings are kept);
- `capturedAt` is at most 2 minutes in the future and at most 7 days old;
- for `fix`: latitude 3 to 15, longitude 32.5 to 48.5 (Ethiopia with a margin), accuracy 0 to 5,000 m;
- at most 20 fixes per trip per clock hour.

Stored in `{prefix}-trip-locations`: PK `tripId`, SK `{capturedAt}#{kind}`, coordinates rounded to 4 decimals (about 11 m), `receivedAt`, `source`, `accuracyM`, `mocked`, `pausedUntil`, `expiresAt` = `capturedAt` + 60 days (TTL; the same attribute name as the rate-limits, notifications and fraud-signals tables, not `ttl` as on match-events). Duplicates (same SK) are idempotent successes.

### 6.2 Track (`GET /deliveries/{deliveryId}/track`)

- Readable by the delivery's buyer, farmer and trucker only. Anyone else: `404` (no existence leak).
- Before pickup: `sharing: not_started`, no position.
- From the delivery's `pickedUpAt` until `confirmedAt`: the trail of fixes in that window, coordinates rounded to 3 decimals (about 110 m), the last fix, the sharing state, pickup and drop-off points, other stops, the arrival window and the distance left.
- After delivery: `sharing: ended` and a summary (distance, duration, fix count), no coordinates.
- Drop-off point: the buyer's registered place, else the trip's destination place, plus the order's `dropoffNote` when set (LT-12).
- Other stops: the other undelivered pickups and drop-offs on the same trip as `{kind, done}` with coordinates rounded to 2 decimals (about 1 km) and no names, ids or quantities.
- Arrival window: distance left = great-circle distance from the last fix to the drop-off × 1.3, plus 20 minutes for each other undelivered stop that is nearer to the truck than this drop-off; speed = median of the last up to three legs, clamped to 20 to 60 km/h (40 when there is only one fix); centre = last fix time + distance ÷ speed; half-width = 10 min + a quarter of the last fix's age, capped at 90 min; rounded to 5 minutes; the start is never before now.

### 6.3 Side effects of an accepted fix (best effort, never fail the ingest)

- **Auto in transit.** For each `pickedUp` delivery on the trip whose `gpsPickup` (else the listing location) is 2 km or more from the fix: `pickedUp → inTransit` as a system actor, with the existing order coupling (`in_transit`) and a transaction-audit row.
- **Truck nearby.** For each `pickedUp` or `inTransit` delivery whose drop-off point is within 5 km of the fix and that has no `nearbyNotifiedAt`: stamp it and push `truck_nearby` to that buyer.
- **Fraud signals.** Over 130 km/h between consecutive fixes more than 1 km apart: `GPS_JUMP`. `mocked: true`: `GPS_MOCKED`. Both through the existing fraud signal writer.

### 6.4 Admin (`GET /admin/deliveries/{deliveryId}/trail`)

Admin group only. Every fix and event from pickup to delivery at 4 decimals, with leg distance, minutes and speed, and the pauses.

As built (LT-13): the response with the QR scan points, the `coarse` and `late` flags, the jump flag on each leg (the `GPS_JUMP` rule), the gaps (over 90 minutes without a check-in, pauses left out) and the summary is in `live-trip-api.md` section 3 (1.0.5). The admin app shows it in a Trip trail tab on a new dispute page (`/disputes/{escrowId}`, linked from the Disputes list), a minimal shell FRD-14 adds its evidence tabs to. Not built: "stops off the stop list" from section 4, which needs the planned stops of every order on the trip; a follow-up for FRD-14.

## 7. Privacy and roles

- Farmer: only their own order's trail; other stops unnamed. Trucker: their own trips. Buyer: their own deliveries. Admin: dispute evidence. Nobody can follow a trucker before pickup or after delivery.
- Trip rows, browse, matching and messages never carry live location (D6).
- Party reads at 3 decimals, other stops at 2, admin at 4. Rows expire after 60 days.
- The consent sheet says who sees what and for how long; Settings can change the default.

## 8. The map (D1)

- Data: the bundled `mobile/assets/data/ethiopia_woredas.json` (3,860 GeoNames places, 70 KB gzipped, already in the APK), the trail and the stops. No tiles. A roads layer (OpenStreetMap trunk and primary roads, estimated 100 to 150 KB gzipped) is a follow-up, not in this epic; without it the map draws towns, the real trail and a dashed line ahead.
- Rendering: a Flutter `CustomPainter` with an equirectangular projection fitted to the trail, stops and drop-off; no new map package; the same code on web (no `dart:io`).
- Labels: towns inside the padded bounds ranked by population plus the nearest town to each fix; greedy collision removal; at most 10. Check-in rows in the trip log name the nearest town too.
- Credits on the about or licences screen: "© GeoNames (CC BY 4.0)" (and "© OpenStreetMap contributors" once roads ship).
- Place data fixes (LT-02), found while validating the design on 2026-10-27:
  - display names without transliteration marks ("Mek’ī" → "Meki", "‘Alem T’ēna" → "Alem Tena", "K’ok’a" → "Koka");
  - aliases: Adama (the city is only "Nazrēt" today, and "Adama" finds a village in Amhara), Batu (Ziway), and "Debre Zeyit" pointing at Bishoftu instead of a village in Benishangul;
  - Amharic and Oromo names: 37 and 4 of 3,860 places have them. An overrides file curated for the top towns, each entry marked for native review.

## 9. Ethiopian clock (D3)

- Ethiopian hour = (24-hour hour − 6) mod 12, with 0 shown as 12. Minutes unchanged.
- Part of the day, from the 24-hour hour: 00 to 05 night, 06 to 11 morning, 12 to 17 day, 18 to 23 evening.
  - Amharic, before the time: ከሌሊቱ, ከጠዋቱ, ከቀኑ, ከምሽቱ ("ከቀኑ 7:20").
  - Afaan Oromo, after the time: halkan, ganama, waaree booda, galgala ("7:20 waaree booda").
- Never a bare hour. Ranges repeat the part of the day only when it changes ("ከቀኑ 11:30-ከምሽቱ 12:30").
- Deadlines and arrival windows also show the 24-hour time in small text in Amharic and Oromo.
- Clock setting: Auto (default: Ethiopian for am and om, international for en), Ethiopian, International.
- One formatter on the client (Dart) and one on the server (C#, `PushText`), both tested against `specs/contracts/fixtures/ethiopian-clock-cases.json`.
- Storage, deadlines and contracts keep UTC; only display changes.

## 10. Drop-off landmark (LT-12)

The place list has towns, not neighbourhoods (Merkato, Kality and Saris are missing), so "Addis Ababa" is too vague for a driver. The buyer may add an optional `dropoffNote` (up to 80 characters, the same phone-number guard as listing descriptions) when placing an order, editable until pickup. The trucker sees it on the stop list once committed; the buyer's map labels the drop-off with it.

## 11. Bottom bar FAB (LT-01, D8)

`mobile/lib/screens/main_navigation.dart` docks a labeled `FloatingActionButton.extended` ("New Buying Intent", "Sell Crops", "Post Trip", about 190 dp) into a 48 px `SizedBox` gap of the `BottomAppBar`, so the button covers the second and third tabs. The labeled button stays (it is deliberate for low-literacy users) and floats centred above the bar; the bar loses the notch and spaces its four tabs evenly; scrollable bodies get bottom padding so the button never hides the last item. The web `AdaptiveShell` FAB is checked for the same problem.

## 12. Contracts

- New: `specs/contracts/live-trip-api.md` 1.0.0 (the entity, the endpoints, the push type, the error codes).
- New fixture: `specs/contracts/fixtures/ethiopian-clock-cases.json`.
- Bumped by the ticket that implements the change: `sync-protocol.md` (LT-05, the `location_ping` entity, its permanent codes and the `deferred` `LOCATION_NOT_ACTIVE`), `delivery-verification-api.md` (LT-07, the system `pickedUp → inTransit` and `nearbyNotifiedAt`), `notifications-api.md` (LT-04 the clock in push text, LT-07 `truck_nearby`), `order-lifecycle.md` and `data-models.md` (LT-12 `dropoffNote`).

## 13. Constraints check

- **Offline first**: pings queue offline; the buyer's map shows the last snapshot with its age; Fayda and payments already say they need network.
- **Low-end phones and data**: no tiles, no new packages, about 15 KB a day of check-in bodies per truck (§5.3), about 2 KB per map refresh, a repaint only when data changes.
- **Roles**: §7. Farmer never sees another farmer's crop; buyer browse is unchanged.
- **Mobile and web**: every new screen exists on both. New Isar schemas get their web model pair; sync executor and queue changes land on both sides.
- **Three languages**: every new string in en, am and om; the Amharic and Oromo copy on the canvas needs a native speaker's review before release.

## 14. Tickets

One PR per ticket, stacked in this order (each PR's base is the previous ticket's branch, the first is based on this spec's branch):

| ID | Scope | Summary |
|---|---|---|
| LT-01 | @Mobile | FAB floats above the bottom bar (§11) |
| LT-02 | @Mobile @Data | Place data: display names, aliases, Amharic and Oromo overrides (§8) |
| LT-03 | @Mobile @l10n | Ethiopian clock formatter, Clock setting, applied app-wide (§9) |
| LT-04 | @Server | `PushText` deadlines on the Ethiopian clock for am and om (§9) |
| LT-05 | @Server @Infra | Location ingest: table, `location_ping` sync entity, validation (§6.1) |
| LT-06 | @Server @Infra | Track endpoint and arrival window (§6.2) |
| LT-07 | @Server | Side effects: auto in transit, `truck_nearby`, fraud signals (§6.3) |
| LT-08 | @Mobile | Check-in engine: schema pair, sync, cadence, foreground service (§5) |
| LT-09 | @Mobile @l10n | Trucker UI: consent, trip mode, pause, check in now, arrived (§4) |
| LT-10 | @Mobile | Track client with offline snapshot, and the map painter (§6.2, §8) |
| LT-11 | @Mobile @l10n | Buyer and farmer Live trip screens, deliveries card, trip log, arriving, delivered (§4) |
| LT-12 | @Server @Mobile @l10n | Drop-off landmark note (§10) |
| LT-13 | @Server @Admin | Admin dispute evidence: trail endpoint and Trip trail tab (§6.4) |

## 15. Not in this epic

- Restyling the existing screens to the canvas look (the canvas draws them in the new look only to show nothing is lost). A separate decision.
- The roads layer, road-snapped routes and turn-by-turn navigation.
- The Ethiopian calendar.
- iOS background location (the app targets Android 8+ and web).

## 16. Risks

- **Battery on old phones**: one fix an hour with medium accuracy is cheap; the 15-minute mode is limited to the last 10 km.
- **Vendor-killed services** (aggressive battery savers on some Android brands): the queue and the scans still work; the map shows "late" honestly.
- **Spoofing**: mock-location flag, impossible-speed check, and the QR scans remain the money evidence; the trail is supporting evidence only.
- **Clock confusion between languages**: the part of the day is always shown and deadlines carry the 24-hour time too.
