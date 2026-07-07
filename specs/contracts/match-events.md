# Match-Events Contract (`match_event` sync entity)

**Status**: Active (logging only — no consumer yet)
**Owner**: Architect
**Related**: [`match-api.md`](./match-api.md), [`sync-protocol.md`](./sync-protocol.md)

## Purpose

Append-only telemetry of buyer match interactions. These logs are the training
corpus for a **future** learned ranker (LTR/GBDT). Nothing reads them yet — the
point is to start accumulating labelled data *now*, because a learned ranker
needs tens of thousands of logged interactions before it beats the hand-tuned
weighted sum, and that lead time only starts once logging ships.

The current ranking algorithm is unchanged: the transparent weighted sum in
`MatchScoring` (server) / `MatchScorer` (mobile). See `match-api.md`.

## Transport

Events ride the existing offline sync queue as a `match_event` entity — no new
endpoint. They queue offline and flush with the next sync batch, so logging
survives the offline-first flow and adds no online dependency.

- **entityType**: `match_event`
- **action**: `create` only (append-only; `update`/`delete` are rejected)
- **entityId**: client-generated `eventId` (UUID v4)
- **Authorization**: buyers only; `data.buyerId` must equal the caller.
- **Idempotency**: standard sync idempotency key; a duplicate `eventId` is a
  no-op success (never a conflict).

## Event payload (`data`)

| Field | Type | Required | Notes |
|-------|------|----------|-------|
| `eventId` | string (UUID) | Yes | Also the `entityId`. |
| `intentId` | string | Yes | The intent whose matches were interacted with. |
| `buyerId` | string | Yes | Must match the authenticated user. |
| `eventType` | string | Yes | One of `shown`, `opened`, `order_created`. |
| `listingId` | string | for `opened`/`order_created` | The crop interacted with. |
| `score` | number [0,1] | No | Scorer relevance at interaction time. |
| `rank` | int ≥ 0 | No | 0-based display position. |
| `isSubstitute` | bool | No | Whether the match was a substitute crop. |
| `matchCount` | int | No | Total matches shown (on `shown`). |
| `impressions` | string (JSON) | No | On `shown`: compact JSON array of `{listingId, score, rank, isSubstitute}`, capped at the top 20. |
| `occurredAt` | string (ISO 8601) | No | Client event time. |
| `policyTag` | string | No | Ranking policy version that produced the scores (currently `weighted-v1`). |

Unknown fields are rejected (`MATCH_EVENT_VALIDATION`).

## Event types

- **`shown`** — emitted once per results view, carrying the impression list
  (which listings, at which scores/ranks, were displayed).
- **`opened`** — the buyer opened a specific match (detail or order).
- **`order_created`** — an order was placed for a matched listing (the
  conversion signal a learned ranker optimises for).

## Storage

- Table: `wereda-connect-{env}-match-events`, PK=`intentId`, SK=`eventId`.
- **TTL**: item-level `ttl` attribute (epoch seconds), 90-day rolling window
  (`MatchEventRepository.RetentionDays`). Storage stays bounded at no extra cost.
- **Access**: the sync Lambda has **write-only** grant. No read path exists yet;
  a future training/export job would read via a batch/analytics path, not the
  app.

## Invariants

1. **Best-effort** — logging must never block or fail the marketplace flow. The
   mobile `MatchEventLogger` swallows all its own errors; a missing table
   (`MATCH_EVENT_UNAVAILABLE`) degrades to no-op.
2. **Append-only** — only `create`; events are immutable once written.
3. **Buyer-scoped** — a buyer can only log events for their own intents.
4. **No ranking effect** — these logs do not (yet) influence any score or order.
