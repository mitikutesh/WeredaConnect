# Fieldway canvas: board by board audit

Audit of the 35 boards in this folder (identical to the live canvas K6DUM33R6apPwa2jmdsLax) against the app, done for PR #255 on 2026-10-29.

## Method

1. **Phrases.** Every visible phrase of every board was extracted and searched in what the app ships: the en arb values, Dart string literals, admin TSX and admin i18n. A phrase matched when its words appear in one string (placeholders ignored).
2. **Unmatched phrases.** Each one is resolved in the table below: sample data the app fills into a template, an existing template with other words, or a real difference (built in PR #255, or kept on purpose).
3. **Layout.** The live trip boards (Trucker trip mode, Share consent, Offline and pause, Buyer live trip, Farmer order on the road) were rendered next to the app's screens (board HTML in a headless browser, the screens in widget tests with the app theme and fonts). Trip mode, the consent and pause sheets and the farmer screen were then restyled to the boards. The other boards were aligned in #254 and PR #255 from their markup.
4. **Phone boards against the real app (PR #256).** The web build against the dev API was opened signed out and signed in as the farmer, trucker and buyer test users, and the app screen for each of these 16 boards was shot at phone width beside it: Welcome, Register role, Register Fayda, Farmer home, My Crops, Create listing, Crop detail, Farmer order, Trucker home, Create trip, Trip detail, Market, Create intent, Place order (the buyer's crop detail with transport and costs), Buyer order detail and Buyer deliveries. The other 14 phone boards show states the dev data cannot reach (a truck on the road, a real Fayda verification, an order waiting for payment), so the same running web app was driven in a headless browser that answered the screens' API reads with sample data (a delivery on the road with its track, a delivered one with its escrow, a trip with the trucker's saved sharing state, an order waiting for payment, the Fayda start and verify replies and a 202 register) and let nothing write to dev; the share consent sheet was reached through a real pickup scan of a QR shown to a fake camera. Shot that way beside their boards: Trucker trip mode, Share consent, Offline and pause, Buyer live trip, Farmer order on the road, Register OTP, Register personal, Register role details, Payment, Buyer arriving, Buyer delivered, Buyer trip log, and the Amharic and Afaan Oromoo live trip boards (the app switched to am and om on the login screen). All 30 phone boards were compared in the running app. Messages and Profile have no board. The gaps found were fixed and shot or rendered again (see "Built in PR #256").
5. **Web and system boards in the running apps.** Buyer · Live deliveries: the web build at 1280 px with four sample deliveries (two on the road, one waiting, one delivered). Admin trip evidence: the admin app (local dev server) with a seeded admin session and the dispute and its trail answered with sample data. Time on the Ethiopian clock: the web build's Settings (Clock: Auto, Ethiopian, International) and the live trip in am and om. How a check-in travels: trip mode's Check in now in the web build, with the request it sends logged (`POST /sync`, `entityType: location_ping`, key `{truckerId}:location_ping:{tripId}#{capturedAt}:create:{capturedAt}`), and the buyer's map reading `GET /deliveries/{id}/track`. The gaps found were fixed and shot again (see "Built after #256").

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
| Overnight, until 06:00 (Offline and pause, at 10:55) | The app offers the overnight pause only from 12:00 Ethiopia time: the longest pause the engine sends ends at 06:00. |
| 87 km done / 47 km to go / 4 h 05 as three cards (Buyer trip log) | The trip log keeps its one stats card, the same one the delivered summary uses. |
| Rate Farmer and Rate Trucker side by side (Buyer delivered) | The rating buttons are the shared ratings section the order detail also shows, one under the other. |
| Teff · 1,500 kg / Chickpea · 800 kg on the waiting and delivered cards (Buyer · Live deliveries) | Only a delivery on the road reads its order for the crop line (one read per truck on the road, kept small for 3G); waiting and delivered ones keep the delivery card (number, parties, status). |
| Koka, Mojo, Bishoftu in the check-ins table and on the map (Admin trip evidence) | The admin app has no town list, so places show as coordinates and the map labels each point with its time. |
| Timeline, Photos and Messages tabs (Admin trip evidence) | Only the Trip trail tab is built; the other evidence tabs belong to FRD-14. |
| Farmer wins · release 29,500 ETB / Buyer wins · refund 30,900 ETB | The buttons do not promise an amount: what moves depends on the disposition the admin picks next (return the goods, delivered anyway, settle now). The frozen card shows the split instead. |
| Ethiopian / International (Clock in Settings) | The app also offers Auto, which follows the language: the board's "Follows the language until the person picks one". |

## Built in PR #255 after this audit

Login language switch; registration step counter; Create trip as one page (Repeat Last Trip, swap, route preview, 24 h booking cutoff, two fields per row); My Crops status chips, header, listed date and ordered quantity; Find Crops transport line and Market "Deliver to"; suggested transport from Find Crops; trucker load bar; trip detail notes; farmer reject reason; checkout Fayda and password note; order summary card; driver card truck and plate; delivered report window; trip log stop-ahead estimate; trip mode, consent, pause and farmer screens restyled to the boards; admin Decide and the away-stop check (`awayStops`, live-trip-api 1.0.6).

## Built in PR #256

- Light headers as on the boards: the app bar and page share the chalk background with dark text; Home, My Crops, Find Crops and browse trips lose the green band, and Market and Find Crops show "Deliver to" with the board's place tile. Home shows the sync chip under the greeting and count tiles as label plus big number (the trucker keeps the dark header).
- My Crops cards: price per kg, status under the price, ordered share bar; buyer cards show the transport line as a pill, highlighted when a trip runs this week. The privacy note sits in a blue box.
- Crop detail header: icon tile, big title, quantity and listed date, big price, status chip.
- Create listing: quantity and price side by side with labels above (the create trip form's `LabeledFieldPair`, now shared).
- Order detail: crop and quantity as the page title with the status chip, the other party under it, and the viewer's amount in a white card with the escrow lock line.
- Deliveries: buyer and farmer see the list under stage headings (On the road now, Waiting for pickup, Delivered, Disputed, Cancelled); the trucker's list is unchanged.
- Trip detail: the full-width dark header with day, price and route, and once orders are booked how much of the truck is booked with "Booked trips keep their date, price and route."; Cancel trip is a plain outlined button (the confirm dialog still asks).
- Create intent: crop chips wrap instead of scrolling, quantity and maximum price side by side, no duplicate "Find Crops" bar title.
- Market: Browse and My Requests as the board's segmented switch.
- Crop detail: a farmer card (initials, name, rating) in place of the Farmer and Farmer Rating rows.
- Registration: each step starts under the progress bar instead of in the middle of the screen. Personal info puts the labels above the fields, the display name first and the two passwords side by side.
- Confirm your phone (OTP): top aligned, one box per digit over the real field (keyboard, paste and SMS autofill unchanged), the expiry and Resend on one line, the title on the page only (not repeated in the bar).
- Payment: the amount grouped by thousands ("68,400 ETB").
- Buyer live trip and Truck arriving: the map runs edge to edge with the details on a white sheet over its foot (spec section 4: map hero plus bottom sheet).
- Buyer delivered: a green check beside "Delivered at", and while the money waits out the 48 h window one escrow card (amount, Release pending, You confirmed delivery, Check window ends, who gets what, Report a problem). Five new strings in en, am and om; any other escrow state keeps the full timeline.
- Admin trail: "stops off the stop list" now also judges each pause against the trip's planned stops (every non-cancelled order's listing location and the trip's destination), read from the trips, deliveries and crops tables the admin function already reads; scans alone judge when those reads fail (`nearestPlannedStopKm`, live-trip-api 1.0.6).

## Not built

- "Tap any time to see it on the other clock" (Time on the Ethiopian clock, rule 3): not in the live trip spec, and a tap on every time would fight the buttons many times sit in ("Report a problem until..."). The 24-hour time already shows beside every Ethiopian deadline and window (rule 2).
- The am and om strings need a native speaker's review before release: tracked as FUP-86 in TASK.md, which lists every key #255 and #256 added.

## Built after #256

- Buyer · Live deliveries (web): from 560 px wide, the live trip's check-ins sit in their own card beside the details under the map, with who sees the truck.
- Admin trip evidence: the parties as chips under the title, the frozen amount in its own card with the split (crop, trucker, commission), Decide in the trail's right column, each map point labelled with its time and a stop off the stop list ringed.
- Confirm your phone: the title on the page only.
