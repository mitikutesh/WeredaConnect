# Feature Implementation Workflow

## Purpose
Step-by-step checklist for implementing a new feature in WeredaConnect. Ensures offline-first design, Ethiopian context validation, and proper testing.

---

## Pre-Implementation

### 1. Read the Spec
- [ ] Read the feature spec in `specs/features/[feature-name].md`
- [ ] Understand the user problem being solved
- [ ] Identify offline behavior requirements
- [ ] Note localization requirements (Amharic, Afan Oromo, English)

### 2. Claim the Task
- [ ] Add `@Scope` tag to task in `TASK.md` (see `.agents/task-protocol.md`)
- [ ] Commit the claim before starting work
- [ ] Create branch: `<scope>/<feature-description>` (see `.agents/branch-protocol.md`)

### 3. Validate Context
- [ ] Check `docs/local_reality/README.md` for Ethiopian constraints
- [ ] Verify feature works with 3G speeds and intermittent connectivity
- [ ] Confirm feature is appropriate for low-literacy users (if applicable)
- [ ] Review agricultural/market terminology if relevant

---

## Implementation

### 4. Design Offline-First
- [ ] **Local DB first**: Design Isar schema for offline storage
- [ ] **Sync queue**: Plan how changes sync to server
- [ ] **Conflict resolution**: Define merge strategy (server wins, client merges)
- [ ] **Graceful degradation**: What happens when offline?

### 5. Implement Mobile (if applicable)
- [ ] Create feature module in `mobile/lib/features/[feature]/`
- [ ] Follow clean architecture: `data/`, `domain/`, `presentation/`
- [ ] Use offline-first repository pattern (see `.ai/standards.md`)
- [ ] Add to sync queue for server updates
- [ ] Test on 2GB RAM Android 8 emulator

### 6. Implement Server (if applicable)
- [ ] Create Lambda function in `server/src/Functions/`
- [ ] Ensure AOT compliance (check `server/AOT_COMPLIANCE.md`)
- [ ] Make API idempotent (use idempotency keys)
- [ ] Handle conflict resolution (version checks)
- [ ] Optimize for cold start (<2s)

### 7. Implement Infrastructure (if applicable)
- [ ] Add CDK constructs in `infra/lib/`
- [ ] Configure Lambda: ARM64, 512MB memory, 10s timeout
- [ ] Set up DynamoDB tables (on-demand billing)
- [ ] Add CloudWatch alarms for errors
- [ ] Optimize for cost (reserved concurrency limits)

### 8. Localization
- [ ] Add strings to `.arb` files in `mobile/lib/shared/l10n/`
- [ ] Translate to Amharic, Afan Oromo, English
- [ ] Use culturally appropriate terminology (validate with locals)
- [ ] Test with Ethiopic script rendering

---

## Testing

### 9. Write Tests
- [ ] **Unit tests**: Business logic, repositories (>80% coverage)
- [ ] **Widget tests**: All custom UI components
- [ ] **Integration tests**: Offline → online sync flows
- [ ] **Offline scenario tests**: Disconnect network during test
- [ ] **Conflict resolution tests**: Concurrent edits from multiple devices

### 10. Manual Testing
- [ ] Test on low-end device (2GB RAM, Android 8)
- [ ] Test offline mode (airplane mode)
- [ ] Test sync after reconnection
- [ ] Test with slow 3G connection (Chrome DevTools throttling)
- [ ] Test in all three languages (Amharic, Afan Oromo, English)
- [ ] Verify data usage (<1MB per session)
- [ ] Check battery usage (<5% per hour)

---

## Documentation

### 11. Update Documentation
- [ ] Update feature spec status to "Implemented"
- [ ] Add code comments explaining offline behavior
- [ ] Document any Ethiopian context adaptations
- [ ] Update API reference if server changes made
- [ ] Add migration guide if breaking changes

---

## Deployment

### 12. Pre-Deployment Checklist
- [ ] Run full test suite (mobile, server, infra)
- [ ] Validate AOT compilation: `dotnet publish -c Release /p:PublishAot=true`
- [ ] Check APK size increase (<5MB)
- [ ] Review security implications (see `qa/threat_models/`)
- [ ] Get human developer approval

### 13. Deploy
- [ ] Deploy to staging first: `cdk deploy --context env=staging`
- [ ] Run smoke tests on staging
- [ ] Monitor CloudWatch for errors
- [ ] Deploy to production: `cdk deploy --context env=prod`
- [ ] Monitor for 24 hours

---

## Post-Deployment

### 14. Monitor & Iterate
- [ ] Check CloudWatch metrics (errors, latency, cost)
- [ ] Gather user feedback (if beta users available)
- [ ] Document lessons learned in feature spec
- [ ] Mark task as complete `[x]` in `TASK.md`

---

## Ethiopian Context Checklist

Before marking feature complete, validate:
- [ ] Works offline (disconnect network during test)
- [ ] Tested on 2GB RAM Android 8 emulator
- [ ] APK size increase <5MB
- [ ] Data usage <1MB per typical user session
- [ ] Localized in Amharic, Afan Oromo, English
- [ ] Icons/images used for key actions (not just text)
- [ ] Battery usage <5% per hour of active use
- [ ] Culturally appropriate (validated with local context)

---

**Last Updated**: 2026-02-10  
**Version**: 1.0.0

