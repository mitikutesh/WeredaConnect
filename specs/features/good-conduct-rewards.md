# Feature: Good-Conduct Rewards (verified handoffs)

**Status**: DRAFT 2026-11-19, awaiting owner approval. Decisions D1 to D6 in section 3 are open; each carries a recommended default.
**Tasks**: EPIC GCR in `TASK.md` (GCR-01 to GCR-14, section 12), added 2026-11-19 at the owner's request.
**Contracts touched**: `reputation-api.md`, `delivery-verification-api.md`, `sync-protocol.md`, `data-models.md`, `match-api.md`, `order-lifecycle.md`, `escrow-protocol.md`, `notifications-api.md`
**Author**: AI-assisted
**Date**: 2026-11-19
**Related**: `specs/features/three-party-escrow-commitments-reputation.md` (penalties, tiers, earn-back), `specs/reports/gps-unavailable-spike.md` (SPK-01: QR is the proof, GPS is evidence), `specs/architecture/reputation-architecture.md`, FUP-440, FUP-442, FUP-445, FUP-448, FUP-187, FUP-207

---

## 1. What

Every completed order gives each of its parties a small conduct record: which of the optional, careful steps they took. The share of steps taken over the last 90 days is shown as one number, "Verified handoffs N%", and that number earns real advantages.

1. **Reward only.** A step not taken earns nothing. It never costs points, never restricts, never hides anyone.
2. **Only what the server can check.** A photo exists, a QR was scanned, a fix was sent. Never "the photo shows the right goods".
3. **All three roles.** Farmers and truckers get the number on their cards. Buyers, who have no reputation today, get a standing that the farmer and the trucker see when asked to accept.
4. **Fair to no-signal areas and to the web app.** A step the party could not take (no GPS signal, a browser without background location) is left out of the sum instead of counted as missed.
5. **It pays.** Ranking, the faster payout path, and a banked credit against a future penalty point.

## 2. Why

- Today the platform only measures failure. `ORDER_COMPLETED` is worth 0 points whether the handoff had a scan, a fix and a photo or none of them (`reputation-api.md` section 1). The tier `good` unlocks nothing and there is no tier above it.
- The whole penalty system is switched off in every environment (`reputationPenalties`, `timeoutPenalties`, `reputationPublicTags`, `pickupClassification`, `dropoffClassification` in `infra/lib/config/environments.ts`). Buyers see raw star averages only. A rewards-first launch is a softer way to introduce conduct scoring than penalties: nobody loses anything on day one.
- Evidence is what settles a dispute. SPK-01 concluded that the QR scan is the proof and GPS and photos are evidence, and that making evidence mandatory blocks honest people where there is no signal. The remaining tool is to make leaving evidence worth it.
- A photo or a fix can be staged. That is acceptable: the score rewards leaving a record, and a record left behind is what convicts a dishonest party later. Money still moves only on the other party's scan, an admin verdict or a timer.
- In a market where most farmers and truckers are new to escrow, a visible "this person does it properly" mark is cheaper trust than a star average built from a handful of ratings.
- The one existing reward is invisible: a "trusted" payee is paid after 24 hours instead of 72 (`server/src/Services/PayoutDelayPolicy.cs`), and nobody is told.

## 3. Decisions (open)

| # | Decision | Recommended default | Set by |
|---|---|---|---|
| D1 | Ship rewards before the penalty flags go on | Yes. Needs none of the dark flags | open |
| D2 | Buyers get a standing | Yes, shown only to the farmer and the trucker of that buyer's own orders | open |
| D3 | Faster payout tied to the score | Yes, but only as an extra condition on top of today's fraud conditions (risk score 0, account 30 days, 3 escrows). Never looser than today | open |
| D4 | Fee discount for high scorers | Not in v1. The fee is a flat 2.5% (`EscrowService.cs`); revisit with data | open |
| D5 | A new positive tier (`trusted`) or a separate badge | Badge only in v1. Tiers stay a penalty concept | open |
| D6 | A system confirm for a silent buyer still earns the `CLEAN_DELIVERY` credit | No for the buyer, yes for the farmer and the trucker (open owner decision already noted in the fraud spec) | open |

## 4. Signals per role

"Live" means the field is written today. "Needs" names the ticket that makes it measurable.

### 4.1 Farmer

| Signal | Measured by | State |
|---|---|---|
| Goods as described | no dispute upheld for `wrong_quantity` or `quality_mismatch` on the order | live |
| Stock was current | the order was not declined with `crop_not_ready` | live |
| Sold listings removed | the listing was deleted or reduced within 48 hours of the order settling | live (`deletedAt`, `version`) |
| Farm point measured | the listing point came from GPS, not a picked place | needs FUP-442 |
| Identity verified | `faydaVerified` | live, counted once per window, not per order |
| Ready on time | `crops_ready` check-in inside its window | needs `pickupClassification` on (GCR-13) |

### 4.2 Trucker

| Signal | Measured by | State |
|---|---|---|
| Pickup scanned, not typed | handoff method `qr` | needs GCR-01 (today only in a best-effort audit row) |
| Fix at pickup | `gpsPickup` present and inside 5 km | live; fairness needs FUP-440 |
| Pickup photo | `pickupPhotoRef` attached, capture within 30 minutes of the scan | live |
| Picked up on the planned day | `pickedUpAt` on or before the trip's departure day | live |
| Drop-off code ready | drop-off nonce registered before the buyer's scan | live |
| Shared the trip | pings by `capturedAt` cover the trip, none `mocked` | live, mobile only |
| Arrived and showed it | `arrived_dropoff` check-in with its photo | needs `dropoffClassification` on (GCR-13) |

### 4.3 Buyer

| Signal | Measured by | State |
|---|---|---|
| Confirmed in person | `confirmedAt` set by the buyer, not `autoConfirmedAt` | live |
| Drop-off scanned, not typed | handoff method `qr` | needs GCR-01 |
| Fix at drop-off | `gpsDropoff` present and inside 5 km | live; fairness needs FUP-440 |
| Drop-off photo | `dropoffPhotoRef` attached | live |
| Drop-off place set | `deliveryLatitude` and `deliveryLongitude` on a trucked order | live |
| Rated the parties | a rating row for each ratee within 14 days | live |
| Fair disputes | no dispute on the order resolved against the buyer | live |

### 4.4 Deliberately not scored

- **Response speed** (accept, decline, transport answer). Deadlines run on server receipt time, so an offline farmer who answered on time reads as slow.
- **Message response time.** Only the phone's clock exists.
- **Listing photo source** (camera or gallery). Not recorded, EXIF is stripped.
- **GPS accuracy as a reward.** Stored for admins by FUP-440, used here only to leave a coarse fix out of the sum.
- **Anything the other party types** about you beyond the existing stars.

## 5. The number

Per settled order and per party the server stamps two integers: `offered` (the steps that applied) and `earned` (the steps taken).

`verifiedPct = round(100 * sum(earned) / sum(offered))` over orders settled in the last 90 days. Absent below 3 counted orders, like `reliabilityPct`.

**A step is left out of `offered` when the party could not take it:**

| Case | Left out |
|---|---|
| Handoff `gpsStatus` is `timeout` (no signal) | the fix step |
| Action came from the web app | trip sharing coverage; photo steps on desktop browsers |
| Self-pickup (no trucker) | every trucker step; the buyer's drop-off place |
| Flag off (`pickupClassification`, `dropoffClassification`) | the check-in steps |
| Old client that does not send `gpsStatus` or platform | the steps that need them |

`gpsStatus` of `denied`, `denied_forever` or `services_off` stays in `offered`: the party chose not to give a fix.

**Which orders count (anti-farming, the earn-back rules of `reputation-api.md` section 1.6):**

- the order reached `settled` and was never disputed, or the dispute was resolved for this party
- order amount of 500 ETB or more
- at most 2 counted orders per pair of parties per 30 days
- neither party holds a `SELF_TRADE` or `SHARED_DEVICE_TRADE` signal

## 6. Rewards

| Reward | Who | How | Phase |
|---|---|---|---|
| Badge "Verified handoffs N%" | farmer, trucker | beside reliability on listing and trip cards and detail; own number on the reputation screen | P1 |
| Own breakdown | all three | "You earned 5 of 6 on this order" on order detail, with what was missed | P1 |
| Ranking | farmer, trucker | a `verified` term in `MatchScoring` (weight 0.05, neutral 0.7 like reliability); second tie-break after reliability in trip suggestions | P2 |
| Faster payout, made visible | farmer, trucker | `PayoutDelayPolicy`: the 24 hour path additionally asks for `verifiedPct` of 80 or more once the party has 3 counted orders; order detail says "Paid a day sooner: verified handoffs" | P2, D3 |
| Banked credit | farmer, trucker | a fully earned order with no active points banks one credit (at most 2 held); a later 1 point penalty spends a credit instead of landing | P2 |
| Buyer standing | buyer | "Reliable buyer" line on the farmer's and the trucker's accept screen for that order | P3, D2 |
| Nudges | all three | one line on the scan screen and after settle: what counts and what was missed | P3 |
| Rating reminder | buyer | one push 48 hours after delivery when no rating exists | P3 |

## 7. Experience per role

| Role | Sees |
|---|---|
| **Farmer** | Own `verifiedPct` and the per-order breakdown. On the accept screen, the buyer's standing. Never another farmer's listing or number (role rule unchanged) |
| **Trucker** | Own `verifiedPct` and breakdown. On the transport request, the buyer's standing. Never another trucker's trip or number |
| **Buyer** | The badge on every crop and trip (buyers see all). Own standing and breakdown on the profile and order detail |
| **Admin** | The stamped `offered`/`earned` per party on the transaction view, with which steps |

The buyer standing travels only on order rows the farmer or the trucker already receives. It is never on a listing, a trip or a browse response.

## 8. Server

1. **Handoff method on the delivery (GCR-01).** `pickupMethod` and `dropoffMethod` (`qr` or `code`), server-only, written in the same update as the transition. The server already knows it from `scannedQrNonce` against `scannedQrCode`; no client change.
2. **Platform (GCR-02).** The sync operation carries an optional `platform` (`android`, `web`). Stamped server-only on the delivery per leg. Absent on old clients.
3. **GPS reason, time and accuracy.** FUP-440 as written. This epic depends on it and does not redo it.
4. **Conduct stamp (GCR-03).** A pure function `ConductScoring.Evaluate(order, delivery, pings summary, ratings, role)` returns `offered`, `earned` and the step list. Called when the order settles; the result is written as a `HANDOFF_QUALITY` row per party in the reputation events table, idempotent on `{orderId}:{partyId}`. Buyer rows are the first buyer rows in that table: every reader that assumes farmer or trucker must be checked.
5. **Aggregate (GCR-04).** `verifiedPct` and `verifiedOrders90d` computed at read time from the rows, as `reliabilityPct` is. Returned by `GET /reputation/me` and the public summary.
6. **Copies (GCR-05).** `ownerVerifiedPct` on crop and trip rows through `ReputationCopyRepository`, refreshed when the value changes by a whole percent. `buyerVerifiedPct` stamped on the order for the farmer and the trucker.
7. **Flag.** `conductRewards` (default off in every environment), independent of `reputationPenalties` and `reputationPublicTags`. Stamping (items 1 to 4) is unflagged so data exists before the badge shows.
8. **Late evidence.** A rating or a photo that arrives after the stamp re-evaluates that order once, inside its 14 day window.

## 9. Offline behaviour and sync

- Nothing new is asked of the phone at the handoff. Method, fix, photo and their times already travel in the queued operation; `platform` is one short field on it.
- All timing uses the phone's `capturedAt` where a plausibility check exists (pickup, pings, check-ins), so a handoff done offline and synced a day later scores the same as one done online.
- The badge is read from the cached crop and trip rows (`ownerVerifiedPct` arrives through `/sync` and browse like `ownerReliabilityPct`). The own number and the breakdown are cached with the reputation summary and shown with "As of {date}" when offline.
- No new request on the handoff path, no new background work, no new package.
- Web has no offline queue: same fields, sent at once.

## 10. Platform pairs and cross-module

| Area | Files |
|---|---|
| Server models | `server/src/Models/DeliveryDtos.cs`, `OrderDtos.cs`, `CropDtos.cs`, `TripDtos.cs`, `ReputationDtos.cs`, `MatchDtos.cs`, `AppJsonContext.cs` |
| Server logic | new `server/src/Helpers/ConductScoring.cs`; `ReputationPolicy.cs`, `ReputationPolicySettings.cs`, `ReputationReadModels.cs`, `OwnerReputationView.cs`, `MatchScoring.cs`; `Services/ReputationService.cs`, `ReputationRecordProcessor.cs`, `PayoutDelayPolicy.cs`, `CleanDeliveryCreditService.cs` (D6); `Data/ReputationEventRepository.cs`, `ReputationCopyRepository.cs`; `Functions/SyncFunction.cs` (delivery transitions), `SyncFunction.Reputation.cs`, `ReputationFunction.cs`, `CropFunction.cs`, `TripFunction.cs`, `MatchFunction.cs`, `CropMatchStreamFunction.cs`, `AdminFunction.Reputation.cs` |
| Infra | `infra/lib/config/environments.ts` (flag), `infra/lib/stacks/api-stack.ts` (env var on the Lambdas that read it) |
| Mobile schemas (pair) | `mobile/lib/core/database/schemas/crop_listing.dart` and `trip.dart` with `mobile/lib/core/models/web/crop_listing.dart` and `trip.dart`; `core/models/owner_reputation_wire.dart` |
| Mobile feature | `mobile/lib/features/reputation/` (domain `owner_reputation.dart`, cards, own screen), order detail (breakdown, payout line), accept screens (buyer standing), scan screen (nudge), `features/buyer/domain/services/match_scorer.dart` and `web_matching_service.dart` |
| Mobile sync (pair) | `core/sync/sync_queue.dart` and `sync_queue_web.dart` (the `platform` field) |
| Admin | `admin/src/lib/reputation.ts`, `admin/src/components/reputation/ReputationPanel.tsx`, the transaction view |
| L10n | `mobile/lib/shared/l10n/app_en.arb`, `app_am.arb`, `app_om.arb` |

## 11. Localization (en, am, om)

| Key | English |
|---|---|
| `conductVerifiedBadge` | "{pct}% verified handoffs" |
| `conductVerifiedExplain` | "How often this person scans, shares location and takes the handoff photo" |
| `conductOwnTitle` | "Your verified handoffs" |
| `conductOrderEarned` | "You earned {earned} of {offered} on this order" |
| `conductStepScan` | "Scanned the code" |
| `conductStepFix` | "Location was on" |
| `conductStepPhoto` | "Took the handoff photo" |
| `conductStepOnTime` | "Picked up on the planned day" |
| `conductStepShared` | "Shared the trip" |
| `conductStepConfirmed` | "Confirmed the delivery yourself" |
| `conductStepRated` | "Rated the farmer and the driver" |
| `conductStepAsDescribed` | "Goods matched the listing" |
| `conductStepNotCounted` | "Not counted: no signal here" |
| `conductNudgeScan` | "Scan, location and photo count toward your verified handoffs" |
| `conductPayoutSooner` | "Paid a day sooner: verified handoffs" |
| `conductBuyerReliable` | "Reliable buyer: {pct}% verified handoffs" |
| `conductBuyerNew` | "New buyer" |
| `notifRateReminderTitle` / `Body` | "Rate your order" / "Tell others how {name} did" |

Amharic and Afaan Oromo are machine drafted and need a native review ticket, as the reputation texts did.

## 12. Tickets (proposed)

| ID | Phase | Scope | What |
|---|---|---|---|
| GCR-01 | P0 | @Server | `pickupMethod` and `dropoffMethod` on the delivery, server-only; contract bump |
| GCR-02 | P0 | @Mobile @Server | `platform` on sync operations, both queue files, stamped per leg |
| GCR-03 | P0 | @Server | `ConductScoring` and the `HANDOFF_QUALITY` rows for all three roles, unflagged, with the fairness and anti-farming rules of section 5 |
| GCR-04 | P1 | @Server | `verifiedPct` in the reputation reads; `conductRewards` flag |
| GCR-05 | P1 | @Server | `ownerVerifiedPct` copies on crops and trips; `buyerVerifiedPct` on orders |
| GCR-06 | P1 | @Mobile @l10n | Badge on cards and detail, own screen, order breakdown; mobile and web; three languages |
| GCR-07 | P1 | @Admin | Conduct stamp on the transaction view and the reputation panel |
| GCR-08 | P2 | @Server @Mobile | `verified` term in match scoring (server and both client scorers) and the trip tie-break; answers FUP-187 alongside |
| GCR-09 | P2 | @Server @Mobile | Payout: the extra condition and the visible line (D3) |
| GCR-10 | P2 | @Server | Banked credit against a future penalty point |
| GCR-11 | P3 | @Mobile @l10n | Buyer standing on the accept screens; scan screen and settle nudges |
| GCR-12 | P3 | @Server @Mobile | Rating reminder push |
| GCR-13 | P4 | @Server | Check-in and arrival steps, after FUP-326 and FUP-372 turn the classification flags on |
| GCR-14 | P4 | @l10n | Native am and om review of the section 11 texts |

Depends on: FUP-440 (GPS reason, time, accuracy), FUP-442 (listing point source). Order: FUP-440, then GCR-01 to GCR-03, then data for two weeks before P1 shows a number.

## 13. Testing

**Server (xUnit, NSubstitute)**
- `ConductScoring`: one test per step per role; a `timeout` fix leaves the step out of `offered`; `denied` keeps it in; web leaves out sharing; self-pickup has no trucker steps.
- Which orders count: under 500 ETB, disputed, third order of a pair in 30 days, `SELF_TRADE` holder.
- Idempotency: settling twice writes one row per party; a late rating re-evaluates once.
- `verifiedPct` absent below 3 orders; 90 day window edge.
- Role rule: `buyerVerifiedPct` appears on an order only for its farmer and its trucker; never on a crop, trip, browse or match response. `ownerVerifiedPct` never reaches another farmer or trucker.
- Flag off: no response field changes (snapshot of the current reads).
- `PayoutDelayPolicy`: never shorter than today for any input; the extra condition only removes the 24 hour path.
- Old client (no `platform`, no `gpsStatus`): accepted, steps left out.
- LocalStack: copy stamping on crops and trips; the buyer's first row in the events table does not break the farmer and trucker reads.

**Mobile (flutter test)**
- Wire parsing of `ownerVerifiedPct` on Isar and web models; absent value hides the badge.
- Cards and detail for all three roles; a farmer's browse still shows own crops only.
- Offline: the badge and own number render from cache with "As of {date}"; the breakdown shows "Not counted: no signal here".
- Queue: `platform` is on the operation on both `sync_queue.dart` and `sync_queue_web.dart`; a handoff queued offline scores by its capture time.
- Match scorer parity between `match_scorer.dart`, `web_matching_service.dart` and the server.
- Web build: `flutter build web --no-tree-shake-icons`.

**End to end (dev test users)**
- Farmer lists, buyer orders, trucker scans with fix and photo, buyer scans and rates: all three parties show full marks.
- Same flow with a typed code and no photo: lower `earned`, nothing refused, nothing penalised.

## 14. Risks

| Risk | Mitigation |
|---|---|
| A low percent reads as a penalty | Left-out rule in section 5; the badge is hidden below 3 orders and below a floor (show only at 60% or more, otherwise show nothing) |
| Staged evidence earns marks | Accepted (section 2). Money never moves on it |
| Collusion farming between two accounts | Earn-back caps reused; fraud signals exclude |
| Rural and web users systematically lower | Left-out rule; measure the distribution per platform and region for two weeks before the badge ships (the reason stamping is unflagged) |
| Buyer rows in a table that assumed two roles | GCR-03 audits every reader; LocalStack test |
| Role leak of the buyer standing | Stamped on order rows only; tests in section 13 |
| Payout made faster for a bad actor | D3 only adds a condition; `PayoutDelayPolicy` test pins "never shorter than today" |
| AOT | New DTO fields and the events row shape registered in `AppJsonContext.cs`; `ConductScoring` is plain code, no reflection |
| Cold start and cost | No new Lambda, table or index. One extra write per party per settled order; aggregate read rides the existing reputation query |
| Data use | One short field per operation; one integer per cached crop and trip row |
| Depends on dark flags for some steps | P0 to P3 use live fields only; P4 waits for FUP-326 and FUP-372 |
| Three machine-drafted languages | GCR-14 |

## 15. Not in this epic

- Fee discounts, airtime or fuel vouchers, referral bonuses (`docs/Research/Marketing_Strategy_Deep_Dive.md`).
- Two-way star ratings (the 2026-11-04 trust proposal).
- Any penalty for missing evidence. A fraud weight for a missing fix stays FRD's call after FUP-440 has data.
- Background location, mandatory GPS, mandatory photos.
