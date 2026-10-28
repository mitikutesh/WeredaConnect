# Notifications API Contract

## Version
1.3.0

## Parties
- **Mobile** (client): pulls the inbox, renders the entries and the Action-needed cards, receives the Android push, opens the order on tap
- **Web** (client): pulls and renders the inbox; no push
- **Backend** (server): derives one event per party from an order, delivery or escrow change (`NotificationRules`), writes the inbox row, renders and sends the push, serves `GET /notifications`
- **Infrastructure**: the three table streams, the `{prefix}-notifications` table, `OrderNotifyStreamFunction`, its dead-letter queue and alarms

## Overview

Every party to an order is told about every state and money change of it: the buyer, the
farmer and, on a trip order, the trucker. The server derives the events from the DynamoDB
streams of the orders, deliveries and escrows tables, writes one inbox row per recipient and
sends one Android push per recipient who has a device token.

The inbox is the source of truth. Push is additive: Android only, FCM through SNS, the same
device tokens and platform application as `push-alerts-api.md`. Web has no push and reads the
inbox alone. A party without a registered token, or offline when the push went out, sees the
same entry on the next pull. Design: `specs/architecture/push-notifications-architecture.md`.
Setup: `.ai/push-notifications-setup.md`. Ticket: ESC-21.

---

## 1. Read the inbox

**Method**: GET /notifications
**Authentication**: Required (Bearer, Cognito ID token). Any role. The query is scoped to the
caller; there is no way to read another user's inbox.

**Query parameters**:

| Parameter | Type | Meaning |
|---|---|---|
| `limit` | integer, 1 to 50, default 20 | Page size |
| `nextToken` | string, opaque | Continues the previous page; taken from the previous response |
| `since` | string, the `cursor` of an earlier response (ISO 8601 UTC, at most 64 characters) | Returns only the entries written after that time less a 2 minute overlap, newest write first (the client's incremental pull; the client drops ids it already has). To page it, send the same `since` with the `nextToken` it returned |

**Response (200)**:
```json
{
  "notifications": [
    {
      "notificationId": "2026-10-26T08:15:02Z#order_request#ord-7f3a",
      "type": "order_request",
      "entity": "order",
      "entityId": "ord-7f3a",
      "orderId": "ord-7f3a",
      "params": {
        "buyerName": "Abebe T.",
        "cropType": "teff",
        "quantityKg": "500",
        "amountEtb": "42500.00",
        "deadlineAt": "2026-10-27T08:15:02Z"
      },
      "createdAt": "2026-10-26T08:15:02Z"
    }
  ],
  "nextToken": "eyJ...",
  "cursor": "2026-10-26T09:00:00.0000000Z"
}
```

- Without `since`, `notifications` is newest event first (the inbox as shown). With `since`,
  it is newest write first. `notificationId` is the row's sort key
  `{createdAt}#{type}#{entityId}`: a stable id the client dedupes on, otherwise opaque.
- `nextToken` is present only when more entries exist.
- `cursor` is always present: the server time the read started. The client sends it back as
  `since` on its next pull. Rows reach the inbox out of event order (three streams in
  parallel, retries up to a day late), so the incremental pull goes by write time, never by
  `notificationId`.
- `type` is one of the types in section 3 (`NotificationTypes.All` on the server). A client
  that meets a type it does not know renders a generic "order update" line and still opens
  the order.
- `entity` is `order`, `delivery` or `escrow`: the table the change came from. `entityId` is
  the row's id in that table.
- `orderId` is present when the event is about a known order (always for `entity: order`;
  for a delivery or escrow when its row names one). The clients open `/orders/{orderId}` from it.
- `params` is a flat string map (section 4). Plain text, never a phone number.
- `createdAt` is the UTC time of the change that produced the entry (the stream record's
  time), ISO 8601.

Entries expire 90 days after `createdAt` (DynamoDB TTL on `expiresAt`). Read state (seen,
dismissed) is kept on the device, not the server: there is no write endpoint.

**Error codes**:
- `400 NOTIFICATION_VALIDATION`: `limit` not an integer (an integer outside 1 to 50 is clamped), a `since` that is not an ISO time or is over 64 characters, or a `nextToken` the server did not issue for this caller's inbox (tampered, truncated, another endpoint's or user's, or before the `since` sent with it)
- `401 NOTIFICATION_UNAUTHORIZED`: no authenticated user
- `500 NOTIFICATION_SERVER_ERROR`: storage failure

**Table**: `{prefix}-notifications`, PK `userId`, SK `sortKey` = `{createdAt}#{type}#{entityId}`,
TTL attribute `expiresAt` (epoch seconds). No GSI: the only read is the owner's own page. Local
index `WrittenAtIndex` (PK `userId`, SK `writtenAt`, the ISO time the row was first written,
all attributes) serves `since`, read strongly consistent.

**Client pull**: mobile pulls with `since` = the `cursor` it stored from its last pull, on app
open, on resume and when a push arrives, follows `nextToken` with the same `since`, drops ids it
already has, and stores the first page's `cursor`; web does the same from a session cache. A
first pull (no `since`) reads the newest page and stores its `cursor`; older entries come with
`nextToken` as the user scrolls.

---

## 2. Push payload (FCM via SNS)

Sent to each SNS endpoint registered for the recipient (`device-tokens`, `POST /device-tokens`
in `push-alerts-api.md`). The SNS message structure is the same `{"GCM": "<fcm json>"}`
envelope as the chat and match pushes. The FCM payload:

```json
{
  "notification": {
    "title": "New order: 500 kg teff",
    "body": "Abebe T. paid 42,500 ETB into escrow. Accept by 27 Oct 11:15.",
    "tag": "order-ord-7f3a"
  },
  "data": {
    "type": "order",
    "event": "order_request",
    "orderId": "ord-7f3a",
    "entityId": "ord-7f3a",
    "entity": "order",
    "notificationId": "2026-10-26T08:15:02Z#order_request#ord-7f3a"
  }
}
```

Rules:

- `title` and `body` are rendered on the server (`PushText`) in the recipient's
  `preferredLanguage`: `en`, `am` or `om`; any other or missing value renders `am`. Both exist
  for every type in `NotificationTypes.All` (parity-tested).
- A deadline in the text (`deadlineAt`, section 4) is Ethiopia time (UTC+3): the Gregorian day
  and month in the language, then the time of day (1.2.0, LT-04). `en` reads the 24-hour clock:
  `27 Oct 18:00`. `am` and `om` read the Ethiopian clock, always with the part of the day: `am`
  says it before the time (`27 ኦክቶ ከምሽቱ 12:00`), `om` after it (`27 Onk 12:00 galgala`). The
  rule is `specs/features/live-trip.md` section 9 and the expected times are
  `fixtures/ethiopian-clock-cases.json`, which the server and the app both test against. The
  server does not know the app's Clock setting, so the text follows `preferredLanguage` only.
- `tag` is `order-<orderId>`. The device collapses successive pushes about one order into the
  latest, so a party who was away sees one tray entry per order, not a stack. An event with no
  `orderId` (an escrow row from before order-funded escrows) sends no tag and no `orderId`; a
  tap on it opens the inbox.
- `data.type` is `order` for every type in this contract and `data.event` carries the type.
  Chat pushes are `data.type == "message"` (`messaging.md`); match alerts have no `type`
  (`push-alerts-api.md`). The client dispatches on `data.type`.
- Time to live: the SNS message attribute `AWS.SNS.MOBILE.FCM.TTL` is set per push. For an
  action-required type (section 3) it is the seconds left until the deadline, at least 60.
  `truck_nearby` lives one hour (3600): "the truck is 3 km away" is stale within the hour (1.3.0).
  Every other type lives 24 hours (86400). A phone that comes back online after the deadline
  never gets "accept by tonight" the next morning; the inbox entry is still there.
- A tap opens the order (`orderId`), from the background and after a cold start; the app reads an
  order the device has not pulled yet from `GET /orders/{orderId}` first. A push without `orderId`
  opens the app. The foreground handler pulls the inbox with `since` (and the order) instead of
  showing a tray entry, with one follow-up pull 3 seconds later.
- Inbox-only events send no push: `dispute_received` (the disputer's own receipt) and
  `under_review` (an escrow frozen for fraud review).
- Never a phone number in `title`, `body` or `data`. Names are the display names the order row
  already denormalizes.
- A push goes to at most 10 of the recipient's registered devices, and none once the invocation
  has under 5 seconds left (the inbox row is still written).

---

## 3. Event matrix

One row of the matrix is one stream record. `NotificationRules` reads the OLD and NEW images,
decides what changed, and emits at most one event per party. Two rules apply to every row:

1. **The actor is never told.** The party whose own action caused the change gets no entry for
   it: the farmer who accepts is not told the order was accepted, the buyer who cancels is not
   told it was cancelled. The order's `cancelledBy`, `farmerResponse` and `truckerResponse` and
   the escrow's `disputedBy` identify the actor. One deliberate exception: the buyer who
   confirmed the delivery still gets `delivered`, because that entry opens the buyer's own
   48 hour dispute window.
2. **One event per write.** A write that moves several things at once emits only the strongest
   row: a trucker accept that completes the agreement emits `order_confirmed`, not
   `farmer_accepted`; a farmer accept on a self-pickup order goes straight to `order_confirmed`.

The trucker column applies only to a trip order (`fulfillment: trucker`); a self-pickup order
has no trucker to tell. `-` means no entry. **Action required** names the party whose push gets
the deadline-bound time to live (section 2) and whose inbox entry renders as an Action-needed
card; the set is `NotificationTypes.ActionRequired`.

### Orders table (`MODIFY`: a status or marker change)

| Change | Buyer | Farmer | Trucker | Action required |
|---|---|---|---|---|
| `funded` (payment confirmed) | `payment_confirmed` | `order_request` | - | farmer, until `farmerDeadlineAt` |
| farmer accepted while `funded` (trip order; the trucker is now asked) | `farmer_accepted` | - | `transport_request` | trucker, until `truckerDeadlineAt` |
| `accepted` (every party committed) | `order_confirmed` | `order_confirmed`, unless the farmer's accept completed it (self-pickup, or the farmer-only accept with `COMMITMENTS_ENABLED` off) | `order_confirmed` (trip orders), unless the trucker's accept completed it | none; carries `pickupDate` |
| `rejected` | `order_rejected` | - | - | none |
| `awaiting_transport` | `transport_failed` (`reason` = `transportFailure`) | `transport_failed_keep_crops` if the farmer had accepted; `request_on_hold` if the farmer had not answered yet (the trip was full or gone right after funding, which closes the farmer's `order_request`) | `request_expired`, only when the trucker let the request expire | buyer, 48h (`buyerChoiceDeadlineAt`) |
| `cancelled` | `order_expired_unpaid` for an unpaid order the cron cancelled; otherwise `order_cancelled`, unless the buyer cancelled | `request_expired` when the farmer let the request expire; otherwise `order_cancelled` unless the farmer cancelled; paid orders only | `order_cancelled` when the trucker had been asked or had accepted and did not cancel | none |
| `payment_failed` | `payment_failed` | - | - | buyer |
| `pickup_confirmed` | `picked_up` | `handoff_recorded` | - | none |
| `in_transit` | `in_transit` | - | - | none |
| `delivered` | `delivered` | `delivered_payout_pending` | `delivered_payout_pending` | buyer, 48h to dispute, then rate |
| `disbursing` | - | - (the escrow tells the payees) | - | none |
| `settled` | `order_settled` | - (the escrow tells the payees) | - | none |
| reminder marker stamped (`farmerReminderAt`, `truckerReminderAt`, `buyerChoiceReminderAt`; 6h before the deadline) | `request_reminder` when the buyer's choice is due | `request_reminder` when the farmer's answer is due | `request_reminder` when the trucker's answer is due | the party whose answer is due, until its deadline |
| pickup overdue marker stamped (`pickupOverdueNotifiedAt`) | `pickup_overdue` | `pickup_missed` | `pickup_missed` | buyer |

### Deliveries table (`INSERT`, and the `MODIFY` that stamps `nearbyNotifiedAt`)

| Change | Buyer | Farmer | Trucker | Action required |
|---|---|---|---|---|
| trucker delivery created | `delivery_scheduled` | `delivery_scheduled` | - (the creator) | none |
| self-pickup delivery created | - (the creator) | `buyer_collecting` | - | none |
| truck nearby marker stamped (`nearbyNotifiedAt`, absent before the write and present after it; 1.3.0) | `truck_nearby` (`truckerName`, `distanceKm`, `minutes`) | - | - | none |

`truck_nearby` (LT-07, `live-trip-api.md` section 4): an accepted check-in put the truck within
5 km of the delivery's drop-off point while the delivery was `pickedUp` or `inTransit`. The
server stamps the delivery's server-only `nearbyNotifiedAt` (with `nearbyDistanceKm` and
`nearbyMinutes`) in a conditional write, once per delivery (`delivery-verification-api.md`
1.20.0), and this row derives the event from that stamp, so a check-in sent again, a later
check-in or a retried record never tells the buyer twice. The change is the server's, not the
buyer's, so the buyer is told. The event's `entity` is `delivery` and its `orderId` the
delivery's order, so the push carries `tag` `order-<orderId>`, `data.type` `order` and
`data.event` `truck_nearby`. Any other delivery update (a status move, a GPS write) emits
nothing here: the order's mirror tells the parties. The deliveries stream's event filter passes
`INSERT` and exactly this `MODIFY` to the function.

### Escrows table (`MODIFY`)

| Change | Buyer | Farmer | Trucker | Action required |
|---|---|---|---|---|
| `refunded` | `refund_sent` (`amountEtb`) | - | - | none |
| `released` | - | `payout_sent` (`amountEtb`) | `payout_blocked` when the trucker leg waits on identity or phone checks (`truckerPayoutPending`; `reason`) | trucker, when blocked |
| `completed` | - | - | `payout_sent` (`amountEtb`, the transport leg) | none |
| settlement blocked by the farmer's identity check (the sweep stamps the block on the escrow row; the `release_blocked_kyc` audit row alone is not streamed) | - | `payout_blocked` (`reason`) | - | farmer |
| manual payout queue opened (`manualPayout: open`) | - | `payout_processing` | `payout_processing` (transport leg) | none |
| `disputed` | `dispute_opened`; the disputer gets `dispute_received` instead (inbox only) | same | same, once a delivery is linked (`deliveryId`) | none |
| `dispute_resolved` | `dispute_resolved` (`resolution`) | `dispute_resolved` (`resolution`) | `dispute_resolved` (`resolution`), once a delivery is linked | none |
| `held` (fraud review) | `under_review` (inbox only) | `under_review` (inbox only) | `under_review` (inbox only), once a delivery is linked | none |

The escrow names the trip's trucker from funding on, before the trucker is asked; a status change
reaches the trucker only once a delivery (the trucker's own, after committing) is linked.

The matrix is the complete list of types: every constant in `NotificationTypes.All` appears
above, and nothing else is ever written. A new type is a version bump here first.

---

## 4. Params

`params` is a flat map of plain-text strings. The keys are `NotificationParams` on the server;
a type carries only the keys its text needs, and a missing key renders as nothing.

| Key | Meaning |
|---|---|
| `buyerName` | The buyer's display name as the order row denormalizes it. All three names are scrubbed before the row is written: a name with 7 or more numerals in total loses its numerals and phone separators, and control and format characters (a right-to-left override) are removed |
| `farmerName` | The farmer's display name |
| `truckerName` | The trucker's display name (trip orders) |
| `cropType` | The order's crop type code (`teff`, `wheat`, ...); clients localize it |
| `quantityKg` | The order quantity in kilograms, digits only |
| `amountEtb` | The amount moved or held, in ETB with two decimals, digits and a point only; clients format it |
| `deadlineAt` | ISO 8601 UTC; when the request closes or the window ends; clients show it in Ethiopia time (the clock per language: section 2) |
| `pickupDate` | ISO 8601 UTC; the agreed pickup date (the trip's departure on a trip order) |
| `reason` | A code, never free text: a `transportFailure`, `declineReason`, `cancelReason` or payout-block reason; clients localize it |
| `cancelledBy` | `buyer`, `farmer`, `trucker` or `system` |
| `resolution` | `farmer_wins` or `buyer_wins` on `dispute_resolved` |
| `party` | `buyer`, `farmer` or `trucker`: the counterpart the text names (who disputed, who dropped out) |
| `distanceKm` | `truck_nearby` (1.3.0): the truck's great circle distance to the drop-off point at the check-in, in km with one decimal (`3.2`), digits and a point only |
| `minutes` | `truck_nearby` (1.3.0): minutes from the check-in to the start of the arrival window (the track's `eta.from`, `live-trip-api.md` section 2), a whole number rounded to 5; absent when it rounds to 0 or is unknown. Never a clock time |

Clients render the inbox text from `type` + `params` with their own localization (ARB keys per
type in all three languages), so a language switch re-renders the whole history. The push text
is the server's rendering of the same `type` + `params` at send time and is not stored.

---

## 5. Delivery guarantees

- **At least once.** DynamoDB Streams deliver a record at least once and the Lambda may be
  retried. The inbox put is conditional on the key `{createdAt}#{type}#{entityId}` with
  `createdAt` = the stream record's time, which is stable across retries, so one change
  produces exactly one entry per recipient however often the record is replayed.
- **Push follows the put, repeats are harmless.** The push is sent whether or not the put was
  new; the device collapses repeats by `tag`. A failed publish to a stale endpoint disables
  that endpoint and never fails the record.
- **Poison records.** A record that still fails after three retries (four attempts) goes to the
  `{prefix}-notify-dlq` SQS queue and raises an alarm. The queue holds the shard and sequence
  metadata of the record, not the images: the runbook re-derives the event from the current
  table state within the stream's 24 hour retention. The event-source mapping bisects a
  failing batch and gives up on records older than one day.
- **Alarms.** `NotifyDlqMessages` (anything in the queue), `NotifyStreamIteratorAge` (the
  function is falling behind the streams) and `NotifyStreamErrors` (Lambda errors). All three
  notify the monitoring topic.
- **Log line.** The function logs one Information line per emitted event,
  `NOTIFY_EMITTED type=<type> entity=<entity> entityId=<id> recipient=<userId> push=<true|false> new=<true|false>`
  (`new=false` is a replay that found its row). It is the line the setup runbook looks for.
- **Order of arrival.** Entries are shown by `createdAt` but reach the inbox in processing
  order, which differs (three streams in parallel, retries, bisection). The incremental pull
  therefore reads by write time with a 2 minute overlap and the client dedupes by
  `notificationId`, so an entry written late is never skipped. A push may arrive before the
  client's pull sees the row (the table write and the publish are two calls), so the client
  pulls with `since` and a 3 second follow-up on a push, as the chat inbox does.
- **Batch failures stop the batch.** A record whose rules or inbox write fail is reported and
  the records after it in the batch wait for the retry (Lambda resumes from the first failed
  record), so they are not pushed twice. A record without `ApproximateCreationDateTime` fails
  rather than get a clock-based key.

---

## 6. Client rendering (1.1.0)

- The bell lists the inbox on mobile and web (mobile also lists the sync queue's failed and
  pending changes). Opening it marks every entry seen; the dot shows while one is unseen. Tapping
  an entry with an `orderId` opens the order. Web also routes `/orders/{orderId}`.
- Each type has a title and a body in en, am and om; the body places only the names (`buyerName`,
  `farmerName`, `truckerName`; a missing name reads as the role). A detail line under it shows the
  crop and quantity, the amount (`amountEtb`, formatted, with the currency word) and, for an
  action-required type, when it is due (`deadlineAt` in Ethiopia time). `payout_blocked` names what
  is needed from `reason` (Fayda, phone or profile), `dispute_resolved` who gets the money from
  `resolution`. A type the client does not know reads "Order update". `truck_nearby` (1.3.0)
  names the trucker and says the truck is less than 5 km away; it leaves out `distanceKm` and
  `minutes`, which only the push says, because the entry stays in the history and "in 25
  minutes" goes stale.
- Mobile keeps the newest 200 entries per user in Isar and the cursor in shared preferences; a pull
  with no entries on the device (first sign-in, or after a logout cleared them) ignores the stored
  cursor and reads the newest page. Web keeps the entries for the session and the read marker (the
  newest `createdAt` seen) in shared preferences; an entry an incremental pull brings after the bell
  was opened counts as unseen whatever its `createdAt` (rows arrive out of event order). Logout
  deletes the user's entries from Isar.
- One pull follows at most 5 pages and stores the first page's cursor. Entries beyond those pages
  (a phone offline for days) are left to history, as on a first pull: re-reading from the old
  cursor would fetch the same newest pages on every pull.
- A user's pulls are at least 30 seconds apart unless a push forces one (Home rebuilds the bell on
  every sync result); a failed pull does not hold the next one back. Web, which has no push, pulls
  every 2 minutes while Home is shown.

---

## Invariants

1. **Offline-first.** The inbox and the Action-needed cards work without push: a party without
   a token, without Play services or offline at send time sees the same entries on the next pull.
   Push never carries information the inbox lacks.
2. **No PII.** No phone number, email, Fayda number or address in an inbox row, a push title,
   body or data. Names are display names only.
3. **Role visibility unchanged.** An entry only ever describes an order the recipient is a party
   to (its buyer, farmer or trucker). A farmer never learns of another farmer's orders, a
   trucker of another trucker's trips. `GET /notifications` reads the caller's partition and
   nothing else.
4. **Language from the recipient's profile.** The push renders in the recipient's
   `preferredLanguage`, never the actor's or the device's. The inbox renders on the device.
5. **The actor is never notified** of a change their own action caused (section 3, with the
   buyer's `delivered` entry as the one exception).
6. **Exactly one entry per change and recipient**, however often the stream replays (section 5).
7. **Complete.** Every type in `NotificationTypes.All` has a row in section 3, push text in
   three languages and an inbox rendering on both clients.

---

## Changelog

| Version | Date | Change |
|---------|------|--------|
| 1.3.0 | 2026-10-28 | `truck_nearby` (LT-07, `live-trip-api.md` section 4): a new type for the buyer when a check-in puts the truck within 5 km of the drop-off, derived from the deliveries table `MODIFY` that stamps `nearbyNotifiedAt` (once per delivery, conditional write); params `truckerName`, `distanceKm` (one decimal) and `minutes` (to the start of the arrival window, rounded to 5, absent at 0); push text in en, am and om with no clock time; push time to live one hour; inbox text names the trucker without the minutes. The deliveries stream's filter also passes that `MODIFY`. |
| 1.2.0 | 2026-10-28 | Push text deadlines on the Ethiopian clock in `am` and `om` (LT-04, `live-trip.md` section 9): `27 ኦክቶ ከምሽቱ 12:00`, `27 Onk 12:00 galgala`; `en` unchanged (`27 Oct 18:00`). Section 2 states the format per language. No payload, param or endpoint change. |
| 1.1.0 | 2026-10-27 | Clients (ESC-21 PR 3, app build 3): the bell lists the inbox on mobile and web, a tap opens the order (push from the background or a cold start too), web route `/orders/{orderId}`, inbox text per type in three languages with a detail line, read state on the device, the 200-entry cap and the cursor rules (section 6). No server change. |
| 1.0.0 | 2026-10-26 | Initial: `GET /notifications` inbox, FCM push payload with `data.type: order`, the event matrix over the orders, deliveries and escrows streams, params, at-least-once delivery with the conditional inbox put, DLQ and alarms (ESC-21). |

---

**Last Updated**: 2026-10-28
**Status**: Draft
