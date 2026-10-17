# Delivery Verification API Contract

## Version
1.14.0

## Parties
- **Mobile** (client) -- farmers, truckers, and buyers verifying delivery handoffs
- **Backend** (server) -- validating delivery state transitions, GPS proximity, and storing evidence

---

## Overview

The Delivery Verification API tracks the physical handoff of crops from farmer to trucker to buyer using QR codes, GPS coordinates, and optional photo evidence. This is the **trust mechanism** of WeredaConnect -- without verifiable delivery, escrow payments cannot be released.

**Key Features**:
- **Offline-first**: QR generation, QR scanning, GPS capture, and photo capture all work offline
- **QR code verification**: HMAC-SHA256 signed tokens for tamper-proof handoff confirmation
- **GPS proximity validation**: 5km radius check against registered locations
- **Photo evidence**: Optional WebP images (<200KB) for dispute resolution
- **State machine enforcement**: Server validates all status transitions
- **Cancel, never delete**: a cancelled delivery keeps its row with `status: cancelled` (since 1.9.0 without `deletedAt`, so every party's list still returns it); rows soft-deleted before 1.9.0 keep their `deletedAt`

---

## Endpoints

### 1. Create/Update Delivery
**Method**: POST /sync
**Authentication**: Required (Cognito JWT)
**Content-Type**: application/json

**Request Body (Create)**:
```json
{
  "operations": [
    {
      "operationId": "uuid-v4",
      "idempotencyKey": "trucker-789:delivery:del-101:create:1708084800000",
      "entityType": "delivery",
      "entityId": "del-101",
      "action": "create",
      "clientVersion": 1,
      "data": {
        "deliveryId": "del-101",
        "tripId": "trip-101",
        "orderId": "order-101",
        "listingId": "crop-456",
        "farmerId": "farmer-123",
        "truckerId": "trucker-789",
        "buyerId": "buyer-555",
        "qrToken": "eyJkZWxpdmVyeUlkIjoiZGVsLTEwMSIsInRyaXBJZCI6InRyaXAtMTAxIiwibGlzdGluZ0lkIjoiY3JvcC00NTYiLCJmYXJtZXJJZCI6ImZhcm1lci0xMjMiLCJ0aW1lc3RhbXAiOjE3MDgwODQ4MDAwMDAsImhtYWMiOiJhYmNkZWYxMjM0NTYifQ",
        "status": "pending"
      },
      "timestamp": "2026-02-18T08:00:00Z"
    }
  ]
}
```

`orderId` is **required on every create** (v1.2.0; previously only self-pickup).
The server validates the order exists and that the delivery's buyer/farmer match
the order's; it then links the order's escrow to the new delivery
(`escrow-protocol.md` §Order-Funded Escrow). An order-less create is rejected
with `DELIVERY_ORDER_REQUIRED`.

Create guards (v1.3.0, SIM-09), each rejected with nothing written:

| Guard | Rejection |
|---|---|
| Order status must be `accepted`, `pickup_confirmed` or `in_transit` (the farmer has consented; `created`, `funded` and every terminal status are refused) | `DELIVERY_VALIDATION: Order is <status>; no delivery can be created until the farmer has accepted it ...` |
| Trucker flow: the order must name a trucker and the caller must be that trucker | `DELIVERY_PARTICIPANT_MISMATCH: The order has no trucker; only the buyer can create a self-pickup delivery for it.` / `... Only the order's matched trucker can create this delivery.` |
| Self-pickup: the caller must be the order's buyer and the order must have no trucker or transport | unchanged from v1.1.0 |
| At most one active delivery per order: the server claims `activeDeliveryId` on the order and writes the delivery in one transaction (1.9.0, below); a retry of the same `deliveryId` re-claims, a `cancelled` delivery releases the slot | `DELIVERY_VALIDATION: The order already has an active delivery.` |
| Trucker flow (1.9.0, ESC-08): the delivery's `tripId` and `listingId` must be the order's (an order with a trucker but no trip is refused too) | `DELIVERY_PARTICIPANT_MISMATCH: tripId does not match the order.` / `... listingId does not match the order.` |
| Trucker flow (1.9.0): the trip must exist and belong to the caller. Its status is not checked yet (trips do not reach `matched` before ESC-17, and refusing a cancelled trip would strand the order) | `DELIVERY_VALIDATION: Referenced trip not found.` / `DELIVERY_PARTICIPANT_MISMATCH: The trip belongs to another trucker.` |

Delivery slot (1.9.0, ESC-08). The order row carries `activeDeliveryId` and, since 1.9.0,
`activeDeliveryClaimedAt` (both server-only: no client may set them and no order DTO maps them,
but a sync order result's `serverData` echoes the raw order row, so clients must ignore them). The claim (condition: the
slot is free or already this delivery's) and the delivery row (condition: no row with this
`deliveryId`) are one `TransactWriteItems`, so a create that fails or is interrupted leaves
neither, and two creates of one `deliveryId` write one row. When another delivery holds the slot
and that holder is dead (its row is `cancelled`, or it has no row and its claim has no
`activeDeliveryClaimedAt`: a claim left by a create from before 1.9.0; a timed claim is always
written with its row, so a timed claim without a row is never taken over), the create takes the slot over in the same kind of transaction, conditioned on the
holder and its `activeDeliveryClaimedAt` (or its absence) being exactly what was read; a live
holder, or one that changed meanwhile, keeps the slot and the create is refused (no retry loop).
A cancel releases the slot after its own write (`REMOVE activeDeliveryId, activeDeliveryClaimedAt`
conditioned on this delivery holding it); a release that fails is logged
(`DELIVERY_SLOT_RELEASE_FAILED`) and healed by the next create's takeover.

Update guards (v1.3.0): `orderId`, `tripId`, `listingId`, `deliveryType`, and the
buyer/farmer/trucker `Id`, `Name` and `Phone` fields are immutable after create.
An unchanged echo is dropped; a changed value is rejected with
`DELIVERY_VALIDATION: <field> is immutable and cannot be changed on update.`

Echo keys (v1.3.1, SIM-10): `deliveryId` may appear in `data` on create and update
but must equal the operation's `entityId`
(`DELIVERY_VALIDATION: deliveryId does not match the operation entityId.`).
`updatedAt`, `pickedUpAt` and `confirmedAt` in `data` are accepted and ignored: the
server stamps them at the transition (`confirmedAt` anchors the 48-hour dispute
window, so a client value is never trusted). The example payloads above succeed as
written.

Order coupling (v1.4.0, SIM-11): a successful `pickedUp`, `inTransit` or `delivered`
transition advances the linked order to `pickup_confirmed`, `in_transit` or
`delivered` as a system actor (forward only; a self-pickup handoff moves the order
from `accepted` straight to `delivered`). Clients no longer step the order
themselves; see `order-lifecycle.md` 1.4.0.

A `disputed` transition (raised by a participant within the 48-hour window) also
moves a `delivered` order to `disputed` and freezes the escrow behind the delivery
(v1.4.1, SIM-12). In the other direction, an escrow dispute raised through
`POST /escrow/dispute` marks the delivery `disputed`.

**Dispute freezes first (1.10.0, ESC-07).** After the window, party, step-up and reason checks
and a version check against a strongly consistent read (a stale version is a `conflict` and
freezes nothing), the server freezes the order's escrow **before** it writes the delivery
(`escrow-protocol.md` 1.12.0, "Additions in 1.12.0"). When it cannot, nothing is written:

| Escrow behind the delivery | Answer |
|---|---|
| `escrowed` (frozen now) or already `disputed` | the delivery is written (`disputed`, version incremented); the order follows (`delivered → disputed`, a lagging order advanced to `delivered` first) |
| manual payout queued, settlement in flight, any other status (`released`, `completed`, `refunded`, `initiated`, ...), or the order's escrow linked to another delivery | `DELIVERY_DISPUTE_REFUSED: <why> Contact support.` (permanent; audited on the escrow) |
| none, and the delivery has an `orderId` | `DELIVERY_DISPUTE_REFUSED: No payment is on record for this delivery's order, ...` (alarmed `DISPUTE_NO_ESCROW`) |
| none, legacy delivery without an `orderId` | the delivery is written as before (nothing to freeze) |
| the freeze could not be decided (an exception) | `DELIVERY_DISPUTE_RETRY: ...` (transient; nothing written) |

If the delivery write then fails its version condition, the money is frozen, so the delivery and
order are still marked `disputed` and the answer is `conflict` with the row as it is now. A
dispute for a delivery that is already `disputed` (also when only the strongly consistent read
shows it) is never written again: a participant's request finishes the freeze and the order and
answers `conflict` with the row; an outsider gets `DELIVERY_UNAUTHORIZED`. If it
fails any other way (a throttle), the request errors with only the escrow disputed; the retry
finds the escrow already disputed and finishes the delivery and order. The delivery hop is
audited in transaction-audit as `dispute` (the caller as actor), the order hops as a system actor.
Client handling of the new codes: `sync-protocol.md` 1.12.0.

**The dispute step-up fails closed (1.14.0, FUP-26).** The step-up (`AUTH_040` at or above
10,000 ETB, `auth-api.md` 2.8.0) is sized by the order's `orderTotal`, or for a legacy delivery
without an `orderId` by the escrow on its `DeliveryIndex`, and is decided before the version check
and the freeze:

| Step-up amount | Answer |
|---|---|
| the order (or the legacy delivery's escrow) cannot be read | `DELIVERY_DISPUTE_RETRY: ...` (transient; nothing frozen or written) |
| order missing, or its total missing, unreadable, zero or negative (the same for a legacy delivery's indexed escrow amount) | `AUTH_040` unless the session is fresh, then the freeze decides as above |
| legacy delivery without an `orderId` and no indexed escrow | no step-up (nothing to freeze) |

The admin's dispute verdict (`POST /admin/disputes/{id}/resolve`, `escrow-protocol.md` 1.9.0)
reaches the delivery too (v1.8.0, ESC-03): a delivery still `delivered` is marked `disputed`, and a
`disputed` delivery gets the server-only `disputeResolution` (`farmer_wins` or `buyer_wins`) and
`disputeResolvedAt`, once. Its status stays `disputed`; clients cannot set these fields. A
`buyer_wins` refund before pickup cancels a delivery that is still `pending` (`status: cancelled`,
version incremented; since 1.9.0 no `deletedAt`, audited `delivery_cancelled`), as a system actor,
before the order is cancelled. Since 1.13.0 (ESC-09) an admin refund of a plain `escrowed` order
escrow does the same for an order that was never picked up (audit metadata `source=admin_refund`), and
an admin release first advances an order left behind a `delivered` delivery; the delivery itself is
not written by a release.

The hourly refund sweep does the same system cancel (v1.12.0, ESC-10, `escrow-protocol.md` 1.15.0):
before it refunds an expired escrow paid through `POST /escrow/initiate` whose delivery is still
`pending` and whose order is not in flight (none, or not `accepted` to `disbursing`), it cancels the
delivery (`status: cancelled`, conditional on `pending`, version incremented, no `deletedAt`), audited
in transaction-audit as `delivery_cancelled` with `reason=escrow_expired`. A pickup that wins the race
leaves the delivery `pickedUp` and the money held; a pickup synced after the cancel is refused by the
1.9.0 rules (`cancelled` is final). The sweep never refunds that escrow while the delivery is
`pickedUp` or `inTransit`.

**Cancel (1.9.0, ESC-08).** A party cancels with an update `{"status": "cancelled", "version": n}`
(nothing else in `data` but the echo keys; other fields are refused with `DELIVERY_VALIDATION`).
The `delete` action, which app versions before 1.9.0 send (and which may still sit in offline
queues), is the same cancel under the same rules.

| Delivery status | buyer, farmer or trucker of the delivery | anyone else |
|---|---|---|
| `pending` | cancelled | `DELIVERY_UNAUTHORIZED: Only participants can cancel a delivery.` |
| `pickedUp`, `inTransit`, `delivered`, `disputed` | `DELIVERY_CANCEL_NOT_ALLOWED: A delivery can be cancelled only before pickup (status: <status>). After pickup, open a dispute or contact support.` | `DELIVERY_UNAUTHORIZED` |
| `cancelled` (or a row soft-deleted before 1.9.0) | `conflict` with the row in `serverData` (another party cancelled first; nothing written) | `DELIVERY_UNAUTHORIZED` |

A self-pickup has no trucker, so its parties are the buyer and the farmer. The cancel writes
`status: cancelled`, `updatedAt` and the next version, conditioned on the client's version and on
`status = pending`. When that condition fails the server re-reads the row (strongly consistent) and
judges it again: picked up meanwhile answers `DELIVERY_CANCEL_NOT_ALLOWED`, cancelled meanwhile the
`conflict` above, anything else `conflict` with the fresh row. No `deletedAt` is written:
the cancelled row stays in every party's `GET /deliveries` list and `GET /deliveries/{id}` (an
outsider still gets `403`). The cancel is audited in transaction-audit as `delivery_cancelled`
(`pending` to `cancelled`, the caller as actor, metadata `via=update` or `via=delete`) and frees
the order's delivery slot. The order stays `accepted` and the next delivery re-links the order's
escrow.

After pickup no party can cancel: the goods are moving. An admin settles a problem through the
escrow dispute and the verdict (`escrow-protocol.md`, including the override refund for a
`buyer_wins` whose goods are moving); the delivery then stays `pickedUp` or `inTransit`. A direct
admin delivery cancel is not available yet (ESC-24).

**Request Body (Confirm Pickup)**:
```json
{
  "operations": [
    {
      "operationId": "uuid-v4",
      "idempotencyKey": "trucker-789:delivery:del-101:update:1708084900000",
      "entityType": "delivery",
      "entityId": "del-101",
      "action": "update",
      "clientVersion": 1,
      "data": {
        "deliveryId": "del-101",
        "status": "pickedUp",
        "gpsPickup": {
          "latitude": 9.6778,
          "longitude": 39.5318
        },
        "pickupPhotoRef": "uploads/photos/del-101/pickup-1708084900000.webp",
        "pickedUpAt": "2026-02-18T08:15:00Z"
      },
      "timestamp": "2026-02-18T08:15:00Z"
    }
  ]
}
```

**Request Body (Confirm Delivery)**:
```json
{
  "operations": [
    {
      "operationId": "uuid-v4",
      "idempotencyKey": "buyer-555:delivery:del-101:update:1708171200000",
      "entityType": "delivery",
      "entityId": "del-101",
      "action": "update",
      "clientVersion": 2,
      "data": {
        "deliveryId": "del-101",
        "status": "delivered",
        "gpsDropoff": {
          "latitude": 9.0054,
          "longitude": 38.7636
        },
        "dropoffPhotoRef": "uploads/photos/del-101/dropoff-1708171200000.webp",
        "confirmedAt": "2026-02-19T08:00:00Z"
      },
      "timestamp": "2026-02-19T08:00:00Z"
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
      "message": "Delivery created successfully"
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
        "deliveryId": "del-101",
        "tripId": "trip-101",
        "listingId": "crop-456",
        "status": "pickedUp",
        "gpsPickup": {
          "latitude": 9.6778,
          "longitude": 39.5318
        },
        "pickedUpAt": "2026-02-18T08:15:00Z",
        "version": 3
      },
      "message": "Conflict: Server version is newer. Accept server data or re-submit with updated version."
    }
  ]
}
```

**Error Codes**:
- `400 Bad Request` -- Invalid GPS coordinates, missing required fields
- `401 Unauthorized` -- Invalid or expired JWT token
- `409 Conflict` -- Client version behind server version
- `422 Unprocessable Entity` -- Invalid status transition, GPS too far from registered location, referenced trip/listing not found
- `429 Too Many Requests` -- Rate limit exceeded (100 req/s per user)
- `500 Internal Server Error` -- Server-side failure

---

### 2. Browse Deliveries
**Method**: GET /deliveries
**Authentication**: Required (Cognito JWT -- users can only see their own deliveries)
**Query Parameters**:

| Parameter | Type | Required | Description | Example |
|-----------|------|----------|-------------|---------|
| `mine` | boolean | No | Return the caller's own deliveries across all statuses (resolves the buyer via ParticipantIndex) | `mine=true` |
| `farmerId` | string | No | Return the farmer's deliveries (via FarmerIdCreatedAtIndex). **Must equal the authenticated user** or the request is rejected `403 DELIVERY_UNAUTHORIZED` | `farmerId=farmer-123` |
| `truckerId` | string | No | Return the trucker's deliveries (via TruckerIdCreatedAtIndex). **Must equal the authenticated user** or the request is rejected `403 DELIVERY_UNAUTHORIZED` | `truckerId=trucker-789` |
| `tripId` | string | No | Filter by trip | `tripId=trip-101` |
| `listingId` | string | No | Filter by crop listing | `listingId=crop-456` |
| `status` | string | No | Filter by status (a filter on every party scope and on `tripId`; the index key only for a caller whose token carries no role claim) | `status=pending` |
| `role` | string | No | Scope an otherwise unfiltered read as `farmer`, `trucker` or `buyer`; defaults to the token's role claim | `role=trucker` |
| `limit` | number | No | Results per page (default: 20, max: 50) | `limit=20` |
| `nextToken` | string | No | Pagination token from previous response | `nextToken=base64...` |

> **Scoping (1.5.0, SIM-16)**: with no `farmerId`/`truckerId`/`mine`/`tripId`/`listingId`, the read is scoped to the caller by role (`role=` or the token's role claim): farmer → `FarmerIdCreatedAtIndex`, trucker → `TruckerIdCreatedAtIndex`, buyer → `ParticipantIndex` (same as `mine=true`), each across every status with `status` as an optional filter. It used to fall through to the `status` query (default `pending`), so a buyer's plain read missed her own in-transit delivery and farmers and truckers saw `[]`. A token without a role claim (an access token) still gets that legacy query, post-filtered to the caller's own rows. A farmer never authors a delivery (the trucker does) and is not the `ParticipantIndex` key (that is the buyer), which is why each party has its own index.

**Example Request**:
```
GET /deliveries?role=trucker&status=pending&limit=20
```

**Response**:
```json
{
  "deliveries": [
    {
      "deliveryId": "del-101",
      "tripId": "trip-101",
      "orderId": "order-101",
      "listingId": "crop-456",
      "farmerId": "farmer-123",
      "farmerName": "Alemayehu Bekele",
      "truckerId": "trucker-789",
      "truckerName": "Tadesse Worku",
      "buyerId": "buyer-555",
      "buyerName": "Fatuma Ahmed",
      "status": "pending",
      "createdAt": "2026-02-18T08:00:00Z",
      "updatedAt": "2026-02-18T08:00:00Z",
      "version": 1
    }
  ],
  "nextToken": "base64-encoded-pagination-token",
  "hasMore": false,
  "total": 1
}
```

**Response Fields**:
- `deliveries` -- Array of delivery records matching query
- `nextToken` -- Base64-encoded token for next page (null if no more results)
- `hasMore` -- Boolean indicating if more results available
- `total` -- Total count of matching deliveries (approximate, for UI display)

**Error Codes**:
- `400 Bad Request` -- Invalid query parameter
- `401 Unauthorized` -- Authentication required
- `500 Internal Server Error` -- Server-side failure

---

### 3. Get Single Delivery
**Method**: GET /deliveries/{id}
**Authentication**: Required (only participants in the delivery can view it)
**Path Parameters**: `id` (string, UUID) -- Delivery ID

**Example Request**:
```
GET /deliveries/del-101
```

**Response**:
```json
{
  "deliveryId": "del-101",
  "tripId": "trip-101",
  "orderId": "order-101",
  "listingId": "crop-456",
  "farmerId": "farmer-123",
  "farmerName": "Alemayehu Bekele",
  "farmerPhone": "+251911123456",
  "truckerId": "trucker-789",
  "truckerName": "Tadesse Worku",
  "truckerPhone": "+251912345678",
  "buyerId": "buyer-555",
  "buyerName": "Fatuma Ahmed",
  "buyerPhone": "+251913456789",
  "qrToken": "eyJkZWxpdmVyeUlkIjoiZGVsLTEwMSIs...",
  "status": "pickedUp",
  "gpsPickup": {
    "latitude": 9.6778,
    "longitude": 39.5318
  },
  "gpsDropoff": null,
  "pickupPhotoRef": "uploads/photos/del-101/pickup-1708084900000.webp",
  "dropoffPhotoRef": null,
  "pickedUpAt": "2026-02-18T08:15:00Z",
  "confirmedAt": null,
  "disputeReason": null,
  "createdAt": "2026-02-18T08:00:00Z",
  "updatedAt": "2026-02-18T08:15:00Z",
  "version": 2
}
```

**Note**: Phone numbers are included because only delivery participants can access this endpoint. Access is restricted to the farmerId, truckerId, or buyerId matching the authenticated user's JWT `sub` claim.

**Error Codes**:
- `401 Unauthorized` -- Authentication required
- `403 Forbidden` -- User is not a participant in this delivery
- `404 Not Found` -- Delivery not found, or soft-deleted before 1.9.0 (a delivery cancelled since 1.9.0 is returned with `status: cancelled`)
- `500 Internal Server Error` -- Server-side failure

---

### 4. Upload Photo Evidence
**Method**: POST /deliveries/{id}/photos
**Authentication**: Required (only delivery participants)
**Content-Type**: application/json

The server does not receive the file. It issues a pre-signed S3 `PUT` URL and the key the
photo will live at; the client uploads the WebP straight to S3 and then sends the key as
`pickupPhotoRef` / `dropoffPhotoRef` in a delivery sync operation (section 1). This was
documented as `multipart/form-data` with a `photo` file before 1.7.0; the server never
worked that way.

**Request Body**:
```json
{ "type": "pickup" }
```

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `type` | string | Yes | `pickup` or `dropoff` |

**Response (200)**:
```json
{
  "uploadUrl": "https://<uploads-bucket>.s3.<region>.amazonaws.com/uploads/photos/del-101/pickup-1708084900000.webp?X-Amz-...",
  "photoRef": "uploads/photos/del-101/pickup-1708084900000.webp",
  "issuedAt": "2026-02-18T08:15:30Z",
  "expiresInSeconds": 900
}
```

**Upload**: `PUT uploadUrl` with `Content-Type: image/webp` and the file as the body, within
`expiresInSeconds` (15 minutes). Then include `photoRef` in the next delivery update op.

**Constraints** (client-side; the server does not inspect the object):
- Format: WebP only (best compression for 3G); the pre-signed URL is bound to `image/webp`
- Max width: 800px (auto-resized client-side)
- Max file size: 200KB
- Max photos per delivery: 3 per type (pickup/dropoff)

**Client status (1.7.0)**: the shipped mobile and web apps do not call this endpoint yet
(upload is FRD-12). The camera screen keeps the captured photo's local path on the device
(`{appDir}/photos/{deliveryId}/{type}-{ms}.webp`) and the delivery repositories send a
photo ref only when it is a server key for that delivery, so attaching a photo never
blocks a pickup or delivery confirmation. Before 1.7.0 the local path was sent and the
server refused the whole confirmation (`DELIVERY_INVALID_FIELD`).

**Error Codes**:
- `400 DELIVERY_VALIDATION` -- missing delivery id, missing or invalid `type`
- `401 DELIVERY_UNAUTHORIZED` -- authentication required
- `403 DELIVERY_UNAUTHORIZED` -- user is not a participant in this delivery
- `404 DELIVERY_NOT_FOUND` -- delivery not found or cancelled (by `status` or a legacy `deletedAt`, 1.9.0)
- `500 DELIVERY_SERVER_ERROR` -- upload bucket not configured or S3 failure

---

## Data Models

### DeliveryDto Entity

| Field | Type | Required | Constraints | Description |
|-------|------|----------|-------------|-------------|
| `deliveryId` | string (UUID) | Yes | -- | Unique delivery identifier |
| `tripId` | string (UUID) | Yes | -- | Associated trip (links to Trip.tripId) |
| `orderId` | string (UUID) | No | -- | The order this delivery fulfils (required on create since 1.2.0; returned on list and detail since 1.4.2, SIM-14; absent only for pre-1.2.0 deliveries) |
| `listingId` | string (UUID) | Yes | -- | Associated crop listing (links to CropListing.listingId) |
| `farmerId` | string (UUID) | Yes | -- | Farmer who posted the crop |
| `farmerName` | string | Yes | 1-100 chars | Farmer's display name. Server-stamped on create from the farmer's profile (order row as fallback); client value ignored (v1.6.1, SIM-46) |
| `farmerPhone` | string | Server | E.164 | Farmer's phone, server-stamped on create from the users table (v1.6.1); returned on detail to participants only, never on list |
| `truckerId` | string (UUID) | Yes | -- | Trucker transporting the crop |
| `truckerName` | string | Yes | 1-100 chars | Trucker's display name. Server-stamped on create from the caller's profile; absent on self-pickup (v1.6.1) |
| `truckerPhone` | string | Server | E.164 | Trucker's phone, server-stamped on create; absent on self-pickup; detail only, participants only (v1.6.1) |
| `buyerId` | string (UUID) | Yes | -- | Buyer receiving the crop |
| `buyerName` | string | Yes | 1-100 chars | Buyer's display name. Server-stamped on create from the buyer's profile (order row as fallback); client value ignored (v1.6.1) |
| `buyerPhone` | string | Server | E.164 | Buyer's phone, server-stamped on create from the users table (v1.6.1); detail only, participants only |
| `qrToken` | string | Yes | Base64-encoded, <500 bytes | HMAC-signed QR verification token |
| `status` | enum | Yes | See Status Types | Delivery lifecycle status |
| `gpsPickup` | LatLng | No | -- | GPS coordinates at pickup (null until picked up) |
| `gpsDropoff` | LatLng | No | -- | GPS coordinates at dropoff (null until delivered) |
| `pickupPhotoRef` | string | No | `uploads/photos/{deliveryId}/pickup-{ms}.webp`, the key from section 4 | Photo evidence at pickup; any other value fails the op (1.7.0) |
| `dropoffPhotoRef` | string | No | `uploads/photos/{deliveryId}/dropoff-{ms}.webp`, the key from section 4 | Photo evidence at dropoff; any other value fails the op (1.7.0) |
| `pickedUpAt` | string (ISO 8601) | No | -- | Timestamp when trucker picked up crops |
| `confirmedAt` | string (ISO 8601) | No | -- | Timestamp when buyer confirmed receipt |
| `disputeReason` | string | No | 0-500 chars | Reason for dispute (if status is disputed) |
| `disputeResolution` | string | No | Server-only: `farmer_wins` or `buyer_wins` | The admin's dispute verdict, stamped on a `disputed` delivery (v1.8.0) |
| `disputeResolvedAt` | string (ISO 8601) | No | Server-only | When the verdict was recorded (v1.8.0) |
| `createdAt` | string (ISO 8601) | Yes | -- | Timestamp when delivery was created |
| `updatedAt` | string (ISO 8601) | Yes | -- | Timestamp when delivery was last updated |
| `deletedAt` | string (ISO 8601) | No | -- | Only on deliveries soft-deleted before 1.9.0 (such rows are hidden from lists and `GET /deliveries/{id}` answers `404`); a cancel no longer writes it |
| `version` | number | Yes | >=1 | Optimistic concurrency control version |

### LatLng Embedded Object

| Field | Type | Required | Constraints | Description |
|-------|------|----------|-------------|-------------|
| `latitude` | number | Yes | -90 to 90 | GPS latitude |
| `longitude` | number | Yes | -180 to 180 | GPS longitude |

### Status Types (Enum)

| Value | Description |
|-------|-------------|
| `pending` | Delivery created, awaiting trucker pickup |
| `pickedUp` | Trucker has picked up the crops from the farmer |
| `inTransit` | Trucker is en route to the buyer (optional intermediate state) |
| `delivered` | Buyer has confirmed receipt of the crops |
| `cancelled` | Delivery cancelled before completion |
| `disputed` | A participant (buyer, farmer or trucker) raised a dispute within 48 hours of delivery, or an escrow dispute marked it; the payment is frozen first (1.10.0) |

### Status Transitions

Valid status transitions (enforced server-side):

```
pending → pickedUp      (trucker scans farmer's QR code at pickup)
pending → cancelled       (any participant cancels before pickup, by update or delete)
pickedUp → inTransit   (trucker departs from pickup location)
pickedUp → delivered    (buyer confirms the handoff; marking in transit is optional)
inTransit → delivered   (buyer scans trucker's QR code at dropoff)
delivered → disputed     (any participant raises a dispute within 48 hours; the escrow is frozen first, 1.10.0)
```

Since 1.9.0 (ESC-08) nothing cancels a delivery after pickup through sync (before, `pickedUp →
cancelled` was allowed and the trucker's delete skipped the transitions).

Invalid transitions return `422 Unprocessable Entity`:
- `pending → inTransit` (must pick up first)
- `pending → delivered` (must pick up first; a self-pickup goes straight to `delivered`)
- `pickedUp`, `inTransit`, `delivered` or `disputed` `→ cancelled` (`DELIVERY_CANCEL_NOT_ALLOWED`, 1.9.0)
- `cancelled → *` (terminal state)
- `disputed → cancelled` (disputes follow separate resolution flow)

**Dispute Window**: Disputes must be raised within 48 hours of `confirmedAt`. After 48 hours, the `delivered` status becomes final and escrow is released automatically.

---

## QR Code Verification

### QR Token Format

The QR code contains a compact HMAC-SHA256 signed JSON payload, Base64url-encoded for QR compatibility. This approach was chosen over JWT because it is simpler, produces smaller payloads, and works entirely offline.

**Payload Structure**:
```json
{
  "did": "del-101",
  "tid": "trip-101",
  "lid": "crop-456",
  "fid": "farmer-123",
  "ts": 1708084800000,
  "hmac": "a1b2c3d4e5f6..."
}
```

| Field | Full Name | Description |
|-------|-----------|-------------|
| `did` | deliveryId | Unique delivery identifier |
| `tid` | tripId | Associated trip |
| `lid` | listingId | Associated crop listing |
| `fid` | farmerId | Farmer who owns the crop |
| `ts` | timestamp | Unix timestamp (ms) when QR was generated |
| `hmac` | HMAC signature | HMAC-SHA256 of the payload fields |

**Field names are abbreviated** to keep QR payload compact (<500 bytes total after Base64 encoding). This is critical for QR code readability on low-resolution phone cameras.

### HMAC Signing Process

1. **Secret derivation**: The HMAC secret is derived from the delivery creation:
   ```
   secret = SHA256(deliveryId + ":" + tripId + ":" + listingId + ":" + creationTimestamp)
   ```
   This secret is computed at delivery creation time and stored locally in Isar. It does not require server communication.

2. **Signing**:
   ```
   message = did + "|" + tid + "|" + lid + "|" + fid + "|" + ts
   hmac = HMAC-SHA256(secret, message)
   ```

3. **Encoding**: The full payload JSON is Base64url-encoded (no padding) to produce the QR string.

### QR Generation (Offline)

The farmer's device generates the QR code at delivery creation time:
1. Compute HMAC secret from delivery metadata
2. Generate signed payload with current timestamp
3. Base64url-encode the payload
4. Render as QR code on screen (or print if available)

The QR code can be regenerated at any time from locally stored delivery data. No network required.

### QR Scanning (Offline)

The trucker (at pickup) or buyer (at delivery) scans the QR code:
1. Decode Base64url payload
2. Parse JSON to extract fields
3. Recompute HMAC using the same secret derivation
4. Compare computed HMAC with the payload's `hmac` field
5. If match: verification succeeds, update local delivery status
6. If mismatch: reject -- possible tampering

The scanner must have the delivery record in their local Isar database (synced earlier when the delivery was created) to recompute the HMAC secret.

### QR Expiration

QR tokens do not expire in the traditional sense. The `ts` field records generation time for audit purposes. However, the server enforces that:
- Pickup must occur within 7 days of delivery creation
- Delivery must occur within 14 days of pickup

These windows prevent indefinitely stale QR codes from being used.

---

## GPS Validation

### Proximity Rules

GPS coordinates are captured at pickup and delivery to provide physical proof of location. The server validates proximity to registered locations.

**Pickup Validation**:
- `gpsPickup` must be within **5km** of the farmer's registered crop listing location (`CropListing.location`)
- Uses Haversine formula for distance calculation

**Delivery Validation**:
- `gpsDropoff` must be within **5km** of the buyer's registered location or the trip's destination woreda center
- Uses Haversine formula for distance calculation

### Why 5km Threshold

| Factor | Reality |
|--------|---------|
| GPS accuracy on cheap phones | 10-50m outdoors, up to 200m indoors/cloudy |
| Rural farm area | Farms span 1-5km from the village center |
| Market area | Urban markets (Gullit) can span 1-2km |
| Woreda size | Average woreda is 30-50km across |
| Anti-fraud goal | Prevent cross-woreda false claims |

5km is generous enough to accommodate GPS inaccuracy and real-world distances while still preventing obvious fraud (claiming delivery in a different woreda).

### Haversine Distance Calculation

```
a = sin^2(dlat/2) + cos(lat1) * cos(lat2) * sin^2(dlon/2)
c = 2 * atan2(sqrt(a), sqrt(1-a))
d = R * c  (where R = 6371 km)
```

This formula is implemented both client-side (for immediate feedback) and server-side (for authoritative validation).

### GPS Failure Handling

GPS may be unavailable (indoor, poor signal, hardware failure). The system degrades gracefully:

| Scenario | Behavior |
|----------|----------|
| GPS available | Capture coordinates, validate proximity |
| GPS unavailable at pickup | Allow pickup without GPS, flag for manual review |
| GPS unavailable at delivery | Allow delivery without GPS, flag for manual review |
| GPS outside 5km threshold | Reject with error, allow retry with explanation |

Deliveries without GPS evidence are flagged as `requires_review` in server metadata. These receive lower trust scores in future fraud detection (F-14).

---

## Photo Evidence

### Requirements

Photo evidence is **optional but encouraged**. It provides visual proof of crop condition at pickup and delivery, critical for dispute resolution.

These are client-side rules. The server enforces only the key shape (section 4 and Validation Rules, item 8), not the size, width or count.

| Constraint | Value | Rationale |
|------------|-------|-----------|
| Format | WebP | Best compression ratio for 3G upload |
| Max width | 800px | Sufficient for visual proof, small file size |
| Max file size | 200KB | Uploadable on 3G in <2 seconds |
| Max photos per type | 3 | Pickup: 3, Dropoff: 3 (6 total per delivery) |
| Compression quality | 75% | Good visual quality at small file size |

### Offline Storage

1. Camera captures JPEG on device
2. Client resizes to max 800px width
3. Client converts to WebP at 75% quality
4. Client stores in local filesystem: `{appDir}/photos/{deliveryId}/{type}-{timestamp}.webp` (`-fb` suffix for the fallback quality)
5. Isar delivery record keeps the local file path in `pickupPhotoRef` / `dropoffPhotoRef`; it is never sent to the server (1.7.0)
6. Upload queue: planned (FRD-12), not shipped. A later server pull of the delivery overwrites the Isar field with the server value, so FRD-12 needs its own queue entry rather than this field

### Upload Flow (FRD-12, planned)

When connectivity is available:
1. `POST /deliveries/{id}/photos` with `{ "type": ... }` (section 4) returns `uploadUrl` + `photoRef`
2. `PUT` the WebP to `uploadUrl` with `Content-Type: image/webp` within 15 minutes
3. Next delivery sync update op carries `pickupPhotoRef` / `dropoffPhotoRef` = `photoRef`
4. Client deletes the local file once the sync op is applied

### S3 Storage

- **Bucket**: Same uploads bucket used for other media (audio notes, etc.)
- **Key pattern**: `uploads/photos/{deliveryId}/{type}-{timestamp}.webp`
- **Lifecycle**: Photos retained for 1 year, then moved to Glacier
- **Access**: Pre-signed URLs for viewing (15-minute expiry)

---

## Validation Rules

### Server-Side Validation

1. **Delivery ID**:
   - Required, valid UUID format
   - Must be unique (checked via idempotency key)
2. **Trip ID**:
   - Trucker flow: must be the order's `tripId` and reference an existing trip of the authenticated trucker (1.9.0). The trip status is not checked until trips are booked (ESC-17)
3. **Listing ID**:
   - Must reference an existing, not deleted crop listing
   - Must be the order's `listingId` (1.9.0; `matchedListings` is not maintained)
4. **Farmer ID / Trucker ID / Buyer ID**:
   - Must reference existing users
   - Farmer must own the listing, trucker must own the trip
   - On create the server stamps `buyerName`/`buyerPhone`, `farmerName`/`farmerPhone` and (trucker flow only) `truckerName`/`truckerPhone` from the users table; values sent by the client are ignored. A party with no name on their profile keeps the order row's name. The create is rejected with `DELIVERY_VALIDATION` when a party's profile cannot be read or no name is known at all; a self-pickup stores no trucker contact (v1.6.1, SIM-46)
5. **QR Token**:
   - Must be valid Base64url encoding
   - Decoded payload must contain all required fields
   - HMAC must verify against recomputed secret
   - Token size must be <500 bytes
6. **GPS Coordinates**:
   - If provided, latitude must be between -90 and 90
   - If provided, longitude must be between -180 and 180
   - If provided, must be within Ethiopian bounds (3-15 N, 33-48 E) as a sanity check
   - Pickup GPS must be within 5km of listing location
   - Dropoff GPS must be within 5km of trip destination or buyer location
7. **Status Transitions**:
   - Only valid transitions allowed (see Status Transitions above)
   - Invalid transitions return `422 Unprocessable Entity`
   - Dispute must be raised within 48 hours of `confirmedAt`
8. **Photo References** (sync, create and update):
   - `pickupPhotoRef` / `dropoffPhotoRef` must be exactly `uploads/photos/{deliveryId}/(pickup|dropoff)-{ms}.webp` for the operation's own delivery (the key section 4 issued). Anything else, including a local file path, another delivery's key or the pre-1.7.0 `photos/...` shape, fails the whole operation with `DELIVERY_INVALID_FIELD: <field> has invalid format.` (FIX-011, path traversal)
   - The server does not check that the S3 object exists, its size, or the count per type (client-side rules)
9. **Participant Authorization**:
   - Only the order's matched trucker can create a delivery and confirm pickup; the buyer creates self-pickup deliveries (v1.3.0)
   - Only the buyer can confirm delivery
   - Any participant (farmer, trucker, buyer) can raise a dispute
   - Participant, order, listing, trip and type fields are immutable after create (v1.3.0)
10. **Order Binding** (v1.3.0):
   - The order must be `accepted`, `pickup_confirmed` or `in_transit`
   - One active delivery per order (`activeDeliveryId` claimed on the order in the same transaction as the delivery row; released on cancel; a dead holder is taken over, 1.9.0)

### Client-Side Validation

- Same as server-side, plus:
- Show localized error messages in user's preferred language (Amharic, Afan Oromo, English)
- Validate before enqueueing sync operation (prevent invalid operations from clogging queue)
- Display GPS proximity result immediately after capture (green check if within 5km, red warning if outside)
- Compress photos to WebP <200KB before uploading (FRD-12)

---

## Performance Considerations

### Pagination
- Default limit: 20 deliveries per page
- Maximum limit: 50 deliveries per page
- Pagination token: Base64-encoded DynamoDB ExclusiveStartKey
- Token expiration: 24 hours (after which, query restarts from beginning)

### DynamoDB Key Design

**DeliveriesTable**:
- **Primary Key**: `deliveryId` (partition key) + `tripId` (sort key)
- **GSI: StatusCreatedAtIndex**:
  - Partition key: `status`
  - Sort key: `createdAt` (descending)
  - Use case: Query all pending deliveries, sorted by newest first
- **GSI: TripIdIndex**:
  - Partition key: `tripId`
  - Sort key: `createdAt`
  - Use case: Query all deliveries for a specific trip
- **GSI: ParticipantIndex**:
  - Partition key: `participantId`
  - Sort key: `createdAt`
  - Use case: Query the BUYER's deliveries. `participantId` is populated with the
    delivery's `buyerId` at create (a single entry, not one per participant), so
    this index resolves the buyer only.
- **GSI: FarmerIdCreatedAtIndex**:
  - Partition key: `farmerId`
  - Sort key: `createdAt`
  - Use case: Query the FARMER's deliveries. A farmer is not the `ParticipantIndex`
    key and never authors the delivery, so this is the only way to list their own.
- **GSI: TruckerIdCreatedAtIndex** (1.5.0, sparse):
  - Partition key: `truckerId`
  - Sort key: `createdAt`
  - Use case: Query the TRUCKER's deliveries (`truckerId=` or the trucker's
    unfiltered read) and the trucker's cross-device pull. A self-pickup delivery
    carries no `truckerId` and is not indexed.

### Query Patterns

| Query | Index Used | Key Condition | Filter |
|-------|-----------|---------------|--------|
| All pending deliveries | StatusCreatedAtIndex | PK=`pending` | -- |
| Deliveries for a trip | TripIdIndex | PK=`trip-101` | -- |
| My deliveries (as buyer) | ParticipantIndex | PK=`buyer-456` (`mine=true` or the buyer's unfiltered read) | optional `status` |
| My deliveries (as farmer) | FarmerIdCreatedAtIndex | PK=`farmer-123` (`farmerId=` or the farmer's unfiltered read) | optional `status` |
| My deliveries (as trucker) | TruckerIdCreatedAtIndex | PK=`trucker-789` (`truckerId=` or the trucker's unfiltered read) | optional `status` |
| Single delivery by ID | Primary Key | PK=`del-101` | -- |
| Disputed deliveries | StatusCreatedAtIndex | PK=`disputed` | -- |

### Caching
- **Mobile**: All deliveries cached in Isar database (offline access)
- **Server**: No CDN caching for delivery endpoints (authenticated, frequently updated data)

---

## Ethiopian Context

### Trust in Rural Markets

Ethiopian rural markets operate on personal relationships and trust networks. WeredaConnect's delivery verification addresses specific trust gaps:

- **"I delivered but didn't get paid"**: GPS + QR proof ensures the trucker can demonstrate delivery occurred
- **"They didn't deliver but claimed they did"**: Buyer must scan QR code, confirming physical presence
- **"The crops arrived damaged"**: Photo evidence at pickup and delivery documents condition

### Low-Literacy Verification

QR codes were chosen specifically for low-literacy users:
- **No typing required**: Scan instead of entering delivery codes
- **Visual confirmation**: Green checkmark on successful scan, red X on failure
- **Audio feedback**: Success/failure sounds for users who cannot read the screen
- **Icon-based status**: Delivery state shown as icons (package, truck, checkmark, warning)

### Shared Device Considerations

Devices are commonly shared among family members in rural Ethiopia:
- Each delivery operation requires authentication (re-verify PIN if session expired)
- QR tokens are tied to specific delivery IDs, not devices
- Photo evidence is stored per-delivery, not per-device
- Delivery data is only accessible to authenticated participants

### GPS in Rural Ethiopia

| Reality | Design Response |
|---------|-----------------|
| Cheap phones have 10-50m outdoor GPS accuracy | 5km threshold accounts for inaccuracy |
| GPS may fail indoors or in cloudy/mountainous terrain | GPS is encouraged but not mandatory |
| Farmers may meet truckers at roadside, not at the farm | 5km radius is generous for flexibility |
| Some areas lack GPS signal entirely | Delivery proceeds without GPS, flagged for review |

### Market Day Patterns

Rural Ethiopian markets follow weekly schedules (different woredas have different market days). Delivery volume peaks on market days:
- Expect batch delivery creation on market mornings
- Expect batch delivery confirmation in the afternoon
- Sync queue may grow significantly during market days
- Server should handle burst traffic (50+ deliveries per trucker per market day)

---

## Security Considerations

### Authentication
- **POST /sync (delivery operations)**: Requires JWT token (Cognito)
- **GET /deliveries**: Requires JWT token (participants only)
- **GET /deliveries/{id}**: Requires JWT token (participants only)
- **POST /deliveries/{id}/photos**: Requires JWT token (participants only)

### Authorization
- **Create**: Only truckers can create deliveries (they initiate pickup)
- **Confirm pickup**: Only truckers can confirm pickup (they scan farmer's QR)
- **Confirm delivery**: Only buyers can confirm delivery (they scan trucker's QR)
- **Raise dispute**: Any participant (farmer, trucker, buyer) can dispute
- **Cancel**: Any participant can cancel while `pending` (update or the legacy delete); nobody can after pickup (1.9.0)
- Server validates the JWT `sub` claim matches the appropriate participant ID for each action

### QR Token Security
- **HMAC-SHA256**: Prevents token forgery (attacker would need the secret)
- **Secret derivation**: Based on delivery metadata, not a global key (compromise of one delivery does not affect others)
- **Timestamp**: Prevents replay attacks across different deliveries
- **Local verification**: No network needed to verify QR authenticity

### Rate Limiting
- **POST /sync (delivery ops)**: 100 requests/second per user (API Gateway throttling)
- **GET /deliveries**: 200 requests/second per user
- **POST /deliveries/{id}/photos**: 10 requests/second per user (prevent photo spam)

### Data Privacy
- Delivery details visible only to participants (farmer, trucker, buyer)
- Phone numbers included in single-delivery view (participants need to contact each other)
- GPS coordinates are sensitive -- only stored for verification, not displayed to other platform users
- Photo evidence accessible only via pre-signed S3 URLs with 15-minute expiry

---

## Offline Behavior

All core delivery verification operations work offline:

| Operation | Offline Capability | Sync Trigger |
|-----------|--------------------|--------------|
| Create delivery | Full -- saved to Isar, QR generated locally | POST /sync when online |
| Generate QR code | Full -- HMAC computed locally | No sync needed |
| Scan QR code | Full -- HMAC verified locally | No sync needed |
| Capture GPS | Full -- coordinates stored in Isar | POST /sync when online |
| Capture photo | Full -- WebP stored locally | POST /deliveries/{id}/photos when online |
| Update status | Full -- local Isar update | POST /sync when online |
| Browse deliveries | Full -- query local Isar cache | Refresh on next sync |

### Sync Priority

Delivery operations are high-priority sync items (they affect payment release):
1. Delivery status changes sync before crop listing updates
2. Photo uploads sync after status changes (lower priority, larger payload)
3. If sync queue has mixed entity types, delivery operations are processed first

---

## Future Enhancements

### Phase R5+
- **Real-time GPS tracking**: Track trucker location during transit (requires background location permission)
- **Multi-scan verification**: Both farmer and buyer must scan at delivery for dual confirmation
- **Condition rating**: Buyer rates crop condition at delivery (1-5 stars) for quality tracking
- **Automated dispute resolution**: ML-based comparison of pickup vs. delivery photos to detect damage
- **Delivery route optimization**: Suggest optimal pickup order for truckers with multiple deliveries
- **Insurance trigger**: Automatically initiate AICE insurance claim when dispute is raised (F-27)
- **Delivery ETA**: Estimated arrival based on road distance and historical transit times

### Performance
- Add GSI on `farmerId` + `createdAt` for efficient farmer-centric queries
- Implement delta sync for delivery updates (only send changed fields)
- Add gzip compression for delivery payloads >1KB

---

---

## Buyer Self-Pickup (no trucker)

When no trucker is involved, the **buyer** collects the crop at the farm. This is modelled
as a delivery with `deliveryType: "self_pickup"`:

- **Created by the buyer** (not the trucker). `truckerId` is empty; `tripId` is synthesized
  from the `deliveryId` (there is no trip); a linked `orderId` is **required** and that order
  MUST carry no transport cost / trucker (so escrow settles 2-way: farmer + commission).
  Buyer and farmer must differ.
- **Single-scan handoff**: the buyer scans the **farmer's** QR at the farm, which transitions
  the delivery `pending → delivered` directly (buyer-only; permitted only for `self_pickup`).
  This stamps the linked escrow so the auto-release cron pays the farmer.
- **GPS is mandatory** for the self-pickup `delivered` confirmation and is validated against
  the **listing** location (≤5km). A listing with no registered location cannot be
  self-picked-up (the money-releasing confirmation would otherwise be unverifiable).

## QR Secret Storage & Verification (hardened)

- The pickup **nonce** is stored in a dedicated `delivery-qr-secrets` table keyed by
  `deliveryId`, **never on the delivery record** — so it can never be echoed to a client via
  any sync-down / GSI projection / conflict `serverData`. It is write-only from the client's
  perspective and is never returned in any DTO or response.
- The scanner echoes the scanned nonce as `scannedQrNonce`; the server authorizes the
  `pickedUp`/`delivered` transition only if it matches the stored nonce, is within a freshness
  window, and has not already been consumed for that transition (single-use). Consumption is
  recorded only **after** the version CAS commits, so a rejected transition never burns it.
- For a money-bearing delivery whose nonce has not yet synced, the server returns
  **`deferred`** (see sync-protocol) rather than failing open.
- Only the delivery's farmer may set `qrNonce` (SIM-41). A `qrNonce` sent on create, or on
  an update by anyone else, is silently dropped.
- A farmer update carrying only `qrNonce` (plus `version`/`updatedAt`) stores the secret and
  returns `success` with the delivery's **current** `serverVersion`: nothing on the delivery
  record changes, so it is not written and the version does not move (1.6.0). Bumping it made
  the scanner's queued confirm, sent at the version it last pulled, conflict and get undone.
  Web clients send this op directly when the farmer opens the QR (1.6.0; before, web never
  registered the nonce, so a web-shown QR could not be confirmed).
- **Migration**: an on-record `qrNonce` is used only where the secret store is not
  provisioned. Where it is, only the farmer's stored nonce counts (rows written before SIM-41
  could carry a trucker-authored value). It is always scrubbed from echoed `serverData`.

## Changelog

| Version | Date | Change |
|---------|------|--------|
| 1.0.0 | 2026-02-17 | Initial contract: CRUD endpoints, QR verification, GPS validation, photo evidence, state machine, offline behavior, Ethiopian context |
| 1.1.0 | 2026-07-09 | Buyer self-pickup (`deliveryType: self_pickup`); QR nonce moved to a dedicated secret table (never echoed); server-side freshness + single-use; `deferred` sync status for unsynced secrets; listing-GPS mandatory for self-pickup confirmation |
| 1.1.1 | 2026-07-11 | Doc fix: delivery statuses are camelCase on the wire (`pickedUp`, `inTransit`) — the server state machine rejects the previously-documented snake_case (`picked_up`, `in_transit`). `deliveryType: self_pickup` is unchanged (it is a type, not a status). |
| 1.2.0 | 2026-07-12 | `orderId` required on ALL delivery creates (was self-pickup only): order must exist, be non-terminal, and match the delivery's buyer/farmer; the order's escrow is linked to the delivery at creation (`DELIVERY_ORDER_REQUIRED` error). |
| 1.3.0 | 2026-09-25 | Delivery guards (SIM-09): order must be `accepted`/`pickup_confirmed`/`in_transit`; trucker-flow creator must be the order's trucker (order without a trucker is self-pickup only); one active delivery per order via `activeDeliveryId` claimed on the order and released on cancel; participant/order/listing/trip/type fields immutable on update. |
| 1.3.1 | 2026-09-25 | Echo keys (SIM-10): the example payloads' `deliveryId` (must equal `entityId`), `pickedUpAt`, `confirmedAt` and `updatedAt` are accepted; the timestamps are server-stamped and the client values ignored. Also unblocks the mobile dispute call, which echoes `deliveryId` and `updatedAt`. |
| 1.4.0 | 2026-09-25 | Order coupling (SIM-11): `pickedUp`/`inTransit`/`delivered` advance the linked order server-side (forward only, best-effort, system-audited); self-pickup moves the order `accepted → delivered` in one hop. |
| 1.4.1 | 2026-09-25 | Dispute propagation (SIM-12): a delivery `disputed` moves the order to `disputed` and freezes the escrow; an escrow dispute marks the delivery `disputed`. |
| 1.4.2 | 2026-09-25 | `orderId` returned on list and detail (SIM-14); mobile and web keep it locally and prefer it over the trip match when resolving the order behind a delivery. |
| 1.5.0 | 2026-09-25 | Default list scoping (SIM-16): an unfiltered `GET /deliveries` is scoped to the caller by role (farmer/trucker/buyer index, `status` as a filter); new sparse `TruckerIdCreatedAtIndex` GSI with `truckerId=` (must be the caller); `role=` overrides the claim; `mine=true` now honours `status` server-side; a token without a role claim keeps the legacy `status` query. |
| 1.6.0 | 2026-09-29 | Nonce-only farmer update: secret stored, delivery record not written, version unchanged; web clients register the nonce when the QR opens. |
| 1.5.1 | 2026-09-28 | QR nonce (SIM-41): only the farmer may set `qrNonce`; a client value on create or from another party is dropped; the on-record fallback applies only without the secret store. |
| 1.6.1 | 2026-10-06 | Create: `buyerName`/`buyerPhone`, `farmerName`/`farmerPhone` and `truckerName`/`truckerPhone` come from the users table (name falling back to the order row), client values ignored; unreadable profile rejected with `DELIVERY_VALIDATION`; self-pickup stores no trucker contact (SIM-46). |
| 1.7.0 | 2026-10-06 | Photo evidence aligned with the shipped server (SIM-60): section 4 is JSON `{type}` in and a pre-signed S3 `PUT` (`uploadUrl`, `photoRef`, `issuedAt`, `expiresInSeconds`) out; keys are `uploads/photos/{deliveryId}/{type}-{ms}.webp`; a sync photo ref of any other shape fails the whole op (`DELIVERY_INVALID_FIELD`); size and per-type count are client-side rules; shipped clients keep the local path on the device and send a photo ref only when it is a server key (upload is FRD-12). |
| 1.9.0 | 2026-10-09 | Delivery rules (ESC-08): cancel by update `{status, version}` or the legacy `delete` (same rules), any party, `pending` only, `DELIVERY_CANCEL_NOT_ALLOWED` after pickup (`pickedUp → cancelled` removed), `DELIVERY_UNAUTHORIZED` for anyone else, `conflict` with the row when already cancelled, other `data` fields refused, a failed write condition judged again on a fresh read; the cancel writes no `deletedAt` (the system cancel of a verdict neither), is audited `delivery_cancelled` (`via=update` or `via=delete`) and frees the slot; photo upload refuses a cancelled delivery by status too; slot claim and delivery row in one transaction, `activeDeliveryClaimedAt`, a dead holder (cancelled, or an untimed legacy claim without a row) taken over only from the claim that was read; trucker create must carry the order's `tripId` and `listingId` and a trip of the caller; validation and authorization sections brought in line with the server. |
| 1.8.0 | 2026-10-09 | Dispute verdict (ESC-03): the admin's verdict marks a still `delivered` delivery `disputed` and stamps the server-only `disputeResolution` and `disputeResolvedAt` on the disputed delivery (status unchanged, one stamp per delivery, version incremented); a `buyer_wins` refund before pickup cancels a `pending` delivery (system soft delete); system writes audited in transaction-audit. |
| 1.10.0 | 2026-10-10 | Dispute freezes first (ESC-07): a `disputed` transition freezes the order's escrow before the delivery is written (version checked on a consistent read first); a refused freeze writes nothing and answers `DELIVERY_DISPUTE_REFUSED` (manual payout, settlement in flight, escrow not holding the money, linked to another delivery, or no escrow for the order, alarmed `DISPUTE_NO_ESCROW`); an undecided freeze answers `DELIVERY_DISPUTE_RETRY`; a conflict after the freeze still marks the delivery and order; the delivery hop is audited `dispute`; the escrow mirror adds to the delivery version; status and transition text say any participant may dispute, as the authorization sections already did. |
| 1.12.0 | 2026-10-10 | Refund sweep (ESC-10): an expired `/escrow/initiate` escrow with a `pending` delivery and no order in flight has the delivery cancelled by the system first (conditional on `pending`, audited `delivery_cancelled`, `reason=escrow_expired`), then is refunded; one whose delivery is `pickedUp` or `inTransit` is never refunded by the sweep. No client change. |
| 1.13.0 | 2026-10-10 | The order follows the money (ESC-09): an admin refund of a plain escrowed order escrow cancels the order's `pending` delivery first (system actor, no `deletedAt`, audited `delivery_cancelled` with `source=admin_refund`), as the `buyer_wins` refund does; the delivery is otherwise unchanged (the order moves to `disbursing` and `settled` after the money, `order-lifecycle.md` 1.17.0). |
| 1.14.0 | 2026-10-17 | The dispute step-up fails closed (FUP-26): it is decided before the freeze; an order (or a legacy delivery's `DeliveryIndex` escrow) that cannot be read answers `DELIVERY_DISPUTE_RETRY` and nothing is frozen or written, where the step-up was skipped before; a missing order, or a total that is missing, unreadable, zero or negative, needs a fresh password (`AUTH_040`); a legacy delivery without an `orderId` is sized by its indexed escrow (`auth-api.md` 2.8.0). |

---

**Last Updated**: 2026-10-17
**Version**: 1.14.0
**Status**: Draft (Phase R4.2)
