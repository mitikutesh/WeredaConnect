# PR Scaffold — S7-01: Implement `rotateEncryptionKey()`

> **Status:** Design ready · **Depends on:** S3-01 + S5-04 merged
> **Sprint:** HS-7 · **Epic:** E4 — Long-term key hygiene
> **Story catalog:** `specs/features/isar-hardening/sprints.md#s7-01`
> **Reviewer needed:** security + 1 mobile dev · **Story size:** 8 pts · **Risk:** Medium

Today's `IsarService.rotateEncryptionKey()` throws `UnimplementedError`.
This PR implements it. Compromise recovery becomes possible without
data destruction.

---

## 0. TL;DR

1. Generate a new 32-byte key + HMAC tag (same shape as S1-03).
2. In one `writeTxn`: for every encrypted field on every row, decrypt
   with old key + AAD, re-encrypt with new key + AAD.
3. On success: write the new key + tag to secure storage; delete the
   old key entry; `FieldEncryptor` re-init with the new key.
4. On any failure: roll back the txn (Isar atomicity); old key
   remains in use; surface error to caller.

---

## 1. How to ship this

```bash
git fetch origin
git log origin/main --oneline | grep -E 'S3-01|S5-04'

git checkout -b hs7/s7-01-rotate-encryption-key origin/main

# Apply:
#   - mobile/lib/core/database/isar_service.dart    (impl rotateEncryptionKey)
#   - mobile/lib/core/database/migrations/migrate_key_rotation.dart (new helper)
#   - mobile/test/core/database/isar_service_test.dart  (rotation tests)
#   - mobile/test/core/database/migrations/migrate_key_rotation_test.dart (new)

cd mobile && flutter analyze && flutter test
cd ..

git add ... && git commit -m "feat(mobile): implement rotateEncryptionKey for compromise recovery"
git push -u origin hs7/s7-01-rotate-encryption-key
gh pr create --title "feat(mobile): rotateEncryptionKey [S7-01]"
```

---

## 2. PR description

### Summary

Implements `IsarService.rotateEncryptionKey()`:

```dart
Future<void> rotateEncryptionKey() async {
  if (_isar == null) throw StateError('IsarService not initialized');

  // 1. Generate new key + tag
  final newKey = _generateRandomKey();
  final newTag = _computeTag(newKey);

  // 2. Re-encrypt every PII field inside one writeTxn
  await _isar!.writeTxn(() async {
    await _reencryptUsers(newKey);
    await _reencryptCropListings(newKey);
    // ...any other collection with encrypted PII
  });

  // 3. Persist the new key + tag; delete the old entry
  await _secureStorage.write(key: _keyName, value: base64Encode(newKey));
  await _secureStorage.write(key: _tagName, value: base64Encode(newTag));

  // 4. Re-init FieldEncryptor with the new key
  FieldEncryptor.instance.reset();
  FieldEncryptor.instance.initialize(newKey, legacyPlaintextAllowed: false);
}
```

Internal `_reencrypt*` helpers walk every row, decrypt every encrypted
field with the *old* key (via the existing `FieldEncryptor` instance),
swap to a temp `FieldEncryptor` initialized with the new key,
re-encrypt, write back. Both happen inside one outer `writeTxn` so a
crash leaves the file unchanged.

### Why

Today, `rotateEncryptionKey()` throws `UnimplementedError`. A leaked
key (root-device exploit, accidental log, etc.) has no recovery path
other than `clearAllData()` — which destroys all local-only state.

A real rotation lets us:
1. Recover from key leaks without losing user data
2. Periodically rotate as a defense-in-depth measure (annual or on
   suspicious-activity trigger)
3. Migrate to a new key when adopting the Keystore platform channel
   in S7-02

### What changes

- `mobile/lib/core/database/isar_service.dart` — implement
  `rotateEncryptionKey`.
- `mobile/lib/core/database/migrations/migrate_key_rotation.dart` —
  helper that walks all PII columns. Lives under `migrations/` even
  though it's not version-tagged (rotation can run at any time).
- Tests as in §"Test plan".

### What does NOT change

- Schema version. Rotation is not a schema change.
- The AAD format. Same AAD trio, just re-encrypted under a new key.
- Anything user-visible (the rotation runs invisibly).

### Backwards compatibility

`rotateEncryptionKey` is a new public API. No existing caller. PO
decides when/how to trigger it (probably a hidden admin gesture in
dev builds, or wired to a "Reset encryption" button in user
Settings).

### Test plan

```bash
flutter test test/core/database/isar_service_test.dart \
             test/core/database/migrations/migrate_key_rotation_test.dart
```

Required:
1. **Happy path.** Seed 10 User rows + 10 CropListing rows. Rotate.
   All rows still decryptable. AAD survives.
2. **Idempotent re-rotation.** Rotate twice in a row. All rows still
   readable both times. Different ciphertexts each time (per nonce
   freshness).
3. **Crash mid-rotation.** Inject a fault after re-encrypting 5
   rows. The `writeTxn` rolls back; old key still in use; all rows
   still readable.
4. **Wrong-key-then-rotate.** After rotation, the OLD key is useless
   (decrypt of rotated rows fails). Confirms the rotation actually
   changed something.
5. **Performance.** 10k rows on Tecno Spark 8 should rotate in
   < 5 s. Document the threshold.

### Threat model

| Threat | Pre-PR | Post-PR |
|---|---|---|
| Key leaked, no recovery | `clearAllData` destroys local data | `rotateEncryptionKey` preserves data |
| Periodic rotation as defense-in-depth | Not possible | Possible |
| Rotation-in-progress crash | n/a | Atomic via `writeTxn`; old key remains |

### Risk

Medium. Largest risks:

1. **Memory pressure.** A user with 50k rows × ~30 PII fields = 1.5M
   decrypt/encrypt operations in one transaction. May OOM on low-end
   devices. Mitigation: chunk by 500 rows × multiple `writeTxn`s for
   large DBs. Sacrifice atomicity for safety; idempotent skip logic
   (S3-01-style) handles partial rotation.

2. **Test seam for FieldEncryptor with two keys.** Need a way to
   instantiate a second encryptor with the new key without mutating
   the global singleton. Add a `FieldEncryptor.withKey(keyBytes)`
   constructor for this purpose.

### Acceptance criteria

- [ ] Generate new 32-byte key + HMAC tag
- [ ] In a single `writeTxn`: for every PII field on every row, decrypt with old key + AAD, re-encrypt with new key + AAD
- [ ] On success: write new key + tag, delete old key, `FieldEncryptor.reset()` + reinit with new key
- [ ] On any failure: roll back txn, retain old key, surface error to caller
- [ ] Rotation drill test in CI: rotate twice in a row, assert data readable both times
- [ ] Drill takes < 5s for 10k rows on a low-end device (Tecno Spark 8)

---

## 3. Reviewer guide

1. **Atomicity boundary.** The entire walk happens inside one
   `writeTxn`. If you see two separate `writeTxn`s in `rotateEncryptionKey`,
   push back — you're trading atomicity for memory.

2. **`FieldEncryptor.withKey` seam.** Verify the new constructor
   doesn't mutate the global `FieldEncryptor.instance`. The rotation
   creates a temporary encryptor for the new-key side and uses the
   global for the old-key side; they coexist briefly.

3. **AAD preserved.** Confirm `_reencrypt*` helpers pass the same
   `(collection, column, rowKey)` trio to both decrypt and encrypt.
   A bug here would re-encrypt with different AAD and break future
   reads.

---

## 4. Rollback plan

`git revert <merge-commit>`. `rotateEncryptionKey` throws
`UnimplementedError` again. If a rotation was triggered post-merge
and pre-revert, those rows are encrypted under the (now-deleted)
old key. **The new key persisted in secure storage is still there.**
After revert, init reads the new key from secure storage and uses
it — so the rotated data is readable as long as the new key is
intact.

Effectively: the rotation is durable once it commits. Revert is
safe.

---

## 5. Proposed diffs

See §"What changes." Plus this seam:

```dart
// In FieldEncryptor:
/// Test-only constructor: a fresh encryptor instance with a specific
/// key. Used by rotateEncryptionKey to keep two keys in memory briefly.
@visibleForTesting
factory FieldEncryptor.withKey(List<int> keyBytes) {
  final inst = FieldEncryptor._();
  inst.initialize(keyBytes);
  return inst;
}
```

---

## 6. Open questions

1. **Trigger mechanism.** This PR adds the *capability*. How does
   it actually fire? Options:
   - Hidden admin gesture in dev (long-press logo five times)
   - Settings menu item "Reset encryption keys"
   - Server-triggered (push notification with payload that triggers
     rotation on next launch)

   Default: out of scope; capability only in this PR; trigger UX
   in a follow-up.

2. **Chunked rotation for large DBs.** §"Risk" item 1. Should this
   PR ship with chunking? Default: not yet — measure first on real
   devices. Add a `_kRotationChunkSize` constant for future use.

3. **Pepper rotation parity.** `PhoneHasher` uses a build-time
   pepper. Rotating the AES key doesn't rotate the pepper.
   Different concern; track separately.

---

*PR scaffold v1.0 — 2026-05-13*
