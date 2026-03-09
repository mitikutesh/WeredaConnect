# Web Data Display & Action Bugs — Implementation Plan (v3 Final)

**Date**: 2026-03-09
**Branch**: `security-fix` → new branch `fix/web-listing-display`
**Priority**: CRITICAL — core functionality broken for all 3 roles on web

---

## What

Fix a systemic pattern of bugs across the web platform caused by three root patterns:
1. **Response key mismatches** — web parsers look for `data['items']` but server returns entity-specific keys
2. **Isar-on-web crashes** — screens call `IsarService.instance.db` without `kIsWeb` guards
3. **Server crop query** — uses DynamoDB SCAN instead of GSI (but GSI fix requires infra changes first)
4. **Web matching stub** — returns empty results instead of querying API

## Why

All three user roles (Farmer, Trucker, Buyer) have broken core workflows on web. The web app is non-functional for post-creation workflows.

---

## Safety Analysis

### Mobile App Impact: NONE

Changes are **web-only safe**. Verified:
- Mobile and web use **completely separate repository implementations** (Isar vs HTTP)
- All BLoCs depend on **abstract base classes**, not concrete repos
- `kIsWeb` is a **compile-time constant** — mobile builds eliminate web code paths entirely
- Mobile never calls `GET /crops`, `GET /trips` etc. — reads from local Isar cache only
- Pattern already established: `kIsWeb` used in 6+ files, 15+ locations across codebase

### Admin Dashboard Impact: NONE
- Admin does not call `GET /crops` or `GET /trips` endpoints

---

## Bug → Root Cause → Fix Mapping

| # | Bug | Root Cause | Fix Phase |
|---|-----|-----------|-----------|
| 1 | Farmer: crops don't show in My Listings | Server SCAN + GSI has INCLUDE projection (incomplete data) | Phase 3 |
| 2 | Trucker: My Trips empty (overview count correct) | Trip parser: `data['items']` vs server's `data['trips']` | Phase 1 |
| 3 | Buyer: Place Order / Contact Farmer dead | `_lookupFarmerName()` calls Isar → crashes on web | Phase 2 |
| 4 | Buyer: "No matches yet" always | Web matching stub returns `[]` | Phase 4 |
| 5 | Buyer: no trips visible | Same trip parser mismatch as #2 | Phase 1 |
| 6 | Buyer: deliveries page empty | Delivery parser: `data['items']` vs `data['deliveries']` | Phase 1 |
| 7 | Buyer: payments page empty | Escrow parser: `data['items']` vs `data['escrows']` | Phase 1 |
| 8 | Buyer: intents lost on refresh | In-memory only, no persistence | Phase 5 |

---

## Implementation Plan

### Phase 1: Fix All Response Key Mismatches

**Scope**: 3 files, 4 one-line changes
**Risk**: LOW — additive fallback, mobile unaffected (separate repos)
**Unblocks**: Bugs 2, 5, 6, 7

#### 1a. Trip parser
**File**: `mobile/lib/features/trip_listing/data/repositories/web_trip_listing_repository.dart`
**Line 231**:
```dart
// Before:
final items = data['items'] as List<dynamic>? ?? [];
// After:
final items = data['trips'] as List<dynamic>? ?? data['items'] as List<dynamic>? ?? [];
```

#### 1b. Delivery parser
**File**: `mobile/lib/features/delivery_verification/data/repositories/web_delivery_repository.dart`
**Line 313**:
```dart
// Before:
final items = data['items'] as List<dynamic>? ?? [];
// After:
final items = data['deliveries'] as List<dynamic>? ?? data['items'] as List<dynamic>? ?? [];
```

#### 1c. Escrow parser (2 locations)
**File**: `mobile/lib/features/escrow_payment/data/repositories/web_escrow_repository.dart`
**Lines 88 and 135**:
```dart
// Before:
final items = data['items'] as List<dynamic>? ?? [];
// After:
final items = data['escrows'] as List<dynamic>? ?? data['items'] as List<dynamic>? ?? [];
```

### Phase 2: Add kIsWeb Guards to Isar Call Sites

**Scope**: 2 files (home_screen.dart already guarded — verified)
**Risk**: LOW — follows established `kIsWeb` pattern used in 6+ files
**Unblocks**: Bugs 3, plus profile screen stability

#### 2a. Crop listing detail screen
**File**: `mobile/lib/features/crop_listing/presentation/screens/crop_listing_detail_screen.dart`

**`_lookupFarmerName()` (line 427)**: On web, use listing's `farmerName` instead of Isar lookup:
```dart
Future<String> _lookupFarmerName(String farmerId) async {
  if (kIsWeb) {
    return widget.listing.farmerName.isNotEmpty
        ? widget.listing.farmerName
        : farmerId;
  }
  final isar = IsarService.instance.db;
  final farmer = await isar.users.filter().userIdEqualTo(farmerId).findFirst();
  return farmer?.name ?? farmerId;
}
```

**`_loadSuggestedTrips()` (line 61)**: On web, skip Isar-based MatchingService — will be connected to API in Phase 4. For now, guard with `kIsWeb` to prevent crash:
```dart
if (kIsWeb) {
  // Phase 4 will implement API-based trip matching
  setState(() => _suggestedTrips = []);
  return;
}
```

#### 2b. Profile screen
**File**: `mobile/lib/screens/profile_screen.dart`

**`_getUser()` (line 732)**: On web, use auth state user data:
```dart
if (kIsWeb) {
  // Use user data from auth state, no Isar
  return;
}
```

**`_loadProfileStats()` (line 741)**: On web, call API endpoints for counts (same pattern as home_screen's `_loadWebStats()`).

**Debug reset dialog (line 830)**: On web, hide the reset button or no-op.

### Phase 3: Fix Server Crop Query

**Scope**: 2 files (server + infra)
**Risk**: MEDIUM — requires CDK deploy, but changes are additive
**Unblocks**: Bug 1

#### CRITICAL: GSI Projection Must Change First

The `FarmerIdCreatedAtIndex` GSI currently uses `INCLUDE` projection with only `cropType`, `pricePerKg`, `deletedAt`. Querying this GSI returns **incomplete items** — missing `woreda`, `status`, `quantity`, `version`, `updatedAt`, `description`, `location`. The DTO mapper would return broken data.

**Two options (choose one):**

**Option A: Change GSI projection to ALL (recommended)**
- **File**: `infra/lib/stacks/database-stack.ts` (lines 109-115)
- Change `projectionType: dynamodb.ProjectionType.INCLUDE` → `projectionType: dynamodb.ProjectionType.ALL`
- Remove `nonKeyAttributes` array
- Also change `CropTypeCreatedAtIndex` (lines 118-124) to ALL for consistency
- **Requires**: CDK deploy. DynamoDB will recreate the GSI and backfill. Dev table is small, takes seconds.
- **Trade-off**: Slightly more GSI storage cost (negligible at dev scale)

**Option B: Two-step query (no infra change)**
- Query GSI for `cropId` + `createdAt` keys only
- BatchGetItem from main table using those keys
- **Trade-off**: More code complexity, 2 DynamoDB calls instead of 1

**Recommended: Option A** — simpler, one-time CDK deploy.

#### Then: Update CropRepository

**File**: `server/src/Data/CropRepository.cs` (lines 266-351)

Replace the single SCAN path with conditional routing:
```
if (farmerId != null)  → Query on FarmerIdCreatedAtIndex (GSI PK = farmerId, SK = createdAt desc)
else if (status != null) → Query on StatusCreatedAtIndex (already ALL projection ✓)
else if (cropType != null) → Query on CropTypeCreatedAtIndex (after projection fix)
else → SCAN (unchanged — for unfiltered browse, paginated)
```

When multiple filters are provided (e.g., `farmerId` + `status`), use the most selective GSI and apply remaining filters as `FilterExpression`:
```
farmerId + status → Query FarmerIdCreatedAtIndex, filter on status
farmerId + cropType → Query FarmerIdCreatedAtIndex, filter on cropType
status + woreda → Query StatusCreatedAtIndex, filter on woreda
```

### Phase 4: Implement Web Matching Service via API

**Scope**: 2 files (two duplicate stubs to replace)
**Risk**: MEDIUM — new logic, needs API client injection
**Unblocks**: Bug 4

#### Context: Two Stubs Exist

1. `web_matching_service.dart` — used in `main_web.dart` (BLoC injection)
2. `matching_service_web.dart` — used via conditional import in `crop_listing_detail_screen.dart`

Both return `[]`. Both need real implementations.

#### 4a. Update `web_matching_service.dart`
**File**: `mobile/lib/features/buyer/domain/services/web_matching_service.dart`

- Add `ApiClient` constructor parameter (same pattern as `WebCropListingRepository`)
- `matchListingsForIntent(intent)`:
  1. Call `GET /crops?cropType={intent.cropType}&status=available`
  2. Parse response using `data['crops']` key
  3. Filter client-side: `pricePerKg <= intent.maxPricePerKg`
  4. Map to `CropMatchResult` objects
- `refreshMatchCounts(buyerId, repository)`:
  1. Get active intents from repository
  2. For each, call `matchListingsForIntent()` and update count

#### 4b. Update `matching_service_web.dart`
**File**: `mobile/lib/features/buyer/domain/services/matching_service_web.dart`

- Same API-based implementation for `matchListingsForIntent()`
- Implement `findTripsForListing(listing, deliveryWoreda, quantityNeeded)`:
  1. Call `GET /trips?status=open`
  2. Parse response using `data['trips']` key
  3. Filter client-side: destination woreda match + sufficient capacity

#### 4c. Update injection site
**File**: `mobile/lib/main_web.dart` (line 162)

```dart
// Before:
matchingService: WebMatchingService(),
// After:
matchingService: WebMatchingService(api: api),
```

Note: `crop_listing_detail_screen.dart` creates `MatchingService` locally (line 62) via conditional import — no injection change needed there, it picks up `matching_service_web.dart` automatically.

### Phase 5 (Optional): Persist Buyer Intents via localStorage

**Scope**: 1 file
**Risk**: LOW
**Unblocks**: Bug 8

**File**: `mobile/lib/features/buyer/data/repositories/web_buyer_intent_repository.dart`

Use `shared_preferences` (already a project dependency) to serialize intents as JSON and persist across page refreshes. Lower priority — intents are local-only by design.

---

## Complete File List

### Phase 1 — Response Key Fixes (3 files, web-only)
| File | Change | Mobile Impact |
|------|--------|---------------|
| `mobile/.../web_trip_listing_repository.dart` | `data['trips']` fallback (line 231) | None — separate repo |
| `mobile/.../web_delivery_repository.dart` | `data['deliveries']` fallback (line 313) | None — separate repo |
| `mobile/.../web_escrow_repository.dart` | `data['escrows']` fallback (lines 88, 135) | None — separate repo |

### Phase 2 — Isar Guards (2 files, guarded by kIsWeb)
| File | Change | Mobile Impact |
|------|--------|---------------|
| `mobile/.../crop_listing_detail_screen.dart` | `kIsWeb` guard on lines 61, 427 | None — compile-time eliminated |
| `mobile/.../profile_screen.dart` | `kIsWeb` guard on lines 732, 741, 830 | None — compile-time eliminated |

### Phase 3 — Server Crop Query (2 files)
| File | Change | Mobile Impact |
|------|--------|---------------|
| `infra/lib/stacks/database-stack.ts` | GSI projections INCLUDE → ALL | None — data layer only |
| `server/src/Data/CropRepository.cs` | SCAN → conditional GSI Query | None — mobile reads from Isar |

### Phase 4 — Web Matching (3 files, web-only)
| File | Change | Mobile Impact |
|------|--------|---------------|
| `mobile/.../web_matching_service.dart` | API-based matching + ApiClient param | None — mobile uses `MatchingService(isar:)` |
| `mobile/.../matching_service_web.dart` | API-based matching (conditional import) | None — mobile imports `matching_service.dart` |
| `mobile/lib/main_web.dart` | Pass `api` to `WebMatchingService` | None — mobile uses `run_mobile.dart` |

### Phase 5 — Intent Persistence (1 file, optional, web-only)
| File | Change | Mobile Impact |
|------|--------|---------------|
| `mobile/.../web_buyer_intent_repository.dart` | Add localStorage persistence | None — mobile uses Isar repo |

### Tests
| File | Change |
|------|--------|
| `server/tests/Data/CropRepositoryTests.cs` | Add GSI query path tests (Phase 3) |
| New: `mobile/test/.../web_trip_listing_repository_test.dart` | Parser key test |
| New: `mobile/test/.../web_matching_service_test.dart` | API matching test |

---

## Implementation Order & Dependencies

```
Phase 1 (key fixes, trivial)     ──┐── independent, can parallel
Phase 2 (Isar guards, low)       ──┘

Phase 3 (server + CDK)           ── requires CDK deploy first, then server change

Phase 4 (web matching)           ── independent of 1-3, but 2a sets up the guard pattern

Phase 5 (intent persistence)     ── independent, lowest priority
```

**Phases 1 & 2**: Independent, do in parallel. Pure client-side, no deploy needed.
**Phase 3**: Requires CDK deploy (GSI projection change) → then server code change → then server deploy.
**Phase 4**: Can start anytime after Phase 2a (which establishes the kIsWeb pattern in the detail screen).
**Phase 5**: Independent, optional.

---

## Testing Plan

### Per-Role Verification (Web)

**Farmer**:
- [ ] Create crop → appears in My Listings
- [ ] My Listings filter by crop type works
- [ ] Profile page loads without crash
- [ ] Overview stats match actual list counts

**Trucker**:
- [ ] Create trip → appears in My Trips
- [ ] Overview count matches My Trips count
- [ ] Deliveries page shows history (not empty)
- [ ] Payments page shows history (not empty)

**Buyer**:
- [ ] Browse Crops → see all available listings
- [ ] Select crop → Place Order navigates correctly
- [ ] Select crop → Contact Farmer works
- [ ] Create buying intent → shows matches when matching crops exist
- [ ] Browse trips → see all open trips
- [ ] Deliveries page shows history
- [ ] Payments page shows history

### Cross-Role (Web):
- [ ] Farmer creates crop → Buyer sees it in Browse
- [ ] Trucker creates trip → Buyer sees it in trip browse

### Mobile Regression:
- [ ] `flutter analyze` passes (no new warnings)
- [ ] `flutter test` passes (no regressions)
- [ ] Farmer/Trucker/Buyer flows unchanged on mobile
- [ ] Offline mode still works on mobile

### Server:
- [ ] `dotnet build --no-restore` passes
- [ ] `dotnet test --no-restore` passes
- [ ] `GET /crops?farmerId=X` returns complete DTOs (after Phase 3)
- [ ] `GET /crops?status=available` still returns all available crops

---

## Risks & Mitigations

| Risk | Severity | Mitigation |
|------|----------|-----------|
| GSI projection change recreates index | LOW | Dev table is small. Backfill takes seconds. No downtime. |
| Web matching is client-side (won't scale) | LOW | Acceptable for MVP. Server-side matching is future work. |
| Two matching service stubs could drift | LOW | Both get same API-based implementation. Consider consolidating to one file later. |
| Profile screen API calls add latency | LOW | Web assumes connectivity. Cache in widget state. |
| `kIsWeb` in shared screens | NONE | Compile-time constant. Mobile builds eliminate web paths. Already used in 15+ places. |
| Parser fallback `data['items']` kept | NONE | Defensive — if server ever changes key format, fallback catches it. |

---

## Changes NOT Needed (Verified)

| Component | Why No Change |
|-----------|--------------|
| `home_screen.dart` | Already has `kIsWeb` guards routing to `_loadWebStats()` and `_loadWebActivity()` |
| Mobile repositories (Isar-based) | Completely separate from web repos, abstract base class isolation |
| BLoCs | Depend on abstract interfaces, not concrete repos |
| Admin dashboard | Does not call crop/trip/delivery APIs |
| Mobile sync flow | Uses `POST /sync` for writes, Isar for reads — never parses GET responses |
| `StatusCreatedAtIndex` GSI | Already uses `ALL` projection — safe for buyer browse queries |
