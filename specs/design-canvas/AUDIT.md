# Page M ("White, role colours"): board by board audit

Audit of the 42 boards in this folder against the app, written 2026-11-20 with the change that built them. The boards are page M of the canvas "WeredaConnect Color Identity" (`canvas.json` lists them with their notes and the sketch rules). The Fieldway audit this page replaces is kept as `AUDIT-fieldway-2026-10.md`.

## The identity in short

- White ground, black text (`#1A1516`), one orange action per page (`#F0873A`, black ink, 54 px pill). The second button is black with white ink, the third an outlined pill. A selected chip or pill is black.
- One colour per role, worn by the signed-in person's app: farmer plum `#9A45A0`, trucker blue `#2B62C4`, buyer green `#18775F`. It marks links, amounts, progress, tint pills, the selected tab's dot and the person's own trail. In code it is `context.roleColors` (a `ThemeExtension`), never a static token.
- Bricolage Grotesque with Noto Sans Ethiopic.
- A floating black bar with three tabs and the orange create button inside it. Farmer: Home, My Crops, Orders. Trucker: Home, My Trips, Deliveries. Buyer: Deliveries, Market, Orders (no Home).
- Kilograms first, quintals in parentheses. Forms take kilograms; the stored and sent values stay quintals.
- Line sketches (coffee branch, truck, balance scale): decoration only, one per screen, hidden with large text, reduced motion, high contrast or bold text.

## Method

1. Every board was dumped to text and structure (sections, order, sizes, colours) and compared with its Fieldway version, so each change of words or layout was listed before any code was written.
2. Each area was built from its boards, then rendered in widget tests at 390 px with the app theme and fonts and looked at beside the board, in English and, where the board has them, Amharic and Afaan Oromoo, plus 320 px at 2x text.
3. The web shell and every tab and pushed page were opened in a real web build at 1280 and 800 px for the three test users. The admin app was opened in a running build with sample data.
4. The full test suite, the analyzer, the design token check and the web build ran on the merged branch.

## Board by board

"Built" means the screen follows the board. "Kept" lists what differs on purpose. "Left" lists what is not built, with the reason.

### System

| Board | State | Notes |
|---|---|---|
| Main | Built | The token sheet: `AppTheme` and `RoleColors` carry every colour; component themes cover buttons, inputs, chips, cards, sheets, dialogs, pickers, switches, tabs, tooltips, snackbars. |
| Foundations | Built | Map language: warm ground, role-coloured trail with white check-in rings, dotted gap over 90 minutes, dashed road ahead, dashed orange halo, truck as a black square with an orange truck, pickup ring in role colour, slate drop-off, a pale road under the route the truck uses (pickup, check-ins, drop-off) with a short crossing at each end and a side road to each other stop. Left: other roads and terrain (the app has no roads file). |
| EthiopianClock | Built (no change) | Every time goes through `ClockText`; am and om show the small 24 hour range. |
| CheckInFlow | Built (no change) | The request and the read are unchanged. |

### Join

| Board | State | Notes |
|---|---|---|
| Welcome | Built | Pastel header in the picked role's colour, "Your role" switch, language pills, orange "Login as ...", outlined Register. The switch only dresses the screen: the server decides the role and login still sends phone and password only. The last signed-in role is remembered on the device. Kept: the pill says "Works without network" (existing wording), the sketch shrinks or hides instead of overlapping the title. |
| RegisterRole, RegisterFayda, RegisterRoleDetails, RegisterPersonal, RegisterOtp | Built | Round back, step bars (done black), 32 px titles, choice cards (selected raised on paper), sand notes, black ID card and camera frame, paper OTP boxes. Kept: the language globe in the step header. |

### Farmer

| Board | State | Notes |
|---|---|---|
| FarmerHome | Built | Header with place, synced pill, 42 px name and sketch; raised action card; sand "on the road" card with the In Transit pill, ribbon and arrival window in plum; At a glance tiles (My Crops black, Escrow amount in plum, Deliveries with its link). Kept: account button beside the bell, the recent line. |
| MyCrops | Built | Eyebrow with the Farmer pill, 42 px title with sketch, raised cards, "3,200 of 6,000 kg ordered (32 of 60 quintals)". Own crops only: the query is untouched. |
| FarmerEmptyCrops | Built | Large sketch, "No crops listed yet", orange "List a crop", "Back to Home". |
| CreateCropListing | Built | 30 px header with sketch, crop pills (selected black with check), "Quantity (kg)" with the quintal echo, orange Save. Left: one glyph per crop (one sprout glyph for all), the raised location card with "Change". Kept: photos and voice note. |
| FarmerOrderRequest | Built | "New order" header, who has agreed, "It is your turn: accept or reject by ...", orange Accept over outlined Reject. |
| FarmerRejectConfirm | Built | Reject, reason, then the confirm sheet (red "Yes, reject order", outlined "Keep order"). The reject request is unchanged and sent only on yes. |
| FarmerJourney | Built | Sand Farmer pill, raised map card with the plum trail, sand payment steps, "Nothing for you to do now", contact card with Message and Call. |

### Trucker

| Board | State | Notes |
|---|---|---|
| TruckerHome | Built | White ground, "Today" at 42 px with the truck sketch, raised trip card ("3,000 of 5,000 kg loaded (30 of 50 quintals)"), flat orange tint action card, sand tiles, payments row. |
| CreateTrip | Built | Capacity in kg with the quintal echo, "Price per 100 kg (ETB)", black swap button, route preview on the map ground. Left: the board's decorative road curves. |
| TripDetail | Built | Booked card in kg with the role bar, asked booking raised with "It is your turn to answer", accepted on sand, black pickup note with "Open". Kept: Start Trip and Mark Delivered (black, so Accept stays the one orange action). |
| TruckerTripMode | Built | Sand sharing card with the next check-in time, next stop card with Call, stop list (done black, next blue), the arrival window on each later drop-off whose load is on the truck (one track read per such delivery, at most 6, once per load of the page; nothing offline or without an estimate). |
| TruckerShareConsent, TruckerPaused | Built | Restyled; the app's words are kept. |
| TruckerQrFailed | Built | For the scan failures the phone can tell on its own. Kept: "Type the code instead". Left: server refusals after confirm still show as a snackbar. |

### Buyer

| Board | State | Notes |
|---|---|---|
| Market | Built | "Deliver to" eyebrow, crop pills, raised cards with "2,800 kg left (28 quintals)" and the price in green. ALL crops from everyone: the query is untouched. Left: the header sketch does not fit beside the bell and the account button at 390 px. |
| CreateIntent | Built | Crop pills, "Quantity needed (kg)" with the echo, orange Find Matches. |
| CropDetail | Built | 38 px crop and price, farmer tile on sand, transport cards in kg with the per kg and per quintal price, price tile. |
| PlaceOrder | Built | Quantity box with round steppers, "fits this trip, 1,800 kg left (18 quintals)", raised breakdown. |
| BuyerOrderDetail | Built | Who has agreed, "Now waiting for ..., the trucker, to answer by ... You do not need to do anything.", Message and Call per party. Call shows once the number is on the device. |
| Payment, PaymentFailed, PaymentOffline | Built | Amount card in green, confirm it's you on sand, dashed status card; the failed and offline states with their two actions. Left: "Confirmed with Fayda" (the app does not know the method before the server asks). |
| BuyerDeliveries | Built | Stage labels, raised map card with "Open map", dashed waiting card, sand delivered card with "Release pending" and "Nothing for you to do". Kept: bell and account buttons in the header. |
| BuyerDeliveriesEmpty | Built | For all three roles, each with its own sketch, words and buttons. |
| BuyerLiveTrip, LiveTripAm, LiveTripOm | Built | Round back, crop pill, "Whole route" (a toggle), legend, window in green, sand driver card with Message and Call, the check-in chip beside the truck (inside the map, under the floating controls; at the top left when it has no room). |
| BuyerArriving | Built | Orange "Scan driver's QR" with Call beside it, sand checklist. |
| BuyerDelivered | Built | Sand trip summary, raised escrow card, the nothing to do line, black Rate buttons. |
| BuyerTripLog | Built | Black and sand stat tiles, check-ins card, pickup proof with the pickup photo and "View photo" when the delivery has one (`pickupPhotoRef`), price card with "Platform Commission · 5%" from the order's own rate and amount, for the buyer only. Left: the sack count (no delivery, order, check-in or track field carries a package count, in the app or in `specs/contracts/`). |

### Web and admin

| Board | State | Notes |
|---|---|---|
| WebDeliveries | Built | Sidebar (role mark, selected ground, black create button), tablet rail, search box, white cards, the live pane with Zoom in and Zoom out over its map (four steps of 1.5, about the truck). Kept: the Settings row. |
| AdminDispute | Built | The whole admin app is on the page M tokens and font: dark sidebar, cards, badges, the dispute page with breadcrumb, role-coloured party chips, tab track, black and outlined verdict buttons, the trail map and table. Left: the dispute count in the nav (a new request), amounts on the verdict buttons (risk of a wrong figure on a money button). |

## Screens without a board

Built in the same language: profile, settings, data usage, edit profile, delete account, messages inbox, conversation, payments list, escrow status, rating, reputation, appeal, report sheet, sync conflict sheet, bell sheet, account sheet, contact sheet, splash, forgot and reset password, status chips, banners, voice note.

## What moved

- **Buyer has no Home.** A buyer lands on Deliveries (web `/` goes to `/deliveries`). Their action cards ("Pay for the order", "Payment failed: pay again", "Confirm the delivery" and the others) now lead the Orders tab. Find Trips (ALL trips) is in the account sheet and the web sidebar. Buying intents are under Market, My Requests.
- **Farmer Deliveries** left the bar: Home's tile and the account sheet open it.
- **Trucker Payments** left the bar: Home's payments row and the account sheet open it.

## Owed

- Amharic and Afaan Oromoo for every new string are machine drafted. A native review is owed.
- A run on a real device.
- The website (`website/`) still wears the old teal; its stylesheet says it follows the app theme, which is no longer true.
- Unused strings left in the arb files: `capacity`, `pricePerQuintal`, `invalidCapacity`, `listsTripBooked`, `tripRemainingCapacity`, `homeTileHeldForYou`, `tripLoadedOf`, `quantityInQuintals`, `myCropsOrderedOf`, `myCropsNoOrdersYet`.
