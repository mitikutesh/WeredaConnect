# Buyer Match API Contract

## Version
1.0.0

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
  (ties broken by lower `pricePerKg`).
- `count` — Number of matches returned (== `matches.length`).

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

---

## Scoring Model

The relevance score is a transparent weighted sum of five normalised signals
(NOT a learned model). **Weights, bands, and the substitute map must stay
identical between `MatchScoring` (server) and `MatchScorer` (mobile).**

| Signal | Weight | Definition |
|--------|--------|------------|
| Type | 0.35 | Exact crop = 1.0; accepted substitute = 0.6 |
| Price | 0.20 | ≤ budget = 1.0; linear decay across the +15% near-miss band; 0 beyond |
| Geo | 0.20 | Haversine distance decay `1 / (1 + km/50)` when delivery coords + crop GPS are available; else woreda-string proxy (same = 1.0, substring = 0.6, otherwise 0.3) |
| Quantity | 0.15 | `min(listingQuantity / quantityNeeded, 1)`; partial fills score lower |
| Freshness | 0.10 | 1.0 for listings ≤ 7 days old, decaying to a 0.3 floor by 30 days |

**Candidate eligibility** (before scoring): crop type ∈ {requested type +
substitutes}, price ≤ budget × 1.15, and quantity ≥ 50% of `quantityNeeded`.
Listings failing any of these are excluded entirely.

**Accepted substitutes** (lowercase): `wheat↔barley`, `maize↔sorghum`,
`chickpea→{lentil, fababean}`, `lentil→chickpea`. `teff` has no substitute.

---

## Query / Performance

- **Candidate generation**: For the requested type and each accepted substitute,
  the server queries `CropTypeCreatedAtIndex` (PK=`cropType`) and fetches full
  items (the index uses an INCLUDE projection + BatchGetItem). Up to 100
  candidates per type are fetched, then de-duplicated by `cropId`, filtered to
  `status == "available"` and eligibility, scored, and ranked in-Lambda.
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

---

**Last Updated**: 2026-06-19
**Version**: 1.0.0
**Status**: Draft
