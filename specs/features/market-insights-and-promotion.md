# Market Insights and Paid Promotion

**Status**: Future plan, not scheduled. Nothing built, no tickets in `TASK.md`. Decisions in section 13 are open and must be answered before any work starts. Re-check section 3 against the code at that point.
**Created**: 2026-11-19
**Related**: [`matching-roadmap.md`](matching-roadmap.md), [`specs/contracts/match-api.md`](../contracts/match-api.md), [`specs/contracts/match-events.md`](../contracts/match-events.md), [`specs/contracts/reputation-api.md`](../contracts/reputation-api.md), [`specs/contracts/order-lifecycle.md`](../contracts/order-lifecycle.md)

## 1. What

Two products that share one data foundation.

**A. Market insights (free).** Aggregate market signals shown inside the app:

| Role | Sees |
|------|------|
| Buyer | Crops priced below the local median, trips with free capacity, cheapest trip per route |
| Trucker | Routes and woredas where many orders and buyer intents need transport |
| Farmer | Demand per crop: number of active intents, quantity wanted, typical max price, by woreda |

**B. Paid promotion (revenue).** A registered user or company pays the platform to be featured:

| Format | Decision |
|--------|----------|
| Promoted listing: a trip or crop pinned in one labelled "Sponsored" slot on buyer browse and match results | First format to ship |
| Featured company: a trucking company with several trucks gets a badge and a profile page | Second, needs fleet accounts |
| Third-party sponsor card (seed, fertilizer, bank, insurance) on home | Later, sold by hand |
| Ad network (AdMob and similar) | Rejected, see section 10 |

## 2. Why

- Revenue today is one line: commission on the crop leg of an order (default 5%, SSM `/{prefix}/escrow/commission-rate`). Insights raise match rate, which raises commission. Promotion adds a second line.
- Farmers in rural weredas set prices without knowing demand. Truckers drive back empty. Both are information problems the platform already holds the data to solve.
- Promotion only earns money once sellers compete for buyer attention. Insights build that liquidity, so A ships before B.
- `docs/project-background.md` already lists "Premium listings" and "Data/analytics" as revenue streams. `docs/local_reality/README.md` says "Consider ad-free model (ads use data)". This spec resolves the conflict by allowing first-party promoted listings only and rejecting ad networks. See decision D2.

## 3. Current state (from code survey, 2026-11-19)

Line numbers drift. Re-check before implementing.

**Reusable**

- Buyer intents: `{prefix}-intents` table with GSI `CropTypeStatusIndex`. Fields include `cropType`, `quantityNeeded`, `maxPricePerKg`, `deliveryWoreda`, coordinates, `neededByDate`.
- Matching: `server/src/Helpers/MatchScoring.cs` and `mobile/lib/features/buyer/domain/services/match_scorer.dart`, parity-tested.
- Price statistic: median and MAD of the last 50 listings per crop type in `server/src/Services/FraudDetectionService.cs`. Internal only.
- Realized prices: orders carry `cropType`, `quantity`, `pricePerUnit`, `pickupPlace`, `dropoffPlace`. Orders table has a stream.
- Free capacity: trips carry `remainingQuintals`. `GET /trips` accepts `minCapacity`.
- Event logging pattern: `match_event` sync entity and `{prefix}-match-events` table (90-day TTL). Nothing reads it yet (TASK.md MATCH-02).
- Owner fields copied onto listing rows (reputation tier, reliability). Same pattern can carry a company badge.
- Three-leg ledger at settlement (`server/src/Services/EscrowLedgerBook.cs`).
- Admin patterns: Listing Reports queue, Revenue page.
- Feature flags in `infra/lib/config/environments.ts`.

**Missing**

| # | Gap |
|---|-----|
| G1 | No aggregate store, job or read endpoint for demand, supply or price. Trips table has no stream. |
| G2 | No geographic index. Server rows have free-text `woreda` only, no region or zone. Trip origins have no coordinates. Intent centroids cover about 40 towns, the place catalogue has 3,860. |
| G3 | No policy on what farmers and truckers may see in aggregate. |
| G4 | No company or fleet account. Truckers have no company field. `faydaVerified` is not on listing DTOs or cards. |
| G5 | No promotion entity, featured flag or ranking slot. No admin tools for it. |
| G6 | No non-order payment. Every Telebirr collect is bound to an order or escrow. Payout and refund are manual. No ledger category besides commission. |
| G7 | No UI slot for a banner, featured card or insight on any home or browse screen. No broadcast notification type. |
| G8 | No impression or click tracking for promotions. No cache or expiry rules for this content. Web has no local store at all. |
| G9 | Buyer trip browse is ordered differently on mobile (newest created) and web (soonest departure). Fix before adding a pinned slot. |

## 4. Role visibility constraints (HARD)

The rule in `CLAUDE.md` stands: a farmer never sees another farmer's crops, a trucker never sees another trucker's trips.

1. **Promoted listings appear on buyer surfaces only** (buyer browse, match results, buyer home). Never on farmer or trucker screens.
2. **Aggregates for farmers and truckers** are allowed only if decision D1 is approved, and then only under these rules:
   - Counts, sums and medians only. No listing ids, user ids, names, phone numbers or exact coordinates.
   - Minimum group size: a bucket is returned only when it has at least 5 distinct contributors. Smaller buckets are dropped by the server, not hidden by the client.
   - Geography no finer than woreda.
3. The server enforces both. The insights endpoint reads the caller's role from the ID token (`custom:role`) and returns only that role's shape.
4. Reputation visibility is unchanged: farmers and truckers never see another party's reputation.
5. Sponsor and promotion text passes the existing phone-number screening used for listing text.

## 5. Design

### 5.1 Aggregates (product A foundation)

- New table `{prefix}-market-aggregates`, on-demand billing.
  - PK `scope`, for example `CROP#teff#WOREDA#adama` or `ROUTE#adama#addis-ababa`.
  - SK `window`, for example `7D`.
  - Attributes by scope type: `listingCount`, `medianPricePerKg`, `intentCount`, `quantityWanted`, `medianMaxPrice`, `openOrderCount`, `quintalsNeedingTransport`, `openTripCount`, `freeQuintals`, `contributorCount`, `computedAt`, TTL.
- Writer: one scheduled Lambda (hourly) that walks existing GSIs and rewrites aggregates. Chosen over stream consumers because it is one code path, needs no trips stream, and is cheap at current volume. Revisit when a full walk exceeds the 10s timeout.
- Woreda keys use `WoredaNames` normalisation, shared with `normalizeWoreda` on the device.
- Price median reuses the fraud rule's median code, but with the skew protections in section 10.

### 5.2 Insight surfaces

| Role | Surface | Content |
|------|---------|---------|
| Buyer | Crop card badge on browse and match results | "Good price" when price is at least 10% under the woreda median |
| Buyer | Trip card badge | "Space free" with quintals, "Lowest price on this route" |
| Trucker | Home card under the trip card | Top 3 routes by quintals needing transport, tap opens trip create with the route filled |
| Farmer | Home card and crop-create screen hint | Per crop the farmer lists: buyers looking, quantity wanted, typical max price in their woreda and nearby |

### 5.3 Company accounts (product B prerequisite)

- Add `accountType` (`individual` or `company`) and `companyName` to truckers, mirroring the buyer fields in `UserDto` and `RequestValidator`.
- Optional `fleetSize`. A full vehicle registry is out of scope.
- `companyVerified` set by admin after a manual document check. Copied onto trip rows like reputation fields, shown as a badge on trip cards.
- One login per company in this phase. Sub-accounts for drivers are a later item.

### 5.4 Promotion

- New table `{prefix}-promotions`. PK `promotionId`. GSI on `status` + `endsAt`.
- Fields: `ownerId`, `targetType` (`trip`, `crop`, `company`, `sponsor`), `targetId`, `scopeWoreda` or route, `startsAt`, `endsAt`, `status` (`pending_payment`, `pending_review`, `active`, `rejected`, `expired`, `refunded`), `feeAmount`, `paymentRef`.
- Eligibility to buy: `faydaVerified`, no active reputation restriction, no open fraud case.
- Placement: exactly one sponsored slot at the top of buyer browse and match results, labelled "Sponsored" in all 3 languages. Organic order below it is untouched. Promotion never changes the match score.
- Selection when several promotions match a buyer's filter: rotate by least recently shown, computed on the device from cached promotions.
- Every promotion is admin-approved before it goes live.

### 5.5 Charging

- New Telebirr collect purpose `promotion`, separate from order and escrow flows, with its own idempotency key `{userId}:promotion:{promotionId}:pay:{timestamp}`.
- New ledger category `promotion_fee`. Admin Revenue page splits commission and promotion income.
- Refund on rejection goes to the existing manual payout queue until Telebirr refund is built.
- Phase 4 skips all of this: admin creates the promotion after an offline payment.

### 5.6 Pricing model

- Flat prepaid fee per scope per week (one route or one woreda). Not per click and not per impression: offline devices report late, counts would be disputed, and a flat fee is easier to explain.
- Fee stored in SSM so it can change without a deploy.
- Fee levels are not set here. Test with a few trucking companies during phase 4.
- Advertiser sees impressions and opens for their promotion as a courtesy stat, not as a billing basis.

## 6. API contract sketch

Contracts to be written in `specs/contracts/` before implementation.

| Endpoint | Role | Purpose |
|----------|------|---------|
| `GET /insights` | Any signed-in role | Returns the role's insight payload. Optional `woreda`, `cropType`. Small response, target under 5 KB. Supports `If-None-Match`. |
| `GET /promotions/active` | Buyer, admin | Active promotions with the denormalised listing summary needed to render a card. 403 for farmer and trucker. |
| `POST /promotions` | Farmer, trucker (per D3) | Create a promotion request for the caller's own listing or company. |
| `GET /promotions/mine` | Owner | Own promotions with stats. |
| `POST /promotions/{id}/pay` | Owner | Start Telebirr collect (phase 5). |
| `GET/POST /admin/promotions…` | Admin | List, approve, reject, create on behalf, refund. |
| `/sync` entity `promo_event` | Buyer | `shown`, `opened`, `order_created` per promotion, batched like `match_event`. |

All new DTOs registered in `server/src/AppJsonContext.cs`.

## 7. Offline behavior and sync

- **Insights**: cached per role in Isar with `computedAt`. Shown offline with an "as of" time. Hidden when older than 48 hours. Pulled with the existing marketplace pull, throttled the same way.
- **Good-price badge**: computed on the device from the cached median, so it works offline.
- **Promotions**: cached in Isar with `endsAt`. The device drops expired rows locally without a network call. A promoted listing missing from the local crop or trip cache is not shown.
- **Promotion events**: queued in the sync queue and uploaded in batches. Loss of some events is acceptable because billing is flat fee.
- **Buying a promotion**: online only. The screen says so when offline. No queued purchases, to avoid a delayed surprise charge.
- **Web**: in-memory only, fetched per session. Models need web counterparts under `core/models/web/`, and the Isar-backed caches need web stubs (platform pair rule).
- **Data cost**: no images in sponsored slots in phases 1 to 5. Text and existing listing thumbnails only.

## 8. Localization

New keys in `app_en.arb`, `app_am.arb`, `app_om.arb`. Indicative list:

- `sponsoredLabel`, `goodPriceBadge`, `spaceFreeBadge`, `lowestPriceOnRouteBadge`
- `insightsAsOf`, `insightsDemandTitle`, `insightsBuyersLooking`, `insightsQuantityWanted`, `insightsTypicalMaxPrice`
- `insightsTruckDemandTitle`, `insightsQuintalsNeedTransport`, `insightsCreateTripForRoute`
- `companyVerifiedBadge`, `accountTypeIndividual`, `accountTypeCompany`, `companyNameLabel`
- `promoteListingAction`, `promotionDurationWeek`, `promotionFeeLabel`, `promotionPendingReview`, `promotionActiveUntil`, `promotionRejected`, `promotionNeedsInternet`, `promotionStatsShown`, `promotionStatsOpened`

"Sponsored" needs a native-speaker check in Amharic and Afan Oromo so it reads as paid placement and not as an endorsement by the platform.

## 9. Files to create or modify

**Server**
- `Functions/InsightsFunction.cs`, `Functions/PromotionFunction.cs`, `Functions/MarketAggregateJobFunction.cs` (new)
- `Data/MarketAggregateRepository.cs`, `Data/PromotionRepository.cs` (new)
- `Models/InsightDtos.cs`, `Models/PromotionDtos.cs` (new), `AppJsonContext.cs`
- `Functions/SyncFunction.cs` (`promo_event`, trucker company fields), `Functions/AdminFunction.cs`
- `Models/UserDto.cs`, `Validation/RequestValidator.cs`, `Helpers/UserProfileEdit.cs`
- `Services/EscrowLedgerBook.cs` or a new ledger for `promotion_fee`, Telebirr gateway classes
- Tests under `server/tests/` for each

**Infra**
- `lib/stacks/database-stack.ts` (2 tables, 1 GSI), `lib/stacks/api-stack.ts` (routes, schedule, SSM fee, grants), `lib/config/environments.ts` (flags `marketInsights`, `promotions`), alarms

**Mobile and web**
- `features/insights/` and `features/promotion/` (new: BLoCs, repositories with web variants, widgets, screens)
- `screens/home_screen.dart` (cards per role), `features/crop_listing/.../browse_listings_screen.dart`, `features/trip_listing/.../browse_trips_screen.dart`, `features/buyer/.../match_results_screen.dart`
- `core/database/schemas/` plus `core/models/web/` counterparts, `core/sync/isar_marketplace_sync.dart`
- Trip repositories on both platforms (G9 sort fix)
- Profile and registration for trucker company fields
- 3 `.arb` files

**Admin**
- `pages/PromotionsPage.tsx` (new), `pages/RevenuePage.tsx`, `components/layout/Sidebar.tsx`, user detail (company verify)

**Docs and contracts**
- `specs/contracts/insights-api.md`, `specs/contracts/promotions-api.md` (new), `sync-protocol.md`, `specs/contracts/README.md`
- `docs/local_reality/README.md` and `docs/project-background.md` aligned per D2
- `website/` fees page if promotion goes public

## 10. Risks

| Risk | Mitigation |
|------|------------|
| Role visibility leak through aggregates | Server-side minimum group of 5, woreda granularity, no ids. Tests per role. |
| Pay-to-win erodes trust | One slot per screen, clear label, eligibility gates, organic order untouched. |
| Promoted scam listing gets extra reach | Admin approval, `faydaVerified`, no open fraud case, instant admin takedown. |
| Price median manipulated by a few accounts (see `specs/decisions/f15-security-review-f14.md`) | One listing per farmer counted, minimum group, outliers trimmed by MAD, prefer realized order prices once volume allows. |
| Farmers read the median as a guaranteed price | Wording "typical", always with "as of" time. |
| Demand card sends many truckers to one route | Show top 3, refresh hourly, include open trip count beside demand. |
| Data cost and low-end devices | Text only, payload cap, no ad SDK, no extra images. |
| Ad network SDK | Rejected: needs constant connectivity, spends user data, low payout in this market, unvetted content, APK growth, privacy exposure. |
| AOT | Source-generated JSON only for new DTOs. No new reflection-based packages. |
| Lambda timeout on aggregate walk | Hourly job paged per crop type. Move to stream-updated counters if it nears 10s. |
| Low revenue at current volume | Phase 4 manual sales test demand before any payment code is written. |
| Legal | Ethiopian advertising law is understood to require ads to be identifiable as ads, and ad fees likely carry VAT. Not verified. Confirm with a local advisor before charging. |
| Telebirr refund not built | Rejected promotions refunded through the manual payout queue. |

## 11. Testing

**Server**
- Farmer calling `GET /insights` gets no listing ids, no other farmer fields. Same for trucker.
- Bucket with 4 contributors is absent, with 5 it is present.
- `GET /promotions/active` returns 403 for farmer and trucker.
- Promotion for a listing the caller does not own is rejected.
- Ineligible buyer of promotion (not verified, restricted, open fraud case) is rejected.
- Expired promotion is not returned. Rejected promotion creates a manual refund row.
- `promo_event` idempotency. Promotion payment idempotency.
- Aggregate job: median, one-listing-per-farmer rule, woreda normalisation.

**Mobile and web**
- Each role's home shows its own card and none of the others (all 3 roles).
- Offline: cached insights render with "as of", hide after 48 hours.
- Offline: expired promotion disappears without network.
- Sponsored slot appears once, labelled, and organic order below is unchanged.
- Mobile and web trip browse return the same order after the G9 fix.
- Purchase screen blocks when offline.
- Web build passes: `flutter build web --no-tree-shake-icons`.

**End to end**
- Core marketplace flow unchanged with both flags off and with both on.

## 12. Phases

| Phase | Scope | Closes |
|-------|-------|--------|
| 1 | Aggregates table, hourly job, `GET /insights`, home cards for all 3 roles, flag `marketInsights` | G1, G3, part of G7 |
| 2 | Buyer badges ("Good price", "Space free", "Lowest price on route"), trip sort parity fix | G9 |
| 3 | Trucker company account, admin company verification, badge on trip cards | G4 |
| 4 | Promoted trips, admin-created after offline payment, sponsored slot, `promo_event`, flag `promotions` | G5, G8 |
| 5 | Self-serve purchase with Telebirr, ledger category, advertiser stats, admin Promotions page | G6 |
| 6 | Third-party sponsor cards, sold by hand | rest of G7 |

G2 (geographic index) is not required for phases 1 to 5 because everything keys on normalised woreda names. Revisit if "nearby" insights are wanted.

Each phase follows the Implementation Protocol in `CLAUDE.md` and gets its own tickets in `TASK.md` once approved.

## 13. Open decisions

| # | Decision | Recommendation |
|---|----------|----------------|
| D1 | May farmers and truckers see anonymous aggregates derived from other users' intents, orders and listings? | Yes, under the rules in section 4. Without this, product A exists for buyers only. |
| D2 | Drop the "ad-free" stance in `docs/local_reality/README.md`? | Replace with "no third-party ad networks, first-party labelled promotion only". |
| D3 | Who may buy promotion: truckers only, or farmers too? | Truckers and trucking companies first. Farmers after phase 5 data. |
| D4 | Sell by hand first (phase 4) before building payments (phase 5)? | Yes. |
