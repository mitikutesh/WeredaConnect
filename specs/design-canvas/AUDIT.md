# Fieldway canvas: board by board audit

Audit of the 35 boards in this folder (identical to the live canvas K6DUM33R6apPwa2jmdsLax) against the app, done for PR #255 on 2026-10-29.

## Method

1. **Phrases.** Every visible phrase of every board was extracted and searched in what the app ships: the en arb values, Dart string literals, admin TSX and admin i18n. A phrase matched when its words appear in one string (placeholders ignored).
2. **Unmatched phrases.** Each one is resolved in the table below: sample data the app fills into a template, an existing template with other words, or a real difference (built in PR #255, or kept on purpose).
3. **Layout.** The live trip boards (Trucker trip mode, Share consent, Offline and pause, Buyer live trip, Farmer order on the road) were rendered next to the app's screens (board HTML in a headless browser, the screens in widget tests with the app theme and fonts). Trip mode, the consent and pause sheets and the farmer screen were then restyled to the boards. The other boards were aligned in #254 and PR #255 from their markup.
4. **Every screen, in the real app (PR #256).** The web build against the dev API was signed in as the farmer, trucker and buyer test users, and each role's screens (home, My Crops, crop detail, create listing, Find Crops, Market, browse trips, create trip, trip detail, orders, order detail, deliveries, messages, profile) were shot at phone width next to the board for the same screen. The layout gaps found that way were then fixed (see "Built in PR #256").

Final phrase run: 85 unmatched phrases (78 unique), all resolved below. Welcome, the five Join boards, Create trip, Payment, Buyer order detail and Share consent have none.

## Unmatched phrases

### Sample data (names, places, crops, quantities, times, amounts)

The app fills these from the order, trip, delivery, listing or track; the template is named where it is not obvious.

| Phrase on the board | Where the app shows it |
|---|---|
| Dawit Tesfaye Alemu, Selam Traders, Hawi Flour Mill, Dukem, Merkato, Addis Ababa, Fri 31 Oct, Clean and dry, in 100 kg sacks... | names, places, dates and listing descriptions from the data |
| Welcome back, Almaz / Welcome back, Dawit | `welcomeBackUser` |
| Due today 18:00 / Due today 16:00 | `actionDueBy` with the clock's deadline |
| Abebe Trading · Wheat · 1,200 kg; Abebe Trading · Addis Ababa | action card and order rows: party, crop, `quantityKg` |
| Selam Traders · arrives 13:20-14:00 | farmer home live trip card: buyer and arrival window |
| Wheat · 28 quintals left; 28 quintals left · listed Mon | Find Crops and crop detail: crop, quantity |
| Meki · listed Mon / Meki · listed today | My Crops: `myCropsListedOn` (date as "Oct 26") |
| Almaz Bekele, Meki · 4.8 (12 ratings); Tolosa Gemechu, Koka · 4.9 (21 ratings); Kebede Alemu, Bichena · 4.6 (8 ratings) | listing card: seller, place, rating |
| Dawit Tesfaye · Thu 30 Oct; Abdi Kedir · Sat 1 Nov; Meki → Addis Ababa · 18 quintals room · 4.7; Ziway → Addis Ababa · 40 quintals room · 4.5 | suggested transport cards on crop detail and place order |
| Thu 30 Oct · 150 ETB per quintal | trip detail header: `tripPricePerQuintalValue` |
| Almaz Bekele, Meki → Selam Traders, Merkato (and the other booking rows) | trip bookings section |
| Thu 30 Oct · Dawit Tesfaye's truck | farmer order: pickup row |
| Today · 2 orders · 30 of 50 quintals | trip mode header: `homeToday`, `tripModeOrders`, `tripLoadedOf` (PR #255) |
| Pickup · Tolosa Gemechu, Koka; Drop-off · Hawi Flour Mill, Dukem; Drop-off · Selam Traders, Merkato | trip mode stops: `tripModeStopPickup`, `tripModeStopDropoff` |
| Maize · 1,000 kg · scanned 09:40; Maize · 1,000 kg · about 16 km · 12:35 | trip mode stop detail: cargo, `tripModeScannedAt` |
| 2,000 kg to Selam Traders, Merkato | farmer live trip: `liveTripFarmerTo` |
| From Almaz Bekele, Meki · Dawit Tesfaye; Almaz Bekele, Meki · Dawit Tesfaye; Gemechu Farm, Asella · Abdi Kedir; Kebede Alemu, Bichena · pickup Thu 30 Oct | deliveries cards: `liveTripFrom` and party names |
| Merkato, Addis Ababa | Market "Deliver to" (PR #255) and the drop-off note |
| 13:38 · Nifas Silk; 5:42 ganama · Bishooftuu | live trip check-in chip: clock time and nearest town (Ethiopian clock in om) |
| Bishoftu · latest | trip log: town and the `liveTripLogLatest` tag |
| Merkato · you | trip log: the drop-off note, else `liveTripYou` |
| Message Almaz / Message Dawit | `liveTripMessageParty` |
| Maize · 1,000 kg; Buyer · Hawi Flour Mill · raised Tue 12:50; Farmer · Tolosa Gemechu | admin dispute page: parties and dates |
| Selam Traders · Merkato | deliveries: buyer and drop-off |

### Afaan Oromoo board (the app's om strings with live values)

| Phrase | om key |
|---|---|
| Geejjibarra Jira | `liveTripStatusOnTheRoad` |
| Galmee imalaa fi ajaja | `liveTripTripLogLink` |
| Daqiiqaa 28 dura | `liveTripAgo` |
| Yeroo Merkaatoo ga'u | `liveTripArrivingAt` |
| Fe'ame 2:05 ganama | `liveTripStepPickedUp` with `clockMorning` |
| · 13:20-14:00 · gara km 47 hafe | arrival window and `liveTripKmToGo` |
| Birrii 91,200 keessan hanga QR ... tursiifama. | `liveTripEscrowNote` |

### Same meaning, other words (existing templates)

| Phrase on the board | The app's string |
|---|---|
| 1 check-in saved on this phone (10:41, Mojo). It sends by itself when you are back in coverage. | `tripModeOfflineWaiting` (the count, without the time and town of the waiting check-in) |
| Likely no network on this road. ... Your 91,200 ETB stays in escrow. | `liveTripLateNote`, with the escrow line `liveTripEscrowNote` beside it |
| Checked in 28 min ago / 3 min ago · 3.2 km away | the freshness chip (`liveTripAgo`) and `liveTripKmFromDropoff` |
| Almaz gets 84,000 · Dawit gets 3,000; Almaz is paid 84,000 ETB and Dawit 3,000 ETB, 48 h after you confirm delivery. | `liveTripPayoutNote` |
| Drop-off proof: QR scanned 14:02 · GPS 0.3 km from Merkato; Dawit scanned Almaz's QR · 0.8 km from the farm | `liveTripDropoffProof`, `liveTripPickupProof`, `liveTripQrScannedAt` with the GPS distance |
| Delivered Mon · report a problem until Wed 16:10 | delivered rows: `liveTripReportProblemUntil` (PR #255) |
| Please answer by today 18:00.; The farmer accepted. Please answer by today 16:00. | `commitmentAnswerBy`, `actionAnswerTransport` |
| Resend in 45s | `resendOtpIn` |
| 30,900 ETB frozen | admin `disputes.frozen` (PR #255) |
| Every verdict is written to the audit log with your name. | admin `disputes.auditNote`: "under your admin account" (the server stores the admin's user id) |
| No impossible jumps, legs at 25 to 27 km/h | admin `trail.saysNoJumps`: "the fastest leg was {kmh} km/h" |
| WeredaConnect Admin | admin `app.title` |
| Koka · other stop, 22 min | trip log: "Another stop on this truck" with `liveTripLogStopAhead` (PR #255) |

### Real differences kept on purpose

| Phrase on the board | Why the app differs |
|---|---|
| Dukem · other stop; Koka · other stop | Other orders' stops are never named, not even by town (live-trip spec section 7; as built in LT-11). The app says "Another stop on this truck". |
| QR scanned 08:05 · GPS 0.8 km from the farm · 20 sacks | No sack count is recorded (LT-11 as built). |
| 10:55 · near Mojo (Offline and pause header) | Trip mode's header shows the trip (day, orders, load, route), not the phone's current town. |
| 28 min ago · next one due by about 12:45 | The buyer's trip log shows the age of the latest check-in; the next one's time is shown to the trucker in trip mode ("next by"). |
| Recent: pickup scanned at Koka 09:40 (Trucker home) | The home's Recent Activity list shows the latest events in its own format. |
| Active · 2 / Delivered · 1 / All (Buyer · Deliveries) | The tab keeps its status chips (they filter on the server); the All list is grouped under the board's stage headings instead. |

## Built in PR #255 after this audit

Login language switch; registration step counter; Create trip as one page (Repeat Last Trip, swap, route preview, 24 h booking cutoff, two fields per row); My Crops status chips, header, listed date and ordered quantity; Find Crops transport line and Market "Deliver to"; suggested transport from Find Crops; trucker load bar; trip detail notes; farmer reject reason; checkout Fayda and password note; order summary card; driver card truck and plate; delivered report window; trip log stop-ahead estimate; trip mode, consent, pause and farmer screens restyled to the boards; admin Decide and the away-stop check (`awayStops`, live-trip-api 1.0.6).

## Built in PR #256

- Light headers as on the boards: the app bar and page share the chalk background with dark text; Home, My Crops, Find Crops and browse trips lose the green band, and Market and Find Crops show "Deliver to" with the board's place tile. Home shows the sync chip under the greeting and count tiles as label plus big number (the trucker keeps the dark header).
- My Crops cards: price per kg, status under the price, ordered share bar; buyer cards show the transport line as a pill, highlighted when a trip runs this week. The privacy note sits in a blue box.
- Crop detail header: icon tile, big title, quantity and listed date, big price, status chip.
- Create listing: quantity and price side by side with labels above (the create trip form's `LabeledFieldPair`, now shared).
- Order detail: crop and quantity as the page title with the status chip, the other party under it, and the viewer's amount in a white card with the escrow lock line.
- Deliveries: buyer and farmer see the list under stage headings (On the road now, Waiting for pickup, Delivered, Disputed, Cancelled); the trucker's list is unchanged.
- Admin trail: "stops off the stop list" now also judges each pause against the trip's planned stops (every non-cancelled order's listing location and the trip's destination), read from the trips, deliveries and crops tables the admin function already reads; scans alone judge when those reads fail (`nearestPlannedStopKm`, live-trip-api 1.0.6).

## Not built

- The am and om strings need a native speaker's review before release.
