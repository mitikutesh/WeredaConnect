# Trip Listing API Contract

## Version
2.5.2

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
- **Soft deletes**: Cancelled trips stay in the table (soft delete: `status: cancelled`, plus `deletedAt` when cancelled through the legacy `delete` action) and are hidden from both read endpoints; since 2.0.0 a trip with goods on the move cannot be cancelled (`TRIP_HAS_BOOKINGS`), and cancelling a booked trip moves its paid orders to `awaiting_transport` (Bookings, below). Since 2.4.0 mobile and web cancel with an `update` to `cancelled`, and the server closes and finishes trips on its own (Trip lifecycle, below)

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
        "destinationLatitude": 9.0054,
        "destinationLongitude": 38.7636,
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
- `429 Too Many Requests`: `SYNC_RATE_LIMITED` when the user sent more than 60 sync requests in 60 seconds (`Retry-After: 60`), or API Gateway's own throttle (25 req/s per route, burst 50, no `error.code`). See Rate Limiting
- `500 Internal Server Error` — Server-side failure

---

### 2. Browse Trips
**Method**: GET /trips
**Authentication**: Required. Browse-all is restricted to Buyer and Admin (F-36). Truckers see only their own trips (a `truckerId` other than their own is `403`); farmers and other roles receive `403 TRIP_FORBIDDEN`. The route sits behind the API's JWT authorizer, so a call without a valid token is answered `401` by API Gateway before the Lambda runs (2.3.1; the text before said such callers got 403).

Cancelled trips are never listed, for any role: a trip with `status: cancelled` (status update) or with `deletedAt` (soft delete) is omitted, the same trips `GET /trips/{id}` answers with `404`. `status=cancelled` returns an empty page (`trips: []`, `hasMore: false`). These trips are dropped while the server fills the page (2.5.0, see Paging below), so they no longer leave a page short.

Open trips that have left are not listed (2.2.0, L-7). A trip has left once its departure day is over in Ethiopia (Africa/Addis_Ababa, UTC+3 all year): `departureDate` is the instant of Ethiopia midnight on the day the trucker picked, and trucks run late, so a trip stays listed all of its day. The browse of `status=open` (the default) therefore returns only trips with `departureDate` at or after the start of today in Ethiopia (21:00 UTC the day before). The cutoff is part of the DynamoDB key condition on the `StatusDepartureDateIndex` sort key, not a filter: departed trips are never read and never leave a page short. The string compare is exact because `departureDate` is stored as UTC with milliseconds (1.3.0). Not affected: the trucker's own list (`truckerId`, the trucker sees every trip; the apps mark a passed day as "Date passed"), `GET /trips/{id}` (orders and the live trip still read the trip), and the other statuses. A `nextToken` read before the cutoff moved (Ethiopia midnight between two pages) still works: that page starts at the token and drops the trips that have left. The apps apply the same cutoff to their cached open trips, so an offline phone does not offer a truck that left either. On a buyer's phone the trip pull keeps only what the browse returns (other truckers' trips the server no longer lists are dropped from the cache), so a read by id from that cache, such as a conversation's trip context or the live trip's vehicle line, finds nothing for an open trip that has left and those screens show without it.

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
  "total": 1,
  "orderLeadHours": 24
}
```

**Response Fields**:
- `trips` — Array of trip listings matching query
- `nextToken`: token for the next page (2.5.0). Absent, never `null`, when no trip follows; when present a trip may follow (a long run of dropped trips can use up the read budget, see Paging)
- `hasMore`: `true` exactly when `nextToken` is present (2.5.0)
- `total`: the number of trips on this page (`trips.length`), not a count of every match (2.5.0; it was always the page count, the text before called it a total). Kept for older clients; a client that needs a count follows `nextToken` to the end
- `orderLeadHours` (2.3.0, FUP-122): integer, on every page and for every caller. An order create refuses a trip whose `departureDate` is less than this many hours after the server's now (`departureDate < now + orderLeadHours`, so a trip exactly that far ahead is taken). `24` while the server runs the commitments (`COMMITMENTS_ENABLED` on the trips Lambda, the same value as on sync), `0` when it does not (no lead; the left-trip rule of 2.2.0 still applies). The list itself does not change: browse still returns every open trip that has not left. The apps leave trips inside the lead out of the buyer's match suggestions (mobile `MatchingService`, web `WebMatchingService`) with the same comparison on the device clock. The phone keeps the last value a trip pull returned for its offline matching (app-wide, kept over logout) and forgets it when a page comes without the field; a missing or unreadable value reads as unknown, which keeps the FUP-85 rule (a trip leaving today is suggested all day)

**Paging (2.5.0, FUP-126)**: some trips are dropped after DynamoDB counts the page: soft-deleted ones and the `fromWoreda`, `toWoreda` and (trucker list) `status` filters in DynamoDB, cancelled ones and `minCapacity`, `maxPrice`, `departureDateFrom` and `departureDateTo` on the server. One read can therefore come back short while matching trips remain. The server reads again until the page holds `limit` trips, then reads one more trip to learn whether anything follows. Before 2.5.0 a page could be short with a token, and a full last page carried a `nextToken` that led to an empty page, with `hasMore: true`. One request makes at most 5 reads: in a long run of dropped trips a page can hold fewer than `limit` trips (even none) while `hasMore` is `true`. Clients keep following `nextToken` while it is present and never treat a short page as the end. Same rule as `crop-listing-api.md` 1.9.0. The role rules do not change: every read of a trucker's list stays on that trucker's trips, and a buyer's browse reads every trucker's.

**Error Codes**:
- `400 Bad Request`: Invalid query parameter (e.g., negative minCapacity, invalid date format)
- `401 Unauthorized`: No valid token (API Gateway, 2.3.1)
- `403 TRIP_FORBIDDEN`: A farmer or another role, or a trucker asking for another trucker's trips (2.3.1)
- `500 Internal Server Error`: Server-side failure

---

### 3. Get Single Trip
**Method**: GET /trips/{id}
**Authentication**: Required, with the browse rules (2.3.1; the text before said "Optional"). The route sits behind the JWT authorizer. A buyer or an admin may read any trip; a trucker only their own; farmers and other roles get `403 TRIP_FORBIDDEN`.
**Path Parameters**: `id` (string, UUID): Trip ID

**Example Request**:
```
GET /trips/trip-101
```

**Response** (2.3.1: the trip is wrapped in `trip`, as the server has always answered; the example before showed it bare):
```json
{
  "trip": {
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
}
```

**Note**: `truckerPhone` is only included for the trip's own trucker and for admins (1.4.0, ESC-48). A buyer never gets it: contact opens on the order once the farmer accepted the paid order (`order-lifecycle.md` 1.18.0).

**Error Codes** (checked in this order, 2.3.1):
- `400 TRIP_VALIDATION`: No trip id in the path
- `401 Unauthorized`: No valid token (API Gateway)
- `404 TRIP_NOT_FOUND`: Trip not found or cancelled; answered before the role check, so any signed-in caller learns only that the id does not exist
- `403 TRIP_FORBIDDEN`: A farmer or another role, or a trucker reading another trucker's trip
- `500 TRIP_SERVER_ERROR`: Server-side failure

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
| `bookedQuintals` | number | No | 0 to `capacity` | Quintals reserved by funded and accepted orders (2.0.0). Server-written; a client value is dropped |
| `remainingQuintals` | number | No | 0 to `capacity` | `capacity - bookedQuintals` (2.0.0). Server-written. Null on a row from before 2.0.0, which clients read as `capacity` |
| `createdAt` | string (ISO 8601) | Yes | — | Timestamp when trip was created |
| `updatedAt` | string (ISO 8601) | Yes | — | Timestamp when trip was last updated |
| `deletedAt` | string (ISO 8601) | No | ISO 8601 UTC | Timestamp when trip was soft-deleted (cancelled through the legacy `delete`) |
| `cancelReason` | string | No | `expired` | Server-written (2.4.0): set only when the order cron closed a trip that left unbooked. Not a client field (unknown fields are refused) |
| `cancelledAt` | string (ISO 8601) | No | ISO 8601 UTC | Server-written (2.4.0), with `cancelReason` |
| `version` | number | Yes | >=1 | Optimistic concurrency control version |

The trip row also stores `bookedOrderIds` and `acceptedOrderIds` (the orders holding capacity and the ones the trucker accepted, 2.0.0); they are not returned.

### Status Types (Enum)

| Value | Description |
|-------|-------------|
| `open` | Trip posted; buyers may order on it while it has room (2.0.0: `remainingQuintals` above 0) |
| `matched` | Fully booked: `remainingQuintals` is 0 (2.0.0). Set by the system as orders reserve the capacity, never by a client; before 2.0.0 the trucker set it by hand |
| `inTransit` | Truck has picked up crops and is en route |
| `delivered` | Successfully delivered to destination: set by the trucker, or since 2.4.0 by the server once every delivery on the trip is done or cancelled |
| `cancelled` | Trip cancelled by trucker, or since 2.4.0 closed by the server because it left with no booking (`cancelReason: expired`) |

### Status Transitions

Valid status transitions (enforced server-side):

```
open → matched      (system: the last quintal is booked, 2.0.0; a client status: matched is refused)
open → inTransit    (2.0.0: allowed when the trip has bookings, so a partly booked truck can leave)
open → cancelled    (trucker cancels; 2.0.0: refused while an order on it is pickup_confirmed or in_transit)
matched → inTransit (trucker picks up crops and departs)
matched → cancelled (trucker cancels after match, re-opens matched listings; 2.0.0: same refusal, booked orders move to awaiting_transport)
inTransit → delivered (trucker confirms delivery at destination; 2.4.0: also the system, once every delivery is done or cancelled)
open → cancelled    (system, 2.4.0: the trip left with no booking and no live order, cancelReason: expired)
```

Since 2.4.0 a change to a trip that is already `cancelled`, `delivered` or deleted answers `conflict` with the stored row (`serverData`, `serverVersion`; server wins) when what it asks already holds (a cancel of a cancelled trip, `delivered` for a delivered one, any legacy `delete` of a cancelled trip) or when its `version` is behind the stored one (the server closed or finished the trip while the phone was offline). Otherwise it is still refused with `TRIP_TERMINAL_STATE` / `TRIP_ALREADY_CANCELLED`. Ownership is checked first, so another trucker never gets the row.

Invalid transitions return `422 Unprocessable Entity`:
- `open → inTransit` without bookings (2.0.0; before, always: must match first)
- a client `open → matched` (2.0.0: the system sets `matched` when the trip is full)
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
   - **destinationLatitude / destinationLongitude** (2.1.0): optional numbers, sent together, latitude 3 to 15 and longitude 32.5 to 48.5 (the `location_ping` box), else `TRIP_VALIDATION`. The point of the destination the trucker picked in Create trip; the live track's drop-off point (`live-trip-api.md`, `TripTrack.PlannedDropoff`) and so the buyer's arrival window come from it. Once the trip is booked a stored point is frozen like the route (`TRIP_HAS_BOOKINGS`), but a booked trip without one may still get it.
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
11. **Account under review** (1.6.0, FRD-04):
   - A create from a trucker at risk tier CRITICAL (`specs/architecture/fraud-detection-architecture.md` sections 16 and 17) is refused with `FRAUD_ACCOUNT_HELD: Your account is under review. Some actions are paused. Contact support.` and nothing is written. The check runs after the field rules above (rule 10 included) and before the write. Permanent on clients like every `FRAUD_*` code (`sync-protocol.md` 1.9.0, 1.25.0). The tier is never returned.
   - Updates and deletes of existing trips are not gated. A score that cannot be read is LOW (the create goes on).
12. **Frozen once booked** (2.0.0, ESC-15, behind `COMMITMENTS_ENABLED`):
   - Once `bookedQuintals` is above 0, an update that changes `departureDate`, `pricePerQuintal`, `fromWoreda` or `toWoreda` is refused with `TRIP_HAS_BOOKINGS` and nothing is written; an unchanged echo is accepted. `capacity` may change but not drop below `bookedQuintals` (same code).
   - A client `status: matched` is refused with `TRIP_VALIDATION: 'matched' is set by the server when the trip is fully booked.` and nothing is written: the system sets it when `remainingQuintals` reaches 0. `open → inTransit` is allowed when the trip has bookings.
   - Permanent on clients since `sync-protocol.md` 1.17.0 (`errorTripHasBookings`); the trip is reverted to the server row.
13. **Cancelling a booked trip** (2.0.0):
   - An update to `cancelled` or a `delete` is refused with `TRIP_HAS_BOOKINGS` while an order on the trip is `pickup_confirmed` or `in_transit` (goods on the move).
   - Otherwise the cancel goes through and cascades: every `funded` or `accepted` order on the trip moves to `awaiting_transport` (`transportFailure: trip_cancelled`, `truckerResponse: cancelled`, `faultParty: trucker`, `order-lifecycle.md` 2.0.0), its pending delivery is cancelled and its booking released; unpaid orders are left alone (the payment gate refuses them later). The order cron repeats the cascade for orders it missed when each is next due (`order-lifecycle.md` 2.8.0).

### Client-Side Validation

- Same as server-side, plus:
- Show localized error messages in user's preferred language
- Validate before enqueueing sync operation (prevent invalid operations from clogging queue)

---

## Bookings (2.0.0, ESC-15)

Design: `specs/features/three-party-escrow-commitments-reputation.md` 4.10 and `order-lifecycle.md`
2.0.0 (Commitments). Behind `COMMITMENTS_ENABLED` (off until the clients ship; off, the server behaves
as 1.6.0). Orders now hold capacity on the trip they name, and the trucker commits to each order.

**Fields.** The row carries `bookedQuintals`, `remainingQuintals` (`capacity - bookedQuintals`),
`bookedOrderIds` (orders holding capacity) and `acceptedOrderIds` (orders the trucker accepted).
`TripDto` returns the first two; a row from before 2.0.0 has neither, and clients read a missing
`remainingQuintals` as `capacity`. Units are quintals; an order's `quantity` is kilograms, so an order
holds `quantity / 100` quintals.

**Reserve, verify, release.**

| When | What |
|---|---|
| order create (`POST /sync`, `entityType: order`) | The trip must be `open` or `matched`, depart at least 24 hours ahead (`ORDER_VALIDATION`) and have `remainingQuintals` of at least the order's quintals, else `TRIP_CAPACITY_EXCEEDED` (permanent on clients since `sync-protocol.md` 1.17.0; before 2.0.0 the check was against `capacity`). Nothing is reserved yet. Whatever `COMMITMENTS_ENABLED`, a trip that has left (its Ethiopia day is over, 2.2.0) is refused first with `ORDER_TRIP_NOT_ELIGIBLE: This trip has already left; choose another trip.`, so a phone's stale offline cache cannot book it. |
| buyer `payment_submitted` | Refused with `ORDER_VALIDATION: This trip is no longer available; place a new order.` when the trip is no longer `open` or `matched`, has less room than the order, or departs in under 12 hours. Permanent; the client reverts the order. |
| funding (`→ funded`) | The order's quintals are reserved in one conditional write on the trip's `remainingQuintals` (never below 0), and the order id is added to `bookedOrderIds`; at 0 the status becomes `matched`. A trip with no room or already cancelled sends the order to `awaiting_transport` (`trip_full` / `trip_cancelled`) instead, and the farmer is not asked. |
| the trucker's accept (`truckerResponse: accept`) | The booking is verified and confirmed in the same transaction as the order write (`acceptedOrderIds`). |
| the order leaves the trip (farmer decline or expiry, trucker decline, expiry or drop-out, a cancel in `awaiting_transport`, a trip cancel; since 2.2.1 also the escrow cancels with `cancelReason: escrow_refunded`: the 7 day auto-refund sweep's cancel of a `funded` or `awaiting_transport` order, and the refund settlement's cancel before pickup for a `buyer_wins` verdict or an admin refund) | The quintals are released and the order id removed. On the escrow cancels the release is best-effort: one that fails is logged (`TRIP_RELEASE_FAILED`) and never blocks the cancel, the stock restore or the refund. |

**Frozen fields** (rule 12): once booked, `departureDate`, `pricePerQuintal`, `fromWoreda` and
`toWoreda` cannot change (`TRIP_HAS_BOOKINGS`), and `capacity` cannot drop below `bookedQuintals`.
The buyers paid for that date, price and route.

**`matched`** means fully booked and is set by the system; a client that sends it gets
`TRIP_VALIDATION: 'matched' is set by the server when the trip is fully booked.`
`open → inTransit` is allowed when the trip has bookings, so a partly booked truck can leave.

**Cancel cascade** (rule 13): refused with `TRIP_HAS_BOOKINGS` while goods are on the move
(an order on the trip is `pickup_confirmed` or `in_transit`); otherwise the `funded` and
`accepted` orders move to `awaiting_transport` (`transportFailure: trip_cancelled`), their pending
deliveries are cancelled, and unpaid orders are left to the payment gate. The order cron repeats
the cascade for orders it missed when each is next due (`order-lifecycle.md` 2.8.0).

**Browse is unchanged** by bookings: `GET /trips` lists `open` trips as before (since 2.2.0 only
those that have not left). Clients read `remainingQuintals` for the free room.

## Trip lifecycle (2.4.0, ESC-17)

Design: `specs/features/three-party-escrow-commitments-reputation.md` 4.10 and edge case G4. The two
server moves are behind `COMMITMENTS_ENABLED` like the bookings; the cancel path is not.

**One cancel path.** Mobile and web cancel a trip with the same operation: `entityType: trip`,
`action: update`, `data: {status: cancelled, version: <the version the server holds>}` (web also echoes
`truckerId`). It goes through the status transitions (only `open` or `matched` may be cancelled), the
SIM-28 stale-version conflict and the 2.0.0 cascade. Mobile refuses locally what the server would refuse
(a trip not `open` or `matched`), so such a cancel is never queued. The `delete` action that mobile
builds before 2.4.0 queued stays accepted for the operations still in the field, with its old rules: it
does not go through the status transitions, so it still cancels any trip not yet cancelled or delivered,
`inTransit` included (as before 2.4.0); the 2.0.0 `TRIP_HAS_BOOKINGS` check for crops on the road and the
cascade run as for the update; and it stamps `deletedAt`. Only the trip's trucker can cancel it
(`TRIP_UNAUTHORIZED` otherwise), by either form.

**Trips that left unbooked close themselves.** The order cron action `commitment_deadlines` (every 15
minutes since 2.5.1, ESC-16) ends with a step that reads the `open` partition of the StatusDepartureDateIndex below the browse
cutoff (`departureDate` before the start of today in Ethiopia, 2.2.0; no Scan). It reads newest
departures first, 10 pages of 100 rows and 200 closes a run at most: a trip is at the head of the read
from the first run after its Ethiopia day ends, and the trips kept open after they left (booked, or
named by a live order, below) pile up at the old end, where they never hide a newer one. A closable trip
beyond the bounds waits for the next run; a kept trip that becomes closable once it is deeper than 1,000
departed open trips is left open (it is not listed either way). A failure on one trip's write is logged
and the walk goes on. A trip is closed when it holds no
booking (no `bookedOrderIds`, `bookedQuintals` absent or 0) and no order on it (orders `TripIdIndex`) is
still live: every order is `cancelled`, `rejected`, `payment_failed` or `awaiting_transport`. Any other
order, an unpaid one or one from before bookings existed, keeps the trip open until it resolves. The
close is one write: `status: cancelled`, `cancelReason: expired`, `cancelledAt`, `updatedAt`,
`version` + 1, conditioned on the version read, `open`, not deleted, departed and still unbooked, so a
re-run, a trucker's edit or a funding that lands in between changes nothing (the funding then finds the
trip `open` and books it; a funding after the close parks the order with `trip_cancelled` as for any
cancelled trip). The run's response counts `departedTripsClosed` and `departedTripsKept`. A trip that
left with bookings stays for its trucker to start or cancel. The trip is never listed again, like any
cancelled trip; a trucker's phone that still holds it as `open` learns the new state from the conflict
answer of its next change.

**A trip becomes `delivered` when all its deliveries are done or cancelled.** After a delivery reaches
`delivered`, `disputed` or `cancelled` through `/sync` (the buyer's confirm, a dispute, a party's cancel
before pickup, by update or legacy delete), the server reads the trip (strongly consistent) and, when it
is `inTransit`, every delivery on it through the deliveries `TripIdIndex` (soft-deleted rows left out;
the delivery just written counts with its new status, since the index can lag). When every delivery is
`delivered`, `disputed` or `cancelled` and at least one is `delivered` or `disputed`, the server also reads
the trip's orders (orders `TripIdIndex`): an order `funded`, `accepted`, `pickup_confirmed` or
`in_transit` with no `delivered` or `disputed` delivery on the trip still waits for the truck (an agreed
order may have no delivery yet), and the trip is left as it is. An order row that lags its done delivery
does not hold the trip. Otherwise the trip moves to `delivered` in one write conditioned on the version
read and `inTransit` (`version` + 1). A trip whose deliveries were all cancelled, or that has none, is left
as it is; so is any trip not `inTransit`, and one with more than 500 deliveries. Best effort: the delivery write has landed whatever happens; a
failure is logged as `TRIP_COMPLETE_FAILED` and the trucker can still mark the trip delivered. A
delivery closed outside `/sync` (an admin's dispute disposition, an order cancel that cancels its
pending delivery) does not move the trip yet.

---

## Performance Considerations

### Pagination
- Default limit: 20 trips per page
- Maximum limit: 50 trips per page
- Pagination token: the DynamoDB key to start after, Base64-encoded and HMAC-signed (a changed token is `400 TRIP_VALIDATION`). Tokens do not expire (the text before 2.5.0 said 24 hours; nothing ever checked it)
- No token on the last page, and `hasMore` agrees with the token (2.5.0, see Browse Trips)

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
| All open trips by departure | StatusDepartureDateIndex | PK=`open`, SK >= start of today in Ethiopia (2.2.0) | none |
| Open trips from specific woreda | StatusDepartureDateIndex | PK=`open`, SK >= start of today in Ethiopia | `fromWoreda = X` |
| Open trips to specific woreda | StatusDepartureDateIndex | PK=`open`, SK >= start of today in Ethiopia | `toWoreda = X` |
| Open trips with capacity filter | StatusDepartureDateIndex | PK=`open`, SK >= start of today in Ethiopia | `capacity >= X` |
| Open trips with price filter | StatusDepartureDateIndex | PK=`open`, SK >= start of today in Ethiopia | `pricePerQuintal <= X` |
| Single trip by ID | Primary Key | PK=`tripId` | — |

**Note**: Woreda, capacity, and price filters use DynamoDB FilterExpression (post-query filtering). This is acceptable because:
- The `status=open` partition is bounded (only active trips)
- Ethiopian woredas are finite (~500 total)
- Future optimization: Add GSI on `fromWoreda` + `departureDate` if woreda queries become dominant

### Caching
- **Mobile**: All trips cached in Isar database (offline browsing)
- **Server**: none. No CDN or cache sits in front of the API: clients call the API Gateway HTTP API directly, and the CloudFront distributions in `infra/lib/stacks/web-stack.ts` serve only the static sites from S3. Every API response carries `Cache-Control: no-store`. `GET /trips` is authenticated and filtered by role, so a shared cache in front of it would be a role-visibility risk and must not be added without a per-caller cache key

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
- **GET /trips**: Requires JWT token (Cognito); buyers and admins browse every trip, a trucker sees only their own, anyone else gets `403` (F-36). Before 2.3.1 this line said the browse was public, which it has not been since F-36
- **GET /trips/{id}**: Requires JWT token, with the same role rules; `truckerPhone` only shown to the owning trucker and admins (1.4.0)

### Authorization
- Truckers can only create/update/delete their own trips
- Server validates `truckerId` matches JWT `sub` claim
- `truckerId`, `truckerName` and `truckerPhone` are immutable after create. An update that changes any of them is rejected with `TRIP_VALIDATION: <field> is immutable and cannot be changed on update.` and nothing is written. Echoing the stored values (phone in any accepted spelling) is allowed; the server strips them before writing.
- On create, `truckerName` and `truckerPhone` are set by the server from the trucker's profile; values sent by the client are ignored. The name is the profile display name, else the legal name received from Fayda. If the profile cannot be read, or has neither name, the create is rejected with `TRIP_VALIDATION`.

### Rate Limiting
- **API Gateway (all routes)**: the HTTP API stage throttles each route at 25 requests/second steady with a burst of 50 (`DefaultRouteSettings` in `infra/lib/stacks/api-stack.ts`). The limit is shared by all callers, not counted per user or per IP. Over it, API Gateway answers `429` with its own body (`{"message":"Too Many Requests"}`), no `error.code` and no `Retry-After`
- **POST /sync**: on top of that, 60 requests per 60 seconds per user, counted by the server in the rate-limits table: `429 SYNC_RATE_LIMITED` with `Retry-After: 60`
- **GET /trips**: no per-user or per-IP limit of its own. The `trips` Lambda has a 10 second timeout (sync: 30 seconds) and, in staging and prod, a reserved concurrency of 10 (sync: 10); dev sets no reserved concurrency (`lambda-construct.ts` skips it there). A request beyond the reserved concurrency, or one that runs out of time, fails at API Gateway with a 5xx
- **WAF**: none attached. AWS WAF cannot be put on an HTTP API directly; `infra/lib/constructs/waf-construct.ts` is not used by any stack

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
| 1.6.0 | 2026-10-23 | Account under review (FRD-04, rule 11): a create from a trucker at risk tier CRITICAL is refused with `FRAUD_ACCOUNT_HELD` after the field rules and before the write, nothing written; permanent on clients (already handled since `sync-protocol.md` 1.18.0); updates and deletes not gated |
| 2.0.0 | 2026-10-26 | Bookings (ESC-15, `order-lifecycle.md` 2.0.0, behind `COMMITMENTS_ENABLED`): `TripDto` gains `bookedQuintals` and `remainingQuintals` (null on old rows, read as `capacity`), the row also `bookedOrderIds` and `acceptedOrderIds`; capacity is reserved per order at funding (atomic on `remainingQuintals`), verified at the trucker's accept and released when the order leaves the trip; order create refuses a trip departing in under 24 hours and a quantity above `remainingQuintals` (`TRIP_CAPACITY_EXCEEDED`), and `payment_submitted` a trip no longer open or matched, too small, or departing in under 12 hours (`ORDER_VALIDATION: This trip is no longer available; place a new order.`); `matched` means fully booked and is set by the system (a client `status: matched` is refused), `open → inTransit` is allowed with bookings; once booked `departureDate`, `pricePerQuintal`, `fromWoreda` and `toWoreda` are frozen and `capacity` may not drop below `bookedQuintals` (`TRIP_HAS_BOOKINGS`); cancelling (update or delete) is refused with `TRIP_HAS_BOOKINGS` while an order on the trip is `pickup_confirmed` or `in_transit`, otherwise funded and accepted orders move to `awaiting_transport` (`trip_cancelled`) with their pending deliveries cancelled and unpaid orders left alone; browse unchanged |
| 2.1.0 | 2026-10-31 | Create and update accept `destinationLatitude` and `destinationLongitude` (both or neither, inside Ethiopia's box). No client sent a destination point before, so no trip had one and the live track had no drop-off point: the buyer's arrival window and the trucker's "about 12:35" never showed. Mobile and web send the point of the place picked in Create trip. A booked trip's stored point is frozen with the route; a missing one may be added |
| 2.2.0 | 2026-11-04 | Trips that left (L-7): seen live, `GET /trips?status=open` listed departures from 2026-07-14 to 2026-11-01 as open. The browse of `open` now returns only trips with `departureDate` at or after the start of today in Ethiopia (Africa/Addis_Ababa), so a trip stays listed all of its day; the cutoff is in the key condition, not a filter; a `nextToken` from before Ethiopia midnight still works. The trucker's own list, `GET /trips/{id}` and other statuses are unchanged. An order create naming a trip that has left is refused with `ORDER_TRIP_NOT_ELIGIBLE` whatever `COMMITMENTS_ENABLED` (`order-lifecycle.md` 2.3.0, `sync-protocol.md` 1.34.0). Mobile and web apply the same cutoff to their open trips (cache and web reads), mark a trucker's open trip with a passed day as "Date passed" with the trips still ahead listed first, and check a chosen truck (open, not left, room for the quantity) before an order is placed. The buyer's cache no longer keeps open trips that left, so by-id reads from it (conversation trip context, live trip vehicle line) come back empty for them. The cutoff uses the device clock; the apps keep no server time offset |
| 2.2.1 | 2026-11-06 | Release on the escrow cancels (FUP-123): the auto-refund sweep's cancel of a stale `funded` or `awaiting_transport` order, and the refund settlement's cancel before pickup (a `buyer_wins` verdict, an admin refund), now release the order's booking like the sync cancels; before, the trip kept a phantom booking, read too little room, and a full trip could stay `matched`. Behind `COMMITMENTS_ENABLED` like every booking write. Server only, no wire change, no client change |
| 2.3.0 | 2026-11-06 | The order lead on the browse (FUP-122): with `COMMITMENTS_ENABLED` on, an order create refuses a trip departing in under 24 hours (2.0.0), but the buyer's match suggestions still offered such trips (FUP-85 keeps a trip leaving today all day), so a buyer could pick a truck the server then refused. `GET /trips` now returns `orderLeadHours` on every page: 24 with the flag on, 0 off (`OrderCommitment.OrderLeadHours`, the same `ResponseWindow` the order create checks). The trips Lambda now gets `COMMITMENTS_ENABLED` too. Additive: older apps ignore it, and the list is unchanged. Mobile caches the last value from its trip pull (SharedPreferences, for offline matching) and web reads it with the trips it matches; both drop from the suggestions a trip with `departureDate < now + orderLeadHours`. Lead 0 or unknown (an older server) keeps FUP-85. The buyer's trip browse is not filtered |
| 2.4.0 | 2026-11-07 | Trip lifecycle (ESC-17, design 4.10 and G4): one cancel path, mobile now sends an `update` to `cancelled` like web and refuses locally a trip not `open` or `matched` (the legacy `delete` stays accepted with its old rules: no transition check, same `TRIP_HAS_BOOKINGS` check and cascade, `deletedAt`); the hourly `commitment_deadlines` run closes `open` trips whose Ethiopia day is over, that hold no booking and that no live order names (`cancelled`, new server-written `cancelReason: expired` and `cancelledAt`, version CAS, read newest departures first through the StatusDepartureDateIndex, bounded per run); an `inTransit` trip becomes `delivered` once every delivery on it is `delivered`, `disputed` or `cancelled`, one is done and no `funded`, `accepted`, `pickup_confirmed` or `in_transit` order on it lacks a done delivery (hooked on the `/sync` delivery writes, version CAS); a change to a trip already cancelled, delivered or deleted answers `conflict` with the stored row when it asks for what holds or its version is behind (before: `TRIP_TERMINAL_STATE` / `TRIP_ALREADY_CANCELLED` errors the apps retried). Both server moves behind `COMMITMENTS_ENABLED`. No new index, no wire change for clients beyond the conflict |
| 2.3.1 | 2026-11-07 | Doc only (ESC-14, survey 9.2): Security Considerations said `GET /trips` and `GET /trips/{id}` are public; both sit behind the JWT authorizer and the F-36 role rules (buyers and admins any trip, a trucker their own, everyone else `403 TRIP_FORBIDDEN`). Browse and Get Single Trip now say that a caller without a valid token gets `401` from API Gateway (not `403`), list the `401` and `403` answers, show the `{"trip": {...}}` wrapper the server has always sent for `GET /trips/{id}`, and give that endpoint's error order (`404` before the role check). The status transition note that a matched cancel "re-opens matched listings" is left for ESC-17, which owns trip cancel. No wire change |
| 2.5.0 | 2026-11-07 | Paging (FUP-126, as `crop-listing-api.md` 1.9.0 for crops): `GET /trips` set `hasMore` from DynamoDB's last key, so a full last page carried a `nextToken` that led to an empty page, and the trips dropped after DynamoDB's count (cancelled, `minCapacity`, `maxPrice`, the departure dates) left pages short. The server now fills each page through every filter (at most 5 reads) and looks one trip ahead, so a full last page no longer carries a `nextToken` and `hasMore` agrees with it. `total` is documented as what it always was, the trips on this page. A bad `minCapacity` or `maxPrice` is still `400`, now checked before any read. Docs: tokens are HMAC-signed and do not expire. No new field, role rules unchanged. Web: the farmer's My crops lists and the farmer and trucker home tiles follow every page (bounded) instead of the first one |
| 2.5.1 | 2026-11-07 | Order deadlines (ESC-16, `order-lifecycle.md` 2.8.0): the order cron's catch-up of a trip-cancel cascade runs when each missed order is next due (its reminder, deadline or pickup deadline), no longer every hour, and `commitment_deadlines` (with the departed-trip close of 2.4.0) runs every 15 minutes instead of hourly; `orderLeadHours` and the order-create lead follow the SSM parameter `/{prefix}/orders/response-window-hours` (24 by default), which the trips Lambda now reads like the order writers. Server only, no wire change |
| 2.5.2 | 2026-11-11 | Docs only, no behaviour change (FUP-132): the infrastructure claims corrected against the CDK code, as in `crop-listing-api.md` 1.10.0. No CloudFront cache in front of `GET /trips` (none exists, responses are `no-store`, and a shared cache on a role-filtered list would be a visibility risk), no 100 req/s per user or 200 req/s per IP limits; the real limits are the HTTP API stage throttle (25 req/s per route, burst 50, all callers together), 60 sync requests per minute per user (`SYNC_RATE_LIMITED`) and the Lambda reserved concurrency (staging and prod only; dev sets none) |

---

**Last Updated**: 2026-11-11
**Version**: 2.5.2
**Status**: Draft (Phase R4.1)
