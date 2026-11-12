# Buyer Match API Contract

## Version
1.5.0

## Parties
- **Mobile** (client) — buyers matching their purchase intents against crop listings
- **Backend** (server) — scoring and ranking crop listings against buyer criteria

---

## Overview

The Match API ranks crop listings against a buyer's declared intent (crop type,
budget, quantity, delivery destination) and returns the best matches first. It is
the server-side counterpart of the on-device matcher: it lets a buyer fetch a
small, already-ranked result set instead of downloading all listings and scoring
them on the device.

**Key Features**:
- **Shared scoring**: The server uses the *same weighted, multi-signal formula*
  as the mobile on-device scorer (`MatchScorer` in
  `mobile/lib/features/buyer/domain/services/match_scorer.dart`,
  `MatchScoring` in `server/src/Helpers/MatchScoring.cs`). Server-ranked and
  device-ranked results must agree. **This parity is a hard invariant.**
- **Substitute crops**: A buyer asking for one crop also matches accepted
  substitutes (e.g. wheat ↔ barley), scored below an exact match.
- **Near-miss tolerance**: Listings slightly over budget or short on quantity are
  still returned, with a decayed score (not hard-filtered out).
- **True geo-distance**: When the buyer supplies delivery coordinates, geo
  relevance uses haversine distance from the crop's stored GPS location;
  otherwise it falls back to a woreda-string proximity proxy.

---

## Endpoints

### Match Crop Listings
**Method**: GET /match
**Authentication**: Required (Cognito JWT). **Buyer and Admin only** (F-36) —
matching reads ALL crop listings, which only buyers/admins may do. Farmers,
truckers, other roles, and unauthenticated callers receive `403 MATCH_FORBIDDEN`.

**Query Parameters**:

| Parameter | Type | Required | Description | Example |
|-----------|------|----------|-------------|---------|
| `cropType` | string | **Yes** | Crop the buyer wants (lowercase English name) | `cropType=wheat` |
| `maxPricePerKg` | number | No | Budget ceiling in ETB; a near-miss band of +15% still matches (decayed) | `maxPricePerKg=120` |
| `quantityNeeded` | number | No | Needed quantity in quintals; listings covering ≥50% still match (decayed) | `quantityNeeded=10` |
| `deliveryWoreda` | string | No | Delivery destination woreda (used for the woreda-string geo fallback) | `deliveryWoreda=Adama` |
| `latitude` | number | No | Delivery point latitude (resolved client-side from the woreda) for haversine geo | `latitude=8.54` |
| `longitude` | number | No | Delivery point longitude | `longitude=39.27` |
| `limit` | number | No | Max ranked matches to return (default 20, max 100) | `limit=20` |

**Example Request**:
```
GET /match?cropType=wheat&maxPricePerKg=120&quantityNeeded=10&deliveryWoreda=Adama&latitude=8.54&longitude=39.27&limit=20
```

**Response (200)**:
```json
{
  "matches": [
    {
      "cropId": "crop-001",
      "farmerId": "farmer-123",
      "farmerName": "Abebe Kebede",
      "cropType": "wheat",
      "woreda": "Adama",
      "quantityKg": 12,
      "pricePerKg": 95.0,
      "status": "available",
      "createdAt": "2026-06-15T08:00:00Z",
      "latitude": 8.54,
      "longitude": 39.27,
      "score": 0.97,
      "isSubstitute": false
    },
    {
      "cropId": "crop-044",
      "farmerId": "farmer-987",
      "farmerName": "Mulu Tadesse",
      "cropType": "barley",
      "woreda": "Bishoftu",
      "quantityKg": 10,
      "pricePerKg": 88.0,
      "status": "available",
      "createdAt": "2026-06-14T09:30:00Z",
      "latitude": 8.75,
      "longitude": 38.98,
      "score": 0.71,
      "isSubstitute": true
    }
  ],
  "count": 2
}
```

**Response Fields**:
- `matches` — Array of scored crop matches, **ranked by `score` descending**
  (ties broken by lower `pricePerKg`; the mobile on-device ranker applies the
  same tie-break so online/offline orderings agree).
- `count` — Number of matches returned (== `matches.length`).
- `totalMatches` — Total eligible matches BEFORE the `limit` cut. Clients
  derive match-count badges from this so counts don't saturate at the page
  limit.

**Error Codes**:
- `400 MATCH_VALIDATION` — Missing `cropType`, or a non-numeric
  `maxPricePerKg` / `quantityNeeded` / `latitude` / `longitude` / `limit`.
- `403 MATCH_FORBIDDEN` — Caller is not a buyer or admin.
- `405 MATCH_VALIDATION` — Method other than GET.
- `500 MATCH_SERVER_ERROR` — Server-side failure.

---

## Data Models

### MatchDto

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `cropId` | string (UUID) | Yes | Crop listing identifier |
| `farmerId` | string (UUID) | Yes | Farmer who posted the listing |
| `farmerName` | string | No | Farmer's display name |
| `cropType` | string | Yes | Crop type of the matched listing (may differ from the requested type for substitutes) |
| `woreda` | string | Yes | Listing's woreda |
| `quantityKg` | number (int) | Yes | Listing quantity (stored unit; quintals) |
| `pricePerKg` | number | Yes | Listing price per kg (ETB) |
| `status` | string | Yes | Always `available` in match results |
| `createdAt` | string (ISO 8601) | Yes | Listing creation timestamp |
| `latitude` | number | No | Crop GPS latitude (absent if the farmer had no GPS fix) |
| `longitude` | number | No | Crop GPS longitude |
| `score` | number | Yes | Relevance score in `[0, 1]` (higher = better match) |
| `isSubstitute` | boolean | Yes | `true` when matched via a substitute crop type, not the exact ask |
| `farmerReliability` | number | No | Deprecated (1.4.0, ESC-30): the old listing-conversion value in `[0, 1]`. No longer written and not part of the score; returned only for a listing that still stores one |
| `ownerTier`, `ownerEffectiveRating`, `ownerRatingCount`, `ownerReliabilityPct`, `ownerReputationAt` | string, number, number, number, string | No | 1.3.0 (ESC-29): the farmer's public reputation as copied onto the listing (`crop-listing-api.md` 1.11.0, `reputation-api.md` section 7); returned only while `reputationPublicTags` is on. Since 1.4.0 `ownerReliabilityPct` is the score's reliability input (see the scoring model) |

---

## Scoring Model

The relevance score is a transparent weighted sum of six normalised signals
(NOT a learned model). **Weights, bands, and the substitute map must stay
identical between `MatchScoring` (server) and `MatchScorer` (mobile).**

| Signal | Weight | Definition |
|--------|--------|------------|
| Type | 0.30 | Exact crop = 1.0; accepted substitute = 0.6 |
| Price | 0.20 | ≤ budget = 1.0; linear decay across the +15% near-miss band; 0 beyond |
| Geo | 0.18 | Haversine distance decay `1 / (1 + km/50)` when delivery coords + crop GPS are available; else woreda-string proxy on normalised names (same = 1.0, whole-word overlap = 0.6, otherwise 0.3) |
| Quantity | 0.12 | `min(listingQuantity / quantityNeeded, 1)`; partial fills score lower |
| Freshness | 0.10 | 1.0 for listings ≤ 7 days old, decaying to a 0.3 floor by 30 days |
| Reliability | 0.10 | 1.4.0 (ESC-30): the farmer's `ownerReliabilityPct / 100`, clamped to [0, 1] (`reputation-api.md` 1.4 and section 7); a listing whose reader gets no `ownerReliabilityPct` uses the neutral 0.7 |

**Reliability input (1.4.0, ESC-30).** `ownerReliabilityPct` is the farmer's completed orders
over completed, fault-cancelled and no-show orders in 90 days, in whole percent, copied onto the
listing by the server. The score uses exactly the value the caller is given for the row, on both
sides: the server scores `GET /match` with the field as it returns it, the apps score from the
field as they stored it (the Isar row on the phone, the `GET /crops` row on web).

- **Absent means neutral (0.7).** The field is absent while `reputationPublicTags` is off, for a
  farmer with fewer than 3 orders in the window, and on a row that has no copy yet. All three
  score as 0.7, so with the flag off every listing has the same reliability term and the ranking
  is the one the other five signals give. 0.7 is kept because it is what nearly every listing
  scored with before (the old `farmerReliability` was null, or 1.0 in rare cases, see below), so
  turning the flag on only adds signal: 70 and above moves a listing up by at most 0.03, below
  70 moves it down by at most 0.07.
- **`farmerReliability` is retired.** It was sold / (sold + expired) over the farmer's listings
  at create time; `expired` never happens, so it was absent or 1.0. The server no longer computes
  or stores it (a client value is dropped), and neither scorer reads it. The one ranking change
  with the flags off: a listing that still stores `farmerReliability` 1.0 loses the 0.03 it had
  over a listing without the field.
- **Push alerts** (`CropMatchStreamFunction`) score a new listing with the neutral reliability,
  whatever the flags.
- **Role visibility.** Only buyers and admins get scores. A farmer's or trucker's phone holds only
  their own rows, so no other party's reliability reaches them.

**Candidate eligibility** (before scoring): crop type ∈ {requested type +
substitutes}, price ≤ budget × 1.15, and quantity ≥ 50% of `quantityNeeded`.
Listings failing any of these are excluded entirely.

**Accepted substitutes** (lowercase, matching the stored `CropType` enum
names): `wheat↔barley`, `maize↔sorghum`, `chickpea↔lentils`. `teff` has no
substitute.

**Woreda name normalisation** (v1.1.0). Before the woreda-string proxy compares
`deliveryWoreda` with the listing's `woreda`, both sides go through ONE shared
rule (`WoredaNames` on the server, `normalizeWoreda` on mobile; the two must
stay identical):

1. keep only the place segment before a `, Region` / `(Region)` suffix
   (Latin `,` or Ethiopic `፣` comma, `(` or `[`);
2. lowercase, fold Latin diacritics with the shared explicit table
   (`Ādama` to `adama`), collapse whitespace;
3. drop apostrophes (`Debre Mark’os` to `debre markos`), treat `-`, `_`, `/`
   as spaces.

Ge'ez text passes through unchanged. Comparison: equal keys = same place
(1.0); every word of the shorter name present as a whole word in the longer
(`Adama` vs `Adama Zone`) = partial (0.6); anything else, including a blank
side, = neutral (0.3). Whole-word overlap replaces the raw substring rule,
which scored `Ambo` as a partial match of `Gambo`. Mobile applies the same
rule when it pairs matched crops with trucker trips, so a trip typed as
`Adama, Oromia` is suggested for a crop picked as `Ādama`.

### Trips paired with the matches (1.2.0)

`/match` ranks crops only and reads no trips. The apps pair each matched crop with trucker
trips themselves: the mobile matcher (`MatchingService.findTripsForListing`, on the Isar cache)
and the web matcher (`WebMatchingService`, on `GET /trips?status=open`) keep only `open` trips
departing today or later in Ethiopia (Africa/Addis_Ababa), the day rule of FUP-85, with room
for the quantity. Since `trip-listing-api.md` 2.2.0 the server's open-trips browse uses the
same cutoff, so the trips the phone caches and the ones the web reads agree, and a trip that
left is never suggested. No wire change.

**Order of the suggested trips (1.4.0, ESC-30).** Cheapest `pricePerQuintal` first, as before.
Among trips at the same price the trucker with the higher `ownerReliabilityPct` (`trip-listing-api.md`
2.6.0) comes first; a trip without the field counts as the neutral 70. The first suggestion is
therefore still a cheapest trip (the estimated total uses it). Since 1.5.0 the route score comes
between the two, and ties no longer keep their input order (below).

### Trip pairing by distance (1.5.0, MATCH-01)

The route used to be a hard woreda-name filter: a trip from a neighbouring woreda was never
suggested. One shared, pure function now decides which trips to suggest and in what order:
`MatchScorer.suggestTrips` on the phone (over the Isar cache) and on web (over
`GET /trips?status=open`), mirrored in the server's `MatchScoring.SuggestTrips` for parity (the
server has no caller yet; `/match` still reads no trips). No model, no new field, no network call:
the phone uses its cached trips and the centroid table it ships (`WoredaCentroids`).

**Eligibility (a trip outside it is dropped, not scored down):**

| Rule | Detail |
|------|--------|
| Status | `open` |
| Departure day | today or later in Ethiopia (FUP-85, the `GET /trips` cutoff) |
| Order lead | departs at least `orderLeadHours` after now when the server sends a lead (FUP-122) |
| Room left | `remainingQuintals`, else `capacity`, at least the intent's quantity minus 0.01 quintal (the order create's tolerance); a full trip never appears. With no positive quantity the trip needs 1 quintal of room |
| Deadline | departs on or before the buyer's `neededByDate` when set (departure, not arrival: trips carry no transit time) |

**Route.** Each end of the trip is scored against the place it should serve: the trip's
`fromWoreda` against the crop's woreda (`locationName`), its `toWoreda` against the intent's
`deliveryWoreda`. An end scores the best of these, whether or not the centroids are known:

- 1.0 when the two names are the same place after the shared woreda normalisation;
- `1 / (1 + km / 50)` (the crop geo decay) when both woredas have a centroid and they are at most
  **40 km** apart, the distance being the haversine between centroids (radius 6371000 m, divided
  by 1000) rounded half away from zero to 0.1 km (so every platform gets the same number);
- 0.6 for a whole-word name match. For a woreda without a centroid (as `Hawassa Zuria`) only the
  name rules can apply.

An end that scores none of these (no centroid and no name match, or more than 40 km away), or a
blank name, drops the trip. The route score is the mean of the two ends; 1.0 is the exact route.

**Direction.** When the crop's and the delivery woredas are close, a trip running the other way
can have each end within 40 km of a wanted place (crop in Adama going to Bishoftu, trip Bishoftu
to Adama: both ends 39.5 km off). The route is therefore also scored with the crop's and the
delivery woreda swapped, and the trip is dropped when the swapped score is higher than the
forward one. Equal scores stay (the crop and the delivery in one woreda, or a trip that stays in
one place).
The crop's woreda centroid is used, not its GPS fix, because `GET /crops` may omit the fix and
both apps must see the same input. Examples: Shashemene to Hawassa is 22.0 km (end 0.694, route
0.847 with an exact destination); Adama to Bishoftu 39.5 km (kept); Addis Ababa to Bishoftu
40.8 km (dropped); `Addis Abeba` and `Addis Ababa` share one centroid (0 km, scored 1.0). A trip
that only passes through the crop's woreda on its way is not suggested (follow-up).

**Order.** Cheapest `pricePerQuintal` first (the estimated total still uses the first trip);
then the higher route score, so the exact route ranks above a neighbouring woreda at the same
price; then the more reliable trucker (ESC-30); then the earlier departure; then `tripId`
(ordinal). The order therefore never depends on the order the trips came in, so the phone's
cache and the web's API pages give the same list.

**Parity.** `match_scorer_test.dart` ("trip pairing (MATCH-01)") and `MatchScoringTests.cs`
(`TripEndScore_GoldenVectors`, `SuggestTrips_GoldenOrder_IsStableAcrossPlatforms`,
`TripRouteScore_Reverse`, `SuggestTrips_Reverse` and the tests next to them) carry the same literal coordinates and the same trip set, and assert the same
distances, scores and order. The two matching services run the same scenario
(`MATCH-01: trips by centroid distance`).

---

## Query / Performance

- **Candidate generation**: For the requested type and each accepted substitute,
  the server queries `CropTypeCreatedAtIndex` (PK=`cropType`) and fetches full
  items (the index uses an INCLUDE projection + BatchGetItem). When the caller
  has a budget, the price ceiling (budget × 1.15 near-miss band) is pushed into
  the GSI query on the projected `pricePerKg`, so over-budget crops are skipped
  before the full-item fetch and candidate pages contain only price-eligible
  listings. Up to 100 candidates per page × 20 pages per type are walked
  (truncation beyond that is logged), then de-duplicated by `cropId`, filtered
  to `status == "available"` and eligibility, scored, and ranked in-Lambda.
- **No new index required**: crop `latitude`/`longitude` are already persisted by
  the sync ingestion path, so haversine geo needs no schema change. A geohash GSI
  is intentionally **not** used — it serves area-browse ("crops near a point, any
  type"), not type-driven intent matching.
- **No pagination**: `/match` returns a single ranked page (`limit`), not a
  `nextToken` cursor — matching is a ranked top-N, not a browse.

---

## Invariants

1. **Scoring parity** — `MatchScoring` (server) and `MatchScorer` (mobile)
   produce the same ranking for the same inputs. Guarded by parity tests on both
   sides; any weight/band/substitute change must update both.
2. **Buyer-sees-all** — Only buyers and admins may call `/match`; it ranks
   listings from ALL farmers (never restricted to one farmer).
3. **Available only** — Only `status == "available"` listings appear in results.
4. **Graceful geo degradation** — Missing delivery coordinates, a crop without a
   GPS fix, or an unknown woreda must not error; geo falls back to the woreda
   proxy.
5. **Read-only** — `/match` never mutates state.

---

## Ethiopian Context

- Buyers typically deliver to **market cities** (Addis Ababa, Adama, Hawassa,
  Mekelle, …) while crops are listed in **rural woredas**. The client resolves the
  delivery woreda to coordinates from a curated centroid table
  (`WoredaCentroids`) and passes `latitude`/`longitude`; the server stays free of
  geographic data.
- Quantities are in **quintals** (100 kg). `quantityNeeded` and the listing
  quantity are compared in the same unit.

---

## Future Enhancements

- **Crop + trip bundling**: pair each matched crop with a trucker trip whose route
  fits the crop → delivery corridor.
- **Geohash GSI**: add an area-browse query path ("nearby/trending crops") backed
  by a geohash GSI and bounding-box candidate generation.
- **Server-cached scores**: persist `serverScore` so devices can re-rank a cached
  pool offline without recomputation.

---

## Changelog

| Version | Date | Change |
|---------|------|--------|
| 1.0.0 | 2026-06-19 | Initial contract: GET /match endpoint, MatchDto, shared scoring model, candidate-generation and invariants. |
| 1.1.0 | 2026-10-06 | Woreda-string geo proxy compares normalised names (region suffix cut, diacritics folded, whole-word partial) on both server and mobile (SIM-52). No wire change. |
| 1.2.0 | 2026-11-04 | Trips paired with the matches (L-7): the app-side pairing keeps only open trips departing today or later in Ethiopia, the same cutoff `GET /trips` applies since `trip-listing-api.md` 2.2.0. `/match` reads no trips and is unchanged. No wire change. |
| 1.3.0 | 2026-11-11 | Restricted farmers and owner reputation (ESC-29, `reputation-api.md` 1.2.0 sections 7 and 8.3), off in every environment: with `reputationPenalties` on, a listing whose stored owner tier is `restricted` is no candidate for a buyer; with `reputationPublicTags` on, each match carries `ownerTier`, `ownerEffectiveRating`, `ownerRatingCount`, `ownerReliabilityPct` and `ownerReputationAt`. The score is unchanged. |

| 1.4.0 | 2026-11-12 | Reliability from reputation (ESC-30, `reputation-api.md` 1.3.0 section 7): the score's reliability input is the listing's `ownerReliabilityPct / 100` as the caller is given it, the neutral 0.7 when absent (flag off, under 3 orders, no copy), identical in `MatchScoring` and `MatchScorer`; `farmerReliability` is no longer written or scored (deprecated in `MatchDto`); the app-side trip suggestions order equal prices by the trucker's `ownerReliabilityPct`. No new field on the wire. |
| 1.5.0 | 2026-11-12 | Trip pairing by distance (MATCH-01): a trip is suggested when each end is the same woreda, within 40 km of it by centroid distance (rounded to 0.1 km), or a whole-word name match (the best of the three counts), and it does not run the other way (the route with crop and delivery swapped must not score higher); room left must cover the quantity less 0.01 quintal (now also on web), 1 quintal when no quantity; order is price, route score (exact above a neighbouring woreda), trucker reliability, earlier departure, `tripId`, so ties no longer depend on input order. One pure rule, `MatchScorer.suggestTrips`, on phone and web, mirrored in `MatchScoring.SuggestTrips` with shared golden vectors. `/match` unchanged. No wire change. |

---

**Last Updated**: 2026-11-12
**Version**: 1.5.0
**Status**: Draft
