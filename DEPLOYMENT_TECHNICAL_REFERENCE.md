# Deployment Technical Reference

---

## AWS CDK Stack Structure

### Current Architecture
```
WeredaConnectStack (single stack)
├── DynamoDB Tables
│   ├── users (on-demand)
│   ├── crops (on-demand)
│   └── trips (on-demand)
├── S3 Bucket
│   └── uploads (lifecycle: 7 days)
├── API Gateway HTTP API
│   └── (Lambda integration - commented out)
└── Outputs
    ├── ApiUrl
    ├── TableNames
    └── BucketName
```

### Recommended Modular Structure
```
App
├── DatabaseStack (foundational)
│   └── DynamoDB tables, S3 bucket
├── ApiStack (depends on DatabaseStack)
│   └── Lambda functions, API Gateway
├── MonitoringStack (independent)
│   └── CloudWatch dashboards, alarms
└── Environment-specific configs
    ├── dev.ts (minimal resources)
    ├── staging.ts (production-like)
    └── prod.ts (optimized, reserved concurrency)
```

---

## Environment Configuration Template

### dev.ts
```typescript
export const devConfig = {
  environment: 'dev',
  lambda: {
    memorySize: 512,
    timeout: 10,
    reservedConcurrentExecutions: 1,
  },
  dynamodb: {
    billingMode: 'PAY_PER_REQUEST',
    pointInTimeRecovery: true,
  },
  api: {
    throttle: { rateLimit: 100, burstLimit: 200 },
  },
};
```

### staging.ts
```typescript
export const stagingConfig = {
  environment: 'staging',
  lambda: {
    memorySize: 512,
    timeout: 10,
    reservedConcurrentExecutions: 5,
  },
  dynamodb: {
    billingMode: 'PAY_PER_REQUEST',
    pointInTimeRecovery: true,
  },
  api: {
    throttle: { rateLimit: 1000, burstLimit: 2000 },
  },
};
```

### prod.ts
```typescript
export const prodConfig = {
  environment: 'prod',
  lambda: {
    memorySize: 512,
    timeout: 10,
    reservedConcurrentExecutions: 10,
  },
  dynamodb: {
    billingMode: 'PAY_PER_REQUEST',
    pointInTimeRecovery: true,
  },
  api: {
    throttle: { rateLimit: 5000, burstLimit: 10000 },
  },
};
```

---

## Lambda Deployment Strategy

### Version & Alias Pattern
```
Function: wereda-connect-api-prod
├── Version 1 (old)
├── Version 2 (previous)
├── Version 3 (live) ← alias points here
├── Version 4 (canary) ← 10% traffic
└── Version 5 (latest) ← new deployment
```

### Deployment Flow
1. Build AOT binary: `dotnet publish -c Release -r linux-arm64`
2. Create new Lambda version
3. Point canary alias to new version (10% traffic)
4. Monitor for 5 minutes
5. Shift to 50% traffic
6. Monitor for 5 minutes
7. Shift to 100% traffic
8. Keep previous version for quick rollback

---

## Mobile Release Process

### Version Format
```
version: X.Y.Z+B
where:
  X = major (breaking changes)
  Y = minor (features)
  Z = patch (fixes)
  B = build number (auto-increment)
```

### Release Checklist
- [ ] Update version in `pubspec.yaml`
- [ ] Update `CHANGELOG.md`
- [ ] Build release APK: `flutter build apk --release --split-per-abi`
- [ ] Build App Bundle: `flutter build appbundle --release`
- [ ] Sign with release key
- [ ] Upload to Google Play Console
- [ ] Set staged rollout (10% → 50% → 100%)
- [ ] Monitor crash reports
- [ ] Publish to production

### Google Play Staged Rollout
```
Day 1: 10% of users
  ↓ (monitor for 24h)
Day 2: 50% of users
  ↓ (monitor for 24h)
Day 3: 100% of users
```

---

## Monitoring & Alarms

### CloudWatch Metrics to Track
```
Lambda:
  - Invocations (count)
  - Errors (count)
  - Duration (p50, p99)
  - Throttles (count)
  - ConcurrentExecutions (max)

API Gateway:
  - Count (requests)
  - 4XXError (count)
  - 5XXError (count)
  - Latency (p50, p99)

DynamoDB:
  - ConsumedReadCapacityUnits
  - ConsumedWriteCapacityUnits
  - UserErrors (throttles)
  - SystemErrors
```

### Alarm Thresholds
```
Lambda Error Rate > 1% → CRITICAL
Lambda Duration p99 > 5s → WARNING
API Gateway 5xx > 0.5% → CRITICAL
DynamoDB Throttles > 0 → WARNING
```

---

## Secrets Management

### AWS Secrets Manager Structure
```
/wereda-connect/dev/
  ├── database-url
  ├── api-key
  └── jwt-secret

/wereda-connect/staging/
  ├── database-url
  ├── api-key
  └── jwt-secret

/wereda-connect/prod/
  ├── database-url
  ├── api-key
  └── jwt-secret
```

### Rotation Policy
- API keys: every 90 days
- JWT secrets: every 180 days
- Database passwords: every 30 days

---

## Disaster Recovery

### RTO/RPO Targets
```
Database:
  RTO: 1 hour (restore from backup)
  RPO: 5 minutes (point-in-time recovery)

Lambda:
  RTO: 5 minutes (rollback to previous version)
  RPO: 0 (code stored in S3)

S3:
  RTO: 1 hour (restore from versioning)
  RPO: 0 (versioning enabled)
```

### Backup Strategy
```
DynamoDB:
  - Point-in-time recovery: enabled
  - Backup frequency: continuous
  - Retention: 35 days

S3:
  - Versioning: enabled
  - Lifecycle: delete old versions after 90 days

Lambda:
  - Code stored in S3
  - Versions retained: last 10
```

---

## Cost Optimization

### Estimated Monthly Costs
```
Dev:
  Lambda: $0.20 (1M invocations)
  DynamoDB: $1.25 (on-demand)
  S3: $0.50 (storage)
  Total: ~$5/month

Staging:
  Lambda: $2.00 (10M invocations)
  DynamoDB: $5.00 (on-demand)
  S3: $1.00 (storage)
  Total: ~$10/month

Prod:
  Lambda: $5.00 (25M invocations)
  DynamoDB: $10.00 (on-demand)
  S3: $2.00 (storage)
  Total: ~$20/month
```

### Cost Controls
- Reserved concurrency: prevents runaway costs
- DynamoDB on-demand: pay per request
- S3 lifecycle: auto-delete old files
- CloudWatch alarms: alert on cost spikes

---

## Security Checklist

- [ ] IAM roles follow least privilege
- [ ] Secrets stored in AWS Secrets Manager
- [ ] API Gateway has throttling enabled
- [ ] Lambda has reserved concurrency
- [ ] S3 bucket blocks public access
- [ ] DynamoDB encryption enabled
- [ ] CloudTrail logging enabled
- [ ] VPC endpoints for private access (future)
- [ ] WAF rules for API Gateway (future)

---

## Performance Targets

```
Cold Start: < 2 seconds (ARM64 + AOT)
Warm Start: < 100ms
API Latency p95: < 500ms
Database Query: < 100ms
```

---

## Troubleshooting

### Lambda Deployment Fails
```bash
# Check function code
aws lambda get-function --function-name wereda-connect-api-prod

# Check recent errors
aws logs tail /aws/lambda/wereda-connect-api-prod --follow

# Rollback to previous version
aws lambda update-alias \
  --function-name wereda-connect-api-prod \
  --name live \
  --function-version <previous-version>
```

### API Gateway Issues
```bash
# Check API logs
aws logs tail /aws/apigateway/wereda-connect-api-prod

# Check throttling
aws cloudwatch get-metric-statistics \
  --namespace AWS/ApiGateway \
  --metric-name Throttle
```

### DynamoDB Throttling
```bash
# Check consumed capacity
aws cloudwatch get-metric-statistics \
  --namespace AWS/DynamoDB \
  --metric-name ConsumedWriteCapacityUnits

# Increase on-demand capacity (automatic)
# Or switch to provisioned mode with auto-scaling
```

