# Risk Acceptance & Sign-Off — Isar Hardening Roadmap

> **Purpose:** Capture explicit, dated approval from the founder and the
> security reviewer for the highest-stakes work in the roadmap before any of
> it lands in production. This is the accountability hook — once countersigned,
> the team can execute without re-litigating each decision mid-sprint.
>
> **Read alongside:** `README.md` (roadmap overview), `sprint-3-rollout.md`
> (the staged rollout that depends on this sign-off), `docs/mobile/isar-implementation.md`
> (technical specs and threat model).

---

## Plain-language summary

Today, WeredaConnect mobile stores Ethiopian phone numbers, Fayda national-ID
numbers, Fayda ID-card scans, and user names **in plaintext** on the device.
A lost or stolen phone, a rooted-device exploit, a malicious app on the
same device, or a Google-Drive backup compromise gives an attacker
straightforward access to this PII.

The roadmap closes this gap over 7 sprints (~14 weeks). The single highest-
risk piece is **HS-3** — a one-shot migration that converts every existing
user's plaintext data to encrypted form. Because the migration runs on
user devices that the team cannot reach, a bug in HS-3 can produce
unrecoverable corruption on a subset of devices.

We mitigate that risk with: a dev-cohort gate (15 users for 7 days), a
staged Play Console rollout (5% → 25% → 100%), and a remote kill switch
that can disable the migration mid-rollout. Worst-case impact, with all
mitigations holding, is **~0.5% of users** needing to wipe and re-sync,
losing local-only state (anything they did offline that hadn't synced
to the server when the bug manifested).

This document asks the founder to accept that worst-case outcome before
HS-3 ships, and asks the security reviewer to confirm the plan addresses
the threats they raised.

---

## What we are signing off

By signing this document, the named approvers acknowledge:

1. **The current production state.** PII fields including phone numbers
   and Fayda IDs are stored in plaintext in Isar today. Every day before
   the roadmap completes is a day with elevated exposure on lost/stolen
   devices.

2. **The chosen mitigation.** Field-level AES-256-GCM encryption with AAD
   binding, version-prefixed ciphertexts, biometric gate before key load,
   and key-integrity HMAC. (See `docs/mobile/isar-implementation.md` §7.)

3. **The migration plan.** HS-3 runs a one-shot conversion of existing
   plaintext rows to encrypted form. Migration is idempotent under
   crash-retry. Rolled out via dev cohort → 5% → 25% → 100% Play Console
   stages, with halt criteria defined in `sprint-3-rollout.md` §3.

4. **The worst-case outcome.** A bug that escapes the dev-cohort gate
   and reaches some fraction of the 5% rollout could require
   `clearAllData()` on affected installs. Local-only state — offline
   crop listings, queued mutations not yet synced — is **not
   recoverable** for affected users. Server-stored state is recoverable
   via re-login. **Estimated worst-case affected: ≤ 0.5% of installs**
   (5% rollout × ~10% with offline tail).

5. **The kill switch.** A remote-config flag (`migration_disabled`)
   exists, is tested in production before HS-3 ships, and can be flipped
   server-side within 15 minutes of an anomaly to stop new migrations.
   Already-completed migrations are not reversible.

6. **The out-of-scope items.** iOS migration, HSM-backed key wrapping,
   and per-row CRDT conflict resolution are explicitly deferred. The
   founder accepts that this work will be revisited separately.

---

## Risks and explicit decisions

### Risk 1 — Migration corruption on a subset of devices
- **Likelihood:** Medium (untested migrations at scale always surprise)
- **Impact:** Critical for affected users — local-only state unrecoverable
- **Mitigation:** Dev cohort → 5%/25%/100% staged rollout; kill switch; idempotent migration
- **Founder decision needed:** Accept ≤ 0.5% worst-case affected? **YES / NO**

### Risk 2 — Biometric requirement excludes users with no enrolled biometric/PIN
- **Likelihood:** Medium (rural devices vary widely)
- **Impact:** UX friction — first-run "set up a PIN" flow + persistent warning banner; fallback to no-gate is permitted
- **Founder decision needed:** Accept showing a PIN-setup nag for unprotected installs? **YES / NO**

### Risk 3 — HMAC pepper rotation invalidates server-side phone lookups
- **Likelihood:** Low (rotation only on major-version release, coordinated)
- **Impact:** Login by phone fails until users re-register or migrate
- **Mitigation:** Pepper-management runbook in HS-2; rotate only on coordinated mobile + server release
- **Security-reviewer decision needed:** Approve build-time-injected app-static secret as the pepper? **YES / NO**

### Risk 4 — Cert-pin rotation bricks installs that don't update before the cert changes
- **Likelihood:** Low (already mitigated in current build with overlap window)
- **Impact:** Critical for affected installs — app cannot reach API until updated
- **Mitigation:** Always release with both pins active before rotating the API Gateway certificate
- **Founder decision needed:** Maintain the pin-overlap window discipline at every cert rotation? **YES / NO**

### Risk 5 — Solo-dev burnout over 14 weeks of security-heavy work
- **Likelihood:** Medium (real, not theoretical)
- **Impact:** Quality drops; sprints slip; rollout pressure compounds
- **Mitigation:** 2-pt buffer per sprint; HS-7 is explicitly slippable; founder accepts a pause if signals appear
- **Founder decision needed:** Commit to an honest pulse check at end of HS-3 and HS-5? **YES / NO**

---

## Worst-case scenarios and acceptance

### Scenario A — Migration corruption in production despite the rollout gate

**What happens:**
1. Sentry alarms fire (`FieldDecryptionException` rate spike) during 5% rollout
2. Engineering flips `migration_disabled = true` within 15 min
3. Play Console rollout halted; existing 5% installs frozen mid-state
4. Within 24 hours: root cause + patch + new APK to dev cohort
5. Affected users receive in-app banner + force-relogin CTA
6. ≤ 0.5% of users lose local-only state

**Acceptance:** The founder accepts this outcome as acceptable rather than
deferring the roadmap indefinitely. The alternative — shipping plaintext
PII indefinitely — is judged worse.

### Scenario B — HS-3 dev-cohort fails repeatedly

**What happens:**
1. Two consecutive dev-cohort runs surface bugs
2. PO decision: pause HS-3 for one full sprint to add tests / refactor
3. HS-4 still ships on schedule (unrelated files)
4. HS-5 partially gated — biometric gate still ships, but only protects
   already-encrypted installs (which is everyone post-HS-3, so the gate
   is effectively a no-op until HS-3 lands)
5. End-state ship date slips from 2026-08-20 to 2026-09-15 worst case

**Acceptance:** The founder accepts a four-week schedule slip is preferable
to shipping HS-3 with insufficient confidence.

### Scenario C — Roadmap is paused mid-flight

**What happens:**
1. Solo-dev pulse check at end of HS-3 or HS-5 returns "burned out"
2. Roadmap pauses for 2 weeks
3. Resume from current state — no rework needed because each sprint is
   independent post-foundation (HS-1 + HS-2)

**Acceptance:** The founder pre-authorizes the pulse-check pause without
needing a re-approval.

---

## Sign-off

### Founder

```
I acknowledge that I have read the risk summary above and the supporting
documents:
  - specs/features/isar-hardening/README.md
  - specs/features/isar-hardening/sprint-3-rollout.md
  - docs/mobile/isar-implementation.md §⚠️ Required hardening

I accept the worst-case outcomes described in Scenarios A, B, and C.
I authorize the team to execute the roadmap as specified, with the named
PO making stage-promotion decisions on my behalf during the rollout window.

Decisions on the five risks above:
  Risk 1 (≤0.5% migration loss):     [ ] YES   [ ] NO
  Risk 2 (PIN-setup nag):             [ ] YES   [ ] NO
  Risk 3 (build-time HMAC pepper):    [ ] N/A — security reviewer's call
  Risk 4 (pin-overlap discipline):    [ ] YES   [ ] NO
  Risk 5 (HS-3/HS-5 pulse checks):    [ ] YES   [ ] NO

Founder name:  __________________________
Signature:     __________________________
Date:          __________________________
```

### Security reviewer

```
I acknowledge that I have reviewed the threat model and mitigations in
docs/mobile/isar-implementation.md §⚠️ Required hardening through §7, and
that the AAD binding, prefix versioning, key-integrity HMAC, and
fail-closed decrypt path address the AAD-substitution, ciphertext-
downgrade, key-swap, and silent-tampering threats I raised.

I confirm that the items I flagged are either:
  (a) closed by an explicit story in the catalog (cite story ID),
  (b) deferred with explicit acknowledgement in the out-of-scope list, or
  (c) listed below as a residual risk I accept.

Decisions on the security-relevant risks above:
  Risk 3 (build-time HMAC pepper):    [ ] APPROVE   [ ] REQUIRE ALTERNATIVE

Residual risks I accept (if any):
  _________________________________________________________________
  _________________________________________________________________

Reviewer name: __________________________
Signature:     __________________________
Date:          __________________________
```

### PO

```
I confirm that:
  - The roadmap matches the security review findings.
  - Sprint cadence and capacity are realistic for the assigned engineer.
  - Stage-promotion authority during HS-3 rollout sits with me, with
    escalation to the founder for halt-events.
  - I will update this document if the plan materially changes mid-execution.

PO name:       __________________________
Signature:     __________________________
Date:          __________________________
```

---

## Change log

Track material changes to this document so the audit trail is intact.

| Date | Author | Change |
|---|---|---|
| 2026-05-13 | PO | Initial version, awaiting signatures |
| | | |

---

*Sign-off page v1.0 — 2026-05-13*
