# API Reference

## Backend API Documentation

Complete reference for the WeredaConnect REST API. WeredaConnect is an offline-first agritech logistics platform connecting Ethiopian farmers, transport providers, and urban wholesalers.

## Audience
- Mobile developers
- Integration partners
- API consumers

## Base URL

- **Development**: `https://dev-api.weredaconnect.com`
- **Staging**: `https://staging-api.weredaconnect.com`
- **Production**: `https://api.weredaconnect.com`

---

## Authentication

### Bearer Token

All API requests (except `/auth/login` and `/auth/register`) require authentication via JWT bearer token.

```http
Authorization: Bearer <token>
```

---

## Auth API

### Login

```http
POST /auth/login
Content-Type: application/json

{
  "phoneNumber": "+251912345678",
  "password": "your-password"
}
```

**Response** (200 OK):
```json
{
  "token": "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9...",
  "refreshToken": "d4f8a2c1-9b3e-4f7a-b5c6-1234abcd5678",
  "expiresIn": 3600,
  "user": {
    "id": "usr-a1b2c3d4",
    "name": "Abebe Kebede",
    "phoneNumber": "+251912345678",
    "role": "farmer"
  }
}
```

### Register

```http
POST /auth/register
Content-Type: application/json

{
  "phoneNumber": "+251912345678",
  "password": "secure-password",
  "name": "Abebe Kebede",
  "faydaId": "FID-1234-5678-9012",
  "role": "farmer",
  "location": {
    "latitude": 9.0320,
    "longitude": 38.7469,
    "woreda": "Bako Tibe",
    "zone": "West Shewa",
    "region": "Oromia"
  },
  "preferredLanguage": "am"
}
```

**Field Rules:**
- `faydaId` -- Ethiopia's national digital ID; used for identity verification
- `role` -- one of `farmer`, `transporter`, `buyer`
- `preferredLanguage` -- one of `am` (Amharic), `om` (Afan Oromo), `en` (English)

**Response** (201 Created):
```json
{
  "token": "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9...",
  "refreshToken": "e5g9b3d2-0c4f-5g8b-c6d7-2345bcde6789",
  "expiresIn": 3600,
  "user": {
    "id": "usr-e5f6g7h8",
    "name": "Abebe Kebede",
    "phoneNumber": "+251912345678",
    "role": "farmer",
    "faydaId": "FID-1234-5678-9012",
    "location": {
      "latitude": 9.0320,
      "longitude": 38.7469,
      "woreda": "Bako Tibe",
      "zone": "West Shewa",
      "region": "Oromia"
    },
    "preferredLanguage": "am",
    "createdAt": "2026-02-09T10:00:00Z"
  }
}
```

### Refresh Token

```http
POST /auth/refresh
Content-Type: application/json

{
  "refreshToken": "d4f8a2c1-9b3e-4f7a-b5c6-1234abcd5678"
}
```

**Response** (200 OK):
```json
{
  "token": "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9...",
  "refreshToken": "f6h0c4e3-1d5g-6h9c-d7e8-3456cdef7890",
  "expiresIn": 3600
}
```

---

## Listings API

Crop listings allow farmers to advertise their produce to buyers and transporters.

### Create Listing

```http
POST /listings
Authorization: Bearer <token>
Content-Type: application/json

{
  "cropType": "teff",
  "variety": "magna",
  "quantity": 500,
  "unit": "kg",
  "pricePerUnit": 85.00,
  "currency": "ETB",
  "description": "Fresh teff from Bako Tibe, harvested this week",
  "harvestDate": "2026-02-05",
  "location": {
    "latitude": 9.0320,
    "longitude": 38.7469,
    "woreda": "Bako Tibe",
    "zone": "West Shewa",
    "region": "Oromia"
  },
  "photos": ["media-id-001", "media-id-002"],
  "availableUntil": "2026-02-28T23:59:59Z",
  "idempotencyKey": "550e8400-e29b-41d4-a716-446655440000"
}
```

**Field Rules:**
- `cropType` -- one of `teff`, `wheat`, `maize`, `sorghum`, `barley`, `coffee`, `sesame`, `chickpea`, `lentil`, `enset`, `chat`, `other`
- `unit` -- one of `kg`, `quintal` (100kg), `ton`
- `currency` -- always `ETB` (Ethiopian Birr)
- `idempotencyKey` -- UUIDv4, client-generated at time of user action

**Response** (201 Created):
```json
{
  "id": "lst-a1b2c3d4",
  "farmerId": "usr-e5f6g7h8",
  "cropType": "teff",
  "variety": "magna",
  "quantity": 500,
  "unit": "kg",
  "pricePerUnit": 85.00,
  "currency": "ETB",
  "description": "Fresh teff from Bako Tibe, harvested this week",
  "harvestDate": "2026-02-05",
  "location": {
    "latitude": 9.0320,
    "longitude": 38.7469,
    "woreda": "Bako Tibe",
    "zone": "West Shewa",
    "region": "Oromia"
  },
  "photos": [
    {
      "id": "media-id-001",
      "url": "https://cdn.weredaconnect.com/media/media-id-001.jpg",
      "thumbnail": "https://cdn.weredaconnect.com/media/media-id-001-thumb.jpg"
    },
    {
      "id": "media-id-002",
      "url": "https://cdn.weredaconnect.com/media/media-id-002.jpg",
      "thumbnail": "https://cdn.weredaconnect.com/media/media-id-002-thumb.jpg"
    }
  ],
  "status": "active",
  "availableUntil": "2026-02-28T23:59:59Z",
  "createdAt": "2026-02-09T10:30:00Z",
  "updatedAt": "2026-02-09T10:30:00Z",
  "version": 1
}
```

### Browse Listings

```http
GET /listings?cropType=teff&location=9.0320,38.7469&radius=50&priceRange=50-100&limit=20&offset=0
Authorization: Bearer <token>
```

**Query Parameters**:
- `cropType` (optional): Filter by crop type (e.g., `teff`, `wheat`, `coffee`)
- `location` (optional): Latitude,longitude for proximity search
- `radius` (optional): Search radius in kilometers (default: 25, max: 200)
- `priceRange` (optional): Min-max price per unit in ETB (e.g., `50-100`)
- `farmerId` (optional): Filter by specific farmer
- `status` (optional): `active`, `sold`, `expired` (default: `active`)
- `limit` (optional): Results per page (default: 20, max: 100)
- `offset` (optional): Pagination offset (default: 0)
- `sortBy` (optional): `recent`, `nearby`, `price_asc`, `price_desc` (default: `recent`)

**Response** (200 OK):
```json
{
  "listings": [
    {
      "id": "lst-a1b2c3d4",
      "farmerId": "usr-e5f6g7h8",
      "farmerName": "Abebe Kebede",
      "cropType": "teff",
      "variety": "magna",
      "quantity": 500,
      "unit": "kg",
      "pricePerUnit": 85.00,
      "currency": "ETB",
      "location": {
        "latitude": 9.0320,
        "longitude": 38.7469,
        "woreda": "Bako Tibe"
      },
      "distanceKm": 12.5,
      "photos": ["https://cdn.weredaconnect.com/media/media-id-001-thumb.jpg"],
      "status": "active",
      "availableUntil": "2026-02-28T23:59:59Z",
      "createdAt": "2026-02-09T10:30:00Z"
    }
  ],
  "total": 87,
  "limit": 20,
  "offset": 0,
  "hasMore": true
}
```

### Get Listing by ID

```http
GET /listings/{id}
Authorization: Bearer <token>
```

**Response** (200 OK): Same structure as Create Listing response.

### Update Listing

```http
PUT /listings/{id}
Authorization: Bearer <token>
Content-Type: application/json

{
  "quantity": 400,
  "pricePerUnit": 90.00,
  "description": "Fresh teff, 100kg already sold",
  "version": 1
}
```

**Field Rules:**
- `version` -- required; must match current server version for optimistic concurrency

**Response** (200 OK): Updated listing object with incremented `version`.

### Delete Listing

```http
DELETE /listings/{id}
Authorization: Bearer <token>
```

**Response** (204 No Content)

---

## Transport API

Transport trips connect farmers with available trucks and drivers to move produce from rural areas to urban markets.

### Create Trip

```http
POST /trips
Authorization: Bearer <token>
Content-Type: application/json

{
  "listingId": "lst-a1b2c3d4",
  "origin": {
    "latitude": 9.0320,
    "longitude": 38.7469,
    "woreda": "Bako Tibe",
    "zone": "West Shewa",
    "region": "Oromia",
    "address": "Bako Tibe grain collection point"
  },
  "destination": {
    "latitude": 9.0054,
    "longitude": 38.7636,
    "woreda": "Arada",
    "zone": "Addis Ababa",
    "region": "Addis Ababa",
    "address": "Ehil Berenda (Grain Market)"
  },
  "cargoType": "teff",
  "cargoWeightKg": 500,
  "requestedPickupDate": "2026-02-12T06:00:00Z",
  "offeredPriceETB": 3500.00,
  "notes": "Needs to arrive before market opens at noon",
  "idempotencyKey": "660f9511-f39c-52e5-b827-557766551111"
}
```

**Response** (201 Created):
```json
{
  "id": "trp-b2c3d4e5",
  "requesterId": "usr-e5f6g7h8",
  "listingId": "lst-a1b2c3d4",
  "origin": {
    "latitude": 9.0320,
    "longitude": 38.7469,
    "woreda": "Bako Tibe",
    "zone": "West Shewa",
    "region": "Oromia",
    "address": "Bako Tibe grain collection point"
  },
  "destination": {
    "latitude": 9.0054,
    "longitude": 38.7636,
    "woreda": "Arada",
    "zone": "Addis Ababa",
    "region": "Addis Ababa",
    "address": "Ehil Berenda (Grain Market)"
  },
  "cargoType": "teff",
  "cargoWeightKg": 500,
  "requestedPickupDate": "2026-02-12T06:00:00Z",
  "offeredPriceETB": 3500.00,
  "notes": "Needs to arrive before market opens at noon",
  "status": "pending",
  "transporterId": null,
  "createdAt": "2026-02-09T11:00:00Z",
  "updatedAt": "2026-02-09T11:00:00Z",
  "version": 1
}
```

### Browse Trips

```http
GET /trips?status=pending&origin=9.0320,38.7469&radius=50&limit=20&offset=0
Authorization: Bearer <token>
```

**Query Parameters**:
- `status` (optional): `pending`, `accepted`, `in_transit`, `completed`, `cancelled` (default: `pending`)
- `origin` (optional): Latitude,longitude of origin for proximity search
- `destination` (optional): Latitude,longitude of destination
- `radius` (optional): Search radius in kilometers (default: 50, max: 200)
- `cargoType` (optional): Filter by cargo/crop type
- `requesterId` (optional): Filter by requester
- `transporterId` (optional): Filter by transporter
- `limit` (optional): Results per page (default: 20, max: 100)
- `offset` (optional): Pagination offset (default: 0)
- `sortBy` (optional): `recent`, `nearby`, `price_desc` (default: `recent`)

**Response** (200 OK):
```json
{
  "trips": [
    {
      "id": "trp-b2c3d4e5",
      "requesterId": "usr-e5f6g7h8",
      "requesterName": "Abebe Kebede",
      "origin": {
        "latitude": 9.0320,
        "longitude": 38.7469,
        "woreda": "Bako Tibe"
      },
      "destination": {
        "latitude": 9.0054,
        "longitude": 38.7636,
        "woreda": "Arada"
      },
      "cargoType": "teff",
      "cargoWeightKg": 500,
      "requestedPickupDate": "2026-02-12T06:00:00Z",
      "offeredPriceETB": 3500.00,
      "status": "pending",
      "createdAt": "2026-02-09T11:00:00Z"
    }
  ],
  "total": 34,
  "limit": 20,
  "offset": 0,
  "hasMore": true
}
```

### Get Trip by ID

```http
GET /trips/{id}
Authorization: Bearer <token>
```

**Response** (200 OK): Same structure as Create Trip response.

### Accept Trip

A transporter accepts an available trip.

```http
PUT /trips/{id}/accept
Authorization: Bearer <token>
Content-Type: application/json

{
  "estimatedPickupDate": "2026-02-12T07:00:00Z",
  "vehiclePlate": "AA-3-12345",
  "driverPhone": "+251922334455"
}
```

**Response** (200 OK):
```json
{
  "id": "trp-b2c3d4e5",
  "requesterId": "usr-e5f6g7h8",
  "transporterId": "usr-t1r2a3n4",
  "status": "accepted",
  "estimatedPickupDate": "2026-02-12T07:00:00Z",
  "vehiclePlate": "AA-3-12345",
  "driverPhone": "+251922334455",
  "escrowStatus": "pending",
  "updatedAt": "2026-02-09T12:00:00Z",
  "version": 2
}
```

### Complete Trip

Mark a trip as completed after delivery verification.

```http
PUT /trips/{id}/complete
Authorization: Bearer <token>
Content-Type: application/json

{
  "deliveryVerificationId": "dlv-c3d4e5f6"
}
```

**Response** (200 OK):
```json
{
  "id": "trp-b2c3d4e5",
  "status": "completed",
  "deliveryVerificationId": "dlv-c3d4e5f6",
  "completedAt": "2026-02-13T11:30:00Z",
  "escrowStatus": "released",
  "updatedAt": "2026-02-13T11:30:00Z",
  "version": 3
}
```

---

## Escrow API

Telebirr-based escrow ensures secure payment. Funds are locked when a trip is accepted and released only after verified delivery.

### Lock Funds

```http
POST /escrow/lock
Authorization: Bearer <token>
Content-Type: application/json

{
  "tripId": "trp-b2c3d4e5",
  "amountETB": 3500.00,
  "payerPhone": "+251912345678",
  "idempotencyKey": "770e9622-g40d-63f6-c938-668877662222"
}
```

**Response** (201 Created):
```json
{
  "escrowId": "esc-d4e5f6g7",
  "tripId": "trp-b2c3d4e5",
  "amountETB": 3500.00,
  "payerPhone": "+251912345678",
  "status": "locked",
  "telebirrRef": "TBR-2026-0209-001234",
  "lockedAt": "2026-02-09T12:05:00Z",
  "expiresAt": "2026-02-16T12:05:00Z"
}
```

### Release Funds

```http
POST /escrow/release
Authorization: Bearer <token>
Content-Type: application/json

{
  "tripId": "trp-b2c3d4e5",
  "deliveryVerificationId": "dlv-c3d4e5f6"
}
```

**Response** (200 OK):
```json
{
  "escrowId": "esc-d4e5f6g7",
  "tripId": "trp-b2c3d4e5",
  "amountETB": 3500.00,
  "status": "released",
  "recipientPhone": "+251922334455",
  "telebirrRef": "TBR-2026-0209-001234",
  "releasedAt": "2026-02-13T11:35:00Z"
}
```

### Get Escrow Status

```http
GET /escrow/{tripId}
Authorization: Bearer <token>
```

**Response** (200 OK):
```json
{
  "escrowId": "esc-d4e5f6g7",
  "tripId": "trp-b2c3d4e5",
  "amountETB": 3500.00,
  "payerPhone": "+251912345678",
  "recipientPhone": "+251922334455",
  "status": "locked",
  "telebirrRef": "TBR-2026-0209-001234",
  "lockedAt": "2026-02-09T12:05:00Z",
  "expiresAt": "2026-02-16T12:05:00Z",
  "releasedAt": null
}
```

**Escrow Statuses**: `pending`, `locked`, `released`, `disputed`, `expired`, `refunded`

---

## Delivery API

Delivery verification combines QR token scanning, GPS confirmation, and photo evidence to prove that goods were delivered.

### Verify Delivery

```http
POST /delivery/verify
Authorization: Bearer <token>
Content-Type: application/json

{
  "tripId": "trp-b2c3d4e5",
  "qrToken": "qr-abc123def456",
  "gpsLocation": {
    "latitude": 9.0054,
    "longitude": 38.7636,
    "accuracy": 15.0
  },
  "photoEvidence": ["media-id-101", "media-id-102"],
  "receiverConfirmation": true,
  "notes": "All 5 quintals delivered in good condition",
  "idempotencyKey": "880f0733-h51e-74g7-d049-779988773333"
}
```

**Field Rules:**
- `qrToken` -- generated when trip is accepted; scanned by receiver at delivery
- `gpsLocation.accuracy` -- meters; server validates proximity to destination
- `photoEvidence` -- media IDs from prior `/media/upload` calls
- `receiverConfirmation` -- buyer/receiver explicitly confirms receipt

**Response** (201 Created):
```json
{
  "id": "dlv-c3d4e5f6",
  "tripId": "trp-b2c3d4e5",
  "status": "verified",
  "qrTokenValid": true,
  "gpsProximityValid": true,
  "gpsDistanceMeters": 45.2,
  "photoEvidence": [
    {
      "id": "media-id-101",
      "url": "https://cdn.weredaconnect.com/media/media-id-101.jpg"
    },
    {
      "id": "media-id-102",
      "url": "https://cdn.weredaconnect.com/media/media-id-102.jpg"
    }
  ],
  "receiverConfirmation": true,
  "verifiedAt": "2026-02-13T11:30:00Z"
}
```

### Get Delivery Details

```http
GET /delivery/{tripId}
Authorization: Bearer <token>
```

**Response** (200 OK): Same structure as Verify Delivery response.

---

## Sync API

The sync endpoint enables offline-first operation. The mobile client queues all write operations locally (in Isar) and syncs them to the server when connectivity is available. The format below aligns with the sync protocol contract defined in `specs/contracts/sync-protocol.md`.

### Sync Data

```http
POST /sync
Authorization: Bearer <token>
Content-Type: application/json

{
  "syncBatchId": "550e8400-e29b-41d4-a716-446655440000",
  "clientId": "device-a1b2c3d4",
  "operations": [
    {
      "operationId": "660f9511-f39c-52e5-b827-557766551111",
      "idempotencyKey": "770e0622-g40d-63f6-c938-668877662222",
      "entityType": "listing",
      "entityId": "lst-local-001",
      "action": "create",
      "clientVersion": 0,
      "data": {
        "cropType": "teff",
        "variety": "magna",
        "quantity": 500,
        "unit": "kg",
        "pricePerUnit": 85.00,
        "currency": "ETB",
        "description": "Fresh teff from Bako Tibe",
        "location": {
          "latitude": 9.0320,
          "longitude": 38.7469,
          "woreda": "Bako Tibe"
        }
      },
      "timestamp": "2026-02-09T08:00:00Z"
    },
    {
      "operationId": "880f0733-h51e-74g7-d049-779988773333",
      "idempotencyKey": "990f1844-i62f-85h8-e150-880099884444",
      "entityType": "trip",
      "entityId": "trp-b2c3d4e5",
      "action": "update",
      "clientVersion": 2,
      "data": {
        "notes": "Changed pickup time to afternoon"
      },
      "timestamp": "2026-02-09T09:15:00Z"
    },
    {
      "operationId": "aa0f2955-j73g-96i9-f261-991100995555",
      "idempotencyKey": "bb0f3066-k84h-07j0-g372-002211006666",
      "entityType": "profile",
      "entityId": "usr-e5f6g7h8",
      "action": "update",
      "clientVersion": 1,
      "data": {
        "preferredLanguage": "om"
      },
      "timestamp": "2026-02-09T09:30:00Z"
    }
  ]
}
```

**Field Rules:**
- `syncBatchId` -- UUIDv4; unique per sync attempt; used for batch-level idempotency
- `clientId` -- device identifier; stable across app restarts
- `operationId` -- UUIDv4; unique identifier for each operation in the batch
- `idempotencyKey` -- UUIDv4; client-generated at the time of the user action, NOT at sync time; prevents duplicate processing on retry
- `entityType` -- one of `listing`, `trip`, `profile`
- `action` -- one of `create`, `update`, `delete`
- `clientVersion` -- the version the client had when making the change; `0` for new entities
- `timestamp` -- ISO 8601, UTC; used for display ordering, NOT for conflict resolution
- `data` -- entity-specific payload; only changed fields for updates (delta sync)

**Response** (200 OK):
```json
{
  "syncBatchId": "550e8400-e29b-41d4-a716-446655440000",
  "results": [
    {
      "operationId": "660f9511-f39c-52e5-b827-557766551111",
      "status": "success",
      "serverVersion": 1,
      "serverData": {
        "id": "lst-a1b2c3d4",
        "cropType": "teff",
        "variety": "magna",
        "quantity": 500,
        "unit": "kg",
        "pricePerUnit": 85.00,
        "status": "active",
        "createdAt": "2026-02-09T10:30:00Z",
        "version": 1
      },
      "message": null
    },
    {
      "operationId": "880f0733-h51e-74g7-d049-779988773333",
      "status": "conflict",
      "serverVersion": 3,
      "serverData": {
        "id": "trp-b2c3d4e5",
        "notes": "Pickup confirmed for morning",
        "status": "accepted",
        "updatedAt": "2026-02-09T09:00:00Z",
        "version": 3
      },
      "message": "Client version 2 is behind server version 3"
    },
    {
      "operationId": "aa0f2955-j73g-96i9-f261-991100995555",
      "status": "success",
      "serverVersion": 2,
      "serverData": {
        "id": "usr-e5f6g7h8",
        "preferredLanguage": "om",
        "updatedAt": "2026-02-09T10:30:05Z",
        "version": 2
      },
      "message": null
    }
  ]
}
```

### Sync Error Codes

| Code | Meaning | Client Action |
|------|---------|---------------|
| `SYNC_SUCCESS` | Operation applied | Remove from local queue |
| `SYNC_CONFLICT` | Client version behind server | Accept server data, re-queue if needed |
| `SYNC_DUPLICATE` | Idempotency key already processed | Remove from local queue (already done) |
| `SYNC_VALIDATION` | Payload failed validation | Fix data, re-queue with new idempotency key |
| `SYNC_AUTH` | Authentication failed | Re-authenticate, retry |
| `SYNC_RATE_LIMIT` | Too many requests | Back off, retry with exponential delay |
| `SYNC_SERVER_ERROR` | Server failure | Retry with exponential backoff |

### Conflict Resolution

- **Version wins, not timestamp.** The server compares `clientVersion` against its current version. Timestamps are informational only.
- **Server is authoritative.** On conflict, the server returns its state in `serverData`. The client must accept it or re-submit with the updated version.
- **Deletes are soft.** Deleted entities are marked with a `deletedAt` timestamp, not physically removed.
- **Idempotency keys expire after 7 days.** Clients should not retry operations older than 7 days.

---

## Media API

Upload crop photos, delivery evidence, and audio notes. Files are uploaded before being referenced in other API calls.

### Upload Media

```http
POST /media/upload
Authorization: Bearer <token>
Content-Type: multipart/form-data

file: <binary-data>
type: crop_photo | delivery_evidence | audio_note
```

**Field Rules:**
- `type` -- categorizes the media for storage and CDN optimization
- Accepted formats: JPEG, PNG, WebP for images; AAC, OGG for audio
- Max file size: 5MB for images, 2MB for audio
- Images are automatically compressed and thumbnailed server-side

**Response** (201 Created):
```json
{
  "id": "media-id-001",
  "url": "https://cdn.weredaconnect.com/media/media-id-001.jpg",
  "thumbnail": "https://cdn.weredaconnect.com/media/media-id-001-thumb.jpg",
  "type": "crop_photo",
  "size": 1024000,
  "width": 1920,
  "height": 1080,
  "createdAt": "2026-02-09T10:25:00Z"
}
```

---

## Error Responses

### 400 Bad Request

```json
{
  "error": "validation_error",
  "message": "Invalid request data",
  "details": [
    {
      "field": "cropType",
      "message": "cropType is required"
    },
    {
      "field": "quantity",
      "message": "quantity must be greater than 0"
    }
  ]
}
```

### 401 Unauthorized

```json
{
  "error": "unauthorized",
  "message": "Invalid or expired token"
}
```

### 409 Conflict

```json
{
  "error": "conflict",
  "message": "Version conflict detected",
  "serverVersion": 3,
  "serverData": {
    "id": "lst-a1b2c3d4",
    "quantity": 300,
    "pricePerUnit": 90.00,
    "version": 3
  }
}
```

### 429 Too Many Requests

```json
{
  "error": "rate_limit_exceeded",
  "message": "Too many requests",
  "retryAfter": 3600
}
```

### 500 Internal Server Error

```json
{
  "error": "internal_error",
  "message": "An unexpected error occurred",
  "requestId": "req-123-456"
}
```

---

## Rate Limiting

Rate limits are designed to accommodate offline-first sync patterns where clients may send large batches after periods without connectivity.

- **Standard endpoints**: 1000 requests per hour per user
- **Sync endpoint** (`POST /sync`): 60 requests per hour per device (batches can contain up to 100 operations each)
- **Media upload**: 100 uploads per hour per user

**Response Headers**:
- `X-RateLimit-Limit`: Maximum requests allowed
- `X-RateLimit-Remaining`: Requests remaining in current window
- `X-RateLimit-Reset`: Unix timestamp when the window resets

---

## Related Documentation

- **[Sync Protocol Contract](../../specs/contracts/sync-protocol.md)** - Authoritative sync protocol specification
- **[Data Flow](../architecture/data-flow.md)** - Sync and offline patterns
- **[Testing Guidelines](./testing-guidelines.md)** - API testing

---

**Last Updated**: 2026-02-09
**Version**: 1.0.0
**API Version**: v1
