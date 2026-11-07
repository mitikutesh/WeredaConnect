# Contract: In-App Messaging (context-scoped chat)

**Status**: Implemented (2026-07-09). Requires deploy + the manual SNS Platform
Application step (see `.ai/push-notifications-setup.md`) for push delivery.

## Version
1.4.0

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

### Read rule (1.3.0, FUP-124, `MessageFunction`)

On an `order` or `delivery` thread the order's trucker reads only while committed to the
transport: `truckerResponse: accepted` (a trip order from before ESC-15 counts as committed
from `accepted` on), `ContactReveal.MayReadThreads`. A trucker who declined, let the request
expire, dropped out (`truckerResponse: cancelled`, also when the trip was cancelled) or was
never asked reads none of that order's threads, nor of its deliveries' threads.

- A delivery thread follows the delivery's `orderId`. A delivery without an order, or an
  order or delivery that is gone, is closed to the trucker, as for a new message.
- The buyer and the farmer read their threads whatever the trucker did, also after the
  contact closed. A buyer's or a farmer's token (role claim) is not checked and costs no
  extra read; any other token (a trucker's, or one without the role claim) is checked
  against the order. An order's trucker is always a trucker (only truckers create trips).
- A committed trucker keeps reading once the order ends or its contact closes (`delivered`,
  `settled`, a decided dispute, an admin override refund), as the buyer and the farmer do.
  Sending follows the contact (above), so a closed order takes no new message from anyone.
- Crop and trip threads are unchanged.

### Admin read of a dispute (1.4.0, FRD-14)

The parties are not the only readers of one case: an admin in the admin group may read the order and
delivery threads of an order whose escrow has a dispute, to decide it, through
`GET /admin/disputes/{escrowId}/evidence?include=messages` (`escrow-protocol.md` 1.40.0). Only those
threads (every pair of the order's or the delivery's parties, `conversationId` as above), read only, at
most the newest 100 messages per thread, no phone (as for the parties). Every such read writes an
`admin_dispute_messages_read` transaction-audit row with the admin and the counts before anything is
answered; a read that cannot be audited answers no message. No other admin route returns messages, and
`GET /messages` is unchanged.

---

## Read API (`MessageFunction`, authenticated)

- `GET /messages?conversationId=X`: a thread, newest-first, `nextToken` paginated. Results filtered to the caller's own messages. A thread the caller may not read under the read rule (1.3.0) answers an empty page with no `nextToken`, as a non-participant gets.
- `GET /messages?since=T`: sync-down pull, all messages to/from the caller with `createdAt > T` (oldest-first), merged inbound ∪ outbound.
  - `T` must be one of: empty (no lower bound, the bootstrap full pull), a `yyyy-MM-dd` date, or a UTC `yyyy-MM-ddTHH:mm:ss[.fraction]Z` timestamp (seconds required, 1 to 7 fraction digits, uppercase `T` and `Z`). Nothing may come before or after it (no whitespace, no trailing newline), and it must be a real instant (`2026-02-30` is rejected).
  - The compare is a strict string `>` on the stored UTC `createdAt`, so the server passes `T` through unchanged. Offsets such as `+03:00` and zone-less date-times are rejected rather than silently shifting the window.
  - Any other value (epoch numbers, `10/01/2026`, `yesterday`, ...) gets `400 MESSAGE_VALIDATION`, and no query runs.
  - `since` is ignored when `conversationId` is present.
  - Since 1.3.0 the pull leaves out the order and delivery messages the caller may not read under the read rule. Each distinct order and delivery is read once per request (8 at a time).
- `GET /messages/{id}`: a single message (403 if caller is not a party, or since 1.3.0 may not read it under the read rule).
- An order or delivery read for the read rule that fails (or a Lambda without the orders or deliveries table) answers `500 MESSAGE_SERVER_ERROR`, never a partial result: a pull that dropped the messages would move the client's `since` cursor past them for good. The client retries on its next pull.

### Read API errors

| Status | Code | When |
|--------|------|------|
| 400 | `MESSAGE_VALIDATION` | neither `conversationId` nor `since` given, bad `limit`, bad `nextToken`, or an invalid `since` |
| 401 | `UNAUTHORIZED` | not authenticated |
| 403 | `MESSAGE_FORBIDDEN` | `GET /messages/{id}` for a message the caller did not send or receive, or (1.3.0) may not read under the read rule |
| 404 | `MESSAGE_NOT_FOUND` | message does not exist |
| 500 | `MESSAGE_SERVER_ERROR` | unexpected server failure, including a failed order or delivery read for the read rule (1.3.0) |

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

A tap (from the background, or the tap that cold-started the app) opens the
thread (FUP-90). The app opens it only when the signed-in user is the
recipient: `conversationId` must equal the deterministic id for
`contextType`, `contextId`, `senderId` and that user, who is not the sender.
A notification left for another account on a shared phone opens nothing. A
thread the device holds opens at once while the inbox pulls the new message;
a thread it holds nothing of is pulled first (bounded, 8 s), and when the
device still holds none of it (offline, or a thread the read rule closes) the
messages list opens instead. No wire change.

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
- Order detail has a Contact section since 1.2.0 (ESC-48): each counterparty of an open order with Call, Copy and Message (order thread). Crop and trip detail no longer offer contact. Delivery detail has no contact entry; delivery threads started before stay reachable from the inbox.
- The read rule (1.3.0) is server side only: a trucker's phone keeps the copies it pulled while committed, and web keeps them for the session. The server no longer serves them.

## Changelog
- **1.4.0** (2026-11-07): Admin read of a dispute (FRD-14): an admin in the admin group reads the order and delivery threads of a disputed order through `GET /admin/disputes/{escrowId}/evidence?include=messages` (`escrow-protocol.md` 1.40.0), read only, at most 100 per thread, every read audited (`admin_dispute_messages_read`) before it is answered. The parties' reads are unchanged.
- **1.3.0** (2026-11-06): Read rule (FUP-124): on order and delivery threads the order's trucker reads only while committed to the transport. A trucker who declined, let the request expire, dropped out or was never asked gets an empty thread page with no `nextToken`, loses those messages from the `since` pull and gets `403 MESSAGE_FORBIDDEN` by id. A failed order or delivery read answers `500 MESSAGE_SERVER_ERROR` instead of a partial pull. Buyer and farmer reads, a committed trucker's reads and crop and trip threads are unchanged. No client change.
- **1.2.0** (2026-10-17): Contact only after the money is held (ESC-48): crop and trip threads take no new message (`MESSAGE_CONTACT_LOCKED`, owners too); order and delivery threads only while the order's contact is open; a refused resend of a stored message is a `duplicate`; `senderPhone`/`recipientPhone` are no longer stored nor returned by any read; clients take the number from the order; order detail gets the Contact entry.
- **1.1.0** (2026-10-01): `GET /messages?since=` is validated. A value that is not empty, a `yyyy-MM-dd` date or a UTC `...Z` timestamp now gets `400 MESSAGE_VALIDATION` instead of a 200 with the wrong window (SIM-34). Both shipped clients already send `toUtc().toIso8601String()` or an empty cursor, so their pulls are unchanged.
- **1.0.0** (2026-07-09): Initial contract: create-only `message` entity over `/sync`, `GET /messages` reads, FCM push.
