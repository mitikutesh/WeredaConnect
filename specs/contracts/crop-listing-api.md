# Crop Listing API Contract

## Version
1.13.0

## Parties
- **Mobile** (client) — farmers creating and browsing crop listings
- **Backend** (server) — storing, validating, and serving crop listings

---

## Overview

The Crop Listing API enables farmers to post crop availability (type, quantity, price, location) for transport providers and wholesalers to discover. This is the **first user-facing feature** in WeredaConnect's offline-first architecture.

**Key Features**:
- **Offline-first**: Listings created offline via POST /sync, browsed from local Isar cache
- **Ethiopian crops**: 9 crop types (Teff, Wheat, Barley, Maize, Chickpea, Lentils, Sorghum, Coffee, Sesame)
- **Location-based**: Query by woreda (district) for local sourcing
- **Price transparency**: Price per kg in Ethiopian Birr (ETB)
- **Soft deletes**: A deleted listing keeps its row, with `status: deleted` and `deletedAt`; it is not physically removed

---

## Endpoints

### 1. Create/Update/Delete Crop Listing
**Method**: POST /sync
**Authentication**: Required (Cognito JWT)
**Content-Type**: application/json

**Request Body**:
```json
{
  "operations": [
    {
      "operationId": "uuid-v4",
      "idempotencyKey": "farmer-123:crop_listing:crop-456:create:1707998400000",
      "entityType": "crop_listing",
      "entityId": "crop-456",
      "action": "create",
      "clientVersion": 1,
      "data": {
        "farmerId": "farmer-123",
        "cropType": "teff",
        "quantity": 50.5,
        "unit": "quintals",
        "pricePerKg": 45.0,
        "currency": "ETB",
        "location": {
          "woreda": "Debre Birhan",
          "zone": "North Shewa",
          "region": "Amhara",
          "coordinates": {
            "latitude": 9.6778,
            "longitude": 39.5318
          }
        },
        "description": "High-quality white teff, harvested last week",
        "status": "available",
        "harvestDate": "2026-02-01T00:00:00Z"
      },
      "timestamp": "2026-02-15T10:30:00Z"
    }
  ]
}
```

**Response (Success)**:
```json
{
  "results": [
    {
      "operationId": "uuid-v4",
      "status": "success",
      "serverVersion": 1,
      "message": "Crop listing created successfully"
    }
  ]
}
```

**Response (Conflict)**:
```json
{
  "results": [
    {
      "operationId": "uuid-v4",
      "status": "conflict",
      "serverVersion": 3,
      "serverData": {
        "farmerId": "farmer-123",
        "cropType": "teff",
        "quantity": 45.0,
        "pricePerKg": 50.0,
        "status": "sold",
        "version": 3
      },
      "message": "Conflict: Server version is newer. Accept server data or re-submit with updated version."
    }
  ]
}
```

**Error Codes**:
- `400 Bad Request` — Invalid crop type, negative quantity, missing required fields
- `401 Unauthorized` — Invalid or expired JWT token
- `409 Conflict` — Client version behind server version
- `422 Unprocessable Entity` — Validation error (e.g., quantity > 10,000 quintals)
- `CROP_HAS_ACTIVE_ORDERS` (1.7.0, per-operation `error` result of `POST /sync`): a `delete`, or an `update` to `status: deleted`, of a listing that orders still depend on (Validation rule 10)
- `429 Too Many Requests`: `SYNC_RATE_LIMITED` when the user sent more than 60 sync requests in 60 seconds (`Retry-After: 60`), or API Gateway's own throttle (25 req/s per route, burst 50, no `error.code`). See Rate Limiting
- `500 Internal Server Error` — Server-side failure

---

### 2. Browse Crop Listings
**Method**: GET /crops
**Authentication**: Required — browse-all is restricted to Buyer and Admin (F-36). Farmers see only their own crops; truckers, other roles, and unauthenticated callers receive 403.
**Query Parameters**:

| Parameter | Type | Required | Description | Example |
|-----------|------|----------|-------------|---------|
| `farmerId` | string | No | Only this farmer's listings. A farmer always gets their own: another id is `403 CROP_FORBIDDEN` | `farmerId=farmer-123` |
| `cropType` | string | No | Filter by crop type (enum) | `cropType=teff` |
| `woreda` | string | No | Filter by woreda (district), exact match | `woreda=Debre Birhan` |
| `status` | string | No | Filter by status (enum). Default (1.9.0): `available` for buyers and admins; a farmer's own list has every status unless one is given | `status=available` |
| `limit` | number | No | Page size, default 20. A value above 50 is served as 50 (1.9.0); not a whole number, or below 1: `400 CROP_VALIDATION` | `limit=20` |
| `nextToken` | string | No | Pagination token from the previous response | `nextToken=eyJ...` |

`zone`, `region`, `minQuantity` and `maxPrice` were listed up to 1.8.0 but never built: the server ignores them. The web marketplace filters price on the device.

**Example Request**:
```
GET /crops?cropType=teff&woreda=Debre%20Birhan&limit=20
```

**Response** (the read model, see Data Models):
```json
{
  "crops": [
    {
      "cropId": "crop-456",
      "farmerId": "farmer-123",
      "farmerName": "Alemayehu Bekele",
      "cropType": "teff",
      "woreda": "Debre Birhan",
      "quantityKg": 50.5,
      "pricePerKg": 45.0,
      "status": "available",
      "createdAt": "2026-02-15T10:30:00Z",
      "updatedAt": "2026-02-15T10:30:00Z",
      "version": 1,
      "description": "High-quality white teff, harvested last week",
      "locationName": "Debre Birhan",
      "latitude": 9.6778,
      "longitude": 39.5318,
      "photos": [
        { "photoId": "7d1c0a52-3f0e-4c4b-9a8e-0f3b6c2d9e11", "thumbUrl": "https://..." }
      ]
    }
  ],
  "nextToken": "eyJ...",
  "hasMore": true
}
```

**Response Fields**:
- `crops`: the listings of this page, newest first. `quantityKg` holds quintals (see Data Models).
- `nextToken`: present only when at least one more listing matches (1.9.0). Absent, never `null`, on the last page.
- `hasMore` (1.9.0): `true` exactly when `nextToken` is present.
- There is no `total` (listed up to 1.8.0, never returned). Clients that need a count follow `nextToken` to the end.

**Paging (1.9.0, SIM-26)**: DynamoDB applies the filters (soft-deleted listings, `woreda`, and `status` on a farmer's list) after it counts the page, so one read can come back short while matching listings remain. The server reads again until the page is full, then reads one more listing to learn whether anything follows. Before 1.9.0 a full last page carried a `nextToken` that led to an empty page, and there was no `hasMore`. One request makes at most 5 reads: in a long run of filtered-out listings a page can hold fewer than `limit` listings (even none) while `hasMore` is `true`. Clients keep following `nextToken` while it is present and never treat a short page as the end.

**Error Codes**:
- `400 CROP_VALIDATION`: `limit` is not a whole number of at least 1, or `nextToken` is malformed or was changed (tokens are HMAC-signed)
- `403 CROP_FORBIDDEN`: a trucker, another role or no caller identity; a farmer asking for another farmer's listings
- `500 CROP_SERVER_ERROR`: server-side failure

---

### 3. Get Single Crop Listing
**Method**: GET /crops/{id}
**Authentication**: Required (F-36). Buyers and admins read any listing; a farmer reads only their own; truckers, other roles and callers without an identity get `403 CROP_FORBIDDEN`.
**Path Parameters**: `id` (string, UUID): the listing id (`cropId`)

**Example Request**:
```
GET /crops/crop-456
```

**Response**: the listing in the read model (see Data Models), wrapped in `crop`:
```json
{
  "crop": {
    "cropId": "crop-456",
    "farmerId": "farmer-123",
    "farmerName": "Alemayehu Bekele",
    "farmerPhone": "+251911123456",
    "cropType": "teff",
    "woreda": "Debre Birhan",
    "quantityKg": 50.5,
    "pricePerKg": 45.0,
    "status": "available",
    "createdAt": "2026-02-15T10:30:00Z",
    "updatedAt": "2026-02-15T10:30:00Z",
    "version": 1,
    "description": "High-quality white teff, harvested last week",
    "locationName": "Debre Birhan",
    "latitude": 9.6778,
    "longitude": 39.5318,
    "photos": [
      { "photoId": "7d1c0a52-3f0e-4c4b-9a8e-0f3b6c2d9e11", "thumbUrl": "https://...", "fullUrl": "https://..." }
    ]
  }
}
```

**Note**: `farmerPhone` is only included for the listing's own farmer and for admins (1.3.0, ESC-48). A buyer never gets it: contact opens on the order once the farmer accepted the paid order (`order-lifecycle.md` 1.18.0).

**Error Codes**:
- `400 CROP_VALIDATION`: no id in the path
- `403 CROP_FORBIDDEN`: not a buyer or admin, or a farmer reading another farmer's listing
- `404 CROP_NOT_FOUND`: no such listing, or it was deleted (`deletedAt` set, or `status: deleted`, 1.10.0)
- `500 CROP_SERVER_ERROR`: server-side failure

---

### 4. Request Photo Upload URLs (1.6.0)
**Method**: POST /crops/{id}/photos
**Authentication**: Required. Farmers only; any other role gets `403 CROP_FORBIDDEN`.
**Path Parameters**: `id` (string, UUID): the listing id. The device makes it, so the listing may not exist on the server yet (offline-first: photos go up before the create).

Every listing carries 2 to 4 photos (rule 9). The device uploads each original once to a private incoming bucket; a processor Lambda turns it into the only two renditions that are kept (see Photo Renditions). Full design and costs: `specs/architecture/crop-photos-architecture.md`.

**Request Body**:
```json
{
  "photos": [
    { "photoId": "7d1c0a52-3f0e-4c4b-9a8e-0f3b6c2d9e11", "contentType": "image/jpeg", "sizeBytes": 412873 }
  ]
}
```

| Field | Rule |
|-------|------|
| `photos` | 1 to 4 entries |
| `photoId` | UUID, made on the device; distinct within the request |
| `contentType` | `image/jpeg`, `image/png` or `image/webp` |
| `sizeBytes` | 1 to 10485760 (10 MiB): the exact length of the bytes the device will PUT |

**Ownership**: when a listing with this id already exists, its `farmerId` must be the caller (`403 CROP_FORBIDDEN` otherwise). The upload key is always built from the caller's own id, so a farmer can never write into another farmer's photos.

**Response (200)**:
```json
{
  "uploads": [
    {
      "photoId": "7d1c0a52-3f0e-4c4b-9a8e-0f3b6c2d9e11",
      "uploadUrl": "https://<incoming-bucket>.s3.eu-north-1.amazonaws.com/<farmerId>/<cropId>/<photoId>?X-Amz-...",
      "contentType": "image/jpeg",
      "sizeBytes": 412873
    }
  ],
  "expiresInSeconds": 900
}
```

The device sends `PUT uploadUrl` with the raw bytes, `Content-Type` equal to `contentType` and `Content-Length` equal to `sizeBytes`. Both headers are signed, so any other type or length fails at S3 with `403 SignatureDoesNotMatch` and nothing is stored. A URL lives 15 minutes; a device that missed it asks again.

**Error Codes**:
- `400 CROP_VALIDATION`: bad id, body, count, type or size
- `403 CROP_FORBIDDEN`: not a farmer, or the listing belongs to another farmer
- `500 CROP_SERVER_ERROR`: signing failed or photo storage is not configured

### Photo Renditions (1.6.0)

The S3 `ObjectCreated` event on the incoming bucket (key `{farmerId}/{cropId}/{photoId}`) runs the photo processor. It:

1. Deletes, without decoding, an object over 10 MiB or a key that is not three UUID segments.
2. Reads only the header first and refuses an image over 40 megapixels (decompression bombs) or a format other than JPEG, PNG or WebP.
3. Applies the EXIF orientation, then drops all metadata (EXIF, GPS, ICC, XMP), so the farm's location never reaches a buyer.
4. Writes the two renditions to the private photos bucket, never upscaling:

| Rendition | Key | Longest edge | Encoding | Typical size | Used by |
|-----------|-----|--------------|----------|--------------|---------|
| `thumb` | `{farmerId}/{cropId}/{photoId}/thumb.webp` | 320 px | WebP, quality 60 | 8 to 20 KB | Marketplace list cards |
| `full` | `{farmerId}/{cropId}/{photoId}/full.webp` | 1024 px | WebP, quality 75; re-encoded at 60 when over 150 KB | 60 to 150 KB | Listing detail and zoom |

5. Deletes the original. A failed decode deletes it too and writes nothing. The incoming bucket expires anything still there after 1 day.

Processing is asynchronous (usually 1 to 3 seconds). Until it finishes, or when it refused the photo, the photo's URLs answer 403/404 from S3 and clients show a placeholder.

---

## Data Models

### CropListing Entity

The listing's domain fields. The read endpoints answer in the flat read model further down.

| Field | Type | Required | Constraints | Description |
|-------|------|----------|-------------|-------------|
| `id` | string (UUID) | Yes | — | Unique crop listing identifier |
| `farmerId` | string (UUID) | Yes | — | Farmer who created the listing |
| `farmerName` | string | Yes | 1-100 chars | Farmer's display name |
| `farmerPhone` | string | Yes | E.164 format | Farmer's number, server-stamped at create; returned only to the owning farmer and admins (1.3.0) |
| `cropType` | enum | Yes | See Crop Types | Type of crop |
| `quantity` | number | Yes | 0.01-10,000 | Quantity available: the free stock. A funded order's quintals leave it at funding (1.7.0, rule 10) |
| `unit` | string | Yes | Fixed: "quintals" | Unit of measurement (100kg) |
| `pricePerKg` | number | Yes | 0.01-1,000 | Price per kilogram in ETB |
| `currency` | string | Yes | Fixed: "ETB" | Currency code (Ethiopian Birr) |
| `location.woreda` | string | Yes | 1-100 chars | District name |
| `location.zone` | string | Yes | 1-100 chars | Zone name |
| `location.region` | string | Yes | 1-100 chars | Region name |
| `location.coordinates.latitude` | number | No | -90 to 90 | GPS latitude |
| `location.coordinates.longitude` | number | No | -180 to 180 | GPS longitude |
| `description` | string | No | 0-500 chars, no phone number (1.4.0) | Additional details |
| `status` | enum | Yes | See Status Types | Listing status |
| `harvestDate` | string (ISO 8601) | Yes | — | Date crop was harvested |
| `createdAt` | string (ISO 8601) | Yes | — | Timestamp when listing was created |
| `updatedAt` | string (ISO 8601) | Yes | — | Timestamp when listing was last updated |
| `deletedAt` | string (ISO 8601) | No | — | Timestamp when listing was soft-deleted |
| `version` | number | Yes | ≥1 | Optimistic concurrency control version |
| `stockDecrementedFor`, `stockRestoredFor`, `stockRefusedFor` | string set | No | Server-only | Stored item only: the orders whose funding took stock, whose stock came back, and (1.7.0) whose funding found too little stock and took none. Never sent by clients; not in `GET /crops` |
| `photoIds` | string[] (UUID) | Yes on create (1.6.0) | 2 to 4, distinct | Sync payload and stored item only: the listing's photos in display order (the first is the cover). Absent on listings made before 1.6.0 |
| `photos` | object[] | No | none | Read responses only (1.6.0): one entry per `photoIds` element, same order. `GET /crops` gives `{photoId, thumbUrl}`; `GET /crops/{id}` gives `{photoId, thumbUrl, fullUrl}`. Omitted when the listing has no photos |

**Photo URLs (1.6.0)**: `thumbUrl` and `fullUrl` are presigned S3 GET URLs, signed only for a caller who passed the visibility gate above. `thumbUrl` lives 6 hours and `fullUrl` 1 hour (less when the server's signing credentials expire first). The query string changes on every read, so clients cache an image by `photoId` and rendition, never by URL, and treat a 403 from S3 as "fetch the listing again".

### Read Model (`GET /crops`, `GET /crops/{id}`) (1.9.0)

The read endpoints return one flat object per listing (server `CropDto`). It differs from the entity above, and clients (mobile, web, e2e) depend on these names, so they are kept as they are (SIM-26). `GET /crops` lists them under `crops`; `GET /crops/{id}` wraps the one listing in `crop`. A field without a value is omitted, never `null`.

| Field | Type | Always | Description |
|-------|------|--------|-------------|
| `cropId` | string (UUID) | Yes | The listing id (`id` in the entity; `entityId` on `/sync`) |
| `farmerId` | string | Yes | Owner |
| `farmerName` | string | No | Stamped at create from the farmer's profile (1.2.0) |
| `farmerPhone` | string | No | Only to the owning farmer and admins (1.3.0) |
| `cropType` | string | Yes | Crop type enum |
| `woreda` | string | Yes | Woreda (flat; there is no nested `location` object, and no `zone` or `region`) |
| `quantityKg` | number | Yes | The free stock in **quintals** (100 kg), despite the name: the stored `quantity`, the same number the farmer sends as `quantity` on `/sync`. The name predates the unit and every client reads it, so it is not renamed |
| `pricePerKg` | number | Yes | ETB per kilogram |
| `status` | string | Yes | Status enum |
| `createdAt`, `updatedAt` | string (ISO 8601) | Yes | Server timestamps |
| `version` | number | Yes | Optimistic concurrency version |
| `description` | string | No | Free text (rule 6) |
| `location` | string | No | Legacy: a free-text place some older listings carry; `/sync` does not accept it |
| `locationName` | string | No | The place the farmer entered for this listing; clients show it before `woreda` |
| `deletedAt` | string (ISO 8601) | No | Soft-delete stamp, written by a `delete` and by an `update` to `status: deleted` (1.10.0); a deleted listing is never listed and `GET /crops/{id}` answers 404 for it. A row with `status: deleted` and no stamp is treated the same |
| `farmerReliability` | number | No | Deprecated (1.12.0, ESC-30): no longer stamped at create and not used by matching (`ownerReliabilityPct` is); returned only for a listing that still stores one |
| `ownerTier` | string | No | 1.11.0 (ESC-29, `reputation-api.md` section 7): the farmer's public reputation tier, `good` or `low`, copied onto the listing by the server. Returned only while `reputationPublicTags` is on; never taken from a client |
| `ownerEffectiveRating` | number | No | The farmer's rating after the reputation deduction, 1.0 to 5.0 with one decimal; absent with no ratings |
| `ownerRatingCount` | number | No | How many ratings the farmer has |
| `ownerReliabilityPct` | number | No | 0 to 100: completed orders out of completed, cancelled at fault and no-shows in 90 days; absent below 3 orders |
| `ownerReputationAt` | string (ISO 8601) | No | When the copy was written; the apps show "As of {date}" |
| `latitude`, `longitude` | number | No | GPS of the listing. The web client matches by fetching `GET /crops?status=available` and scoring with the shared `MatchScorer`, so they must be returned for true haversine geo-scoring; when absent the matcher falls back to a woreda-string proxy (`match-api.md`, same fields on `MatchDto`) |
| `photos` | object[] | No | 1.6.0: `{photoId, thumbUrl}` on the list, plus `fullUrl` on the single read |

`unit` (always quintals), `currency` (always ETB), `harvestDate` and the server-only stock sets are not returned.

### Crop Types (Enum)

| Value | English Name | Amharic Name | Afan Oromo Name |
|-------|--------------|--------------|-----------------|
| `teff` | Teff | ጤፍ | Xaafii |
| `wheat` | Wheat | ስንዴ | Qamadii |
| `barley` | Barley | ገብስ | Garbuu |
| `maize` | Maize | በቆሎ | Boqqolloo |
| `chickpea` | Chickpea | ሽምብራ | Shumbura |
| `lentils` | Lentils | ምስር | Misira |
| `sorghum` | Sorghum | ማሽላ | Bisinga |
| `coffee` | Coffee | ቡና | Bunaa |
| `sesame` | Sesame | ሰሊጥ | Selit |

### Status Types (Enum)

| Value | Description |
|-------|-------------|
| `available` | Crop is available for purchase |
| `reserved` | Crop is reserved (pending payment) |
| `sold` | Crop has been sold |
| `expired` | Listing has expired (>30 days old) |

---

## Validation Rules

### Server-Side Validation

1. **Crop Type**: Must be one of the 9 supported crop types (enum)
2. **Quantity**:
   - Minimum: 0.01 quintals (1kg)
   - Maximum: 10,000 quintals (1,000 metric tons)
   - Precision: 2 decimal places
3. **Price Per Kg**:
   - Minimum: 0.01 ETB
   - Maximum: 1,000 ETB
   - Precision: 2 decimal places
4. **Location**:
   - Woreda, zone, region required (manual entry, not GPS-only)
   - Coordinates optional (GPS may not work in remote areas): absent, `null` or empty is accepted
   - If a coordinate is provided (top-level `latitude` / `longitude` on the wire), it must be a number within the Ethiopia bounding box (3-15°N, 33-48°E). Enforced on **create and update** since 1.1.1 (SIM-17): `CROP_VALIDATION: latitude must be between 3 and 15 (Ethiopia).`
5. **Harvest Date**:
   - Cannot be in the future
   - Cannot be more than 1 year in the past
   - (Not yet on the wire: `harvestDate` is rejected as an unknown field by `POST /sync`.)
5b. **createdAt** (1.1.1, SIM-17):
   - Server-stamped: the repository ignores the client value on create and update.
   - A client value that is present must still be a valid ISO 8601 timestamp and must not be in the future. Clients send device local time without an offset and device clocks drift, so up to 24 hours ahead of server time is tolerated; `createdAt: 2027-…` is rejected with `CROP_VALIDATION: createdAt cannot be in the future.`
   - Enforced on create and update alike.
6. **Description**:
   - Maximum 500 characters
   - Allow Amharic, Afan Oromo, English characters
6b. **No phone number in listing text** (1.4.0, FUP-49):
   - `description`, `locationName` and (1.8.0, FUP-98) `woreda` must not hold an Ethiopian mobile number. Buyers read them before any order, and contact opens only after the money is held in escrow (1.3.0, ESC-48).
   - A create, or an update that changes one of these fields, is refused with `CROP_CONTACT_IN_TEXT: Remove the phone number from <fields>. Contact opens after payment is held in escrow.` and nothing is written. `<fields>` lists the fields that hold a number, comma-separated, in the order `description`, `locationName`, `woreda`. The check runs after the `CROP_VALIDATION` field rules, so one payload always gets the same answer. Clients treat the code as permanent (`sync-protocol.md` 1.23.0).
   - On update, a value equal to the stored one is an unchanged echo and is not checked, so a listing stored before 1.4.0 (or, for `woreda`, before 1.8.0) can still change status.
   - `woreda` equal to the farmer's profile `woreda` is not checked either (1.8.0). Clients send it from the registration profile, which is screened since `auth-api.md` 2.12.0 (FUP-52); a profile stored before that keeps its value (`user-entity.md` 1.2.0), and a refusal would leave the farmer nothing to fix in the form. The two values are compared after trimming white space at both ends, exactly otherwise (case and every character count): never by the woreda matching key, which drops everything after a comma or bracket. The create reads the profile anyway (it stamps `farmerName` and `farmerPhone` from it, 1.2.0), so it costs no extra read; an update reads the profile only for a changed `woreda` that holds a number.
   - The detector (server `server/src/Validation/ContactInText.cs`, client `mobile/lib/core/utils/contact_in_text.dart`, rule for rule the same and tested with the same table of cases):
     1. Digits: ASCII, Arabic-Indic (U+0660 to U+0669), Extended Arabic-Indic (U+06F0 to U+06F9) and fullwidth (U+FF10 to U+FF19); the Ethiopic digits one to nine (U+1369 to U+1371; Ethiopic has no zero).
     2. Invisible format characters are removed before anything else, so they neither split a number nor count toward a gap: the soft hyphen U+00AD, U+200B to U+200F (zero-width space, joiners, direction marks), U+2060 to U+2064 (word joiner, invisible operators), U+FEFF, and the variation selectors U+FE00 to U+FE0F and the combining keycap U+20E3 (so keycap emoji digits are read as digits).
     3. Digit groups are maximal runs of digits. Two neighbouring groups belong to one number when 1 to 3 separator characters lie between them: white space (with U+00A0, U+202F, the Unicode spaces U+2000 to U+200A, U+205F, U+3000, U+1680 and the Ethiopic wordspace U+1361), `.`, `-`, `(`, `)`, `+`, the Unicode hyphens and dashes U+2010 to U+2015 and the minus sign U+2212. A comma, slash, colon, underscore or any letter ends the number.
     4. Dates are not numbers: three groups joined by the same single `-`, `.` or space that read day and month (1 to 31) then a 2- or 4-digit year, or a 4-digit year (1900 to 2099) then month and day. With a 4-digit year this holds even when the same separator goes on (`01-09-2017-30-09-2017`, `09 10 2017 50 quintals`); with a 2-digit year only when it does not, so the tail of `0911-12-10-15` stays a number.
     5. A number is a 9 (Ethio Telecom) or a 7 (Safaricom) and 8 more digits: after a trunk `0`, after `251` or `00251` (with or without the trunk `0`, so `+251 (0) 9...` too), or bare. A prefixed number may start and end on any group border inside a run of linked groups (`price 45 0911234567`); a bare 9-digit number must be the whole run, so `750 000 - 800 000 birr` stays a price range.
   - Found: `0911234567`, `0911 23 45 67`, `0911-234-567`, `0911.23.45.67`, `+251 911 234 567`, `(+251) 911234567`, `00251911234567`, `0712345678`, `911 234 567`, one digit per space, the same in Amharic or Afan Oromo text.
   - Allowed: prices, quantities and weights (`500 kg`, `1,200 ETB`, `1 200 000 birr`), dates (`2026-10-23`, `09-10-2018 50 quintals`, `09 10 2017 50 quintals`, `01-09-2017-30-09-2017`), plates (`3-AA-12345`), times (`09:30`).
   - Not covered (accepted misses): landlines (`011 ...`); digits spelled out in any language; a letter, slash, comma or underscore inside the number; 4 or more separators between groups; a number shaped around a 2-digit-year date (`09-11-22 33 44`); a number grouped around a 4-digit year (`09 11 22 2019`: the year mask takes `11 22 2019` as a date); a bare 9-digit number written right after another number (`45 911234567`); digits glued to other digits; digit sets other than the five above. Audio notes (`audioNoteRef`) cannot be screened.
   - Known false positives (rare in listings): coordinates written as decimals (`GPS 9.0712 38.7478`), a 2-digit-year date followed by more numbers (`Sacks 09 10 20 30 40 kg`, `09 10 17 5000 kg`), a 9-digit lot number or price starting with 7 or 9.
7. **Farmer ID**:
   - Must match authenticated user's ID (cannot create listings for other users)
8. **Account under review** (1.5.0, FRD-04):
   - A create from a farmer at risk tier CRITICAL (`specs/architecture/fraud-detection-architecture.md` sections 16 and 17) is refused with `FRAUD_ACCOUNT_HELD: Your account is under review. Some actions are paused. Contact support.` and nothing is written. The check runs after the field rules above (rule 6b included) and before the fraud listing checks (velocity, duplicate, price), so a held account writes no velocity signal either. Permanent on clients like every `FRAUD_*` code (`sync-protocol.md` 1.9.0, 1.25.0). The tier is never returned.
   - Updates and deletes of existing listings are not gated. A score that cannot be read is LOW (the create goes on).
9. **Photos** (1.6.0):
   - `photoIds` is required on create: a JSON array of 2 to 4 distinct UUID strings. Anything else (missing, not an array, a non-UUID or repeated id, fewer than 2, more than 4) is refused with `CROP_VALIDATION: photoIds must list 2 to 4 distinct photo ids.` and nothing is written. Permanent on clients like every `CROP_VALIDATION`.
   - On update `photoIds` is optional; when present the same rule applies.
   - The server stores the ids as a list of strings. Rendition keys are derived from the listing's own `farmerId` and id, so a listing can only ever show photos that its farmer uploaded for that listing id.
   - The server does not check at create time that the renditions exist: processing is asynchronous and the create may arrive a second after the upload. Clients upload first (mobile: the sync executor holds a crop create until its photos are uploaded; web: upload, then `POST /sync`).
   - Shipped apps older than 1.6.0 send no `photoIds`, so their creates are refused. Accepted while only dev builds exist.

10. **Listings that orders depend on** (1.7.0, ESC-18; `order-lifecycle.md` 2.4.0 Stock at funding):
   - `quantity` is the free stock. Funding an order takes its quintals out of `quantity` in one conditional write that requires `quantity >= q`, and writes the remainder exactly (orders are kilograms with 2 decimals, so the stock can carry up to 4 after a funding; a restore adds back the same amount); a funding that finds less takes nothing and the order is cancelled with a full refund (`listing_out_of_stock`). So stock that funded and later orders hold is never part of `quantity`, and a farmer's quantity edit cannot drop below it: any value from 0.01 is accepted (rule 2), and an edit made before a funding fails the version check (`SYNC_CONFLICT`) instead of putting the taken stock back.
   - A `delete`, or an `update` with `status: deleted`, is refused while any order of the listing is `payment_submitted`, `funded`, `awaiting_transport` or `accepted`: `CROP_HAS_ACTIVE_ORDERS: This listing has N order(s) in progress. It cannot be deleted until they are completed or cancelled.`, nothing written. An `update` to `status: deleted` that passes is a delete: it writes `deletedAt` as the `delete` action does (1.10.0). Orders in any other status (unpaid, failed, cancelled, rejected, or past pickup) do not block. The orders are read through the orders table's `ListingIdIndex` (one query, every page). When they cannot be read the answer is the unprefixed `The listing's orders could not be checked. Please try again later.` (retryable, nothing written).
   - In every environment since 1.13.0 (SIM-56), whatever `COMMITMENTS_ENABLED` says (it was behind the flag, like `TRIP_HAS_BOOKINGS`, so off in staging and prod). A deleted listing refuses its delivery create, so with the flag off a delete stranded a funded order on its way to a truck. Clients handle the code since `sync-protocol.md` 1.17.0, and an app that predates it gets the `SYNC_VALIDATION` sentence (FUP-241, below). Ownership is checked first (`CROP_UNAUTHORIZED`).
   - Permanent on clients: the mobile queue reverts the listing from `GET /crops/{id}` and shows `errorCropHasActiveOrders`; web shows the same text (`sync-protocol.md` 1.17.0).
   - Only to an app that sends `X-App-Build` (1.12.1, FUP-241): an app that sends none (build 1 or older, which may predate the 1.17.0 client) gets the same sentence as `SYNC_VALIDATION: This listing has N order(s) in progress. ...`, permanent on every client (`sync-protocol.md` 1.52.0).

### Client-Side Validation

- Same as server-side, plus:
- Show localized error messages in user's preferred language
- Validate before enqueueing sync operation (prevent invalid operations from clogging queue)

### The farmer's edit and delete (1.13.0, SIM-56)

The apps let a farmer change and delete their own listing, from its detail screen, with the
operations of section 1 (no new endpoint). Only the owner sees the two actions, and only on an
`active` (`available`) listing: a reserved, sold or expired listing has neither. Buyers and
truckers never see them.

- **Edit** changes `quantity` (the free stock, quintals), `pricePerKg` and `description`, and
  nothing else (the crop, the photos and the place stay; the form says to delete and list
  again for those). The operation is a `crop_listing` `update` whose `data` holds `listingId`,
  only the fields the farmer changed, and `version`: the version of the copy the form showed
  (the pre-increment value, like the delete), never one read at save time. The update takes
  partial data (rules 1 to 3 are optional on update), so an untouched `quantity` is not sent.
  Together these keep an edit from putting back stock an order took after the form opened:
  the server's version check answers `conflict` (rule 10), and nothing is written. An edit
  that changes nothing sends nothing. The form allows what the server accepts (rules 1 to 3
  and 6b): above 0 and at most 9,999.99 quintals, above 0 and at most 999.99 ETB per kg (two
  decimals), 500 characters and no phone number in the description.
  - Mobile: when the phone's copy moved on since the form read it (a pull, a revert), the edit
    is refused on the phone with the same conflict and nothing is queued. Otherwise it is
    queued first, then saved in Isar (offline-first; a full queue changes nothing and says
    so). A server conflict takes the server's copy; a refusal (`CROP_CONTACT_IN_TEXT`, a
    restricted account) reverts the listing from `GET /crops/{id}` (`sync-protocol.md` 1.17.0).
  - Web: sends the update straight to the server; a conflict or a refusal fails the save with
    nothing changed.
  - On a conflict the detail reads the listing again and tells the farmer it changed
    (`editListingChanged`), so the next edit starts from what is there now.
- **Delete** asks first, then checks the farmer's orders the device knows: an order of the
  listing in `payment_submitted`, `funded`, `awaiting_transport` or `accepted` (the rule 10
  set) stops it on the device with `errorCropHasActiveOrders`. When the orders cannot be read
  (offline on web) the delete stops too (`deleteListingOrdersUnreadable`): the server would
  refuse it anyway now, but the device says why in plain words, and on the phone a server
  refusal only comes back after the listing left My Crops. Then the existing `delete`
  (mobile: the listing turns `expired` locally and the operation is queued; web: straight to
  the server). The server's rule 10 check runs in every environment since 1.13.0.

---

## Performance Considerations

### Pagination
- Default limit: 20 listings per page
- Maximum limit: 50 listings per page; a larger `limit` is served as 50 (1.9.0)
- Pagination token: the DynamoDB key to start after, Base64-encoded and HMAC-signed (a changed token is `400 CROP_VALIDATION`). Tokens do not expire
- No token on the last page, and `hasMore` agrees with the token (1.9.0, see Browse Crop Listings)

### Indexing
- **Primary Index**: `id` (partition key)
- **GSI: StatusCreatedAtIndex**:
  - Partition key: `status`
  - Sort key: `createdAt` (descending)
  - Use case: Query all available listings, sorted by newest first

### Caching
- **Mobile**: All listings cached in Isar database (offline browsing)
- **Server**: none. No CDN or cache sits in front of the API: clients call the API Gateway HTTP API directly, and the CloudFront distributions in `infra/lib/stacks/web-stack.ts` serve only the static sites from S3. Every API response carries `Cache-Control: no-store`. `GET /crops` is authenticated and filtered by role, so a shared cache in front of it would be a role-visibility risk and must not be added without a per-caller cache key

### Query Optimization
- **Avoid full table scans**: Always filter by `status` using GSI
- **Limit woreda queries**: Use DynamoDB FilterExpression (not ideal, but woredas are finite ~500 in Ethiopia)
- **Future optimization**: Add GSI on `woreda` + `createdAt` if woreda queries become common

---

## Ethiopian Context

### Unit of Measurement: Quintals
- **1 quintal = 100 kilograms**
- Standard unit in Ethiopian agriculture
- Familiar to farmers ("kuntal" in Amharic)

### Pricing: Birr per Kilogram
- Ethiopian Birr (ETB) is the national currency
- As of 2026, 1 USD ≈ 55 ETB (exchange rate varies)
- Typical crop prices:
  - Teff: 40-60 ETB/kg
  - Wheat: 15-25 ETB/kg
  - Coffee: 80-150 ETB/kg

### Regional Structure
Ethiopia has a federal structure:
- **Regions** (11 total): Amhara, Oromia, Tigray, etc.
- **Zones** (68 total): North Shewa, East Gojjam, etc.
- **Woredas** (~500 total): Debre Birhan, Bahir Dar, etc.

Users manually enter woreda/zone/region (not GPS-only) because:
- GPS may not work in remote areas
- Transport providers search by administrative boundaries, not coordinates
- Familiar to farmers (they know their woreda, not latitude/longitude)

---

## Security Considerations

### Authentication
- **POST /sync**: Requires JWT token (Cognito)
- **GET /crops**: Requires JWT token. Buyers and admins browse every listing; a farmer gets only their own; anyone else `403` (F-36)
- **GET /crops/{id}**: Same gate; `farmerPhone` only shown to the owning farmer and admins (1.3.0)

### Authorization
- Farmers can only create/update/delete their own listings
- Server validates `farmerId` matches JWT `sub` claim
- `farmerId`, `farmerName` and `farmerPhone` are immutable after create. An update that changes any of them is rejected with `CROP_VALIDATION: <field> is immutable and cannot be changed on update.` and nothing is written. Echoing the stored values (phone in any accepted spelling) is allowed; the server strips them before writing.
- On create, `farmerName` and `farmerPhone` are set by the server from the farmer's profile; values sent by the client are ignored. The name is the profile display name, else the legal name received from Fayda. If the profile cannot be read, or has neither name, the create is rejected with `CROP_VALIDATION`.

### Rate Limiting
- **API Gateway (all routes)**: the HTTP API stage throttles each route at 25 requests/second steady with a burst of 50 (`DefaultRouteSettings` in `infra/lib/stacks/api-stack.ts`). The limit is shared by all callers, not counted per user or per IP. Over it, API Gateway answers `429` with its own body (`{"message":"Too Many Requests"}`), no `error.code` and no `Retry-After`
- **POST /sync**: on top of that, 60 requests per 60 seconds per user, counted by the server in the rate-limits table: `429 SYNC_RATE_LIMITED` with `Retry-After: 60`
- **GET /crops**: no per-user or per-IP limit of its own. The `crops` Lambda has a 10 second timeout (sync: 30 seconds) and, in staging and prod, a reserved concurrency of 10 (sync: 10); dev sets no reserved concurrency (`lambda-construct.ts` skips it there). A request beyond the reserved concurrency, or one that runs out of time, fails at API Gateway with a 5xx
- **WAF**: none attached. AWS WAF cannot be put on an HTTP API directly; `infra/lib/constructs/waf-construct.ts` is not used by any stack

### Data Privacy
- Phone numbers returned only to the owning farmer and admins, never to buyers (1.3.0, ESC-48: no contact before the money is held, so a buyer and a farmer cannot agree to trade off the platform)
- GPS coordinates optional (farmers may not want exact farm location public)
- Listing text carries no phone number (1.4.0, FUP-49, rule 6b)
- Photos (1.6.0): both buckets block all public access. Reads go only through short-lived presigned URLs handed to callers who may see the listing, so a farmer cannot fetch another farmer's photos and a trucker cannot browse them. Renditions carry no EXIF or GPS data. On account deletion `photoIds` is stripped with the other listing fields, so no URL is ever signed for them again

---

## Future Enhancements

### Phase R4+
- **Voice notes**: Attach audio description (Amharic/Afan Oromo) for low-literacy farmers
- **Photo editing**: change a listing's photos after create (the wire already accepts `photoIds` on update)
- **Bidding system**: Transport providers bid on crop lots
- **Delivery tracking**: Link crop listings to trip entities
- **Quality certification**: Verify crop quality (Ethiopian Grain Trade Enterprise standards)

### Performance
- Add GSI on `woreda` + `createdAt` for efficient location-based queries
- Implement delta sync (only send changed fields, not full entity)
- Add gzip compression for payloads >1KB

---

## Changelog

| Version | Date | Change |
|---------|------|--------|
| 1.0.0 | 2026-02-15 | Initial contract: Create/browse/get endpoints, data model, validation rules, Ethiopian context |
| 1.1.0 | 2026-09-25 | Update: `farmerId`/`farmerName`/`farmerPhone` are immutable after create (SIM-02); a changed value is rejected with `CROP_VALIDATION`, an echoed value is stripped |
| 1.1.1 | 2026-09-25 | Geo/date rules on update as on create (SIM-17): `latitude`/`longitude` must be inside the Ethiopia bounding box when present (null tolerated), `createdAt` must be ISO 8601 and not in the future (24h skew); both `CROP_VALIDATION` |
| 1.2.0 | 2026-09-28 | Create: `farmerName`/`farmerPhone` come from the farmer's profile, client values ignored (SIM-40) |
| 1.3.0 | 2026-10-17 | `GET /crops` and `GET /crops/{id}` return `farmerPhone` only to the listing's farmer and admins; buyers get none (ESC-48). Clients read `farmerPhone ?? ''`, so shipped apps keep working and their browse cache loses the numbers at the next pull |
| 1.4.0 | 2026-10-23 | No phone number in listing text (FUP-49): a `description` or `locationName` that holds an Ethiopian mobile number is refused with `CROP_CONTACT_IN_TEXT` on create and on an update that changes it (an unchanged echo is accepted), nothing written; permanent on clients; the forms run the same check first; detector in rule 6b; audio notes are not screened |
| 1.5.0 | 2026-10-23 | Account under review (FRD-04, rule 8): a create from a farmer at risk tier CRITICAL is refused with `FRAUD_ACCOUNT_HELD` after the field rules and before the fraud listing checks, nothing written; permanent on clients (already handled since `sync-protocol.md` 1.18.0); updates and deletes not gated |
| 1.6.0 | 2026-10-31 | Crop photos: `POST /crops/{id}/photos` hands out presigned PUT URLs (farmer only, 1 to 4 per call, type and length signed); an S3-triggered processor keeps only a 320 px thumb and a 1024 px full WebP, metadata stripped, original deleted; `photoIds` (2 to 4) required on create (rule 9); `GET /crops` returns `photos[].thumbUrl`, `GET /crops/{id}` adds `photos[].fullUrl` |
| 1.7.0 | 2026-11-06 | Listings that orders depend on (ESC-18, rule 10): `quantity` is the free stock (exact, never rounded to 2 decimals by the server) and funding takes an order's quintals only when `quantity >= q` (a loser is cancelled with a full refund, `order-lifecycle.md` 2.4.0); server-only `stockRefusedFor` joins `stockDecrementedFor` and `stockRestoredFor`; with `COMMITMENTS_ENABLED`, a `delete` or an update to `status: deleted` is refused with `CROP_HAS_ACTIVE_ORDERS` while an order is `payment_submitted`, `funded`, `awaiting_transport` or `accepted` (orders read through `ListingIdIndex`; unreadable: retryable unprefixed error). No client change (`sync-protocol.md` 1.17.0 already handles the code) |
| 1.8.0 | 2026-11-06 | The crop `woreda` is screened for a phone number on `/sync` create and update (FUP-98, rule 6b), refused with `CROP_CONTACT_IN_TEXT` like `description` and `locationName` (listed last in `<fields>`), nothing written. Not checked: an unchanged echo of the stored value, or a value equal to the farmer's profile `woreda` after trimming (accounts registered before `auth-api.md` 2.12.0). No client change: the code is permanent on clients since `sync-protocol.md` 1.23.0, and the app sends the profile woreda, which is exempt |
| 1.9.0 | 2026-11-06 | Read model reconciled with the server (SIM-26). Documented as served, not renamed, because mobile, web and e2e read these names: the flat read model (`cropId`, `quantityKg` that holds quintals, flat `woreda`, no `unit`, `currency`, `zone`, `region` or `harvestDate`), and `GET /crops/{id}` wrapped in `crop`. Server changes: `GET /crops` from a buyer or admin defaults to `status=available` (it listed every status before; a farmer's own list still has every status); a `limit` above 50 is served as 50 (it accepted up to 100 before; web asked for 100 and now asks for 50); the server fills each page and looks one listing ahead, so `nextToken` is sent only when a listing follows, and the new `hasMore` agrees with it. Docs only: `total`, `zone`, `region`, `minQuantity` and `maxPrice` were never built and are gone from the contract; the reads need a caller (F-36), they were marked public; pagination tokens are HMAC-signed and do not expire |
| 1.10.0 | 2026-11-11 | Deleted by update, and the infrastructure claims (FUP-132). An `update` to `status: deleted` passed the delete guard (1.7.0) but wrote no `deletedAt`, and every list filters on `deletedAt`, so the listing stayed in the farmer's own `GET /crops`. The update now writes `deletedAt` like a `delete` (kept if already set). Rows already in that state are hidden too: `GET /crops` (farmer's own list, buyer browse), `GET /match` and `GET /crops/{id}` (404) treat `status: deleted` as deleted with or without the stamp, and a delivery create on such a listing is refused like on a stamped one. No client change: the shipped apps delete with the `delete` action. Docs corrected against the CDK code: no CloudFront cache in front of `GET /crops` (none exists, responses are `no-store`, and a shared cache on a role-filtered list would be a visibility risk), no 100 req/s per user or 200 req/s per IP limits; the real limits are the HTTP API stage throttle (25 req/s per route, burst 50, all callers together), 60 sync requests per minute per user (`SYNC_RATE_LIMITED`) and the Lambda reserved concurrency (staging and prod only; dev sets none) |
| 1.11.0 | 2026-11-11 | Owner reputation and restricted farmers (ESC-29, `reputation-api.md` 1.2.0 sections 7 and 8.3), all off in every environment. With `reputationPublicTags` on, `GET /crops` and `GET /crops/{id}` return `ownerTier` (`good` or `low`), `ownerEffectiveRating`, `ownerRatingCount`, `ownerReliabilityPct` and `ownerReputationAt`, copied onto the listing by the server (stamped at create, refreshed when the farmer's reputation changes, without a `version` or `updatedAt` change); a client value is dropped. With `reputationPenalties` on, a buyer's `GET /crops` leaves out the listings of a farmer whose stored tier is `restricted` (a moderation filter like the one for deleted listings; the page is still filled); the farmer's own list, `GET /crops/{id}` and an admin's browse are not filtered, and a restricted farmer's `crop_listing` create is refused with `REPUTATION_RESTRICTED` (`sync-protocol.md` 1.48.0). Role visibility is unchanged: a farmer sees only their own listings, a buyer every listing on the market |

| 1.12.0 | 2026-11-12 | `farmerReliability` retired (ESC-30, `match-api.md` 1.4.0): the server no longer computes it at create (one query on the farmer's listings less) and drops a client value; matching reads `ownerReliabilityPct`. Listings that still store it keep returning it. |
| 1.12.1 | 2026-11-12 | `CROP_HAS_ACTIVE_ORDERS` only to an app that sends `X-App-Build` (FUP-241, rule 10, `sync-protocol.md` 1.52.0): an app without the header gets the same sentence as `SYNC_VALIDATION:`, which it treats as permanent. Apps that send the header get the code as before. Server only |
| 1.13.0 | 2026-11-12 | Listing removal guarded everywhere, and the farmer's edit and delete in the apps (SIM-56). Server: the rule 10 check (`CROP_HAS_ACTIVE_ORDERS` on a `delete` or an update to `status: deleted` while an order is `payment_submitted`, `funded`, `awaiting_transport` or `accepted`) runs whatever `COMMITMENTS_ENABLED` says, so staging and prod too; an app without `X-App-Build` still gets the `SYNC_VALIDATION` sentence (FUP-241). Apps ("The farmer's edit and delete" under Validation Rules): the owner of an `active` listing edits `quantity`, `pricePerKg` and `description` with a `crop_listing` `update` of the changed fields at the version the form showed (a conflict, not an overwrite, when an order took stock meanwhile), and deletes it after a device-side check of the rule 10 order statuses that fails closed when the orders cannot be read. No wire change |

---

**Last Updated**: 2026-11-12
**Version**: 1.13.0
**Status**: Implemented (Phase R3)
