# Delivery Verification API Contract

## Version
1.25.0

## Parties
- **Mobile** (client) -- farmers, truckers, and buyers verifying delivery handoffs
- **Web** (client) -- the same handoffs in the browser; uploads handoff photos as they are taken (1.25.0)
- **Backend** (server) -- validating delivery state transitions, GPS proximity, and storing evidence
- **Infrastructure** -- the uploads bucket and its CORS, the Lambdas' photo grants (1.25.0)
- **Admin** -- the handoff photos on the transaction detail, dispute evidence (1.25.0)

---

## Overview

The Delivery Verification API tracks the physical handoff of crops from farmer to trucker to buyer using QR codes, GPS coordinates, and optional photo evidence. This is the **trust mechanism** of WeredaConnect -- without verifiable delivery, escrow payments cannot be released.

**Key Features**:
- **Offline-first**: QR generation, QR scanning, GPS capture, and photo capture all work offline
- **QR code verification**: HMAC-SHA256 signed tokens for tamper-proof handoff confirmation
- **GPS proximity validation**: 5km radius check against registered locations
- **Photo evidence**: Optional handoff photos (WebP from the phone, JPEG from the browser, at most 200 KB) for dispute resolution, seen only by the delivery's buyer, farmer and trucker and by admins (1.25.0)
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
window, so a client value is never trusted). Since 1.23.0 the `pickedUp` transition
also stamps the server-only `pickupCapturedAt` from the operation's `timestamp`
(DeliveryDto Entity); clients send `timestamp` on every operation already. The example payloads above succeed as
written.

Photo refs (1.25.0, FRD-12): a handoff photo is attached by its own `delivery_photo` operation
(section 4b), never by the handoff. A `pickupPhotoRef` or `dropoffPhotoRef` in a delivery create
or update is still checked (any value that is not this delivery's photo key fails the operation
with `DELIVERY_INVALID_FIELD`, Validation Rules item 8) but a valid key is dropped, not written:
it would skip the attach's checks of the taker and the object.

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
| the order's escrow has a refund scheduled (`refundReason`: an order cancel or reject, or an admin refund; FUP-32) | `DELIVERY_DISPUTE_REFUSED: The payment for this delivery is being refunded to the buyer, so it can no longer be disputed. Contact support.` (permanent) |
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

An admin's dispute disposition (v1.16.0, FUP-16, `escrow-protocol.md` 1.21.0) closes a delivery that
never reached `delivered`, as a system actor, before its order moves to `dispute_resolved`. The write is
conditional on the status the verdict read and on no verdict yet on the delivery, adds to the version,
writes no `deletedAt`, and stamps the server-only `disputeResolution` and `disputeResolvedAt`:

| Disposition | From | To | Also written | Transaction audit |
|-------------|------|----|--------------|-------------------|
| `return_to_farmer` (`buyer_wins`) | `pending`, `pickedUp`, `inTransit` | `cancelled` | `cancelReason: dispute_buyer_wins`, `cancelledAt` | `delivery_cancelled` |
| `settle_now` (`farmer_wins`) | `pending`, `pickedUp`, `inTransit` | `cancelled` | `cancelReason: dispute_farmer_wins`, `cancelledAt` | `delivery_cancelled` |
| `delivered_anyway` (`buyer_wins`) | `pending`, `pickedUp`, `inTransit` | `disputed` | `confirmedAt` when missing (the goods count as delivered) | `dispute_resolved` |

Metadata `resolution=<verdict>;disposition=<disposition>` (plus `;cancelReason=...` on a cancel). A
handoff that lands first makes the condition fail and nothing is written; the admin is told to send the
verdict again. The parties see the row in their usual lists with its known status; `cancelReason`,
`cancelledAt` and the verdict fields are extra keys the apps ignore. A party's own cancel after pickup
stays refused (`DELIVERY_CANCEL_NOT_ALLOWED`); only the admin's disposition closes a delivery on the way.

A dispute through sync starts only from `delivered` (the transitions below), so a dispute before pickup
is impossible on this path; `POST /escrow/dispute` refuses one with `ESCROW_041` (v1.16.0, the rule
lives in the escrow contract).

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
      "cropType": "teff",
      "quantity": 1500,
      "pickupPlace": "Bichena",
      "pickupPlannedAt": "2026-02-20T06:00:00Z",
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
  "truckerId": "trucker-789",
  "truckerName": "Tadesse Worku",
  "buyerId": "buyer-555",
  "buyerName": "Fatuma Ahmed",
  "qrToken": "eyJkZWxpdmVyeUlkIjoiZGVsLTEwMSIs...",
  "status": "pickedUp",
  "cropType": "teff",
  "quantity": 1500,
  "pickupPlace": "Bichena",
  "pickupPlannedAt": "2026-02-18T06:00:00Z",
  "gpsPickup": {
    "latitude": 9.6778,
    "longitude": 39.5318
  },
  "gpsDropoff": null,
  "pickupPhotoRef": "uploads/photos/del-101/pickup-1708084900000.webp",
  "pickupPhotoUrl": "https://<uploads-bucket>.s3.<region>.amazonaws.com/uploads/photos/del-101/pickup-1708084900000.webp?X-Amz-...",
  "dropoffPhotoRef": null,
  "pickedUpAt": "2026-02-18T08:15:00Z",
  "confirmedAt": null,
  "disputeReason": null,
  "createdAt": "2026-02-18T08:00:00Z",
  "updatedAt": "2026-02-18T08:15:00Z",
  "version": 2
}
```

**Photos (1.25.0, FRD-12)**: `pickupPhotoUrl` and `dropoffPhotoUrl` are presigned S3 `GET`s
(15 minutes) of the stored photo keys, signed only after the participant check below, so the
delivery's buyer, farmer and trucker see both photos and nobody else gets a URL. A key that is not
this delivery's photo key of that type (written through a delivery update before 1.25.0) is not
signed. Absent when there is no photo, the uploads bucket is not configured or signing fails (the
delivery is still answered). Lists (`GET /deliveries`) never carry them. Admins get the same URLs
in the transaction view (`GET /admin/transactions/{orderId}`).

**Note**: Access is restricted to the farmerId, truckerId, or buyerId matching the authenticated user's JWT `sub` claim. Since 1.15.0 (ESC-48) no phone number is returned, to any party: the parties take each other's number from the order, only while its contact is open (`order-lifecycle.md` 1.18.0). Admins still see the stored phones in the transaction view (`GET /admin/transactions/{orderId}`).

**Error Codes**:
- `401 Unauthorized` -- Authentication required
- `403 Forbidden` -- User is not a participant in this delivery
- `404 Not Found` -- Delivery not found, or soft-deleted before 1.9.0 (a delivery cancelled since 1.9.0 is returned with `status: cancelled`)
- `500 Internal Server Error` -- Server-side failure

---

### 4. Upload Photo Evidence
**Method**: POST /deliveries/{id}/photos
**Authentication**: Required (the photo's taker only, 1.25.0)
**Content-Type**: application/json

The server does not receive the file. It issues a pre-signed S3 `PUT` URL and the key the
photo will live at; the client uploads the photo straight to S3 and then attaches the key with a
`delivery_photo` sync operation (section 4b). This was documented as `multipart/form-data` with a
`photo` file before 1.7.0; the server never worked that way. Before 1.25.0 the key went in
`pickupPhotoRef` / `dropoffPhotoRef` of a delivery update; no shipped client did that.

**Request Body**:
```json
{ "type": "pickup", "sizeBytes": 148213, "contentType": "image/webp" }
```

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `type` | string | Yes | `pickup` or `dropoff` |
| `sizeBytes` | number | Yes (1.25.0) | The photo's exact size, 1 to 204800 (200 KiB) |
| `contentType` | string | No | `image/webp` (default, the phone) or `image/jpeg` (the browser) |

**Who may ask (1.25.0, FRD-12)**: only the party who confirms that handoff takes its photo.

| `type` | Taker | Everyone else (the farmer, the other party, an outsider) |
|--------|-------|----------------------------------------------------------|
| `pickup` | the delivery's trucker (a self-pickup has no trucker, so no pickup photo) | `403 DELIVERY_UNAUTHORIZED` |
| `dropoff` | the delivery's buyer (the buyer's delivery confirm, or the self-pickup confirm at the farm) | `403 DELIVERY_UNAUTHORIZED` |

The delivery may still be `pending` (a phone uploads before its pickup syncs). A cancelled
delivery (by `status` or a legacy `deletedAt`) gets `409 DELIVERY_PHOTO_REFUSED`, after the
participant check: a `409` is final for the phone, which keeps waiting on a `404` (the delivery's
own create may still be on its way). A handoff that already has its photo gets
`409 DELIVERY_PHOTO_EXISTS`: evidence is never replaced.

**Response (200)**:
```json
{
  "uploadUrl": "https://<uploads-bucket>.s3.<region>.amazonaws.com/uploads/photos/del-101/pickup-1708084900000.webp?X-Amz-...",
  "photoRef": "uploads/photos/del-101/pickup-1708084900000.webp",
  "issuedAt": "2026-02-18T08:15:30Z",
  "expiresInSeconds": 900
}
```

The key is `uploads/photos/{deliveryId}/{type}-{ms}.webp`, or `.jpg` for `image/jpeg` (1.25.0).

**Upload**: `PUT uploadUrl` with `Content-Type` equal to `contentType` and the file as the body,
within `expiresInSeconds` (15 minutes). Both the type and the length (`sizeBytes`) are signed into
the URL, so S3 refuses a body of any other size or type (`403 SignatureDoesNotMatch`): the 200 KiB
cap holds whatever the client does. A browser sets `Content-Length` itself. Then attach `photoRef`
(section 4b).

**Constraints**:
- Size: at most 200 KiB, enforced twice by the server since 1.25.0 (the signed length here, the
  object's size at the attach)
- Format: WebP (phone) or JPEG (browser); the URL is bound to the type
- Max width: 800px (resized client-side)
- One photo per handoff type (the first stays)

**Client status (1.25.0)**: both apps use it. The phone keeps the compressed WebP and its sync
queue uploads it when online (Offline Storage, Upload Flow); the browser uploads the JPEG as soon
as it is taken.

**Error Codes**:
- `400 DELIVERY_VALIDATION` -- missing delivery id, missing or invalid `type`, `contentType` other than `image/webp` or `image/jpeg`
- `400 DELIVERY_PHOTO_TOO_LARGE` -- `sizeBytes` missing, below 1 or above 204800 (1.25.0)
- `401 DELIVERY_UNAUTHORIZED` -- authentication required
- `403 DELIVERY_UNAUTHORIZED` -- not a participant, or not this photo's taker (1.25.0)
- `404 DELIVERY_NOT_FOUND` -- delivery not found
- `409 DELIVERY_PHOTO_REFUSED` -- the delivery was cancelled (by `status` or a legacy `deletedAt`; a `404` before 1.25.0)
- `409 DELIVERY_PHOTO_EXISTS` -- the handoff already has its photo (1.25.0)
- `500 DELIVERY_SERVER_ERROR` -- upload bucket not configured or S3 failure

---

### 4b. Attach a Photo (`delivery_photo`, 1.25.0, FRD-12)
**Method**: POST /sync (`sync-protocol.md` 1.39.0)
**Authentication**: Required (the photo's taker only)

After the `PUT`, the taker attaches the key to the delivery with a create-only sync operation:

```json
{
  "operationId": "uuid-v4",
  "idempotencyKey": "trucker-789:delivery_photo:del-101#pickup:create:1708084960000",
  "entityType": "delivery_photo",
  "entityId": "del-101#pickup",
  "action": "create",
  "data": { "photoRef": "uploads/photos/del-101/pickup-1708084900000.webp" },
  "timestamp": "2026-02-18T08:16:00Z"
}
```

The entity id is `{deliveryId}#{pickup|dropoff}`: it names the photo, not the delivery, so a photo
waiting in a phone's queue never holds back the delivery's own operations or its refresh.

The server, in this order:

1. `action` must be `create`, the entity id must have a known type, `data` must hold `photoRef`
   only, and `photoRef` must be this delivery's key of this type (section 4); else
   `DELIVERY_PHOTO_INVALID`.
2. Reads the delivery: missing answers the unprefixed `Delivery not found.` (retried: its create
   may still be on its way); a read failure answers `deferred`.
3. The caller must be the photo's taker (the table in section 4); else `DELIVERY_PHOTO_FORBIDDEN`.
   An outsider also writes a `ROLE_FORBIDDEN` fraud signal (`sync-protocol.md` 1.27.0); a party
   of the delivery sending the other type does not.
4. A cancelled delivery refuses (`DELIVERY_PHOTO_REFUSED`). The same key already attached is a
   `success` with nothing written; another key is `DELIVERY_PHOTO_EXISTS`.
5. `HEAD` on the object in the uploads bucket: missing is `DELIVERY_PHOTO_MISSING`; over 200 KiB
   (or empty) is `DELIVERY_PHOTO_TOO_LARGE`; a stored type that is not the key's is
   `DELIVERY_PHOTO_INVALID`; any other storage failure, or no bucket configured, is `deferred`.
6. Writes `pickupPhotoRef` or `dropoffPhotoRef` only, with the S3 version id the `HEAD` saw in the
   server-only `pickupPhotoVersionId` / `dropoffPhotoVersionId` (the uploads bucket is
   versioned), conditioned on the field being absent, the delivery not cancelled and not
   soft-deleted. `version` and `updatedAt` do not move, so a handoff queued on another phone
   never conflicts with a photo, and the next pull of the same version brings the key. A
   condition that fails is judged again on a strongly consistent read (step 4).

Every read signs the recorded version (`GET /deliveries/{id}`, the admin transaction view), so a
second `PUT` to the key within its 15-minute presign window adds a version and never replaces the
attached evidence. A row without a version id signs the key's latest version.

| Answer | Mobile client (sync queue) | Web |
|--------|----------------------------|-----|
| `success` (`serverVersion` is the delivery's, no `serverData`) | Synced | Done |
| `deferred` with `DELIVERY_PHOTO_PENDING:` | Re-queued without a retry | Logged |
| `DELIVERY_PHOTO_*` error | Rejected for good, not reverted, not announced and not counted in "Sync failed": the handoff stands without its photo | Logged; the confirmed handoff stands |
| unprefixed error | Retried | Logged |

The write's stream record is filtered out by the notifier (it reads deliveries on `INSERT` and
on the `nearbyNotifiedAt` stamp only), so attaching a photo sends no push.

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
| `farmerPhone` | string | Server | E.164 | Farmer's phone, server-stamped on create from the users table (v1.6.1); never returned to clients since 1.15.0 (ESC-48); shown to admins in the transaction view |
| `truckerId` | string (UUID) | Yes | -- | Trucker transporting the crop |
| `truckerName` | string | Yes | 1-100 chars | Trucker's display name. Server-stamped on create from the caller's profile; absent on self-pickup (v1.6.1) |
| `truckerPhone` | string | Server | E.164 | Trucker's phone, server-stamped on create; absent on self-pickup; never returned to clients since 1.15.0, admin view only |
| `buyerId` | string (UUID) | Yes | -- | Buyer receiving the crop |
| `buyerName` | string | Yes | 1-100 chars | Buyer's display name. Server-stamped on create from the buyer's profile (order row as fallback); client value ignored (v1.6.1) |
| `buyerPhone` | string | Server | E.164 | Buyer's phone, server-stamped on create from the users table (v1.6.1); never returned to clients since 1.15.0, admin view only |
| `qrToken` | string | Yes | Base64-encoded, <500 bytes | HMAC-signed QR verification token |
| `status` | enum | Yes | See Status Types | Delivery lifecycle status |
| `cropType` | string | No | Server-only, a crop key | The order's crop, stamped on create from the order row (1.21.0) so the deliveries list titles a card ("Teff · 1,500 kg") without one order read per delivery; a client value is refused as an unknown field; absent on deliveries created before 1.21.0 |
| `quantity` | number | No | Server-only, kilograms | The order's quantity, stamped with `cropType` (1.21.0) |
| `pickupPlace` | string | No | Server-only | The listing's `locationName` at create time (1.21.0); absent when the listing has none |
| `pickupPlannedAt` | string (ISO 8601) | No | Server-only | The trip's `departureDate` at create time (1.21.0), the planned pickup day on the waiting card; absent on a self-pickup |
| `gpsPickup` | LatLng | No | -- | GPS coordinates at pickup (null until picked up) |
| `gpsDropoff` | LatLng | No | -- | GPS coordinates at dropoff (null until delivered) |
| `pickupPhotoRef` | string | No | `uploads/photos/{deliveryId}/pickup-{ms}.{webp\|jpg}`, the key from section 4 | Photo evidence at pickup, written only by a `delivery_photo` attach since 1.25.0 (section 4b); in a delivery op any other value fails the op (1.7.0) and a valid key is dropped (1.25.0) |
| `dropoffPhotoRef` | string | No | `uploads/photos/{deliveryId}/dropoff-{ms}.{webp\|jpg}`, the key from section 4 | Photo evidence at dropoff, as `pickupPhotoRef` |
| `pickupPhotoUrl` | string | No | Server-only, read-only | Presigned GET (15 minutes) of the pickup photo's attached version, on `GET /deliveries/{id}` for the parties and on the admin transaction view only (1.25.0) |
| `pickupPhotoVersionId` / `dropoffPhotoVersionId` | string | No | Server-only, never returned | The S3 version id recorded at the attach; the reads sign it (1.25.0) |
| `dropoffPhotoUrl` | string | No | Server-only, read-only | Presigned GET of the drop-off photo, as `pickupPhotoUrl` (1.25.0) |
| `pickedUpAt` | string (ISO 8601) | No | -- | Timestamp when trucker picked up crops |
| `pickupCapturedAt` | string (ISO 8601) | No | Server-only | When the phone made the pickup scan: the `pickedUp` operation's `timestamp`, stamped with `pickedUpAt` (1.23.0, FRD-09). Stored only when plausible (not more than 5 minutes after the operation reached the server, not older than 30 days); absent otherwise and on deliveries picked up before 1.23.0. The fraud rule for a delivery too fast for its distance reads it (`fraud-detection-architecture.md` 15.1); a client value is never written |
| `confirmedAt` | string (ISO 8601) | No | -- | Timestamp when buyer confirmed receipt |
| `disputeReason` | string | No | 0-500 chars | Reason for dispute (if status is disputed) |
| `disputeResolution` | string | No | Server-only: `farmer_wins` or `buyer_wins` | The admin's dispute verdict, stamped on a `disputed` delivery (v1.8.0) |
| `disputeResolvedAt` | string (ISO 8601) | No | Server-only | When the verdict was recorded (v1.8.0) |
| `nearbyNotifiedAt` | string (ISO 8601) | No | Server-only | When a check-in first put the truck within 5 km of the drop-off point (v1.20.0, LT-07): stamped once, conditional on the status `pickedUp` or `inTransit` and on no stamp yet, without moving `version` or `updatedAt`; its stream record tells the buyer `truck_nearby` (`notifications-api.md` 1.3.0) |
| `nearbyDistanceKm` | number | No | Server-only, one decimal | The truck's great circle distance to the drop-off point at that check-in (v1.20.0) |
| `nearbyMinutes` | number | No | Server-only, a multiple of 5 | Minutes from that check-in to the start of the arrival window (v1.20.0); absent when unknown |
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
pending → pickedUp      (trucker scans farmer's QR code at pickup; required since 1.24.0, money or not)
pending → cancelled       (any participant cancels before pickup, by update or delete)
pickedUp → inTransit   (trucker departs from pickup location)
pickedUp → delivered    (buyer confirms the handoff; marking in transit is optional)
inTransit → delivered   (buyer scans the trucker's drop-off QR, the farmer's pickup token kept on the trucker's phone, 1.22.0)
delivered → disputed     (any participant raises a dispute within 48 hours; the escrow is frozen first, 1.10.0)
```

System only, never through sync (1.16.0): `pending`, `pickedUp` or `inTransit` `→ cancelled` or
`→ disputed`, by an admin's dispute disposition (see the dispute verdict paragraphs above).

System, on a check-in (1.20.0, LT-07, `live-trip-api.md` section 5): `pickedUp → inTransit` when
an accepted `location_ping` `fix` of the delivery's trip with `accuracyM` at most 1,000 was
captured at or after its `pickedUpAt` and is 2 km or more (great circle) from its pickup point, its `gpsPickup`, else its
listing's location. The delivery is read strongly consistent first, and the write is the same as
the trucker's own transition (status, `updatedAt`, `version` + 1), conditional on the status
`pickedUp` and the version read; a delivery that moved on in between is left alone. It is
audited in transaction-audit as the trucker's transition is (`delivery_updated`, `pickedUp` to
`inTransit`) but with the actor `system` and `metadata` `source=check_in;distanceKm=<km>`, and
the order follows exactly as for the trucker's own `inTransit` (`pickup_confirmed → in_transit`,
forward only, system-audited, 1.4.0). The trucker's button stays as the fallback (a driver who
shares no location, a check-in that waited offline from before the pickup synced). The
trucker's own `inTransit` for a delivery that is already `inTransit` (the tap reached the server
after a check-in moved it) answers `conflict` with the row and its version, nothing written, so
the app takes the server's row instead of keeping a failed change; anyone else still gets
`DELIVERY_INVALID_TRANSITION`. A check-in never moves money: the QR handoffs, the admin verdicts
and the timers do.

Since 1.9.0 (ESC-08) nothing cancels a delivery after pickup through sync (before, `pickedUp →
cancelled` was allowed and the trucker's delete skipped the transitions).

Invalid transitions return `422 Unprocessable Entity`:
- `pending → inTransit` (must pick up first)
- `pending → delivered` (must pick up first; a self-pickup goes straight to `delivered`)
- `pickedUp`, `inTransit`, `delivered` or `disputed` `→ cancelled` (`DELIVERY_CANCEL_NOT_ALLOWED`, 1.9.0)
- `cancelled → *` (terminal state)
- `disputed → cancelled` (disputes follow separate resolution flow)

**Dispute Window**: Disputes must be raised within 48 hours of `confirmedAt`. After 48 hours, the `delivered` status becomes final and escrow is released automatically, later when the payout delay applies (1.19.0: the `delivered` transition stamps the escrow's `releaseNotBefore` with its `deliveryConfirmedAt` and `disputeWindowEndsAt`, `escrow-protocol.md` 1.33.0).

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

As built (1.24.0): no `requires_review` flag is written yet. A scanned pickup without a fix is accepted (a typed code on a paid delivery and a self-pickup confirm need one, see QR Secret Storage); whether to require or record a missing fix is the GPS spike's call (SPK-01). The scan screen takes the fix when the QR is read and its Confirm waits for that attempt (at most 30 seconds), so the fix sent is the one taken at the scan.

---

## Photo Evidence

### Requirements

Photo evidence is **optional but encouraged**. It provides visual proof of crop condition at pickup and delivery, critical for dispute resolution.

Since 1.25.0 the server enforces the size (signed into the `PUT` and checked at the attach), the
format (WebP or JPEG, bound to the URL), the taker and one photo per type; the width and the
quality are client rules.

| Constraint | Value | Rationale |
|------------|-------|-----------|
| Format | WebP (phone), JPEG (browser) | WebP is the best ratio for 3G; the browser's canvas re-encodes JPEG |
| Max width | 800px | Sufficient for visual proof, small file size |
| Max file size | 200 KiB (server-enforced, 1.25.0) | Uploadable on 3G in a few seconds |
| Photos per type | 1 (the first stays, 1.25.0) | One pickup and one drop-off photo per delivery |
| Compression quality | 75%, then 60%, then 45% at 640 px (phone); 70% (browser) | Good visual quality at small file size; a photo still over the cap is dropped |

### Offline Storage

1. Camera captures JPEG on device
2. Client resizes to max 800px width
3. Client converts to WebP at 75% quality (60%, then 45% at 640 px, when over 200 KiB; still over, the photo is dropped and the screen says so)
4. Client stores in local filesystem: `{appDir}/photos/{deliveryId}/{type}-{timestamp}.webp` (`-fb` and `-fb2` suffixes for the fallbacks)
5. Isar delivery record keeps the local file path in `pickupPhotoRef` / `dropoffPhotoRef`; it is never sent to the server (1.7.0). A pull keeps it while the server has no photo for that type, and takes the server's key once it has one (1.25.0)
6. Upload queue (1.25.0, FRD-12): the handoff confirm queues a `delivery_photo` create behind the handoff, with the file path in `data._localPath` (a `_` key, never sent: see Upload Flow)
7. After the attach succeeded, the phone's files of that handoff are deleted and the row takes the server's key; a delivery pull or conflict keeps the local path only while the server has no photo of that type

The browser keeps no files: it takes the photo at 800 px and JPEG quality 70 and uploads it at once (Upload Flow).

### Upload Flow (1.25.0, FRD-12)

Mobile, when the sync queue reaches the `delivery_photo` operation (a preflight of the batch):
1. `POST /deliveries/{id}/photos` with `{type, sizeBytes, contentType}` (section 4) returns `uploadUrl` + `photoRef`
2. `PUT` the file to `uploadUrl` with the signed `Content-Type` and `Content-Length` within 15 minutes
3. The operation's payload becomes `{"photoRef": key}` (plus the never-sent `_localPath`), in the batch and in the queue, so a later run never uploads twice; the batch then attaches it (section 4b)
4. Offline, a `PUT` that did not reach S3 or got a `5xx`, a presign `5xx` or `401` keep the operation queued without using a retry; the handoff in front of it is sent anyway. A presign `404` waits while the delivery's own create waits on the phone and 10 minutes after the photo was queued, then gives up. A presign `400`, `403` or `409` (including `DELIVERY_PHOTO_REFUSED`, a cancelled delivery), a file that is gone, or the third `PUT` refused with a `4xx` give up: the operation goes without a key so the server refuses it for good. A refused delivery create, pickup or delivery confirm rejects the photo waiting behind it
5. After the attach, the phone's files of that handoff are deleted; until then the local file shows the photo on this phone offline

Web: the browser uploads right after the photo is taken (steps 1 and 2), shows it from memory,
and after the handoff confirm succeeded sends the `delivery_photo` operation on its own `/sync`
call; a refusal is logged and the confirmed handoff stands.

Viewing: the delivery detail of each party loads `pickupPhotoUrl` / `dropoffPhotoUrl` from
`GET /deliveries/{id}` (one read per delivery, reused 10 minutes) and caches the image on the
phone under its key; offline and never cached, the tile says it shows when online.

### S3 Storage

- **Bucket**: Same uploads bucket used for other media (audio notes, etc.); private, SSE-S3, versioned
- **Key pattern**: `uploads/photos/{deliveryId}/{type}-{timestamp}.{webp|jpg}`
- **CORS (1.25.0)**: `PUT`, `GET`, `HEAD` with `Content-Type` from the web app and admin origins only (the browser `PUT`s and CanvasKit fetches images with XHR); the CloudFront CSP already allows the regional S3 hosts
- **Lifecycle**: the bucket's `photos/` rule (Glacier after a year) does not match the `uploads/photos/` keys; no lifecycle applies to them yet (follow-up)
- **Access**: Pre-signed URLs for viewing (15-minute expiry), signed by the delivery Lambda after its participant check and by the admin Lambda; the sync Lambda reads `uploads/photos/*` for the attach's `HEAD` only

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
   - `pickupPhotoRef` / `dropoffPhotoRef` must be exactly `uploads/photos/{deliveryId}/(pickup|dropoff)-{ms}.(webp|jpg)` for the operation's own delivery (the key section 4 issued). Anything else, including a local file path, another delivery's key or the pre-1.7.0 `photos/...` shape, fails the whole operation with `DELIVERY_INVALID_FIELD: <field> has invalid format.` (FIX-011, path traversal)
   - A valid key is dropped, not written (1.25.0): photos are attached only by `delivery_photo` (section 4b), which checks the taker, the object's presence, size and type, and keeps the first photo
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
- Compress photos to at most 200 KiB before uploading (WebP on the phone, JPEG in the browser; 1.25.0)

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
- **Handoff photos (1.25.0)**: the trucker takes (uploads and attaches) the pickup photo, the buyer the drop-off photo; the farmer takes none. The delivery's buyer, farmer and trucker, and admins, see both; nobody else gets a URL
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
- No phone numbers in any client read of a delivery (detail, list, `/sync` data) since 1.15.0; contact goes through the order (ESC-48)
- GPS coordinates are sensitive -- only stored for verification, not displayed to other platform users
- Photo evidence accessible only via pre-signed S3 URLs with 15-minute expiry, signed for the delivery's three parties (`GET /deliveries/{id}`) and admins (transaction view) only; never on a list

---

## Offline Behavior

All core delivery verification operations work offline:

| Operation | Offline Capability | Sync Trigger |
|-----------|--------------------|--------------|
| Create delivery | Full -- saved to Isar, QR generated locally | POST /sync when online |
| Generate QR code | Full -- HMAC computed locally | No sync needed |
| Scan QR code | Full -- HMAC verified locally | No sync needed |
| Capture GPS | Full -- coordinates stored in Isar | POST /sync when online |
| Capture photo | Full -- WebP stored locally, a `delivery_photo` op queued at the confirm | POST /deliveries/{id}/photos and the S3 `PUT` when online, then the attach in `/sync` (1.25.0) |
| Update status | Full -- local Isar update | POST /sync when online |
| Browse deliveries | Full -- query local Isar cache | Refresh on next sync |

### Sync Priority

The sync queue has no per-entity priority. `SyncQueue` sends pending operations oldest
first (FIFO, `sortByCreatedAt` in `mobile/lib/core/sync/sync_queue.dart`), so a delivery
status change syncs after whatever was queued before it and before whatever was queued
after it, regardless of entity type. A handoff photo (1.25.0) is a `delivery_photo` operation
queued right behind its handoff: its upload runs as a preflight when the batch is built, and a
photo that cannot go up yet waits without holding back the handoff or anything else.

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
- **Pickup proof (1.24.0, FRD-11)**: a pickup at the farm, the trucker's `pending → pickedUp`
  and the self-pickup buyer's `pending → delivered`, needs the farmer's nonce whatever the money.
  With no nonce on record it is **`deferred`** (`DELIVERY_NONCE_PENDING: Waiting for the farmer's
  QR code to sync. ...`, nothing written, the op stays queued) until the farmer's nonce syncs,
  and refused with `DELIVERY_NONCE_MISSING: The farmer's QR code never reached the server for
  this pickup. Contact support.` once 14 days have passed since the later of the delivery's
  creation and the scan (the operation's `timestamp`, used when plausible: at most 5 minutes
  after it reached the server and not older than 30 days). Counting from the scan matters for a
  delivery made weeks before its trip: an honest scan that syncs before the farmer's queued
  nonce waits instead of being refused at once. No escrow is read for it. Before 1.24.0 a pickup of a delivery without an escrow (cash, or an order whose escrow write
  failed) passed with no QR. With the nonce on record the rules below apply unchanged: the scan
  (`scannedQrNonce`) or the typed code (`scannedQrCode`) must match, within 14 days of the nonce,
  once per leg (consumed after the write commits). A trucker's pickup stores `gpsPickup` as
  taken at the scan (when the phone had a fix), the server's `pickedUpAt` and the phone's
  `pickupCapturedAt`; a self-pickup stores `gpsDropoff` (mandatory) and `confirmedAt`; both
  audit `handoff=qr` or `handoff=code`. A cargo photo taken at the scan
  goes up and is attached since 1.25.0 (FRD-12, section 4b). Shipped apps already prove every
  pickup this way (the farmer's QR registers its nonce, the scan sends it), so no client or flag
  change is needed.
- For a trucker's drop-off of a money-bearing delivery whose nonce has not yet synced, the server returns
  **`deferred`** (see sync-protocol) rather than failing open, and refuses it with
  `DELIVERY_NONCE_MISSING` once 14 days have passed since the later of the delivery's creation
  and the scan (from creation alone before 1.24.0). A delivery carries money
  when its order's escrow (`escrow-order-{orderId}`, read strongly consistent, any status)
  exists, or else when the escrow `DeliveryIndex` finds one for it (an escrow paid through
  `POST /escrow/initiate`, or a legacy delivery without an `orderId`). The order's escrow is
  read first because its link to the delivery can fail (`ORDER_ESCROW_LINK_FAILED`) or lag
  (1.17.0, FUP-42: before, only the `DeliveryIndex` was asked and such a delivery passed with
  no QR). When either read fails the answer is also `deferred` (`DELIVERY_NONCE_PENDING`,
  nothing written, never the 14-day refusal), and the server logs
  `QR_GATE_UNDECIDED deliveryId=.. orderId=.. reason=..`, which the order escrow problems
  alarm counts, so a read that keeps failing is seen. Web clients show their QR-pending text
  for this deferral too (accepted: the user tries again and a later retry clears it). The
  drop-off of a delivery with no escrow (cash) and no nonce passes without a QR, as before: since
  1.24.0 only one picked up without a QR before then can reach it.
- Only the delivery's farmer may set `qrNonce` (SIM-41). A `qrNonce` sent on create, or on
  an update by anyone else, is silently dropped. Since 1.22.0 the farmer's nonce op stores the
  nonce and nothing else (other fields riding along are ignored, never written), and once the
  delivery left `pending` it stores nothing at all and answers `success` at the current version:
  the nonce then is the drop-off's secret, and a late one (a QR reopened on a phone whose copy
  still said `pending`) would void the trucker's drop-off QR and clear the drop-off's misses.
  The farmer's app reads the delivery again before it makes a nonce and makes none after
  pickup.
- A farmer update carrying only `qrNonce` (plus `version`/`updatedAt`) stores the secret and
  returns `success` with the delivery's **current** `serverVersion`: nothing on the delivery
  record changes, so it is not written and the version does not move (1.6.0). Bumping it made
  the scanner's queued confirm, sent at the version it last pulled, conflict and get undone.
  Web clients send this op directly when the farmer opens the QR (1.6.0; before, web never
  registered the nonce, so a web-shown QR could not be confirmed).
- **Typed code (1.22.0, usability audit T-1)**: when a camera or a screen fails, the
  scanner may send `scannedQrCode` instead of `scannedQrNonce` on the `pickedUp` or
  `delivered` update (a scanned nonce wins when both are sent; neither is stored on the
  delivery). The code is nine ASCII digits: the first eight are HMAC-SHA256 with the UTF-8
  nonce as key over `wc-handoff-code:v1:{deliveryId}`, the first 8 bytes big-endian modulo
  10^8, left-padded; the ninth is a Damm check digit, so the apps catch a mistyped digit or two
  swapped neighbours before sending. Shared vectors pin the server and the apps to the same
  digits. The phone that shows the QR reveals the code only behind "Camera not working?", with
  a warning never to read it out on the phone. The server derives the code from the stored
  nonce and compares in fixed time, then applies the same freshness, single-use and deferral
  rules as a scan (an unsynced nonce on a paid delivery is `deferred`). Errors:
  `DELIVERY_VALIDATION: scannedQrCode must be 9 digits with a valid check digit.` (no miss
  counted); `DELIVERY_CODE_MISMATCH:` (a wrong code, counted); `DELIVERY_CODE_LOCKED:` from the
  fifth wrong code of a leg on, even for the right code, until the farmer's Regenerate stores a
  new nonce (a scanned QR still works when locked), and for any typed code where the secret
  store is not provisioned; `DELIVERY_CODE_LOCKED_DROPOFF:` instead for the `delivered` leg of a
  trucker delivery, whose code nothing can renew (the farmer's Regenerate only works before
  pickup). Way out of a locked drop-off: the buyer scans the trucker's drop-off QR when the
  trucker has one; when the trucker typed the code at pickup there is no QR, so the buyer (or the
  trucker) contacts support, who confirm the handoff with both parties by phone and settle the
  order's escrow with a `farmer_wins` verdict and the `settle_now` disposition
  (`escrow-protocol.md` 1.21.0, the farmer and the trucker leg paid), or with `buyer_wins` and
  `return_to_farmer` when the goods did not arrive; `DELIVERY_CODE_GPS_REQUIRED:` for a typed code without the leg's
  `gpsPickup` or `gpsDropoff` on a delivery that carries money (an escrow read that fails
  answers `deferred`, `DELIVERY_NONCE_PENDING`, logged `QR_GATE_UNDECIDED`). Attempts are kept in
  the secret row as one string set of attempt ids per leg (`codeMissesPickedUp`,
  `codeMissesDelivered`); an attempt id is `{operationId}:{the first 16 hex digits of SHA-256 of
  the code}`. Each attempt is taken before the comparison by one `ADD` conditional on the nonce
  the code is checked against and on the set holding fewer than 5 ids (or this very id), so
  guesses sent in parallel cannot pass the cap, a retry of the same operation with the same code
  counts once, and a new code under a reused operation id (errors never store the idempotency
  key) always counts; a right code removes its id again, so only wrong codes stay counted.
  Storing a different nonce removes both sets. The secret row is read strongly consistent. Mismatches and locks write the
  `QR_HANDOFF_FAILURE` signal (`sync-protocol.md` 1.33.0). The `pickedUp` and `delivered`
  audit entries carry `metadata` `handoff=qr` or `handoff=code` as dispute and fraud evidence.
- **Drop-off QR (1.22.0, T-3)**: the buyer's `delivered` is checked against the same pickup
  nonce, and no screen showed it after pickup, so a paid trucker delivery could not be
  confirmed. The trucker's phone now keeps the farmer's token it scanned at pickup (or the
  code it typed) in secure storage and shows it as the drop-off QR, with its typed code; the
  buyer scans or types it. One nonce serves both legs, each consumed once, within the same
  14-day freshness from the first QR. Trade-off: the trucker holds the pickup secret after
  pickup (it was on their screen anyway); a separate trucker nonce in the same store is a
  follow-up. No server change for this part.
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
| 1.15.0 | 2026-10-17 | Contact only after the money is held (ESC-48): `GET /deliveries/{id}` and every delivery row in `/sync` data (create, update, conflict, cancel, dispute) carry no `farmerPhone`, `truckerPhone` or `buyerPhone`; the server still stamps them at create for the admin transaction view. No shipped client reads them. |
| 1.16.0 | 2026-10-22 | Dispute dispositions (FUP-16, `escrow-protocol.md` 1.21.0): an admin's disposition closes a delivery that never reached `delivered` as a system actor, conditional on the status read and on no verdict yet, version added to: `return_to_farmer` and `settle_now` to `cancelled` with `cancelReason` (`dispute_buyer_wins`, `dispute_farmer_wins`) and `cancelledAt`, `delivered_anyway` to `disputed` with `confirmedAt` set when missing; the verdict is stamped on both, audited in transaction-audit; a sync dispute still starts only from `delivered`, so the escrow's pickup rule (`ESCROW_041`) needs no change here. No client change: the statuses are known and the extra keys are ignored. |
| 1.17.0 | 2026-10-23 | QR gate money check (FUP-42): with no nonce on record, a `pickedUp` or `delivered` (trucker flow and self-pickup) is money-bearing when the order's escrow `escrow-order-{orderId}` exists (strongly consistent, any status) or the `DeliveryIndex` finds an escrow; before, only the `DeliveryIndex` was asked, so an order escrow whose delivery link failed or lagged let the transition pass with no QR. A failed read answers `deferred` (`DELIVERY_NONCE_PENDING`), writes nothing, never hard-fails, and logs the alarmed `QR_GATE_UNDECIDED` (order escrow problems alarm); web shows its QR-pending text for it. Cash deliveries and the nonce checks are unchanged. No client change (`sync-protocol.md` 1.19.0). |
| 1.20.0 | 2026-10-28 | Check-in effects (LT-07, `live-trip-api.md` sections 4 and 5): a system `pickedUp → inTransit` when an accepted check-in captured after the pickup is 2 km or more from the pickup point (fresh read, conditional on the status and the version read, the trucker's write, audited with the actor `system` and `source=check_in`, the same order coupling to `in_transit`); the trucker's own `inTransit` on an `inTransit` delivery answers `conflict` with the row instead of `DELIVERY_INVALID_TRANSITION`; the server-only `nearbyNotifiedAt`, `nearbyDistanceKm` and `nearbyMinutes`, stamped once when a check-in is within 5 km of the drop-off point (conditional on `pickedUp` or `inTransit` and no stamp; `version` and `updatedAt` unchanged), which sends the buyer `truck_nearby` (`notifications-api.md` 1.3.0). No client change: the status is known and the extra keys are ignored. |
| 1.21.0 | 2026-10-30 | Cargo on the row (Fieldway deliveries boards): the create stamps the server-only `cropType` and `quantity` (kg) from the order, `pickupPlace` from the listing's `locationName` and `pickupPlannedAt` from the trip's `departureDate` (none on a self-pickup), all returned on `GET /deliveries`, `GET /deliveries/{id}` and every `/sync` delivery row, so the list cards read "Teff · 1,500 kg" and "Kebede Alemu, Bichena · pickup Thu 30 Oct" without one order read per delivery; a client that sends them hits the field whitelist; deliveries created before 1.21.0 carry none and the clients fall back to the delivery number. |
| 1.22.0 | 2026-11-04 | Typed handoff code and drop-off QR (usability audit T-1, T-3; see QR Secret Storage): `scannedQrCode` (nine digits, eight derived from the nonce and a Damm check digit) stands in for `scannedQrNonce` on `pickedUp` and `delivered`, compared in fixed time, with the scan's freshness, single-use and deferral rules; per-leg misses in the secret row (string sets of operation ids, cleared by a new nonce), `DELIVERY_CODE_LOCKED` from the fifth wrong code (a scan still works), `DELIVERY_CODE_MISMATCH`, `DELIVERY_CODE_LOCKED_DROPOFF` for a trucker's drop-off (support settles it), `DELIVERY_CODE_GPS_REQUIRED` for a typed code without GPS on a paid delivery, a malformed code is `DELIVERY_VALIDATION`; attempt ids carry a hash of the code, so a reused operation id cannot guess for free; the farmer's nonce op stores only the nonce and nothing once the delivery left `pending`; the secret row is read strongly consistent; `handoff=qr` or `handoff=code` in the transition's audit `metadata`. The trucker's phone keeps the farmer's pickup token and shows it as the drop-off QR the buyer scans (one nonce for both legs). Header drift fixed (the README and footer still said 1.20.0). |
| 1.23.0 | 2026-11-06 | Pickup capture time (FRD-09, `fraud-detection-architecture.md` 3.12.0, section 15.1): the `pickedUp` transition also stamps the server-only `pickupCapturedAt`, the operation's `timestamp` when it is plausible (at most 5 minutes after the operation reached the server and not older than 30 days), so the fraud rule for a delivery too fast for its distance can tell a pickup synced late from an offline phone from a fast trip. Returned on `/sync` delivery rows like the other server-only fields; no request or answer changes, no client change. A confirmed delivery may write the `IMPOSSIBLE_DELIVERY_TIME` signal on its trucker, flag-only. |
| 1.25.0 | 2026-11-07 | Handoff photos (FRD-12): `POST /deliveries/{id}/photos` takes `sizeBytes` (1 to 204800, required) and `contentType` (`image/webp` default or `image/jpeg`, `.jpg` keys), signs both into the `PUT`, answers only the photo's taker (the trucker for `pickup`, the buyer for `dropoff`; anyone else `403`) `409 DELIVERY_PHOTO_EXISTS` once the handoff has its photo and `409 DELIVERY_PHOTO_REFUSED` for a cancelled delivery (was `404`); new create-only sync entity `delivery_photo` (`{deliveryId}#{type}`, `{photoRef}`) attaches the key after a `HEAD` (present, at most 200 KiB, its key's type), first photo kept, its S3 version id recorded and signed by every read (a later `PUT` to the key cannot swap it), no version or `updatedAt` move, `DELIVERY_PHOTO_INVALID` / `_FORBIDDEN` / `_REFUSED` / `_EXISTS` / `_MISSING` / `_TOO_LARGE` final and `DELIVERY_PHOTO_PENDING` deferred; a photo ref in a delivery create or update is still shape-checked but no longer written; `GET /deliveries/{id}` and the admin transaction view carry `pickupPhotoUrl` / `dropoffPhotoUrl` (15-minute presigned GETs), lists never; the uploads bucket gets CORS for the web app and admin. Clients: the phone queues the photo behind the handoff and uploads it when online, the browser uploads at capture and attaches after the confirm; the three parties see the photos on the delivery detail, admins on the transaction detail |
| 1.24.0 | 2026-11-07 | Pickup proof (FRD-11, see QR Secret Storage): a pickup at the farm (the trucker's `pickedUp`, the self-pickup buyer's `delivered`) with no farmer's nonce on record is `deferred` until the nonce syncs and refused with `DELIVERY_NONCE_MISSING: The farmer's QR code never reached the server for this pickup. Contact support.` once 14 days have passed since the later of the delivery's creation and the scan (the operation's `timestamp` when plausible), whatever the money and with no escrow read; before, a delivery without an escrow passed with no QR. The drop-off keeps the money rule; its 14 days now count the same way (before, from creation only). The scan screen's Confirm waits for the fix taken at the scan (at most 30 seconds; a phone with no fix still confirms a scanned QR). No new code or field; shipped clients already send the farmer's nonce on every pickup and handle `deferred`. GPS Failure Handling says what is built. |
| 1.19.0 | 2026-10-23 | Payout delay (FRD-13, `escrow-protocol.md` 1.33.0): the `delivered` transition stamps the linked escrow's `releaseNotBefore` in the same write as `deliveryConfirmedAt` and `disputeWindowEndsAt` (the trucker named by the delivery when the escrow has none yet); the automatic release waits for it. The dispute window and the delivery's own fields are unchanged; no client change on deliveries. |

---

**Last Updated**: 2026-11-07
**Version**: 1.25.0
**Status**: Draft (Phase R4.2)
