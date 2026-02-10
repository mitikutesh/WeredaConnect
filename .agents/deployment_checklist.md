# Deployment Checklist

## Purpose
Pre-deployment validation checklist for WeredaConnect. Ensures production readiness, security, and Ethiopian context compliance.

---

## ⚠️ Critical Rule
**NEVER deploy without explicit human developer approval.**

---

## Pre-Deployment Validation

### 1. Code Quality
- [ ] All tests pass (unit, integration, E2E)
- [ ] Code review completed (if applicable)
- [ ] No compiler warnings
- [ ] Linting passes (`flutter analyze`, `dotnet format`, `eslint`)
- [ ] No hardcoded secrets or API keys

### 2. Mobile (Flutter)
- [ ] APK builds successfully: `flutter build apk --release`
- [ ] APK size <50MB (check `build/app/outputs/flutter-apk/`)
- [ ] Tested on 2GB RAM Android 8 emulator
- [ ] Tested offline mode (airplane mode)
- [ ] Tested sync after reconnection
- [ ] All three languages work (Amharic, Afan Oromo, English)
- [ ] Ethiopic script renders correctly (Noto Sans Ethiopic font)
- [ ] No memory leaks (profiled with Flutter DevTools)
- [ ] Battery usage <5% per hour (tested with Battery Historian)

### 3. Server (.NET)
- [ ] AOT compilation succeeds: `dotnet publish -c Release /p:PublishAot=true`
- [ ] All Lambda functions have unit tests (>75% coverage)
- [ ] Idempotency keys implemented for all write operations
- [ ] Conflict resolution tested (concurrent edits)
- [ ] Cold start time <2 seconds (measured with X-Ray)
- [ ] No banned libraries used (check `server/AOT_COMPLIANCE.md`)
- [ ] API responses <100KB (compressed)
- [ ] Error handling returns user-friendly messages

### 4. Infrastructure (AWS CDK)
- [ ] CDK synth succeeds: `cdk synth`
- [ ] CDK diff reviewed: `cdk diff --context env=prod`
- [ ] No wildcard IAM permissions (`Resource: "*"`)
- [ ] Lambda reserved concurrency set (prevent runaway costs)
- [ ] CloudWatch alarms configured (errors, latency, cost)
- [ ] DynamoDB on-demand billing enabled (low traffic)
- [ ] S3 buckets have encryption enabled
- [ ] API Gateway throttling configured

---

## Security Validation

### 5. Security Checklist
- [ ] Threat model reviewed (see `qa/threat_models/`)
- [ ] No secrets in code or environment variables
- [ ] HTTPS enforced (API Gateway, CloudFront)
- [ ] Input validation on all API endpoints
- [ ] SQL injection prevention (use parameterized queries)
- [ ] XSS prevention (sanitize user input)
- [ ] CORS configured correctly (not `*`)
- [ ] Authentication required for sensitive endpoints
- [ ] Rate limiting enabled (prevent abuse)

### 6. Data Privacy
- [ ] Minimal data collection (privacy by design)
- [ ] User data encrypted at rest (DynamoDB, S3)
- [ ] User data encrypted in transit (TLS 1.2+)
- [ ] Privacy policy updated (if new data collected)
- [ ] GDPR/data deletion flow tested (if applicable)

---

## Ethiopian Context Validation

### 7. Offline-First Compliance
- [ ] All features work offline (tested with network disconnected)
- [ ] Sync queue handles retries (tested with intermittent connectivity)
- [ ] Conflict resolution works (tested with concurrent edits)
- [ ] Graceful degradation (clear error messages when offline)

### 8. Low-End Device Compliance
- [ ] Tested on 2GB RAM Android 8 emulator
- [ ] APK size <50MB
- [ ] Memory usage <100MB
- [ ] No UI jank (60fps on low-end device)
- [ ] Background sync doesn't drain battery

### 9. Data Efficiency
- [ ] Data usage <1MB per typical user session
- [ ] Images compressed (WebP, max 800px width)
- [ ] API responses compressed (gzip/brotli)
- [ ] Data saver mode available (if applicable)

### 10. Localization
- [ ] All UI strings localized (Amharic, Afan Oromo, English)
- [ ] Ethiopic script renders correctly
- [ ] Culturally appropriate terminology (validated with locals)
- [ ] Voice-to-text works in Amharic/Oromo (if applicable)

---

## Deployment Steps

### 11. Staging Deployment
- [ ] Deploy to staging: `cdk deploy --context env=staging`
- [ ] Run smoke tests on staging
- [ ] Test critical user journeys (E2E tests)
- [ ] Monitor CloudWatch logs for errors
- [ ] Verify data sync works (staging → production DB)

### 12. Production Deployment
- [ ] **Get human developer approval** (required)
- [ ] Backup production database (if applicable)
- [ ] Deploy to production: `cdk deploy --context env=prod`
- [ ] Run smoke tests on production
- [ ] Monitor CloudWatch for errors (first 10 minutes)
- [ ] Test critical flows manually (login, sync, payment)

---

## Post-Deployment Monitoring

### 13. Immediate Monitoring (First Hour)
- [ ] Check CloudWatch errors (should be zero)
- [ ] Check Lambda cold start times (<2s)
- [ ] Check API latency (p99 <500ms)
- [ ] Check DynamoDB throttling (should be zero)
- [ ] Check S3 upload success rate (>99%)

### 14. Extended Monitoring (First 24 Hours)
- [ ] Monitor CloudWatch alarms (no critical alerts)
- [ ] Check AWS costs (no unexpected spikes)
- [ ] Monitor user feedback (if beta users available)
- [ ] Check crash reports (Firebase Crashlytics, if enabled)

---

## Rollback Plan

### 15. If Deployment Fails
- [ ] Immediately rollback: `cdk deploy --context env=prod --rollback`
- [ ] Check CloudWatch logs for root cause
- [ ] Document the failure in `TASK.md`
- [ ] Fix the issue locally
- [ ] Re-run full checklist before redeploying

---

## Cost Validation

### 16. AWS Cost Check
- [ ] Lambda invocations within free tier (1M/month)
- [ ] DynamoDB reads/writes within free tier (25GB storage)
- [ ] S3 storage within budget (<$5/month)
- [ ] CloudFront data transfer within budget (<$1/month)
- [ ] Total monthly cost <$10 (low-traffic scenario)

---

## Documentation

### 17. Update Documentation
- [ ] Update `STATE.md` with deployment timestamp
- [ ] Update `TASK.md` (mark deployment task complete)
- [ ] Document any manual steps taken
- [ ] Update version number in `README.md`

---

**Last Updated**: 2026-02-10  
**Version**: 1.0.0

