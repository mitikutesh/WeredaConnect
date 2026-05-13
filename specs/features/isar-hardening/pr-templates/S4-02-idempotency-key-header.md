# PR Scaffold — S4-02: Idempotency-Key header end-to-end

> **Status:** Design ready · **Depends on:** — (parallel with S4-01, S4-03)
> **Sprint:** HS-4 · **Epic:** E2
> **Story catalog:** `specs/features/isar-hardening/sprints.md#s4-02`
> **Reviewer needed:** security + payments-aware mobile dev
> **Story size:** 5 pts · **Risk:** High (Telebirr escrow exposure)

Ensures the `Idempotency-Key` HTTP header makes it from the
`SyncOperation` row to the server on every retry — and is **never**
regenerated. Without this, a network flap mid-Telebirr-charge can
double-charge a buyer.

---

## 0. TL;DR

The server already supports idempotency (`EscrowIdempotencyRepository`,
`ISyncMetadataRepository.IdempotencyKeyExistsAsync`). The client just
has to use it correctly:

1. Generate the key **once** at enqueue time (already done by
   `idempotency_key_generator.dart`).
2. Persist the key in `SyncOperation.idempotencyKey` (already done).
3. Send the key as the `Idempotency-Key` HTTP header on every POST/PUT/
   DELETE inside `SyncExecutor` (this PR).
4. On retry, reuse the same key. Never regenerate from `DateTime.now()`.

---

## 1. How to ship this

```bash
git fetch origin
git checkout -b hs4/s4-02-idempotency-key-header origin/main

# Apply changes in §5:
#   - mobile/lib/core/network/api_client.dart    (add Idempotency-Key header param)
#   - mobile/lib/core/sync/sync_executor.dart    (pass op.idempotencyKey through)
#   - mobile/test/core/sync/idempotency_test.dart (new — retry stability)

cd mobile
flutter analyze && flutter test
cd ..

git add ... && git commit -m "feat(mobile): Idempotency-Key header end-to-end"
git push -u origin hs4/s4-02-idempotency-key-header
gh pr create --title "feat(mobile): Idempotency-Key header end-to-end [S4-02]"
```

---

## 2. PR description

### Summary

`ApiClient.post/put/delete` accepts an optional `idempotencyKey`
parameter. When set, it's sent as the `Idempotency-Key` HTTP header.
`SyncExecutor._processOne` passes `op.idempotencyKey` through on every
attempt of every op.

Adds a property-style test asserting that a `SyncOperation` row's
`idempotencyKey` does not change across retries.

### Why

Telebirr escrow charges are real money. Without idempotency, the
following happens on flaky 3G (common in rural Ethiopia):

1. Mobile POSTs `create order` with body `{amount: 500 ETB, ...}`
2. Server receives request, creates the order, sends 200 response
3. Network drops the response before mobile receives it
4. Mobile sees a timeout, retries
5. Server creates a SECOND order — buyer charged twice

The server's `EscrowIdempotencyRepository` table dedupes by
`idempotencyKey`. Mobile must send the same key on every retry of the
same `SyncOperation` row. If mobile generates a new key per retry
(e.g., from `DateTime.now()`), the dedup table sees a brand-new key
and creates a second order.

### What changes

- **`mobile/lib/core/network/api_client.dart`** — every method that
  mutates server state (`post`, `put`, `delete`) gains an optional
  `idempotencyKey` parameter. When provided, set
  `headers['Idempotency-Key'] = idempotencyKey`.
- **`mobile/lib/core/sync/sync_executor.dart`** — every call that goes
  through `ApiClient` from `_processOne` passes `op.idempotencyKey`.
- **`mobile/test/core/sync/idempotency_test.dart`** — new test file
  that exercises retry stability (see §5c).
- **`mobile/test/core/sync/idempotency_key_generator_test.dart`** —
  audit/add tests if missing. The generator is presumed to exist; if
  it doesn't, this PR also adds it.

### What does NOT change

- The server side. Per `server/src/Functions/SyncFunction.cs:328` and
  `server/src/Data/EscrowIdempotencyRepository.cs`, the contract is
  already in place.
- The format of the idempotency key: still
  `{userId}:{entityType}:{entityId}:{action}:{timestamp}`.
- The retry/backoff logic. Same retries, same timing — just with the
  same idempotency key.

### Backwards compatibility

Server already accepts the header (existing flow). Client adds it; any
client build without the header still gets dedup *if* the server
constructs a key from request fields — verify by checking the server
handler.

### Test plan

```bash
cd mobile
flutter test test/core/sync/idempotency_test.dart
flutter test                                  # full suite

# Staging integration test (manual)
# 1. Enqueue an order op; intercept the POST mid-flight with a
#    network proxy
# 2. Mobile retries, gets through on attempt 2
# 3. Inspect server DynamoDB → exactly one row in EscrowOperations
#    with that idempotencyKey
```

### Threat model

| Threat | Pre-PR | Post-PR |
|---|---|---|
| Duplicate Telebirr charge from flaky network | Possible (no idempotency header sent) | Prevented by server-side dedup |
| Client generates new key per retry | Possible regression | Tests catch it |

### Risk

High. Telebirr-money-loss territory. Mitigation: the property test in
§5c is non-negotiable — it asserts that calling `enqueue` once and
re-reading the row N times yields the same `idempotencyKey`.

### Acceptance criteria

- [ ] Idempotency key generated once at `enqueue` time, stored on the `SyncOperation` row
- [ ] Retry uses the same key — never regenerated from `DateTime.now()`
- [ ] `Idempotency-Key: {userId}:{entityType}:{entityId}:{action}:{ts}` header on every POST/PUT/DELETE
- [ ] Unit test asserts key stability across simulated retries
- [ ] Integration test against staging: kill mid-POST, retry → server's `EscrowIdempotencyRepository` shows single row

---

## 3. Reviewer guide

1. **Grep for `DateTime.now()` near anything `idempotencyKey`.** Should
   appear only in the generator at row-creation time, never inside
   `SyncExecutor.flush` / `_processOne`.
2. **Header name is exactly `Idempotency-Key`.** Servers are
   case-insensitive but `Idempotency-Key` is the convention.
3. **All three verbs.** POST, PUT, and DELETE all need the header.
   GET doesn't (no mutation).

---

## 4. Rollback plan

`git revert <merge-commit>`. Header stops being sent. Server still
gets requests, but without dedup — exactly the pre-PR state.

---

## 5. Proposed diffs

### 5a. ApiClient

```diff
 Future<Response> post(String path, dynamic body, {
+  String? idempotencyKey,
   Map<String, String>? headers,
 }) async {
+  final mergedHeaders = {...?headers};
+  if (idempotencyKey != null) {
+    mergedHeaders['Idempotency-Key'] = idempotencyKey;
+  }
-  return _dio.post(path, data: body, options: Options(headers: headers));
+  return _dio.post(path, data: body, options: Options(headers: mergedHeaders));
 }
```

Same pattern for `put` and `delete`.

### 5b. SyncExecutor

```diff
 case SyncOp.create:
-  final res = await _api.create(op.entityType, op.payload);
+  final res = await _api.create(op.entityType, op.payload,
+      idempotencyKey: op.idempotencyKey);
   // ...
```

Same pattern for `update` and `delete`.

### 5c. Property test

```dart
test('idempotency key is stable across retries', () async {
  final queue = SyncQueue(_isar);
  final op = await queue.enqueue(
    userId: 'sub-A', entityType: 'Order', entityId: 'o1',
    action: 'create', data: '{}', operationId: 'op-1',
  );
  final key1 = op.idempotencyKey;

  // Simulate a failed retry: re-read the row, key must be unchanged.
  final reread = (await _isar.syncOperations.get(op.id))!;
  expect(reread.idempotencyKey, equals(key1));

  // Even after incrementing retryCount.
  await _isar.writeTxn(() async {
    reread.retryCount = 2;
    await _isar.syncOperations.put(reread);
  });
  final reread2 = (await _isar.syncOperations.get(op.id))!;
  expect(reread2.idempotencyKey, equals(key1));
});

test('SyncExecutor passes op.idempotencyKey on POST', () async {
  // Mock ApiClient; assert idempotencyKey: op.idempotencyKey on .create()
});
```

---

## 6. Open questions

1. **Idempotency-key TTL on server.** Confirm server stores keys for
   ≥ `maxRetries × backoffMax` of the mobile client + safety margin.
   Should be ≥ 24 h. Doc the assumption in this PR; flag to server
   team if shorter.

2. **What about non-mutating retries (idempotent reads)?** GET
   doesn't need the header; confirm `SyncExecutor` never queues GETs.

---

*PR scaffold v1.0 — 2026-05-13*
