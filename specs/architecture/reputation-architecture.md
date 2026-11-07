# Reputation Architecture

## Version
1.0.0 | 2026-11-07 (ESC-27)

## Status
PLANNED. Design for EPIC ESC phase P2 (ESC-28 to ESC-33); nothing is built. To be reviewed
before P2 code starts.

**Contracts**: `specs/contracts/reputation-api.md` 1.0.0, `rating-api.md` 1.2.0,
`user-entity.md` 2.0.0.
**Rules and decisions**: `specs/features/three-party-escrow-commitments-reputation.md` 4.11,
5.I, 5.K, 14 (D1 to D9). Product rules set by the user, not changed here: a farmer or trucker who
declines or cancels a paid order gets penalty points and a lower rating and can be tagged "Low
reputation"; farmers and truckers only ever see their own reputation details; buyers see public
tags on listings and trips.
**Related**: `fraud-detection-architecture.md` (FRD-09, FRD-13, FRD-15; ESC-33 hooks),
`push-notifications-architecture.md`, `order-lifecycle.md` 2.x (the commitment fields the events
are read from).

Values marked **proposed** are not settled by the spec; each is listed in section 19.

---

## 1. Overview

Reputation turns broken commitments on paid orders into penalty points, and points into a tier
that buyers can see and that can block new commitments. Four moving parts:

1. **Events.** `ReputationStreamFunction`, a second consumer of the orders table stream, compares
   each order change's old and new image and records at most one event per party and slot in a
   new `reputation-events` table.
2. **Policy.** `ReputationPolicy` is a pure function from a user's events, rating sum and count,
   overrides and `now` to points, tier, effective rating, reliability and restriction.
3. **Aggregate.** `ReputationService` writes the policy's result as a `reputation` map on the
   users row, in the same transaction as the event, and copies the public part onto the party's
   active crop and trip rows.
4. **Daily job.** The order-cron Lambda expires points, ends restrictions and admin tags,
   refreshes copies and heals anything a best-effort step missed.

Reads (`GET /reputation/*`), gates (`REPUTATION_RESTRICTED`), browse and match filtering, the
matching signal, the app, the admin panel and the fraud hooks sit on top (ESC-29 to ESC-33).

## 2. Design principles

1. **System-observed only.** Every event comes from a change the server made to an order (or an
   admin action). Clients never send points, tiers or penalties (spec 4.1 rule 8).
2. **One slot per (user, cause).** Event ids are deterministic (`CANCEL#{orderId}`), so a replayed
   or duplicated stream record writes nothing (conditional put).
3. **Order-independent.** An event is derived from one record's old and new image alone, and the
   aggregate is recomputed from all of the user's events on every write. Records may arrive late,
   twice or out of order without changing the totals (section 7.5 names the one edge).
4. **Atomic aggregate.** The event row and the aggregate change in one `TransactWriteItems`,
   conditioned on the aggregate's own version. They cannot drift apart.
5. **Reads are exact without the job.** A read past the aggregate's `nextChangeAt` recomputes from
   the events, so an expired point or an ended restriction never shows.
6. **Conservative for the party** where the spec is silent: when in doubt, fewer points, never a
   permanent lockout, every penalty explained and appealable.
7. **Role visibility is a hard rule.** Parties read only their own reputation; buyers read only
   the public summary of farmers and truckers; nobody reads a buyer's.
8. **No Scan, on-demand only.** Every steady-state access is a key lookup or a Query on an index.
9. **Behind flags** (`reputationPenalties`, `timeoutPenalties`, `reputationPublicTags`), off by
   default, so the build can ship dark.

---

## 3. Data model

### 3.1 Table `{prefix}-reputation-events`

| Property | Value |
|---|---|
| Partition key | `userId` (S) |
| Sort key | `eventId` (S), the slot id |
| Billing | on-demand |
| Point-in-time recovery | on (`WeredaDynamoTable` default); `RETAIN` in prod |
| TTL | `purgeAt` (N, epoch seconds). Penalty, restriction, override and tier rows: `recordedAt` + 400 days; `ORDER_COMPLETED`: `occurredAt` + 100 days; `HEAL`: + 7 days (proposed). The name is fixed once deployed (a TTL attribute cannot be renamed on a live table) |
| Stream | `NEW_IMAGE`, one consumer: `OrderNotifyStreamFunction` (section 9) |

The 400 days keep 12 months of restrictions for the repeat rule plus margin; completion rows are
only needed inside the 90 day windows, and purging them bounds the partition a busy trucker reads
(section 6.1).

### 3.2 Row kinds

**Penalty and completion rows** (the codes of `reputation-api.md` 1.1):

| Attribute | Type | Meaning |
|---|---|---|
| `userId`, `eventId` | S | keys; `eventId` is the slot (`REQUEST#`, `CANCEL#`, `TRIP_CANCEL#`, `NOSHOW#`, `OVERDUE#`, `COMPLETED#` + order or trip id) |
| `code` | S | the event code |
| `party` | S | `farmer` or `trucker`, the role in the order |
| `basePoints` | N | policy points before caps |
| `points` | N | points after caps |
| `zeroReason` | S | why a penalty got 0: `pair_cap`, `daily_cap`, `not_reachable`, `timeout_penalties_off`, `restricted`, `no_fault` |
| `earnedBack` | N | points taken back by completed orders (starts at 0) |
| `status` | S | `active`, `expired`, `excused` |
| `orderId`, `tripId`, `buyerId` | S | the subject; `buyerId` for the pair cap and earn-back, admin-only |
| `orderTotal` | N | on `ORDER_COMPLETED`, for the earn-back minimum |
| `pickupDate` | S | the lateness reference, as read |
| `occurredAt` | S | server time of the order change: the new image's `updatedAt`, else the record's `ApproximateCreationDateTime` |
| `recordedAt` | S | when the row was written |
| `pointsExpireAt` | S | `occurredAt` + 90 days |
| `dueDay`, `dueAt` | S | `DueIndex` keys while the row will still change state (3.3) |
| `earnBackEventId` | S | on `ORDER_COMPLETED` that earned a point back: the penalty it reduced |
| `appealStatus` | S | `pending` (party appeal) or `review` (`DELIVERY_OVERDUE`), sparse, removed on decision |
| `appealedAt` | S | `AppealStatusIndex` sort key (for `review` rows: `recordedAt`) |
| `appealText`, `appealPhotoRef` | S | the party's appeal |
| `appealDecision`, `decidedAt`, `decidedBy`, `decisionNote` | S | the admin's decision |
| `sourceSeq` | S | the stream sequence number, for support |
| `version` | N | row compare-and-set for earn-back and decisions |
| `purgeAt` | N | TTL |

**System rows** (never in a party's event list):

| `eventId` | `code` | Attributes |
|---|---|---|
| `RESTRICTION#{since}` | `RESTRICTION` | `cause` (`points`, `delivery_overdue`, `admin`), `since`, `until` (absent while an admin review is open), `status` (`active`, `ended`, `lifted`), `reason` and `by` (admin), `liftedAt`, `liftedBy`, `dueDay`/`dueAt` = `until` |
| `OVERRIDE#{at}` | `ADMIN_TAG` or `ADMIN_UNTAG` | `tag` (`low` or `clear`), `until`, `reason`, `by`, `status` (`active`, `ended`, `replaced`), `dueDay`/`dueAt` = `until` |
| `TIER#{at}` | `TIER_CHANGED` | `fromTier`, `toTier`, `restrictedUntil`; feeds `tier_changed` |
| `HEAL#{yyyy-mm-dd}` | `HEAL` | `dueDay` = that day, `dueAt` = first write; upserted by any writer that leaves a best-effort step behind; deleted by the daily job |

### 3.3 Indexes

| Index | Partition key | Sort key | Projection | Holds |
|---|---|---|---|---|
| `AppealStatusIndex` (spec) | `appealStatus` | `appealedAt` | `ALL` | open appeals (`pending`) and open `DELIVERY_OVERDUE` reviews (`review`); the admin queue reads it oldest first |
| `DueIndex` (proposed) | `dueDay` (`yyyy-mm-dd`, UTC) | `dueAt` | `KEYS_ONLY` | every row that will change state at `dueAt`: active penalties and completions (at `pointsExpireAt`), active restrictions and overrides with an end, `HEAL` rows. The attribute pair is removed once processed |

Both are sparse. `DueIndex` is how the daily job finds its work without a Scan: one Query per day
partition. Its partitions are per day, so writes spread; at this volume no partition is hot.

### 3.4 On the users row

`user-entity.md` 2.0.0 section 2: the `reputation` map (with its own `version`), and the
top-level `ratingSum`, `ratingCount`, `ratingsAggregatedAt`. Proposed: the rating sum and count
sit outside the map so `ADD` works without the map and a rating create never conflicts with the
map's compare-and-set.

### 3.5 On crop and trip rows

`ownerTier` (internal tier stored, public tier returned), `ownerEffectiveRating`,
`ownerRatingCount`, `ownerReliabilityPct`, `ownerReputationAt` (`reputation-api.md` section 7).
Written only by `ReputationService` and the create paths; no `version` bump, no `updatedAt`
change.

### 3.6 On rating rows

`inAggregate` (BOOL): whether the stars are in the ratee's stored sum (`rating-api.md` 1.2.0).

---

## 4. Components

```
  orders table --stream (NEW_AND_OLD_IMAGES)--+--> OrderNotifyStreamFunction (ESC-21, existing)
                                              |
                                              +--> ReputationStreamFunction (ESC-28, new)
                                                     | derive event (section 7)
                                                     | on error: park record --> SQS reputation-parked
                                                     v
  /sync rating create ---------------------> ReputationService ---> users row: reputation map,
  admin reputation actions (ESC-32) -------> (ReputationPolicy)       ratingSum, ratingCount
  order-cron reputation_daily (ESC-28) ----> |                  ---> reputation-events rows
  order-cron reputation_unpark (ESC-28) ---> |                  ---> crops / trips owner fields
                                             v
  reputation-events --stream (NEW_IMAGE)--> OrderNotifyStreamFunction: penalty_recorded, tier_changed

  GET /reputation/*  (ReputationFunction, ESC-29)  ---- reads users row + events
  ReputationGate in /sync (ESC-29) ------------------- reads the caller's row (already read) and
                                                       the counterparty's row at order create
  browse / match (ESC-29, ESC-30) -------------------- reads the copied owner fields only
```

| Lambda | New or changed | Trigger | Notes |
|---|---|---|---|
| `{prefix}-reputation-stream` (`ReputationStreamFunction`) | new (ESC-28) | orders stream | 256 MB, ARM64, 30 s, reserved concurrency 2, parallelization factor 1 |
| `{prefix}-reputation` (`ReputationFunction`) | new (ESC-29) | HTTP API: `GET /reputation/me`, `GET /reputation/{userId}`, `GET /reputation/me/events`, `POST /reputation/appeals/photo-url`, `POST /reputation/events/{eventId}/appeal` | 256 MB, 10 s, reserved concurrency 5 |
| `{prefix}-order-cron` | changed (ESC-28) | new daily rule `reputation_daily`, hourly `reputation_unpark`; manual `reputation_backfill_ratings` | reserved concurrency 3 to 5 |
| `{prefix}-sync` | changed (ESC-28, ESC-29) | | rating transaction, gates, owner stamps, suspension |
| `{prefix}-admin` | changed (ESC-32) | admin routes of `reputation-api.md` section 9 | |
| `{prefix}-order-notify-stream` | changed (ESC-28) | new source: reputation-events stream (INSERT) | |
| crops, trips, match Lambdas | changed (ESC-29, ESC-30) | | filter restricted owners, return owner fields |

---

## 5. Policy (`Helpers/ReputationPolicy.cs`, pure)

Input: the user's event rows, `ratingSum`, `ratingCount`, the active restriction and override,
the policy settings and `now`. Output: points per new event, the aggregate, the tier, whether a
restriction starts, the earn-back target. No I/O, so every rule is a table-driven unit test.

### 5.1 Points (defaults)

| Code | Points |
|---|---|
| `DECLINED_REQUEST` | 1 |
| `RESPONSE_EXPIRED` | 1 if reachable and `timeoutPenalties` on, else 0 |
| `CANCELLED_AFTER_ACCEPT` | 3 |
| `LATE_CANCEL` | 4 (cancel at or after `pickupDate` - 24 h) |
| `TRIP_CANCELLED_WITH_BOOKINGS` | 3, or 4 when late |
| `NO_SHOW` (P3) | 5 |
| `DELIVERY_OVERDUE` (P3) | 5 |
| `CANCELLED_TRANSPORT_FAILED`, `ORDER_COMPLETED` | 0 |

Proposed: no `pickupDate` on the order (a self-pickup before ESC-20) means not late.

### 5.2 Caps (declines and expiries only)

1. Restricted at `occurredAt`: 0 (`restricted`), since the restriction blocked the accept.
2. Pair cap: an earlier `DECLINED_REQUEST` or `RESPONSE_EXPIRED` of the same user with the same
   `buyerId`, `points` above 0, not excused, `occurredAt` within 30 days: 0 (`pair_cap`).
3. Daily cap: the points of the user's declines and expiries with `occurredAt` in the 24 hours
   before this one, plus this one, may not exceed 6; otherwise 0 (`daily_cap`).

### 5.3 Tier

1. An active restriction (`since <= now`, `until` absent or after `now`): `restricted`.
2. An active override `low`: `low`.
3. `activePoints` 6 or more: `low`, unless an active override `clear`, then `warning`.
4. 3 to 5: `warning`. 0 to 2: `good`.

Public tier: `good` for `good` and `warning`; `low` for `low` and `restricted` (proposed).
A suspended account (`suspended: true`) is stored and copied as `restricted` for buyers
(`reputation-api.md` 8.4).

### 5.4 Restriction

- Starts when a new penalty with points above 0 leaves `activePoints` at 10 or more and none is
  active: `RESTRICTION` row, `cause: points`, 14 days. (A party whose restriction ended with
  points still at 10 or more is restricted again by the next penalty.)
- `DELIVERY_OVERDUE` starts `cause: delivery_overdue` with no `until` and opens a `review`.
- Points added during an active restriction do not extend it (proposed).
- Ends at `until`, by admin `lift`, or (proposed) when an excuse takes a `points` restriction's
  points below 10. Earn-back never ends one.
- Repeat: a second `cause: points` restriction whose `since` is within 12 months of an earlier
  one is flagged for ESC-33 (`REPUTATION_REPEAT_RESTRICTION`, section 13).

### 5.5 Effective rating

`max(1.0, ratingSum / ratingCount - min(1.0, 0.1 x activePoints))`, rounded half up to one
decimal; none when `ratingCount` is 0 (D3).

### 5.6 Reliability

Over events with `occurredAt` in the last 90 days, not excused: `completed` = `ORDER_COMPLETED`;
`faultCancels` = `CANCELLED_AFTER_ACCEPT` + `LATE_CANCEL` + `TRIP_CANCELLED_WITH_BOOKINGS`;
`noShows` = `NO_SHOW` + `DELIVERY_OVERDUE`. `reliabilityPct` = round(100 x completed / (completed
+ faultCancels + noShows)), none while the denominator is below 3 (proposed counting).

### 5.7 Active points and expiry

`activePoints` = sum of `points - earnedBack` over rows with a penalty code, `status: active` and
`pointsExpireAt > now`. Expired rows keep their history (`status: expired`) until the TTL.

### 5.8 Earn-back

An `ORDER_COMPLETED` row earns a point back when all hold (proposed values):
1. `orderTotal` >= 500 ETB;
2. fewer than 2 completion rows with `earnBackEventId` set in the 30 days before;
3. no completion row with the same `buyerId` and `earnBackEventId` set in the 90 days before;
4. no `SELF_TRADE` or `SHARED_DEVICE_TRADE` fraud signal on the order for either party
   (`FraudDetectionService.HasSignalAsync`, FRD-15);
5. some active penalty has `points - earnedBack > 0`.

The target is that penalty with the oldest `occurredAt`; its `earnedBack` goes up by 1 in the
same transaction.

### 5.9 Settings (SSM `/{prefix}/reputation/policy`, JSON)

Read once per container with `AwsParameterResolver` and parsed with `JsonDocument` (AOT safe). A
key that is missing, not a number or out of range keeps its default and logs
`REPUTATION_POLICY_INVALID key=<k>` (alarmed). The CDK creates the parameter with the defaults.

| Key | Default | Range |
|---|---|---|
| `declinePoints` | 1 | 0 to 10 |
| `expiryPoints` | 1 | 0 to 10 |
| `cancelPoints` | 3 | 0 to 10 |
| `lateCancelPoints` | 4 | 0 to 10 |
| `tripCancelPoints` | 3 | 0 to 10 |
| `tripLateCancelPoints` | 4 | 0 to 10 |
| `noShowPoints` | 5 | 0 to 20 |
| `overduePoints` | 5 | 0 to 20 |
| `lateCancelHours` | 24 | 1 to 168 |
| `pointsLifeDays` | 90 | 7 to 365 |
| `pairCapDays` | 30 | 1 to 365 |
| `dailyCapPoints` | 6 | 1 to 50 |
| `warningAt` | 3 | 1 to 50 |
| `lowAt` | 6 | 2 to 50, above `warningAt` |
| `restrictAt` | 10 | 3 to 100, above `lowAt` |
| `restrictionDays` | 14 | 1 to 90 |
| `repeatWindowDays` | 365 | 30 to 730 |
| `ratingDeductionPerPoint` | 0.1 | 0 to 1 |
| `ratingDeductionMax` | 1.0 | 0 to 4 |
| `reliabilityMinOrders` | 3 | 1 to 50 |
| `earnBackMinOrderEtb` | 500 | 0 to 100,000 |
| `earnBackPer30Days` | 2 | 0 to 30 |
| `earnBackBuyerRepeatDays` | 90 | 0 to 365 |
| `appealWindowDays` | 7 | 1 to 30 |
| `tagMaxDays` | 90 | 1 to 365 |
| `adminRestrictMaxDays` | 365 | 1 to 730 |
| `buyerDeclineSignalCount` | 3 | 2 to 50 |
| `buyerDeclineSignalDays` | 30 | 1 to 365 |
| `eventsReadCap` | 2000 | 100 to 10,000 |

A changed value applies to each user at their next recompute; reads recompute tiers with the live
values (5.3 runs at read time), so only the stored copies lag. A full recompute after a policy
change is the manual `reputation_recompute_all` action (section 8.3).

---

## 6. `ReputationService`

### 6.1 Record an event

`RecordAsync(candidate)`, where the candidate carries user, slot, code, party, subject ids,
`occurredAt`, `pickupDate`, `orderTotal`, `buyerId` and the reachability bit:

1. Read the users row (`GetItem`, strongly consistent, projection `role`, `deletionState`,
   `suspended`, `reputation`, `ratingSum`, `ratingCount`) and the user's event rows (`Query` on
   `userId`, all pages up to `eventsReadCap`; past the cap log `REPUTATION_EVENTS_TRUNCATED` and
   go on with what was read).
2. No row, or `deletionState` present: drop the candidate, log `REPUTATION_SKIPPED reason=...`.
3. The slot already exists: replay, done.
4. Policy: points with caps (5.2), the aggregate with the candidate, a restriction start, the
   earn-back target, the tier before and after.
5. One `TransactWriteItems` (6.2). On `TransactionCanceledException`: the event put failed its
   condition, so it is a replay and done; the users condition failed, so re-read and repeat from 1
   (at most 5 attempts with jitter, then throw so the stream parks the record).
6. After the commit, if the public tier, the effective rating (one decimal), the rating count or
   the reliability changed: refresh the copies (6.3), best effort.

A busy party's partition is bounded by the TTLs (3.1): about 100 days of completions plus 400
days of penalties. A trucker with 5 completed orders a day reads about 500 rows (about 16 read
units eventually consistent) per event.

### 6.2 The transaction (at most 6 items)

| Item | Operation | Condition |
|---|---|---|
| the event | `Put` | `attribute_not_exists(eventId)` |
| the users row | `Update SET reputation = :map` | `attribute_exists(userId) AND attribute_not_exists(deletionState) AND reputation.version = :v` (first write: `attribute_not_exists(reputation)`) |
| a restriction | `Put RESTRICTION#{since}` | `attribute_not_exists(eventId)` |
| the earn-back target | `Update SET earnedBack = earnedBack + :one, version = version + :one` | `version = :seen AND #status = :active` |
| a tier change | `Put TIER#{occurredAt}` | `attribute_not_exists(eventId)` |
| the day's heal marker | `Update HEAL#{today} SET dueDay, dueAt = if_not_exists(...), purgeAt` | none |

The map is written whole, with `version` + 1, `updatedAt` = now and `nextChangeAt`. The row's own
`version` and `updatedAt` are not touched (`user-entity.md` 2.0.0 invariant 12).

### 6.3 Copy refresh

1. By the user's role: `Query` crops on `FarmerIdCreatedAtIndex` or trips on
   `TruckerIdCreatedAtIndex`, newest first, up to 200 rows.
2. For each row with an active status (crops `available`, `reserved`; trips `open`, `matched`):
   `UpdateItem SET ownerTier, ownerEffectiveRating, ownerRatingCount, ownerReliabilityPct,
   ownerReputationAt` (REMOVE the ones with no value), condition `attribute_exists` of the key and
   the status still active.
3. A failure is logged `REPUTATION_COPY_FAILED` and left to the daily job (the transaction wrote
   the `HEAL` marker).

The create paths stamp the same fields from the owner's row, so a new listing starts current.
Suspend and unsuspend (admin) also refresh, so a suspended owner's rows read `restricted`.

### 6.4 Rating create

`rating-api.md` 1.2.0 "Stored aggregate": the `/sync` rating create is one transaction (rating
put, `ADD ratingSum, ratingCount`, heal marker), then a best-effort `RefreshAsync(rateeId)`:
read, recompute, write the map if the effective rating changed (`version` CAS), refresh copies.

### 6.5 Admin actions (ESC-32)

Excuse, uphold, tag, untag, restrict and lift run the same recompute with a system row or a row
update in place of a candidate event, in one transaction with the users row. Each also writes a
transaction-audit row (entity `user`) after the commit; an audit write that fails is logged and
alarmed like the other admin audits.

### 6.6 Reads

`GET /reputation/*` and the gate read the users row. When `reputation` is absent: 0 points,
`good`, no reliability. When `now >= reputation.nextChangeAt`, they also `Query` the events and
recompute in memory (no write). Tier, effective rating and public mapping are always computed at
read time from the stored counts and the live policy, so a missed daily run never shows a stale
restriction, tag or expired point.

---

## 7. `ReputationStreamFunction` (ESC-28)

### 7.1 Source

The orders table stream (`NEW_AND_OLD_IMAGES`, already on for ESC-21). DynamoDB Streams serve
at most two readers per shard without throttling: this is the second. A third consumer later
needs a fan-out (EventBridge Pipes or Kinesis).

| Setting | Value | Why |
|---|---|---|
| starting position | `LATEST` | no points for anything before the deploy (spec 4.11, K2) |
| batch size | 25 | small records |
| parallelization factor | 1 | keeps per-shard order (not required, principle 3) |
| retry attempts | 10 | then the on-failure destination |
| bisect on error, partial batch response | on | one bad record cannot hold its shard |
| max record age | 24 hours | the stream's retention |
| on-failure destination | SQS `{prefix}-reputation-dlq` (metadata only), alarmed | |
| filters | `MODIFY` where the new image has `farmerResponse` in (`declined`, `expired`), or `truckerResponse` in (`declined`, `expired`, `cancelled`), or `transportFailure`, or `cancelReason` in (`farmer_cancelled`, `farmer_no_show`), or `status` in (`settled`, `disputed`) | five filters; anything else costs no invocation |

The filters also match later changes of a row that keeps those values; the derivation compares
old and new and ignores them.

### 7.2 Derivation rules

O is the old image, N the new one. A response field missing from O (an order from before
`COMMITMENTS_ENABLED`) never yields a penalty (proposed; spec K2).

| # | When | Code | User | Slot |
|---|---|---|---|---|
| R1 | N.`farmerResponse` = `declined`, O differs | `DECLINED_REQUEST` | N.`farmerId` | `REQUEST#{orderId}` |
| R2 | N.`farmerResponse` = `expired`, O differs | `RESPONSE_EXPIRED` | N.`farmerId` | `REQUEST#{orderId}` |
| R3 | N.`truckerResponse` = `declined`, O = `pending` | `DECLINED_REQUEST` | O.`truckerId` | `REQUEST#{orderId}` |
| R4 | N.`truckerResponse` = `expired`, O = `pending` | `RESPONSE_EXPIRED` | O.`truckerId` | `REQUEST#{orderId}` |
| R5 | N.`truckerResponse` = `cancelled`, O = `pending`, N.`transportFailure` = `trip_cancelled` | `DECLINED_REQUEST` (spec B9) | O.`truckerId` | `REQUEST#{orderId}` |
| R6 | N.`truckerResponse` = `cancelled`, O = `accepted`, N.`transportFailure` = `trucker_cancelled` | `CANCELLED_AFTER_ACCEPT` or `LATE_CANCEL` | O.`truckerId` | `CANCEL#{orderId}` |
| R7 | N.`truckerResponse` = `cancelled`, O = `accepted`, N.`transportFailure` = `trip_cancelled` | `TRIP_CANCELLED_WITH_BOOKINGS` | O.`truckerId` | `TRIP_CANCEL#{O.tripId}` |
| R8 | N.`cancelReason` = `farmer_cancelled`, N.`faultParty` = `farmer`, O.`status` not `cancelled` | `CANCELLED_AFTER_ACCEPT` or `LATE_CANCEL` | N.`farmerId` | `CANCEL#{orderId}` |
| R9 | N.`cancelReason` = `farmer_cancelled`, N.`faultParty` = `none`, O.`status` = `awaiting_transport` | `CANCELLED_TRANSPORT_FAILED` | N.`farmerId` | `CANCEL#{orderId}` |
| R10 (P3) | N.`transportFailure` = `trucker_no_show`, O differs | `NO_SHOW` | O.`truckerId` | `NOSHOW#{orderId}` |
| R11 (P3) | N.`cancelReason` = `farmer_no_show`, O.`status` not `cancelled` | `NO_SHOW` | N.`farmerId` | `NOSHOW#{orderId}` |
| R12 (P3) | N.`status` = `disputed`, O differs, the dispute reason is `delivery_overdue` (as ESC-36 names the order field) | `DELIVERY_OVERDUE` | N.`truckerId` | `OVERDUE#{orderId}` |
| R13 | N.`status` = `settled`, O differs | `ORDER_COMPLETED` | N.`farmerId`; and N.`truckerId` when `fulfillment` is `trucker` and `truckerResponse` is `accepted` | `COMPLETED#{orderId}` |

One record can yield more than one candidate (R13 for both parties); each is recorded on its own.
Not events: `trip_full` and `failed_capacity` (a race the system caused, spec A3), the cancels
`listing_out_of_stock`, `transport_not_arranged`, `transport_failed_buyer_cancelled`,
`buyer_withdrew`, `unpaid_expired`, `buyer_no_show` (no points in P3), and a farmer's `rejected`
reached through a dispute verdict.

ESC-34 and ESC-36 must keep R10 to R12 in step with the field names they ship; ESC-19's
replacement trucks reuse R3, R4 and R6 (the replaced trucker is O.`truckerId`; one slot per
trucker and order is enough because a failed trucker cannot be asked again, spec C11).

### 7.3 Inputs read per candidate

- Lateness: `occurredAt >= pickupDate - lateCancelHours` (R6, R7, R8). R7 is per trip: the first
  record of the trip records it; the other orders of the same cancel hit the existing slot.
- Reachability (R2, R4): one `Query` of the device tokens table for the user, `Limit 1`, keys
  only. A failed read throws (the record is parked and retried); it never silently zeroes.
- `buyerId` from N, `orderTotal` from N (R13).

### 7.4 Flags

`reputationPenalties` off: every record is acknowledged without a write (log
`REPUTATION_STREAM_DISABLED` once per container). `timeoutPenalties` off: R2 and R4 record 0
points (`timeout_penalties_off`). P3 rules also need `pickupClassification`.

### 7.5 Parking failed records

A record whose handling throws is sent whole (keys, both images, sequence number) to SQS
`{prefix}-reputation-parked` (14 days, SSE) and reported as processed, so the shard moves on.
Only when that send fails is the record reported as a batch item failure (stream retries, then
the metadata DLQ and its alarm). The order-cron action `reputation_unpark` (hourly) receives up
to 100 messages per run, re-runs the derivation and deletes each that succeeds; after 5 receives
the redrive policy moves a message to `{prefix}-reputation-parked-dlq`, alarmed. Replays are safe
by the slot ids.

The one order-dependent edge: a parked penalty replayed after a completion that would have earned
a point back from it leaves that point standing (the completion saw no active points). Rare and
in the strict direction; accepted.

---

## 8. Daily job (ESC-28)

### 8.1 Schedule

Order-cron action `reputation_daily`, EventBridge `cron(0 0 * * ? *)` (03:00 in Ethiopia), a
fourth rule on the order-cron Lambda. `reputationPenalties` off: log and return.

### 8.2 Steps

1. For each day from 7 days ago to today: `Query DueIndex dueDay = d AND dueAt <= now` (paged).
   Group the keys by `userId`.
2. Per user, until 4 minutes of the 5 minute timeout are used (the rest stays due and is picked up
   by the next run):
   - recompute (6.1 without a candidate);
   - in one transaction with the users row (chunks of at most 99 items): expired penalties and
     completions get `status: expired` and lose `dueDay`/`dueAt`; restrictions past `until` get
     `status: ended`; overrides past `until` get `status: ended`; `HEAL` rows are deleted; a
     `TIER#` row when the tier changed;
   - refresh copies when the public fields changed (6.3).
3. Queue age: open `review` rows older than 7 days log `REPUTATION_REVIEW_OVERDUE`; `pending`
   appeals older than 7 days log `REPUTATION_APPEAL_OVERDUE` (both alarmed).
4. One summary line: `REPUTATION_DAILY users=<n> expired=<n> ended=<n> copies=<n> leftover=<n>`.

### 8.3 Manual actions (order-cron, invoked by hand)

| Action | Use |
|---|---|
| `reputation_daily` with `{"catchUpDays": n}` | after an outage longer than 7 days (n up to 100) |
| `reputation_recompute_all` | after a policy change: walks `DueIndex` from today to 100 days ahead (every user with an active row has one) and recomputes each user once |
| `reputation_backfill_ratings` | once per environment (12.2) |

---

## 9. Notifications (ESC-28 server, ESC-31 app)

The reputation-events stream (`NEW_IMAGE`, filter `INSERT`) becomes a fourth source of
`OrderNotifyStreamFunction`, with the same settings as its order sources (partial batch, bisect,
DLQ `{prefix}-notify-dlq`). `NotificationRules` gains:

| Row inserted | Type | Recipient |
|---|---|---|
| a penalty code with `points` above 0 | `penalty_recorded` | the row's `userId` |
| `TIER_CHANGED` | `tier_changed` | the row's `userId` |

The inbox row id is deterministic (`{recordedAt}#{type}#{eventId}`), the push text comes from
`PushText` in the user's `preferredLanguage`, and the tap opens My reputation. Keeping push in
the one notify Lambda means no new SNS or device-token grants elsewhere. `notifications-api.md`
gets the two types in ESC-28.

---

## 10. Gates, visibility and suspension (ESC-29)

- **`Helpers/ReputationGate.cs`** (pure, the `KycPolicy` pattern): `CheckOwn(snapshot, now)` and
  `CheckCounterparty(snapshot, now)` return null or the `REPUTATION_RESTRICTED:` message of
  `reputation-api.md` 8.1 and 8.2. The snapshot is parsed from the users row item.
- **Own gate**: `/sync` already reads the caller's row strongly consistent for the ACC-02
  deletion guard; the gate parses `reputation` and `suspended` from that read, at no extra cost,
  and checks crop create, trip create and the two accepts.
- **Counterparty gate**: order create (and ESC-19's transport choice) reads the listing farmer's
  or trip trucker's row (one `GetItem`). A failed read refuses with the retryable text and writes
  nothing.
- **Browse and match**: a FilterExpression `attribute_not_exists(ownerTier) OR ownerTier <>
  :restricted` on the buyer's browse queries, the match candidates and the trip suggestions.
  Admin browse and the owner's own list are not filtered (proposed). The crop browse already
  refills short pages (crop-listing-api 1.9.0); the trip browse can return short pages (it
  already does for cancelled trips).
- **Suspension**: section 8.4 of the contract. The `/sync` refusal reuses the ACC-02 read; the
  escrow, Fayda and device-token POSTs reuse theirs.
- **Reads**: `ReputationFunction` decides visibility from the caller's `custom:role` claim (or the
  admin group) and the target row's `role`; section 3 of the contract is the table. The rating
  read rules of `rating-api.md` 1.2.0 sit in `RatingFunction`.

## 11. Matching (ESC-30)

- `MatchScoring` (C#) and `MatchScorer` (Dart) read `ownerReliabilityPct / 100` from the crop
  candidate in place of `farmerReliability`; absent means the neutral 0.7 (unchanged default).
- Trip suggestions rank by the trip's `ownerReliabilityPct` the same way.
- The parity golden vectors (`match_scorer` tests on both sides) get cases for absent, 0, 50 and
  100.
- The server stops computing `farmerReliability` at crop create (`SyncFunction`); read models keep
  returning a stored value until a later contract drops it.

---

## 12. Rollout

### 12.1 Order

1. **ESC-28 infra and server, every flag off**: the table and its indexes, the stream Lambda and
   its queues, the daily and unpark rules, the SSM policy parameter, the flags in
   `environments.ts`. The rating create transaction and `ratingsAggregatedAt` on new registrations
   go live at once (no wire change, not flagged).
2. **Backfill** the rating aggregates (12.2) in each environment.
3. **ESC-29, ESC-30, ESC-32, ESC-33** server and admin, behind the flags. The suspension gate is
   not flagged: confirm first that both clients end the session on `AUTH_025` from `/sync`.
4. **ESC-31** app and web builds.
5. **`reputationPenalties` on** in dev (where `commitmentsEnabled` is on); run the synthetic
   personas (flaky trucker, griefing buyer, happy path). Staging and prod only after
   `commitmentsEnabled` is on there.
6. **`reputationPublicTags` on** once the ESC-31 build is the minimum in use (older apps ignore
   the new fields, so this is about showing them, not about breaking anything).
7. **`timeoutPenalties` on** last, after Android push delivery is confirmed on real devices (D8
   relies on it).

### 12.2 Rating backfill

`reputation_backfill_ratings`, run by hand once per environment after step 1:
1. `Query RoleWaredaIndex` for `role = farmer`, then `role = trucker` (all pages).
2. Per user without `ratingsAggregatedAt`: `Query RateeIdCreatedAtIndex` for the user; take the
   ratings without `inAggregate`.
3. One transaction per user (up to 99 ratings per chunk): `ADD ratingSum, ratingCount` for the
   ratings whose `rateeRole` is the user's role, `SET inAggregate` on each rating (true or false,
   conditioned `attribute_not_exists(inAggregate)`), and on the last chunk `SET
   ratingsAggregatedAt`.
4. Log `REPUTATION_BACKFILL users=<n> ratings=<n>`. A rerun skips users already stamped and
   ratings already flagged.

Ratings created between step 1 and the backfill were added by their own transaction and carry
`inAggregate: true`, so nothing is counted twice. Users who registered after step 1 already carry
`ratingsAggregatedAt`.

---

## 13. FRD hooks (ESC-33)

| Hook | Change | Notes |
|---|---|---|
| `NO_SHOW`, `DELIVERY_OVERDUE` | the stream Lambda also writes fraud signals `NO_SHOW` (proposed weight 15, hard) and `DELIVERY_OVERDUE` (proposed 25, hard) on the party, the order as entity, through `RecordFraudSignalAsync` | a write onto a user other than the caller: add the guard FUP-62 asks for first |
| buyer griefing | on each R1, R3, R4 or R5, count the buyer's paid orders declined or expired by any party in the last 30 days (`BuyerIdCreatedAtIndex`, bounded like FRD-09); 3 or more writes `BUYER_DECLINE_PATTERN` (proposed name, soft, weight 10) on the buyer | feeds FRD-09's pattern set; added to the fraud signal catalogue in the same PR |
| repeat restriction | `REPUTATION_REPEAT_RESTRICTION`, weight 0 (review only), on the party | lands in the Fraud Review queue, whose reject is the existing suspend (FRD-05) |
| payout delay (FRD-13) | `PayoutDelayPolicy` already reads the payee's users row; it also reads the tier: `low` waits like `New`, `restricted` like `Flagged` (proposed) | no extra read |
| `GPS_ANOMALY` | attributed to the delivery's trucker, not to the buyer who confirms | the FUP-62 guard applies |
| silent buyer (P3) | `delivery-verification-api.md` 2.0.0 already reads the trucker's tier at T9 | uses the gate snapshot |

Reputation stays separate from the risk score: reputation is public and about reliability, the
score is private and about fraud (spec 4.11).

---

## 14. Failure modes

| Failure | Effect | Handling |
|---|---|---|
| A record throws (throttle, bug) | none lost | parked (7.5), replayed hourly; 5 failed replays go to the parked DLQ, alarmed |
| Parking fails too | stream retries 10 times, then only metadata reaches the DLQ | alarm `ReputationDlqMessages`; the event may be lost (the record expires after 24 hours). Follow-up: a reconcile tool that re-derives from an order's current row |
| Users row compare-and-set keeps failing | | 5 attempts, then park |
| Event slot exists | replay | no write |
| Account deleting or deleted, or no users row | | candidate dropped, logged |
| Device token read fails | | throw and park; never a silent 0 |
| Copy refresh fails | stale owner fields on some listings | logged; the `HEAL` row makes the daily job retry within a day |
| Daily job missed or timed out | stored aggregates and copies lag | reads stay exact (6.6); the next run reads 7 days back and leaves leftovers due; `catchUpDays` for longer gaps |
| Policy parameter missing or invalid | | defaults, `REPUTATION_POLICY_INVALID` alarm |
| More than `eventsReadCap` rows | counts may be low | `REPUTATION_EVENTS_TRUNCATED` alarm |
| Counterparty row unreadable at order create | | retryable refusal, nothing written |
| Rating transaction fails | nothing written | the client retries the operation |
| Ratee's role changed since the order | the rating is stored, not in the aggregate | by design (rating-api 1.2.0) |
| A flag turned off mid-flight | | the stream acks without writing; existing points keep expiring; the gates pass |
| A third orders stream consumer is wanted | throttling | needs a fan-out first (7.1) |

Monitoring (ESC-28, `monitoring-stack.ts`): DLQ and parked-DLQ depth, the reputation stream's
iterator age, Lambda errors, and metric filters on `REPUTATION_POLICY_INVALID`,
`REPUTATION_EVENTS_TRUNCATED`, `REPUTATION_REVIEW_OVERDUE`, `REPUTATION_APPEAL_OVERDUE` and
`REPUTATION_COPY_FAILED` (rate).

---

## 15. Cost

All on-demand; no Scan in steady state; no new always-on resource.

- **Per recorded event**: one users `GetItem`, one events `Query` (usually a few rows, about 16
  read units for a very busy trucker), one transaction of 2 to 6 items (transactions bill double).
  The users row sits in three `ALL`-projection indexes, so each users write also writes up to
  three index entries. Roughly 10 to 15 write units per event. At 1,000 orders a day and about 3
  events per order that is about 1.4 million write units a month: a dollar or two.
- **Copies**: one `Query` and a few updates per public change (crops have three `ALL` indexes,
  trips two). Only on change.
- **Rating create**: the transaction adds the users write and a heal upsert to what a rating cost
  before; the read side saves up to 50 index pages per `GET /ratings` and one call per card.
- **Daily job**: 8 small Queries plus one recompute per user with due rows.
- **Lambda**: the stream function runs only on filtered records; ARM64 at 256 MB.
- **SQS**: three queues, cents.
- **Indexes**: both sparse; `DueIndex` keys only.
- **Data use on 3G**: about 60 bytes per card, and the per-card rating calls go away.

---

## 16. Security and privacy

- **Role visibility**: enforced in `ReputationFunction`, `RatingFunction` (1.2.0) and the read
  models (only the public tier and numbers on listings and trips). A farmer never sees another
  farmer's reputation; a trucker never sees another trucker's (spec I8). The buyer's id on an event
  is admin-only.
- **Admin**: admin group only; every write audited with the admin id and a reason or note.
- **Appeal photos**: the private uploads bucket, keys under `uploads/appeals/{userId}/`, pre-signed
  `PUT` for 15 minutes, admin `GET` for 10 minutes.
- **Account deletion**: the tombstone removes the users row attributes (`user-entity.md` 2.0.0
  section 7). The user's `reputation-events` partition (appeal text and photos included) must be
  deleted too: `account-deletion.md` gets the table in ESC-28. The copied owner fields leave with
  the listings.
- **Logs**: user and order ids through `MoneyAlarmLog.LogId`; appeal text is never logged.
- **AOT**: stream images parsed with `JsonDocument` (the `CropMatchStreamFunction` pattern); new
  DTOs in `AppJsonContext`; low-level DynamoDB client; no reflection.

---

## 17. Testing

| Ticket | Tests |
|---|---|
| ESC-28 | `ReputationPolicyTests`: points per code, both caps, restricted zero, tier boundaries 2/3, 5/6, 9/10, overrides, restriction start and non-extension, effective rating bounds (0, 1.0 cap, 1.0 floor, no ratings), reliability below and at 3, expiry at the boundary, every earn-back condition. `ReputationStreamFunctionTests`: each rule R1 to R13 from recorded stream images, plus non-events (`trip_full`, `listing_out_of_stock`, missing O fields), the same record twice and two orders of one trip cancel (one event), flags off, parking on a throw. `ReputationServiceTests`: the transaction items and conditions (asserted on the real expressions), a CAS retry, a deleted account, copy refresh selection. Daily job: expiry, restriction end, override end, heal rows, the time budget leftover, catch-up. Rating create transaction and the backfill (rerun adds nothing). Infra: synth assertions for the table, indexes, TTL, stream, mapping and filters, queues, rules, grants, alarms |
| ESC-29 | gate tests for all 3 roles (farmer and trucker refused on create and accept, decline allowed, buyer refused on a restricted counterparty); browse hides restricted owners for buyers and shows them to the owner; endpoint tests for every row of the visibility table; appeal window, idempotency and closed cases; rating read rules; suspension refusal on `/sync` |
| ESC-30 | parity golden vectors on both sides with the new reliability source |
| ESC-31 | widget tests: badge, effective rating, low-reputation confirm, My reputation with events and expiry, appeal dialog, Profile rating; cache with "As of"; web build; 3 roles checked |
| ESC-32 | admin vitest for the panel and the queue; e2e for excuse and appeal; audit rows |
| ESC-33 | signal tests per hook, including the attribution guard |
| End to end | synthetic personas: a flaky trucker who declines then cancels a trip; a griefing buyer; the happy path with a replacement truck |

---

## 18. Ticket split

| Ticket | Scope | Main files |
|---|---|---|
| ESC-28 | table and indexes; `ReputationStreamFunction` with parking; `ReputationPolicy`; `ReputationService` (record, copies, refresh); rating create transaction and backfill; daily, unpark and manual actions; the two notification types; flags and SSM policy; alarms; `account-deletion.md` and `notifications-api.md` bumps | `Functions/ReputationStreamFunction.cs`, `Helpers/ReputationPolicy.cs`, `Services/ReputationService.cs`, `Data/ReputationEventRepository.cs` + interface, `Models/ReputationDtos.cs`, `Functions/OrderCronFunction.cs`, `Functions/SyncFunction.cs` (rating), `Services/NotificationRules.cs`, `Helpers/PushText.cs`, `AppJsonContext.cs`, `Program.cs`; `database-stack.ts`, `api-stack.ts`, `monitoring-stack.ts`, `environments.ts` |
| ESC-29 | `ReputationFunction` (sections 2 to 6 of the contract); `ReputationGate`; owner stamps at create; browse, match and trip suggestion filters; rating read rules; suspension gate; `crop-listing-api.md`, `trip-listing-api.md`, `sync-protocol.md`, `auth-api.md` bumps | `Functions/ReputationFunction.cs`, `Helpers/ReputationGate.cs`, `Functions/SyncFunction.cs`, `Functions/CropFunction.cs`, `Functions/TripFunction.cs`, `Functions/MatchFunction.cs`, `Functions/RatingFunction.cs`, `Data/RatingRepository.cs`, `Functions/EscrowFunction.cs`, `Functions/FaydaVerifyFunction.cs`, `Functions/DeviceTokenFunction.cs`; routes in `api-stack.ts` |
| ESC-30 | reliability source in both scorers; trip suggestion ranking; stop writing `farmerReliability` | `Helpers/MatchScoring.cs`, `Functions/MatchFunction.cs`, `mobile/lib/features/buyer/domain/services/match_scorer.dart`, both matching services |
| ESC-31 | badges, effective rating and reliability on cards and detail screens; low-reputation confirm; My reputation and appeal; Profile rating; cache; web counterparts; l10n (spec section 9 keys) | `mobile/lib/features/reputation/**` (new), `shared/widgets/listing_card.dart`, crop and trip detail and card widgets, `screens/profile_screen.dart`, schemas `crop_listing.dart`, `trip.dart` + web models, `main.dart`, `main_web.dart`, the three arb files |
| ESC-32 | admin user panel and appeals queue; decision and tag or restrict endpoints; role-change rating rebuild | `Functions/AdminFunction.cs`, `admin/src/pages/UserDetailPage.tsx`, `admin/src/pages/ReputationAppealsPage.tsx` (new), `App.tsx`, `Sidebar.tsx`, `i18n/{en,am,om}.json` |
| ESC-33 | the five hooks of section 13 and their catalogue rows | `Functions/ReputationStreamFunction.cs`, `Services/PayoutDelayPolicy.cs`, `Functions/SyncFunction.cs` (GPS attribution), `fraud-detection-architecture.md` |

ESC-28 ships first; ESC-29 needs its rows; ESC-30 needs ESC-29's copies; ESC-31 needs ESC-29's
endpoints and copies; ESC-32 and ESC-33 need ESC-28 only.

---

## 19. Decisions for the user

Each is the proposed (conservative) reading where the spec leaves the value or rule open. All are
reversible before ESC-28 ships.

| # | Question | Proposed |
|---|---|---|
| R1 | Event ids | one slot per user and cause (`REQUEST#`, `CANCEL#`, `TRIP_CANCEL#`, `NOSHOW#`, `OVERDUE#`, `COMPLETED#`), not `{CODE}#{orderId}`, so a replay can never record two codes for one cause |
| R2 | Where the rating sum and count live | top level on the users row, not in the `reputation` map; one-time backfill without Scan |
| R3 | How the daily job finds work | a second sparse index, `DueIndex` (keys only) |
| R4 | Flags | environment config and env vars like `commitmentsEnabled` (spec K3 said SSM); policy numbers in one SSM JSON parameter (D9) |
| R5 | Restriction | starts on crossing 10; 14 days; not extended while active; after it, `low` until the next penalty restricts again; earn-back never lifts it; an excuse below 10 lifts a points restriction |
| R6 | `DELIVERY_OVERDUE` restriction | no end date until an admin decides the review; uphold sets 14 days |
| R7 | Reliability counting | fault cancels and trip cancels (one per trip) and no-shows and overdue deliveries; declines and expiries not counted; whole percent; none below 3 orders |
| R8 | Completed orders | only `settled`; a disputed order (won or lost) counts neither way |
| R9 | Earn-back | order total at least 500 ETB, at most 2 per 30 days, at most one per buyer in 90 days, none on an order with a `SELF_TRADE` or `SHARED_DEVICE_TRADE` signal, nothing banked |
| R10 | Late cancel with no `pickupDate` | not late (3 points) |
| R11 | Trip cancel | accepted orders: one trip event (3 or 4); pending requests: a decline each (caps apply), per B9; not yet asked: nothing |
| R12 | Reachable by push | at least one device token when the expiry is processed; otherwise 0 points, shown to the party |
| R13 | Decline or expiry while restricted | 0 points (the restriction blocked the accept) |
| R14 | Public tier | `warning` shows as `good`, `restricted` as `low`; with public tags off buyers see only the raw rating |
| R15 | Appeals | 7 days from when the event was recorded; one per event; 1 to 500 characters; optional photo; online request with a local draft; admins may excuse any event without an appeal |
| R16 | Admin decision path | `POST /admin/users/{userId}/reputation/events/{eventId}/decision` (the spec's path had no user id) |
| R17 | Admin limits | tag and untag up to 90 days; restrict up to 365 days; reason required; admin restrictions do not count toward the repeat rule |
| R18 | Repeat restriction | a weight 0 fraud signal into the Fraud Review queue (reject = suspend) |
| R19 | Admin role change | points follow the person; the rating aggregate is rebuilt for the new role |
| R20 | Suspension | write endpoints refuse a suspended caller with `403 AUTH_025` (not flagged); a suspended owner counts as restricted for buyers |
| R21 | Event retention | 400 days (TTL `purgeAt`), completions 100 days |
| R22 | Reputation pushes | from the reputation-events stream through `OrderNotifyStreamFunction`, via `TIER_CHANGED` rows |
| R23 | Durability | park failing records in SQS and replay hourly |
| R24 | Copies | no `version` or `updatedAt` change on the listing |
| R25 | Orders from before the flag | a response field missing from the old image never yields a penalty |
| R26 | FRD weights and names | `NO_SHOW` 15 hard, `DELIVERY_OVERDUE` 25 hard, `BUYER_DECLINE_PATTERN` 10 soft; payout delay `low` as `New`, `restricted` as `Flagged` |

---

## Changelog

| Version | Date | Change |
|---|---|---|
| 1.0.0 | 2026-11-07 | First design (ESC-27): data model, stream derivation, policy and defaults, service and transaction, daily job, notifications, gates, matching, rollout and backfill, FRD hooks, failure modes, cost, ticket split ESC-28 to ESC-33, decisions R1 to R26 |
