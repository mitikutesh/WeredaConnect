# Contract: In-App Messaging (context-scoped chat)

**Status**: Implemented (2026-07-09). Requires deploy + the manual SNS Platform
Application step (see `.ai/push-notifications-setup.md`) for push delivery.

## Version
1.2.0

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

`senderName`, `recipientName`: denormalized authoritatively from the users
table (`GetUserByIdAsync`) so neither party can spoof identity. Plus
`version = 1`. `senderPhone` and `recipientPhone` are stripped from the client
data and, since 1.2.0 (ESC-48), never stored or returned: rows written before
still hold them, and no read answers them. Call/Copy takes the number from the
order (`order-lifecycle.md` 1.18.0), only while its contact is open.

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
3. Load the context and require both parties are legitimate, and the contact open (1.2.0, ESC-48):
   - **crop**, **trip**: closed to new messages from anyone, the owner included: `MESSAGE_CONTACT_LOCKED`. A thread before any order would let a buyer and a farmer or trucker agree to trade off the platform. Old threads stay readable.
   - **order**: sender and recipient are two distinct members of `{buyerId, farmerId, truckerId}`, and the order's contact is open: `accepted`, `pickup_confirmed`, `in_transit`, `delivered`, `disbursing`, `settled` or `disputed`, with no admin override refund flag. Otherwise `MESSAGE_CONTACT_LOCKED`.
   - **delivery**: sender and recipient are two distinct members of the delivery's `{buyerId, farmerId, truckerId}`, and the delivery's order (`orderId`) is open as above. A delivery without an order, or whose order is missing, is `MESSAGE_CONTACT_LOCKED`. An order read that fails is an unprefixed error (the client retries).
4. A refused create whose `messageId` this sender already stored (a resend after a lost answer, for example a crop message from before 1.2.0) answers `duplicate` instead, so the client keeps it.

A third party posting into someone else's conversation is rejected
(`MESSAGE_FORBIDDEN`, checked before the contact). `MESSAGE_CONTACT_LOCKED` is
permanent: the mobile client marks the operation rejected, removes its local
copy of the message and says why (`sync-protocol.md` 1.15.0); web shows the
same text when the send is refused. Read access (`GET /messages`) only returns messages the
caller sent or received.

---

## Read API (`MessageFunction`, authenticated)

- `GET /messages?conversationId=X` — a thread, newest-first, `nextToken` paginated. Results filtered to the caller's own messages.
- `GET /messages?since=T`: sync-down pull, all messages to/from the caller with `createdAt > T` (oldest-first), merged inbound ∪ outbound.
  - `T` must be one of: empty (no lower bound, the bootstrap full pull), a `yyyy-MM-dd` date, or a UTC `yyyy-MM-ddTHH:mm:ss[.fraction]Z` timestamp (seconds required, 1 to 7 fraction digits, uppercase `T` and `Z`). Nothing may come before or after it (no whitespace, no trailing newline), and it must be a real instant (`2026-02-30` is rejected).
  - The compare is a strict string `>` on the stored UTC `createdAt`, so the server passes `T` through unchanged. Offsets such as `+03:00` and zone-less date-times are rejected rather than silently shifting the window.
  - Any other value (epoch numbers, `10/01/2026`, `yesterday`, ...) gets `400 MESSAGE_VALIDATION`, and no query runs.
  - `since` is ignored when `conversationId` is present.
- `GET /messages/{id}` — a single message (403 if caller is not a party).

### Read API errors

| Status | Code | When |
|--------|------|------|
| 400 | `MESSAGE_VALIDATION` | neither `conversationId` nor `since` given, bad `limit`, bad `nextToken`, or an invalid `since` |
| 401 | `UNAUTHORIZED` | not authenticated |
| 403 | `MESSAGE_FORBIDDEN` | `GET /messages/{id}` for a message the caller did not send or receive |
| 404 | `MESSAGE_NOT_FOUND` | message does not exist |
| 500 | `MESSAGE_SERVER_ERROR` | unexpected server failure |

Mobile pulls incrementally using its newest local message time as the cursor
(stateless). Web hydrates a session-scoped in-memory cache on first load, then
pulls incrementally with the same newest-cached-time cursor.

While the app is foregrounded, both clients poll `GET /messages?since=` every
60 s (`InboxBloc.defaultPollInterval`); newly discovered inbound messages surface
as an in-app arrival banner (suppressed for the thread currently on screen, and
on the very first hydration of an empty store — bootstrapping history is not an
arrival). Background timeliness remains FCM's job.

The poll is lifecycle-aware and push-accelerated:

- **Foreground push (native)**: an FCM `onMessage` whose `data.type == "message"`
  dispatches an immediate poll (plus one 3 s follow-up — the recipient GSI is
  eventually consistent), so arrival is effectively instant while the app is
  open. Concurrent poll requests coalesce to at most an in-flight + one trailing
  pull.
- **Lifecycle**: `paused`/`hidden` stops the poll timer (backgrounded apps and
  hidden web tabs stop burning data); `resumed` restarts it with an immediate
  catch-up pull, throttled to skip when the last pull is <10 s old.
- **Sign-out** resets the inbox (timer stopped, per-user state dropped).

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

Contact (1.2.0, ESC-48): the conversation screen asks the context resolver for
the contact of the thread (uncached, the order status changes): crop and trip
threads are closed; an order thread follows the order's `contactOpen` (falling
back to its status when the order row has none); a delivery thread follows the
delivery's order. A closed thread is read-only with a short note that says when
contact opens or that it stays closed: "Contact opens after payment is held in
escrow" (crop and trip threads, an unpaid order), "Contact opens once the farmer
accepts the order" (a funded order), "Contact is closed for this conversation"
(cancelled, rejected, a decided dispute, an admin override refund). The Call
action shows only when the contact is open and the order carries the
counterpart's phone. An unresolvable context keeps the composer (the server
decides).

## Known limitations / deferrals

- Foreground arrival is push-instant on native once FCM is configured (falls back to the 60 s poll on non-GMS devices); web has no push — its floor is the 60 s poll plus the instant resume/tab-refocus catch-up.
- Cross-device / web sent-message history relies on the `SenderIdCreatedAtIndex` pull.
- Tap-to-thread deep-linking from a push is deferred (no global navigator key yet); the notification still delivers and the inbox badge updates.
- Order detail has a Contact section since 1.2.0 (ESC-48): each counterparty of an open order with Call, Copy and Message (order thread). Crop and trip detail no longer offer contact. Delivery detail has no contact entry; delivery threads started before stay reachable from the inbox.

## Changelog
- **1.2.0** (2026-10-17): Contact only after the money is held (ESC-48): crop and trip threads take no new message (`MESSAGE_CONTACT_LOCKED`, owners too); order and delivery threads only while the order's contact is open; a refused resend of a stored message is a `duplicate`; `senderPhone`/`recipientPhone` are no longer stored nor returned by any read; clients take the number from the order; order detail gets the Contact entry.
- **1.1.0** (2026-10-01): `GET /messages?since=` is validated. A value that is not empty, a `yyyy-MM-dd` date or a UTC `...Z` timestamp now gets `400 MESSAGE_VALIDATION` instead of a 200 with the wrong window (SIM-34). Both shipped clients already send `toUtc().toIso8601String()` or an empty cursor, so their pulls are unchanged.
- **1.0.0** (2026-07-09): Initial contract: create-only `message` entity over `/sync`, `GET /messages` reads, FCM push.
