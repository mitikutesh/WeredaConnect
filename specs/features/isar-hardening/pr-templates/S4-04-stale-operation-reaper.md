# PR Scaffold — S4-04: Stale-operation reaper on app start

> **Status:** Design ready · **Depends on:** S4-01 merged
> **Sprint:** HS-4 · **Epic:** E2
> **Story catalog:** `specs/features/isar-hardening/sprints.md#s4-04`
> **Reviewer needed:** 1 mobile dev · **Story size:** 2 pts · **Risk:** Low

Drops `SyncOperation` rows whose `userId` doesn't match the active
session, on every app start. The session gate (S4-01) is the runtime
defense; this is the housekeeping that keeps orphan rows from
piling up.

---

## 0. TL;DR

`SyncQueue.reapStaleOperations(currentUserId)` deletes all
`SyncOperation` rows where `userId != currentUserId`. Called once on
app startup after `IsarService.initialize()` and after the user's
session resolves.

---

## 1. How to ship this

```bash
git fetch origin
git log origin/main --oneline | grep S4-01     # confirm S4-01 merged

git checkout -b hs4/s4-04-stale-op-reaper origin/main

# Apply:
#   - mobile/lib/core/sync/sync_queue.dart      (+ reapStaleOperations method)
#   - mobile/lib/app/run_mobile.dart            (call it after login)
#   - mobile/test/core/sync/sync_queue_test.dart (extend)

cd mobile && flutter test && flutter analyze
cd ..

git add ... && git commit -m "feat(mobile): reap stale SyncOperation rows on app start"
git push -u origin hs4/s4-04-stale-op-reaper
gh pr create --title "feat(mobile): stale-op reaper [S4-04]"
```

---

## 2. PR description

### Summary

`SyncQueue` gains:
```dart
Future<int> reapStaleOperations(String currentUserId) async {
  final stale = await _isar.syncOperations
      .filter().not().userIdEqualTo(currentUserId).findAll();
  await _isar.writeTxn(() async {
    for (final op in stale) {
      await _isar.syncOperations.delete(op.id);
    }
  });
  return stale.length;
}
```

Called from `run_mobile.dart` after `IsarService.initialize()` and
after `auth` resolves the current user:

```dart
final userId = await authService.currentUserId();
if (userId != null) {
  final reaped = await syncQueue.reapStaleOperations(userId);
  if (reaped > 0) {
    debugPrint('[sync] reaped $reaped stale ops');
    // S6-01: Sentry breadcrumb
  }
}
```

### Why

S4-01 protects against POSTing under the wrong session at flush time.
But the rows still pile up in the queue forever. Two reasons to drop
them:

1. **Storage.** A device passed between several users accumulates
   ops from all of them. Unbounded growth on low-storage devices.
2. **Safety.** A row that sits forever is a row that *could* be
   posted if the session gate ever has a bug. Belt-and-braces.

### What changes

- `mobile/lib/core/sync/sync_queue.dart` — `reapStaleOperations`
  method.
- `mobile/lib/app/run_mobile.dart` — call it on startup.
- Test: seed mixed-user ops, login as one, assert only that user's
  ops remain.

### What does NOT change

- The session gate (S4-01). Both layers exist in parallel.

### Backwards compatibility

Pre-S4-01 rows have no `userId`. Two policy choices:
- **A.** Treat empty `userId` as a wildcard (skip reaping). Default.
- **B.** Reap empty-`userId` rows too. Risk: legitimate pre-S4-01 work
  gets dropped.

PO call: **A**. The session gate (S4-01) already skips empty-userId
ops at POST time, so they don't post anyway. Reaping happens slowly
as users hit the app post-S4-01.

### Test plan

```bash
flutter test test/core/sync/sync_queue_test.dart
```

Required:
1. Seed 3 ops for `sub-A` and 2 ops for `sub-B`. Reap with
   `currentUserId = 'sub-A'`. Assert 3 remain (all `sub-A`).
2. Empty-userId ops: reap with `currentUserId = 'sub-A'`. Assert
   they survive (policy A).
3. Empty active userId: reap with `currentUserId = null`. The caller
   should skip the reap entirely (no-op).

### Risk

Low. Hard-delete is destructive but only on rows the active session
shouldn't see anyway.

### Acceptance criteria

- [ ] `reapStaleOperations` deletes all rows where `userId != currentUserId`
- [ ] Pre-S4-01 rows (empty `userId`) survive (policy A)
- [ ] Called from `run_mobile.dart` after `IsarService.initialize()` and after login resolved
- [ ] Logged at info level for ops visibility

---

## 3. Reviewer guide

1. **Hard-delete vs. soft-delete.** Stale ops are deleted outright.
   No undo. If a user logs back in after several days, their queued
   ops are gone. Confirm this is acceptable (it is — they can
   re-issue the action).

2. **The reap loop is inside a single `writeTxn`.** Atomic — partial
   reap doesn't happen.

---

## 4. Rollback plan

Revert. Stale ops accumulate again; S4-01 session gate still prevents
them from posting.

---

## 5. Proposed diffs

See §"What changes." One method, one call site, one test group.

---

## 6. Open questions

1. **Time-of-day for the reap.** Currently on every app start. For
   users who reopen the app many times per day, that's wasteful.
   Default: every start is fine — query is cheap (filter on indexed
   userId).

2. **Should reap also clear orphan `SyncOperation` rows whose
   `entityType` references a deleted entity?** Out of scope. Track
   as separate cleanup story.

---

*PR scaffold v1.0 — 2026-05-13*
