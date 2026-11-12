# Sync Protocol Contract

## Version
1.54.0

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
      "entityType": "crop_listing | trip | delivery | order | buyer_intent | match_event | rating | message | user | location_ping | delivery_photo | listing_report",
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
  later; the server hard-fails (`error`) it if it stays unverifiable past its window, except
  a deferral caused by a read failure (FUP-42), which never hard-fails (the server alarms on
  it instead). This keeps offline-first: the op is never lost. Currently emitted for a
  delivery `pickedUp`/`delivered` transition whose QR handoff secret (farmer-authored nonce)
  has not yet reached the server, and (1.19.0) for one without a nonce whose escrow could
  not be read to decide whether it needs the QR (`delivery-verification-api.md` 1.17.0),
  and (1.31.0) for a `location_ping` whose trip has no delivery on the road yet
  (`LOCATION_NOT_ACTIVE:`, see Location pings).
  `serverData`/`serverVersion` are absent.
- `status: "error"` — operation failed (validation, auth, etc.); `message` explains why
- `serverData` (on `success` and `conflict`) is the entity row with the JSON types GET gives
  the same row (1.37.0, SIM-25): numbers are JSON numbers (`version`, quantities, prices and
  money fields such as `pricePerKg`, `pricePerUnit`, `orderTotal`, `capacity`,
  `pricePerQuintal`, `bookedQuintals`, coordinates including the `latitude` and `longitude`
  inside a delivery's `gpsPickup` and `gpsDropoff`, a rating's `stars`) and a trip's
  `matchedListings` is an array of listing ids. Every other value keeps its type: a string
  that reads as a number (a woreda code, a phone number) stays a string, a stored list such
  as `photoIds` stays a list. A whole number comes without a decimal point, so clients read
  numbers as `num`. Before 1.37.0 these numbers came as strings (`"version":"2"`,
  `"pricePerKg":"18"`) and `matchedListings` as the text `"[]"`.

---

## Conflict Resolution Rules

**Strategy**: Server Wins (implemented in Phase R3)

1. **Version wins, not timestamp.** Compare `clientVersion` against the server's current version. Timestamps are informational only.
2. **Server is authoritative.** On conflict, the server returns its state. The client **automatically accepts** the server version and updates the local Isar database.
3. **Last-write-wins within same version.** If `clientVersion` equals server version, the operation is applied (optimistic concurrency).
4. **Client behind = conflict.** If `clientVersion < serverVersion`, return `conflict` with the server's current data.
5. **Deletes are soft.** Deleted entities are marked with a `deletedAt` timestamp, not physically removed. This prevents sync issues with other clients that haven't synced yet.
6. **The loser of a status race gets a conflict (1.38.0, SIM-28).** A status change that is not a valid transition from the stored status, sent with a version lower than the stored one (another device or party moved the entity first), is answered `conflict` with `serverVersion` and the current row in `serverData`, like any version conflict, instead of the entity's invalid-transition error (`ORDER_VALIDATION: Invalid status transition ...`, `DELIVERY_INVALID_TRANSITION`, `TRIP_INVALID_TRANSITION`, `CROP_INVALID_TRANSITION`). Nothing is written. Only a caller who may ask for that status gets it, and the row is scrubbed as in the other conflicts: an order's party for a status that party may set (a system-managed status or another party's keeps the error, `order-lifecycle.md` 2.6.0), a delivery's trucker for `pickedUp` and `inTransit`, its buyer for `delivered`, any of its parties for `disputed` (anyone else keeps the error), the listing's farmer, the trip's trucker. With the current version, a newer one or none, the invalid-transition error stands. Clients need no change: they already resolve a `conflict` server-wins (mobile) or treat it as a failure (web).
7. **The same for a change that reaches a finished entity (1.45.0, FUP-128).** A delivery update that reaches a delivery already `cancelled` (or soft-deleted before `delivery-verification-api.md` 1.9.0) with a version lower than the stored one is answered `conflict` with the row, by rule 6's caller rule (the trucker for `pickedUp` and `inTransit`, the buyer for `delivered`, any party for `disputed`; for an update without a status the trucker, or the farmer storing `qrNonce`). A trip update or delete that reaches a trip already `cancelled`, `delivered` or deleted has answered that way to its trucker since `trip-listing-api.md` 2.4.0. In every other case the errors stand (`DELIVERY_ALREADY_CANCELLED`, `TRIP_TERMINAL_STATE`, `TRIP_ALREADY_CANCELLED`), and clients no longer resend them (see Changes to a finished delivery or trip).

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
6. **A key belongs to one payload (1.38.0, SIM-28).** The server stores a fingerprint of the operation with its key: the SHA-256 of a canonical JSON of `entityType`, `entityId`, `action`, `clientVersion` and `data` (after the server drops the fields it owns, such as `isFlagged`), with object keys sorted at every depth and numbers compared by value (`18`, `18.0` and `1.8e1` are equal). `operationId` and `timestamp` are left out, so a resend that renews them still matches. The same key sent again with the same payload answers `duplicate` as before. Sent with a different payload it answers `error` with `SYNC_VALIDATION: idempotency key already used for a different operation payload; send this change with a new idempotency key.`: nothing is processed or stored, because answering `duplicate` would drop the change as if it had been applied. Clients already treat `SYNC_VALIDATION` as permanent (mobile marks the operation rejected, and reverts a refused order from `GET /orders/{id}`; web shows the message). Records written before 1.38.0 carry no fingerprint and keep answering `duplicate` until they expire.

### SyncMetadata Table Schema (DynamoDB)

| Field | Type | Description |
|-------|------|-------------|
| `idempotencyKey` | String (PK) | Caller-scoped key (rule 5): the client key as sent when it starts with `{sub}:` (documented format `{userId}:{entityType}:{entityId}:{action}:{timestamp}`), otherwise `{sub}:{clientKey}` |
| `timestamp` | Number (SK) | Unix timestamp (milliseconds) when operation was processed |
| `operationId` | String | Client-generated UUID for the sync operation |
| `result` | String | JSON-encoded sync result (for returning on duplicate requests) |
| `expiresAt` | Number (TTL) | Unix timestamp when this record should be deleted (7 days from creation) |
| `payloadFingerprint` | String | Lowercase hex SHA-256 of the operation's canonical payload (rule 6, 1.38.0); absent on records written before it |

**Performance**: Every stored key starts with the caller's `sub`, so the table naturally partitions across users, preventing hot partition issues.

---

## Error Codes

| Code | Meaning | Client Action |
|------|---------|---------------|
| `SYNC_SUCCESS` | Operation applied | Remove from local queue |
| `SYNC_CONFLICT` | Client version behind | Accept server data, re-queue if needed |
| `SYNC_DUPLICATE` | Idempotency key already processed | Remove from local queue (already done) |
| `SYNC_VALIDATION` | Payload failed validation, or (1.38.0) an idempotency key already used for a different payload | Fix data, re-queue with new idempotency key |
| `SYNC_AUTH` | Authentication failed | Re-authenticate, retry |
| `SYNC_RATE_LIMIT` | Too many requests | Back off, retry with exponential delay |
| `SYNC_SERVER_ERROR` | Server failure | Retry with exponential backoff |
| `ACCOUNT_004` (HTTP 410, whole request) | The caller's account is being or has been deleted (1.26.0) | Stop this sync, no retry counted, the operations stay queued. `cognito_pending` or `completed`: never resend while the app runs, wipe and go to login; `in_progress`: a later sync may resend. See Deleted accounts (1.26.0) |
| `AUTH_025` (HTTP 403, whole request) | The caller's account is suspended (1.48.0) | Stop this sync, no retry counted, the operations stay queued; end the session like a rejected token refresh. See Suspended accounts and reputation gates (1.48.0) |

---

## Invariants

These rules must always hold:

1. **No data loss.** An operation in the client's local queue is never silently dropped. It either succeeds, conflicts (with resolution), errors (with retry), or is rejected for good, in which case the client undoes its local change and tells the user why (1.10.0). One exception: a legacy `cropListing` operation, which the user never made and the server never accepted, is removed (1.20.0, Legacy listing side effect).
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
- `DELIVERY_CODE_MISMATCH`, `DELIVERY_CODE_LOCKED`, `DELIVERY_CODE_LOCKED_DROPOFF` and `DELIVERY_CODE_GPS_REQUIRED` (since 1.33.0) → Mark as permanently rejected and revert the local delivery (see Typed handoff code)
- `CROP_CONTACT_IN_TEXT` and `TRIP_CONTACT_IN_TEXT` (since 1.23.0) → Mark as permanently rejected; since 1.43.0 also revert the local crop listing or trip (see Listing text refusal)
- `DELIVERY_NONCE_MISSING` (since 1.43.0) → Mark as permanently rejected and revert the local delivery (see Refusals no retry can pass)
- `DELIVERY_ALREADY_CANCELLED`, `TRIP_TERMINAL_STATE` and `TRIP_ALREADY_CANCELLED` as an `error` (since 1.45.0) → Mark as permanently rejected and revert the local delivery or trip (see Changes to a finished delivery or trip)
- Every `*_UNAUTHORIZED` code but a role gate (`... Only farmers can ...`, `truckers`, `buyers`), `RATING_FORBIDDEN: You can only rate your own orders.` and a `CROP_VALIDATION`, `TRIP_VALIDATION` or `DELIVERY_VALIDATION` `... is immutable and cannot be changed on update.` (since 1.43.0) → Mark as permanently rejected, not reverted (see Refusals no retry can pass)
- `LOCATION_VALIDATION`, `LOCATION_NOT_TRUCKER`, `LOCATION_SHARING_ENDED`, `LOCATION_RATE_LIMITED` and `LOCATION_STALE` (since 1.31.0) → Mark as permanently rejected, not reverted (see Location pings); `LOCATION_NOT_ACTIVE` comes as `deferred`, not as an error
- A refused `listing_report` (since 1.44.0, always `SYNC_VALIDATION`) → Mark as permanently rejected; nothing local to revert (see Listing reports)

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

## User profile updates (1.28.0)

The `user` entity carries a profile change the client made in Settings. Only
`action: "update"` is accepted, `entityId` MUST equal the authenticated caller (a
user can only change their own profile), and the server allow-lists a single field:

- `preferredLanguage`: one of `en`, `am`, `om`. Every other key in `data` is
  ignored, so role, woreda, phone and the like can never change through sync.

The update is version-less (last-write-wins): the server reads the caller's row,
writes the new language and bumps `version`; an unchanged value is a no-op
`success`. This lets the language chosen on one device follow the user to the
others, and gives order notifications (ESC-21) the right language.

Errors (all `status: "error"`): `USER_PROFILE_VALIDATION` (missing or invalid
`preferredLanguage`, or a non-update action), `USER_PROFILE_UNAUTHORIZED`
(`entityId` is not the caller), `USER_PROFILE_NOT_FOUND`, `USER_PROFILE_CONFLICT`
(a rare concurrent write; the client retries) and `USER_PROFILE_UNAVAILABLE`.

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
| any other token (since 1.49.0) | not known to this client | explain only; no Fayda action, nothing sent again |

A failed, cancelled or refused verification (`FAYDA_005`/`006`/`007`) leaves the payment
unsent. Clients fall back to the wording ("farmer", "phone") for servers older than 1.5.0.

---

## Operation errors added (1.9.0; built in 1.22.0 for the accept refusal, FRD-03, and in 1.25.0 for the risk tier, FRD-04)

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

### Server (1.25.0, FRD-04)

The server sends these since 1.25.0. The tier is the caller's, read at most once per batch; the
counterparty's tier is never checked or revealed. A score that could not be read, or a server
without the fraud-signals table, is LOW (today's checks apply); a score read cut at its page cap
is CRITICAL.

| Operation | Where the check sits |
|-----------|----------------------|
| crop `create` (farmer), trip `create` (trucker) | after the field rules (`CROP_VALIDATION`, `TRIP_VALIDATION`, the contact-in-text rule), before the listing fraud checks and the write |
| order `create` (buyer) | after the field rules (`ORDER_VALIDATION`), before the listing and trip reads and the write |
| order update to `payment_submitted` (buyer) | after the transition rules, before KYC (`ORDER_KYC_REQUIRED`); the tier then sets the step-up (`AUTH_041` before `AUTH_040`) |

A refused operation writes nothing and stores no idempotency key, like every operation error.
The `AUTH_041:` text names no amount: `AUTH_041: Confirm it's you with Fayda to continue with this
payment.` Clients are unchanged (1.18.0 below).

### Client handling (1.18.0, FRD-06)

| Code | Mobile client (sync queue) | Web client (calls `/sync` directly) |
|------|----------------------------|-------------------------------------|
| `FRAUD_ACCOUNT_HELD:` | Permanent, as before. A refused order operation is reverted as in 1.10.0 (a refused create removes the local order, a refused `payment_submitted` puts the server's order back); a refused crop or trip create keeps its local row, like the other `FRAUD_*` codes (until 1.43.0: since then it is removed, with the 1.17.0 revert, FUP-51). For every entity and action the user is told, in en, am and om: "Your account is under review, so this action is paused. Contact support." The batch result's error message picks the code too. | The refused call fails with the same text (crop, trip and order create, payment). |
| `AUTH_041:` | Unchanged: kept pending, the Fayda login opens, the operation goes again after it. The prompt no longer names an amount, since the server may ask at any amount. | Unchanged: the payment screen opens the Fayda login and sends the same payment again, with the same prompt. |

The escrow routes (`/escrow/initiate`, `/escrow/order-checkout`, `/escrow/dispute`) and the
escrow `held` status are covered in `escrow-protocol.md` 1.23.0. Clients older than 1.18.0 show
a generic refusal for `FRAUD_ACCOUNT_HELD` and do not retry it either.

---

## Permanent order rejections (1.10.0, ESC-12 part A)

Every `ORDER_*` operation error except `ORDER_KYC_REQUIRED` is permanent: sending
the same payload again gets the same answer. Today the server sends
`ORDER_VALIDATION:`, `ORDER_UNAUTHORIZED:`, `ORDER_CANCEL_NOT_ALLOWED:` (1.13.0) and
`ORDER_PAYMENT_METHOD_UNAVAILABLE:` (1.16.0), `ORDER_RESPONSE_NOT_PENDING:` and
`ORDER_DEADLINE_PASSED:` (1.29.0, Order commitments), `ORDER_CONTACT_IN_TEXT:` and
`ORDER_DROPOFF_NOTE_LOCKED:` (1.36.0, Drop-off landmark), `ORDER_TRIP_NOT_ELIGIBLE:` (1.34.0, on an
order create naming a trip that has left); the planned `ORDER_ACTION_NOT_ALLOWED` and
`ORDER_TRANSPORT_ATTEMPTS_EXCEEDED` (Transport choice, PLANNED) fall under the same rule. `ORDER_KYC_REQUIRED`
keeps its own revert and Fayda prompt (1.4.0, 1.5.0).

Mobile client (sync queue):

| Case | Client action |
|------|---------------|
| `ORDER_*` error | Mark the operation rejected: it is never sent again and a reconnect does not reset it. A rejected order operation is not counted as failed (it was reverted and explained). |
| Refused order `create` | The server never stored the order: remove the local order. |
| Refused order update | Re-read `GET /orders/{id}` and replace the local order with it (server wins), unless another operation for that order is still queued (its own outcome settles the row). A 404 or 403 on that read removes the local order. Any other failed read (offline, 401, 5xx) puts the local version back to the lowest version the order's refused operations sent, so the next pull overwrites the row. |
| Any other permanent rejection of an order operation (`FRAUD_*`, `SYNC_VALIDATION`, a whole batch refused with HTTP 400 or 422) | Reverted the same way. FRD-06 adds the `FRAUD_ACCOUNT_HELD` wording. |
| Any refused `create` (every entity type, every permanent code) | Also reject the operations queued behind it for that entity: they could only get a transient "not found" and would be resent forever. |
| Permanent rejection of any other entity (listing, trip, delivery, rating, message) | Not reverted, except the cases in the later sections (delivery 1.11.0 and 1.12.0, message 1.15.0, listing and trip 1.17.0). Explained (the listing-form wording for `FRAUD_DUPLICATE_LISTING` and `FRAUD_VELOCITY_EXCEEDED`, otherwise "This change could not be saved on the server") and still counted as failed, because its local change never reached the server. Background match events are neither. |
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
  `CROP_HAS_ACTIVE_ORDERS` and `APP_UPDATE_REQUIRED` are supported by clients since 1.17.0
  (ESC-12 part B, below). A server may send them once that client version is the minimum
  in use (spec `three-party-escrow-commitments-reputation.md` K1: client support first).

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

## Payment method refusal (1.16.0, FUP-20)

With the live gateway (`TELEBIRR_MOCK` off) only the Telebirr checkout funds an order
(`order-lifecycle.md` 1.19.0, Payment gates). An order update to `payment_submitted` whose
`paymentMethod` is present and not `telebirr` is refused with
`ORDER_PAYMENT_METHOD_UNAVAILABLE: Bank transfer (cbe_transfer) is not available yet. Pay with Telebirr.`
(for any other value: `ORDER_PAYMENT_METHOD_UNAVAILABLE: Payment method '<value>' is not available yet. Pay with Telebirr.`).
It is decided after the KYC, step-up and Telebirr total gates and before any write: no
`paymentSubmittedAt`, no transaction-audit row, no idempotency key. Only the update's own
value is checked, never the stored one (the mobile create writes `cbe_transfer` as a default
on every order); an update without a `paymentMethod` is left to the checkout. Mock mode (dev,
staging) still funds every method. Bank transfers return with ESC-46.

| Server answer | Mobile client (sync queue) | Web client |
|---|---|---|
| `ORDER_PAYMENT_METHOD_UNAVAILABLE:` | Permanent under the `ORDER_*` rule above: mark rejected, never resent, revert the local order (re-read `GET /orders/{id}`), show `paymentMethodUnavailable` (en, am, om). | Failure with the same message, shown on the payment screen. |

Live builds never send it (they show the Telebirr rail only, `order-lifecycle.md` 1.13.0); it
comes from a demo build pointed at a live server. Clients older than 1.16.0 revert the order
and show the generic `errorOrderRefused` text (1.10.0).

---

## Permanent trip and crop rejections (1.17.0, ESC-12 part B)

Five codes from spec `three-party-escrow-commitments-reputation.md` section 8 are permanent:
replaying the same payload gets the same answer, so the client never resends them. The one
exception is `APP_UPDATE_REQUIRED` on anything but an order: the build is the problem, not the
payload, so the client keeps that operation for the updated build (below). Since 1.29.0 the server sends
`TRIP_HAS_BOOKINGS` and `TRIP_CAPACITY_EXCEEDED` behind `COMMITMENTS_ENABLED` (ESC-15, Order
commitments below), and since 1.35.0 `CROP_HAS_ACTIVE_ORDERS` behind the same flag (ESC-18);
since 1.41.0 `APP_UPDATE_REQUIRED` on order operations from an app below the minimum build (ESC-25,
Minimum app version for order operations below); ESC-29 adds the others, each with its own server rules. The three `ORDER_*` codes of section 8 that had the generic order text get
their own text too.

| Code | Sent on | What it means to the client |
|------|---------|-----------------------------|
| `REPUTATION_RESTRICTED:` | `crop_listing` create, `trip` create, `order` update (the farmer's or trucker's accept), `order` create (the listing's or trip's owner is restricted, spec A7) | The account (or, on a buyer's create, the owner) may not do this for now. Existing orders continue. |
| `TRIP_HAS_BOOKINGS:` | `trip` update or delete | Orders are booked on the trip, so this change is not allowed. |
| `TRIP_CAPACITY_EXCEEDED:` | `order` create or update naming a trip | The trip has no room left for this order: an order refusal. |
| `CROP_HAS_ACTIVE_ORDERS:` | `crop_listing` delete, or update to `status: deleted` (sent since 1.35.0 behind `COMMITMENTS_ENABLED`, ESC-18, `crop-listing-api.md` 1.7.0 rule 10). A quantity edit never gets it: `quantity` is the free stock, so it cannot reach what orders hold | Orders still depend on the listing: one is `payment_submitted`, `funded`, `awaiting_transport` or `accepted`. |
| `APP_UPDATE_REQUIRED:` | `order` operations from an app below the server's minimum (the minimum-version gate, spec K1, ESC-25; sent since 1.41.0). Clients also handle it defensively on `crop_listing`, `trip`, `delivery`, `rating`, `message`, `buyer_intent` and `match_event` operations: kept pending, never reverted. | This build must be updated. |
| `ORDER_DEADLINE_PASSED:`, `ORDER_TRIP_NOT_ELIGIBLE:`, `ORDER_TRANSPORT_ATTEMPTS_EXCEEDED:` | `order` operations (permanent since 1.10.0; `ORDER_DEADLINE_PASSED` sent since 1.29.0; `ORDER_TRIP_NOT_ELIGIBLE` sent since 1.34.0 on an `order` create naming a trip that has left, `trip-listing-api.md` 2.2.0) | The deadline passed; the named truck cannot take the order; no more trucks may be tried. |

Mobile client (sync queue):

| Case | Client action |
|------|---------------|
| Any of the five codes (`APP_UPDATE_REQUIRED` only on an `order`) | Mark the operation rejected: never sent again, a reconnect does not reset it. |
| Refused `crop_listing` with `REPUTATION_RESTRICTED` or `CROP_HAS_ACTIVE_ORDERS`; refused `trip` with `REPUTATION_RESTRICTED` or `TRIP_HAS_BOOKINGS`; since 1.43.0 (FUP-51) also a `crop_listing` with `CROP_CONTACT_IN_TEXT` and a `trip` with `TRIP_CONTACT_IN_TEXT`, and either with `FRAUD_ACCOUNT_HELD` | Reverted with the order rules. A refused `create` was never stored: remove the local row. A refused update or delete: re-read `GET /crops/{id}` or `GET /trips/{id}` (the owner's own read: only a farmer sends crop operations and only a trucker trip operations) and replace the local row (server wins), unless another operation for it is still queued. A 404 (deleted, or for a trip cancelled) or 403 removes the local row. Any other failed read puts the local version back to the lowest version the refused operations sent and clears the pull stamp, so the next crop or trip pull overwrites the row. |
| Reverted crop listing or trip | Once the revert returned (a failed read included, since the next pull settles the row), its rejected operations are flagged `reverted` and leave the "Sync failed" count: nothing is left for the user to act on. A revert that threw keeps them counted. Since 1.43.0 (FUP-25) the same holds for a reverted delivery and for a message removed because its thread is closed. |
| Any other refusal of a crop listing or trip (`FRAUD_*` other than `FRAUD_ACCOUNT_HELD`, `SYNC_VALIDATION`, `*_UNAUTHORIZED`, a changed immutable field, a code meant for another entity, a whole batch refused) | Unchanged: not reverted, still counted. When one entity has several refused operations in a batch, the first one whose code undoes the change drives the revert; the first refusal is what the user is told. |
| Refused delivery `create` that reaches the delivery revert (any code that undoes a delivery change) | The server never stored it: the local delivery is removed without a read. Every other refused delivery change is still re-read and never removed (1.11.0). |
| Order operation refused with one of the five codes | Reverted like any permanent order rejection (1.10.0), not counted. |
| `APP_UPDATE_REQUIRED` on anything but an `order` (`crop_listing`, `trip`, `delivery`, `rating`, `message`, `buyer_intent`, `match_event`) | Kept pending without burning a retry and never reverted or removed (a create included): the same payload goes through once the app is updated. Not counted as failed. This build does not send it, or any operation queued behind it for the same entity, again while it runs (an in-memory hold, cleared by a restart or an update); every other entity keeps syncing. Explained once per entity, except background match events. |
| `APP_UPDATE_REQUIRED`, refused order or held operation | The app also raises the "update required" flag the shell's banner reads (since 1.53.0 kept with the build number and cleared by an accepted order operation, see Minimum app version for order operations). The client does no version gating of its own. |
| Reason | Each code has its own text in en, am and om (below), shown in the rejection snackbar. On an `order` create, `REPUTATION_RESTRICTED` says the farmer or trucker cannot take orders; on every other operation it says the user's account cannot do this for now. On a refused order create, `REPUTATION_RESTRICTED`, `TRIP_CAPACITY_EXCEEDED`, `ORDER_TRIP_NOT_ELIGIBLE` and `APP_UPDATE_REQUIRED` keep their own text (they already say what to do next); any other code on a refused create still reads "This order could not be placed". |

Texts (keys in `app_en.arb`, `app_am.arb`, `app_om.arb`):

| Code | Key |
|------|-----|
| `REPUTATION_RESTRICTED` | `errorReputationRestricted`; on an order create `errorCounterpartyRestricted` |
| `TRIP_HAS_BOOKINGS` | `errorTripHasBookings` |
| `TRIP_CAPACITY_EXCEEDED` | `errorTripCapacityExceeded` |
| `CROP_HAS_ACTIVE_ORDERS` | `errorCropHasActiveOrders` |
| `APP_UPDATE_REQUIRED` | `errorAppUpdateRequired` (an order, and every web refusal); `errorAppUpdateRequiredKept` (an operation held for the update) |
| `ORDER_DEADLINE_PASSED` | `errorDeadlinePassed` |
| `ORDER_TRIP_NOT_ELIGIBLE` | `errorTripNotEligible` |
| `ORDER_TRANSPORT_ATTEMPTS_EXCEEDED` | `errorTransportAttemptsExceeded` |
| `ORDER_ACTION_NOT_ALLOWED` | `errorOrderRefused` (unchanged) |

Web client (no queue, calls `/sync` directly): every code is a failure with the same text. A
buyer's order create refused `REPUTATION_RESTRICTED` reads `errorCounterpartyRestricted`. Web
reads live data, so nothing is reverted. Since 1.53.0 (FUP-75) an `APP_UPDATE_REQUIRED` refusal
also raises the "update required" flag, as on mobile (before, web raised none).

Rules for server authors:
- Each code must be deterministic for the same payload. A transient failure in the same
  handler (a read that failed) uses no code from this list.
- Send `APP_UPDATE_REQUIRED` on order commands only (spec K1). A client holds any other
  operation answered with it until the app is updated, so sending it there only delays that
  operation.
- Send `REPUTATION_RESTRICTED`, `TRIP_HAS_BOOKINGS` and `CROP_HAS_ACTIVE_ORDERS` only to an app that
  sends `X-App-Build`; an app without it gets the sentence under `SYNC_VALIDATION` or
  `ORDER_VALIDATION` (1.52.0, Codes older apps do not know).
- `CROP_VALIDATION` and `TRIP_VALIDATION` stay retryable: they also carry "The farmer profile
  could not be read" when the users table read fails. Since 1.43.0 the client treats only their
  immutable-field text (`... is immutable and cannot be changed on update.`) as permanent, and
  `CROP_UNAUTHORIZED` and `TRIP_UNAUTHORIZED` as permanent except their role gate (Refusals no
  retry can pass): never send those for a read that failed.

Clients older than 1.17.0 retry the five codes like any other error (up to three tries per sync,
and `retryFailed` puts the operation back on every reconnect) and keep their local change. An
`order` refusal with `TRIP_CAPACITY_EXCEEDED`, `REPUTATION_RESTRICTED` or `APP_UPDATE_REQUIRED`
is not reverted by them either, because those codes have no `ORDER_` prefix.

---

## Legacy listing side effect (1.20.0, FUP-18)

Mobile builds before 1.20.0 queued a second operation next to two delivery operations: a listing
`update` with `entityType: "cropListing"` (status `reserved` when the trucker created a delivery,
`sold` when the buyer confirmed one), and flipped their local copy of the listing the same way. The
server routes `crop_listing`, not `cropListing`. It answers `Unsupported entity type: 'cropListing'.`,
an unprefixed error, so the client sent it three times per sync and `retryFailed` put it back on
every reconnect, without end. Spelled right it would still be refused with `CROP_UNAUTHORIZED`: the
trucker and the buyer do not own the listing. The listing's stock belongs to the server: funding
takes the ordered quantity off and a listing at 0 becomes `reserved` (`order-lifecycle.md`, the
`funded` transition).

| | Mobile client | Web client |
|---|---|---|
| Delivery create (trucker) and confirm (buyer, self-pickup included) | Only the delivery operation is queued. The local listing row is not touched; the next crop pull shows the server's listing. | Unchanged: it never sent a listing operation. |
| A `cropListing` operation an older build left in the queue | Removed at app start, whatever its status, before the sync executor runs: not sent, not counted as failed, not announced. While it was queued it kept its listing row out of the crop pull's stale delete, so a listing the server took off the market is now dropped by the next pull. A listing the server still returns was already overwritten by every pull. | Nothing is queued on web. |

No server change: the server never applied a `cropListing` operation. Older builds keep sending
theirs until they update, and the server answers each the same way.

---

## Listing text refusal (1.23.0, FUP-49)

A `crop_listing` or `trip` create, or an update that changes the text, is refused when text that
buyers read before any order holds a phone number (`crop-listing-api.md` 1.4.0 rule 6b for
`description`, `locationName`; `trip-listing-api.md` 1.5.0 rule 10 for
`vehicleDescription`, `vehiclePlate`, `fromWoreda`, `toWoreda`):

`CROP_CONTACT_IN_TEXT: Remove the phone number from <fields>. Contact opens after payment is held in escrow.`

`TRIP_CONTACT_IN_TEXT: Remove the phone number from <fields>. Contact opens after payment is held in escrow.`

Nothing is written. The answer depends only on the payload (on update, also on the stored value:
an unchanged echo is not checked), so the same operation always gets the same answer.

| Server answer | Mobile client (sync queue) | Web client |
|---|---|---|
| `CROP_CONTACT_IN_TEXT:` or `TRIP_CONTACT_IN_TEXT:` | Permanent: mark the operation rejected (never resent, not reset by a reconnect). Since 1.43.0 (FUP-51) reverted with the 1.17.0 rules: a refused create removes the local row, a refused update puts back the server's row, and the operations leave the "Sync failed" count (before, the row stayed and counted as failed). Only the entity's own code reverts: `TRIP_CONTACT_IN_TEXT` on a crop listing does not. Explained in en, am and om with `errorContactInText` ("Phone numbers are not allowed here. Remove the number: contact opens after payment is held in escrow."). | Failure with the same text; nothing local to undo. |

The crop and trip forms run the same detector before anything is queued (offline-first) and show
the same text under the field, so the refusal comes back only for an operation queued by an older
build, a client that skips the form, or a rule that changed on one side.

`CROP_VALIDATION` and `TRIP_VALIDATION` are unchanged.

Clients from before 1.23.0 do not know the codes: they retry them like any other error and keep
their local change; every retry gets the same refusal.

The crop and trip revert for permanent refusals (1.17.0, ESC-12 part B, FUP-01) reverts only the
codes it lists. Since 1.43.0 (FUP-51) these two codes are on that list.

---

## Drop-off landmark (1.36.0, LT-12)

An `order` create may carry `dropoffNote`, and the order's buyer may send it alone on an `update`
(`{"dropoffNote": "...", "version": n}`, the pre-change version; an empty string clears it) until
pickup (`order-lifecycle.md` 2.5.0). The server refuses, nothing written:

`ORDER_CONTACT_IN_TEXT: Remove the phone number from dropoffNote. Contact opens after payment is held in escrow.`

`ORDER_DROPOFF_NOTE_LOCKED: The drop-off note can only be changed before pickup.`

and `ORDER_UNAUTHORIZED` for a change from anyone but the buyer, and for any `dropoffNote` at all
from a party who may not read it (a trucker not committed: the same text whatever the value, so no
answer tells what the note says), `ORDER_VALIDATION` for more than 80 user-visible characters, a
control character or a U+2028/U+2029 separator. Clients send the note on one line: every control
character and line or paragraph separator becomes a space, runs of spaces collapse, the ends are
trimmed (mobile and web share the helper), so an offline order never meets that refusal. An
unchanged echo of the stored note from a party who reads it is dropped, never refused. A delivery that cannot be read for the edit window answers the unprefixed
`The order's delivery could not be read. Please try again later.` (retryable).

| Server answer | Mobile client (sync queue) | Web client |
|---|---|---|
| `ORDER_CONTACT_IN_TEXT:` | Permanent under the `ORDER_*` rule (1.10.0): a refused create removes the local order and says it was not placed; a refused update puts the order back to the server's view and is explained with `errorContactInText` (the listing text refusal's wording). | Failure with `errorContactInText`. |
| `ORDER_DROPOFF_NOTE_LOCKED:` | Permanent under the `ORDER_*` rule: the order is put back to the server's view, explained with `errorDropoffNoteLocked` ("The goods were picked up, so the drop-off landmark can no longer be changed."). | Failure with the same text. |

The order forms run the same length and phone checks first, and offer the edit only to the buyer
of a trip order before pickup, so the refusals come back only for a race with the pickup or an
operation queued by another build. An order row in `serverData` leaves out `dropoffNote` for a
caller who may not read it (a trucker not committed): clients apply the row as is, so a device
holding the order of such a trucker drops its copy.

Deploy order: a server before 1.36.0 refuses the field (`ORDER_VALIDATION: Unknown fields:
dropoffNote`, permanent), so the server ships before clients that send it.

---

## Drop-off place (1.42.0, LT-14)

An `order` create may carry `deliveryLatitude` and `deliveryLongitude` (JSON numbers, both or
neither, inside Ethiopia's box), and the order's buyer may send them alone on an `update`
(`{"deliveryLatitude": 8.5872, "deliveryLongitude": 39.1213, "version": n}`, the pre-change version)
until pickup (`order-lifecycle.md` 2.9.0). The server refuses, nothing written:

`ORDER_DELIVERY_LOCATION_LOCKED: The drop-off place can only be changed before pickup.`

and `ORDER_UNAUTHORIZED: Only the buyer can change the drop-off place.` for a change from anyone but
the buyer and for either field at all from a party who may not read the point (the trucker of an
order whose money is not held, or one who declined, expired, dropped out or failed on capacity; an
asked trucker reads it), `ORDER_VALIDATION` for half a point, a value that is not a number or one outside the
box. An unchanged echo from a party who reads it is dropped. The clients send only the centroid of a
place picked from the bundled list, checked against the box first, so an offline order never meets
the validation refusal.

| Server answer | Mobile client (sync queue) | Web client |
|---|---|---|
| `ORDER_DELIVERY_LOCATION_LOCKED:` | Permanent under the `ORDER_*` rule: the order is put back to the server's view, explained with `errorDeliveryLocationLocked` ("The goods were picked up, so the drop-off place can no longer be changed."). | Failure with the same text. |

On an order without a trip (self-pickup) the fields are ignored: dropped, never refused, never
stored. An order row in `serverData` leaves the point out for a caller who may not read it, by its
own rule (the buyer, the farmer, the order's trucker while the money is held and the trucker is
still on the order), and the note by the note's.
Deploy order: a server before 1.42.0 refuses the fields (`ORDER_VALIDATION: Unknown fields`,
permanent), so the server ships before clients that send them.

---

## Payment total refusal (1.24.0, FUP-22)

With the live gateway (`TELEBIRR_MOCK` off) one Telebirr payment takes 100 to 500,000 ETB
(`order-lifecycle.md` 1.24.0, Payment gates). An order update to `payment_submitted` whose
`orderTotal`, rounded to the cent, is outside that range is refused with
`ORDER_PAYMENT_TOTAL_OUT_OF_RANGE: This order total (X ETB) is outside what one Telebirr payment can take (100 to 500000 ETB).`
Before 1.24.0 the same refusal was `ORDER_VALIDATION:` with the same sentence, and clients had
to read the English text. The gate, its bounds and its place among the payment gates (after KYC
and step-up, before any write) are unchanged. Mock mode (dev, staging) skips it as before.

| Server answer | Mobile client (sync queue) | Web client |
|---|---|---|
| `ORDER_PAYMENT_TOTAL_OUT_OF_RANGE:` | Permanent under the `ORDER_*` rule above: mark rejected, never resent, revert the local order (re-read `GET /orders/{id}`), show `paymentTotalOutOfRange` (en, am, om). | Failure with the same message, shown inline as `paymentTotalOutOfRange`. |
| `ORDER_VALIDATION:` with the sentence above (servers before 1.24.0) | Same as the row above; matched for one more release. | Same. |

Live builds check the total before they send it (`order-lifecycle.md` 1.13.0), so the refusal
comes back only after a total changed since that check or from a demo build pointed at a live
server. Clients older than 1.24.0 do not know the code: they revert the order and show the
generic `errorOrderRefused` text (1.10.0).

---

## Deleted accounts (1.26.0, ACC-02)

Account deletion is `account-deletion.md` 1.2.0. It is online only and never goes through
`/sync`. What changes here:

| Case | Server answer | Client |
|---|---|---|
| The caller's users row carries `deletionState` (`in_progress`, `cognito_pending` or `completed`), read strongly consistent | The whole request answers HTTP `410` with `{"error":{"code":"ACCOUNT_004","message":"This account has been deleted.","deletionState":"<state>"}}`. No operation is applied, no idempotency key is recorded | Not retried within that sync and no retry counted; the operations stay queued and the sync stops (`account-deletion.md` 1.3.0, ACC-03). `cognito_pending` or `completed`: that user's batches are not sent again while the app runs, and the device wipes its local data and goes to login. `in_progress`: nothing is wiped and a later sync may send the batch again, because the deletion can still abort at step 9 and the user then keeps the account; Profile > Delete account resumes the deletion (a "Finish deleting" prompt is a follow-up). Web gets the error from its API client and applies the same rule |
| The caller's row cannot be read | The whole request answers HTTP `503` `SYNC_SERVER_ERROR`; nothing applied | Retry with backoff, as for any server error |
| A message create whose recipient's row carries `deletionState` | Op-level `error`: `SYNC_VALIDATION: The recipient has deleted their account.` | Permanent under the `SYNC_VALIDATION` rule |
| A rating create whose ratee's row carries `deletionState` | Op-level `error`: `SYNC_VALIDATION: The rated user has deleted their account.` | Permanent under the `SYNC_VALIDATION` rule |
| An order `payment_submitted` whose buyer, farmer or trucker row carries `deletionState` (`order-lifecycle.md` 1.26.0) | Op-level `error`: `ORDER_VALIDATION: A party to this order has deleted their account.`, nothing written | Permanent under the `ORDER_*` rule: the order is reverted |

A deleted user's crop listings and trips come down to other devices as tombstones (`deletedAt`
set, copied names `Deleted user`), so browse drops them as it drops any deleted listing.

---

## Typed handoff code (1.33.0, usability audit T-1)

When the camera or the screen fails, a pickup (`pickedUp`) or a delivery confirm
(`delivered`) may carry `scannedQrCode`, the nine-digit code of the QR's nonce, instead of
`scannedQrNonce` (`delivery-verification-api.md` 1.22.0, QR Secret Storage). The apps check the
Damm digit before anything is queued, so a typo never reaches the server, and they require a
GPS fix for a typed code. The code is checked only by the server: the mobile app says "Pickup
saved. The code is checked when your phone is online." instead of "verified".

| Server answer | Mobile client (sync queue) | Web client |
|---|---|---|
| `success` | Synced. | Done. |
| `DELIVERY_CODE_MISMATCH:` (not this delivery's code), `DELIVERY_CODE_LOCKED:` (five wrong codes for this leg; scan the QR, or the farmer regenerates), `DELIVERY_CODE_LOCKED_DROPOFF:` (the same at a trucker's drop-off, whose code cannot be renewed: scan the trucker's drop-off QR, or contact support; checked before `DELIVERY_CODE_LOCKED`, which it starts with) and `DELIVERY_CODE_GPS_REQUIRED:` (a paid delivery's typed code without the leg's GPS) | Permanent: mark the operation rejected (never resent, so the queue never spends more of the five tries) and revert the local delivery as for `DELIVERY_CANCEL_NOT_ALLOWED` (re-read `GET /deliveries/{id}`, server wins; else the lowest sent version). Explained in en, am and om, one text per code. | Shows the same message. |
| `deferred` (`DELIVERY_NONCE_PENDING`) | Re-queued without a retry, as for a scan. | Shows its QR-pending text. |
| `DELIVERY_VALIDATION: scannedQrCode ...` (not nine digits with a valid check digit) | Unchanged `DELIVERY_*` handling (the apps never send one). | Failure. |

A miss is counted once per operation id and code, so a resent operation cannot lock the code by
itself, and a new code under a reused operation id still counts.

---

## Fraud signals from refusals (1.27.0, FRD-08)

Some operation refusals now also write a fraud signal on the caller (the authenticated user of the
batch, never the user the operation names). The response does not change: same status, same
message, same `serverData`, whether the signal write works or fails, and a failed write is only
logged. Signals feed the caller's risk score (`specs/architecture/fraud-detection-architecture.md`
3.9.0, sections 14 to 17), so they can later bring step-ups or `FRAUD_ACCOUNT_HELD`.

| Refusal | Signal | Weight |
|---------|--------|--------|
| A crop, trip, order or delivery `create` that conflicts with a row the caller does not own and is no party to (the 1.4.0 conflict with no `serverData`) | `IDOR_CREATE_CONFLICT` | 40 |
| `CROP_UNAUTHORIZED`, `TRIP_UNAUTHORIZED`, `ORDER_UNAUTHORIZED`, `DELIVERY_UNAUTHORIZED`, `INTENT_UNAUTHORIZED`, `RATING_UNAUTHORIZED`, `MESSAGE_UNAUTHORIZED` and `RATING_FORBIDDEN: You can only rate your own orders.`; a role gate met by a token without `custom:role` writes nothing | `ROLE_FORBIDDEN` | 25 |
| `CROP_VALIDATION`, `TRIP_VALIDATION`, `DELIVERY_VALIDATION` or `ORDER_VALIDATION` for a changed identity or money field (`... is immutable and cannot be changed on update.`), and `ORDER_VALIDATION: orderId does not match the operation entityId.` | `IMMUTABLE_FIELD_CHANGE` | 25 |
| `DELIVERY_NONCE_MISMATCH`, `DELIVERY_NONCE_REPLAY`, `DELIVERY_NONCE_MISSING: QR nonce verification required for this transition.`, and since 1.33.0 `DELIVERY_CODE_MISMATCH`, `DELIVERY_CODE_LOCKED` (except the one for a delivery without the secret store) and `DELIVERY_CODE_LOCKED_DROPOFF` | `QR_HANDOFF_FAILURE` | 5 |
| The 10th `conflict` on an `update` or `delete` in one batch | `STALE_VERSION_BURST` | 0 |

The caller holds at most one row per signal type per hour, and the score counts these types once
per refused entity (except `IDOR_CREATE_CONFLICT`, which no honest retry repeats), so an operation
the queue keeps retrying after every reconnect counts once; `ROLE_FORBIDDEN` and
`IMMUTABLE_FIELD_CHANGE` add at most 50 each, and an admin approve clears the operation.
What the shipped clients do in normal use scores nothing or little: a version conflict after
offline edits writes nothing below 10 in one batch and weighs 0 above; the owner's own create retry
writes nothing; an unchanged echo of an identity field writes nothing; a QR failure weighs 5 once
per delivery. The rest are requests the shipped apps never send. Clients need no change.

---

## Order commitments (1.29.0, ESC-15)

Server slice of the sequential commitments (`order-lifecycle.md` 2.0.0, Commitments), behind
`COMMITMENTS_ENABLED` (off everywhere until the clients ship; off, the server behaves as before).
Two commands travel as an order `update` (`entityType: order`, `action: update`), each with the
order's pre-increment `version`:

| Command | `data` | Who | Effect |
|---|---|---|---|
| farmer response | `{"farmerResponse": "accept" \| "decline", "declineReason"?: code, "version": n}` | the order's farmer, on a `funded` order | accept: `accepted` (self-pickup), or the order stays `funded` and the trucker is asked (trip order); decline: `rejected`, refund |
| trucker response | `{"truckerResponse": "accept" \| "decline", "declineReason"?: code, "version": n}` | the order's trucker, on a `funded` order whose farmer accepted | accept: `accepted`, booking confirmed; decline: `awaiting_transport`; from `accepted` before pickup a `decline` is the drop-out (fresh sign-in) |

`farmerResponse`, `truckerResponse` and `declineReason` are the only new update keys a client may
send (`ORDER_VALIDATION: Unknown fields: ...` otherwise, as before). `declineReason` is one of
`date`, `capacity`, `price`, `route`, `crop_not_ready`, `other`. The farmer's legacy
`status: accepted | rejected` is mapped to the farmer command, so shipped farmer apps keep working.
The new status `awaiting_transport` and every new order field (`fulfillment`, `pickupDate`, the
response, deadline and reminder stamps, `transportFailure`, `cancelledBy`, `faultParty`) are
server-written: a client value is dropped from create and update payloads, and `GET /orders`
returns them.

| Server answer | Mobile client (sync queue) | Web client |
|---|---|---|
| `success` | Synced; the pulled order says what happened (an accepted trip order stays `funded` with `truckerResponse: pending`). A repeated accept is a `success` with no write. | Done; re-read the order. |
| `ORDER_RESPONSE_NOT_PENDING:` (the party's answer is not pending: the farmer has not accepted yet, the request is closed, or the order was already answered) | Permanent under the `ORDER_*` rule (1.10.0): revert the local order and explain. | Failure with the same message. |
| `ORDER_DEADLINE_PASSED:` (the farmer's or the trucker's deadline passed; the cron is ending the order) | Permanent: revert and explain (`errorDeadlinePassed`, shipped since 1.17.0). Like `ORDER_CANCEL_NOT_ALLOWED` it depends on the server clock, and the client never resends it. | Failure. |
| `ORDER_CANCEL_NOT_ALLOWED:` on a buyer's `funded -> cancelled` ("The farmer and the trucker are deciding; you will be told as soon as they answer.") or on a committed cancel after pickup | Permanent (1.13.0): revert the local cancel. | Failure. |
| `AUTH_040:` on a trucker `decline` from `accepted`, or a farmer `cancelled` from `accepted` (the drop-out needs a fresh sign-in) | Ask for the password and resend, as on a payment (`order-lifecycle.md` 1.5.0); not a permanent rejection. | Re-authenticate and retry. |
| `TRIP_CAPACITY_EXCEEDED:` on an order create above the trip's `remainingQuintals`; `TRIP_HAS_BOOKINGS:` on a trip update or delete the bookings forbid | Permanent since 1.17.0 (ESC-12 part B): the order create is reverted, the trip change reverted with the trip rules. | Failure. |
| `ORDER_VALIDATION: This trip is no longer available; place a new order.` on `payment_submitted` | Permanent: revert the local order (the trip left `open` / `matched`, lost the room, or departs in under 12 hours). | Failure. |
| unprefixed error (a trip or escrow that could not be read) | Retried. | Failure (generic). |

Both codes are new strings on an existing rule: the `ORDER_` prefix made them permanent on every
client since 1.10.0, so no client change is needed for them. The ESC-15 client PRs add the commands,
the `awaiting_transport` status and the texts. Clients older than those PRs never send a trucker
command and send the farmer's answer as the legacy status.

**Clients (1.30.0, app build 3).** The farmer's accept and reject are sent as
`{"farmerResponse": "accept" | "decline", "respondedAt", "rejectionReason"?, "version"}` on every
server: with `COMMITMENTS_ENABLED` off the server translates the command into the old status write.
The trucker's command is sent only on an order whose `fulfillment` was stamped (the server runs the
commitments); a server without the flag answers it `ORDER_VALIDATION: Transport requests are not
enabled on this server yet.` `ORDER_RESPONSE_NOT_PENDING:` now has its own text
(`errorResponseNotPending`, en, am and om) on mobile and web; it stays permanent and reverts the
local order like any `ORDER_*` refusal. On mobile an `AUTH_040` on the drop-out or the farmer's
committed cancel waits in the queue for the password prompt, as for a payment.

---

## Location pings (1.31.0, LT-05)

The `location_ping` entity carries one check-in (`fix`) or sharing event (`paused`, `resumed`,
`declined`) of the trucker's phone after pickup. The fields, bounds, storage and the read side are
in `live-trip-api.md` 1.0.0, section 1. What matters to the sync queue:

- `action: "create"` only. `entityId` is `{tripId}#{capturedAt in epoch ms}` and must match `data`.
  Fields that belong to another kind (a `source` on a `paused` event) are ignored and not stored.
- Accepted only from the trip's trucker while a delivery on the trip is `pickedUp` or `inTransit`,
  or left those statuses after `capturedAt` (`delivered` and `disputed` at `confirmedAt`, an admin
  cancel after pickup at `cancelledAt`), so a ping that waited offline is kept.
- A resend of the same `entityId` and `kind` is an idempotent `success` with
  `message: "Location already stored."`; nothing is written twice. A repeated idempotency key
  answers `duplicate`, as for every entity.

| Server answer | Meaning | Mobile client (sync queue) |
|---|---|---|
| `success` | Stored, or an idempotent resend. | Synced. |
| `deferred` with `LOCATION_NOT_ACTIVE:` | No delivery on the trip is on the road yet: the first pickup may still wait in a queue (for example held back until its QR handoff secret syncs), or another delivery of the trip is still `pending`. Also sent instead of `LOCATION_SHARING_ENDED` when an earlier operation of the same batch wrote a delivery, because the server's deliveries-by-trip read may not show that write yet. | Re-queue without using a retry (the `deferred` rule above). Once the ping is older than 7 days it answers `LOCATION_STALE`. |
| `LOCATION_VALIDATION:` | A missing or out-of-range field, an unknown `kind`, `source` or field, an `entityId` that does not match `tripId` and `capturedAt`, or an `update` or `delete`. | Permanent: mark rejected, never resent. |
| `LOCATION_NOT_TRUCKER:` | The caller is not the trip's trucker, or the trip does not exist. | Permanent. |
| `LOCATION_SHARING_ENDED:` | Every delivery on the trip that was picked up left the road before `capturedAt`, and none is still `pending`. | Permanent; the phone also stops sharing for the trip. |
| `LOCATION_RATE_LIMITED:` | The trip already has 20 `fix` pings in the clock hour (UTC) of `capturedAt`. Events do not count. | Permanent. |
| `LOCATION_STALE:` | `capturedAt` is more than 2 minutes in the future or more than 7 days old by the server clock. | Permanent. |
| unprefixed error (a read failed, or the server has no trip-locations table) | Nothing was written. | Retried. |

A refused or deferred ping writes nothing and stores no idempotency key. Refused pings are not
reverted and not announced to the user: there is no local row to put back, and a lost check-in
only leaves a gap in the trail. The server sends these answers since 1.31.0; the mobile and web
handling ships with LT-08, and no client sends the entity before it.

**Client handling as built (1.32.0, LT-08).** No server change.

- Mobile queues each ping as a `location_ping` create with the idempotency key
  `{userId}:location_ping:{entityId}:create:{capturedAt in epoch ms}` and keeps its own copy in
  Isar (`LocationPing`, platform pair `core/models/web/location_ping.dart`).
- A batch takes every other pending operation first (FIFO) and fills the rest with pings (FIFO
  among them): a trip's pickup always goes before its pings, and pings that wait as `deferred`
  never hold back other writes.
- Once a trip's ping is answered `deferred`, each sync sends only one ping of that trip, the
  oldest, as a probe; the others wait until a probe is stored or refused with a `LOCATION_*`
  code, then go in the same sync. A transient error keeps probing. The probe state is in memory,
  so the first sync after a restart sends the waiting pings once.
- A `LOCATION_*` error is rejected for good, not reverted, not announced and not counted in the
  "Sync failed" badge. `LOCATION_SHARING_ENDED` also stops sharing for the ping's trip, once per
  trip per batch. `deferred` is re-queued without a retry; an unprefixed error is retried.
- Coalescing: when more than 24 pings wait unsent (`pending`), at most one `fix` per trip and
  clock hour (UTC) of `capturedAt` stays (by source: `start`, `scan`, `arrived`, `manual`,
  `near_stop`, `hourly`; the latest on a tie) plus every event. A ping on its way is never
  coalesced.
- A delivery update to `pickedUp` rejected for good (any permanent code, or a refused batch)
  drops the waiting pings of its trip, unless another delivery of the trip is on the road on the
  phone; they could only wait as `deferred` for 7 days. Pings captured before another delivery
  of the trip left the road (its `confirmedAt`, or its last update when cancelled after pickup)
  stay, and nothing is dropped when the phone cannot read the trip's deliveries.
- Web has no queue: each ping goes to `/sync` at once, together with any that wait, with the
  same key, codes and coalescing; what waits is kept in memory while the page is open.

---

## Handoff photos (1.39.0, FRD-12)

The `delivery_photo` entity attaches a handoff photo, already uploaded through the presigned `PUT`
of `POST /deliveries/{id}/photos`, to its delivery. The rules, the checks and the order they run in
are in `delivery-verification-api.md` 1.25.0, section 4b. What matters to the sync queue:

- `action: "create"` only. `entityId` is `{deliveryId}#{pickup|dropoff}` and `data` is
  `{"photoRef": key}`, nothing else. The id names the photo, not the delivery, so a photo waiting
  in the queue holds back neither the delivery's operations nor its pull.
- Only the photo's taker may send it: the delivery's trucker for `pickup`, its buyer for `dropoff`.
- The write sets the delivery's `pickupPhotoRef` or `dropoffPhotoRef` and nothing else: the
  delivery's `version` and `updatedAt` stay, so no queued handoff of another phone conflicts with
  it. A `success` carries the delivery's `serverVersion` and no `serverData`.
- Since 1.39.0 a `pickupPhotoRef` or `dropoffPhotoRef` in a `delivery` create or update is still
  shape-checked (`DELIVERY_INVALID_FIELD` as before) but a valid key is dropped, not written.
  Since 1.43.0 (FUP-08) the mobile queue leaves either field out of a `delivery` operation unless
  it holds this delivery's upload key, so an operation an old build queued with a photo file path
  goes through instead of being refused on every sync.

| Server answer | Meaning | Mobile client (sync queue) |
|---|---|---|
| `success` | Attached, or the same key was attached already. | Synced. |
| `deferred` with `DELIVERY_PHOTO_PENDING:` | The delivery or the photo's storage could not be read, or the server has no uploads bucket. | Re-queue without using a retry. |
| `DELIVERY_PHOTO_INVALID:` | Not a create, an entity id without a known type, a field other than `photoRef`, a key that is not this delivery's key of this type, or an object whose stored type is not its key's. | Permanent. |
| `DELIVERY_PHOTO_FORBIDDEN:` | The caller does not take this photo (the farmer, the other party, an outsider; anyone for a self-pickup's pickup). An outsider also writes `ROLE_FORBIDDEN` (1.27.0). | Permanent. |
| `DELIVERY_PHOTO_REFUSED:` | The delivery was cancelled. | Permanent. |
| `DELIVERY_PHOTO_EXISTS:` | The handoff already has another photo; the first stays. | Permanent. |
| `DELIVERY_PHOTO_MISSING:` | No object at the key: the photo never went up. | Permanent. |
| `DELIVERY_PHOTO_TOO_LARGE:` | The object is empty or over 200 KiB. | Permanent. |
| unprefixed `Delivery not found.` | The delivery's own create may still be on its way. | Retried. |

A refused photo writes nothing. Like a refused check-in, it is not reverted, not announced and not
counted in the "Sync failed" badge: the handoff it belongs to stands without its photo, and there
is nothing for the user to do (an exception to invariant 1's "tells the user why").

**Client handling (1.39.0).**

- Mobile queues the photo right behind its handoff (the pickup or delivery confirm) with the
  file's path in `data._localPath` (a `_` key: never sent). When a batch is built, a preflight
  uploads the file and stores `{"photoRef": key, "_localPath": path}` in the batch and in the
  queued operation, so a later run sends the key without uploading again.
- Every wait is bounded. Offline, a `PUT` that did not reach S3 or got a `5xx`, or a presign
  answered `401` or `5xx` holds only the photo until the next run, without using a retry. A
  presign answered `404` (the delivery is unknown) holds it while an operation of the delivery
  itself (its create) still waits on the phone, and for 10 minutes after the photo was queued;
  after that the photo is given up. A presign answered `400`, `403` or `409` (over the cap, not
  this user's photo, the handoff has its photo, or since this version a cancelled delivery,
  `409 DELIVERY_PHOTO_REFUSED`), a file that is gone, and the third `PUT` refused with a `4xx`
  also give it up. A photo given up is sent without a key, so the server refuses it for good
  (`DELIVERY_PHOTO_INVALID`) and the queue settles it.
- A `delivery` operation rejected for good rejects the photos waiting behind it: a create both,
  a `pickedUp` the pickup photo, a `delivered` the drop-off photo (they could only wait for a
  delivery or a handoff the server never took).
- After a `success` (or `duplicate`) the phone's photo files of that handoff are deleted and the
  delivery row takes the server's key. Until then a delivery pull or a delivery conflict keeps
  the phone's own photo path while the server has no photo of that type.
- Web has no queue: the browser uploads the photo when it is taken and sends the
  `delivery_photo` operation on its own `/sync` call after the handoff succeeded; any refusal is
  logged and the handoff stands.

---

## Refusals no retry can pass (1.43.0, FUP-110, FUP-138)

Client change only; the server sends these as before. The FRD-08 refusals (1.27.0) that the same
payload always gets, and that the shipped apps never cause, are no longer resent: before, the queue
tried each three times per sync and `retryFailed` put it back on every reconnect.

| Server answer | Mobile client (sync queue) | Web client |
|---|---|---|
| Any `*_UNAUTHORIZED` code (`CROP_`, `TRIP_`, `DELIVERY_`, `INTENT_`, `RATING_`, `MESSAGE_`, `MATCH_EVENT_`, `USER_PROFILE_`; `ORDER_UNAUTHORIZED` was already permanent under the `ORDER_*` rule) except a role gate | Permanent: rejected, never resent, not reverted, still counted as failed, explained with `errorChangeNotSaved` (a match event is not announced). These compare the caller's id with the row or the payload: no read that can fail decides them. | Unchanged: a failure. |
| A role gate: `CROP_UNAUTHORIZED: Only farmers can create crop listings.`, `TRIP_UNAUTHORIZED: Only truckers can create trip listings.`, `INTENT_UNAUTHORIZED: Only buyers can create purchase intents.`, `RATING_UNAUTHORIZED: Only buyers can rate counterparties.`, `MATCH_EVENT_UNAUTHORIZED: Only buyers can log match events.` | Retried as before: a token without the `custom:role` claim (an access token sent instead of the ID token, a client set-up problem the server does not score, 1.27.0) gets the same answer, and the same operation passes once the app sends the right token. | Unchanged. |
| `RATING_FORBIDDEN: You can only rate your own orders.` | Same. `RATING_FORBIDDEN: You can only rate a delivered order ...` stays retryable: the order may still be delivered. | Unchanged. |
| `CROP_VALIDATION`, `TRIP_VALIDATION` or `DELIVERY_VALIDATION` with `... is immutable and cannot be changed on update.` | Same. Any other text under these codes stays retryable. | Unchanged. |
| `DELIVERY_NONCE_MISSING:` (all three texts: a handoff without a scanned nonce or typed code while the server holds the farmer's nonce, and a pickup or paid drop-off whose farmer's nonce never reached the server within 14 days) | Permanent and reverted like a refused typed code (1.33.0): the delivery goes back to `GET /deliveries/{id}`, the photo waiting behind the handoff is rejected, a refused pickup drops its trip's waiting check-ins, and the operations leave the "Sync failed" count. Explained with `errorChangeNotSaved`. `DELIVERY_NONCE_MISMATCH`, `_REPLAY`, `_EXPIRED` stay retryable (a code regenerated offline passes once it syncs), and `DELIVERY_NONCE_PENDING` stays `deferred`. | Unchanged: a failure with the generic delivery text. |

Reverted deliveries and messages (FUP-25): once the revert handler returned, a delivery refused for
good (cancel after pickup, refused or late dispute, typed code, missing nonce) and a message removed
because its thread is closed are flagged `reverted` and leave the "Sync failed" count, like a
reverted crop listing or trip (1.17.0). A revert that threw keeps them counted.

Clients before 1.43.0 retry these codes like any other error.

---

## Changes to a finished delivery or trip (1.45.0, FUP-128)

A cancelled delivery and a trip that is `cancelled`, `delivered` or deleted never change again.
A change that reaches one comes from a client that did not know yet: the other party, the cron or
an admin's verdict ended it while the phone was offline. Rule 6 (SIM-28) covered the changes the
transition table refuses; the finished states are refused before that table is read.

Server (the delivery part is new in 1.45.0; the trip part has been built since
`trip-listing-api.md` 2.4.0, ESC-17, and is unchanged):

| Operation | Sent version | Caller | Answer |
|---|---|---|---|
| Delivery `update` on a delivery that is `cancelled` or carries a legacy `deletedAt` | lower than the stored one | may make the change: the trucker for `pickedUp` and `inTransit`, the buyer for `delivered`, any party for `disputed`; without a `status`, the trucker, or the farmer when the update stores `qrNonce` | `conflict`, `serverVersion`, the scrubbed row in `serverData` (no phones, no `qrNonce`, no photo version ids), message `DELIVERY_ALREADY_CANCELLED: The delivery is already cancelled.` Nothing is written. |
| The same | lower than the stored one | anyone else (an outsider, or a party asking for another party's status) | `error` `DELIVERY_ALREADY_CANCELLED: Cannot update a cancelled delivery.` (or `... a deleted delivery.`), no row |
| The same | the stored version, a newer one or none | anyone | The same `error` |
| Delivery cancel (`update` to `cancelled` or the legacy `delete`) of a cancelled delivery | any | a party | `conflict` with the row, as since 1.11.0 |
| Trip `update` or `delete` on a trip that is `cancelled`, `delivered` or deleted | lower than the stored one, or asking for the status it already has | the trip's trucker (ownership is checked first) | `conflict` with the row (2.4.0) |
| The same | the stored version or none, asking for something else | the trip's trucker | `error` `TRIP_TERMINAL_STATE` or `TRIP_ALREADY_CANCELLED` |

A pickup that reaches a delivery cancelled by a transport-failure cancel still logs the alarmed
`ORDER_TRANSPORT_CANCEL_PICKUP_REFUSED` token before it is answered, conflict or error.

| Server answer | Mobile client (sync queue) | Web client |
|---|---|---|
| `conflict` with the cancelled delivery or the finished trip | Server wins, as for any conflict: the local row takes the server's status and version, the operation is settled. The resolver reads a row without its optional fields (a crop without `description` or with the flat `latitude` and `longitude`, a trip without `truckerName` or `truckerPhone`, a self-pickup without a trucker) and maps the server's crop status `available` to the app's `active`. | A failure, as for any conflict (the web reads the version right before it writes, so it is rarely behind); the next read shows the cancelled delivery. |
| `error` `DELIVERY_ALREADY_CANCELLED:` | Permanent and reverted like a refused delivery cancel (1.11.0): rejected, never resent, the delivery goes back to `GET /deliveries/{id}`, the operation leaves the "Sync failed" count, explained with `errorChangeNotSaved`. This is what the second of two queued changes gets (a pickup, then in transit): the first is behind and gets the conflict, the second was queued one version higher, which is the cancelled row's version. | Unchanged: a failure with the message. |
| `error` `TRIP_TERMINAL_STATE:` or `TRIP_ALREADY_CANCELLED:` | Permanent and reverted with the trip rules (1.17.0): the trip goes back to `GET /trips/{id}` (a `404`, which a cancelled trip answers, removes it, as the trip pull does), the operation leaves the "Sync failed" count, explained with `errorChangeNotSaved`. | Unchanged. |

All three roles: the trucker's pickup, in-transit mark and trip changes, the buyer's confirmation
(a self-pickup included) and any party's dispute. Clients before 1.45.0 resolve the new conflict
as any other and keep retrying the errors as before, so the server ships first or together.

---

## Minimum app version for order operations (1.41.0, ESC-25)

Spec `three-party-escrow-commitments-reputation.md` K1: client support first, then the server, and
`/sync` refuses order commands from apps below the minimum. The clients have handled
`APP_UPDATE_REQUIRED` since 1.17.0, and every app from build 2 on sends `X-App-Build` (FUP-61,
`escrow-protocol.md` 1.31.0). `COMMITMENTS_ENABLED` stays off in staging and prod until the owner
turns it on there, after this gate is deployed (an infra test requires the gate at build 3 or more
in every environment with the flag on).

**The build.** `X-App-Build: <n>` on the `/sync` request, `n` being the `+N` of `version:` in
`mobile/pubspec.yaml` (mobile and web send the same number). Read case-insensitively with
`ClientAppBuild`, once per batch; only a positive whole number counts. A missing, empty or unreadable
header is an old app (FUP-61 decision 3, waiting for confirmation in FUP-91).

**The minimum.** `MIN_ORDER_APP_BUILD` on the sync Lambda, set per environment in the infra config
(`minOrderAppBuild` in `infra/lib/config/environments.ts`). Only a positive whole number turns the
gate on; 0, absent or unreadable turns it off. The value is 3 in dev, staging and prod: build 3 is the
first app that renders `awaiting_transport` and sends the farmer and trucker commands (#237). It does
not depend on `COMMITMENTS_ENABLED`, so it is live before the flag. Only the sync Lambda carries it:
no other Lambda takes order commands.

**What it covers.** Every `order` operation, `create`, `update` and `delete` alike: the commitment
commands (`farmerResponse`, `truckerResponse`, the planned `transportChoice`), the legacy status
changes (payment, cancel, accept, reject), the drop-off note. An app below the minimum cannot show
the commitment statuses, so it may not act on orders at all (spec K1). Not covered: every other
entity (`crop_listing`, `trip`, `delivery`, `rating`, `message`, `buyer_intent`, `match_event`,
`location_ping`, `delivery_photo`, `user`), which the clients would only hold (1.17.0), and the
`/escrow/*` routes, which map the statuses an old app cannot parse instead (`escrow-protocol.md`
1.31.0).

**When.** Per order operation, right after the shape check (the required fields and the action) and
before any read, other field check or write, so the answer is the same for every payload from the
same build. Nothing is written, no idempotency key is read or stored, no fraud signal is written
(1.27.0), and the rest of the batch goes on. A retry of an order operation the server applied before
the gate went live gets the refusal too; the client's next order pull brings the order back.

**The answer.**

| Caller | Operation result |
|---|---|
| sends `X-App-Build` (build 2 or later, so it has the 1.17.0 client) below the minimum | `{"operationId": "...", "status": "error", "message": "APP_UPDATE_REQUIRED: This version of the app is too old for orders. Update WeredaConnect to continue."}` |
| sends no readable `X-App-Build` (build 1 or older; such an app may predate the 1.17.0 client, which retries an unknown code forever, FUP-75) | `{"operationId": "...", "status": "error", "message": "ORDER_VALIDATION: This version of the app is too old for orders. Update WeredaConnect to continue."}`, permanent on every client since 1.10.0 |
| at or above the minimum, or the gate off | unchanged |

Clients (1.17.0, no change): mobile marks the order operation rejected (never sent again), reverts
the local order (a refused create removes it), leaves it out of the "Sync failed" count, raises the
in-memory "update required" flag and shows `errorAppUpdateRequired` ("Please update the app to
continue. This change was not saved."); web shows the same text. Server rule unchanged: send
`APP_UPDATE_REQUIRED` on order operations only.

**The update banner (1.53.0, FUP-75).** Client only. The flag is raised by the mobile queue (an
order refused, or an operation held, with `APP_UPDATE_REQUIRED`) and, since 1.53.0, by a web `/sync`
refusal with that code. While it is set, the signed-in shell shows a banner above every main screen
(the phone's tabs; every web page, beside the sidebar): `appUpdateBannerMessage` ("Please update the
app to place or change orders. Everything else still works."), with a button. It takes at most 40% of
the height and scrolls inside when the text is long. The banner never blocks: every screen and the
data kept on the device stay usable, nothing is wiped, and only orders wait.

- Phone: "Update" opens the app's Play Store page (`et.wereda.connect`). The listing is not live yet,
  so a second line, `appUpdateBannerStoreHint`, always says to get the latest app from where it was
  installed (a launch that "worked" may land on a page without the app).
- Web: "Reload page" reloads only once the newer app is in hand, because the app's code can be held
  in two places. A Flutter caching service worker (the default of the Flutter that CI builds the web
  app with) answers `main.dart.js` from its own cache whatever the page asks, so the page first asks
  its worker to update and waits (up to 3 minutes) for the newer worker to be active (its install
  downloads the app past the browser cache). Then it fetches `main.dart.js` with `cache: 'reload'`
  (the browser cache keeps it for a year, `immutable`), then reloads. Offline (`navigator.onLine`
  false), or when the worker update or the fetch fails, nothing is reloaded and nothing cached is
  dropped, so the app keeps running and still starts offline; a snackbar says so
  (`appUpdateReloadFailed`).

The flag is kept on the device with the running build's number (`X-App-Build`'s `n`): a restart on
the same build shows the banner again before any sync; the first start of another build forgets it,
and the next refusal raises it again if that build is still too old. Logout keeps it. An order
operation the server accepts (`success` or `duplicate`; mobile in the queue, web in the order
repository) clears it, kept value included: the minimum-version gate runs first on every order
operation, so this build passes it now (a minimum raised by mistake and rolled back, say). The
client still does no version gating of its own: the banner follows the server's last answer.

---

## Transport choice (1.40.0, PLANNED: ESC-19)

> **PLANNED. Not built at 1.40.0.** ESC-19 builds it, on the escrow ledger of ESC-20
> (`escrow-protocol.md` 2.0.0, P.10). Source: spec 4.7, 8 and 10.1, decisions D4, D10 and D14. Built
> today in `awaiting_transport` (Order commitments, 1.29.0): the buyer's cancel (`status: cancelled`),
> the farmer's cancel without penalty, and the 48 hour `buyerChoiceDeadlineAt`.
>
> **D4 awaits the user's confirmation** (spec 14: confirm the reading when ESC-19 starts): a pricier
> truck is covered from the commission, at most the commission left. **Open question for the user:**
> after a D4 shift, a later cheaper truck refunds "old cost minus new" from the current
> `truckerPayout`, so the commission the platform shifted ends up with the buyer. Example: transport
> 1,000 and commission 750; truck 2 at 1,500 shifts 500 from the commission; truck 3 at 800 refunds
> 700, and the platform has lost the 500 it shifted. Shift the 500 back to the commission first
> (refund 200), or let the buyer keep it? `escrow-protocol.md` P.10 asks the same.

One more order `update` command, sent by the order's buyer with the pre-increment `version`, alone
in `data` (any other key with it is `ORDER_VALIDATION`):

| `data` | Effect |
|---|---|
| `{"transportChoice": {"kind": "trip", "tripId": "trip-202"}, "version": n}` | Another truck: the rebind below, then the farmer's or the trucker's answer |
| `{"transportChoice": {"kind": "self_pickup", "pickupDate": "2026-11-20"}, "version": n}` | Collect it myself: below |
| `{"transportChoice": {"kind": "cancel"}, "version": n}` | Cancel: the same as the built buyer cancel (`transport_failed_buyer_cancelled`, full refund with commission, stock and capacity returned). The legacy `status: cancelled` stays accepted |

**Another truck.** The server rebinds the order (`tripId`, `truckerId`, `truckerName`, `transportCost`
computed from the new trip, a new `transportAttempts` entry with `outcome: pending`), reserves the
trip's capacity, rebinds the escrow (cheaper: the difference is refunded; pricier: covered from the
commission, D4, awaiting confirmation) and removes `buyerChoiceDeadlineAt`. What follows depends on
the farmer:

- **The farmer already accepted:** the order stays `awaiting_transport` and the new trucker is asked
  at once (`truckerResponse: pending`, `truckerAskedAt`, `truckerDeadlineAt` = T6 = the built
  `ResponseDeadline` against the new departure: min(now + 24h, departure - 12h), at least 1 hour
  from now). The trucker's accept makes the order `accepted` (with `pickupDeadlineAt`, as built).
  A decline or T6 fails the attempt (`outcome: declined` or `expired`, the trucker's fault
  recorded) and the buyer chooses again with a fresh 48 hours (`buyerChoiceDeadlineAt`).
- **The farmer has not accepted yet** (the D14 park: the trip was full or cancelled at funding, so
  the order went `funded -> awaiting_transport` with `farmerResponse: pending`, and the farmer can
  answer only in `funded`): the order goes back to `funded` with the farmer asked:
  `farmerResponse: pending`, `truckerResponse: not_asked`, and `farmerDeadlineAt` **restamped**
  against the new departure with the same rule (the old deadline was against the failed trip). The
  trucker is asked after the farmer's accept (D10), as for any trip order; the farmer's decline or
  deadline ends the order as built (`rejected` or `cancelled`, full refund).

**Collect it myself.** `pickupDate` is an ISO date 1 to 7 days ahead, counted in Ethiopia time
(Africa/Addis_Ababa). `fulfillment: self_pickup`, `truckerResponse: none`, the transport leg refunded,
the trip's capacity released, `buyerChoiceDeadlineAt` removed. Farmer already accepted: `accepted`,
with `pickupDeadlineAt` = the switch + 7 days, the built self-pickup window (`PickupDeadline`, counted
from acceptance). Farmer not yet: `funded` with the farmer asked (`farmerResponse: pending`,
`farmerDeadlineAt` restamped with the same rule against the start of `pickupDate` in Ethiopia time);
the farmer's accept makes it `accepted` with `pickupDeadlineAt` = that accept + 7 days. The P3 pickup
judgement (ESC-34, `delivery-verification-api.md` 2.0.0 P.2) later judges at `pickupDate` + 24 hours
plus the grace instead.

**Eligible trip** (spec 4.7): status `open` or `matched`, as the order create accepts (a `matched` trip
is full since D12, so the room rule below refuses it); `remainingQuintals` at least the order's
quintals; departing at least `orderLeadHours` (24) from now and no more than 7 days after the original
`pickupDate`; its trucker not restricted (`REPUTATION_RESTRICTED`, P2) and not already tried on this
order; its extra transport cost at most the commission still held. At most 3 trucks per order, the
original included.

**New order fields** (server-written, returned by `GET /orders`, dropped when a client sends them):

- `transportAttempts`: at most 3 entries `{tripId, truckerId, transportCost, outcome, requestedAt,
  closedAt}`, the first being the original trip. `outcome`: `pending` (asked, or waiting for the
  farmer's accept), `accepted` (the committed truck), `declined`, `expired`, `cancelled` (the trucker
  dropped out after accepting), `trip_cancelled`, `trip_full` (the failure values match
  `transportFailure`, D11), `order_closed` (the order ended while the attempt was open, such as the
  farmer's cancel), and `no_show` from P3 (ESC-34). **Backfill:** an order already in
  `awaiting_transport` when ESC-19 deploys gets entry 1 from its original trip (`outcome` from its
  `transportFailure`, `requestedAt` = `truckerAskedAt`, else `paymentVerifiedAt`; `closedAt` =
  `truckerRespondedAt`, else its `updatedAt`); any other order gets entry 1 at its first transport
  event.
- `refundedTotal`: ETB returned so far; `orderTotal` stays what the buyer paid.

| Refusal | When |
|---|---|
| `ORDER_UNAUTHORIZED:` | the caller is not the order's buyer (the built answer to an order command from the wrong party) |
| `ORDER_ACTION_NOT_ALLOWED:` | the order is not `awaiting_transport`, or a replacement request is still open |
| `ORDER_DEADLINE_PASSED:` | `buyerChoiceDeadlineAt` has passed |
| `ORDER_TRIP_NOT_ELIGIBLE:` | the trip fails a rule above other than its room (status, departure window, trucker), its extra cost is above the commission left, or the `pickupDate` is outside 1 to 7 days |
| `TRIP_CAPACITY_EXCEEDED:` | the trip has less room than the order's quintals. The same code when the room is too small at the buyer's request (read) and when the reservation write loses a race, as the order create (`trip-listing-api.md` 2.0.0) |
| `ORDER_TRANSPORT_ATTEMPTS_EXCEEDED:` | the order already used 3 trucks |
| `ORDER_VALIDATION:` | a malformed choice, an unknown `kind`, or other keys with it |

All are permanent and already handled by the clients (1.10.0, 1.17.0): mobile reverts the local order
and shows the code's text; web shows the failure. An unprefixed error (a trip or escrow that could not
be read) stays retryable. The buyer may browse cached trips offline; the server checks the choice when
it arrives (spec section 7). The choice sheet is ESC-23's.

**Pushes** (spec 4.12, `notifications-api.md` bumps with ESC-19): the new trucker gets
`replacement_request` when asked; a farmer asked again after the D14 park gets `order_request` as at
funding.

---

## Suspended accounts and reputation gates (1.48.0, ESC-29)

Source: `reputation-api.md` 1.2.0 sections 7 and 8.

**Suspended caller (not flagged).** The read the server already makes for a deleted account
(Deleted accounts, 1.26.0: the caller's users row, strongly consistent, once per request) also
refuses a row with `suspended: true`: the whole request answers HTTP `403` with
`{"error":{"code":"AUTH_025","message":"Account suspended."}}`. No operation is applied, no
idempotency key is read or stored and nothing is recorded for a retry to trip over. A deleting
account still gets its `410` first. Before 1.48.0 a suspended user's ID token kept working on `/sync`
until it expired (FRD-08).

| Client | On `403 AUTH_025` from `/sync` |
|---|---|
| Mobile queue (app build with ESC-29) | Reverts the batch to pending without a retry counted, signs the user out as after a rejected refresh, with the "account disabled" text on the login screen, and keeps the queue for the next sign-in |
| Web (app build with ESC-29) | The API client does the same for a `403 AUTH_025` from any route outside `/auth/`; the caller still gets the error |
| Older builds | Keep the batch pending without a retry counted (the `401`/`403` rule) and are signed out when their token refresh is refused with `AUTH_025` |

**Reputation gates (behind `reputationPenalties`, off in every environment).** The codes and the
client rules are those of Permanent trip and crop rejections (1.17.0); 1.48.0 is the server
sending them:

| Operation | Refusal | When |
|---|---|---|
| `crop_listing` create, `trip` create | `REPUTATION_RESTRICTED: until=<ISO time or review>: Your account can't take new orders until <date>. Your current orders continue.` | The caller's stored restriction has not ended. Update, delete and cancel stay allowed |
| `order` update with `farmerResponse: accept` or `truckerResponse: accept` | the same text | As above, after the no-op answer for an accept already given; `decline` stays allowed |
| `order` create | `REPUTATION_RESTRICTED: counterparty: This farmer or trucker can't take new orders right now.` | The listing's farmer or the trip's trucker is restricted or suspended (one strongly consistent read each). An order id the server already holds gets its `conflict` answer instead |
| `order` create | `The seller's account could not be checked. Please try again later.` (no prefix, retryable) | The owner's row could not be read; nothing is written |

To a client that sends no readable `X-App-Build` (build 1 or older; it may not know the code, as for
the minimum app version, 1.41.0) the same sentence goes out as `SYNC_VALIDATION: ...` on a crop or
trip and `ORDER_VALIDATION: ...` on an order, which every client treats as permanent.

**Owner fields (section 7 of `reputation-api.md`).** `ownerTier`, `ownerEffectiveRating`,
`ownerRatingCount`, `ownerReliabilityPct` and `ownerReputationAt` are server-written on crop and trip
rows. The server drops them from every client payload, create and update alike (no refusal), stamps
them on a create while `reputationPenalties` is on, and returns them in a `serverData` row (success
and conflict) only while `reputationPublicTags` is on, with `ownerTier` as the public tier (`good` or
`low`) and the three numbers as JSON numbers (1.37.0).

**`farmerReliability` (1.50.0, ESC-30).** Server-only like the owner fields: dropped from every
client payload, and no longer stamped on a crop create (matching reads `ownerReliabilityPct`,
`match-api.md` 1.4.0). A crop create reads no listing history any more.

---

## Listing reports (1.44.0, FUP-67)

New create-only entity `listing_report`: a buyer reports a crop listing or a trip to the admins. Full contract: `listing-report-api.md` 1.0.0.

- `entityId` = `{listingType}#{listingId}`; `data` = `{listingType: crop | trip, listingId, reason}` with `reason` one of `contact_info`, `misleading`, `scam`, `offensive`, `other`. No free text; any other field is refused.
- Buyers only (a farmer or a trucker sees only their own listings), never the caller's own listing, one report per buyer and listing (a repeat answers `duplicate`), at most 10 a day per buyer.
- Every refusal is `SYNC_VALIDATION:`, which every client already treats as permanent (Retry Logic): `Only buyers can report a listing.`, `Unsupported action ...`, the payload problems, `The reported listing does not exist.`, `You cannot report your own listing.`, `report limit reached. A buyer can send at most 10 reports a day.` A server without the reports table answers the unprefixed `Listing reports are not enabled on this server yet.`, which clients retry.
- Nothing is written to the listing and no `serverData` is returned: the owner never learns who reported.
- Client handling: mobile queues the create in the sync queue (offline first) with no local row, so a refusal reverts nothing; it is announced like any refusal, as `errorReportLimitReached` for the limit and `errorReportNotSent` otherwise (en, am, om). `listing_report` is not in the unannounced types. Web sends the create straight to `/sync` and shows the same texts in the report sheet; `duplicate` reads as "already reported".

---

## Order detail read (1.46.0, ESC-23)

Client only, no server change. The mobile order detail no longer waits for a list pull to learn what the other parties did.

- On open the screen shows the order the device holds at once, then reads `GET /orders/{id}` once behind it. Pull to refresh reads it again. There is no polling and no read per rebuild or per local reload.
- The read follows the pull rules: it is not sent while an operation for that order is pending, syncing or failed but retryable (the queued change is newer than the server's copy), the server's order is stored only when its `version` is not lower than the local one, and a change queued while the read was in flight wins.
- Best effort and silent: offline, a timeout, a 5xx, a `404` and a `403` leave the local order and the screen as they were. Unlike the revert of a refused update (1.10.0), a `404` or `403` here never removes the local order.
- The screen and its actions are never blocked by the read: the stored row is shown again without a loading state.
- Web has no local store; its order detail load is `GET /orders/{id}` already. Pull to refresh there loads the order again.

---

## Step-up leftovers on the clients (1.47.0, FUP-24, FUP-43)

Client only, no server change.

**The pending step-up follows the held operation (mobile, FUP-24).** An operation answered `AUTH_040:` or `AUTH_041:` stays pending as before, and the device remembers that a step-up is pending so the checkout can offer "Confirm it's you" after a cancelled prompt (ESC-44). That memory is now tied to the held operations (sync queue operations and queued escrow payment intents):

- It is dropped once every operation held for it has left its queue or got any other answer: sent, `duplicate`, `conflict`, `deferred`, refused for good, failed, out of retries, rejected along with a refused `create`, or deleted. Each sync run also checks the held operations against the queue, so a removal that never got an answer is noticed on the next run.
- While another held operation still waits it stays, and it asks for Fayda only if one of the operations still waiting was answered `AUTH_041:`.
- A cancelled prompt releases nothing: the memory and the 10 minute quiet period are unchanged. A completed step-up and a logout drop it as before. It is kept in memory only, as before: after an app restart the next sync raises it again.
- An operation the response left out keeps its hold (it is still unanswered).

**Delivery dispute on web (FUP-43).** Web sends the dispute straight to `/sync`. An `AUTH_040:` answer (as the operation's error, or as a `403` on the request) now opens the password prompt the payment screens use, and after a confirmed password the dispute is sent once more as the same operation (same `operationId`, `timestamp` and `idempotencyKey`, a fresh `syncBatchId`). A cancelled prompt, or a second `AUTH_040:` after the resend, leaves the dispute unsent with `deliveryDisputeNotConfirmed` (en, am, om). A reason changed in between is a new operation. `AUTH_041:` and every other answer keep their handling. Any party of the delivery (buyer, farmer, trucker) can dispute, so this holds for all three roles. Mobile is unchanged: the queue holds the operation and prompts.

---

## Refusal texts the clients read (1.49.0, FUP-77, FUP-83)

Client only, no server change. Two refusals are now told apart by what the server already sends.

**Order cancel refused for its payment.** The buyer's transport-failure cancel is answered
`ORDER_CANCEL_NOT_ALLOWED: The payment is being settled or reviewed.` when a dispute froze the money, or it
is being settled or held for review (1.13.0, escrow-protocol.md 1.25.0). The clients read the phrase
`payment is being settled or reviewed` after the code and show "This order can't be cancelled now because
its payment is under dispute or being settled. Wait for the outcome or contact support." (en, am, om)
instead of the pickup rule text. The phrase is now part of the contract: a server that rewords it must keep
it, or the clients fall back to the pickup rule text. Still permanent, and the local cancel is still undone.
Every other `ORDER_CANCEL_NOT_ALLOWED` keeps its text.

**`ORDER_KYC_REQUIRED` reason tokens.** In `ORDER_KYC_REQUIRED: <reason>: <text>` (1.5.0) a `<reason>` is
lower-case words joined by underscores.

- A reason the client does not know (a later server may add one, for example `trucker_fayda`) is explained
  only: "This payment was not sent because an identity check is still missing. Update the app or contact
  support to see what is needed." No Fayda action is offered and nothing is sent again, because the check may
  be another party's. Before, such a token was read as the buyer's and opened the buyer's Fayda flow.
- `buyer_phone` has its own error text (the phone wording) on every surface. Before, the inline error used
  the 10,000 ETB Fayda wording; the dialog already used the phone wording. It still offers Fayda, with the
  phone proof.
- A message without a token (servers before 1.5.0) is read from its wording as before, and the bare code
  still reads as the buyer's Fayda.

A new reason token therefore reaches old clients safely only from this version on; clients before it treat
it as the buyer's.

---

## A funded order's reject or cancel and its refund (1.51.0, FUP-53)

Server only; no client change. The `funded → rejected|cancelled` write (the farmer's reject, the buyer's
cancel while it is allowed, and the commitment `farmerResponse: decline`) and the refund arm on the
order's escrow are one transaction (`escrow-protocol.md` 2.11.0). New answers to that operation, all
with nothing written:

| Answer | When | Client |
|---|---|---|
| `ORDER_CANCEL_NOT_ALLOWED: The payment is being settled or reviewed.` | The money is frozen by a dispute, being settled, in the manual payout queue or past a confirmed delivery | Permanent like every `ORDER_*` refusal: the local change is undone; the 1.49.0 payment text is shown where the client reads the phrase |
| `The order's payment could not be read. Please try again later.` | The escrow read failed | Unprefixed, retried |
| `The order's payment changed while the order was updated. Please try again later.` | The escrow kept changing over 3 writes | Unprefixed, retried |
| `The order could not be updated right now. Please try again later.` | The transaction was cancelled by a conflicting transaction or throttling | Unprefixed, retried |

A `conflict` (the order moved) and a success answer as before. With nothing in the way the success,
`serverVersion` and `serverData` are the same as before.

---

## Codes older apps do not know (1.52.0, FUP-241)

Server only; no client change. The five codes of Permanent trip and crop rejections (1.17.0) are
known to clients only since FUP-01. An app that sends no readable `X-App-Build` (build 1 or older,
read as in Minimum app version for order operations, 1.41.0) may predate that client, and such a
client resends an unknown code on every reconnect. So the server sends each code only to an app that
sends the header. An app without it gets the same sentence under a code every client already treats
as permanent (Retry Logic): the code and any reason token before the sentence (`until=...`,
`counterparty`) are dropped.

| Code | Sent on | To an app without `X-App-Build` |
|---|---|---|
| `TRIP_HAS_BOOKINGS` (new in 1.52.0) | `trip` update or delete, behind `COMMITMENTS_ENABLED` (1.29.0) | `SYNC_VALIDATION: <sentence>`, for example `SYNC_VALIDATION: capacity cannot drop below the 20 quintals already booked.` or `SYNC_VALIDATION: Crops from this trip are already on the way; it can no longer be cancelled.` |
| `CROP_HAS_ACTIVE_ORDERS` (new in 1.52.0) | `crop_listing` delete or update to `status: deleted`, behind `COMMITMENTS_ENABLED` (1.35.0) | `SYNC_VALIDATION: This listing has N order(s) in progress. It cannot be deleted until they are completed or cancelled.` |
| `REPUTATION_RESTRICTED` (1.48.0, unchanged) | `crop_listing` and `trip` create, `order` accept and create, behind `reputationPenalties` | `ORDER_VALIDATION: <sentence>` on an order, `SYNC_VALIDATION: <sentence>` on anything else |
| `APP_UPDATE_REQUIRED` (1.41.0, unchanged) | `order` operations below `MIN_ORDER_APP_BUILD` | `ORDER_VALIDATION: This version of the app is too old for orders. Update WeredaConnect to continue.` |
| `TRIP_CAPACITY_EXCEEDED` (unchanged) | `order` create or update naming a trip | Never reaches such an app: the minimum app version gate (1.41.0) refuses every `order` operation without the header first, and an infra test keeps that gate on in every environment with `COMMITMENTS_ENABLED` on. |

Why `SYNC_VALIDATION` and not `TRIP_VALIDATION` or `CROP_VALIDATION`: those two stay retryable on
every client (only their immutable-field text is permanent, and only since 1.43.0), while every
mobile build since March 2026 marks a `SYNC_VALIDATION` refusal rejected and never resends it. Such
an app does not revert its local change (the 1.17.0 revert came with the codes) and counts the
operation as failed; web shows the message as a failure. An app that sends the header (build 2 or
later) gets the code exactly as before, with the 1.17.0 revert and text. The fraud signal of a
refused booked-trip change (1.27.0, `IMMUTABLE_FIELD_CHANGE`) still records the `TRIP_HAS_BOOKINGS`
text. Built in `Helpers/OldClientRefusal.cs`, shared with the 1.48.0 fallback.

## Handoff fields by status (1.54.0, FUP-246)

Server only; no client change. A `delivery` operation writes each handoff field only with the status
of its own handoff. The role check already pins each of those statuses to one party, so the field
can only come from that party's handoff:

| Field | Written only with | By |
|---|---|---|
| `gpsPickup` | `status: pickedUp` | the trucker (pickup scan) |
| `gpsDropoff` | `status: delivered` | the buyer (drop-off scan, or a self-pickup at the farm) |
| `disputeReason` | `status: disputed` | the party who disputes |

Anywhere else (a create, which is always `pending`; `inTransit`; `delivered` for `gpsPickup`;
`disputed` for both points; an update without a status) the field is dropped, not refused: the
operation is answered as if it had not been sent, and the server logs
`DELIVERY_FIELD_DROPPED field=<field> status=<status or none> deliveryId=<id>` at Information (no
personal data). Before, a buyer's confirm could carry `gpsPickup` and move the trucker's pickup point
(the impossible delivery time rule then wrote `IMPOSSIBLE_DELIVERY_TIME` on an honest trucker), and
any party's dispute, or the trucker's update without a status, could rewrite both points the admin
reads as dispute evidence (`fraud-detection-architecture.md` 3.34.0,
`delivery-verification-api.md` 2.5.0).

Shipped clients are unaffected: mobile and web send `gpsPickup` only in the pickup confirm,
`gpsDropoff` only in the delivery confirm and `disputeReason` only in the dispute, and none of them
on a create, `inTransit` or the farmer's `qrNonce` update.

Checked and unchanged: the photo keys (`pickupPhotoRef`, `dropoffPhotoRef`) were already dropped
from every `delivery` operation (1.39.0, attached only by their taker through `delivery_photo`, first
photo kept); `pickedUpAt`, `confirmedAt` and `updatedAt` are stamped by the server and a client
value is dropped (1.3.1); `pickupCapturedAt` and the photo version ids are refused as unknown fields;
`qrNonce` is taken only from the farmer (SIM-41) and a party's `scannedQrNonce` or `scannedQrCode`
is never stored.

---

## Changelog

| Version | Date | Change |
|---------|------|--------|
| 1.54.0 | 2026-11-12 | Handoff fields by status (FUP-246, `delivery-verification-api.md` 2.5.0, `fraud-detection-architecture.md` 3.34.0): a `delivery` operation writes `gpsPickup` only with `status: pickedUp`, `gpsDropoff` only with `delivered` and `disputeReason` only with `disputed`; anywhere else (a create, `inTransit`, the other handoffs, a dispute, an update without a status) the field is dropped, not refused, and logged as `DELIVERY_FIELD_DROPPED`. A buyer's confirm can no longer move the trucker's pickup point, nor a dispute rewrite either point. Server only; every shipped client already sends each field only with its own status. |
| 1.53.0 | 2026-11-12 | The "update the app" banner (FUP-75), client only: the shell shows it above every main screen while the "update required" flag is set, with a Play Store button and a hint on the phone, and on web a reload that first updates the caching service worker, then fetches `main.dart.js` past the browser cache, and does nothing offline; web now raises the flag on an `APP_UPDATE_REQUIRED` refusal; the flag is kept with the build number (survives a restart on the same build, forgotten by another build) and an accepted order operation clears it (see Minimum app version for order operations). No server change. |
| 1.52.0 | 2026-11-12 | Codes older apps do not know (FUP-241, `crop-listing-api.md` 1.12.1, `trip-listing-api.md` 2.6.1): `TRIP_HAS_BOOKINGS` and `CROP_HAS_ACTIVE_ORDERS` go only to an app that sends `X-App-Build`; an app without it (build 1 or older) gets the same sentence as `SYNC_VALIDATION:`, permanent on every mobile build since March 2026, as `REPUTATION_RESTRICTED` already did (1.48.0, unchanged). Apps with the header get the codes as before. Server only |
| 1.51.0 | 2026-11-12 | A funded order's reject or cancel and its refund arm are one transaction (FUP-53, `escrow-protocol.md` 2.11.0, `order-lifecycle.md` 2.12.0): money frozen or moving answers `ORDER_CANCEL_NOT_ALLOWED: The payment is being settled or reviewed.` (permanent), and three unprefixed retryable texts cover a failed escrow read, an escrow that kept changing and a cancelled transaction; nothing is written on any of them. Server only. |
| 1.50.0 | 2026-11-12 | `farmerReliability` retired (ESC-30, `match-api.md` 1.4.0): dropped from client payloads like the owner fields and no longer stamped on a crop create. |
| 1.49.0 | 2026-11-12 | Refusal texts the clients read, client only (FUP-77, FUP-83): `ORDER_CANCEL_NOT_ALLOWED: The payment is being settled or reviewed.` has its own text (the phrase is now relied on by the clients); an `ORDER_KYC_REQUIRED` reason token the client does not know is explained only, with no Fayda action; `buyer_phone` has its own error text on every surface. 1.48.0 is taken by another branch |
| 1.48.0 | 2026-11-11 | Suspended accounts and reputation gates (ESC-29, `reputation-api.md` 1.2.0, `auth-api.md` 2.15.0). Not flagged: a batch from a suspended account answers HTTP `403 AUTH_025` for the whole request from the caller row the server already reads, nothing applied, no idempotency key stored; clients with ESC-29 keep the queue without a retry and end the session, older builds keep the queue and are signed out at their next token refresh. Behind `reputationPenalties` (off everywhere): the server now sends `REPUTATION_RESTRICTED` on a restricted caller's `crop_listing` create, `trip` create and order accept, and on an `order` create whose listing farmer or trip trucker is restricted or suspended (an unreadable owner row answers an unprefixed retryable text); a client without `X-App-Build` gets the sentence as `SYNC_VALIDATION` or `ORDER_VALIDATION`. The owner fields (`ownerTier`, `ownerEffectiveRating`, `ownerRatingCount`, `ownerReliabilityPct`, `ownerReputationAt`) are dropped from client payloads and returned in `serverData` only with `reputationPublicTags` on. |
| 1.47.0 | 2026-11-11 | Step-up leftovers, client only (FUP-24, FUP-43): on mobile the remembered pending step-up is tied to the held operations and dropped once they all left the queue or got another answer, kept across a cancelled prompt; on web a delivery dispute answered `AUTH_040:` opens the password prompt and is sent once more as the same operation, and a cancelled prompt leaves it unsent with its own text (en, am, om) |
| 1.46.0 | 2026-11-11 | Order detail read (ESC-23), client only: the mobile order detail shows the stored order at once and reads `GET /orders/{id}` once per open and on pull to refresh, under the pull rules (not sent while an operation for the order is queued, never to an older `version`); a failed read, `404` and `403` included, is silent and removes nothing. No server change. |
| 1.45.0 | 2026-11-11 | Changes to a finished delivery or trip (FUP-128, `delivery-verification-api.md` 2.2.0): a delivery update that reaches a cancelled (or legacy soft-deleted) delivery with a version behind the stored one answers `conflict` with the scrubbed row to a caller who may make that change (SIM-28's rule; the trucker or the farmer's `qrNonce` for an update without a status), instead of `DELIVERY_ALREADY_CANCELLED`; at the current version, without one or for anyone else the error stands. The trip side was built in `trip-listing-api.md` 2.4.0. Mobile: `DELIVERY_ALREADY_CANCELLED`, `TRIP_TERMINAL_STATE` and `TRIP_ALREADY_CANCELLED` errors are permanent and revert the delivery or trip (they were retried); the conflict resolver no longer fails on a crop row without `description`, a trip row without `truckerName` or `truckerPhone`, reads a crop's flat `latitude` and `longitude` and maps `available` to `active`. Web unchanged. New conflict resolution rule 7 |
| 1.44.1 | 2026-11-11 | A JSON `null` inside `operations` (FUP-127 audit): the whole batch is refused with `400 SYNC_VALIDATION` `"Operations must not contain null."` and nothing is applied. Before, the request failed with an unhandled error (a `502` without a code from API Gateway), also for the valid operations sent with it. No client sends one; no app change |
| 1.44.0 | 2026-11-07 | Listing reports (FUP-67, `listing-report-api.md` 1.0.0): new create-only entity `listing_report` (`{listingType}#{listingId}`, `{listingType, listingId, reason}`), buyers only, one per buyer and listing (`duplicate` on a repeat), 10 a day; every refusal `SYNC_VALIDATION:`, already permanent on every client; nothing local to revert. New mobile and web report sheet, texts `errorReportNotSent` and `errorReportLimitReached` (en, am, om). The entityType list in Request Format now also names `delivery_photo` (1.39.0) |
| 1.43.0 | 2026-11-07 | Mobile sync refusals (B1: FUP-08, FUP-25, FUP-51, FUP-74, FUP-110, FUP-138), client only: every `*_UNAUTHORIZED` but a role gate (a token without the role claim gets it too), `RATING_FORBIDDEN` for another user's order, the immutable-field `CROP_VALIDATION`, `TRIP_VALIDATION` and `DELIVERY_VALIDATION` texts and `DELIVERY_NONCE_MISSING` are permanent (see Refusals no retry can pass; `CROP_UNAUTHORIZED` and `TRIP_UNAUTHORIZED` no longer stay retryable, 1.17.0); `DELIVERY_NONCE_MISSING` is reverted like a refused typed code; `CROP_CONTACT_IN_TEXT`, `TRIP_CONTACT_IN_TEXT` and `FRAUD_ACCOUNT_HELD` revert a crop listing or trip (1.17.0 rules); a reverted delivery and a message removed for a closed thread leave the "Sync failed" count; the mobile queue leaves a `pickupPhotoRef` or `dropoffPhotoRef` that is not this delivery's upload key out of a `delivery` operation (an old build's file path); the old `ORDER_VALIDATION` total text is no longer read as `ORDER_PAYMENT_TOTAL_OUT_OF_RANGE` (1.24.0). No server change |
| 1.42.0 | 2026-11-07 | Drop-off place (LT-14, `order-lifecycle.md` 2.9.0): `deliveryLatitude` and `deliveryLongitude` are new order fields on create and on the buyer's update until pickup; new permanent `ORDER_DELIVERY_LOCATION_LOCKED` (a change after pickup) under the `ORDER_*` rule, with its own text in en, am and om (`errorDeliveryLocationLocked`); an order row in `serverData` leaves the point out for a caller who may not read it, and carries it as numbers. Older clients revert and show the generic order refusal |
| 1.41.0 | 2026-11-07 | Minimum app version for order operations built (ESC-25): `/sync` refuses every `order` operation (create, update, delete) from a build below `MIN_ORDER_APP_BUILD` on the sync Lambda (infra `minOrderAppBuild`, 3 in dev, staging and prod; 0 or absent = off; independent of `COMMITMENTS_ENABLED`) before any read or write: `APP_UPDATE_REQUIRED: This version of the app is too old for orders. Update WeredaConnect to continue.` to an app that sends `X-App-Build`, `ORDER_VALIDATION:` with the same text to one that sends none (confirmed). Nothing stored, no idempotency key, no fraud signal; the rest of the batch goes on; other entities and `/escrow/*` are not gated. `COMMITMENTS_ENABLED` stays off in staging and prod until the owner turns it on after the gate is deployed. No client change: mobile and web already treat it as permanent on an order and show `errorAppUpdateRequired` (1.17.0) |
| 1.40.0 | 2026-11-07 | Two PLANNED sections (ESC-14), nothing built changes. Minimum app version for order operations (ESC-25): the server refuses every `order` operation, create and update, from a build below a per-environment minimum (proposed `MIN_ORDER_APP_BUILD`, 0 or absent = off; build 3 is the first that renders the commitments) before any read or write, with `APP_UPDATE_REQUIRED:` to an app that sends `X-App-Build` and, proposed, `ORDER_VALIDATION:` with the same text to one that sends none (it may predate the 1.17.0 client, FUP-75); other entities and the `/escrow/*` routes are not gated. Transport choice (ESC-19): the buyer's `transportChoice` command (`trip` with `tripId`, `self_pickup` with `pickupDate`, `cancel`) in `awaiting_transport`; a choice on an order the farmer has not accepted (the D14 park) goes back to `funded` with the farmer asked and `farmerDeadlineAt` restamped, the trucker asked after the farmer (D10); the eligible trip rules (`open` or `matched`), the order fields `transportAttempts` (with its `outcome` values and the backfill) and `refundedTotal`, the `replacement_request` push, and its refusals (`ORDER_UNAUTHORIZED`, `ORDER_ACTION_NOT_ALLOWED`, `ORDER_DEADLINE_PASSED`, `ORDER_TRIP_NOT_ELIGIBLE`, `ORDER_TRANSPORT_ATTEMPTS_EXCEEDED`, `TRIP_CAPACITY_EXCEEDED` at read time and in a race, `ORDER_VALIDATION`), all already handled by the clients. D4 is marked as awaiting the user's confirmation, with the open question of a cheaper truck after a shift. Doc fix: Permanent order rejections listed `ORDER_TRIP_NOT_ELIGIBLE` as planned; it is sent since 1.34.0 |
| 1.39.0 | 2026-11-07 | Handoff photos (FRD-12, `delivery-verification-api.md` 1.25.0): new create-only entity `delivery_photo` (`{deliveryId}#{pickup\|dropoff}`, `{photoRef}`), sent by the photo's taker only, attaching an uploaded photo without moving the delivery's version; `DELIVERY_PHOTO_INVALID`, `_FORBIDDEN`, `_REFUSED`, `_EXISTS`, `_MISSING` and `_TOO_LARGE` are permanent, not reverted, not announced and not counted as failures; `DELIVERY_PHOTO_PENDING` answers `deferred`; a photo ref in a `delivery` op is shape-checked but no longer written. Mobile uploads the photo in a batch preflight and stores the key in the operation; every wait is bounded (a presign `404` only while the delivery's own create waits on the phone and 10 minutes, the third refused `PUT`), a refused delivery create, pickup or delivery confirm rejects its waiting photo, and an attached photo's files are deleted; web uploads at capture and attaches after the confirm |
| 1.38.0 | 2026-11-06 | Idempotency and races (SIM-28): the server stores a payload fingerprint with each idempotency key (SHA-256 of a canonical JSON of `entityType`, `entityId`, `action`, `clientVersion` and `data`; keys sorted, numbers by value; `operationId` and `timestamp` left out) in the new `payloadFingerprint` attribute; a key sent again with a different payload answers `error` `SYNC_VALIDATION: idempotency key already used for a different operation payload; ...` instead of `duplicate`, nothing processed or stored; records from before keep answering `duplicate`. A status change that is not a valid transition from the stored status and carries a version behind the stored one answers `conflict` with the row (to a caller who may ask for that status, `order-lifecycle.md` 2.6.0) instead of `ORDER_VALIDATION: Invalid status transition`, `DELIVERY_INVALID_TRANSITION`, `TRIP_INVALID_TRANSITION` or `CROP_INVALID_TRANSITION`; at the current version the error stands. No client change: `SYNC_VALIDATION` is already permanent and `conflict` already resolved server-wins (mobile) or a failure (web) |
| 1.37.0 | 2026-11-06 | Typed `serverData` (SIM-25): `success` and `conflict` rows carry numbers as JSON numbers (`version`, quantities, prices, money fields, coordinates, `stars`) and a trip's `matchedListings` as an array, the types GET returns for the same row; before, every number was a string and `matchedListings` the text `"[]"`. Other values keep their type. The mobile conflict resolver already read these as numbers and a list, so its crop, trip and order conflicts failed on the string shape and are now applied server-wins; it also no longer requires `totalPrice` on an order row (the server never stores it): the local total is kept, a new row starts at 0 as the order pull does. Web reads only `status` from a row. No new code |
| 1.36.0 | 2026-11-06 | Drop-off landmark (LT-12, `order-lifecycle.md` 2.5.0): `dropoffNote` is a new order field on create and on the buyer's update until pickup; new permanent `ORDER_CONTACT_IN_TEXT` (a phone number in it) and `ORDER_DROPOFF_NOTE_LOCKED` (a change after pickup), both under the `ORDER_*` rule clients already apply (1.10.0), now with their own text in en, am and om (`errorContactInText`, `errorDropoffNoteLocked`); an order row in `serverData` leaves the note out for a caller who may not read it. Older clients revert and show the generic order refusal |
| 1.35.0 | 2026-11-06 | Listings that orders depend on (ESC-18, `crop-listing-api.md` 1.7.0, `order-lifecycle.md` 2.4.0): behind `COMMITMENTS_ENABLED`, the server sends `CROP_HAS_ACTIVE_ORDERS: This listing has N order(s) in progress. It cannot be deleted until they are completed or cancelled.` on a `crop_listing` delete, or an update to `status: deleted`, while an order of the listing is `payment_submitted`, `funded`, `awaiting_transport` or `accepted`; when the orders cannot be read, the unprefixed `The listing's orders could not be checked. Please try again later.` (retryable). A quantity edit never gets the code. Clients already handle it (1.17.0: permanent, the listing reverted from `GET /crops/{id}`, `errorCropHasActiveOrders`). A funding that loses the listing's stock cancels the order on the server (`listing_out_of_stock`); clients see it through the order pull. No client change |
| 1.34.0 | 2026-11-04 | Orders on a trip that left (L-7, `order-lifecycle.md` 2.3.0, `trip-listing-api.md` 2.2.0): the server sends `ORDER_TRIP_NOT_ELIGIBLE: This trip has already left; choose another trip.` on an `order` create naming a trip whose departure day is over in Ethiopia, flag on or off. Clients already treat the code as permanent (1.10.0) and give it its own text on a refused create (1.17.0, `errorTripNotEligible`): the mobile queue removes the local order and explains it, web shows the same text. No client change; no new code |
| 1.33.0 | 2026-11-04 | Typed handoff code (T-1, `delivery-verification-api.md` 1.22.0): a delivery `pickedUp` or `delivered` update may carry `scannedQrCode` in place of `scannedQrNonce` (allowed in `data`, never stored); new permanent `DELIVERY_CODE_MISMATCH`, `DELIVERY_CODE_LOCKED`, `DELIVERY_CODE_LOCKED_DROPOFF` and `DELIVERY_CODE_GPS_REQUIRED`, rejected and reverted by the mobile queue and shown by web with their own en, am and om text; mismatches and locks write `QR_HANDOFF_FAILURE`. Trucker clients: Stop sharing in trip mode sends a `declined` `location_ping` before the phone stops (no server change). |
| 1.32.0 | 2026-10-28 | Location pings, clients (LT-08): mobile queues `location_ping` with the capture time in the idempotency key, fills a batch with pings only after every other operation, sends one probe ping per sync for a trip whose pings are deferred, rejects `LOCATION_*` errors for good without announcing or counting them (`LOCATION_SHARING_ENDED` also stops sharing), coalesces over 24 waiting pings to one `fix` per trip and clock hour plus every event, and drops a trip's waiting pings when its pickup is refused for good; web posts each ping at once with the same rules. No server change |
| 1.31.0 | 2026-10-28 | Location pings (LT-05, `live-trip-api.md` 1.0.0): new create-only entity `location_ping` (the trucker's check-ins and sharing events after pickup), accepted from the trip's trucker while a delivery on the trip is on the road or left it after `capturedAt` (late offline pings kept); `LOCATION_VALIDATION`, `LOCATION_NOT_TRUCKER`, `LOCATION_SHARING_ENDED`, `LOCATION_RATE_LIMITED` and `LOCATION_STALE` are permanent errors, not reverted; `LOCATION_NOT_ACTIVE` answers `deferred` (no delivery on the road yet, another still pending, or a delivery written earlier in the same batch); a resend of the same `entityId` and `kind` is `success`; clients handle it from LT-08 |
| 1.30.0 | 2026-10-27 | Order commitments, clients (ESC-22, ESC-23, app build 3): the farmer's answer is sent as `farmerResponse: accept \| decline` on every server (translated with the flag off); the trucker's command only on an order with a `fulfillment` stamp; `ORDER_RESPONSE_NOT_PENDING:` has its own text. No server change. |
| 1.29.0 | 2026-10-26 | Order commitments (ESC-15, `order-lifecycle.md` 2.0.0, behind `COMMITMENTS_ENABLED`): the farmer answers a `funded` order with `farmerResponse: accept \| decline` (legacy `status: accepted \| rejected` mapped) and the trucker, once the farmer accepted, with `truckerResponse: accept \| decline`, each with an optional `declineReason` (`date`, `capacity`, `price`, `route`, `crop_not_ready`, `other`) and the pre-increment `version`; these three are the only new allowed update keys, every other new order field and the system-managed status `awaiting_transport` are server-written and returned by `GET /orders`; `ORDER_RESPONSE_NOT_PENDING:` and `ORDER_DEADLINE_PASSED:` are permanent under the `ORDER_*` rule (no client change for them); `AUTH_040:` on the trucker's drop-out from `accepted` and the farmer's cancel from `accepted`; `ORDER_CANCEL_NOT_ALLOWED:` on a buyer's `funded -> cancelled`; the server now sends `TRIP_CAPACITY_EXCEEDED:` (order create above `remainingQuintals`) and `TRIP_HAS_BOOKINGS:` (a trip change the bookings forbid) behind the flag; header and footer versions aligned (the footer had stayed at 1.27.0) |
| 1.28.0 | 2026-10-24 | User profile updates (SIM-37): the `user` entity takes `action: update` from the caller on their own row with the single allow-listed field `preferredLanguage` (`en`, `am`, `om`), version-less last-write-wins, an unchanged value a no-op `success`; errors `USER_PROFILE_VALIDATION`, `USER_PROFILE_UNAUTHORIZED`, `USER_PROFILE_NOT_FOUND`, `USER_PROFILE_CONFLICT` (retried) and `USER_PROFILE_UNAVAILABLE` (row added 2026-10-26; the section was in the body without one) |
| 1.27.0 | 2026-10-23 | Fraud signals from refusals (FRD-08, fraud spec 3.9.0): create conflicts on another user's id, the `*_UNAUTHORIZED` refusals, identity and money field changes, QR nonce failures and a burst of 10 version conflicts in one batch write a typed signal on the caller, at most one row per type per hour, counted once per refused entity; responses unchanged, a failed write only logged; clients unchanged |
| 1.26.0 | 2026-10-23 | Deleted accounts (ACC-02, `account-deletion.md` 1.2.0): a batch from an account that is being or has been deleted answers HTTP `410 ACCOUNT_004` with `error.deletionState` and nothing is applied (the client stops that sync without counting a retry; for `cognito_pending` and `completed` it never resends and wipes, for `in_progress` a later sync may resend because the deletion can still abort); a batch whose caller row cannot be read answers `503 SYNC_SERVER_ERROR`; a message to, or a rating about, an account with `deletionState` is op-level `SYNC_VALIDATION`; a `payment_submitted` for an order of such an account is `ORDER_VALIDATION` (`order-lifecycle.md` 1.26.0) |
| 1.25.0 | 2026-10-23 | Risk tier built on the server (FRD-04, the 1.9.0 rules): `FRAUD_ACCOUNT_HELD:` on crop, trip and order create and on order `payment_submitted` for a caller at risk tier CRITICAL (a truncated score read included), nothing written; MEDIUM and HIGH raise `AUTH_040:` and `AUTH_041:` at any amount (Fayda first); the caller's tier only, read once per batch; an unreadable score or a missing fraud-signals table is LOW; the position of each check; `AUTH_041:` text amount-neutral; clients unchanged |
| 1.24.0 | 2026-10-23 | Payment total refusal (FUP-22): the live gateway's refusal of a `payment_submitted` whose total is outside 100 to 500,000 ETB is `ORDER_PAYMENT_TOTAL_OUT_OF_RANGE` with the same sentence (it was `ORDER_VALIDATION`); permanent under the `ORDER_*` rule, nothing written; clients match the code and, for one release, the old text; same gate, bounds and order; clients before 1.24.0 show the generic refusal |
| 1.23.0 | 2026-10-23 | Listing text refusal (FUP-49): `CROP_CONTACT_IN_TEXT` and `TRIP_CONTACT_IN_TEXT` for a crop listing or trip whose buyer-visible text holds a phone number (`crop-listing-api.md` 1.4.0, `trip-listing-api.md` 1.5.0), nothing written; deterministic, permanent on the mobile queue, not reverted, explained in en, am and om; web shows the same text; the forms run the same check before queueing; older clients retry it |
| 1.20.0 | 2026-10-23 | Legacy listing side effect (FUP-18): mobile no longer queues a `cropListing` update on delivery create and confirm (the server cannot route it and refuses non-owners anyway) nor flips its local listing copy; `cropListing` operations already queued are removed at app start, the one exception to invariant 1; no server change |
| 1.19.0 | 2026-10-23 | QR gate money check (FUP-42, `delivery-verification-api.md` 1.17.0): `deferred` is also sent for a delivery `pickedUp`/`delivered` with no nonce on record when the escrow that decides whether it needs the QR cannot be read (message `DELIVERY_NONCE_PENDING: ...`, nothing written, never hard-failed, alarmed on the server); such a delivery whose order escrow is not linked to it is now `deferred` too, where it passed. Clients unchanged: mobile re-queues `deferred` without using a retry, web shows its QR-pending message. |
| 1.18.0 | 2026-10-23 | Client handling of fraud codes (FRD-06): `FRAUD_ACCOUNT_HELD` gets its own text in en, am and om on mobile (every entity and action, and the batch error message) and on web; order operations keep the 1.10.0 revert, crop and trip creates keep their local row; the `AUTH_041` prompt no longer names an amount; no server change |
| 1.17.0 | 2026-10-23 | Permanent trip and crop rejections (ESC-12 part B, FUP-01): `REPUTATION_RESTRICTED`, `TRIP_HAS_BOOKINGS`, `TRIP_CAPACITY_EXCEEDED` and `CROP_HAS_ACTIVE_ORDERS` are permanent on any entity, `APP_UPDATE_REQUIRED` on an order (it is sent on order commands, spec K1); the mobile client reverts a refused crop listing or trip with the order rules (create: remove; update or delete: re-read `GET /crops/{id}` or `GET /trips/{id}`, 404/403 remove, other failures restore the lowest sent version and clear the pull stamp) and leaves a reverted one out of the failed count; order refusals with these codes are reverted like any order refusal; any other operation answered `APP_UPDATE_REQUIRED` is kept pending for the updated build, never reverted, not resent by this build; a refused delivery create is removed; every section 8 code has its own text in en, am and om, `REPUTATION_RESTRICTED` on an order create names the owner; `APP_UPDATE_REQUIRED` also raises an in-memory update flag; servers may send the five codes once this client is the minimum in use |
| 1.16.0 | 2026-10-22 | Payment method refusal (FUP-20): `ORDER_PAYMENT_METHOD_UNAVAILABLE` for a live `payment_submitted` whose `paymentMethod` is present and not `telebirr` (bank transfers wait for ESC-46); decided after the other payment gates and before any write (no payment clock, audit row or idempotency key); only the update's value counts, an update without a method is left to the checkout; mock mode unchanged; permanent, with its own text in en, am and om on mobile and web |
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

**Last Updated**: 2026-11-12
**Version**: 1.54.0
