# Security Review: F-11 Trip Listing

Date: 2026-02-18
Reviewer: server-security
Version: 1.0
Status: PASS WITH FINDINGS

## Scope

- **Files reviewed** (with full paths):
  1. `server/src/Functions/TripFunction.cs` (406 lines) -- GET /trips, GET /trips/{id} handler
  2. `server/src/Data/TripRepository.cs` (127 lines) -- DynamoDB trip repository
  3. `server/src/Functions/SyncFunction.cs` (1668 lines) -- ProcessTripAsync section (trip create/update/delete via /sync), lines 522-962
  4. `mobile/lib/features/trip_listing/data/repositories/trip_listing_repository.dart` (152 lines) -- Mobile offline-first repository
  5. `mobile/lib/features/trip_listing/presentation/bloc/trip_listing_bloc.dart` (103 lines) -- BLoC state management
  6. `infra/lib/stacks/api-stack.ts` (555 lines) -- Routes, authorizers, IAM for trip endpoints

- **Additional files reviewed** (supporting analysis):
  - `mobile/lib/core/database/schemas/trip.dart` (88 lines) -- Isar schema
  - `mobile/lib/core/sync/sync_queue.dart` (271 lines) -- Sync queue implementation
  - `mobile/lib/features/trip_listing/presentation/bloc/trip_listing_event.dart` (85 lines)
  - `mobile/lib/features/trip_listing/presentation/bloc/trip_listing_state.dart` (58 lines)

- **Lines of code reviewed**: ~3,011 (primary files), ~502 (supporting files)
- **Commits covered**: branch `trips-delivery-escrow`, commits `80fa561` through `3044ff9`

---

## Checklist (from qa/README.md)

### Authentication & Authorization
- [x] JWT tokens validated and not expired -- Cognito JWT authorizer on /sync; 15-minute access token validity configured in api-stack.ts line 82. GET /trips intentionally public (marketplace data).
- [x] Authorization checks on all API endpoints -- /sync validates `truckerId == authenticatedUserId` for create/update/delete (SyncFunction.cs lines 542-544, 680-681, 860-861). GET endpoints are intentionally unauthenticated with phone numbers redacted for anonymous users (TripFunction.cs lines 237, 256-279).
- [x] No hardcoded credentials in code -- Table names from environment variables. Cognito config from CDK outputs. No secrets in source.

### Data Privacy
- [x] Sensitive data encrypted at rest and in transit -- DynamoDB encryption at rest is on by default (AWS managed). API Gateway enforces HTTPS. Phone numbers redacted for unauthenticated GET requests (TripFunction.cs line 237).
- [x] Minimal data collection; user consent obtained -- Trip data limited to operational fields only. AllowedTripFields whitelist (SyncFunction.cs lines 44-48) prevents extraneous data storage.

### Input Validation
- [x] All user input validated (length, type, format) -- ValidateTripFields (SyncFunction.cs lines 968-1092) validates: capacity (0.1-1000), pricePerQuintal (0.01-10000), fromWoreda/toWoreda (1-100 chars, must differ), departureDate (ISO 8601), truckerPhone (+251 prefix), truckerName (1-100 chars). Query params validated in TripFunction.cs (limit 1-50, base64 nextToken).
- [x] Injection prevention (SQL, XSS) -- No SQL (DynamoDB). Field whitelisting (AllowedTripFields) prevents attribute injection. ExpressionAttributeNames/Values used for all DynamoDB queries (parameterized). No HTML rendering of user data.
- [x] File upload validation (type, size, content) -- N/A for trip listing feature. No file uploads.

### API Security
- [ ] Rate limiting enabled -- API Gateway global throttle set (100 req/sec, 200 burst) in api-stack.ts lines 521-527. However, no per-user rate limiting on /sync endpoint. See Finding 3.
- [x] Idempotency keys for write operations -- Idempotency keys checked before processing each sync operation (SyncFunction.cs lines 209-221). Keys stored with 7-day TTL.
- [x] Error messages don't leak sensitive info -- Generic error messages returned: "Failed to query trip listings." (TripFunction.cs line 182), "Internal server error." (SyncFunction.cs line 258). Exception messages logged server-side only, not returned to client.

### Mobile Security
- [x] No sensitive data in logs -- BLoC emits typed state objects, no raw data logging. Exception `toString()` in error states (trip_listing_bloc.dart line 33) may include stack details but this is standard Flutter error handling, not PII leakage.
- [ ] Secure storage for tokens -- Isar database is unencrypted on device. TruckerPhone stored in plaintext in Isar (trip.dart line 30). See Finding 5.
- [ ] Obfuscation enabled in release builds -- Not verified in this review scope; flagged as out-of-scope.

### Infrastructure
- [x] IAM roles follow least privilege -- TripFunction Lambda gets `grantReadData` only on trips table (api-stack.ts line 220). SyncFunction gets `grantReadWriteData` on trips + sync metadata tables (lines 186-189). No cross-table over-provisioning.
- [x] Secrets in AWS Secrets Manager -- No application secrets required for trip listing. Cognito integration uses CDK-managed references.
- [ ] CloudWatch alarms for suspicious activity -- Lambda logs errors to CloudWatch (TripFunction.cs lines 181, 222; SyncFunction.cs line 251), but no CloudWatch Alarms configured for anomaly detection (e.g., spike in 401s, version conflicts). See Finding 6.

---

## Threat Model Cross-Reference

| Finding | Threat ID | Description |
|---------|-----------|-------------|
| Finding 1 (Unsigned Pagination Token) | T2.1 (Tampering) | Crafted nextToken could manipulate DynamoDB scan position |
| Finding 2 (CORS Wildcard) | T4.2 (Info Disclosure) | Wildcard CORS could enable data exfiltration if web client added |
| Finding 3 (No Per-User Rate Limit on /sync) | T5.1 (DoS) | Single authenticated user could flood sync endpoint |
| Finding 4 (IsAuthenticated Header Check) | T1.2 (Spoofing) | Presence of Authorization header treated as authenticated without validation |
| Finding 5 (Unencrypted Isar Database) | T4.3 (Info Disclosure) | Stolen device exposes truckerPhone and trip data |
| Finding 6 (No CloudWatch Alarms) | T5.1 (DoS) | No alerting on suspicious patterns |
| Finding 7 (TOCTOU on Version Check) | T2.2 (Tampering) | Race window between existence check and conditional update |

---

## Findings

### Finding 1 -- MEDIUM: Unsigned Pagination Token Allows Arbitrary DynamoDB Scan Position

- **File**: `server/src/Functions/TripFunction.cs:387-404`
- **OWASP**: A01:2021 -- Broken Access Control
- **Description**: The `nextToken` pagination parameter is a base64-encoded JSON dictionary of DynamoDB key attributes. It is decoded and passed directly as `ExclusiveStartKey` to DynamoDB Query without any signature or integrity check. An attacker can craft a `nextToken` containing arbitrary key attributes, potentially allowing them to start scanning from any position in the GSI. Because the query is always scoped to the StatusDepartureDateIndex GSI with a fixed `status` partition key, the blast radius is limited to iterating within a single status partition. However, a crafted token with unexpected attribute names could cause DynamoDB validation errors that leak table schema information.
- **Current mitigation**: The query is scoped to StatusDepartureDateIndex GSI with a mandatory status partition key, limiting scan to one status partition. Invalid tokens produce a 400 error with generic message (line 103).
- **Recommendation**: Sign the nextToken with HMAC-SHA256 using a server-side secret (e.g., from AWS Secrets Manager or an environment variable derived from a KMS key). Verify signature on decode. Alternatively, encrypt the token with AES-256. This is a known deferred item requiring key management infrastructure.
- **Risk**: DEFERRED -- Low exploitability due to GSI scoping. Requires key management infrastructure for proper fix.

---

### Finding 2 -- LOW: CORS Wildcard Origin Allows Cross-Origin Requests from Any Domain

- **File**: `infra/lib/stacks/api-stack.ts:341-345`
- **OWASP**: A05:2021 -- Security Misconfiguration
- **Description**: The HTTP API Gateway CORS configuration sets `allowOrigins: ['*']`, permitting any web origin to make cross-origin requests. For the current mobile-only API (Flutter app communicates via direct HTTPS, not browser-based), this has no practical impact. However, if a web client is ever added, this would allow malicious websites to make authenticated cross-origin requests using the user's cookies/tokens.
- **Current mitigation**: API is mobile-only. No web client exists. Mobile apps are not subject to browser CORS restrictions.
- **Recommendation**: When a web client is planned, restrict `allowOrigins` to the specific web domain(s). Add this to a pre-launch checklist for any web frontend work.
- **Risk**: ACCEPTED -- No impact for mobile-only API. Must be revisited before any web client deployment.

---

### Finding 3 -- MEDIUM: No Per-User Rate Limiting on /sync Endpoint

- **File**: `infra/lib/stacks/api-stack.ts:394-401` and `server/src/Functions/SyncFunction.cs:132-135`
- **OWASP**: A04:2021 -- Insecure Design
- **Description**: The /sync endpoint has a server-side batch limit of 50 operations per request (SyncFunction.cs line 132) and API Gateway global throttling of 100 req/sec (api-stack.ts line 525). However, there is no per-user rate limiting. A single authenticated user could submit 100 requests/second x 50 operations = 5,000 sync operations/second, consuming significant DynamoDB write capacity and potentially causing throttling that affects all users. The global throttle applies across all users and endpoints, so a single abuser can starve legitimate traffic.
- **Current mitigation**: Batch size limit of 50 operations. Global API Gateway throttle at 100 req/sec, 200 burst. Cognito authentication required (barrier to entry for abuse).
- **Recommendation**: Implement per-user rate limiting using the existing `rateLimitsTable` DynamoDB table (already provisioned in api-stack.ts). Apply a limit of 10 sync requests per minute per user. This pattern already exists in the auth functions (RegisterFunction, LoginFunction use rateLimitsTable).
- **Risk**: MUST FIX -- A single compromised or malicious account could degrade service for all users, especially harmful in the Ethiopian rural context where connectivity windows are limited.

---

### Finding 4 -- MEDIUM: IsAuthenticated Check Relies on Header Presence, Not Validation

- **File**: `server/src/Functions/TripFunction.cs:256-279`
- **OWASP**: A07:2021 -- Identification and Authentication Failures
- **Description**: The `IsAuthenticated` method checks for the **presence** of an `Authorization` header (line 263-265) to determine if the request is authenticated. Because the GET /trips and GET /trips/{id} routes have no JWT authorizer configured in api-stack.ts (lines 419-433), the Authorization header is not validated by API Gateway. An unauthenticated user can send `Authorization: garbage` and the `IsAuthenticated` method will return `true`, causing `truckerPhone` to be included in the response. This is an information disclosure issue -- phone numbers are PII that should only be visible to authenticated users.
- **Current mitigation**: The header presence check also looks for `request.RequestContext.Authorizer` claims (lines 272-274), but since no authorizer is configured for trip GET routes, this path is never reached. The Authorization header path (lines 259-267) is the vulnerability.
- **Recommendation**: Either (a) add the JWT authorizer as an optional authorizer on GET /trips and GET /trips/{id} routes so that API Gateway validates tokens when present, or (b) change `IsAuthenticated` to only check `request.RequestContext.Authorizer` claims (which are set by API Gateway after JWT validation) and ignore raw Authorization headers. Option (b) is simpler and does not require infrastructure changes.
- **Risk**: MUST FIX -- Allows unauthenticated users to access truckerPhone numbers by adding a fake Authorization header. Phone numbers are PII and this violates the privacy design intent.

---

### Finding 5 -- HIGH: Unencrypted Isar Database Exposes PII on Stolen/Rooted Devices

- **File**: `mobile/lib/core/database/schemas/trip.dart:30` and `mobile/lib/features/trip_listing/data/repositories/trip_listing_repository.dart:57-59`
- **OWASP**: A02:2021 -- Cryptographic Failures
- **Description**: The Isar database on the mobile device stores trip data including `truckerPhone` (E.164 format with +251 prefix), `truckerName`, route information (fromWoreda/toWoreda), and schedule data (departureDate) in plaintext. If a device is stolen, lost, or rooted, this data is directly accessible. In the Ethiopian rural context, device theft is a realistic threat, and shared devices (T7.2) mean one user could access another's data from the local database.
- **Current mitigation**: None. Isar is not configured with encryption.
- **Recommendation**: Enable Isar database encryption using `Isar.open(schemas, encryptionKey: ...)` with a key derived from Android Keystore / iOS Keychain. This aligns with the threat model recommendation for T4.3. As an interim measure, consider not persisting `truckerPhone` in the local database when viewing other truckers' listings (only persist for the authenticated user's own trips).
- **Risk**: DEFERRED -- Requires encryption key management infrastructure. Track in backlog. High severity but mitigated by device-level security (screen lock).

---

### Finding 6 -- LOW: No CloudWatch Alarms for Trip-Specific Anomaly Detection

- **File**: `infra/lib/stacks/api-stack.ts` (entire stack -- no alarm resources defined)
- **OWASP**: A09:2021 -- Security Logging and Monitoring Failures
- **Description**: While Lambda functions log errors to CloudWatch Logs (TripFunction.cs lines 181, 222; SyncFunction.cs line 251), no CloudWatch Alarms are configured to alert on suspicious patterns such as: (a) spike in version conflicts (potential tampering), (b) spike in TRIP_UNAUTHORIZED errors (potential IDOR attempts), (c) unusual volume of trip creates/deletes from a single user. Without alarms, attacks may go unnoticed until user complaints.
- **Current mitigation**: Error logging exists. CloudWatch Logs are retained by default.
- **Recommendation**: Add CloudWatch Metric Filters and Alarms for: (1) `TRIP_UNAUTHORIZED` error rate > 10/min, (2) version conflict rate > 50/min, (3) 500 error rate > 5/min. Route alarms to SNS topic for notification. This can be done incrementally.
- **Risk**: ACCEPTED -- Logging exists; alarms are an operational maturity improvement. Should be addressed before production launch.

---

### Finding 7 -- LOW: TOCTOU Race Condition in Trip Update/Delete

- **File**: `server/src/Functions/SyncFunction.cs:668-836` (update path) and `848-951` (delete path)
- **OWASP**: A04:2021 -- Insecure Design
- **Description**: In the update and delete paths of `ProcessTripAsync`, the code first fetches the existing trip item (line 668 for update, line 848 for delete), performs authorization and validation checks against it, then executes a conditional `UpdateItemRequest` with a version check (lines 802-806, 897-924). There is a time-of-check-to-time-of-use (TOCTOU) gap between the fetch and the conditional write. If another concurrent request modifies the item between these two operations, the version-based `ConditionExpression` will correctly reject the stale write (returning a conflict). However, the authorization check was performed against the pre-modification state -- for example, if ownership was transferred between fetch and write, the authorization check would pass against the old owner but the write would succeed against the new owner's data. In practice, trip ownership (`truckerId`) is immutable after creation, so this is theoretical.
- **Current mitigation**: `ConditionExpression` on version prevents stale writes. `truckerId` is immutable (not in the updatable fields whitelist for the value, and the create path forces it to match the authenticated user). The race window is sub-millisecond in a single-Lambda execution.
- **Recommendation**: No immediate action required. The version-based conditional write provides adequate protection. If trip ownership transfer is ever implemented, add `truckerId` to the ConditionExpression to enforce ownership atomically.
- **Risk**: ACCEPTED -- Theoretical only. Version-based concurrency and immutable ownership mitigate this.

---

## Positive Security Patterns

The following patterns demonstrate strong security practices and should be maintained:

1. **Field Whitelisting (AllowedTripFields)** -- SyncFunction.cs line 44-48. Only explicitly allowed fields are written to DynamoDB. Unknown fields are rejected with a clear error message. This prevents attribute injection and data model pollution.

2. **Status Transition State Machine (ValidTransitions)** -- SyncFunction.cs lines 58-63. Hardcoded dictionary of allowed state transitions (`open -> matched|cancelled`, `matched -> inTransit|cancelled`, `inTransit -> delivered`) prevents business logic bypass. Requests for invalid transitions are rejected server-side.

3. **Owner Validation on All Write Paths** -- The `truckerId == authenticatedUserId` check is enforced consistently on create (line 542-544), update (line 680-681), and delete (line 860-861). No write path is missing this check.

4. **Forced Initial Status** -- On create, status is always forced to `"open"` (SyncFunction.cs line 599) regardless of what the client sends. This prevents a client from creating a trip already marked as `delivered` or `inTransit`.

5. **Soft Delete with Guard Rails** -- Delete operations set `deletedAt` timestamp and `status=cancelled` rather than physically removing data (SyncFunction.cs lines 897-924). Already-deleted/cancelled trips are explicitly blocked from further modification (lines 692-712, 871-891).

6. **Version-Based Optimistic Concurrency** -- All write operations use DynamoDB `ConditionExpression` with version checking, providing conflict detection for offline-first sync. Conflicts return the server's current data to the client for resolution.

7. **Idempotency Key Deduplication** -- Every sync operation is checked against stored idempotency keys before processing (SyncFunction.cs lines 209-221), preventing double-processing of retried operations. Keys have a 7-day TTL.

8. **Phone Number Redaction for Anonymous Users** -- TripFunction.cs line 237 conditionally hides `truckerPhone` based on authentication status, reducing PII exposure in public browse results.

9. **Comprehensive Input Validation (ValidateTripFields)** -- SyncFunction.cs lines 968-1092 validates all trip fields with appropriate ranges, required checks for create vs. optional checks for update, and cross-field validation (fromWoreda != toWoreda).

10. **Least-Privilege IAM** -- TripFunction Lambda has read-only DynamoDB access (api-stack.ts line 220). SyncFunction has read-write access only to the tables it needs (lines 186-190). No wildcard IAM policies.

11. **Generic Error Responses** -- Error messages returned to clients are sanitized and do not include stack traces, table names, or internal identifiers. Exception details are logged server-side only.

12. **Batch Size Limit** -- The sync endpoint limits requests to 50 operations (SyncFunction.cs line 132), preventing a single request from causing excessive Lambda execution time or DynamoDB consumption.

13. **Query Limit Enforcement** -- GET /trips enforces a maximum limit of 50 results per page (TripFunction.cs line 86), preventing response payloads from growing unbounded.

14. **Mobile Sync Queue Bounds** -- The SyncQueue has a maxQueueSize of 1000 (sync_queue.dart line 23) with maxRetries of 3 (line 26), preventing unbounded queue growth and infinite retry loops on the device.

---

## Summary

**Overall Risk Assessment**: MEDIUM

The F-11 Trip Listing feature demonstrates solid security engineering fundamentals. The server-side code follows defense-in-depth principles with field whitelisting, ownership validation, status transition enforcement, and version-based concurrency. The offline-first mobile architecture correctly separates local writes from server sync with idempotency guarantees.

**Critical Issues**: None.

**Must-Fix Issues (2)**:
1. **Finding 3** -- Per-user rate limiting on /sync endpoint. A single malicious user could degrade service for all users. The infrastructure already exists (rateLimitsTable) -- this is an implementation gap.
2. **Finding 4** -- `IsAuthenticated` header-presence check bypasses the intended phone number redaction. Fix by checking `RequestContext.Authorizer` claims only instead of raw headers.

**Deferred Issues (2)**:
1. **Finding 1** -- Unsigned pagination token. Requires key management infrastructure. Low exploitability due to GSI scoping.
2. **Finding 5** -- Unencrypted Isar database. Requires encryption key management. High severity but device-level security provides partial mitigation.

**Accepted Issues (3)**:
1. **Finding 2** -- CORS wildcard. No impact for mobile-only API.
2. **Finding 6** -- No CloudWatch alarms. Address before production launch.
3. **Finding 7** -- TOCTOU race condition. Theoretical only; mitigated by version checks and immutable ownership.

**Production Readiness**: The feature is ready for staging deployment after addressing the two MUST FIX items. Finding 4 (IsAuthenticated bypass) is a quick code fix. Finding 3 (per-user rate limiting) requires moderate implementation effort using the existing rateLimitsTable pattern. Deferred items should be tracked in the backlog and addressed before production launch with real users.
