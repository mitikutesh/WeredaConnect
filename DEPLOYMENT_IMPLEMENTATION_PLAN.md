# Deployment Implementation Plan

**Target**: Enable staging and production deployments with automation

---

## Phase 1: Foundation (Week 1-2)

### 1.1 Fix CDK Stack Definitions
**Files**: `infra/bin/wereda-connect.ts`, `infra/lib/stacks/wereda-connect-stack.ts`

- [ ] Uncomment staging and prod stacks
- [ ] Create environment-specific config files:
  - `infra/lib/config/dev.ts`
  - `infra/lib/config/staging.ts`
  - `infra/lib/config/prod.ts`
- [ ] Implement config interface for environment-specific values
- [ ] Update stack to use config (memory, timeout, reserved concurrency)
- [ ] Test synthesis for all environments

### 1.2 Uncomment Lambda Functions
**Files**: `infra/lib/stacks/wereda-connect-stack.ts`

- [ ] Uncomment HealthCheckFunction definition
- [ ] Update code path to use published AOT binary
- [ ] Add environment variables for each environment
- [ ] Grant IAM permissions to Lambda
- [ ] Connect Lambda to API Gateway

### 1.3 Test Infrastructure
**Files**: `infra/test/`

- [ ] Add unit tests for staging/prod stacks
- [ ] Add snapshot tests for all environments
- [ ] Verify CDK synthesis succeeds for all envs
- [ ] Test cross-stack references

---

## Phase 2: Server Deployment (Week 2-3)

### 2.1 Create Deployment Workflow
**File**: `.github/workflows/deploy-server.yml` (NEW)

```yaml
name: Deploy Server
on:
  workflow_dispatch:
    inputs:
      environment:
        type: choice
        options: [staging, prod]
      version:
        description: 'Version tag (e.g., v1.2.0)'
        required: true

jobs:
  deploy:
    runs-on: ubuntu-latest
    environment: ${{ github.event.inputs.environment }}
    steps:
      - uses: actions/checkout@v4
      - name: Setup .NET 10
        uses: actions/setup-dotnet@v4
      - name: Build AOT binary
        run: dotnet publish -c Release -r linux-arm64
      - name: Deploy to Lambda
        run: |
          aws lambda update-function-code \
            --function-name wereda-connect-api-${{ env.ENVIRONMENT }} \
            --zip-file fileb://server/bin/Release/net10.0/linux-arm64/publish/bootstrap
      - name: Run smoke tests
        run: npm run smoke-test:${{ env.ENVIRONMENT }}
```

### 2.2 Lambda Versioning & Aliases
- [ ] Create Lambda versions on each deploy
- [ ] Use aliases (live, canary, previous)
- [ ] Implement traffic shifting (10% → 50% → 100%)

### 2.3 Deployment Validation
- [ ] Health check endpoint tests
- [ ] Database connectivity tests
- [ ] API response validation

---

## Phase 3: Mobile Deployment (Week 3-4)

### 3.1 Release Signing Setup
**Files**: `mobile/android/key.properties` (NEW, gitignored)

- [ ] Generate release keystore
- [ ] Store in GitHub Secrets
- [ ] Update `build.gradle` to use release signing config
- [ ] Test release APK signing

### 3.2 Google Play Integration
**File**: `.github/workflows/release-mobile.yml` (NEW)

- [ ] Setup Google Play service account
- [ ] Create release workflow (manual trigger)
- [ ] Build release APK/AAB
- [ ] Upload to Google Play Console
- [ ] Implement staged rollout (10% → 50% → 100%)

### 3.3 Version Management
- [ ] Automate version bumping (major.minor.patch+build)
- [ ] Generate release notes from commits
- [ ] Tag releases in git

---

## Phase 4: Infrastructure Deployment (Week 4-5)

### 4.1 CDK Deployment Workflow
**File**: `.github/workflows/deploy-infra.yml` (NEW)

- [ ] Manual trigger with environment selection
- [ ] Require approval for staging/prod
- [ ] Synthesize CloudFormation templates
- [ ] Show diff before deployment
- [ ] Deploy with approval gates

### 4.2 Post-Deployment Validation
- [ ] Smoke tests (API health, database, S3)
- [ ] Performance baseline checks
- [ ] Cost validation
- [ ] Security checks

### 4.3 Monitoring & Alarms
**File**: `infra/lib/stacks/monitoring-stack.ts` (NEW)

- [ ] CloudWatch dashboards
- [ ] Lambda error rate alarms
- [ ] API Gateway 5xx alarms
- [ ] DynamoDB throttle alarms
- [ ] SNS notifications

---

## Phase 5: Rollback & Recovery (Week 5-6)

### 5.1 Automated Rollback
**File**: `.github/workflows/rollback.yml` (NEW)

- [ ] Detect deployment failures
- [ ] Trigger automatic rollback
- [ ] Restore previous Lambda version
- [ ] Restore database from backup
- [ ] Notify team

### 5.2 Disaster Recovery
- [ ] Document RTO/RPO targets
- [ ] Test backup restoration
- [ ] Create runbooks for each failure scenario
- [ ] Implement cross-region failover (future)

### 5.3 Secrets Management
- [ ] Migrate to AWS Secrets Manager
- [ ] Rotate credentials regularly
- [ ] Audit access logs

---

## Phase 6: Documentation & Testing (Week 6-7)

### 6.1 Deployment Runbooks
- [ ] Staging deployment procedure
- [ ] Production deployment procedure
- [ ] Rollback procedures
- [ ] Incident response guide

### 6.2 Testing
- [ ] End-to-end deployment tests
- [ ] Rollback scenario tests
- [ ] Disaster recovery drills
- [ ] Load testing

### 6.3 Team Training
- [ ] Deployment process training
- [ ] Incident response training
- [ ] Monitoring dashboard walkthrough

---

## Success Criteria

- ✅ All three environments deployable via GitHub Actions
- ✅ Automated mobile app releases to Google Play
- ✅ Automated server deployments with health checks
- ✅ Rollback procedures tested and documented
- ✅ Monitoring and alerting configured
- ✅ Team trained on deployment procedures

---

## Risk Mitigation

| Risk | Mitigation |
|------|-----------|
| Deployment fails | Automated rollback, manual override |
| Data loss | Point-in-time recovery, backups |
| Security breach | Secrets Manager, IAM policies, audit logs |
| Cost overrun | Reserved concurrency, alarms, budgets |
| Performance degradation | Canary deployments, smoke tests |

---

## Timeline

- **Week 1-2**: Foundation (CDK stacks, configs)
- **Week 2-3**: Server deployment automation
- **Week 3-4**: Mobile release automation
- **Week 4-5**: Infrastructure deployment automation
- **Week 5-6**: Rollback and recovery
- **Week 6-7**: Documentation and testing

**Total**: 7 weeks for full implementation

---

## Dependencies

- AWS account with appropriate permissions
- GitHub Actions secrets configured
- Google Play service account (for mobile)
- Team availability for testing/training

