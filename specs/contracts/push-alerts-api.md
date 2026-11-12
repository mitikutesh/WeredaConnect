# Push Alerts API Contract

## Version
1.5.0

## Parties
- **Mobile** (client): registers device tokens, syncs buyer intents, reads them back (1.5.0), receives push
- **Backend** (server) — stores intents/tokens, matches new crops, sends push via SNS→FCM

## Overview

Buyers are notified (Android push) when a newly listed crop matches one of their saved
purchase intents. The flow has three parts: register a device token, sync intents to the
server, and a server-side match-on-insert that publishes the push. Design:
`specs/architecture/push-notifications-architecture.md`. Setup:
`.ai/push-notifications-setup.md`.

Order, delivery and money pushes (an order funded, a transport request, a payout sent, a
dispute opened) are specified in `notifications-api.md` (1.3.0, ESC-21). They share the device
token registration of section 1 and the same SNS platform application, and the clients tell
the three push families apart by the `data.type` discriminator: chat pushes carry
`message` (`messaging.md`), order pushes carry `order`, and the match alerts of section 3
have no `type` at all.

---

## 1. Register Device Token

**Method**: POST /device-tokens
**Authentication**: Required (Cognito JWT). The endpoint is registered under the
authenticated user. Any authenticated role may register; only buyers receive match alerts
(matching is intent-driven).

**Request Body**:
```json
{ "token": "<FCM registration token>", "platform": "android" }
```

`platform` is optional and defaults to `android`, the only allowed value (compared after
trim and lower-case; 1.2.0). The SNS platform application is configured for FCM, so a
token for any other platform could never be delivered to; it is refused instead of stored.

**Response (200)**:
```json
{ "success": true }
```

**Behavior**: the server calls SNS `CreatePlatformEndpoint` for the token (under the
`{prefix}-fcm` platform application) and stores `userId → endpointArn`. Re-registering the
same token is idempotent. The client should register on login and on FCM token refresh.

**Shared phone: the endpoint follows the registering user (1.4.0, FUP-121).** One FCM token
means one SNS endpoint, whose `CustomUserData` names the user it belongs to. When a user
logs out offline, their `DELETE /device-tokens` (section 4) never arrives, and the next
user who signs in on the phone registers the same token. Registration therefore always:

1. Creates the endpoint, or, when SNS refuses because the token's endpoint carries other
   attributes ("Endpoint arn:... already exists with the same Token, but different
   attributes"), takes the ARN SNS names in that refusal.
2. Reads the endpoint's attributes (`GetEndpointAttributes`).
3. If it is the caller's, enabled and holds this token, changes nothing.
4. If it belongs to another user, deletes that user's row `(previousUserId, endpointArn)`
   first, then rebinds the endpoint (`SetEndpointAttributes`: `CustomUserData` = caller,
   `Token`, `Enabled` = `true`). The same rebind re-enables the caller's own endpoint when
   SNS disabled it or it holds an older token.
5. Stores the caller's row.

Pushes are routed by the recipient's rows, so after step 4 none of the previous user's
pushes reaches the phone. If the endpoint was deleted between steps 1 and 4 it is created
once more; a second disappearance is a `500`. If the previous user's row cannot be deleted
the call fails closed: `500 DEVICE_TOKEN_SERVER_ERROR`, the endpoint is not rebound (so it
still names the previous user and the next registration repeats the claim) and is disabled
(best effort), so the previous user's pushes stop reaching the phone and their publishers
prune the row. The response never names the previous user, and the logs carry neither user
ids nor tokens. Same for every role. No wire change: the request and the responses are
unchanged.

**Error Codes**:
- `400 DEVICE_TOKEN_VALIDATION`: missing/empty `token`, malformed body, or a `platform` other than `android` (1.2.0)
- `401 DEVICE_TOKEN_UNAUTHORIZED` — no authenticated user
- `500 DEVICE_TOKEN_SERVER_ERROR` — SNS/storage failure

---

## 2. Sync Buyer Intents

**Method**: POST /sync (existing sync protocol; new `entityType`)
**Authentication**: Required. **Buyer role only**. On `create` the payload `buyerId` is
required and must equal the authenticated user. On `update` it is optional (the row is
keyed by the caller) and, when sent, must still equal the authenticated user (1.2.0).
`delete` is keyed by the caller and ignores it.

**Operation** (`entityType: "buyer_intent"`, actions `create` / `update` / `delete`):
```json
{
  "operationId": "uuid",
  "idempotencyKey": "buyer-1:buyer_intent:intent-1:create:<ts>",
  "entityType": "buyer_intent",
  "entityId": "intent-1",
  "action": "create",
  "data": {
    "intentId": "intent-1",
    "buyerId": "buyer-1",
    "cropType": "wheat",
    "quantityNeeded": 10,
    "maxPricePerKg": 120,
    "deliveryWoreda": "Adama",
    "deliveryLatitude": 8.54,
    "deliveryLongitude": 39.27,
    "status": "active"
  },
  "timestamp": "2026-06-19T10:00:00Z"
}
```

**Allowed `data` fields**: `intentId`, `buyerId`, `cropType`, `quantityNeeded`,
`maxPricePerKg`, `deliveryWoreda`, `deliveryLatitude`, `deliveryLongitude`, `status`,
`createdAt`. Unknown fields are rejected. `status` is forced to `active` on create.
`deliveryLatitude`/`deliveryLongitude` are resolved client-side from the woreda
(`WoredaCentroids`) so the server can score geo-distance.

**Per-operation result** (within the standard `/sync` batch response): `success`,
`conflict` (version), or `error`.

**Error messages** (in the operation result): `INTENT_UNAUTHORIZED` (non-buyer or buyerId
mismatch), `INTENT_VALIDATION` (unknown field / invalid value), `INTENT_UNAVAILABLE`
(intent sync not enabled on this environment).

**Table**: `{prefix}-intents`, PK `buyerId`, SK `intentId`; GSI `CropTypeStatusIndex`
(PK `cropType`) for match-on-insert.

---

## 2a. List the Buyer's Own Intents (1.5.0, SIM-56)

**Method**: GET /intents
**Authentication**: Required (Cognito ID token, the mobile JWT authorizer). **Buyer role
only.** Served by the match Lambda (the `GET /match` integration).

Section 2 only writes intents. Without a read, a web reload (the web app keeps no local
store) or a new phone showed an empty My Requests while the requests still drove the
match-on-insert pushes. This read gives them back.

**Request**: no body and no parameters. The partition read is the caller's own id (the JWT
`sub`); a `buyerId` or any other query parameter is ignored, so a buyer only ever reads their
own requests (role visibility: own intents).

**Response (200)**:
```json
{
  "intents": [
    {
      "intentId": "intent-1",
      "cropType": "wheat",
      "quantityNeeded": 10,
      "maxPricePerKg": 120,
      "deliveryWoreda": "Adama",
      "deliveryLatitude": 8.54,
      "deliveryLongitude": 39.27,
      "neededByDate": "2026-12-01T00:00:00.000Z",
      "status": "active",
      "createdAt": "2026-06-19T10:00:00.0000000Z",
      "updatedAt": "2026-06-19T10:00:00.0000000Z",
      "version": 1
    }
  ],
  "count": 1
}
```

- Only `active` rows (a row without `status` counts as active). The app deletes a fulfilled
  request from the server; an `update` that stored `fulfilled` or `expired` is left out too.
- Newest first by `createdAt`. Every page of the partition is read (capped at 50 pages).
- `buyerId` is not repeated (every row is the caller's). Absent optional fields
  (`deliveryLatitude`, `deliveryLongitude`, `neededByDate`, `updatedAt`) are omitted. A row
  without `intentId` or `cropType` is left out.
- `quantityNeeded` is in quintals, `maxPricePerKg` in ETB, as the app sent them.
- Not returned: the app's local-only fields (`placeId`, the match count and the "new
  matches" snapshot); the app keeps its own copies and recomputes matches.

**Error Codes**:
- `401 INTENT_UNAUTHORIZED`: no authenticated user
- `403 INTENT_FORBIDDEN`: the caller is not a buyer (farmer, trucker, or a token without
  `custom:role`); nothing is read
- `405 INTENT_VALIDATION`: a method other than GET
- `503 INTENT_UNAVAILABLE`: intents are not provisioned on this environment
- `500 INTENT_SERVER_ERROR`: the read failed

**Clients (SIM-56).** Both apps read it when My Requests loads and on its pull to refresh,
best effort: offline or on any error the list shows what the device has.
- Mobile (Isar): a server row the phone does not have is added (active, match count 0);
  a row the phone has is never overwritten (the phone's own changes win); a row with an
  open `buyer_intent` operation in the sync queue (a delete or fulfil not sent yet) is not
  added back. Nothing local is removed because the server lacks it (a create still queued,
  or refused, stays on the phone).
- Web (memory only): the server's rows fill the session list, except a request deleted or
  fulfilled in this session; a request created in this session stays even when its sync
  failed.

---

## 3. Match-on-insert Alert (server-internal)

Not a client API. When a crop listing is inserted into the crops table, a DynamoDB Stream
(`NEW_IMAGE`, filtered to `eventName=INSERT`) invokes `CropMatchStreamFunction`, which:
1. skips non-`available` crops;
2. looks up active intents whose wanted type is the crop's type or a type for which the
   crop is an accepted substitute (`MatchScoring.CandidateCropTypes`);
3. scores each with `MatchScoring` (same weights as `GET /match` and the mobile scorer) and
   selects matches above the alert threshold;
4. publishes a push to each matching buyer's SNS endpoints.

**Push payload** (FCM, via SNS GCM message structure):
```json
{
  "notification": { "title": "New wheat listing", "body": "A new wheat listing in Adama matches your request." },
  "data": { "intentId": "intent-1", "cropId": "crop-1" }
}
```
The client uses `data.intentId` to open the buyer's match results on tap. No PII is sent.

---

## Invariants

1. **Offline-first** — intent creation and the on-device "N new matches" badge work without
   connectivity; push is additive. A buyer without a registered token still sees matches
   in-app on next open.
2. **Buyer ownership** — intents are owned by `buyerId == sub`; mismatches are rejected.
3. **Scoring parity** — the push match uses the same `MatchScoring` as `GET /match` and the
   mobile `MatchScorer`.
4. **INSERT only** — updates/deletes to crops do not trigger alerts.
5. **No numeric stream filtering** — price/quantity matching happens in the Lambda
   (DynamoDB stream filters can't match numeric ranges); the stream filter is `eventName`
   only.

## 4. Unregister Device Token (1.1.0)

`DELETE /device-tokens` (Bearer) with body `{"token": "<fcm token>"}` removes the caller's
registration for that token and deletes the SNS endpoint (best effort). `204` whether or
not the token was registered; `400 DEVICE_TOKEN_VALIDATION` without a token. Only the
caller's own registration can be removed. The mobile client calls it during logout, before
clearing the session, with a 3 second timeout, so the next user of a shared phone does not
receive the previous user's alerts.

---

## Changelog

| Version | Date | Change |
|---------|------|--------|
| 1.5.0 | 2026-11-12 | `GET /intents` (section 2a, SIM-56): the buyer reads back their own active requests, keyed by the JWT `sub` (no parameter can name another buyer); buyers only (`403 INTENT_FORBIDDEN` otherwise), `503 INTENT_UNAVAILABLE` without an intents table. Served by the match Lambda, which gains `INTENTS_TABLE` and `dynamodb:Query` on that table only. The apps merge it into My Requests on load and on refresh (mobile adds what the phone lacks, web fills the session list). Additive: no change to section 2 |
| 1.4.0 | 2026-11-06 | `POST /device-tokens` on a shared phone: when the token's endpoint is bound to another user (their logout's DELETE was skipped offline), that user's row is deleted, then the endpoint is rebound to the caller; a disabled or stale endpoint of the caller is re-enabled; a failed delete fails closed (section 1, FUP-121). No wire change. |
| 1.3.0 | 2026-10-26 | Overview names `notifications-api.md` for order, delivery and money pushes: same device tokens and platform application, told apart by `data.type` (`message`, `order`, none for match alerts). No wire change here (ESC-21). |
| 1.2.0 | 2026-10-06 | `POST /device-tokens`: `platform` is an allow-list (`android`, the default); anything else is `400 DEVICE_TOKEN_VALIDATION`. `buyer_intent` update no longer requires an echoed `buyerId`; a mismatching one is still refused (SIM-60). |
| 1.1.0 | 2026-09-26 | `DELETE /device-tokens` unregisters the caller's push endpoint (logout on shared phones). |
| 1.0.0 | 2026-06-19 | Initial: device-token registration, buyer_intent sync, match-on-insert push. |

---

**Last Updated**: 2026-11-12
**Status**: Draft
