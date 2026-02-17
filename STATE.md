# STATE.md - Session Checkpoint

## Checkpoint

**Date:** 2026-02-16
**Branch:** `sync-first-feat`
**Status:** Phase R3 COMPLETE — Phase R4 next
**Latest Commit:** 0d68ab6 - docs: archive 27 historical/stale files

---

## Resumption Context

```
Phase R3 COMPLETE ✅ (Feb 15, 2026 — 2 weeks early)
  - Sync engine: Queue, executor, connectivity monitor, conflict resolver
  - Crop listing: Create + browse, fully localized (3 languages)
  - Server: 8 Lambda functions on AWS (me-south-1), 228 tests, 95ms cold start
  - Infra: 7 DynamoDB tables, API Gateway with 9 routes
  - Integration tests: 12+ passing, zero data loss validated

WHAT WORKS: Register/login (+251 phone), create crop offline, auto-sync,
  conflict resolution (server-wins), browse from server, filter by crop/location

NEXT: Phase R4 — Trip listings, delivery verification, Telebirr escrow
  Target: Apr 1 - Apr 30, 2026. Contracts needed first (see TASK.md).
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

- **F-01**: Git history purge (requires force-push maintenance window)
- **F-05**: Certificate pinning (deferred to R4)

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

**Last Updated:** 2026-02-16
