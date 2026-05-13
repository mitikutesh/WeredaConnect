# Isar Hardening — Sprint Roadmap

> **Status:** Proposed · **PO:** [owner] · **Start:** 2026-05-19 · **Target ship:** 2026-08-20
> **Source threat model:** `docs/mobile/isar-implementation.md` §⚠️ Required hardening (17 items)
> **Engineering capacity:** 1 dev · 7 sprints × 2 weeks · ~20 pts/sprint
> **Confirms prior audit:** `specs/features/sprint-plan-2026-03-19.md` row *"Isar unencrypted — TRUE — Key generated but never used"*

This directory holds the planning artifacts for closing every gap in the Isar
implementation guide's hardening table. The work is organized as 4 epics over
7 sprints. Migration (Sprint 3) is the highest-blast-radius work and is
gated by a separate dev-cohort rollout plan.

---

## Documents in this folder

| File | Purpose | Audience |
|---|---|---|
| `README.md` (this) | Roadmap, epics, risks, success metrics | PO, founder, security reviewer |
| `sprints.md` | All 28 user stories with JIRA-ready acceptance criteria | Engineering, QA |
| `stories.csv` | Same 28 stories as CSV — bulk-import-ready for JIRA / Linear / GitHub Issues | Eng PM, tooling |
| `pr-templates/` | Per-story PR scaffolds (descriptions + branch names) | Engineering |
| `risk-acceptance-signoff.md` | Founder + security-reviewer countersignature page for HS-3 migration | Founder, security reviewer |
| `sprint-3-rollout.md` | Dev cohort → 5% → 25% → 100% staged-rollout plan | Release manager, PO |

---

## Epics

| # | Epic | Sprints | Items closed | Outcome |
|---|---|---|---|---|
| E1 | **Encrypt at rest** | S1, S2, S3 | #1, #2, #3, #4, #9, #10, #11 | Device theft no longer = PII disclosure |
| E2 | **Safe sync under flap & shared device** | S4 | #5, #6, #7, #12 | No duplicate Telebirr charges; no cross-user mutations |
| E3 | **Observability & ops** | S5, S6 | #14, #15 | <5 min mean-time-to-detect on sync regressions |
| E4 | **Long-term key hygiene** | S7 | #8 | Compromise recovery is possible |

Items #16 (`allowBackup="false"`) and #17 (server-side idempotency) are
**already implemented** in production — covered by doc updates in S1 with
no code change.

---

## Sprint summary

| Sprint | Dates | Goal | Pts | Risk |
|---|---|---|---|---|
| S1 | May 19 – May 30 | Encryptor v1: AAD + prefix + key-integrity HMAC | 15 | Low |
| S2 | Jun 02 – Jun 13 | Wire encryption into User + Crop repositories | 18 | **High** (HMAC pepper, dual-column refactor) |
| S3 | Jun 16 – Jun 27 | One-shot v1→v2 migration | 16 | **Critical** (data-loss risk) |
| S4 | Jun 30 – Jul 11 | Sync queue session gating + idempotency wiring | 17 | Medium |
| S5 | Jul 14 – Jul 25 | Biometric gate + WorkManager hardening | 14 | Medium (UX impact) |
| S6 | Jul 28 – Aug 08 | Observability + release tooling | 13 | Low |
| S7 | Aug 11 – Aug 20 | Key rotation + (Android) Keystore platform channel | 12 | Low (can slip) |

**Total:** 105 pts across 14 weeks. **Buffer:** 2 pts/sprint built in for S1–S6
unplanned work; S7 explicitly slip-friendly.

---

## Critical path

```
S1 ──► S2 ──► S3 (gated: dev cohort) ──► production
                  │
                  └─► S4 (can start in parallel after S2 lands)
                         │
                         └─► S5 ──► S6 ──► S7
```

S3 is the single bottleneck. If S3 dev-cohort gate fails, S4 still ships
(different files), but S5+ assumes encryption is live in prod.

---

## Top risks

| Risk | Likelihood | Impact | Mitigation |
|---|---|---|---|
| Migration corrupts data on a subset of devices | M | **Critical (unrecoverable)** | Dev cohort → 5%/25%/100% Play Console rollout → remote kill switch in S6 |
| HMAC pepper rotation breaks server-side phone lookup | L | High | Document pepper-management runbook in S2; rotate only on major version |
| Cert-pin mismatch after API Gateway cert rotation bricks installs | L | Critical | S6 adds pin-overlap window check; coordinate with infra release calendar |
| Biometric requirement blocks rural users with no PIN enrolled | M | Medium | First-run "set up a device PIN" flow; graceful fallback with warning banner |
| Sentry/CloudWatch quota overrun | L | Low | Free tier covers steady state; paid ≈ $26/mo, in cost guardrails |
| Solo-dev burnout on a 14-week security sprint | **M** | High | 2-pt buffer per sprint, S7 slippable, no scope creep |

---

## Out of scope (PO call)

- iOS app — lands in its own track; reuses the AAD-bound FieldEncryptor
- Web admin offline mode — **by explicit design** (see `docs/mobile/isar-implementation.md` §6)
- HSM/cloud-KMS-backed key wrapping — overkill at 100k-user scale, revisit at 1M
- Server-side per-row encryption — DynamoDB SSE-KMS covers at-rest already
- Local audit logging of who-read-what — would require OS-level capability not available

---

## Decision points (PO must call)

| When | Decision | Default |
|---|---|---|
| S1 planning | `cryptography` version pin | `cryptography: 2.7.0` exact (no `^`) |
| S2 planning | HMAC pepper strategy | App-static secret, build-time-injected via Secrets Manager |
| End of S2 | Promote S3 to dev cohort, or add a staging-validation sprint? | Promote if S2 demo passes |
| End of S3 dev-cohort week | Promote to 5% / hold / rollback? | See `sprint-3-rollout.md` halt criteria |
| S5 planning | Biometric UX on no-PIN devices | First-run "set up PIN" + warning banner fallback |
| S7 planning | Platform-channel Keystore — Android only or both? | Android only; iOS deferred to iOS GA |

---

## Success metrics (definition of done at roadmap level)

| Metric | Baseline (today) | Target (end S7) |
|---|---|---|
| PII fields encrypted in Isar | **0%** | **100%** of `late String` PII fields |
| Fayda images on disk as plaintext bitmaps | **100%** | **0%** (encrypted blobs in Isar) |
| Duplicate Telebirr escrow charges per 1k orders | unknown | **0** |
| Cross-user mutations posted under wrong session | unknown | **0** (measured: SyncFunction logs) |
| Median cold-start on Tecno Spark 8 (2GB RAM) | 1.2s | ≤ **1.8s** (+600ms biometric prompt budget) |
| Time-to-detect sync regression | days | < **5 min** (CloudWatch + synthetic) |
| `FieldDecryptionException` rate steady-state | n/a | **0/day** |
| Apps reachable after CA rotation | manual | **100%** (pin overlap window) |

---

## Stakeholder communication plan

| Stakeholder | Cadence | Format | What they need |
|---|---|---|---|
| Founder | Per-sprint | Demo + 1-page summary | Schedule risk, cost delta |
| Security reviewer | After S1, S3, S5, S7 | PR walkthrough | Code-level signoff on each crypto change |
| Compliance (Fayda regulator if applicable) | After S3 | 1-page note | "PII at rest is now AES-256-GCM encrypted" |
| Beta users | Dev cohort, then staged | Play Console release notes | "Brief one-time storage update on next launch" |

---

## References

- Threat model and acceptance specs: `docs/mobile/isar-implementation.md`
- Prior PO audit (confirms today's baseline): `specs/features/sprint-plan-2026-03-19.md`
- Project completeness rules: `.ai/completeness-rules.md`
- Marketplace flow that must not regress: `CLAUDE.md` § Core Marketplace Flow

---

*Roadmap version 1.0 — 2026-05-13*
