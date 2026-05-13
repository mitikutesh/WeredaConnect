# PR Scaffold — S4-03: Error classification (no token leaks)

> **Status:** Design ready · **Depends on:** — (parallel with S4-01, S4-02)
> **Sprint:** HS-4 · **Epic:** E2
> **Story catalog:** `specs/features/isar-hardening/sprints.md#s4-03`
> **Reviewer needed:** security + 1 mobile dev · **Story size:** 3 pts · **Risk:** Low

Renames `SyncOperation.lastError` → `lastErrorCode` and replaces
`e.toString()` with a `_classifyError(e)` mapping to opaque codes.
Stops bearer tokens from being persisted to Isar and forwarded to
Sentry.

---

## 0. TL;DR

`SyncOperation.lastError` was a free-text field set to `e.toString()`.
Dio exceptions carry URLs, headers (potentially with
`Authorization: Bearer ...`), and response body snippets. That string
gets persisted to Isar and (once S6-01 lands) forwarded to Sentry.
Net effect: credential leak via crash reporting.

This PR maps every known exception type to an opaque code:
`network_timeout`, `http_401_unauthenticated`, etc. Raw exception text
never persists.

---

## 1. How to ship this

```bash
git fetch origin
git checkout -b hs4/s4-03-error-classification origin/main

# Apply changes in §5:
#   - mobile/lib/core/models/sync_operation.dart     (rename field)
#   - mobile/lib/core/models/sync_operation.g.dart   (regen)
#   - mobile/lib/core/sync/sync_executor.dart         (add _classifyError)
#   - mobile/lib/core/database/migrations/migrate_lasterror_to_lasterrorcode.dart (new)
#   - mobile/test/core/sync/sync_executor_test.dart  (add classifyError tests)

cd mobile
dart run build_runner build --delete-conflicting-outputs
flutter analyze && flutter test
cd ..

git add ... && git commit -m "feat(mobile): opaque error codes on SyncOperation, no token leaks"
git push -u origin hs4/s4-03-error-classification
gh pr create --title "feat(mobile): classify sync errors to opaque codes [S4-03]"
```

---

## 2. PR description

### Summary

1. Rename `SyncOperation.lastError` → `lastErrorCode`.
2. Add `SyncExecutor._classifyError(Object e) → String` that maps to
   a small fixed set of opaque codes.
3. Catch site in `_processOne` writes the code, never `e.toString()`.
4. Migration step: existing rows with non-null `lastError` get
   `lastErrorCode = 'unknown_legacy_error'`.

### Why

`SyncOperation.lastError = e.toString();` today serializes Dio
exceptions verbatim. Dio's `DioException.toString()` returns lines
like:
```
DioException [bad response]: request details (with Authorization: Bearer eyJhbGciOiJSUzI1...) ...
```

That string lands in Isar. Once Sentry wiring ships (S6-01), it
propagates to crash reporting too. Bearer tokens in third-party log
sinks are a credential leak. Opaque codes carry no PII.

### What changes

```diff
 @collection
 class SyncOperation {
   // ...
   String? lastErrorCode;
-  String? lastError;
 }
```

```dart
// SyncExecutor
String _classifyError(Object e) {
  if (e is TimeoutException) return 'network_timeout';
  if (e is SocketException) return 'network_unreachable';
  if (e is DioException) {
    final code = e.response?.statusCode;
    if (code == null) return 'network_error';
    if (code == 401) return 'http_401_unauthenticated';
    if (code == 403) return 'http_403_forbidden';
    if (code == 409) return 'http_409_conflict';
    if (code >= 500) return 'http_5xx_server';
    return 'http_${code}';
  }
  if (e is FormatException) return 'parse_error';
  return 'unknown_error';
}
```

```diff
 } on Object catch (e) {
   await _isar.writeTxn(() async {
     op
       ..status = op.canRetry ? SyncStatus.pending : SyncStatus.failed
       ..retryCount += 1
-      ..lastError = e.toString();
+      ..lastErrorCode = _classifyError(e);
     await _isar.syncOperations.put(op);
   });
 }
```

### What does NOT change

- Retry logic.
- Server-side anything.
- Other persistence layers — only `SyncOperation` carried this field.

### Backwards compatibility

A one-shot migration sets `lastErrorCode = 'unknown_legacy_error'`
for any row that has a non-null `lastError`. The old field can be
dropped in a follow-up PR after one sprint.

### Test plan

```bash
cd mobile
flutter test test/core/sync/sync_executor_test.dart
flutter test
```

Required: tests that simulate each `_classifyError` branch and assert
the stored `lastErrorCode` matches.

The token-leak test from `docs/mobile/isar-implementation.md` §15:
```dart
test('lastErrorCode never contains bearer tokens', () async {
  final exec = SyncExecutor(...);
  final err = DioException(
    response: Response(
      statusCode: 401,
      data: 'Unauthorized — token Bearer eyJhbGciOi...',
      requestOptions: RequestOptions(path: '/x'),
    ),
    requestOptions: RequestOptions(path: '/x'),
  );
  final code = exec.classifyErrorForTesting(err);
  expect(code, equals('http_401_unauthenticated'));
  expect(code, isNot(contains('Bearer')));
  expect(code, isNot(contains('eyJ')));
});
```

### Risk

Low. The mapping is mechanical; the migration is trivial.

### Acceptance criteria

- [ ] `_classifyError(Object e)` returns one of the listed codes
- [ ] No raw exception string ever assigned to `lastErrorCode`
- [ ] Test: simulate Dio 401 with token in error message → stored code is `'http_401_unauthenticated'`, contains no `Bearer` / `eyJ`
- [ ] Existing `lastError` field migrated to `lastErrorCode` (one-shot)

---

## 3. Reviewer guide

1. **`@visibleForTesting` on `_classifyError`.** Required so the test
   can call it directly without setting up a full failing POST.
2. **Default branch is `'unknown_error'`, not `e.toString()`.** Triple-
   check the catch site.

---

## 4. Rollback plan

Revert. The `lastError` field returns; rows lose their error context
written under S4-03. No production impact.

---

## 5. Proposed diffs

See §2. Migration:

```dart
// mobile/lib/core/database/migrations/migrate_lasterror_to_lasterrorcode.dart
class MigrateLastErrorToLastErrorCode {
  final Isar _isar;
  MigrateLastErrorToLastErrorCode(this._isar);

  Future<void> run() async {
    final ops = await _isar.syncOperations.where().findAll();
    for (final op in ops) {
      if (op.lastError != null && op.lastErrorCode == null) {
        op.lastErrorCode = 'unknown_legacy_error';
        await _isar.syncOperations.put(op);
      }
    }
  }
}
```

Register in the migrations runner under `_runV4` (or whichever version
slot HS-4 uses).

---

## 6. Open questions

1. **Drop `lastError` field entirely or leave deprecated?** Default:
   leave deprecated for one sprint, then drop in a follow-up.

2. **More codes?** The set is small (8 codes). For granular metrics
   we might want `http_2xx_success` (impossible but…), `network_dns`,
   etc. Default: ship lean, expand if Sentry shows clustering on
   `unknown_error`.

---

*PR scaffold v1.0 — 2026-05-13*
