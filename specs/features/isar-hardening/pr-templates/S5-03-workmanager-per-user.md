# PR Scaffold — S5-03: WorkManager per-user binding

> **Status:** Design ready · **Depends on:** S4-01 merged
> **Sprint:** HS-5 · **Epic:** E3
> **Story catalog:** `specs/features/isar-hardening/sprints.md#s5-03`
> **Reviewer needed:** 1 mobile dev · **Story size:** 3 pts · **Risk:** Medium

Replaces the global WorkManager periodic task with one per-user task,
re-registered with `ExistingWorkPolicy.replace` on every login. Closes
the "worker registered under User-A keeps firing for User-B after re-
login on a shared device" gap.

---

## 0. TL;DR

`BackgroundSync.register(userId)` — task ID is `wereda_bg_sync_<userId>`,
policy is `replace`. `BackgroundSync.cancelForUser(userId)` runs on
logout. The callback re-validates `userId` + access-token freshness
before flushing.

---

## 1. How to ship this

```bash
git fetch origin
git log origin/main --oneline | grep S4-01

git checkout -b hs5/s5-03-workmanager-per-user origin/main

# Apply:
#   - mobile/lib/core/sync/background_sync.dart           (refactor)
#   - mobile/lib/features/auth/bloc/auth_bloc.dart        (register on login, cancel on logout)
#   - mobile/test/core/sync/background_sync_test.dart     (new — limited; WM isn't unit-testable)

cd mobile && flutter analyze && flutter test
cd ..

git add ... && git commit -m "feat(mobile): WorkManager per-user task binding"
git push -u origin hs5/s5-03-workmanager-per-user
gh pr create --title "feat(mobile): WorkManager per-user binding [S5-03]"
```

---

## 2. PR description

### Summary

`BackgroundSync.register(userId)`:
- Task ID: `wereda_bg_sync_<userId>` (per-user unique)
- `existingWorkPolicy: ExistingWorkPolicy.replace`
- Same constraints (network connected, battery not low, 15-min freq)

`BackgroundSync.cancelForUser(userId)` calls
`Workmanager().cancelByUniqueName('wereda_bg_sync_<userId>')`.

Wired into:
- `auth_bloc.dart` on `AuthLoginSuccess` → register
- `auth_bloc.dart` on `AuthLogoutRequested` → cancel

Callback dispatcher (`@pragma('vm:entry-point')`) gains a userId
check: read the current Cognito sub from `auth.currentUserId()`; if
null or mismatch with the task's intended userId, no-op.

### Why

Today's `WorkManager.registerPeriodicTask('wereda_bg_sync', ...,
existingWorkPolicy: ExistingWorkPolicy.keep)` registers once and
keeps the task across user switches. After User-A registers and then
User-B logs in, the worker continues firing — and although S4-01's
session gate blocks the actual POST, the worker still:
- Opens Isar with whoever's key is current
- Runs the flush logic (which then no-ops)

Per-user binding is belt-and-braces: each user has their own task,
cancelled on logout, replaced on login. The worker never runs for a
user who isn't logged in.

### What changes

```diff
 class BackgroundSync {
-  static Future<void> initialize() async {
+  /// Register the periodic worker for the given user.
+  /// Call on every successful login. `replace` policy ensures the
+  /// worker is bound to the *current* user, not whoever first
+  /// registered it on this device.
+  static Future<void> register({required String userId}) async {
     await Workmanager().initialize(callbackDispatcher);
     await Workmanager().registerPeriodicTask(
-      'wereda_bg_sync',
+      'wereda_bg_sync_$userId',
       'periodic_sync',
       frequency: const Duration(minutes: 15),
       constraints: Constraints(
         networkType: NetworkType.connected,
         requiresBatteryNotLow: true,
       ),
-      existingWorkPolicy: ExistingWorkPolicy.keep,
+      existingWorkPolicy: ExistingWorkPolicy.replace,
+      inputData: {'userId': userId},
     );
   }

+  static Future<void> cancelForUser(String userId) =>
+      Workmanager().cancelByUniqueName('wereda_bg_sync_$userId');
+
+  static Future<void> cancelAll() => Workmanager().cancelAll();
 }
```

Callback:

```diff
 @pragma('vm:entry-point')
 void callbackDispatcher() {
   Workmanager().executeTask((taskName, inputData) async {
     if (taskName != 'periodic_sync') return true;
+    final expectedUserId = inputData?['userId'] as String?;
     try {
       await IsarService.instance.initialize();
+      final auth = AuthServiceFactory.build();
+      final currentUserId = await auth.currentUserId();
+      if (expectedUserId != null && currentUserId != expectedUserId) {
+        // The user who registered this task is no longer signed in.
+        // Cancel ourselves to stop firing.
+        await Workmanager().cancelByUniqueName('wereda_bg_sync_$expectedUserId');
+        await IsarService.instance.close();
+        return true;
+      }
+      if (currentUserId == null || !await auth.hasValidAccessToken()) {
+        await IsarService.instance.close();
+        return true;
+      }
       final executor = SyncExecutorFactory.build();
       await executor.flush();
+      await IsarService.instance.close();
       return true;
     } catch (_) {
       return false;
     }
   });
 }
```

### What does NOT change

- The 15-minute cadence.
- The flush logic itself.
- The Constraints (network, battery).

### Backwards compatibility

Existing installs have a worker registered under `'wereda_bg_sync'`
(no user suffix). Two approaches:

- **A.** Migration: on first launch after S5-03, call
  `cancelByUniqueName('wereda_bg_sync')` (old task ID) and register
  the new per-user task. Default.
- **B.** Let the old task linger; it no-ops via the userId check.

**A** is cleaner. PO call.

### Test plan

WorkManager is hard to unit-test (it's an Android system service).
What we can test:
1. `BackgroundSync.register(userId: 'sub-A')` → mock Workmanager
   plugin records the call with task ID `wereda_bg_sync_sub-A`
2. `cancelForUser('sub-A')` → mock records `cancelByUniqueName`
3. Callback dispatcher logic without WorkManager: extract the
   "what to do" function and unit-test it directly

**Manual on a real device:**
- Login as User-A → adb shell `dumpsys jobscheduler | grep wereda` →
  one job with `wereda_bg_sync_sub-A`
- Logout → `dumpsys` again → no wereda jobs
- Login as User-B → `dumpsys` → one job with `wereda_bg_sync_sub-B`

### Risk

Medium. WorkManager behavior diverges between Android versions and
OEM skins. Mitigations:
- Test on at least 3 devices (Tecno Spark 8, Itel A23 Pro, Samsung A04)
- Aikido scan; cross-platform CI
- The S4-01 session gate inside `SyncExecutor` is the runtime
  belt-and-braces — even if the worker fires under a stale user, it
  no-ops at flush time

### Acceptance criteria

- [ ] `BackgroundSync.register(userId)` uses task ID `wereda_bg_sync_<userId>`
- [ ] `existingWorkPolicy: ExistingWorkPolicy.replace`
- [ ] `BackgroundSync.cancelForUser(userId)` cancels by unique name
- [ ] Called on every successful login + on every logout
- [ ] WorkManager callback re-validates `userId` and `hasValidAccessToken()` before flushing

---

## 3. Reviewer guide

1. **The `userId` is passed via `inputData`.** Confirm it's not also
   read from any global state inside the dispatcher — the dispatcher
   runs in a separate isolate with no shared state.

2. **Cancel-self on stale-user.** When the dispatcher detects a
   user-id mismatch, it cancels its own task before returning. This
   is the cleanup. Confirm the cancel call uses the *expected*
   user-id (from `inputData`), not the *current* user-id.

---

## 4. Rollback plan

Revert. The old single-task model returns. S4-01 session gate still
protects against cross-user POSTs at the flush boundary.

---

## 5. Proposed diffs

See §"What changes."

---

## 6. Open questions

1. **What if a user has TWO active sessions?** Not possible today
   (single-device-login per server-side check). If that changes,
   per-user task ID still scales: register one per device-userId
   pair.

2. **Multi-user devices.** A family-shared phone with three users
   logging in/out: three tasks at peak. Acceptable.

---

*PR scaffold v1.0 — 2026-05-13*
