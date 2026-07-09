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

Wire it after login (e.g. in `run_mobile.dart`, once the user is a buyer):
```dart
await PushNotificationService(api: ApiClient()).init();
```

Tap-to-open routing — register a background handler in `main()` and handle taps:
```dart
@pragma('vm:entry-point')
Future<void> _firebaseBgHandler(RemoteMessage message) async {}

// in main(): FirebaseMessaging.onBackgroundMessage(_firebaseBgHandler);
// FirebaseMessaging.onMessageOpenedApp.listen((m) {
//   final intentId = m.data['intentId'];
//   // navigate to MatchResultsScreen for intentId
// });
```

**Behavior once activated**: on buyer login the app requests permission, gets its FCM
token, and `POST /device-tokens`. Buyer intents already sync (`buyer_intent`), so the
backend matches new crops and pushes `{intentId, cropId}`; tapping opens that intent's
match results.

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

**Last Updated**: 2026-06-19
