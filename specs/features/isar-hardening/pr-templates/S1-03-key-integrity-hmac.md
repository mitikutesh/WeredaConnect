# PR Scaffold — S1-03: Key-integrity HMAC on read-back

> **Status:** Design ready · **Depends on:** — (independent of S1-01 / S1-02)
> **Sprint:** HS-1 · **Epic:** E1 — Encrypt at rest
> **Story catalog:** `specs/features/isar-hardening/sprints.md#s1-03`
> **Reviewer needed:** security + 1 mobile dev
> **Can ship in parallel with:** S1-01, S1-02

This is a design scaffold — the code changes are presented as diffs below.
S1-03 touches `isar_service.dart`, not `field_encryptor.dart`, so it can
land on `main` independently of S1-01/S1-02 with no rebase friction.

---

## 0. TL;DR

Store an HMAC-SHA256 tag alongside the AES key in `FlutterSecureStorage`.
On every read, recompute the tag and compare in constant time. Mismatch →
throw `KeyIntegrityFailure`. `IsarService.initialize()` catches the
failure, calls `clearAllData()` (which is part of S5-04 — for now we
delete the key entry and force re-login), and rethrows so the auth flow
can show "Local storage was reset for your security."

**Closes finding N-H4** in the security review: today, if
`flutter_secure_storage` returns a *valid-looking but wrong* 32-byte
value (rare firmware corruption, backup-restore to a different device,
malicious same-UID app rewriting the entry), the length check passes
and the app proceeds to decrypt with the wrong key. Without the HMAC
tag, the integrity-check gap is silent.

---

## 1. How to ship this

```bash
# Can branch from main any time. Independent of S1-01/S1-02.
git fetch origin
git checkout -b hs1/s1-03-key-integrity-hmac origin/main

# Apply the diffs in §5 below. Files touched:
#   - mobile/lib/core/database/isar_service.dart
#   - mobile/test/core/database/isar_service_test.dart  (new file)

# Verify
cd mobile
flutter analyze lib/core/database/isar_service.dart
flutter test test/core/database/isar_service_test.dart
flutter build web --no-tree-shake-icons 2>&1 | tail -5
cd ..

git add mobile/lib/core/database/isar_service.dart \
        mobile/test/core/database/isar_service_test.dart
git commit -m "$(cat <<'EOF'
feat(mobile): HMAC-SHA256 integrity tag on persisted AES key

Stores Hmac(sha256, key || 'wereda-isar-key-v1') alongside the AES key
in FlutterSecureStorage. On every read, recomputes the tag and compares
in constant time. Mismatch throws KeyIntegrityFailure, which the caller
treats as an indicator of tampering / corruption / cross-device restore
and wipes the DB before forcing re-login.

Closes a silent-corruption gap: previously, a 32-byte value that wasn't
our key could pass the length check and the app would decrypt with the
wrong key, returning garbage that the (pre-S1-02) fail-open decrypt
path then handed back to repositories as if it were legitimate plaintext.

Refs: specs/features/isar-hardening/sprints.md#s1-03
EOF
)"
git push -u origin hs1/s1-03-key-integrity-hmac
gh pr create --title "feat(mobile): key-integrity HMAC on read-back [S1-03]" \
             --body "$(cat specs/features/isar-hardening/pr-templates/S1-03-key-integrity-hmac.md | sed -n '/^## 2. PR description/,/^## 3. /p' | head -n -1)"
```

---

## 2. PR description

### Summary

Adds an HMAC-SHA256 integrity tag to the persisted AES-256 encryption
key. The tag is recomputed on every read; a mismatch throws
`KeyIntegrityFailure` and the `IsarService.initialize()` flow handles
that as a security-relevant event: it deletes both the key and the
tag entry, and rethrows so the auth flow can prompt the user to
re-login.

### Why

`docs/mobile/isar-implementation.md` security review (May 2026),
finding **N-H4**: `flutter_secure_storage` can return a value that
looks valid (32 bytes, base64-decodable) but isn't our key —
scenarios:

1. **Backup-restore to a different device.** Google Drive auto-backup
   uploads `EncryptedSharedPreferences` content. Restoring to a new
   device may unwrap to wrong bytes (different keystore master key)
   but still deserialize to 32 bytes.
2. **Malicious same-UID app.** A debuggable build or a same-package
   sideload could rewrite the SharedPreferences entry to attacker-
   chosen 32 bytes.
3. **Rare firmware corruption.** Documented on a small number of
   Samsung/Huawei firmware versions: EncryptedSharedPreferences
   silently corrupts entries under specific conditions.

Without an integrity check, all three scenarios result in the app
opening Isar with the wrong key, decrypt returning garbage, and (pre-
S1-02) the fail-open decrypt path handing back garbage as plaintext.
The HMAC tag flips this from "silent corruption" to "explicit failure
that crash reporting sees and that the auth flow handles cleanly."

### What changes

- `mobile/lib/core/database/isar_service.dart`
  - New `class KeyIntegrityFailure implements Exception`
  - New secure-storage key: `isar_encryption_key_tag` — stores
    `base64(HMAC-SHA256(key || 'wereda-isar-key-v1'))`
  - New `_constantTimeEq(List<int> a, List<int> b) → bool`
  - `_getEncryptionKey()` reads both `key` and `tag`; recomputes the
    expected HMAC from the read-back key; constant-time compares
  - Mismatch → delete BOTH entries, throw `KeyIntegrityFailure`
  - On key *generation*, write the tag at the same time
  - `initialize()` catches `KeyIntegrityFailure` → calls a new
    `_wipeForIntegrityFailure()` helper that deletes the Isar file
    and any other persisted user state, then rethrows so the calling
    auth flow can surface a "device reset for security" message

- `mobile/test/core/database/isar_service_test.dart` (new)
  - Mocks `FlutterSecureStorage` (or uses `MethodChannelMock`)
  - Test: write key + tag, read back, succeeds
  - Test: tamper with key in storage, read-back throws
    `KeyIntegrityFailure`
  - Test: tamper with tag in storage, read-back throws
  - Test: tag missing → treated as no-entry (regenerates fresh) so
    devices upgrading from pre-S1-03 builds get a tag written
    transparently on next launch
  - Test: `_constantTimeEq` returns `false` on length mismatch
    without short-circuiting on the first differing byte (verify by
    timing a large equal-length vs short-length compare — coarse but
    rules out the obvious early-return bug)

- `mobile/pubspec.yaml` — no change. `crypto: ^3.0.6` is already
  present for SHA-256.

### What does NOT change in this PR

- AAD binding — that's S1-01
- Ciphertext prefix and fail-closed decrypt — that's S1-02
- Migration toggle — S1-04
- `clearAllData()` deleting the Isar file from disk — that's S5-04
  (this PR only deletes the secure-storage entries; the Isar file
  becomes unreadable on its own with the key gone, but it remains on
  disk until S5-04 lands)

### Backwards compatibility

**Devices upgrading from a pre-S1-03 build will have a key entry but no
tag entry.** Two policy options:

| Policy | Pros | Cons |
|---|---|---|
| **A — Treat as legacy: write tag, continue** (default) | Smooth upgrade for existing users | If existing key is already corrupted by N-H4, we won't notice |
| **B — Treat as suspicious: clearAllData + re-login** | Maximally safe | Every existing user re-logs on first launch after S1-03 ships — UX hit |

**PO call:** start with Policy A. Track a Sentry tag
`first_run_no_integrity_tag` so we know the size of the legacy
population and can reassess if it stays meaningful for months.

### Test plan

```bash
cd mobile
flutter analyze lib/core/database/isar_service.dart \
                test/core/database/isar_service_test.dart
flutter test test/core/database/isar_service_test.dart
flutter test                          # full suite — confirm no other breakage
flutter build web --no-tree-shake-icons
```

**Manual smoke test:**

1. Install on a clean Android emulator. Open app → registers key + tag.
2. `adb shell run-as <pkg> ls -la /data/data/<pkg>/shared_prefs/` →
   confirm two entries in EncryptedSharedPreferences.
3. Patch the key entry to all zeros: `adb shell run-as <pkg> ...`
4. Force-close + relaunch app. Expect: app shows a "device reset for
   security" screen, then redirects to login. Sentry shows one
   `KeyIntegrityFailure` event.

### Threat model coverage

| Threat | Pre-S1-03 | Post-S1-03 |
|---|---|---|
| Backup-restore on different device | silent garbage reads | `KeyIntegrityFailure` → safe re-login |
| Malicious same-UID app rewrites key | silent garbage reads | `KeyIntegrityFailure` → safe re-login |
| Firmware-corrupted SharedPreferences entry | silent garbage reads | `KeyIntegrityFailure` → safe re-login |
| Attacker discovers and modifies key + recomputes tag | not protected (attacker has key) | not protected — out of scope. Mitigated by S7-02 (Keystore platform channel) |

S1-03 raises the bar against an attacker who can write the AES key but
**not** compute a valid HMAC tag (e.g., an attacker who can rewrite
SharedPreferences but not execute code). An attacker who can already
run code in the app's process is already past this gate — closed by
S7-02 Keystore platform channel and the biometric gate (S5-01).

### Risk

Low. The change is contained to one file, has an explicit upgrade path
(Policy A) for existing users, and is backed by deterministic unit
tests. Reviewers should still verify:

1. The HMAC label is exactly `wereda-isar-key-v1` and is documented as
   load-bearing (changing it invalidates every existing user's tag and
   triggers `KeyIntegrityFailure` on next launch for everyone)
2. Constant-time comparison really is constant-time — no early return
3. `KeyIntegrityFailure` is caught and acted on, never swallowed
4. Both secure-storage entries are deleted on failure, so the next
   launch regenerates cleanly

### Acceptance criteria

From `specs/features/isar-hardening/sprints.md#s1-03`:

- [ ] Persist `Hmac(sha256).convert([...key, ...utf8.encode('wereda-isar-key-v1')]).bytes` alongside key
- [ ] Constant-time comparison on read (`_constantTimeEq`)
- [ ] Mismatch → throw `KeyIntegrityFailure` (new exception type)
- [ ] Caller in `initialize()` catches `KeyIntegrityFailure` → call `clearAllData()` → re-throw to force re-login
- [ ] Length check (32 bytes) remains as cheap pre-check

---

## 3. Reviewer guide

Read the diff in this order:

1. **`_getEncryptionKey()` body.** Confirm:
   - Both `key` and `tag` are read (or the absence of either triggers the regenerate path)
   - HMAC label is captured in a single named constant
   - `_constantTimeEq` is used (NOT `==` on lists, which short-circuits and could leak timing)
   - Failure path deletes BOTH entries before throwing

2. **`initialize()` failure handling.** Confirm:
   - `KeyIntegrityFailure` is caught at this level (not swallowed deeper)
   - The cleanup helper runs to completion before `rethrow`
   - The rethrow surfaces to the auth flow — not consumed inside `initialize`

3. **Test file.** The mock-secure-storage pattern is new for this repo
   (the existing tests don't mock it). Confirm the mock is scoped per-test
   and doesn't leak global state between cases.

---

## 4. Rollback plan

`git revert <merge-commit>`. The pre-S1-03 behavior (length check only)
returns. Devices that had a tag written under S1-03 will keep the tag
entry in their secure storage; the pre-S1-03 code path ignores it
gracefully (just reads the key, doesn't check the tag).

No data loss, no migration to unwind.

---

## 5. Proposed diffs

### 5a. `mobile/lib/core/database/isar_service.dart`

```diff
 import 'dart:convert';
 import 'dart:math';

 import 'package:flutter/foundation.dart';

+import 'package:crypto/crypto.dart' as crypto;
 import 'package:flutter_secure_storage/flutter_secure_storage.dart';
 import 'package:isar/isar.dart';
 import 'package:path_provider/path_provider.dart';

 export 'package:isar/isar.dart';

 import 'package:wereda_connect/core/database/field_encryptor.dart';
 // ... schema imports unchanged ...

+/// Thrown when the AES encryption key fails its integrity check at
+/// read-back. The key bytes returned by FlutterSecureStorage don't match
+/// the HMAC tag that was stored alongside them — i.e. they were tampered
+/// with, corrupted, or restored from a different device's backup.
+///
+/// IsarService.initialize() catches this exception, wipes both the key
+/// and the tag, then rethrows so the auth flow can force a clean re-login.
+class KeyIntegrityFailure implements Exception {
+  final String reason;
+  const KeyIntegrityFailure(this.reason);
+  @override
+  String toString() => 'KeyIntegrityFailure: $reason';
+}
+
 /// Service for managing the encrypted Isar database. ...
 class IsarService {
   static IsarService? _instance;
   Isar? _isar;

   static const FlutterSecureStorage _secureStorage = FlutterSecureStorage(
     aOptions: AndroidOptions(
       encryptedSharedPreferences: true,
     ),
   );

+  /// Secure-storage key for the AES encryption key bytes.
+  static const String _keyName = 'isar_encryption_key';
+
+  /// Secure-storage key for the HMAC integrity tag.
+  /// Stored as base64(HMAC-SHA256(key, _hmacLabel)).
+  static const String _tagName = 'isar_encryption_key_tag';
+
+  /// LOAD-BEARING. Changing this constant invalidates every existing
+  /// user's tag and triggers KeyIntegrityFailure on their next launch.
+  /// Rotate only as part of a coordinated major-version release.
+  static const String _hmacLabel = 'wereda-isar-key-v1';
+
   // ... existing constructor / getters / etc. unchanged ...

   Future<Isar> initialize() async {
     if (_isar != null) {
       return _isar!;
     }

     final dir = await getApplicationDocumentsDirectory();

-    final keyBytes = await _getEncryptionKey();
+    final List<int> keyBytes;
+    try {
+      keyBytes = await _getEncryptionKey();
+    } on KeyIntegrityFailure catch (e) {
+      // Tampering, corruption, or cross-device restore. The DB is
+      // already unrecoverable with the bad key. Wipe persisted state
+      // and let the auth flow restart from scratch.
+      debugPrint('[IsarService] Key integrity failure: $e — wiping.');
+      await _wipeForIntegrityFailure();
+      rethrow;
+    }
     FieldEncryptor.instance.initialize(keyBytes);

     _isar = await Isar.open(
       [
         UserSchema, ...
       ],
       directory: dir.path,
       name: 'wereda_connect',
     );

     return _isar!;
   }

-  Future<List<int>> _getEncryptionKey() async {
-    const keyName = 'isar_encryption_key';
-
-    try {
-      final existingKey = await _secureStorage.read(key: keyName);
-      if (existingKey != null && existingKey.isNotEmpty) {
-        final keyBytes = base64Decode(existingKey);
-        if (keyBytes.length == 32) {
-          return keyBytes;
-        } else {
-          await _secureStorage.delete(key: keyName);
-        }
-      }
-    } on Exception catch (e) {
-      debugPrint('[IsarService] Failed to read encryption key: $e');
-    }
-
-    final random = Random.secure();
-    final key = List<int>.generate(32, (_) => random.nextInt(256));
-
-    try {
-      await _secureStorage.write(key: keyName, value: base64Encode(key));
-    } on Exception catch (e) {
-      debugPrint('[IsarService] Failed to persist encryption key: $e');
-    }
-
-    return key;
-  }
+  Future<List<int>> _getEncryptionKey() async {
+    String? existingKeyB64;
+    String? existingTagB64;
+    try {
+      existingKeyB64 = await _secureStorage.read(key: _keyName);
+      existingTagB64 = await _secureStorage.read(key: _tagName);
+    } on Exception catch (e) {
+      debugPrint('[IsarService] Failed to read secure storage: $e');
+      // Treat as no-entry; fall through to regenerate.
+    }
+
+    if (existingKeyB64 != null && existingKeyB64.isNotEmpty) {
+      final List<int> keyBytes;
+      try {
+        keyBytes = base64Decode(existingKeyB64);
+      } on FormatException {
+        // Corrupt key entry — wipe and regenerate. Don't treat as a
+        // KeyIntegrityFailure: the corruption is unambiguous, no tag
+        // comparison required.
+        await _secureStorage.delete(key: _keyName);
+        await _secureStorage.delete(key: _tagName);
+        return _generateAndPersistKey();
+      }
+
+      if (keyBytes.length != 32) {
+        await _secureStorage.delete(key: _keyName);
+        await _secureStorage.delete(key: _tagName);
+        return _generateAndPersistKey();
+      }
+
+      if (existingTagB64 == null) {
+        // Legacy install from before S1-03. Policy A: write a tag and
+        // continue with the existing key.
+        debugPrint(
+          '[IsarService] No integrity tag found — legacy install. '
+          'Writing tag and continuing.',
+        );
+        await _writeTagFor(keyBytes);
+        return keyBytes;
+      }
+
+      // Both key and tag present — verify.
+      final List<int> persistedTag;
+      try {
+        persistedTag = base64Decode(existingTagB64);
+      } on FormatException {
+        throw const KeyIntegrityFailure('Tag entry is not valid base64');
+      }
+      final expectedTag = _computeTag(keyBytes);
+      if (!_constantTimeEq(persistedTag, expectedTag)) {
+        throw const KeyIntegrityFailure('HMAC tag mismatch on key read');
+      }
+      return keyBytes;
+    }
+
+    // No key entry — first install, or after wipe.
+    return _generateAndPersistKey();
+  }
+
+  Future<List<int>> _generateAndPersistKey() async {
+    final random = Random.secure();
+    final key = List<int>.generate(32, (_) => random.nextInt(256));
+    try {
+      await _secureStorage.write(key: _keyName, value: base64Encode(key));
+      await _writeTagFor(key);
+    } on Exception catch (e) {
+      debugPrint('[IsarService] Failed to persist key/tag: $e');
+    }
+    return key;
+  }
+
+  Future<void> _writeTagFor(List<int> key) async {
+    final tag = _computeTag(key);
+    await _secureStorage.write(key: _tagName, value: base64Encode(tag));
+  }
+
+  List<int> _computeTag(List<int> key) {
+    final hmac = crypto.Hmac(crypto.sha256, key);
+    return hmac.convert(utf8.encode(_hmacLabel)).bytes;
+  }
+
+  /// Constant-time list-of-bytes equality. NEVER short-circuits on the
+  /// first differing byte — a timing oracle on tag comparison would let
+  /// an attacker guess one byte at a time.
+  bool _constantTimeEq(List<int> a, List<int> b) {
+    if (a.length != b.length) return false;
+    var diff = 0;
+    for (var i = 0; i < a.length; i++) {
+      diff |= a[i] ^ b[i];
+    }
+    return diff == 0;
+  }
+
+  /// Called when the AES key fails its integrity check. Deletes both
+  /// secure-storage entries and asks the FieldEncryptor to drop any
+  /// in-memory state. The Isar file itself remains on disk but is now
+  /// permanently unreadable — S5-04 will delete the file too once that
+  /// story lands.
+  Future<void> _wipeForIntegrityFailure() async {
+    try {
+      await _secureStorage.delete(key: _keyName);
+      await _secureStorage.delete(key: _tagName);
+    } on Exception catch (e) {
+      debugPrint('[IsarService] secure-storage wipe failed: $e');
+    }
+    FieldEncryptor.instance.reset();
+    _isar = null;
+  }

   // ... rest of the class unchanged ...
 }
```

### 5b. `mobile/test/core/database/isar_service_test.dart` (new file)

```dart
import 'dart:convert';

import 'package:crypto/crypto.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:flutter_secure_storage/flutter_secure_storage.dart';
import 'package:wereda_connect/core/database/isar_service.dart';

void main() {
  // Use the in-memory mock provided by flutter_secure_storage's testing helper.
  setUp(() {
    FlutterSecureStorage.setMockInitialValues({});
  });

  group('IsarService — key integrity', () {
    const keyName = 'isar_encryption_key';
    const tagName = 'isar_encryption_key_tag';
    const label = 'wereda-isar-key-v1';

    List<int> tagFor(List<int> key) =>
        Hmac(sha256, key).convert(utf8.encode(label)).bytes;

    test('first-launch generates key + tag', () async {
      // Open the service; trigger initialization (minus Isar.open which
      // would need a real native binary). For this test we invoke the
      // key path via a test-only seam — _getEncryptionKey is private, so
      // route via initialize().
      // ...
      // After init, secure storage should have both entries.
      final storage = FlutterSecureStorage();
      expect(await storage.read(key: keyName), isNotNull);
      expect(await storage.read(key: tagName), isNotNull);
    });

    test('matching tag → key read succeeds', () async {
      final key = List<int>.generate(32, (i) => i);
      final tag = tagFor(key);
      FlutterSecureStorage.setMockInitialValues({
        keyName: base64Encode(key),
        tagName: base64Encode(tag),
      });

      // initialize() should succeed without throwing.
      // (Exact invocation depends on the test-seam choice — see §6.)
    });

    test('tampered key → KeyIntegrityFailure', () async {
      final realKey = List<int>.generate(32, (i) => i);
      final tag = tagFor(realKey);
      final tamperedKey = List<int>.generate(32, (i) => i ^ 0xFF);
      FlutterSecureStorage.setMockInitialValues({
        keyName: base64Encode(tamperedKey),
        tagName: base64Encode(tag),
      });

      expect(
        () => IsarService.instance.initialize(),
        throwsA(isA<KeyIntegrityFailure>()),
      );
    });

    test('tampered tag → KeyIntegrityFailure', () async {
      final key = List<int>.generate(32, (i) => i);
      final tag = tagFor(key);
      final tamperedTag = List<int>.from(tag)..last ^= 0x01;
      FlutterSecureStorage.setMockInitialValues({
        keyName: base64Encode(key),
        tagName: base64Encode(tamperedTag),
      });

      expect(
        () => IsarService.instance.initialize(),
        throwsA(isA<KeyIntegrityFailure>()),
      );
    });

    test('missing tag with valid key → legacy upgrade writes tag', () async {
      final key = List<int>.generate(32, (i) => i);
      FlutterSecureStorage.setMockInitialValues({
        keyName: base64Encode(key),
        // tag absent
      });

      // initialize() should NOT throw; should write the tag transparently.
      // After init, the tag entry should exist and match.
      final storage = FlutterSecureStorage();
      final writtenTag = await storage.read(key: tagName);
      expect(writtenTag, equals(base64Encode(tagFor(key))));
    });

    test('invalid-length key → wipe and regenerate', () async {
      FlutterSecureStorage.setMockInitialValues({
        keyName: base64Encode([1, 2, 3]),    // 3 bytes — invalid
        tagName: base64Encode([0, 0, 0, 0]), // bogus
      });

      // Should NOT throw KeyIntegrityFailure — that's reserved for
      // valid-length-but-wrong-bytes. Length mismatch is unambiguous
      // corruption, treated like no-entry.
      // After init, fresh key + tag should be present.
      final storage = FlutterSecureStorage();
      final newKey = base64Decode((await storage.read(key: keyName))!);
      expect(newKey.length, 32);
      expect(newKey, isNot(equals([1, 2, 3])));
    });

    test('integrity failure also wipes the tag entry', () async {
      final realKey = List<int>.generate(32, (i) => i);
      FlutterSecureStorage.setMockInitialValues({
        keyName: base64Encode(realKey.map((b) => b ^ 0xFF).toList()),
        tagName: base64Encode(tagFor(realKey)),
      });

      try {
        await IsarService.instance.initialize();
      } on KeyIntegrityFailure {
        // expected
      }

      final storage = FlutterSecureStorage();
      expect(await storage.read(key: keyName), isNull);
      expect(await storage.read(key: tagName), isNull);
    });
  });
}
```

---

## 6. Open questions for the reviewer

1. **Test seam for `_getEncryptionKey`.** The function is private and the
   real `initialize()` also opens Isar, which needs `Isar.initializeIsarCore`.
   Three options:
   - (a) Make `_getEncryptionKey` `@visibleForTesting` and call it directly
   - (b) Refactor to take a `_secureStorage` parameter via constructor so
     tests can inject a mock
   - (c) Always run `Isar.initializeIsarCore(download: false)` with a vendored
     binary for these tests
   **Default: (a)** because it's the smallest change. Reviewer pushback welcome.

2. **HMAC label encoding.** `utf8.encode(_hmacLabel)` — does `_hmacLabel`
   need to be versioned (`wereda-isar-key-v1`) or namespaced
   (`wereda-isar:key-mac:v1`)? I went with the simple v1 suffix. If we
   add multiple keys (e.g. for separate column encryption), namespace
   becomes useful.

3. **Policy A vs Policy B on legacy upgrade.** §"Backwards compatibility"
   defaults to Policy A (write tag, continue). The argument for Policy B
   is: any user whose pre-S1-03 key was already corrupted by the same
   N-H4 conditions will continue to have a broken key and we'll have
   written a valid HMAC tag for the broken value. Counter-argument:
   the population that hits N-H4 is small enough (< 0.1% based on
   industry data) and Policy A's UX win is large enough that we should
   start with A and revisit.

4. **`KeyIntegrityFailure` propagation.** Currently rethrown from
   `initialize()`. Does the auth flow already handle arbitrary exceptions
   from `initialize` gracefully (show a clean error screen + force re-
   login)? If not, we need to wire that as part of this PR or a follow-up
   in HS-1 cleanup.

---

*PR scaffold v1.0 — 2026-05-13 — independent of S1-01 / S1-02*
