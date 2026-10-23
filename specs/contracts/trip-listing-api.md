# Trip Listing API Contract

## Version
1.5.0

## Parties
- **Mobile** (client) — truckers creating and browsing trip listings
- **Backend** (server) — storing, validating, and serving trip listings

---

## Overview

The Trip Listing API enables transport providers (truckers) to post available truck capacity (origin, destination, departure date, price) for buyers to discover and match with crop listings. This is the **second user-facing feature** in WeredaConnect's offline-first architecture, following Crop Listings (Phase R3).

**Key Features**:
- **Offline-first**: Trips created offline via POST /sync, browsed from local Isar cache
- **Woreda-to-woreda routing**: Ethiopian districts, not GPS routes
- **Capacity in quintals**: Standard Ethiopian bulk measurement (100kg)
- **Pricing in ETB per quintal**: Transport cost transparency for farmers
- **Soft deletes**: Cancelled trips stay in the table (soft delete: `status: cancelled`, plus `deletedAt` when cancelled through the `delete` action) and are hidden from both read endpoints

---

## Endpoints

### 1. Create/Update/Delete Trip
**Method**: POST /sync
**Authentication**: Required (Cognito JWT)
**Content-Type**: application/json

**Request Body**:
```json
{
  "operations": [
    {
      "operationId": "uuid-v4",
      "idempotencyKey": "trucker-789:trip:trip-101:create:1708084800000",
      "entityType": "trip",
      "entityId": "trip-101",
      "action": "create",
      "clientVersion": 1,
      "data": {
        "truckerId": "trucker-789",
        "truckerName": "Tadesse Worku",
        "truckerPhone": "+251912345678",
        "fromWoreda": "Bishoftu",
        "toWoreda": "Addis Ababa",
        "departureDate": "2026-02-18T06:00:00Z",
        "capacity": 150.0,
        "pricePerQuintal": 120.0,
        "status": "open",
        "matchedListings": []
      },
      "timestamp": "2026-02-16T10:30:00Z"
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
      "message": "Trip created successfully"
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
        "truckerId": "trucker-789",
        "truckerName": "Tadesse Worku",
        "fromWoreda": "Bishoftu",
        "toWoreda": "Addis Ababa",
        "departureDate": "2026-02-18T06:00:00Z",
        "capacity": 100.0,
        "pricePerQuintal": 130.0,
        "status": "matched",
        "matchedListings": ["crop-456"],
        "version": 3
      },
      "message": "Conflict: Server version is newer. Accept server data or re-submit with updated version."
    }
  ]
}
```

**Error Codes**:
- `400 Bad Request` — Invalid woreda names, negative capacity, missing required fields
- `401 Unauthorized` — Invalid or expired JWT token
- `409 Conflict` — Client version behind server version
- `422 Unprocessable Entity` — Validation error (e.g., departure date in past, capacity > 1000)
- `429 Too Many Requests` — Rate limit exceeded (100 req/s per user)
- `500 Internal Server Error` — Server-side failure

---

### 2. Browse Trips
**Method**: GET /trips
**Authentication**: Required — browse-all is restricted to Buyer and Admin (F-36). Truckers see only their own trips; farmers, other roles, and unauthenticated callers receive 403.

Cancelled trips are never listed, for any role: a trip with `status: cancelled` (status update) or with `deletedAt` (soft delete) is omitted, the same trips `GET /trips/{id}` answers with `404`. `status=cancelled` returns an empty page (`trips: []`, `hasMore: false`). These trips are dropped after DynamoDB applies `limit`, so a page can hold fewer than `limit` trips while `hasMore` is `true`.

**Query Parameters**:

| Parameter | Type | Required | Description | Example |
|-----------|------|----------|-------------|---------|
| `fromWoreda` | string | No | Filter by origin woreda | `fromWoreda=Bishoftu` |
| `toWoreda` | string | No | Filter by destination woreda | `toWoreda=Addis Ababa` |
| `status` | string | No | Filter by status (default: open). `cancelled` always returns an empty page. | `status=open` |
| `minCapacity` | number | No | Minimum available capacity (quintals) | `minCapacity=50` |
| `maxPrice` | number | No | Maximum price per quintal (ETB) | `maxPrice=150` |
| `departureDateFrom` | string | No | Earliest departure date (ISO 8601) | `departureDateFrom=2026-02-17T00:00:00Z` |
| `departureDateTo` | string | No | Latest departure date (ISO 8601) | `departureDateTo=2026-02-20T00:00:00Z` |
| `limit` | number | No | Results per page (default: 20, max: 50) | `limit=20` |
| `nextToken` | string | No | Pagination token from previous response | `nextToken=base64...` |

**Example Request**:
```
GET /trips?fromWoreda=Bishoftu&toWoreda=Addis%20Ababa&status=open&limit=20
```

**Response**:
```json
{
  "trips": [
    {
      "tripId": "trip-101",
      "truckerId": "trucker-789",
      "truckerName": "Tadesse Worku",
      "fromWoreda": "Bishoftu",
      "toWoreda": "Addis Ababa",
      "departureDate": "2026-02-18T06:00:00Z",
      "capacity": 150.0,
      "pricePerQuintal": 120.0,
      "status": "open",
      "matchedListings": [],
      "createdAt": "2026-02-16T10:30:00Z",
      "updatedAt": "2026-02-16T10:30:00Z",
      "version": 1
    }
  ],
  "nextToken": "base64-encoded-pagination-token",
  "hasMore": true,
  "total": 12
}
```

**Response Fields**:
- `trips` — Array of trip listings matching query
- `nextToken` — Base64-encoded token for next page (null if no more results)
- `hasMore` — Boolean indicating if more results available
- `total` — Total count of matching trips (approximate, for UI display)

**Error Codes**:
- `400 Bad Request` — Invalid query parameter (e.g., negative minCapacity, invalid date format)
- `500 Internal Server Error` — Server-side failure

---

### 3. Get Single Trip
**Method**: GET /trips/{id}
**Authentication**: Optional
**Path Parameters**: `id` (string, UUID) — Trip ID

**Example Request**:
```
GET /trips/trip-101
```

**Response**:
```json
{
  "tripId": "trip-101",
  "truckerId": "trucker-789",
  "truckerName": "Tadesse Worku",
  "truckerPhone": "+251912345678",
  "fromWoreda": "Bishoftu",
  "toWoreda": "Addis Ababa",
  "departureDate": "2026-02-18T06:00:00Z",
  "capacity": 150.0,
  "pricePerQuintal": 120.0,
  "status": "open",
  "matchedListings": [],
  "createdAt": "2026-02-16T10:30:00Z",
  "updatedAt": "2026-02-16T10:30:00Z",
  "version": 1
}
```

**Note**: `truckerPhone` is only included for the trip's own trucker and for admins (1.4.0, ESC-48). A buyer never gets it: contact opens on the order once the farmer accepted the paid order (`order-lifecycle.md` 1.18.0).

**Error Codes**:
- `404 Not Found` — Trip not found or cancelled
- `500 Internal Server Error` — Server-side failure

---

## Data Models

### TripDto Entity

| Field | Type | Required | Constraints | Description |
|-------|------|----------|-------------|-------------|
| `tripId` | string (UUID) | Yes | — | Unique trip identifier |
| `truckerId` | string (UUID) | Yes | — | Trucker who posted the trip |
| `truckerName` | string | Yes | 1-100 chars | Trucker's display name |
| `truckerPhone` | string | Yes | E.164 format | Trucker's number, server-stamped at create; returned only to the owning trucker and admins (1.4.0) |
| `fromWoreda` | string | Yes | 1-100 chars | Origin district (Ethiopian woreda) |
| `toWoreda` | string | Yes | 1-100 chars | Destination district (Ethiopian woreda) |
| `vehiclePlate` | string | No | Free text, no phone number (1.5.0) | Plate of the truck |
| `vehicleDescription` | string | No | Free text, no phone number (1.5.0) | Make, model, size of the truck |
| `departureDate` | string (ISO 8601) | Yes | — | Scheduled departure date and time (UTC) |
| `capacity` | number | Yes | 0.1-1000 | Available capacity in quintals (100kg) |
| `pricePerQuintal` | number | Yes | 0.01-10000 | Transport price per quintal in ETB |
| `status` | enum | Yes | See Status Types | Trip lifecycle status |
| `matchedListings` | string[] | Yes | — | Crop listing IDs matched to this trip |
| `createdAt` | string (ISO 8601) | Yes | — | Timestamp when trip was created |
| `updatedAt` | string (ISO 8601) | Yes | — | Timestamp when trip was last updated |
| `deletedAt` | string (ISO 8601) | No | — | Timestamp when trip was soft-deleted (cancelled) |
| `version` | number | Yes | >=1 | Optimistic concurrency control version |

### Status Types (Enum)

| Value | Description |
|-------|-------------|
| `open` | Trip posted, awaiting crop matches |
| `matched` | Trip matched with crop listings, awaiting pickup |
| `inTransit` | Truck has picked up crops and is en route |
| `delivered` | Successfully delivered to destination |
| `cancelled` | Trip cancelled by trucker |

### Status Transitions

Valid status transitions (enforced server-side):

```
open → matched      (crop listings matched to trip)
open → cancelled    (trucker cancels before any match)
matched → inTransit (trucker picks up crops and departs)
matched → cancelled (trucker cancels after match — re-opens matched listings)
inTransit → delivered (trucker confirms delivery at destination)
```

Invalid transitions return `422 Unprocessable Entity`:
- `open → inTransit` (must match first)
- `open → delivered` (must match and transit first)
- `delivered → *` (terminal state)
- `cancelled → *` (terminal state)

---

## Validation Rules

### Server-Side Validation

1. **fromWoreda / toWoreda**:
   - Required, 1-100 characters
   - Allow Amharic, Afan Oromo, English characters
   - fromWoreda and toWoreda must be different
2. **Departure Date**:
   - Must be an ISO 8601 date and time, for example `2026-02-18T06:00:00.000Z`, `2026-02-18T09:00:00+03:00` or `2026-02-18T06:00:00.000` (seconds and a fraction of up to 7 digits optional; a value without a zone is read as UTC, as mobile builds before 2026-06-18 sent it), on create and whenever an update sends it (1.3.0). A blank value or any other shape (`10/15/2026`, a bare date) is `TRIP_VALIDATION: departureDate must be a valid ISO 8601 date.` and nothing is written. It is the sort key of the browse index, so a lenient parse would let such a value sort ahead of every real date
   - Stored in one form, UTC with milliseconds (`2026-02-18T06:00:00.000Z`, what mobile and web send with `toUtc().toIso8601String()`), whatever zone the client used (1.3.0). Responses return that form, so an offset cannot move a trip within the browse order
   - Must be in the future (for create action)
   - Must be within 30 days from now
   - On update (1.3.0): a `departureDate` that differs from the stored one must satisfy the same two rules (`TRIP_VALIDATION: departureDate must be in the future when changed.` / `departureDate must be within 30 days.`). An unchanged echo of the stored date is accepted even when it is past, so status changes on departed trips still sync. Before 1.3.0 any parseable date was accepted on update. Shipped clients send only `status` + `version` on update.
3. **Capacity**:
   - Minimum: 0.1 quintals (10kg)
   - Maximum: 1,000 quintals (100 metric tons)
   - Precision: 2 decimal places
4. **Price Per Quintal**:
   - Minimum: 0.01 ETB
   - Maximum: 10,000 ETB
   - Precision: 2 decimal places
5. **Trucker ID**:
   - Must match authenticated user's ID (cannot create trips for other users)
6. **Trucker Name**:
   - Required, 1-100 characters
   - Allow Amharic, Afan Oromo, English characters
7. **Trucker Phone**:
   - Must be valid E.164 format (Ethiopian: +251XXXXXXXXX)
8. **Status Transitions**:
   - Only valid transitions allowed (see Status Transitions above)
   - Invalid transitions return `422 Unprocessable Entity`
9. **Matched Listings**:
   - Each listing ID must reference an existing crop listing
   - Can only add listings when transitioning to `matched` status
10. **No phone number in trip text** (1.5.0, FUP-49):
   - `vehicleDescription`, `vehiclePlate`, `fromWoreda` and `toWoreda` must not hold an Ethiopian mobile number. Buyers read them before any order, and contact opens only after the money is held in escrow (1.4.0, ESC-48).
   - A create, or an update that changes one of these fields, is refused with `TRIP_CONTACT_IN_TEXT: Remove the phone number from <fields>. Contact opens after payment is held in escrow.` and nothing is written. `<fields>` lists the fields that hold a number, comma-separated, in the order `vehicleDescription`, `vehiclePlate`, `fromWoreda`, `toWoreda`. The check runs after the `TRIP_VALIDATION` field rules, so one payload always gets the same answer. Clients treat the code as permanent (`sync-protocol.md` 1.23.0).
   - On update, a value equal to the stored one is an unchanged echo and is not checked, so a trip stored before 1.5.0 can still change status.
   - The detector is the one defined in `crop-listing-api.md` 1.4.0 rule 6b (mobile numbers with or without `0`, `251`, `00251`; spaces, dots, dashes, parentheses between digit groups; dates, prices and plates such as `3-AA-12345` are not numbers).

### Client-Side Validation

- Same as server-side, plus:
- Show localized error messages in user's preferred language
- Validate before enqueueing sync operation (prevent invalid operations from clogging queue)

---

## Performance Considerations

### Pagination
- Default limit: 20 trips per page
- Maximum limit: 50 trips per page
- Pagination token: Base64-encoded DynamoDB ExclusiveStartKey
- Token expiration: 24 hours (after which, query restarts from beginning)

### DynamoDB Key Design

**TripsTable**:
- **Primary Key**: `tripId` (partition key) + `createdAt` (sort key)
- **GSI: StatusDepartureDateIndex**:
  - Partition key: `status`
  - Sort key: `departureDate` (ascending)
  - Use case: Query all open trips, sorted by soonest departure

### Query Patterns

| Query | Index Used | Key Condition | Filter |
|-------|-----------|---------------|--------|
| All open trips by departure | StatusDepartureDateIndex | PK=`open`, SK between dates | — |
| Open trips from specific woreda | StatusDepartureDateIndex | PK=`open`, SK between dates | `fromWoreda = X` |
| Open trips to specific woreda | StatusDepartureDateIndex | PK=`open`, SK between dates | `toWoreda = X` |
| Open trips with capacity filter | StatusDepartureDateIndex | PK=`open`, SK between dates | `capacity >= X` |
| Open trips with price filter | StatusDepartureDateIndex | PK=`open`, SK between dates | `pricePerQuintal <= X` |
| Single trip by ID | Primary Key | PK=`tripId` | — |

**Note**: Woreda, capacity, and price filters use DynamoDB FilterExpression (post-query filtering). This is acceptable because:
- The `status=open` partition is bounded (only active trips)
- Ethiopian woredas are finite (~500 total)
- Future optimization: Add GSI on `fromWoreda` + `departureDate` if woreda queries become dominant

### Caching
- **Mobile**: All trips cached in Isar database (offline browsing)
- **Server**: CloudFront CDN caches GET /trips responses for 60 seconds (reduce DynamoDB read costs)

---

## Ethiopian Context

### Woreda-to-Woreda Routing
Ethiopian users navigate by **woredas** (administrative districts), not GPS routes. This matches the trucker's mental model:
- "I'm driving from **Bishoftu** to **Addis Ababa** on Tuesday"
- "Anyone in **Debre Birhan** need a truck to **Adama**?"

Common route patterns:
- **Rural → Urban**: Farmers ship crops to urban market hubs
- **Hub cities**: Addis Ababa, Adama, Bahir Dar, Hawassa, Dire Dawa, Mekelle
- **Regional routes**: Within Amhara, Oromia, SNNPR regions

### Capacity: Quintals
- **1 quintal = 100 kilograms**
- Standard unit in Ethiopian agriculture and transport
- "Kuntal" (ኩንታል) in Amharic
- Typical truck capacities:
  - Small truck (Isuzu): 50-100 quintals
  - Medium truck: 100-200 quintals
  - Large truck: 200-500 quintals

### Pricing: ETB per Quintal
- Ethiopian Birr (ETB) is the national currency
- Transport prices vary by:
  - Distance between woredas
  - Road conditions (paved vs. gravel)
  - Season (harvest season = higher demand)
  - Fuel prices
- Typical rates: 50-300 ETB per quintal depending on distance

### Trip Planning Horizon
- Truckers typically plan **1-3 days ahead**
- Maximum 30-day advance posting prevents stale listings
- Farmers search for trips departing in the next few days

---

## Security Considerations

### Authentication
- **POST /sync**: Requires JWT token (Cognito)
- **GET /trips**: Public (no auth required for browsing)
- **GET /trips/{id}**: Public, but `truckerPhone` only shown to the owning trucker and admins (1.4.0)

### Authorization
- Truckers can only create/update/delete their own trips
- Server validates `truckerId` matches JWT `sub` claim
- `truckerId`, `truckerName` and `truckerPhone` are immutable after create. An update that changes any of them is rejected with `TRIP_VALIDATION: <field> is immutable and cannot be changed on update.` and nothing is written. Echoing the stored values (phone in any accepted spelling) is allowed; the server strips them before writing.
- On create, `truckerName` and `truckerPhone` are set by the server from the trucker's profile; values sent by the client are ignored. The name is the profile display name, else the legal name received from Fayda. If the profile cannot be read, or has neither name, the create is rejected with `TRIP_VALIDATION`.

### Rate Limiting
- **POST /sync**: 100 requests/second per user (API Gateway throttling)
- **GET /trips**: 200 requests/second per IP (CloudFront caching reduces load)

### Data Privacy
- Phone numbers returned only to the owning trucker and admins, never to buyers (1.4.0, ESC-48: no contact before the money is held)
- Trucker identity visible only through name (not phone) in browse results
- Trip text carries no phone number (1.5.0, FUP-49, rule 10)

---

## Future Enhancements

### Phase R5+
- **Trip-crop auto-matching**: Automatically suggest crop listings that match a trip's route and capacity
- **Price negotiation**: Farmers and truckers negotiate transport price in-app
- **Return trips**: Truckers post return routes at discounted rates (fill empty trucks)
- **Route optimization**: Suggest pickup order for multiple farmers along a route
- **Real-time tracking**: GPS tracking during transit (Phase R6)
- **Rating system**: Farmers rate truckers for reliability

### Performance
- Add GSI on `fromWoreda` + `departureDate` for efficient origin-based queries
- Implement delta sync (only send changed fields, not full entity)
- Add gzip compression for payloads >1KB

---

## Changelog

| Version | Date | Change |
|---------|------|--------|
| 1.0.0 | 2026-02-16 | Initial contract: Create/browse/get endpoints, data model, validation rules, status transitions, Ethiopian context |
| 1.1.0 | 2026-09-25 | Update: `truckerId`/`truckerName`/`truckerPhone` are immutable after create (SIM-03); a changed value is rejected with `TRIP_VALIDATION`, an echoed value is stripped |
| 1.2.0 | 2026-09-28 | Create: `truckerName`/`truckerPhone` come from the trucker's profile, client values ignored (SIM-40) |
| 1.2.1 | 2026-10-01 | Browse: cancelled trips (status update or soft delete) are never listed, matching the detail 404; `status=cancelled` returns an empty page (SIM-31) |
| 1.3.0 | 2026-10-06 | Update: a changed `departureDate` must be in the future and within 30 days like on create (SIM-60); an unchanged echo of the stored date is accepted. Create and update: `departureDate` must be ISO 8601 date and time (`Z`, an offset, or no zone read as UTC); blank or any other shape is refused; it is stored as UTC with milliseconds |
| 1.4.0 | 2026-10-17 | `GET /trips` and `GET /trips/{id}` return `truckerPhone` only to the trip's trucker and admins; buyers get none (ESC-48). `/sync` conflict data for the owner still carries it (shipped clients read it as required). Clients read `truckerPhone ?? ''` |
| 1.5.0 | 2026-10-23 | No phone number in trip text (FUP-49): a `vehicleDescription`, `vehiclePlate`, `fromWoreda` or `toWoreda` that holds an Ethiopian mobile number is refused with `TRIP_CONTACT_IN_TEXT` on create and on an update that changes it (an unchanged echo is accepted), nothing written; permanent on clients; the trip form runs the same check first; detector as `crop-listing-api.md` 1.4.0; `vehiclePlate` and `vehicleDescription` added to the TripDto table |

---

**Last Updated**: 2026-10-23
**Version**: 1.5.0
**Status**: Draft (Phase R4.1)
