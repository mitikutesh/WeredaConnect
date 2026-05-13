# PR Scaffold — S4-01: `SyncOperation.userId` field + session gate

> **Status:** Design ready · **Depends on:** HS-2 fully merged
> **Sprint:** HS-4 · **Epic:** E2 — Safe sync under flap & shared device
> **Story catalog:** `specs/features/isar-hardening/sprints.md#s4-01`
> **Reviewer needed:** security + 1 mobile dev · **Story size:** 5 pts · **Risk:** Medium

Closes the shared-device session-leak attack: today, queued mutations
from User-A can be POSTed under User-B's session token after a
logout/login on a shared device.

---

## 0. TL;DR

Add `userId` field to `SyncOperation` schema. Populate at enqueue time
from the active Cognito sub. In the executor, refuse to POST any
operation whose `userId` doesn't match the active session.

---

## 1. How to ship this

```bash
git fetch origin
git checkout -b hs4/s4-01-sync-operation-user-id origin/main

# Apply changes in §5:
#   - mobile/lib/core/models/sync_operation.dart  (+ userId field)
#   - mobile/lib/core/models/sync_operation.g.dart (regen)
#   - mobile/lib/core/sync/sync_queue.dart          (require userId at enqueue)
#   - mobile/lib/core/sync/sync_executor.dart       (session-gate before POST)
#   - mobile/test/core/sync/sync_executor_test.dart (add session-gate test)

cd mobile
dart run build_runner build --delete-conflicting-outputs
flutter analyze && flutter test
flutter build apk --release --dart-define=PHONE_PEPPER=$PHONE_PEPPER
cd ..

git add ... && git commit -m "feat(mobile): session-gate sync executor by SyncOperation.userId"
git push -u origin hs4/s4-01-sync-operation-user-id
gh pr create --title "feat(mobile): sync queue session gate [S4-01]"
```

---

## 2. PR description

### Summary

Adds `@Index() late String userId` to `SyncOperation`. `SyncQueue.enqueue`
now requires `userId` as a parameter (compile-fail without it).
`SyncExecutor._processOne` calls `auth.currentUserId()` and skips any
operation whose `userId` doesn't match.

### Why

A shared device (common in rural Ethiopia) is the realistic attack
surface here. Scenario:

1. User-A creates an order → queued offline in Isar
2. User-A logs out before the queue flushes
3. User-B logs in (same device, different account)
4. 15 min later WorkManager fires → `flush()` POSTs User-A's order
   under User-B's session token
5. Server creates the order, attributes it to User-B

`SyncOperation.userId` is the only line between this and a clean
shared-device experience.

### What changes

```diff
 @collection
 class SyncOperation {
   Id id = Isar.autoIncrement;

+  /// The Cognito sub that enqueued this operation. SyncExecutor
+  /// refuses to POST if currentUserId() != this value.
+  @Index() late String userId;
+
   @Index() late String entityType;
   // ... rest unchanged
 }
```

`SyncQueue.enqueue` gains a required `userId` parameter. Every existing
call site updates to pass it (or fail to compile — that's the
guarantee).

`SyncExecutor._processOne(op)`:
```dart
final activeUserId = await _auth.currentUserId();
if (activeUserId == null) return;       // no live session, skip
if (op.userId != activeUserId) {
  // Park the op. The reaper (S4-04) decides whether to drop it.
  return;
}
// ... proceed with POST as before
```

### What does NOT change

- The retry/backoff logic. Skipped ops stay `pending`; reaper deletes
  cross-user rows on app start (S4-04).
- The idempotency-key wiring (S4-02).
- Server-side: nothing — server trusts the JWT, not the request body's
  userId.

### Backwards compatibility

**Existing queue rows have no `userId`.** Two options:
- **A.** Treat absence as a wildcard (skip in executor — never POST).
  Reaper sweeps on startup. Default.
- **B.** Backfill on first launch after S4-01 ships: assume the
  current logged-in user owns all existing rows. Risky if the device
  switched users between when ops were queued and when S4-01 ships.

PO call: **A**. Don't auto-attribute. Reaper drops them; user-A
re-issues the action when they next log in.

### Test plan

```bash
cd mobile
flutter test test/core/sync/sync_executor_test.dart
flutter test                                  # full suite
flutter build web --no-tree-shake-icons       # check stub parity
```

Required cases:
1. Op with `userId == active` → POSTed normally
2. Op with `userId != active` → skipped (not POSTed); op stays `pending`
3. Op with `userId == null` (legacy row) → skipped
4. `auth.currentUserId()` returns null → executor early-returns; nothing posted

### Risk

Medium. Logic change is small but touches the critical sync path.

### Acceptance criteria

- [ ] `SyncOperation` gains `@Index() late String userId`
- [ ] `SyncQueue.enqueue(...)` requires `userId` (compile-fail without)
- [ ] `SyncExecutor._processOne(op)` calls `auth.currentUserId()` and returns early if mismatch
- [ ] Test "background flush refuses when userId != active session" green
- [ ] Telemetry breadcrumb (not error) when a skip occurs

---

## 3. Reviewer guide

1. **Compile-fail discipline.** `SyncQueue.enqueue` parameter is
   `required` (named). No default. Verify by trying to call it without
   `userId` — should fail `flutter analyze`.
2. **No silent skip.** Skipped ops emit a Sentry breadcrumb at `info`
   level so we can measure how often this happens in production.
3. **Empty-string vs. null `userId`.** Schema declares `late String`
   (non-null). Backfill of legacy rows should set `userId = ''` or
   delete the row outright. Default: delete (reaper handles this).

---

## 4. Rollback plan

`git revert <merge-commit>`. The field stays in the schema but no
caller uses it. Schema is additive — old code reading new-schema rows
just ignores the field.

---

## 5. Proposed diffs

### 5a. Schema

```diff
+@Index() late String userId;
```

### 5b. SyncQueue

```diff
 Future<SyncOperation> enqueue({
+  required String userId,
   required String entityType,
   required String entityId,
   required String action,
   required String data,
   required String operationId,
 }) async {
   // ...
   final op = SyncOperation()
+    ..userId = userId
     ..entityType = entityType
     // ...
   await _isar.writeTxn(() => _isar.syncOperations.put(op));
   return op;
 }
```

### 5c. SyncExecutor

```diff
 Future<void> _processOne(SyncOperation op) async {
+  final activeUserId = await _auth.currentUserId();
+  if (activeUserId == null) {
+    return; // no live session; will retry when user logs in
+  }
+  if (op.userId != activeUserId) {
+    _sentry?.addBreadcrumb('sync_op_user_mismatch',
+        data: {'op_user': op.userId, 'active': activeUserId, 'op_id': op.id});
+    return; // belongs to a different user; reaper will clean up
+  }
+
   await _isar.writeTxn(() async {
     op
       ..status = SyncStatus.processing
       ..lastAttemptAt = DateTime.now().toUtc();
     await _isar.syncOperations.put(op);
   });
   // ... POST logic unchanged
 }
```

### 5d. Test

```dart
test('refuses to POST op belonging to a different user', () async {
  await isar.writeTxn(() => isar.syncOperations.put(SyncOperation()
    ..userId = 'sub-A'
    ..entityType = 'CropListing'
    ..idempotencyKey = 'k1'
    ..payload = '{}'
    ..operation = SyncOp.create
    ..createdAt = DateTime.now().toUtc()));
  final auth = FakeAuth(currentUserId: 'sub-B');
  final exec = SyncExecutor(isar: isar, auth: auth, ...);
  await exec.flush();
  verifyNever(mockApi.create(any));
  final pending = await isar.syncOperations.where().findAll();
  expect(pending.first.status, equals(SyncStatus.pending)); // not processed
});
```

---

## 6. Open questions

1. **Schema additive vs. breaking.** `late String userId` is non-null;
   old rows without it will fail to read after schema regen. Use
   `String userId = ''` default to keep it additive. Default: do this.

2. **What if `currentUserId()` is slow on cold start?** Cache it in
   `SyncExecutor`. Default: call once per `flush()`, not per op.

---

*PR scaffold v1.0 — 2026-05-13*
