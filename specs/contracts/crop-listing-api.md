# Crop Listing API Contract

## Version
1.4.0

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
- **Soft deletes**: Expired listings marked with `deletedAt`, not physically removed

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
- `429 Too Many Requests` — Rate limit exceeded (100 req/s per user)
- `500 Internal Server Error` — Server-side failure

---

### 2. Browse Crop Listings
**Method**: GET /crops
**Authentication**: Required — browse-all is restricted to Buyer and Admin (F-36). Farmers see only their own crops; truckers, other roles, and unauthenticated callers receive 403.
**Query Parameters**:

| Parameter | Type | Required | Description | Example |
|-----------|------|----------|-------------|---------|
| `cropType` | string | No | Filter by crop type (enum) | `cropType=teff` |
| `woreda` | string | No | Filter by woreda (district) | `woreda=Debre Birhan` |
| `zone` | string | No | Filter by zone | `zone=North Shewa` |
| `region` | string | No | Filter by region | `region=Amhara` |
| `status` | string | No | Filter by status (default: available) | `status=available` |
| `minQuantity` | number | No | Minimum quantity in quintals | `minQuantity=10` |
| `maxPrice` | number | No | Maximum price per kg in ETB | `maxPrice=50` |
| `limit` | number | No | Results per page (default: 20, max: 50) | `limit=20` |
| `nextToken` | string | No | Pagination token from previous response | `nextToken=base64...` |

**Example Request**:
```
GET /crops?cropType=teff&woreda=Debre%20Birhan&limit=20
```

**Response**:
```json
{
  "crops": [
    {
      "id": "crop-456",
      "farmerId": "farmer-123",
      "farmerName": "Alemayehu Bekele",
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
      "harvestDate": "2026-02-01T00:00:00Z",
      "createdAt": "2026-02-15T10:30:00Z",
      "updatedAt": "2026-02-15T10:30:00Z",
      "version": 1
    }
  ],
  "nextToken": "base64-encoded-pagination-token",
  "hasMore": true,
  "total": 45
}
```

**Response Fields**:
- `crops` — Array of crop listings matching query
- `nextToken` — Base64-encoded token for next page (null if no more results)
- `hasMore` — Boolean indicating if more results available
- `total` — Total count of matching listings (approximate, for UI display)

**Error Codes**:
- `400 Bad Request` — Invalid query parameter (e.g., negative minQuantity)
- `500 Internal Server Error` — Server-side failure

---

### 3. Get Single Crop Listing
**Method**: GET /crops/{id}
**Authentication**: Optional
**Path Parameters**: `id` (string, UUID) — Crop listing ID

**Example Request**:
```
GET /crops/crop-456
```

**Response**:
```json
{
  "id": "crop-456",
  "farmerId": "farmer-123",
  "farmerName": "Alemayehu Bekele",
  "farmerPhone": "+251911123456",
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
  "harvestDate": "2026-02-01T00:00:00Z",
  "createdAt": "2026-02-15T10:30:00Z",
  "updatedAt": "2026-02-15T10:30:00Z",
  "version": 1
}
```

**Note**: `farmerPhone` is only included for the listing's own farmer and for admins (1.3.0, ESC-48). A buyer never gets it: contact opens on the order once the farmer accepted the paid order (`order-lifecycle.md` 1.18.0).

**Error Codes**:
- `404 Not Found` — Crop listing not found or deleted
- `500 Internal Server Error` — Server-side failure

---

## Data Models

### CropListing Entity

| Field | Type | Required | Constraints | Description |
|-------|------|----------|-------------|-------------|
| `id` | string (UUID) | Yes | — | Unique crop listing identifier |
| `farmerId` | string (UUID) | Yes | — | Farmer who created the listing |
| `farmerName` | string | Yes | 1-100 chars | Farmer's display name |
| `farmerPhone` | string | Yes | E.164 format | Farmer's number, server-stamped at create; returned only to the owning farmer and admins (1.3.0) |
| `cropType` | enum | Yes | See Crop Types | Type of crop |
| `quantity` | number | Yes | 0.01-10,000 | Quantity available |
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

> **Implementation note — flat wire shape (`CropDto`).** The live `GET /crops`
> response is **flat**, not the nested `location` object above: it returns
> top-level `woreda`, `quantityKg`, `pricePerKg`, and — for on-device
> matching — top-level **`latitude`**, **`longitude`**, and `farmerReliability`
> (all `double?`, omitted when absent). The web client matches by fetching
> `GET /crops?status=available` and scoring with the shared `MatchScorer`, so
> `latitude`/`longitude` MUST be present for true haversine geo-scoring; when
> absent the matcher falls back to a woreda-string proxy. See
> `specs/contracts/match-api.md` (same coordinate fields on `MatchDto`).

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
   - `description` and `locationName` must not hold an Ethiopian mobile number. Buyers read them before any order, and contact opens only after the money is held in escrow (1.3.0, ESC-48). `woreda` is not screened here: clients send it from the farmer's registration profile, so a refusal would leave the farmer nothing to fix in the form (profile text is a registration and profile-update matter).
   - A create, or an update that changes one of these fields, is refused with `CROP_CONTACT_IN_TEXT: Remove the phone number from <fields>. Contact opens after payment is held in escrow.` and nothing is written. `<fields>` lists the fields that hold a number, comma-separated, in the order `description`, `locationName`. The check runs after the `CROP_VALIDATION` field rules, so one payload always gets the same answer. Clients treat the code as permanent (`sync-protocol.md` 1.23.0).
   - On update, a value equal to the stored one is an unchanged echo and is not checked, so a listing stored before 1.4.0 can still change status.
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

### Client-Side Validation

- Same as server-side, plus:
- Show localized error messages in user's preferred language
- Validate before enqueueing sync operation (prevent invalid operations from clogging queue)

---

## Performance Considerations

### Pagination
- Default limit: 20 listings per page
- Maximum limit: 50 listings per page
- Pagination token: Base64-encoded DynamoDB ExclusiveStartKey
- Token expiration: 24 hours (after which, query restarts from beginning)

### Indexing
- **Primary Index**: `id` (partition key)
- **GSI: StatusCreatedAtIndex**:
  - Partition key: `status`
  - Sort key: `createdAt` (descending)
  - Use case: Query all available listings, sorted by newest first

### Caching
- **Mobile**: All listings cached in Isar database (offline browsing)
- **Server**: CloudFront CDN caches GET /crops responses for 60 seconds (reduce DynamoDB read costs)

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
- **GET /crops**: Public (no auth required for browsing)
- **GET /crops/{id}**: Public, but `farmerPhone` only shown to the owning farmer and admins (1.3.0)

### Authorization
- Farmers can only create/update/delete their own listings
- Server validates `farmerId` matches JWT `sub` claim
- `farmerId`, `farmerName` and `farmerPhone` are immutable after create. An update that changes any of them is rejected with `CROP_VALIDATION: <field> is immutable and cannot be changed on update.` and nothing is written. Echoing the stored values (phone in any accepted spelling) is allowed; the server strips them before writing.
- On create, `farmerName` and `farmerPhone` are set by the server from the farmer's profile; values sent by the client are ignored. The name is the profile display name, else the legal name received from Fayda. If the profile cannot be read, or has neither name, the create is rejected with `CROP_VALIDATION`.

### Rate Limiting
- **POST /sync**: 100 requests/second per user (API Gateway throttling)
- **GET /crops**: 200 requests/second per IP (CloudFront caching reduces load)

### Data Privacy
- Phone numbers returned only to the owning farmer and admins, never to buyers (1.3.0, ESC-48: no contact before the money is held, so a buyer and a farmer cannot agree to trade off the platform)
- GPS coordinates optional (farmers may not want exact farm location public)
- Listing text carries no phone number (1.4.0, FUP-49, rule 6b)

---

## Future Enhancements

### Phase R4+
- **Voice notes**: Attach audio description (Amharic/Afan Oromo) for low-literacy farmers
- **Image upload**: Photos of crop quality (S3 storage, optimized for 3G)
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

---

**Last Updated**: 2026-10-23
**Version**: 1.4.0
**Status**: Implemented (Phase R3)
