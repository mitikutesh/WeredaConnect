# WeredaConnect Sprint Planning - Sprint 4 & 5 (Phase R3)

**Date**: 2026-02-28 (Sprint 4 Start)
**PO**: Product Owner Assessment
**Phase**: R3 - Sync Engine and First Feature
**Branch**: `sync-engine-crop-listing` (to be created)
**Last Sprint End**: 2026-02-28 (Sprint 3)
**Sprint Duration**: 4 weeks total (2 x 2-week sprints)
  - **Sprint 4**: Mar 1 - Mar 14, 2026
  - **Sprint 5**: Mar 15 - Mar 28, 2026

---

## 📊 Phase R3 Overview

### Phase Goal
**"The offline-first promise works. Users can create and browse crop listings offline, and data syncs when online."**

This is the **first end-to-end demonstration** of WeredaConnect's value proposition. If this works, the architecture is validated.

### What We're Building

#### Core Features (3 major features)
1. **F-08: Sync Engine Foundation** (Score: 15, Layer 1-2)
   - Phase A: Local queue (mobile)
   - Phase B: Sync executor (mobile)
   - Phase C: Server sync endpoint
   - Phase D: Conflict resolution UI

2. **F-10: Crop Listing Flow** (Score: 13, Layer 1)
   - Create crop listing (offline-first)
   - Browse crop listings
   - Sync listings to server

3. **F-17: Least-Privilege IAM** (Score: 6, Layer 2)
   - Per-function IAM roles
   - Security hardening

### Why Phase R3 Now?

**Dependencies Met:**
- ✅ R1 Security Foundation: 75% complete (assuming A1+A2 done in Sprint 3)
- ✅ R2 Auth + Data Models: 90% complete (register/login working)
- ✅ All 6 Isar schemas exist
- ✅ Auth service (online + offline) complete
- ✅ Server auth Lambda functions deployed

**Blockers Cleared:**
- Lambda must be deployed to AWS (R1 A2)
- Git history must be clean (R1 A1)
- DynamoDB SyncMetadata table must exist (R2 B4 - created in Sprint 3 or Sprint 4 prep)

---

## 📊 Sprint 3 Handoff - Current State

### What Was Completed in Sprint 3 ✅

#### Priority Group A: Security & Deployment
- [x] **A3**: F-19 CI/CD security scans — ✅ COMPLETE
- [ ] **A1**: F-01 Git history purge — ⚠️ MUST BE DONE before Sprint 4
- [ ] **A2**: F-16 Lambda deployment — ⚠️ MUST BE DONE before Sprint 4

#### Priority Group B: Mobile Foundation
- [x] **B1**: F-07 Isar schemas (6 schemas) — ✅ COMPLETE
- [x] **B2**: F-04 Isar encryption — ✅ COMPLETE
- [x] **B3**: F-03 Mobile auth flow — ✅ COMPLETE
- [ ] **B4**: F-07 DynamoDB tables (3 missing: Deliveries, Payments, **SyncMetadata**) — ⚠️ SyncMetadata CRITICAL for R3

### What Must Be Done Before Sprint 4 Starts 🚨

**Sprint 4 Pre-Flight Checklist** (to be completed in final days of Sprint 3 or Day 1 of Sprint 4):
1. [ ] **A1 Complete**: Git history purged, force-pushed, verified clean
2. [ ] **A2 Complete**: Lambda deployed to AWS dev, cold start <2s measured
3. [ ] **B4 Complete**: SyncMetadata DynamoDB table deployed (CRITICAL for sync engine)
4. [ ] Missing DynamoDB tables added: Deliveries, Payments (needed for R4 but good to have)
5. [ ] Verify AWS credentials active: `aws sts get-caller-identity`
6. [ ] Create new branch: `sync-engine-crop-listing`

**Sprint 4 CANNOT start until A1, A2, and SyncMetadata table are DONE.**

---

## 🎯 Sprint 4 & 5 Goals (Phase R3)

### Primary Objective
**Build and validate the offline-first sync engine and prove the architecture with the first user-facing feature: crop listings.**

### Sprint Theme
"**Make Offline-First Real: Sync Engine + First Feature**"

### Success Criteria (Phase R3 Exit)
By March 28, 2026:
- ✅ Farmer can create a crop listing OFFLINE on a 2GB Android device
- ✅ Listing appears in local Isar database immediately
- ✅ When connectivity returns, listing syncs to server automatically
- ✅ Another user can browse listings from server
- ✅ Sync conflicts are detected and resolved (server wins by default)
- ✅ Idempotency keys prevent duplicate listings on retry
- ✅ Each Lambda has minimum-necessary IAM permissions
- ✅ Zero data loss in 100 offline-online-offline cycles (integration test)
- ✅ Sync completes within 5s of connectivity restoration

---

## 📋 Phase R3 Backlog (Prioritized by Dependency Chain)

### Sprint 4 (Mar 1-14): Sync Engine Foundation

#### Priority Group A: Infrastructure Prerequisites (Week 1, Days 1-2)

---

#### **Task A1: Add Missing DynamoDB Tables** 🔴 CRITICAL
**Feature**: F-07 (Infrastructure completion)
**Priority**: P0 (BLOCKER for sync engine)
**Effort**: 3 hours
**Owner**: @Infra
**Sprint**: Sprint 4, Week 1, Day 1

**Acceptance Criteria**:
- [ ] Add **SyncMetadata** table to `infra/lib/stacks/database-stack.ts`:
  - PK: `idempotencyKey` (String)
  - SK: `timestamp` (Number)
  - Attributes: `userId`, `entityType`, `entityId`, `action`, `status`, `createdAt`, `expiresAt`
  - TTL attribute: `expiresAt` (7 days from creation for automatic cleanup)
  - No GSIs needed (query by PK only)
- [ ] Add **Deliveries** table to `database-stack.ts`:
  - PK: `deliveryId` (String)
  - SK: `tripId` (String)
  - Attributes: `listingId`, `qrToken`, `gpsPickup`, `gpsDropoff`, `photoRef`, `status`, `timestamps`, `version`
- [ ] Add **Payments** table to `database-stack.ts`:
  - PK: `paymentId` (String)
  - SK: `deliveryId` (String)
  - Attributes: `farmerId`, `buyerId`, `amount`, `telebirrRef`, `status`, `timestamps`, `version`
- [ ] Add GSIs to existing tables:
  - **CropsTable**: `StatusCreatedAtIndex` (GSI on `status` + `createdAt` for filtering active listings)
  - **TripsTable**: `StatusDepartureDateIndex` (GSI on `status` + `departureDate` for upcoming trips)
- [ ] Run `cdk synth` and verify CloudFormation template contains all tables
- [ ] Deploy to dev: `cd infra && npm run deploy -- --profile dev`
- [ ] Verify tables exist in AWS Console
- [ ] Update CDK tests to assert new tables and GSIs

**Why Critical**: Sync engine depends on SyncMetadata table for idempotency. Without this, duplicate operations will occur.

---

#### **Task A2: Network Layer Foundation (Dio Client)** ✅ COMPLETE
**Feature**: F-08 (Sync Engine dependency)
**Priority**: P0 (BLOCKER for sync)
**Effort**: 1 day
**Owner**: @Mobile
**Sprint**: Sprint 4, Week 1, Days 1-2
**Dependencies**: Lambda deployed (A2 from Sprint 3)
**Completed**: Feb 15, 2026

**Acceptance Criteria**:

**Phase 1: Basic Dio Setup (4 hours)**
- [x] Add `dio: ^5.4.3` to `mobile/pubspec.yaml`
- [ ] Create `mobile/lib/core/network/api_client.dart`:
  ```dart
  class ApiClient {
    final Dio _dio;
    final AuthService _authService;

    ApiClient({required AuthService authService})
      : _authService = authService,
        _dio = Dio(BaseOptions(
          baseUrl: const String.fromEnvironment('API_BASE_URL',
            defaultValue: 'https://api.weredaconnect.dev'),
          connectTimeout: const Duration(seconds: 10),
          receiveTimeout: const Duration(seconds: 30),
          headers: {
            'Content-Type': 'application/json',
            'Accept': 'application/json',
          },
        ));

    Future<Response> get(String path, {Map<String, dynamic>? queryParameters});
    Future<Response> post(String path, {dynamic data});
    Future<Response> put(String path, {dynamic data});
    Future<Response> delete(String path);
  }
  ```
- [ ] Create `mobile/lib/core/network/api_exception.dart`:
  - `NetworkException` (no internet)
  - `ServerException` (5xx errors)
  - `ClientException` (4xx errors)
  - `TimeoutException`
  - `UnauthorizedException` (401)

**Phase 2: Auth Token Interceptor (2 hours)**
- [ ] Create `mobile/lib/core/network/interceptors/auth_interceptor.dart`:
  - Inject access token from `AuthService` into `Authorization: Bearer <token>` header
  - On 401 response, attempt token refresh via `AuthService.refreshToken()`
  - Retry original request with new token
  - If refresh fails, trigger logout and redirect to login screen

**Phase 3: Logging Interceptor (1 hour)**
- [ ] Create `mobile/lib/core/network/interceptors/logging_interceptor.dart`:
  - Log request: method, URL, headers, body (redact sensitive fields)
  - Log response: status, headers, body
  - Only enable in debug mode (use `kDebugMode`)

**Phase 4: Environment Configuration (1 hour)**
- [ ] Create `mobile/lib/core/config/environment.dart`:
  ```dart
  class Environment {
    static const apiBaseUrl = String.fromEnvironment('API_BASE_URL',
      defaultValue: 'https://api.weredaconnect.dev');
    static const isProduction = bool.fromEnvironment('PRODUCTION', defaultValue: false);
  }
  ```
- [ ] Document in `mobile/README.md`: how to run with different environments
  - Dev: `flutter run --dart-define=API_BASE_URL=https://dev.api.weredaconnect.com`
  - Prod: `flutter run --dart-define=API_BASE_URL=https://api.weredaconnect.com --dart-define=PRODUCTION=true`

**Phase 5: Unit Tests (2 hours)**
- [ ] Create `mobile/test/core/network/api_client_test.dart`:
  - Test: GET request adds auth token
  - Test: POST request serializes body
  - Test: 401 triggers token refresh
  - Test: Network error throws `NetworkException`
  - Test: Timeout throws `TimeoutException`

**Why Critical**: All server communication depends on this. Sync engine, crop listing API calls, future features all use this client.

---

### Priority Group B: Sync Engine Phase A - Local Queue (Week 1, Days 3-5)

---

#### **Task B1: Sync Queue Service (Local Persistence)**
**Feature**: F-08 Phase A
**Priority**: P0 (BLOCKER for sync executor)
**Effort**: 2 days
**Owner**: @Mobile
**Sprint**: Sprint 4, Week 1, Days 3-4
**Dependencies**: A1 (SyncMetadata table), Isar schemas (done)

**Acceptance Criteria**:

**Phase 1: SyncQueue Class (1 day)**
- [ ] Create `mobile/lib/core/sync/sync_queue.dart`:
  ```dart
  class SyncQueue {
    final Isar _isar;

    Future<void> enqueue(SyncOperation operation);
    Future<List<SyncOperation>> getPendingOperations({int limit = 50});
    Future<void> markAsSyncing(String operationId);
    Future<void> markAsSynced(String operationId);
    Future<void> markAsFailed(String operationId, String errorMessage);
    Future<void> retryFailed({int maxRetries = 3});
    Future<int> getPendingCount();
  }
  ```
- [ ] Implement using Isar queries on `SyncOperation` collection
- [ ] Generate unique `operationId` (UUID v4)
- [ ] Generate `idempotencyKey` at action time: `{userId}:{entityType}:{entityId}:{action}:{timestamp}`
- [ ] Store operation with status `pending`, retryCount `0`

**Phase 2: Idempotency Key Generation (4 hours)**
- [ ] Create `mobile/lib/core/sync/idempotency_key_generator.dart`:
  ```dart
  class IdempotencyKeyGenerator {
    static String generate({
      required String userId,
      required String entityType,
      required String entityId,
      required String action, // 'create', 'update', 'delete'
    }) {
      final timestamp = DateTime.now().millisecondsSinceEpoch;
      return '$userId:$entityType:$entityId:$action:$timestamp';
    }
  }
  ```
- [ ] Document format in `specs/contracts/sync-protocol.md` (update existing contract)

**Phase 3: Unit Tests (4 hours)**
- [ ] Create `mobile/test/core/sync/sync_queue_test.dart`:
  - Test: Enqueue operation creates pending record
  - Test: GetPendingOperations returns sorted by timestamp ASC
  - Test: MarkAsSynced updates status
  - Test: RetryFailed only retries operations with retryCount < maxRetries
  - Test: Idempotency key format is correct

**Why Now**: This is the foundation of offline-first. All user actions that need sync go through this queue.

---

#### **Task B2: Connectivity Monitor**
**Feature**: F-08 Phase A
**Priority**: P0 (BLOCKER for sync executor)
**Effort**: 1 day
**Owner**: @Mobile
**Sprint**: Sprint 4, Week 1, Day 5
**Dependencies**: None

**Acceptance Criteria**:

**Phase 1: ConnectivityMonitor Class (4 hours)**
- [ ] Add `connectivity_plus: ^6.0.0` to `mobile/pubspec.yaml`
- [ ] Create `mobile/lib/core/sync/connectivity_monitor.dart`:
  ```dart
  class ConnectivityMonitor {
    final Connectivity _connectivity = Connectivity();
    Stream<ConnectivityResult> get onConnectivityChanged;

    Future<bool> isConnected();
    Future<bool> hasInternetAccess(); // Ping API endpoint to verify
  }
  ```
- [ ] `onConnectivityChanged` stream emits ConnectivityResult.mobile, wifi, none
- [ ] `hasInternetAccess()` pings `/health` endpoint with 3-second timeout

**Phase 2: Stream Debouncing (2 hours)**
- [ ] Debounce connectivity changes by 2 seconds (prevent rapid toggling)
- [ ] Use `RxDart` or manual timer-based debouncing

**Phase 3: Unit Tests (2 hours)**
- [ ] Create `mobile/test/core/sync/connectivity_monitor_test.dart`:
  - Test: isConnected returns true when wifi
  - Test: isConnected returns true when mobile data
  - Test: isConnected returns false when none
  - Test: hasInternetAccess pings API and returns true on 200
  - Test: hasInternetAccess returns false on timeout

**Why Now**: Sync executor needs to know when to start syncing. This is the trigger.

---

### Priority Group C: Sync Engine Phase B - Sync Executor (Week 2, Days 1-3)

---

#### **Task C1: Sync Executor (Batch Processing)**
**Feature**: F-08 Phase B
**Priority**: P0 (BLOCKER for sync endpoint)
**Effort**: 2 days
**Owner**: @Mobile
**Sprint**: Sprint 4, Week 2, Days 1-2
**Dependencies**: B1 (SyncQueue), B2 (ConnectivityMonitor), A2 (ApiClient)

**Acceptance Criteria**:

**Phase 1: SyncExecutor Class (1 day)**
- [ ] Create `mobile/lib/core/sync/sync_executor.dart`:
  ```dart
  class SyncExecutor {
    final SyncQueue _syncQueue;
    final ApiClient _apiClient;
    final ConnectivityMonitor _connectivityMonitor;

    Future<void> start(); // Listen to connectivity changes
    Future<void> syncNow(); // Manual sync trigger
    Future<SyncResult> executeBatch(List<SyncOperation> operations);
  }
  ```
- [ ] On connectivity detected, call `syncNow()`
- [ ] Batch operations: fetch up to 50 pending operations from queue
- [ ] For each operation, send to server via POST `/sync`
- [ ] Update operation status based on server response

**Phase 2: Retry Logic with Exponential Backoff (4 hours)**
- [ ] On failure, increment `retryCount`
- [ ] If `retryCount < 3`, schedule retry with exponential backoff:
  - Retry 1: 5 seconds
  - Retry 2: 15 seconds
  - Retry 3: 45 seconds
- [ ] If `retryCount >= 3`, mark as `failed` permanently
- [ ] Use WorkManager or Timer for retry scheduling

**Phase 3: Error Handling (4 hours)**
- [ ] On network error: mark as pending, retry
- [ ] On 401 Unauthorized: refresh token, retry
- [ ] On 409 Conflict: call conflict resolution handler (Phase D), mark as synced
- [ ] On 400 Bad Request: mark as failed (permanent), log error
- [ ] On 5xx Server Error: retry with backoff

**Phase 4: Unit Tests (4 hours)**
- [ ] Create `mobile/test/core/sync/sync_executor_test.dart`:
  - Test: syncNow fetches pending operations
  - Test: executeBatch sends POST /sync for each operation
  - Test: Retry increments retryCount on failure
  - Test: Exponential backoff delays are correct
  - Test: 3 failures mark operation as failed
  - Test: 409 Conflict triggers conflict handler

**Why Now**: This executes the queue. Without this, operations pile up but never sync.

---

#### **Task C2: Integration with User Actions**
**Feature**: F-08 Phase B
**Priority**: P1 (HIGH)
**Effort**: 1 day
**Owner**: @Mobile
**Sprint**: Sprint 4, Week 2, Day 3
**Dependencies**: B1 (SyncQueue)

**Acceptance Criteria**:

**Phase 1: Enqueue on Create/Update/Delete (4 hours)**
- [ ] Update `CropListingBLoC` (to be created in Task D1):
  - On `CreateCropListing` event, save to Isar, enqueue sync operation
  - On `UpdateCropListing` event, update Isar, enqueue sync operation
  - On `DeleteCropListing` event, delete from Isar, enqueue sync operation
- [ ] Enqueue format:
  ```dart
  SyncOperation(
    operationId: uuid.v4(),
    idempotencyKey: IdempotencyKeyGenerator.generate(...),
    entityType: 'CropListing',
    entityId: listing.listingId,
    action: 'create', // 'update', 'delete'
    data: listing.toJson(),
    status: SyncStatus.pending,
    retryCount: 0,
    createdAt: DateTime.now(),
  )
  ```

**Phase 2: Background Sync Worker (4 hours)**
- [ ] Create `mobile/lib/core/sync/sync_worker.dart`:
  - Use WorkManager to schedule periodic sync every 15 minutes (when online)
  - On app foreground, trigger manual sync
  - On connectivity restored, trigger manual sync

**Phase 3: Unit Tests (2 hours)**
- [ ] Test: Creating crop listing enqueues sync operation
- [ ] Test: Updating crop listing enqueues sync operation with correct idempotency key
- [ ] Test: Sync worker triggers sync on connectivity change

**Why Now**: User actions need to be captured and queued. This wires the queue into the app.

---

### Priority Group D: Sync Engine Phase C - Server Sync Endpoint (Week 2, Days 4-5)

---

#### **Task D1: Server Sync Function**
**Feature**: F-08 Phase C
**Priority**: P0 (BLOCKER for end-to-end sync)
**Effort**: 2 days
**Owner**: @Server
**Sprint**: Sprint 4, Week 2, Days 4-5
**Dependencies**: A1 (SyncMetadata table), F-07 server models

**Acceptance Criteria**:

**Phase 1: SyncFunction.cs Lambda Handler (1 day)**
- [ ] Create `server/src/Functions/SyncFunction.cs`:
  ```csharp
  public async Task<APIGatewayProxyResponse> HandleSync(APIGatewayProxyRequest request)
  {
      var syncRequest = JsonSerializer.Deserialize<SyncRequest>(request.Body);

      // 1. Check idempotency key in SyncMetadata table
      if (await _syncRepo.IdempotencyKeyExists(syncRequest.IdempotencyKey))
          return LambdaResponse.Ok("Already synced");

      // 2. Route to correct entity handler
      var result = syncRequest.EntityType switch {
          "CropListing" => await _cropRepo.Sync(syncRequest),
          "User" => await _userRepo.Sync(syncRequest),
          _ => throw new ArgumentException("Unknown entity type")
      };

      // 3. Store idempotency key with 7-day TTL
      await _syncRepo.StoreIdempotencyKey(syncRequest.IdempotencyKey);

      return result;
  }
  ```
- [ ] Create `server/src/Models/SyncRequest.cs`:
  ```csharp
  public record SyncRequest(
      string IdempotencyKey,
      string EntityType,
      string EntityId,
      string Action, // "create", "update", "delete"
      JsonElement Data,
      int Version
  );
  ```

**Phase 2: Idempotency Repository (4 hours)**
- [ ] Create `server/src/Repositories/SyncMetadataRepository.cs`:
  - `Task<bool> IdempotencyKeyExists(string key)`
  - `Task StoreIdempotencyKey(string key, int ttlDays = 7)`
  - Manual DynamoDB marshalling (AOT-safe)
  - Set `ExpiresAt` attribute for TTL

**Phase 3: Version-Based Conflict Detection (4 hours)**
- [ ] In `CropRepository.Sync()`:
  - Fetch existing item from DynamoDB by entityId
  - If server version > client version: return 409 Conflict with server data
  - If server version == client version: apply update, increment version
  - If item doesn't exist and action is "create": insert with version 1
- [ ] Return conflict response:
  ```json
  {
    "statusCode": 409,
    "body": {
      "error": "SYNC_CONFLICT",
      "serverVersion": 3,
      "serverData": { ... }
    }
  }
  ```

**Phase 4: Unit Tests (4 hours)**
- [ ] Create `server/tests/Functions/SyncFunctionTests.cs`:
  - Test: Duplicate idempotency key returns 200 (already synced)
  - Test: Create operation inserts item with version 1
  - Test: Update with matching version succeeds
  - Test: Update with outdated version returns 409 Conflict
  - Test: Delete operation removes item
  - Test: Unknown entity type returns 400

**Why Now**: Without the server endpoint, sync queue builds up but never clears.

---

#### **Task D2: API Gateway Route for Sync**
**Feature**: F-08 Phase C
**Priority**: P0 (BLOCKER)
**Effort**: 2 hours
**Owner**: @Infra
**Sprint**: Sprint 4, Week 2, Day 5
**Dependencies**: D1 (SyncFunction), F-16 (Lambda construct)

**Acceptance Criteria**:
- [ ] Add sync route to `infra/lib/stacks/api-stack.ts`:
  - POST /sync
  - Requires authentication (use existing Cognito authorizer)
  - Throttling: 100 req/s per user (higher than auth endpoints)
- [ ] Add SyncFunction to Lambda constructs with:
  - IAM: Read-write on SyncMetadata, CropsTable, UsersTable
  - Memory: 512MB
  - Timeout: 30s
- [ ] Update API Gateway integration tests
- [ ] Deploy: `cd infra && npm run deploy -- --profile dev`

**Why Now**: Endpoint must exist for mobile client to call.

---

### Sprint 5 (Mar 15-28): First Feature - Crop Listing Flow

---

### Priority Group E: Crop Listing - Mobile UI (Week 3, Days 1-3)

---

#### **Task E1: Crop Listing Creation Screen**
**Feature**: F-10
**Priority**: P0 (First user feature)
**Effort**: 2 days
**Owner**: @Mobile
**Sprint**: Sprint 5, Week 3, Days 1-2
**Dependencies**: Isar schemas (done), SyncQueue (B1)

**Acceptance Criteria**:

**Phase 1: CropListingBloc (1 day)**
- [ ] Create `mobile/lib/features/crop_listing/bloc/crop_listing_bloc.dart`:
  - Events: `CreateCropListing`, `UpdateCropListing`, `DeleteCropListing`, `LoadCropListings`
  - States: `CropListingInitial`, `CropListingLoading`, `CropListingCreated`, `CropListingError`, `CropListingsLoaded`
- [ ] On `CreateCropListing`:
  1. Save to Isar immediately (offline-first)
  2. Enqueue sync operation via `SyncQueue.enqueue()`
  3. Emit `CropListingCreated` state
- [ ] On `LoadCropListings`:
  1. Query Isar for all listings where `status != 'deleted'`
  2. Emit `CropListingsLoaded(listings)` state

**Phase 2: Create Listing Screen UI (1 day)**
- [ ] Create `mobile/lib/features/crop_listing/screens/create_crop_listing_screen.dart`:
  - **Crop Type Dropdown**: Teff, Wheat, Maize, Barley, Chickpea, Lentils (localized in Amharic/Oromo)
  - **Quantity Input**: Number field, label "Quintals" (ኩንታል in Amharic, Ku'intaala in Oromo)
  - **Price Input**: Number field, label "Birr per kg" (ብር በኪሎ)
  - **Location**: Auto-filled via GPS (with manual override dropdown for woreda)
  - **Description**: Multiline text field (optional)
  - **Audio Note Button**: Record 30-second voice note (defer implementation to R6, show placeholder)
  - **Save Button**: Large, green, icon of checkmark, label "Save" (አስቀምጥ / Olkaa'i)
- [ ] Form validation:
  - Crop type required
  - Quantity > 0
  - Price > 0
  - Location required (GPS or manual)
- [ ] On save, dispatch `CreateCropListing` event to BLoC
- [ ] Show snackbar: "Listing saved. Will sync when online." (localized)

**Phase 3: Unit Tests (4 hours)**
- [ ] Create `mobile/test/features/crop_listing/bloc/crop_listing_bloc_test.dart`:
  - Test: CreateCropListing saves to Isar
  - Test: CreateCropListing enqueues sync operation
  - Test: CreateCropListing emits CropListingCreated state
  - Test: LoadCropListings queries Isar and emits CropListingsLoaded

**Why Now**: This is the first user-visible feature. Proves the stack works end-to-end.

---

#### **Task E2: Crop Listing Browse Screen**
**Feature**: F-10
**Priority**: P1 (HIGH)
**Effort**: 1 day
**Owner**: @Mobile
**Sprint**: Sprint 5, Week 3, Day 3
**Dependencies**: E1 (CropListingBloc)

**Acceptance Criteria**:

**Phase 1: Browse Screen UI (6 hours)**
- [ ] Create `mobile/lib/features/crop_listing/screens/crop_listings_screen.dart`:
  - **App Bar**: Title "Crop Listings" (የሰብል ዝርዝር / Tarree Midhaan)
  - **Filter Chips**: Crop type filter (Teff, Wheat, All)
  - **List View**: Display listings as cards
    - Card shows: Crop icon, crop type, quantity, price, location (woreda), created date
    - Tap card to view details (defer detail screen to R4)
  - **Empty State**: "No listings yet. Create your first listing!" (localized)
  - **Floating Action Button**: "+" icon to create new listing (navigates to E1 screen)
- [ ] On screen load, dispatch `LoadCropListings` event
- [ ] BLoC listener updates UI on `CropListingsLoaded` state

**Phase 2: Offline Indicator (2 hours)**
- [ ] Show banner at top when offline: "Offline mode. Listings will sync when online." (ከመስመር ውጭ)
- [ ] Use `ConnectivityMonitor` to detect online/offline state
- [ ] Banner is yellow/orange with info icon

**Phase 3: Widget Tests (2 hours)**
- [ ] Create `mobile/test/features/crop_listing/screens/crop_listings_screen_test.dart`:
  - Test: Empty state shows when no listings
  - Test: Listings display in list
  - Test: FAB navigates to create screen
  - Test: Offline banner shows when offline

**Why Now**: Users need to see their listings. This completes the basic create + browse flow.

---

### Priority Group F: Crop Listing - Server & API (Week 3, Days 4-5)

---

#### **Task F1: Server Crop Listing Functions**
**Feature**: F-10
**Priority**: P0 (BLOCKER for online sync)
**Effort**: 2 days
**Owner**: @Server
**Sprint**: Sprint 5, Week 3, Days 4-5
**Dependencies**: F-07 server models (CropListing)

**Acceptance Criteria**:

**Phase 1: CropRepository (1 day)**
- [ ] Create `server/src/Repositories/CropRepository.cs`:
  ```csharp
  public interface ICropRepository {
      Task<Result<CropListing>> Create(CropListing listing);
      Task<Result<List<CropListing>>> GetAll(string? woreda = null, string? cropType = null);
      Task<Result<CropListing>> GetById(string listingId);
      Task<Result<CropListing>> Sync(SyncRequest request); // For sync endpoint
  }
  ```
- [ ] Manual DynamoDB marshalling (AOT-safe)
- [ ] Create operation: insert with version 1
- [ ] GetAll: query with optional filters on woreda/cropType using GSI
- [ ] Sync: handle create/update/delete with version checking

**Phase 2: CropFunction Lambda Handlers (1 day)**
- [ ] Create `server/src/Functions/CropFunction.cs`:
  - `HandleGetCrops(APIGatewayProxyRequest request)` — GET /crops
  - `HandleGetCropById(APIGatewayProxyRequest request)` — GET /crops/{id}
  - No CREATE endpoint — crops are created via /sync only (offline-first pattern)
- [ ] Input validation via `RequestValidator`:
  - Crop type must be in enum: Teff, Wheat, Maize, Barley, Chickpea, Lentils
  - Quantity > 0 and < 10000 quintals
  - Price > 0 and < 100000 birr/kg
- [ ] Authorization: requires valid JWT
- [ ] Rate limiting: 100 req/s (via API Gateway throttling)

**Phase 3: Unit Tests (4 hours)**
- [ ] Create `server/tests/Functions/CropFunctionTests.cs`:
  - Test: GetCrops returns all listings
  - Test: GetCrops with woreda filter returns filtered results
  - Test: GetCropById returns single listing
  - Test: GetCropById with invalid ID returns 404
  - Test: Unauthorized request returns 401

**Why Now**: Server must handle synced listings and serve them to other users.

---

#### **Task F2: API Gateway Routes for Crops**
**Feature**: F-10
**Priority**: P0 (BLOCKER)
**Effort**: 2 hours
**Owner**: @Infra
**Sprint**: Sprint 5, Week 3, Day 5
**Dependencies**: F1 (CropFunction)

**Acceptance Criteria**:
- [ ] Add crop routes to `infra/lib/stacks/api-stack.ts`:
  - GET /crops (query params: woreda, cropType)
  - GET /crops/{id}
- [ ] CropFunction IAM: Read-only on CropsTable
- [ ] Memory: 256MB (read-only, less memory needed)
- [ ] Timeout: 10s
- [ ] Deploy: `cd infra && npm run deploy -- --profile dev`

**Why Now**: API routes needed for mobile to fetch listings.

---

### Priority Group G: Sync Engine Phase D - Conflict Resolution (Week 4, Days 1-2)

---

#### **Task G1: Conflict Resolution Handler**
**Feature**: F-08 Phase D
**Priority**: P1 (HIGH for reliability)
**Effort**: 1.5 days
**Owner**: @Mobile
**Sprint**: Sprint 5, Week 4, Days 1-2
**Dependencies**: C1 (SyncExecutor), E1 (CropListingBLoC)

**Acceptance Criteria**:

**Phase 1: ConflictResolver Service (1 day)**
- [ ] Create `mobile/lib/core/sync/conflict_resolver.dart`:
  ```dart
  class ConflictResolver {
    Future<ConflictResolution> resolve({
      required SyncOperation operation,
      required dynamic serverData,
      required int serverVersion,
    });
  }

  enum ConflictStrategy { acceptServer, acceptLocal, showUserPrompt }

  class ConflictResolution {
    final ConflictStrategy strategy;
    final dynamic resolvedData;
  }
  ```
- [ ] Default strategy: **Accept server** (server wins)
- [ ] Update local Isar record with server data
- [ ] Discard local changes
- [ ] Mark sync operation as synced

**Phase 2: Conflict UI Dialog (Optional - Show User) (4 hours)**
- [ ] Create `mobile/lib/features/sync/widgets/conflict_dialog.dart`:
  - Show when conflict occurs on user-important entity (crop listing)
  - Display: Your version | Server version (side-by-side)
  - Buttons: "Keep Mine" | "Use Server's" | "Cancel"
  - Default to "Use Server's" after 10 seconds (auto-resolve)
- [ ] For MVP, skip UI and auto-resolve to server (document in ADR)

**Phase 3: Integration with SyncExecutor (2 hours)**
- [ ] In `SyncExecutor.executeBatch()`:
  - On 409 Conflict response, parse server data
  - Call `ConflictResolver.resolve()`
  - Apply resolved data to Isar
  - Mark operation as synced

**Phase 4: Unit Tests (2 hours)**
- [ ] Create `mobile/test/core/sync/conflict_resolver_test.dart`:
  - Test: AcceptServer strategy updates local Isar
  - Test: Conflict resolution marks operation as synced
  - Test: Server version > local version triggers conflict

**Why Now**: Conflicts will happen. Must handle gracefully to prevent data loss.

---

### Priority Group H: Security Hardening (Week 4, Days 3-4)

---

#### **Task H1: Least-Privilege IAM per Lambda**
**Feature**: F-17
**Priority**: P1 (HIGH for production readiness)
**Effort**: 1.5 days
**Owner**: @Infra
**Sprint**: Sprint 5, Week 4, Days 3-4
**Dependencies**: All Lambda functions deployed

**Acceptance Criteria**:

**Phase 1: IAM Policy Audit (4 hours)**
- [ ] Review current Lambda IAM roles in `infra/lib/constructs/lambda-construct.ts`
- [ ] Document current permissions per function
- [ ] Identify over-privileged functions

**Phase 2: Per-Function IAM Roles (1 day)**
- [ ] Create minimal IAM policies:
  - **HealthCheckFunction**: DynamoDB read-only on all tables
  - **RegisterFunction**: DynamoDB read-write on Users table only
  - **LoginFunction**: DynamoDB read-only on Users table
  - **RefreshFunction**: No DynamoDB (Cognito only)
  - **SyncFunction**: DynamoDB read-write on SyncMetadata, CropsTable, UsersTable (entity-specific)
  - **CropFunction (GET)**: DynamoDB read-only on CropsTable
  - **AuthorizerFunction**: No DynamoDB (JWT validation only)
- [ ] Update `lambda-construct.ts` to accept per-function IAM policies
- [ ] Remove wildcard permissions (`dynamodb:*` → specific actions)

**Phase 3: Deploy and Verify (2 hours)**
- [ ] Deploy to dev: `cd infra && npm run deploy -- --profile dev`
- [ ] Test each Lambda function still works
- [ ] Verify HealthCheck cannot write to DynamoDB (403 error on write attempt)
- [ ] Update CDK tests to assert IAM policies

**Why Now**: Production readiness. Least privilege limits blast radius of compromised Lambda.

---

### Priority Group I: Integration Testing & Polish (Week 4, Day 5)

---

#### **Task I1: End-to-End Integration Test**
**Feature**: F-08 + F-10 (Phase R3 validation)
**Priority**: P0 (CRITICAL for phase exit)
**Effort**: 1 day
**Owner**: @Mobile + @Server
**Sprint**: Sprint 5, Week 4, Day 5
**Dependencies**: All tasks above

**Acceptance Criteria**:

**Phase 1: Integration Test Scenario (6 hours)**
- [ ] Create `mobile/integration_test/sync_crop_listing_test.dart`:
  - Step 1: User creates crop listing OFFLINE (airplane mode on)
  - Step 2: Verify listing appears in Isar immediately
  - Step 3: Verify sync operation enqueued with status `pending`
  - Step 4: Turn connectivity ON (airplane mode off)
  - Step 5: Wait for sync executor to run (or trigger manually)
  - Step 6: Verify sync operation status changes to `syncing` → `synced`
  - Step 7: Verify listing appears on server (call GET /crops API)
  - Step 8: Another user fetches listings and sees the new listing
  - Step 9: Repeat 100 times (offline-online cycles) and verify zero data loss

**Phase 2: Conflict Test Scenario (4 hours)**
- [ ] Create `mobile/integration_test/sync_conflict_test.dart`:
  - Step 1: User A creates listing offline
  - Step 2: User B creates same listing offline (simulate conflict)
  - Step 3: User A syncs first (listing version 1 on server)
  - Step 4: User B syncs (server version 1 > client version 0)
  - Step 5: Verify User B receives 409 Conflict
  - Step 6: Verify User B's local data updates to server version
  - Step 7: Verify conflict marked as resolved

**Phase 3: Performance Test (2 hours)**
- [ ] Measure sync latency: from connectivity restored to sync complete
- [ ] Target: <5 seconds for 50 queued operations
- [ ] Measure on 2GB RAM Android device (emulator with resource limits)
- [ ] Document results in `docs/performance/sync-performance-metrics.md`

**Why Now**: This validates the entire Phase R3. If this passes, the architecture is proven.

---

#### **Task I2: Documentation and ADRs**
**Feature**: Phase R3 documentation
**Priority**: P2 (MEDIUM)
**Effort**: 4 hours
**Owner**: @Architect
**Sprint**: Sprint 5, Week 4, Day 5
**Dependencies**: All features implemented

**Acceptance Criteria**:
- [ ] Update `specs/contracts/sync-protocol.md`:
  - Add conflict resolution strategy documentation
  - Add idempotency key format specification
  - Add retry logic documentation
- [ ] Create `specs/decisions/adr-003-sync-engine-architecture.md`:
  - Decision: Local queue + executor pattern
  - Rationale: Offline-first, Ethiopian connectivity context
  - Alternatives considered: Direct API calls, background sync only
  - Consequences: More complexity, but resilient to network failures
- [ ] Create `specs/contracts/crop-listing-api.md`:
  - GET /crops endpoint specification
  - GET /crops/{id} endpoint specification
  - Sync via POST /sync pattern (no direct POST /crops)
  - Query parameters, response formats, error codes
- [ ] Update `STATE.md` with Phase R3 completion status
- [ ] Update `TASK.md` with Phase R4 preparation tasks

**Why Now**: Documentation ensures future developers (or future you) understand the design.

---

## 🚫 Sprint 4 & 5 Blockers

| Blocker | Impact | Probability | Mitigation |
|---------|--------|-------------|------------|
| **Sprint 3 incomplete (A1, A2, B4)** | 🔴 CRITICAL (blocks entire Sprint 4) | MEDIUM | **Day 0 of Sprint 4: Complete remaining Sprint 3 tasks.** Cannot start Sprint 4 without Lambda deployed and SyncMetadata table. |
| **AWS API Gateway throttling limits** | 🟡 MEDIUM (sync fails at scale) | LOW | Monitor CloudWatch metrics. If throttling occurs, request limit increase or implement client-side rate limiting. |
| **Connectivity detection unreliable on low-end devices** | 🟡 MEDIUM (sync doesn't trigger) | MEDIUM | Test on real devices (2GB RAM Android 8). Add manual "Sync Now" button in settings as fallback. |
| **Idempotency key collisions** | 🟡 MEDIUM (duplicate data) | LOW | Use UUID v4 + timestamp in key. Test collision probability. |
| **DynamoDB hot partition on SyncMetadata** | 🟡 MEDIUM (throttling) | LOW | SyncMetadata uses idempotency key as PK (high cardinality). If needed, add random suffix to distribute writes. |
| **Sync queue grows unbounded offline** | 🟡 MEDIUM (storage full) | MEDIUM | Implement queue size limit (1000 operations). Purge synced operations older than 30 days. |

---

## 📊 Sprint Success Metrics

### Sprint 4 (Sync Engine Foundation) - Must-Have
- [ ] SyncMetadata DynamoDB table deployed and verified
- [ ] Dio ApiClient created and tested
- [ ] SyncQueue can enqueue and retrieve operations
- [ ] ConnectivityMonitor detects online/offline state
- [ ] SyncExecutor sends operations to server
- [ ] Server /sync endpoint handles idempotency and version conflicts
- [ ] Retry logic works with exponential backoff
- [ ] Unit tests: 50+ tests covering sync engine components

### Sprint 5 (Crop Listing Flow) - Must-Have
- [ ] Crop listing create screen functional
- [ ] Crop listing browse screen functional
- [ ] Listings save to Isar immediately (offline-first)
- [ ] Listings sync to server when online
- [ ] GET /crops API returns listings
- [ ] Conflict resolution updates local data on 409 response
- [ ] IAM roles follow least-privilege principle
- [ ] Integration test: 100 offline-online cycles, zero data loss
- [ ] Integration test: Sync completes in <5s

### Phase R3 Exit Criteria (All Must Pass)
- ✅ Farmer can create crop listing OFFLINE on 2GB Android device
- ✅ Listing appears in local Isar database immediately
- ✅ When connectivity returns, listing syncs to server automatically
- ✅ Another user can browse listings from server
- ✅ Sync conflicts detected and resolved (server wins by default)
- ✅ Idempotency keys prevent duplicate listings on retry
- ✅ Each Lambda has minimum-necessary IAM permissions
- ✅ Zero data loss in 100 offline-online-offline cycles
- ✅ Sync completes within 5s of connectivity restoration

---

## 🔄 Definition of Done (Phase R3)

A task is "DONE" when:
1. ✅ Code is written and merged to `sync-engine-crop-listing` branch
2. ✅ Unit tests are written and passing (80%+ coverage for new code)
3. ✅ Integration tests validate end-to-end flow
4. ✅ Localized in all 3 languages (Amharic, Afan Oromo, English) for user-facing features
5. ✅ Works offline (if applicable)
6. ✅ Documented in `specs/contracts/` or `specs/decisions/`
7. ✅ Reviewed against CLAUDE.md constraints (offline-first, low-end devices, data efficiency)
8. ✅ Deployed to AWS dev environment (for server/infra changes)

**Phase R3 is "DONE" when**:
- All Must-Have sprint metrics achieved
- Phase R3 exit criteria (all 9 items) verified
- Integration test passes 100 cycles
- TASK.md updated with Phase R4 preparation
- STATE.md updated with Phase R3 completion
- Sprint retrospective completed and documented

---

## 📅 Detailed Sprint Timeline

### Sprint 4 (Mar 1-14): Sync Engine Foundation

#### Week 1: Infrastructure + Local Queue
| Day | Date | Tasks | Owner | Hours |
|-----|------|-------|-------|-------|
| **Fri 1** | Mar 1 | Pre-flight: Verify Sprint 3 complete (A1, A2, B4). Create branch. | All | 4h |
| **Mon 4** | Mar 4 | **A1**: Add DynamoDB tables (SyncMetadata + GSIs) | @Infra | 3h |
| **Mon 4** | Mar 4 | **A2**: Network layer - Dio client basics | @Mobile | 5h |
| **Tue 5** | Mar 5 | **A2**: Network layer - Auth interceptor + tests | @Mobile | 8h |
| **Wed 6** | Mar 6 | **B1**: SyncQueue service - Phase 1 | @Mobile | 8h |
| **Thu 7** | Mar 7 | **B1**: SyncQueue service - Phase 2+3 (tests) | @Mobile | 8h |
| **Fri 8** | Mar 8 | **B2**: ConnectivityMonitor | @Mobile | 8h |

**Week 1 Exit**: SyncQueue and ConnectivityMonitor functional. Network layer ready.

#### Week 2: Sync Executor + Server Endpoint
| Day | Date | Tasks | Owner | Hours |
|-----|------|-------|-------|-------|
| **Mon 11** | Mar 11 | **C1**: SyncExecutor - Phase 1 (class + batch logic) | @Mobile | 8h |
| **Tue 12** | Mar 12 | **C1**: SyncExecutor - Phase 2+3 (retry + error handling) | @Mobile | 8h |
| **Wed 13** | Mar 13 | **C2**: Integration with user actions | @Mobile | 6h |
| **Wed 13** | Mar 13 | **D1**: Server SyncFunction - Phase 1 (handler + idempotency) | @Server | 4h |
| **Thu 14** | Mar 14 | **D1**: Server SyncFunction - Phase 2+3 (conflict detection + tests) | @Server | 8h |
| **Fri 15** | Mar 15 | **D2**: API Gateway sync route + deploy | @Infra | 2h |

**Week 2 Exit**: Full sync engine loop working (mobile → server → conflict resolution).

---

### Sprint 5 (Mar 15-28): Crop Listing Flow

#### Week 3: Crop Listing UI + Server
| Day | Date | Tasks | Owner | Hours |
|-----|------|-------|-------|-------|
| **Mon 18** | Mar 18 | **E1**: CropListingBloc | @Mobile | 8h |
| **Tue 19** | Mar 19 | **E1**: Create listing screen UI + tests | @Mobile | 8h |
| **Wed 20** | Mar 20 | **E2**: Browse listings screen | @Mobile | 8h |
| **Thu 21** | Mar 21 | **F1**: CropRepository (server) | @Server | 8h |
| **Fri 22** | Mar 22 | **F1**: CropFunction Lambda handlers + tests | @Server | 8h |
| **Fri 22** | Mar 22 | **F2**: API Gateway crop routes + deploy | @Infra | 2h |

**Week 3 Exit**: Crop listing create and browse working. Server API deployed.

#### Week 4: Conflict Resolution + Polish
| Day | Date | Tasks | Owner | Hours |
|-----|------|-------|-------|-------|
| **Mon 25** | Mar 25 | **G1**: Conflict resolver service | @Mobile | 8h |
| **Tue 26** | Mar 26 | **G1**: Conflict resolution integration + tests | @Mobile | 4h |
| **Tue 26** | Mar 26 | **H1**: Least-privilege IAM audit | @Infra | 4h |
| **Wed 27** | Mar 27 | **H1**: Per-function IAM policies + deploy | @Infra | 8h |
| **Thu 28** | Mar 28 | **I1**: End-to-end integration tests | @Mobile + @Server | 8h |
| **Fri 29** | Mar 29 | **I2**: Documentation (ADRs, contracts) | @Architect | 4h |
| **Fri 29** | Mar 29 | Buffer: Bug fixes, polish, retrospective | All | 4h |

**Week 4 Exit**: Phase R3 complete. Integration tests passing. Ready for Phase R4.

---

## 🚀 Looking Ahead: Phase R4 Preview

**Likely Sprint 6-8 Goals (Apr 1 - May 10, ~5 weeks)**:
- **F-11**: Trip listing flow (create + browse + match)
- **F-12**: Delivery verification (QR + GPS + photo)
- **F-13**: Telebirr escrow integration (hold + release)
- **F-14**: Fraud detection checks
- **F-15**: Conflict resolution UI (visual diff)
- **F-18**: S3 access logging + versioning

**Phase R4 Milestone**: **Minimum Viable Product (MVP)**
- Full transaction flow: Crop → Trip → Delivery → Payment
- Farmers get paid through Telebirr escrow
- Platform can facilitate real transactions

**Dependencies**:
- Phase R3 must be 100% complete (all exit criteria met)
- Sync engine must be stable in production simulation
- Telebirr API sandbox access (coordinate early)

---

## 📝 Post-Sprint 5 Activities (Mar 29)

1. **Update STATE.md**:
   - Current checkpoint timestamp
   - Phase R3 status (should be 100%)
   - Blockers for Phase R4
   - Sync performance metrics

2. **Update TASK.md**:
   - Move R3 tasks to "Completed" section
   - Add Phase R4 tasks (F-11, F-12, F-13, F-14, F-15)
   - Update phase status summary

3. **Update ROADMAP.md**:
   - Mark R3 as COMPLETE
   - Update R4 timeline if behind schedule
   - Adjust estimates based on R3 learnings

4. **Create Sprint Retrospective**:
   - What went well? (Wins)
   - What blocked us? (Challenges)
   - What should we do differently in Sprint 6-8?
   - Estimate accuracy: planned vs actual hours
   - Document in `docs/retrospectives/sprint-4-5-retrospective.md`

5. **Demo Video** (optional but recommended):
   - Record 3-minute demo: create listing offline → go online → see sync happen → browse on another device
   - Share with stakeholders (even if solo dev, good for portfolio)

---

## 🎬 Sprint 4 Kickoff Checklist

Before starting Sprint 4 (Mar 1), verify:
- [ ] Sprint 3 is COMPLETE:
  - [ ] A1: Git history purged ✅
  - [ ] A2: Lambda deployed to AWS ✅
  - [ ] B4: SyncMetadata table deployed ✅
- [ ] AWS CLI configured and verified: `aws sts get-caller-identity`
- [ ] Flutter SDK installed and verified: `flutter doctor`
- [ ] Node.js 20.x LTS: `node --version`
- [ ] .NET 10 SDK: `dotnet --version`
- [ ] New branch created: `git checkout -b sync-engine-crop-listing`
- [ ] TASK.md updated with Sprint 4 tasks
- [ ] Team (even if solo) understands sprint goals

---

## 📊 Success Metrics Summary

### Sprint 4 Metrics
| Metric | Target | How Measured |
|--------|--------|--------------|
| SyncQueue operations enqueued | 100+ | Integration test creates 100 operations |
| Sync latency (connectivity → synced) | <5s | Measure in integration test |
| Retry success rate | >95% | Failed operations eventually sync after retry |
| Unit test coverage | >80% | Coverage report from `flutter test --coverage` |
| Server cold start (SyncFunction) | <2s | CloudWatch metrics |
| Zero data loss in offline cycles | 100/100 | Integration test with 100 cycles |

### Sprint 5 Metrics
| Metric | Target | How Measured |
|--------|--------|--------------|
| Crop listings created offline | 50+ | Integration test |
| Listings synced to server | 50/50 (100%) | Server GET /crops returns all |
| Conflict resolution success | 100% | No sync failures due to conflicts |
| IAM policies least-privilege | All Lambdas | Manual audit + tests |
| Mobile app size | <50MB | Build APK and check size |
| App load time on 3G | <3s | Manual test on low-end device |

---

**Document Version**: 2.0.0
**Created**: 2026-02-28
**Phase**: R3 - Sync Engine and First Feature
**Sprint Goal**: Make Offline-First Real
**Next Review**: 2026-03-14 (Sprint 4 mid-point check)
**Final Review**: 2026-03-29 (Sprint 5 retrospective)
**Total Estimated Effort**: ~20-25 days (solo developer, 4 weeks)
