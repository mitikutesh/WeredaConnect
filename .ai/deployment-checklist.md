# Deployment Checklist

**NEVER deploy without explicit human developer approval.**

---

## Code Quality
- [ ] All tests pass (unit, integration, E2E)
- [ ] No compiler warnings, linting passes
- [ ] No hardcoded secrets or API keys

## Mobile (Flutter)
- [ ] APK builds: `flutter build apk --release`
- [ ] APK size <50MB
- [ ] Tested on 2GB RAM Android 8 emulator
- [ ] Tested offline mode (airplane mode) + sync after reconnection
- [ ] All three languages work (Amharic, Afan Oromo, English)
- [ ] Ethiopic script renders correctly
- [ ] No memory leaks (profiled with Flutter DevTools)

## Server (.NET)
- [ ] AOT compilation succeeds: `dotnet publish -c Release /p:PublishAot=true`
- [ ] Lambda unit tests >75% coverage
- [ ] Idempotency keys on all write operations
- [ ] Conflict resolution tested
- [ ] Cold start <2 seconds
- [ ] No banned libraries (check `server/AOT_COMPLIANCE.md`)
- [ ] API responses <100KB compressed

## Infrastructure (AWS CDK)
- [ ] `cdk synth` succeeds
- [ ] `cdk diff` reviewed
- [ ] No wildcard IAM permissions (`Resource: "*"`)
- [ ] Lambda reserved concurrency set
- [ ] CloudWatch alarms configured
- [ ] DynamoDB on-demand billing enabled
- [ ] S3 encryption enabled
- [ ] API Gateway throttling configured

## Security
- [ ] Threat model reviewed (`qa/threat_models/`)
- [ ] No secrets in code or env vars
- [ ] HTTPS enforced, CORS configured (not `*`)
- [ ] Input validation on all API endpoints
- [ ] Authentication required for sensitive endpoints
- [ ] Rate limiting enabled

## Data Privacy
- [ ] User data encrypted at rest and in transit (TLS 1.2+)
- [ ] Minimal data collection (privacy by design)

## Offline-First Compliance
- [ ] All features work offline
- [ ] Sync queue handles retries with intermittent connectivity
- [ ] Conflict resolution works with concurrent edits
- [ ] Graceful degradation with clear error messages

## Low-End Device Compliance
- [ ] Tested on 2GB RAM Android 8
- [ ] Memory usage <100MB, 60fps, no UI jank
- [ ] Data usage <1MB per typical session
- [ ] Images compressed (WebP, max 800px)

## Deployment Steps
1. Deploy to staging: `cdk deploy --context env=staging`
2. Run smoke tests + E2E on staging
3. Monitor CloudWatch for errors
4. **Get human developer approval**
5. Deploy to production: `cdk deploy --context env=prod`
6. Monitor first hour: errors=0, cold start <2s, p99 latency <500ms, no DynamoDB throttling

## Rollback
- Immediately rollback: `cdk deploy --context env=prod --rollback`
- Document failure in `TASK.md`, fix locally, re-run full checklist

## Cost Validation
- [ ] Lambda, DynamoDB, S3 within free tier limits
- [ ] Total monthly cost <$10 (low-traffic)
