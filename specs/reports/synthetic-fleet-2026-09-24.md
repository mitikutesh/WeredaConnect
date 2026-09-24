# Synthetic User Fleet Simulation: dev stack, 2026-09-24 (run `sim0924`)

**What ran.** 13 agents against the deployed dev stack (server `dc95aca`, webapp `881c64d`,
API `vhhm8gh9c1`): 9 user personas (3 farmers, 2 truckers, 4 buyers) each with its own freshly
registered account, split across "mobile" (offline sync-queue behaviour: batched ops, idempotent
retries, stale-version replays), "web" (one op per sync + immediate re-read) and device-switching;
plus a negative-testing validator, a real-browser (Playwright) web-app checker, an admin checker,
and a real Android app smoke on the emulator. Every HTTP call was logged with an
`X-Correlation-Id` (`sim0924-<persona>-<platform>-<seq>`); every bug below has request + response
evidence in the persona finding files. Findings without evidence were dropped. Raw bundle:
`SIM/` in the session scratchpad (`findings/*.json`, `reports/*.md`, `logs/*.ndjson`).

**Flows that completed end to end**
- With-truck: buyer browses ALL crops/trips → chats with farmer (crop) and trucker (trip) →
  order → pay (mock auto-fund) → escrow `escrow-order-{id}` → farmer accepts → trucker creates
  delivery → farmer persists QR nonce → trucker `pickedUp` (nonce+GPS) → `inTransit` → buyer
  `delivered` (nonce+GPS) → escrow confirmation stamped, 48 h dispute window → buyer rates
  farmer + trucker (duplicate rating → conflict). Done twice (buyer-meron, buyer-yonas).
- Self-pickup (no trucker): company buyer → `/match` → order without transport → pay → farmer
  accepts → buyer creates `self_pickup` delivery → farmer nonce → buyer `delivered` with GPS
  within 5 km of the listing (far GPS, no GPS, wrong nonce, `pickedUp` all correctly rejected)
  → 2-way split reconciles (300 + 15 = 315) → rating.
- Dispute: buyer-sara (mobile↔web) intents + `/match` (score matched the contract formula
  exactly) → order → delivery → `POST /escrow/dispute` → escrow `disputed`, idempotent replay,
  cancel/release/refund by buyer or farmer all rejected.
- Cross-device: the same human on mobile then web saw identical crops, orders, messages
  (including messages sent from the other device); stale offline edit → `conflict` with
  server data; corrected edit succeeds.

**What held up (no bug)**
- Role visibility: farmer sees only own crops, trucker only own trips, buyer sees all
  (10 farmers, 5 truckers). Cross-role reads → 403 (`CROP_FORBIDDEN`, `TRIP_FORBIDDEN`,
  `MATCH_FORBIDDEN`). Cross-user reads/writes on another user's crop, trip, order, delivery,
  escrow (`ESCROW_022`) and conversation all denied; third-party message into a conversation
  rejected; deleted listing hidden from browse, detail (404) and `/match`.
- Money safety under races: double-tap create, double pay from two devices, concurrent
  `delivered`: exactly one success, one escrow, no double charge. Client-set `funded` /
  `settled` / `accepted`-as-buyer rejected. Stale replay → conflict, state unchanged.
- QR nonce never leaked in any response; wrong nonce → `DELIVERY_NONCE_MISMATCH`; replay →
  `duplicate`; nonce not yet synced → `deferred` (per sync-protocol).
- Auth: 3 failed logins → `AUTH_015` lockout with `retryAfterSeconds 900`; no phone
  enumeration; 401 on every protected route; refresh after logout fails; buyerType/companyName
  validation (F-32) works.
- Limits: 51-op batch → 400 before processing; 6th listing/hour → fraud velocity; duplicate
  listing → fraud duplicate; pagination `limit`/`nextToken` validation on every list endpoint.
- Admin API: 15 GET + 3 POST `/admin/*` routes × {no token, farmer id, buyer id, farmer
  access} = 72 calls, all 401; 17 unauthenticated deep links redirect to login with zero API
  calls; admin bundle has Cognito config baked in.
- Web app: cold load 2.7 s, real login form works, 33 role×route loads with no JS errors,
  fleet data from "mobile" appeared on web, language switch / logout / session restore /
  360 px layout OK. Repo e2e: webapp 53/54, admin 35/36 (both failures are stale tests).
- Latency: every API call < 3 s (most < 1.5 s), no 5xx, no 429 at fleet pace.

---

## Bug list (deduplicated by root cause, 37 items)

Severity: **P0** = money/ownership/data-integrity breach or app unusable · **P1** = a role's
flow broken or a money rule not enforced · **P2** = wrong behaviour / contract deviation that a
conformant client hits · **P3** = cosmetic, docs, test maintenance.
Scope tags follow `.ai/ownership.md`. "Found by" = persona finding file(s) holding the evidence.

### P0

| ID | Scope | Bug | Root cause / evidence |
|---|---|---|---|
| SIM-01 | @Mobile | **Android app cannot reach the API: certificate pinning rejects every request, login impossible** (release builds included, `enableCertPinning` defaults true). | Leaf pin in `mobile/lib/core/network/api_client.dart:48-51` was extracted 2026-04-07; the API Gateway leaf rotated 2026-06-26 (SPKI `Ng6d…` vs pinned `n+bs…`). Worse, `cert_pinning_io.dart` uses a `SecurityContext` without trusted roots, so `badCertificateCallback` only ever receives the ROOT cert (Amazon Root CA 1, `++MB…`), which is not pinned; the matching M04 intermediate pin can never match. logcat: `[CertPinning] REJECTED … no pin matched (hash=++MB…)`. Verified with openssl from the host. Found by mobile-smoke, orchestrator. |
| SIM-02 | @Server | **Crop update accepts `farmerId` (and `farmerName`/`farmerPhone`)**: a farmer can move a listing into any user's account or spoof contact details. | `SyncFunction.cs` crop update checks the EXISTING owner, then `CropRepository.UpdateAsync` writes every allowed field including `farmerId`. Repro 3× (to a new farmer, a trucker, a buyer). Found by validator. |
| SIM-03 | @Server | **Trip update accepts `truckerId`/`truckerPhone`**: same ownership transfer / spoof for trips. | Trip update only skips `tripId/version/createdAt/updatedAt` (`SyncFunction.cs` ~L2378); `truckerId` is in `AllowedTripFields`. Found by validator. |
| SIM-04 | @Server | **`POST /escrow/initiate` is not bound to the delivery**: any authenticated user (farmer, trucker, unrelated buyer) can create an `escrowed` escrow on any deliveryId with an arbitrary payee and amount; the rogue escrow then blocks the real buyer with `409 ESCROW_009`. | `EscrowService.InitiateAsync` never compares `buyerId`/`farmerId` with the delivery, never checks the caller is the delivery's buyer, never checks amount vs order total. 201 responses captured. Found by validator. |

### P1

| ID | Scope | Bug | Root cause / evidence |
|---|---|---|---|
| SIM-05 | @Server | **Order create trusts the client for every money field**: `pricePerUnit` 1 ETB on a 120 ETB/kg listing, `quantity` 9999 on a 30-quintal listing, `farmerId` ≠ listing owner, nonexistent `listingId`, `cropType` ≠ listing, negative `transportCost` (orderTotal −470.6), `truckerId` without `tripId`, nonexistent `tripId`, all accepted and priced. | Order create never loads the listing or trip (`ValidateOrder*`, ~L4590-4650). Found by buyer-yonas, validator, buyer-dawit, farmer-alemu. |
| SIM-06 | @Server | **Order update has no field allow-list / immutability**: a participant can rewrite `pricePerUnit`, `quantity`, `transportCost`, `farmerId`, `buyerId` and inject arbitrary fields after create; `orderTotal` is not recalculated. (Tested before funding; post-funding untested.) | Order update path (~L4700) filters nothing but status transitions. Found by buyer-yonas, validator. |
| SIM-07 | @Server @Docs | **Funding subtracts the order quantity in KG from the listing stock in QUINTALS**, one real in-app order (5 quintal = 500 kg) zeroes a 60-quintal listing, which then leaves the market as `reserved`. Order quantity unit is undocumented. | Mobile sends kg (`place_order_screen.dart:55` `listing.quantity*100`); server prices `cropCost = quantity × pricePerUnit` (correct ETB) but `TryDecreaseListingQuantityAsync` does `listing.quantity − order.quantity` with no `/100`. Fleet: sara ordered "30" → maize stock 60 → 30 while cropCost was 570 ETB (30 kg). Found by orchestrator (code trace), buyer-sara, farmer-girma, farmer-alemu, farmer-hana, buyer-meron, buyer-dawit. |
| SIM-08 | @Server @Docs | **Sync ignores the documented op-level `clientVersion` and reads `data.version`**: crops/trips/deliveries updated per contract always `conflict` ("client expected 0"); orders with no version anywhere are applied with NO concurrency check (even on a cancelled order). | `SyncFunction.cs` L869/1961/2005/2352/4047 `operation.Data.TryGetValue("version")`; `ClientVersion` never read. The shipped clients send `data.version`, so only contract-conformant clients break; order path silently falls back to the stored version. Found by orchestrator, farmer-girma, farmer-alemu, buyer-yonas. |
| SIM-09 | @Server | **Delivery participant and ordering gaps**: (a) a trucker not on the order can create the delivery when the order has no trucker; (b) delivery update can re-point `farmerId`; (c) a second active delivery for the same order is accepted (unlinked to the escrow, so its QR gate may fail open); (d) a delivery can be created for an unpaid `created` order, or a `funded` one before the farmer accepts. | `delivery-verification-api.md` 1.2.0 only requires a non-terminal order. Found by validator, trucker-selam, trucker-tadesse, farmer-alemu. |

### P2

| ID | Scope | Bug | Root cause / evidence |
|---|---|---|---|
| SIM-10 | @Server @Docs | **Contract example payloads are rejected as unknown fields**: `deliveryId` (delivery create/update), `confirmedAt` (`delivered`), `pickedUpAt` (`pickedUp`) → `DELIVERY_VALIDATION: Unknown fields`. A client following `delivery-verification-api.md` cannot confirm pickup or delivery. | Found by buyer-meron, buyer-dawit, buyer-sara, buyer-yonas, trucker-tadesse, trucker-selam. |
| SIM-11 | @Server @Docs | **Order lifecycle does not follow the delivery**: delivery `pickedUp`/`inTransit`/`delivered` never advance the order; the order's own trucker is refused `pickup_confirmed`/`in_transit` (`ORDER_UNAUTHORIZED`, contract says "either participant"); the buyer must hand-step `pickup_confirmed → in_transit → delivered` (even for self-pickup); no delivery-state gate (order can be `delivered` while the delivery is `inTransit`); ratings stay blocked until stepped. | Found by trucker-selam, trucker-tadesse, buyer-meron, buyer-sara, buyer-dawit, buyer-yonas. |
| SIM-12 | @Server | **Dispute does not propagate**: escrow → `disputed` but order and delivery stay `delivered`; the buyer can still rate the farmer after disputing. Money is frozen correctly (release 403, status changes rejected). | Found by buyer-sara, farmer-girma. |
| SIM-13 | @Server | **Listing stock is not restored when a funded order is cancelled/refunded** (wheat 30 → 22 with 5 sold; maize 29 with 30 live). Listed as a known gap in `escrow-protocol.md`, now observed. | Found by farmer-alemu, farmer-girma. |
| SIM-14 | @Server | **Delivery DTO omits `orderId`** (list and detail) although `orderId` is required on create; no client can tie a delivery to its order. | Found by 6 personas. |
| SIM-15 | @Server @Mobile | **Escrow read model hides the trucker's money**: `MapToEscrowDto` omits `truckerPayout`; escrow rows carry no `truckerId` so the trucker's `GET /escrow/history` is empty and `by-delivery` returns 403 `ESCROW_022`; the farmer's view does not reconcile (3345 = 900 + 45 + 2400 unshown). Web: trucker Payments page empty while Profile shows 3,000 ETB. | Found by trucker-tadesse, farmer-alemu, buyer-meron, farmer-girma, web-ui. |
| SIM-16 | @Server @Docs | **Unfiltered list reads silently default to buyer scope**: `GET /orders` and `GET /escrow/history` return `[]` for farmers/truckers unless `farmerId`/`truckerId`/`role=` is passed; the self-pickup escrow is missing from the farmer's history; the buyer's plain `GET /deliveries` misses her own in-transit delivery (works with `mine=true`). | Found by farmer-alemu, farmer-girma, farmer-hana, trucker-tadesse, buyer-sara. |
| SIM-17 | @Server | **Crop update skips validation that create enforces**: latitude 20 and 95 accepted (Ethiopia bounds 3-15°N), `createdAt` in 2027 accepted. | Found by validator. |
| SIM-18 | @Infra | **Dev admin user (`+251900100009`) is not seeded** → the admin dashboard cannot be exercised in dev at all (all Cognito attempts `NotAuthorizedException`). Remediation: CloudShell `ADMIN_USER_POOL_ID=eu-north-1_hSYkEt6nd ADMIN_CLIENT_ID=4dntti20l1if7jfutdskjdkrmm bash seed-admin-user.sh` (see `.ai/test-users.md`). | Found by admin-check. |
| SIM-19 | @Mobile | **Every crop card shows a hardcoded 4.5★ rating and an "Escrow" badge** (`browse_listings_screen.dart:418`), real ratings exist via `GET /ratings`. | Found by web-ui. |
| SIM-20 | @Mobile | **Farmer Payments/Profile count the trucker's transport as the farmer's held funds** (3,345 shown where the farmer gets 900) and **escrows of cancelled orders still show "Funds Held Safely"** with a blank delivery reference. | Found by web-ui. |
| SIM-21 | @Mobile | **Web router has no client-side role guard**: wrong-role routes render and fire 403s; trucker inbox fires two 403 crop lookups on every load; dashboards issue wrong-role and duplicate calls (six trip calls for the trucker home). No data leaks (server scopes everything). | Found by web-ui. |
| SIM-22 | @Mobile @l10n | **Amharic filter chips clipped to 1-2 characters**; crop names on listing/order cards stay raw English in Amharic and Afan Oromo. | Found by web-ui. |
| SIM-23 | @Mobile | **TLS/pin failure while online is shown as "Please log in online first to enable offline mode"** (logcat `OS connectivity: true`). Also web: a 403 on crop browse shows "Your data will sync when you're back online". | Found by mobile-smoke, web-ui. |
| SIM-24 | @Server | **Escrow initiate on an already-funded order returns `409 ESCROW_009` instead of the contract's `ESCROW_024`** (double charge is still blocked; client error mapping keyed on ESCROW_024 shows the wrong message). | Found by buyer-meron. |

### P3

| ID | Scope | Bug | Evidence |
|---|---|---|---|
| SIM-25 | @Server | `/sync` `serverData` (success and conflict) stringifies numbers and arrays (`"version":"2"`, `"pricePerKg":"18"`, `matchedListings:"[]"`) while GET returns proper types. | 5 personas |
| SIM-26 | @Server @Docs | Crop read-model drift vs `crop-listing-api.md`: `quantityKg` holds the quintal count; `{crop:…}` wrapper and `cropId`; default browse includes `reserved` listings; `nextToken` that leads to an empty page and no `hasMore`/`total`; `/crops` accepts `limit` up to 100 while every other list caps at 50. | farmer-hana, farmer-alemu, farmer-girma, buyer-meron, orchestrator |
| SIM-27 | @Server @Docs | Auth/escrow error-code deviations: missing field → `AUTH_007 "Invalid JSON"` instead of the field code (also `/escrow/release`); refresh with a revoked token → `AUTH_018 expired` not `AUTH_019 revoked`; local phone format `09…` accepted (contract: `+251` only); `/auth/register` works without `faydaVerificationToken` in dev although the contract marks it required. | validator, farmer-hana, buyer-sara |
| SIM-28 | @Server | Idempotency/race semantics: a reused idempotency key with a DIFFERENT payload is answered `duplicate` and silently dropped; the loser of a concurrent status race gets `error` (invalid transition) instead of `conflict` + `serverData`. | buyer-yonas |
| SIM-29 | @Server | Escrow record details: version never increments on delivery linkage/confirmation stamping; second dispute returns 422 instead of `409 ESCROW_020`; cancelled order's escrow has `expiresAt` stamped but no `refundReason`. | buyer-sara, buyer-yonas |
| SIM-30 | @Server @Docs | Commission: stored as an unrounded float (`0.9500000000000001`); `escrow-protocol.md` says 2.5 % deducted at release, the server adds 5 % of crop cost on top of the order. | farmer-girma, farmer-hana |
| SIM-31 | @Server | Cancelled trip still returned by `GET /trips` while `GET /trips/{id}` says 404 "cancelled". | trucker-selam |
| SIM-32 | @Mobile | Login form: validation errors stay red after the field is corrected (Android); on web the error is truncated and absent from the accessibility tree. | mobile-smoke, web-ui |
| SIM-33 | @Mobile | Display: orders list renders impossible values (−471 ETB, "0 kg", 9999 kg, consequence of SIM-05); buyer home "Available Crops" tile counts only the first page (20 of 33); a card shows a raw farmer ID as seller and location "105"; trip cards omit the trucker name. | web-ui |
| SIM-34 | @Server | `GET /messages?since=<non-date>` returns 200 instead of 400. | orchestrator |
| SIM-35 | @Infra | `/version.json` returns 404 on the dev webapp (cache strategy in `.ai/deployment.md` expects it). | web-ui |
| SIM-36 | @Tests | Stale e2e assertions: `e2e/admin/navigation.spec.ts:65` expects 10 nav labels but lists 11; `e2e/webapp/listing-flow.spec.ts` sends an invalid crop type; the webapp login spec passes even when its redirect times out. | admin-check, web-ui |
| SIM-37 | @Mobile @Server | Changing the language in Settings makes no API call, so `preferredLanguage` never syncs to the server profile. | web-ui |

---

## Not covered / caveats
- Cron-driven paths (auto-refund ≤ 1 h after cancel, auto-release after 48 h) were not observable within the run; cancelled-order escrows were still `escrowed` ~25 min after cancel (within contract).
- Admin dashboard functional checks (stats, users, escrows, transactions, audit, fraud review, dispute resolution) are blocked by SIM-18.
- Android post-login screens, offline cache/queue/sync on device are blocked by SIM-01.
- Post-funding order-field mutation (SIM-06 after `funded`) and `buyer_intent` `buyerId` reassignment were not tested.
- Substitute-crop ranking in `/match` could not be exercised (no sorghum listings on dev at match time).
- Personas sent order `quantity` in quintals (the unit the contracts use everywhere else) while the shipped app sends kg; their "cropCost 100× too low" observations are therefore a symptom of the undocumented unit, and SIM-07 describes the real defect.

## Test data left on dev (all `Sim0924` accounts, phones `+2519009240xx`)
Fleet users 01-03 (farmers), 11-12 (truckers), 21-24 (buyers), 31-34 + 41-42 (validator / web-ui), one stray dev user `+251911234567` (local-phone-format test); ~30 orders (most cancelled), 2 rogue escrows on the validator's own deliveries, 1 crop each moved to the validator's trucker and throwaway farmer.
