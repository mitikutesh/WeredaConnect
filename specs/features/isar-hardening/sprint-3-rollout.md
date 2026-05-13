# Sprint 3 — v1→v2 Migration Rollout Plan

> **Status:** Proposed · **Sprint:** S3 (Jun 16 – Jun 27, 2026)
> **Risk level:** **CRITICAL — unrecoverable on bad outcome**
> **Decision owner:** PO with security reviewer sign-off
> **Last updated:** 2026-05-13

This document is the rollout runbook for the one-shot v1→v2 migration that
encrypts every existing user's plaintext PII inside Isar. It is the **only
sprint in the roadmap that can permanently scramble user data on devices we
cannot reach.** Every decision below is sized accordingly.

---

## 0. Why this needs its own runbook

By the end of Sprint 2, code on user devices will:

- Have the new `phoneSearchHash` + `phoneEncrypted` columns declared but unused
- Still read/write plaintext to the old `phoneNumber`, `displayName`, `faydaId` fields
- Have the new AAD-bound `FieldEncryptor` API available but no rows are encrypted yet

Sprint 3 ships the **one and only** migration that converts existing rows to
the new format. After that point, the legacy plaintext fallback is removed
and reads fail closed. **A migration bug that escapes the staged rollout
results in:**

- Some installs with mixed-key rows that decrypt to garbage
- No remote ability to fix it (mobile DB on user device, no SSH)
- Affected users have to `clearAllData()` and re-sync from server — but
  any **local-only** state (offline crop listings, pending orders queued for
  Telebirr) is **lost**

Hence the dev cohort gate, the 5%/25%/100% staging, the kill switch, and
this runbook.

---

## 1. Pre-flight checklist (gate to start dev cohort)

**Must all be ✅ before promoting Sprint 3 build to dev cohort.** PO signs off.

### Code

- [ ] All Sprint 1 + Sprint 2 stories merged to `main`
- [ ] `_migrateToV2` is idempotent (verified by S3-01 test "migration is idempotent under crash-retry")
- [ ] Schema version persisted inside Isar `_meta` collection, NOT in secure storage (S3-03)
- [ ] `FieldEncryptor` `legacyPlaintextAllowed: true` accepted only during migration scope
- [ ] Crash-mid-migration test green — kill at row N, restart → completes without corruption
- [ ] Empty-DB migration test green — fresh install runs migration as no-op

### Performance

- [ ] Migration of 1000 User rows completes in < 500 ms on Tecno Spark 8
  (2 GB RAM, target floor device per `.ai/tech_stack.md`)
- [ ] If runtime > 500 ms, a splash screen with "Updating storage…" message in
  all 3 languages is shown
- [ ] App cold start excluding migration stays < 1.2 s

### Observability

- [ ] Sentry SDK wired and `FieldDecryptionException` reports at warning
  level (S6-01 brought forward into S3 release scope — exception to "S6 ships later")
- [ ] Sentry breadcrumb fires at each migration step:
  `migration_v2_started`, `migration_v2_row`, `migration_v2_complete`
- [ ] Migration failure is captured as `Sentry.captureException`, NOT swallowed

### Release artifacts

- [ ] Internal-testing-only Play Console track created (`internal-testing-isar-v2`)
- [ ] Build signing config matches production
- [ ] APK build verified against `flutter build apk --release`
- [ ] Aikido security scan passes on touched paths
- [ ] `flutter build web --no-tree-shake-icons` passes (platform-pair sanity)

### Rollback prep

- [ ] Migration kill-switch flag mechanism in place (S6-03 brought forward —
  the kill switch must exist BEFORE the migration ships, or we have no
  recourse mid-rollout)
- [ ] Server-side endpoint `/config/mobile.json` serving `{migration_disabled: false}` deployed and reachable
- [ ] Verified: flipping `migration_disabled` on the server causes new app launches to skip the migration (manual test)

---

## 2. Stage 1 — Dev cohort (1 week)

### Cohort composition

| Profile | Count | Source | Why |
|---|---|---|---|
| Internal QA devices | 3 | dev-team Android phones | Full debug instrumentation |
| Recruited beta farmers | 5 | Existing beta users with longest local-only data tail | Real-world data volume |
| Recruited beta truckers | 3 | Existing beta users | Different schema mix |
| Recruited beta buyers | 4 | Existing beta users | Including users with 50+ orders |
| **Total** | **15** | | |

**Recruitment:** SMS via Telebirr-verified contacts; offer 50 ETB airtime for
participation. Consent form (Amharic + English) covers: a known migration
runs on this build, a rollback may occur, you'll be asked for screenshots
if issues arise.

**Devices to cover:** at minimum one each of: Tecno Spark 8, Itel A23 Pro,
Samsung A04, Infinix Smart 6. These are the four most common in the
existing beta population.

### Monitoring during cohort week

**Sentry dashboard:** filter by app version `v2.0.0-cohort.1`. Watch:

| Signal | Threshold | Action |
|---|---|---|
| `FieldDecryptionException` count | **> 0** | **HALT** — investigate the row before promoting |
| Migration-failure exceptions | **> 0** | **HALT** — root cause before promoting |
| `unknown_error` rate (from `_classifyError`) | > 5% of error volume | Investigate, do not yet halt |
| App-cold-start p95 | > 2.5 s | Investigate (likely splash logic) |
| Crash-free session rate | < 99% | **HALT** |
| Sync queue success rate | < baseline by > 1pt | Investigate |

**CloudWatch:** synthetic mobile-flavor sync test should remain green
throughout (S6-04 is concurrent with S3 ship — wire both).

**User-reported (Telegram beta channel):**
- Any "can't see my name" report → investigate as `FieldDecryptionException` proxy
- Any "phone number not recognized at login" → S2-02 dual-column fallback failed; investigate

### Daily check (PO + 1 engineer, 15 minutes)

Day-1, Day-2, Day-3, Day-7 standing calls. Each call:
1. Sentry dashboard scroll (any new exceptions?)
2. Cohort Telegram channel scan (any user reports?)
3. CloudWatch synthetic-monitor status
4. Decision: continue / hold / halt

### Promotion criteria (Day 7 → Stage 2)

**All must hold:**
- [ ] Zero `FieldDecryptionException` across all 15 cohort devices for 7 consecutive days
- [ ] Zero migration-failure exceptions
- [ ] Crash-free session rate ≥ 99%
- [ ] No cohort user reports name/phone display issues
- [ ] App cold-start p95 ≤ 2.0 s
- [ ] Synthetic sync test green for the full window

**If ANY criterion fails:** **HALT.** Root cause via Sentry + reproduction.
Fix, retest in cohort with a new build (`v2.0.0-cohort.2`), restart the
7-day clock.

---

## 3. Stage 2 — 5% Play Console staged rollout (3 days)

### What 5% means

Play Console randomly selects 5% of users for the new APK. Existing installs
on the old version are untouched until they next update. This caps blast
radius: a critical bug affects only 5% of the install base, and we can
halt before the remaining 95% see it.

### Monitoring (same dashboards as Stage 1)

Volumes are higher — expect ~500 installs on the new APK if base is 10k.
With higher volume, statistical thresholds tighten:

| Signal | Stage 1 threshold | Stage 2 threshold |
|---|---|---|
| `FieldDecryptionException` count | > 0 | **> 5 in 24 hours** |
| Migration failures | > 0 | **> 2 in 24 hours** |
| Crash-free session rate | < 99% | **< 99.5%** |

**The thresholds harden as the base grows.** A handful of cohort failures
might be device-specific; the same rate at 5% rollout is a systemic
problem.

### Halt actions

If any threshold breached:

1. **Immediate (within 15 min):** flip the remote `migration_disabled = true`
   flag. New app launches skip the migration; existing migrated installs
   stay migrated (no down-conversion is possible).
2. **Within 1 hour:** Play Console → halt rollout. New users do not get
   the affected APK. Existing 5% are stuck — but the flag means they're at
   least not actively running the migration.
3. **Within 24 hours:** root cause + patch + new APK to dev cohort. Repeat
   Stage 1.

### Promotion to Stage 3 (Day 3 → 25%)

All Stage 2 thresholds held. PO sign-off required.

---

## 4. Stage 3 — 25% Play Console staged rollout (3 days)

Same monitoring as Stage 2. Volumes 5× higher; thresholds same as Stage 2.

**Additional check at 25%:** server-side `EscrowIdempotencyRepository` write
patterns. Any spike in duplicate-key writes (where the same idempotency key
appears with materially different payloads) suggests a client-side
regression that isn't visible from Sentry alone.

### Promotion to Stage 4 (Day 3 → 100%)

All thresholds held. PO sign-off required.

---

## 5. Stage 4 — 100% rollout

Same monitoring continues for a further **7 days post-100%**. After that,
S3 is closed and the migration is considered done.

**Day +14 from 100%:** flip `_legacyPlaintextAllowed` default in
`FieldEncryptor` to permanently false in code (removes the migration
escape hatch from the codebase). Ship as part of a subsequent release.

---

## 6. Rollback plan

### 6.1 What's recoverable

| Scenario | Recoverable? | How |
|---|---|---|
| Migration disabled by remote flag before run | YES | User reopens app post-fix → normal migration runs |
| Migration runs but encrypts only some rows (partial) | YES — IF idempotent (S3-01) | User reopens app → migration resumes from where it stopped |
| Migration corrupts rows (writes garbage) | **NO** | `clearAllData()` + force-relogin → user loses local-only state |
| Wrong AAD bound to ciphertext (bug in encrypt path) | **NO** | Same as above |
| Key written but HMAC tag missing (S1-03 bug) | YES | `KeyIntegrityFailure` triggers `clearAllData()` automatically |

### 6.2 Comms playbook

**If kill-switch flip + halt happens during Stage 2/3:**

1. Telegram beta channel (15 min): "We've paused a storage update for a
   subset of users while we investigate. No data loss expected. Your app
   continues to work normally."
2. PO email to founder (1 hr): What broke, blast radius, ETA to fix.
3. If user-visible (display name showing as garbage): in-app banner
   (Amharic + Afan Oromo + English): "We're fixing a display issue. Your
   data is safe on our servers. Tap here to refresh from cloud."
4. The "refresh from cloud" CTA triggers `clearAllData()` + force-relogin.

**If we have to clearAllData on affected users:**

- This **loses local-only state** for those users — anything they did
  offline that hadn't synced yet
- Worst-case affected: 5% of install base × estimated 10% with offline tail
  = 0.5% of users. Cap the visible damage with the kill switch FIRST so
  this is the recovery path, not the default path.
- Reach affected users by Cognito sub from Sentry → trigger a server-side
  "force re-sync" notification

### 6.3 Decision tree

```
Migration anomaly detected
         │
         ▼
Is FieldDecryptionException rate above threshold?
         │
    ┌────┴────┐
    YES       NO
    │         │
    │         ▼
    │     Continue monitoring; bias toward early-flip if doubt remains
    │
    ▼
Flip migration_disabled = true (server side)
         │
         ▼
Stop Play Console rollout (no new users get the APK)
         │
         ▼
Is data corruption confirmed for any user?
         │
    ┌────┴────┐
    YES       NO
    │         │
    │         ▼
    │     Patch, dev cohort, restart Stage 2 with new APK
    │
    ▼
Trigger in-app banner + clearAllData CTA for affected cohort
         │
         ▼
Server-side push notification to affected Cognito subs to re-login
         │
         ▼
Post-mortem (template §7)
```

---

## 7. Post-mortem template

To be filled within 5 business days of any halt-event.

```markdown
# Migration v2 Halt — Post-Mortem

**Date:** YYYY-MM-DD
**Halt stage:** Stage [1/2/3/4]
**Users affected:** N (% of total install base)
**Data loss:** [None / Local-only state for N users / Other]

## Timeline
- HH:MM — Anomaly first observed in [signal]
- HH:MM — Migration_disabled flag flipped
- HH:MM — Play Console rollout halted
- HH:MM — Root cause identified
- HH:MM — Fix shipped to dev cohort

## Root cause
[One paragraph. What broke and why.]

## Why monitoring caught it (or didn't)
[What signal fired, how fast, what we'd add to catch it sooner]

## Affected users — recovery path
[How they got their data back, or what local state was unrecoverable]

## Action items
- [ ] Code fix: ...
- [ ] Test gap closed: ...
- [ ] Monitoring improvement: ...
- [ ] Runbook update: ...
```

---

## 8. Calendar

Assuming Sprint 3 starts 2026-06-16 (Monday).

| Date | Activity |
|---|---|
| Jun 16 – Jun 24 | Sprint 3 development (S3-01, S3-02, S3-03) |
| Jun 25 | Pre-flight checklist gate (§1) — PO sign-off |
| Jun 25 – Jun 26 | Cohort recruitment + APK provisioning |
| Jun 27 – Jul 03 | **Stage 1 — dev cohort (7 days)** |
| Jul 03 | Stage 1 → Stage 2 promotion decision |
| Jul 04 – Jul 06 | **Stage 2 — 5% rollout (3 days)** |
| Jul 06 | Stage 2 → Stage 3 promotion decision |
| Jul 07 – Jul 09 | **Stage 3 — 25% rollout (3 days)** |
| Jul 09 | Stage 3 → Stage 4 promotion decision |
| Jul 10 | **Stage 4 — 100% rollout** |
| Jul 10 – Jul 17 | Post-100% monitoring (7 days) |
| Jul 24 | Code cleanup: flip `_legacyPlaintextAllowed` default to permanently false (ships in next release) |

**Total: 6 weeks from start of Sprint 3 to migration fully closed.**
This is longer than the 2-week sprint nominally suggests — but the rollout
window naturally extends past sprint boundaries. Sprint 4 starts on
schedule (Jun 30) in parallel; the team isn't blocked.

---

## 9. PO sign-off checklist

The PO signs the following before starting Stage 1, and again before each
stage promotion. This is the explicit accountability hook.

```
[ ] I have reviewed the pre-flight checklist (§1) and all items are green.
[ ] I have reviewed the cohort composition (§2) and approve the recruitment list.
[ ] I have reviewed the halt criteria (§3) and accept them as binding.
[ ] I have reviewed the rollback plan (§6) and confirm the kill switch
    is functional in production.
[ ] I accept that ~0.5% of users may need to clearAllData() in worst case,
    losing offline-only local state.
[ ] I designate _________ as the on-call engineer for the rollout window.
[ ] I will personally sign off each stage promotion.

Signed: ____________________  Date: ____________
```

---

## 10. Open questions for PO before Sprint 3 plan kicks off

1. **Cohort airtime budget.** 15 users × 50 ETB = 750 ETB. Approve?
2. **Telegram beta channel** — does this exist, or do we need to create it
   as part of S2?
3. **In-app banner localization.** Amharic + Afan Oromo translations
   needed pre-rollout. Lead time?
4. **Server-side push for force-relogin.** Does Cognito support this with
   our current setup, or does it need work?
5. **Founder availability.** Is the founder reachable during the Stage 2/3
   weekend? If not, who has stop-rollout authority?
6. **iOS migration parity** — when does iOS ship? If overlapping, the iOS
   migration MUST follow the same playbook against the iOS beta cohort.

Block Sprint 3 kickoff until these are answered.

---

*Rollout plan v1.0 — 2026-05-13*
