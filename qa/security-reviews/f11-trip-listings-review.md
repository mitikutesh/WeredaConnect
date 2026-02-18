# F-11 Trip Listings — Security Review

## Version
1.0.0 | 2026-02-18

## Reviewer
security-agent (automated security review)

## Scope
Code-level security review of the F-11 Trip Listings feature covering:
- Server: `SyncFunction.cs` (trip CRUD via POST /sync)
- Server: `TripFunction.cs` (GET /trips, GET /trips/{id})
- Server: `TripRepository.cs` (DynamoDB queries)
- Server: `PaginationTokenHelper.cs` (HMAC-signed pagination tokens)
- Server: `TripDtos.cs` (data transfer objects)
- Server: `LambdaResponse.cs` (response security headers)
- Mobile: `trip_listing_repository.dart` (offline-first CRUD)
- Mobile: `trip_listing_bloc.dart` (state management)

## Verdict

**APPROVED WITH CONDITIONS**

Core security controls are solid. No MUST FIX items. Six SHOULD FIX items should be addressed during implementation or shortly after. Three INFO items are noted for awareness.

---

## Summary of Controls Verified

| Control | Status | Notes |
|---------|--------|-------|
| JWT authentication (POST /sync) | PASS | Extracts `sub` from API Gateway authorizer context, not raw headers |
| Trip ownership (truckerId == JWT sub) | PASS | Enforced on create, update, and delete |
| Field whitelist (AllowedTripFields) | PASS | Unknown fields rejected with error |
| Status transition validation | PASS | `ValidTransitions` dictionary enforces valid state machine |
| Terminal state protection (cancelled) | PASS | Blocks updates/deletes on cancelled and soft-deleted trips |
| Phone privacy (IsAuthenticated) | PASS | Only trusts `RequestContext.Authorizer`, not raw Authorization header |
| DynamoDB injection prevention | PASS | All queries use parameterized `ExpressionAttributeValues` |
| HMAC pagination tokens | PASS | HMAC-SHA256 signed, timing-safe comparison via `CryptographicOperations.FixedTimeEquals` |
| Security response headers | PASS | HSTS, X-Content-Type-Options, X-Frame-Options, Cache-Control: no-store |
| Error message safety | PASS | Generic "Internal server error" for caught exceptions; no PII leakage |
| Rate limiting | PASS | `RateLimitService.CheckSyncAllowedAsync()` per authenticated user |
| Batch size limit | PASS | Max 50 operations per sync request |
| Soft delete (cancelled trips) | PASS | `deletedAt` + `status=cancelled` set atomically in delete handler |
| Optimistic concurrency (versioning) | PASS | `ConditionExpression = "#v = :expectedVersion"` on all updates |
| Idempotency key deduplication | PASS | Checked before processing, stored after success/conflict |

---

## Findings

### F-SEC-01 — Departure date validation missing future/30-day constraint

| Field | Value |
|-------|-------|
| **ID** | F-SEC-01 |
| **Severity** | SHOULD FIX |
| **Affected File** | `server/src/Functions/SyncFunction.cs` lines 1092-1107 |
| **Category** | Input Validation |

**Description:**
`ValidateTripFields()` validates that `departureDate` is a valid ISO 8601 string, but does NOT enforce:
1. Departure date must be in the future (for create action)
2. Departure date must be within 30 days from now

The API contract (Validation Rules #2) explicitly requires both constraints.

**Risk:** A trucker could create trips with past departure dates or dates far in the future, polluting browse results and degrading data quality.

**Recommended Fix:**
Add to `ValidateTripFields()` when `isCreate == true`:
```csharp
if (isCreate && DateTime.TryParse(dateObj.ToString(), null,
    DateTimeStyles.RoundtripKind, out var parsedDate))
{
    if (parsedDate <= DateTime.UtcNow)
        errors.Add("departureDate must be in the future for create.");
    else if (parsedDate > DateTime.UtcNow.AddDays(30))
        errors.Add("departureDate must be within 30 days.");
}
```

---

### F-SEC-02 — Phone number E.164 validation insufficient

| Field | Value |
|-------|-------|
| **ID** | F-SEC-02 |
| **Severity** | SHOULD FIX |
| **Affected File** | `server/src/Functions/SyncFunction.cs` lines 1117-1131 |
| **Category** | Input Validation |

**Description:**
`truckerPhone` validation only checks `StartsWith("+251")`. This accepts invalid values like `+251`, `+251abc`, or `+25191234567890000` (arbitrarily long).

Ethiopian E.164 phone numbers are exactly 13 characters: `+251XXXXXXXXX` (9 digits after country code).

**Risk:** Invalid phone numbers stored in database; potential abuse via oversized string storage.

**Recommended Fix:**
```csharp
var phone = phoneObj.ToString()!;
if (!System.Text.RegularExpressions.Regex.IsMatch(phone, @"^\+251\d{9}$"))
{
    errors.Add("truckerPhone must be valid Ethiopian E.164 format (+251XXXXXXXXX).");
}
```

---

### F-SEC-03 — GET /trips/{id} does not check status=cancelled

| Field | Value |
|-------|-------|
| **ID** | F-SEC-03 |
| **Severity** | SHOULD FIX |
| **Affected File** | `server/src/Functions/TripFunction.cs` lines 204-208 |
| **Category** | Broken Access Control |

**Description:**
`HandleGetTripByIdAsync()` checks for `deletedAt` to return 404, but does NOT check `status == "cancelled"`. If a trip is cancelled via a status update operation (e.g., `open -> cancelled`), the trip will have `status=cancelled` but no `deletedAt` attribute. This trip would still be returned by GET /trips/{id}.

The delete handler always sets BOTH `deletedAt` and `status=cancelled`, so the primary cancellation flow works. However, the status transition path (`update` with `status: "cancelled"`) does not set `deletedAt`.

**Risk:** Cancelled trips accessible via direct ID lookup when cancelled via status update. Contract specifies 404 for cancelled trips.

**Recommended Fix:**
Add status check after the `deletedAt` check:
```csharp
// Check if cancelled (via status update path, which doesn't set deletedAt)
if (trip.TryGetValue("status", out var statusVal)
    && statusVal?.ToString() == "cancelled")
{
    return LambdaResponse.Error(404, "TRIP_NOT_FOUND", "Trip listing has been cancelled.");
}
```

---

### F-SEC-04 — Mobile repository lacks client-side validation

| Field | Value |
|-------|-------|
| **ID** | F-SEC-04 |
| **Severity** | SHOULD FIX |
| **Affected File** | `mobile/lib/features/trip_listing/data/repositories/trip_listing_repository.dart` lines 28-88 |
| **Category** | Client-Side Validation |

**Description:**
`createTrip()` saves to Isar and enqueues a sync operation without any input validation:
- No check that `fromWoreda != toWoreda`
- No capacity range check (0.1-1000)
- No pricePerQuintal range check (0.01-10000)
- No departureDate future check
- No truckerPhone format check (+251XXXXXXXXX)
- No truckerName length check (1-100)

Similarly, `cancelTrip()` doesn't validate the trip's current status allows cancellation (e.g., prevents cancelling a `delivered` trip locally).

**Risk:** Invalid operations enqueued to the sync queue will fail server-side, wasting bandwidth (expensive mobile data) and degrading UX (user sees success locally but sync fails). The contract explicitly states: "Validate before enqueueing sync operation (prevent invalid operations from clogging queue)."

**Recommended Fix:**
Add a `_validateTripData()` method that mirrors server-side rules. Throw a descriptive exception on failure so the BLoC can show localized error messages.

---

### F-SEC-05 — Pagination token has no expiration

| Field | Value |
|-------|-------|
| **ID** | F-SEC-05 |
| **Severity** | SHOULD FIX |
| **Affected File** | `server/src/Helpers/PaginationTokenHelper.cs` |
| **Category** | Token Management |

**Description:**
The contract specifies "Token expiration: 24 hours (after which, query restarts from beginning)." The current HMAC-signed token does not include a timestamp. A valid pagination token can be reused indefinitely.

**Risk:** Low. Stale pagination tokens could reference DynamoDB positions that no longer contain relevant data, leading to unexpected results but not a security vulnerability per se.

**Recommended Fix:**
Include an `exp` (expiration) field in the token payload:
```csharp
tokenData["__exp"] = DateTimeOffset.UtcNow.AddHours(24).ToUnixTimeSeconds().ToString();
```
On decode, check if current time exceeds `__exp`.

---

### F-SEC-06 — Updates to delivered trips not blocked

| Field | Value |
|-------|-------|
| **ID** | F-SEC-06 |
| **Severity** | SHOULD FIX |
| **Affected File** | `server/src/Functions/SyncFunction.cs` lines 728-748 |
| **Category** | Business Logic |

**Description:**
The H3 control blocks updates to `cancelled` and `deletedAt` trips, but does NOT block updates to `delivered` trips. The architecture document declares `delivered` as a terminal state. A trucker could modify non-status fields (fromWoreda, capacity, pricePerQuintal, etc.) on a delivered trip.

**Risk:** Delivered trip audit trail can be modified after completion, potentially impacting dispute resolution.

**Recommended Fix:**
Add `delivered` to the terminal state check:
```csharp
if (currentStatus is "cancelled" or "delivered")
{
    return new SyncOperationResult { ... Message = "Cannot update a terminal-state trip." };
}
```

---

### F-SEC-07 — No Unicode normalization for woreda names

| Field | Value |
|-------|-------|
| **ID** | F-SEC-07 |
| **Severity** | INFO |
| **Affected File** | `server/src/Functions/SyncFunction.cs`, `server/src/Data/TripRepository.cs` |
| **Category** | Data Integrity (Ethiopian Context) |

**Description:**
Woreda names containing Amharic/Oromo characters are stored and compared as-is without Unicode normalization (NFC/NFD). The same woreda could be stored with different Unicode representations (e.g., composed vs. decomposed characters), causing filter mismatches in DynamoDB queries.

**Risk:** Low for MVP. Ethiopian text input methods typically produce NFC-normalized strings. Could become relevant with copy-paste from external sources.

**Recommendation:**
Consider applying `string.Normalize(NormalizationForm.FormC)` to woreda fields before storage. Not blocking.

---

### F-SEC-08 — PaginationTokenHelper default signing key

| Field | Value |
|-------|-------|
| **ID** | F-SEC-08 |
| **Severity** | INFO |
| **Affected File** | `server/src/Helpers/PaginationTokenHelper.cs` lines 21-23 |
| **Category** | Key Management |

**Description:**
Falls back to hardcoded `"WeredaConnect-Pagination-Default-Key-v1"` if `PAGINATION_SECRET` env var is not set. Already noted with a TODO comment.

**Risk:** Acceptable for MVP/development. Must be configured via SSM Parameter Store before production deployment.

**Recommendation:**
Track as a pre-production checklist item. The key is hashed with SHA256 before use, so the default string is not directly exposed.

---

### F-SEC-09 — DeletedAt field exposed in TripDto

| Field | Value |
|-------|-------|
| **ID** | F-SEC-09 |
| **Severity** | INFO |
| **Affected File** | `server/src/Models/TripDtos.cs` line 114 |
| **Category** | Data Exposure |

**Description:**
`TripDto.DeletedAt` is included in the DTO and serialized in API responses. Since deleted trips are filtered out in GET /trips (via `attribute_not_exists(deletedAt)`) and return 404 on GET /trips/{id}, this field will always be `null` in practice.

**Risk:** None. The field is nullable and only appears as null in valid responses. No action needed.

---

## DynamoDB Injection Analysis

All DynamoDB operations in `TripRepository.cs` and `SyncFunction.cs` use parameterized expressions:

| Operation | Expression Type | User Input Handling | Safe? |
|-----------|----------------|---------------------|-------|
| GSI Query (QueryTripsAsync) | `KeyConditionExpression = "#status = :status"` | Status passed via `ExpressionAttributeValues` | YES |
| Woreda filter | `FilterExpression = "#fromWoreda = :fromWoreda"` | Woreda passed via `ExpressionAttributeValues` | YES |
| Trip create (PutItem) | `ConditionExpression = "attribute_not_exists(tripId)"` | No user input in expression | YES |
| Trip update (UpdateItem) | `SET #f0 = :f0, ...` | Field names from `AllowedTripFields` whitelist; values via `ExpressionAttributeValues` | YES |
| Trip delete (UpdateItem) | `ConditionExpression = "#v = :expectedVersion"` | Version via `ExpressionAttributeValues` | YES |
| GetItemAsync (Query) | `KeyConditionExpression = "tripId = :id"` | ID via `ExpressionAttributeValues` | YES |

**Verdict: No DynamoDB injection vectors found.** All user-supplied values flow through `ExpressionAttributeValues`. Field names in update expressions use indexed aliases (`#f0`, `#f1`, ...) mapped from the `AllowedTripFields` whitelist, preventing attribute name injection.

---

## SSRF Analysis

Woreda name fields (`fromWoreda`, `toWoreda`) accept free-text strings up to 100 characters. These values are:
1. Stored in DynamoDB as string attributes
2. Used in DynamoDB `FilterExpression` via parameterized values
3. Returned in API responses as JSON strings

At no point are woreda values used to construct URLs, make HTTP requests, or access external resources. **No SSRF vectors found.**

---

## Findings Summary

| ID | Severity | Finding | Blocks Implementation? |
|----|----------|---------|----------------------|
| F-SEC-01 | SHOULD FIX | Departure date missing future/30-day validation | No |
| F-SEC-02 | SHOULD FIX | Phone E.164 validation insufficient | No |
| F-SEC-03 | SHOULD FIX | GET /trips/{id} missing status=cancelled check | No |
| F-SEC-04 | SHOULD FIX | Mobile repository lacks client-side validation | No |
| F-SEC-05 | SHOULD FIX | Pagination token has no expiration | No |
| F-SEC-06 | SHOULD FIX | Updates to delivered trips not blocked | No |
| F-SEC-07 | INFO | No Unicode normalization for woreda names | No |
| F-SEC-08 | INFO | Default pagination signing key (noted TODO) | No |
| F-SEC-09 | INFO | DeletedAt field exposed in DTO (harmless) | No |

**MUST FIX: 0** | **SHOULD FIX: 6** | **INFO: 3**

---

## Conditions for Approval

Implementation may proceed. The following SHOULD FIX items should be addressed during implementation (Tasks #3-#6):

1. **Server (Task #3):** Address F-SEC-01 (departure date validation), F-SEC-02 (phone E.164), F-SEC-03 (cancelled trip 404), F-SEC-06 (delivered terminal state)
2. **Mobile (Task #5):** Address F-SEC-04 (client-side validation)
3. **Infrastructure (Task #4):** Ensure `PAGINATION_SECRET` env var is configured in Lambda (F-SEC-08)
4. **F-SEC-05** (pagination token expiration) can be deferred to post-MVP

---

**Last Updated:** 2026-02-18
**Contract Reference:** `specs/contracts/trip-listing-api.md` v1.0.0
**Architecture Reference:** `specs/architecture/f11-trip-listings-plan.md` v1.0.0
