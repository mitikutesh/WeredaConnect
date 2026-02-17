# Crop Listing API Contract

## Version
1.0.0

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
**Authentication**: Optional (public endpoint for browsing, auth required to see farmer contact)
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

**Note**: `farmerPhone` is only included if the requester is authenticated (to prevent spam).

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
| `farmerPhone` | string | Yes | E.164 format | Contact number (hidden from unauthenticated users) |
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
| `description` | string | No | 0-500 chars | Additional details |
| `status` | enum | Yes | See Status Types | Listing status |
| `harvestDate` | string (ISO 8601) | Yes | — | Date crop was harvested |
| `createdAt` | string (ISO 8601) | Yes | — | Timestamp when listing was created |
| `updatedAt` | string (ISO 8601) | Yes | — | Timestamp when listing was last updated |
| `deletedAt` | string (ISO 8601) | No | — | Timestamp when listing was soft-deleted |
| `version` | number | Yes | ≥1 | Optimistic concurrency control version |

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
   - Coordinates optional (GPS may not work in remote areas)
   - If coordinates provided, must be within Ethiopian bounds (3-15°N, 33-48°E)
5. **Harvest Date**:
   - Cannot be in the future
   - Cannot be more than 1 year in the past
6. **Description**:
   - Maximum 500 characters
   - Allow Amharic, Afan Oromo, English characters
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
- **GET /crops/{id}**: Public, but `farmerPhone` only shown to authenticated users

### Authorization
- Farmers can only create/update/delete their own listings
- Server validates `farmerId` matches JWT `sub` claim

### Rate Limiting
- **POST /sync**: 100 requests/second per user (API Gateway throttling)
- **GET /crops**: 200 requests/second per IP (CloudFront caching reduces load)

### Data Privacy
- Phone numbers hidden from unauthenticated users (prevent spam)
- GPS coordinates optional (farmers may not want exact farm location public)

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

---

**Last Updated**: 2026-02-15
**Version**: 1.0.0
**Status**: Implemented (Phase R3)
