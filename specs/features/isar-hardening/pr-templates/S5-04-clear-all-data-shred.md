# PR Scaffold — S5-04: `clearAllData` shreds the DB file

> **Status:** Design ready · **Depends on:** S5-01 merged · **Closes HS-5**
> **Sprint:** HS-5 · **Epic:** E3
> **Story catalog:** `specs/features/isar-hardening/sprints.md#s5-04`
> **Reviewer needed:** security + 1 mobile dev · **Story size:** 3 pts · **Risk:** Low

Today's `clearAllData()` calls `Isar.clear()` which truncates collections
but leaves the Isar file (and WAL pages) on disk. This PR deletes the
file too, so logout is forensically clean.

---

## 0. TL;DR

`IsarService.clearAllData()` order becomes:

1. Close DB
2. Delete `wereda_connect.isar` (and `.lock` if present)
3. Delete encryption key + HMAC tag from secure storage
4. `FieldEncryptor.reset()`

Idempotent — running on a missing file doesn't throw.

---

## 1. How to ship this

```bash
git fetch origin
git log origin/main --oneline | grep S5-01

git checkout -b hs5/s5-04-clear-all-data-shred origin/main

# Apply:
#   - mobile/lib/core/database/isar_service.dart       (refactor clearAllData)
#   - mobile/test/core/database/isar_service_test.dart  (assert file gone)

cd mobile && flutter test
cd ..

git add ... && git commit -m "feat(mobile): clearAllData deletes the Isar file from disk"
git push -u origin hs5/s5-04-clear-all-data-shred
gh pr create --title "feat(mobile): shred DB file on clearAllData [S5-04]"
```

---

## 2. PR description

### Summary

`clearAllData()` now physically deletes the Isar file in addition to
clearing its contents. Catch the rare case where the file is already
missing (idempotent).

### Why

`Isar.clear()` truncates collections at the logical level. The
underlying file (and any WAL/temp pages) remain on disk. Combined
with the encryption-key deletion, the data is *meant* to be
unreadable — but:

- Forensic tools can recover dirty WAL pages
- If any backup grabs the file later (despite `allowBackup="false"`)
  AND the key somehow leaks, the data is back

Deleting the file outright closes the gap.

### What changes

```diff
   Future<void> clearAllData() async {
-    // Close database connection
     await close();

-    // Delete encryption key from secure storage
-    const keyName = 'isar_encryption_key';
-    await _secureStorage.delete(key: keyName);
-
-    // Note: The old database file still exists but is unreadable without the key
-    // Isar will create a new database on next initialize()
+    final dir = await getApplicationDocumentsDirectory();
+    final dbFile = File('${dir.path}/wereda_connect.isar');
+    final lockFile = File('${dir.path}/wereda_connect.isar.lock');
+
+    try {
+      if (await dbFile.exists()) await dbFile.delete();
+    } on FileSystemException catch (e) {
+      debugPrint('[IsarService] db file delete failed: $e');
+    }
+    try {
+      if (await lockFile.exists()) await lockFile.delete();
+    } on FileSystemException catch (_) {
+      // Lock files are sometimes held by the OS; tolerate.
+    }
+
+    await _secureStorage.delete(key: _keyName);
+    await _secureStorage.delete(key: _tagName);          // S1-03 HMAC tag
+    FieldEncryptor.instance.reset();
   }
```

### What does NOT change

- `close()` itself.
- The init flow.

### Backwards compatibility

`clearAllData()` is called from logout flows. Existing callers see
no behavior change — they just get a cleaner result.

### Test plan

```bash
flutter test test/core/database/isar_service_test.dart
```

Required:
1. After `initialize()` + `clearAllData()`: db file does not exist
2. After `clearAllData()` on a missing file: doesn't throw
3. After `clearAllData()`: a fresh `initialize()` succeeds and
   creates a new file with a new key
4. After `clearAllData()` + `initialize()`: old encrypted rows are
   gone (the new key cannot decrypt them, but the file is gone
   anyway)

### Risk

Low.

### Acceptance criteria

- [ ] `clearAllData()` order: (1) close DB, (2) delete `wereda_connect.isar`, (3) delete `.lock` if present, (4) delete key from secure storage, (5) delete HMAC tag, (6) `FieldEncryptor.reset()`
- [ ] Idempotent — running on missing file doesn't throw
- [ ] Test asserts file absent after call
- [ ] Test asserts a fresh `initialize()` after `clearAllData` creates a brand-new file with a brand-new key

---

## 3. Reviewer guide

1. **Order matters.** Close DB BEFORE deleting the file (else
   Isar holds an open handle that may keep the file on some OSes).

2. **Tolerate FileSystemException.** Some Android OEMs lock the
   file briefly after close. Catch + log + continue.

3. **HMAC tag from S1-03.** Don't forget to delete the
   `isar_encryption_key_tag` entry too. Add a constant if not
   already declared.

---

## 4. Rollback plan

Revert. `Isar.clear()` returns as the logical truncation; key gets
deleted but file lingers. No new failure modes.

---

## 5. Proposed diffs

See §"What changes."

---

## 6. Open questions

1. **Should we also clear `getApplicationDocumentsDirectory()/`
   audio recordings, Fayda image files (pre-S3-02 migration)?**
   For "logout-with-full-clear", yes. Track as follow-up: a
   `wipeUserData()` helper that walks `documents/` and removes
   user-scoped subtrees.

2. **Multi-user device: clearing one user's data shouldn't affect
   another's.** Currently `clearAllData` is global. Track for HS-7
   if multi-user becomes a real requirement.

---

*PR scaffold v1.0 — 2026-05-13 — closes HS-5*
