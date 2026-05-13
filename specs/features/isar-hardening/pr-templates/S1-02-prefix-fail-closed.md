# PR Scaffold — S1-02: `enc:v1:` prefix + fail-closed decrypt

> **Status:** Design ready · **Depends on:** S1-01 merged to `main`
> **Sprint:** HS-1 · **Epic:** E1 — Encrypt at rest
> **Story catalog:** `specs/features/isar-hardening/sprints.md#s1-02`
> **Reviewer needed:** security + 1 mobile dev

This is a design scaffold — the code changes are presented as diffs below
rather than applied to the working tree, because they sit on top of S1-01.
Once S1-01 is merged, the engineer follows §1 to materialize this PR.

---

## 0. TL;DR

Add `enc:v1:` prefix to every ciphertext, and make `decrypt()` throw
`FieldDecryptionException` on **any** failure (missing prefix, bad base64,
truncated body, MAC verify fail). Add a one-shot `_legacyPlaintextAllowed`
flag used only by the HS-3 migration so existing unprefixed values can still
be read once, then re-encrypted with a prefix.

**Closes the downgrade-attack vector** identified as **C-1** in the security
review: today, an attacker who can write to the Isar file can replace
`base64(...)` with a literal `+251911234567` and the app accepts it
because `decrypt` falls through to returning the input on failure.

---

## 1. How to ship this

```bash
# Prerequisite: S1-01 merged to main
git fetch origin
git log origin/main --oneline | head -5     # confirm S1-01 commit present

# 1. Branch from main (NOT from the S1-01 branch — that's been merged)
git checkout -b hs1/s1-02-prefix-fail-closed origin/main

# 2. Apply the diffs in §5 below. They touch:
#    - mobile/lib/core/database/field_encryptor.dart
#    - mobile/lib/core/database/field_encryptor_web.dart  (signature parity)
#    - mobile/test/core/database/field_encryptor_test.dart

# 3. Run the gates
cd mobile
flutter analyze lib/core/database/field_encryptor.dart \
                lib/core/database/field_encryptor_web.dart \
                test/core/database/field_encryptor_test.dart
flutter test test/core/database/field_encryptor_test.dart
flutter build web --no-tree-shake-icons 2>&1 | tail -5
cd ..

# 4. Commit + push + PR
git add mobile/lib/core/database/field_encryptor.dart \
        mobile/lib/core/database/field_encryptor_web.dart \
        mobile/test/core/database/field_encryptor_test.dart
git commit -m "$(cat <<'EOF'
feat(mobile): enc:v1: ciphertext prefix + fail-closed decrypt

Every ciphertext now starts with the literal "enc:v1:" prefix. decrypt()
throws FieldDecryptionException on missing prefix, bad base64, truncated
body, or MAC verify failure — never returns the on-disk bytes as plaintext.
This closes the downgrade-attack vector where an attacker could replace
encrypted blobs with literal plaintext.

A one-shot _legacyPlaintextAllowed flag on initialize() permits unprefixed
values to read through unchanged. The HS-3 migration sets this true, runs
once, then re-init with the flag false. Production reads always run with
the flag false; the doc-comment in the field encryptor makes this load-
bearing.

Refs: specs/features/isar-hardening/sprints.md#s1-02
EOF
)"
git push -u origin hs1/s1-02-prefix-fail-closed
gh pr create --title "feat(mobile): enc:v1: prefix + fail-closed decrypt [S1-02]" \
             --body "$(cat specs/features/isar-hardening/pr-templates/S1-02-prefix-fail-closed.md | sed -n '/^## 2. PR description/,/^## 3. /p' | head -n -1)"
```

---

## 2. PR description

### Summary

Tags every ciphertext with the literal prefix `enc:v1:` and makes the
decrypt path fail closed. `decrypt()` now throws `FieldDecryptionException`
on every failure mode that previously returned the input unchanged:

| Failure mode | Before this PR | After this PR |
|---|---|---|
| No `enc:v1:` prefix | returned ciphertext as-is | throws `FieldDecryptionException` |
| Invalid base64 body | returned ciphertext as-is | throws `FieldDecryptionException` |
| Body shorter than 28 bytes | returned ciphertext as-is | throws `FieldDecryptionException` |
| GCM MAC verify failed | returned ciphertext as-is | throws `FieldDecryptionException` |
| Encryptor not initialized | returned plaintext | **encrypt** throws `StateError`; **decrypt** throws `StateError` |

A one-shot `_legacyPlaintextAllowed` flag on `initialize()` exists for the
HS-3 migration only. When `true`, `decrypt` returns unprefixed values as-is
(so the migration can read existing plaintext, re-encrypt, write back). After
the migration completes, production re-initializes with `legacyPlaintextAllowed: false`
and the door is closed.

### Why

`docs/mobile/isar-implementation.md` security review (May 2026), finding
**C-1**: today, an attacker with write access to the Isar file (root,
malicious backup-restore, debuggable build via `adb`) can replace a
victim's encrypted blob with a literal plaintext string. The fail-open
`decrypt` path returns the planted plaintext unchanged. The app reads
attacker-controlled values as if they were legitimate.

The prefix flips this from "silently accepted" to "raises an exception that
crash reporting sees within minutes."

### What changes

- `mobile/lib/core/database/field_encryptor.dart`
  - New `class FieldDecryptionException implements Exception`
  - New `static const String cipherPrefix = 'enc:v1:'`
  - New `bool _legacyPlaintextAllowed` (default false)
  - `initialize(keyBytes, {bool legacyPlaintextAllowed = false})`
  - `encrypt()`: throws `StateError` if uninitialized (was: returned plaintext)
  - `encrypt()`: returns `'$cipherPrefix${base64Encode(combined)}'` (was: bare base64)
  - `decrypt()`: throws `StateError` if uninitialized (was: returned input)
  - `decrypt()`: prefix check before anything else; throws `FieldDecryptionException`
    if missing AND `_legacyPlaintextAllowed == false`
  - `decrypt()`: throws `FieldDecryptionException` on base64 failure (was: returned)
  - `decrypt()`: throws `FieldDecryptionException` on length < 28 (was: returned)
  - `decrypt()`: throws `FieldDecryptionException` on MAC fail (was: returned)
  - New `_report(reason)` hook for telemetry — currently logs via `debugPrint`,
    wires to Sentry in S6-01

- `mobile/lib/core/database/field_encryptor_web.dart`
  - `initialize()` accepts the new `legacyPlaintextAllowed` named param,
    ignores it (no-op on web)

- `mobile/test/core/database/field_encryptor_test.dart`
  - Tests for "encrypt returns plaintext when not initialized" → flipped to
    `throwsA(isA<StateError>())`
  - Same for the decrypt-uninitialized test
  - "decrypt returns non-base64 input as-is" → flipped to expect `FieldDecryptionException`
  - "decrypt returns short base64 as-is" → flipped to expect `FieldDecryptionException`
  - "data encrypted with one key cannot be decrypted with another" → flipped to expect `FieldDecryptionException`
  - The four AAD-binding tests added in S1-01 → flipped from "returns ciphertext" to "throws `FieldDecryptionException`"
  - **New** tests: `enc:v1:` prefix is present on encrypt output; legacy-mode allow; tampered MAC byte throws.

### What does NOT change in this PR

- AAD binding — already shipped in S1-01.
- Key-integrity HMAC on read-back — that's S1-03.
- Repository wiring (encrypt PII fields on User/Crop writes) — HS-2.
- Migration that re-encrypts existing rows — HS-3.

### Backwards compatibility

This is the **breaking** change for any caller that relies on the fail-open
behavior. Per the grep audit in S1-01, there are still zero production
callers of `encrypt/decrypt`. The only callers are tests, which this PR
updates.

For data already in production (which today is plaintext, since no caller
encrypts), the legacy-mode flag is the migration bridge. HS-3 runs once
with `legacyPlaintextAllowed: true`, encrypts everything, writes the new
prefix, then production re-initializes with `false`.

### Test plan

```bash
cd mobile
flutter analyze lib/core/database/field_encryptor.dart \
                lib/core/database/field_encryptor_web.dart \
                test/core/database/field_encryptor_test.dart
flutter test test/core/database/field_encryptor_test.dart
flutter build web --no-tree-shake-icons   # confirm web stub compiles
```

Expected: all tests green. Count rises from 21 (post-S1-01) to ~25 with
the new prefix/legacy/MAC tampering tests.

### Threat model coverage

| Threat | Pre-S1-02 | Post-S1-02 |
|---|---|---|
| Plaintext substitution (downgrade attack) | **silently accepted** | **rejected, exception raised** |
| Truncated ciphertext (potentially malicious) | silently returned | rejected |
| Wrong key | silently returned | rejected |
| Cross-AAD swap (S1-01 territory) | rejected by MAC | rejected, but now as a *typed* exception |
| Encryptor uninitialized at write time | silently writes plaintext | crash early — visible in Sentry |

### Risk

Low-medium. The change is contained to a single file, but it changes the
error contract — anything that catches `decrypt()`'s fall-through as a
success will now see exceptions. Mitigated by: (a) no production callers
yet, (b) the test suite explicitly exercises every failure path.

The medium part: developers using the encryptor in HS-2 will need to handle
`FieldDecryptionException` consciously. Document this in the §7 of the
implementation guide (already done — references it).

### Acceptance criteria

From `specs/features/isar-hardening/sprints.md#s1-02`:

- [ ] `static const String _cipherPrefix = 'enc:v1:'`
- [ ] `encrypt()` returns `'enc:v1:${base64Encode(...)}'`
- [ ] `decrypt()` throws `FieldDecryptionException('Missing cipher prefix')` on input without prefix (unless `legacyPlaintextAllowed` is true)
- [ ] `decrypt()` throws `FieldDecryptionException('Invalid base64')` on malformed body
- [ ] `decrypt()` throws `FieldDecryptionException('Truncated ciphertext')` if combined length < 28
- [ ] `decrypt()` throws `FieldDecryptionException('MAC verification failed')` on `SecretBoxAuthenticationError`
- [ ] No code path returns the input value as plaintext on failure

---

## 3. Reviewer guide

Two questions to answer in review:

1. **Is the fail-closed contract complete?** Walk the `decrypt()` body
   top-to-bottom and confirm every `return ciphertext;` is gone except
   the one inside the legacy-mode branch.
2. **Is the legacy-mode escape hatch tight?** The flag must default to
   `false`, must only be settable via `initialize(..., legacyPlaintextAllowed: true)`,
   and must be cleared by `reset()`. Confirm there's no public setter
   and no path that flips it without the migration in scope.

Spot-check the test file. The renamed tests should each have a comment
explaining "previously returned X, now throws Y" so the change is
traceable in git blame.

---

## 4. Rollback plan

`git revert <merge-commit>`. The PR has no migration, no schema change,
no DB write, and (still) no production callers. Tests will go back to
the S1-01 baseline.

If HS-3 has already shipped and depends on the prefix being present in
production rows, rollback becomes harder — but the schedule sequences
S1-02 well before HS-3, so this should not happen.

---

## 5. Proposed diffs

### 5a. `mobile/lib/core/database/field_encryptor.dart`

```diff
 import 'dart:convert';

 import 'package:cryptography/cryptography.dart';
 import 'package:flutter/foundation.dart';

+/// Thrown by [FieldEncryptor.decrypt] when a ciphertext fails any of:
+/// - missing `enc:v1:` version prefix (downgrade attack or legacy data)
+/// - invalid base64 body
+/// - truncated body (< 12-byte nonce + 16-byte MAC = 28 bytes)
+/// - GCM MAC verification failure (wrong key, tampering, or AAD mismatch)
+///
+/// Callers MUST treat this as abnormal. Never catch-and-fallback to the
+/// raw input value — doing so reintroduces the downgrade-attack surface
+/// this exception was added to close.
+class FieldDecryptionException implements Exception {
+  final String reason;
+  const FieldDecryptionException(this.reason);
+  @override
+  String toString() => 'FieldDecryptionException: $reason';
+}
+
 /// Provides AES-256-GCM field-level encryption for PII fields stored in Isar.
 /// ... (existing doc comment unchanged) ...
 class FieldEncryptor {
   static FieldEncryptor? _instance;

   final AesGcm _algorithm = AesGcm.with256bits();
   SecretKey? _secretKey;
   bool _initialized = false;

+  /// Version tag prepended to every ciphertext written by [encrypt].
+  /// [decrypt] rejects anything without this prefix unless
+  /// [_legacyPlaintextAllowed] is set — see [initialize].
+  static const String cipherPrefix = 'enc:v1:';
+
+  /// MIGRATION-ONLY escape hatch. When true, [decrypt] returns unprefixed
+  /// inputs as-is so the HS-3 migration can read existing plaintext rows.
+  /// Production reads MUST always run with this `false`.
+  bool _legacyPlaintextAllowed = false;
+
   // Private constructor for singleton pattern
   FieldEncryptor._();

   /// Get the singleton instance of FieldEncryptor.
   static FieldEncryptor get instance {
     _instance ??= FieldEncryptor._();
     return _instance!;
   }

   /// Whether the encryptor has been initialized with a key.
   bool get isInitialized => _initialized;

   /// Initialize the encryptor with the encryption key bytes.
   ///
   /// Must be called once during app startup (from IsarService.initialize()).
   /// The [keyBytes] should be the 32-byte AES-256 key from secure storage.
-  void initialize(List<int> keyBytes) {
+  ///
+  /// [legacyPlaintextAllowed] is for the HS-3 v1→v2 migration only. When
+  /// true, [decrypt] returns unprefixed input as-is (so plaintext legacy
+  /// rows read through unchanged). Migration code flips it to false again
+  /// after the migration completes.
+  void initialize(
+    List<int> keyBytes, {
+    bool legacyPlaintextAllowed = false,
+  }) {
     if (keyBytes.length != 32) {
       throw ArgumentError('Encryption key must be 32 bytes (AES-256)');
     }
     _secretKey = SecretKey(Uint8List.fromList(keyBytes));
+    _legacyPlaintextAllowed = legacyPlaintextAllowed;
     _initialized = true;
   }

   /// Build the AAD ... (S1-01 _aad helper unchanged)

   /// Encrypt a plaintext string to a base64-encoded ciphertext.
   ///
-  /// Format: base64(nonce + ciphertext + mac), with AAD bound to the trio
-  /// `(collection, column, rowKey)`. See [_aad] for the rationale.
-  ///
-  /// Returns plaintext as fallback if the encryptor is not initialized.
-  /// This ensures the app works during first-launch before the key is ready.
-  /// Note: fail-closed behavior on uninitialized encryptor lands in S1-02.
+  /// Format: `enc:v1:base64(nonce + ciphertext + mac)`, with AAD bound to
+  /// the trio `(collection, column, rowKey)`. See [_aad] for the rationale.
+  ///
+  /// **Fail-closed:** throws [StateError] if [initialize] has not run.
+  /// Never returns plaintext on error — silent encryption bypass is the
+  /// exact bug this throw is meant to surface.
   Future<String> encrypt(
     String plaintext, {
     required String collection,
     required String column,
     required String rowKey,
   }) async {
     if (!_initialized || _secretKey == null) {
-      // Encryptor not yet initialized — storing plaintext.
-      // IsarService.initialize() must complete before repositories write PII.
-      // TODO(S1-02): replace this fail-open path with a StateError throw.
-      debugPrint('[FieldEncryptor] encrypt() called before initialization');
-      return plaintext;
+      throw StateError(
+        'FieldEncryptor.encrypt called before initialize(). '
+        'IsarService.initialize() must complete before any repository write.',
+      );
     }

     final secretBox = await _algorithm.encrypt(
       utf8.encode(plaintext),
       secretKey: _secretKey!,
       aad: utf8.encode(_aad(collection, column, rowKey)),
     );

     // Pack nonce + ciphertext + mac ... (unchanged)
-    return base64Encode(combined);
+    return '$cipherPrefix${base64Encode(combined)}';
   }

   /// Decrypt a base64-encoded ciphertext back to plaintext.
   ///
-  /// Caller MUST supply the same `(collection, column, rowKey)` trio used at
-  /// encrypt time — AAD mismatch fails the MAC check.
-  ///
-  /// Returns the input string as-is if:
-  /// - The encryptor is not initialized (graceful fallback)
-  /// - The input doesn't appear to be encrypted (not valid base64 or too short)
-  /// - Decryption fails (data was stored before encryption was enabled,
-  ///   or the AAD trio doesn't match)
-  ///
-  /// This graceful fallback ensures existing unencrypted data continues to
-  /// work after encryption is enabled — a migration-friendly design.
-  /// Note: this fail-open behavior is itself a downgrade-attack vector and
-  /// is replaced with `enc:v1:` prefix + fail-closed in S1-02.
+  /// Fail-closed contract:
+  /// - Uninitialized        → [StateError]
+  /// - No `enc:v1:` prefix  → [FieldDecryptionException] (unless legacy mode)
+  /// - Bad base64           → [FieldDecryptionException]
+  /// - Truncated body       → [FieldDecryptionException]
+  /// - MAC verify failed    → [FieldDecryptionException]
+  ///
+  /// The legacy-mode escape (returning unprefixed input unchanged) is
+  /// gated by [_legacyPlaintextAllowed] and is intended only for the HS-3
+  /// migration window.
   Future<String> decrypt(
-    String ciphertext, {
+    String value, {
     required String collection,
     required String column,
     required String rowKey,
   }) async {
     if (!_initialized || _secretKey == null) {
-      return ciphertext;
+      throw StateError(
+        'FieldEncryptor.decrypt called before initialize(). '
+        'IsarService.initialize() must complete before any repository read.',
+      );
+    }
+
+    if (!value.startsWith(cipherPrefix)) {
+      if (_legacyPlaintextAllowed) {
+        // ONLY reachable while the v2-encryption migration is running.
+        return value;
+      }
+      _report('Missing cipher prefix on read for $collection:$column');
+      throw const FieldDecryptionException('Missing cipher prefix');
     }

+    final body = value.substring(cipherPrefix.length);
+    final List<int> combined;
     try {
-      final combined = base64Decode(ciphertext);
-
-      // AES-GCM: 12-byte nonce + ciphertext + 16-byte MAC
-      // Minimum length: 12 (nonce) + 0 (empty plaintext) + 16 (MAC) = 28
-      if (combined.length < 28) {
-        return ciphertext; // Too short to be encrypted data
-      }
-
-      final nonce = combined.sublist(0, 12);
-      final mac = combined.sublist(combined.length - 16);
-      final encrypted = combined.sublist(12, combined.length - 16);
-
-      final secretBox = SecretBox(
-        encrypted,
-        nonce: nonce,
-        mac: Mac(mac),
-      );
-
-      final decrypted = await _algorithm.decrypt(
-        secretBox,
-        secretKey: _secretKey!,
-        aad: utf8.encode(_aad(collection, column, rowKey)),
-      );
-
-      return utf8.decode(decrypted);
-    } on FormatException {
-      // Not valid base64 — unencrypted legacy data, return as-is.
-      return ciphertext;
-    } on SecretBoxAuthenticationError {
-      // MAC check failed — wrong key, tampered data, or AAD mismatch
-      // (column/row swap). Log so it surfaces in crash reporting.
-      debugPrint(
-        '[FieldEncryptor] MAC verification failed for '
-        '$collection:$column — possible key mismatch, AAD mismatch, or '
-        'tampering. Returning ciphertext as-is.',
-      );
-      return ciphertext;
+      combined = base64Decode(body);
+    } on FormatException {
+      _report('Ciphertext is not valid base64 for $collection:$column');
+      throw const FieldDecryptionException('Invalid base64');
     }
+    if (combined.length < 28) {
+      _report('Ciphertext truncated (length=${combined.length}) for $collection:$column');
+      throw const FieldDecryptionException('Truncated ciphertext');
+    }
+
+    final nonce = combined.sublist(0, 12);
+    final mac = combined.sublist(combined.length - 16);
+    final encrypted = combined.sublist(12, combined.length - 16);
+
+    try {
+      final plain = await _algorithm.decrypt(
+        SecretBox(encrypted, nonce: nonce, mac: Mac(mac)),
+        secretKey: _secretKey!,
+        aad: utf8.encode(_aad(collection, column, rowKey)),
+      );
+      return utf8.decode(plain);
+    } on SecretBoxAuthenticationError {
+      _report('GCM MAC verification failed for $collection:$column');
+      throw const FieldDecryptionException('MAC verification failed');
+    }
   }

+  /// Telemetry hook. Replace with Sentry.captureMessage in S6-01.
+  /// Do NOT include the raw value or the AAD rowKey here — those can
+  /// leak PII or identifiers into the telemetry sink.
+  void _report(String reason) {
+    debugPrint('[FieldEncryptor] $reason');
+    // S6-01: Sentry.captureMessage(reason, level: SentryLevel.warning);
+  }
+
   /// Reset the encryptor (for testing or key rotation).
-  void reset() {
+  void reset() {
     _secretKey = null;
     _initialized = false;
+    _legacyPlaintextAllowed = false;
   }

   /// Reset the singleton instance (for testing only).
   static void resetInstance() {
     _instance?.reset();
     _instance = null;
   }
 }
```

### 5b. `mobile/lib/core/database/field_encryptor_web.dart`

```diff
 class FieldEncryptor {
   // ...
   bool get isInitialized => false;

-  void initialize(List<int> keyBytes) {
+  void initialize(
+    List<int> keyBytes, {
+    bool legacyPlaintextAllowed = false,
+  }) {
     // No-op on web — no Isar, no PII on disk
   }
   // ... encrypt/decrypt signatures already match (from S1-01)
 }
```

### 5c. `mobile/test/core/database/field_encryptor_test.dart`

Renames + new tests. Key changes:

```diff
-    test('encrypt returns plaintext when not initialized', () async {
-      const plaintext = '+251911234567';
-      final result = await encryptor.encrypt(...);
-      expect(result, equals(plaintext));
+    test('encrypt throws StateError when not initialized', () async {
+      expect(
+        () => encryptor.encrypt('any',
+            collection: aadCollection, column: aadColumn, rowKey: aadRowKey),
+        throwsA(isA<StateError>()),
+      );
     });

-    test('decrypt returns ciphertext when not initialized', () async {
-      ...
+    test('decrypt throws StateError when not initialized', () async {
+      expect(
+        () => encryptor.decrypt('any',
+            collection: aadCollection, column: aadColumn, rowKey: aadRowKey),
+        throwsA(isA<StateError>()),
+      );
     });

+    test('encrypt output has enc:v1: prefix', () async {
+      encryptor.initialize(generateKey());
+      final out = await encryptor.encrypt('hello',
+          collection: aadCollection, column: aadColumn, rowKey: aadRowKey);
+      expect(out, startsWith('enc:v1:'));
+    });

-    test('decrypt returns non-base64 input as-is (legacy data)', () async {
-      ...
+    test('decrypt throws on unprefixed input (downgrade attack)', () async {
+      encryptor.initialize(generateKey());
+      expect(
+        () => encryptor.decrypt('+251911234567',
+            collection: aadCollection, column: aadColumn, rowKey: aadRowKey),
+        throwsA(isA<FieldDecryptionException>()),
+      );
     });

+    test('legacy mode allows unprefixed input', () async {
+      encryptor.initialize(generateKey(), legacyPlaintextAllowed: true);
+      const legacy = '+251911234567';
+      final out = await encryptor.decrypt(legacy,
+          collection: aadCollection, column: aadColumn, rowKey: aadRowKey);
+      expect(out, equals(legacy));
+    });

+    test('decrypt throws on tampered MAC byte', () async {
+      encryptor.initialize(generateKey());
+      final blob = await encryptor.encrypt('Abebe',
+          collection: aadCollection, column: aadColumn, rowKey: aadRowKey);
+      // Flip the last byte of the MAC.
+      final body = base64Decode(blob.substring('enc:v1:'.length));
+      body[body.length - 1] ^= 0x01;
+      final tampered = 'enc:v1:${base64Encode(body)}';
+      expect(
+        () => encryptor.decrypt(tampered,
+            collection: aadCollection, column: aadColumn, rowKey: aadRowKey),
+        throwsA(isA<FieldDecryptionException>()),
+      );
+    });

-    // S1-01's AAD-binding tests asserted `result == blob` (fail-open).
-    // Flip them to expect FieldDecryptionException now.
+    // AAD binding (flipped — these now throw instead of returning the blob)
     group('AAD binding', () {
-      test('cross-row swap is rejected: ...returns ciphertext', ...);
+      test('cross-row swap throws FieldDecryptionException', () async {
+        encryptor.initialize(generateKey());
+        final blobRowA = await encryptor.encrypt('Abebe',
+            collection: 'User', column: 'displayName', rowKey: 'sub-A');
+        expect(
+          () => encryptor.decrypt(blobRowA,
+              collection: 'User', column: 'displayName', rowKey: 'sub-B'),
+          throwsA(isA<FieldDecryptionException>()),
+        );
+      });
+      // Same flip for cross-column and cross-collection.
     });
```

---

## 6. Open questions for the reviewer

1. **`cipherPrefix` is public (no leading underscore).** The HS-3 migration
   needs to skip rows that already start with the prefix (idempotency).
   Keeping it public lets the migration import it without reflection. OK?

2. **Legacy-mode flag is a runtime boolean.** A reviewer might prefer a
   compile-time `const bool kAllowLegacyDecrypt` so HS-3 must ship its own
   build flavor. Pros: zero risk of someone flipping it in prod. Cons:
   need two release artifacts. Default is the runtime flag for simplicity.

3. **`_report` writes to `debugPrint`.** Silenced in release. Acceptable
   until S6-01 wires Sentry; document explicitly so we don't ship S1-02
   to production thinking we're already alerting on failures.

4. **`StateError` vs. custom exception for uninit.** I picked `StateError`
   because it's stdlib and signals a programming error. A reviewer might
   prefer `FieldEncryptionException('Not initialized')` for consistency.
   Either works.

---

*PR scaffold v1.0 — 2026-05-13 — depends on S1-01 merged*
