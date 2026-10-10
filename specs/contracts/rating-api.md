# Rating API

## Version
1.1.0

## Parties
Mobile, Backend, Infrastructure

## Overview
Lets a **buyer** rate the counterparties of a completed order — the **farmer**
and, when the order was transported, the **trucker**. Ratings are 1–5 stars with
an optional comment, are **create-only and immutable**, and there is **exactly
one rating per (order, ratee)**.

- **Writes** go through the offline-first `/sync` endpoint as `rating` create
  operations (mirrors `buyer_intent`).
- **Reads** are served by a dedicated `RatingFunction` at `GET /ratings`.

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
- `GET /ratings?rateeId=<userId>&limit=<1-50>&nextToken=<token>` — ratings a user
  received, newest first, plus an aggregate summary.
- `GET /ratings/{id}` — a single rating by its `ratingId`.

Both require a valid JWT (any authenticated user may read a ratee's aggregate —
buyers need farmer averages while browsing; farmers/truckers see their own).

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
  "summary": { "average": 4.6, "count": 12 },
  "nextToken": null
}
```

### `GET /ratings/{id}`
```json
{ "rating": { /* RatingDto as above */ } }
```

### Create result (inside the `/sync` batch response)
Standard `SyncOperationResult`: `status` is `success` (with `serverData` and
`serverVersion: 1`), `duplicate` (idempotency replay), or `conflict` (a rating
for this order+ratee already exists).

## Error Codes
| Code | HTTP | Meaning |
|------|------|---------|
| `RATING_VALIDATION` | 400 | Missing/invalid fields; unknown fields; `stars` out of 1–5; bad `rateeRole`; `ratee` is not the order's farmer/trucker; order not found; malformed `ratingId`; bad `limit`/`nextToken` |
| `RATING_UNAUTHORIZED` | 200 (sync op `error`) | Caller is not a buyer, or `raterId` ≠ authenticated user |
| `RATING_FORBIDDEN` | 200 (sync op `error`) | Order is not the buyer's, or order not `delivered`, `disbursing` or `settled` (1.1.0) |
| `RATING_UNAVAILABLE` | 200 (sync op `error`) | Ratings table not provisioned in this environment |
| `RATING_NOT_FOUND` | 404 | `GET /ratings/{id}` — no such rating |
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

## DynamoDB Schema (Ratings table)
- PK `ratingId` (STRING, `"{orderId}:{rateeId}"`) — **sole key, no sort key**, so
  `attribute_not_exists(ratingId)` on create truly enforces one-per-(order,ratee).
- GSI `RateeIdCreatedAtIndex`: PK `rateeId`, SK `createdAt`, projection ALL —
  powers `GET /ratings?rateeId=` and the aggregate summary.

## Changelog
- **1.1.0** (2026-10-10): ratings also open on `disbursing` (ESC-09); `RATING_FORBIDDEN` reads "You can only rate a delivered order (delivered, disbursing or settled)."
- **1.0.0** (2026-07-09) — Initial contract: buyer rates farmer/trucker after a
  delivered order; sync-create + `GET /ratings` reads; ratings immutable.
