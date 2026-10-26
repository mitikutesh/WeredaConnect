# Data Models Contract

**Version**: 1.1.0
**Last Updated**: 2026-10-26
**Status**: IMPLEMENTED
**Owner**: @Mobile

## Purpose

This document defines the core domain models for WeredaConnect, including their schema definitions, indexes, relationships, and design decisions. These models form the foundation of the offline-first architecture.

---

## Overview

WeredaConnect uses **Isar** (NoSQL embedded database) for mobile offline storage and **DynamoDB** for server-side persistence. All models are designed to:

1. Support offline-first workflows (create/update/query without connectivity)
2. Enable efficient sync with optimistic concurrency control (version fields)
3. Minimize data storage for low-end devices (<50MB total app size)
4. Support Ethiopian context (phone formats, woreda districts, local crop types)

---

## Core Entities

### 1. User

See `specs/contracts/user-entity.md` for the full User entity specification, including fields, DynamoDB schema, Isar schema, sync behavior, and invariants.

---

### 2. CropListing

**Mobile Schema**: `/mobile/lib/core/database/schemas/crop_listing.dart`

**Purpose**: Represents a crop listing posted by a farmer for sale.

**Fields**:

| Field | Type | Required | Description | Ethiopian Context |
|-------|------|----------|-------------|-------------------|
| `id` | Id | Yes | Isar auto-increment ID | N/A |
| `listingId` | String | Yes | Server-assigned UUID | Unique listing identifier |
| `farmerId` | String | Yes | User ID of farmer | Links to User.userId |
| `cropType` | String | Yes | Crop type (English name) | teff, wheat, barley, maize, chickpea, etc. |
| `quantity` | double | Yes | Quantity in quintals | Ethiopian standard unit (1 quintal = 100kg) |
| `pricePerKg` | double | Yes | Price per kilogram (ETB) | Ethiopian Birr currency |
| `location` | LatLng | Yes | Farm GPS coordinates | Latitude/longitude for truckers |
| `description` | String | Yes | Crop condition/quality | Supports Amharic/Oromo/English |
| `audioNoteRef` | String? | No | Voice note file reference | Format: `audio/{userId}/{listingId}.m4a` |
| `status` | ListingStatus | Yes | Enum: draft/active/sold/expired | Lifecycle state |
| `createdAt` | DateTime | Yes | UTC timestamp | Listing creation time |
| `updatedAt` | DateTime | Yes | UTC timestamp | Last edit time |
| `version` | int | Yes | Optimistic concurrency control | Incremented on each update |

**Embedded Objects**:
- `LatLng` - `{latitude: double, longitude: double}` (Ethiopian range: 3-15°N, 33-48°E)

**Indexes**:
- `listingId` (unique) - Fast lookup
- `farmerId` - Query all listings by a farmer
- `status` - Filter active/sold/expired listings
- `cropType` - Search by crop type
- `createdAt` - Sort by recency

**Design Decisions**:

1. **Quintals instead of kg**: Ethiopian farmers universally use quintals (100kg) for bulk crop measurement. Mobile UI will display in quintals but store precise values.

2. **audioNoteRef for low-literacy users**: Many rural farmers have low literacy. Voice notes in Amharic/Oromo allow richer descriptions without typing.

3. **Draft status**: Farmers can create listings offline and finalize later. Draft listings are not visible to truckers until status=active.

4. **Embedded LatLng**: GPS coordinates are stored directly (not a separate collection) for simplicity. Reduces query complexity.

5. **No buyer ID in listing**: Crop listings are visible to buyers (and admins); farmers see only their own. Truckers do not browse crops (F-36). Buyer is assigned during delivery/payment phase.

---

### 3. Trip

**Mobile Schema**: `/mobile/lib/core/database/schemas/trip.dart`

**Purpose**: Represents a transport trip posted by a trucker offering cargo capacity.

**Fields**:

| Field | Type | Required | Description | Ethiopian Context |
|-------|------|----------|-------------|-------------------|
| `id` | Id | Yes | Isar auto-increment ID | N/A |
| `tripId` | String | Yes | Server-assigned UUID | Unique trip identifier |
| `truckerId` | String | Yes | User ID of trucker | Links to User.userId |
| `fromWoreda` | String | Yes | Origin district | Ethiopian woreda name |
| `toWoreda` | String | Yes | Destination district | Typically rural → urban route |
| `departureDate` | DateTime | Yes | Scheduled departure (UTC) | Truckers plan 1-3 days ahead |
| `capacity` | double | Yes | Available capacity (quintals) | Typical truck: 100-200 quintals |
| `pricePerQuintal` | double | Yes | Transport price (ETB/quintal) | Market rate varies by distance |
| `status` | TripStatus | Yes | Wire values: `open`, `matched`, `inTransit`, `delivered`, `cancelled` (camelCase on the wire, unlike order statuses) | Lifecycle state; `matched` means fully booked since trip-listing-api 2.0.0 |
| `matchedListings` | List<String> | Yes | Crop listing IDs | Empty when open, populated when matched |
| `bookedQuintals` | double? | No | Quintals held by funded and accepted orders (trip-listing-api 2.0.0) | Server-written; null on older rows |
| `remainingQuintals` | double? | No | `capacity - bookedQuintals`; null on older rows, read as `capacity` | The free room buyers can order |
| `bookedOrderIds` | List<String> | No | Orders holding capacity (server row only, not in `TripDto`) | Released when the order leaves the trip |
| `acceptedOrderIds` | List<String> | No | Orders the trucker accepted (server row only) | Confirmed at the trucker's accept |
| `createdAt` | DateTime | Yes | UTC timestamp | Trip posted time |
| `updatedAt` | DateTime | Yes | UTC timestamp | Last update time |
| `version` | int | Yes | Optimistic concurrency control | Incremented on each update |

**Indexes**:
- `tripId` (unique) - Fast lookup
- `truckerId` - Query all trips by a trucker
- `status` - Filter open/matched trips
- `departureDate` - Sort by upcoming trips

**Design Decisions**:

1. **Woreda-to-woreda routing**: Ethiopian users navigate by districts, not GPS routes. Simplifies UX and matches mental model.

2. **departureDate planning**: Truckers typically plan trips in advance to maximize load. Farmers can match to upcoming trips.

3. **matchedListings array**: Stores IDs of matched crop listings. Allows one trip to carry multiple farmers' crops (common scenario).

4. **Capacity in quintals**: Aligns with crop quantity units. Easy for truckers to calculate: "I have 150 quintal capacity, can take 3 farmers with 50 quintals each."

5. **No GPS tracking during transit**: MVP focuses on pickup/delivery proof (in Delivery entity). Real-time GPS tracking is a future feature (Phase R6).

---

### 4. Delivery

**Mobile Schema**: `/mobile/lib/core/database/schemas/delivery.dart`

**Purpose**: Tracks the physical handoff of crops from farmer → trucker → buyer.

**Fields**:

| Field | Type | Required | Description | Ethiopian Context |
|-------|------|----------|-------------|-------------------|
| `id` | Id | Yes | Isar auto-increment ID | N/A |
| `deliveryId` | String | Yes | Server-assigned UUID | Unique delivery identifier |
| `tripId` | String | Yes | Associated trip ID | Links to Trip.tripId |
| `orderId` | String? | No | The order this delivery fulfils | Links to Order.orderId; required on create since delivery-verification-api 1.2.0, exposed on list/detail and stored locally since 1.4.2 (SIM-14); null only for pre-1.2.0 deliveries |
| `listingId` | String | Yes | Associated crop listing ID | Links to CropListing.listingId |
| `qrToken` | String | Yes | QR code verification token | Scanned at pickup/dropoff |
| `gpsPickup` | LatLng? | No | GPS at pickup location | Null until trucker confirms pickup |
| `gpsDropoff` | LatLng? | No | GPS at dropoff location | Null until trucker confirms delivery |
| `photoEvidenceRef` | String? | No | Photo proof file reference | Format: `photos/{deliveryId}/{timestamp}.jpg` |
| `status` | DeliveryStatus | Yes | Enum: pending/pickedUp/inTransit/delivered/disputed | Lifecycle state |
| `confirmedAt` | DateTime? | No | Delivery confirmation time (UTC) | Null until buyer confirms receipt |
| `version` | int | Yes | Optimistic concurrency control | Incremented on each update |

**Indexes**:
- `deliveryId` (unique) - Fast lookup
- `tripId` - Query all deliveries for a trip
- `listingId` - Query delivery for a listing
- `status` - Filter pending/disputed deliveries

**Design Decisions**:

1. **QR token for low-literacy verification**: Farmers/buyers scan QR codes instead of typing IDs. Reduces human error.

2. **GPS proof for trust**: Ethiopian rural markets have low trust. GPS timestamps prove physical location at pickup/delivery. Helps resolve disputes.

3. **Photo evidence optional but recommended**: Captures crop condition. Important for quality disputes but not mandatory (slow 3G upload).

4. **Nullable GPS fields**: GPS may not be available at time of pickup (indoor location, poor signal). System degrades gracefully.

5. **Separate from Trip**: One trip can have multiple deliveries (one per farmer). Each delivery tracks independently for fine-grained status.

---

### 5. Payment

**Mobile Schema**: `/mobile/lib/core/database/schemas/payment.dart`

**Purpose**: Represents a payment transaction in the Telebirr escrow system.

**Fields**:

| Field | Type | Required | Description | Ethiopian Context |
|-------|------|----------|-------------|-------------------|
| `id` | Id | Yes | Isar auto-increment ID | N/A |
| `paymentId` | String | Yes | Server-assigned UUID | Unique payment identifier |
| `deliveryId` | String | Yes | Associated delivery ID | Links to Delivery.deliveryId |
| `farmerId` | String | Yes | User ID receiving payment | Links to User.userId |
| `buyerId` | String | Yes | User ID making payment | Links to User.userId |
| `amount` | double | Yes | Payment amount (ETB) | Crop cost + transport cost |
| `telebirrRef` | String? | No | Telebirr transaction reference | Null until Telebirr API call succeeds |
| `status` | PaymentStatus | Yes | Enum: escrowed/released/disputed/refunded | Lifecycle state |
| `createdAt` | DateTime | Yes | UTC timestamp | Escrow created time |
| `version` | int | Yes | Optimistic concurrency control | Incremented on each update |

**Indexes**:
- `paymentId` (unique) - Fast lookup
- `deliveryId` (unique, effectively) - One payment per delivery
- `status` - Filter escrowed/disputed payments

**Design Decisions**:

1. **Telebirr integration**: Telebirr is the dominant mobile money platform in Ethiopia (government-backed). Higher trust than private payment systems.

2. **Escrow for farmer protection**: Farmers often face non-payment issues. Escrow holds buyer's money until delivery is confirmed.

3. **Amount calculation**: `amount = (quantity * pricePerKg) + (quantity * pricePerQuintal / 100)`. Mobile app calculates automatically to prevent disputes.

4. **Nullable telebirrRef**: Payment object is created when delivery is matched (status=escrowed), but Telebirr API is called asynchronously. Reference is populated after API success.

5. **No trucker payment in MVP**: Trucker payment is handled separately (future feature). This payment focuses on farmer ↔ buyer transaction.

---

### 6. SyncOperation

**Mobile Schema**: `/mobile/lib/core/database/schemas/sync_operation.dart`

**Purpose**: Queues local changes for syncing to server when connectivity is available.

**Fields**:

| Field | Type | Required | Description | Offline-First Pattern |
|-------|------|----------|-------------|-----------------------|
| `id` | Id | Yes | Isar auto-increment ID | N/A |
| `operationId` | String | Yes | Server-assigned UUID | Unique operation identifier |
| `idempotencyKey` | String | Yes | Idempotency key | Format: `{userId}:{entityType}:{entityId}:{action}:{timestamp}` |
| `entityType` | String | Yes | Entity type | Values: user, crop_listing, trip, delivery, payment |
| `entityId` | String | Yes | ID of entity being synced | References primary entity ID |
| `action` | String | Yes | CRUD action | Values: create, update, delete |
| `data` | String | Yes | Serialized JSON payload | Full entity data as JSON string |
| `status` | SyncStatus | Yes | Enum: pending/syncing/synced/failed | Sync lifecycle state |
| `retryCount` | int | Yes | Number of retry attempts | Max 3 retries before marking as failed |
| `createdAt` | DateTime | Yes | UTC timestamp | When operation was queued |

**Indexes**:
- `operationId` (unique) - Fast lookup
- `idempotencyKey` (unique) - Prevent duplicate operations on retry
- `status` - Query pending/failed operations
- `entityType` - Filter operations by entity type

**Design Decisions**:

1. **Idempotency key prevents duplicates**: If sync fails midway and retries, server uses idempotency key to detect duplicate operations. Format: `usr123:crop_listing:123:update:1234567890`.

2. **JSON string storage**: Isar doesn't support complex nested objects efficiently. Storing JSON as string is simpler and allows flexible schema evolution.

3. **Retry with exponential backoff**: Delay formula: `2^retryCount` seconds. Prevents hammering server during outages. After 3 failures, requires manual intervention.

4. **status=synced operations are purged after 30 days**: Synced operations are retained (not deleted immediately) and purged in bulk once older than the 30-day threshold (`SyncQueue.purgeSyncedOperations`) to prevent database bloat while keeping a recent audit trail.

5. **No TTL on failed operations**: Failed operations stay in queue indefinitely until user manually resolves (e.g., conflict resolution UI). Prevents silent data loss.

---

### 7. Order

**Server DTO**: `/server/src/Models/OrderDtos.cs` (`OrderDto`)
**Mobile Schema**: `/mobile/lib/core/database/schemas/order.dart` (web mirror in `core/models/web/`)
**Contract**: `specs/contracts/order-lifecycle.md` (statuses, transitions, who sets what)

**Purpose**: A buyer's purchase of a crop listing, optionally carried by a trip. Owns the escrow leg (`escrow-order-{orderId}`) and the commitments of the farmer and the trucker.

**Fields** (wire names; every timestamp is ISO 8601 UTC; `quantity` is kilograms, unlike listing stock and trip capacity):

| Field | Type | Required | Description | Set by |
|-------|------|----------|-------------|--------|
| `orderId` | String | Yes | UUID v4 | client at create |
| `listingId` | String | Yes | The crop listing | client; immutable |
| `buyerId`, `buyerName` | String | Yes | The buyer | client; immutable |
| `farmerId`, `farmerName` | String | Yes | The listing's farmer | client; `farmerName` overwritten from the listing; immutable |
| `cropType` | String | Yes | Must match the listing | client; immutable |
| `quantity` | double | Yes | Kilograms, 0.01 to 1,000,000 | client; immutable |
| `pricePerUnit` | double | Yes | ETB per kg, equal to the listing's `pricePerKg` | client; immutable |
| `cropCost` | double | Yes | `quantity × pricePerUnit` | server |
| `tripId`, `truckerId`, `truckerName` | String? | No | The trip and its trucker; all present or all absent | client; `truckerName` from the trip; immutable |
| `transportCost` | double? | No | `quantity / 100 × pricePerQuintal`; 0 without a trip | server |
| `commissionRate`, `commissionAmount` | double | Yes | Rate from SSM (default 0.05); `cropCost × rate`, never on transport | server |
| `orderTotal` | double | Yes | `cropCost + transportCost + commissionAmount`; what the buyer pays | server |
| `paymentMethod` | String? | No | `telebirr` or `cbe_transfer` | buyer on `payment_submitted` |
| `cbeTransactionId` | String? | No | Bank reference (demo builds) | buyer |
| `paymentVerifiedAt` | String? | No | When the money was confirmed held (`→ funded`) | server |
| `status` | String | Yes | snake_case wire status (`order-lifecycle.md`) | per the actor rules |
| `createdAt`, `updatedAt` | String | Yes | | server |
| `version` | int | Yes | Optimistic concurrency; clients send the pre-increment value | server |
| `respondedAt` | String? | No | The farmer's answer | server |
| `rejectionReason` | String? | No | The farmer's text on a reject | farmer |
| `cancelledAt` | String? | No | | server |
| `cancelReason` | String? | No | The buyer's text, or a server code (`buyer_withdrew`, `transport_failed_buyer_cancelled`, `farmer_cancelled`, `farmer_response_expired`, `transport_not_arranged`, `escrow_refunded`, `account_deleted`) | buyer or server |
| `pickupDeadlineAt` | String? | No | When an accepted order nobody picked up counts as a transport failure (order-lifecycle 1.15.0; on a trip order stamped at the trucker's accept since 2.0.0) | server |
| `adminQueueReason`, `adminQueuedAt` | String? | No | The admin stuck-order queue (`hold_cap`) | server |
| `contactOpen` | bool | Yes | Whether the parties may reach each other on this order (computed on every read) | server |
| `buyerPhone`, `farmerPhone`, `truckerPhone` | String? | No | E.164, only to a party of an order whose contact is open (the trucker's only once the trucker accepted, order-lifecycle 2.0.0) | server, at read time |
| `fulfillment` | String? | No | `trucker` or `self_pickup` (order-lifecycle 2.0.0) | server, at funding |
| `pickupDate` | String? | No | The trip's departure (trip orders, 2.0.0) | server, at funding |
| `farmerResponse` | String? | No | `pending`, `accepted`, `declined`, `expired` (2.0.0) | server |
| `farmerRespondedAt`, `farmerDeadlineAt` | String? | No | The farmer's answer and its deadline (2.0.0) | server |
| `truckerResponse` | String? | No | `none`, `not_asked`, `pending`, `accepted`, `declined`, `expired`, `cancelled`, `failed_capacity` (2.0.0) | server |
| `truckerAskedAt`, `truckerRespondedAt`, `truckerDeadlineAt` | String? | No | When the trucker was asked, answered, and had to answer by (2.0.0) | server |
| `buyerChoiceDeadlineAt` | String? | No | The buyer's 48 hour window in `awaiting_transport` (2.0.0) | server |
| `transportFailure` | String? | No | `trucker_declined`, `trucker_expired`, `trucker_cancelled`, `trip_cancelled`, `trip_full` (2.0.0) | server |
| `declineReason` | String? | No | `date`, `capacity`, `price`, `route`, `crop_not_ready`, `other` (2.0.0) | farmer or trucker, with a `decline` command |
| `cancelledBy` | String? | No | `buyer`, `farmer`, `trucker`, `system` (2.0.0) | server |
| `faultParty` | String? | No | `farmer`, `trucker`, `buyer`, `none` (2.0.0) | server |
| `farmerReminderAt`, `truckerReminderAt`, `buyerChoiceReminderAt` | String? | No | Reminder markers 6 hours before a deadline (2.0.0) | order cron |
| `pickupOverdueNotifiedAt` | String? | No | Stamped when `pickupDeadlineAt` passed on an accepted order (2.0.0) | order cron |

Stored on the row but never returned: `paymentSubmittedAt`, `pendingPaymentAt`, `paymentHoldUntil`, the Telebirr references (`telebirrMerchOrderId`, `telebirrPaymentOrderId`, `telebirrTransId`), `cancelNote`, `adminQueue`, `adminQueueEscrowId`, `moneySweepSkip`, `moneySweepSkipAt` and the admin override flags (`refundOverride*`, `disputeOverride*`). Clients may send only `status` and its companions (`rejectionReason`, `cancelReason`, `paymentMethod`, `cbeTransactionId`) and, since order-lifecycle 2.0.0, the commands `farmerResponse` / `truckerResponse` (`accept` or `decline`) with `declineReason`; everything else above is dropped or refused (`order-lifecycle.md`, Order Update Rules).

**Indexes (DynamoDB)**: `orderId` (PK); `StatusCreatedAtIndex` (the crons page it by status); the party indexes behind `GET /orders` (`buyerId`, `farmerId`, `truckerId`); the sparse `AdminQueueIndex`.

**Design Decisions**:

1. **Money is computed by the server** from the listing and the trip at create; client previews use the same formula (`order_pricing.dart`).

2. **Kilograms on the wire**: `quantity` is kg while stock and capacity are quintals; every conversion divides or multiplies by 100.

3. **Sequential commitments** (order-lifecycle 2.0.0): the farmer is asked first and the trucker only after the farmer accepts, so a trucker never commits to an order the farmer may still decline. The responses, deadlines and failure codes live on the order; the status alone no longer says who committed.

4. **Server-only fields**: everything a client could use to skip a step (deadlines, responses, money, Telebirr references) is dropped from payloads and stamped by the server.

---

## Relationships

```
User (farmer) ──1:N── CropListing
User (trucker) ──1:N── Trip
Trip ──1:N── Delivery
User (buyer) ──1:N── Order
CropListing ──1:N── Order
Trip ──1:N── Order
Order ──1:1── Delivery
CropListing ──1:1── Delivery
Delivery ──1:1── Payment
User (farmer) ──1:N── Payment (as recipient)
User (buyer) ──1:N── Payment (as payer)

SyncOperation ──references── All entities (via entityType + entityId)
```

**Notes**:
- No foreign key constraints in Isar (NoSQL). Relationships are enforced at application layer.
- Server-side DynamoDB uses composite keys (PK + SK) for relationships.

---

## Sync Protocol

All entities except `SyncOperation` follow the same sync pattern:

1. **Client creates/updates entity offline**:
   - Save to local Isar database
   - Enqueue `SyncOperation` with entity data and idempotency key

2. **Connectivity detected**:
   - Sync engine processes queue (oldest first)
   - POST to `/api/sync` with batch of operations
   - Server checks idempotency keys to prevent duplicates

3. **Conflict resolution** (if client version < server version):
   - Server returns conflict with server version
   - Client presents conflict resolution UI (future feature)
   - User chooses: keep local, keep server, or merge

4. **Success**:
   - Delete `SyncOperation` from queue
   - Update local entity with server version

See `specs/contracts/sync-protocol.md` for detailed API specification.

---

## Storage Constraints

**Target devices**: 2GB RAM, 16GB storage Android phones

**Max database size**: 50MB (includes all entities + photos/audio)

**Per-entity estimates**:
- User: ~500 bytes
- CropListing: ~1 KB (with description)
- Trip: ~800 bytes
- Delivery: ~600 bytes
- Payment: ~400 bytes
- SyncOperation: ~2 KB (includes JSON data)

**Capacity planning**:
- 10,000 users: ~5 MB
- 50,000 crop listings: ~50 MB (LIMIT REACHED)
- Strategy: Expire old listings after 30 days, clear synced operations immediately

---

## Ethiopian Context Summary

| Feature | Ethiopian Adaptation |
|---------|---------------------|
| **Phone format** | +251 country code, E.164 format |
| **Location** | Woreda (district) names instead of GPS for UX |
| **Measurement** | Quintals (100kg) for listing stock and trip capacity; `order.quantity` travels in kilograms and `pricePerUnit` is ETB/kg (see `order-lifecycle.md`, Units) |
| **Currency** | Ethiopian Birr (ETB) |
| **Languages** | Amharic, Afan Oromo, English (Unicode support) |
| **Payment** | Telebirr (dominant mobile money platform) |
| **Crops** | Teff, wheat, barley, maize, chickpea, lentils |
| **Routes** | Rural woreda → urban markets (Addis Ababa, Adama, etc.) |
| **Trust** | Low trust environment → GPS proof, QR codes, escrow |
| **Literacy** | Voice notes for low-literacy farmers |

---

## Version History

| Version | Date | Changes | Author |
|---------|------|---------|--------|
| 1.0.0 | 2026-02-14 | Initial schema definitions for all 6 core entities | @Mobile |
| 1.0.1 | 2026-09-25 | Documented the order quantity unit: kilograms on the wire vs quintal stock and capacity (SIM-07) | @Server |
| 1.0.2 | 2026-09-25 | Delivery gains `orderId` (server DTO, Isar schema, web mirror) so clients can tie a delivery to its order (SIM-14) | @Server |
| 1.1.0 | 2026-10-26 | Order entity table added (every `OrderDto` field plus the ESC-15 commitment fields: `fulfillment`, `pickupDate`, `farmerResponse`, `truckerResponse`, their deadlines and reminders, `transportFailure`, `declineReason`, `cancelledBy`, `faultParty`); Trip status casing fixed to the wire values (`open`, `matched`, `inTransit`, `delivered`, `cancelled`) and the booking fields `bookedQuintals`, `remainingQuintals`, `bookedOrderIds`, `acceptedOrderIds` added; relationships gain Order (`order-lifecycle.md` 2.0.0, `trip-listing-api.md` 2.0.0) | @Server |

---

## Next Steps

1. **Server-side DynamoDB schemas**: Create matching entity definitions in `server/src/Models/` with DynamoDB annotations.

2. **Add missing DynamoDB tables** (Task B4):
   - Deliveries table (PK: deliveryId, SK: tripId)
   - Payments table (PK: paymentId, SK: deliveryId)
   - SyncMetadata table (PK: idempotencyKey, SK: timestamp)

3. **Add Global Secondary Indexes**:
   - CropsTable: `StatusCreatedAtIndex` (query active listings sorted by recency)
   - TripsTable: `StatusDepartureDateIndex` (query upcoming trips)

4. **Implement sync engine** (F-08):
   - SyncQueue class (enqueue operations)
   - ConnectivityMonitor (detect online/offline)
   - SyncExecutor (process queue with retry logic)

---

**Document Status**: ✅ COMPLETE
**Implementation Status**: ✅ Mobile schemas implemented and tested
**Blocked By**: None
**Blocks**: F-08 (Sync Engine), F-10 (Crop Listing UI)
