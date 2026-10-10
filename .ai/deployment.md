# Deployment & API Reference

Quick reference for deploying WeredaConnect and understanding all API endpoints.

---

## Architecture Overview

```
                    CloudFront (CDN)
                   /       |        \
          website/     webapp/     admin/
          (Astro)    (Flutter)    (React)
             |           |           |
          S3 bucket   S3 bucket   S3 bucket

              API Gateway (HTTP API v2)
                     |
            JWT Authorizer (Cognito)
                     |
         ┌───────────┼───────────────┐
    14 Lambda Functions (.NET 10 AOT, ARM64)
         └───────────┼───────────────┘
                     |
              DynamoDB (12 tables)
              S3 (uploads bucket)
              Secrets Manager (prod secrets only)
              SSM Parameter Store (config + dev/staging secrets)
```

**Region**: `eu-north-1` (Stockholm) for dev; `me-south-1` (Bahrain) for staging/prod
**Compute**: Lambda on Graviton2 (ARM64), 512MB, 10s timeout
**Runtime**: .NET 10 Native AOT on `provided.al2023`

---

## Environments

| Env | Website | Webapp | Admin | Auto-Deploy |
|-----|---------|--------|-------|-------------|
| **dev** | `dev.weredaconnect.com` | `dev-app.weredaconnect.com` | `dev-admin.weredaconnect.com` | Push to `main` |
| **staging** | `staging.weredaconnect.com` | `staging-app.weredaconnect.com` | `staging-admin.weredaconnect.com` | Manual |
| **prod** | `www.weredaconnect.com` | `app.weredaconnect.com` | `admin.weredaconnect.com` | Manual |

Config source: `infra/lib/config/environments.ts`

---

## Prerequisites

1. **AWS CLI** configured with admin access: `aws sts get-caller-identity`
2. **CDK CLI**: `npm install -g aws-cdk` (v2.170+)
3. **GitHub CLI**: `gh auth login`
4. **.NET 10 SDK** (for local server builds)
5. **Docker** (for cross-compiling AOT to linux-arm64)
6. **Flutter 3.38.9** (for webapp builds)
7. **Node 20** (for CDK and admin dashboard)

---

## Deployment Procedures

### 1. First-Time Infrastructure Setup

```bash
# Deploy all CDK stacks (Database, API, Web, Monitoring, Certificate)
cd infra
npm install
npx cdk bootstrap aws://<ACCOUNT_ID>/eu-north-1

# NON-PROD ONLY, BEFORE cdk deploy: seed the SSM SecureString secrets.
# dev/staging secrets live in SSM Parameter Store, not Secrets Manager, and
# CloudFormation cannot create SecureStrings. Skipping this leaves every
# secret-reading Lambda failing with ParameterNotFound at cold start.
../scripts/seed-ssm-secrets.sh dev

npx cdk deploy --all --context environment=dev

# Configure GitHub secrets from CDK outputs
./scripts/setup-gh-secrets.sh --env dev
```

### 2. Full Backend Deploy (Server + Infra)

```bash
# One command does it all: test → Docker build → seed → CDK deploy
./scripts/deploy.sh dev       # or staging, prod
```

Steps performed by `deploy.sh`:
1. `dotnet test` — run server tests
2. Docker build — .NET 10 AOT for linux-arm64 via `mcr.microsoft.com/dotnet/sdk:10.0`
3. `npx tsc` — compile CDK TypeScript
4. `./scripts/seed-ssm-secrets.sh $ENV` (non-prod) — seed/verify SSM SecureString secrets
5. `npx cdk deploy --all --context environment=$ENV --require-approval broadening`

### 3. Manual Backend Deploy (without script)

```bash
# Build server
cd server
dotnet publish -c Release -r linux-arm64 --self-contained

# NON-PROD ONLY: seed SSM secrets BEFORE cdk deploy — see §1. This step is
# MANDATORY: deploying without it deletes the legacy Secrets Manager secrets
# (no recovery window) and leaves the Lambdas pointing at missing parameters.
../scripts/seed-ssm-secrets.sh dev

# Deploy infra
cd ../infra
npm install
npx cdk deploy --all --context environment=dev
```

### 3b. Post-deploy on dev: dashboard admin user + admin API e2e

`deploy-server.yml` ends every **dev** run with two extra steps (SIM-18):

1. **Seed the dashboard admin user**: `scripts/seed-admin-user.sh` with
   `AdminUserPoolId` / `AdminClientId` taken from the deploy's own
   `--outputs-file`, so a replaced pool is seeded as well. Idempotent: creates
   the user once, resets the password to the documented dev value, ensures
   `admin` group membership, and fails the job if the login check fails
   (`SEED_ADMIN_STRICT=true`). It warns when the `COGNITO_ADMIN_USER_POOL_ID`
   GitHub secret (baked into the admin app by `deploy-admin.yml`) no longer
   matches the deployed pool. The IAM grant is `SeedAdminUser` in
   `github-actions-stack.ts` (non-prod only).
2. **Admin API e2e**: `python3 scripts/e2e_admin.py` logs in as that admin
   through Cognito and reads every dashboard endpoint (stats, users, escrows,
   transactions, audit, fraud signals, disputes, health, compliance,
   analytics) plus one detail per list. Non-blocking (`continue-on-error`);
   the result is in the run log.

Manual equivalent (any shell with AWS credentials, or CloudShell):

```bash
ADMIN_USER_POOL_ID=<AdminUserPoolId> ADMIN_CLIENT_ID=<AdminClientId> bash scripts/seed-admin-user.sh
python3 scripts/e2e_admin.py   # dev defaults; --base-url / --client-id to override
```

Staging and prod are never auto-seeded: create admins by hand (prod requires MFA).

### 3c. Post-deploy on every environment: legacy trucker legs (ESC-04)

`deploy-server.yml` ends every run (dev, staging, prod), once the CDK deploy
succeeded, with one invocation of escrow-cron's `park_legacy_trucker_legs`
action. Before ESC-04 a release could complete an escrow with its trucker leg
unpaid (`completed` with `truckerPayoutPending = true`), and the runbook said
to pay such a leg by hand. The action moves each such escrow back to
`released` and puts the leg into Manual Payouts as `GATEWAY_OUTCOME_UNKNOWN`,
so the admin checks the portal before paying. It never calls Telebirr, is
idempotent (a parked row is no longer `completed`), and answers 500 when a row
could not be parked. The step retries an invoke error that may pass on its own
(throttling: escrow-cron's reserved concurrency of 2 can be busy with an hourly
run; `AccessDenied`: on the first run the new IAM grant may not have propagated
yet; a 5xx from the Lambda service) up to 6 times with backoff, fails at once on
any other invoke error, and fails after 2 attempts when the function itself
failed (a `FunctionError` such as a timeout, or a 500 answer), so the worst case
stays under 15 minutes. On failure re-run the workflow. The IAM grant is
`InvokeEscrowCronAfterDeploy` in `github-actions-stack.ts` (this one function).

The read is bounded by a marker in the escrow idempotency table
(`migration:esc-04:park_legacy_trucker_legs`, 30-day TTL): the first run that
finds nothing writes `clean`, a later run that finds nothing at least 15 minutes
after that writes `done`, and a `done` marker skips the read (the step logs
`skipped=true`). Delete the marker to make the next deploy read again.

A Lambda instance still running the old code when the deploy finishes can
complete a release with its trucker leg unpaid after the step ran. That row
(`completed` with `truckerPayoutPending`) waits for the next deploy's run: the
hourly trucker retry and the `TRUCKER_PAYOUT_OVERDUE` report read only
`released` rows. The two clean runs needed for `done` make sure a deploy after
this one still looks. A `released` row left pending by the old code (no
`truckerPayoutPendingSince`) is parked by the hourly retry the same way, never
paid again.

Manual equivalent:

```bash
# Count first (read-only): escrows completed with the trucker leg still owed
aws dynamodb scan --table-name wereda-connect-dev-escrows \
  --filter-expression "truckerPayoutPending = :t AND #s = :c" \
  --expression-attribute-names '{"#s":"status"}' \
  --expression-attribute-values '{":t":{"BOOL":true},":c":{"S":"completed"}}' --select COUNT

# Park them (the handler reads the action from the request body)
aws lambda invoke --function-name wereda-connect-dev-escrow-cron \
  --cli-binary-format raw-in-base64-out --cli-read-timeout 330 \
  --payload '{"body":"{\"action\":\"park_legacy_trucker_legs\"}"}' /dev/stdout

# Read again on the next run (removes the done marker)
aws dynamodb delete-item --table-name wereda-connect-dev-escrow-idempotency \
  --key '{"idempotencyKey":{"S":"migration:esc-04:park_legacy_trucker_legs"}}'
```

#### Rolling back ESC-04

The code before ESC-04 cannot close a trucker leg queued on a `released`
escrow (`manualPayoutFromStatus = released`, from a retry or a legacy park):
its close accepts only `escrowed`, `disputed` and `dispute_resolved`, so the
admin page answers 409 `ESCROW_INVALID_TRANSITION`. Its crash recovery would
also complete every `released` escrow with `truckerPayoutPending`, dropping the
owed leg silently again. Before reverting, list both kinds:

```bash
aws dynamodb scan --table-name wereda-connect-<env>-escrows \
  --filter-expression "#s = :r AND truckerPayoutPending = :t" \
  --expression-attribute-names '{"#s":"status"}' \
  --expression-attribute-values '{":r":{"S":"released"},":t":{"BOOL":true}}' \
  --projection-expression "escrowId,createdAt,manualPayout,manualPayoutReason,truckerId,truckerPayout"
```

After the revert, for each one find the trucker leg in the Telebirr portal
(`GATEWAY_OUTCOME_UNKNOWN`: it may already be paid) or pay it there, then close
the row by hand with the portal transaction id:

```bash
aws dynamodb update-item --table-name wereda-connect-<env>-escrows \
  --key '{"escrowId":{"S":"<id>"},"createdAt":{"S":"<createdAt>"}}' \
  --update-expression "SET #s = :c, truckerTelebirrRef = :ref, truckerPaidAt = :now, completedAt = :now, manualTruckerStatus = :paid, manualTruckerRef = :ref, manualTruckerPaidBy = :admin, manualTruckerPaidAt = :now REMOVE manualPayout, truckerPayoutPending, truckerPayoutBlockReason, truckerPayoutBlockReasonAt, truckerPayoutLockId, truckerPayoutLockAt" \
  --condition-expression "#s = :r" \
  --expression-attribute-names '{"#s":"status"}' \
  --expression-attribute-values '{":c":{"S":"completed"},":r":{"S":"released"},":paid":{"S":"paid"},":ref":{"S":"<portal transaction id>"},":admin":{"S":"<admin id>"},":now":{"S":"<ISO time>"}}'
```

The `manualTrucker*` fields apply only to a queued row; leave them out for a
row without `manualPayout`. The other new attributes (`truckerPayoutPendingSince`,
`truckerPaidAt`, the marker) are ignored by the old code.

After redeploying ESC-04 following a revert, delete the legacy marker, so the
next deploy reads the `completed` partition again for the rows the old code
completed in between (deleting it just before the redeploy lets that deploy's own
run read):

```bash
aws dynamodb delete-item --table-name wereda-connect-<env>-escrow-idempotency \
  --key '{"idempotencyKey":{"S":"migration:esc-04:park_legacy_trucker_legs"}}'
```

### 4. Web App Deploys (CI/CD)

Automatic on push to `main` when files change in the relevant directory:

| Workflow File | Trigger Path | Build Command |
|---------------|-------------|---------------|
| `deploy-webapp.yml` | `mobile/**` | `flutter build web --release --dart-define=...` |
| `deploy-admin.yml` | `admin/**` | `npm run build` (Vite) |
| `deploy-website.yml` | `website/**` | `npm run build` (Astro) |

Manual trigger:
```bash
gh workflow run "Deploy Webapp (Flutter Web)" --field environment=dev
gh workflow run "Deploy Admin Dashboard (Vite)" --field environment=dev
gh workflow run "Deploy Website (Astro)" --field environment=dev
```

### 5. Manual Web App Deploy (without CI)

```bash
# Flutter webapp
cd mobile
flutter build web --release \
  --dart-define=ENVIRONMENT=dev \
  --dart-define=API_BASE_URL=$API_BASE_URL \
  --dart-define=COGNITO_USER_POOL_ID=$COGNITO_USER_POOL_ID \
  --dart-define=COGNITO_CLIENT_ID=$COGNITO_CLIENT_ID \
  --dart-define=COGNITO_REGION=eu-north-1
aws s3 sync build/web/ s3://wereda-connect-dev-webapp/ --delete
aws cloudfront create-invalidation --distribution-id <ID> --paths "/index.html" "/flutter_service_worker.js"

# Admin dashboard
cd admin
VITE_API_BASE_URL=$API_BASE_URL \
VITE_COGNITO_ADMIN_USER_POOL_ID=$COGNITO_ADMIN_USER_POOL_ID \
VITE_COGNITO_ADMIN_CLIENT_ID=$COGNITO_ADMIN_CLIENT_ID \
VITE_COGNITO_REGION=eu-north-1 \
npm run build
aws s3 sync dist/ s3://wereda-connect-dev-admin/ --delete
aws cloudfront create-invalidation --distribution-id <ID> --paths "/index.html"

# Marketing website (no env vars needed)
cd website && npm run build
aws s3 sync dist/ s3://wereda-connect-dev-website/ --delete
aws cloudfront create-invalidation --distribution-id <ID> --paths "/index.html"
```

### 6. Android App Release

```bash
cd mobile
# Update version in pubspec.yaml: version: 1.2.0+10
flutter build appbundle --release
# Upload build/app/outputs/bundle/release/app-release.aab to Google Play Console
# Staged rollout: 10% → 50% → 100%
```

---

## CDK Stacks

| Stack | Key | Resources |
|-------|-----|-----------|
| **DatabaseStack** | `{prefix}-db` | 12 DynamoDB tables, 2 S3 buckets (uploads, access-logs) |
| **ApiStack** | `{prefix}-api` | 14 Lambdas, API Gateway, 2 Cognito pools, EventBridge rules, Secrets Manager (prod) / SSM SecureString refs (dev+staging), SSM params |
| **WebStack** | `{prefix}-web` | 3 S3 buckets + 3 CloudFront distributions (website, webapp, admin) |
| **CertificateStack** | `{prefix}-cert` | ACM wildcard cert (`*.weredaconnect.com`) in us-east-1 |
| **MonitoringStack** | `{prefix}-monitoring` | CloudWatch dashboard + alarms |

Stack source: `infra/lib/stacks/`

### Push notifications (FCM) — Step 5 additions

The buyer match-alert feature adds these resources (full setup + one-time Firebase
steps in **`.ai/push-notifications-setup.md`**):

| Stack | Adds |
|-------|------|
| **DatabaseStack** | `+2` tables (`intents`, `device-tokens`); DynamoDB Stream (`NEW_IMAGE`) on the crops table |
| **ApiStack** | `+2` Lambdas (`device-tokens` → `POST /device-tokens`; `crop-match-stream` stream consumer + crops-stream event-source mapping); IAM referencing the SNS platform application's deterministic ARN |

**One-time prerequisite before deploy** (CloudFormation has no resource for SNS platform
applications): create the Firebase project, then create the SNS Platform Application
`wereda-connect-{env}-fcm` (platform `GCM`) with the FCM v1 service-account credential via
the CLI (runbook §1–§2a). Then `cdk deploy --all`.

---

## DynamoDB Tables (12)

| Table | Partition Key | Sort Key | GSIs | TTL |
|-------|--------------|----------|------|-----|
| `users` | `userId` | — | PhoneIndex, RoleWaredaIndex, FaydaIdIndex | — |
| `crops` | `listingId` | — | — | — |
| `trips` | `tripId` | — | — | — |
| `orders` | `orderId` | — | — | — |
| `deliveries` | `deliveryId` | — | — | — |
| `escrows` | `escrowId` | — | — | — |
| `escrow-audit` | `escrowId` | `timestamp` | — | — |
| `escrow-idempotency` | `idempotencyKey` | — | — | 7 days |
| `payments` | `paymentId` | — | — | — |
| `fraud-signals` | `signalId` | — | — | 30 days |
| `rate-limits` | `key` | — | — | 15 min |
| `sync-metadata` | `userId` | `entityKey` | — | — |

Billing: PAY_PER_REQUEST (on-demand) for all tables.
Removal policy: RETAIN (prod), DESTROY (dev/staging).

---

## API Endpoints

**Base URL**: `https://<api-id>.execute-api.eu-north-1.amazonaws.com` (dev) / `me-south-1` (staging/prod)
**Rate Limit**: 25 req/s sustained, 50 burst (API Gateway level)

### Authentication (public, no JWT required)

| Method | Path | Handler | Purpose |
|--------|------|---------|---------|
| POST | `/auth/register` | RegisterFunction | Phone + password registration with Fayda eKYC |
| POST | `/auth/login` | LoginFunction | Phone + password login (rate-limited: 3 fails = 15min lockout) |
| POST | `/auth/refresh` | RefreshFunction | Refresh expired access token |
| POST | `/auth/logout` | LogoutFunction | Revoke refresh token (requires Bearer token) |
| POST | `/auth/fayda-verify` | FaydaVerifyFunction | Exchange Fayda OIDC auth code for verification token |
| GET | `/health` | HealthCheckFunction | Health check + DynamoDB connectivity |

**Auth request/response shapes:**

```
POST /auth/register
  Request:  { phoneNumber, password, role, woreda, displayName, preferredLanguage, faydaVerificationToken }
  Response: { userId, tokens: { accessToken, refreshToken, idToken, expiresIn }, user: {...} }

POST /auth/login
  Request:  { phoneNumber, password, deviceFingerprint }
  Response: { tokens: { accessToken, refreshToken, idToken, expiresIn }, user: {...} }

POST /auth/refresh
  Request:  { refreshToken }
  Response: { tokens: { accessToken, idToken, expiresIn, tokenType } }

POST /auth/logout
  Request:  { refreshToken }
  Response: { loggedOut: true, message: "..." }

POST /auth/fayda-verify
  Request:  { authorizationCode, state, redirectUri }
  Response: { verified, faydaId, verifiedName, verificationToken, expiresAt }
```

### Data Endpoints (JWT required)

| Method | Path | Handler | Purpose |
|--------|------|---------|---------|
| GET | `/crops` | CropFunction | Browse crop listings (paginated, filterable) |
| GET | `/crops/{id}` | CropFunction | Single crop listing |
| GET | `/trips` | TripFunction | Browse trip listings (paginated, filterable) |
| GET | `/trips/{id}` | TripFunction | Single trip listing |
| GET | `/orders` | OrderFunction | Browse orders |
| GET | `/orders/{id}` | OrderFunction | Single order |
| GET | `/deliveries` | DeliveryFunction | Browse user's deliveries |
| GET | `/deliveries/{id}` | DeliveryFunction | Single delivery (participants only) |
| POST | `/deliveries/{id}/photos` | DeliveryFunction | Upload delivery photo (multipart, max 200KB WebP) |

**Common query params** (GET list endpoints):
`limit` (default 20, max 50), `nextToken` (pagination), `status`, plus entity-specific filters.

**Common response shape** (GET list endpoints):
```json
{ "items": [...], "nextToken": "...", "hasMore": true, "total": 42 }
```

### Sync Endpoint (JWT required)

| Method | Path | Handler | Purpose |
|--------|------|---------|---------|
| POST | `/sync` | SyncFunction | Offline-first batch sync (max 50 ops per request) |

```
POST /sync
  Request:  { syncBatchId, clientId, operations: [
    { operationId, idempotencyKey, entityType, entityId, action, clientVersion, data, timestamp }
  ]}
  Response: { syncBatchId, results: [
    { operationId, status: "success|conflict|error", serverVersion, serverData, message }
  ]}

  Entity types: crop_listing, trip, delivery, order, profile
  Actions: create, update, delete
  Idempotency key: {userId}:{entityType}:{entityId}:{action}:{timestamp}
```

### Escrow/Payment Endpoints (JWT required)

| Method | Path | Handler | Purpose |
|--------|------|---------|---------|
| POST | `/escrow/initiate` | EscrowFunction | Buyer initiates escrow payment |
| POST | `/escrow/release` | EscrowFunction | Release funds to farmer |
| POST | `/escrow/dispute` | EscrowFunction | Raise dispute (within 48h of delivery) |
| POST | `/escrow/refund` | EscrowFunction | Refund to buyer |
| GET | `/escrow/{id}` | EscrowFunction | Get escrow status |
| GET | `/escrow/history` | EscrowFunction | User's payment history (paginated) |

```
POST /escrow/initiate
  Request:  { deliveryId, buyerId, farmerId, amount, currency, idempotencyKey }
  Response: { escrowId, status, amount, commission, farmerPayout, message }

POST /escrow/release
  Request:  { escrowId, trigger, deliveryId, idempotencyKey }
  Response: { escrowId, status: "released", farmerPayout, telebirrRef, releasedAt }

POST /escrow/dispute
  Request:  { escrowId, disputedBy, reason, description, evidenceRefs, idempotencyKey }
  Response: { escrowId, status: "disputed", disputeId, disputeWindowEndsAt }

POST /escrow/refund
  Request:  { escrowId, trigger, reason, idempotencyKey }
  Response: { escrowId, status: "refunded", refundAmount, telebirrRef, refundedAt }
```

**Business rules:**
- Commission: 5% (SSM param)
- Dispute window: 48 hours after delivery confirmation (SSM param)
- Auto-release: After dispute window passes without dispute
- Auto-refund: After 7 days without delivery (SSM param)

### Admin Endpoints (Admin JWT required — separate Cognito pool)

| Method | Path | Purpose |
|--------|------|---------|
| GET | `/admin/dashboard/stats` | Platform statistics |
| GET | `/admin/escrows` | List all escrows |
| GET | `/admin/escrows/{id}` | Single escrow details |
| GET | `/admin/escrows/{id}/audit` | Escrow audit trail |
| GET | `/admin/disputes` | List disputes |
| GET | `/admin/fraud-signals` | List fraud signals |
| GET | `/admin/users` | List users |
| GET | `/admin/users/{id}` | Single user details |
| GET | `/admin/health/overview` | System health |
| GET | `/admin/compliance/summary` | Compliance metrics |
| GET | `/admin/analytics/revenue` | Revenue analytics |
| GET | `/admin/analytics/transactions` | Transaction analytics |
| GET | `/admin/payouts/manual` | Manual payout queue (ESC-02): settlements the live gateway could not make |
| POST | `/admin/disputes/{id}/resolve` | Record a dispute verdict: `farmer_wins` (action `release`) or `buyer_wins` (action `refund`), admin group only; the hourly sweep settles it (ESC-03) |
| POST | `/admin/fraud-signals/{id}/action` | Flag/investigate fraud |
| POST | `/admin/users/{id}/action` | Disable/enable user |
| POST | `/admin/payouts/{escrowId}/{ref}/complete` | Record an admin-paid manual payout leg (`{externalRef}`, admin group only) |

#### Manual payout queue (live mode runbook, ESC-02)

The live Telebirr gateway has no payout API yet. In live mode (`enableTelebirrLive`)
every release and refund fails closed and the escrow waits in the manual payout
queue. Since ESC-05 an unknown gateway outcome (a timeout, a lost answer, a call that
threw) or a settlement lock older than 15 minutes (a crashed run) also lands here, in
mock mode (`TELEBIRR_MOCK=true`, dev) too.

1. Open the admin app, "Manual Payouts" (`/payouts`). Each row is one payee leg:
   escrow, order, operation (release or refund), reason, payee role, name,
   registered phone and the exact amount. Rows older than 24 hours are flagged.
   The page shows the oldest 50; "Load more" fetches the next 50. Only one
   admin works the queue at a time: there is no claim step, so two admins could
   both pay the same leg in the portal.
2. Check the reason before paying:
   - "Gateway has no payout API" (`PAYOUT_NOT_SUPPORTED`): nothing was paid. Pay
     that amount to that phone in the Telebirr merchant portal.
   - "Gateway returned no reference" (`GATEWAY_REF_MISSING`): the gateway said
     it paid, so the money has probably moved. Find that payment in the portal
     and record its id. Pay only if it is really missing.
   - "Gateway outcome unknown" (`GATEWAY_OUTCOME_UNKNOWN`, ESC-05): the gateway may
     have paid. Search the portal for the gateway reference the row shows
     (`release:<escrowId>`, `release:<escrowId>:trucker` or `refund:<escrowId>`); the row
     also shows how long ago the gateway was called. If it is not there, search
     again later before paying (a payment can show up late). Record its id if you
     find it; pay and record only if it is still missing. In mock mode (dev) no
     money exists: record any reference to close it. A `disputed` escrow in the
     queue (only a lock taken before ESC-03 can put one there) settles without a
     verdict: fix its order and delivery by hand. A row that warns "goes against the
     dispute verdict" (a lock taken before the verdict): do not pay it; check the
     escrow history and settle the case by hand.
   - A refund row that says the delivery was confirmed after the refund was
     queued: the goods arrived. Check with the buyer and the farmer before
     refunding. Queued escrows cannot be disputed (`ESCROW_038`); a delivery
     dispute on one shows up as `dispute_refused_manual_payout` in its audit.
3. Paste the portal's transaction id into "Telebirr transaction ID", click
   "Mark as paid" (or "Record reference") and confirm the amount and phone. The
   last leg of an escrow moves it to `completed` (release) or `refunded`
   (refund); until then the escrow keeps its status and stays locked against any
   other settlement.
4. Errors: `ESCROW_037` bad reference format; `ESCROW_036` the reference was
   already recorded for this escrow (another admin, or a replay with a different
   id); `ESCROW_029` the payee fails the identity gate (phone or Fayda), wait for
   them to verify (the leg stays in the queue and the overdue alarm stays on);
   `ESCROW_035` the leg is not open any more (reload). Refusals are audited as
   `manual_payout_refused`.
5. Alarm: the hourly escrow-cron logs `MANUAL_PAYOUT_OVERDUE` for every pending
   leg queued more than 24 hours ago, which feeds
   `${prefix}-manual-payouts-overdue` (ESC-13, see Monitoring below; it needs a
   subscriber on the alarm topic). The cron also logs `ESCROW_MANUAL_PAYOUT_WAITING` for
   every queued escrow a sweep skips, and the service logs
   `ESCROW_MANUAL_PAYOUT_QUEUED` when one enters the queue.

Every step is audited: escrow-audit `manual_payout_queued`, `manual_payout_paid`,
then `release` + `complete` or `refund` with metadata `manual`; transaction-audit
`admin_manual_payout`. A stale lock parked by the hourly recovery step (ESC-05) also
has `stale_lock_parked` just before its `manual_payout_queued` row.

Deploying ESC-05: the first hourly run after the deploy parks every settlement lock
that is already stale in each environment (they were stuck for good before). Count
them first (needs AWS credentials):

```bash
aws dynamodb scan --table-name wereda-connect-<env>-escrows \
  --filter-expression "attribute_exists(settlementLock) AND attribute_not_exists(manualPayout) AND #s IN (:e, :r, :d)" \
  --expression-attribute-names '{"#s":"status"}' \
  --expression-attribute-values '{":e":{"S":"escrowed"},":r":{"S":"dispute_resolved"},":d":{"S":"disputed"}}' \
  --select COUNT
```

Each one shows up in Manual Payouts as `GATEWAY_OUTCOME_UNKNOWN`, raises the
unknown-outcomes alarm once, and the overdue alarm after 24 hours.

### Scheduled Jobs (EventBridge, not HTTP)

| Function | Schedule | Purpose |
|----------|----------|---------|
| EscrowCronFunction | Hourly | Auto-release confirmed escrows, auto-refund expired escrows |
| EscrowCronFunction | Every 6 hours | Reconciliation with Telebirr |
| OrderCronFunction | Hourly | Auto-cancel unpaid expired orders |

---

## Cognito Authentication

**Two separate user pools:**

| Pool | Self-Signup | MFA | Token Validity | Use |
|------|------------|-----|----------------|-----|
| Mobile | Yes (phone) | Optional | Access: 15min, Refresh: 7d | Webapp + mobile app |
| Admin | No | Required (prod) | Access: 15min, Refresh: 7d | Admin dashboard |

**Custom attributes** (mobile pool): `role`, `woreda`, `displayName`, `preferredLanguage`
**Admin group**: `admin` (JWT `cognito:groups` claim)
**Auth flow**: `USER_PASSWORD_AUTH` (phone + password)

---

## GitHub Secrets (13)

Set via `./scripts/setup-gh-secrets.sh --env <env>`:

| Secret | Source |
|--------|--------|
| `AWS_DEPLOY_ROLE_ARN` | IAM OIDC role (auto-created by script) |
| `API_BASE_URL` | CDK ApiStack `ApiUrl` output |
| `COGNITO_USER_POOL_ID` | CDK ApiStack `UserPoolId` output |
| `COGNITO_CLIENT_ID` | CDK ApiStack `UserPoolClientId` output |
| `COGNITO_ADMIN_CLIENT_ID` | CDK ApiStack `AdminClientId` output |
| `COGNITO_ADMIN_USER_POOL_ID` | CDK ApiStack `AdminUserPoolId` output |
| `COGNITO_REGION` | `eu-north-1` (dev) / `me-south-1` (staging/prod) |
| `WEBSITE_S3_BUCKET` | CDK WebStack output |
| `WEBAPP_S3_BUCKET` | CDK WebStack output |
| `ADMIN_S3_BUCKET` | CDK WebStack output |
| `WEBSITE_CF_DISTRIBUTION_ID` | CDK WebStack output |
| `WEBAPP_CF_DISTRIBUTION_ID` | CDK WebStack output |
| `ADMIN_CF_DISTRIBUTION_ID` | CDK WebStack output |

---

## Lambda Environment Variables

All functions receive:
- `COGNITO_USER_POOL_ID`, `COGNITO_CLIENT_ID`, `COGNITO_REGION`
- `{TABLE}_TABLE` — DynamoDB table names (e.g., `USERS_TABLE`, `CROPS_TABLE`)
- `{PARAM}_PARAM` — SSM parameter names (commission rate, dispute window, escrow timeout)
- `{SECRET}_ARN` — secret references (pagination secret, Fayda secrets): a Secrets
  Manager ARN in prod, an SSM SecureString parameter name in dev/staging (free tier;
  seeded by `scripts/seed-ssm-secrets.sh`, which CI runs before every non-prod deploy).
  The server routes on the value's shape — see
  `specs/decisions/secrets-backend-per-environment.md`.

Dev-only flags (blocked in prod by CDK guard):
- `DEV_FAYDA_BYPASS=true` — skip real Fayda verification
- `TELEBIRR_MOCK=true` — use mock Telebirr gateway

---

## CI/CD Workflows

| Workflow | Trigger | What |
|----------|---------|------|
| `deploy-webapp.yml` | Push to `main` (mobile/**) | Build Flutter web, sync to S3, invalidate CloudFront |
| `deploy-admin.yml` | Push to `main` (admin/**) | Build Vite, sync to S3, invalidate CloudFront |
| `deploy-website.yml` | Push to `main` (website/**) | Build Astro, sync to S3, invalidate CloudFront |
| `mobile-tests.yml` | PRs | Flutter analyze + unit/widget tests |
| `server-tests.yml` | PRs | .NET unit + integration tests (LocalStack), AOT validation |
| `infra-tests.yml` | PRs | `npx cdk synth` validation |
| `pr-validation.yml` | PRs | Linting, type checks |

Auth: GitHub OIDC → AWS STS AssumeRoleWithWebIdentity (no stored credentials).

---

## S3 Cache Strategy

| File Type | Cache-Control | CloudFront Invalidated |
|-----------|---------------|----------------------|
| Static assets (JS, CSS, images) | `max-age=31536000, immutable` | No (content-hashed) |
| `index.html` | `max-age=0, must-revalidate` | Yes |
| `flutter_service_worker.js` | `max-age=0, must-revalidate` | Yes |
| `version.json` | `max-age=0, must-revalidate` | Yes |

---

## Rollback Procedures

### Lambda Rollback
```bash
aws lambda list-versions-by-function --function-name <name>
aws lambda update-alias --function-name <name> --name live --function-version <prev>
```

### Full Stack Rollback
```bash
git checkout <previous-tag>
cd infra && npx cdk deploy --all --context environment=prod
```

### DynamoDB Rollback
```bash
aws dynamodb restore-table-to-point-in-time \
  --source-table-name <table> \
  --target-table-name <table>-restored \
  --restore-date-time <ISO8601>
```

---

## Monitoring

```bash
# Tail Lambda error logs
aws logs tail /aws/lambda/<function-name> --follow --filter-pattern "ERROR"

# Search specific errors
aws logs filter-log-events \
  --log-group-name /aws/lambda/<function-name> \
  --filter-pattern "NullReferenceException" \
  --start-time $(date -u -v-1H +%s)000
```

Key alarms (CloudWatch). Every alarm notifies the `wereda-connect-<env>-alarms` SNS topic;
the stack only outputs the ARN, so subscribe an email or phone to it or the alarms fire into
nothing.

- Platform: Lambda errors across the critical functions, API Gateway 5xx rate, DynamoDB
  throttles, cold start duration, order-cron errors, auth brute force, escrow dispute rate.
- Money (ESC-13; token formats and sources in `specs/contracts/escrow-protocol.md`,
  "Additions in 1.8.1"):

| Alarm | Meaning | First action |
|---|---|---|
| `<prefix>-escrow-settlement-failures` | a release, refund or trucker leg failed, or a sweep hit an exception (`ESCROW_SETTLEMENT_FAILED` in the escrow or escrow-cron log group); a payout queued for an admin (`202 ESCROW_034`) is not counted | find the line by escrowId. `outcome=failed` (a definite refusal, a failure before the settlement lock, or a bookkeeping failure after the payout was recorded: the escrow row is `released` or `refunded`, and the crash recovery completes a `released` one): `release_failed` and `refund_failed`: the next sweep retries; `trucker_release_failed`: the farmer is paid and the escrow stays `released` with `truckerPayoutPending`; the hourly trucker retry pays the leg with the same reference (ESC-04), so do not pay the trucker by hand (`<prefix>-trucker-payouts-overdue` fires if it is still owed after 24 hours). `outcome=unknown`: see the next row |
| `<prefix>-escrow-settlement-unknown-outcomes` | (ESC-05) a line above with `outcome=unknown`: a Telebirr timeout or lost answer, a gateway call that threw, a queue write that failed after the gateway answered, an exception after the settlement lock, or a stale lock parked by the recovery step (`action=stale_lock_parked`); the money may have moved and nothing retries it | never release or refund it by hand. Open Manual Payouts (`/payouts`): the escrow is there as `GATEWAY_OUTCOME_UNKNOWN` (or `GATEWAY_REF_MISSING`), or will be within about 75 minutes. Search the portal for the row's gateway reference, record it if found, pay only if it is still missing (runbook above). `ESCROW_SETTLEMENT_PAID_UNRECORDED` lines in the same log group carry the gateway references of a payout that was made but not recorded |
| `<prefix>-escrow-cron-errors` | escrow-cron was killed (5 minute timeout, out of memory) or crashed; a run killed mid-settlement logs no token; 4 periods of 5 minutes, clears 20 to 25 minutes after the last error | find the failed RequestId (REPORT line, "Task timed out") and the last escrowId it logged. An escrow left holding its settlement lock is parked in Manual Payouts by the next hourly run once the lock is 15 minutes old (ESC-05, `stale_lock_parked` line and the unknown-outcomes alarm); treat it as an unknown outcome |
| `<prefix>-order-escrow-problems` | `ORDER_ESCROW_CREATE_FAILED`, `_LINK_FAILED` or `_REFUND_SCHEDULE_FAILED` in the sync Lambda | create self-heals at delivery linkage; link: tie the delivery to the order escrow by hand; refund schedule: set the escrow expiry by hand so the sweep refunds |
| `<prefix>-manual-payouts-overdue` | a manual payout leg (ESC-02) has waited more than 24 hours (`MANUAL_PAYOUT_OVERDUE ... ref=<op>:<leg>`) | open Manual Payouts (`/payouts`), check the reason before paying (runbook above) and record the portal transaction id |
| `<prefix>-trucker-payouts-overdue` | (ESC-04) a trucker leg is still owed more than 24 hours after the farmer was paid (`TRUCKER_PAYOUT_OVERDUE escrowId=.. reason=.. ageHours=..` in the escrow-cron log group); the escrow stays `released` and the hourly retry keeps trying | `reason=phone_unverified` or `fayda_required`: ask the trucker to verify; the next hourly run pays the leg (with the live gateway it moves to Manual Payouts). `reason=no_trucker`, `profile_missing` or `trucker_mismatch`: the escrow has no payable trucker. The hourly run copies the truckerId from the order when the delivery names the same trucker; if the order has none, the leg has no payee until ESC-20. Escalate, and never edit the escrow row by hand. `reason=gateway_refused` or `queue_failed`: see the settlement-failures row |
| `<prefix>-admin-queue-overdue` | a stuck order in the admin queue (ESC-06) has waited more than 24 hours; no producer until ESC-06, so it stays OK | decide it in the admin stuck-order queue (release, refund or extend) |
| `<prefix>-dispute-verdict-settlement-blocked` | a dispute verdict's settlement failed closed in three of the last four hourly periods (1 hour periods, 3 of 4 datapoints, so sweep jitter that leaves one hour empty does not hide it) (`DISPUTE_VERDICT_SETTLEMENT_BLOCKED escrowId=.. op=.. legs=..` in the escrow-cron log group, ESC-03): `read:<error>` (the order or delivery cannot be read), `cancel:<reason>` or `restock:<reason>` (a `buyer_wins` order could not be cancelled or its stock given back before the refund); no money moved | find the escrow (escrow-audit row `dispute_settlement_blocked` has the legs), fix the cause (Lambda permissions, throttling, the order or listing row), and let the next sweep settle it; it clears once a sweep is no longer blocked |
| `<prefix>-dispute-verdict-order-mismatch` | a dispute verdict was not settled because its order or delivery is in a status the verdict cannot follow (`DISPUTE_VERDICT_ORDER_MISMATCH escrowId=.. op=.. legs=..` in the escrow or escrow-cron log group, ESC-03); usually a dispute raised while the goods were moving, or a `farmer_wins` before delivery; no money moved and the hourly sweep retries | find the escrow by escrowId (the escrow-audit row `dispute_verdict_mismatch` carries the legs). `farmer_wins`: it settles by itself once the delivery is confirmed and the order reaches `delivered`. `buyer_wins` with the goods moving: confirm the delivery, or refund by hand with `POST /escrow/refund` (admin group) and `{"escrowId": ..., "trigger": "dispute_resolved", "reason": "<10-500 chars>", "overrideOrderMismatch": true, "idempotencyKey": ...}`; the override is audited with your id and reason and the order is flagged (`disputeOverrideBy`) |

The settlement-failure, the unknown-outcome, the verdict-mismatch, the trucker-payouts and the two queue alarms evaluate 5 periods of 15 minutes, so an
hourly producer keeps them in ALARM without flapping; they clear by themselves 75 to 90
minutes after the last line. Order escrow problems uses one 15 minute period.

Synthetic test of the money alarms (after a deploy; needs AWS credentials, see the script
header for the exact permissions):

```bash
# Read-only: deployed filter patterns match the server's exact lines, alarm shapes, current states
scripts/money_alarms_synthetic.sh --env dev

# Fire every money alarm once from synthetic log lines plus one failed escrow-cron invocation
# (no money moves; subscribers get one ALARM and later one OK per alarm), then wait for them to clear
# (up to 2 hours). An alarm already in ALARM is reported as not proven and the run fails.
scripts/money_alarms_synthetic.sh --env dev --fire --wait-clear
```

---

## Troubleshooting

| Error | Fix |
|-------|-----|
| `UPDATE_ROLLBACK_COMPLETE` | `cdk destroy <stack>` then redeploy |
| Insufficient permissions | Verify `aws sts get-caller-identity`; need CF/Lambda/APIGW/DDB/S3/IAM:PassRole |
| Lambda not updating | Force: `aws lambda update-function-code --function-name <name> ...` |
| GitHub Actions "Credentials could not be loaded" | Run `./scripts/setup-gh-secrets.sh` |
| Webapp "App setup incomplete" | `API_BASE_URL` / `COGNITO_*` secrets are empty |
| Admin loads without login | `VITE_COGNITO_*` secrets missing |
| CloudFront stale content | `aws cloudfront create-invalidation --distribution-id <ID> --paths "/*"` |

---

## Key File Paths

| Category | Path |
|----------|------|
| CDK entry point | `infra/bin/wereda-connect.ts` |
| CDK stacks | `infra/lib/stacks/` |
| CDK constructs | `infra/lib/constructs/` |
| Environment config | `infra/lib/config/environments.ts` |
| Lambda bootstrap | `server/src/Program.cs` |
| Lambda handlers | `server/src/Functions/` |
| Business logic | `server/src/Services/` |
| DynamoDB repos | `server/src/Data/` |
| API contracts | `specs/contracts/` |
| Deploy script | `scripts/deploy.sh` |
| GH secrets script | `scripts/setup-gh-secrets.sh` |
| CI/CD workflows | `.github/workflows/` |
| Deployment guide | `docs/developer/deployment.md` |

---

**Last Updated**: 2026-03-07
