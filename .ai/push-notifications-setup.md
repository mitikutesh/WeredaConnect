# Push Notifications — Setup Runbook (FCM via AWS SNS)

**Audience**: developer/operator setting up buyer match push alerts for the first time.
**Outcome**: a buyer gets an Android push notification when a newly listed crop matches one of their saved purchase intents.

> **Why Firebase if we're on AWS?** Android push can only be delivered through
> **FCM (Firebase Cloud Messaging)** — Google's push gateway. AWS SNS *Mobile Push*
> does not replace FCM; it wraps it. The path is
> `Lambda → SNS Platform Application → FCM → device`. Your backend stays 100% on
> AWS; Firebase is only the last-mile delivery gateway, and FCM itself is free.
> FCM is unlimited-free; AWS SNS mobile push is free for the first 1M publishes/month.

---

## 0. Architecture at a glance

```
 Buyer device                         AWS (eu-north-1 dev)                       Google
 ────────────                         ────────────────────                       ──────
 saves intent ──POST /sync──────────► SyncFunction ──► intents table
 gets FCM token ─POST /device-tokens► DeviceTokenFunction
                                          │ SNS CreatePlatformEndpoint(token)
                                          └──► device-tokens table (userId→endpointArn)

 farmer lists crop ─POST /sync──────► SyncFunction ──► crops table
                                          │ (DynamoDB Stream, NEW_IMAGE, INSERT)
                                          ▼
                                      CropMatchStreamFunction
                                          │ query active intents by cropType
                                          │ score with MatchScoring (same as /match)
                                          │ for each matching buyer:
                                          ▼
                                      SNS Publish(endpointArn) ──► SNS Platform App ──► FCM ──► 📱 push
```

Components this feature adds (all created by CDK except the Google/Firebase parts):

| Resource | Name (`{env}` = dev/staging/prod) | Created by |
|---|---|---|
| DynamoDB table | `wereda-connect-{env}-intents` | CDK (DatabaseStack) |
| DynamoDB table | `wereda-connect-{env}-device-tokens` | CDK (DatabaseStack) |
| DynamoDB Stream | on `wereda-connect-{env}-crops` (NEW_IMAGE) | CDK (DatabaseStack) |
| Lambda | `wereda-connect-{env}-device-tokens` (`POST /device-tokens`) | CDK (ApiStack) |
| Lambda | `wereda-connect-{env}-crop-match-stream` (stream consumer) | CDK (ApiStack) |
| SNS Platform Application | `wereda-connect-{env}-fcm` (platform `GCM`) | **you** (CLI/console — CloudFormation has no resource for it; CDK references its deterministic ARN) |
| Firebase project + Android app | (your choice of name) | **you** (Google) |
| `google-services.json` | `mobile/android/app/google-services.json` | **you** (download from Firebase) |

> ⚠️ AWS updated SNS to **FCM HTTP v1** in 2024 (the old "server key" is deprecated by
> Google). This runbook uses the **v1 service-account JSON** credential. Console labels
> shift over time — cross-check against the current AWS doc
> "Adding FCM credentials to Amazon SNS" if a field name differs.

---

## 1. Create the Firebase project (Google side)

1. Go to <https://console.firebase.google.com> and sign in with the Google account that should own the project.
2. Click **Add project** (a Firebase project is also a GCP project under the hood; you can later see it at <https://console.cloud.google.com>).
   - Project name: e.g. `weredaconnect-dev`.
   - Disable Google Analytics (not needed for FCM) or leave default.
   - Click **Create project**.
3. In the project, click the **Android** icon (**Add app → Android**).
   - **Android package name**: `et.wereda.connect` (must match `applicationId` in `mobile/android/app/build.gradle`).
   - App nickname: optional (e.g. `WeredaConnect Android`).
   - Debug signing SHA-1: optional for FCM (not required).
   - Click **Register app**.
4. **Download `google-services.json`** and place it at:
   ```
   mobile/android/app/google-services.json
   ```
   (This file ties the app build to the FCM project. It is safe to commit for a single-project app, but if you keep dev/prod Firebase projects separate, gitignore it and inject per-environment.)
5. Skip the remaining "add SDK" wizard steps in the console — the Flutter integration is already coded (see §5).

---

## 2. Generate the FCM v1 service-account credential (for AWS SNS)

AWS SNS authenticates to FCM v1 using a Google **service-account private key (JSON)**.

1. Firebase Console → ⚙ **Project settings** → **Service accounts** tab.
2. Ensure **Firebase Admin SDK** is selected, click **Generate new private key** → **Generate key**.
3. A JSON file downloads (contains `project_id`, `private_key`, `client_email`, …). **Treat it as a secret** — do not commit it. Rename it to `service-account.json`.

> ⚠️ This is **not** `mobile/android/app/google-services.json`. That file is the Firebase
> Android *client* config (keys: `project_info`, `client`, `configuration_version`) and has
> **no `private_key`** — SNS cannot authenticate with it. Use the service-account key from the
> **Service accounts** tab, which starts with `"type": "service_account"`.

### 2a. Create the SNS Platform Application (CLI)

CloudFormation has **no resource type** for SNS platform applications, so create it
once with the CLI. **The name must be exactly `wereda-connect-dev-fcm`** (platform `GCM`)
— the CDK references this deterministic ARN.

```bash
# --attributes is passed as JSON, NOT the Key=Value shorthand. The service-account key
# is full of commas, and the shorthand parser splits on them — giving the misleading
# "parsing parameter '--attributes': expected: =" error. jq --rawfile escapes the whole
# file into a single JSON string value, so nothing breaks the parser.
aws sns create-platform-application \
  --region eu-north-1 \
  --name wereda-connect-dev-fcm \
  --platform GCM \
  --attributes "$(jq -n --rawfile cred service-account.json '{PlatformCredential: $cred}')"
```

This prints the `PlatformApplicationArn` — it will be
`arn:aws:sns:eu-north-1:<account-id>:app/GCM/wereda-connect-dev-fcm`, which is what the
Lambdas use (no need to copy it anywhere).

> FCM v1 note: SNS still identifies FCM by the `GCM` platform name; the credential is the
> v1 **service-account JSON** (not the deprecated legacy server key). If your AWS CLI/region
> rejects `PlatformCredential` for v1, follow the current AWS doc "Adding FCM credentials to
> Amazon SNS" — the platform name and ARN format are unchanged.
>
> Rotating later (same JSON-not-shorthand reason): `aws sns set-platform-application-attributes --platform-application-arn <arn> --attributes "$(jq -n --rawfile cred service-account.json '{PlatformCredential: $cred}')"`. No redeploy needed.
>
> Optional: also keep the JSON in Secrets Manager (`aws secretsmanager create-secret --name wereda-connect-dev/fcm-service-account --secret-string file://service-account.json`) for safekeeping/rotation.

---

## 3. Deploy the infrastructure (AWS side)

With the SNS Platform Application created (§2a), the CDK creates the two new tables, the
crops stream, the two Lambdas (referencing the platform application's deterministic ARN),
and the `POST /device-tokens` route.

```bash
cd infra
npm install                       # first time only
npx cdk synth --quiet             # sanity check — should succeed with no errors

# Deploy (dev). DatabaseStack must deploy before ApiStack (CDK orders this automatically).
npx cdk deploy --all --context environment=dev --require-approval broadening
```

What to expect:
- `WeredaConnect-dev-DB` updates: new `intents` and `device-tokens` tables; crops table gains a stream (`StreamViewType: NEW_IMAGE`).
- `WeredaConnect-dev-API` updates: `wereda-connect-dev-device-tokens` Lambda + `POST /device-tokens` route, `wereda-connect-dev-crop-match-stream` Lambda with a DynamoDB event-source mapping (filtered to `eventName = INSERT`), and IAM referencing the SNS platform application's deterministic ARN. (The platform application itself was created in §2a, not by CDK.)

Verify:
```bash
# Platform application exists
aws sns list-platform-applications --region eu-north-1

# Stream is enabled on crops
aws dynamodb describe-table --region eu-north-1 \
  --table-name wereda-connect-dev-crops \
  --query 'Table.StreamSpecification'
```

> `cdk deploy` does not depend on the FCM credential or the platform application
> existing — IAM references the deterministic ARN. But pushes only work at runtime once
> the platform application from §2a exists, so create it before testing (§6).

---

## 4. Mobile build configuration (activation steps)

> **What's already committed (as of 2026-07-09)**: buyer intents sync
> (`buyer_intent` ops) **and the FCM client is now implemented** —
> `lib/core/notifications/push_notification_service.dart` (registered on auth in
> `run_mobile.dart`), the `google-services` Gradle plugin (`settings.gradle` +
> `app/build.gradle`), the `firebase_core`/`firebase_messaging` deps, and the
> `POST_NOTIFICATIONS` manifest permission. The push client now drives **two**
> alert types: buyer match alerts (`CropMatchStreamFunction`) and **new-message
> alerts** (`MessageStreamFunction`, see `specs/contracts/messaging.md`).
>
> The remaining human-only steps are §1 (`google-services.json`) — already
> present in this repo — and §2a (the SNS Platform Application), plus a deploy.

1. **`google-services.json`** → `mobile/android/app/google-services.json` (from §1.4). ✅ present.
2. **Gradle Google Services plugin** — ✅ done:
   - `mobile/android/settings.gradle` plugins block: `id "com.google.gms.google-services" version "4.4.2" apply false`.
   - `mobile/android/app/build.gradle` plugins block: `id "com.google.gms.google-services"`.
3. **Dependencies** in `mobile/pubspec.yaml` — ✅ done (`firebase_core: ^3.6.0`, `firebase_messaging: ^15.1.3`).
4. **minSdk** ≥ 21 (already satisfied; target is Android 8 / API 26).
5. **Android 13+ permission**: `POST_NOTIFICATIONS` — ✅ added to `AndroidManifest.xml`; requested at runtime by the service.

Build once the above are in place:
```bash
cd mobile
flutter pub get
flutter build apk --debug    # or: flutter run  (use a real device; emulators need Play services)
```

---

## 5. Mobile FCM client (drop-in code)

Add `lib/core/notifications/push_notification_service.dart`:

```dart
import 'package:firebase_core/firebase_core.dart';
import 'package:firebase_messaging/firebase_messaging.dart';
import 'package:wereda_connect/core/network/api_client.dart';

/// Registers the device's FCM token with the backend (POST /device-tokens) so
/// the server can deliver buyer match alerts. Best-effort and fully optional:
/// if Firebase isn't configured (no google-services.json) it silently no-ops,
/// preserving offline-first behavior.
class PushNotificationService {
  PushNotificationService({required ApiClient api}) : _api = api;
  final ApiClient _api;

  Future<void> init() async {
    try {
      await Firebase.initializeApp();
      final messaging = FirebaseMessaging.instance;

      // Android 13+ runtime permission.
      await messaging.requestPermission();

      final token = await messaging.getToken();
      if (token != null) await _register(token);

      // Re-register on rotation.
      messaging.onTokenRefresh.listen(_register);
    } catch (_) {
      // Firebase not configured / unavailable — push disabled, app continues.
    }
  }

  Future<void> _register(String token) async {
    try {
      await _api.post<Map<String, dynamic>>(
        '/device-tokens',
        data: {'token': token, 'platform': 'android'},
      );
    } catch (_) {
      // Non-fatal: the in-app "N new matches" badge still works.
    }
  }
}
```

Wire-up (IMPLEMENTED — `run_mobile.dart`, on every auth transition, all roles):
```dart
PushNotificationService(api: ApiClient()).init(
  onMessagePush: () { /* dispatches InboxPollRequested (+ one 3s follow-up) */ },
  // ESC-21: a foreground order push pulls the notification inbox (forced) and the order.
  onOrderPush: (orderId) { /* pull(userId, force: true) + pullOrder(orderId), 3s follow-up */ },
  // ESC-21: a tapped order push (background or cold start) opens the order.
  onOpenOrder: (orderId) { /* pullOrderIfMissing, then orderDetailRoute on the navigatorKey */ },
);
```

**Foreground chat pushes (IMPLEMENTED):** `init()` subscribes to
`FirebaseMessaging.onMessage`. Android suppresses the tray notification while the
app is in the foreground, so this is the only foreground signal — when the payload
is a chat message the callback nudges `InboxBloc` to pull immediately, and the
existing in-app banner + unread badges make arrival feel instant (no more waiting
for the 60s poll). The subscriptions are static and replaced on each `init()` so
repeated logins don't stack listeners.

**Payload contract (must stay in sync with `MessageStreamFunction.cs`):** chat
pushes carry `data.type == "message"` plus `conversationId`/`messageId`/`senderId`/
`contextType`/`contextId`; buyer-match pushes carry `{intentId, cropId}` and no
`type`. The client filter is `PushNotificationService.isMessagePush`. Order pushes
(ESC-21, §5a) carry `data.type == "order"` plus `event`/`orderId`/`notificationId`;
see `specs/contracts/notifications-api.md` section 2.

Tap-to-open routing: IMPLEMENTED for order pushes (ESC-21). `WeredaConnectApp.navigatorKey`
sits on the `MaterialApp`; `FirebaseMessaging.onMessageOpenedApp` and `getInitialMessage()` (once
per launch) call `onOpenOrder(orderId)`, which reads an order the device has not pulled yet and
opens its detail screen. A chat or match push still only opens the app. For a match alert, still
DEFERRED: register a background handler in `main()` and handle taps:
```dart
@pragma('vm:entry-point')
Future<void> _firebaseBgHandler(RemoteMessage message) async {}

// in main(): FirebaseMessaging.onBackgroundMessage(_firebaseBgHandler);
// FirebaseMessaging.onMessageOpenedApp.listen((m) {
//   final intentId = m.data['intentId'];
//   // navigate to MatchResultsScreen for intentId
// });
```

**Behavior once activated**: on login the app requests permission, gets its FCM
token, and `POST /device-tokens`. New chat messages push instantly (tray when
backgrounded, in-app banner via the onMessage → pull path when foregrounded).
Buyer intents already sync (`buyer_intent`), so the backend matches new crops and
pushes `{intentId, cropId}`; tapping opens the app (deep-link deferred).

**Related client behavior (no FCM needed):** the inbox poll is lifecycle-aware —
paused/hidden stops the 60s timer (3G data budget), resume/tab-refocus restarts it
with an immediate throttled catch-up pull, and sign-out resets it. See
`InboxPaused`/`InboxResumed`/`InboxReset` in `inbox_bloc.dart` and the
`WidgetsBindingObserver` in `message_arrival_listener.dart`.

---

## 5a. Order notifications (ESC-21)

Every party of an order (buyer, farmer, trucker) gets an inbox entry and an Android push for
each state and money change: the farmer is asked to accept a funded order, the trucker to
take the transport, the buyer told about pickup, delivery, refunds, payouts and disputes.
Contract: `specs/contracts/notifications-api.md`. Design: section 4 of
`specs/architecture/push-notifications-architecture.md`.

**What a dev tester needs**: nothing new to create. The pushes go through the same
`wereda-connect-dev-fcm` platform application and the same `POST /device-tokens`
registration as the match and chat alerts (§2a, §5). The CDK deploy adds the
`wereda-connect-dev-notifications` table, the `wereda-connect-dev-order-notify-stream`
Lambda on the orders, deliveries and escrows streams, `GET /notifications`, the
`wereda-connect-dev-notify-dlq` queue and three alarms.

**Verify without a device**: the inbox is the source of truth, so a funded order is enough.

1. As the buyer (`+251900100003`), place an order on one of the farmer's crops and submit the
   payment. Dev runs `TELEBIRR_MOCK=true`, so the order is `funded` within a second.
2. Log in as the farmer (`+251900100001`) and read the inbox with the **ID token** (the
   access token has no role claim and is refused):

   ```bash
   API=https://vhhm8gh9c1.execute-api.eu-north-1.amazonaws.com
   TOKEN=$(curl -s -X POST "$API/auth/login" -H 'Content-Type: application/json' \
     -d '{"phoneNumber":"+251900100001","password":"TestPass1dev"}' | jq -r '.idToken')
   curl -s "$API/notifications?limit=5" -H "Authorization: Bearer $TOKEN" | jq .
   ```

   The newest entry is `"type": "order_request"` with the buyer's name, the crop, the
   quantity, the amount and `deadlineAt`. The buyer's own inbox shows `payment_confirmed`
   for the same order.
3. With a registered device the farmer also gets the push (title and body in the farmer's
   `preferredLanguage`, tag `order-<orderId>`), and the tap opens the order. In the app, the bell
   (mobile and web) lists the same entries in the app's language, and the farmer's Home shows an
   "Action needed" card for the order.

**The log line to look for** (one per emitted event):

```bash
aws logs tail /aws/lambda/wereda-connect-dev-order-notify-stream --region eu-north-1 --follow \
  --filter-pattern NOTIFY_EMITTED
# NOTIFY_EMITTED type=order_request entity=order entityId=<orderId> recipient=<farmerUserId> push=true new=true
```

`new=false` is a stream replay that found its inbox row already written (harmless; the push is
collapsed by tag on the device). No line at all after a funding means the stream mapping or
the filter is wrong: `aws dynamodb describe-table --table-name wereda-connect-dev-orders
--query 'Table.StreamSpecification'` must show `NEW_AND_OLD_IMAGES`. A message in
`wereda-connect-dev-notify-dlq` means a record failed three times; re-derive it from the
order row (the queue holds only shard metadata) and check the `NotifyStreamErrors` alarm.

---

## 6. End-to-end test

Use the dev test users (`.ai/test-users.md`):

1. **Buyer** (`+251900100003`) logs in on a physical Android device (emulators need Google
   Play services). Accept the notification prompt. Create a purchase intent, e.g. *wheat,
   10 quintals, ≤120 ETB/kg, deliver to Adama*.
   - Confirm the token registered: `aws dynamodb scan --region eu-north-1 --table-name wereda-connect-dev-device-tokens`.
2. **Farmer** (`+251900100001`) logs in and lists a matching crop (*wheat, Adama*). This
   syncs to the crops table, firing the stream.
3. Watch the stream Lambda:
   ```bash
   aws logs tail /aws/lambda/wereda-connect-dev-crop-match-stream --region eu-north-1 --follow
   ```
   You should see it match the buyer's intent and publish to SNS.
4. The buyer's device receives a push: *"New wheat listing in Adama matches your request."*

Manual push sanity check (bypasses matching):
```bash
aws sns publish --region eu-north-1 \
  --target-arn "<endpointArn from device-tokens table>" \
  --message '{"GCM":"{\"notification\":{\"title\":\"Test\",\"body\":\"Hello\"}}"}' \
  --message-structure json
```

---

## 7. Cost & operational notes

- **FCM**: free, unlimited.
- **SNS mobile push**: first 1,000,000 publishes/month free, then ~$0.50/million. Effectively free at this scale.
- **DynamoDB Streams + Lambda**: charged per stream read + Lambda invocation — pennies/month at low listing volume.
- **No per-message SMS-style charge** (that's the SMS channel, which we did *not* choose).
- **Opt-in / frequency**: alerts only go to buyers who registered a token (i.e. granted
  permission). Consider a per-intent daily cap if listing volume grows (not yet implemented).
- **Quiet/dedup**: the stream Lambda only fires on `INSERT` (new listings), so updates and
  deletes don't notify.

---

## 8. Troubleshooting

| Symptom | Likely cause | Fix |
|---|---|---|
| Token registers but every push fails | Platform application missing/misnamed | Ensure §2a created `wereda-connect-dev-fcm` (platform `GCM`) in the deploy region |
| No endpoint row after login | Permission denied / token not retrieved | Check device granted notifications; check `POST /device-tokens` logs |
| Stream Lambda never invoked | Stream not enabled / mapping missing | `describe-table … StreamSpecification`; redeploy ApiStack |
| Lambda runs but no push | Bad/expired token, or FCM creds wrong | Check SNS endpoint `Enabled` attr; re-upload service-account JSON; re-register token |
| Push works manually but not via match | No matching intent / intent not synced | Confirm intent row in `intents` table; check Lambda match logs |
| Android 13 device silent | `POST_NOTIFICATIONS` not granted | Re-prompt / enable in system settings |

---

## 9. Quick checklist

- [ ] Firebase project created; Android app `et.wereda.connect` added
- [ ] `google-services.json` → `mobile/android/app/`
- [ ] Service-account JSON generated; SNS Platform Application `wereda-connect-dev-fcm` (platform `GCM`) created (§2a)
- [ ] `cd infra && npx cdk deploy --all --context environment=dev`
- [ ] SNS Platform Application + stream + Lambdas verified
- [ ] `cd mobile && flutter pub get && flutter run` on a real device
- [ ] Buyer logs in → token registered (device-tokens table has a row)
- [ ] Farmer lists matching crop → buyer receives push

---

**See also**: `specs/architecture/push-notifications-architecture.md` (design),
`specs/contracts/push-alerts-api.md` (API contract), `.ai/deployment.md` (full deploy reference).

**Last Updated**: 2026-10-26
