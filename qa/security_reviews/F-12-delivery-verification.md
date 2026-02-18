# Security Review: F-12 Delivery Verification

Date: 2026-02-18
Reviewer: mobile-security
Version: 1.0
Status: PASS WITH FINDINGS

## Scope

- Files reviewed (with full paths):
  1. `server/src/Functions/DeliveryFunction.cs` -- GET /deliveries, GET /deliveries/{id} handler (422 LOC)
  2. `server/src/Data/DeliveryRepository.cs` -- DynamoDB delivery repository (154 LOC)
  3. `server/src/Functions/SyncFunction.cs` -- ProcessDeliveryAsync section, delivery create/update/delete via /sync (1668 LOC total; ~530 LOC delivery-specific: lines 1099-1627)
  4. `server/src/Models/DeliveryDtos.cs` -- DTOs for delivery data (186 LOC)
  5. `mobile/lib/features/delivery_verification/data/repositories/delivery_repository.dart` -- Mobile offline-first repository (305 LOC)
  6. `mobile/lib/features/delivery_verification/presentation/bloc/delivery_bloc.dart` -- BLoC state management (166 LOC)
  7. `mobile/lib/core/sync/conflict_resolver.dart` -- Conflict resolution logic (257 LOC)
- Supporting files read: `delivery_event.dart`, `delivery.dart` (Isar schema)
- Approximate lines of code reviewed: ~3,158 (direct) + ~250 (supporting)
- Commits covered (branch: `trips-delivery-escrow`): `3044ff9` through `a07142a`

## Checklist (from qa/README.md)

### Authentication & Authorization
- [x] JWT tokens validated and not expired -- API Gateway JWT authorizer configured; `DeliveryFunction.IsAuthenticated` checks for Authorization header or authorizer claims; `SyncFunction` extracts `sub`/`userId` from authorizer and rejects with 401 if missing.
- [x] Authorization checks on all API endpoints -- GET /deliveries filters by participant; GET /deliveries/{id} checks `IsParticipant`; sync create requires truckerId match; sync update enforces role-based transitions; sync delete requires trucker ownership.
- [x] No hardcoded credentials in code -- Table names read from environment variables; no secrets in source.

### Data Privacy
- [x] Sensitive data encrypted at rest and in transit -- DynamoDB encryption at rest (AWS default); HTTPS via API Gateway. Phone numbers hidden from non-participants in list and detail views (`includePhone: false` in list, conditional in detail).
- [x] Minimal data collection; user consent obtained -- Only collects delivery-relevant data. GPS coordinates captured only at pickup/dropoff events.

### Input Validation
- [~] All user input validated (length, type, format) -- GPS coordinates validated (-90/90, -180/180) with NaN/Infinity checks. Field whitelisting via `AllowedDeliveryFields`. Status transitions validated. However, `disputeReason` has no length limit (see Finding F-3). Photo ref strings have no format/content-type validation (see Finding F-4).
- [x] Injection prevention (SQL, XSS) -- DynamoDB parameterized expressions used throughout; no raw string concatenation in queries. `ExpressionAttributeNames` and `ExpressionAttributeValues` used correctly.
- [~] File upload validation (type, size, content) -- Photo refs (`pickupPhotoRef`, `dropoffPhotoRef`) are stored as plain strings with no validation of content type, path format, or size constraints (see Finding F-4).

### API Security
- [x] Rate limiting enabled -- API Gateway provides request throttling at the stage level.
- [x] Idempotency keys for write operations -- `SyncFunction` checks `IdempotencyKeyExistsAsync` before processing; stores keys with 7-day TTL.
- [x] Error messages don't leak sensitive info -- All error responses use generic messages (e.g., "Delivery not found", "Internal server error"); stack traces logged server-side only.

### Mobile Security
- [x] No sensitive data in logs -- BLoC and repository do not log delivery data. Exception messages propagated to UI state are generic.
- [~] Secure storage for tokens -- QR token is stored in Isar (unencrypted local database). On a rooted device, the QR token could be extracted (relates to T4.3 in threat model; broader issue, not delivery-specific).
- [~] Obfuscation enabled in release builds -- Not assessed in this file-level review; build configuration is out of scope.

### Infrastructure
- [~] IAM roles follow least privilege -- Not directly assessed (CDK stack not in review scope). Lambda functions use environment variables for table names, indicating per-function configuration.
- [~] Secrets in AWS Secrets Manager -- No secrets required by delivery feature specifically. Table names are non-sensitive config.
- [~] CloudWatch alarms for suspicious activity -- Lambda logger used for errors; alarm configuration not in review scope.

## Threat Model Cross-Reference

| Threat ID | Title | Relevance to F-12 | Status |
|-----------|-------|-------------------|--------|
| T1.1 | Attacker Impersonates Farmer | Delivery create requires truckerId match to JWT `sub`. Attacker cannot create deliveries on behalf of others. | MITIGATED for this feature |
| T1.2 | Session Hijacking | JWT authorizer validates tokens. Delivery endpoints require valid auth. | MITIGATED at API Gateway layer |
| T2.1 | Data Manipulation | Field whitelisting (`AllowedDeliveryFields`), status transition validation, version-based optimistic concurrency all prevent tampering. Status forced to "pending" on create. | MITIGATED |
| T2.2 | Local Database Tampering | Isar is unencrypted. Rooted device could modify delivery status/QR token locally. Server-side validation catches invalid transitions on sync. | PARTIALLY MITIGATED (server validates on sync) |
| T4.1 | Location Tracking | GPS coordinates stored only at pickup/dropoff events. Coordinates validated for range but not fuzzed. | ACCEPTED (exact GPS needed for delivery verification) |
| T4.3 | Unencrypted Mobile DB | QR tokens, phone numbers, GPS data stored in unencrypted Isar. | KNOWN RISK (platform-wide; not delivery-specific) |
| T5.1 | API DDoS | Sync batch limited to 50 operations. Query pagination limited to 50 items. | PARTIALLY MITIGATED |
| T6.1 | Unauthorized Data Access | Participant-only filtering on GET endpoints. Role-based transition enforcement on status changes. Non-status updates restricted to trucker. | MITIGATED |
| T7.2 | Shared Device Access | Multiple users on same device could see each other's deliveries in Isar. No local per-user isolation. | KNOWN RISK (platform-wide) |

## Findings

### F-1 [MEDIUM]: ConflictResolver throws on 'trip' entity type

- **File**: `mobile/lib/core/sync/conflict_resolver.dart:63-69`
- **OWASP**: A06:2021 - Vulnerable and Outdated Components (incomplete implementation)
- **Description**: The `ConflictResolver.resolve()` method handles `crop_listing` and `delivery` entity types but throws `ConflictResolutionException` for `trip`, `payment`, and `user` types. If a trip sync operation results in a version conflict, the mobile client will throw an unhandled exception instead of resolving it gracefully. Since trips and deliveries are closely coupled in the F-12 feature, a trip conflict during a delivery workflow could disrupt the user experience.
- **Current mitigation**: Server-side trip operations still work correctly. The exception is caught at a higher level but the local trip data becomes stale.
- **Recommendation**: Implement `_resolveTrip()` in `ConflictResolver` using the same server-wins strategy. The trip Isar schema already exists; the resolution logic mirrors `_resolveDelivery`.
- **Risk**: MUST FIX (before production; trips and deliveries sync together)

### F-2 [MEDIUM]: Pagination token not signed -- forgery allows DynamoDB key injection

- **File**: `server/src/Functions/DeliveryFunction.cs:383-421`
- **OWASP**: A04:2021 - Insecure Design
- **Description**: The `nextToken` is a base64-encoded JSON dictionary that maps directly to DynamoDB `ExclusiveStartKey`. An attacker can craft arbitrary base64 payloads to inject custom DynamoDB keys, potentially scanning records they should not access or causing unexpected query behavior. While the participant filter (line 122) provides a secondary defense, the attacker could enumerate delivery IDs and `createdAt` timestamps from the DynamoDB table.
- **Current mitigation**: Participant-level filtering at the application layer (line 122) prevents data from being returned to non-participants. DynamoDB IAM policies restrict table access.
- **Recommendation**: Sign the `nextToken` using HMAC-SHA256 with a server-side secret. Verify the signature on decode. This requires key management infrastructure.
- **Risk**: DEFERRED (requires key management infra; participant filter provides defense-in-depth)

### F-3 [LOW]: No dispute reason length validation on server

- **File**: `server/src/Functions/SyncFunction.cs:1099-1627` (ProcessDeliveryAsync, update path)
- **OWASP**: A03:2021 - Injection
- **Description**: The `disputeReason` field is included in `AllowedDeliveryFields` (line 55) and passed through to DynamoDB without any length validation. An attacker could submit an extremely long dispute reason string (e.g., megabytes of text), causing DynamoDB item size limits to be hit (400KB max) or increasing storage costs. The mobile `raiseDispute` method (`delivery_repository.dart:204-241`) also does not validate the `reason` parameter length.
- **Current mitigation**: DynamoDB enforces a 400KB item size limit, so the damage is bounded. The sync batch limit of 50 operations also limits throughput.
- **Recommendation**: Add server-side validation: `disputeReason` must be 1-2000 characters. Add the same constraint on the mobile side in `DeliveryRepository.raiseDispute()`.
- **Risk**: MUST FIX (simple validation, prevents abuse)

### F-4 [LOW]: No file upload content-type or path validation for photo refs

- **File**: `server/src/Functions/SyncFunction.cs:1154-1193` (create path), `mobile/lib/features/delivery_verification/data/repositories/delivery_repository.dart:130-132`
- **OWASP**: A04:2021 - Insecure Design
- **Description**: The `pickupPhotoRef` and `dropoffPhotoRef` fields are stored as arbitrary strings with no validation. These fields are intended to hold S3 object references but could contain any string value, including path traversal sequences (e.g., `../../sensitive-bucket/key`), URLs to external resources, or XSS payloads that could be rendered in a web admin panel later.
- **Current mitigation**: These strings are not currently used to generate S3 presigned URLs or rendered in HTML. They are treated as opaque references.
- **Recommendation**: Validate that photo refs match an expected pattern (e.g., `^[a-zA-Z0-9\-_/]+\.(jpg|jpeg|png)$`). When the photo upload feature is built, enforce content-type validation (image/jpeg, image/png only) and maximum file size (5MB).
- **Risk**: DEFERRED (no upload endpoint exists yet; validate when implementing photo upload)

### F-5 [INFO]: Mobile confirmDelivery allows transition from pickedUp, but server requires inTransit

- **File**: `mobile/lib/features/delivery_verification/data/repositories/delivery_repository.dart:157-158`, `server/src/Functions/SyncFunction.cs:65-71`
- **OWASP**: N/A (logic inconsistency)
- **Description**: The mobile `confirmDelivery` method allows the transition when status is `pickedUp` OR `inTransit` (line 157-158). However, the server's `DeliveryTransitions` dictionary (line 69) only allows `inTransit -> delivered`. If a buyer attempts to confirm delivery while status is `pickedUp` (skipping `inTransit`), the mobile will update locally but the sync will fail on the server with `DELIVERY_INVALID_TRANSITION`. The conflict resolver will then overwrite the local state back to `pickedUp`.
- **Current mitigation**: Server-side validation is authoritative; the local change will be reverted by conflict resolution. No data integrity risk.
- **Recommendation**: Align mobile validation to match server transitions. Remove `DeliveryStatus.pickedUp` from the `confirmDelivery` guard. Only allow `inTransit -> delivered` locally to prevent confusing UX where the user sees a success then a revert.
- **Risk**: MUST FIX (UX issue; simple alignment change)

### F-6 [INFO]: Non-participant can receive 404 vs 401 to infer delivery existence

- **File**: `server/src/Functions/DeliveryFunction.cs:158-202`
- **OWASP**: A01:2021 - Broken Access Control
- **Description**: In `HandleGetDeliveryByIdAsync`, when a non-participant requests a delivery by ID, the function returns the full `DeliveryDto` with `includePhone: false` and `includeQrToken: false` (line 192). This is because the `participant` boolean controls the flags but the response is still returned (line 195). A non-participant can see: delivery IDs, participant names, status, GPS coordinates, timestamps, and version -- everything except phone numbers and QR token. While the list view properly filters (line 122), the detail view leaks data to any authenticated user who guesses a delivery ID.
- **Current mitigation**: Delivery IDs are UUIDv4 (not guessable). The list endpoint filters by participant. API Gateway JWT authorizer prevents unauthenticated access.
- **Recommendation**: Return 403 or 404 for non-participants on the detail endpoint. Change `HandleGetDeliveryByIdAsync` to return an error when `participant` is `false`, rather than returning a redacted DTO.
- **Risk**: MUST FIX (information disclosure to authenticated non-participants)

### F-7 [INFO]: Phone numbers visible to all participants including counterparties

- **File**: `server/src/Functions/DeliveryFunction.cs:192`, `server/src/Models/DeliveryDtos.cs:88-120`
- **OWASP**: A01:2021 - Broken Access Control (privacy)
- **Description**: In the detail view, all three phone numbers (farmer, trucker, buyer) are visible to any participant. This means a buyer can see the farmer's phone number and vice versa. In the Ethiopian context, this could enable direct contact that bypasses the platform, or expose farmer phone numbers to buyers who could share them externally.
- **Current mitigation**: Phone numbers are hidden from non-participants in list view. Detail view restricts to participants only.
- **Recommendation**: For the current MVP phase, this is an acceptable trade-off since participants need to coordinate. In a future release, consider showing only the relevant counterparty's phone number (e.g., trucker sees farmer + buyer, buyer sees trucker only).
- **Risk**: ACCEPTED (necessary for MVP coordination; revisit in later release)

## Positive Security Patterns

1. **Field whitelisting**: `AllowedDeliveryFields` (SyncFunction.cs:50-56) prevents arbitrary attribute injection into DynamoDB. Only known fields are accepted; unknown fields trigger an error.

2. **Role-based status transitions**: The `ProcessDeliveryAsync` update path (SyncFunction.cs:1307-1353) enforces fine-grained role checks per transition: `pickedUp`/`inTransit` require trucker, `delivered` requires buyer, `disputed`/`cancelled` require any participant.

3. **Status transition state machine**: `DeliveryTransitions` dictionary (SyncFunction.cs:65-71) prevents illegal status jumps (e.g., `pending` directly to `delivered`). Combined with role enforcement, this provides strong protection against status manipulation.

4. **QR token hidden in list view**: `MapToDeliveryDto` (DeliveryFunction.cs:126) passes `includeQrToken: false` for the list endpoint, preventing QR token enumeration via bulk listing.

5. **Idempotency protection**: Sync operations check `IdempotencyKeyExistsAsync` before processing, preventing duplicate delivery creation from retried offline requests.

6. **Optimistic concurrency control**: Version-based `ConditionExpression` on all DynamoDB writes (create uses `attribute_not_exists`, update/delete use version check) prevents lost updates and race conditions.

7. **GPS coordinate validation**: Both create and update paths validate latitude (-90/90) and longitude (-180/180) with NaN and Infinity checks (SyncFunction.cs:1168-1177, 1423-1432).

8. **Forced initial status**: Delivery creation forces `status = "pending"` (SyncFunction.cs:1196) regardless of client-supplied status, preventing privilege escalation at creation time.

9. **Soft delete pattern**: Deliveries are soft-deleted (set `deletedAt` + `status=cancelled`) rather than hard-deleted, preserving audit trail. Deleted items are excluded from queries via `attribute_not_exists(#deletedAt)` filter.

10. **Conflict resolution for deliveries**: `ConflictResolver._resolveDelivery` (conflict_resolver.dart:153-246) properly handles all delivery fields including nested GPS data, ensuring local state converges with server after conflicts.

11. **Mobile-side status validation**: `DeliveryRepository` validates status preconditions before transitions (e.g., `confirmPickup` only from `pending`, `raiseDispute` only from `delivered`, `cancelDelivery` only from `pending`/`pickedUp`), reducing unnecessary sync operations.

12. **Error message hygiene**: Server-side error messages use coded error types (e.g., `DELIVERY_UNAUTHORIZED`, `DELIVERY_INVALID_TRANSITION`) without exposing internal state or stack traces.

## Summary

**Overall Risk Assessment**: MEDIUM

The F-12 Delivery Verification feature demonstrates solid security fundamentals. Authentication is enforced on all endpoints, authorization uses a well-designed role-based model for status transitions, field whitelisting prevents injection, and optimistic concurrency prevents race conditions. The offline-first sync architecture correctly handles conflicts with a server-wins strategy.

**Open Items (ordered by priority)**:

| # | Finding | Severity | Risk | Action |
|---|---------|----------|------|--------|
| F-6 | Non-participant detail view leaks delivery metadata | INFO | MUST FIX | Return 403/404 for non-participants |
| F-1 | ConflictResolver missing trip resolution | MEDIUM | MUST FIX | Implement `_resolveTrip()` |
| F-5 | Mobile/server transition mismatch (pickedUp->delivered) | INFO | MUST FIX | Align mobile guard to server rules |
| F-3 | No dispute reason length validation | LOW | MUST FIX | Add 1-2000 char limit both sides |
| F-2 | Pagination token not signed | MEDIUM | DEFERRED | Sign with HMAC when key mgmt available |
| F-4 | Photo ref strings not validated | LOW | DEFERRED | Validate when upload endpoint is built |
| F-7 | Phone visible to all participants | INFO | ACCEPTED | Revisit after MVP |

**Recommendation for Production Readiness**: The four MUST FIX items (F-6, F-1, F-5, F-3) should be resolved before production deployment. F-6 is the most significant as it allows authenticated users to enumerate delivery details for deliveries they are not participants in. The fixes are all straightforward and do not require architectural changes. Once these are addressed, F-12 is ready for production from a security perspective.
