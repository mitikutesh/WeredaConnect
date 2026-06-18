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
| SNS Platform Application | `wereda-connect-{env}-fcm` | CDK (ApiStack) |
| Secrets Manager secret | `wereda-connect/{env}/fcm-service-account` | **you** (holds Google creds) |
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
3. A JSON file downloads (contains `project_id`, `private_key`, `client_email`, …). **Treat it as a secret** — do not commit it.

Store it in AWS Secrets Manager so CDK can wire it into the SNS Platform Application:

```bash
# From the directory containing the downloaded key (rename it to service-account.json)
aws secretsmanager create-secret \
  --region eu-north-1 \
  --name "wereda-connect/dev/fcm-service-account" \
  --description "FCM v1 service account JSON for SNS Platform Application (dev)" \
  --secret-string file://service-account.json
```

> Rotating later: `aws secretsmanager put-secret-value --secret-id wereda-connect/dev/fcm-service-account --secret-string file://service-account.json` then redeploy the ApiStack.

---

## 3. Deploy the infrastructure (AWS side)

The CDK reads the secret above and creates the SNS Platform Application, the two new
tables, the crops stream, and the two Lambdas.

```bash
cd infra
npm install                       # first time only
npx cdk synth --quiet             # sanity check — should succeed with no errors

# Deploy (dev). DatabaseStack must deploy before ApiStack (CDK orders this automatically).
npx cdk deploy --all --context environment=dev --require-approval broadening
```

What to expect:
- `WeredaConnect-dev-DB` updates: new `intents` and `device-tokens` tables; crops table gains a stream (`StreamViewType: NEW_IMAGE`).
- `WeredaConnect-dev-API` updates: `wereda-connect-dev-fcm` SNS Platform Application, `wereda-connect-dev-device-tokens` Lambda + `POST /device-tokens` route, `wereda-connect-dev-crop-match-stream` Lambda with a DynamoDB event-source mapping (filtered to `eventName = INSERT`).

Verify:
```bash
# Platform application exists
aws sns list-platform-applications --region eu-north-1

# Stream is enabled on crops
aws dynamodb describe-table --region eu-north-1 \
  --table-name wereda-connect-dev-crops \
  --query 'Table.StreamSpecification'
```

> If `cdk deploy` fails with "secret not found", complete §2 first — the ApiStack
> references `wereda-connect/dev/fcm-service-account`.

---

## 4. Mobile build configuration

Most of this is already coded (see §5); these are the project-level bits the build needs:

1. **`google-services.json`** in place (see §1.4).
2. **Gradle Google Services plugin** — `mobile/android/build.gradle` (project) has the
   classpath and `mobile/android/app/build.gradle` applies `com.google.gms.google-services`.
   (Wired by the push feature commit; verify after pulling.)
3. **`firebase_messaging`** dependency in `mobile/pubspec.yaml` (added by the feature commit).
4. **minSdk** ≥ 21 (already satisfied; target is Android 8 / API 26).
5. **Notification runtime permission** (Android 13+/API 33): the app requests
   `POST_NOTIFICATIONS` at runtime on first launch of the buyer flow.

Build:
```bash
cd mobile
flutter pub get
flutter build apk --debug    # or run on a device: flutter run
```

---

## 5. How the app behaves (already implemented)

- On buyer login, the app requests notification permission, retrieves its FCM token, and
  calls `POST /device-tokens { token, platform: "android" }`. The server registers an SNS
  endpoint and stores `userId → endpointArn`.
- Buyer intents are now **synced to the server** (entityType `buyer_intent` via `POST /sync`),
  so the backend can match new crops against them.
- Tapping a received notification opens the buyer's match results for that intent.
- Token refresh re-registers automatically.

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
| `cdk deploy` "secret not found" | §2 not done | Create the Secrets Manager secret, redeploy |
| No endpoint row after login | Permission denied / token not retrieved | Check device granted notifications; check `POST /device-tokens` logs |
| Stream Lambda never invoked | Stream not enabled / mapping missing | `describe-table … StreamSpecification`; redeploy ApiStack |
| Lambda runs but no push | Bad/expired token, or FCM creds wrong | Check SNS endpoint `Enabled` attr; re-upload service-account JSON; re-register token |
| Push works manually but not via match | No matching intent / intent not synced | Confirm intent row in `intents` table; check Lambda match logs |
| Android 13 device silent | `POST_NOTIFICATIONS` not granted | Re-prompt / enable in system settings |

---

## 9. Quick checklist

- [ ] Firebase project created; Android app `et.wereda.connect` added
- [ ] `google-services.json` → `mobile/android/app/`
- [ ] Service-account JSON generated and stored in Secrets Manager (`wereda-connect/dev/fcm-service-account`)
- [ ] `cd infra && npx cdk deploy --all --context environment=dev`
- [ ] SNS Platform Application + stream + Lambdas verified
- [ ] `cd mobile && flutter pub get && flutter run` on a real device
- [ ] Buyer logs in → token registered (device-tokens table has a row)
- [ ] Farmer lists matching crop → buyer receives push

---

**See also**: `specs/architecture/push-notifications-architecture.md` (design),
`specs/contracts/push-alerts-api.md` (API contract), `.ai/deployment.md` (full deploy reference).

**Last Updated**: 2026-06-19
