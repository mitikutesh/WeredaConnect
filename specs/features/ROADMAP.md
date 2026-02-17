# WeredaConnect Product Roadmap
**Version**: 2.0.0 | **Date**: 2026-02-16 | **Status**: Active — Phase R4 next

---

## Phase Status

| Phase | Status | Completion |
|-------|--------|------------|
| **R1: Security Foundation** | ✅ COMPLETE | Feb 2026 |
| **R2: Auth + Data Models** | ✅ COMPLETE | Feb 14, 2026 |
| **R3: Sync + First Feature** | ✅ COMPLETE | Feb 15, 2026 (2 weeks early) |
| **R4: Complete Transaction Flow** | ⏳ PREP DONE | Target: Apr-May 2026 |

**What works today**: Register/login (online+offline), create crop listings offline, auto-sync when online, conflict resolution, browse listings from server, 3 languages, 228 server tests passing.

**Full feature scoring for all 31 features**: see [BACKLOG.md](BACKLOG.md)

---

## Prioritized Roadmap -- Sequenced Phases

### Phases R1-R3: COMPLETE ✅

R1 (Security Foundation), R2 (Auth + Data Models), and R3 (Sync Engine + Crop Listings) are all complete. 228 server tests passing, 12+ integration tests, all deployed to AWS (me-south-1). See `docs/archive/` for detailed completion records.

**Deferred items**: F-01 (git history purge — runbook ready at `docs/runbooks/f01-git-history-purge.md`), F-05 (cert pinning — real hashes extracted, activation at `docs/runbooks/f05-cert-pinning-activation.md`).

---

### Phase R4: Complete Transaction Flow (Week 10-14)

**Goal**: The full Crop > Trip > Delivery > Payment flow works end-to-end.
**Layer**: 2 - Stability and Trust + Core Transaction Flow

| Order | Feature | Score | Delivers |
|-------|---------|-------|----------|
| R4.1 | F-11: Trip listing flow | 13 | Transport marketplace |
| R4.2 | F-12: Delivery verification (QR + GPS) | 16 | Trust mechanism |
| R4.3 | F-13: Telebirr escrow integration | 19 | Revenue |
| R4.4 | F-14: Fraud detection checks | 13 | Platform safety |
| R4.5 | F-15: Conflict resolution UI | 10 | User experience |
| R4.6 | F-18: S3 access logging + versioning | 3 | Audit trail |

**Exit Criteria**:
- Trucker can post trip availability
- System suggests matching crop listings for a trip route
- QR code generated at pickup, scanned at delivery, GPS captured at both
- Telebirr escrow holds funds on buyer confirmation, releases on verified delivery
- Fraud detection flags suspicious activity
- Sync conflicts show user-friendly resolution UI
- S3 uploads bucket has access logging and versioning enabled

**Milestone**: Minimum Viable Product (MVP) -- WeredaConnect can facilitate a real transaction from crop listing to payment.
**Estimated Effort**: 25-30 days (solo developer)

---

Phases R5 (Production Hardening) and R6 (Growth and Expansion) are deferred. See [BACKLOG.md](BACKLOG.md) for details.

---

## Contracts Required for R4

| Contract | Location | Status |
|----------|----------|--------|
| Escrow Protocol | `specs/contracts/escrow-protocol.md` | ✅ v1.0.0 (Feb 17, 2026) |
| Trip Listing API | `specs/contracts/trip-listing-api.md` | ✅ v1.0.0 (Feb 16, 2026) |
| Delivery Verification API | `specs/contracts/delivery-verification-api.md` | ✅ v1.0.0 (Feb 17, 2026) |
| Auth API | `specs/contracts/auth-api.md` | ✅ EXISTS |
| Crop Listing API | `specs/contracts/crop-listing-api.md` | ✅ EXISTS |
| Sync Protocol | `specs/contracts/sync-protocol.md` | ✅ EXISTS |

---

## Risk Register

| Risk | Impact | Probability | Mitigation |
|------|--------|-------------|------------|
| Cognito SMS delivery in Ethiopia (Ethio Telecom) | HIGH | MEDIUM | Test SMS OTP with Ethiopian numbers early. Have fallback (voice OTP). |
| .NET 10 AOT publish fails on Linux ARM64 | HIGH | LOW | Already scaffolded; need to verify dotnet publish works end-to-end. |
| Isar 4.x encryption key management edge cases | MEDIUM | MEDIUM | Test: key rotation, app reinstall, device reset, migration. |
| Telebirr API availability and documentation | HIGH | MEDIUM | Contact Telebirr developer relations early. Build mock API for development. |
| AICE insurance API terms and availability | MEDIUM | HIGH | Defer to Phase R6. Confirm API availability before planning. |
| Solo developer burnout (25+ week roadmap) | HIGH | HIGH | Strict phase gates. Ship MVP by Phase R4. Celebrate milestones. |
| Flutter SDK not installed on dev machine | LOW | KNOWN | Install before Phase R2. Blocks all mobile work. |
| Node.js 18 vs 20 LTS mismatch | LOW | KNOWN | Upgrade to 20.x before Phase R1. Blocks CDK if breaking changes exist. |
| Offline speech recognition in Amharic quality | MEDIUM | HIGH | Defer to Phase R6. Research models early but don't block on it. |

---

## Timeline Summary

| Phase | Weeks | Calendar | Milestone |
|-------|-------|----------|-----------|
| **R1: Security Foundation** | 1-2 | Feb 2026 | Backend deployable, secrets purged |
| **R2: Auth + Data Models** | 3-5 | Mar 2026 | Users can register and log in |
| **R3: Sync + First Feature** | 6-9 | Mar-Apr 2026 | Offline crop listing works E2E |
| **R4: Complete Flow (MVP)** | 10-14 | Apr-May 2026 | Full transaction flow with payment |
| **R5: Production Hardening** | 15-17 | Jun 2026 | Production-ready |
| **R6: Growth + Expansion** | 18+ | Jul 2026+ | Voice, insurance, dashboards |

**Total to MVP**: ~14 weeks (solo developer)
**Total to Production**: ~17 weeks

---

**Document Version**: 1.3.0
**Created**: 2026-02-11
**Last Updated**: 2026-02-17 (all R4 contracts written; deferred items planned)
**Completed task history**: See `docs/archive/completed-tasks.md`
