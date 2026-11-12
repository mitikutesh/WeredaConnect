# Reputation Architecture

## Version
1.4.0 | 2026-11-12 (ESC-33)

## Status
PARTLY BUILT. ESC-28 (the core write side) is built and ships dark: sections 3 (the table and
its indexes), 5, 6.1, 6.2, 6.4, 7, 8, 9, 12.2 and the ESC-28 monitoring of 14; section 20 lists
where the build reads the design. ESC-29 (the server reads and gates) is built and ships dark too:
6.3 (copies), 6.6 (reads) and 10 (gates, visibility, suspension); section 21 lists where that build
reads the design. ESC-32 (the admin actions, 6.5, and the admin app) is built and ships dark:
section 22. ESC-33 (the fraud hooks) is built: section 13, as built in 13.1. PLANNED: 11
(ESC-31).

**Contracts**: `specs/contracts/reputation-api.md` 1.4.0, `rating-api.md` 1.4.0,
`user-entity.md` 2.1.0, `notifications-api.md` 1.5.0, `account-deletion.md` 1.6.0.
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
3. **Arrival order cannot break a limit.** An event is derived from one record's old and new
   image alone, and the aggregate is recomputed from all of the user's events on every write.
   Orders are keyed by `orderId`, so one party's orders sit on different stream shards and arrive
   in any order (the hourly cron also expires many orders of one party in one run). Every window
   rule (the caps of 5.2, the earn-back limits of 5.8) therefore looks both ways in time from the
   candidate's `occurredAt`, over the events already recorded, so each cap holds whatever the
   arrival order. What arrival order can still decide is which of two capped events carries the
   point, and the one edge of 7.5.
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
| `zeroReason` | S | why a penalty got 0: `suspended`, `restricted`, `short_window`, `pair_cap`, `daily_cap`, `request_cap`, `not_reachable`, `timeout_penalties_off`, `no_fault` |
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
| `activePointsAfter` | N | on a penalty: the user's active points right after it (as built: the `activePoints` param of `penalty_recorded`, so the notify Lambda reads nothing) |
| `version` | N | row compare-and-set for earn-back and decisions |
| `purgeAt` | N | TTL |

**System rows** (never in a party's event list):

| `eventId` | `code` | Attributes |
|---|---|---|
| `RESTRICTION#{since}` | `RESTRICTION` | `cause` (`points`, `delivery_overdue`, `admin`), `since`, `until` (absent while an admin review is open), `status` (`active`, `ended`, `lifted`), `reason` and `by` (admin), `liftedAt`, `liftedBy`, `dueDay`/`dueAt` = `until` |
| `OVERRIDE#{at}` | `ADMIN_TAG` or `ADMIN_UNTAG` | `tag` (`low` or `clear`), `until`, `reason`, `by`, `status` (`active`, `ended`, `replaced`), `dueDay`/`dueAt` = `until` |
| `TIER#{at}#{cause}` | `TIER_CHANGED` | `fromTier`, `toTier`, `restrictedUntil`; feeds `tier_changed`. `{cause}` is the slot that changed it, or `daily` and `refresh` (as built: unique when several orders of one write share a time) |
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
| `{prefix}-reputation-stream` (`ReputationStreamFunction`) | new (ESC-28) | orders stream | 256 MB, ARM64, 30 s, parallelization factor 1 (one invocation per shard); reserved concurrency 10, kept at or above the stream's shard count, because a shard with no free concurrency waits and its iterator age grows towards the 24 hour `maxRecordAge` (alarm at 1 hour, section 14) |
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

"Counted" below means a `DECLINED_REQUEST` or `RESPONSE_EXPIRED` row of the same user with
`points` above 0 and `status` not `excused`, among the rows already recorded (read strongly
consistent, 6.1). Applied in this order; the first that fires gives 0 points and its
`zeroReason`. A candidate gets its full point or 0, never part of one.

1. **Suspended** (`suspended: true` on the users row read for the write): 0 (`suspended`). Spec
   H1: a suspended party's pending requests expire with no extra points.
2. **Restricted** at `occurredAt`: 0 (`restricted`), since the restriction blocked the accept.
3. **Short window** (expiries only): the request gave the party less than `expiryMinWindowHours`
   (12) to answer: 0 (`short_window`). The window is `farmerDeadlineAt - paymentVerifiedAt` for
   the farmer and `truckerDeadlineAt - truckerAskedAt` for the trucker. `ResponseDeadline`
   (`OrderCommitment.cs`) can give as little as 1 hour before departure, and a buyer could
   choose such a trip to force a timeout. A missing timestamp counts as a short window.
4. **Pair cap**: a counted event with the same `buyerId` and `occurredAt` within 30 days before
   **or after** the candidate's: 0 (`pair_cap`).
5. **Daily cap**: for every 24 hour window that contains the candidate's `occurredAt` (windows
   starting anywhere from 24 hours before it to the candidate itself), the counted points inside
   the window plus the candidate's point must stay at or below 6; otherwise 0 (`daily_cap`).
6. **Request cap**: the active points from counted declines and expiries (`points - earnedBack`,
   not expired) plus the candidate's must stay at or below `requestPointsMax` (proposed 9, one
   below `restrictAt`); otherwise 0 (`request_cap`). Declines and expiries alone can bring a
   party to Low reputation (the user's product rule) but never to a restriction, which only a
   broken commitment (a cancel, a trip cancel, a no-show, an overdue delivery) can add.

Invariants, whatever the arrival order: no two counted events of one user and buyer are within 30
days of each other; no 24 hour window holds more than 6 counted points of one user; the active
points from declines and expiries never exceed `requestPointsMax`. Each insert checks every
window that contains it, and a window that does not contain it is unchanged. Because every
write is serialized per user by the aggregate's version check (6.2), two candidates can never
both pass the same window. Example: the hourly cron expires 10 orders of one farmer in one run
and the stream delivers them newest first; at most 6 of them carry a point, and at most 1 per
buyer.

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
- Only the transaction that records a penalty starts a `points` restriction. Reads and the daily
  recompute never start one (proposed): a restriction cannot be missed, because the start is in
  the same transaction as the penalty that crosses the line, and a policy change that lowers
  `restrictAt` does not restrict anyone until their next penalty.
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
2. every 30 day window that contains its `occurredAt` holds fewer than 2 completion rows with
   `earnBackEventId` set (both ways in time, as 5.2);
3. no completion row with the same `buyerId` and `earnBackEventId` set within 90 days before or
   after it;
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
| `expiryMinWindowHours` | 12 | 0 to 24 (0 turns the rule off) |
| `requestPointsMax` | 9 | 1 to 100, below `restrictAt` |
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

1. Read the users row first (`GetItem`, `ConsistentRead: true`, projection `role`,
   `deletionState`, `suspended`, `reputation`, `ratingSum`, `ratingCount`), then the user's event
   rows (`Query` on the base table by `userId`, `ConsistentRead: true`, all pages up to
   `eventsReadCap`; past the cap log `REPUTATION_EVENTS_TRUNCATED` and go on with what was read).
   Both reads are strongly consistent so that the version check of 6.2 covers the events read:
   every write that changes a counted attribute of an event row (a new event, a status, points,
   `earnedBack`) commits in the same transaction as a `reputation.version` bump. An event
   committed before the users read is in the Query; one committed after it moved the version, so
   the transaction fails and the record is read again. (A GSI could not give this: index reads
   are always eventually consistent.)
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
days of penalties. A trucker with 5 completed orders a day reads about 500 rows, about 125 KB, so
about 32 read units per event with strongly consistent reads (twice the eventually consistent
cost); a typical party reads a few rows for 1 unit.

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

1. By the user's role, newest first, up to 200 rows:
   - trips: `Query TruckerIdCreatedAtIndex` (projection `ALL`, so `status` is on the index);
   - crops: `Query FarmerIdCreatedAtIndex`, whose projection is `INCLUDE` (`cropType`,
     `pricePerKg`, `deletedAt`) without `status`, so the keys go to one `BatchGetItem` per 100
     with `ProjectionExpression` `cropId, createdAt, #status` (the pattern
     `CropRepository.QueryByFarmerIdAsync` already uses); rows with `deletedAt` are skipped
     before the BatchGet.
2. For each row with an active status (crops `available`, `reserved`; trips `open`, `matched`):
   `UpdateItem SET ownerTier, ownerEffectiveRating, ownerRatingCount, ownerReliabilityPct,
   ownerReputationAt` (REMOVE the ones with no value), condition `attribute_exists` of the key and
   the status still active. Inactive rows are never written, so no conditional write is spent
   (and billed) on a row that would fail.
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
| R8 | N.`status` = `cancelled`, N.`cancelReason` = `farmer_cancelled`, O.`status` = `accepted` | `CANCELLED_AFTER_ACCEPT` or `LATE_CANCEL` | N.`farmerId` | `CANCEL#{orderId}` |
| R9 | N.`status` = `cancelled`, N.`cancelReason` = `farmer_cancelled`, O.`status` = `awaiting_transport` | `CANCELLED_TRANSPORT_FAILED` | N.`farmerId` | `CANCEL#{orderId}` |
| R10 (P3) | N.`transportFailure` = `trucker_no_show`, O differs | `NO_SHOW` | O.`truckerId` | `NOSHOW#{orderId}` |
| R11 (P3) | N.`cancelReason` = `farmer_no_show`, O.`status` not `cancelled` | `NO_SHOW` | N.`farmerId` | `NOSHOW#{orderId}` |
| R12 (P3) | N.`status` = `disputed`, O differs, the dispute reason is `delivery_overdue` (as ESC-36 names the order field) | `DELIVERY_OVERDUE` | N.`truckerId` | `OVERDUE#{orderId}` |
| R13 | N.`status` = `settled`, O differs | `ORDER_COMPLETED` | N.`farmerId`; and N.`truckerId` when `fulfillment` is `trucker` and `truckerResponse` is `accepted` | `COMPLETED#{orderId}` |

R8 and R9 tell the two farmer cancels apart by the old status, never by `faultParty`: the farmer's
cancel in `awaiting_transport` keeps the fault recorded when the transport failed (usually
`trucker`; `CommittedCancelSpec.FarmerAwaitingTransport` writes no `faultParty`), while the
cancel of an `accepted` order writes `faultParty: farmer`.

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
- Request window (R2, R4, the short-window rule of 5.2): N.`paymentVerifiedAt` and
  N.`farmerDeadlineAt` for the farmer, N.`truckerAskedAt` and N.`truckerDeadlineAt` for the trucker.
- Suspension (5.2 rule 1): `suspended` on the users row 6.1 reads anyway.
- `buyerId` from N, `orderTotal` from N (R13).

### 7.4 Flags

`reputationPenalties` off: every record is acknowledged without a write (log
`REPUTATION_STREAM_DISABLED` once per container). `timeoutPenalties` off: R2 and R4 record 0
points (`timeout_penalties_off`). P3 rules also need `pickupClassification`.

### 7.5 Parking failed records

A record whose handling throws is sent to SQS `{prefix}-reputation-parked` (14 days, SSE) and
reported as processed, so the shard moves on. The message holds the keys, the sequence number,
`ApproximateCreationDateTime` and, from each image, only the attributes section 7 reads
(`status`, the response fields, `transportFailure`, `cancelReason`, `cancelledBy`, `faultParty`,
the party and buyer ids, `tripId`, `fulfillment`, `pickupDate`, `orderTotal`, the request window
timestamps, `updatedAt` and the P3 dispute reason): well under 2 KB. Two whole order images can
pass SQS's 256 KB message limit (an item may be 400 KB), so the images are never parked whole; a
projected message that still does not fit (not expected) is reported as a batch item failure.
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

The reputation-events stream (`NEW_IMAGE`) becomes a fourth source of
`OrderNotifyStreamFunction`, with the same settings as its order sources (partial batch, bisect,
DLQ `{prefix}-notify-dlq`). Its filter keeps `INSERT` events whose `code` is one of the penalty
codes (`DECLINED_REQUEST`, `RESPONSE_EXPIRED`, `CANCELLED_AFTER_ACCEPT`, `LATE_CANCEL`,
`TRIP_CANCELLED_WITH_BOOKINGS`, `NO_SHOW`, `DELIVERY_OVERDUE`) or `TIER_CHANGED`, so `HEAL`,
`ORDER_COMPLETED`, restriction, override and zero-point rows that are not penalties cost no
invocation; the rules skip a penalty row with 0 points. `NotificationRules` gains:

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
- **Browse and match**: buyers' browse, the match candidates and the trip suggestions leave out
  rows whose stored `ownerTier` is `restricted`. How, per read path:
  - `StatusCreatedAtIndex` (crops, projection `ALL`), `StatusDepartureDateIndex` (trips, `ALL`)
    and the crop scan fallback: a FilterExpression `attribute_not_exists(ownerTier) OR ownerTier
    <> :restricted`;
  - `CropTypeCreatedAtIndex` (projection `INCLUDE`: `pricePerKg`, `deletedAt`, no `ownerTier`):
    a FilterExpression there would silently pass every row, so the check runs in code on the
    full items after the existing `BatchGetItem` (`CropRepository.QueryByCropTypeAsync`), like
    its woreda filter.

  The crop browse already refills short pages (crop-listing-api 1.9.0); the trip browse can
  return short pages (it already does for cancelled trips). Admin browse and the owner's own list
  are not filtered (proposed). This is a moderation filter, like the one that drops deleted
  listings and departed trips: the rule that a buyer sees all farmers' crops and all truckers'
  trips stands, and only listings the platform has taken off the market are left out.
- **Suspension**: section 8.4 of the contract. The `/sync` refusal reuses the ACC-02 read; the
  escrow, Fayda and device-token POSTs reuse theirs.
- **Reads**: `ReputationFunction` decides visibility from the caller's `custom:role` claim and the
  target row's `role`; section 3 of the contract is the table. `/reputation/*` and `/ratings` sit
  behind the app user pool's JWT authorizer (`jwtAuthorizer` in `api-stack.ts`), so an admin pool
  token gets `401` there: admins read reputation and ratings only through the `/admin` routes
  (`reputation-api.md` section 9, behind `adminJwtAuthorizer`). The rating read rules of
  `rating-api.md` 1.2.0 sit in `RatingFunction`.

## 11. Matching (ESC-30)

- `MatchScoring` (C#) and `MatchScorer` (Dart) read `ownerReliabilityPct / 100` from the crop
  candidate in place of `farmerReliability`; absent means the neutral 0.7 (unchanged default).
- Trip suggestions rank by the trip's `ownerReliabilityPct` the same way.
- The parity golden vectors (`match_scorer` tests on both sides) get cases for absent, 0, 50 and
  100.
- The server stops computing `farmerReliability` at crop create (`SyncFunction`); read models keep
  returning a stored value until a later contract drops it.

**As built (ESC-30, `match-api.md` 1.4.0)**:
- One rule on both sides, `ReliabilityFromPct` (C#) and `reliabilityFromPct` (Dart): absent is
  0.7, else `pct / 100` clamped to [0, 1]. The golden vectors (absent, 0, 50, 70, 83, 100, minus 5,
  150) and one ranked order are duplicated in `MatchScoringTests` and `match_scorer_test.dart`.
- The server scores with the field as the caller is given it (`OwnerReputationView`), so with
  `reputationPublicTags` off the score is neutral even where a copy is stored, and the server's
  ranking equals the one the app computes from its rows.
- Trips: `MatchScorer.rankTripSuggestions` (mobile and web) keeps price as the first key and
  orders equal prices by the trucker's reliability, unknown as 70, otherwise the previous order.
  `/match` reads no trips, so there is no C# twin.
- The apps store only `ownerReliabilityPct` on the cached crop and trip rows; the other owner
  fields are left to ESC-31.
- The push alert Lambda scores with the neutral value (it has no public tags flag).
- `farmerReliability` is also dropped from client payloads on `/sync`.

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

### 13.1 As built (ESC-33)

`fraud-detection-architecture.md` 3.32.0 section 15.3 is the specification; where the build reads
the table above differently:

| Hook | As built |
|---|---|
| `NO_SHOW`, `DELIVERY_OVERDUE` | written by `ReputationFraudHooks`, called from `ReputationService.RecordAsync` right after the event's transaction committed (not by the stream Lambda itself), so the unpark replay in order-cron writes them too and a replayed slot never does. Only for an event with points above 0. Weights 15 and 25, hard, once per order, and the two share one score cap of 50 (`RISK_PATTERN_TYPE_CAP`): reliability alone stops at MEDIUM. The events are produced by ESC-34 and ESC-36 (rules R10 to R12); nothing records them yet |
| buyer griefing | `BUYER_DECLINE_PATTERN` (10, soft, an FRD-09 pattern type: once per order, at most 50). Checked on every recorded `DECLINED_REQUEST` or `RESPONSE_EXPIRED` (R1 to R5, R2 included; with or without points). Counts the buyer's paid orders created in `buyerDeclineSignalDays` whose `farmerResponse` or `truckerResponse` is `declined` or `expired`, the order just recorded included when the read shows it paid, and needs at least 2 different declining farmers or truckers (one party declining one buyer three times flags nobody); `BuyerIdCreatedAtIndex`, at most 2 pages of 50 |
| repeat restriction | not built (follow-up): `REPUTATION_REPEAT_RESTRICTION` needs its Fraud Review handling decided with ESC-32's admin restrict and lift |
| payout delay | `PayoutDelayPolicy.ReputationWait` on the stored map: a restriction active now waits as flagged, a stored `low` (or an ended restriction not yet rewritten) as new. One extra GetItem only for a payee the risk score already made new. Read on sync and escrow-cron (`REPUTATION_PENALTIES_ENABLED`) |
| `GPS_ANOMALY` | the confirming buyer keeps the row as before (the evidence is the buyer's phone); with `reputationPenalties` on, the delivery's trucker gets a second row behind the attribution guard (one per delivery, one per buyer and trucker, two per trucker in 30 days, held by conditional slot rows); never for a self-pickup. Rollout note: turning `reputationPenalties` on (12.1 step 5) turns the trucker row on too; do that outside dev only after FUP-203 gives trucker-side evidence, or split the flag first |
| silent buyer (P3) | ESC-36 |

A failed hook logs `REPUTATION_FRAUD_HOOK_FAILED` and never fails the event. The reputation-stream
Lambda gained `ORDERS_TABLE`, Query on the orders `BuyerIdCreatedAtIndex` and PutItem on the
fraud-signals table.

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
| More stream shards than the Lambda's reserved concurrency | the extra shards wait; their iterator age climbs and records older than 24 hours go to the metadata DLQ | reserved concurrency (10) kept at or above the shard count; iterator age alarm at 1 hour |
| Two order images too big for one SQS message | | only the attributes the derivation reads are parked (7.5) |

Monitoring (ESC-28, `monitoring-stack.ts`): DLQ and parked-DLQ depth, the reputation stream's
iterator age (alarm above 1 hour), Lambda errors, and metric filters on `REPUTATION_POLICY_INVALID`,
`REPUTATION_EVENTS_TRUNCATED`, `REPUTATION_REVIEW_OVERDUE`, `REPUTATION_APPEAL_OVERDUE` and
`REPUTATION_COPY_FAILED` (rate).

---

## 15. Cost

All on-demand; no Scan in steady state; no new always-on resource.

- **Per recorded event**: one users `GetItem` and one events `Query`, both strongly consistent
  (1 read unit for a typical party, about 32 for a very busy trucker, 6.1), one transaction of 2
  to 6 items (transactions bill double). The users row sits in three `ALL`-projection indexes, so
  each users write also writes up to three index entries. Roughly 10 to 15 write units per event.
  At 1,000 orders a day and about 3 events per order that is about 1.4 million write units a
  month: a dollar or two.
- **Copies**, only on a public change: trips, one `Query` on an `ALL` index and one update per
  active trip (base table plus the two `ALL` trip indexes); crops, one `Query` on the `INCLUDE`
  owner index, one `BatchGetItem` for the status and one update per active crop. Of the three
  crop indexes only `StatusCreatedAtIndex` projects `ALL`; the two `INCLUDE` indexes do not hold
  the owner fields, so a copy writes the base table and one index entry.
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
| ESC-28 | `ReputationPolicyTests`: points per code, every cap of 5.2 (suspended, restricted, short window at 11h59 and 12h, pair cap before and after, daily cap with windows on both sides, request cap at 9), tier boundaries 2/3, 5/6, 9/10, overrides, restriction start and non-extension, effective rating bounds (0, 1.0 cap, 1.0 floor, no ratings), reliability below and at 3, expiry at the boundary, every earn-back condition. `ReputationStreamFunctionTests`: each rule R1 to R13 from recorded stream images (R9 with `faultParty: trucker` kept from the transport failure), plus non-events (`trip_full`, `listing_out_of_stock`, missing O fields), the same record twice and two orders of one trip cancel (one event), flags off, parking on a throw and a parked message under 256 KB for 400 KB images. **Arrival order**: the same set of decline and expiry records (one farmer, mixed buyers, `occurredAt` spread over a day and over 40 days) fed in order, in reverse order and in 20 shuffled orders; every run must satisfy the three invariants of 5.2, and 10 expiries from one cron minute fed newest first give at most 6 points and at most 1 per buyer. The same for earn-back limits. `ReputationServiceTests`: the transaction items and conditions (asserted on the real expressions), a CAS retry, a deleted account, copy refresh selection. Daily job: expiry, restriction end, override end, heal rows, the time budget leftover, catch-up. Rating create transaction and the backfill (rerun adds nothing). Infra: synth assertions for the table, indexes, TTL, stream, mapping and filters, queues, rules, grants, alarms |
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
| R5 | Restriction | starts on crossing 10; 14 days; not extended while active; after it, `low` until the next penalty restricts again; earn-back never lifts it; an excuse below 10 lifts a points restriction; only the transaction recording a penalty starts one (reads and the daily recompute never do, so a lowered `restrictAt` waits for the next penalty) |
| R6 | `DELIVERY_OVERDUE` restriction | no end date until an admin decides the review; uphold sets 14 days |
| R7 | Reliability counting | fault cancels and trip cancels (one per trip) and no-shows and overdue deliveries; declines and expiries not counted; whole percent; none below 3 orders |
| R8 | Completed orders | only `settled`; a disputed order (won or lost) counts neither way |
| R9 | Earn-back | order total at least 500 ETB, at most 2 in any 30 day window, at most one per buyer within 90 days either way, none on an order with a `SELF_TRADE` or `SHARED_DEVICE_TRADE` signal, nothing banked |
| R10 | Late cancel with no `pickupDate` | not late (3 points) |
| R11 | Trip cancel | accepted orders: one trip event (3 or 4); pending requests: a decline each (caps apply), per B9; not yet asked: nothing |
| R12 | Reachable by push | at least one device token when the expiry is processed; otherwise 0 points, shown to the party |
| R13 | Decline or expiry while restricted or suspended | 0 points: `restricted` (the restriction blocked the accept), `suspended` (spec H1: pending requests of a suspended party expire with no extra points) |
| R14 | Public tier | `warning` shows as `good`, `restricted` as `low`; with public tags off buyers see only the raw rating |
| R15 | Appeals | 7 days from when the event was recorded; one per event; 1 to 500 characters; optional photo; online request with a local draft; admins may excuse any event without an appeal |
| R16 | Admin decision path | `POST /admin/users/{userId}/reputation/events/{eventId}/decision` (the spec's path had no user id) |
| R17 | Admin limits | tag and untag up to 90 days; restrict up to 365 days; reason required; admin restrictions do not count toward the repeat rule |
| R18 | Repeat restriction | a weight 0 fraud signal into the Fraud Review queue (reject = suspend) |
| R19 | Admin role change | points follow the person; the rating aggregate is rebuilt for the new role |
| R20 | Suspension | write endpoints refuse a suspended caller with `403 AUTH_025` (not flagged); a suspended owner counts as restricted for buyers |
| R21 | Event retention | 400 days (TTL `purgeAt`), completions 100 days, `HEAL` markers 7 days (the daily job deletes them after use; the TTL only cleans up markers a stopped job left behind) |
| R22 | Reputation pushes | from the reputation-events stream through `OrderNotifyStreamFunction`, via `TIER_CHANGED` rows |
| R23 | Durability | park failing records in SQS and replay hourly |
| R24 | Copies | no `version` or `updatedAt` change on the listing |
| R25 | Orders from before the flag | a response field missing from the old image never yields a penalty |
| R26 | FRD weights and names | `NO_SHOW` 15 hard, `DELIVERY_OVERDUE` 25 hard, `BUYER_DECLINE_PATTERN` 10 soft; payout delay `low` as `New`, `restricted` as `Flagged` |
| R27 | Windows and arrival order | every cap and earn-back limit looks both ways in time from the candidate (5.2), so it holds in any arrival order; which of two capped events carries the point may depend on arrival order |
| R28 | Short request windows | an expiry whose window was under 12 hours (`expiryMinWindowHours`) gives 0 points (`short_window`): `ResponseDeadline` allows windows down to 1 hour, which a buyer could use to force a timeout |
| R29 | Can declines and expiries alone restrict a party | no: their active points are capped at `requestPointsMax` = 9, one below `restrictAt` (`request_cap`). They can still reach Low reputation (the product rule says decliners can be tagged); only broken commitments push a party into a restriction. Alternative for you: cap at `lowAt` - 1 = 5, so declines alone never reach the tag |
| R30 | Completions in the party's event list | `ORDER_COMPLETED` is listed only when it earned a point back (`reputation-api.md` section 4), to keep the list short on 3G |
| R31 | Admin browse | not filtered: admins see restricted parties' listings and trips (`reputation-api.md` 8.3, section 10 here) |
| R32 | Admin reads on the app routes | none: `/reputation/*` and `/ratings` use the app pool's authorizer, so admins read only through the `/admin` routes |

---

## 20. As built (ESC-28)

Code: `Helpers/ReputationPolicy.cs`, `Helpers/ReputationPolicySettings.cs`,
`Helpers/ReputationDerivation.cs`, `Data/ReputationEventRepository.cs` (+ interface),
`Data/DeviceReachability.cs`, `Services/ReputationService.cs`, `Services/ReputationRecordProcessor.cs`
(and `ReputationParking`), `Services/ReputationJobs.cs` (daily job, unpark, rating backfill),
`Functions/ReputationStreamFunction.cs`, the order-cron actions, `Services/NotificationRules.cs`
and `Helpers/PushText.cs` (two types), `Data/RatingRepository.cs` (the rating transaction),
`Data/UserRepository.cs` (the registration stamp), the account deletion step; infra in
`database-stack.ts`, `api-stack.ts`, `monitoring-stack.ts`, `environments.ts`.

How the build reads points the design left open:

1. **Not built here (ESC-29)**: the copy refresh of 6.3. A write that changes a public field
   (public tier, effective rating, reliability) still upserts the day's `HEAL#` marker, so the
   daily job will retry the copy once ESC-29 adds it; today the job consumes the marker and
   rewrites the aggregate.
2. **TIER rows** are written whenever the tier about to be stored differs from the stored one (an
   absent map reads as `good`), by the event transaction, the daily job and the refresh alike, so
   a time-driven change (an ended restriction) is never written without its row.
3. **Restriction start**: `since` is the recording time (the stream is seconds late; a parked
   record replayed hours later does not start a restriction that has partly run).
4. **R8** also needs `farmerResponse` in the old image (decision R25 applied to the farmer's
   cancel). **R12** reads the order's `disputeReason`; ESC-36 must write `delivery_overdue` there
   (the order takes no client `disputeReason`, so a buyer's dispute text cannot spell it).
5. **Reachability** (7.3) is read only while `timeoutPenalties` is on; off, the expiry gets 0
   (`timeout_penalties_off`) with no read.
6. **Earn-back rule 4** checks `SELF_TRADE` and `SHARED_DEVICE_TRADE` on the order for the party
   and the buyer. Without the fraud-signals table no earn-back is given (stricter, never one a
   self-trade could buy). A failed signal read throws (the record is parked).
7. **Request cap**: the active request points are checked at every moment of the candidate's
   points life (its start and each later counted row's start), so it holds in any arrival order
   like the other caps.
8. **`nextChangeAt`** also takes a completion's expiry and the moment a row leaves the 90 day
   reliability window, so a read past it recomputes reliability too.
9. **Read-lag check** (the optional hardening of the design review): the map stores
   `lastEventId` and `lastEventRecordedAt`; a writer whose strongly consistent events Query does
   not return that row (recorded in the last 30 days, before any TTL could remove it) reads again
   up to 3 times, then throws so the record is parked.
10. **Rating refresh** (6.4) runs only while `reputationPenalties` is on. The rating create
    transaction itself is not flagged.
11. **The order-cron** body takes `catchUpDays` as a number or a numeric string (1 to 100);
    `reputation_daily`, `reputation_unpark` and `reputation_recompute_all` log and return while
    the flag is off; `reputation_backfill_ratings` is not flagged. Reserved concurrency 6 (six
    rules since ESC-16 and ESC-28).
12. **Parking** keeps the record's shape (keys, sequence number, record time, the projected
    images), so `reputation_unpark` replays it through the same `ReputationRecordProcessor`. A
    parking failure logs `REPUTATION_PARK_FAILED` (alarmed) and reports the record.
13. **Monitoring**: the reputation stream's log group also carries the fraud signal filters
    (it builds the fraud service for rule 4; ESC-33 makes it write signals). The six reputation
    log metrics publish without a default.
14. **Notifications**: the inbox key is `{record time}#{type}#{eventId}` like every other
    source (the record time of an `INSERT` is the row's `recordedAt` within a second).

---

## 21. As built (ESC-29)

Code: `Functions/ReputationFunction.cs` (the five routes), `Helpers/ReputationGate.cs`,
`Helpers/OwnerReputationView.cs`, `Helpers/ReputationReadModels.cs`, `Data/ReputationCopyRepository.cs`,
`Services/AppealPhotoStore.cs`, `Functions/SyncFunction.Reputation.cs` (gates and stamps),
`Functions/AdminFunction.Reputation.cs` (the heal marker on a suspend), and changes in
`Services/ReputationService.cs` (copy refresh, `ReadAsync`), `Data/ReputationEventRepository.cs` (the
stored map as a snapshot, the appeal write), `Functions/CropFunction.cs`, `TripFunction.cs`,
`MatchFunction.cs`, `RatingFunction.cs`, `EscrowFunction.cs`, `FaydaVerifyFunction.cs`,
`DeviceTokenFunction.cs`, `Helpers/AccountDeletionGuard.cs`; infra in `api-stack.ts` and
`environments.ts`; the mobile and web session rule in `core/network/api_client.dart` and
`core/sync/sync_executor.dart`.

How the build reads points the design left open:

1. **One read of the caller's row** (section 10): `UserRepository.FromItem` parses the `reputation`
   map, `ratingSum`, `ratingCount` and `ratingsAggregatedAt` into server-only fields of the user, so
   the `/sync` batch read serves the deletion guard, the suspension refusal, the own gate and the
   owner stamps. The gate decides from the stored restriction and its `until` (no events read).
2. **Copies only while `reputationPenalties` is on** (6.3): the stamp at create and every refresh
   are behind the flag, because only the flagged writes keep a copy current. Turning the flag on
   leaves older rows without a copy until their owner's next change; a one-off stamp is TASK FUP-160.
3. **What triggers a copy**: a change of the stored tier (the internal one, so `low` to `restricted`
   and `good` to `warning` count; the heal marker follows the same rule), of the effective rating or
   of the reliability; every rating create (the count); the daily job for a user with a heal marker,
   **before** the marker is deleted. A row that already holds the values is not written.
4. **Suspend and unsuspend** (6.3, last line): the admin Lambda leaves the day's heal marker and the
   daily job rewrites the copies (the aggregate reads `suspended`), so the copy follows within a day,
   not at once. The order gate reads the live row, so no order reaches a suspended owner meanwhile.
   (Since ESC-32 the admin Lambda also refreshes the copies in the same request: section 22 point 7.)
5. **Hidden restricted owners** (section 10): a check in code on the full rows on every read path, in
   the three read Lambdas, for the `buyer` role only. No FilterExpression: the rows are read and
   billed either way, the crop browse refills its page, and one rule covers the `INCLUDE` index.
6. **Reads** (6.6): `ReputationService.ReadAsync` builds the aggregate from the stored map under the
   live policy (`ReputationPolicy.FromSnapshot`) and recomputes from the events once `now` has passed
   `nextChangeAt`. The party's own view also reads the events while points are active, for
   `nextPointExpiryAt`. With `reputationPenalties` off the read Lambdas use the default policy and
   never read the parameter.
7. **Ratings without a backfill stamp**: a row without `ratingsAggregatedAt` contributes no rating
   to the aggregate, the copies or the reads (it would count only the ratings since the deploy);
   `GET /ratings` keeps the walk for it. This closes the second "before the flag goes on" note of
   the ESC-28 review. The first (the unpark's short poll) is TASK FUP-161.
8. **Event list paging**: in memory over one Query of the user's partition (the sort key is the
   slot), newest `occurredAt` first, with a signed token bound to the caller.
9. **Appeal**: one conditional `UpdateItem`, no version change; `appealTextAt` keeps when the text
   arrived, and a `DELIVERY_OVERDUE` row keeps its `review` status and queue key. Account deletion
   deletes every version under `uploads/appeals/{userId}/`, only while `reputationPenalties` is on
   (off: no S3 call on the erasure path; TASK FUP-166 for the first run against real S3).
10. **Old apps** (TASK FUP-75): to a request without `X-App-Build` the restriction text goes out as
    `SYNC_VALIDATION` or `ORDER_VALIDATION`.
11. **Suspension** (R20): `/sync`, the three escrow POSTs that read the caller's row, the two Fayda
    POSTs, `POST /device-tokens` and the two reputation POSTs. `POST /escrow/release` and
    `/escrow/refund` read no caller row and are unchanged.
12. **Rating reads**: the role rule is not flagged; the summary comes from the users row when the
    role asked for is the current one and the row is stamped. A ratee without a usable users row
    keeps the old read, and a role-filtered page is filled (`PageFill`).
13. **Monitoring**: `REPUTATION_COPY_FAILED` is counted on the stream, order-cron and sync log
    groups as before; the admin Lambda's marker failure logs the same token but is not in that
    alarm (the suspension itself stands and is audited).

---

## 22. As built (ESC-32)

Code: `Services/ReputationService.Admin.cs` (the read, the decision, the four actions, the refresh
after a suspension), `Functions/AdminFunction.Reputation.cs` (the four routes of
`reputation-api.md` section 9, the audit rows, the photo URLs), `Data/ReputationEventRepository.cs`
(`ReputationRowUpdate` in the transaction, the appeals queue Query), `Models/AdminReputationDtos.cs`;
infra in `api-stack.ts` (routes, grants, the policy parameter on the admin Lambda); the admin app in
`admin/src/components/reputation/`, `admin/src/pages/ReputationAppealsPage.tsx`,
`admin/src/hooks/useAdminReputation.ts`.

How the build reads points the design left open:

1. **Row updates in the transaction** (6.5): an admin change of an existing row is a
   `ReputationRowUpdate` item next to the users map: SET and REMOVE under the row's own condition
   (the status read, and when asked the version read, the appeal status read, `appealTextAt` as
   read, no `until` yet). New rows (`OVERRIDE#{at}`, `RESTRICTION#{at}`, `TIER#{at}#admin`) are put
   with `attribute_not_exists`. A failed condition cancels all of it and the service plans again
   from a fresh read, up to 5 times.
2. **Which row a version protects**: an earn-back bumps the event's `version`, an appeal does not
   (ESC-29). So a decision conditions on the version (earn-back) and an uphold also on
   `appealStatus` and `appealTextAt`. An uphold is made on what the admin's page showed: the
   request carries `seenAppealTextAt`, a stored value that differs is refused
   (`REPUTATION_APPEAL_CHANGED`) before any write and never retried, and a text that lands between
   the read and the write fails the condition, after which the next read refuses it the same way.
   An excuse is not guarded (it only helps the party).
3. **Restrictions** (5.4): an excuse lifts every points restriction in force when the points fall
   below `restrictAt`, and the restriction of the excused `DELIVERY_OVERDUE` event (matched by
   `since` = the event's `recordedAt`, both written by one transaction). An uphold of such a review
   sets that row's `until` and puts it on `DueIndex`. A lifted row has status `lifted`, `liftedAt`
   and `liftedBy` and leaves `DueIndex`. An admin `restrict` is refused while any restriction is in
   force (`REPUTATION_ALREADY_RESTRICTED`); `lift` ends all of them.
4. **Overrides** (5.3): the new `OVERRIDE#` row is the one in force; every earlier row still
   `active` becomes `replaced` in the same transaction.
5. **Replays**: an excuse of an excused row, an uphold of an upheld row with no open appeal, and
   the same tag, untag or restrict already in force (same end, same reason) write nothing and are
   not audited again.
6. **Decided with no party text** (R15): an event upheld before the party wrote anything (only a
   `DELIVERY_OVERDUE` review can be) may be appealed once inside the appeal window; it then has an
   open `pending` appeal and takes a new decision. A row with the party's text never reopens.
7. **Copies at once** (6.3): an admin write that changes a copied field refreshes the owner's
   active listings in the same request and leaves the heal marker for a failure. The same now
   holds for an admin suspend or unsuspend (`RefreshAfterSuspensionAsync`, TASK FUP-163): the map
   is written when the tier changed (a `TIER#{at}#suspension` row) and the copies are rewritten,
   after the Cognito disable and sign-out so ending the sessions never waits for it (the heal
   marker is still left right after the profile write);
   point 4 of section 21 (the daily job) is now only the fallback.
8. **Reads for admins** (6.6): always one Query of the user's partition and an aggregate computed
   from it; the stored map is not trusted for the admin view. The reads are not flagged; with
   `reputationPenalties` off the policy parameter is not read and no write is accepted.
9. **Audit** (16): one transaction-audit row per applied write, with the tier and points before
   and after and, in the metadata, what else changed (the tag and the override row, the restriction
   written or lifted with the end it had, the end given to a reviewed restriction); the appeal text
   is never in it. A failed audit write is logged by the shared writer
   and does not undo the action (it is already committed).
10. **Grants**: on `reputation-events` the admin Lambda may Query the table and
    `AppealStatusIndex`, and put and update items; no Scan, no delete, no `DueIndex`. On crops and
    trips it gained `UpdateItem` for the copies. On the uploads bucket it reads `uploads/appeals/*`
    for the 10 minute photo URLs.
11. **Not built here**: the rating rebuild after an admin role change (section 18, R19; TASK
    FUP-196) and a notification to the party when an appeal is decided (TASK FUP-195).

---

## Changelog

| Version | Date | Change |
|---|---|---|
| 1.4.0 | 2026-11-12 | ESC-33 built: section 13.1 (as built: the hooks run in `ReputationService.RecordAsync` after the commit, the signals' caps, the buyer decline pattern on R1 to R5, the payout delay from the stored map, a guarded `GPS_ANOMALY` row on the trucker beside the buyer's, behind the flag; the repeat restriction signal left as a follow-up). |
| 1.3.0 | 2026-11-12 | ESC-32 built: 6.5 (the admin actions as one transaction with row updates), the admin reads and the appeals queue; section 22 (as built: row update conditions, restriction lift and review end, overrides replaced, replays, one appeal after a decision without party text, copies refreshed in the request for admin writes and for a suspend or unsuspend, admin grants) |
| 1.2.0 | 2026-11-11 | ESC-29 built: 6.3, 6.6 and 10 on the server, behind the flags; section 21 (as built: the caller's row read once with its reputation, copies only while `reputationPenalties` is on, copy triggers and the daily job copying before it deletes the heal marker, the heal marker on an admin suspend, the browse check in code, reads from the stored map with a recompute past `nextChangeAt`, no rating for an unstamped row, in-memory event paging, the appeal write, the wording for apps without a build header, the routes the suspension gate covers) |
| 1.1.0 | 2026-11-07 | ESC-28 built: status PARTLY BUILT; section 20 (as built: copies left to ESC-29 with the heal marker kept, TIER rows on every stored tier change and keyed `TIER#{at}#{cause}`, restriction from the recording time, R8 needs the old `farmerResponse`, R12 on the order's `disputeReason`, reachability only with `timeoutPenalties`, earn-back none without the fraud table, the request cap over the candidate's whole life, `nextChangeAt` with completions and the reliability window, the read-lag check with `lastEventId`, the refresh behind `reputationPenalties`, `catchUpDays`, parking shape, monitoring, inbox key); `activePointsAfter` on penalty rows |
| 1.0.0 | 2026-11-07 | First design (ESC-27): data model, stream derivation, policy and defaults, service and transaction, daily job, notifications, gates, matching, rollout and backfill, FRD hooks, failure modes, cost, ticket split ESC-28 to ESC-33, decisions R1 to R32. Review fixes before merge: caps and earn-back limits look both ways in time; strongly consistent events read under the version check; R8 and R9 keyed on the old status; suspended, short-window and request caps; crop owner index is `INCLUDE` (BatchGet for status, browse filter in code on the `INCLUDE` path); admins only on `/admin` routes; parked messages hold projected images; stream concurrency at or above the shard count; notify source filtered on `code` |
