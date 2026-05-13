# PR Scaffold — S3-02: Migrate Fayda images to encrypted blobs

> **Status:** Design ready · **Depends on:** S3-01 merged and stable in production
> **Sprint:** HS-3 · **Epic:** E1 — Encrypt at rest
> **Story catalog:** `specs/features/isar-hardening/sprints.md#s3-02`
> **Reviewer needed:** security + 1 mobile dev · **Story size:** 5 pts · **Risk:** High
> **Defer-friendly:** If S3-01 slips, this story slips to a HS-3+1 cleanup sprint.

⚠️ **Fayda is the Ethiopian national identity document.** This PR
moves ID-card scans from loose filesystem paths into AES-encrypted
blobs inside Isar. The risk is lower than S3-01 (we own this code
path; images are user-uploaded, not derived) but the **sensitivity
is higher** — these are national-ID images. A bug that loses an
image file requires the user to re-upload, which means re-doing the
Fayda eKYC flow.

---

## 0. TL;DR

Adds three new `List<byte>` blob columns to `User` (front, back,
driving license). On migration:

1. Read the existing file from `faydaIdFrontImagePath`, etc.
2. Encrypt the bytes with AAD `(User, faydaId:<side>, userId)`
3. Write to the new blob column
4. Delete the loose file from `documents/`
5. Clear the path column

Idempotent: rows that already have an empty path field are skipped.
UI image widgets switch from `FileImage` to `MemoryImage` reading
from the blob.

---

## 1. How to ship this

```bash
# Prerequisite: S3-01 merged, 100% rollout green for ≥ 7 days
git fetch origin
git log origin/main --oneline | grep S3-01
# Also confirm Sentry shows zero FieldDecryptionException spike in production

git checkout -b hs3/s3-02-migrate-fayda-images origin/main

# Apply the changes in §5:
#   - mobile/lib/core/database/schemas/user.dart   (+ 3 List<byte> fields)
#   - mobile/lib/core/database/schemas/user.g.dart  (regen)
#   - mobile/lib/core/database/migrations/migrate_v3_fayda_images.dart  (new)
#   - mobile/lib/core/database/migrations/migrations_runner.dart        (register v3)
#   - mobile/lib/features/user/data/repositories/user_repository.dart   (read/write blob)
#   - mobile/lib/features/fayda/widgets/fayda_image_widget.dart         (FileImage → MemoryImage)
#   - mobile/test/core/database/migrations/migrate_v3_fayda_images_test.dart  (new)

cd mobile
dart run build_runner build --delete-conflicting-outputs
flutter analyze
flutter test test/core/database/migrations/
flutter build apk --release --dart-define=PHONE_PEPPER=$PHONE_PEPPER
cd ..

git add ...
git commit -m "$(cat <<'EOF'
feat(mobile): migrate Fayda ID images to encrypted Isar blobs

Moves Fayda ID-card scans (front/back + driving license for truckers)
from loose files under documents/ into AES-GCM encrypted blobs stored
inside Isar. AAD bound to (User, faydaId:<side>, userId).

The loose file is deleted from disk only AFTER the encrypted blob is
successfully written inside the writeTxn. If the write fails, the file
stays and migration retries on next launch.

Schema version bumped 2 → 3. Migration v3 is gated by the same kill
switch as v2 and follows the same dev-cohort + staged-rollout playbook
(though with smaller blast radius — at most one user's ID images are
affected by a single bug, vs. all PII for S3-01).

Refs: specs/features/isar-hardening/sprints.md#s3-02
EOF
)"
git push -u origin hs3/s3-02-migrate-fayda-images
# DO NOT merge until founder + security sign off on the Fayda data flow
gh pr create --title "feat(mobile): migrate Fayda images to encrypted blobs [S3-02]"
```

---

## 2. PR description

### Summary

Three new `List<byte>` blob columns on `User`:
- `faydaIdFrontEnc` — AES-GCM ciphertext of the front scan
- `faydaIdBackEnc` — AES-GCM ciphertext of the back scan
- `drivingLicenseEnc` — AES-GCM ciphertext of the driving license (trucker only)

Schema version 2 → 3. Migration walks every `User` row with a non-null
`faydaIdFrontImagePath` / `faydaIdBackImagePath` / `drivingLicensePath`,
reads the file, encrypts the bytes, writes to the new blob column,
deletes the file, clears the path column.

UI image widgets switch from `FileImage(...)` to `MemoryImage(...)`
reading the in-memory bytes after decryption.

### Why

`user.dart:43-49` currently stores three filesystem paths pointing at
raw bitmap images of the Fayda national ID. These files sit at
`getApplicationDocumentsDirectory()/...`, readable by:

- The app itself (intended)
- adb `run-as <pkg>` on a debuggable build (unintended)
- Google Drive auto-backup (blocked by `allowBackup="false"`, but
  belt-and-braces)
- Any process running as the same UID on a rooted device (unintended)

Encrypting the bytes and storing them inside Isar puts them behind
the same AES-GCM + AAD protection HS-1 + S2-01 already provide for
text PII. Fayda is a national-ID document; this gap was load-bearing
in the threat model.

### What changes

#### Schema

```diff
 class User {
   // ... existing fields ...
   String? faydaIdFrontImagePath;
   String? faydaIdBackImagePath;
   String? drivingLicensePath;
+
+  /// AES-GCM encrypted Fayda front scan. ~200 KB typical.
+  /// AAD: (User, faydaId:front, userId). Empty until migrated from
+  /// faydaIdFrontImagePath.
+  List<byte> faydaIdFrontEnc = const [];
+
+  /// AES-GCM encrypted Fayda back scan.
+  List<byte> faydaIdBackEnc = const [];
+
+  /// AES-GCM encrypted driving license (trucker only).
+  List<byte> drivingLicenseEnc = const [];
 }
```

The path columns stay populated for one sprint after S3-02 reaches
100% rollout, then get removed in a follow-up. (Same pattern as
phoneNumber in S2-02.)

#### Migration (v2 → v3)

`migrate_v3_fayda_images.dart`:

```dart
class MigrateV3FaydaImages {
  final Isar _isar;
  final FieldEncryptor _enc;

  Future<MigrationStats> run() async {
    final users = await _isar.users.where().findAll();
    var imagesMigrated = 0;
    var imagesSkipped = 0;
    var imagesFailed = 0;

    for (final u in users) {
      imagesMigrated += await _migrateOne(u, 'front',
          () => u.faydaIdFrontImagePath,
          (v) => u.faydaIdFrontEnc = v,
          () => u.faydaIdFrontImagePath = null) ? 1 : 0;
      imagesMigrated += await _migrateOne(u, 'back',
          () => u.faydaIdBackImagePath,
          (v) => u.faydaIdBackEnc = v,
          () => u.faydaIdBackImagePath = null) ? 1 : 0;
      imagesMigrated += await _migrateOne(u, 'license',
          () => u.drivingLicensePath,
          (v) => u.drivingLicenseEnc = v,
          () => u.drivingLicensePath = null) ? 1 : 0;
      await _isar.users.put(u);
    }
    return MigrationStats(...);
  }

  /// Returns true if a file was successfully migrated for this user/side.
  /// Returns false if (a) no path exists (already migrated), (b) the
  /// file is missing on disk (user never uploaded), or (c) read failed.
  Future<bool> _migrateOne(
    User u,
    String side,
    String? Function() getPath,
    void Function(List<byte>) setBlob,
    void Function() clearPath,
  ) async {
    final path = getPath();
    if (path == null || path.isEmpty) return false;

    final file = File(path);
    if (!await file.exists()) {
      // User had a path but the file is gone (manual cleanup, OS purge).
      // Clear the path so this row doesn't retry next launch.
      clearPath();
      return false;
    }

    try {
      final bytes = await file.readAsBytes();
      // Encrypt with raw-bytes API (not the string encrypt).
      // FieldEncryptor needs a byte-variant — added in this PR if not
      // already there. See §5.
      final blob = await _enc.encryptBytes(
        bytes,
        collection: 'User', column: 'faydaId:$side', rowKey: u.userId,
      );
      setBlob(blob);

      // Delete the loose file only AFTER the blob write is staged
      // in the txn. If the txn fails for any reason, the file remains.
      await file.delete();
      clearPath();
      return true;
    } catch (e) {
      debugPrint('[migrate_v3] failed for ${u.userId} $side: $e');
      // Don't rethrow — one bad image shouldn't block other rows.
      return false;
    }
  }
}
```

#### FieldEncryptor — add byte-array variant

```diff
+/// Like [encrypt] but for raw bytes (e.g., images). Returns
+/// `enc:v1:` prefix bytes followed by nonce + ciphertext + mac.
+/// Used by the S3-02 Fayda image migration.
+Future<List<byte>> encryptBytes(
+  List<byte> plaintext, {
+  required String collection,
+  required String column,
+  required String rowKey,
+}) async {
+  // ... same algorithm as encrypt() but skip utf8.encode/base64Encode
+}
+
+Future<List<byte>> decryptBytes(
+  List<byte> ciphertext, {
+  required String collection,
+  required String column,
+  required String rowKey,
+}) async { ... }
```

Format choice: byte arrays don't need the base64 wrap. Prefix is the
literal byte sequence `'enc:v1:'` + nonce + ciphertext + mac.

#### UI

`mobile/lib/features/fayda/widgets/fayda_image_widget.dart` (or
wherever Fayda images render):

```diff
-Image.file(File(user.faydaIdFrontImagePath!))
+FutureBuilder<List<int>>(
+  future: _decryptImage(user),
+  builder: (context, snap) {
+    if (!snap.hasData) return const CircularProgressIndicator();
+    return Image.memory(Uint8List.fromList(snap.data!));
+  },
+)
```

The decrypt happens off the UI thread (Isar `txn` async). For a
~200 KB image, decrypt takes ~5-20 ms on a Tecno Spark 8 — acceptable.

### What does NOT change

- Audio note files (`audioNoteRef` on CropListing) — different
  pattern (voice recordings, larger files). Out of scope.
- Server-side: server stores presigned S3 URLs for Fayda images;
  S3 server-side encryption (SSE-KMS) is already in place. This PR
  closes the local-storage gap only.

### Backwards compatibility

Schema 2 → 3. Rows on disk after S3-01:
- Path columns populated, blob columns empty.
- Migration v3 walks them, encrypts the file bytes, populates blobs,
  deletes files, clears paths.

Rows on disk after S3-02:
- Path columns null, blob columns populated.
- Reads go through the new blob path.

If S3-02 is reverted while users have already migrated → blob columns
are populated, path columns are null → UI tries `FileImage(null)` →
crash. **The feature flag is the rollback path.** See §4.

### Test plan

```bash
cd mobile
flutter test test/core/database/migrations/migrate_v3_fayda_images_test.dart
flutter test                              # full suite
flutter build apk --release --dart-define=PHONE_PEPPER=$PHONE_PEPPER
```

**Required test cases:**

1. **No images present** (user never uploaded): migration is no-op for that row
2. **Path populated, file present**: migration encrypts, blob populated, file deleted
3. **Path populated, file missing**: path cleared, no blob written
4. **Already migrated** (path null, blob populated): no-op
5. **Crash mid-migration**: one user halfway through (front done, back not). Restart resumes; back image migrates next launch.
6. **Encrypt failure**: simulate an `encryptBytes` exception. Row left alone, migration continues for other users.
7. **Image round-trip**: encrypt → decrypt → bytes match original

**Manual on a real device:**

1. Build pre-S3-02 → register → upload Fayda front + back → verify they show in profile
2. Upgrade to S3-02 build → relaunch → migration runs
3. Re-open profile → images still show (via blob now)
4. `adb shell run-as <pkg> ls -la app_flutter/` → confirm Fayda image files are gone
5. Isar Inspector → `User.faydaIdFrontEnc` is populated bytes; path columns are null

### Threat model coverage

| Threat | Pre-PR | Post-PR |
|---|---|---|
| Rooted device dumps `documents/` directory | All Fayda scans readable as plain bitmaps | Files gone; only encrypted blobs inside Isar |
| adb backup pulls app data | (Blocked by `allowBackup="false"`) | Belt-and-braces: even if backup leaks, encrypted |
| Same-UID malicious app reads the file | Trivial | Files gone |
| AAD swap (plant front-A onto back-B) | n/a | Detected (AAD includes side and userId) |

### Risk

High but lower than S3-01:

1. **Bug encrypts the wrong bytes.** Mitigated by round-trip test in
   CI; manual smoke on a real device pre-merge.

2. **File delete happens before blob write commits.** Mitigated by
   the `try`/`catch` flow: delete is inside the same `await` block
   as the encrypt-and-stage-write. If `_isar.users.put(u)` fails,
   the txn rolls back AND the file is already deleted. **This is a
   real risk.** Mitigation: keep a temporary copy until txn commits.

   See §5c for the safer flow.

3. **Image-size cap.** Isar imposes no hard limit but DynamoDB items
   are capped at 400 KB; our server's image-receive endpoint accepts
   up to 5 MB. A 5 MB blob inside Isar inflates the DB significantly.
   Audit: are typical Fayda scans < 250 KB? If yes, fine. If users
   are uploading raw camera shots (1-3 MB), add a downscale before
   encrypt.

### Acceptance criteria

From `specs/features/isar-hardening/sprints.md#s3-02`:

- [ ] New fields populated by migration: read file → encrypt with AAD `User:faydaId:<userId>:<side>` → store as blob
- [ ] Old file deleted from `documents/` after successful blob write (atomic within txn boundary where possible)
- [ ] Old path fields cleared (`null`)
- [ ] Migration handles missing files gracefully (user never uploaded ID)
- [ ] UI image widgets switch to reading from blob (memory image)

---

## 3. Reviewer guide

1. **Order of operations on file delete.** Verify the file is only
   deleted AFTER the blob is staged for write AND in a path that the
   outer txn commits atomically. The §5c diff is the safest version
   I could write — review it carefully.

2. **Image size audit.** Run `flutter logs` during the manual smoke
   test and watch the byte count. If any user has > 500 KB images,
   add a downscale step.

3. **UI flicker on first load post-migration.** The first time the
   profile renders, the image is decrypt-on-demand. Shouldn't be
   visible (5-20 ms), but a `FutureBuilder` with no placeholder shows
   a blank frame. Confirm the placeholder is in place.

---

## 4. Rollback plan

**Feature flag:** `enableFaydaImageMigration`. Default `true` for the
staged rollout; flip to `false` to skip the migration on new launches.
Users already migrated keep their data in blob form.

**If a user's images are corrupted:** they'll need to re-upload, which
means re-doing Fayda eKYC. Mitigated by:
- The round-trip test in CI catches encrypt-then-decrypt mismatches
- Manual smoke on a real device catches end-to-end visual issues
- The dev cohort gate catches device-specific bugs

**Code revert:** `git revert <merge-commit>` removes the migration
runner registration but **does not unwind already-migrated data**.
Users migrated under S3-02 will have null paths and populated blobs;
post-revert code tries `FileImage(null)` and crashes the image widget.

**Hence the feature flag is mandatory** — never code-revert without
the flag also set to false on already-migrated installs (which is
moot for them anyway).

---

## 5. Proposed diffs

### 5a. Schema (`mobile/lib/core/database/schemas/user.dart`)

Diff in §"What changes — Schema" above. Append three `List<byte>` fields.

### 5b. `FieldEncryptor.encryptBytes` / `decryptBytes`

Add to `mobile/lib/core/database/field_encryptor.dart`:

```dart
Future<List<byte>> encryptBytes(
  List<byte> plaintext, {
  required String collection,
  required String column,
  required String rowKey,
}) async {
  if (!_initialized) throw StateError(...);
  final box = await _algorithm.encrypt(
    plaintext,
    secretKey: _secretKey!,
    aad: utf8.encode(_aad(collection, column, rowKey)),
  );
  // Format: literal 'enc:v1:' bytes + nonce + ciphertext + mac
  return Uint8List.fromList([
    ...utf8.encode('enc:v1:'),
    ...box.nonce,
    ...box.cipherText,
    ...box.mac.bytes,
  ]);
}

Future<List<byte>> decryptBytes(
  List<byte> value, {
  required String collection,
  required String column,
  required String rowKey,
}) async {
  // Verify prefix, slice nonce/ct/mac, decrypt with AAD.
  // Same fail-closed semantics as decrypt().
}
```

### 5c. Migration with safe file-delete order

```dart
Future<bool> _migrateOne(
  User u, String side,
  String? Function() getPath,
  void Function(List<byte>) setBlob,
  void Function() clearPath,
) async {
  final path = getPath();
  if (path == null || path.isEmpty) return false;

  final file = File(path);
  if (!await file.exists()) {
    clearPath();
    return false;
  }

  final bytes = await file.readAsBytes();
  final blob = await _enc.encryptBytes(
    bytes,
    collection: 'User', column: 'faydaId:$side', rowKey: u.userId,
  );

  // 1. Stage the blob into the in-memory User object.
  setBlob(blob);
  clearPath();

  // 2. DO NOT delete the file here. The outer migration loop calls
  //    isar.users.put(u) inside the same writeTxn. The file delete
  //    happens AFTER the txn commits successfully — see _commitPhase.
  //
  //    For now, record the file we intend to delete.
  _pendingDeletes.add(file.path);
  return true;
}

/// Called after the migration writeTxn commits successfully.
Future<void> _commitPhase() async {
  for (final p in _pendingDeletes) {
    try {
      await File(p).delete();
    } on Exception catch (e) {
      // File still on disk but no longer referenced. Orphan cleanup
      // will catch it on the next launch (or it never matters because
      // the path column is null, so the migration can't see it).
      debugPrint('[migrate_v3] orphan file (delete failed): $p — $e');
    }
  }
}
```

Migration runner calls `_commitPhase` after the `writeTxn` returns.
On exception (txn rollback), the staged file deletes are skipped — the
files remain on disk and will be retried next launch.

### 5d. UI widget refactor

Pseudocode for `fayda_image_widget.dart`:

```dart
class FaydaImageWidget extends StatelessWidget {
  final User user;
  final String side;   // 'front' | 'back' | 'license'
  final FieldEncryptor encryptor;

  Future<Uint8List?> _load() async {
    final blob = switch (side) {
      'front' => user.faydaIdFrontEnc,
      'back' => user.faydaIdBackEnc,
      'license' => user.drivingLicenseEnc,
      _ => null,
    };
    if (blob == null || blob.isEmpty) {
      // Fallback to legacy path during the transition sprint
      final legacy = switch (side) {
        'front' => user.faydaIdFrontImagePath,
        'back' => user.faydaIdBackImagePath,
        'license' => user.drivingLicensePath,
        _ => null,
      };
      if (legacy != null) return File(legacy).readAsBytes();
      return null;
    }
    try {
      final bytes = await encryptor.decryptBytes(
        blob, collection: 'User', column: 'faydaId:$side', rowKey: user.userId,
      );
      return Uint8List.fromList(bytes);
    } on FieldDecryptionException {
      return null;   // UI shows "image unavailable" placeholder
    }
  }

  @override
  Widget build(BuildContext context) {
    return FutureBuilder<Uint8List?>(
      future: _load(),
      builder: (context, snap) {
        if (snap.connectionState == ConnectionState.waiting) {
          return const CircularProgressIndicator();
        }
        final bytes = snap.data;
        if (bytes == null) return const Icon(Icons.broken_image);
        return Image.memory(bytes);
      },
    );
  }
}
```

---

## 6. Open questions for the reviewer

1. **Image downscale before encrypt?** If users upload raw 3 MB camera
   shots, the Isar DB inflates fast. Suggest: downscale to 1024px max
   on capture (separate story, pre-S3-02). For this PR, just measure
   and flag if it's a problem.

2. **What's the user-visible UX during migration?** Profile screen
   may render before migration completes. Should we block the auth
   resume on the migration runner? Default: block (already does, per
   `_runMigrationsIfNeeded` in `initialize()`). Splash screen shows
   "Updating storage…" if it takes > 500 ms.

3. **Server-side parity.** Should the server-stored Fayda images
   (S3-presigned URL) also use AAD-bound encryption client-side
   before upload? Default: out of scope for this PR. Server already
   has SSE-KMS. Track as a follow-up.

4. **Orphan-file cleanup.** §5c admits files may stay on disk if a
   delete fails. Run a one-shot orphan-cleanup task on app launch
   that walks `documents/` for files matching `faydaId_*.{jpg,png}`
   not referenced by any User row. Add as a follow-up.

---

*PR scaffold v1.0 — 2026-05-13 — depends on S3-01 stable at 100% rollout*
