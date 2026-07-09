# Contract: In-App Messaging (context-scoped chat)

**Status**: Implemented (2026-07-09). Requires deploy + the manual SNS Platform
Application step (see `.ai/push-notifications-setup.md`) for push delivery.

Offline-first, asynchronous chat between the two legitimate parties of a
marketplace context. There is **no real-time transport**: messages are created
locally, queued, and synced over `/sync`; the recipient sees them on their next
sync/pull, and an FCM push makes it timely.

---

## Entity: `message` (create-only, immutable)

Synced via `/sync` with `entityType: "message"`, `action: "create"` only. The
partition key is the client-generated `messageId`, so a retry is an idempotent
no-op (`attribute_not_exists(messageId)`).

### Client-supplied `data` fields

| Field | Type | Notes |
|-------|------|-------|
| `messageId` | string (uuid v4) | == the op `entityId`; table PK |
| `conversationId` | string | deterministic (see formula) |
| `contextType` | enum | `crop` \| `order` \| `trip` \| `delivery` |
| `contextId` | string | cropId (== listingId) / orderId / tripId / deliveryId |
| `senderId` | string | MUST equal the authenticated user |
| `recipientId` | string | the counterpart |
| `body` | string | ≤ 1000 chars |
| `createdAt` | ISO-8601 UTC | client compose time; immutable, stable across retries |

### Server-injected fields (client MUST NOT send; stripped if present)

`senderName`, `senderPhone`, `recipientName`, `recipientPhone` — denormalized
authoritatively from the users table (`GetUserByIdAsync`) so neither party can
spoof identity/phone and either side can Call/Copy the other's number. Plus
`version = 1`.

### Deterministic conversation id

```
conversationId = "{contextType}:{contextId}:{userIdLow}:{userIdHigh}"
```

`userIdLow`/`userIdHigh` = the two participant ids sorted ordinal-ascending, so
both directions converge on the same key. A conversation is a **client-side
grouping** of messages by this id — there is no mutable server conversation row
(that would be a two-writer merge-conflict surface).

---

## Role / visibility enforcement (CRITICAL — server, `ProcessMessageAsync`)

1. `senderId == authenticatedUserId`, else `MESSAGE_UNAUTHORIZED`.
2. `conversationId` must equal the deterministic value for the context + pair.
3. Load the context and require both parties are legitimate:
   - **crop**: exactly one of {sender, recipient} is `crop.farmerId`; the other has role `buyer`.
   - **trip**: exactly one is `trip.truckerId`; the other has role `buyer`.
   - **order**: sender and recipient are two distinct members of `{buyerId, farmerId, truckerId}`.
   - **delivery**: sender and recipient are two distinct members of `{buyerId, farmerId, truckerId}`.

A third party posting into someone else's conversation is rejected
(`MESSAGE_FORBIDDEN`). Read access (`GET /messages`) only returns messages the
caller sent or received.

---

## Read API (`MessageFunction`, authenticated)

- `GET /messages?conversationId=X` — a thread, newest-first, `nextToken` paginated. Results filtered to the caller's own messages.
- `GET /messages?since=T` — sync-down pull: all messages to/from the caller with `createdAt > T` (oldest-first), merged inbound ∪ outbound.
- `GET /messages/{id}` — a single message (403 if caller is not a party).

Mobile pulls incrementally using its newest local message time as the cursor
(stateless). Web reads live (no local store, no unread badges).

---

## Push (`MessageStreamFunction`)

Messages-table `NEW_IMAGE` stream (INSERT only) → look up the recipient's device
endpoints (`device-tokens`) → SNS publish an FCM notification with data:

```json
{ "type": "message", "conversationId": "...", "messageId": "...",
  "senderId": "...", "contextType": "...", "contextId": "..." }
```

Android only (iOS/APNs deferred). Recipients with no registered device fall back
to the in-app unread badge.

---

## Unread / read state

Local-only, per device (`ConversationRead` Isar store): a conversation's unread
count = messages in it not sent by me with `createdAt` after my `lastReadAt`.
Not synced. Read receipts ("seen") are a documented future op (`read_marker`).

## Known limitations / deferrals

- Not real-time (appears on next sync/pull; push is the timeliness signal).
- Cross-device / web sent-message history relies on the `SenderIdCreatedAtIndex` pull.
- Tap-to-thread deep-linking from a push is deferred (no global navigator key yet); the notification still delivers and the inbox badge updates.
- Order/delivery-scoped chat is reachable from the inbox once started; dedicated "Message" buttons on the order/delivery detail screens are a follow-up (crop→farmer and trip→trucker entry points ship now).
