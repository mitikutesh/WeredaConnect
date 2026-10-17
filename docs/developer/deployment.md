# Deployment Guide

**Prerequisites**: AWS CLI configured, CDK CLI installed, `gh` CLI authenticated.

## Environments

| Environment | Auto-Deploy | Approval | URLs |
|-------------|-------------|----------|------|
| **Dev** | On push to `main` | None | `dev-app.weredaconnect.com`, `dev-admin.weredaconnect.com`, `dev.weredaconnect.com` |
| **Staging** | Manual | Yes | `staging-app.weredaconnect.com` |
| **Production** | Manual | Yes | `app.weredaconnect.com`, `admin.weredaconnect.com`, `weredaconnect.com` |

---

## First-Time Setup: GitHub Secrets

Before any deployment workflow can run, you must configure GitHub repository secrets. A setup script automates this.

### Prerequisites

1. **AWS CLI** authenticated with admin access: `aws sts get-caller-identity`
2. **GitHub CLI** authenticated: `gh auth login`
3. **CDK stacks deployed** (so CloudFormation outputs exist): `cd infra && npx cdk deploy --all`

### Run the Setup Script

```bash
# Auto mode — reads CDK outputs + .env files, creates OIDC IAM role if needed
./scripts/setup-gh-secrets.sh --env dev

# Manual mode — prompts for every value
./scripts/setup-gh-secrets.sh --manual

# With explicit AWS region
./scripts/setup-gh-secrets.sh --env dev --region eu-north-1
```

The script configures these 13 secrets:

| Secret | Source | Used By |
|--------|--------|---------|
| `AWS_DEPLOY_ROLE_ARN` | Created by script or manual | All deploy workflows |
| `API_BASE_URL` | CDK `ApiUrl` output / `.env` | Webapp, Admin |
| `COGNITO_USER_POOL_ID` | CDK `UserPoolId` output / `.env` | Webapp |
| `COGNITO_CLIENT_ID` | CDK `UserPoolClientId` output / `.env` | Webapp |
| `COGNITO_ADMIN_USER_POOL_ID` | CDK `AdminUserPoolId` output / `admin/.env` | Admin |
| `COGNITO_ADMIN_CLIENT_ID` | CDK `AdminClientId` output / `admin/.env` | Admin |
| `COGNITO_REGION` | AWS region (e.g. `eu-north-1`) | Webapp, Admin |
| `WEBSITE_S3_BUCKET` | CDK `WebsiteBucketName` output | Website |
| `WEBAPP_S3_BUCKET` | CDK `WebappBucketName` output | Webapp |
| `ADMIN_S3_BUCKET` | CDK `AdminBucketName` output | Admin |
| `WEBSITE_CF_DISTRIBUTION_ID` | CDK `WebsiteDistributionIdOutput` | Website |
| `WEBAPP_CF_DISTRIBUTION_ID` | CDK `WebappDistributionIdOutput` | Webapp |
| `ADMIN_CF_DISTRIBUTION_ID` | CDK `AdminDistributionIdOutput` | Admin |

### Verify Secrets

```bash
gh secret list
```

### GitHub OIDC IAM Role

The setup script automatically creates an IAM role (`wereda-connect-{env}-github-deploy`) with:
- **Trust policy**: GitHub Actions OIDC, only jobs on `main` that set `environment: {env}` (sub `repo:<owner>/<repo>:environment:{env}:ref:refs/heads/main`; needs the repo's OIDC subject template, see `.ai/deployment.md` "CI deploy role trust (FUP-31)")
- **Permissions**: S3 put/get/delete + CloudFront cache invalidation

If you prefer to create the role manually, see [AWS docs on GitHub OIDC](https://docs.github.com/en/actions/security-for-github-actions/security-hardening-your-deployments/configuring-openid-connect-in-amazon-web-services).

---

## CI/CD Workflows

### Automatic Deploys (on push to `main`)

| Workflow | Trigger Paths | What It Deploys |
|----------|---------------|-----------------|
| `Deploy Webapp (Flutter Web)` | `mobile/**` | Flutter web app → S3 + CloudFront |
| `Deploy Admin Dashboard (Vite)` | `admin/**` | React admin → S3 + CloudFront |
| `Deploy Website (Astro)` | `website/**` | Marketing site → S3 + CloudFront |

### Manual Deploys

```bash
# Trigger individual workflows with environment selection
gh workflow run "Deploy Webapp (Flutter Web)" --field environment=dev
gh workflow run "Deploy Admin Dashboard (Vite)" --field environment=dev
gh workflow run "Deploy Website (Astro)" --field environment=dev
```

### Test & Validation Workflows (on PRs)

| Workflow | What It Checks |
|----------|----------------|
| `PR Validation` | Linting, type checks, Flutter analyze |
| `Mobile Tests (Flutter)` | Unit + widget tests |
| `Server Tests (.NET 10)` | Unit + integration tests |
| `Infrastructure Tests (CDK)` | CDK synth validation |

---

## Deploy to Development

### Automatic Deployment

**Trigger**: Push to `main` branch

```bash
git checkout main
git pull origin main
git merge feature/your-feature
git push origin main

# GitHub Actions automatically:
# 1. Runs tests (PR Validation, Mobile Tests, Server Tests)
# 2. Builds web artifacts (Flutter web, Vite admin, Astro website)
# 3. Syncs to S3 with proper cache headers
# 4. Invalidates CloudFront cache for index.html / service worker
```

### Manual Deployment (Backend / Infrastructure)

```bash
# 1. Navigate to infrastructure
cd infra

# 2. Install dependencies
npm install

# 3. Build backend
cd ../server
dotnet publish -c Release -r linux-arm64 --self-contained

# 4. Deploy infrastructure
cd ../infra
npm run cdk deploy WeredaConnect-*-Dev

# 5. Run smoke tests
npm run smoke-test:dev
```

### Manual Deployment (Web Apps Only)

```bash
# Admin dashboard
cd admin && npm ci && npm run build
aws s3 sync dist/ s3://wereda-connect-dev-admin/ --delete
aws cloudfront create-invalidation --distribution-id <ID> --paths "/index.html"

# Flutter webapp
cd mobile && flutter build web --release
aws s3 sync build/web/ s3://wereda-connect-dev-webapp/ --delete
aws cloudfront create-invalidation --distribution-id <ID> --paths "/index.html" "/flutter_service_worker.js"

# Marketing website
cd website && npm ci && npm run build
aws s3 sync dist/ s3://wereda-connect-dev-website/ --delete
aws cloudfront create-invalidation --distribution-id <ID> --paths "/index.html"
```

---

## Deploy to Staging

```bash
git checkout main && git pull origin main
git checkout -b release/v1.2.0  # optional

# Build + deploy
cd server && dotnet publish -c Release -r linux-arm64 --self-contained
cd ../infra && npm run cdk deploy WeredaConnect-*-Staging --require-approval broadening

# Verify
curl https://staging-api.weredaconnect.com/health
npm run test:integration:staging
cd ../mobile && maestro test maestro/ --env staging
```

---

## Deploy to Production

```bash
# Tag release
git tag -a v1.2.0 -m "Release version 1.2.0"
git push origin v1.2.0

# Verify DynamoDB backups
aws dynamodb describe-continuous-backups --table-name WeredaConnect-Reports-Prod

# Deploy
cd infra
npm run cdk deploy WeredaConnect-*-Prod --require-approval broadening

# Smoke test + monitor
npm run smoke-test:prod
aws logs tail /aws/lambda/WeredaConnect-CreateReport-Prod --follow --filter-pattern "ERROR"
```

---

## Rollback Procedures

### Lambda Rollback

```bash
# 1. List function versions
aws lambda list-versions-by-function \
  --function-name WeredaConnect-CreateReport-Prod

# 2. Update alias to previous version
aws lambda update-alias \
  --function-name WeredaConnect-CreateReport-Prod \
  --name live \
  --function-version <previous-version>

# 3. Verify rollback
curl https://api.weredaconnect.com/health
```

### Full Stack Rollback

```bash
# 1. Revert to previous CDK version
git checkout v1.1.0

# 2. Redeploy
cd infra
npm run cdk deploy WeredaConnect-*-Prod

# 3. Verify
npm run smoke-test:prod
```

### Database Rollback

```bash
# Restore from point-in-time backup
aws dynamodb restore-table-to-point-in-time \
  --source-table-name WeredaConnect-Reports-Prod \
  --target-table-name WeredaConnect-Reports-Prod-Restored \
  --restore-date-time 2026-02-09T10:00:00Z

# Swap tables (requires downtime)
# 1. Update Lambda environment variables to point to restored table
# 2. Verify data integrity
# 3. Delete old table
```

---

## Mobile App Deployment

### Android (Google Play)

```bash
# 1. Update version
# Edit mobile/pubspec.yaml
version: 1.2.0+10  # version+build number

# 2. Build release APK
cd mobile
flutter build apk --release

# 3. Build App Bundle (recommended)
flutter build appbundle --release

# 4. Upload to Google Play Console
# - Go to https://play.google.com/console
# - Select WeredaConnect app
# - Create new release
# - Upload build/app/outputs/bundle/release/app-release.aab
# - Fill in release notes
# - Submit for review

# 5. Staged rollout (recommended)
# - Start with 10% of users
# - Monitor crash reports
# - Increase to 50%, then 100%
```

### iOS (App Store) - Coming Soon

---

## Monitoring

**Key alarms** (configured in CDK):
- Lambda error rate > 1%
- API Gateway 5xx rate > 0.5%
- Lambda p99 duration > 5s
- DynamoDB throttles > 0

```bash
# Tail error logs
aws logs tail /aws/lambda/WeredaConnect-CreateReport-Prod --follow --filter-pattern "ERROR"

# Search specific errors
aws logs filter-log-events \
  --log-group-name /aws/lambda/WeredaConnect-CreateReport-Prod \
  --filter-pattern "NullReferenceException" \
  --start-time $(date -u -d '1 hour ago' +%s)000
```

---

## Web Deployment Details

### S3 Cache Strategy

All deploy workflows use a two-tier caching strategy:

| File Type | Cache-Control | Reason |
|-----------|---------------|--------|
| Static assets (JS, CSS, images) | `public, max-age=31536000, immutable` | Content-hashed filenames, safe to cache forever |
| `index.html` | `public, max-age=0, must-revalidate` | Entry point must always be fresh |
| `flutter_service_worker.js` | `public, max-age=0, must-revalidate` | Controls app updates |
| `version.json` | `public, max-age=0, must-revalidate` | Version check file |

### Build Environment Variables

**Flutter Webapp** — passed via `--dart-define`:
```
ENVIRONMENT, API_BASE_URL, COGNITO_USER_POOL_ID, COGNITO_CLIENT_ID, COGNITO_REGION
```

**Admin Dashboard (Vite)** — passed via `VITE_*` env vars:
```
VITE_ENVIRONMENT, VITE_API_BASE_URL, VITE_COGNITO_ADMIN_USER_POOL_ID, VITE_COGNITO_ADMIN_CLIENT_ID, VITE_COGNITO_REGION
```

**Website (Astro)** — no runtime env vars needed (static marketing site).

### Flutter Web SDK Version

The deploy workflow uses Flutter 3.38.9. If you update your local Flutter SDK, also update `.github/workflows/deploy-webapp.yml` and `.github/workflows/mobile-tests.yml` to match.

---

## Troubleshooting

| Error | Fix |
|-------|-----|
| "UPDATE_ROLLBACK_COMPLETE state" | `cdk destroy <stack>` then redeploy |
| "Insufficient permissions" | Verify with `aws sts get-caller-identity`; need cloudformation/lambda/apigateway/dynamodb/s3/iam:PassRole |
| Lambda not updating | Force: `aws lambda update-function-code --function-name <name> --s3-bucket <bucket> --s3-key <key>` |
| "Credentials could not be loaded" in deploy workflow | Run `./scripts/setup-gh-secrets.sh` — `AWS_DEPLOY_ROLE_ARN` secret is missing or the OIDC role doesn't exist |
| "App setup incomplete" on webapp | `API_BASE_URL` / `COGNITO_*` secrets are empty — verify with `gh secret list` and re-run setup script |
| Admin dashboard loads without login | `VITE_COGNITO_*` secrets are empty — the auth gate requires Cognito config at build time |
| Flutter web build fails with "Could not find constructor" | Flutter version mismatch — update `flutter-version` in `deploy-webapp.yml` to match local SDK |
| "Could not find option --web-renderer" | Remove `--web-renderer html` from workflow — deprecated in Flutter 3.38+ |
| CloudFront returns stale content | Manually invalidate: `aws cloudfront create-invalidation --distribution-id <ID> --paths "/*"` |

---

**Last Updated**: 2026-03-05
