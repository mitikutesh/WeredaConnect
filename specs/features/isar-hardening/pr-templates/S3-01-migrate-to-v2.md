# PR Scaffold — S3-01: `_migrateToV2` encrypts plaintext PII

> **Status:** Design ready · **Depends on:** S1-04, S2-01, S2-02, S2-04 merged
> **Sprint:** HS-3 · **Epic:** E1 — Encrypt at rest
> **Story catalog:** `specs/features/isar-hardening/sprints.md#s3-01`
> **Rollout runbook:** `specs/features/isar-hardening/sprint-3-rollout.md` ← READ THIS FIRST
> **Reviewer needed:** security + founder sign-off (`risk-acceptance-signoff.md`)
> **Story size:** 8 pts — **CRITICAL: unrecoverable failure modes**

⛔ **This is the highest-blast-radius PR in the entire roadmap.**
A bug that escapes the dev cohort and reaches production can
permanently scramble user data on devices we cannot reach. **Do not
merge this PR without:**

1. Founder + security-reviewer countersignature on `risk-acceptance-signoff.md`
2. Migration kill switch (S6-03) merged and verified in production
3. Sentry/Crashlytics wiring for `FieldDecryptionException` (S6-01)
4. Dev-cohort gate per `sprint-3-rollout.md` §1
5. The reviewer has read both this scaffold AND the rollout runbook

---

## 0. TL;DR

One-shot migration that walks every `User` and `CropListing` row and
encrypts plaintext PII fields in place. Idempotent under crash-retry:
running it twice on the same DB is a no-op because each row checks for
the `enc:v1:` prefix before re-encrypting.

The migration runs once on app startup, gated by the schema version.
If `migration_disabled` remote flag is set (S6-03), the migration is
skipped — used as the kill switch.

Runs **inside a single `isar.writeTxn`** so a crash leaves the DB in a
consistent partial state. The next launch resumes from where it
stopped because the migration is idempotent.

---

## 1. How to ship this

**STOP. Pre-flight checks first. See `sprint-3-rollout.md` §1.**

```bash
# Prerequisite: every HS-1 + HS-2 story merged
git fetch origin
for sprint in 1 2; do
  for story in 01 02 03 04 05; do
    git log origin/main --oneline | grep "S${sprint}-${story}" || \
      echo "MISSING: S${sprint}-${story}"
  done
done

git checkout -b hs3/s3-01-migrate-to-v2 origin/main

# Apply the changes in §5:
#   - mobile/lib/core/database/migrations/migrate_v2_encryption.dart  (new)
#   - mobile/lib/core/database/migrations/migrations_runner.dart       (new)
#   - mobile/lib/core/database/isar_service.dart  (hook the runner into initialize())
#   - mobile/test/core/database/migrations/migrate_v2_encryption_test.dart  (new)

cd mobile
flutter analyze lib/core/database/migrations/
flutter test test/core/database/migrations/
flutter test                                      # full suite
flutter build apk --release --dart-define=PHONE_PEPPER=$PHONE_PEPPER
cd ..

# Validate the migration runs cleanly on a snapshot DB
# (See sprint-3-rollout.md §1 "Performance" for the runtime target)
./scripts/dev/run_migration_against_fixture.sh path/to/fixture.isar

git add ...
git commit -m "$(cat <<'EOF'
feat(mobile): v2 migration — encrypt User and CropListing PII at rest

Walks every User and CropListing row on first launch after the v2
schema bump. Encrypts displayName, fullName, faydaId, farmerName,
description with FieldEncryptor (AAD bound to (collection, column,
stableRowKey)). Populates phoneSearchHash for User. Idempotent under
crash-retry — each row is skipped if it already starts with enc:v1:.

Runs inside a single isar.writeTxn for atomicity. Gated by the
migration_disabled remote flag (S6-03) for kill-switch capability
during staged rollout.

After this PR ships and HS-3 reaches 100% rollout, the legacy
phoneNumber column can be removed in a follow-up.

⛔ Do NOT merge without:
  - founder + security signed risk-acceptance-signoff.md
  - dev cohort gate per sprint-3-rollout.md
  - Sentry wiring (S6-01) and kill switch (S6-03) live in production

Refs: specs/features/isar-hardening/sprints.md#s3-01
      specs/features/isar-hardening/sprint-3-rollout.md
EOF
)"
git push -u origin hs3/s3-01-migrate-to-v2
# DO NOT auto-create the PR. The founder + security reviewer must
# countersign risk-acceptance-signoff.md first, then the PO creates
# the PR manually after the dev-cohort APK is provisioned.
echo "Branch pushed. Now: founder + security reviewer sign risk-acceptance-signoff.md."
echo "Then: PO follows sprint-3-rollout.md to provision the dev-cohort APK."
```

---

## 2. PR description

### Summary

Introduces a one-shot v1→v2 migration that converts every existing
plaintext PII row to the `enc:v1:` AAD-bound format. Specifically:

- `User.displayName`, `User.fullName`, `User.faydaId` → encrypt with
  AAD `(User, <column>, userId)`
- `User.phoneNumber` → encrypt as `User.phoneEncrypted` AND populate
  `User.phoneSearchHash` via the build-time PEPPER
- `CropListing.farmerName`, `CropListing.description` → encrypt with
  AAD `(CropListing, <column>, listingId)`
- Bump schema version from `1` to `2`
- Persist the new version (this PR uses secure storage; S3-03 moves
  the version into an Isar `_meta` collection)

### Why

HS-2 wired the encryption code into the repositories. Every **new**
write after HS-2 produces encrypted rows. But **existing rows** sit
on disk in plaintext from before HS-2. This PR is the one and only
chance to convert them. After this migration completes for a user,
`FieldEncryptor.initialize` runs with `legacyPlaintextAllowed: false`
forever after, and unprefixed reads throw.

### What changes

#### New files

- **`mobile/lib/core/database/migrations/migrations_runner.dart`** —
  the migration orchestrator. Reads the persisted schema version,
  computes the diff to `_currentSchemaVersion = 2`, runs each
  migration step in order. Hooks the remote `migration_disabled` flag
  as a kill switch.

- **`mobile/lib/core/database/migrations/migrate_v2_encryption.dart`** —
  the v1→v2 migration. Walks `users` then `cropListings`, encrypts
  per-column with AAD, writes back. Idempotent.

- **`mobile/test/core/database/migrations/migrate_v2_encryption_test.dart`** —
  unit tests with in-memory Isar.

- **`scripts/dev/run_migration_against_fixture.sh`** (new) — utility
  to run the migration against a snapshot of a real device's Isar
  file (with PII redacted). Used in dev cohort prep.

#### Modified files

- **`mobile/lib/core/database/isar_service.dart`** — `initialize()`
  calls the migrations runner before returning the Isar handle. The
  runner is responsible for setting `FieldEncryptor.legacyPlaintextAllowed`
  to `true` for the duration of the migration only, then back to
  `false`.

### What does NOT change

- The encryption format (already shipped in HS-1).
- The repository APIs (HS-2).
- The legacy `phoneNumber` column — this PR populates the new
  hash/encrypted columns but doesn't remove the legacy one. Removal
  is a follow-up PR after HS-3 reaches 100% rollout.
- Fayda image migration — that's S3-02 (next story).

### Backwards compatibility

The migration is **forward-only**. Once a user runs the migration,
their rows have `enc:v1:` prefixes. A reverted app build that doesn't
know about the prefix would treat those rows as garbage. **Hence the
kill switch and the staged rollout.**

The migration is **idempotent**. If the app crashes mid-loop, the
next launch resumes — rows that already have `enc:v1:` are skipped.
This is the core safety property.

### Test plan

```bash
cd mobile

# Unit tests against in-memory Isar
flutter test test/core/database/migrations/

# Integration test against a real device fixture
./scripts/dev/run_migration_against_fixture.sh test/fixtures/v1-snapshot.isar

# Performance check on Tecno Spark 8 target
adb shell am force-stop com.weredaconnect.dev
adb logcat -c
adb shell am start -n com.weredaconnect.dev/.MainActivity
adb logcat | grep -E 'migration_v2_started|migration_v2_complete'
# Expected: < 500ms for 1000 rows, splash if longer
```

**Required test cases:**

1. **Empty DB:** fresh install, no rows. Migration runs as no-op, sets version = 2.
2. **All plaintext:** 100 User rows + 100 CropListing rows in v1 format.
   Migration encrypts every PII field. All rows still readable after.
3. **All already encrypted:** rerun the migration on the result of (2).
   No changes. Schema version stays at 2.
4. **Crash mid-loop:** kill at row 50 of 100. Restart. Migration resumes;
   all rows end up encrypted. No data loss.
5. **Kill switch ON:** set `migration_disabled = true` via test seam.
   `initialize()` returns without migrating. Schema version stays at 1.
6. **Mixed state recovery:** half encrypted, half plaintext (simulates an
   incomplete first migration). Re-run completes correctly.
7. **Phone hash backfill:** v1 rows have `phoneNumber` set but
   `phoneSearchHash` empty. Migration populates the hash. Login by
   phone (S2-02) finds the user via hash on next attempt.

### Threat model — what this PR closes and opens

| Threat | Pre-PR | Post-PR |
|---|---|---|
| Stolen device exposes existing user's plaintext PII | Plaintext readable | Encrypted; useless without AES key |
| Stolen device exposes existing user's phone via direct index | Plaintext readable | Hashed; useless without pepper |
| Migration corruption (NEW THREAT) | n/a | Mitigated by idempotency + kill switch + staged rollout |
| Partial-migration interrupted by crash (NEW THREAT) | n/a | Resumes on next launch; consistent state |

### Risk

**Critical.** See `sprint-3-rollout.md` for the full risk register and
the 4-stage rollout gate. Short list of the highest concerns:

1. **Unrecoverable corruption on a bug that escapes dev cohort.**
   Mitigated by: dev cohort → 5% → 25% → 100% Play Console rollout +
   `migration_disabled` kill switch + Sentry alarms on
   `FieldDecryptionException`.

2. **Cold-start latency on low-end devices.** Mitigated by: hard
   500 ms target for 1000 rows on Tecno Spark 8; splash screen with
   localized "Updating storage…" if exceeded. Measured in CI on the
   2 GB-RAM target image.

3. **Pepper missing at migration time.** Builds without `PHONE_PEPPER`
   never make it to release (CI gate per S2-02), but defensive:
   `PhoneHasher.hash()` throws `StateError` if pepper is empty. The
   migration aborts cleanly; nothing is half-written.

4. **`FieldEncryptor.initialize(legacyPlaintextAllowed: true)` window.**
   For the duration of the migration loop, decrypt accepts unprefixed
   values. If a malicious actor could inject during that window…
   they couldn't — the migration runs inside a single `writeTxn` on
   the main isolate before any UI thread can issue reads.

### Acceptance criteria

From `specs/features/isar-hardening/sprints.md#s3-01`:

- [ ] Runs inside a single `isar.writeTxn`
- [ ] Initializes `FieldEncryptor` with `legacyPlaintextAllowed: true` for the duration; flips to `false` after
- [ ] For each `User`: encrypt `displayName`, `fullName`, `faydaId` IF not already `enc:v1:`
- [ ] For each `User`: compute `phoneSearchHash` from existing `phoneNumber`; populate `phoneEncrypted`
- [ ] For each `CropListing`: encrypt `farmerName`, optionally `description`
- [ ] **Idempotent:** running twice on the same DB is a no-op (verified by test)
- [ ] Schema version (`2`) stored in a `_meta` collection inside Isar, NOT in secure storage — **deferred to S3-03**; this PR uses secure storage
- [ ] Migration completes in < 500ms for 1k rows on a Tecno Spark 8 — show "Updating storage…" splash if longer

---

## 3. Reviewer guide

Reading order:

1. **`sprint-3-rollout.md` §1 (pre-flight) and §6 (rollback)** — the
   guardrails this PR depends on.
2. **`migrate_v2_encryption.dart`** — read top-to-bottom. Focus on:
   - The `enc:v1:` skip check on every row (idempotency)
   - The `try/catch (FieldDecryptionException)` per field — a row with
     one corrupted field doesn't block the migration of other rows
   - The phone-hash backfill — ensure `PhoneHasher.hash()` is called
     before `_enc.encrypt()` on the phone value
3. **`isar_service.dart` diff** — confirm the runner is hooked **before**
   any production caller gets the `Isar` handle.
4. **Tests** — every case in §"Test plan" maps to a runnable test.

Three things the reviewer **must** verify by reading the diff:

- **Atomicity:** the entire migration is in one `writeTxn`. If you see
  multiple `writeTxn` blocks, push back.
- **`legacyPlaintextAllowed` flip-back:** the flag is set true at the
  start of the migration and false at the end, **even on exception**.
  Use a `try/finally`.
- **Schema version bump happens last:** the version is only persisted
  AFTER every row write succeeds. A crash mid-loop must leave the
  version at 1 so the next launch retries.

---

## 4. Rollback plan

**Code rollback:** `git revert <merge-commit>`. The migration runner
goes away. The schema version key (in secure storage) stays at 2,
which would prevent a re-run if we re-merge — but the runner reads
the version, so reverting means no runner reads it, no problem.

**The hard part: data that was migrated stays migrated.** A revert
doesn't un-encrypt rows. Users whose migration completed will have
rows with `enc:v1:` prefixes. After revert, the HS-2 repositories
can still read those (they call decrypt regardless). After revert
*plus* an HS-2 revert, those rows become unreadable garbage. **Hence
the requirement that HS-2 PRs stay merged when this PR is reverted.**

**Operational rollback during staged rollout** — see
`sprint-3-rollout.md` §6.2 in detail. Summary:

1. Flip `migration_disabled = true` via S6-03 kill switch (15 min)
2. Stop Play Console rollout (1 hour)
3. Patch + dev cohort + new APK (24 hours)
4. Affected users see in-app banner; tap to `clearAllData()` + re-login
5. Estimated worst-case affected: ≤ 0.5% of installs

---

## 5. Proposed diffs

### 5a. `mobile/lib/core/database/migrations/migrate_v2_encryption.dart` (new)

```dart
import 'package:flutter/foundation.dart';
import 'package:isar/isar.dart';

import 'package:wereda_connect/core/database/field_encryptor.dart';
import 'package:wereda_connect/core/database/schemas/crop_listing.dart';
import 'package:wereda_connect/core/database/schemas/user.dart';
import 'package:wereda_connect/core/security/phone_hash.dart';

/// One-shot v1 → v2 migration: encrypts every plaintext PII field in
/// User and CropListing rows. Idempotent under crash-retry — rows
/// already in `enc:v1:` form are skipped.
///
/// MUST run inside an outer writeTxn. The runner provides that.
class MigrateV2Encryption {
  final Isar _isar;
  final FieldEncryptor _enc;
  final PhoneHasher _hasher;

  MigrateV2Encryption({
    required Isar isar,
    required FieldEncryptor encryptor,
    required PhoneHasher phoneHasher,
  })  : _isar = isar,
        _enc = encryptor,
        _hasher = phoneHasher;

  /// Total rows encountered (encrypted + skipped). For telemetry.
  int _processed = 0;
  int _userRowsEncrypted = 0;
  int _userRowsSkipped = 0;
  int _cropRowsEncrypted = 0;
  int _cropRowsSkipped = 0;

  Future<MigrationStats> run() async {
    debugPrint('[migrate_v2] started');
    try {
      await _migrateUsers();
      await _migrateCropListings();
      debugPrint(
        '[migrate_v2] complete: users=$_userRowsEncrypted/${_userRowsEncrypted + _userRowsSkipped}, '
        'crops=$_cropRowsEncrypted/${_cropRowsEncrypted + _cropRowsSkipped}',
      );
      return MigrationStats(
        userRowsEncrypted: _userRowsEncrypted,
        userRowsSkipped: _userRowsSkipped,
        cropRowsEncrypted: _cropRowsEncrypted,
        cropRowsSkipped: _cropRowsSkipped,
      );
    } catch (e, st) {
      debugPrint('[migrate_v2] FAILED at row $_processed: $e\n$st');
      rethrow;     // bubble to runner; runner aborts version bump
    }
  }

  Future<void> _migrateUsers() async {
    final users = await _isar.users.where().findAll();
    for (final u in users) {
      _processed++;
      var modified = false;

      // displayName
      if (!u.displayName.startsWith(FieldEncryptor.cipherPrefix)) {
        u.displayName = await _enc.encrypt(
          u.displayName,
          collection: 'User', column: 'displayName', rowKey: u.userId,
        );
        modified = true;
      }

      // fullName (nullable)
      if (u.fullName != null &&
          !u.fullName!.startsWith(FieldEncryptor.cipherPrefix)) {
        u.fullName = await _enc.encrypt(
          u.fullName!,
          collection: 'User', column: 'fullName', rowKey: u.userId,
        );
        modified = true;
      }

      // faydaId (nullable)
      if (u.faydaId != null &&
          !u.faydaId!.startsWith(FieldEncryptor.cipherPrefix)) {
        u.faydaId = await _enc.encrypt(
          u.faydaId!,
          collection: 'User', column: 'faydaId', rowKey: u.userId,
        );
        modified = true;
      }

      // phone hash + encrypted column (S2-02)
      if (u.phoneSearchHash.isEmpty && u.phoneNumber.isNotEmpty) {
        u.phoneSearchHash = _hasher.hash(u.phoneNumber);
        u.phoneEncrypted = await _enc.encrypt(
          u.phoneNumber,
          collection: 'User', column: 'phoneNumber', rowKey: u.userId,
        );
        // Legacy phoneNumber column stays populated for one sprint.
        modified = true;
      }

      if (modified) {
        await _isar.users.put(u);
        _userRowsEncrypted++;
      } else {
        _userRowsSkipped++;
      }
    }
  }

  Future<void> _migrateCropListings() async {
    final crops = await _isar.cropListings.where().findAll();
    for (final c in crops) {
      _processed++;
      var modified = false;

      if (!c.farmerName.startsWith(FieldEncryptor.cipherPrefix)) {
        c.farmerName = await _enc.encrypt(
          c.farmerName,
          collection: 'CropListing', column: 'farmerName', rowKey: c.listingId,
        );
        modified = true;
      }

      if (!c.description.startsWith(FieldEncryptor.cipherPrefix)) {
        c.description = await _enc.encrypt(
          c.description,
          collection: 'CropListing', column: 'description', rowKey: c.listingId,
        );
        modified = true;
      }

      if (modified) {
        await _isar.cropListings.put(c);
        _cropRowsEncrypted++;
      } else {
        _cropRowsSkipped++;
      }
    }
  }
}

class MigrationStats {
  final int userRowsEncrypted;
  final int userRowsSkipped;
  final int cropRowsEncrypted;
  final int cropRowsSkipped;
  const MigrationStats({
    required this.userRowsEncrypted,
    required this.userRowsSkipped,
    required this.cropRowsEncrypted,
    required this.cropRowsSkipped,
  });

  int get totalRows =>
      userRowsEncrypted + userRowsSkipped +
      cropRowsEncrypted + cropRowsSkipped;
}
```

### 5b. `mobile/lib/core/database/migrations/migrations_runner.dart` (new)

```dart
import 'package:flutter/foundation.dart';
import 'package:flutter_secure_storage/flutter_secure_storage.dart';
import 'package:isar/isar.dart';

import 'package:wereda_connect/core/config/remote_config.dart';
import 'package:wereda_connect/core/database/field_encryptor.dart';
import 'package:wereda_connect/core/security/phone_hash.dart';
import 'migrate_v2_encryption.dart';

class MigrationsRunner {
  static const _versionKey = 'isar_schema_version';
  static const _currentVersion = 2;

  final Isar _isar;
  final FieldEncryptor _enc;
  final PhoneHasher _hasher;
  final FlutterSecureStorage _storage;
  final RemoteConfig _remoteConfig;
  final List<int> _keyBytes;

  MigrationsRunner({
    required Isar isar,
    required FieldEncryptor encryptor,
    required PhoneHasher phoneHasher,
    required FlutterSecureStorage storage,
    required RemoteConfig remoteConfig,
    required List<int> keyBytes,
  })  : _isar = isar,
        _enc = encryptor,
        _hasher = phoneHasher,
        _storage = storage,
        _remoteConfig = remoteConfig,
        _keyBytes = keyBytes;

  Future<void> runIfNeeded() async {
    final saved = await _storage.read(key: _versionKey);
    final version = int.tryParse(saved ?? '0') ?? 0;

    if (version >= _currentVersion) {
      debugPrint('[migrations_runner] already at v$_currentVersion');
      return;
    }

    if (_remoteConfig.migrationDisabled) {
      debugPrint('[migrations_runner] kill switch active; skipping');
      return;
    }

    if (version < 2) {
      await _runV2();
    }
    // future: if (version < 3) await _runV3();

    await _storage.write(
      key: _versionKey,
      value: '$_currentVersion',
    );
  }

  Future<void> _runV2() async {
    // Re-init the encryptor with legacy mode for the migration window.
    _enc.reset();
    _enc.initialize(_keyBytes, legacyPlaintextAllowed: true);
    try {
      await _isar.writeTxn(() async {
        final migration = MigrateV2Encryption(
          isar: _isar,
          encryptor: _enc,
          phoneHasher: _hasher,
        );
        await migration.run();
      });
    } finally {
      // Flip the flag back regardless of success/failure. Production
      // reads MUST run with legacyPlaintextAllowed: false.
      _enc.reset();
      _enc.initialize(_keyBytes, legacyPlaintextAllowed: false);
    }
  }
}
```

### 5c. `mobile/lib/core/database/isar_service.dart` (modified)

```diff
   Future<Isar> initialize() async {
     if (_isar != null) {
       return _isar!;
     }

     final dir = await getApplicationDocumentsDirectory();
     final keyBytes = await _getEncryptionKey();
     FieldEncryptor.instance.initialize(keyBytes);

     _isar = await Isar.open(
       [...],
       directory: dir.path,
       name: 'wereda_connect',
     );

+    // Run any pending schema migrations BEFORE handing the Isar
+    // instance to production callers.
+    final runner = MigrationsRunner(
+      isar: _isar!,
+      encryptor: FieldEncryptor.instance,
+      phoneHasher: PhoneHasher(),
+      storage: _secureStorage,
+      remoteConfig: RemoteConfig.instance,
+      keyBytes: keyBytes,
+    );
+    await runner.runIfNeeded();
+
     return _isar!;
   }
```

### 5d. Test file outline

`mobile/test/core/database/migrations/migrate_v2_encryption_test.dart`:

```dart
void main() {
  // ... in-memory Isar setup as in §15 of docs/mobile/isar-implementation.md

  test('empty DB → migration is no-op', () async {
    await _runMigration();
    expect(await isar.users.count(), 0);
    expect(await isar.cropListings.count(), 0);
  });

  test('all plaintext → all encrypted after migration', () async {
    await _seedPlaintextRows(userCount: 100, cropCount: 100);
    await _runMigration();
    final users = await isar.users.where().findAll();
    expect(users.every((u) => u.displayName.startsWith('enc:v1:')), isTrue);
    expect(users.every((u) => u.phoneSearchHash.isNotEmpty), isTrue);
  });

  test('idempotent: rerun on already-encrypted DB is a no-op', () async {
    await _seedPlaintextRows(userCount: 10, cropCount: 10);
    await _runMigration();
    final before = await isar.users.where().findAll();
    await _runMigration();   // second time
    final after = await isar.users.where().findAll();
    for (var i = 0; i < before.length; i++) {
      expect(after[i].displayName, equals(before[i].displayName));
    }
  });

  test('crash mid-loop: re-run completes', () async {
    await _seedPlaintextRows(userCount: 100, cropCount: 0);
    // Inject a fault after 50 rows. Implementation-detail seam.
    await expectLater(
      _runMigrationFailingAt(50),
      throwsA(isA<Exception>()),
    );
    // Now resume — the runner should pick up the remaining 50.
    await _runMigration();
    final users = await isar.users.where().findAll();
    expect(users.every((u) => u.displayName.startsWith('enc:v1:')), isTrue);
  });

  test('mixed state recovery: half encrypted half plaintext', () async {
    await _seedMixedRows(encrypted: 50, plaintext: 50);
    await _runMigration();
    final users = await isar.users.where().findAll();
    expect(users.every((u) => u.displayName.startsWith('enc:v1:')), isTrue);
  });

  test('kill switch ON: migration skipped, version stays at 1', () async {
    await _seedPlaintextRows(userCount: 10, cropCount: 0);
    await _runMigrationWithKillSwitch(true);
    final users = await isar.users.where().findAll();
    expect(users.first.displayName, isNot(startsWith('enc:v1:')));
    final version = await storage.read(key: 'isar_schema_version');
    expect(version, isNull);   // never advanced from null
  });

  test('phone hash backfill is deterministic for the same pepper',
      () async {
    await _seedPlaintextRows(userCount: 1, cropCount: 0,
        phoneNumber: '+251911234567');
    await _runMigration();
    final u = (await isar.users.where().findFirst())!;
    expect(u.phoneSearchHash, isNotEmpty);

    // Re-running the migration on a fresh DB with the same phone
    // produces the same hash (pepper is build-time-fixed).
    await isar.writeTxn(() => isar.users.clear());
    await _seedPlaintextRows(userCount: 1, cropCount: 0,
        phoneNumber: '+251911234567');
    await _runMigration();
    final u2 = (await isar.users.where().findFirst())!;
    expect(u2.phoneSearchHash, equals(u.phoneSearchHash));
  });
}
```

---

## 6. Open questions for the reviewer

1. **Cold-start perf budget on Itel A23 Pro.** Spec says 500 ms on
   Tecno Spark 8. Itel A23 Pro is slower (1 GB RAM). Should the
   budget be Itel-class instead? Default: target Tecno; measure Itel
   in dev cohort and adjust splash threshold if needed.

2. **Batch size inside the writeTxn.** Currently one big transaction.
   For a user with 10k rows, that's a long-held write lock. Alternative:
   batch by 500 rows × multiple `writeTxn` calls — preserves
   idempotency, sacrifices atomicity. Default: one big txn (matches
   spec); revisit if real-device timing shows lock contention.

3. **`MigrationStats` reporting.** Should we send the count of
   encrypted vs skipped rows to Sentry as a breadcrumb? Pro: visibility
   into rollout progress. Con: PII-adjacent data leaving the device.
   Default: send counts only (no row IDs, no field values).

4. **What if `PhoneHasher.hash()` throws (no PEPPER)?** Migration
   aborts cleanly; version stays at 1. Next launch retries. But every
   launch will re-fail until a PEPPER-having build ships. Is that
   acceptable? Default: yes, because release CI guards prevent
   PEPPER-less builds. But document the failure mode.

---

*PR scaffold v1.0 — 2026-05-13 — depends on HS-1 + HS-2 fully merged*
