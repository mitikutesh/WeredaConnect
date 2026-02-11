# Deployment Quick Start Guide

---

## Prerequisites

```bash
# AWS CLI configured
aws configure

# CDK CLI installed
npm install -g aws-cdk

# .NET 10 SDK
dotnet --version

# Flutter (for mobile)
flutter --version

# GitHub CLI (optional)
gh --version
```

---

## Local Testing (Before Deployment)

### 1. Test Infrastructure Locally
```bash
cd infra

# Install dependencies
npm install

# Run tests
npm test

# Synthesize CloudFormation templates
npm run synth

# View diff (what will be deployed)
npm run diff
```

### 2. Test Server Locally
```bash
cd server

# Restore dependencies
dotnet restore

# Build with AOT validation
dotnet build

# Run tests
dotnet test

# Publish AOT binary
dotnet publish -c Release -r linux-arm64 --self-contained

# Check binary size
ls -lh bin/Release/net10.0/linux-arm64/publish/bootstrap
```

### 3. Test Mobile Locally
```bash
cd mobile

# Get dependencies
flutter pub get

# Run tests
flutter test

# Build debug APK
flutter build apk --debug

# Build release APK (requires signing)
flutter build apk --release
```

---

## Development Deployment (Auto)

**Trigger**: Push to `main` branch

```bash
# Make changes
git checkout -b feature/my-feature
# ... make changes ...

# Commit and push
git add .
git commit -m "feat: add new feature"
git push origin feature/my-feature

# Create PR, get approval, merge to main
# GitHub Actions automatically:
# 1. Runs all tests
# 2. Builds artifacts
# 3. Deploys to dev
# 4. Runs smoke tests
```

**Verify deployment**:
```bash
# Check API health
curl https://dev-api.weredaconnect.com/health

# Check CloudWatch logs
aws logs tail /aws/lambda/wereda-connect-api-dev --follow
```

---

## Staging Deployment (Manual)

### Step 1: Prepare Release
```bash
# Checkout main
git checkout main
git pull origin main

# Create release branch
git checkout -b release/v1.2.0

# Update versions
# - mobile/pubspec.yaml: version: 1.2.0+12
# - server/WeredaConnect.Server.csproj: <Version>1.2.0</Version>
# - infra/package.json: "version": "1.2.0"

# Update CHANGELOG.md
# Commit
git add .
git commit -m "chore: release v1.2.0"
git push origin release/v1.2.0

# Create PR, get approval, merge to main
```

### Step 2: Deploy Infrastructure
```bash
cd infra

# Build server
cd ../server
dotnet publish -c Release -r linux-arm64 --self-contained

# Deploy to staging
cd ../infra
npm run cdk deploy WeredaConnect-*-Staging --require-approval broadening

# Review CloudFormation changes
# Type 'y' to approve
```

### Step 3: Verify Deployment
```bash
# Check API health
curl https://staging-api.weredaconnect.com/health

# Run integration tests
npm run test:integration:staging

# Run E2E tests
cd ../mobile
maestro test maestro/ --env staging

# Manual testing
# - Test critical user flows
# - Check performance
# - Verify data integrity
```

### Step 4: Release Mobile (Optional)
```bash
cd mobile

# Build release APK
flutter build apk --release --split-per-abi

# Build App Bundle
flutter build appbundle --release

# Upload to Google Play Console
# - Go to https://play.google.com/console
# - Select WeredaConnect
# - Create new release
# - Upload build/app/outputs/bundle/release/app-release.aab
# - Set staged rollout: 10% → 50% → 100%
```

---

## Production Deployment (Manual, Requires Approval)

### Step 1: Tag Release
```bash
# Create release tag
git tag -a v1.2.0 -m "Release version 1.2.0"
git push origin v1.2.0
```

### Step 2: Backup Database
```bash
# Verify backups exist
aws dynamodb describe-continuous-backups \
  --table-name wereda-connect-users

# Create manual backup (optional)
aws dynamodb create-backup \
  --table-name wereda-connect-users \
  --backup-name wereda-connect-users-v1.2.0-backup
```

### Step 3: Deploy Infrastructure
```bash
cd infra

# Build server
cd ../server
dotnet publish -c Release -r linux-arm64 --self-contained

# Deploy to production
cd ../infra
npm run cdk deploy WeredaConnect-*-Prod --require-approval broadening

# Review CloudFormation changes carefully
# Type 'y' to approve
```

### Step 4: Monitor Deployment
```bash
# Monitor Lambda errors (30 minutes)
aws logs tail /aws/lambda/wereda-connect-api-prod --follow

# Monitor API Gateway errors
aws cloudwatch get-metric-statistics \
  --namespace AWS/ApiGateway \
  --metric-name 5XXError \
  --start-time $(date -u -d '30 minutes ago' +%Y-%m-%dT%H:%M:%S) \
  --end-time $(date -u +%Y-%m-%dT%H:%M:%S) \
  --period 300 \
  --statistics Sum

# Check CloudWatch dashboard
# - Lambda invocations, errors, duration
# - API Gateway requests, errors
# - DynamoDB read/write capacity
```

### Step 5: Release Mobile
```bash
cd mobile

# Build release APK
flutter build apk --release --split-per-abi

# Build App Bundle
flutter build appbundle --release

# Upload to Google Play Console
# - Set staged rollout: 10% → 50% → 100%
# - Monitor crash reports
# - Increase rollout after 24h
```

---

## Rollback Procedures

### Quick Rollback (Lambda Only)
```bash
# Get previous version
aws lambda list-versions-by-function \
  --function-name wereda-connect-api-prod

# Update alias to previous version
aws lambda update-alias \
  --function-name wereda-connect-api-prod \
  --name live \
  --function-version <previous-version>

# Verify
curl https://api.weredaconnect.com/health
```

### Full Stack Rollback
```bash
# Revert to previous release tag
git checkout v1.1.0

# Redeploy
cd infra
npm run cdk deploy WeredaConnect-*-Prod

# Verify
npm run smoke-test:prod
```

### Database Rollback
```bash
# Restore from point-in-time backup
aws dynamodb restore-table-to-point-in-time \
  --source-table-name wereda-connect-users \
  --target-table-name wereda-connect-users-restored \
  --restore-date-time 2026-02-09T10:00:00Z

# Verify data
# Swap tables (update Lambda env vars)
# Delete old table
```

---

## Monitoring Commands

```bash
# View recent Lambda errors
aws logs tail /aws/lambda/wereda-connect-api-prod \
  --follow \
  --filter-pattern "ERROR"

# Check Lambda metrics
aws cloudwatch get-metric-statistics \
  --namespace AWS/Lambda \
  --metric-name Duration \
  --dimensions Name=FunctionName,Value=wereda-connect-api-prod \
  --start-time $(date -u -d '1 hour ago' +%Y-%m-%dT%H:%M:%S) \
  --end-time $(date -u +%Y-%m-%dT%H:%M:%S) \
  --period 300 \
  --statistics Average,Maximum

# Check DynamoDB throttles
aws cloudwatch get-metric-statistics \
  --namespace AWS/DynamoDB \
  --metric-name UserErrors \
  --dimensions Name=TableName,Value=wereda-connect-users \
  --start-time $(date -u -d '1 hour ago' +%Y-%m-%dT%H:%M:%S) \
  --end-time $(date -u +%Y-%m-%dT%H:%M:%S) \
  --period 300 \
  --statistics Sum
```

---

## Troubleshooting

### Deployment Fails
1. Check CloudFormation events: `aws cloudformation describe-stack-events`
2. Check IAM permissions: `aws sts get-caller-identity`
3. Check CDK logs: `npm run cdk deploy -- --debug`

### Lambda Not Updating
1. Check function code: `aws lambda get-function`
2. Check environment variables: `aws lambda get-function-configuration`
3. Force update: `aws lambda update-function-code`

### API Gateway Errors
1. Check API logs: `aws logs tail /aws/apigateway/wereda-connect-api-prod`
2. Check throttling: `aws cloudwatch get-metric-statistics`
3. Check Lambda integration: `aws apigatewayv2 get-integration`

---

## Emergency Contacts

- **On-Call**: [Team member name]
- **Escalation**: [Manager name]
- **AWS Support**: [Support plan details]

---

## Related Documentation

- `DEPLOYMENT_INFRASTRUCTURE_SUMMARY.md` - Overview
- `DEPLOYMENT_IMPLEMENTATION_PLAN.md` - Implementation roadmap
- `DEPLOYMENT_TECHNICAL_REFERENCE.md` - Technical details
- `docs/developer/deployment.md` - Full deployment guide

