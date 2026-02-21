# F-15: Conflict Resolution UI — Security Review

**Status**: APPROVED_WITH_CHANGES
**Date**: 2026-02-21
**Reviewer**: Security Review Agent
**Reviewed Document**: `specs/decisions/f15-conflict-resolution-ui.md`

---

## Verdict

The F-15 architecture is **sound and appropriate** for the threat model (rural Ethiopian farmers, local device storage, trusted server). The "notify after resolve" pattern correctly separates data integrity (ConflictResolver) from UI notification (ConflictNotificationService), meaning the notification layer cannot corrupt data.

Two issues require changes before implementation. Four additional low-severity findings are documented for awareness.

---

## Required Changes Before Implementation

### Finding 1 — Broadcast stream can silently swallow conflict events (Medium)

**Category**: Stream Safety
**Severity**: Medium
**Location**: `ConflictNotificationService` (proposed), `main.dart` wiring

**Issue**: `StreamController<ConflictEvent>.broadcast()` does not buffer events. If `ConflictBloc` has not yet subscribed when `notify()` is called, the event is lost silently. The proposed startup order creates a timing window:

1. `main()` creates `ConflictNotificationService` and `SyncExecutor` before `runApp()`
2. `SyncExecutor.start()` triggers an immediate `syncNow()` (via `SyncWorker.start()`)
3. `ConflictBloc` is created inside `MultiBlocProvider` in the widget tree (after `runApp()`)

If connectivity is available at app launch and sync completes before the widget tree is built, conflict notifications are silently lost. The data is still resolved correctly (server wins), but the user never sees the notification.

**Recommended Fix**: Buffer events in `ConflictNotificationService` until a subscriber connects. Replace the bare broadcast controller with a small replay buffer:

```dart
class ConflictNotificationService {
  final StreamController<ConflictEvent> _controller =
      StreamController<ConflictEvent>.broadcast();

  final List<ConflictEvent> _pendingEvents = [];
  bool _hasSubscriber = false;

  Stream<ConflictEvent> get onConflict {
    _hasSubscriber = true;
    // Replay any buffered events on first subscription
    if (_pendingEvents.isNotEmpty) {
      for (final event in _pendingEvents) {
        _controller.add(event);
      }
      _pendingEvents.clear();
    }
    return _controller.stream;
  }

  void notify(ConflictEvent event) {
    if (_controller.isClosed) return;
    if (_hasSubscriber) {
      _controller.add(event);
    } else {
      _pendingEvents.add(event);
    }
  }

  void dispose() {
    _pendingEvents.clear();
    _controller.close();
  }
}
```

This avoids adding rxdart as a dependency and keeps the solution minimal.

---

### Finding 2 — Potential `RangeError` crash in `_extractDisplayFields` (Medium)

**Category**: Data Integrity / Crash Safety
**Severity**: Medium
**Location**: `ConflictResolutionScreen._extractDisplayFields()` (proposed)

**Issue**: In the delivery case:

```dart
'Trip': data['tripId']?.toString().substring(0, 8) ?? '-',
```

If `tripId` is non-null but shorter than 8 characters, `substring(0, 8)` throws a `RangeError`, crashing the bottom sheet. The `?? '-'` fallback only covers the `null` case from the `?.` operator, not a short string.

**Recommended Fix**: Use a safe substring helper or inline length check:

```dart
'Trip': () {
  final id = data['tripId']?.toString();
  if (id == null) return '-';
  return id.length > 8 ? id.substring(0, 8) : id;
}(),
```

Or apply the same pattern used for `entityId` in `_buildEntityBadge` (line 501–502 of the arch doc), which already handles this correctly:

```dart
final shortId = conflict.entityId.length > 8
    ? conflict.entityId.substring(0, 8)
    : conflict.entityId;
```

---

## Advisory Findings (No Changes Required)

### Finding 3 — Multiple bottom sheets can stack on batch conflicts (Low)

**Category**: UX / Denial of Service
**Severity**: Low

**Issue**: If a sync batch contains multiple conflicting operations, `ConflictBloc` will emit multiple `ConflictPending` states in rapid succession. Each triggers `showModalBottomSheet` in the `AuthGate` listener, stacking dialogs on top of each other.

**Risk Assessment**: Conflicts are rare in the Ethiopian context (most users work solo on one device). Multiple simultaneous conflicts within one batch are extremely unlikely. Even if they occur, the user can dismiss each dialog — no data issue, just a UX annoyance.

**Future Mitigation (optional)**: Queue events in `ConflictBloc` and show them one at a time, advancing to the next event when the user dismisses the current dialog.

---

### Finding 4 — ConflictEvent stream is informational only — no injection risk (Low)

**Category**: Data Integrity
**Severity**: Low

**Issue Investigated**: Can a malicious local actor manipulate the `ConflictEvent` stream to inject false server data?

**Finding**: No practical risk. The `ConflictNotificationService` is internal to the app process. Only code with a direct reference to the service instance can call `notify()`. On a non-rooted Android device, this is inaccessible to external actors. More importantly, `ConflictEvent` is purely informational — it triggers a display-only dialog. All actual data writes happen in `ConflictResolver.resolve()` before the notification fires. Injecting a fake event would show a misleading dialog but cannot corrupt Isar data.

---

### Finding 5 — serverData is appropriately filtered for display (Low)

**Category**: Information Disclosure
**Severity**: Low

**Issue Investigated**: Does `ConflictResolutionScreen` expose data the current user shouldn't see?

**Finding**: No. Two protections are in place:
1. Conflicts only fire for the current user's own sync operations (the `SyncQueue` is scoped to the authenticated session).
2. `_extractDisplayFields()` applies a whitelist per entity type — only specific fields (crop type, price, status, route) are shown. Raw JSON is never dumped to the UI. Internal IDs are truncated to 8 characters.

The only cross-user scenario would require a bug in session management (one user's auth token syncing another user's entities), which is outside the scope of this feature.

---

### Finding 6 — ConflictResolver.resolve() is idempotent (None)

**Category**: Idempotency
**Severity**: None

**Issue Investigated**: Does calling `resolve()` twice on the same entity cause data corruption?

**Finding**: No. The method is fully idempotent. It finds the entity by ID, overwrites all fields with the server's authoritative values, and sets the version number. Calling it N times with the same inputs produces the same Isar state. The write transaction is atomic — no partial updates are possible.

---

### Finding 7 — Optional `ConflictNotificationService` param is null-safe (None)

**Category**: Injection Safety
**Severity**: None

**Issue Investigated**: Is passing `ConflictNotificationService?` as an optional constructor parameter to `SyncExecutor` safe?

**Finding**: Yes. Dart's null safety guarantees that `_conflictNotificationService?.notify(...)` is a no-op when the service is null. There is no null pointer risk. The optional pattern preserves backward compatibility with existing `SyncExecutor` instantiations (e.g., in tests) that don't use conflict notifications.

---

## Summary Table

| # | Finding | Severity | Action |
|---|---------|----------|--------|
| 1 | Broadcast stream can swallow events before BLoC subscribes | Medium | **Required** — add replay buffer |
| 2 | `substring(0, 8)` crash on short `tripId` | Medium | **Required** — add length guard |
| 3 | Multiple bottom sheets can stack | Low | Advisory — optional queue |
| 4 | ConflictEvent stream injection | Low | No risk — informational only |
| 5 | Information disclosure in UI | Low | No risk — whitelist filtering |
| 6 | ConflictResolver idempotency | None | Confirmed safe |
| 7 | Optional param null safety | None | Confirmed safe |

---

## Conclusion

The architecture is well-designed for the threat model. The separation between data resolution (ConflictResolver) and UI notification (ConflictNotificationService) is the correct pattern — it ensures that the notification layer can never corrupt data, and that all security-critical operations (Isar writes, version updates) happen in the existing, tested code path.

The two required changes are straightforward implementation fixes (< 10 lines each) that prevent a missed-notification bug and a potential crash. Neither requires architectural changes.

**Approved with changes** — implement findings #1 and #2 before merging.

---

**Last Updated**: 2026-02-21
