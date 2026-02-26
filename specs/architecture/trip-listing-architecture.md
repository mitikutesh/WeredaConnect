# Trip Listing System Architecture

## Version
1.0.0 | 2026-02-21

## Status
Implemented (R4.1)

---

## 1. Component Overview

```
┌─────────────────────────────────────────────────────────────┐
│                       MOBILE (Flutter)                       │
│                                                              │
│  ┌──────────────────┐  ┌────────────────┐  ┌──────────────┐│
│  │ TripListingBloc   │  │ TripListing    │  │   Isar DB    ││
│  │                   │──│ Repository     │──│  (Trip       ││
│  │ Events/States     │  │ (offline-first)│  │   schema)    ││
│  └──────────────────┘  └───────┬────────┘  └──────────────┘│
│                                │                             │
│                         ┌──────┴────────┐                    │
│                         │  SyncQueue    │                    │
│                         │  (trip ops)   │                    │
│                         └──────┬────────┘                    │
└────────────────────────────────┼────────────────────────────┘
                                 │ POST /sync (trip ops)
                                 │ GET /trips (browse)
                                 │ GET /trips/{id}
                                 ▼
┌─────────────────────────────────────────────────────────────┐
│                  API GATEWAY (HTTP API v2)                    │
│  Cognito JWT Authorizer                                      │
│  POST /sync → SyncFunction  |  GET /trips → TripFunction    │
└─────────────────────────────┬───────────────────────────────┘
                              ▼
┌─────────────────────────────────────────────────────────────┐
│                  SERVER (.NET 10 AOT)                         │
│  SyncFunction: ProcessTripAsync (create/update/delete)       │
│  TripFunction: GET /trips (browse), GET /trips/{id}          │
│  ITripRepository: DynamoDB manual marshalling                │
└─────────────────────────────┬───────────────────────────────┘
                              ▼
┌─────────────────────────────────────────────────────────────┐
│                      DynamoDB                                │
│  TripsTable: PK=tripId | SK=createdAt                       │
│  GSI: StatusDepartureDateIndex (PK=status, SK=departureDate)│
│  SyncMetadataTable (shared, 7-day TTL)                      │
└─────────────────────────────────────────────────────────────┘
```

---

## 2. Data Flow

### 2.1 Create Trip (Trucker, Offline → Sync)

```
Trucker taps "Post Trip"
  │
  ▼
[TripListingBloc] ── CreateTripEvent
  │
  ▼
[TripListingRepository]
  ├── 1. Save to Isar (version=1, status=open)
  ├── 2. Enqueue SyncOperation:
  │      entityType: "trip"
  │      idempotencyKey: "{truckerId}:trip:{tripId}:create:{timestamp}"
  │
  ▼
[SyncExecutor] (when online)
  ├── POST /sync with trip create operation
  │
  ▼
[SyncFunction] (server)
  ├── Idempotency check → field validation → PutItem
  └── Return {status: "success", serverVersion: 1}
```

### 2.2 Browse Trips (Farmer)

```
Farmer opens "Find Trucks"
  │
  ├── OFFLINE: Query local Isar
  │   └── Trip.filter().statusEqualTo(open).sortByDepartureDate()
  │
  ├── ONLINE: GET /trips?fromWoreda=X&status=open
  │   └── Server queries StatusDepartureDateIndex → return paginated results
  │
  └── Cache server response in Isar for offline access
```

### 2.3 Status Transitions

```
open → matched       (crop listings matched to trip)
open → cancelled     (trucker cancels)
matched → inTransit  (trucker picks up crops)
matched → cancelled  (trucker cancels after match)
inTransit → delivered (trucker confirms delivery)

Terminal states: delivered, cancelled
```

---

## 3. Trip Isar Schema

```dart
@collection
class Trip {
  Id id
  @Index(unique: true) String tripId
  @Index() String truckerId
  String truckerName
  String truckerPhone
  String fromWoreda
  String toWoreda
  DateTime departureDate
  double capacity           // quintals
  double pricePerQuintal    // ETB
  @Enumerated(EnumType.name) TripStatus status
  List<String> matchedListings
  DateTime createdAt
  DateTime updatedAt
  int version               // optimistic concurrency
}

enum TripStatus { open, matched, inTransit, delivered, cancelled }
```

---

## 4. BLoC Architecture

### Events
| Event | Purpose |
|-------|---------|
| `CreateTripEvent` | Trucker posts new trip |
| `LoadTripsEvent(filters)` | Browse/filter trips |
| `UpdateTripEvent` | Status change |
| `CancelTripEvent` | Trucker cancels trip |

### States
| State | Meaning |
|-------|---------|
| `TripListingInitial` | No data loaded |
| `TripListingLoading` | Fetching/processing |
| `TripListingsLoaded` | List available |
| `TripListingCreated` | Trip created successfully |
| `TripListingError` | Operation failed |

---

## 5. Screens

| Screen | Purpose |
|--------|---------|
| TripListScreen | Browse trips with filters |
| CreateTripScreen | Form: route, capacity, departure, price |
| TripDetailScreen | Single trip view with status |
| TripCard | List item widget |

### Create Trip Form
- **From/To Woreda**: Text input (1-100 chars)
- **Departure Date**: Date picker (future dates only)
- **Capacity**: Quintals (0.1-1000)
- **Price**: ETB/quintal (0.01-10000)

---

## 6. Server Implementation

### SyncFunction (Already Implemented)
- `ProcessTripAsync()` handles create/update/delete
- Validates truckerId == JWT sub
- Field whitelist + status transition validation
- PutItem with `attribute_not_exists` condition

### TripFunction (Already Implemented)
- GET /trips with filters (fromWoreda, toWoreda, status, limit, nextToken)
- GET /trips/{id} with soft-delete check
- AOT-safe: `AppJsonContext.Default.GetTripsResponse`

### AOT Compliance
All DTOs registered in AppJsonContext. System.Text.Json source generators. No reflection. Manual DynamoDB marshalling.

---

## 7. Validation

| Field | Server Rule |
|-------|-------------|
| fromWoreda / toWoreda | Required, 1-100 chars |
| departureDate | Valid ISO 8601 |
| capacity | 0.1-1000 quintals |
| pricePerQuintal | 0.01-10000 ETB |
| truckerId | Must match JWT sub |
| Status transition | Enforced by ValidTransitions dictionary |

---

## 8. Risks and Mitigations

| Risk | Mitigation |
|------|-----------|
| StatusDepartureDateIndex hot partition (all "open" trips) | Acceptable at MVP scale (~500 active trips) |
| Stale trips (past departure dates still shown) | Client filters by departureDate > now |
| Multiple trips on same route | Expected behavior — each trip has unique tripId |

---

## 9. File Structure

```
mobile/lib/features/trip_listing/
├── data/
│   └── repositories/
│       └── trip_listing_repository.dart
└── presentation/
    ├── bloc/
    │   ├── trip_listing_bloc.dart
    │   ├── trip_listing_event.dart
    │   └── trip_listing_state.dart
    ├── screens/
    │   ├── trip_list_screen.dart
    │   ├── create_trip_screen.dart
    │   └── trip_detail_screen.dart
    └── widgets/
        └── trip_card.dart
```

---

**Cross-references**:
- Detailed design: `specs/decisions/r4-1-trip-listing-architecture.md`
- API contract: `specs/contracts/trip-listing-api.md`

**Last Updated**: 2026-02-21
