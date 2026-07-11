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
(stateless). Web hydrates a session-scoped in-memory cache on first load, then
pulls incrementally with the same newest-cached-time cursor.

While the app is foregrounded, both clients poll `GET /messages?since=` every
60 s (`InboxBloc.pollInterval`); newly discovered inbound messages surface as an
in-app arrival banner (suppressed for the thread currently on screen, and on the
very first hydration of an empty store — bootstrapping history is not an
arrival). Background timeliness remains FCM's job.

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

Local-only, per device: a conversation's unread count = messages in it not sent
by me with `createdAt` after my `lastReadAt`. Mobile stores markers in the
`ConversationRead` Isar collection; web stores them in browser localStorage
(`shared_preferences`, keys `msg_read_{conversationId}`). Not synced. Marking
read is idempotent (no write when the marker already covers the newest message).
Read receipts ("seen") are a documented future op (`read_marker`).

## Conversation context (client-side)

Threads are keyed to their marketplace context (`contextType:contextId`). The
clients resolve and display what a thread is about via
`ConversationContextResolver` (Isar lookups on mobile; single-entity GETs with
a session cache on web):

- **crop** → localized crop type + quantity ("Teff • 100 kg")
- **trip** → route ("Adama → Bishoftu")
- **order** → "Order • {crop}" from the order's denormalized `cropType`/`quantity`
- **delivery** → "Delivery • {crop}" chained through the delivery's `listingId`

Resolution is best-effort: unresolvable contexts fall back to a generic
localized type label (e.g. a trucker resolving a delivery's crop — the crop
GET is role-gated to farmer-own/buyer-any, and truckers rarely hold listings
locally). No wire change: messages still carry only `contextType`/`contextId`.

In the conversation screen's app bar the tag is tappable for crop/trip threads
and opens the linked `CropListingDetailScreen` / `TripDetailScreen`, loading
the full entity through `getListingById` / `getTripById` (added to the crop and
trip repositories on both platforms). Unreachable entities show a "details not
available" snackbar. Order/delivery tags stay label-only.

## Known limitations / deferrals

- Near-real-time in-app only (60 s poll while foregrounded; push is the background timeliness signal).
- Cross-device / web sent-message history relies on the `SenderIdCreatedAtIndex` pull.
- Tap-to-thread deep-linking from a push is deferred (no global navigator key yet); the notification still delivers and the inbox badge updates.
- Order/delivery-scoped chat is reachable from the inbox once started; dedicated "Message" buttons on the order/delivery detail screens are a follow-up (crop→farmer and trip→trucker entry points ship now).
