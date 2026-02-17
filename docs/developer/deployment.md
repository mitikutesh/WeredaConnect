# Deployment Guide

**Prerequisites**: AWS CLI configured, CDK CLI installed.

## Environments

| Environment | Auto-Deploy | Approval |
|-------------|-------------|----------|
| **Dev** | On push to `main` | None |
| **Staging** | Manual | Yes |
| **Production** | Manual | Yes |

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
# 1. Runs tests
# 2. Builds artifacts
# 3. Deploys to dev
# 4. Runs smoke tests
```

### Manual Deployment

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

## Troubleshooting

| Error | Fix |
|-------|-----|
| "UPDATE_ROLLBACK_COMPLETE state" | `cdk destroy <stack>` then redeploy |
| "Insufficient permissions" | Verify with `aws sts get-caller-identity`; need cloudformation/lambda/apigateway/dynamodb/s3/iam:PassRole |
| Lambda not updating | Force: `aws lambda update-function-code --function-name <name> --s3-bucket <bucket> --s3-key <key>` |

---

**Last Updated**: 2026-02-16

