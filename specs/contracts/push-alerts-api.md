# Push Alerts API Contract

## Version
1.2.0

## Parties
- **Mobile** (client) — registers device tokens, syncs buyer intents, receives push
- **Backend** (server) — stores intents/tokens, matches new crops, sends push via SNS→FCM

## Overview

Buyers are notified (Android push) when a newly listed crop matches one of their saved
purchase intents. The flow has three parts: register a device token, sync intents to the
server, and a server-side match-on-insert that publishes the push. Design:
`specs/architecture/push-notifications-architecture.md`. Setup:
`.ai/push-notifications-setup.md`.

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
| 1.2.0 | 2026-10-06 | `POST /device-tokens`: `platform` is an allow-list (`android`, the default); anything else is `400 DEVICE_TOKEN_VALIDATION`. `buyer_intent` update no longer requires an echoed `buyerId`; a mismatching one is still refused (SIM-60). |
| 1.1.0 | 2026-09-26 | `DELETE /device-tokens` unregisters the caller's push endpoint (logout on shared phones). |
| 1.0.0 | 2026-06-19 | Initial: device-token registration, buyer_intent sync, match-on-insert push. |

---

**Last Updated**: 2026-10-06
**Status**: Draft
