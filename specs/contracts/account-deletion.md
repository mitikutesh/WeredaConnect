# Account Deletion Contract

## Version
1.2.0

## Parties
- **Mobile / Web** (client, ACC-03): Profile > "Delete account" flow, step-up re-auth, local wipe. Mobile and web are a platform pair and ship together.
- **Backend** (server, ACC-02): `DELETE /user/account` handler, the completion sweep, and the refusals other endpoints return for a deleted or deleting account.
- **Infrastructure** (ACC-02): route, IAM grants, schedule, alarms.
- **Admin** (ACC-05): renders deleted accounts as "Deleted user" in orders, disputes and audit views.
- **Public web page** (ACC-04): the Play Store "delete account" URL, which points at this same flow after login.

## Overview

A user deletes their own account from inside the app. The platform then deletes or anonymizes every piece of personal data it holds about them, and keeps the money and audit records the business and the law need, with the opaque `userId` (the Cognito `sub`) left on those records as the pseudonym.

Principles:

1. **Personal data goes, money records stay.** Phone, names, Fayda identity, location, free text, device tokens and messages are deleted or scrubbed. Orders, deliveries, escrows and the audit tables keep their ids, amounts, statuses and timestamps.
2. **Never while money is in flight.** Deletion is refused while the user is a party to an active order, an unreleased escrow, an open delivery, an open dispute or a captured payment that still awaits a manual refund. Payouts and refunds read the phone from the users row at run time, so a deletion in that window would strand money.
3. **Immediate and final.** There is no grace period. The `confirm` flag and the step-up re-auth guard against mistakes.
4. **Idempotent and resumable.** Every step can run again. A failed run returns a retryable error and resumes where it stopped. A server-side sweep finishes or aborts every started deletion within 24 hours even if the client never returns.
5. **Online only.** The request is never queued in the sync queue.

Vocabulary used below:

- **The user**: the account being deleted. **A party**: the user appearing as `buyerId`, `farmerId` or `truckerId` on a row.
- **Sentinel**: the reserved display value `Deleted user` written into copied name fields. Clients map an exact match to the localized `deletedUser` string.
- **Tombstone**: what the users row becomes after deletion (see Per-Table Policy).
- `deletionState`: a server-only users-row field with the values `in_progress`, `cognito_pending` and `completed`. Absent on a live account.

## Request Format

```
DELETE /user/account
Authorization: Bearer <Cognito ID token>
Content-Type: application/json

{ "confirm": true }
```

- JWT authorizer, same as every authenticated route. The ID token is required (the access token has no `custom:role` and no `auth_time`).
- Any body other than `{ "confirm": true }` is rejected with `400 ACCOUNT_001`.
- No query parameters. No idempotency key: the user id is the key.
- The route is online only. The client shows an offline message instead of calling, and never enqueues the request in `/sync`.

### Order of checks

The handler evaluates the request in this order and stops at the first hit:

| # | Condition | Result |
|---|-----------|--------|
| 1 | No users row for the caller | `404 AUTH_013` |
| 2 | `deletionState = completed` | `200` with the stored completion body (replay) |
| 3 | `deletionState = cognito_pending` | resume at step 12 of the deletion steps, no new step-up |
| 4 | `deletionState = in_progress` | resume at step 5b of the deletion steps, no new step-up |
| 5 | `suspended = true` | `403 ACCOUNT_003` |
| 6 | Body is not `{ "confirm": true }` | `400 ACCOUNT_001` |
| 7 | Blockers found (see Blocked While Active) | `409 ACCOUNT_002` with the blocker list |
| 8 | Token `auth_time` older than `STEP_UP_MAX_AGE_SECONDS` (300) | `403 AUTH_040` |
| 9 | None of the above | deletion runs |

Blockers are checked before the step-up so a user who cannot delete yet is not asked for their password first. The step-up uses the same rule as payments: `StepUpPolicy.IsFresh`, which reads the `auth_time` claim of the ID token. A fresh password login yields a fresh token.

## Response Format

### 200 OK (deleted, or replay after completion)

```json
{
  "deleted": true,
  "userId": "550e8400-e29b-41d4-a716-446655440000",
  "deletionState": "completed",
  "deletedAt": "2026-10-06T09:15:02.000Z"
}
```

The same body is returned for a replay after completion. The ID token still verifies for up to 15 minutes after the Cognito user is gone, which is what makes the replay reachable.

### 409 Conflict (blocked while active)

```json
{
  "error": {
    "code": "ACCOUNT_002",
    "message": "Finish or cancel your active orders, deliveries and disputes before deleting your account.",
    "blockers": [
      { "type": "order", "id": "ord-123", "status": "funded", "role": "buyer" },
      { "type": "escrow", "id": "escrow-order-ord-123", "status": "escrowed", "role": "buyer" },
      { "type": "delivery", "id": "del-77", "status": "pickedUp", "role": "buyer" },
      { "type": "dispute", "id": "ord-456", "status": "disputed", "role": "buyer" },
      { "type": "payment_review", "id": "ord-789", "status": "order_payment_unmatched", "role": "buyer" }
    ],
    "blockerCount": 5
  }
}
```

- `type` is one of `order`, `escrow`, `delivery`, `dispute`, `payment_review`.
- `role` is the user's role on that row (`buyer`, `farmer`, `trucker`).
- At most 20 blockers are listed. `blockerCount` is the full count.

### 503 Service Unavailable (started, not finished)

```json
{
  "error": {
    "code": "ACCOUNT_005",
    "message": "Account deletion is still in progress. Retry in a moment.",
    "retryAfterSeconds": 2
  },
  "deletionState": "in_progress"
}
```

`deletionState` is `in_progress` or `cognito_pending`. The client retries the same request with backoff; the server resumes. If the client never retries, the completion sweep finishes the job.

### 410 Gone (returned by other endpoints)

```json
{
  "error": {
    "code": "ACCOUNT_004",
    "message": "This account has been deleted.",
    "deletionState": "completed"
  }
}
```

See Effects on Other Endpoints.

## Error Codes

The `ACCOUNT_` prefix is new. No code with that prefix exists in server, specs, admin or mobile today.

| Code | HTTP | Meaning | Client action |
|------|------|---------|---------------|
| `ACCOUNT_001` | 400 | Body is not `{ "confirm": true }` | Client bug; show a generic error |
| `ACCOUNT_002` | 409 | Active items block deletion; `blockers` lists them | Show the list. For `payment_review` show "contact support" |
| `ACCOUNT_003` | 403 | Account is suspended; self-deletion is not allowed | Show "contact support" |
| `ACCOUNT_004` | 410 | Returned by OTHER endpoints: the caller's account is deleted or being deleted. Carries `deletionState` | `in_progress`: offer "Finish deleting" (which calls `DELETE /user/account`). Otherwise wipe local data and go to login |
| `ACCOUNT_005` | 503 | Deletion started but did not finish in this request (partial failure, Lambda deadline, or a checked audit write failed). Carries `deletionState` and `retryAfterSeconds` | Retry the same request with backoff; it resumes |
| `AUTH_040` | 403 | Password older than `STEP_UP_MAX_AGE_SECONDS` (300 s). Same rule as payments (`auth-api.md` 2.1.0) | Ask for the password, log in again, retry |
| `AUTH_013` | 404 | No users row for the caller | Sign out |
| `AUTH_007` | 500 | Unexpected error before any write | Retry later |

## Blocked While Active

Deletion is refused while the user is a party (`buyerId`, `farmerId` or `truckerId`) to any row below. **Any status this contract does not list blocks** (fail closed; this covers the future FRD `held` escrow state and anything added later).

### Orders (`order-lifecycle.md` 1.7.0 statuses)

| Status | Effect on deletion |
|--------|--------------------|
| `payment_submitted`, `funded`, `accepted`, `pickup_confirmed`, `in_transit`, `delivered`, `disbursing` | Blocks, type `order` |
| `disputed` | Blocks, type `dispute` |
| `created`, `pending_payment` | Not blocking. Cancelled by deletion step 8 |
| `payment_failed` | Not blocking and cancelled by step 8, EXCEPT when the user is the order's buyer and the order has a checkout attempt (an `order-checkout:{orderId}` slot in escrow-idempotency, consistent GetItem) or a payment reference (`cbeTransactionId`, or any `telebirr*` field): then it blocks, type `order`. The buyer's phone is the refund contact for a late capture. A farmer or trucker deletion still cancels it: the buyer's phone is untouched, so a manual refund still reaches the buyer |
| `settled`, `cancelled`, `rejected`, `dispute_resolved` | Terminal. Not blocking, left unchanged. Money still held behind one of these is caught by the escrow rule; an unapplied payment by the `payment_review` rule |

### Escrows (`escrow-protocol.md` statuses)

- Status not in `completed`, `cancelled`, `refunded` blocks, type `escrow`. `released` is not terminal (the farmer has not received the money yet). Rows with `refundPending = true` are still `escrowed` and block.
- `disputed` blocks with type `dispute`.
- An escrow with `truckerPayoutPending = true` blocks its **trucker** whatever its status, including `completed`. The trucker leg is still owed and pays out to the trucker's phone (ESC-04 removes this case once the pending leg has its own lifecycle).

### Deliveries

- A delivery that is not soft-deleted (`deletedAt` absent) and whose status is not `delivered` or `cancelled` blocks, type `delivery` (statuses `pending`, `pickedUp`, `inTransit`).
- `disputed` blocks with type `dispute`.
- A `delivered` delivery does not block by itself: its order (`delivered`, `disbursing`) and its escrow block until settlement, which covers the 48 hour dispute window.

### Payment review (buyer only)

For each of the buyer's orders that has a checkout slot, a captured payment the webhook did not apply blocks with type `payment_review` until its manual refund is recorded. The rule is the one in `escrow-protocol.md` §Additions in 1.20.0, and the server keeps it in one helper (`UnappliedPayments`) that this check must call:

- The payments are the escrow-audit rows under `escrow-order-{orderId}` with action `order_payment_unmatched`, `order_payment_duplicate` or `order_payment_unprocessed`, one payment per payment_order_id (`telebirrRef`), or per merch_order_id for a row without one.
- A payment is resolved by an `order_payment_refunded_manual` row under the same key that names its payment_order_id (for a nameless payment, its merch_order_id). `POST /admin/payments/{orderId}/refund-manual` writes that row (FUP-02). A refund row lifts only the payment it names: a second payment on the same order keeps the blocker.
- A payment that funded the order (the order's `telebirrPaymentOrderId`, or for a nameless payment its `telebirrMerchOrderId`) never blocks here: its money is in the order's escrow, which the escrow rule covers.

The buyer cannot lift this blocker alone, so the client shows "contact support": support refunds the payment in the Telebirr portal and records it, and the next attempt passes. This rule catches a captured payment on an order of any status, including one the buyer cancelled first.

### Why these rules

- `EscrowService.ResolvePayoutAsync` and `ResolveRefundPhoneAsync` read the phone from the users row when money moves. A tombstone has no phone, so a payout would fail `ESCROW_029` and a refund would fall back to the raw buyer id.
- Unapplied captured payments are refunded by hand to the buyer's phone.
- A dispute needs both parties reachable.

### Lookups (existing indexes only)

No table scan except the one in Completion Sweep.

| What | How |
|------|-----|
| Orders as a party | orders `BuyerIdCreatedAtIndex`, `FarmerIdCreatedAtIndex`, `TruckerIdCreatedAtIndex`; then a consistent GetItem on each candidate before acting on it |
| Escrows as a party | escrows `BuyerStatusIndex`, `FarmerStatusIndex`, `TruckerStatusIndex` |
| Deliveries as a party | deliveries `FarmerIdCreatedAtIndex`, `TruckerIdCreatedAtIndex`, `ParticipantIndex` (buyer). Fallback for deliveries created without `participantId`: for each of the buyer's orders, query `ListingIndex` by the order's `listingId` and keep rows whose `buyerId` is the user or whose `orderId` is that order. The same set feeds the blocker check and the anonymization |
| Checkout slots | escrow-idempotency GetItem `order-checkout:{orderId}`, consistent read |
| Payment review | escrow-audit Query PK `escrow-order-{orderId}`, only for orders with a slot; the order's own consistent read supplies `telebirrPaymentOrderId` and `telebirrMerchOrderId` |
| Ratings authored (buyer) | ratings GetItem `{orderId}:{farmerId}` and, when the order has a trucker, `{orderId}:{truckerId}`, per buyer order (there is no rater index) |
| Ratings received (farmer, trucker) | ratings `RateeIdCreatedAtIndex` |

GSIs are eventually consistent. Steps 5, 8 and 9 plus the money-entry refusals in Effects on Other Endpoints close the race between a blocker check and a concurrent payment.

## Deletion Steps (server)

Every step is idempotent. A missing item, or a Cognito or SNS NotFound, counts as done. "Checked write" means a conditional `attribute_not_exists` PutItem whose failure (other than ConditionalCheckFailed) stops the run with `503 ACCOUNT_005`.

1. Load the users row with a consistent read.
2. Run the Order of checks (state, suspension, body).
3. Blocker check.
4. Step-up check.
5. Conditional write on the users row: `deletionState = in_progress`, `deletionRequestedAt = now`. Allowed when `deletionState` is absent or already `in_progress`. From this write on, no money can enter an order of this user (see Effects on Other Endpoints).
   - 5b. Checked write of transaction-audit `account_deletion_started`: `transactionId = account-{userId}`, `timestamp = deletionRequestedAt`, `entityType = account`, `entityId = userId`, `actor = userId`, `actorRole = role`. ConditionalCheckFailed counts as done, so a resume never duplicates it.
6. Cognito `AdminUserGlobalSignOut(userId)`. Revokes the refresh tokens on every device. The `userId` is the Cognito username, so no phone is needed.
7. Withdraw the user's listings, keeping settled history. The existing client delete paths are not reused: the trip delete refuses `delivered` and `cancelled` trips and the crop delete rewrites every status to `deleted`.
   - Crops (`FarmerIdCreatedAtIndex`) in `available` or `reserved`: soft delete (`status = deleted`, `deletedAt`, `version + 1`, conditional on the read status and version) plus the scrub in the per-table policy.
   - Crops in `sold` or `deleted`: status unchanged, `deletedAt` set only if missing, scrub.
   - Trips (`TruckerIdCreatedAtIndex`) in `open` or `matched`: soft delete (`status = cancelled`, `deletedAt`, same conditions) plus the scrub.
   - Trips in `inTransit`, `delivered` or `cancelled`: status unchanged, `deletedAt` set only if missing, scrub.
   - A lost condition re-reads the row and reapplies this rule.
   - The scrubbed rows stay as tombstones so sync-down removes them from other devices and old orders still resolve their `listingId` and `tripId`.
8. Cancel the user's unpaid orders: `created`, `pending_payment`, and the `payment_failed` orders the blocker table lets through. System actor, `cancelReason = account_deleted`, written with a condition `status IN (created, pending_payment, payment_failed) AND version = <read version>`. A failed condition re-reads the order; if it is now in a blocking status, the run goes to step 9's abort with that order as a blocker. One transaction-audit `order_cancelled` row per order, best effort like the order cron. No stock change (stock moves only at funding).
9. Blocker check again (GSI queries, then a consistent read of every order found). If anything blocks: remove `deletionState` and `deletionRequestedAt`, write transaction-audit `account_deletion_aborted` (best effort), return `409 ACCOUNT_002`. Listings stay withdrawn and the cancelled orders stay cancelled; the user signs in again because step 6 revoked their sessions. This is a rare race and is documented to the user as such.
10. Apply the Per-Table Policy below.
11. Tombstone the users row: apply the allowlist, set `deletedAt = now` (this is the moment personal data is erased) and `deletionState = cognito_pending`.
12. Cognito `AdminDeleteUser(userId)`.
13. Checked write of transaction-audit `account_deleted`: `transactionId = account-{userId}`, `timestamp = deletedAt`, same entity and actor fields as 5b, `metadata` with per-table counts. Then set `deletionState = completed`. A failed audit write returns 503 and the resume reruns steps 12 and 13.
14. Return `200`.

## Failures, Idempotency and Resume

- Any failure after step 5, or the handler nearing the Lambda deadline, returns `503 ACCOUNT_005` with the current `deletionState`. A client retry of the same request resumes: `in_progress` restarts at step 5b, `cognito_pending` at step 12. Steps already done are no-ops.
- No new step-up on a resume: the step-up gated the write in step 5, and the user's sessions were revoked in step 6.
- Login on an `in_progress` row is allowed so the user can finish. Every write they attempt returns `410 ACCOUNT_004` with `deletionState = in_progress` and the client offers "Finish deleting".
- Login or refresh on a `cognito_pending` row returns `AUTH_014` (no enumeration) and retries steps 12 and 13 best effort.
- The server logs `ACCOUNT_DELETION_INCOMPLETE` on every 503 and for each row the sweep finishes or aborts.
- The handler needs more than the 10 s Lambda default for a user with a long history; the flow is resumable either way.

## Completion Sweep (bounds retention)

A scheduled job runs every 6 hours. It scans the users table for `deletionState IN (in_progress, cognito_pending)` with `deletionRequestedAt` older than 1 hour. The users table has no index on this; a filtered Scan four times a day is cheap and needs no new GSI.

- `in_progress`: reruns steps 5b to 13 as the system actor. Step 9 (and step 8's condition) still apply, so a row that became blocked in the meantime is aborted exactly as in step 9.
- `cognito_pending`: reruns steps 12 and 13.
- Bound: every started deletion completes or aborts within 24 hours of `deletionRequestedAt`. The first run that may take it comes at most 7 hours after the request (1 hour of age plus one 6 hour interval), the next ones 6 hours apart, so three runs try it within the bound. A user who got a 503 and uninstalled still has their phone, Cognito user and remaining personal data removed within that bound.
- The sweep logs `ACCOUNT_DELETION_STUCK` (alarmed) for a row it fails on twice.
- The sweep also exposes a `reapply` action that takes a list of `userId`s and reruns step 10 and 11 for each. Ops uses it after a backup restore (see Outside DynamoDB).

Delivery: an EventBridge rule `{prefix}-account-deletion-sweep` with `body.action = "sweep"`, the same shape as the escrow and order crons. Reserved concurrency 1, timeout 5 minutes.

## Per-Table Policy

### Tombstone (users row)

Allowlist: the row keeps only `userId`, `role`, `deletionState`, `deletionRequestedAt`, `deletedAt`, `createdAt`, `updatedAt` and `version` (incremented). Everything else is removed: `phoneNumber`, `displayName`, `fullName`, `woreda`, `faydaId`, `faydaVerified`, `faydaVerifiedAt`, `faydaAuthenticatedAt`, `preferredLanguage`, `isPhoneVerified`, `suspended`, `knownDevices`, `profilePhotoRef`, `buyerType`, `companyName`, and any field added later that is not on the allowlist.

- Removing `phoneNumber`, `faydaId` and `woreda` drops the row out of `PhoneIndex`, `FaydaIdIndex` and `RoleWaredaIndex` (sparse GSIs), so it is never found by phone or Fayda id again. This is also how the Fayda link is deleted: the platform stores no Fayda tokens, only these fields.
- The same phone or Fayda id can register again as a new `userId` with no history.
- The tombstone has no expiry. `UserRepository` must map a row with no phone and no woreda without throwing.

### Sentinel for copied names

Names and phones are copied onto other rows at create time (crops, trips, orders, deliveries, escrows, messages). On every row the user is a party to, the user's copied name field (`farmerName`, `truckerName`, `buyerName`, `senderName`, `recipientName`) becomes the sentinel `Deleted user` and the matching phone field is removed.

- Clients map an exact match to the localized `deletedUser` string (ACC-03 for mobile and web, ACC-05 for admin). Older clients show the English text, which is still anonymous.
- Registration and profile update reject a `displayName` or `fullName` equal to the sentinel (trimmed, case-insensitive) so a live user can never look deleted (ACC-02).

### Table by table

Every DynamoDB table in `infra/lib/stacks/database-stack.ts` and `infra/lib/constructs/` has a row. Add a row here whenever a table is added.

| Table | Action | Detail |
|-------|--------|--------|
| `users` | Anonymize (tombstone) | See Tombstone. Written in step 11 |
| `crops` | Delete (withdraw and scrub) | Step 7 rule per status. Remove `farmerPhone`, `latitude`, `longitude`, `locationName`, `description`, `audioNoteRef`; `farmerName` to sentinel. Keep `cropId`, `listingId`, `farmerId`, `cropType`, `quantity`, `pricePerKg`, `woreda`, `status`, `deletedAt`, timestamps, `version` |
| `trips` | Delete (withdraw and scrub) | Step 7 rule per status. Remove `truckerPhone`, `vehiclePlate`, `vehicleDescription`; `truckerName` to sentinel. Keep `tripId`, `truckerId`, `fromWoreda`, `toWoreda`, `departureDate`, `capacity`, `pricePerQuintal`, `matchedListings`, `status`, `deletedAt`, timestamps, `version` |
| `orders` | Anonymize (kept as money record) | The user's copied name to sentinel. Ids, `quantity`, `pricePerUnit`, `transportCost`, `paymentMethod`, `cbeTransactionId`, `telebirr*` fields, statuses, reasons and timestamps unchanged. Unpaid orders are cancelled first (step 8) |
| `deliveries` | Anonymize | The user's copied name to sentinel, phone removed. Evidence kept: `gpsPickup`, `gpsDropoff`, `pickupPhotoRef`, `dropoffPhotoRef`, `qrToken`, `qrNonce`, `gpsAnomalyFlag`, `disputeReason`, `disputedBy`, statuses, timestamps. Found via the Lookups table, including the `ListingIndex` fallback |
| `escrows` | Retain (money record) with one pseudonymization write | Money, ids, status, flags, `expiresAt`, dispute fields never change. The only write is the user's copied `buyerName` or `farmerName` to sentinel (see Open Points 2) |
| `escrow-audit` | Retain untouched | Append-only. Holds ids, `ipAddress`, notes and, for payer mismatches, the last 4 phone digits (`payerSuffix`). Never rewritten. Read by the `payment_review` blocker |
| `transaction-audit` | Retain untouched, append only | Never rewritten. Deletion appends `account_deletion_started` and `account_deleted` (checked writes), `account_deletion_aborted` on a step 9 abort, and one `order_cancelled` per step 8 order |
| `payments` | Retain untouched | Legacy money table from the first escrow design. The admin function holds a read grant and loads the table name, but no code path reads or writes it today |
| `ratings` | Anonymize | Remove `comment`; keep `stars` (they back the counterparty's reputation), ids, `rateeRole`, timestamps. Authored ratings (buyer) via GetItem per buyer order; received ratings (farmer, trucker) via `RateeIdCreatedAtIndex`. This is a system erasure and the one exception to `rating-api.md` invariant 4 |
| `messages` | Delete | Hard delete every message where `senderId` or `recipientId` is the user (`SenderIdCreatedAtIndex`, `RecipientIdCreatedAtIndex`). The message stream consumer ignores REMOVE. The counterparty's device may keep its local copy (create-only entity, no tombstone is synced) |
| `intents` | Delete | Hard delete every row with PK `buyerId = userId`. Read the `intentId`s first for the next row |
| `match-events` | Delete | For each of the user's intents, hard delete the rows with PK `intentId`. Any row missed expires through the 90 day `ttl` |
| `device-tokens` | Delete | Per row (PK `userId`, SK `endpointArn`): SNS `DeleteEndpoint(endpointArn)`, then DeleteItem. NotFound counts as done |
| `fraud-signals` | Retain until TTL | Fraud prevention. PK `userId`, SK `signalKey`, weight and rule text; `expiresAt` 30 days after the signal. Not rewritten |
| `rate-limits` | Retain until TTL | Counters and auth state keyed by phone or `userId` (`LOGIN#`, `REGISTER#`, `OTP#`, `OTPSEND#`, `SYNC#`, `IP#`, `AUTHSTATE#OTP#`, `AUTHSTATE#OIDC#`, `AUTHSTATE#JTI#`). `expiresAt` within 24 hours. Kept so abuse limits still apply to a phone that re-registers; the OIDC state rows are the only Fayda-related rows outside the users row and hold no identity data |
| `sync-metadata` | Retain until TTL | Idempotency keys `{userId}:{entityType}:{entityId}:{action}:{timestamp}` plus a result string; `expiresAt` 7 days. Deleted-account syncs are refused before this table is touched |
| `escrow-idempotency` | Retain until TTL | Money idempotency rows (30 days); ESC-01 checkout slot and mapping rows (`buyerId`, amount, ids only) 400 days; webhook inbox rows 30 days; FUP-02 refund claim rows (order, payment and refund ids, admin id; no buyer data) 400 days. Read by the blocker check, never written by deletion |
| `delivery-qr-secrets` | Retain until TTL | Per-delivery nonce and consumption flags, no personal data; `expiresAt` 30 days |
| `fayda-mock-codes` | Retain until TTL | Dev and staging only (`infra/lib/constructs/fayda-mock-construct.ts`). Short-lived mock authorization codes with `expiresAt`. Not deployed in prod |

Client-side drafts (unsynced Isar rows, the sync queue, unsent message text) exist only on the device. The server holds no draft table; ACC-03 wipes them locally.

## Outside DynamoDB

| Store | Action |
|-------|--------|
| Cognito user pool | `AdminUserGlobalSignOut` (step 6), then `AdminDeleteUser` (step 12). The username is the `userId` |
| SNS platform endpoints | Deleted with the device-tokens rows |
| S3 uploads bucket | Delete the object at `profilePhotoRef` if set (there is no upload pipeline for it today, so this is usually a no-op). Delivery photos under `photos/` are evidence and are kept under the existing lifecycle rule (Glacier after 1 year) |
| CloudWatch logs | Not rewritten. Lambda logs expire after 3 months by default; the escrow, escrow cron, Telebirr webhook and API access logs keep 1 year |
| Point-in-time recovery | Every table built with the shared `WeredaDynamoTable` construct has PITR (35 days); the dev-only `fayda-mock-codes` table has none. Restores can bring deleted data back. Before a restored table is put in service, ops runs the sweep's `reapply` action for every `userId` found in either replay source: transaction-audit `account_deleted` rows (checked writes, so none is missing) and the live users tombstones. `reapply` is idempotent like every step |
| Fayda, Telebirr | No call is made. The platform stores no Fayda tokens. Telebirr keeps its own transaction records under its own policy |

## Effects on Other Endpoints (ACC-02)

### Caller side

Once the caller's users row carries `deletionState`, every handler that writes for the caller returns `410 ACCOUNT_004` with that `deletionState`: `POST /sync` (the whole request), `/escrow/*`, `/user/fayda-start`, `/user/fayda-link`, `POST /device-tokens`. Reads may keep working until the ID token expires (15 minutes or less) and see only scrubbed data.

### Counterparty money entry points

Each of these refuses an order whose buyer, farmer or trucker users row has `deletionState` set, read with a consistent read (`UserRepository.GetUserByIdAsync` gains a consistent option):

| Entry point | Refusal |
|-------------|---------|
| `POST /sync` order transition to `payment_submitted` | Extends the existing payment KYC gate, which reads the buyer and farmer today; the trucker is added. Op-level `ORDER_VALIDATION`, message "a party to this order has deleted their account" |
| `POST /escrow/order-checkout` (ESC-01) | `422 ESCROW_030`, "the order cannot be paid online" |
| `POST /escrow/initiate` | `422 ESCROW_030`, same message |

Without this a buyer could fund an order of a farmer mid-deletion and the payout would later fail `ESCROW_029`. An order created against a listing in the window between steps 5 and 7 can never be paid; step 8 or the 24 hour order cron cancels it.

### Other rules

- Login checks `deletionState` next to the existing suspended check (`LoginFunction`).
- Sync creates that name a deleted counterparty are rejected with op-level `SYNC_VALIDATION`: a message to a deleted recipient, a rating for a deleted ratee.
- Orders against withdrawn listings are already refused (the listing is not `available`).
- `sync_executor` and `sync_executor_web` treat 410 as terminal (no retry).

## Client Obligations (ACC-03, mobile and web pair)

- Entry: Profile > "Delete account" for Farmer, Trucker and Buyer alike. The screen explains what is deleted (profile, phone, Fayda link, listings, messages, device tokens, intents) and what is kept anonymized (orders, deliveries, escrow and audit records).
- The call is online only. Offline, show the localized offline message and do not enqueue anything.
- Flow: explain, confirm, `DELETE /user/account`. On `409` show the blockers (a `payment_review` blocker shows "contact support"). On `AUTH_040` ask for the password, log in again, retry. On `503` retry automatically with backoff.
- On `200`, or on `410 ACCOUNT_004` with a state other than `in_progress`, wipe local state: Isar `clearAllData` (web stub no-op), the sync queue (mobile and web), auth storage (secure and web), the offline auth cache (both), the FCM token, shared_preferences keys (message read markers, per-account data usage), unsynced drafts. Then navigate to login.
- Map the sentinel to the `deletedUser` key in all 3 arb files (en, am, om). Deletion screen, confirm and blocker strings (one per blocker type, including `payment_review`) in all 3 languages.
- Known limit: another device still offline can log in from its cached hash until it next reaches the server, then receives `410` or `AUTH_014` and wipes.
- e2e tests use throwaway accounts. The shared dev test users (`+2519001000xx`) are never deleted.

## Admin Obligations (ACC-05)

- Render a tombstone, and any copied name equal to the sentinel, as "Deleted user (role)" in orders, deliveries, disputes, transactions and audit views.
- Audit actors that resolve to a tombstone render the same way.
- Render the `account` `entityType` and the `account_deletion_started`, `account_deleted` and `account_deletion_aborted` actions in the audit feed.
- Refuse suspend and role change on a tombstone.

## Retention

| Data | Retention after deletion |
|------|--------------------------|
| Live personal data in DynamoDB, Cognito, SNS | Gone when the request returns `200`; otherwise within 24 hours of `deletionRequestedAt` (sweep bound) |
| TTL rows (`fraud-signals`, `rate-limits`, `sync-metadata`, `escrow-idempotency`, `delivery-qr-secrets`, `match-events`, `fayda-mock-codes`) | Expire per the table above, 400 days at most, all pseudonymous (`userId` only) |
| Backups (PITR) | 35 days or less; `reapply` after any restore |
| CloudWatch logs | 3 months by default, 1 year at most |
| Kept money and audit rows (`orders`, `deliveries`, `escrows`, `escrow-audit`, `transaction-audit`, `payments`) and the users tombstone | Today's retention: no automatic expiry |

Open for legal review: the retention period for financial records and a purge job after it. Until that is decided nothing is purged, which is today's behaviour.

## Invariants

1. Deletion never runs while money is held or moving, while a payout leg is owed, while a captured payment awaits a manual refund, or while a dispute is open.
2. No new money enters an order once any of its parties has `deletionState` set.
3. No personal data stays in the users row or in Cognito after `completed`.
4. A started deletion completes or aborts within 24 hours (sweep).
5. Kept money and audit rows keep their ids, amounts and statuses unchanged; terminal listing and order statuses are never rewritten.
6. Audit tables are never rewritten; `account_deletion_started` and `account_deleted` are checked writes, so every finished deletion has exactly one of each.
7. Every step is idempotent; a retry or the sweep resumes without duplicating side effects.
8. Self-deletion is never queued offline and never flows through `/sync`.
9. A suspended account cannot self-delete.
10. Role visibility is unchanged: buyers stop seeing a deleted farmer's crops and a deleted trucker's trips because those rows carry `deletedAt`, which the browse queries already exclude. Farmers and truckers still see only their own rows.
11. Every DynamoDB table has a row in the per-table policy.

## Interaction With Other Contracts

- This contract takes precedence over `rating-api.md` invariant 4 (ratings immutable) and the create-only rule in `messaging.md`, only for the system erasure described here.
- `user-entity.md` 1.1.0 replaces its "deletion not supported" rule with a pointer to this contract and documents `deletionState`, `deletionRequestedAt` and `deletedAt`.
- When ACC-02 ships, it adds the `ACCOUNT_004` row to `sync-protocol.md` and `auth-api.md`, and the deleted-party refusal to `order-lifecycle.md` and `escrow-protocol.md`, each with a version bump.
- The `payment_review` resolution row, its admin endpoint and the matching rule are in `escrow-protocol.md` 1.20.0 (FUP-02; reserved in 1.8.0 by ESC-02).

## Open Points

1. Legal retention period for financial records and a purge job (default: keep, as today).
2. Escrows: the single copied-name write follows the principle that no personal data stays, but differs from the ACC-01 task row's "retain untouched". Reverting is a one-row change in the table above.
3. Ban evasion: suspended users cannot self-delete, so no hash of the phone or Fayda id is kept. The FRD epic may revisit.
4. Admin-initiated deletion (support requests from the ACC-04 page, suspended users, users stuck on `payment_review`) is out of scope and needs its own task.

## Implementation Status (1.2.0, ACC-02)

Built: `server/src/Functions/AccountDeletionFunction.cs`, `Services/AccountDeletionService.cs`, `Services/AccountDeletionPolicy.cs`, `Data/AccountDeletionRepository.cs`, `Helpers/AccountDeletionGuard.cs`; infrastructure in `infra/lib/stacks/api-stack.ts` and `monitoring-stack.ts`. The wire shape of this contract (route, body, responses, codes, statuses) is unchanged.

- `DELETE /user/account` (JWT authorizer, payload 1.0) runs the Order of checks, the blocker check, deletion steps 5 to 14 and the resume rules. Lambda `{prefix}-account-deletion`, 30 seconds, reserved concurrency 5 in staging and prod.
- The Completion Sweep runs in its own Lambda `{prefix}-account-deletion-sweep` with the same handler (5 minutes, reserved concurrency 1 in staging and prod), from the EventBridge rule `{prefix}-account-deletion-sweep` every 6 hours with `body.action = "sweep"`. The dev account sets no reserved concurrency on any Lambda (`WeredaLambda` drops it there: the account limit is too low), so in dev the sweep is not single-flight; two overlapping runs are safe because every step is idempotent. `{"action":"reapply","userIds":[...]}` is accepted by a direct invoke. An API Gateway request (it always carries a request context) never reaches either action.
- Caller-side `410 ACCOUNT_004` (with `error.deletionState`, read strongly consistent): `POST /sync` (whole request), `POST /escrow/initiate`, `POST /escrow/order-checkout`, `POST /escrow/dispute`, `POST /user/fayda-start`, `POST /user/fayda-link` (checked before the state is read or the code exchanged), `POST /device-tokens`. `DELETE /device-tokens` stays open so a logout can still unregister a phone; `/escrow/release` and `/escrow/refund` are admin routes, not writes for the caller. When the caller's row cannot be read the endpoint answers its own retryable error (`503 SYNC_SERVER_ERROR`, `503 ESCROW_012`, `503 AUTH_008` on `/user/fayda-*`, `500 DEVICE_TOKEN_SERVER_ERROR`) and writes nothing.
- Login and refresh answer `401 AUTH_014` for a `cognito_pending` or `completed` row (refresh also revokes the refresh token); `in_progress` logs in (`auth-api.md` 2.11.0).
- Sync creates naming a deleted counterparty: a message to a recipient whose row has `deletionState`, and a rating about such a ratee, are op-level `SYNC_VALIDATION` (`sync-protocol.md` 1.26.0).
- Counterparty money entry points (`order-lifecycle.md` 1.26.0, `escrow-protocol.md` 1.28.0): the buyer's, farmer's and trucker's rows are read strongly consistent and any `deletionState` refuses the payment, with nothing written:
  - `/sync` order `payment_submitted`: `ORDER_VALIDATION: A party to this order has deleted their account.` After FRD-04's risk read and CRITICAL check and before the KYC rows (part of the KYC gate, so a deleted farmer is not reported as an unverified phone), before the step-up.
  - `POST /escrow/order-checkout`: `422 ESCROW_030` (`This order cannot be paid online.`). After FRD-04's role, body, CRITICAL and velocity checks, as one of the order checks: after the existing ones and before the checkout slot (an open checkout is not handed back either) and any Telebirr call.
  - `POST /escrow/initiate`: `422 ESCROW_030`. After FRD-04's role, CRITICAL, step-up and velocity checks, in the user validation once the delivery and its order named the parties (the trucker comes from the order, else the delivery).
  A failed read fails closed: the sync operation answers `Internal server error.` (retried), the escrow routes `500 ESCROW_012`.
- A refund to a deleted buyer goes to the buyer id, never to the tombstone's empty phone, and the manual payout queue shows no phone for a deleted payee (who is not payable: no verified phone).
- Registration refuses a `displayName` equal to the sentinel (trimmed, any case) with `400 AUTH_007`. There is no profile update endpoint; `fullName` comes from Fayda and is not checked.
- `UserRepository` maps a tombstone (no phone, no woreda) and every profile write (`UpdateUserAsync`) is conditional on `attribute_not_exists(deletionState)`, so no write can put personal data back on a deleting or deleted row.
- Alarm `{prefix}-account-deletion-stuck` on `ACCOUNT_DELETION_STUCK` in the sweep log group.

How the implementation reads points this contract leaves open:

1. A request stops starting writes 24 seconds in (API Gateway ends it at 30) or when the Lambda has 4 seconds left, and answers `503 ACCOUNT_005`; the 503 also carries a `Retry-After: 2` header. A request that runs out of time before step 5 answers `500 AUTH_007` (nothing was written).
2. Every party index is queried whatever the user's role (an admin role change can leave rows under another role). Escrow and delivery index rows are used as read; a non-terminal order is re-read strongly consistent.
3. `payment_review` goes through `UnappliedPayments.Evaluate`, the function `HasOpen` wraps, so the 409 lists one blocker per open payment; its `status` is that payment's first unapplied action.
4. "Fails on twice": the sweep logs `ACCOUNT_DELETION_STUCK` when it fails on a row whose `deletionRequestedAt` is at least 7 hours old, that is a row the previous 6 hourly run had too. No counter is stored (the tombstone allowlist stays as it is). A run that ran out of time inside a row stops there: that row is neither counted as failed nor logged as stuck, and the next run starts over. The sweep checks its time before each row too.
5. `account_deletion_started` and `account_deleted` name the user as `actor` with their role, whoever runs the step. `order_cancelled` rows have actor `system` and `cancelReason=account_deleted` in `metadata`; `account_deletion_aborted` has the actor of the run (the user on a request, `system` in the sweep). The `account_deleted` metadata holds the counts of rows that invocation wrote, plus `resumed=true` when it resumed a started deletion.
6. A copied name becomes the sentinel only on a row that has that field (no field is added to a money record). Orders and deliveries also get a new `updatedAt` so pulls see the change, without a `version` bump; escrows get no other change. Withdrawn and scrubbed listings take `version + 1` and a new `updatedAt`. A listing already withdrawn and scrubbed is not written again.
7. Step 8 retries the cancel of one order three times (each after a fresh consistent read); an order still unpaid after the third lost condition is neither cancelled nor a blocker, so the run answers `503 ACCOUNT_005` and the resume decides on it.
8. `reapply` acts only on a userId whose row is `completed` or whose `account-{userId}` trail holds `account_deleted`, so a mistyped id cannot erase a live account; a row still `in_progress` or `cognito_pending` without that audit row is left to the sweep. It reruns steps 10 and 11 (step 10 there includes the step 7 listing rule), ends `completed` and keeps the original `deletedAt`, so it writes no second `account_deleted` row.

Not built yet (follow-ups):

- Login and refresh on `cognito_pending` do not retry steps 12 and 13 themselves; the client's retry of `DELETE /user/account` or the next sweep run does.
- No S3 call for `profilePhotoRef`: nothing writes that field today. Add the delete when an upload pipeline lands.
- A `pending_payment` order of the buyer is cancelled by step 8 even when it carries a checkout slot from an earlier attempt; the `payment_failed` rule (slot or payment reference keeps it blocking) does not cover it. A late capture of that earlier checkout would then need the `payment_review` blocker, which only sees it once the webhook recorded it. Decide whether the slot rule should apply to `pending_payment` too.

## Changelog

| Version | Date | Change |
|---------|------|--------|
| 1.2.0 | 2026-10-23 | Built (ACC-02): Implementation Status added: the endpoint, the steps, the sweep Lambda, `reapply`, the caller-side `410 ACCOUNT_004` endpoints, login and refresh `AUTH_014`, the counterparty money entry refusals and their place in FRD-04's gate order (`order-lifecycle.md` 1.26.0, `escrow-protocol.md` 1.28.0), the sync refusals of a deleted counterparty, the sentinel display name refusal, the stuck alarm; the sweep runs every 6 hours (was 12) so the 24 hour bound holds with three runs; eight points the contract left open (request time budget, party indexes, one `payment_review` blocker per payment, the stateless stuck rule and budget cuts, audit actors and counts, where scrubs write, step 8 lost races, `reapply` guard); the login and refresh retry of steps 12 and 13, the `profilePhotoRef` delete and the `pending_payment` slot question are listed as not built. No wire change |
| 1.1.0 | 2026-10-23 | `payment_review` resolution defined (FUP-02, `escrow-protocol.md` 1.20.0): one blocker per unapplied payment (by payment_order_id, or merch_order_id for a nameless one), lifted by the `order_payment_refunded_manual` row naming that payment whenever it was written, not by any later row of the order; a payment that funded the order never blocks; the admin endpoint that records the refund exists, so the blocker is no longer permanent and "contact support" now leads to a resolution; escrow-idempotency's per-table row lists the refund claim rows (400 days) |
| 1.0.0 | 2026-10-06 | Initial contract (ACC-01): endpoint, error codes, blocked-while-active rule, deletion steps, sweep, per-table policy for all 20 tables, retention, client and admin obligations |

---

**Last Updated**: 2026-10-23
**Version**: 1.2.0
