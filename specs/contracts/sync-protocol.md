# Sync Protocol Contract

## Version
1.2.0

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
      "idempotencyKey": "uuid-v4",
      "entityType": "crop_listing | trip | delivery | order | buyer_intent | match_event | rating",
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
- `idempotencyKey` — **composite key format**: `{userId}:{entityType}:{entityId}:{action}:{timestamp}` (prevents duplicate processing on retry)
- `clientVersion` — the version the client had when making the change
- `timestamp` — ISO 8601, UTC; used for display ordering, NOT for conflict resolution
- `data` — entity-specific payload; schema defined per `entityType`

**Idempotency Key Format**:
```
{userId}:{entityType}:{entityId}:{action}:{timestamp}
```
Example: `farmer-123:crop_listing:crop-456:create:1707998400000`

This format ensures:
- **User isolation**: Different users can create entities with the same ID
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
      "status": "success | conflict | error",
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

### SyncMetadata Table Schema (DynamoDB)

| Field | Type | Description |
|-------|------|-------------|
| `idempotencyKey` | String (PK) | Composite key: `{userId}:{entityType}:{entityId}:{action}:{timestamp}` |
| `timestamp` | Number (SK) | Unix timestamp (milliseconds) when operation was processed |
| `operationId` | String | Client-generated UUID for the sync operation |
| `result` | String | JSON-encoded sync result (for returning on duplicate requests) |
| `expiresAt` | Number (TTL) | Unix timestamp when this record should be deleted (7 days from creation) |

**Performance**: With composite keys including userId, the table naturally partitions across users, preventing hot partition issues.

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
2. **Idempotent retries.** Sending the same `idempotencyKey` twice produces the same result.
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
- Fraud rejections (`FRAUD_*`, `SYNC_VALIDATION`) → Mark as permanently rejected

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

## Changelog

| Version | Date | Change |
|---------|------|--------|
| 1.2.0 | 2026-03-03 | Updated: Network errors no longer increment retryCount; added crash recovery for stale syncing operations; clarified retry vs. revert categories |
| 1.1.0 | 2026-02-15 | Added: Composite idempotency key format, server-wins conflict resolution details, exponential backoff retry logic, SyncMetadata table schema, implementation status |
| 1.0.0 | 2026-02-09 | Initial contract: sync request/response, conflict resolution, idempotency |

---

**Last Updated**: 2026-03-03
**Version**: 1.2.0
