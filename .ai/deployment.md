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
              Secrets Manager
              SSM Parameter Store
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
npx cdk deploy --all --context environment=dev

# Configure GitHub secrets from CDK outputs
./scripts/setup-gh-secrets.sh --env dev
```

### 2. Full Backend Deploy (Server + Infra)

```bash
# One command does it all: test → Docker build → CDK deploy
./scripts/deploy.sh dev       # or staging, prod
```

Steps performed by `deploy.sh`:
1. `dotnet test` — run server tests
2. Docker build — .NET 10 AOT for linux-arm64 via `mcr.microsoft.com/dotnet/sdk:10.0`
3. `npx tsc` — compile CDK TypeScript
4. `npx cdk deploy --all --context environment=$ENV --require-approval broadening`

### 3. Manual Backend Deploy (without script)

```bash
# Build server
cd server
dotnet publish -c Release -r linux-arm64 --self-contained

# Deploy infra
cd ../infra
npm install
npx cdk deploy --all --context environment=dev
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
| **ApiStack** | `{prefix}-api` | 14 Lambdas, API Gateway, 2 Cognito pools, EventBridge rules, Secrets Manager, SSM params |
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
| POST | `/admin/disputes/{id}/resolve` | Resolve dispute (farmer_wins, buyer_wins, partial_refund) |
| POST | `/admin/fraud-signals/{id}/action` | Flag/investigate fraud |
| POST | `/admin/users/{id}/action` | Disable/enable user |

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
- `{SECRET}_ARN` — Secrets Manager ARNs (pagination secret, Fayda secrets)

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

Key alarms (CloudWatch):
- Lambda error rate > 1%
- API Gateway 5xx rate > 0.5%
- Lambda p99 duration > 5s
- DynamoDB throttles > 0

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
