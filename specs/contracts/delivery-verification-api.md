# Delivery Verification API Contract

## Version
1.3.1

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
- **Soft deletes**: Cancelled deliveries marked with `deletedAt`, not physically removed

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
| At most one active delivery per order: the server claims `activeDeliveryId` on the order with a conditional write before writing the delivery; a retry of the same `deliveryId` re-claims, a `cancelled` delivery (update or soft delete) releases the slot | `DELIVERY_VALIDATION: The order already has an active delivery.` |

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
        "pickupPhotoRef": "photos/del-101/pickup-1708084900000.webp",
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
        "dropoffPhotoRef": "photos/del-101/dropoff-1708171200000.webp",
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
| `tripId` | string | No | Filter by trip | `tripId=trip-101` |
| `listingId` | string | No | Filter by crop listing | `listingId=crop-456` |
| `status` | string | No | Filter by status (combined with `farmerId`/`tripId` as a filter; used as the index key when alone) | `status=pending` |
| `role` | string | No | Filter by user's role in delivery (farmer, trucker, buyer) | `role=trucker` |
| `limit` | number | No | Results per page (default: 20, max: 50) | `limit=20` |
| `nextToken` | string | No | Pagination token from previous response | `nextToken=base64...` |

> **Scoping note**: A farmer never authors a delivery (the trucker does) and is not the `ParticipantIndex` key (that is the buyer), so a farmer must scope with `farmerId=` to see their deliveries; `mine=true` resolves the buyer. Truckers currently author deliveries locally; a symmetric `truckerId=` scope (needing a `TruckerIdCreatedAtIndex`) is a follow-up.

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
  "pickupPhotoRef": "photos/del-101/pickup-1708084900000.webp",
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
- `404 Not Found` -- Delivery not found or cancelled
- `500 Internal Server Error` -- Server-side failure

---

### 4. Upload Photo Evidence
**Method**: POST /deliveries/{id}/photos
**Authentication**: Required (only delivery participants)
**Content-Type**: multipart/form-data

**Request Body**:

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `photo` | file (WebP) | Yes | Photo evidence file |
| `type` | string | Yes | `pickup` or `dropoff` |

**Constraints**:
- Format: WebP only (best compression for 3G)
- Max width: 800px (auto-resized client-side)
- Max file size: 200KB
- Max photos per delivery: 3 per type (pickup/dropoff)

**Response**:
```json
{
  "photoRef": "photos/del-101/pickup-1708084900000.webp",
  "uploadedAt": "2026-02-18T08:15:30Z"
}
```

**Offline Behavior**: Photos are stored locally in Isar as base64 blobs (or filesystem reference). When connectivity is available, they are uploaded via this endpoint. The `photoRef` in the delivery record is updated via the next sync operation.

**Error Codes**:
- `400 Bad Request` -- Invalid format, file too large, wrong content type
- `401 Unauthorized` -- Authentication required
- `403 Forbidden` -- User is not a participant in this delivery
- `413 Payload Too Large` -- File exceeds 200KB
- `429 Too Many Requests` -- Rate limit exceeded
- `500 Internal Server Error` -- Server-side failure

---

## Data Models

### DeliveryDto Entity

| Field | Type | Required | Constraints | Description |
|-------|------|----------|-------------|-------------|
| `deliveryId` | string (UUID) | Yes | -- | Unique delivery identifier |
| `tripId` | string (UUID) | Yes | -- | Associated trip (links to Trip.tripId) |
| `listingId` | string (UUID) | Yes | -- | Associated crop listing (links to CropListing.listingId) |
| `farmerId` | string (UUID) | Yes | -- | Farmer who posted the crop |
| `farmerName` | string | Yes | 1-100 chars | Farmer's display name |
| `truckerId` | string (UUID) | Yes | -- | Trucker transporting the crop |
| `truckerName` | string | Yes | 1-100 chars | Trucker's display name |
| `buyerId` | string (UUID) | Yes | -- | Buyer receiving the crop |
| `buyerName` | string | Yes | 1-100 chars | Buyer's display name |
| `qrToken` | string | Yes | Base64-encoded, <500 bytes | HMAC-signed QR verification token |
| `status` | enum | Yes | See Status Types | Delivery lifecycle status |
| `gpsPickup` | LatLng | No | -- | GPS coordinates at pickup (null until picked up) |
| `gpsDropoff` | LatLng | No | -- | GPS coordinates at dropoff (null until delivered) |
| `pickupPhotoRef` | string | No | S3 key | Photo evidence at pickup |
| `dropoffPhotoRef` | string | No | S3 key | Photo evidence at dropoff |
| `pickedUpAt` | string (ISO 8601) | No | -- | Timestamp when trucker picked up crops |
| `confirmedAt` | string (ISO 8601) | No | -- | Timestamp when buyer confirmed receipt |
| `disputeReason` | string | No | 0-500 chars | Reason for dispute (if status is disputed) |
| `createdAt` | string (ISO 8601) | Yes | -- | Timestamp when delivery was created |
| `updatedAt` | string (ISO 8601) | Yes | -- | Timestamp when delivery was last updated |
| `deletedAt` | string (ISO 8601) | No | -- | Timestamp when delivery was soft-deleted (cancelled) |
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
| `disputed` | Buyer or farmer raised a dispute (within 48 hours of delivery) |

### Status Transitions

Valid status transitions (enforced server-side):

```
pending → pickedUp      (trucker scans farmer's QR code at pickup)
pending → cancelled       (any participant cancels before pickup)
pickedUp → inTransit   (trucker departs from pickup location)
pickedUp → cancelled    (cancellation after pickup — re-opens matched listings)
inTransit → delivered   (buyer scans trucker's QR code at dropoff)
delivered → disputed     (farmer or buyer raises dispute within 48 hours)
```

Invalid transitions return `422 Unprocessable Entity`:
- `pending → inTransit` (must pick up first)
- `pending → delivered` (must pick up and transit first)
- `delivered → cancelled` (cannot cancel after delivery -- use dispute instead)
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
4. Client stores in local filesystem: `{appDir}/photos/{deliveryId}/{type}-{timestamp}.webp`
5. Isar delivery record updated with local file path
6. Photo added to upload queue (separate from sync queue)

### Upload Flow

When connectivity is available:
1. Upload photo via `POST /deliveries/{id}/photos`
2. Server stores in S3: `uploads/photos/{deliveryId}/{type}-{timestamp}.webp`
3. Server returns `photoRef` (S3 key)
4. Client updates local Isar record with server `photoRef`
5. Client deletes local file after successful upload
6. Next sync operation includes the `photoRef` in delivery data

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
   - Must reference an existing trip with status `matched` or `inTransit`
   - Trip must belong to the authenticated trucker (for create)
3. **Listing ID**:
   - Must reference an existing crop listing
   - Listing must be in the trip's `matchedListings` array
4. **Farmer ID / Trucker ID / Buyer ID**:
   - Must reference existing users
   - Farmer must own the listing, trucker must own the trip
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
8. **Photo References**:
   - If provided, must reference an existing S3 object
   - Max 3 photos per type (pickup/dropoff)
9. **Participant Authorization**:
   - Only the order's matched trucker can create a delivery and confirm pickup; the buyer creates self-pickup deliveries (v1.3.0)
   - Only the buyer can confirm delivery
   - Any participant (farmer, trucker, buyer) can raise a dispute
   - Participant, order, listing, trip and type fields are immutable after create (v1.3.0)
10. **Order Binding** (v1.3.0):
   - The order must be `accepted`, `pickup_confirmed` or `in_transit`
   - One active delivery per order (`activeDeliveryId` claimed on the order; released on cancel)

### Client-Side Validation

- Same as server-side, plus:
- Show localized error messages in user's preferred language (Amharic, Afan Oromo, English)
- Validate before enqueueing sync operation (prevent invalid operations from clogging queue)
- Display GPS proximity result immediately after capture (green check if within 5km, red warning if outside)
- Compress photos to WebP <200KB before adding to upload queue

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
  - Follow-up: a symmetric `TruckerIdCreatedAtIndex` is not yet added (DynamoDB
    allows only one GSI addition per table update; truckers author locally today).

### Query Patterns

| Query | Index Used | Key Condition | Filter |
|-------|-----------|---------------|--------|
| All pending deliveries | StatusCreatedAtIndex | PK=`pending` | -- |
| Deliveries for a trip | TripIdIndex | PK=`trip-101` | -- |
| My deliveries (as buyer) | ParticipantIndex | PK=`buyer-456` (`mine=true`) | -- |
| My deliveries (as farmer) | FarmerIdCreatedAtIndex | PK=`farmer-123` (`farmerId=`) | optional `status` |
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
- **Cancel**: Any participant can cancel before `pickedUp` status; only trucker can cancel after pickup
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
- **Migration**: in-flight deliveries created before this change keep their on-record
  `qrNonce` and are validated against it (and it is scrubbed from any echoed `serverData`).

## Changelog

| Version | Date | Change |
|---------|------|--------|
| 1.0.0 | 2026-02-17 | Initial contract: CRUD endpoints, QR verification, GPS validation, photo evidence, state machine, offline behavior, Ethiopian context |
| 1.1.0 | 2026-07-09 | Buyer self-pickup (`deliveryType: self_pickup`); QR nonce moved to a dedicated secret table (never echoed); server-side freshness + single-use; `deferred` sync status for unsynced secrets; listing-GPS mandatory for self-pickup confirmation |
| 1.1.1 | 2026-07-11 | Doc fix: delivery statuses are camelCase on the wire (`pickedUp`, `inTransit`) — the server state machine rejects the previously-documented snake_case (`picked_up`, `in_transit`). `deliveryType: self_pickup` is unchanged (it is a type, not a status). |
| 1.2.0 | 2026-07-12 | `orderId` required on ALL delivery creates (was self-pickup only): order must exist, be non-terminal, and match the delivery's buyer/farmer; the order's escrow is linked to the delivery at creation (`DELIVERY_ORDER_REQUIRED` error). |
| 1.3.0 | 2026-09-25 | Delivery guards (SIM-09): order must be `accepted`/`pickup_confirmed`/`in_transit`; trucker-flow creator must be the order's trucker (order without a trucker is self-pickup only); one active delivery per order via `activeDeliveryId` claimed on the order and released on cancel; participant/order/listing/trip/type fields immutable on update. |
| 1.3.1 | 2026-09-25 | Echo keys (SIM-10): the example payloads' `deliveryId` (must equal `entityId`), `pickedUpAt`, `confirmedAt` and `updatedAt` are accepted; the timestamps are server-stamped and the client values ignored. Also unblocks the mobile dispute call, which echoes `deliveryId` and `updatedAt`. |

---

**Last Updated**: 2026-09-25
**Version**: 1.3.1
**Status**: Draft (Phase R4.2)
