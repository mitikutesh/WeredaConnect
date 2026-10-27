# Push Notifications Architecture — Buyer Match Alerts

**Status**: Design (implementation in progress — Step 5 of the buyer intent-matching series)
**Feature**: Notify a buyer (Android push) when a newly listed crop matches one of their saved purchase intents.
**Setup runbook**: `.ai/push-notifications-setup.md` · **Contract**: `specs/contracts/push-alerts-api.md`

---

## 1. Goal & context

Buyers save purchase intents (crop, quantity, budget, delivery woreda). Today those
intents are **local-only** (Isar) and a buyer only sees new matches when they open the app
(on-device recompute — see the count-delta badge in `new_matches.dart`). This feature adds
**server-side, closed-app alerting**: when a farmer lists a crop, the backend checks it
against every active buyer intent and pushes a notification to matching buyers.

Constraints (from `CLAUDE.md`):
- Offline-first: intent creation and the badge keep working with no connectivity; push is an
  enhancement, never a dependency.
- AOT server: no reflection; System.Text.Json source generators; manual DynamoDB marshalling.
- Role visibility: only buyers own intents; matching reads ALL crops (buyer-scope), consistent
  with `GET /match` and `GET /crops`.

## 2. Why FCM via SNS

Android push must traverse **FCM** (Google's gateway) — AWS has no native channel to devices.
**AWS SNS Mobile Push wraps FCM**: register the FCM v1 service-account credential once on an
SNS *Platform Application*, create one *endpoint* per device token, and `Publish` to the
endpoint ARN. This keeps all backend logic on AWS and avoids implementing Google
service-account OAuth (JWT/RS256) inside .NET AOT. FCM is free; SNS push is free to 1M/month.

Channel was chosen over SMS (per-message cost to Ethiopia is high) and in-app-only (no
closed-app delivery). See the decision discussion in the Step 5 plan.

## 3. Components

| Component | Type | Responsibility |
|---|---|---|
| `intents` table | DynamoDB | Server copy of buyer intents (synced via `/sync`). PK `buyerId`, SK `intentId`; GSI `CropTypeStatusIndex` (PK `cropType`) for match-on-insert lookup. |
| `device-tokens` table | DynamoDB | `userId → endpointArn` (+ platform, token, timestamps). PK `userId`, SK `endpointArn`. |
| crops table **Stream** | DynamoDB Streams | `NEW_IMAGE`; emits a record per crop write. |
| `DeviceTokenFunction` | Lambda (`POST /device-tokens`) | Registers a device: `SNS CreatePlatformEndpoint(token)` → store `endpointArn`. Buyer/Admin only. |
| `CropMatchStreamFunction` | Lambda (stream consumer) | On `INSERT` of an available crop: find candidate intents, score with `MatchScoring`, publish to each matching buyer's endpoint. |
| SNS Platform Application | SNS (`wereda-connect-{env}-fcm`) | Holds FCM v1 credential; relays publishes to FCM. |
| `SyncFunction` (extended) | Lambda | New `buyer_intent` entityType handler (create/update/delete) writing to `intents`. |
| orders, deliveries, escrows table **Streams** | DynamoDB Streams | `NEW_AND_OLD_IMAGES` (ESC-21): the rules compare both images to tell what changed. Event-source filters: orders `MODIFY`, deliveries `INSERT`, escrows `MODIFY`. |
| `notifications` table | DynamoDB | The inbox (ESC-21). PK `userId`, SK `{createdAt}#{type}#{entityId}`, TTL `expiresAt` (90 days). The conditional put on that key is the pipeline's idempotency record. |
| `OrderNotifyStreamFunction` | Lambda (stream consumer, ESC-21) | One function on the three streams: `NotificationRules` derives one event per party (`notifications-api.md` section 3), the inbox row is put, the push text is rendered in the recipient's language and published to each of their endpoints. |
| `NotificationFunction` | Lambda (`GET /notifications`, ESC-21) | The caller's inbox page, newest first, `since` and `nextToken` paging. Any role; scoped to the caller. |
| `{prefix}-notify-dlq` | SQS (ESC-21) | Dead-letter queue of the three event-source mappings (section 6). |

## 4. Data flow

**Registration**
```
device → POST /device-tokens {token, platform}
  → DeviceTokenFunction: SNS.CreatePlatformEndpoint(PlatformApplicationArn, token)
  → put device-tokens { userId, endpointArn, platform, token, createdAt }
```

**Intent sync**
```
device saves/edits/cancels intent → enqueue SyncOperation(entityType="buyer_intent")
  → POST /sync → SyncFunction.ProcessBuyerIntentAsync → put/update/delete intents row
     (role must be buyer; buyerId must match the authenticated user)
```

**Match-on-insert (the alert)**
```
farmer lists crop → POST /sync → crops table PutItem
  → DynamoDB Stream (eventName=INSERT, filtered at the event-source mapping)
  → CropMatchStreamFunction(NEW_IMAGE crop):
       skip if status != "available"
       for cropType ∈ {crop.cropType + substitute types that map to it}:
         query intents GSI CropTypeStatusIndex (PK=cropType, status=active)
       for each candidate intent:
         build MatchCriteria from the intent, CropCandidate from the new crop
         if MatchScoring.IsCandidate && score ≥ ALERT_THRESHOLD:
           look up buyer's endpointArns (device-tokens)
           SNS.Publish(endpointArn, {title, body, data:{intentId, cropId}})
```

**Order, delivery and money notifications (ESC-21)**
```
sync / cron / webhook / admin writes an order, delivery or escrow row
  → DynamoDB Stream (NEW_AND_OLD_IMAGES; orders MODIFY, deliveries INSERT, escrows MODIFY)
  → OrderNotifyStreamFunction(record):
       events = NotificationRules.Derive(OLD image, NEW image)   # 0..3, one per party, never the actor
       for each event:
         put notifications { userId, sortKey = createdAt#type#entityId, ... }   # conditional, once
         if event.Push:
           read the recipient's preferredLanguage and endpoints (users, device-tokens)
           title/body = PushText.Render(type, params, language)
           SNS.Publish(endpointArn, {notification:{title, body, tag:order-<orderId>},
                                     data:{type:order, event, orderId, entityId, entity, notificationId}},
                       AWS.SNS.MOBILE.FCM.TTL = seconds to the deadline (min 60) | 86400)

device → GET /notifications?since=<cursor of the last pull>   # on open, resume and push
  → NotificationFunction → query WrittenAtIndex PK = caller, writtenAt > since - 2 min,
    newest write first (client dedupes by notificationId); response carries the next cursor
```
The inbox is the source of truth and web reads it alone (no push). Contract:
`specs/contracts/notifications-api.md`.

> **Numeric filtering caveat** (from research): DynamoDB stream event-source filters cannot
> match numeric ranges (values are stringified). So the mapping filters only on
> `eventName = INSERT`; crop-type / price / quantity matching is done in the Lambda using
> `MatchScoring` — the same scorer as `GET /match` and the mobile on-device scorer.

## 5. Scoring reuse (parity)

`CropMatchStreamFunction` uses the existing `MatchScoring` helper — **no new scoring logic**.
This keeps push-match decisions identical to `GET /match` and the mobile `MatchScorer`
(weights, substitutes, near-miss bands, haversine). The alert fires when a crop would rank as
a genuine match (score ≥ a configured threshold), not merely a coarse type match. Substitute
crops can trigger alerts (lower score) just like in interactive matching.

Inversion note: interactive `/match` queries crops by the buyer's wanted type; here we have
**one new crop** and look up **intents** whose wanted type is the crop's type *or* a type for
which this crop is an accepted substitute. The substitute map is symmetric in practice
(wheat↔barley, maize↔sorghum), so we query intents for the crop's type and its substitutes.

## 6. Offline-first & failure behavior

- Intent creation and the on-device "N new" badge are unchanged and work offline; push is
  additive. If a device never registers a token (offline / permission denied), the buyer
  still sees matches in-app on next open.
- The stream Lambda is idempotent-friendly: a failed publish to a stale endpoint disables
  that endpoint (SNS) and is logged; other endpoints/buyers are unaffected.
- Stream retries are bounded by the event-source mapping; a poison record is sent to a DLQ
  rather than blocking the shard. For the ESC-21 mappings the DLQ is configured:
  `{prefix}-notify-dlq` (SQS-managed encryption, 14 day retention). It receives the record's
  shard and sequence metadata, not the images, so the runbook is to re-derive the event from
  the current table state within the stream's 24 hour retention. The mappings retry three
  times, bisect a failing batch on error and drop records older than one day (max record age).
- The order notification pipeline is idempotent by construction: the inbox put is conditional
  on `{createdAt}#{type}#{entityId}` (the stream record's time, stable across retries), and a
  repeated push is collapsed on the device by its `tag`.
- Three alarms on the monitoring topic: `NotifyDlqMessages` (anything in the queue),
  `NotifyStreamIteratorAge` (the function is falling behind) and `NotifyStreamErrors`
  (Lambda errors). The crop match and message mappings keep their earlier retry-only setup.

## 7. Security / privacy

- `POST /device-tokens` requires JWT; the endpoint is created under the authenticated user.
- Intent rows are owned by `buyerId == sub`; the sync handler rejects mismatches (mirrors the
  crop/trip handlers).
- Notification payload carries only `intentId` + `cropId` (no PII); the app fetches details
  after the user taps.

## 8. Deferred / not in this feature

- iOS/APNs (target is Android 8+).
- Per-intent frequency caps / digest batching.
- Trip-match alerts (only crop listings trigger alerts here).
- Geohash GSI / area-browse (separate follow-on).
- SMS channel (rejected on cost).

---

**Last Updated**: 2026-10-26 (ESC-21 order notifications added to sections 3, 4 and 6)
