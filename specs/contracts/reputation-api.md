# Reputation API Contract

## Version
1.7.0

## Status
PARTLY BUILT (1.7.0). BUILT in FUP-160 (1.7.0): the one-off owner stamp of section 7. BUILT in ESC-28: the events, the policy and the aggregate (section 1),
the server side of the two notifications (section 10) and the flags and policy parameter
(section 11). BUILT in ESC-29 (server): the reads (sections 2 to 4), the appeal photo and the appeal
(5, 6), the copied owner fields (7) and the gates (8). BUILT in ESC-32: the admin endpoints
(section 9) and the admin app's panel and appeals queue. Everything ships dark (all three flags off
everywhere), except the suspension gate of 8.4, which is not flagged. BUILT in ESC-30: the matching
signal (section 7, `match-api.md` 1.4.0). BUILT in ESC-33 (server): the fraud hooks of section 1.7. BUILT in ESC-31 (1.6.0): the app on the phone and the web (the cards of section 7, the
`until=` of 8.1, the inbox side of section 10, section 13). Each section names its ticket. Design: `specs/architecture/reputation-architecture.md`. Rules and decisions D1 to D9:
`specs/features/three-party-escrow-commitments-reputation.md` 4.11, 5.I and 14.

## Parties
- **Mobile** (client): shows the party's own reputation, events and appeal; shows buyers the
  public tag, effective rating and reliability on cards (from the copied owner fields) and asks
  for a confirm before ordering from a low-reputation party
- **Web** (client): the same screens, online only
- **Backend** (server): records events from the orders stream, applies the policy, keeps the
  aggregate on the users row, copies owner fields onto crop and trip rows, gates new commitments,
  serves the endpoints below
- **Admin**: the user reputation panel and the appeals queue
- **Infrastructure**: the `{prefix}-reputation-events` table and its two indexes, the stream
  Lambda, the daily job rule, the SSM policy parameter, the flags

---

## Overview

A farmer or trucker who declines, ignores, cancels or misses a paid order gets penalty points.
Points lower the rating buyers see, add a public "Low reputation" tag at 6 points and block new
commitments at 10 points for 14 days. Points expire after 90 days, completed orders earn them
back, and an admin can excuse them on appeal. Buyers have no reputation; a buyer who griefs
farmers and truckers gets a fraud signal instead (ESC-33, section 1.7).

Penalties come only from changes the server made to orders (the orders table stream). No party
can file a penalty against another, and no client sends a penalty, a point or a tier.

**Who sees what** (`CLAUDE.md` role visibility; spec 5.I8):

| Caller | Own reputation | A farmer's or trucker's reputation | A buyer's |
|---|---|---|---|
| Farmer | full detail: points, events, tier, restriction, appeal | never (`403`) | never |
| Trucker | full detail | never (`403`) | never |
| Buyer | none (buyers have no reputation) | the public summary only: tag, effective rating, rating count, reliability | never |
| Admin (admin pool `admin` group), only through the `/admin` routes of section 9 | | everything, including buyer ids on events | |

The public summary never shows points, events, reasons, appeals or restriction dates.

### Reading this contract

| Section | What | Ticket |
|---|---|---|
| 1 | Concepts: events, tiers, effective rating, reliability, restriction | ESC-28 (BUILT) |
| 2 to 4 | Party and buyer reads | ESC-29 (BUILT) |
| 5, 6 | Appeal photo and appeal | ESC-29 (BUILT) |
| 7 | Owner fields copied onto crop and trip rows | ESC-29 (copy, BUILT), ESC-30 (matching, BUILT) |
| 8 | Gates: `REPUTATION_RESTRICTED`, hidden restricted parties, suspension | ESC-29 (BUILT) |
| 9 | Admin endpoints | ESC-32 (BUILT) |
| 10 | Notifications `penalty_recorded`, `tier_changed` | ESC-28 (server, BUILT), ESC-31 (app, BUILT) |
| 11 | Feature flags | ESC-28 (BUILT) |
| 12 | Error codes | all |
| 13 | Offline and sync | ESC-31 (BUILT) |

Every response body is JSON with camelCase keys. The server omits a field that has no value
(it never sends `null`); clients treat a missing field as "not set". Timestamps are ISO 8601 UTC
with milliseconds (`2026-11-07T09:00:00.000Z`). Every endpoint needs a valid Cognito ID token
(`Authorization: Bearer`); the HTTP API answers `401` without one before the Lambda runs.
Sections 2 to 6 sit behind the app user pool's authorizer, so an admin pool token gets `401`
there; section 9 sits behind the admin pool's.

---

## 1. Concepts (ESC-28, BUILT)

Built in `Helpers/ReputationPolicy.cs` (the rules, pure), `Helpers/ReputationDerivation.cs` (the
events from the orders stream), `Services/ReputationService.cs` (record, refresh, the daily step),
`Functions/ReputationStreamFunction.cs` and the order-cron actions `reputation_daily`,
`reputation_unpark`, `reputation_recompute_all`, `reputation_backfill_ratings`. What is not
visible yet: nothing reads these values back to a client until ESC-29.

### 1.1 Event codes

An event is one row in the `reputation-events` table (architecture doc section 3). The stream
Lambda derives it from one order change, comparing the old and the new row. The points below are
the defaults (proposed values are marked); the live values come from the SSM policy parameter
(section 11).

| Code | Party | When (server-observed) | Points |
|---|---|---|---|
| `DECLINED_REQUEST` | farmer, trucker | declined a paid order (`farmerResponse` or `truckerResponse` became `declined`); for a trucker also a pending request closed by cancelling its trip (spec B9) | 1, with the griefing caps (1.3) |
| `RESPONSE_EXPIRED` | farmer, trucker | let a paid request expire (`farmerResponse` or `truckerResponse` became `expired`) | 1 when the party had a registered push device (D8), the request gave at least 12 hours to answer and `timeoutPenalties` is on; else 0; with the caps (1.3) |
| `CANCELLED_AFTER_ACCEPT` | farmer, trucker | farmer: cancelled an `accepted` order (`farmer_cancelled`, `faultParty: farmer`); trucker: dropped out of an accepted order (`truckerResponse: cancelled`, `transportFailure: trucker_cancelled`); more than 24 hours before `pickupDate` | 3 |
| `LATE_CANCEL` | farmer, trucker | the same, 24 hours or less before `pickupDate` | 4 |
| `TRIP_CANCELLED_WITH_BOOKINGS` | trucker | cancelled a trip that held at least one accepted order (`transportFailure: trip_cancelled` on an order whose `truckerResponse` was `accepted`); one event per trip | 3, or 4 when late |
| `NO_SHOW` (P3) | farmer, trucker | judged a no-show at pickup (`transportFailure: trucker_no_show`, or `cancelReason: farmer_no_show`; `delivery-verification-api.md` 2.0.0 P.3) | 5 |
| `DELIVERY_OVERDUE` (P3) | trucker | no delivery by T8 (the order moved to `disputed` with reason `delivery_overdue`) | 5, plus a restriction pending admin review |
| `CANCELLED_TRANSPORT_FAILED` | farmer | cancelled during `awaiting_transport` (`farmer_cancelled` from that status; the fault recorded when the transport failed stays on the order, spec C9) | 0 (recorded, never penalized) |
| `ORDER_COMPLETED` | farmer, trucker | the order reached `settled` | 0; counts for reliability and earn-back |

System rows that are not penalties live in the same table and never appear in a party's event
list: `RESTRICTION`, `ADMIN_TAG`, `ADMIN_UNTAG`, `TIER_CHANGED`, `HEAL` (architecture doc 3.2).

**Not penalized** (spec 4.11): a race the system caused (stock or trip capacity lost at funding:
`trip_full`, `listing_out_of_stock`), a farmer cancelling during `awaiting_transport`, a request
from a buyer later found fraudulent (admin excuse), anything the stream delivered while
`reputationPenalties` was off, and (proposed) a decline or expiry while the party was already
restricted, since the restriction blocked the accept (`zeroReason: restricted`), or suspended
(spec H1, `zeroReason: suspended`), and an expiry of a request that gave less than 12 hours to
answer (`zeroReason: short_window`; a trip close to departure can leave as little as 1 hour).

### 1.2 Tiers

| Tier | Active points | What it does |
|---|---|---|
| `good` | 0 to 2 | nothing |
| `warning` | 3 to 5 | a private warning to the party (`tier_changed` push); buyers see the same as `good` |
| `low` | 6 to 9, or an admin `tag` | buyers see the red "Low reputation" chip, the lowered rating and a confirm before ordering; lower in ranking |
| `restricted` | a restriction is active (1.5) | listings and trips hidden from browse and matching; new listings, trips and accepts refused (`REPUTATION_RESTRICTED`); existing orders continue |

An admin `untag` (section 9.4) hides the Low reputation tag until its `until`: the tier shows
as `warning` while points are 6 or more. It never lifts a restriction.

**Public tier** (what a buyer sees, proposed): `good` for `good` and `warning`, `low` for `low`
and `restricted`. A buyer meets a restricted party only through an order placed before the
restriction; "Low reputation" is accurate and reveals no dates.

### 1.3 Griefing caps (spec 4.11)

Applied when a `DECLINED_REQUEST` or `RESPONSE_EXPIRED` event is recorded, against the party's
events already recorded on **both sides** of its `occurredAt` (the stream delivers one party's
orders in any order, and the hourly cron expires many at once):
- at most 1 point per party and buyer pair in any 30 days: 0 when a counted event of the same
  pair lies within 30 days before or after (`zeroReason: pair_cap`);
- at most 6 points per party from these two codes in any 24 hours: 0 when some 24 hour window
  containing this event would pass 6 (`zeroReason: daily_cap`);
- (proposed) at most 9 active points from these two codes, one below the restriction line, so
  declines and expiries alone can bring the Low reputation tag but never a restriction
  (`zeroReason: request_cap`).

A partial point is never given: an event gets its full point or 0. The event is still recorded
with `points: 0`, so the party sees it and can tell why. The caps hold in any arrival order
(architecture doc 5.2).

### 1.4 Effective rating and reliability

- `ratingAverage` = `ratingSum / ratingCount` of the ratings the party received in their current
  role (`rating-api.md` 1.2.0). Absent when `ratingCount` is 0.
- `effectiveRating` = max(1.0, `ratingAverage` - min(1.0, 0.1 x `activePoints`)), rounded to
  one decimal (D3). Absent when `ratingCount` is 0. The raw average stays visible on the
  party's own profile with the deduction ("4.8 from buyers, 0.4 deducted for cancellations").
- `reliabilityPct` = `completedOrders90d` / (`completedOrders90d` + `faultCancels90d` +
  `noShows90d`), a whole percent rounded half up, absent while the denominator is below 3.
  Proposed counting: `faultCancels90d` counts `CANCELLED_AFTER_ACCEPT`, `LATE_CANCEL` and
  `TRIP_CANCELLED_WITH_BOOKINGS` (one per trip); `noShows90d` counts `NO_SHOW` and
  `DELIVERY_OVERDUE`; declines and expiries do not count (they are answers, not broken
  commitments); excused events never count.

### 1.5 Restriction

- **By points**: when a new penalty takes `activePoints` to 10 or more and no restriction is
  active, the party is restricted for 14 days (D9). Proposed: points added during an active
  restriction do not extend it; when it ends with points still at 10 or more, the tier is `low`
  until the next penalty, which restricts again. Only recording a penalty starts a restriction;
  a read or the daily job never does.
- **By `DELIVERY_OVERDUE`** (P3): restricted with no end date until an admin decides the review
  (section 9.3).
- **By an admin**: `restrict` with a reason and an end date (section 9.4).
- **Ends**: at its `until` (the daily job writes it; reads never show an ended restriction as
  active), by an admin `lift`, or (proposed) when an excuse takes the points of a points
  restriction below 10. Earn-back never lifts a restriction.
- **Repeat**: a second points restriction within 12 months writes the fraud signal
  `REPUTATION_REPEAT_RESTRICTION` (weight 0, review only), which puts the account in the Fraud
  Review queue, whose reject is the existing suspend (proposed; not built in ESC-33, a follow-up).

### 1.7 Fraud hooks (ESC-33, BUILT)

Server-internal: nothing here is on the wire, and no client reads a fraud signal, a risk score or
a payout class. Specified in `fraud-detection-architecture.md` 3.32.0 section 15.3.

| When | Effect | Flag |
|---|---|---|
| a `NO_SHOW` or `DELIVERY_OVERDUE` event is recorded with points above 0 | the same-named fraud signal on the penalised party (weight 15 and 25, hard), the order as the entity; once per event, never on a replay; the two together add at most 50 to a risk score | `reputationPenalties` (the events are produced by ESC-34 and ESC-36) |
| a `DECLINED_REQUEST` or `RESPONSE_EXPIRED` event is recorded, with or without points | when the order's buyer has `buyerDeclineSignalCount` (3) or more paid orders declined or expired among those created in the last `buyerDeclineSignalDays` (30) days, this one included, by at least 2 different farmers or truckers: the fraud signal `BUYER_DECLINE_PATTERN` on the buyer (weight 10, soft, at most 50 per buyer). One party declining one buyer repeatedly flags nobody. The party who declined gets no fraud signal | `reputationPenalties` |
| a delivery whose GPS looks inconsistent is confirmed (fraud Rule 4) | the confirming buyer's `GPS_ANOMALY` signal as before; in addition a capped row on the delivery's trucker (one per delivery, one per buyer and trucker, two per trucker in 30 days) | `reputationPenalties` (the buyer's row is not flagged) |
| a delivery is confirmed and escrow stamps the payout date | a payee whose tier is `low` waits like a new payee, a restricted payee (an open `DELIVERY_OVERDUE` review included) like a flagged one (`escrow-protocol.md` 2.10.0); `good` and `warning` change nothing | `reputationPenalties` |

A hook that fails is logged (`REPUTATION_FRAUD_HOOK_FAILED`) and never fails or delays the event.
With `reputationPenalties` off none of the four runs, and nothing in ESC-33 is live.

### 1.6 Expiry and earn-back

- Each penalty expires 90 days after it occurred (D9).
- Each `ORDER_COMPLETED` that qualifies removes 1 active point from the oldest active penalty
  (`earnedBack` + 1). It qualifies (proposed values) when the order total is at least 500 ETB,
  the party has had fewer than 2 earn-backs in the last 30 days, the order's buyer gave this
  party no earn-back in the last 90 days, and the order carries no `SELF_TRADE` or
  `SHARED_DEVICE_TRADE` fraud signal. With no active points nothing is banked.
- An order that went through a dispute ends at `dispute_resolved`, never `settled`
  (`order-lifecycle.md`), so it neither completes nor earns back (proposed).

---

## 2. GET /reputation/me (ESC-29)

The caller's own reputation. Farmers and truckers only.

**Auth**: farmer or trucker (the `custom:role` claim). A buyer gets `403 REPUTATION_FORBIDDEN`.

**Response (200)**:
```json
{
  "reputation": {
    "userId": "f-1a2b",
    "role": "farmer",
    "tier": "warning",
    "activePoints": 4,
    "thresholds": { "warning": 3, "low": 6, "restricted": 10 },
    "ratingAverage": 4.8,
    "ratingCount": 12,
    "ratingDeduction": 0.4,
    "effectiveRating": 4.4,
    "reliabilityPct": 83,
    "completedOrders90d": 10,
    "faultCancels90d": 1,
    "noShows90d": 1,
    "nextPointExpiryAt": "2027-01-20T09:00:00.000Z",
    "penaltiesEnabled": true,
    "asOf": "2026-11-07T09:00:00.000Z"
  }
}
```

| Field | Meaning |
|---|---|
| `tier` | the internal tier of 1.2, `warning` and `restricted` included |
| `activePoints` | sum of `points - earnedBack` over active, unexpired, unexcused penalties |
| `thresholds` | the live policy values, so the app can say "At 6 points buyers will see a Low reputation label" |
| `ratingDeduction` | `ratingAverage - effectiveRating`; absent with no ratings |
| `restriction` | present while restricted: `{ "since", "until"?, "cause" }`, `cause` one of `points`, `delivery_overdue`, `admin`; `until` absent while an admin review is open |
| `override` | present while an admin tag is in force: `{ "tag": "low" \| "clear", "until", "reason" }` (the admin's id is not shown) |
| `nextPointExpiryAt` | when the oldest active penalty expires; absent with no active points |
| `penaltiesEnabled` | the `reputationPenalties` flag; the app hides the points UI while it is off |
| `asOf` | server time of this answer; the app shows it with the cached copy (spec I6) |

The values are computed at read time from the stored aggregate. When `now` has passed the
aggregate's `nextChangeAt` (an expiry or a restriction end the daily job has not written yet),
the server recomputes from the events first (one Query), so a read never shows an expired point
or an ended restriction (architecture doc 6.6).

**Errors**: `403 REPUTATION_FORBIDDEN` (a buyer, or no role claim); `404 REPUTATION_NOT_FOUND`
(no users row, or a deleting or deleted account); `500 REPUTATION_SERVER_ERROR`.

**Flags**: not gated. With `reputationPenalties` off the answer shows the stored state (normally
0 points, `good`) and `penaltiesEnabled: false`.

**As built (1.2.0, `Functions/ReputationFunction.cs`, `ReputationService.ReadAsync`)**: the map does
not store the next expiry, so this read also Queries the events while `activePoints` is above 0 (one
Query; the app calls it at most every 30 minutes). `ratingAverage`, `ratingDeduction` and
`effectiveRating` are left out, and `ratingCount` is 0, for a row without `ratingsAggregatedAt` (a
farmer or trucker the rating backfill skipped, `rating-api.md` "Stored aggregate" step 4): its stored
sum holds only the ratings created since the deploy. With `reputationPenalties` off the policy
parameter is not read and the defaults give the thresholds.

---

## 3. GET /reputation/{userId} (ESC-29)

The public summary of a farmer or trucker: what buyers see on a detail screen. Cards do not call
it; they read the copied owner fields (section 7).

**Auth and visibility**:

| Caller | Target | Answer |
|---|---|---|
| buyer | a farmer or trucker | `200`, public summary |
| buyer | a buyer, a missing row, or a deleting or deleted account | `404 REPUTATION_NOT_FOUND` |
| farmer or trucker | themselves | `200`, public summary (how buyers see me) |
| farmer or trucker | anyone else | `403 REPUTATION_FORBIDDEN` |

Admins do not use this route (an admin pool token gets `401` from the app pool's authorizer);
they read section 9.1. `userId` `me` is section 2 (the static route wins).

**Response (200)**:
```json
{
  "reputation": {
    "userId": "f-1a2b",
    "role": "farmer",
    "tier": "low",
    "ratingAverage": 4.8,
    "ratingCount": 12,
    "effectiveRating": 4.2,
    "reliabilityPct": 83,
    "completedOrders90d": 10,
    "totalOrders90d": 12,
    "asOf": "2026-11-07T09:00:00.000Z"
  }
}
```

- `tier` is the public tier (`good` or `low`, 1.2).
- `totalOrders90d` = `completedOrders90d + faultCancels90d + noShows90d`, for
  "{completed} of {total} orders completed".
- With `reputationPublicTags` off: `tier`, `effectiveRating`, `reliabilityPct`,
  `completedOrders90d` and `totalOrders90d` are left out; `ratingAverage` and `ratingCount` stay
  (they exist today through `GET /ratings`).

**Errors**: as section 2, plus `403` for the cases in the table.

**As built (1.2.0)**: the `403` for a farmer or trucker asking about anyone else is decided from the
caller's claim before any read, so it never tells whether the id exists. The events are read only
when `now` has passed `nextChangeAt`. The rating fields follow the rule of section 2 for a row
without `ratingsAggregatedAt`.

---

## 4. GET /reputation/me/events (ESC-29)

The caller's penalty history, newest `occurredAt` first.

**Auth**: farmer or trucker; a buyer gets `403 REPUTATION_FORBIDDEN`.

**Query**:

| Parameter | Type | Meaning |
|---|---|---|
| `limit` | integer 1 to 100, default 50 | page size |
| `nextToken` | opaque, HMAC-signed | from the previous page |

**Response (200)**:
```json
{
  "events": [
    {
      "eventId": "CANCEL#ord-7f3a",
      "code": "LATE_CANCEL",
      "points": 4,
      "earnedBack": 1,
      "remainingPoints": 3,
      "status": "active",
      "occurredAt": "2026-11-01T06:00:00.000Z",
      "recordedAt": "2026-11-01T06:00:04.000Z",
      "pointsExpireAt": "2027-01-30T06:00:00.000Z",
      "orderId": "ord-7f3a",
      "appeal": { "status": "none", "canAppeal": true, "deadlineAt": "2026-11-08T06:00:04.000Z" }
    },
    {
      "eventId": "REQUEST#ord-91c0",
      "code": "RESPONSE_EXPIRED",
      "points": 0,
      "zeroReason": "not_reachable",
      "earnedBack": 0,
      "remainingPoints": 0,
      "status": "active",
      "occurredAt": "2026-10-20T12:00:00.000Z",
      "recordedAt": "2026-10-20T12:00:02.000Z",
      "pointsExpireAt": "2027-01-18T12:00:00.000Z",
      "orderId": "ord-91c0",
      "appeal": { "status": "none", "canAppeal": false }
    }
  ],
  "nextToken": "eyJ..."
}
```

| Field | Values |
|---|---|
| `code` | the penalty codes of 1.1, `CANCELLED_TRANSPORT_FAILED`, and `ORDER_COMPLETED` only when it earned a point back (proposed: keeps the list short on 3G) |
| `points` | after the caps; `basePoints` is admin-only |
| `zeroReason` | present when `points` is 0 on a penalty code: `suspended`, `restricted`, `short_window`, `pair_cap`, `daily_cap`, `request_cap`, `not_reachable`, `timeout_penalties_off`, `no_fault` |
| `status` | `active`, `expired`, `excused` |
| `tripId` | on `TRIP_CANCELLED_WITH_BOOKINGS` instead of `orderId` |
| `earnBackEventId` | on an `ORDER_COMPLETED` row: the penalty it took a point from |
| `appeal.status` | `none`, `pending`, `review` (admin review of `DELIVERY_OVERDUE`), `excused`, `upheld` |
| `appeal.canAppeal` | true while 6.1 allows an appeal |
| `appeal.deadlineAt` | `recordedAt` + 7 days, while `canAppeal` |
| `appeal.decidedAt`, `appeal.note` | after a decision; `note` is the admin's note to the party |

The buyer's id is never in a party's answer. Rows older than the retention (400 days,
architecture doc 3.1) are gone.

**Errors**: `400 REPUTATION_VALIDATION` (bad `limit` or `nextToken`); `403`, `404`, `500` as
section 2.

**As built (1.2.0)**: the table's sort key is the slot, not the time, so the server reads the
caller's rows once (one Query, up to the policy's `eventsReadCap`), sorts them by `occurredAt` then
`eventId`, newest first, and pages in memory. `nextToken` carries the last row's `occurredAt` and
`eventId` and the caller's id, signed like every other list token; a token of another caller is
`400`. `status` is computed at read time: an `active` row whose `pointsExpireAt` has passed reads
`expired` before the daily job writes it, with `remainingPoints` 0.

---

## 5. POST /reputation/appeals/photo-url (ESC-29)

A pre-signed S3 `PUT` URL for the optional appeal photo. Same rules as the delivery photos
(`delivery-verification-api.md` section 4).

**Auth**: farmer or trucker. **Flag**: `reputationPenalties` (off: `503 REPUTATION_UNAVAILABLE`).

**Request**:
```json
{ "sizeBytes": 148213, "contentType": "image/webp" }
```
`sizeBytes` 1 to 204800 (200 KiB); `contentType` `image/webp` (default) or `image/jpeg`.

**Response (200)**:
```json
{
  "uploadUrl": "https://...",
  "photoRef": "uploads/appeals/f-1a2b/5b0c9a7e-2d41-4c6b-8f0e-1d2c3b4a5e6f.webp",
  "expiresInSeconds": 900
}
```
The client `PUT`s the file with the signed `Content-Type` and `Content-Length` within 15 minutes,
then sends `photoRef` with the appeal.

**Errors**: `400 REPUTATION_VALIDATION`; `403 REPUTATION_FORBIDDEN` (a buyer); `403 AUTH_025`
(suspended, section 8.4); `410 ACCOUNT_004` (deleting account, `account-deletion.md`);
`503 REPUTATION_UNAVAILABLE`; `500 REPUTATION_SERVER_ERROR`.

**As built (1.2.0)**: checked in this order: the role (`403`), the flag (`503`), the caller's row
read strongly consistent (`404` with no row, `410`, `403 AUTH_025`), then the body. The file name is
a new UUID with `.webp` or `.jpg`. The Lambda may put and read objects only under
`uploads/appeals/`.

---

## 6. POST /reputation/events/{eventId}/appeal (ESC-29)

The party asks an admin to excuse one penalty.

**Path**: `eventId` URL-encoded (`CANCEL%23ord-7f3a`); the server decodes it once, as for the
manual payout ref (`escrow-protocol.md`). The event is looked up under the caller's own id, so
nobody can appeal another user's event.

**Auth**: farmer or trucker. **Flag**: `reputationPenalties` (off: `503 REPUTATION_UNAVAILABLE`).

**Request**:
```json
{ "text": "The road to Kombolcha was closed by the flood, I called the buyer.", "photoRef": "uploads/appeals/f-1a2b/5b0c....webp" }
```

| Field | Rule |
|---|---|
| `text` | required, 1 to 500 characters after trimming (proposed) |
| `photoRef` | optional; must start with `uploads/appeals/{callerId}/` and the object must exist (HeadObject) |

### 6.1 When an appeal is allowed

- the event is a penalty code with `points` above 0 (`DECLINED_REQUEST`, `RESPONSE_EXPIRED`,
  `CANCELLED_AFTER_ACCEPT`, `LATE_CANCEL`, `TRIP_CANCELLED_WITH_BOOKINGS`, `NO_SHOW`,
  `DELIVERY_OVERDUE`);
- its `status` is `active`;
- the server received the appeal within 7 days of `recordedAt` (server clock; proposed:
  `recordedAt`, not `occurredAt`, so a party who could not see the event earlier is not cut short);
- the event has no appeal text yet. A `DELIVERY_OVERDUE` event is already in admin review; an
  appeal on it adds the party's text and photo to that review (its `appealStatus` stays
  `review`).
- **After an admin decision (1.4.0, ESC-32)**: an appeal is the party's one chance to be heard. An
  event an admin upheld before the party sent any text (a `DELIVERY_OVERDUE` review decided on the
  admin's own reading) can still be appealed once, inside the same 7 day window: the appeal goes to
  the queue as `pending` and takes a new decision (9.3). An event that already carries the party's
  text never reopens, whatever was decided, and an excused event is never appealable (its status is
  no longer `active`).

**Response (200)**:
```json
{
  "appeal": { "status": "pending", "appealedAt": "2026-11-02T08:00:00.000Z", "hasPhoto": true },
  "event": { "eventId": "CANCEL#ord-7f3a", "code": "LATE_CANCEL", "points": 4, "status": "active", "appeal": { "status": "pending", "canAppeal": false } },
  "alreadyAppealed": false
}
```

**Idempotency**: one appeal per event (conditional write on the event row). The same request
sent again (same `text`, same `photoRef`) answers `200` with `alreadyAppealed: true` and writes
nothing; a different text on an appealed event is `409 REPUTATION_APPEAL_EXISTS`.

**Errors**: `400 REPUTATION_VALIDATION` (text, photoRef); `403 REPUTATION_FORBIDDEN` (a buyer);
`404 REPUTATION_EVENT_NOT_FOUND` (no such event under the caller, or a system row);
`409 REPUTATION_APPEAL_CLOSED` (the window passed, the event expired or was excused, or the code
cannot be appealed); `409 REPUTATION_APPEAL_EXISTS`; `403 AUTH_025`; `410 ACCOUNT_004`;
`503 REPUTATION_UNAVAILABLE`; `500 REPUTATION_SERVER_ERROR`.

**Audit**: transaction-audit row `reputation_appealed` (entity `user`, the event id as metadata).

**As built (1.2.0)**: the same order of refusals as section 5. `photoRef` must be
`uploads/appeals/{callerId}/` plus one file name of letters, digits, `-` and `.` (at most 64
characters); anything else is `400` before any read. The write is one conditional `UpdateItem` on
the caller's event row (the row exists, is `active` and has no `appealText`); it changes no counted
attribute, so neither the row's nor the users map's `version` moves. A write that loses its
condition reads the row again and answers `alreadyAppealed`, `REPUTATION_APPEAL_EXISTS` or
`REPUTATION_APPEAL_CLOSED` from what is stored. The row keeps when the text arrived in
`appealTextAt`; that is the answer's `appealedAt`. On a `DELIVERY_OVERDUE` row the stored
`appealStatus: review` and `appealedAt` (the review queue's sort key) stay as they were. The appeal
text is never logged and never written to the audit row.

---

## 7. Owner fields on crop and trip rows (ESC-29)

When a party's public reputation changes, their active listings and trips get a copy, so cards,
match results and the offline Isar cache need no extra call (spec 4.11, "Copied onto listings and
trips"). This retires the per-farmer `GET /ratings` call on browse and the per-trucker call on
the crop detail screen.

| Field (stored and returned) | Type | Meaning |
|---|---|---|
| `ownerTier` | string | stored: the internal tier (`good`, `warning`, `low`, `restricted`); returned: the public tier (`good`, `low`) |
| `ownerEffectiveRating` | number, 1 decimal | absent with no ratings |
| `ownerRatingCount` | integer | |
| `ownerReliabilityPct` | integer 0 to 100 | absent below 3 orders |
| `ownerReputationAt` | ISO 8601 | when the copy was written; the app shows "As of {date}" (spec I6) |

- **Rows**: crops with `status` `available` or `reserved` (the owner's `FarmerIdCreatedAtIndex`),
  trips with `status` `open` or `matched` (`TruckerIdCreatedAtIndex`).
- **Written**: by the server only, never taken from a client (dropped from create and update
  payloads like the other server fields). Stamped at create from the owner's users row (the
  `/sync` batch already reads it, ACC-02). Refreshed when the owner's public tier, effective
  rating (at one decimal), rating count or reliability changes. The copy does not bump the row's
  `version` and does not change `updatedAt`, so an owner's offline edit never conflicts with it.
  An owner's own update never writes these fields.
- **Returned**: on `GET /crops`, `GET /crops/{id}`, `GET /trips`, `GET /trips/{id}`, the match
  results and the sync conflict echo, to every caller that may read the row. With
  `reputationPublicTags` off all five are left out of read models (proposed); the stored copy
  keeps updating, so turning the flag on shows current values at once.
- **Contracts**: ESC-29 adds the fields to `crop-listing-api.md` and `trip-listing-api.md`,
  ESC-31 to the Isar schemas and web models (`data-models.md`).
- **Matching** (ESC-30): `ownerReliabilityPct / 100` replaces `farmerReliability` in
  `MatchScoring` (C#) and `MatchScorer` (Dart), with the neutral 0.7 when absent; trucker
  reliability ranks the trip suggestions. `farmerReliability` stops being written.
  **As built (1.3.0, `match-api.md` 1.4.0)**: the score uses the field as the caller is given it,
  so with `reputationPublicTags` off both scorers use the neutral 0.7 for every listing even where
  a copy is stored; the trip suggestions order equal prices by the trucker's reliability (price
  stays the first key); the push alert for a new listing scores with the neutral value; the apps
  store `ownerReliabilityPct` on the cached crop and trip rows (Isar and the web models), the
  other four owner fields come with ESC-31.

**As built in the apps (1.5.0, ESC-31, `data-models.md` 1.12.0)**:
- All five fields are on the cached crop and trip rows (Isar schemas and web models), read by one
  parser (`core/models/owner_reputation_wire.dart`) in the marketplace pull, the web crop and trip
  repositories, the web matcher and the sync conflict echo. Each is nullable; a row without them
  parses as before and a later row without them clears the stored copy.
- A card keys on the presence of the copy (`ownerTier` or `ownerReputationAt`), not on a client
  flag: with `reputationPublicTags` off nothing changes on any screen. `ownerReliabilityPct` alone
  (stored since ESC-30) does not count as a copy.
- With the copy: the listing card, the crop detail, the buyer's trip card, the suggested trip, the
  match card and the Place order transport line show `ownerEffectiveRating` with
  `ownerRatingCount` (nothing while the count is 0) and make no `GET /ratings` call. The crop
  detail also shows `ownerReliabilityPct` and "As of {date}" from `ownerReputationAt`. Without the
  copy the screens that read `GET /ratings` before still do, so no rating disappears.
- The red "Low reputation" label shows for the public tier `low` (a `restricted` that ever reached
  a client reads as low too; no other tier name is shown), to buyers only. A farmer on My Crops or
  their own listing, and a trucker on their own trips, see their rating without the label; their
  standing is on My reputation.
- Before an order is placed from a party with the label, the buyer gets one confirm naming the
  farmer, the trucker or both, with Back and Order anyway. It reads the cached row, so it works
  offline; 8.2 still refuses a restricted party on the server.

**As built (1.2.0, `Data/ReputationCopyRepository.cs`, `Helpers/OwnerReputationView.cs`)**:
- The stamp at create and the refresh run only while `reputationPenalties` is on: the reputation
  writes that keep the copy current run only then. With the flag off no row gets the fields.
- Refresh: after an event whose commit changed the stored tier (the internal one, so `low` to
  `restricted` counts), the effective rating or the reliability; after every rating create (the
  count changed); and in the daily job for a user with a `HEAL#` marker, before the marker is
  deleted, so a failed copy keeps the marker and the next run tries again. A failure after an event
  or a rating logs `REPUTATION_COPY_FAILED` and is left to the marker.
- One refresh looks at the owner's newest 200 rows (`FarmerIdCreatedAtIndex` plus one
  `BatchGetItem` per 100 for the status, or `TruckerIdCreatedAtIndex`), writes only rows in an
  active status that do not already hold the values, and conditions each `UpdateItem` on the row
  still being active. No Scan.
- A row created before the flag went on has no copy until its owner's next change. **1.7.0
  (FUP-160)**: the order-cron action `reputation_stamp_owner_copies`, run by hand once per
  environment after `reputationPenalties` is on and before `reputationPublicTags` goes on
  (architecture doc 12.3), stamps them: the owners of the active rows come from the crop
  `StatusCreatedAtIndex` (`available`, `reserved`) and the trip `StatusDepartureDateIndex` (`open`,
  `matched`), so an owner without a `woreda` is reached too; each owner's rows get the refresh
  above with the reputation as a read shows it (the values a create stamps). The users row is not
  written. With `reputationPenalties` off it reads and writes nothing (`outcome: disabled`). A run
  out of time returns `next` for `{"startAt": next}`; a rerun writes nothing. No wire change.
- The five names are dropped from every client payload on `/sync`, create and update alike.
- Returned by `GET /crops`, `GET /crops/{id}`, `GET /trips`, `GET /trips/{id}`, `GET /match`
  (`match-api.md` 1.3.0) and a crop or trip row in a `/sync` answer, as the public tier, only with
  `reputationPublicTags` on.

---

## 8. Gates (ESC-29)

### 8.1 `REPUTATION_RESTRICTED` on the party's own commitments

Behind `reputationPenalties`. A restricted caller is refused, permanently under the
`sync-protocol.md` 1.17.0 rule (the client reverts the change):

| Operation | Refused | Still allowed |
|---|---|---|
| `crop_listing` create | yes | update, delete |
| `trip` create | yes | update, cancel (penalized when it has bookings) |
| `order` `farmerResponse: accept`, `truckerResponse: accept` | yes | `decline` (0 points while restricted, 1.1), `cancelCommitted` |

Message (sync op `error`):
```
REPUTATION_RESTRICTED: until=2026-11-21T09:00:00.000Z: Your account can't take new orders until 21 Nov 2026. Your current orders continue.
```
`until=review` while an admin review is open. Clients that parse only the prefix keep working;
ESC-31 reads `until` for `errorReputationRestricted`.

**As built in the apps (1.5.0, ESC-31)**: a refusal with a date reads "can't take new orders,
listings or trips until {date}" (`errorReputationRestrictedUntil`, en, am, om; the Ethiopia
calendar day), on the phone's sync rejection and on the web create and accept screens.
`until=review`, the `counterparty` form, the no-build wording and a date that does not parse keep
the text from before (`errorReputationRestricted`, `errorCounterpartyRestricted`).

**As built (1.2.0, `Helpers/ReputationGate.cs`)**: the gate reads the restriction stored on the
caller's row (the batch's one strongly consistent read) and its `until`; a restriction whose
`until` has passed never refuses, whether or not the daily job has run. The accept gate runs after
the no-op answer, so a replayed accept from before the restriction still succeeds. To a client that
sends no readable `X-App-Build` (build 1 or older, which may not know the code, TASK FUP-75) the
same sentence goes out under a code every client treats as permanent: `SYNC_VALIDATION:` on a crop
or trip, `ORDER_VALIDATION:` on an order.

### 8.2 `REPUTATION_RESTRICTED` on the counterparty

An `order` create whose listing farmer or trip trucker is restricted or suspended (8.4) is
refused (spec A7):
```
REPUTATION_RESTRICTED: counterparty: This farmer or trucker can't take new orders right now.
```
The same applies to the buyer's transport choice naming a restricted trucker's trip
(`sync-protocol.md` 1.40.0, Transport choice, ESC-19). The server reads the owner's users row
(one GetItem; the copied `ownerTier` may be stale in the buyer's cache). A failed read refuses
the create with the unprefixed retryable text `The seller's account could not be checked. Please
try again later.` and writes nothing.

**As built (1.2.0)**: behind `reputationPenalties` like 8.1, the suspended owner included (flag
off: no owner read, no refusal; a suspended owner's payout is still held by the settlement gate).
The read is strongly consistent. An order id the server already holds is not refused here: the
create is a replay and gets its `conflict` answer, so a client never drops an order that exists.
The no-build wording of 8.1 applies (`ORDER_VALIDATION:`). The transport choice is not built
(ESC-19) and has no gate yet.

### 8.3 Hidden restricted parties

Behind `reputationPenalties`. A buyer's browse (`GET /crops`, `GET /trips`), the match
candidates and the trip suggestions leave out rows with stored `ownerTier: restricted`. On the
reads that use an `ALL`-projection index or the scan fallback this is a FilterExpression; on the
crop read through `CropTypeCreatedAtIndex`, whose `INCLUDE` projection has no `ownerTier`, it is a
check in code after the existing `BatchGetItem` (architecture doc 10). The crop browse already
refills short pages. The owner's own list, `GET /crops/{id}` and `GET /trips/{id}` still return
them, so existing orders keep working. Admin browse is not filtered (proposed).

This is a moderation filter, like the one that already drops deleted listings and departed
trips. The rule that a buyer sees all crops and all trips from every farmer and trucker stands:
only listings the platform has taken off the market are left out, and they come back when the
restriction ends.

**As built (1.2.0)**: one check in code on the full rows for every read path (not a
FilterExpression: the rows are read either way and the crop browse refills the page), in
`CropFunction`, `TripFunction` and `MatchFunction`, only for a caller whose role is `buyer`. A
farmer's or trucker's own list, a read by id and an admin's browse are never filtered. The trip
suggestions are the buyer's trip browse.

### 8.4 Suspension blocks marketplace actions

Not flag-gated (it closes spec R5 and FRD-08's gap: today a suspended user's ID token keeps
working on `/sync` until it expires, up to an hour). The write endpoints that already read the
caller's row for the ACC-02 deletion guard refuse a row with `suspended: true`, at no extra
read: `POST /sync` (whole request `403` with `{"error":{"code":"AUTH_025","message":"Account
suspended."}}`, no operation applied, no idempotency key recorded), `POST /escrow/*`,
`POST /user/fayda-start`, `POST /user/fayda-link`, `POST /device-tokens` and the two POSTs of
sections 5 and 6. Reads are left alone (the token dies within the hour). A suspended owner counts
as restricted for buyers: their copied `ownerTier` becomes `restricted` (the suspend action
refreshes the copy) and 8.2 refuses orders. ESC-29 bumps `sync-protocol.md` and `auth-api.md`
and checks that both clients end the session on `AUTH_025` from `/sync`.

**As built (1.2.0)**:
- `POST /sync`: `403 AUTH_025` for the whole request from the read the deletion guard already
  made; a deleting account still gets its `410` first.
- `POST /escrow/initiate`, `/escrow/order-checkout` and `/escrow/dispute` (the three that read the
  caller's row), `POST /user/fayda-start`, `POST /user/fayda-link`, `POST /device-tokens` and the
  two POSTs of sections 5 and 6. `POST /escrow/release` and `/escrow/refund` read no caller row
  and stay as they were (a suspended buyer can still confirm a delivery so the farmer is paid; the
  payout itself is held by the settlement gate).
- A suspended owner reads as `restricted` in every aggregate. An admin `suspend` or `unsuspend`
  leaves the day's `HEAL#` marker (only while `reputationPenalties` is on) and, since 1.4.0
  (ESC-32, TASK FUP-163), recomputes the aggregate and rewrites the copied owner fields in the same
  request, after the sign-in service was updated (ending the user's sessions never waits for it).
  When that refresh fails the suspension still stands, the failure is logged
  (`REPUTATION_COPY_FAILED`) and the daily job rewrites the copies from the marker within a day;
  until a copy lands 8.2 refuses orders from the live row.
- Clients (app build with ESC-29): a `403 AUTH_025` from `/sync` or any other route outside
  `/auth/` ends the session like a rejected refresh; the sync queue stays for the next sign-in, and
  the login screen shows the existing "account disabled" text (`authAccountDisabled`, en, am, om)
  on mobile and web. On the `/auth/` routes it stays the sign-in screen's answer. Older builds keep the queue without
  burning a retry and are signed out when their token refresh is refused.

---

## 9. Admin endpoints (ESC-32, BUILT)

**Auth**: admin pool ID token in the `admin` group (`AdminAuthHelper.IsInAdminGroup`); anyone
else `403 ADMIN_001`. Every write is audited: one transaction-audit row (entity `user`, the user
id, action as named below, the reason or note as metadata) and an `AuditLogger` line. Writes are
not gated by `reputationPublicTags`; they need `reputationPenalties` (off:
`503 REPUTATION_UNAVAILABLE`). The two reads work whatever the flags say, so an admin can inspect
the stored state before the rollout.

**As built (1.4.0, `Functions/AdminFunction.Reputation.cs`, `Services/ReputationService.Admin.cs`)**:
- The four routes sit on the admin pool's authorizer and the Lambda checks the group again. A
  token with only a `role` claim (which passes the Lambda's general admin check) gets `403`.
- **One transaction per write** (`TransactWriteItems`): the row updates, the new system rows and
  the `reputation` map under its own `version`, plus a `TIER#` row when the tier changed and the
  day's `HEAL#` marker when a copied field changed. Each row update carries its own condition (the
  status read, the row version read, and for an uphold the appeal status and `appealTextAt` read).
  A failed condition writes nothing; the request reads again and answers from what is stored, so
  two admins acting at once apply a change once: the loser gets the replay answer or the refusal.
- **Audit**: one transaction-audit row per applied write, never for a replay: `fromState` and
  `toState` are `tier={tier};points={activePoints}` before and after, `metadata` is
  `event={eventId};note={note}` for a decision and `reason={reason};until={until}` for an action
  (`until` left out when there is none), followed by what else changed, since a tag, an untag or a
  lift can leave the tier and the points as they were: `tag={low|clear};override={row id}` and
  `replaced={row id}` per override replaced; `restriction={row id}` for a restrict;
  `lifted={row id};cause={cause};was={its end, or review}` per restriction lifted; for a decision
  `lifted={row id}` per restriction it lifted and `restriction={row id};restrictionUntil={end}`
  when an uphold gave a review's restriction its end. A body `until` that applied to nothing is
  not recorded. The actor is the admin's id. The appeal text is never written to the audit row or
  a log.
- **Copies**: when the write changed the tier, the effective rating or the reliability, the owner
  fields on the user's active listings are rewritten in the same request (section 7). A copy that
  fails is logged (`REPUTATION_COPY_FAILED`), does not fail the action, and is retried by the
  daily job through the heal marker.
- A deleting or deleted account answers `404 REPUTATION_NOT_FOUND` on all three user routes.

### 9.1 GET /admin/users/{userId}/reputation

The full view: section 2's fields plus `override.by`, every restriction (current and past 12
months), and the events (all codes, system rows included, `basePoints`, `buyerId`, the appeal text
and a pre-signed `appealPhotoUrl` valid 10 minutes). Query `limit` (1 to 100, default 50) and
`nextToken` page the events.

Errors: `404 REPUTATION_NOT_FOUND` (no row; a buyer answers `200` with no events and `role:
buyer`); `500 REPUTATION_SERVER_ERROR`.

**As built (1.4.0)**. The response:
```json
{
  "reputation": { "userId": "t-77aa", "role": "trucker", "tier": "restricted", "activePoints": 12,
                  "override": { "tag": "low", "until": "...", "reason": "...", "by": "admin-1" }, "...": "section 2" },
  "restrictions": [
    { "eventId": "RESTRICTION#2026-11-07T09:00:00.000Z", "cause": "admin", "status": "lifted",
      "since": "...", "until": "...", "reason": "...", "by": "admin-1", "liftedAt": "...", "liftedBy": "admin-2" }
  ],
  "events": [
    { "eventId": "CANCEL#ord-7f3a", "code": "LATE_CANCEL", "party": "trucker", "points": 4, "basePoints": 4,
      "earnedBack": 0, "remainingPoints": 4, "status": "active", "occurredAt": "...", "recordedAt": "...",
      "pointsExpireAt": "...", "orderId": "ord-7f3a", "buyerId": "b-19",
      "appeal": { "status": "pending", "canAppeal": false, "text": "...", "appealedAt": "...", "textAt": "...", "hasPhoto": true,
                  "appealPhotoUrl": "https://...", "decidedAt": "...", "decidedBy": "admin-1", "note": "..." } }
  ],
  "nextToken": "eyJ..."
}
```
- `reputation` is always computed from the user's rows (one strongly consistent Query), never
  from the stored map alone.
- `restrictions` holds every `RESTRICTION` row the table still has (rows live 400 days), newest
  `since` first, not paged. `status` is `active`, `ended` or `lifted`; an active row whose `until`
  has passed reads `ended` before the daily job writes it.
- `events` are the user's rows newest `occurredAt` first, paged in memory like section 4 with a
  signed token bound to the user id in the path. Every code is listed, system rows included
  (`RESTRICTION`, `ADMIN_TAG`, `ADMIN_UNTAG`, `TIER_CHANGED`, with their `cause`, `since`,
  `until`, `tag`, `reason`, `by`, `fromTier`, `toTier`, `restrictedUntil`, `liftedAt`,
  `liftedBy`), except the `HEAL` markers, which are the daily job's bookkeeping. `appeal` is
  present on order events only.
- `appeal.textAt` is when the party's text arrived, absent while there is none. The admin app
  sends it back with an uphold (9.3, `seenAppealTextAt`).
- `appealPhotoUrl` is a pre-signed `GET` valid 10 minutes, signed only for a key under
  `uploads/appeals/{that user's id}/` with the file name shape of section 6; any other stored
  value is not signed. The admin Lambda may read objects only under `uploads/photos/` and
  `uploads/appeals/`.

### 9.2 GET /admin/reputation/appeals

The queue, oldest first, from `AppealStatusIndex`.

| Parameter | Meaning |
|---|---|
| `status` | `pending` (default, party appeals) or `review` (`DELIVERY_OVERDUE` reviews) |
| `limit`, `nextToken` | 1 to 100, default 50 |

```json
{
  "appeals": [
    {
      "userId": "t-77aa",
      "role": "trucker",
      "displayName": "Kebede A.",
      "eventId": "CANCEL#ord-7f3a",
      "code": "LATE_CANCEL",
      "points": 4,
      "occurredAt": "2026-11-01T06:00:00.000Z",
      "orderId": "ord-7f3a",
      "appealStatus": "pending",
      "appealedAt": "2026-11-02T08:00:00.000Z",
      "text": "The road to Kombolcha was closed by the flood.",
      "hasPhoto": true
    }
  ],
  "nextToken": "eyJ..."
}
```

**As built (1.4.0)**: one Query of the `status` partition of `AppealStatusIndex`, ascending, with
`Limit` (no Scan, no filter), then one `BatchGetItem` for the page's display names and roles.
`nextToken` is the index key, signed; a token of the other status is `400 REPUTATION_VALIDATION`.
Each item also carries `tripId` in place of `orderId` on a `TRIP_CANCELLED_WITH_BOOKINGS` event,
`appealTextAt` (when the party's text arrived, absent while there is none; sent back with an
uphold, 9.3) and, when `hasPhoto`, `appealPhotoUrl` (the 10 minute pre-signed `GET` of 9.1), so
the queue shows the photo without a second call. The index is eventually consistent: a row decided a moment ago
can still be listed, and deciding it again answers the replay or the refusal of 9.3.

### 9.3 POST /admin/users/{userId}/reputation/events/{eventId}/decision

Excuse or uphold one event. The user id is in the path because the table key is
`(userId, eventId)` (the spec wrote `/admin/reputation/events/{eventId}/decision`; proposed
change).

```json
{ "decision": "excuse", "note": "Flood closed the road; confirmed with the buyer." }
```

| `decision` | Allowed on | Effect |
|---|---|---|
| `excuse` | any penalty event with `status: active`, appealed or not (spec: "excuse any event") | `status: excused`, its points leave every count, the appeal (if any) closes as `excused`; a points restriction lifts when points fall below 10; a `DELIVERY_OVERDUE` review's restriction lifts |
| `uphold` | an event with `appealStatus` `pending` or `review` | the appeal closes as `upheld`; a `DELIVERY_OVERDUE` review's restriction gets `until` = now + 14 days (or the body's `until`, at most 365 days ahead) |

`note`: required, 3 to 500 characters; the party sees it. Audit actions
`reputation_excused`, `reputation_upheld`.

Idempotency: the same decision again answers `200` with `alreadyDecided: true`; the other
decision on a decided event is `409 REPUTATION_EVENT_DECIDED`.

Response: `{ "event": { ...admin event shape... }, "reputation": { ...section 2 shape... }, "alreadyDecided": false }`.

Errors: `400 REPUTATION_VALIDATION`; `404 REPUTATION_EVENT_NOT_FOUND`;
`409 REPUTATION_EVENT_DECIDED`; `409 REPUTATION_APPEAL_CLOSED` (`uphold` with no open appeal);
`409 REPUTATION_APPEAL_CHANGED` (1.4.0: `uphold` on an appeal that is not the one the admin's page
showed); `503 REPUTATION_UNAVAILABLE`; `500 REPUTATION_SERVER_ERROR`.

**An uphold is made on the appeal the admin read (1.4.0)**. An uphold spends the party's one
appeal for good (no second appeal, no reversal), so the server must know what the admin saw:

```json
{ "decision": "uphold", "note": "The buyer confirmed the cancel.", "seenAppealTextAt": "2026-11-02T08:00:00.000Z" }
```

| Field | Rule |
|---|---|
| `seenAppealTextAt` | for `uphold`: the `appeal.textAt` (9.1) or `appealTextAt` (9.2) of the event as the admin's page showed it; left out when the page showed no text. Ignored on `excuse` |

When the event's stored `appealTextAt` differs from `seenAppealTextAt` (a text arrived, or the
page is older than the text), the answer is `409 REPUTATION_APPEAL_CHANGED`, nothing is written
and the server does not retry: the admin reloads, reads the appeal and decides again. The case:
a `DELIVERY_OVERDUE` review listed with no text, the trucker sends their appeal, the admin clicks
Uphold on the page loaded before it. The same answer is given when the text lands between the
server's own read and its write (the write is conditioned on `appealTextAt`). The admin app closes
its dialog, reloads the list and says so. `excuse` has no such guard: it only removes a penalty,
so deciding it on an older view cannot cost the party anything.

**As built (1.4.0)**:
- `eventId` in the path is URL-encoded and decoded once, as in section 6. A system row
  (`RESTRICTION#`, `OVERRIDE#`, `TIER#`, `HEAL#`) is `404 REPUTATION_EVENT_NOT_FOUND`.
- `excuse` needs a penalty code whose points still count: stored `active` and not past
  `pointsExpireAt`. An expired event, `CANCELLED_TRANSPORT_FAILED` and `ORDER_COMPLETED` are
  `409 REPUTATION_APPEAL_CLOSED`. A penalty with 0 points can be excused (it leaves the counts).
- `uphold` on an expired event with an open appeal is allowed: it only closes the appeal.
- "Decided" for the replay rule means the stored decision with no open appeal. `excuse` on an
  excused event and `uphold` on an upheld event answer `200`, `alreadyDecided: true`, with the
  stored note (the new note is ignored) and no audit row. `excuse` on an upheld event and `uphold`
  on an excused event are `409 REPUTATION_EVENT_DECIDED`. An upheld event the party then appealed
  (6.1, "After an admin decision") has an open appeal again and takes either decision.
- A client that sends no `seenAppealTextAt` can uphold only an appeal with no party text (an
  untouched `DELIVERY_OVERDUE` review); a value longer than 40 characters is `400`.
- The restriction of a `DELIVERY_OVERDUE` review is the `RESTRICTION` row written with the event
  (`since` equal to the event's `recordedAt`). `uphold` gives it `until` (the body's, or now plus
  `restrictionDays`); a body `until` in the past or more than `adminRestrictMaxDays` ahead is
  `400`. `until` in the body of an `excuse` is `400`; on an uphold of any other event it is ignored
  (and not written to the audit row).
  `excuse` lifts that restriction, and lifts every points restriction in force when the active
  points fall below `restrictAt`. A lifted row keeps `liftedAt` and `liftedBy`.
- The event row keeps `appealDecision` (`excused` or `upheld`), `decidedAt`, `decidedBy` and
  `decisionNote`, and leaves `AppealStatusIndex`; an excused row also leaves `DueIndex`.
- The party is not notified of a decision by itself (only through `tier_changed` when the tier
  moved); the note shows in their event list (TASK FUP-195).

### 9.4 POST /admin/users/{userId}/reputation

```json
{ "action": "restrict", "reason": "Repeated no-shows reported by three buyers.", "until": "2026-12-07T00:00:00.000Z" }
```

| `action` | `until` | Effect | Audit action |
|---|---|---|---|
| `tag` | required, at most 90 days ahead (proposed) | `override = {tag: low}`: Low reputation shown whatever the points | `reputation_tagged` |
| `untag` | required, at most 90 days ahead | `override = {tag: clear}`: the Low reputation tag is not shown (computed or admin) | `reputation_untagged` |
| `restrict` | required, at most 365 days ahead (proposed) | restriction with `cause: admin`; does not count toward the repeat rule (proposed) | `reputation_restricted` |
| `lift` | not taken | ends the active restriction now, any cause | `reputation_lifted` |

The latest `tag` or `untag` replaces the previous override. `reason` is required, 3 to 500
characters. The target must be a farmer or trucker. Each action writes a system row (history),
recomputes the aggregate, refreshes the copied owner fields and, on a tier change, notifies the
party.

Response: `{ "reputation": { ...section 2 shape plus override.by... }, "alreadyApplied": false }`.

Errors: `400 REPUTATION_VALIDATION` (action, reason, until missing or out of range, a buyer
target); `404 REPUTATION_NOT_FOUND`; `409 REPUTATION_NOT_RESTRICTED` (`lift` with no active
restriction); `409 REPUTATION_ALREADY_RESTRICTED` (1.4.0: `restrict` while a restriction is in
force); `503 REPUTATION_UNAVAILABLE`; `500 REPUTATION_SERVER_ERROR`.

**As built (1.4.0)**:
- `until` must be after the server's now and at most `tagMaxDays` (tag, untag) or
  `adminRestrictMaxDays` (restrict) ahead, both from the policy parameter. `lift` ignores `until`.
- **Replay**: the same `tag`, `untag` or `restrict` again (same `until`, same `reason`) while it
  is in force answers `200` with `alreadyApplied: true` and writes nothing (no second row, no
  audit row). A different `tag` or `untag` replaces the previous override, whose row becomes
  `replaced`.
- **`restrict` while any restriction is in force** (points, a review, or another admin's) is
  `409 REPUTATION_ALREADY_RESTRICTED`: nothing is stacked or silently extended. To change one,
  `lift` first, then `restrict`.
- `lift` ends every restriction row in force (status `lifted`, `liftedAt`, `liftedBy`). The
  points stay, so the tier after a lift is whatever the points give. A `lift` of a
  `DELIVERY_OVERDUE` review's restriction leaves the review open in the queue.
- System rows: `OVERRIDE#{at}` (`ADMIN_TAG` or `ADMIN_UNTAG`, with `tag`, `until`, `reason`,
  `by`) and `RESTRICTION#{at}` (`cause: admin`, `since`, `until`, `reason`, `by`), both on
  `DueIndex` at their `until` so the daily job ends them.
- A suspended target can be tagged, restricted and lifted; its tier stays `restricted` while the
  suspension lasts.

---

## 10. Notifications (ESC-28 server BUILT, ESC-31 app)

Two new inbox and push types for the party (`notifications-api.md`; ESC-28 bumps it). They come
from the `reputation-events` table stream into `OrderNotifyStreamFunction`, like the order events
(architecture doc 9). Behind `reputationPenalties`.

| Type | When | `params` | Opens |
|---|---|---|---|
| `penalty_recorded` | a penalty row with `points` above 0 is recorded | `eventId`, `code`, `points`, `activePoints` | My reputation |
| `tier_changed` | the tier changed (a `TIER_CHANGED` row) | `fromTier`, `toTier`, `restrictedUntil` when `toTier` is `restricted` | My reputation |

`tier_changed` to `warning` is the private warning of spec 4.11. Push text in en, am and om
(`Helpers/PushText.cs`; am and om await native review). As built: a `TIER_CHANGED` row is written
whenever the tier stored on the users row changes (a penalty, the daily job, a refresh), and
`restrictedUntil` is absent while a `DELIVERY_OVERDUE` review is open (`notifications-api.md`
1.5.0).

**As built in the apps (1.5.0, ESC-31, `notifications-api.md` 1.5.1)**: both types have their own
inbox title and body in en, am and om (`penalty_recorded` names the event and its points;
`tier_changed` says what the new tier means in the words of My reputation), and tapping the inbox
entry opens My reputation on the phone and the web. A tapped push still opens the app, not My
reputation (TASK FUP-205).

---

## 11. Feature flags and policy (ESC-28, BUILT)

Flags follow the environment config pattern of `commitmentsEnabled`
(`infra/lib/config/environments.ts`, an env var on each Lambda that needs it; spec K3 named SSM,
proposed change). All three default to off.

| Flag (config / env var) | On | Off |
|---|---|---|
| `reputationPenalties` / `REPUTATION_PENALTIES_ENABLED` | the stream records events; the daily job runs; gates 8.1 to 8.3; appeal and admin writes | the stream acks without writing (no retroactive points, spec 4.11); gates pass; reads show the stored state |
| `timeoutPenalties` / `REPUTATION_TIMEOUT_PENALTIES_ENABLED` | `RESPONSE_EXPIRED` carries its point (when reachable) | `RESPONSE_EXPIRED` recorded with 0 points, `zeroReason: timeout_penalties_off` |
| `reputationPublicTags` / `REPUTATION_PUBLIC_TAGS_ENABLED` | buyers get tier, effective rating and reliability (sections 3, 7; `rating-api.md` 1.2.0) | those fields are left out of buyer-facing answers |

`reputationPenalties` is turned on only where `commitmentsEnabled` is on: the events read the
commitment fields. The P3 codes (`NO_SHOW`, `DELIVERY_OVERDUE`) also need `pickupClassification`
(ESC-34 to ESC-36).

Policy numbers (points, caps, thresholds, windows) live in one SSM String parameter
`/{prefix}/reputation/policy` (JSON), read once per Lambda container; a missing or out-of-range
value keeps its default and logs `REPUTATION_POLICY_INVALID` (architecture doc 5.9).

As built: `reputationPenalties`, `timeoutPenalties` and (1.2.0, ESC-29) `reputationPublicTags` are
in `infra/lib/config/environments.ts`, `false` in dev, staging and prod. `reputationPenalties` also
reaches the crops, trips, match, ratings, reputation and admin Lambdas (8.3, sections 5 and 6, the
heal marker of 8.4, and since 1.4.0 the admin writes of section 9, with the policy parameter); `reputationPublicTags` the crops, trips, match, ratings, reputation and sync
Lambdas. With
`reputationPenalties` off nothing reads the policy parameter, the rating create still keeps the
stored rating sum and count (`rating-api.md` 1.3.0) and the backfill still runs.

---

## 12. Error codes

| Code | HTTP | Meaning |
|---|---|---|
| `REPUTATION_VALIDATION` | 400 | bad query, body or path value |
| `REPUTATION_FORBIDDEN` | 403 | the caller may not read this reputation (role visibility), or a buyer called a party-only endpoint |
| `REPUTATION_NOT_FOUND` | 404 | no such farmer or trucker (missing row, a buyer target for a buyer, a deleting or deleted account) |
| `REPUTATION_EVENT_NOT_FOUND` | 404 | no such event under that user |
| `REPUTATION_APPEAL_CLOSED` | 409 | the appeal window passed, the event is not active or not appealable, or no appeal is open to uphold |
| `REPUTATION_APPEAL_EXISTS` | 409 | the event already has a different appeal |
| `REPUTATION_EVENT_DECIDED` | 409 | the event was already decided the other way |
| `REPUTATION_APPEAL_CHANGED` | 409 | `uphold` on an appeal that is not the one the admin's page showed (1.4.0): reload and decide again |
| `REPUTATION_NOT_RESTRICTED` | 409 | `lift` with no active restriction |
| `REPUTATION_ALREADY_RESTRICTED` | 409 | `restrict` while a restriction is in force (1.4.0) |
| `REPUTATION_UNAVAILABLE` | 503 | `reputationPenalties` is off, or the table is not configured |
| `REPUTATION_SERVER_ERROR` | 500 | unexpected failure |
| `REPUTATION_RESTRICTED:` | 200 (sync op `error`) | section 8.1 and 8.2; permanent (`sync-protocol.md` 1.17.0) |
| `AUTH_025` | 403 | suspended caller (8.4) |
| `ACCOUNT_004` | 410 | deleting or deleted account (`account-deletion.md`) |
| `ADMIN_001` | 403 | not in the admin group (section 9) |

Error body: `{"error":{"code":"REPUTATION_FORBIDDEN","message":"..."}}`, as every REST error.
`REPUTATION_*` codes are strings, not numbered, so parallel tickets cannot collide.

---

## 13. Offline and sync (ESC-31)

- **Own reputation**: `GET /reputation/me` at start-up and on pull-to-refresh, at most every 30
  minutes (spec 8), cached on the device with its `asOf`; offline the app shows the cache with
  "As of {date}". Events load when the My reputation screen opens and are cached the same way.
- **Buyers**: tags, effective ratings and reliability arrive inside the cached crop and trip rows
  (section 7) with `ownerReputationAt`; no extra call. The server re-checks restriction at order
  create (8.2), so a stale cache never places an order with a restricted party.
- **Appeals**: online requests. The phone keeps an unsent appeal as a local draft and sends it
  when online; the server judges the 7 day window by its own clock. Web needs a connection and
  says so.
- **Sync queue**: `REPUTATION_RESTRICTED` is already a permanent rejection with its own text
  (`sync-protocol.md` 1.17.0). A whole-batch `403 AUTH_025` (8.4) ends the session; the queue
  stays for the next sign-in.
- **Data use**: about 60 bytes of owner fields per card; the per-card rating GETs go away.

**As built (1.5.0, ESC-31, `features/reputation/`)**: one repository for the phone and the web
(HTTP plus a copy in the app's key-value store, SharedPreferences, so no Isar schema and no
platform pair).
- **When it is read**: `GET /reputation/me` when a farmer or trucker opens Profile or My
  reputation, at most every 30 minutes; pull-to-refresh on My reputation forces it. It is not read
  at start-up (no call for a user who never opens Profile). A buyer never calls it.
- **Offline**: the last answer and the first page of events are kept per user and shown with "You're
  offline. Showing what was saved on {date}"; with nothing saved the screen says to connect.
  Logout and account deletion remove the user's copy and drafts.
- **Events**: pages of 20 (`limit=20`), "Show more" for the next; only the first page is kept.
  With `penaltiesEnabled: false` the screen shows the rating only and asks for no events.
- **Appeal**: offered where `appeal.canAppeal` is true (the server's rule, so an event decided
  without a party text can still be appealed once). Text up to 500 characters and an optional
  JPEG or WebP photo up to 200 KiB through section 5, then section 6, both online. The text is
  kept on the device as a draft until an appeal is sent, on the phone and the web; the user sends
  it, the app does not send a draft by itself when the connection returns (TASK FUP-206).
  `REPUTATION_APPEAL_CLOSED`, `REPUTATION_EVENT_NOT_FOUND`, `REPUTATION_APPEAL_EXISTS` and
  `REPUTATION_UNAVAILABLE` have their own texts.
- **Profile**: the Rating tile shows a farmer's or trucker's own `ratingAverage` (the raw buyers'
  average, as My reputation does). When the answer carries no rating (a new user, or a rated user
  whose row has no `ratingsAggregatedAt` yet, section 2 "As built"), the app asks the user's own
  `GET /ratings?rateeId={self}&limit=1` aggregate and shows that. This read treats any failure
  (offline, a timeout, a 5xx, a 429, an answer without a readable `summary`) as a failure, never
  as "no ratings". Only an answer the server gave is saved, and asked again after 30 minutes; the
  neutral "None yet" is shown only when the server answered `summary.count: 0`. When the read
  fails, or the reputation answer is the offline copy, nothing is saved: a saved rating still
  shows as the last known one, a saved 0 older than 30 minutes does not, and otherwise the tile
  keeps the dash. When `/reputation/me` cannot be read and nothing is saved, the same aggregate
  is tried and shown only when it has ratings, else the dash. A buyer is never rated: the dash as
  before, no call. The My reputation row shows for a farmer or trucker only while
  `penaltiesEnabled` is true.
- **Errors**: an appeal refused with `REPUTATION_VALIDATION`, `AUTH_025` or `ACCOUNT_004`, and a
  load the server answered with an error, read as the generic error text, never as a connection
  problem. After `REPUTATION_APPEAL_CLOSED` or `REPUTATION_APPEAL_EXISTS` the kept draft is removed.
  A retry after a failed appeal reuses the photo already uploaded.

---

## 14. Invariants

1. Points come only from order changes the server made (the orders stream) or an admin action;
   no client field sets a point, a tier or a restriction.
2. One event per (user, slot): `REQUEST#{orderId}`, `CANCEL#{orderId}`, `TRIP_CANCEL#{tripId}`,
   `NOSHOW#{orderId}`, `OVERDUE#{orderId}`, `COMPLETED#{orderId}`. A replayed stream record never
   counts twice.
3. The aggregate on the users row is written in the same transaction as the event that changes
   it, conditioned on the aggregate's own `version`; it is never written by the profile update
   path and never bumps the row's `version`.
4. Farmers and truckers read only their own reputation; buyers read only the public summary of
   farmers and truckers; nobody reads a buyer's.
5. A restriction blocks new commitments only. Existing orders continue.
6. Every admin change is audited with the admin's id and a reason.
7. Reads never show an expired point, an ended restriction or an ended override, whether or not
   the daily job has run.

---

## Changelog

| Version | Date | Change |
|---|---|---|
| 1.7.0 | 2026-11-12 | FUP-160 BUILT (server, no endpoint or field change): the one-off stamp of the owner fields of section 7 on rows created before `reputationPenalties` went on, the order-cron action `reputation_stamp_owner_copies` (owners from the active partitions of the crop and trip status indexes, so owners without a `woreda` too; the reputation as read; behind `reputationPenalties`; a `startAt` cursor; a rerun writes nothing). Run it before `reputationPublicTags` goes on (architecture doc 12.1 step 6, 12.3). |
| 1.6.0 | 2026-11-12 | ESC-31 BUILT (apps, no endpoint or field change): the owner fields on the cached crop and trip rows and on the cards (label for the public tier `low`, effective rating with its count, no per-card `GET /ratings` when the copy is present), the confirm before ordering from a party with the label, `until=` in the restricted error, My reputation (own standing, rating breakdown, events in pages of 20, appeal with optional photo and a kept draft), the Profile rating, the inbox texts and tap for `penalty_recorded` and `tier_changed`. "As built" notes under sections 7, 8.1, 10 and 13 (the read happens on Profile or My reputation, not at start-up; a draft is not sent by itself; a tapped push still opens the app). |
| 1.5.0 | 2026-11-12 | ESC-33 BUILT (server): new section 1.7, the fraud hooks. A recorded `NO_SHOW` or `DELIVERY_OVERDUE` with points writes a fraud signal on the party; a recorded decline or expiry checks the buyer's paid orders and writes `BUYER_DECLINE_PATTERN` at 3 in 30 days from at least 2 different parties; the payout delay reads the tier (`low` as new, restricted as flagged); a GPS-inconsistent delivery also puts a capped `GPS_ANOMALY` row on the trucker. All behind `reputationPenalties`; no wire change. The repeat restriction signal of 1.5 is not built. (1.3.0 and 1.4.0 are other tickets' versions.) |
| 1.4.0 | 2026-11-12 | ESC-32 BUILT: section 9 on the admin Lambda (`GET /admin/users/{userId}/reputation`, `GET /admin/reputation/appeals`, `POST /admin/users/{userId}/reputation/events/{eventId}/decision`, `POST /admin/users/{userId}/reputation`), admin group only, writes behind `reputationPenalties` (reads not gated), one transaction and one audit row per applied write, the copied owner fields refreshed in the request. New: the response shape of 9.1 (`restrictions`, the admin event with `basePoints`, `buyerId`, the appeal text and `appealPhotoUrl`), `appealPhotoUrl` and `tripId` on the queue items, `alreadyApplied` on 9.4, `409 REPUTATION_ALREADY_RESTRICTED`, `seenAppealTextAt` on an uphold with `409 REPUTATION_APPEAL_CHANGED` (an uphold is refused when the appeal is not the one the admin's page showed), `textAt` and `appealTextAt` on the reads, what changed in the audit metadata, the replay rules of 9.3 and 9.4, and the rule of 6.1 that an event upheld before the party sent any text can be appealed once. 8.4: an admin suspend or unsuspend now refreshes the copied owner fields in the same request (TASK FUP-163). (1.3.0 is taken by an open branch) |
| 1.3.0 | 2026-11-12 | ESC-30 BUILT: the matching signal of section 7. `ownerReliabilityPct / 100` is the reliability input of `MatchScoring` and `MatchScorer` (neutral 0.7 when the caller gets no value), the trip suggestions order equal prices by the trucker's reliability, `farmerReliability` is no longer computed or stored (`match-api.md` 1.4.0, `crop-listing-api.md` 1.12.0, `sync-protocol.md` 1.50.0). No endpoint or field change here. |
| 1.2.0 | 2026-11-11 | ESC-29 BUILT (server): `GET /reputation/me`, `GET /reputation/{userId}`, `GET /reputation/me/events`, `POST /reputation/appeals/photo-url`, `POST /reputation/events/{eventId}/appeal` on the new `{prefix}-reputation` Lambda; the owner fields stamped at create and copied on change; `REPUTATION_RESTRICTED` on crop create, trip create, accept and on an order to a restricted or suspended owner; restricted owners left out of a buyer's browse and match; the third flag `reputationPublicTags`; all off everywhere. Not flagged: `403 AUTH_025` for a suspended caller on `/sync` and the write routes of 8.4. "As built" notes under sections 2 to 8: the events read while points are active, no rating for a row without `ratingsAggregatedAt`, in-memory paging of the event list, `appealTextAt`, copies only while `reputationPenalties` is on, the browse check in code, the no-build wording, `/escrow/release` and `/escrow/refund` not gated, the heal marker on an admin suspend. Sections 9 and 13 stay PLANNED |
| 1.1.0 | 2026-11-07 | ESC-28 BUILT: section 1 (events from the orders stream, the policy with its caps, tiers, restriction, expiry and earn-back, the aggregate on the users row), section 10 on the server (`penalty_recorded`, `tier_changed`) and section 11 (`reputationPenalties`, `timeoutPenalties`, the SSM policy parameter); everything off by default. Sections 2 to 9 and 13 stay PLANNED (ESC-29 to ESC-33). No endpoint yet |
| 1.0.0 | 2026-11-07 | New contract, PLANNED (ESC-27 for ESC-28 to ESC-33): event codes, tiers, caps, effective rating, reliability, restriction, expiry and earn-back; `GET /reputation/me`, `GET /reputation/{userId}`, `GET /reputation/me/events`, `POST /reputation/appeals/photo-url`, `POST /reputation/events/{eventId}/appeal`; copied owner fields on crop and trip rows; `REPUTATION_RESTRICTED` gates, hidden restricted parties, the suspension gate; admin panel, appeals queue, decision and tag or restrict endpoints; `penalty_recorded` and `tier_changed`; flags `reputationPenalties`, `timeoutPenalties`, `reputationPublicTags`; `REPUTATION_*` error codes |

---

**Last Updated**: 2026-11-12
**Version**: 1.6.0
