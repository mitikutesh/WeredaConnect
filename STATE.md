# STATE.md - Session Checkpoint

## Checkpoint

**Date:** 2026-03-19
**Branch:** `fix-feat`
**Status:** R4 COMPLETE — All features done. Sprint plan created for ship-ready + security + production.
**Latest Commit:** 0f2f0c6 - refactor(docs): restructure AI agent infrastructure with centralized completeness rules

---

## Resumption Context

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

- **API:** `https://b8xqjg4myb.execute-api.me-south-1.amazonaws.com`
- **Region:** me-south-1 (Bahrain)
- **Lambda:** 15 functions, ARM64 Native AOT
- **DynamoDB:** 12+ tables (on-demand billing)
- **Web:** CloudFront + S3 (webapp + admin + website)

---

**Last Updated:** 2026-03-19
