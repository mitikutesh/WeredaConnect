# F-11 Trip Listings — Architecture & Implementation Plan

## Version
1.0.0 | 2026-02-18

## Status
**Phase R4** — Implementation largely complete. This document captures the existing architecture and identifies remaining gaps.

---

## 1. Executive Summary

F-11 Trip Listings enables truckers to post available transport capacity (origin → destination, departure date, price) and farmers to browse and match trips with their crop listings. This is the **second user-facing feature** following Crop Listings (R3).

**Key finding from codebase review**: The server, infrastructure, and mobile scaffolding for F-11 are **already implemented**. The remaining work is primarily validation, testing gaps, and security hardening.

---

## 2. Implementation Status

### Already Implemented

| Module | File | Status |
|--------|------|--------|
| **Server** | `server/src/Models/TripDtos.cs` | Complete — TripDto, GetTripsResponse, GetTripResponse |
| **Server** | `server/src/Functions/TripFunction.cs` | Complete — GET /trips, GET /trips/{id} with filtering |
| **Server** | `server/src/Data/TripRepository.cs` | Complete — StatusDepartureDateIndex GSI query |
| **Server** | `server/src/Data/ITripRepository.cs` | Complete — Interface |
| **Server** | `server/src/Functions/SyncFunction.cs` | Complete — entityType="trip" create/update/delete |
| **Server** | `server/src/AppJsonContext.cs` | Complete — TripDto, GetTripsResponse, GetTripResponse registered |
| **Server** | `server/src/Helpers/PaginationTokenHelper.cs` | Complete — HMAC-signed pagination tokens |
| **Infra** | `infra/lib/stacks/database-stack.ts` | Complete — TripsTable with StatusDepartureDateIndex GSI |
| **Infra** | `infra/lib/stacks/api-stack.ts` | Complete — GET /trips, GET /trips/{id} routes, Lambda, permissions |
| **Mobile** | `mobile/lib/core/database/schemas/trip.dart` | Complete — Isar schema with all fields |
| **Mobile** | `mobile/lib/features/trip_listing/` | Complete — Full feature module scaffolding |
| **Mobile** | `mobile/lib/shared/l10n/app_en.arb` | Complete — All trip-related English strings |
| **Tests** | `server/tests/Functions/TripFunctionTests.cs` | Exists |
| **Tests** | `server/tests/Data/TripRepositoryTests.cs` | Exists |
| **Tests** | `server/tests/Functions/SyncFunctionTests.cs` | Exists |

---

## 3. Architecture Overview

### 3.1 Data Flow (Offline-First)

```
┌──────────────────────────────────────────────────────────────┐
│                     MOBILE (Flutter)                          │
│                                                              │
│  ┌─────────────┐    ┌──────────────┐    ┌──────────────┐    │
│  │ Create Trip  │───>│ TripListing  │───>│  Isar DB     │    │
│  │ Screen       │    │ Repository   │    │  (trip.dart)  │    │
│  └─────────────┘    │              │    └──────────────┘    │
│                     │              │           │             │
│  ┌─────────────┐    │              │    ┌──────────────┐    │
│  │ Browse Trips │<───│              │    │  SyncQueue    │    │
│  │ Screen       │    └──────────────┘    │  (enqueue)    │    │
│  └─────────────┘                        └──────┬───────┘    │
│                                                │             │
│  ┌─────────────┐    ┌──────────────┐    ┌──────┴───────┐    │
│  │ TripListing  │───>│ TripListing  │    │ SyncWorker   │    │
│  │ Bloc         │    │ Event/State  │    │ (when online) │    │
│  └─────────────┘    └──────────────┘    └──────┬───────┘    │
└─────────────────────────────────────────────────┼────────────┘
                                                  │
                        POST /sync ───────────────┘
                        GET /trips  ──────────────────>
                                                  │
┌─────────────────────────────────────────────────┼────────────┐
│                     SERVER (.NET 10 AOT)         │            │
│                                                  │            │
│  ┌──────────────┐    ┌──────────────┐    ┌──────┴───────┐   │
│  │ SyncFunction  │───>│ ProcessTrip  │───>│  DynamoDB    │   │
│  │ (POST /sync)  │    │ Async()      │    │  TripsTable  │   │
│  └──────────────┘    └──────────────┘    │  PK: tripId  │   │
│                                          │  SK: createdAt│   │
│  ┌──────────────┐    ┌──────────────┐    │  GSI: Status  │   │
│  │ TripFunction  │───>│ TripRepo     │───>│  +DepartDate  │   │
│  │ (GET /trips)  │    │ QueryTrips() │    └──────────────┘   │
│  └──────────────┘    └──────────────┘                        │
└──────────────────────────────────────────────────────────────┘
```

### 3.2 Status Transition State Machine

```
                    ┌──────────┐
          ┌────────>│ cancelled │ (terminal)
          │         └──────────┘
          │              ^
          │              │
    ┌─────┴─┐      ┌────┴───┐      ┌──────────┐      ┌───────────┐
    │ open  │─────>│matched │─────>│ inTransit │─────>│ delivered │
    └───────┘      └────────┘      └──────────┘      └───────────┘
                        │                               (terminal)
                        │
                        v
                   ┌──────────┐
                   │ cancelled │ (terminal, re-opens matched listings)
                   └──────────┘
```

**Valid transitions** (enforced in `SyncFunction.ValidTransitions`):
| From | To | Trigger |
|------|----|---------|
| `open` | `matched` | Crop listings matched to trip |
| `open` | `cancelled` | Trucker cancels before any match |
| `matched` | `inTransit` | Trucker picks up crops and departs |
| `matched` | `cancelled` | Trucker cancels after match |
| `inTransit` | `delivered` | Trucker confirms delivery |

**Terminal states**: `delivered`, `cancelled` — no further transitions allowed.

---

## 4. Module-by-Module Architecture

### 4.1 Server (.NET 10 Native AOT)

#### Files and Classes

| File | Class | Purpose |
|------|-------|---------|
| `server/src/Models/TripDtos.cs` | `TripDto`, `GetTripsResponse`, `GetTripResponse` | Data transfer objects for API responses |
| `server/src/Functions/TripFunction.cs` | `TripFunction` | Lambda handler for GET /trips, GET /trips/{id} |
| `server/src/Data/ITripRepository.cs` | `ITripRepository` | Repository interface for DI/testing |
| `server/src/Data/TripRepository.cs` | `TripRepository` | DynamoDB CRUD using low-level API (AOT-safe) |
| `server/src/Functions/SyncFunction.cs` | `SyncFunction.ProcessTripAsync()` | Trip sync operations (create/update/delete) |
| `server/src/AppJsonContext.cs` | `AppJsonContext` | AOT source generator with TripDto types registered |
| `server/src/Helpers/PaginationTokenHelper.cs` | `PaginationTokenHelper` | HMAC-signed pagination tokens |
| `server/src/Helpers/LambdaResponse.cs` | `LambdaResponse` | Standardized API responses with security headers |

#### AOT Compliance

- All JSON serialization via `AppJsonContext` source generator
- No reflection: manual DynamoDB marshalling via `CropRepository.ToAttributeValue()` / `CropRepository.FromItem()`
- No banned libraries (Newtonsoft.Json, EF Core, Moq, AutoMapper)
- Explicit DI in Lambda constructors

#### DynamoDB Key Design

**TripsTable**:
- **PK**: `tripId` (STRING) — UUID partition key
- **SK**: `createdAt` (STRING) — ISO 8601 sort key
- **GSI `StatusDepartureDateIndex`**:
  - PK: `status` (STRING) — partition by lifecycle state
  - SK: `departureDate` (STRING) — sort by departure (soonest first)
  - Projection: ALL

**Query patterns** (from `TripRepository.QueryTripsAsync()`):
- `status=open` → GSI key condition → `ScanIndexForward=true` (soonest first)
- `fromWoreda`, `toWoreda` → GSI filter expressions (post-query)
- `minCapacity`, `maxPrice`, `departureDateFrom/To` → Application-side filtering in `TripFunction`
- Single trip by ID → Primary key query with `Limit=1`

#### Validation (in `SyncFunction.ValidateTripFields()`)

| Field | Create | Update | Rule |
|-------|--------|--------|------|
| `truckerId` | Required | N/A | Must match JWT `sub` claim |
| `truckerName` | Required | Optional | 1-100 chars, supports Amharic/Oromo/English |
| `truckerPhone` | Required | Optional | E.164 format, must start with `+251` |
| `fromWoreda` | Required | Optional | 1-100 chars |
| `toWoreda` | Required | Optional | 1-100 chars, must differ from `fromWoreda` |
| `departureDate` | Required | Optional | Valid ISO 8601 |
| `capacity` | Required | Optional | 0.1–1000 quintals |
| `pricePerQuintal` | Required | Optional | 0.01–10000 ETB |
| `status` | Forced `open` | Optional | Valid transition only |
| `matchedListings` | Optional | Optional | Array of crop listing IDs |

#### Security Controls (per F-11 security review)

- **C2**: `truckerId` must match authenticated user's JWT `sub` claim
- **C3**: Field whitelist (`AllowedTripFields`) — unknown fields rejected
- **H1**: Status transition validation via `ValidTransitions` dictionary
- **H3**: Block updates/deletes on cancelled/deleted trips
- **Rate limiting**: Per-user via `RateLimitService` (100 req/s)
- **Phone privacy**: `TripFunction.IsAuthenticated()` checks JWT authorizer claims (not raw headers) before exposing `truckerPhone`

### 4.2 Infrastructure (AWS CDK)

#### Files and Constructs

| File | Construct | Purpose |
|------|-----------|---------|
| `infra/lib/stacks/database-stack.ts` | `DatabaseStack.tripsTable` | TripsTable DynamoDB with GSI |
| `infra/lib/stacks/api-stack.ts` | `ApiStack` (Trip Lambda + routes) | TripFunction Lambda, GET /trips routes |
| `infra/lib/constructs/dynamodb-construct.ts` | `WeredaDynamoTable` | Reusable DynamoDB construct (PAY_PER_REQUEST) |
| `infra/lib/constructs/lambda-construct.ts` | `WeredaLambda` | Reusable Lambda construct (ARM64, .NET 10) |

#### Lambda Configuration (from `api-stack.ts`)

```
TripFunction:
  Runtime: .NET 10 (ARM64)
  Memory: 256 MB
  Timeout: 10 seconds
  Environment:
    TRIPS_TABLE: {tripsTable.tableName}
  Permissions: Read-only on TripsTable
```

```
SyncFunction:
  Runtime: .NET 10 (ARM64)
  Memory: 512 MB (default)
  Timeout: 30 seconds
  Environment:
    TRIPS_TABLE, CROPS_TABLE, SYNC_METADATA_TABLE, DELIVERIES_TABLE
  Permissions: Read-write on TripsTable (+ other entity tables)
```

#### API Gateway Routes

| Method | Path | Authorizer | Lambda | Notes |
|--------|------|------------|--------|-------|
| `GET` | `/trips` | None (public) | TripFunction | Browse open trips |
| `GET` | `/trips/{id}` | None (public) | TripFunction | Phone hidden if unauthenticated |
| `POST` | `/sync` | JWT (Cognito) | SyncFunction | Trip create/update/delete via sync |

### 4.3 Mobile (Flutter)

#### Files and Classes

| File | Class | Purpose |
|------|-------|---------|
| **Isar Schema** | | |
| `mobile/lib/core/database/schemas/trip.dart` | `Trip`, `TripStatus` | Isar collection with indexes |
| `mobile/lib/core/database/schemas/trip.g.dart` | Generated | Isar query builders |
| **Repository** | | |
| `mobile/lib/features/trip_listing/data/repositories/trip_listing_repository.dart` | `TripListingRepository` | Offline-first CRUD, enqueues sync |
| **BLoC** | | |
| `mobile/lib/features/trip_listing/presentation/bloc/trip_listing_bloc.dart` | `TripListingBloc` | State management |
| `mobile/lib/features/trip_listing/presentation/bloc/trip_listing_event.dart` | `LoadTrips`, `CreateTrip`, `CancelTrip`, `FilterTrips` | BLoC events |
| `mobile/lib/features/trip_listing/presentation/bloc/trip_listing_state.dart` | `TripListingInitial`, `TripListingLoading`, `TripsLoaded`, `TripCreated`, `TripListingError` | BLoC states |
| **Screens** | | |
| `mobile/lib/features/trip_listing/presentation/screens/create_trip_screen.dart` | `CreateTripScreen` | Trucker creates trip |
| `mobile/lib/features/trip_listing/presentation/screens/browse_trips_screen.dart` | `BrowseTripsScreen` | Farmer browses open trips |
| **Widgets** | | |
| `mobile/lib/features/trip_listing/presentation/widgets/trip_card.dart` | `TripCard` | Trip listing card widget |
| `mobile/lib/features/trip_listing/presentation/widgets/offline_banner.dart` | `OfflineBanner` | Offline status indicator |
| **Sync** | | |
| `mobile/lib/core/sync/sync_queue.dart` | `SyncQueue` | Enqueue trip operations |
| `mobile/lib/core/sync/sync_worker.dart` | `SyncWorker` | Process queue when online |
| **Localization** | | |
| `mobile/lib/shared/l10n/app_en.arb` | — | English strings (already has trip keys) |
| `mobile/lib/shared/l10n/app_am.arb` | — | Amharic strings (needs trip keys) |
| `mobile/lib/shared/l10n/app_om.arb` | — | Afan Oromo strings (needs trip keys) |

#### Isar Schema (from `trip.dart`)

```dart
@collection
class Trip {
  Id id = Isar.autoIncrement;
  @Index(unique: true) late String tripId;
  @Index() late String truckerId;
  late String truckerName;
  late String truckerPhone;
  late String fromWoreda;
  late String toWoreda;
  @Index() late DateTime departureDate;
  late double capacity;
  late double pricePerQuintal;
  @Index() @Enumerated(EnumType.name) late TripStatus status;
  late List<String> matchedListings;
  late DateTime createdAt;
  late DateTime updatedAt;
  late int version;
}
```

**Indexes**: `tripId` (unique), `truckerId`, `departureDate`, `status`

#### Offline-First Flow

1. **Create Trip** (trucker, offline):
   - Validate locally (fromWoreda != toWoreda, capacity 0.1-1000, etc.)
   - Generate UUID, save to Isar with `status=open`, `version=1`
   - Enqueue sync operation: `entityType="trip"`, `action="create"`
   - Show success immediately (offline-first UX)

2. **Browse Trips** (farmer, offline):
   - Query Isar: `Trip.filter().statusEqualTo(TripStatus.open).sortByDepartureDateAsc()`
   - Apply local filters (fromWoreda, toWoreda, minCapacity, maxPrice)
   - Display from cache — no network required

3. **Sync** (when connectivity returns):
   - `SyncWorker` picks up pending operations
   - POST /sync with trip operations
   - On success: update Isar `version` to serverVersion
   - On conflict: apply server data to Isar (server wins)
   - On error: retry with exponential backoff (max 3 retries)

4. **Background refresh** (when online):
   - GET /trips periodically → update Isar cache
   - Farmers see latest trips even without creating sync operations

---

## 5. Integration Points

### 5.1 Sync Protocol Integration

Trip operations flow through the existing sync engine:

```
Mobile SyncQueue → POST /sync → SyncFunction.ProcessTripAsync()
                                    ├── create → PutItem (conditional)
                                    ├── update → UpdateItem (version check)
                                    └── delete → UpdateItem (soft delete)
```

**Entity type**: `"trip"` (registered in `SyncFunction` switch statement)
**Allowed fields**: `AllowedTripFields` whitelist in `SyncFunction.cs`

### 5.2 Cross-Entity References

- `Trip.matchedListings` references `CropListing.listingId` values
- Future: Trip-crop auto-matching will link these entities (Phase R5)
- `Delivery.tripId` references `Trip.tripId` (Phase R4.2, already implemented)

### 5.3 Authentication Flow

```
Mobile → Cognito JWT → API Gateway JWT Authorizer → Lambda
                                                      ├── POST /sync: JWT required
                                                      │   └── sub claim = truckerId
                                                      ├── GET /trips: JWT optional
                                                      │   └── Phone shown only if authorized
                                                      └── GET /trips/{id}: JWT optional
                                                          └── Phone shown only if authorized
```

---

## 6. Remaining Work / Gaps

### 6.1 Server — Potential Gaps to Verify

| # | Item | Priority | Notes |
|---|------|----------|-------|
| S1 | Verify departure date future validation on create | High | `ValidateTripFields` checks format but may not enforce future-only on create |
| S2 | Verify departure date 30-day max on create | High | Contract requires max 30 days advance posting |
| S3 | Verify `matchedListings` references existing crop listings | Medium | Contract says each ID must reference existing crop |
| S4 | Verify `matchedListings` only addable on `matched` transition | Medium | Contract restriction |
| S5 | Consider adding TripService layer | Low | Currently sync operates directly on DynamoDB — matches Delivery pattern but diverges from Crop pattern (which uses CropRepository) |

### 6.2 Mobile — Potential Gaps to Verify

| # | Item | Priority | Notes |
|---|------|----------|-------|
| M1 | Verify Amharic (`app_am.arb`) has all trip strings | High | English strings exist, need to check am/om |
| M2 | Verify Afan Oromo (`app_om.arb`) has all trip strings | High | Same as above |
| M3 | Verify `TripListingRepository` follows `CropListingRepository` pattern | Medium | Should enqueue sync ops identically |
| M4 | Verify Trip Detail screen exists | Medium | `browse_trips_screen.dart` exists but no `trip_detail_screen.dart` found |
| M5 | Verify client-side validation matches server-side | Medium | Same rules in both places |

### 6.3 Testing — Potential Gaps to Verify

| # | Item | Priority | Notes |
|---|------|----------|-------|
| T1 | Verify `TripFunctionTests.cs` covers all filters | High | fromWoreda, toWoreda, minCapacity, maxPrice, dateRange |
| T2 | Verify `TripRepositoryTests.cs` covers GSI queries | High | StatusDepartureDateIndex |
| T3 | Verify `SyncFunctionTests.cs` covers trip CRUD + validation | High | Status transitions, field whitelist, auth checks |
| T4 | Verify mobile widget tests exist | Medium | TripCard, CreateTripScreen, BrowseTripsScreen |
| T5 | Verify BLoC tests exist | Medium | TripListingBloc events/states |

### 6.4 Security Hardening

| # | Item | Priority | Notes |
|---|------|----------|-------|
| SEC1 | Verify `IsAuthenticated` only trusts JWT authorizer (not raw headers) | Critical | Already implemented — needs security review confirmation |
| SEC2 | Verify HMAC pagination tokens prevent DynamoDB injection | Critical | `PaginationTokenHelper` already implements HMAC signing |
| SEC3 | Verify rate limiting covers trip sync operations | High | `RateLimitService` in SyncFunction — verify it applies |
| SEC4 | Verify soft delete prevents data leaks in GET responses | High | `TripFunction` checks `deletedAt` — verify consistency |

---

## 7. Implementation Order

Since most code already exists, the implementation order focuses on **verification and gap-filling**:

### Phase 1: Audit Existing Implementation (Task #2 — Security Review)
1. Read all existing Trip files and verify they match the contract
2. Run existing tests: `cd server && dotnet test`
3. Run mobile analysis: `cd mobile && flutter analyze`
4. Document any deviations from `specs/contracts/trip-listing-api.md`

### Phase 2: Fix Server Gaps (Task #3)
1. Add departure date future/30-day validation in `ValidateTripFields()` if missing
2. Add `matchedListings` validation (reference existing crops, only on `matched` transition)
3. Ensure all error codes match contract (400, 401, 409, 422, 429, 500)

### Phase 3: Infrastructure Verification (Task #4)
1. Verify `npx cdk synth --quiet` succeeds
2. Verify TripsTable GSI matches contract schema
3. Verify Lambda permissions are least-privilege

### Phase 4: Mobile Gap-Filling (Task #5)
1. Add missing trip strings to `app_am.arb` and `app_om.arb`
2. Add Trip Detail screen if missing
3. Verify repository pattern matches `CropListingRepository`
4. Verify BLoC handles all events correctly

### Phase 5: Test Coverage (Task #6)
1. Run existing tests, fix any failures
2. Add missing test cases for gaps identified in 6.3
3. Ensure >=80% coverage on Trip-related code

---

## 8. Key Design Decisions

### 8.1 No Separate TripService Layer
**Decision**: Trip sync operations are handled directly in `SyncFunction.ProcessTripAsync()` (inline DynamoDB operations), not through a separate `TripService` class.

**Rationale**: This matches the `Delivery` pattern (also inline in SyncFunction). The `CropListing` pattern uses a separate `CropRepository` for sync operations because it was implemented first and established a slightly different pattern. For consistency going forward, both Trip and Delivery use the inline pattern which reduces file count and simplifies the call chain.

### 8.2 Phone Number Privacy via API Gateway Authorizer
**Decision**: `TripFunction.IsAuthenticated()` checks `request.RequestContext.Authorizer` (set by API Gateway after JWT validation), NOT raw `Authorization` headers.

**Rationale**: Raw headers can be spoofed. Only trusting API Gateway's post-validation authorizer context prevents authentication bypass attacks.

### 8.3 GSI-Based Query with Application-Side Filtering
**Decision**: `TripRepository.QueryTripsAsync()` uses `StatusDepartureDateIndex` GSI for the key condition (`status=open`), with `fromWoreda`/`toWoreda` as DynamoDB FilterExpressions, and `minCapacity`/`maxPrice`/`dateRange` as application-side filters.

**Rationale**:
- The `status=open` partition is bounded (only active trips)
- Ethiopian woredas are finite (~500 total)
- Adding more GSIs increases cost and complexity for marginal gain
- Future optimization: Add `fromWoreda+departureDate` GSI if woreda queries become dominant

### 8.4 HMAC-Signed Pagination Tokens
**Decision**: `PaginationTokenHelper` signs pagination tokens with HMAC-SHA256 to prevent clients from crafting arbitrary DynamoDB scan positions.

**Rationale**: Without signing, a malicious client could encode arbitrary DynamoDB `ExclusiveStartKey` values to scan arbitrary table partitions. HMAC signing ensures tokens can only be generated server-side.

### 8.5 Soft Deletes (Cancelled Status)
**Decision**: Cancelled trips are soft-deleted (`deletedAt` timestamp + `status=cancelled`), not physically removed.

**Rationale**:
- Preserves audit trail for dispute resolution
- Allows re-opening matched crop listings when a trip is cancelled post-match
- GET endpoints filter out deleted records via `attribute_not_exists(deletedAt)` or status check

---

## 9. Ethiopian Context Considerations

### 9.1 Woreda-to-Woreda Routing
- Users think in **woredas** (administrative districts), not GPS coordinates
- Common patterns: rural → urban hub (e.g., Bishoftu → Addis Ababa)
- Hub cities: Addis Ababa, Adama, Bahir Dar, Hawassa, Dire Dawa, Mekelle

### 9.2 Capacity: Quintals
- 1 quintal = 100 kg ("kuntal" / ኩንታል in Amharic)
- Typical truck capacities: 50-500 quintals
- Validation range: 0.1–1000 quintals

### 9.3 Pricing: ETB per Quintal
- Ethiopian Birr (ETB) — national currency
- Typical transport rates: 50-300 ETB/quintal depending on distance
- Validation range: 0.01–10000 ETB

### 9.4 Trip Planning Horizon
- Truckers plan 1-3 days ahead
- Maximum 30-day advance posting
- Farmers search for departures in next few days

### 9.5 Connectivity Assumptions
- **Offline-first**: All operations save locally before syncing
- **3G latency**: Sync operations batch up to 50 ops per request
- **Data efficiency**: Pagination limits (20 default, 50 max) reduce payload size

---

## 10. Performance Budget

| Metric | Target | Implementation |
|--------|--------|----------------|
| GET /trips cold start | <2s | ARM64 + Native AOT |
| GET /trips warm response | <500ms | DynamoDB GSI query |
| POST /sync (trip create) | <1s | Single PutItem with conditional |
| Mobile trip browse (offline) | <200ms | Isar indexed query |
| Mobile trip create (offline) | <100ms | Isar write + sync enqueue |
| Sync batch (50 trip ops) | <5s | Sequential processing |
| App size impact | <2MB | Minimal additional code |

---

## Changelog

| Version | Date | Change |
|---------|------|--------|
| 1.0.0 | 2026-02-18 | Initial architecture document capturing existing implementation and gaps |

---

**Contract Reference**: `specs/contracts/trip-listing-api.md` v1.0.0
**Last Updated**: 2026-02-18
