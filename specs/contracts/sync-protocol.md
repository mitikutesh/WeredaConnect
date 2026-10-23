# Sync Protocol Contract

## Version
1.18.0

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

1. **No data loss.** An operation in the client's local queue is never silently dropped. It either succeeds, conflicts (with resolution), errors (with retry), or is rejected for good, in which case the client undoes its local change and tells the user why (1.10.0).
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
- Order rejections (every `ORDER_*` code except `ORDER_KYC_REQUIRED`, since 1.10.0) → Mark as permanently rejected and revert the local order (see Permanent order rejections)
- `DELIVERY_CANCEL_NOT_ALLOWED` (since 1.11.0) → Mark as permanently rejected and revert the local delivery (see Delivery cancel)
- `DELIVERY_DISPUTE_REFUSED` and `DELIVERY_DISPUTE_EXPIRED` (since 1.12.0) → Mark as permanently rejected and revert the local delivery (see Delivery dispute); `DELIVERY_DISPUTE_RETRY` is transient and retried

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
| `AUTH_040:` | fresh password needed (order `payment_submitted` up to 10,000 ETB, delivery `disputed` on large orders, or with the order or a usable total missing, since 1.14.0) | keep the operation pending, re-authenticate, sync again |
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
since 1.10.0 it also reverts a permanently rejected order operation (Permanent order rejections),
so only the message needs FRD-06: the batch error message names only
`FRAUD_DUPLICATE_LISTING` and `FRAUD_VELOCITY_EXCEEDED`. The web repositories, which call the API
directly, handle the code in FRD-06 too. Design:
`specs/architecture/fraud-detection-architecture.md` 3.0.0, section 17.

### Client handling (1.18.0, FRD-06)

| Code | Mobile client (sync queue) | Web client (calls `/sync` directly) |
|------|----------------------------|-------------------------------------|
| `FRAUD_ACCOUNT_HELD:` | Permanent, as before. A refused order operation is reverted as in 1.10.0 (a refused create removes the local order, a refused `payment_submitted` puts the server's order back); a refused crop or trip create keeps its local row, like the other `FRAUD_*` codes. For every entity and action the user is told, in en, am and om: "Your account is under review, so this action is paused. Contact support." The batch result's error message picks the code too. | The refused call fails with the same text (crop, trip and order create, payment). |
| `AUTH_041:` | Unchanged: kept pending, the Fayda login opens, the operation goes again after it. The prompt no longer names an amount, since the server may ask at any amount. | Unchanged: the payment screen opens the Fayda login and sends the same payment again, with the same prompt. |

The escrow routes (`/escrow/initiate`, `/escrow/order-checkout`, `/escrow/dispute`) and the
escrow `held` status are covered in `escrow-protocol.md` 1.23.0. Clients older than 1.18.0 show
a generic refusal for `FRAUD_ACCOUNT_HELD` and do not retry it either.

---

## Permanent order rejections (1.10.0, ESC-12 part A)

Every `ORDER_*` operation error except `ORDER_KYC_REQUIRED` is permanent: sending
the same payload again gets the same answer. Today the server sends
`ORDER_VALIDATION:` and `ORDER_UNAUTHORIZED:`; the planned `ORDER_DEADLINE_PASSED`,
`ORDER_CANCEL_NOT_ALLOWED`, `ORDER_ACTION_NOT_ALLOWED`, `ORDER_TRIP_NOT_ELIGIBLE` and
`ORDER_TRANSPORT_ATTEMPTS_EXCEEDED` fall under the same rule. `ORDER_KYC_REQUIRED`
keeps its own revert and Fayda prompt (1.4.0, 1.5.0).

Mobile client (sync queue):

| Case | Client action |
|------|---------------|
| `ORDER_*` error | Mark the operation rejected: it is never sent again and a reconnect does not reset it. A rejected order operation is not counted as failed (it was reverted and explained). |
| Refused order `create` | The server never stored the order: remove the local order. |
| Refused order update | Re-read `GET /orders/{id}` and replace the local order with it (server wins), unless another operation for that order is still queued (its own outcome settles the row). A 404 or 403 on that read removes the local order. Any other failed read (offline, 401, 5xx) puts the local version back to the lowest version the order's refused operations sent, so the next pull overwrites the row. |
| Any other permanent rejection of an order operation (`FRAUD_*`, `SYNC_VALIDATION`, a whole batch refused with HTTP 400 or 422) | Reverted the same way. FRD-06 adds the `FRAUD_ACCOUNT_HELD` wording. |
| Any refused `create` (every entity type, every permanent code) | Also reject the operations queued behind it for that entity: they could only get a transient "not found" and would be resent forever. |
| Permanent rejection of any other entity (listing, trip, delivery, rating, message) | Not reverted until part B. Explained (the listing-form wording for `FRAUD_DUPLICATE_LISTING` and `FRAUD_VELOCITY_EXCEEDED`, otherwise "This change could not be saved on the server") and still counted as failed, because its local change never reached the server. Background match events are neither. |
| Pulls | A rejected operation no longer protects its row: `GET /orders` (and the crop, trip and delivery pulls) may overwrite it. Pending, syncing and retryable failed operations still do. |
| Reason | A localized message (en, am, om) says the change was not allowed, that a refused order was removed, or that a change was not saved. Refusals from one batch share one message per distinct text. A rejection raised before the signed-in screen listens (the first sync after a launch) is kept, up to 10, and shown once it does; it is shown only to the user whose operation it was, and logout drops kept ones. |

Web client (no queue, calls `/sync` directly): a `conflict` result is a failure, like
`error` and `deferred`, because nothing was written. Before an update the web client reads
the entity's current version and fails when that read fails, instead of guessing one. A
rating `create` answered `conflict` means the rating already exists.

Rules for server authors:
- An `ORDER_*` operation error must be deterministic. A transient order failure uses no
  `ORDER_` prefix: the unprefixed `Order not found.` on an update stays retryable.
- `ORDER_VALIDATION: Unknown fields: ...` is now permanent, so the server must accept a
  new order field before any client sends it.
- `REPUTATION_RESTRICTED`, `TRIP_HAS_BOOKINGS`, `TRIP_CAPACITY_EXCEEDED`,
  `CROP_HAS_ACTIVE_ORDERS` and `APP_UPDATE_REQUIRED` are not supported by clients until
  ESC-12 part B (trip and crop revert, their own messages). A server must not send them
  before that ships (spec `three-party-escrow-commitments-reputation.md` K1: client
  support first).

Clients older than 1.10.0 keep retrying `ORDER_*` errors and need no server change.

---

## Delivery cancel (1.11.0, ESC-08)

A party cancels a delivery with `action: "update"` and
`data: {"status": "cancelled", "version": n}`. The server still accepts `action: "delete"`,
which app versions before 1.11.0 send and may still hold in their offline queues, as the same
cancel under the same rules; clients do not rewrite queued operations. Rules and errors:
`delivery-verification-api.md` 1.9.0 (Cancel).

| Server answer | Mobile client (sync queue) | Web client |
|---|---|---|
| `success` | Synced. | Done. |
| `DELIVERY_CANCEL_NOT_ALLOWED:` (picked up meanwhile) | Permanent: mark the operation rejected (never resent, not reset by a reconnect) and revert the local delivery: re-read `GET /deliveries/{id}` and replace the local row (server wins) unless another operation for it is still queued; when that read fails, put the local version back to the lowest version the refused operations sent so the next delivery pull overwrites the row. A delivery is never removed. Explained in en, am and om ("This delivery was already picked up, so it can no longer be cancelled..."). It still counts as failed, like other rejected delivery operations. | Shows the same message. |
| `conflict` with the row (another party cancelled first, or the version moved) | Server wins, as for any conflict. | A re-read showing `cancelled` means the cancel is done; anything else is a failure. |
| `DELIVERY_UNAUTHORIZED:` and every other `DELIVERY_*` code | Unchanged: retried. | Failure. |

The client shows Cancel only while a delivery is `pending` and refuses a local cancel of any
later status, so the refusal only comes back after a race (a pickup synced from another device).
Clients older than 1.11.0 keep retrying `DELIVERY_CANCEL_NOT_ALLOWED`, as they retried the
`DELIVERY_UNAUTHORIZED` their farmer and buyer `delete` got before.

---

## Delivery dispute (1.12.0, ESC-07)

A party disputes a delivered delivery with `action: "update"` and
`data: {"status": "disputed", "disputeReason": "...", "version": n}` (unchanged). Since 1.12.0
the server freezes the order's escrow before it writes the delivery (`escrow-protocol.md` 1.12.0,
"Additions in 1.12.0"), so a dispute can be refused with nothing written. A client order update
to `disputed` is refused `ORDER_UNAUTHORIZED` (`order-lifecycle.md` 1.14.0); no shipped app sent
it, and clients since 1.10.0 revert it like any `ORDER_*` refusal.

| Server answer | Mobile client (sync queue) | Web client |
|---|---|---|
| `success` | Synced. | Done. |
| `DELIVERY_DISPUTE_REFUSED:` (the payment is queued for an admin payout, being settled or no longer held, the order's payment belongs to another delivery, or no payment is on record for the order) | Permanent: mark the operation rejected and revert the local delivery exactly as for `DELIVERY_CANCEL_NOT_ALLOWED` (re-read `GET /deliveries/{id}`, server wins; else the lowest sent version). Explained in en, am and om with one neutral text for every reason ("This dispute can't be opened right now. Contact support."). | Shows the same message. |
| `DELIVERY_DISPUTE_EXPIRED:` (after the 48-hour window) | Permanent since 1.12.0, reverted the same way, explained with the existing "Dispute window has expired" text. Before 1.12.0 it was retried and the local row stayed `disputed`. | Shows the same message. |
| `DELIVERY_DISPUTE_RETRY:` (the order or escrow the step-up is sized by could not be read, since 1.14.0, or the freeze could not be decided) | Transient: marked failed and retried, nothing was written. | Failure (generic message); the user can try again. |
| `conflict` with the row | Server wins, as for any conflict. The row may already show `disputed`: when the delivery changed after the version check but the payment was frozen, or the delivery was already disputed (a retry after the server stopped between the delivery and the order write, or the other party's dispute), the server finishes the freeze and the order and answers `conflict` with the disputed row; nothing is written to the delivery again. | The row in the answer decides: `disputed` means the dispute is done, anything else is a failure; an answer without a row falls back to a re-read. |
| error after a successful freeze (for example a throttled delivery write) | Retried; the retry finds the payment already frozen and finishes the delivery and order. | Failure; the user's next try finishes it the same way. |

Clients older than 1.12.0 treat `DELIVERY_DISPUTE_REFUSED` as a transient error: three tries per
sync, and `retryFailed` puts the operation back on every reconnect, so they resend it without end
and keep the local row `disputed`. The server answers each the same way and audits the refusal once
per escrow, delivery and reason a day.

---

## Order cancel refusal (1.13.0, ESC-06)

The server now sends `ORDER_CANCEL_NOT_ALLOWED:` when a buyer cancels an `accepted` order before
its `pickupDeadlineAt`, after its crops were picked up, or while its money is being settled or
reviewed (`order-lifecycle.md` 1.15.0, Transport failure cancel). It is permanent under the
`ORDER_*` rule above (mark rejected, never resent, revert the local order), with its own message in
en, am and om. Unlike the other `ORDER_*` codes it depends on the server clock: the same cancel can
be allowed later. The client never resends the refused operation; the buyer cancels again once it is
allowed. The cancel's read or write failures (trip, delivery, escrow, order) answer unprefixed errors
(`... Please try again later.`), which mobile retries.

| Server answer | Mobile client (sync queue) | Web client |
|---|---|---|
| `success` | Synced; the order is `cancelled`, the refund follows within the hourly sweep. | Done. |
| `ORDER_CANCEL_NOT_ALLOWED:` | Permanent: revert as for any `ORDER_*` (re-read `GET /orders/{id}`), show `errorOrderCancelNotAllowed`. | Failure with the same message. |
| `conflict` (the order moved since the client read it) | Server wins, as for any conflict. | Failure. |
| unprefixed error | Retried. | Failure (generic message). |

The client shows Cancel on an `accepted` order only once the device clock is past
`pickupDeadlineAt`, and mobile refuses a local cancel before it (`OrderCancelNotAllowedException`,
same message), so the refusal only comes back after a clock difference or a race (a pickup synced
first). A delivery create racing the cancel is refused by the slot write:
`DELIVERY_VALIDATION: The order no longer takes a delivery (it must be accepted, pickup_confirmed or in_transit).`
Clients older than 1.13.0 never send an `accepted -> cancelled` change.

---

## Message contact refusal (1.15.0, ESC-48)

A `message` create into a context whose contact is not open is refused with
`MESSAGE_CONTACT_LOCKED: Contact opens after payment is held in escrow and the farmer accepts the order.`:
every `crop` and `trip` thread, an `order` thread whose order is not open (before the farmer
accepted, or the money went back: `cancelled`, `rejected`, `dispute_resolved`, an admin override
refund), and a `delivery` thread whose order is not open or missing (`messaging.md` 1.2.0,
`order-lifecycle.md` 1.18.0). A refused create whose `messageId` the same sender already stored
answers `duplicate` instead.

| Server answer | Mobile client (sync queue) | Web client |
|---|---|---|
| `MESSAGE_CONTACT_LOCKED:` | Permanent: mark the operation rejected (never resent), remove the local copy of the message (the server never stored it), explain it in en, am and om ("Contact is closed for this conversation.": the refusal does not say why, so the text does not guess). Counted as failed like other non-order rejections. | Failure with the same message; nothing is cached. |
| unprefixed error (the order could not be read) | Retried. | Failure (generic message). |

Clients from before 1.15.0 do not know the code: they retry it like any other error (up to three
times) and keep their local copy.

---

## Changelog

| Version | Date | Change |
|---------|------|--------|
| 1.18.0 | 2026-10-23 | Client handling of fraud codes (FRD-06): `FRAUD_ACCOUNT_HELD` gets its own text in en, am and om on mobile (every entity and action, and the batch error message) and on web; order operations keep the 1.10.0 revert, crop and trip creates keep their local row; the `AUTH_041` prompt no longer names an amount; no server change |
| 1.15.0 | 2026-10-17 | Message contact refusal (ESC-48): `MESSAGE_CONTACT_LOCKED` for a message into a `crop` or `trip` thread, or into an `order` or `delivery` thread whose order's contact is not open; permanent, the mobile client removes its local copy and explains it in en, am and om; a refused resend of a message the sender already stored answers `duplicate`; message rows in `/sync` data carry no phone; delivery rows in `/sync` data carry no phone (`delivery-verification-api.md` 1.15.0) |
| 1.14.0 | 2026-10-17 | Delivery dispute step-up fails closed (FUP-26): when the order (or a legacy delivery's `DeliveryIndex` escrow) that sizes the step-up cannot be read, the dispute answers `DELIVERY_DISPUTE_RETRY` before the freeze and nothing is written (it was let through without the step-up); a missing order, or a total that is missing, unreadable, zero or negative, answers `AUTH_040` to a stale session at any amount; clients unchanged (`DELIVERY_DISPUTE_RETRY` retried, `AUTH_040` kept pending) |
| 1.13.0 | 2026-10-10 | Order cancel refusal (ESC-06): `ORDER_CANCEL_NOT_ALLOWED` is sent for a buyer cancel of an `accepted` order before its pickup deadline, after pickup or while its money is being settled; permanent (server-clock dependent, never resent), its own localized message on mobile and web; the cancel's read and write failures are unprefixed and retried; a delivery create on an order that no longer takes one is refused with `DELIVERY_VALIDATION`; order rows carry `pickupDeadlineAt` |
| 1.12.0 | 2026-10-10 | Delivery dispute (ESC-07): the server freezes the escrow before it writes a disputed delivery; new `DELIVERY_DISPUTE_REFUSED` (permanent, the mobile client reverts the delivery like a refused cancel and explains it in en, am and om with one neutral text) and `DELIVERY_DISPUTE_RETRY` (transient); `DELIVERY_DISPUTE_EXPIRED` becomes permanent and reverted; a conflict after a successful freeze, and a dispute for a delivery already disputed, carry the row already `disputed` (the order is finished first; web: the answer's row decides, else a re-read); a client order update to `disputed` is `ORDER_UNAUTHORIZED`; older clients resend a refusal on every reconnect, audited once a day |
| 1.11.0 | 2026-10-09 | Delivery cancel (ESC-08): sent as `update {status, version}` by every party; `delete` still accepted under the same rules (old queued operations are not rewritten); `DELIVERY_CANCEL_NOT_ALLOWED` is permanent, the mobile client reverts the delivery (read-back, else the lowest sent version) and explains it in en, am and om; a cancel of a cancelled delivery answers `conflict` with the row (web: done when a re-read shows `cancelled`) |
| 1.10.0 | 2026-10-09 | Permanent order rejections (ESC-12 part A): every `ORDER_*` operation error except `ORDER_KYC_REQUIRED` is permanent; the mobile client marks it rejected, reverts the local order (create: remove it; update: re-read `GET /orders/{id}`, 404/403 remove it, other failures restore the lowest sent version), shows a localized reason (kept until the UI listens, per user, dropped on logout), rejects the operations queued behind any refused create, and stops rejected operations from blocking pulls; other permanent rejections of order operations are reverted the same way; other entities' permanent rejections are explained and still counted as failed; web treats `conflict` as a failure and reads versions strictly; server rules for `ORDER_*` errors and new order fields; the five non-order codes wait for part B |
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

**Last Updated**: 2026-10-23
**Version**: 1.18.0
