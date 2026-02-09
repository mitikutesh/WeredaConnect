# Deployment Architecture

## Purpose
This document describes the AWS infrastructure, deployment pipelines, and environment management for WeredaConnect.

## Audience
- DevOps engineers
- Infrastructure developers
- Deployment managers

## Prerequisites
- AWS account with appropriate permissions
- AWS CDK CLI installed
- Read [System Architecture](./system-architecture.md)

---

## AWS Infrastructure Overview

```
┌─────────────────────────────────────────────────────────────────┐
│                         CloudFront (CDN)                         │
│  - Global edge locations                                         │
│  - HTTPS termination                                             │
│  - Static asset caching                                          │
└─────────────────────────────────────────────────────────────────┘
                              ↓
┌─────────────────────────────────────────────────────────────────┐
│                       API Gateway (REST)                         │
│  - /reports, /users, /sync endpoints                             │
│  - Request validation                                            │
│  - Throttling (1000 req/sec)                                     │
│  - CORS configuration                                            │
└─────────────────────────────────────────────────────────────────┘
                              ↓
┌─────────────────────────────────────────────────────────────────┐
│                    Lambda Functions (ARM64)                      │
│  ┌──────────────┐  ┌──────────────┐  ┌──────────────┐          │
│  │ CreateReport │  │ GetReports   │  │ SyncData     │          │
│  │ Function     │  │ Function     │  │ Function     │          │
│  └──────────────┘  └──────────────┘  └──────────────┘          │
│  - .NET 10 Native AOT                                            │
│  - 512MB memory                                                  │
│  - 10s timeout                                                   │
│  - Reserved concurrency: 5                                       │
└─────────────────────────────────────────────────────────────────┘
                              ↓
┌─────────────────────────────────────────────────────────────────┐
│                         Data Layer                               │
│  ┌──────────────────┐  ┌──────────────────┐  ┌──────────────┐  │
│  │ DynamoDB         │  │ S3               │  │ Cognito      │  │
│  │ - Reports table  │  │ - Media bucket   │  │ - User pool  │  │
│  │ - Users table    │  │ - Static assets  │  │ - Identity   │  │
│  │ - On-demand      │  │ - Lifecycle      │  │   pool       │  │
│  └──────────────────┘  └──────────────────┘  └──────────────┘  │
└─────────────────────────────────────────────────────────────────┘
                              ↓
┌─────────────────────────────────────────────────────────────────┐
│                    Monitoring & Logging                          │
│  ┌──────────────────┐  ┌──────────────────┐                     │
│  │ CloudWatch Logs  │  │ CloudWatch       │                     │
│  │ - Lambda logs    │  │ Alarms           │                     │
│  │ - API Gateway    │  │ - Error rate     │                     │
│  │   logs           │  │ - Latency        │                     │
│  └──────────────────┘  └──────────────────┘                     │
└─────────────────────────────────────────────────────────────────┘
```

---

## Environments

### Development (dev)
- **Purpose**: Active development and testing
- **Deployment**: Auto-deploy from `main` branch
- **Data**: Test data, can be reset
- **Cost**: ~$5/month
- **URL**: `https://dev-api.weredaconnect.com`

### Staging (staging)
- **Purpose**: Pre-production testing
- **Deployment**: Manual deploy from `main` branch
- **Data**: Production-like data
- **Cost**: ~$10/month
- **URL**: `https://staging-api.weredaconnect.com`

### Production (prod)
- **Purpose**: Live user traffic
- **Deployment**: Manual deploy with approval
- **Data**: Real user data
- **Cost**: Variable (starts ~$10/month)
- **URL**: `https://api.weredaconnect.com`

---

## Deployment Pipeline

### CI/CD Flow (GitHub Actions)

```
┌─────────────────────────────────────────────────────────────────┐
│                      Developer Workflow                          │
└─────────────────────────────────────────────────────────────────┘
                              ↓
                    git push origin main
                              ↓
┌─────────────────────────────────────────────────────────────────┐
│                      GitHub Actions                              │
│  1. Checkout code                                                │
│  2. Run tests (mobile, server, infra)                            │
│  3. Build .NET with AOT                                          │
│  4. Run security scans                                           │
│  5. Generate coverage reports                                    │
└─────────────────────────────────────────────────────────────────┘
                              ↓ (if tests pass)
┌─────────────────────────────────────────────────────────────────┐
│                   Auto-Deploy to Dev                             │
│  1. CDK synth                                                    │
│  2. CDK deploy --require-approval never                          │
│  3. Run smoke tests                                              │
│  4. Notify team (Slack/Discord)                                  │
└─────────────────────────────────────────────────────────────────┘
                              ↓ (manual trigger)
┌─────────────────────────────────────────────────────────────────┐
│                  Deploy to Staging                               │
│  1. Manual approval required                                     │
│  2. CDK deploy staging                                           │
│  3. Run integration tests                                        │
│  4. Run E2E tests                                                │
└─────────────────────────────────────────────────────────────────┘
                              ↓ (manual trigger)
┌─────────────────────────────────────────────────────────────────┐
│                  Deploy to Production                            │
│  1. Manual approval required (2 approvers)                       │
│  2. Create backup of DynamoDB                                    │
│  3. CDK deploy prod                                              │
│  4. Run smoke tests                                              │
│  5. Monitor for 30 minutes                                       │
│  6. Rollback if errors detected                                  │
└─────────────────────────────────────────────────────────────────┘
```

---

## Infrastructure as Code (CDK)

### Stack Organization

```typescript
// bin/app.ts
const app = new cdk.App();

// Database stack (foundational)
const dbStack = new DatabaseStack(app, 'WeredaConnect-DB-Dev', {
  env: 'dev',
});

// API stack (depends on database)
const apiStack = new ApiStack(app, 'WeredaConnect-API-Dev', {
  env: 'dev',
  reportsTable: dbStack.reportsTable,
  usersTable: dbStack.usersTable,
});

// CDN stack (depends on API)
const cdnStack = new CdnStack(app, 'WeredaConnect-CDN-Dev', {
  env: 'dev',
  apiEndpoint: apiStack.apiEndpoint,
});
```

### Cost Optimization Strategies

1. **ARM64 Lambda**: 20% cheaper than x86_64
2. **On-Demand DynamoDB**: Pay per request (no wasted capacity)
3. **Reserved Concurrency**: Prevent runaway Lambda costs
4. **S3 Lifecycle Policies**: Auto-delete old files
5. **CloudFront Caching**: Reduce API Gateway costs

---

## Deployment Commands

### Deploy to Development
```bash
cd infra
npm install
npm run cdk deploy WeredaConnect-*-Dev
```

### Deploy to Staging
```bash
npm run cdk deploy WeredaConnect-*-Staging --require-approval broadening
```

### Deploy to Production
```bash
# Requires manual approval
npm run cdk deploy WeredaConnect-*-Prod --require-approval broadening
```

### Rollback
```bash
# Revert to previous version
aws lambda update-function-code \
  --function-name WeredaConnect-CreateReport-Prod \
  --s3-bucket <previous-version-bucket> \
  --s3-key <previous-version-key>
```

---

## Monitoring & Alerts

### CloudWatch Alarms

1. **Lambda Errors**: Alert if error rate > 1%
2. **Lambda Duration**: Alert if p99 > 5 seconds
3. **API Gateway 5xx**: Alert if count > 10 in 5 minutes
4. **DynamoDB Throttles**: Alert if throttled requests > 0

### Logging

- **Lambda Logs**: CloudWatch Logs (7-day retention)
- **API Gateway Logs**: CloudWatch Logs (30-day retention)
- **Application Logs**: Structured JSON logging

---

## Security

### Network Security
- **HTTPS Only**: TLS 1.3
- **API Gateway**: WAF rules (rate limiting, IP filtering)
- **VPC**: Lambda functions in VPC (if needed)

### IAM Policies
- **Least Privilege**: Minimal permissions
- **Service Roles**: Separate roles per Lambda
- **No Wildcards**: Explicit resource ARNs

### Secrets Management
- **AWS Secrets Manager**: API keys, database credentials
- **Environment Variables**: Non-sensitive config

---

## Disaster Recovery

### Backup Strategy
- **DynamoDB**: Point-in-time recovery enabled
- **S3**: Versioning enabled
- **Lambda**: Code stored in S3

### Recovery Procedures
1. **Database Restore**: Restore from point-in-time backup
2. **Lambda Rollback**: Deploy previous version
3. **Full Stack Rollback**: Redeploy previous CDK version

---

## Related Documentation

- **[System Architecture](./system-architecture.md)** - Architecture overview
- **[Deployment Guide](../developer/deployment.md)** - Deployment procedures
- **[Developer Workflow](../developer/dev-workflow.md)** - Development process

---

**Last Updated**: 2026-02-09  
**Version**: 1.0.0  
**Owner**: Infrastructure Team

