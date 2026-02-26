# Crop Listing System Architecture

## Version
1.0.0 | 2026-02-21

## Status
Implemented (R3)

---

## 1. Component Overview

```
┌──────────────────────────────────────────────────────────────┐
│                       MOBILE (Flutter)                        │
│                                                               │
│  ┌──────────────────┐  ┌────────────────┐  ┌──────────────┐ │
│  │ CropListingBloc   │  │ CropListing    │  │   Isar DB    │ │
│  │                   │──│ Repository     │──│  (CropListing│ │
│  │ Events/States     │  │ (offline-first)│  │   schema)    │ │
│  └──────────────────┘  └───────┬────────┘  └──────────────┘ │
│                                │                              │
│                         ┌──────┴────────┐                     │
│                         │  SyncQueue    │                     │
│                         │  (enqueue     │                     │
│                         │   crop ops)   │                     │
│                         └──────┬────────┘                     │
└────────────────────────────────┼─────────────────────────────┘
                                 │ POST /sync (crop ops)
                                 │ GET /crops (browse)
                                 │ GET /crops/{id}
                                 ▼
┌──────────────────────────────────────────────────────────────┐
│                  API GATEWAY (HTTP API v2)                     │
│  ┌──────────────┐  ┌─────────────────────────────────┐       │
│  │ Cognito JWT   │  │ Routes:                         │       │
│  │ Authorizer    │  │  POST /sync → SyncFunction      │       │
│  │               │  │  GET /crops → CropFunction      │       │
│  │               │  │  GET /crops/{id} → CropFunction │       │
│  └──────────────┘  └─────────────────────────────────┘       │
└──────────────────────────────┬───────────────────────────────┘
                               │
                               ▼
┌──────────────────────────────────────────────────────────────┐
│                   SERVER (.NET 10 AOT)                         │
│                                                               │
│  ┌──────────────────┐  ┌───────────────────────┐             │
│  │ SyncFunction      │  │ CropFunction           │             │
│  │ (POST /sync)      │  │ (GET /crops,           │             │
│  │ crop entity       │  │  GET /crops/{id})       │             │
│  │ handler           │  │                         │             │
│  └────────┬─────────┘  └──────────┬──────────────┘             │
│           │                       │                            │
│           ▼                       ▼                            │
│  ┌────────────────────────────────────────┐                   │
│  │         ICropRepository                │                   │
│  │  (DynamoDB manual marshalling)         │                   │
│  └──────────────────┬─────────────────────┘                   │
│                     │                                          │
└─────────────────────┼──────────────────────────────────────────┘
                      ▼
┌──────────────────────────────────────────────────────────────┐
│                      DynamoDB                                 │
│  ┌───────────────────────────────┐                           │
│  │ CropListingsTable              │                           │
│  │ PK: listingId | SK: createdAt  │                           │
│  │                                │                           │
│  │ GSI: FarmerIdIndex             │                           │
│  │ GSI: StatusCropTypeIndex       │                           │
│  └───────────────────────────────┘                           │
│                                                               │
│  ┌───────────────────────────────┐                           │
│  │ SyncMetadataTable (shared)     │                           │
│  │ PK: idempotencyKey | TTL: 7d   │                           │
│  └───────────────────────────────┘                           │
└──────────────────────────────────────────────────────────────┘
```

---

## 2. Offline-First Data Flow

### 2.1 Create Crop Listing (Farmer)

```
Farmer fills form → taps "Save"
  │
  ▼
[CropListingBloc] ── CreateCropListing event
  │
  ▼
[CropListingRepository]
  ├── 1. Generate UUID for listingId
  ├── 2. Save to Isar (version=1, status=active)
  ├── 3. Enqueue SyncOperation:
  │      entityType: "crop_listing"
  │      action: "create"
  │      idempotencyKey: "{farmerId}:crop_listing:{listingId}:create:{timestamp_ms}"
  │
  ▼
[SyncExecutor] (when online)
  ├── Batch up to 50 pending operations
  ├── POST /sync with crop create operation
  │
  ▼
[SyncFunction] (server)
  ├── 1. Check idempotency key → duplicate or new
  ├── 2. Validate farmerId == JWT sub
  ├── 3. Field whitelist check
  ├── 4. ValidateCropFields (type, quantity, price)
  ├── 5. PutItem with condition: attribute_not_exists(listingId)
  ├── 6. Store idempotency key (7-day TTL)
  └── Return {status: "success", serverVersion: 1}
```

### 2.2 Browse Listings (Read Path)

```
User opens Browse screen
  │
  ▼
[CropListingBloc] ── LoadActiveListings / LoadCropListings
  │
  ├── ALWAYS: Query local Isar first (instant, offline-safe)
  │   └── CropListing.filter().statusEqualTo(active).sortByCreatedAtDesc()
  │
  ├── IF ONLINE: GET /crops?cropType=teff&status=available
  │   ├── Server queries StatusCropTypeIndex
  │   ├── Returns paginated CropDto[]
  │   └── Cache response in Isar for offline access
  │
  └── Emit CropListingsLoaded with results
```

### 2.3 Sync Conflict Resolution

```
SyncExecutor receives 'conflict' response
  │
  ▼
[ConflictResolver]
  ├── Server-wins strategy: accept server data
  ├── Update local Isar entity with server version
  ├── Set version = serverVersion
  │
  ▼
[ConflictNotificationService]
  └── Notify UI via stream → ConflictBloc → modal dialog
```

---

## 3. Isar Schema

### CropListing Collection

```dart
@collection
class CropListing {
  Id id                         // Isar auto-increment (internal)

  @Index(unique: true)
  String listingId              // Server UUID

  @Index()
  String farmerId               // Query by owner

  @Index()
  String cropType               // Query by type

  double quantity               // In quintals (100kg)
  double pricePerKg             // In ETB

  LatLng location               // Embedded: latitude, longitude
  String description            // Amharic/Oromo/English
  String? audioNoteRef          // Path: audio/{userId}/{listingId}.m4a

  @Index()
  @Enumerated(EnumType.name)
  ListingStatus status          // active, draft, sold, expired

  @Index()
  DateTime createdAt            // UTC
  DateTime updatedAt            // UTC
  int version                   // Optimistic lock
}

enum ListingStatus { draft, active, sold, expired }
```

### Indexes for Query Performance
- **listingId** (unique) — server reconciliation
- **farmerId** — "my listings" queries
- **cropType** — filter by crop
- **status** + **createdAt** — browse active listings

---

## 4. Sync Queue Integration

### SyncOperation Entity

```dart
@collection
class SyncOperation {
  Id id
  @Index(unique: true) String operationId    // UUID
  @Index(unique: true) String idempotencyKey // Deterministic
  @Index() String entityType                  // 'crop_listing'
  String entityId                             // Listing ID
  String action                               // 'create', 'update', 'delete'
  String data                                 // JSON serialized entity
  @Index() SyncStatus status                  // pending → syncing → synced/failed
  int retryCount                              // 0-3 (max retries)
  DateTime createdAt
}
```

### Idempotency Key Format
```
{userId}:{entityType}:{entityId}:{action}:{timestamp_ms}
Example: farmer-123:crop_listing:crop-456:create:1707998400000
```

### Queue Management
| Operation | Behavior |
|-----------|----------|
| Enqueue | Generate idempotency key, check for duplicates |
| Fetch batch | `getPendingOperations(limit: 50)` — FIFO |
| Retry | Increment retryCount, reset to pending if < 3 |
| Purge | Remove synced operations > 30 days old |
| Capacity | Max 1000 operations (bounded queue) |

### Sync Triggers
- Connectivity detected (debounced)
- App resume (foreground)
- Periodic 15-minute interval
- Manual developer trigger

---

## 5. BLoC Architecture

### Events
| Event | Purpose |
|-------|---------|
| `LoadCropListings(farmerId)` | Load farmer's own listings |
| `LoadActiveListings(cropTypeFilter?)` | Load browsable listings |
| `CreateCropListing(...)` | Create new listing |
| `DeleteCropListing(listingId, farmerId)` | Soft-delete |
| `FilterCropListings(farmerId, cropType?)` | Filter by crop type |

### States
| State | Meaning |
|-------|---------|
| `CropListingInitial` | No data loaded |
| `CropListingLoading` | Fetching/processing |
| `CropListingsLoaded` | List available (with optional filter) |
| `CropListingCreated` | Successful creation |
| `CropListingError(message)` | Failed operation |

### Role-Aware Loading
- **Farmer**: `LoadCropListings(farmerId)` — shows own listings with delete
- **Trucker/Buyer**: `LoadActiveListings()` — browses all active, no delete

---

## 6. Screens

### CreateCropListingScreen
- **Crop Type Grid**: 2x5 icon grid with localized labels (teff, wheat, barley, etc.)
- **Quantity Input**: Quintals (100kg), `^\d+\.?\d{0,2}` validation
- **Price Input**: ETB/kg, required, > 0
- **Location Input**: Woreda (manual text entry, GPS deferred)
- **Description**: Multiline, supports Amharic/Oromo Unicode
- **Voice Note Widget**: Optional audio recording (M4A, 60s max)
- **Save Button**: Full-width, 48dp, validates all fields

### BrowseListingsScreen
- **OfflineBanner**: Amber banner with cloud_off icon when offline
- **Filter Chips Row**: Horizontal scroll — "All" + crop type chips with icons
- **Listings ListView**: CropListingCard widgets, lazy-loaded
- **Empty State**: Icon + message when no results
- **Pull-to-refresh**: Triggers sync (future enhancement)

### CropListingCard
```
┌─────────────────────────────────────┐
│ [Icon] Crop Name — Qty  |  Price   │
│        Date              |  [Play]  │
│        Status chip       |  [Del]   │
└─────────────────────────────────────┘
```
- Status chip: green=active, red=expired, blue=sold, gray=draft
- Audio playback widget if voice note exists
- Delete button (red, farmers only)

---

## 7. Validation Rules

### Client-Side (Pre-enqueue)
| Field | Rule |
|-------|------|
| Crop Type | Must be selected from enum |
| Quantity | Required, > 0, max 2 decimal places |
| Price | Required, > 0, max 2 decimal places |
| Location | Required, non-empty |
| Description | Optional, max 500 chars |

### Server-Side (Per API Contract)
| Field | Rule |
|-------|------|
| Crop Type | One of 9 enum values |
| Quantity | 0.01 — 10,000 quintals |
| Price/kg | 0.01 — 1,000 ETB |
| Location | Woreda/zone/region required; coords within Ethiopian bounds if provided |
| Harvest Date | Not future, not > 1 year past |
| Description | Max 500 chars, Amharic/Oromo/English |
| Farmer ID | Must match JWT sub |

### Fraud Detection (Server-Side)
- **Duplicate listing**: Same farmer + identical crop in 1 hour → rejected
- **Velocity limit**: Max 5 listings/hour per user → rate limited

---

## 8. Crop Types

| Enum Value | Icon | Ethiopian Context |
|------------|------|-------------------|
| teff | grass | Staple grain, Ethiopian highland |
| wheat | grain | Common cereal crop |
| barley | eco | Highland crop |
| maize | spa | Lowland staple |
| sorghum | grass | Drought-resistant |
| coffee | coffee | Export crop |
| sesame | spa | Oilseed export |
| chickpea | eco | Pulse crop |
| lentil | eco | Pulse crop |

All crop names are localized in English, Amharic, and Afan Oromo.

---

## 9. Voice Note Integration

| Parameter | Value |
|-----------|-------|
| Codec | AAC-LC |
| Bitrate | 32 kbps |
| Sample Rate | 22050 Hz |
| Max Duration | 60 seconds |
| File Size | ~240 KB |
| Storage Path | `{appDir}/audio/{userId}/{entityId}.m4a` |

Voice notes allow low-literacy farmers to describe their crop in their local language. Recordings are stored locally and referenced in the listing. Security: UUID validation prevents path traversal attacks.

---

## 10. Server API

### POST /sync (Crop Operations)
Batch sync via the shared sync endpoint. Each operation includes `entityType: "crop_listing"` with `action: create/update/delete`.

### GET /crops
Browse active listings with filters:
```
?cropType=teff&woreda=Debre+Birhan&status=available&limit=20&nextToken=...
```

### GET /crops/{id}
Single listing detail. Phone number only visible to authenticated users.

Full contract: `specs/contracts/crop-listing-api.md`

---

## 11. Ethiopian Context Optimizations

| Aspect | Design |
|--------|--------|
| Units | Quintals (kuntal, 100kg) — standard Ethiopian measure |
| Currency | ETB (Birr) — prices in ETB/kg |
| Localization | Crop types in all 3 languages |
| Voice Notes | Amharic/Oromo audio for low-literacy farmers |
| Location | Woreda/zone/region hierarchy (not GPS-only) |
| Batch Sync | Minimizes expensive mobile data usage |
| Idempotency | Prevents duplicates on unreliable 3G networks |

---

## 12. File Structure

```
mobile/lib/features/crop_listing/
├── data/
│   └── repositories/
│       └── crop_listing_repository.dart
├── domain/
│   └── models/
│       └── crop_type.dart
└── presentation/
    ├── bloc/
    │   ├── crop_listing_bloc.dart
    │   ├── crop_listing_event.dart
    │   └── crop_listing_state.dart
    ├── screens/
    │   ├── create_crop_listing_screen.dart
    │   └── browse_listings_screen.dart
    └── widgets/
        ├── crop_listing_card.dart
        └── offline_banner.dart

mobile/lib/core/database/schemas/
├── crop_listing.dart          # Isar entity
└── sync_operation.dart        # Sync queue entity

mobile/lib/core/sync/
├── sync_queue.dart            # Queue management
├── sync_executor.dart         # Batch sync orchestration
├── sync_worker.dart           # Background worker (15-min interval)
├── conflict_resolver.dart     # Server-wins conflict handling
├── idempotency_key_generator.dart
└── connectivity_monitor.dart
```

---

**Cross-references**:
- API contract: `specs/contracts/crop-listing-api.md`
- Sync engine: `specs/decisions/adr-003-sync-engine-architecture.md`

**Last Updated**: 2026-02-21
