# 05 Meron, buyer (honest)

- Account: `+251900191288`, userId `001cb93c-50a1-70c5-b8e2-aaf8c5887401`, display name `SIM-2026-11-19 Meron`, English.
- Device and network in the story: laptop on office Wi-Fi in Addis Ababa (web app), Android phone on 4G at Merkato.
- How it was driven:
  - **Web UI**: Playwright on `https://dev-app.weredaconnect.com`, headless Chromium, 1366x768 and 390x844. Screenshots `screenshots/web-*.png`. Video `video/web-meron-order-flow.webm` (96.7 s).
  - **API**: the teff order with Tolosa's trip, shaped like the phone's sync queue (`evidence/api-log.jsonl`, `who` = `meron`).
  - No Android run for Meron.
- Times are real UTC on 2026-11-19; the "day" is the simulated day.

## Diary

| Day, time (UTC) | Place, network | Wants | Tap or call (status) | What happened | Reaction | Tag |
|---|---|---|---|---|---|---|
| D1 08:38:13 | Office, Wi-Fi | Register | fayda-start 200, fayda-verify 200, register 201 | Account created | Fine | |
| D1 08:42:32 | Office, Wi-Fi, web | Log in on the web app | login form | Home loads, session survives reload | Fine | |
| D1 08:43:37 | Web | Try Amharic, Afan Oromo, back to English | Settings, language | Sticks across reloads on every screen. Each switch throws her from Settings to Home, and the Home "Recent" line stays in the previous language until reload | "Why am I on Home?" | [BUG-24] |
| D2 08:40:51 | API | Browse | `GET /crops` 200 (50 rows, hasMore), `GET /trips` 200 (7) | Listings of Abebe (2), Dawit (4) and about 25 other farmers; trips of Tolosa (1), Henok (4), 2 others | Sees everyone. Correct | |
| D2 08:43:49 | Web | Browse market and trips | Market, Trips tabs | Abebe's maize and Dawit's wheat x3 visible; Henok x4, Tolosa x1 trips visible | Three wheat lots from the same seller at different prices look odd | [BUG-2] |
| D2 08:43:55 | Web | Look at Abebe's maize | open listing | Photo box is an empty 510 px placeholder ("Photo not available yet"), price and seller pushed below. Map shows a waypoint "Gimbi" between Debre Birhan and Addis | Photos: sim listings were created with photo ids but no uploads, so not counted as a bug. Gimbi is in west Oromia | [BUG-28] |
| D3 08:41:38 | API | Order 300 kg of Abebe's teff on Tolosa's trip, landmark "Merkato, near Anwar Mosque" | `order create` success | cropCost 28,500 + transport 360 + commission 1,425 = 30,285 ETB | Clear | |
| D3 08:41:39 | API | Pay | `payment_submitted`: `AUTH_041` (above 10,000 ETB) | Fayda step-up (`/user/fayda-start`, `/user/fayda-link` 200), pay again: success, `funded` | One extra step, explained | |
| D3 08:49:57 | Web | Order 100 kg maize with Tolosa's trip | order form, Place Order (`POST /sync` 200) | Form opens with quantity 2,000 kg (the whole stock, 58,800 ETB). She typed in the drop-off field, text became garbled with a red "Pick a place from the list.", yet Place Order went through | "I nearly bought the whole lot." | [BUG-20] [BUG-18] |
| D3 08:51:49 | Web | Pay order `1fd6a372` from its own page | Pay Now, Telebirr, Submit Payment, password step-up | Paid and funded on the server. The app turned into a blank white page, nothing to tap, 15 s+, until she reloaded | "Did my money go? Should I pay again?" | [BUG-17] |
| D3 08:54:34 | Web, Wi-Fi drops | Order 50 kg maize, self-pickup; network drops at Submit Payment | Submit Payment offline | Snackbar "An error occurred" for a few seconds, then nothing. Back online 20 s later: no retry, screen unchanged | Does not know if it was the network | [BUG-19] |
| D3 08:55:48 | Web | Try again | Orders, order, Pay Now, Submit | "Payment submitted successfully!", funded. API: one order, one escrow, no duplicate | Recovered | |
| D3 08:56:45 | Web | Third small order from a reloaded order page | same path as 08:51 | Blank page again | Reproduced | [BUG-17] |
| D3 09:09:21 | Web | Pay the 20 kg video order in the normal flow | Place Order, Telebirr, Submit | Lands on the market list, not on the order | Has to find the order herself | [BUG-21] |
| D3 web | Web | Read the order detail | open order | Only "Total 3,060 ETB" and "Held in escrow". No crop, transport, commission split; trucker shown as "asked once the farmer accepts" | "What am I paying for?" | [BUG-22] |
| D4 08:44:49 | Addis, 4G | Confirm delivery too early | delivered while pending | `DELIVERY_INVALID_TRANSITION` | Correct | |
| D5 08:45:34 | Merkato, 4G | Confirm receipt | wrong code: `deferred`; no code: `DELIVERY_NONCE_MISSING`; GPS 345 km off: `DELIVERY_GPS_PROXIMITY dropoff_far`; right code at Merkato: success | Delivery and order `delivered` | Fine | |
| D5 08:45:35 | Merkato, 4G drops | Phone resends the confirm (response lost) | same op: `duplicate`; fresh key: `conflict` v4 | No double confirmation | Correct | |
| D5 08:47:33 | Home, Wi-Fi | Rate Abebe and Tolosa | 2 x `rating create` success; second rating for Abebe: `conflict` "Rating already exists" | One rating each | Fine | |
| D5 08:47:41 | Web or API | Check a seller's ratings | `GET /ratings?rateeId=dawit` | Sees Dawit's 5 stars, which came from his own second account | Misled | [BUG-3] |
| D6 08:48:24 | Home | Cancel an order placed by mistake | `order create` 10 kg, `cancelled` with reason: success | cancelled, nothing charged | Fine | |
| D6 09:04:46 | Web | Open farmer pages by URL | `/my-crops`, `/listings`, `/create-listing` | All redirect to `/` | Correct | |

## Web checks for Abebe and Tolosa (same Playwright run)

| Time (UTC) | Who | Check | Result | Tag |
|---|---|---|---|---|
| 08:46:53 | Abebe, Afan Oromo | Login | UI came up in Afan Oromo from his profile language | |
| 08:59:31 | Abebe | Tour: home, My Crops, orders, deliveries, payments, messages, profile | Only his data. `/find-crops`, `/marketplace`, `/trips` redirect to `/`. API with his token: only his crops, Dawit's 403 | |
| 09:01:05 | Abebe | Create-listing form, not submitted | Labels in Afan Oromo. Quantity in quintals, price per kg. A helper line shows the total, but 2,800 Birr/kg (a per-quintal price typed by mistake) gets no warning. Non-digits silently dropped, photo tiles icon-only | [BUG-26] |
| 08:47:16 | Tolosa, Amharic | Login and tour | Only his 1 trip and his orders. Henok's trip 403 | |
| 09:02:34 | Tolosa | First load after 15 min idle | 5 API calls 401, then the screen filled in. No visible error | |
| 09:03 | Tolosa | Opens another buyer's order URL | "ስህተት ተከስቷል" (an error occurred) and a back button, no reason. API 403, no data shown | [BUG-30] |
| both | both | Language leftovers | om dates "Sun 22 Nov", "Nov 19, 2026"; Recent line "Delivery Delivered" in English on am and om home; "ETB" on the profile escrow row; "Back" label in om | [BUG-L2] [BUG-25] |
| both | phone width 390 | Floating "Midhaan Gurguri" / "New Buying Intent" button | Covers the 4th order card and the Recent row | [BUG-27] |

## Web orders left on dev (all Meron, Abebe's maize `d0740ebc`, funded)
- `1fd6a372-7531-4755-b095-8d6cc4989d30`: 100 kg, Tolosa trip, landmark `SIM-2026-11-19 web order`, 3,060 ETB
- `d0d2ad9e-d29c-408d-9f92-7e1ada69dab6`: 50 kg, self-pickup, 1,470 ETB. No note: the self-pickup form has no note field
- `3fafa226-7b05-4b46-9bb1-f1f0cfcf4ced`: 10 kg, self-pickup, 294 ETB. No note possible
- `64ad1f63-79cd-4274-b225-c42c0a0f8d21`: 20 kg, Tolosa trip, landmark `SIM-2026-11-19 video order`, 612 ETB

## Result
Meron can buy from Abebe and book Tolosa end to end, on web and over the API. Visibility is right. The web payment step is where she gets hurt: blank page after a successful payment, a vague offline error, a form that defaults to the whole stock, and no cost breakdown on the order.

## Not tested
Android for Meron. Mouse scrolling of the clipped filter chip rows. Submit-time errors on create forms.
