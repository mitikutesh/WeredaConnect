# Fieldway canvas: board by board audit

Audit of the 35 boards in this folder (identical to the live canvas K6DUM33R6apPwa2jmdsLax) against the app, done for PR #255 on 2026-10-29.

## Method

1. **Phrases.** Every visible phrase of every board was extracted and searched in what the app ships: the en arb values, Dart string literals, admin TSX and admin i18n. A phrase matched when its words appear in one string (placeholders ignored).
2. **Unmatched phrases.** Each one is resolved in the table below: sample data the app fills into a template, an existing template with other words, or a real difference (built in PR #255, or kept on purpose).
3. **Layout.** The live trip boards (Trucker trip mode, Share consent, Offline and pause, Buyer live trip, Farmer order on the road) were rendered next to the app's screens (board HTML in a headless browser, the screens in widget tests with the app theme and fonts). Trip mode, the consent and pause sheets and the farmer screen were then restyled to the boards. The other boards were aligned in #254 and PR #255 from their markup.
4. **Phone boards against the real app (PR #256).** The web build against the dev API was opened signed out and signed in as the farmer, trucker and buyer test users, and the app screen for each of these 16 boards was shot at phone width beside it: Welcome, Register role, Register Fayda, Farmer home, My Crops, Create listing, Crop detail, Farmer order, Trucker home, Create trip, Trip detail, Market, Create intent, Place order (the buyer's crop detail with transport and costs), Buyer order detail and Buyer deliveries. The other 14 phone boards show states the dev data cannot reach (a truck on the road, a real Fayda verification, an order waiting for payment), so the same running web app was driven in a headless browser that answered the screens' API reads with sample data (a delivery on the road with its track, a delivered one with its escrow, a trip with the trucker's saved sharing state, an order waiting for payment, the Fayda start and verify replies and a 202 register) and let nothing write to dev; the share consent sheet was reached through a real pickup scan of a QR shown to a fake camera. Shot that way beside their boards: Trucker trip mode, Share consent, Offline and pause, Buyer live trip, Farmer order on the road, Register OTP, Register personal, Register role details, Payment, Buyer arriving, Buyer delivered, Buyer trip log, and the Amharic and Afaan Oromoo live trip boards (the app switched to am and om on the login screen). All 30 phone boards were compared in the running app. Messages and Profile have no board. The gaps found were fixed and shot or rendered again (see "Built in PR #256").
5. **Web and system boards in the running apps.** Buyer · Live deliveries: the web build at 1280 px with four sample deliveries (two on the road, one waiting, one delivered). Admin trip evidence: the admin app (local dev server) with a seeded admin session and the dispute and its trail answered with sample data. Time on the Ethiopian clock: the web build's Settings (Clock: Auto, Ethiopian, International) and the live trip in am and om. How a check-in travels: trip mode's Check in now in the web build, with the request it sends logged (`POST /sync`, `entityType: location_ping`, key `{truckerId}:location_ping:{tripId}#{capturedAt}:create:{capturedAt}`), and the buyer's map reading `GET /deliveries/{id}/track`. The gaps found were fixed and shot again (see "Built after #256").

Final phrase run: 85 unmatched phrases (78 unique), all resolved below. Welcome, the five Join boards, Create trip, Payment, Buyer order detail and Share consent have none.

6. **Structure, at every width (2026-10-30).** Steps 1 to 5 still let most screens keep their old structure: a phrase matches whatever layout it sits in, and the shots were taken at phone width only. Windows from 600 px (tablet rail, desktop sidebar) ran separate older layouts, the web shell drew a stock bottom bar, and the theme was still the old teal with Noto Sans. This pass compared every board by structure (which sections, in what order, what each card shows, header, switches, labels, sizes) in the running web build at 390 px and 1280 px for the farmer, trucker and buyer, and rebuilt the screens to match. See "Structure pass" at the end.

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
| Recent: pickup scanned at Koka 09:40 (Trucker home) | Home shows one Recent line in the board's shape; its text is the latest event from the app's activity feed. |
| Overnight, until 06:00 (Offline and pause, at 10:55) | The app offers the overnight pause only from 12:00 Ethiopia time: the longest pause the engine sends ends at 06:00. |
| Rate Farmer and Rate Trucker side by side (Buyer delivered) | The rating buttons are the shared ratings section the order detail also shows, one under the other. |
| Koka, Mojo, Bishoftu in the check-ins table and on the map (Admin trip evidence) | The admin app has no town list, so places show as coordinates and the map labels each point with its time. |
| Timeline, Photos and Messages tabs (Admin trip evidence) | Only the Trip trail tab is built; the other evidence tabs belong to FRD-14. |
| Farmer wins · release 29,500 ETB / Buyer wins · refund 30,900 ETB | The buttons do not promise an amount: what moves depends on the disposition the admin picks next (return the goods, delivered anyway, settle now). The frozen card shows the split instead. |
| Ethiopian / International (Clock in Settings) | The app also offers Auto, which follows the language: the board's "Follows the language until the person picks one". |
| The bell alone at the top right of each phone tab (every phone board) | A round account button follows the bell (on Trucker home, the Synced chip) wherever no web sidebar is drawn: the phone app and a browser under 600 px. It opens what the sidebar lists beside the bar's tabs (Messages with its unread count, Payments or the trucker's Orders), then Profile and Settings. Users missed them in the bell's sheet (2026-10-31), so the sheet now holds notifications only. |

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

## Live trip boards after the Fieldway theme (structure pass)

The live trip screens were shot again in the web build (phone width and 1280 px, am and om included) after the theme moved to Manrope and field green, and brought to the boards' structure, not only their colours:

- Buyer · Live trip and Truck arriving: no app bar; the back button and the crop pill float over the map, the sheet (radius 28) opens with the arrival window beside the status pill, the steps colour the current one gold, the escrow line is plain with a lock, the driver card is a chalk tile with the tinted avatar and the white Message and green Call squares, "Trip log and order" is a row with a top border. Arriving: the green Arriving pill, the checklist in a chalk box, Scan driver's QR beside a Call square.
- Buyer · Delivered: the back button and the blue "Sharing ended at delivery" pill head the page, the drop-off proof sits inside the summary card, the escrow steps carry "then" beside the payout, the ratings sit in a "How did it go?" card.
- Buyer · Trip log: the back button beside the title, three stat cards (the first filled green), one Check-ins card with a connected timeline ("Bishoftu · latest", "next" and "then" in the time column, "Sent late, no network until 12:22"), one price card with the unit price on the crop row, the help card with Message Dawit and Message Almaz side by side, the privacy line plain.
- Farmer · Order on the road: the Farmer pill in the header row, the map in a card with the check-in chip and "Arrives" under it, the payment steps as dots with "expected today", the contact card with blue and gold avatars, the order link as a bordered row.
- Trucker · Trip mode, Offline and pause, Share location: the back button beside the trip title on the dark header, the offline card in the header, the stops as a coloured timeline with the next stop gold and a dashed line beyond, a Call square on the next stop, the pay in birr ("4,500 ETB"); the pause choices with "back at" on the same line.

Entries of the "kept" table that changed with it: the trip log now has the board's three stat cards; the Message buttons sit side by side with first names. Still kept: the Delivered rating buttons one under the other (the shared ratings section), "Who has agreed" as the shared commitment section, no photo on the pickup proof, "Show QR" said as "Open delivery", "Pause for 30 minutes" said as "Pause sharing", the transport row without "20 quintals × 150 ETB" (the order carries the cost, not the trip's unit price).

## Structure pass (2026-10-30)

The web app still did not look like the canvas after PRs #254 to #257 (Trucker Home showed a light centred header, six tiles, "No active trip" and a Recent list where the board has the dark header with the trip card, one Action needed card, three tiles and one Recent line). Causes, and what this pass changed:

- **Wide windows ran other layouts.** From 600 px the tabs drew separate tablet and desktop compositions that no pass had touched. They are gone: every tab body is the phone board's column, centred at 720 px from 600 px wide (`FwContentWidth`). The one web board, Buyer · Live deliveries, keeps its list beside the live trip pane, in the board's proportions (a 340 px list; at 1280 px the pane shows the details and the check-ins side by side).
- **The web shell and the theme were older.** The web shell drew a stock bottom bar with plum and sky role colours and an end-aligned button, and `AppTheme` was still the teal theme with Noto Sans. `AppTheme` now carries the Fieldway tokens (field green, ink on chalk, whisper borders, money brown, gold), Manrope (static 400 to 800 from the OFL release) with the Ethiopic fallback, and button, input, card, chip, snackbar and dialog themes in the board sizes. Web and phone share one bottom bar (`RoleNavBar`) with the gold or dark create button centred over it; the desktop sidebar follows the Web board.
- **Screens kept their structure.** Rebuilt to their boards: Home for the farmer and trucker (the buyer on the farmer's pattern), My Crops, Market (Find Crops with Browse and My Requests), Deliveries for all three roles (Active, Delivered and All, stage labels, crop-titled cards), Welcome (now the login board), the five Join boards, the live trip boards (section above), and the detail and create boards: Trip detail, Create trip, Create listing, Crop detail, Place order, Telebirr payment, the buyer's order detail, the farmer's order request and Create intent, each with the square back button, one column at every width and its main action pinned at the foot. Screens without a board (My Trips, Orders, Payments, Profile, Settings, Data usage, Messages, Delete account, Forgot and Reset password, Delivery detail, Create delivery, Show and Scan QR, both Raise Dispute screens, Initiate payment, Match results) use the same header, section labels, switches, white cards and pinned actions; Delivery detail follows the buyer's order detail (four step timeline, role's next step at the foot). The bell sits at the top right of every tab, as on the boards.

Backend: delivery-verification-api 1.21.0 stamps the order's crop and kg, the listing's place and the trip's departure day on each new delivery row, from reads the create already makes, so the deliveries cards read "Teff · 1,500 kg" and "Kebede Alemu, Bichena · pickup Thu 30 Oct" without one order read per row. Rows created before it keep the delivery number as their title.

The trip's day on orders: the server has stamped `pickupDate` (the trip's departure) on every trip order at funding since `order-lifecycle.md` 2.0.0, and the app now reads it, so the buyer's order header, the farmer's pickup row and the trucker's Action needed card name the trip's day as on the boards ("From Almaz Bekele · Thu, Oct 30 trip"). An order funded before 2.0.0, or not yet funded, has none and leaves the day out. A trip order's timeline shows the boards' Live trip step after Accepted.

Button labels: a screen's own `styleFrom(textStyle: ...)` replaces the theme's button text style instead of merging with it, so 41 action buttons drew their label in the platform face without the Ethiopic fallback. Each now names Manrope and the fallback, and `chip_font_fallback_test.dart` fails on any override that does not.

Also from that check: days read as the boards write them ("Thu 30 Oct", a date field "Thu 30 Oct 2026") through `board_day.dart`; crop tiles take the boards' colours per crop (`cropTileColors`: wheat, barley and sesame cream, teff, sorghum and coffee sand, the pulses olive, maize green) and every tile draws the boards' sprout (`FwSprout`, the canvas's own path); phone fields group the digits as they are typed ("0911 234 567"); the trucker home card reads "Next: drop-off in Dukem, about 12:35" from the drop-off's own track (the server answers its trucker too) and "Next: pickup in Meki" from the listing's place, with the party's name only while offline with no stored track; the driver's truck and plate may take two lines in the Web pane. Match results draws each match as the Market listing card; Create trip's From and To run full width under the swap button; the intent's price hint and the OTP's grouped phone ("+251 911 234 567") read as on their boards; and at 1280 px the Web deliveries pane has the board's proportions (a 220 px sidebar with the board's 36 px mark, a 336 px list, two columns of about 320 px with the smaller driver avatar and the short sent-late pill).

Checked after these changes in the running web build (dev API) at 390 and 1280 px for the three roles: every tab, Welcome, Trip detail, Create trip, Crop detail, Create intent, order detail, Delivery detail (pending, self-pickup, trucker), QR show and scan, both dispute screens and Match results; the live trip and Join boards in the same build. Initiate payment cannot be opened on the web (its pay button needs the phone's database), so it was rendered from a widget test with the app's fonts at 390 and 1280 px in en and am.

Closed in the same pass: Trip detail's booking rows name each order's own places ("Almaz Bekele, Meki → Selam Traders, Merkato"), which order create now stamps from the listing and the buyer's profile it already reads (order-lifecycle 2.2.0; an older order shows the trip's route); the suggested transport on Crop detail and Place order ends with the trucker's real rating when they have one; the Place order quantity groups thousands as it is typed; the Web deliveries list has the board's compact cards without stage labels (phone width keeps its labels and cards, as on the phone board); and Find Crops lists first what a trip this week can carry to the buyer's place, then what some open trip leaves from, then the rest, every listing kept, under the board's footnote ("sorted by the best match for Merkato").

The Register personal picker now reads "Meki · Oromia" with a chevron, and Verify on Register OTP is active from the first digit (a short code asks for the rest and sends nothing).

Not differences, for the record: the app has no Transport segment on Market, as the board has none (buyers reach every trucker's trip from Home's Find Trips, `/trips` and the crop detail's suggested transport). Delivery detail has no board; its foot carries no amount because the delivery row has none (the order detail and Payment show it). Payments has no board; it shows no held and released total because the history loads a page at a time, so a total of the loaded rows would be wrong (Home and Profile show the escrow total from every order).

Still different after this pass: none. Home's headers now draw only what the boards draw (the bell and the Synced chip; the greeting, Today and the chip on Trucker home). Messages, with its unread count, and Profile open from the top of the bell's sheet on every tab with the bell, as the phone bar has no tab for them; the desktop sidebar lists them too. (Since 2026-10-31 they open from the account button after the bell; see "Real differences kept on purpose".) On a phone the Synced chip opens what waits to sync. The Find Crops pill reads "Transport Thu · 18 q room" and Crop detail's suggested transport "18 quintals room", the boards' words. The privacy rules of the earlier passes stay as specified (other stops never named, no sack count).

## Board by board on the final build (2026-10-31)

Every board of the canvas (this folder is its archived copy) was set beside the app on the final build of PR #259, at 390 px and, for the web board, 1280 px. "Live" is the web build against the dev API with its real data; "faked" is the same build with the screen's reads answered by sample data (live dev data has no such state); "render" is a widget test with the app's theme and fonts, for a screen the web build cannot open.

The archive was then checked against a download of the live artifact (K6DUM33R6apPwa2jmdsLax, exported 2026-10-31). It has the same 35 pages, and every page matches its file here tag for tag, attribute for attribute (inline styles included) and word for word. The only differences are how the export writes markup (`viewBox` as `sc-camel-view-box`, tables as `sc-raw-table`, self-closing tags written out), not what it draws. So the table below holds for the live design too.

After #259 deployed, test trip 0aac4288 on dev got its destination point (trip-listing-api 2.1.0), and the arrival line was checked live for all three roles: trucker home "Next: drop-off in Dire Dawa, about 14:55", the buyer's deliveries "14:55-15:15", the farmer's home "Arrives 14:55-15:15". Real distances showed one gap the sample data hid: the buyer's card read "About 166.3 km to go". The boards write whole km from 10 km (47 km to go, 87 km done, 134 km driven) and one decimal below (3.2 km away, 0.8 km from the farm); the app now does the same.

| Board | Checked | Result |
|---|---|---|
| Welcome | live | matches |
| Register role, Fayda, role details, personal, OTP | live (fake Fayda portal) | match; the place picker reads "Meki · Oromia" with a chevron, the phone groups as typed, Verify is active from the first digit |
| Farmer home | live | matches (bell only; the on-the-road card shows when a load is in transit) |
| Trucker home | live (a real trip put on the road on dev) and faked | matches: dark header, trip card with In Transit and the load, one Action needed card, three tiles, one Recent line, centred Post Trip; the next stop reads "Next: drop-off in Addis Ababa, about 05:25" (faked) and, live, names the drop-off from the order until the trip has a destination point (trip-listing-api 2.1.0, needs the deploy) |
| My Crops | live | matches (sprout tiles in the crop's colours) |
| Market | live and faked | matches: listings with a trip this week first, "11 trips this week" and "Transport Mon · 18 q room" pills, the board's footnote |
| Crop detail | live | matches (suggested transport as radio cards, cost card, Place Order + Transport) |
| Place order | live | matches ("1,500" grouped, "= 15 quintals", the trucker card when a trip is picked) |
| Payment (Telebirr) | live (order write answered) | the board's Telebirr screen follows the app's payment method step (CBE or Telebirr, no board) |
| Create listing | live | matches; the app adds Record Audio Note under the description (no board), and the headless browser has no GPS |
| Create trip | live | matches (full width From and To under the swap button) |
| Create intent | live | matches |
| Trip detail | live | matches; booking rows name each order's places once the order has them (order-lifecycle 2.2.0, needs the deploy) |
| Buyer order detail, Farmer order request | live | match ("From … · Sun 1 Nov trip", the Live trip step, "Sun 1 Nov · …'s truck") |
| Buyer deliveries | live (the real trip on the road) | matches: Active · count, ON THE ROAD NOW, "Teff · 100 kg", the ribbon, "Checked in just now" |
| Buyer live trip, Arriving, Delivered, Trip log, live trip am and om | faked | match; live, the arrival window waits for a trip with a destination point (trip-listing-api 2.1.0) |
| Farmer journey | faked | matches |
| Trucker trip mode, Offline and pause | faked | match |
| Trucker share consent | render | matches; the fourth row gives the app's measured data use ("about 170 KB a day") where the board says "about 3 KB a day" |
| Buyer · Live deliveries (web) | live and faked, 1280 px | matches: compact cards without stage labels, the map, details and check-ins side by side |
| Admin trip evidence (web) | unchanged in this PR | checked in #257 |
| Ethiopian clock, How it works (system) | the live trip boards in am and om, Settings | the clock rules hold; "tap any time to see the other clock" stays not built (see "Not built") |
