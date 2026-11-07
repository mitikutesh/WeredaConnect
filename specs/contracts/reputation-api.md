# Reputation API Contract

## Version
1.1.0

## Status
PARTLY BUILT (1.1.0). BUILT in ESC-28: the events, the policy and the aggregate (section 1),
the server side of the two notifications (section 10) and the flags and policy parameter
(section 11), all shipping dark (both flags off everywhere). PLANNED: ESC-29 the reads, the gates
and the copied owner fields; ESC-30 the matching signal; ESC-31 the app; ESC-32 the admin
endpoints; ESC-33 the fraud hooks. Each section names its ticket. Design: `specs/architecture/reputation-architecture.md`. Rules and decisions D1 to D9:
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
farmers and truckers gets a fraud signal instead (ESC-33).

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
| 2 to 4 | Party and buyer reads | ESC-29 |
| 5, 6 | Appeal photo and appeal | ESC-29 |
| 7 | Owner fields copied onto crop and trip rows | ESC-29 (copy), ESC-30 (matching) |
| 8 | Gates: `REPUTATION_RESTRICTED`, hidden restricted parties, suspension | ESC-29 |
| 9 | Admin endpoints | ESC-32 |
| 10 | Notifications `penalty_recorded`, `tier_changed` | ESC-28 (server, BUILT), ESC-31 (app) |
| 11 | Feature flags | ESC-28 (BUILT) |
| 12 | Error codes | all |
| 13 | Offline and sync | ESC-31 |

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
  Review queue, whose reject is the existing suspend (ESC-33, proposed).

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

---

## 9. Admin endpoints (ESC-32)

**Auth**: admin pool ID token in the `admin` group (`AdminAuthHelper.IsInAdminGroup`); anyone
else `403 ADMIN_001`. Every write is audited: one transaction-audit row (entity `user`, the user
id, action as named below, the reason or note as metadata) and an `AuditLogger` line. Writes are
not gated by `reputationPublicTags`; they need `reputationPenalties` (off:
`503 REPUTATION_UNAVAILABLE`).

### 9.1 GET /admin/users/{userId}/reputation

The full view: section 2's fields plus `override.by`, every restriction (current and past 12
months), and the events (all codes, system rows included, `basePoints`, `buyerId`, the appeal text
and a pre-signed `appealPhotoUrl` valid 10 minutes). Query `limit` (1 to 100, default 50) and
`nextToken` page the events.

Errors: `404 REPUTATION_NOT_FOUND` (no row; a buyer answers `200` with no events and `role:
buyer`); `500 REPUTATION_SERVER_ERROR`.

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
`503 REPUTATION_UNAVAILABLE`; `500 REPUTATION_SERVER_ERROR`.

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

Response: `{ "reputation": { ...section 2 shape plus override.by... } }`.

Errors: `400 REPUTATION_VALIDATION` (action, reason, until missing or out of range, a buyer
target); `404 REPUTATION_NOT_FOUND`; `409 REPUTATION_NOT_RESTRICTED` (`lift` with no active
restriction); `503 REPUTATION_UNAVAILABLE`; `500 REPUTATION_SERVER_ERROR`.

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

As built: `reputationPenalties` and `timeoutPenalties` are in `infra/lib/config/environments.ts`,
`false` in dev, staging and prod. `reputationPublicTags` is added with the reads (ESC-29). With
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
| `REPUTATION_NOT_RESTRICTED` | 409 | `lift` with no active restriction |
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
| 1.1.0 | 2026-11-07 | ESC-28 BUILT: section 1 (events from the orders stream, the policy with its caps, tiers, restriction, expiry and earn-back, the aggregate on the users row), section 10 on the server (`penalty_recorded`, `tier_changed`) and section 11 (`reputationPenalties`, `timeoutPenalties`, the SSM policy parameter); everything off by default. Sections 2 to 9 and 13 stay PLANNED (ESC-29 to ESC-33). No endpoint yet |
| 1.0.0 | 2026-11-07 | New contract, PLANNED (ESC-27 for ESC-28 to ESC-33): event codes, tiers, caps, effective rating, reliability, restriction, expiry and earn-back; `GET /reputation/me`, `GET /reputation/{userId}`, `GET /reputation/me/events`, `POST /reputation/appeals/photo-url`, `POST /reputation/events/{eventId}/appeal`; copied owner fields on crop and trip rows; `REPUTATION_RESTRICTED` gates, hidden restricted parties, the suspension gate; admin panel, appeals queue, decision and tag or restrict endpoints; `penalty_recorded` and `tier_changed`; flags `reputationPenalties`, `timeoutPenalties`, `reputationPublicTags`; `REPUTATION_*` error codes |

---

**Last Updated**: 2026-11-07
**Version**: 1.1.0
