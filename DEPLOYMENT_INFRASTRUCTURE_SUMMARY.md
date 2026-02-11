# WeredaConnect Deployment Infrastructure Summary

**Date**: 2026-02-10  
**Status**: Exploration Complete

---

## Executive Summary

WeredaConnect has a **partially implemented** deployment infrastructure with:
- ✅ **AWS CDK** infrastructure as code (TypeScript)
- ✅ **GitHub Actions** CI/CD pipelines for testing
- ✅ **Three environments** (dev, staging, prod) defined
- ⚠️ **Incomplete** staging/production deployment automation
- ⚠️ **Manual** mobile app deployment process
- ⚠️ **Commented-out** Lambda function definitions

---

## 1. Current Deployment Setup

### AWS CDK Infrastructure
- **Framework**: AWS CDK 2.170.0 (TypeScript)
- **Location**: `infra/` directory
- **Entry Point**: `infra/bin/wereda-connect.ts`
- **Main Stack**: `WeredaConnectStack` (single stack, not modular)

**Current Resources**:
- DynamoDB tables (users, crops, trips) - on-demand billing
- S3 bucket for uploads - with lifecycle policies
- API Gateway HTTP API (not yet integrated with Lambda)
- **Missing**: Lambda functions (commented out, awaiting AOT publishing)

### Environment Configuration
| Environment | Purpose | Deployment | Approval | Cost |
|-------------|---------|-----------|----------|------|
| **Dev** | Active development | Auto (main branch) | None | ~$5/mo |
| **Staging** | Pre-production | Manual | 1 approver | ~$10/mo |
| **Prod** | Live users | Manual | 2 approvers | ~$10/mo+ |

**Issue**: Only dev stack defined in `wereda-connect.ts`. Staging/prod stacks commented out.

---

## 2. CI/CD Workflows (GitHub Actions)

### Existing Workflows
1. **pr-validation.yml** - PR checks (mobile, server, infra, E2E)
2. **mobile-tests.yml** - Flutter unit/integration/smoke tests
3. **server-tests.yml** - .NET unit/integration/AOT validation
4. **infra-tests.yml** - CDK unit/snapshot/integration tests

### Missing Workflows
- ❌ Automated staging deployment
- ❌ Automated production deployment
- ❌ Mobile app release (Google Play)
- ❌ Server Lambda deployment automation
- ❌ Smoke tests post-deployment

---

## 3. Server Deployment (.NET 10 AOT)

### Current State
- **Framework**: .NET 10 with Native AOT
- **Runtime**: `linux-arm64` (ARM64 for cost optimization)
- **Lambda Config**: `aws-lambda-tools-defaults.json`
  - Memory: 512MB
  - Timeout: 10s
  - Handler: `bootstrap` (AOT binary)
  - Architecture: arm64

### Build Process
```bash
dotnet publish -c Release -r linux-arm64 --self-contained
```

### Deployment
- **Manual**: Via CDK (commented Lambda code)
- **Automated**: Not yet implemented
- **Testing**: LocalStack integration tests in CI

### Issues
1. Lambda functions commented out in CDK stack
2. No automated deployment pipeline
3. No Lambda versioning/aliasing strategy
4. No canary deployment support

---

## 4. Mobile App Deployment

### Current State
- **Framework**: Flutter 3.38.9
- **Target**: Android (iOS coming soon)
- **Build**: APK + App Bundle support
- **Signing**: Currently uses debug key (security issue)

### Build Process
```bash
flutter build apk --release --split-per-abi
flutter build appbundle --release
```

### Deployment
- **Manual**: Upload to Google Play Console
- **Automated**: Not implemented
- **Testing**: Debug APK built in CI

### Issues
1. No automated Google Play release
2. No staged rollout automation
3. Release signing uses debug key (security risk)
4. No version management automation
5. No APK distribution mechanism

---

## 5. Infrastructure Deployment

### CDK Deployment Commands
```bash
# Dev (auto-deploy on main)
cdk deploy WeredaConnect-*-Dev

# Staging (manual, requires approval)
cdk deploy WeredaConnect-*-Staging --require-approval broadening

# Prod (manual, requires approval)
cdk deploy WeredaConnect-*-Prod --require-approval broadening
```

### Issues
1. Only dev stack instantiated
2. No environment-specific config files (dev.ts, staging.ts, prod.ts referenced but not found)
3. No cross-stack dependencies
4. No monitoring/alarms defined
5. No CloudFront CDN configured

---

## 6. Rollback & Disaster Recovery

### Current Capabilities
✅ **DynamoDB**: Point-in-time recovery enabled  
✅ **S3**: Versioning enabled  
✅ **Lambda**: Code stored in S3  
✅ **Git**: Version tags for releases  

### Rollback Procedures (Manual)
1. **Lambda**: Update alias to previous version
2. **Full Stack**: Revert git tag, redeploy CDK
3. **Database**: Restore from point-in-time backup

### Issues
1. No automated rollback triggers
2. No canary deployment for gradual rollout
3. No health check automation
4. No automatic rollback on error detection

---

## 7. What's Needed for Staging/Production

### High Priority
1. **Staging/Prod Stack Definitions** - Uncomment and configure
2. **Lambda Deployment Automation** - GitHub Actions workflow
3. **Mobile Release Automation** - Google Play integration
4. **Environment-Specific Configs** - dev.ts, staging.ts, prod.ts
5. **Smoke Tests Post-Deployment** - Automated verification

### Medium Priority
6. **Monitoring & Alarms** - CloudWatch dashboards
7. **Canary Deployments** - Gradual rollout strategy
8. **Release Signing** - Secure key management
9. **Version Management** - Automated versioning
10. **Secrets Management** - AWS Secrets Manager integration

### Documentation
- Deployment runbooks for each environment
- Incident response procedures
- Cost monitoring guidelines
- Performance baselines

---

## 8. Key Files & Locations

| Component | Location | Status |
|-----------|----------|--------|
| CDK App | `infra/bin/wereda-connect.ts` | ⚠️ Incomplete |
| Main Stack | `infra/lib/stacks/wereda-connect-stack.ts` | ✅ Partial |
| Deployment Guide | `docs/developer/deployment.md` | ✅ Complete |
| Architecture | `docs/architecture/deployment-architecture.md` | ✅ Complete |
| CI/CD Workflows | `.github/workflows/` | ⚠️ Incomplete |
| Server Config | `server/aws-lambda-tools-defaults.json` | ✅ Complete |
| Mobile Config | `mobile/pubspec.yaml` | ✅ Complete |

---

## 9. Recommendations

### Immediate Actions
1. Uncomment staging/prod stacks in `wereda-connect.ts`
2. Create environment-specific config files
3. Implement Lambda deployment workflow
4. Add mobile release automation
5. Create deployment runbooks

### Long-term
1. Implement canary deployments
2. Add comprehensive monitoring
3. Automate rollback triggers
4. Implement blue-green deployments
5. Add cost optimization monitoring

---

## 10. Next Steps

See `DEPLOYMENT_IMPLEMENTATION_PLAN.md` for detailed implementation roadmap.

