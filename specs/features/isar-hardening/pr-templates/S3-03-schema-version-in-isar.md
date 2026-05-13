# PR Scaffold — S3-03: Schema version inside encrypted DB

> **Status:** Design ready · **Depends on:** S3-01 merged
> **Sprint:** HS-3 · **Epic:** E1 — Encrypt at rest
> **Story catalog:** `specs/features/isar-hardening/sprints.md#s3-03`
> **Reviewer needed:** security + 1 mobile dev · **Story size:** 3 pts · **Risk:** Low

Closes HS-3. Moves the `_kSchemaVersion` value from
`FlutterSecureStorage` to a new `Meta` collection inside Isar so an
attacker who can reset secure-storage entries cannot force migrations
to re-run on already-encrypted rows.

---

## 0. TL;DR

Today (after S3-01), `_kSchemaVersion` lives in
`FlutterSecureStorage` under the key `isar_schema_version`. An
attacker who can rewrite that entry (root, malicious same-UID app,
firmware corruption) can reset it to `0`, force the v2 migration to
re-run on already-encrypted rows, and corrupt the DB
permanently (double-encryption → reads return `enc:v1:enc:v1:...`
ciphertext after one decrypt pass).

This PR introduces a `Meta` Isar collection (singleton row, fixed
`id = 1`) that holds the schema version. The runner reads from `Meta`
first, falls back to secure storage once for installs upgrading from
S3-01, then writes `Meta` going forward and deletes the secure-storage
entry.

---

## 1. How to ship this

```bash
# Prerequisite: S3-01 stable at 100% rollout
git fetch origin
git log origin/main --oneline | grep S3-01

git checkout -b hs3/s3-03-schema-version-in-isar origin/main

# Apply the changes in §5:
#   - mobile/lib/core/database/schemas/meta.dart                  (new)
#   - mobile/lib/core/database/isar_service.dart                  (register schema)
#   - mobile/lib/core/database/migrations/migrations_runner.dart  (use Meta)
#   - mobile/test/core/database/migrations/migrations_runner_test.dart  (new tests)

cd mobile
dart run build_runner build --delete-conflicting-outputs
flutter analyze
flutter test
flutter build apk --release --dart-define=PHONE_PEPPER=$PHONE_PEPPER
cd ..

git add ...
git commit -m "$(cat <<'EOF'
feat(mobile): move schema version into encrypted Isar collection

Introduces a Meta collection (singleton row, fixed id=1) for the
schema version. Replaces the FlutterSecureStorage-based version
tracking introduced in S3-01. Closes a corruption vector where an
attacker who could rewrite the secure-storage entry could force
migrations to re-run on already-encrypted rows.

First-launch upgrade flow:
  1. Read version from Meta. If present, use it.
  2. Else: read from FlutterSecureStorage one last time.
  3. Write the version to Meta.
  4. Delete the secure-storage entry.

After this PR, secure storage never holds the schema version again.

Refs: specs/features/isar-hardening/sprints.md#s3-03
EOF
)"
git push -u origin hs3/s3-03-schema-version-in-isar
gh pr create --title "feat(mobile): schema version inside encrypted Isar [S3-03]"
```

---

## 2. PR description

### Summary

Moves the schema version from `FlutterSecureStorage` to a new
`Meta` Isar collection. The Meta collection is a singleton (fixed
`id = 1`) inside the encrypted DB; rewriting it requires owning the
AES key (which `KeyIntegrityFailure` from S1-03 protects).

### Why

After S3-01, the schema version lives in `FlutterSecureStorage`
under `isar_schema_version`. Secure storage is *not* tamper-evident
the way the AAD'd Isar collections are. The N-H5 finding in the
May security review:

> An attacker who can write to secure storage (root, malicious app
> with same UID) can reset version to 0 and force every migration to
> re-run at next launch. If migrations are not idempotent — e.g. v3's
> §15 `_migrateToV2` encrypts every phone number — re-running it on
> already-encrypted data **double-encrypts**, yielding rows that
> decrypt to `enc:v1:...` instead of `+251911234567`.

S3-01's migration *is* idempotent (skips already-`enc:v1:` rows),
which closes the immediate exploit. But future migrations may not be,
and the principle is sound: version state belongs inside the
encrypted boundary, not outside it.

### What changes

#### New schema

```dart
// mobile/lib/core/database/schemas/meta.dart
import 'package:isar/isar.dart';

part 'meta.g.dart';

@collection
class Meta {
  /// Singleton — only one Meta row ever exists. Fixed id keeps writes
  /// idempotent and lookups O(1).
  Id id = 1;

  /// Current schema version applied to this DB.
  late int schemaVersion;

  /// Audit trail: when each version was applied. Sentry-safe.
  late DateTime updatedAt;
}
```

Register in `Isar.open([...])`:

```diff
 _isar = await Isar.open(
   [
     UserSchema,
     CropListingSchema,
     // ... etc
+    MetaSchema,
   ],
   directory: dir.path,
   name: 'wereda_connect',
 );
```

#### Migrations runner

```diff
 class MigrationsRunner {
-  static const _versionKey = 'isar_schema_version';
+  /// Legacy secure-storage key. Read once on first launch after S3-03
+  /// ships; deleted after.
+  static const _legacySsKey = 'isar_schema_version';
   static const _currentVersion = 3;
   // ... existing constructor

   Future<void> runIfNeeded() async {
-    final saved = await _storage.read(key: _versionKey);
-    final version = int.tryParse(saved ?? '0') ?? 0;
+    final version = await _readVersion();

     if (version >= _currentVersion) {
       return;
     }
     // ... existing kill-switch check
     if (version < 2) await _runV2();
     if (version < 3) await _runV3();

-    await _storage.write(key: _versionKey, value: '$_currentVersion');
+    await _writeVersion(_currentVersion);
+    await _maybeDeleteLegacyKey();
   }

+  Future<int> _readVersion() async {
+    // Primary source of truth: Meta collection in Isar.
+    final meta = await _isar.metas.get(1);
+    if (meta != null) return meta.schemaVersion;
+
+    // Fallback for first launch after S3-03 ships: read once from
+    // legacy secure storage. Never reads from there again.
+    final ssRaw = await _storage.read(key: _legacySsKey);
+    final ssVersion = int.tryParse(ssRaw ?? '0') ?? 0;
+    return ssVersion;
+  }
+
+  Future<void> _writeVersion(int v) async {
+    await _isar.writeTxn(() async {
+      await _isar.metas.put(Meta()
+        ..id = 1
+        ..schemaVersion = v
+        ..updatedAt = DateTime.now().toUtc());
+    });
+  }
+
+  Future<void> _maybeDeleteLegacyKey() async {
+    try {
+      await _storage.delete(key: _legacySsKey);
+    } on Exception catch (e) {
+      // Non-fatal: the secure-storage entry stays but is unread.
+      debugPrint('[migrations_runner] legacy SS delete failed: $e');
+    }
+  }
 }
```

### What does NOT change

- The migrations themselves (v2 from S3-01, v3 from S3-02). They run
  the same way; only the version source-of-truth moves.
- The kill switch (S6-03). Still read from `RemoteConfig`.
- Any user-facing behavior.

### Backwards compatibility

**First launch after S3-03 ships:**

1. Runner calls `_readVersion()`.
2. `_isar.metas.get(1)` returns null (Meta collection didn't exist
   in S3-01).
3. Fall back to `_storage.read('isar_schema_version')` — returns the
   value persisted by S3-01.
4. Compare to `_currentVersion = 3`. If less, run migrations.
5. `_writeVersion(3)` persists the new version to Meta.
6. `_maybeDeleteLegacyKey()` removes the secure-storage entry.

**Second launch and forever after:**

1. `_isar.metas.get(1)` returns the Meta row with version `3`.
2. Runner sees `3 >= 3` and returns. No migrations, no fallback,
   no secure-storage read.

This is a one-shot bridge. After every install has run S3-03 at
least once, the legacy code path is dead. A follow-up PR can remove
it after a sprint.

### Test plan

```bash
cd mobile
flutter test test/core/database/migrations/migrations_runner_test.dart
flutter test
```

**Required test cases:**

1. **Fresh install:** no Meta row, no secure-storage entry. Runner
   defaults to version 0, runs all migrations, writes Meta to 3.
2. **Upgrade from S3-01:** Meta empty, secure storage has version 2.
   Runner reads 2 from secure storage, runs v3 migration, writes
   Meta to 3, deletes secure-storage entry.
3. **Already at v3:** Meta has 3, secure storage may have anything.
   Runner reads 3 from Meta (ignores secure storage), returns
   without running anything.
4. **Tampered Meta:** Meta has version 99 (attacker-set). Runner
   sees `99 >= 3`, skips all migrations. **Note:** this is by design.
   An attacker who can rewrite Meta has already breached the AES key
   (`KeyIntegrityFailure` from S1-03 should catch it earlier).
5. **Tampered secure storage during fallback:** secure storage has
   version 99, Meta is empty. Runner trusts the secure-storage value,
   skips migrations. **This is the legacy-bridge risk.** Bounded by:
   the fallback path only runs ONCE per install (on first launch
   after S3-03). After that, Meta takes over.

### Threat model

| Threat | Pre-PR | Post-PR |
|---|---|---|
| Attacker resets secure-storage version to 0 → forces migration re-run | High (mitigated only by migration idempotency) | Mitigated: Meta in Isar is the source of truth; resetting secure storage doesn't affect it |
| Attacker sets Meta to 99 → skips future migrations | n/a | Requires owning the AES key, which is covered by S1-03 key-integrity |
| Attacker poisons secure storage during the one-shot bridge | n/a | Possible but bounded to the very-first-launch-after-S3-03 window |

### Risk

Low. The bridge window is narrow and the new schema is additive.

### Acceptance criteria

From `specs/features/isar-hardening/sprints.md#s3-03`:

- [ ] `@collection class Meta { Id id = 1; late int schemaVersion; }` (singleton row, fixed id)
- [ ] `_runMigrationsIfNeeded` reads/writes schema version via `Meta` collection
- [ ] Old `_schemaVersionKey` in secure storage deleted on first run after this story
- [ ] Test: tamper with secure storage `isar_schema_version` → migration still recognizes correct version from Isar

---

## 3. Reviewer guide

1. **Singleton invariant.** `Meta.id = 1` is the singleton mechanism.
   Isar will overwrite the row on every `put` because the id is
   fixed. Verify the runner never creates multiple Meta rows by
   accident (e.g., from a separate code path).

2. **One-shot fallback discipline.** The `_legacySsKey` read happens
   exactly once per install. After `_writeVersion` succeeds, the
   secure-storage entry is deleted. Next launch reads Meta and
   never touches secure storage. Confirm this in the test for case
   (2) above.

3. **`Meta` is not encrypted at the field level.** It contains only
   an integer version + a timestamp. That's intentional — the value
   is operational, not PII. If the schema ever grows to include
   sensitive fields, add AAD-bound encryption.

---

## 4. Rollback plan

`git revert <merge-commit>`. The Meta collection is removed from
the schema list. Isar will refuse to open the DB if a row exists in
a removed collection — **this is the rollback risk**.

Mitigation: the revert PR keeps the Meta collection registered in
`Isar.open([...])` but the runner reads from secure storage again.
Code: drop the runner changes; keep the schema. Two PRs to revert
fully if needed.

Alternative: if we trust the Meta-collection path enough to ship it,
a partial revert (drop only the runner usage) is fine. Either way,
data in Meta stays readable.

---

## 5. Proposed diffs

### 5a. `mobile/lib/core/database/schemas/meta.dart` (new)

```dart
import 'package:isar/isar.dart';

part 'meta.g.dart';

/// Operational metadata about this Isar database instance.
/// Singleton — only the row with id = 1 is ever used. Stored inside
/// the encrypted DB so an attacker who can tamper with
/// FlutterSecureStorage cannot rewrite the schema version.
@collection
class Meta {
  Id id = 1;
  late int schemaVersion;
  late DateTime updatedAt;
}
```

### 5b. `mobile/lib/core/database/isar_service.dart`

```diff
   _isar = await Isar.open(
     [
       UserSchema,
       CropListingSchema,
       TripSchema,
       DeliverySchema,
       OrderSchema,
       PaymentSchema,
       PaymentIntentSchema,
       SyncOperationSchema,
       BuyerIntentSchema,
+      MetaSchema,
     ],
     directory: dir.path,
     name: 'wereda_connect',
   );
```

### 5c. `mobile/lib/core/database/migrations/migrations_runner.dart`

See the diff in §"What changes — Migrations runner" above. Adds
`_readVersion`, `_writeVersion`, `_maybeDeleteLegacyKey`. Replaces
the direct secure-storage calls in `runIfNeeded`.

### 5d. Test additions

```dart
group('MigrationsRunner — Meta-based version', () {
  test('fresh install: defaults to 0, writes Meta after migrating',
      () async {
    // No Meta row, no secure-storage entry.
    final runner = MigrationsRunner(...);
    await runner.runIfNeeded();
    final meta = await isar.metas.get(1);
    expect(meta!.schemaVersion, equals(3));
  });

  test('upgrade from S3-01: bridges from secure storage', () async {
    await storage.write(key: 'isar_schema_version', value: '2');
    // Meta is empty.
    final runner = MigrationsRunner(...);
    await runner.runIfNeeded();
    final meta = await isar.metas.get(1);
    expect(meta!.schemaVersion, equals(3));
    expect(await storage.read(key: 'isar_schema_version'), isNull);
  });

  test('already at v3: reads from Meta, ignores secure storage',
      () async {
    await isar.writeTxn(() => isar.metas.put(Meta()
      ..id = 1
      ..schemaVersion = 3
      ..updatedAt = DateTime.now().toUtc()));
    await storage.write(key: 'isar_schema_version', value: '99'); // tampered
    final runner = MigrationsRunner(...);
    await runner.runIfNeeded();
    final meta = await isar.metas.get(1);
    expect(meta!.schemaVersion, equals(3));   // unchanged
  });

  test('tampered Meta is trusted: would-be attacker can skip migrations',
      () async {
    // Document the residual risk: an attacker who owns the AES key
    // (which means S1-03 already failed) can rewrite Meta.
    await isar.writeTxn(() => isar.metas.put(Meta()
      ..id = 1
      ..schemaVersion = 99
      ..updatedAt = DateTime.now().toUtc()));
    final runner = MigrationsRunner(...);
    await runner.runIfNeeded();
    // Migration was skipped — by design, because rewriting Meta
    // requires AES key access which means deeper compromise.
  });
});
```

---

## 6. Open questions for the reviewer

1. **What about pre-S3-01 installs that somehow upgrade directly to
   S3-03?** Secure storage has no version key; Meta is empty.
   `_readVersion` returns 0; full v2 + v3 migration runs. That's
   correct. Just confirm the test for case (1) covers this.

2. **Should the Meta collection have a HMAC tag like the AES key in
   S1-03?** Probably not necessary — Meta is inside the AAD-bound
   encrypted DB, so tampering already requires AES-key access.
   Adding a tag is belt-and-braces but adds complexity.

3. **Sprint hygiene.** This is the last story in HS-3. After it lands,
   `STATE.md` and `TASK.md` need updating to "HS-3 complete." Should
   that go in this PR or a separate `hs3-closeout` PR? Default:
   include in this PR (matches the S1-05 pattern).

---

*PR scaffold v1.0 — 2026-05-13 — depends on S3-01 merged; closes HS-3*
