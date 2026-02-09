# Deployment Guide

## How to Deploy WeredaConnect

Step-by-step guide for deploying to AWS environments.

## Audience
- DevOps engineers
- Release managers
- Infrastructure developers

## Prerequisites
- AWS account with appropriate permissions
- AWS CLI configured
- CDK CLI installed
- Read [Deployment Architecture](../architecture/deployment-architecture.md)

---

## Deployment Checklist

### Pre-Deployment

- [ ] All tests passing locally
- [ ] Code reviewed and approved
- [ ] Documentation updated
- [ ] CHANGELOG.md updated
- [ ] Version bumped (if applicable)
- [ ] Database migrations prepared (if applicable)
- [ ] Rollback plan documented

### Post-Deployment

- [ ] Smoke tests passed
- [ ] Monitoring dashboards checked
- [ ] Error rates normal
- [ ] Performance metrics acceptable
- [ ] Team notified
- [ ] Documentation updated with deployment notes

---

## Environment Overview

| Environment | Purpose | Auto-Deploy | Approval Required |
|-------------|---------|-------------|-------------------|
| **Dev** | Active development | ✅ Yes (on push to main) | ❌ No |
| **Staging** | Pre-production testing | ❌ Manual | ✅ 1 approver |
| **Production** | Live users | ❌ Manual | ✅ 2 approvers |

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

### Prerequisites

- [ ] All dev tests passing
- [ ] Feature complete and tested
- [ ] PR approved and merged to main

### Deployment Steps

```bash
# 1. Checkout main
git checkout main
git pull origin main

# 2. Create release branch (optional)
git checkout -b release/v1.2.0

# 3. Update version
# Edit mobile/pubspec.yaml, server/*.csproj, infra/package.json

# 4. Build backend
cd server
dotnet publish -c Release -r linux-arm64 --self-contained

# 5. Deploy to staging
cd ../infra
npm run cdk deploy WeredaConnect-*-Staging --require-approval broadening

# 6. Approve changes in terminal
# Review CloudFormation changes
# Type 'y' to approve

# 7. Wait for deployment (5-10 minutes)

# 8. Run integration tests
npm run test:integration:staging

# 9. Run E2E tests
cd ../mobile
maestro test maestro/ --env staging

# 10. Manual testing
# Test critical user flows manually
```

### Verification

```bash
# Check API health
curl https://staging-api.weredaconnect.com/health

# Check CloudWatch logs
aws logs tail /aws/lambda/WeredaConnect-CreateReport-Staging --follow

# Check metrics
aws cloudwatch get-metric-statistics \
  --namespace AWS/Lambda \
  --metric-name Errors \
  --dimensions Name=FunctionName,Value=WeredaConnect-CreateReport-Staging \
  --start-time $(date -u -d '5 minutes ago' +%Y-%m-%dT%H:%M:%S) \
  --end-time $(date -u +%Y-%m-%dT%H:%M:%S) \
  --period 300 \
  --statistics Sum
```

---

## Deploy to Production

### Prerequisites

- [ ] Staging deployment successful
- [ ] All tests passing on staging
- [ ] Manual testing completed
- [ ] Performance testing completed
- [ ] Security review completed
- [ ] Rollback plan documented
- [ ] Team notified of deployment window
- [ ] 2 approvers available

### Deployment Steps

```bash
# 1. Create release tag
git tag -a v1.2.0 -m "Release version 1.2.0"
git push origin v1.2.0

# 2. Backup DynamoDB (automatic via point-in-time recovery)
# Verify backups exist:
aws dynamodb describe-continuous-backups \
  --table-name WeredaConnect-Reports-Prod

# 3. Deploy infrastructure
cd infra
npm run cdk deploy WeredaConnect-*-Prod --require-approval broadening

# 4. Approve changes (requires 2 approvers in GitHub)
# Or approve in terminal after reviewing changes

# 5. Monitor deployment
# Watch CloudFormation stack in AWS Console
# Or use CLI:
aws cloudformation describe-stack-events \
  --stack-name WeredaConnect-API-Prod \
  --max-items 20

# 6. Run smoke tests
npm run smoke-test:prod

# 7. Monitor for 30 minutes
# Watch CloudWatch dashboards
# Check error rates
# Check latency metrics
```

### Post-Deployment Monitoring

```bash
# Monitor Lambda errors
aws cloudwatch get-metric-statistics \
  --namespace AWS/Lambda \
  --metric-name Errors \
  --dimensions Name=FunctionName,Value=WeredaConnect-CreateReport-Prod \
  --start-time $(date -u -d '30 minutes ago' +%Y-%m-%dT%H:%M:%S) \
  --end-time $(date -u +%Y-%m-%dT%H:%M:%S) \
  --period 300 \
  --statistics Sum

# Monitor API Gateway 5xx errors
aws cloudwatch get-metric-statistics \
  --namespace AWS/ApiGateway \
  --metric-name 5XXError \
  --dimensions Name=ApiName,Value=WeredaConnect-API-Prod \
  --start-time $(date -u -d '30 minutes ago' +%Y-%m-%dT%H:%M:%S) \
  --end-time $(date -u +%Y-%m-%dT%H:%M:%S) \
  --period 300 \
  --statistics Sum

# Check logs for errors
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

## Monitoring After Deployment

### CloudWatch Dashboards

1. **Lambda Metrics**:
   - Invocations
   - Errors
   - Duration (p50, p99)
   - Throttles

2. **API Gateway Metrics**:
   - Request count
   - 4xx errors
   - 5xx errors
   - Latency

3. **DynamoDB Metrics**:
   - Read/write capacity
   - Throttled requests
   - System errors

### Alarms

Set up alarms for:
- Lambda error rate > 1%
- API Gateway 5xx rate > 0.5%
- Lambda duration p99 > 5 seconds
- DynamoDB throttles > 0

### Logs

```bash
# View recent errors
aws logs tail /aws/lambda/WeredaConnect-CreateReport-Prod \
  --follow \
  --filter-pattern "ERROR"

# Search for specific error
aws logs filter-log-events \
  --log-group-name /aws/lambda/WeredaConnect-CreateReport-Prod \
  --filter-pattern "NullReferenceException" \
  --start-time $(date -u -d '1 hour ago' +%s)000
```

---

## Troubleshooting Deployments

### CDK Deploy Fails

**Error**: "Stack is in UPDATE_ROLLBACK_COMPLETE state"

```bash
# Delete stack and redeploy
cdk destroy WeredaConnect-API-Dev
cdk deploy WeredaConnect-API-Dev
```

**Error**: "Insufficient permissions"

```bash
# Check IAM permissions
aws sts get-caller-identity

# Ensure you have:
# - cloudformation:*
# - lambda:*
# - apigateway:*
# - dynamodb:*
# - s3:*
# - iam:PassRole
```

### Lambda Function Not Updating

```bash
# Force update
aws lambda update-function-code \
  --function-name WeredaConnect-CreateReport-Prod \
  --s3-bucket <deployment-bucket> \
  --s3-key <new-code-key>

# Clear cache
aws lambda update-function-configuration \
  --function-name WeredaConnect-CreateReport-Prod \
  --environment Variables={CACHE_BUST=$(date +%s)}
```

---

## Related Documentation

- **[Deployment Architecture](../architecture/deployment-architecture.md)** - Infrastructure overview
- **[Development Workflow](./dev-workflow.md)** - Development process
- **[Getting Started](./getting-started.md)** - Environment setup

---

**Last Updated**: 2026-02-09  
**Version**: 1.0.0  
**Maintainer**: DevOps Team

