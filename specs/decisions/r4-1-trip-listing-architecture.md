# R4.1 Trip Listing System Architecture

## Version
1.0.0 | 2026-02-17

## Status
Approved (Architect)

---

## 1. Component Overview

```
┌─────────────────────────────────────────────────────┐
│                    MOBILE (Flutter)                  │
│                                                      │
│  ┌──────────────┐  ┌──────────────┐  ┌────────────┐ │
│  │ TripListing  │  │  TripListing │  │   Isar DB  │ │
│  │    BLoC      │──│  Repository  │──│  (Trip     │ │
│  │              │  │  (offline-   │  │   schema)  │ │
│  │ Events/States│  │   first)     │  │            │ │
│  └──────────────┘  └──────┬───────┘  └────────────┘ │
│                           │                          │
│                    ┌──────┴───────┐                   │
│                    │  SyncQueue   │                   │
│                    │  (enqueue    │                   │
│                    │   trip ops)  │                   │
│                    └──────┬───────┘                   │
└───────────────────────────┼──────────────────────────┘
                            │ POST /sync (trip ops)
                            │ GET /trips (browse)
                            │ GET /trips/{id}
                            ▼
┌─────────────────────────────────────────────────────┐
│               API GATEWAY (HTTP API v2)              │
│  ┌─────────────┐  ┌────────────────────────────┐    │
│  │ Cognito JWT  │  │ Routes:                    │    │
│  │ Authorizer   │  │  POST /sync → SyncFunction │    │
│  │              │  │  GET /trips → TripFunction  │    │
│  │              │  │  GET /trips/{id} → TripFn   │    │
│  └─────────────┘  └────────────────────────────┘    │
└─────────────────────────────────────────────────────┘
                            │
                            ▼
┌─────────────────────────────────────────────────────┐
│               SERVER (.NET 10 AOT)                   │
│                                                      │
│  ┌──────────────┐    ┌──────────────────────┐       │
│  │ SyncFunction  │    │ TripFunction          │       │
│  │ (POST /sync)  │    │ (GET /trips,          │       │
│  │ trip entity   │    │  GET /trips/{id})      │       │
│  │ handler       │    │                        │       │
│  └──────┬───────┘    └──────────┬─────────────┘       │
│         │                       │                     │
│         ▼                       ▼                     │
│  ┌─────────────────────────────────────┐              │
│  │         ITripRepository             │              │
│  │  (DynamoDB manual marshalling)      │              │
│  └─────────────────┬───────────────────┘              │
│                    │                                  │
└────────────────────┼──────────────────────────────────┘
                     ▼
┌─────────────────────────────────────────────────────┐
│                 DynamoDB                             │
│                                                      │
│  ┌─────────────────────────────┐                     │
│  │ TripsTable                   │                     │
│  │ PK: tripId | SK: createdAt   │                     │
│  │                              │                     │
│  │ GSI: StatusDepartureDateIndex│                     │
│  │   PK: status                 │                     │
│  │   SK: departureDate          │                     │
│  └─────────────────────────────┘                     │
│                                                      │
│  ┌─────────────────────────────┐                     │
│  │ SyncMetadataTable (existing) │                     │
│  │ PK: idempotencyKey           │                     │
│  │ TTL: 7 days                  │                     │
│  └─────────────────────────────┘                     │
└─────────────────────────────────────────────────────┘
```

---

## 2. Data Flow

### 2.1 Create Trip (Offline → Sync)

```
Trucker taps "Post Trip"
  │
  ▼
[TripListingBloc] ── CreateTripEvent
  │
  ▼
[TripListingRepository]
  ├── 1. Save to Isar (Trip schema, version=1, status=open)
  ├── 2. Enqueue SyncOperation:
  │      entityType: "trip"
  │      action: "create"
  │      idempotencyKey: "{truckerId}:trip:{tripId}:create:{timestamp}"
  │
  ▼
[SyncExecutor] (when online)
  ├── POST /sync with trip create operation
  │
  ▼
[SyncFunction] (server)
  ├── 1. Check idempotency key → duplicate or new
  ├── 2. ProcessTripAsync() (already implemented)
  ├── 3. Validate truckerId == JWT sub
  ├── 4. Field whitelist check
  ├── 5. ValidateTripFields (capacity, price, woreda, etc.)
  ├── 6. PutItem with condition: attribute_not_exists(tripId)
  ├── 7. Store idempotency key (7-day TTL)
  └── Return {status: "success", serverVersion: 1}
```

### 2.2 Browse Trips (Read Path)

```
Farmer opens "Find Trucks"
  │
  ▼
[TripListingBloc] ── LoadTripsEvent(filters)
  │
  ├── OFFLINE: Query local Isar
  │   └── Trip.filter().statusEqualTo(open).sortByDepartureDate()
  │
  ├── ONLINE: GET /trips?fromWoreda=X&status=open
  │   │
  │   ▼
  │ [TripFunction.HandleGetTripsAsync]
  │   ├── Query StatusDepartureDateIndex (PK=status, SK=departureDate)
  │   ├── Apply FilterExpression for woreda, capacity, price
  │   ├── Paginate with nextToken
  │   └── Return TripDto[]
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

## 3. Server Implementation

### 3.1 Files to Create/Modify

| File | Action | Description |
|------|--------|-------------|
| `server/src/Functions/TripFunction.cs` | **Already exists** | GET /trips, GET /trips/{id} — follows CropFunction pattern |
| `server/src/Functions/SyncFunction.cs` | **Already modified** | ProcessTripAsync already implemented |
| `server/src/Data/ITripRepository.cs` | **Already exists** | Interface for trip queries |
| `server/src/Data/TripRepository.cs` | **Extend** | Add full CRUD support (currently query-only) |
| `server/src/Models/TripDtos.cs` | **Already exists** | TripDto, GetTripsResponse, GetTripResponse |
| `server/src/AppJsonContext.cs` | **Already registered** | TripDto, List<TripDto>, GetTripsResponse |

### 3.2 TripRepository — Needed Extensions

The existing `TripRepository` handles queries. The `SyncFunction` handles trip CRUD directly via DynamoDB. This is acceptable for now, but for consistency we should extract the create/update/delete logic into `TripRepository`:

```csharp
public interface ITripRepository
{
    // Existing
    Task<(List<Dictionary<string, object>> Items, Dictionary<string, AttributeValue>? LastEvaluatedKey)>
        QueryTripsAsync(string? fromWoreda, string? toWoreda, string? status,
                        int limit, Dictionary<string, AttributeValue>? exclusiveStartKey);
    Task<Dictionary<string, object>?> GetByIdAsync(string tripId);

    // Add for R4.1 (extract from SyncFunction)
    Task<AuthResult<Dictionary<string, object>>> CreateAsync(
        string tripId, Dictionary<string, object> data);
    Task<AuthResult<Dictionary<string, object>>> UpdateAsync(
        string tripId, Dictionary<string, object> data, int expectedVersion);
    Task<AuthResult<Dictionary<string, object>>> SoftDeleteAsync(
        string tripId, int expectedVersion);
}
```

**Decision**: Keep trip CRUD in SyncFunction for R4.1 (already working, tested). Refactor to repository pattern in R4.5 cleanup. The current approach uses direct DynamoDB calls in SyncFunction which is functionally correct and follows the same pattern used for initial crop implementation.

### 3.3 TripFunction — No Changes Needed

The existing `TripFunction.cs` already handles:
- GET /trips with filters (fromWoreda, toWoreda, status, limit, nextToken)
- GET /trips/{id} with soft-delete check
- Uses `AppJsonContext.Default.GetTripsResponse` (AOT-safe)
- Follows CropFunction pattern exactly

### 3.4 AOT Compliance Review

| Check | Status |
|-------|--------|
| All DTOs registered in AppJsonContext | ✅ TripDto, GetTripsResponse, GetTripResponse |
| No reflection in serialization | ✅ Uses JsonSerializerContext |
| No banned libraries | ✅ Only AWS SDK + System.Text.Json |
| Manual DynamoDB marshalling | ✅ Uses CropRepository.ToAttributeValue/FromItem |
| Explicit DI (constructor injection) | ✅ |

---

## 4. Mobile Implementation

### 4.1 Files to Create

| File | Description |
|------|-------------|
| `mobile/lib/features/trip_listing/data/repositories/trip_listing_repository.dart` | Offline-first repo (follow CropListingRepository pattern) |
| `mobile/lib/features/trip_listing/presentation/bloc/trip_listing_bloc.dart` | BLoC for trip CRUD + browsing |
| `mobile/lib/features/trip_listing/presentation/bloc/trip_listing_event.dart` | Events: Create, Load, Update, Cancel |
| `mobile/lib/features/trip_listing/presentation/bloc/trip_listing_state.dart` | States: Loading, Loaded, Error, Creating |
| `mobile/lib/features/trip_listing/presentation/screens/trip_list_screen.dart` | Browse trips screen |
| `mobile/lib/features/trip_listing/presentation/screens/create_trip_screen.dart` | Create trip form |
| `mobile/lib/features/trip_listing/presentation/screens/trip_detail_screen.dart` | Single trip view |
| `mobile/lib/features/trip_listing/presentation/widgets/trip_card.dart` | Trip list item widget |

### 4.2 TripListingRepository Pattern

Follow the CropListingRepository pattern exactly:

```dart
class TripListingRepository {
  final Isar _isar;
  final SyncQueue _syncQueue;

  Future<Trip> createTrip({
    required String truckerId,
    required String truckerName,
    required String truckerPhone,
    required String fromWoreda,
    required String toWoreda,
    required DateTime departureDate,
    required double capacity,
    required double pricePerQuintal,
  }) async {
    final tripId = _uuid.v4();
    final now = DateTime.now().toUtc();

    final trip = Trip()
      ..tripId = tripId
      ..truckerId = truckerId
      ..truckerName = truckerName
      ..truckerPhone = truckerPhone
      ..fromWoreda = fromWoreda
      ..toWoreda = toWoreda
      ..departureDate = departureDate
      ..capacity = capacity
      ..pricePerQuintal = pricePerQuintal
      ..status = TripStatus.open
      ..matchedListings = []
      ..createdAt = now
      ..updatedAt = now
      ..version = 1;

    // 1. Save to Isar
    await _isar.writeTxn(() => _isar.trips.put(trip));

    // 2. Enqueue sync
    await _syncQueue.enqueue(
      userId: truckerId,
      entityType: 'trip',
      entityId: tripId,
      action: 'create',
      data: jsonEncode(trip.toSyncData()),
      operationId: _uuid.v4(),
    );

    return trip;
  }

  Future<List<Trip>> getOpenTrips({String? fromWoreda, String? toWoreda}) async {
    var query = _isar.trips.filter().statusEqualTo(TripStatus.open);
    if (fromWoreda != null) query = query.fromWeredaEqualTo(fromWoreda);
    if (toWoreda != null) query = query.toWeredaEqualTo(toWoreda);
    return query.sortByDepartureDate().findAll();
  }
}
```

### 4.3 Isar Schema — Already Scaffolded

The Trip schema at `mobile/lib/core/database/schemas/trip.dart` already has:
- All required fields (tripId, truckerId, fromWoreda, toWoreda, etc.)
- Proper indexes (@Index on tripId, truckerId, status, departureDate)
- TripStatus enum with correct values
- Version field for optimistic concurrency

**No changes needed to schema.**

### 4.4 SyncExecutor Integration

The existing `SyncExecutor` processes SyncOperations from the queue. When a trip sync result returns, the executor should update the local Isar Trip record:

```dart
// In sync_executor.dart, add trip entity handling:
case 'trip':
  if (result.status == 'success') {
    // Update local version to match server
    await _updateLocalTrip(operation.entityId, result.serverVersion);
  } else if (result.status == 'conflict') {
    // Server-wins: replace local with server data
    await _replaceLocalTrip(operation.entityId, result.serverData);
  }
```

---

## 5. Infrastructure

### 5.1 DynamoDB — Already Provisioned

The `database-stack.ts` already creates the TripsTable:

```typescript
// TripsTable: PK=tripId, SK=createdAt
// GSI: StatusDepartureDateIndex (PK=status, SK=departureDate)
```

**No infra changes needed for R4.1.**

### 5.2 Lambda — Already Configured

The `api-stack.ts` already provisions:
- `trips` Lambda function reading from TRIPS_TABLE
- GET /trips route
- GET /trips/{id} route
- POST /sync already handles entityType="trip"

**No infra changes needed for R4.1.**

---

## 6. Implementation Sequence

```
Phase 1: Server (already mostly done)
  ✅ SyncFunction.ProcessTripAsync — create/update/delete
  ✅ TripFunction — GET /trips, GET /trips/{id}
  ✅ TripDtos — TripDto, GetTripsResponse
  ✅ AppJsonContext — TripDto registered
  ✅ TripsTable — provisioned in database-stack

Phase 2: Mobile Feature Module
  1. Create trip_listing feature directory structure
  2. Implement TripListingRepository (offline-first, follow crop pattern)
  3. Implement TripListingBloc (events/states)
  4. Implement trip screens (list, create, detail)
  5. Add trip sync handling to SyncExecutor
  6. Add l10n strings for all trip UI (3 languages)

Phase 3: Integration Testing
  1. Server: Trip CRUD via /sync + GET /trips
  2. Mobile: Offline create → sync → conflict resolution
  3. E2E: Create trip offline → sync → browse from another device
```

---

## 7. Validation Checklist

| Requirement | Solution |
|-------------|----------|
| Works offline | Isar-first writes, SyncQueue for deferred upload |
| Woreda-to-woreda routing | Free-text fromWoreda/toWoreda (1-100 chars) |
| Capacity in quintals | `double capacity` (0.1-1000) |
| Pricing in ETB/quintal | `double pricePerQuintal` (0.01-10000) |
| Status transitions enforced | Server-side ValidTransitions dictionary |
| Soft deletes | `deletedAt` timestamp, status=cancelled |
| Version concurrency | ConditionExpression on version |
| Phone privacy | truckerPhone hidden from unauthenticated GET |
| AOT-safe | System.Text.Json source generators |
| Paginated browse | nextToken (base64 DynamoDB ExclusiveStartKey) |

---

## 8. Risks and Mitigations

| Risk | Mitigation |
|------|-----------|
| StatusDepartureDateIndex hot partition (all open trips under "open" PK) | Acceptable for MVP scale (~500 active trips). Add fromWoreda GSI in R5 if needed. |
| Stale trips (past departure dates still shown) | Server-side: allow past dates for status updates. Client-side: filter by departureDate > now in browse. |
| Offline trip creation with same fromWoreda/toWoreda by different truckers | No conflict — each trip has unique tripId. Multiple trips on same route is expected behavior. |

---

**Last Updated**: 2026-02-17
**Author**: Architect Agent
