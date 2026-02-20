# F-13 Escrow Security Hardening Addendum

**Date**: 2026-02-19
**Reviewer**: Security Agent
**Scope**: Mobile escrow architecture + server-side escrow implementation
**Inputs**: `f13-mobile-escrow-architecture.md`, `r4-escrow-security-review.md`, server source code
**Status**: Final

---

## 1. Threat Model Summary

### 1.1 Attack Surface

| Surface | Threat Level | Description |
|---------|-------------|-------------|
| **Mobile intent queue (Isar)** | Medium | Local database on shared/rooted devices. Intents contain amounts, user IDs, idempotency keys. |
| **Network (3G)** | High | Ethiopian 3G is vulnerable to MitM, packet injection, replay. Certificate pinning is currently disabled. |
| **API endpoints** | High | Six `/escrow/*` endpoints accept financial mutations. Auth bypass = fund theft. |
| **Telebirr gateway** | Critical | Real Telebirr integration handles actual money. Mock gateway masks real-world failure modes. |
| **DynamoDB tables** | Medium | Server-side. IAM-protected. Risk is logic bugs (race conditions, state machine violations). |
| **Client-side logs** | Low–Medium | `LoggingInterceptor` truncates but does not redact PII from request bodies. |

### 1.2 Key Attack Vectors

| # | Vector | Impact | Likelihood | Mitigation Status |
|---|--------|--------|-----------|-------------------|
| T1 | **Double-charge via idempotency race** | Critical — buyer charged twice | Low (M1 fixed) | Server fixed: conditional PutItem. Mobile must reuse keys on retry. |
| T2 | **Unauthorized fund release/refund** | Critical — funds stolen | Low (M2 fixed) | Server fixed: `IsSystemOrAdmin` check. Mobile must never expose release/refund UI. |
| T3 | **State machine race condition** | Critical — funds both released AND refunded | Low (M3 fixed) | Server fixed: optimistic concurrency with `expectedCurrentStatus`. |
| T4 | **Intent tampering on rooted device** | Medium — modified amount sent to server | Low | Server re-validates all fields. Idempotency key mismatch returns cached original result. |
| T5 | **Replay attack via captured idempotency key** | Low — duplicate returns cached result | Very Low | Idempotency returns cached result, not a new transaction. No financial impact. |
| T6 | **Self-trading (buyer == farmer)** | Medium — commission laundering | Low | Server enforces `buyerId != farmerId` (P3 verified). |
| T7 | **Fayda ID bypass for high-value txn** | Medium — regulatory violation | Low (M4 fixed) | Server checks Fayda when `_userRepo` is available. See residual risk R1. |
| T8 | **PII leakage in logs** | Medium — phone/Fayda IDs in CloudWatch | Medium (S4 open) | Server logs `ex.Message` raw. Fix required. |
| T9 | **Dispute window client-side bypass** | Low — expired dispute accepted | Very Low | Server enforces 48h window (P13 verified). Client check is UX only. Correct approach. |
| T10 | **Offline queue double-processing** | Medium — duplicate intents sent | Low | Same idempotency key reused. Server deduplicates. See mobile hardening MH4. |

### 1.3 Trust Boundaries

```
┌────────────────────────────────┐
│  UNTRUSTED: Mobile Client      │ ← All input is untrusted
│  - Isar DB (user-accessible)   │
│  - Intent queue                │
│  - Amount, IDs, keys           │
└──────────────┬─────────────────┘
               │ TLS 1.2+ (cert pinning TODO)
               ▼
┌────────────────────────────────┐
│  TRUST BOUNDARY: API Gateway   │ ← JWT validation
│  - Cognito JWT Authorizer      │
└──────────────┬─────────────────┘
               │
               ▼
┌────────────────────────────────┐
│  TRUSTED: Server (Lambda)      │ ← All validation re-done here
│  - EscrowService               │
│  - DynamoDB (IAM-protected)    │
│  - Telebirr Gateway            │
└────────────────────────────────┘
```

---

## 2. Mobile Hardening Requirements

### MH1. Payment Intent Integrity

**Risk**: On a rooted device, Isar data can be modified between queue and sync.

**Current state**: Architecture doc (Section 9.2) correctly notes that "tampering local Isar data has no effect on server state." The idempotency key contains the original timestamp, so a modified intent with the same key returns the cached original result.

**Required hardening**:
- **No HMAC needed for MVP**. The server re-validates all fields (amount range, buyerId == JWT sub, deliveryId exists, self-trading check). A tampered intent will either be rejected or return the original cached result.
- **Rationale**: Adding client-side HMAC would require a local secret, which is extractable on rooted devices. The server-authoritative model is the correct defense.
- **Future consideration**: If offline intent manipulation becomes a vector for social engineering attacks (e.g., showing a fake "payment queued" screen to a farmer), consider adding a display-only integrity indicator using a device-bound key from Android Keystore.

**Verdict**: Architecture is sound. No changes required for MVP.

### MH2. Idempotency Key Security

**Risk**: Key format `{buyerId}:{deliveryId}:initiate:{timestamp}` leaks user and entity IDs. Replay of a captured key returns cached result (no financial impact).

**Required hardening**:
- **Key generation**: The architecture's approach (generate once, store with intent, reuse on retry) is correct and critical. Implementation **MUST** ensure:
  1. Key is generated ONCE when the intent is first created.
  2. Key is persisted to Isar BEFORE any API call attempt.
  3. On retry (sync service), the SAME key from the persisted intent is used — never regenerated.
  4. `EscrowSyncService.processQueuedIntents()` must read the key from the `PaymentIntent.idempotencyKey` field, not generate a new one.
- **Timestamp granularity**: `millisecondsSinceEpoch` is sufficient. Two intents for the same buyer+delivery within 1ms is not a realistic scenario.
- **Key cleanup**: Completed/failed intents should be deleted from Isar after 24 hours (as noted in architecture Section 9.2). Add a cleanup method to `EscrowSyncService` that runs on app startup:
  ```dart
  Future<void> cleanupStaleIntents() async {
    final cutoff = DateTime.now().subtract(const Duration(hours: 24));
    await _isar.writeTxn(() async {
      await _isar.paymentIntents
          .filter()
          .statusEqualTo(IntentStatus.completed)
          .or()
          .statusEqualTo(IntentStatus.failed)
          .and()
          .createdAtLessThan(cutoff)
          .deleteAll();
    });
  }
  ```

**Verdict**: Architecture is sound. Add cleanup method. Enforce key reuse discipline in implementation.

### MH3. Amount Validation

**Risk**: Client-side 100–500,000 ETB check can be bypassed by modifying the Dart runtime or API request directly.

**Required hardening**:
- **Client-side check**: Keep as UX-only pre-check. Prevents accidental misentry.
- **Server enforcement**: Already validated at `EscrowService.cs:101-107`. This is the security boundary.
- **Commission/farmerPayout**: The architecture correctly states these are calculated server-side and never accepted from the client. Implementation **MUST NOT** include `commission`, `commissionRate`, or `farmerPayout` in the `POST /escrow/initiate` request body. The `_mapInitiateResponse` method should populate these from the server response only.
- **Display commission**: The client computes `max(amount * 0.025, 5.0)` for display on `InitiatePaymentScreen`. This is acceptable as a preview — the server response will contain authoritative values.

**Verdict**: Architecture is sound. No changes required.

### MH4. Offline Queue Race Conditions

**Risk**: `EscrowSyncService.processQueuedIntents()` could be triggered concurrently (e.g., connectivity flap) and process the same intent twice.

**Required hardening**:
- **Mutex/lock**: The sync service **MUST** use a processing lock to prevent concurrent execution:
  ```dart
  bool _isProcessing = false;

  Future<void> processQueuedIntents() async {
    if (_isProcessing) return; // Prevent concurrent processing
    _isProcessing = true;
    try {
      // ... process intents
    } finally {
      _isProcessing = false;
    }
  }
  ```
- **Atomic status transition**: Before sending an intent to the server, transition its status from `pendingSync` to `syncing` in a single Isar write transaction. Query only `pendingSync` intents. This prevents double-pickup.
- **Server safety net**: Even if double-sent, the server's idempotency check returns the cached result. No financial impact. The lock is a defense-in-depth measure.

**Verdict**: Add processing lock. Architecture's idempotency design provides server-side safety.

### MH5. Self-Trading Prevention

**Risk**: A user creates two accounts to self-trade and extract commission.

**Current state**: Server enforces `buyerId != farmerId` at `EscrowService.cs:126-132` (P3 verified).

**Required hardening**:
- **Mobile**: Add client-side check in `EscrowRepository.initiatePayment()` as a fast-fail UX check:
  ```dart
  if (buyerId == farmerId) {
    return const EscrowError('ESCROW_001', 'Cannot pay yourself.');
  }
  ```
- **Server**: Already enforced. The real defense against multi-account self-trading is phone number verification (one Telebirr account per SIM) and Fayda ID verification for high-value transactions.

**Verdict**: Add client-side fast-fail. Server enforcement is the security boundary.

### MH6. Sensitive Data in Local Storage (Isar)

**Risk**: On rooted/shared devices, Isar DB files can be read directly.

**Data classification for Isar**:

| Field | Sensitivity | Storage Decision |
|-------|------------|-----------------|
| `paymentId` (escrowId) | Low | Store — needed for cache lookups |
| `deliveryId` | Low | Store — needed for cache lookups |
| `farmerId`, `buyerId` | Medium | Store — needed for display. UUIDs, not PII. |
| `amount`, `commission`, `farmerPayout` | Medium | Store — needed for display |
| `telebirrRef` | Medium | Store — needed for reference display. Truncate on list views. |
| `status`, timestamps | Low | Store — informational |
| `disputeReason`, `disputeDescription` | Medium | Store — needed for status display |
| `idempotencyKey` (PaymentIntent) | Medium | Store — needed for retry. Contains buyerId + timestamp. Delete after 24h. |

**Required hardening**:
- **No full Telebirr account numbers on device**: The architecture correctly excludes `telebirrHoldRef`, `telebirrReleaseRef`, `telebirrRefundRef`, `buyerFaydaVerified`, `farmerFaydaVerified` from local storage.
- **Intent cleanup**: Completed/failed intents must be purged after 24 hours (see MH2).
- **No encryption of Isar at rest for MVP**: Isar encryption adds complexity and performance overhead on low-end devices. The data stored is non-critical (server is authoritative). Android's file-based encryption (enabled by default on Android 10+) provides baseline protection. For Android 8-9, this is an accepted residual risk (see R2).
- **Auth tokens**: Already stored in `FlutterSecureStorage` (encrypted). Correct approach.

**Verdict**: Architecture is sound. Add intent cleanup timer.

### MH7. PII in Logs and Error Messages

**Risk**: `LoggingInterceptor` logs request bodies which may contain user IDs, amounts, delivery IDs.

**Current state**: The logging interceptor masks `Authorization` headers (line 157-173) and truncates bodies to 1000 chars. However, it does NOT redact:
- `buyerId`, `farmerId` in request bodies
- `amount` values
- `idempotencyKey` (contains user ID + timestamp)
- `telebirrRef` in response bodies

**Required hardening**:
- **Escrow-specific redaction**: In the `EscrowRepository`, do NOT pass raw error responses to debug logs. Use structured error codes only:
  ```dart
  } catch (e) {
    // BAD: log('Payment failed: $e');
    // GOOD: log('Payment failed: ${e is ApiException ? e.statusCode : "unknown"}');
  }
  ```
- **LoggingInterceptor enhancement**: For paths matching `/escrow/*`, redact the request body before logging. Only log the endpoint path and HTTP status, not the body.
- **Release builds**: Ensure `LoggingInterceptor` is not included in release builds. The architecture should configure this based on `kDebugMode`:
  ```dart
  if (kDebugMode) {
    dio.interceptors.add(LoggingInterceptor());
  }
  ```
- **Snackbar/toast messages**: User-facing error messages from `EscrowError.message` should never include raw IDs or server error details. Use localized strings from the l10n key map (Section 7.3 of the architecture).

**Verdict**: Add escrow-specific log redaction. Ensure LoggingInterceptor is debug-only.

### MH8. Auth Token Handling

**Risk**: JWT token leakage via logs, cache, or MitM.

**Current state analysis**:
- `AuthInterceptor` uses `FlutterSecureStorage` for token storage. Correct.
- `QueuedInterceptor` prevents concurrent refresh races. Correct.
- Token refresh creates a new `Dio()` instance for the retry (line 147) which bypasses the interceptor chain — this means the retry request will NOT have logging interceptor applied. Acceptable.
- Certificate pinning is **disabled** (`enableCertPinning: false`). This is the largest auth risk.

**Required hardening**:
- **Certificate pinning (pre-production)**: Before production deployment, enable certificate pinning with real API Gateway certificate hashes. This is called out as a TODO in `api_client.dart:25` but must be a hard blocker for production.
- **Token in request body**: The escrow endpoints use JWT for auth (via `Authorization` header). Tokens are **never** sent in request body or URL query parameters. Correct.
- **Token scope**: Escrow endpoints do not require any additional token claims beyond `sub` (user ID). The `GetAuthenticatedUserId` method extracts `sub` or `userId` from the authorizer context. This is correct.
- **Shared device logout**: When a user logs out, `_clearTokens()` deletes both access and refresh tokens. The local Isar cache should NOT be cleared on logout (it's per-device, not per-session). However, queued `PaymentIntent` records with `pendingSync` status should be cancelled and deleted on logout, as they belong to the outgoing user.

**Verdict**: Certificate pinning is a pre-production blocker. Add intent cancellation on logout.

### MH9. Dispute Window Enforcement

**Risk**: Client shows "Raise Dispute" button based on local timestamps that could drift.

**Current state**: Architecture Section 5.4 shows the RaiseDisputeScreen uses `disputeWindowEndsAt` from the server response. The server enforces the window at `EscrowService.cs:444-455`.

**Required hardening**:
- **Client check is advisory only**: The `disputeWindowEndsAt` comes from the server and is displayed as a countdown. If the client clock is wrong, the button may appear enabled when the window has actually expired. This is acceptable because:
  1. The server rejects expired disputes with `ESCROW_018`.
  2. The user sees a clear error message.
  3. The alternative (NTP sync) adds complexity inappropriate for low-end devices.
- **Auto-refresh on dispute screen**: When navigating to `RaiseDisputeScreen`, always call `RefreshEscrow` first to get the latest `disputeWindowEndsAt`. This ensures the countdown is as accurate as possible.

**Verdict**: Architecture is correct. Server-side enforcement is the security boundary.

### MH10. Evidence Upload Security (Future)

**Risk**: When photo evidence is added (deferred feature), file upload introduces path traversal, file type, and size risks.

**Required hardening (for future implementation)**:
- **File type**: Accept only `image/jpeg`, `image/png`. Validate both MIME type and magic bytes.
- **File size**: Max 5 MB per image, max 5 images per dispute.
- **Upload path**: Use S3 pre-signed URLs with server-controlled key paths: `evidence/{escrowId}/{disputeId}/{uuid}.{ext}`. Never let the client specify the S3 key.
- **Filename sanitization**: Strip path separators and special characters. Use server-generated UUIDs for S3 keys.
- **Content scanning**: Consider AWS S3 Malware Protection (cost-dependent) for production.

**Verdict**: Not applicable for MVP. Document requirements for future sprint.

---

## 3. Server Fix Specifications

### 3.1 Status of Previously Identified Issues

After reviewing the actual server code against the `r4-escrow-security-review.md`, here is the updated status:

| Issue | Status | Evidence |
|-------|--------|---------|
| **M1**: Idempotency race | **FIXED** | `EscrowIdempotencyRepository.cs:67` has `ConditionExpression = "attribute_not_exists(idempotencyKey)"`. `ConditionalCheckFailedException` caught at line 75, returns `false`. `EscrowService` checks return value and rejects duplicates. |
| **M2**: Release/refund auth | **FIXED** | `EscrowService.cs:270-274` (Release) and `:573-577` (Refund) both call `IsSystemOrAdmin(authenticatedUserId)`. Returns 403 for unauthorized callers. |
| **M3**: Optimistic concurrency | **FIXED** | `EscrowRepository.cs:115-120` adds `ConditionExpression "#status = :expectedStatus"` when `expectedCurrentStatus` is provided. All `UpdateStatusAsync` calls from `EscrowService` pass the expected status. |
| **M4**: Fayda ID enforcement | **FIXED** | `EscrowService.cs:137-160` checks both buyer and farmer Fayda IDs when `amount > FaydaThreshold && _userRepo != null`. Returns ESCROW_007 if unverified. |
| **S1**: Audit for Telebirr failures | **FIXED** | `AppendAuditAsync` calls added for `hold_failed` (line 253), `release_failed` (line 336), `refund_failed` (line 637). |
| **S2**: Dispute input validation | **FIXED** | Description length check at line 475, evidence refs count at line 486, evidence ref length at line 494. |
| **S3**: Locale-dependent number formatting | **OPEN** | `CropRepository.ToAttributeValue` still uses `.ToString()` without `CultureInfo.InvariantCulture`. |
| **S4**: PII in exception logs | **OPEN** | `EscrowFunction.cs` lines 131, 179, 227, 275, 327, 363 all log `ex.Message` directly. |
| **S5**: Cron action parsing | **OPEN** | `EscrowCronFunction.cs:84-88` still falls back to raw string parsing on JSON failure. |

### 3.2 Remaining Server Fixes Required

#### S3-FIX: Locale-Safe Number Formatting

**File**: `server/src/Data/CropRepository.cs` — `ToAttributeValue` method

**Problem**: Number-to-string conversion uses default culture. If Lambda thread culture ever changes (unlikely but possible), decimal separator could become `,` instead of `.`, corrupting DynamoDB data.

**Fix**: Add `CultureInfo.InvariantCulture` to all numeric `.ToString()` calls:

```csharp
// In ToAttributeValue:
int n => new AttributeValue { N = n.ToString(CultureInfo.InvariantCulture) },
long n => new AttributeValue { N = n.ToString(CultureInfo.InvariantCulture) },
double d => new AttributeValue { N = d.ToString(CultureInfo.InvariantCulture) },
float f => new AttributeValue { N = f.ToString(CultureInfo.InvariantCulture) },
decimal m => new AttributeValue { N = m.ToString(CultureInfo.InvariantCulture) },
```

**Priority**: Medium. Low probability, high impact if triggered.

#### S4-FIX: Sanitize Exception Messages in Logs

**File**: `server/src/Functions/EscrowFunction.cs` — all catch blocks

**Problem**: `ex.Message` from downstream services (future real Telebirr gateway) may contain phone numbers, Fayda IDs, or account numbers. These would be written to CloudWatch in plaintext.

**Fix**: Replace all `ex.Message` log entries with exception type only:

```csharp
// BEFORE (lines 131, 179, 227, 275, 327, 363):
context.Logger.LogError($"Error initiating escrow: {ex.Message}");

// AFTER:
context.Logger.LogError($"Error initiating escrow: [{ex.GetType().Name}]");
```

Also apply to `EscrowCronFunction.cs:117`:

```csharp
// BEFORE:
context.Logger.LogError($"EscrowCronFunction error for action '{action}': {ex.Message}");

// AFTER:
context.Logger.LogError($"EscrowCronFunction error for action '{action}': [{ex.GetType().Name}]");
```

And `EscrowCronFunction.cs:357`:

```csharp
// BEFORE:
context.Logger.LogError($"Reconciliation: Telebirr query failed for {escrowId}: {ex.Message}");

// AFTER:
context.Logger.LogError($"Reconciliation: Telebirr query failed for {escrowId}: [{ex.GetType().Name}]");
```

**Priority**: Medium. Becomes critical when real Telebirr gateway is integrated.

#### S5-FIX: Strict Cron Action Parsing

**File**: `server/src/Functions/EscrowCronFunction.cs:84-88`

**Problem**: On JSON parse failure, the raw body is used as the action string. A malformed payload could inject unexpected strings into the switch statement. While the switch's default case returns 400, the raw string is logged at line 103 (`Cron action: {action}`), creating a log injection vector.

**Fix**: Remove the catch fallback. Return 400 on invalid JSON:

```csharp
try
{
    var payload = JsonSerializer.Deserialize(request.Body, AppJsonContext.Default.DictionaryStringString);
    payload?.TryGetValue("action", out action);
}
catch (JsonException)
{
    return LambdaResponse.Error(400, "ESCROW_CRON_VALIDATION",
        "Invalid JSON in request body.");
}
```

Also sanitize the action string in the log at line 103 — truncate to a safe length:

```csharp
var safeAction = action?.Length > 50 ? action[..50] : action;
context.Logger.LogInformation($"Cron action: {safeAction}");
```

**Priority**: Medium. Low risk in current architecture (EventBridge sends controlled payloads), but defense-in-depth.

---

## 4. Validation Rules — Complete Specification

### 4.1 POST /escrow/initiate

| Field | Type | Validation | Enforced By | Error Code |
|-------|------|-----------|-------------|------------|
| `deliveryId` | string | Required, non-empty, must reference existing delivery | Server | ESCROW_002 |
| `buyerId` | string | Required, must match JWT `sub` claim | Server | ESCROW_006 |
| `farmerId` | string | Required, must be registered user, must != buyerId | Server | ESCROW_001 |
| `amount` | decimal | Required, 100.00–500,000.00 ETB, 2 decimal precision | Server (primary), Client (UX) | ESCROW_001 |
| `currency` | string | Required, must be `"ETB"` | Server | ESCROW_004 |
| `idempotencyKey` | string | Required, format `{actor}:{entityId}:{action}:{timestamp}`, timestamp must be valid long | Server | ESCROW_003 |
| (Fayda check) | — | If amount > 10,000 ETB, both buyer and farmer must have verified Fayda ID | Server | ESCROW_007 |
| (Delivery state) | — | Delivery must be in `pending` or `pickedUp` state | Server | ESCROW_010 |
| (Duplicate) | — | One escrow per deliveryId | Server (DeliveryIndex + CreateAsync condition) | ESCROW_009 |

### 4.2 POST /escrow/dispute

| Field | Type | Validation | Enforced By | Error Code |
|-------|------|-----------|-------------|------------|
| `escrowId` | string | Required, must reference existing escrow in `escrowed` state | Server | ESCROW_016, ESCROW_INVALID_TRANSITION |
| `disputedBy` | string | Required, must match JWT `sub`, must be buyer or farmer on escrow | Server | ESCROW_019 |
| `reason` | string | Required, must be one of 6 enum values | Server | ESCROW_017 |
| `description` | string | Optional, max 1000 chars | Server | ESCROW_017 |
| `evidenceRefs` | string[] | Optional, max 5 items, each max 200 chars, non-empty strings | Server | ESCROW_017 |
| `idempotencyKey` | string | Required, format `{actor}:{escrowId}:{action}:{timestamp}` | Server | ESCROW_003 |
| (Dispute window) | — | Must be within 48h of deliveryConfirmedAt | Server | ESCROW_018 |
| (Duplicate) | — | No existing disputeId on escrow | Server | ESCROW_020 |

### 4.3 GET /escrow/{id}

| Field | Type | Validation | Enforced By | Error Code |
|-------|------|-----------|-------------|------------|
| `id` (path) | string | Required, non-empty | Server | ESCROW_001 |
| (Authorization) | — | Caller must be buyer, farmer, or admin on this escrow | Server | ESCROW_022 |

### 4.4 GET /escrow/history

| Field | Type | Validation | Enforced By | Error Code |
|-------|------|-----------|-------------|------------|
| `status` (query) | string | Optional, must be valid PaymentStatus enum value | Server (filter) | — |
| `role` (query) | string | Optional, `buyer` or `farmer` | Server | — |
| `limit` (query) | int | Optional, 1–50, default 20 | Server | ESCROW_023 |
| `nextToken` (query) | string | Optional, must be valid base64-encoded pagination token | Server | ESCROW_023 |

### 4.5 Client-Side Pre-Validation (UX Only)

These checks run on the mobile client before sending the request. They are NOT security boundaries.

| Check | Location | Purpose |
|-------|---------|---------|
| Amount 100–500,000 ETB | `_validateAmount()` in EscrowRepository | Prevent obvious misentry |
| buyerId != farmerId | EscrowRepository.initiatePayment | Fast-fail UX |
| Fayda warning (> 10,000 ETB) | InitiatePaymentScreen | Advisory banner |
| Dispute window countdown | RaiseDisputeScreen | Prevent wasted attempt |
| Online check for disputes | EscrowRepository.raiseDispute | Show "internet required" |

---

## 5. Data Classification

### 5.1 Mobile (Isar) Data Classification

| Data Element | Classification | Retention | Notes |
|-------------|---------------|-----------|-------|
| Payment cache (Payment schema) | **Internal** | Until overwritten by server refresh | Read-only mirror of server state. Tampering has no effect. |
| PaymentIntent (queue) | **Confidential** | 24h after completion/failure | Contains user IDs and financial intent. Must be purged. |
| Idempotency keys | **Confidential** | Same as PaymentIntent | Contains user ID + timestamp. Purge with intent. |
| Auth tokens | **Secret** | Session lifetime | Stored in FlutterSecureStorage (encrypted). |
| Amounts (ETB) | **Internal** | Cache lifetime | Financial data visible to authorized user only. |
| Telebirr references | **Internal** | Cache lifetime | Truncate in list views, full display on detail only. |
| Dispute descriptions | **Internal** | Cache lifetime | May contain user-authored text. |

### 5.2 Server Data Classification

| Data Element | Classification | Retention | Notes |
|-------------|---------------|-----------|-------|
| Escrow records | **Confidential** | 7 years (regulatory) | Contains financial transaction data. |
| Audit trail | **Confidential** | 7 years (regulatory) | Append-only. No delete/update allowed. |
| Idempotency records | **Internal** | 30 days (TTL) | Auto-deleted by DynamoDB TTL. |
| Telebirr credentials | **Secret** | Rotated quarterly | AWS Secrets Manager (not env vars in production). |
| Phone numbers (in Telebirr requests) | **PII** | Not stored (passed through) | Must be redacted from all logs. |
| Fayda IDs | **PII** | Stored in Cognito only | Never stored in escrow tables. Checked at initiation time. |

### 5.3 What Must NEVER Be Stored on Mobile

- Telebirr account numbers / phone numbers of other users
- Fayda IDs
- `telebirrHoldRef`, `telebirrReleaseRef`, `telebirrRefundRef` (internal Telebirr operation refs)
- `buyerFaydaVerified`, `farmerFaydaVerified` (server-internal flags)
- Dispute `resolution`, `resolvedBy`, `resolvedAt` (admin-only fields)
- Full audit trail entries

The architecture doc (Appendix B) correctly excludes all of these. Verified.

---

## 6. Risk Acceptance

### Accepted Residual Risks

| # | Risk | Severity | Rationale for Acceptance | Mitigation |
|---|------|---------|------------------------|-----------|
| R1 | **Fayda check bypassed when `_userRepo` is null** | Medium | `EscrowService` accepts `IUserRepository?` as optional. If `USERS_TABLE` env var is not set, `_userRepo` is null and the Fayda check is skipped entirely (`EscrowFunction.cs:47-52`). This is acceptable for development (mock Telebirr, no real money). **Must be a hard production deployment blocker.** | Pre-deployment checklist: verify `USERS_TABLE` is set in all production Lambda configurations. Add a CloudWatch alarm for any successful escrow > 10,000 ETB where `buyerFaydaVerified == false`. |
| R2 | **Isar not encrypted on Android 8-9** | Low | Android file-based encryption is mandatory from Android 10+. Android 8-9 devices may not have it enabled. The data in Isar is informational (server is authoritative). | Accept for MVP. Minimum target is Android 8+ per CLAUDE.md. Revisit if user complaints about shared device data access emerge. |
| R3 | **Certificate pinning disabled** | High | Currently disabled because API certificates are not yet deployed (`api_client.dart:20-26`). All API traffic is unencrypted against MitM on the TLS layer. | **Hard blocker for production.** Must enable before any real Telebirr integration. Track as pre-production security requirement. |
| R4 | **No velocity/rate limiting on mobile** | Low | Server has rate limits (10 req/s per user for initiate). Mobile does not implement client-side rate limiting. A compromised client could hammer the server, but API Gateway rate limiting provides protection. | Accept for MVP. Server-side rate limits are the correct defense. |
| R5 | **Dispute intent not queued offline** | Low | Architecture decision to reject disputes when offline (Section 3.3). Risk: user may miss the 48h window if offline during the window. | Accepted trade-off. Queueing offline disputes could lead to sending expired disputes, wasting user trust. The 48h window is generous. Server enforcement is correct. |
| R6 | **Admin role check uses string prefix** | Low | `IsSystemOrAdmin` checks `userId.StartsWith("admin:", ...)` (`EscrowService.cs:982`). This convention works but could be spoofed if a user registration bug allows userIds starting with "admin:". | Accept for MVP. Cognito user pool enforces UUID format for `sub` claims. The "admin:" prefix is only used by internal system calls. Add Cognito user pool policy to reject "admin:" or "system" as custom user IDs. |

### Non-Accepted Risks (Must Fix Before Production)

| # | Risk | Fix | Owner |
|---|------|-----|-------|
| NR1 | Certificate pinning disabled | Enable in `ApiClient` with real cert hashes | Mobile team |
| NR2 | `USERS_TABLE` env var optional | Make mandatory in production Lambda config | Infra team |
| NR3 | PII in server logs (S4) | Sanitize `ex.Message` in all catch blocks | Server team (Task #3) |
| NR4 | Cron action parsing (S5) | Remove catch fallback, validate action strings | Server team (Task #3) |
| NR5 | Locale-dependent number formatting (S3) | Add `CultureInfo.InvariantCulture` | Server team (Task #3) |

---

## 7. Implementation Checklist for Agents

### For Mobile Implementation Agents (Tasks #4, #5)

- [ ] **MH2**: Generate idempotency key ONCE, persist to Isar BEFORE API call, reuse on retry
- [ ] **MH2**: Add `cleanupStaleIntents()` to `EscrowSyncService`, call on app startup
- [ ] **MH4**: Add `_isProcessing` lock to `EscrowSyncService.processQueuedIntents()`
- [ ] **MH4**: Use atomic `pendingSync → syncing` transition before API call
- [ ] **MH5**: Add `buyerId == farmerId` fast-fail check in `EscrowRepository`
- [ ] **MH7**: Ensure `LoggingInterceptor` is wrapped in `kDebugMode` check
- [ ] **MH7**: Do not log raw request/response bodies for `/escrow/*` paths
- [ ] **MH7**: Use localized error strings, never raw server messages in UI
- [ ] **MH8**: Cancel and delete `pendingSync` intents on user logout
- [ ] **MH9**: Call `RefreshEscrow` when entering `RaiseDisputeScreen`
- [ ] Do NOT include `commission`, `commissionRate`, or `farmerPayout` in initiate request body
- [ ] Do NOT expose release or refund actions in any mobile UI
- [ ] Do NOT store admin-only fields listed in Section 5.3

### For Server Implementation Agent (Task #3)

- [ ] **S3-FIX**: Add `CultureInfo.InvariantCulture` to numeric `.ToString()` in `CropRepository.ToAttributeValue`
- [ ] **S4-FIX**: Replace `ex.Message` with `ex.GetType().Name` in all `EscrowFunction.cs` catch blocks
- [ ] **S4-FIX**: Apply same fix to `EscrowCronFunction.cs` catch blocks
- [ ] **S5-FIX**: Replace catch-all with `catch (JsonException)` returning 400 in `EscrowCronFunction.cs`
- [ ] **S5-FIX**: Truncate action string in log to max 50 chars

---

**Last Updated**: 2026-02-19
**Version**: 1.0.0
