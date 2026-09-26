# STATE.md - Session Checkpoint

## Checkpoint

**Date:** 2026-05-13 (HS-1 closeout; prior checkpoint 2026-03-19 still authoritative for pre-launch sprint state)
**Branch:** `hs1/s1-05-test-suite-closeout` (closing HS-1); `main` has HS-1 fully merged
**Status:** R4 COMPLETE. **HS-1 (encryption foundation) COMPLETE** — AAD, prefix + fail-closed, key-integrity HMAC, legacy-flag CI guardrail all live in main. HS-2 (repository wiring) is the next sprint. May security review's 7-sprint roadmap continues — see `specs/features/isar-hardening/`.
**Latest Commit:** 016652a - Merge pull request #22 from mitikutesh/hs1/s1-04-legacy-plaintext-guardrail

### Active work: Auth and authorization hardening (2026-09-26, branch `feat/auth-hardening`)

Worktree `.claude/worktrees/auth-hardening`, branched from `e2ec41e` (after SIM-19). Implements
the P0/P1/P2 list from the 2026-09-25 auth assessment plus the mock Fayda provider. Decision
record and deploy notes: `specs/decisions/auth-step-up-plan.md`. Contracts bumped: auth-api
2.1.0, escrow-protocol 1.3.0, order-lifecycle 1.5.0, sync-protocol 1.4.0, push-alerts 1.1.0.
Validation before the PR (after the pre-push review fixes): server 1315 tests green; infra
tsc clean, 46 jest tests + `cdk synth` green; mock provider 23 tests; admin 43 tests green;
mobile analyze clean, 1298 tests green, web build green. **Opened as a PR, NOT deployed.** Deploy needs: new `auth-verify-otp`
Lambda + routes, Cognito client auth-flow change (server-side password login), admin pool
MFA REQUIRED in dev/staging (admins enrol an authenticator on first login), dev mock Fayda
provider (Function URL). Keep `SMS_ENABLED=false` + `DEV_OTP_BYPASS=true` in dev while SNS
is sandboxed.

### Active work — Buyer Intent Matching (2026-06-19, branch `buyer-intenet`)

A buyer intent-matching series is in progress on `buyer-intenet` (not yet merged):
- **Steps 1–4 done & committed**: weighted multi-signal `MatchScorer` (mobile), count-delta
  "new matches" alerts, on-device haversine geo-scoring, server `GET /match` endpoint
  (+ infra route + `match-api.md` contract), and the web online path now using `/match`.
- **Step 5 in progress**: buyer match **push alerts** (FCM via AWS SNS). Design and setup
  docs are written (`.ai/push-notifications-setup.md`,
  `specs/architecture/push-notifications-architecture.md`); server/infra/mobile code
  (intent sync to server, device-token registration, crops Stream → match-on-insert Lambda,
  `firebase_messaging`) is the remaining work. **Not deployed** — requires a Firebase project
  + FCM service-account secret first (see runbook).

### Active work — In-App Messaging (chat + contact + push) (2026-07-09)

Context-scoped in-app chat added across infra + server + mobile, mirroring the
`rating` slice. Buyer↔farmer (crop/order) and buyer↔trucker (trip/delivery);
counterpart phone Call/Copy everywhere; FCM push to the recipient on a new
message. **Also completes the mobile FCM last-mile** (Firebase client + token
registration + Gradle/manifest) that Buyer-Intent Step 5 left pending — so buyer
match alerts can now deliver too. Create-only immutable `message` entity synced
via `/sync`; server denormalizes name/phone authoritatively; role gate enforced
in `ProcessMessageAsync`. New `messages` DynamoDB table (NEW_IMAGE stream) +
`MessageFunction` (GET /messages) + `MessageStreamFunction` (push). Contract:
`specs/contracts/messaging.md`. Verified: server tests + mobile analyze/tests
green. **NOT committed/deployed** — needs `cdk deploy` + the manual SNS Platform
Application (`.ai/push-notifications-setup.md` §2a). Deferred: tap-to-thread
deep-link, order/delivery detail Message buttons. Full notes:
memory `messaging-feature.md`.

---

## Resumption Context

```
ADDENDUM — ISAR HARDENING ROADMAP (updated 2026-05-13)

A deep security review of the Isar local-DB layer produced a 7-sprint
program closing 17 gaps. Start here before resuming the March sprint plan
if the next work session is on encryption / sync / mobile data-at-rest.

ENTRY POINTS:
  - Roadmap overview:   specs/features/isar-hardening/README.md
  - 28 JIRA-ready stories: specs/features/isar-hardening/sprints.md
                         + specs/features/isar-hardening/stories.csv
  - HS-3 migration rollout: specs/features/isar-hardening/sprint-3-rollout.md
  - Threat model & specs:   docs/mobile/isar-implementation.md

HS-1 STATUS: COMPLETE
  All four foundation primitives live in main:
    S1-01  AAD-bound FieldEncryptor encrypt/decrypt           (PR #18)
    S1-02  enc:v1: ciphertext prefix + fail-closed decrypt   (PR #19)
    S1-03  HMAC-SHA256 integrity tag on persisted AES key    (PR #20)
    S1-04  CI lint: legacyPlaintextAllowed allow-list        (PR #22)
  Plus type-design follow-up:
    PR #21  abstract CryptoFailure + enum reasons

  Hardening-table items closed by HS-1:
    #3  No AAD / domain separation
    #10 No key-integrity HMAC on the persisted key

  Closed by HS-1 work outside the original table:
    C-1 Plaintext-on-decrypt-failure downgrade attack (S1-02)
    N-C1 Cross-field ciphertext substitution (S1-01)
    N-H4 Tampered-key undetected on read-back (S1-03)

NEXT SPRINT: HS-2 (wire encryption into User + Crop repositories)
  - 5 stories (S2-01..S2-05), 18 pts
  - Highest-risk story: S2-02 phoneSearchHash + phoneEncrypted split
    (8 pts, breaking schema)
  - Scaffolds ready in specs/features/isar-hardening/pr-templates/

KEY FACTS:
  - HS-1 made the encryption primitive correct, AAD-bound, fail-closed,
    and integrity-tagged. HS-2 actually wires it into production
    repositories — until S2-01..S2-04 land, PII is still plaintext at
    rest in the User and CropListing collections.
  - Server-side idempotency (EscrowIdempotencyRepository,
    ISyncMetadataRepository.IdempotencyKeyExistsAsync) is already wired
    — HS-4 just makes the client use it correctly.
  - allowBackup="false" and cert pinning are already live; doc updated to
    reference them.
  - HS-3 (one-shot v1→v2 migration) is the only sprint with unrecoverable
    failure modes — gated by dev cohort + 5%/25%/100% staged rollout.

TOP OPEN PO DECISIONS (carried forward into HS-2):
  1. cryptography: 2.7.0 exact pin (default: yes) — DONE; in pubspec
  2. HMAC pepper strategy for phoneSearchHash (default: build-time env)
  3. Approve 750 ETB dev-cohort airtime budget
  4. Designate on-call engineer for HS-3 staged rollout window

NEXT IF RESUMING THIS WORK:
  - PO decisions 2-4 above
  - Kick off HS-2: start with S2-01 (encrypt User PII fields).
    Scaffold at specs/features/isar-hardening/pr-templates/S2-01-encrypt-user-pii.md.
  - Note: S2-01 also has to create the UserRepository abstraction —
    no production UserRepository exists today; User reads/writes
    scatter across profile_screen.dart, offline_auth_cache.dart,
    and conflict_resolver.dart.
```

```
PO AUDIT COMPLETE (Mar 19, 2026)

VALIDATED BUILD STATE:
  - Server: 684 tests pass, 0 fail, 0 warnings — SHIP-READY
  - Mobile: 732 tests pass, 3 fail (trip BLoC cancel tests) — near-ready
  - Flutter Web: builds clean, no active bugs
  - Admin: 11 pages, 27 Playwright E2E tests — SHIP-READY
  - CDK: synths clean

AUDIT CORRECTIONS (from initial report):
  - 7 "web bugs" → all already fixed (response keys, Isar guards, matching service)
  - AuthorizerFunction bypass → false alarm (file doesn't exist)
  - 3 failing tests → confirmed (trip BLoC cancel flow, test setup issue)
  - Hardcoded payment string → confirmed ('1000XXXXXXXXXX')
  - JwtValidator dead code → confirmed (never instantiated, but has bypass)

SECURITY POSTURE: 6.5/10
  - Strong: Auth, RBAC, input validation, CORS, rate limiting, IAM, AOT
  - Gaps: cert pinning (disabled), WAF (missing), audit logging (partial),
    git history (exposed key), SNS (sandbox)

SPRINT PLAN (see specs/features/sprint-plan-2026-03-19.md):
  - Sprint 1 (3-4 days): Fix tests, fix hardcoded string, clean dead code,
    update docs, deploy dev, merge to main
  - Sprint 2 (5-7 days): Cert pinning, WAF, audit logging, git purge, SNS exit
  - Sprint 3 (2-3 weeks): Real Telebirr, real Cognito, session TTL, Isar encryption,
    web E2E tests, OTP resend + forgot password

CURRENT: Branch fix-feat has role-features merged (PR #15) + doc restructuring.
NEXT: Start Sprint 1 — fix 3 failing tests (S1-01), then hardcoded string (S1-02),
  then dead JwtValidator (S1-03), then deploy + merge.
```

---

## Key Decisions

- **Server-wins conflict resolution** — simplest for solo farmers (rare conflicts)
- **Composite idempotency keys** — `{userId}:{entityType}:{entityId}:{action}:{timestamp}`
- **Exponential backoff** — 5s, 15s, 45s (max 3 retries)
- **7-day TTL on SyncMetadata** — balances idempotency vs storage costs
- **Batch size 50** — completes in <5s on 3G
- **HMAC-SHA256 over JWT for QR** — smaller payload, better on cheap cameras, simpler
- **5km GPS threshold** — accounts for GPS inaccuracy + farm-to-road distance
- **5 fraud rules** — duplicate detection, price anomaly, velocity limits, GPS consistency, risk scoring
- **3-sprint plan** — ship-ready (days) → security (week) → production (weeks)

---

## Deferred Items

- **T-GA-01**: Real Cognito auth — Sprint 3
- **T-GA-02**: Real Telebirr API — Sprint 3, depends on merchant credentials
- **T-GA-03**: Certificate pinning — Sprint 2
- **T-GA-04**: Git history purge — Sprint 2
- **OTP resend endpoint** — Sprint 3
- **Forgot password** — Sprint 3
- **DynamoDB table consolidation** — Future backlog
- **VeriFayda 2.0 OIDC** — Future sprint
- **SyncFunction refactor** — Future sprint (3,131 lines)

---

## Build Health

- **Server:** 0 errors, 0 warnings, 684 tests passing
- **Mobile:** 0 errors, 0 lint issues, 732 pass / 3 fail
- **Admin:** 0 errors, 27 E2E tests passing
- **Web:** Builds clean (Wasm warnings expected — Isar/ffi)

---

## Key File Locations

| Purpose | Path |
|---------|------|
| Sprint Plan | `specs/features/sprint-plan-2026-03-19.md` |
| AI Quick Start | `.ai/quick-start.md` |
| Constitution | `CLAUDE.md` |
| Current Mission | `TASK.md` |
| Standards | `.ai/standards.md` |
| Tech Stack | `.ai/tech_stack.md` |
| AOT Rules | `server/AOT_COMPLIANCE.md` |
| Completeness Rules | `.ai/completeness-rules.md` |
| Ethiopian Context | `docs/local_reality/README.md` |

---

## Deployment

- **API:** `https://vhhm8gh9c1.execute-api.eu-north-1.amazonaws.com`
- **Region:** eu-north-1 (Stockholm) — dev only; staging/prod remain me-south-1
- **Lambda:** 15 functions, ARM64 Native AOT
- **DynamoDB:** 12+ tables (on-demand billing)
- **Web:** CloudFront + S3 (webapp + admin + website)

---

**Last Updated:** 2026-03-19
