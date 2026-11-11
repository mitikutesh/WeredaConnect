# Rating API

## Version
1.4.0

## Parties
Mobile, Web, Backend, Infrastructure, Admin

## Overview
Lets a **buyer** rate the counterparties of a completed order: the **farmer**
and, when the order was transported, the **trucker**. Ratings are 1 to 5 stars with
an optional comment, are **create-only and immutable**, and there is **exactly
one rating per (order, ratee)**.

- **Writes** go through the offline-first `/sync` endpoint as `rating` create
  operations (mirrors `buyer_intent`).
- **Reads** are served by a dedicated `RatingFunction` at `GET /ratings`.
- **Reputation**: the ratee's star sum and count are stored on their users row
  when a rating is created (1.3.0, BUILT in ESC-28); reads are limited to buyers
  and the ratee and filtered by `rateeRole`, and the summary carries the effective
  rating of `reputation-api.md` (1.2.0; BUILT in 1.4.0, ESC-29). See "Stored
  aggregate" and "Read rules" below.

## Request Format

### Create (via `POST /sync`)
One operation inside the standard sync batch (see `sync-protocol.md`):

```json
{
  "operationId": "<uuid>",
  "entityType": "rating",
  "action": "create",
  "entityId": "<orderId>:<rateeId>",
  "timestamp": "2026-07-09T10:00:00.000Z",
  "idempotencyKey": "<userId>:rating:<orderId>:<rateeId>:create:<ts>",
  "data": {
    "ratingId": "<orderId>:<rateeId>",
    "orderId": "<orderId>",
    "raterId": "<buyerUserId>",
    "rateeId": "<farmerOrTruckerUserId>",
    "rateeRole": "farmer | trucker",
    "stars": 5,
    "comment": "Optional, ≤ 500 chars",
    "version": 1
  }
}
```

- `ratingId` MUST equal `"{orderId}:{rateeId}"` (deterministic; enforces
  one-per-pair via `attribute_not_exists` at the server).
- `raterId` MUST equal the authenticated user (the buyer).
- `action` MUST be `create`. `update`/`delete` are rejected (immutable).

### Read
- `GET /ratings?rateeId=<userId>&rateeRole=<farmer|trucker>&limit=<1-50>&nextToken=<token>`:
  ratings a user received, newest first, plus an aggregate summary. `rateeRole`
  is new in 1.2.0 and optional (default: the ratee's current role).
- `GET /ratings/{id}`: a single rating by its `ratingId`.

Both require a valid app user pool JWT (the route's authorizer refuses an admin
pool token with `401`). Up to 1.1.0 any authenticated user could read any ratee;
since 1.2.0 (BUILT in 1.4.0, ESC-29) only buyers and the ratee can (see "Read rules").

## Response Format

### `GET /ratings`
```json
{
  "ratings": [
    {
      "ratingId": "order123:farmer456",
      "orderId": "order123",
      "raterId": "buyer789",
      "rateeId": "farmer456",
      "rateeRole": "farmer",
      "stars": 5,
      "comment": "Great quality teff",
      "createdAt": "2026-07-09T10:00:00.000Z",
      "updatedAt": "2026-07-09T10:00:00.000Z",
      "version": 1
    }
  ],
  "summary": { "average": 4.6, "count": 12, "rateeRole": "farmer", "effectiveRating": 4.2 },
  "reputation": {
    "userId": "farmer456",
    "role": "farmer",
    "tier": "good",
    "ratingAverage": 4.6,
    "ratingCount": 12,
    "effectiveRating": 4.2,
    "reliabilityPct": 83,
    "completedOrders90d": 10,
    "totalOrders90d": 12,
    "asOf": "2026-11-07T09:00:00.000Z"
  },
  "nextToken": null
}
```

1.2.0 additions (BUILT in 1.4.0, ESC-29):
- `summary.rateeRole`: the role the list and the summary are for.
- `summary.effectiveRating`: `reputation-api.md` 1.4 (the average minus 0.1 per
  active penalty point, at most 1.0 off, never below 1.0, one decimal). Left out
  when `count` is 0, when `reputationPublicTags` is off, or when `rateeRole` is
  not the ratee's current role.
- `reputation`: the public summary of `GET /reputation/{userId}`
  (`reputation-api.md` section 3), only for a farmer or trucker ratee whose
  `rateeRole` is their current role, and only with `reputationPublicTags` on.
  Saves the detail screens a second call.

### `GET /ratings/{id}`
```json
{ "rating": { /* RatingDto as above */ } }
```

### Create result (inside the `/sync` batch response)
Standard `SyncOperationResult`: `status` is `success` (with `serverData` and
`serverVersion: 1`), `duplicate` (idempotency replay), or `conflict` (a rating
for this order+ratee already exists).

## Stored aggregate (1.2.0; BUILT in 1.3.0, ESC-28)

Up to 1.1.0 every `GET /ratings` walked up to 50 pages of the ratee index to
compute the summary, and browse called it once per farmer card (spec R3). Since
1.2.0 the ratee's users row carries the sum and count of their stars
(`user-entity.md` 2.0.0, top-level `ratingSum`, `ratingCount`,
`ratingsAggregatedAt`), kept exact by the create:

1. **Create.** After the 1.1.0 checks, one `TransactWriteItems`:
   - Put the rating with `attribute_not_exists(ratingId)` and `inAggregate: true`;
   - Update the ratee's users row: `ADD ratingSum :stars, ratingCount :one`,
     condition `attribute_exists(userId) AND #role = :rateeRole AND
     attribute_not_exists(deletionState)`;
   - Upsert the ratee's `HEAL#{day}` row in `reputation-events` (the daily job
     refreshes the copied owner fields, `reputation-api.md` section 7).
2. **Outcomes.** Success: `success` as before. The rating's condition failed:
   `conflict` as before (nothing else written). Only the users condition failed
   (the ratee's role changed since the order, a rare admin `change_role`): the
   rating is put alone with `inAggregate: false`, so the summary of the current
   role stays exact; the answer is still `success`. Any other failure: the
   operation fails as before and the client retries; nothing was written.
3. **After the commit**, best effort: `ReputationService` recomputes the ratee's
   effective rating and refreshes their copied owner fields; a failure is logged
   (`REPUTATION_COPY_FAILED`) and the `HEAL` row makes the daily job do it. As
   built (1.3.0): the refresh runs only while `reputationPenalties` is on (no map is
   written while the reputation build ships dark) and writes the `reputation` map
   when a public field changed; the copies onto listings are ESC-29's.
4. **Backfill**, once per environment before ESC-29 reads the aggregate
   (architecture doc 12.2): for each farmer and trucker (Query on
   `RoleWaredaIndex`, no Scan), sum their ratings without `inAggregate` whose
   `rateeRole` is the user's role, `ADD` them in one transaction with `SET
   inAggregate` on those rating rows (conditioned `attribute_not_exists`, so a
   rerun adds nothing twice), and stamp `ratingsAggregatedAt`. Ratings of another
   role get `inAggregate: false`. New registrations start with `ratingSum: 0`,
   `ratingCount: 0` and `ratingsAggregatedAt`. As built (1.3.0): the order-cron
   action `reputation_backfill_ratings`, invoked by hand, not flagged; a rerun
   skips stamped users and flagged ratings. A farmer or trucker without a
   `woreda` is not on `RoleWaredaIndex`: their row stays unstamped and reads keep
   the 1.1.0 walk for them.
5. **Role change.** An admin `change_role` rebuilds the aggregate for the new
   role (sum of the ratee's ratings with that `rateeRole`, `SET` conditioned on
   the `ratingCount` it read) and flips `inAggregate` accordingly (ESC-32, still
   PLANNED).

Built in ESC-28 (1.3.0): `RatingRepository.CreateAsync` (the transaction, the
`inAggregate: false` fallback, `inAggregate` never taken from a payload nor
returned), `UserRepository.CreateUserAsync` (the registration stamp),
`RatingAggregateBackfill` (order-cron `reputation_backfill_ratings`).

The wire shape of the create is unchanged; the mobile and web clients change
nothing for it.

## Read rules (1.2.0; BUILT in 1.4.0, ESC-29)

| Caller | `GET /ratings?rateeId=` | `GET /ratings/{id}` |
|---|---|---|
| buyer | any farmer or trucker ratee | a rating whose ratee is a farmer or trucker |
| farmer or trucker | only `rateeId` = themselves | only a rating they received |
| anyone else, or a farmer or trucker asking about someone else | `403 RATING_FORBIDDEN` | `403 RATING_FORBIDDEN` |

Admins never reach these routes: they sit behind the app user pool's authorizer,
so an admin pool token gets `401`. Admins see a user's rating sum and count in
`GET /admin/users/{userId}/reputation` (`reputation-api.md` 9.1, ESC-32).

- A ratee with no users row, or a buyer ratee, answers a buyer with an empty
  list and `{ "average": 0, "count": 0 }` (no account oracle), as 1.1.0 did for
  an unknown id.
- `rateeRole` filters the list (FilterExpression on `RateeIdCreatedAtIndex`)
  and the summary. A value other than `farmer` or `trucker` is
  `400 RATING_VALIDATION`.
- **Summary source.** When `rateeRole` is the ratee's current role and the row
  carries `ratingsAggregatedAt`: `ratingSum / ratingCount` from the users row
  (one GetItem, which the access check already made). Otherwise the 1.1.0 walk,
  filtered by `rateeRole`.
- The caller's role comes from the `custom:role` claim; the ratee's from the
  users row read for the summary.
- The mobile and web clients already call these routes only as a buyer (browse,
  crop detail, the buyer's `hasRated` check by id) or for the user's own id, so
  no client breaks. ESC-31 drops the per-card calls in favour of the copied
  owner fields.

As built (1.4.0, `Functions/RatingFunction.cs`):
- The read rules are not flagged. The refusal is decided from the caller's
  `custom:role` claim before any read; a caller with no role, or another role,
  is refused too.
- `GET /ratings/{id}` answers a farmer or trucker `403` both for a rating of
  someone else and for an id that does not exist; a buyer gets `404` for a
  missing id.
- A ratee whose account is deleted reads like an unknown id (the empty answer).
- The page is one Query with the role filter, so it can hold fewer rows than
  `limit` while `nextToken` is present (a ratee who changed role); the client
  pages on.
- `summary.rateeRole` is always present once the ratee's row is read.
  `summary.effectiveRating` and `reputation` need `reputationPublicTags`.
- The ratee's row is one `GetItem` (`ReputationService.ReadAsync`); their
  reputation events are read only when `now` has passed the stored
  `nextChangeAt`.

## Error Codes
| Code | HTTP | Meaning |
|------|------|---------|
| `RATING_VALIDATION` | 400 | Missing/invalid fields; unknown fields; `stars` out of 1–5; bad `rateeRole`; `ratee` is not the order's farmer/trucker; order not found; malformed `ratingId`; bad `limit`/`nextToken` |
| `RATING_UNAUTHORIZED` | 200 (sync op `error`) | Caller is not a buyer, or `raterId` ≠ authenticated user |
| `RATING_FORBIDDEN` | 200 (sync op `error`) | Order is not the buyer's, or order not `delivered`, `disbursing` or `settled` (1.1.0) |
| `RATING_FORBIDDEN` | 403 (1.2.0, `GET`) | The caller may not read this ratee's ratings (Read rules) |
| `RATING_UNAVAILABLE` | 200 (sync op `error`) | Ratings table not provisioned in this environment |
| `RATING_NOT_FOUND` | 404 | `GET /ratings/{id}`: no such rating |
| `SYNC_CONFLICT` | 200 (sync op `conflict`) | A rating already exists for this (order, ratee) |
| `RATING_SERVER_ERROR` | 500 | Unexpected server failure on read |

## Invariants
1. **One rating per (order, ratee)** — enforced by the deterministic `ratingId`
   plus a create-time `attribute_not_exists(ratingId)` condition.
2. **Only the buyer of an order may rate**, and only its farmer or trucker.
3. **Order must be `delivered`, `disbursing` or `settled`** at rating time (1.1.0: the order follows its money after delivery, and `disbursing` can last days while a manual payout waits, `order-lifecycle.md` 1.17.0).
4. **Ratings are immutable** — no update/delete.
5. `stars ∈ [1,5]` (integer); `comment` ≤ 500 chars.
6. `createdAt`, `updatedAt`, `version` are server-stamped (client values ignored).
7. **The stored aggregate is exact** (1.2.0): once a user carries
   `ratingsAggregatedAt`, `ratingSum` and `ratingCount` on their users row equal
   the sum and count of the stars of their ratings with `inAggregate: true`, which
   are exactly their ratings whose `rateeRole` is their current role. Only the
   rating create transaction, the backfill and the role-change rebuild write them.
8. **Farmers and truckers never read another user's ratings** (1.2.0); buyers
   read only farmers' and truckers'.

## DynamoDB Schema (Ratings table)
- PK `ratingId` (STRING, `"{orderId}:{rateeId}"`) — **sole key, no sort key**, so
  `attribute_not_exists(ratingId)` on create truly enforces one-per-(order,ratee).
- GSI `RateeIdCreatedAtIndex`: PK `rateeId`, SK `createdAt`, projection ALL —
  powers `GET /ratings?rateeId=` and the aggregate summary.
- `inAggregate` (BOOL, 1.2.0): written by the create transaction and the
  backfill; `true` when the stars are in the ratee's stored aggregate. Never
  returned to clients.

## Changelog
- **1.4.0** (2026-11-11): BUILT (ESC-29): the read rules. `GET /ratings` and `GET /ratings/{id}` only for buyers (farmer and trucker ratees) and the ratee, else `403 RATING_FORBIDDEN`, decided from the caller's role claim before any read; new optional `rateeRole` filter (`400 RATING_VALIDATION` for another value), default the ratee's current role; the summary from the users row when the role is current and the row carries `ratingsAggregatedAt`, else the walk filtered by role; `summary.rateeRole`; `summary.effectiveRating` and the `reputation` block behind `reputationPublicTags` (off everywhere). Create unchanged.
- **1.3.0** (2026-11-07): BUILT (ESC-28): the stored aggregate. The `/sync` rating create is the transaction of "Stored aggregate" (rating, `ADD ratingSum, ratingCount`, heal marker), a changed role puts the rating alone with `inAggregate: false`, registration stamps `ratingSum: 0`, `ratingCount: 0`, `ratingsAggregatedAt`, and the order-cron action `reputation_backfill_ratings` runs the one-time backfill. The best-effort refresh after the commit runs only with `reputationPenalties` on. Read rules stay PLANNED (ESC-29). Create wire shape unchanged.
- **1.2.0** (2026-11-07): PLANNED (ESC-27 for ESC-28 and ESC-29). Stored aggregate: the create puts the rating and `ADD`s the stars to the ratee's users row (`ratingSum`, `ratingCount`) in one transaction, conditioned on the ratee's role, plus a `HEAL` row for the daily job; a one-time backfill without Scan; a role change rebuilds it; ratings gain the server-only `inAggregate`. Read rules: `GET /ratings` and `GET /ratings/{id}` only for buyers (farmer and trucker ratees) and the ratee, else `403 RATING_FORBIDDEN` (admins use the admin reputation panel); new optional `rateeRole` filter; the summary comes from the users row when aggregated and gains `rateeRole` and `effectiveRating`; new optional `reputation` block (`reputation-api.md` 1.0.0) behind `reputationPublicTags`. Create wire shape unchanged.
- **1.1.0** (2026-10-10): ratings also open on `disbursing` (ESC-09); `RATING_FORBIDDEN` reads "You can only rate a delivered order (delivered, disbursing or settled)."
- **1.0.0** (2026-07-09) — Initial contract: buyer rates farmer/trucker after a
  delivered order; sync-create + `GET /ratings` reads; ratings immutable.
