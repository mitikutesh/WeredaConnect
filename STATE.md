# STATE.md - Session Checkpoint

## Checkpoint

**Date:** 2026-02-17
**Branch:** `main`
**Status:** R4 Preparation — contracts written, deferred items planned
**Latest Commit:** f5d664f - Merge pull request #4 (sync-first-feat → main)

---

## Resumption Context

```
R4 PREPARATION IN PROGRESS (Feb 17, 2026)
  - R3 merged to main via PR #4 ✅
  - All 3 R4 contracts written ✅:
    - specs/contracts/trip-listing-api.md (v1.0.0)
    - specs/contracts/delivery-verification-api.md (v1.0.0)
    - specs/contracts/escrow-protocol.md (v1.0.0)
  - F-01 git purge: Runbook ready at docs/runbooks/f01-git-history-purge.md
  - F-05 cert pinning: Assessment + real cert hashes extracted at docs/runbooks/f05-cert-pinning-activation.md
    - Intermediate CA pin: sha256/vxRon/El5KuI4vx5ey1DgmsYmRY0nDd5Cg4GfJ8S+bg= (expires 2030)
    - Leaf cert pin: sha256/YmAL9HFVfF2eJQzG/+cPUiuxcVHBMQxOS28Z8J4H3Tw= (expires 2026-08-24)

WHAT WORKS: Register/login (+251 phone), create crop offline, auto-sync,
  conflict resolution (server-wins), browse from server, filter by crop/location

NEXT: Execute F-01 git purge (human maintenance window), then begin R4 implementation
  starting with F-11 Trip Listings. Target: Apr 1 - Apr 30, 2026.
```

---

## Key Decisions

- **Server-wins conflict resolution** — simplest for solo farmers (rare conflicts)
- **Composite idempotency keys** — `{userId}:{entityType}:{entityId}:{action}:{timestamp}`
- **Exponential backoff** — 5s, 15s, 45s (max 3 retries)
- **7-day TTL on SyncMetadata** — balances idempotency vs storage costs
- **Batch size 50** — completes in <5s on 3G

---

## Deferred Items

- **F-01**: Git history purge — runbook ready at `docs/runbooks/f01-git-history-purge.md`, awaiting human execution
- **F-05**: Certificate pinning — real cert hashes extracted, activation steps at `docs/runbooks/f05-cert-pinning-activation.md`

---

## Key File Locations

| Purpose | Path |
|---------|------|
| AI Quick Start | `.ai/quick-start.md` |
| Constitution | `CLAUDE.md` |
| Current Mission | `TASK.md` |
| Standards | `.ai/standards.md` |
| Tech Stack | `.ai/tech_stack.md` |
| AOT Rules | `server/AOT_COMPLIANCE.md` |
| Ethiopian Context | `docs/local_reality/README.md` |

---

## Deployment

- **API:** `https://s0om0hib4i.execute-api.me-south-1.amazonaws.com`
- **Region:** me-south-1 (Bahrain)
- **Lambda:** 8 functions, ARM64 Native AOT
- **DynamoDB:** 7 tables (on-demand billing)

---

**Last Updated:** 2026-02-17
