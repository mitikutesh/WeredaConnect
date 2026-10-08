# Sync Protocol Contract

## Version
1.9.0

## Parties
- **Mobile** (client) — enqueues operations offline, syncs when connected
- **Backend** (server) — receives sync requests, resolves conflicts, returns results

---

## Overview

The sync protocol enables offline-first operation for WeredaConnect's Ethiopian rural users operating on intermittent 3G connectivity. The mobile client queues all write operations locally (in Isar database) and syncs them to the server when connectivity is available. The server resolves conflicts using version numbers and returns the authoritative state.

**Key Features**:
- **Idempotency**: Prevents duplicate operations on retry using composite idempotency keys
- **Conflict Resolution**: Server-wins strategy with automatic local updates
- **Exponential Backoff**: Retry failed operations at 5s, 15s, 45s intervals
- **Batch Processing**: Process up to 50 operations per sync request
- **7-Day TTL**: Idempotency keys expire after 7 days to prevent unbounded storage

---

## Request Format

### Sync Request (Client → Server)

```json
{
  "syncBatchId": "uuid-v4",
  "clientId": "device-uuid",
  "operations": [
    {
      "operationId": "uuid-v4",
      "idempotencyKey": "farmer-123:crop_listing:crop-456:create:1707998400000",
      "entityType": "crop_listing | trip | delivery | order | buyer_intent | match_event | rating | message",
      "entityId": "uuid-v4",
      "action": "create | update | delete",
      "clientVersion": 3,
      "data": { },
      "timestamp": "2026-02-09T12:00:00Z"
    }
  ]
}
```

**Field Rules:**
- `syncBatchId` — unique per sync attempt; used for batch-level idempotency
- `idempotencyKey`: **composite key format** `{userId}:{entityType}:{entityId}:{action}:{timestamp}` (prevents duplicate processing on retry). The server scopes it to the authenticated caller (1.7.0, see Idempotency Key Conventions rule 5).
- `clientVersion`: the version the client had when making the change. The server accepts it here (op level, preferred) or as `data.version` (what the shipped mobile and web clients send); when both are present the op-level value wins. `order` updates REQUIRE one of them: a versionless order update is rejected with `ORDER_VALIDATION: version is required for order updates ...` and nothing is written. For other entity types a missing version is treated as 0, which yields a `conflict` against any stored entity (1.3.0, SIM-08).
- `timestamp` — ISO 8601, UTC; used for display ordering, NOT for conflict resolution
- `data` — entity-specific payload; schema defined per `entityType`

**Idempotency Key Format**:
```
{userId}:{entityType}:{entityId}:{action}:{timestamp}
```
Example: `farmer-123:crop_listing:crop-456:create:1707998400000`

This format ensures:
- **User isolation**: Different users can create entities with the same ID. Since 1.7.0 the server enforces this itself: every key is scoped to the authenticated caller, and the `{userId}` segment is not trusted (Idempotency Key Conventions rule 5).
- **Action uniqueness**: Create vs. update vs. delete are distinct operations
- **Timestamp precision**: Millisecond-level granularity prevents collisions

---

## Response Format

### Sync Response (Server → Client)

```json
{
  "syncBatchId": "uuid-v4",
  "results": [
    {
      "operationId": "uuid-v4",
      "status": "success | conflict | duplicate | deferred | error",
      "serverVersion": 4,
      "serverData": { },
      "message": "optional human-readable note"
    }
  ]
}
```

**Field Rules:**
- `status: "success"` — operation applied; `serverVersion` is the new version
- `status: "conflict"` — client was behind; `serverData` contains the server's current state; `serverVersion` is the server's version
- `status: "duplicate"` — idempotency key already processed; treated like success (removed from queue)
- `status: "deferred"` — the server cannot verify the op **yet** (a dependency has not
  synced up). The client MUST re-queue it **without** consuming a retry attempt and retry
  later; the server hard-fails (`error`) it if it stays unverifiable past its window. This
  keeps offline-first: the op is never lost. Currently emitted for a delivery
  `pickedUp`/`delivered` transition whose QR handoff secret (farmer-authored nonce) has not
  yet reached the server. `serverData`/`serverVersion` are absent.
- `status: "error"` — operation failed (validation, auth, etc.); `message` explains why

---

## Conflict Resolution Rules

**Strategy**: Server Wins (implemented in Phase R3)

1. **Version wins, not timestamp.** Compare `clientVersion` against the server's current version. Timestamps are informational only.
2. **Server is authoritative.** On conflict, the server returns its state. The client **automatically accepts** the server version and updates the local Isar database.
3. **Last-write-wins within same version.** If `clientVersion` equals server version, the operation is applied (optimistic concurrency).
4. **Client behind = conflict.** If `clientVersion < serverVersion`, return `conflict` with the server's current data.
5. **Deletes are soft.** Deleted entities are marked with a `deletedAt` timestamp, not physically removed. This prevents sync issues with other clients that haven't synced yet.

### Client-Side Conflict Resolution (Mobile)

When the client receives `status: "conflict"`:

1. **Parse server data**: Extract `serverData` and `serverVersion` from response
2. **Update local entity**: Replace local Isar record with server data
3. **Update version**: Set local version to `serverVersion` to prevent future conflicts
4. **Mark as synced**: Operation is marked as `synced`, not `failed` (conflict is resolved, not an error)
5. **No user intervention**: For MVP, conflicts resolve automatically without UI notification

**Rationale**:
- Solo farmers rarely have conflicts (one user per device)
- Automatic resolution prevents confusing users with technical merge dialogs
- Future phases can add merge strategies for specific fields (e.g., combine descriptions)

---

## Idempotency Key Conventions

1. **One key per logical operation.** If the user creates a crop listing, that gets one idempotency key — even if the sync retries 10 times.
2. **Keys are composite strings.** Format: `{userId}:{entityType}:{entityId}:{action}:{timestamp}`. Generated client-side at the time of the user action, NOT at sync time.
3. **Server checks before processing.** On receiving an operation, the server looks up the idempotency key in the `SyncMetadata` DynamoDB table. If found, return the stored result without re-processing.
4. **Keys expire after 7 days.** DynamoDB TTL automatically deletes idempotency records after 7 days. Clients should not retry operations older than 7 days.
5. **Keys are scoped to the caller (1.7.0, SIM-48/ESC-11/SEC-03).** The server never trusts the `{userId}` segment. It stores and looks up a key as sent when the key starts with the caller's Cognito `sub` followed by `:` (the documented format), and as `{sub}:{key}` otherwise (web keys such as `web_create_{id}`, keys carrying another user's id, any other string). So the same key sent by two users is two different operations, and `duplicate` only means the caller itself already sent that key: another user can no longer pre-claim your key and get your operation dropped. Any non-empty key string is accepted; the documented format is still the recommended one. If the caller id cannot scope a key (it contains `:`, which a Cognito `sub` never does) the operation is answered `error` with `SYNC_VALIDATION: caller identity cannot scope the idempotency key.` and nothing is processed. No client change is needed.

### SyncMetadata Table Schema (DynamoDB)

| Field | Type | Description |
|-------|------|-------------|
| `idempotencyKey` | String (PK) | Caller-scoped key (rule 5): the client key as sent when it starts with `{sub}:` (documented format `{userId}:{entityType}:{entityId}:{action}:{timestamp}`), otherwise `{sub}:{clientKey}` |
| `timestamp` | Number (SK) | Unix timestamp (milliseconds) when operation was processed |
| `operationId` | String | Client-generated UUID for the sync operation |
| `result` | String | JSON-encoded sync result (for returning on duplicate requests) |
| `expiresAt` | Number (TTL) | Unix timestamp when this record should be deleted (7 days from creation) |

**Performance**: Every stored key starts with the caller's `sub`, so the table naturally partitions across users, preventing hot partition issues.

---

## Error Codes

| Code | Meaning | Client Action |
|------|---------|---------------|
| `SYNC_SUCCESS` | Operation applied | Remove from local queue |
| `SYNC_CONFLICT` | Client version behind | Accept server data, re-queue if needed |
| `SYNC_DUPLICATE` | Idempotency key already processed | Remove from local queue (already done) |
| `SYNC_VALIDATION` | Payload failed validation | Fix data, re-queue with new idempotency key |
| `SYNC_AUTH` | Authentication failed | Re-authenticate, retry |
| `SYNC_RATE_LIMIT` | Too many requests | Back off, retry with exponential delay |
| `SYNC_SERVER_ERROR` | Server failure | Retry with exponential backoff |

---

## Invariants

These rules must always hold:

1. **No data loss.** An operation in the client's local queue is never silently dropped. It either succeeds, conflicts (with resolution), or errors (with retry).
2. **Idempotent retries.** Sending the same `idempotencyKey` twice from the same caller produces the same result. Another user's key never affects your operations.
3. **Monotonic versions.** Entity versions only increase. A version number is never reused or decremented.
4. **Offline queue persistence.** The client's sync queue survives app restarts and device reboots (Isar database).
5. **Order independence.** Operations within a batch may be processed in any order. If ordering matters, use separate batches.

---

## Retry Logic and Exponential Backoff

**Client-Side Retry Strategy** (implemented in `SyncExecutor`):

| Attempt | Delay | Action |
|---------|-------|--------|
| 1 | Immediate | First sync attempt |
| 2 | 5 seconds | Retry after transient failure |
| 3 | 15 seconds | Retry with increased backoff |
| 4 | 45 seconds | Final retry attempt |
| 5+ | Permanent failure | Mark operation as `failed`, require manual intervention |

**When to Retry (with backoff, increments `retryCount`)**:
- 5xx server errors (500, 502, 503, 504)

**When to Revert to Pending (no `retryCount` increment)**:
- Network errors (timeout, connection refused, DNS failure) — the operation payload is valid, only the transport failed. `syncNow()` exits immediately; the periodic worker (15 min) or connectivity listener triggers a fresh attempt.
- 429 Too Many Requests (rate limiting) — revert and wait
- 401/403 Unauthorized — revert (AuthInterceptor should refresh token)

**When NOT to Retry**:
- 400 Bad Request (validation error) → Mark as `failed`, requires data fix
- 409 Conflict → Resolve conflict, mark as `synced` (not retry)
- 422 Unprocessable Entity → Mark as `failed`, requires data fix
- Fraud rejections (`FRAUD_*`, including `FRAUD_ACCOUNT_HELD` since 1.9.0, and `SYNC_VALIDATION`) → Mark as permanently rejected

**Retry State Management**:
- `retryCount` field in `SyncOperation` entity tracks attempts (only incremented by server errors, not network failures)
- Max retries: 3 (configurable in `SyncExecutor`)
- Failed operations remain in queue for manual inspection

**Crash Recovery**:
- If the app is killed during sync, operations stuck in `syncing` status are recovered by `recoverStaleSyncing()` at the start of each `syncNow()` call. They are reset to `pending` with `retryCount = 0`.

---

## Data Transfer Optimization

Given Ethiopian mobile data costs ($0.50 per MB, average farmer income $300/month):

1. **Compress payloads.** Use gzip for request/response bodies over 1KB (implemented in Dio interceptor).
2. **Delta sync.** Only send changed fields in `data`, not full entities (future optimization).
3. **Batch operations.** Group up to 50 pending operations into a single HTTP request (implemented in `SyncExecutor`).
4. **Pagination.** Server responses with >50 results use base64-encoded `nextToken` for continuation.
5. **Minimize metadata.** Keep field names short (e.g., `ver` instead of `version`) in JSON payloads.
6. **Background sync only on WiFi.** Option in app settings to restrict automatic sync to WiFi connections.

---

## Implementation Status

**Phase R3 (Complete)**:
- ✅ Idempotency tracking with composite keys
- ✅ Server-wins conflict resolution
- ✅ Exponential backoff retry logic
- ✅ Batch processing (up to 50 operations)
- ✅ 7-day TTL on SyncMetadata table
- ✅ Integration tests validating sync protocol

**Future Phases**:
- ⏳ Delta sync (only send changed fields)
- ⏳ gzip compression for payloads >1KB
- ⏳ User-controlled merge strategies for conflicts
- ⏳ WiFi-only sync option in settings

---

## Create-conflict echo (1.4.0)

A `create` for an entity id that already exists returns `status: "conflict"` with
`serverVersion`, but `serverData` is filled **only for the owner or a party**: the crop's
farmer, the trip's trucker, the order's buyer, farmer or trucker, the delivery's buyer,
farmer or trucker. Anyone else gets the conflict status with `serverData: null`
(role-visibility rule). The client treats a null `serverData` as "already exists, not
yours" and drops the operation.

## Operation errors added (1.4.0)

| Message prefix | Meaning | Client action |
|----------------|---------|---------------|
| `AUTH_040:` | fresh password needed (order `payment_submitted` up to 10,000 ETB, delivery `disputed` on large orders) | keep the operation pending, re-authenticate, sync again |
| `AUTH_041:` | fresh Fayda login needed (order `payment_submitted` above 10,000 ETB; 1.6.0) | keep the operation pending, run the Fayda link flow, sync again |
| `ORDER_KYC_REQUIRED:` | phone or Fayda verification missing for this payment | revert the local order status; see the reason below |

Since 1.5.0 the KYC message is `ORDER_KYC_REQUIRED: <reason>: <text>`:

| Reason | Who must verify | Client action |
|--------|-----------------|---------------|
| `buyer_phone` | the buyer (phone) | offer Fayda; send the same payment again only when the link response has `phoneVerified: true` |
| `buyer_fayda` | the buyer (Fayda, above 10,000 ETB) | offer Fayda; on success send the same payment again |
| `farmer_phone` | the farmer (phone) | explain only; do not send the buyer to Fayda |
| `farmer_fayda` | the farmer (Fayda, above 10,000 ETB) | explain only; do not send the buyer to Fayda |
| `unavailable` | nobody (profiles could not be read) | explain, try later |

A failed, cancelled or refused verification (`FAYDA_005`/`006`/`007`) leaves the payment
unsent. Clients fall back to the wording ("farmer", "phone") for servers older than 1.5.0.

---

## Operation errors added (1.9.0, planned FRD-03, FRD-04)

| Message prefix | Where | Client action |
|----------------|-------|---------------|
| `FRAUD_ACCOUNT_HELD:` | crop, trip and order `create`; order update to `payment_submitted` | permanent (the `FRAUD_*` rule above): mark rejected, show the message, do not retry; on `payment_submitted` also revert the local order status as for `ORDER_KYC_REQUIRED` |
| `AUTH_040:` / `AUTH_041:` | order update to `payment_submitted`, now also raised by the account's risk tier at any amount (MEDIUM: password, after Fayda above 10,000 ETB as today; HIGH: Fayda, then password) | unchanged: keep the operation pending, re-authenticate (Fayda first when both are asked), sync again |
| `ORDER_VALIDATION: This order is under review and cannot be accepted. Contact support.` | farmer's order update to `accepted` while the order's escrow is `held` (FRD-03) | show the message; the order stays `funded` until an admin decides, and a retry gets the same answer |

The message after `FRAUD_ACCOUNT_HELD:` is `Your account is under review. Some actions are paused.
Contact support.` The risk tier and score are never part of the response. The shipped mobile
`SyncExecutor` already treats every `FRAUD_` prefix as permanent, so permanence needs no change;
the payment revert and the message need a `SyncExecutor` change (FRD-06), because today only
`ORDER_KYC_REQUIRED` reverts the local order and the batch error message names only
`FRAUD_DUPLICATE_LISTING` and `FRAUD_VELOCITY_EXCEEDED`. The web repositories, which call the API
directly, handle the code in FRD-06 too. Design:
`specs/architecture/fraud-detection-architecture.md` 3.0.0, section 17.

---

## Changelog

| Version | Date | Change |
|---------|------|--------|
| 1.9.0 | 2026-10-06 | `FRAUD_ACCOUNT_HELD:` operation error (planned FRD-04) on crop, trip and order create and on order `payment_submitted`, permanent under the existing `FRAUD_*` rule; `AUTH_040:` and `AUTH_041:` may also come from the account's risk tier at any amount; `ORDER_VALIDATION:` on the farmer's accept while the order's escrow is `held` (planned FRD-03) |
| 1.7.0 | 2026-10-01 | Idempotency keys are scoped to the authenticated caller (SIM-48, ESC-11, SEC-03): another user's key can no longer pre-claim yours; documented-format keys are stored unchanged, other keys as `{sub}:{key}`; no client change |
| 1.6.0 | 2026-09-29 | Payment step-up tiers: `AUTH_040` on every `payment_submitted` up to 10,000 ETB without a fresh password; `AUTH_041` above 10,000 ETB without a fresh Fayda login (the password does not count) |
| 1.5.0 | 2026-09-29 | `ORDER_KYC_REQUIRED` carries a reason token (`buyer_phone`, `buyer_fayda`, `farmer_phone`, `farmer_fayda`, `unavailable`); the prefix is unchanged |
| 1.4.0 | 2026-09-26 | Create-conflict echo is party-scoped (an existing crop, trip, order or delivery is returned in `serverData` only to its owner or a party); `AUTH_040` and `ORDER_KYC_REQUIRED` operation errors |
| 1.3.0 | 2026-09-25 | Versioning (SIM-08): op-level `clientVersion` is now honoured (it was silently ignored), `data.version` remains accepted, op-level wins; order updates require a version (no fallback to the stored version); web order client sends the current version |
| 1.2.0 | 2026-03-03 | Updated: Network errors no longer increment retryCount; added crash recovery for stale syncing operations; clarified retry vs. revert categories |
| 1.1.0 | 2026-02-15 | Added: Composite idempotency key format, server-wins conflict resolution details, exponential backoff retry logic, SyncMetadata table schema, implementation status |
| 1.0.0 | 2026-02-09 | Initial contract: sync request/response, conflict resolution, idempotency |

---

**Last Updated**: 2026-10-06
**Version**: 1.9.0
