# PR Scaffold — S4-05: Connectivity-driven flush on regain

> **Status:** Design ready · **Depends on:** S4-01, S4-02 merged
> **Sprint:** HS-4 · **Epic:** E2 · **Closes HS-4**
> **Story catalog:** `specs/features/isar-hardening/sprints.md#s4-05`
> **Reviewer needed:** 1 mobile dev · **Story size:** 2 pts · **Risk:** Low

Listens for `Connectivity().onConnectivityChanged`. When the device
goes from offline to online AND the user has a valid access token,
triggers an immediate `SyncExecutor.flush()`.

---

## 0. TL;DR

Users on Ethiopian 2G/3G see frequent connectivity flaps. Today, queued
ops wait for the 15-minute WorkManager tick. This PR cuts the wait to
~1 second by triggering an immediate flush on connectivity regain.

---

## 1. How to ship this

```bash
git fetch origin
git log origin/main --oneline | grep -E 'S4-01|S4-02'

git checkout -b hs4/s4-05-connectivity-flush origin/main

# Apply:
#   - mobile/lib/core/sync/connectivity_monitor.dart  (extend; add flush trigger)
#   - mobile/lib/app/run_mobile.dart                  (subscribe at app start)
#   - mobile/test/core/sync/connectivity_monitor_test.dart  (new)

cd mobile && flutter analyze && flutter test
cd ..

git add ... && git commit -m "feat(mobile): flush sync queue on connectivity regain"
git push -u origin hs4/s4-05-connectivity-flush
gh pr create --title "feat(mobile): connectivity-driven flush [S4-05]"
```

---

## 2. PR description

### Summary

`ConnectivityMonitor` (extended) listens to
`Connectivity().onConnectivityChanged`. On transition to a non-`none`
result:

1. Check `auth.hasValidAccessToken()`. If false, skip (let the user
   re-authenticate first).
2. Acquire a flush-in-progress lock to prevent concurrent flushes.
3. Call `syncExecutor.flush()` (which session-gates per S4-01 and
   sends idempotency headers per S4-02).

Emits a Sentry breadcrumb with the trigger source (`timer`,
`resume`, `connectivity`).

### Why

Sync currently runs:
- Every 15 min via foreground Timer (existing)
- Every 15 min via WorkManager (after HS-5 S5-03)
- On app resume

15 minutes is the visible delay. Connectivity flap on 3G is the most
common case — user momentarily loses signal, regains it, and their
queued action sits idle for up to 14 more minutes. This PR makes it
~1 second instead.

### What changes

```diff
 class ConnectivityMonitor {
   StreamSubscription? _sub;
+  bool _isFlushing = false;
+
   void start() {
     _sub = Connectivity().onConnectivityChanged.listen((result) async {
-      // ... existing behavior
+      if (result == ConnectivityResult.none) return;
+      if (_isFlushing) return;
+      if (!await _auth.hasValidAccessToken()) return;
+      _isFlushing = true;
+      try {
+        _sentry?.addBreadcrumb('sync_trigger',
+            data: {'source': 'connectivity', 'result': result.toString()});
+        await _executor.flush();
+      } finally {
+        _isFlushing = false;
+      }
     });
   }

   Future<void> stop() async => _sub?.cancel();
 }
```

### What does NOT change

- The 15-min timer (still runs as a backstop).
- WorkManager (HS-5).
- The session gate inside `SyncExecutor` (S4-01 — still applies).

### Test plan

```bash
flutter test test/core/sync/connectivity_monitor_test.dart
```

Required:
1. Stream emits `wifi` → executor.flush called once
2. Stream emits `wifi` then immediately `mobile` → flush called once
   (lock prevents double-flush)
3. Stream emits `none` → no flush
4. `hasValidAccessToken == false` → no flush
5. Subscription cancelled on `stop()` — no listener leak

### Risk

Low. Single-flight lock prevents stampede. Worst case: a flap-prone
device flushes more often than necessary.

### Acceptance criteria

- [ ] Listener on `Connectivity().onConnectivityChanged`
- [ ] On transition to online: check `auth.hasValidAccessToken()`, then call `syncExecutor.flush()`
- [ ] Single-flight (no concurrent flushes if already running — `SyncExecutor._running` guard)
- [ ] Telemetry breadcrumb on each flush trigger source (timer / resume / connectivity)

---

## 3. Reviewer guide

1. **Subscription cleanup.** Confirm `ConnectivityMonitor.stop()` is
   called on app dispose (in the `WidgetsBindingObserver` lifecycle).
   Leaked subscriptions = leaked listeners across hot reloads.

2. **`_isFlushing` lock.** Both ConnectivityMonitor and SyncExecutor
   have single-flight guards. Belt-and-braces. Confirm there's no
   race where one fires while the other is mid-flight.

---

## 4. Rollback plan

Revert. The 15-min timer continues to be the sync trigger. Users see
the longer delay return.

---

## 5. Proposed diffs

Mostly in §"What changes." Plus a small wiring change in
`run_mobile.dart`:

```diff
+final connMonitor = ConnectivityMonitor(
+  auth: authService,
+  executor: syncExecutor,
+  sentry: SentryWrapper.instance,
+);
+connMonitor.start();
+// Cancel on dispose (via WidgetsBindingObserver)
```

---

## 6. Open questions

1. **What if `connectivity_plus` reports a stale "online" event?** Some
   Android OEMs do this. The flush executes but immediately fails
   with a timeout. Retry logic handles it. Acceptable.

2. **Should we also flush on app foreground?** Already in S4 sprint
   scope via the foreground Timer's "on resume" trigger. This PR
   adds connectivity as a third source. Verify no double-trigger
   when foreground+connectivity-regain happen together.

---

*PR scaffold v1.0 — 2026-05-13 — closes HS-4*
