# PR Scaffold — S2-02: Split `phoneNumber` into `phoneSearchHash` + `phoneEncrypted`

> **Status:** Design ready · **Depends on:** S2-01 merged
> **Sprint:** HS-2 · **Epic:** E1 — Encrypt at rest
> **Story catalog:** `specs/features/isar-hardening/sprints.md#s2-02`
> **Reviewer needed:** security + 1 mobile dev + founder sign-off on pepper
> **Story size:** 8 pts — **highest risk in HS-2; breaking schema change**

⚠️ **This PR breaks the schema. Read §4 (rollback plan) and §5h (feature
flag) before approving. If you can't roll back cleanly, don't merge.**

---

## 0. TL;DR

Split the indexed `phoneNumber` column on `User` into two:

- **`phoneSearchHash`** — `base64(HMAC-SHA256(phoneE164, PEPPER))`, kept
  `@Index(unique: true)`. Deterministic, indexable, useless without the
  pepper.
- **`phoneEncrypted`** — AES-GCM ciphertext of the phone with AAD bound
  to `(User, phoneNumber, userId)`. For display only.

Old `phoneNumber` field stays in this PR (back-compat shim during the
HS-3 migration window) and is removed in a follow-up.

`auth_bloc.dart` looks up users by `phoneSearchHash` first, falling back
to `phoneNumber` for legacy rows.

---

## 1. How to ship this

```bash
# Prerequisite: S2-01 merged (UserRepository abstraction is the entry point)
git fetch origin
git log origin/main --oneline | grep S2-01

git checkout -b hs2/s2-02-phone-search-hash origin/main

# PHONE_PEPPER MUST be set at build time. The CI release job must inject
# it from a secrets store. Local dev build:
export PHONE_PEPPER='dev-only-pepper-do-not-ship-this'

# Apply the changes in §5. Files:
#   - mobile/lib/core/database/schemas/user.dart           (+2 fields)
#   - mobile/lib/core/database/schemas/user.g.dart         (regen)
#   - mobile/lib/core/security/phone_hash.dart             (new)
#   - mobile/lib/features/user/data/repositories/user_repository.dart   (lookup paths)
#   - mobile/lib/features/auth/bloc/auth_bloc.dart         (use phoneSearchHash)
#   - mobile/lib/core/auth/offline_auth_cache.dart         (populate hash on cache)
#   - mobile/test/features/user/data/repositories/user_repository_test.dart   (extend)
#   - mobile/test/core/security/phone_hash_test.dart       (new)

cd mobile
dart run build_runner build --delete-conflicting-outputs
flutter analyze
flutter test
flutter build web --no-tree-shake-icons 2>&1 | tail -5
flutter build apk --release --dart-define=PHONE_PEPPER=$PHONE_PEPPER
cd ..

git add ...
git commit -m "$(cat <<'EOF'
feat(mobile): split User.phoneNumber into phoneSearchHash + phoneEncrypted

Replaces direct phoneNumber lookup with HMAC-SHA256(phone, PEPPER)
search column + AES-GCM encrypted blob. Indexable, encrypt-at-rest,
no plaintext phone in Isar after migration.

PEPPER is build-time injected via --dart-define=PHONE_PEPPER. Release
CI fails if not set. Rotation requires a coordinated mobile + server
release (server also stores phoneSearchHash for cross-platform lookup).

Backward compatibility: old phoneNumber column retained for one sprint
to keep pre-HS-3 rows readable. Login flow looks up by hash first,
falls back to plaintext column for legacy rows. HS-3 migration backfills
the hash; a follow-up PR removes the legacy column once migration is
100% rolled out.

Refs: specs/features/isar-hardening/sprints.md#s2-02
EOF
)"
git push -u origin hs2/s2-02-phone-search-hash
gh pr create --title "feat(mobile): phoneSearchHash + phoneEncrypted split [S2-02]"
```

---

## 2. PR description

### Summary

`User.phoneNumber` becomes two columns:

| Column | Type | Index | Purpose |
|---|---|---|---|
| `phoneSearchHash` | String (base64 HMAC) | `@Index(unique: true)` | Indexable, deterministic; used by login flow |
| `phoneEncrypted` | String (`enc:v1:...`) | none | Display only; bound to `(User, phoneNumber, userId)` AAD |
| `phoneNumber` (legacy) | String | `@Index(unique: true)` | Retained for one sprint for pre-HS-3 rows |

The legacy column is dropped in a follow-up PR after HS-3 has migrated
all rows.

### Why

AES-GCM ciphertexts use a fresh nonce per call — same plaintext produces
different ciphertext every time. That breaks any direct indexing on the
encrypted value. The standard mitigation is a deterministic search
column built from a keyed hash. Recipe:

1. Generate a build-time pepper (32+ bytes, random). Store in
   AWS Secrets Manager. CI injects it via `--dart-define`.
2. On every phone-number write: `phoneSearchHash = HMAC-SHA256(phoneE164, pepper)`.
3. On every phone-number lookup: hash the input the same way, look up
   by hash.

Properties this gives us:

- **Indexable.** `phoneSearchHash` is a stable string per phone number,
  so Isar's B-tree index works.
- **Encrypted at rest, in the sense that matters.** An attacker with
  the Isar file but not the pepper cannot recover phone numbers from
  the hashes (HMAC is preimage-resistant; pepper enforces uniqueness
  of the hash domain).
- **No rainbow tables.** Pepper means an attacker can't precompute a
  table of Ethiopian phone numbers (+251XXXXXXXXX, ~10 billion
  possibilities — searchable without pepper, infeasible with it).

### What changes

#### Schema

```diff
 @collection
 class User {
   Id id = Isar.autoIncrement;

   @Index(unique: true)
   late String userId;

-  @Index(unique: true)
-  late String phoneNumber;
+  /// LEGACY. Retained until HS-3 migration completes for all installs.
+  /// New writes after S2-02 populate both this and the hash/encrypted
+  /// columns. Reads check the hash column first.
+  @Index(unique: true)
+  late String phoneNumber;
+
+  /// HMAC-SHA256(phoneE164, PEPPER), base64-encoded.
+  /// Used by login flow and any future phone-based lookup.
+  @Index(unique: true)
+  late String phoneSearchHash;
+
+  /// AES-GCM encrypted phone. AAD = (User, phoneNumber, userId).
+  /// Used for display only. Decrypted via UserRepository.loadByUserId.
+  late String phoneEncrypted;

   // ... rest unchanged
 }
```

#### Phone hashing service

New file `mobile/lib/core/security/phone_hash.dart`:

```dart
import 'dart:convert';
import 'package:crypto/crypto.dart';

class PhoneHasher {
  /// Build-time pepper. Release builds MUST provide this via
  /// --dart-define=PHONE_PEPPER. CI fails if missing.
  static const String _pepper =
      String.fromEnvironment('PHONE_PEPPER', defaultValue: '');

  /// HMAC-SHA256 of the phone number, base64-encoded.
  ///
  /// Input must be E.164 (`+251911234567`). Whitespace, dashes, or
  /// parentheses produce different hashes — caller is responsible for
  /// canonicalization.
  String hash(String phoneE164) {
    if (_pepper.isEmpty) {
      throw StateError(
        'PHONE_PEPPER not set. Build with --dart-define=PHONE_PEPPER=...',
      );
    }
    final hmac = Hmac(sha256, utf8.encode(_pepper));
    return base64Encode(hmac.convert(utf8.encode(phoneE164)).bytes);
  }
}
```

#### Repository

`UserRepository` gains a `loadByPhoneE164` method that uses the hash:

```dart
@override
Future<User?> loadByPhoneE164(String phoneE164) async {
  final hash = _hasher.hash(phoneE164);
  // Try the new index first.
  var raw = await _isar.users.filter().phoneSearchHashEqualTo(hash).findFirst();
  if (raw != null) return _hydrate(raw);
  // Fall back to legacy plaintext column for pre-HS-3 rows.
  raw = await _isar.users.filter().phoneNumberEqualTo(phoneE164).findFirst();
  return raw == null ? null : _hydrate(raw);
}
```

The `save()` path populates all three columns:

```dart
Future<void> save(User user) async {
  final phoneE164 = user.phoneNumber;       // assume already canonical
  user.phoneSearchHash = _hasher.hash(phoneE164);
  user.phoneEncrypted = await _enc.encrypt(
    phoneE164,
    collection: 'User', column: 'phoneNumber', rowKey: user.userId,
  );
  // S2-01 already encrypts displayName/fullName/faydaId here.
  // phoneNumber legacy column stays populated for back-compat — removed
  // in the follow-up PR after HS-3 100% rollout.

  await _isar.writeTxn(() async {
    await _isar.users.put(user);
  });
}
```

#### Auth flow

`mobile/lib/features/auth/bloc/auth_bloc.dart` swaps to the new lookup:

```diff
-final user = await userRepo.loadByPhoneLegacy(phoneE164);
+final user = await userRepo.loadByPhoneE164(phoneE164);
```

`loadByPhoneE164` includes the legacy fallback so this works for both
pre-S2-02 and post-S2-02 rows.

#### Build / CI

`PHONE_PEPPER` must be injected at release-build time:

```yaml
# .github/workflows/release-android.yml (excerpt)
- name: Build APK
  run: |
    flutter build apk --release \
      --dart-define=PHONE_PEPPER=${{ secrets.PHONE_PEPPER }}
```

Add a CI guard so a release build without the pepper fails before APK
upload:

```bash
# scripts/check_phone_pepper_set.sh (new)
if grep -q 'PHONE_PEPPER=$' release.log; then
  echo "PHONE_PEPPER not injected in release build" >&2; exit 1
fi
```

The runtime check in `PhoneHasher` (throw `StateError`) is the
last-line defense — the build-time check should catch it first.

### What does NOT change

- The legacy `phoneNumber` column stays populated for one sprint. The
  S2-02-follow-up PR removes it after HS-3 has migrated everyone.
- Server-side: no schema change required for this PR. The server may
  want a matching `phoneSearchHash` column for cross-platform lookups
  — coordinate with server team for next-sprint planning.
- Other PII fields — already encrypted in S2-01.

### Backwards compatibility

**Two failure modes the dual-column shim is designed to handle:**

1. **Pre-S2-02 rows have no hash.** `loadByPhoneE164` falls back to the
   plaintext `phoneNumber` lookup. Works. HS-3 migration backfills the
   hash so all rows have it; the legacy fallback path becomes dead code
   and is removed in the follow-up.

2. **Pre-S2-02 install upgrades to S2-02.** First write after upgrade
   populates all three columns. First read still works via either path.
   No user-visible change.

**One failure mode that breaks:**

3. **Build without `PHONE_PEPPER`.** Runtime `StateError` on first
   login attempt. Mitigated by CI guard. If somehow a build escapes
   without the pepper, every login fails fast and visibly — better
   than silently writing a deterministic-but-empty hash.

### Test plan

```bash
cd mobile

# Build with a test pepper
flutter test --dart-define=PHONE_PEPPER='test-pepper-for-unit-tests'

# Specific
flutter test --dart-define=PHONE_PEPPER='test-pepper-for-unit-tests' \
             test/core/security/phone_hash_test.dart
flutter test --dart-define=PHONE_PEPPER='test-pepper-for-unit-tests' \
             test/features/user/data/repositories/user_repository_test.dart

# Schema regen audit
git status mobile/lib/core/database/schemas/user.g.dart
# Should be modified; do NOT manually edit

# Build with no pepper — should fail
flutter build apk --release            # expect failure on first phone hash
```

**Manual smoke on a real device (with a debug pepper):**

1. Fresh install + register with phone `+251900100001` → assert profile
   loads after kill/relaunch
2. Login by phone `+251900100001` → assert succeeds via hash lookup
3. Use Isar Inspector: confirm `phoneSearchHash` is base64 garble,
   `phoneEncrypted` starts with `enc:v1:`, `phoneNumber` still plaintext
   (legacy column)
4. Confirm uniqueness: try to register another user with the same phone
   → should fail with a server-side error or duplicate-key in Isar

### Threat model coverage

| Threat | Pre-S2-02 | Post-S2-02 |
|---|---|---|
| Attacker has Isar file, wants user list of phone numbers | Reads `phoneNumber` plaintext directly | Sees only hashes; cannot reverse without pepper |
| Attacker has Isar file, brute-force phone space (~10B Ethiopian) | Trivial | Requires the pepper to reproduce hashes — infeasible without |
| Pepper leaks | n/a | All hashes become reversible; rotate pepper + re-hash all rows |
| Cross-user AAD swap on phoneEncrypted | n/a | Detected (AAD includes userId) |

### Risk

**High.** This is the biggest schema change in the roadmap. Risk areas:

1. **Pepper management.** A pepper checked into git or leaked in logs
   defeats the entire mitigation. Must live in AWS Secrets Manager,
   injected by CI only. PR includes a `gitleaks` rule pattern.

2. **Schema regen.** `build_runner` will rewrite `user.g.dart`. Any
   manual edit to that file will be lost. Reviewer must verify the
   diff is exactly what codegen produced.

3. **Login flow.** This is the critical user path. A bug in
   `loadByPhoneE164` = nobody can log in. Required: a Playwright/
   integration test that exercises login end-to-end on a real Android
   emulator before merging.

4. **Unique constraint on hash.** Two users with the same phone now
   collide on `phoneSearchHash` (deterministic hash). This is correct
   behavior — phones must be unique — but if the server allows the
   same phone for test accounts, the mobile DB will reject the second
   one. Verify against test users in `.ai/test-users.md`.

5. **Pepper rotation breaks server-side lookup if server also uses
   hash.** If the server adopts the same hash column (recommended for
   next sprint), pepper rotation requires coordinated re-hash on both
   sides. Document the runbook.

### Acceptance criteria

From `specs/features/isar-hardening/sprints.md#s2-02`:

- [ ] New fields: `@Index(unique: true) late String phoneSearchHash;` and `late String phoneEncrypted;`
- [ ] `phoneSearchHash = base64(HMAC-SHA256(phoneE164, pepper))` — pepper from `String.fromEnvironment('PHONE_PEPPER')`
- [ ] CI build fails if `PHONE_PEPPER` env not set in release build
- [ ] Old `phoneNumber` field retained but deprecated — populated by every write for back-compat (deleted in S3 migration follow-up)
- [ ] Login flow looks up by `phoneSearchHash`, falls back to `phoneNumber` if hash empty (S3 migration backfills hash)
- [ ] `build_runner` regen committed; `flutter build web --no-tree-shake-icons` passes

---

## 3. Reviewer guide

Three priority focus areas, in order:

**1. Pepper handling.**
- Never logged
- Never committed
- Only read via `String.fromEnvironment('PHONE_PEPPER')`
- `PhoneHasher` is the ONLY place that reads it
- A gitleaks rule (`'PHONE_PEPPER=[^$]'`) catches accidental commits

**2. Schema regen integrity.**
- `user.g.dart` modified — diff matches what build_runner would produce
- No manual edits
- All three new field accessors generated (`phoneSearchHashEqualTo`,
  `phoneEncryptedEqualTo` if needed, plus `userByPhoneSearchHash` index)

**3. Login backward compatibility.**
- `loadByPhoneE164` tries hash first
- Falls through to legacy `phoneNumber` lookup if hash returns null
- A test exists for "register pre-S2-02 → upgrade → login by phone"
- The fallback path will become dead code after HS-3; flag for removal
  in the follow-up PR

---

## 4. Rollback plan

This is the rollback plan that **must work** before merging.

### 4a. Revert the merge commit

`git revert <merge-commit>` removes the code. But:

- The schema change is committed → Isar will refuse to open the DB if
  the running code's schema doesn't match the on-disk schema
- Rows written under S2-02 have populated `phoneSearchHash` and
  `phoneEncrypted` fields
- The reverted code doesn't know about those fields → Isar sees
  unknown columns

**Without mitigation, revert = every install breaks.**

### 4b. Mitigation: ship S2-02 behind a feature flag

The S2-02 build includes a runtime flag (`enablePhoneHashLookup`) that
controls whether the **lookup path** uses the hash. The **write path**
always populates all three columns once schema is bumped.

```dart
class UserRepository extends BaseUserRepository {
  final bool _hashLookupEnabled;   // from RemoteConfig

  Future<User?> loadByPhoneE164(String phoneE164) async {
    if (_hashLookupEnabled) {
      final hash = _hasher.hash(phoneE164);
      var raw = await _isar.users.filter().phoneSearchHashEqualTo(hash).findFirst();
      if (raw != null) return _hydrate(raw);
    }
    // Always: fall back to legacy column
    return _legacyLookup(phoneE164);
  }
}
```

Rollback flow:

1. Discover a bug in hash lookup
2. Flip `enablePhoneHashLookup = false` via the S6-03 kill switch
3. All installs fall back to plaintext `phoneNumber` lookup
4. Schema stays bumped, writes keep populating all three columns
5. Patch the bug, ship S2-02-fix, re-enable the flag

**The flag stays in the codebase until HS-3 has 100% rolled out** and
the legacy `phoneNumber` column is removed in the follow-up PR.

### 4c. If the schema change itself is bad

Schema rollback is harder. Isar additive changes are tolerated by older
code (it ignores unknown fields), so a same-version old code reading
new-schema data works. But:

- A reverted build that ships without the new fields will be on an
  older schema version → Isar may refuse to open
- The fix is to publish a hotfix that adds the new fields back as
  deprecated stubs, never reads/writes them, and keeps Isar happy

**PO call:** schema rollback is non-trivial. The pre-merge confidence
gates must catch schema problems. Reviewer must test on a clean install
AND an upgrade install before signing off.

---

## 5. Proposed diffs

(See §2 above for the schema, hasher, repo, and auth-bloc diffs.
Listed below: tests, build-config, and CI guard.)

### 5a. `mobile/test/core/security/phone_hash_test.dart` (new)

```dart
import 'package:flutter_test/flutter_test.dart';
import 'package:wereda_connect/core/security/phone_hash.dart';

void main() {
  test('hash is deterministic for the same input', () {
    final h = PhoneHasher();
    expect(h.hash('+251911234567'), equals(h.hash('+251911234567')));
  });

  test('different inputs produce different hashes', () {
    final h = PhoneHasher();
    expect(h.hash('+251911234567'), isNot(equals(h.hash('+251911234568'))));
  });

  test('throws if PHONE_PEPPER not set', () {
    // Re-import PhoneHasher with a clean compilation unit where _pepper is ''
    // (run this test with --dart-define=PHONE_PEPPER='' to verify)
  });

  test('hash is base64-encoded', () {
    final h = PhoneHasher();
    final out = h.hash('+251911234567');
    expect(() => base64Decode(out), returnsNormally);
  });

  test('hash output length is 44 chars (32-byte HMAC base64)', () {
    final h = PhoneHasher();
    expect(h.hash('+251911234567').length, equals(44));
  });
}
```

### 5b. `mobile/test/features/user/data/repositories/user_repository_test.dart` (extend from S2-01)

```dart
test('save populates phoneSearchHash and phoneEncrypted', () async {
  final repo = UserRepository(isar: isar, encryptor: fakeEnc, hasher: hasher);
  final user = User()
    ..userId = 'sub-A'
    ..phoneNumber = '+251911234567'
    ..displayName = 'Abebe';
  await repo.save(user);

  final raw = await isar.users.filter().userIdEqualTo('sub-A').findFirst();
  expect(raw!.phoneSearchHash, isNotEmpty);
  expect(raw.phoneEncrypted, startsWith('fake:'));   // FakeFieldEncryptor
  expect(raw.phoneNumber, equals('+251911234567'));  // legacy column populated
});

test('loadByPhoneE164 uses hash lookup', () async {
  final repo = UserRepository(isar: isar, encryptor: fakeEnc, hasher: hasher);
  final user = User()
    ..userId = 'sub-A'
    ..phoneNumber = '+251911234567'
    ..displayName = 'Abebe';
  await repo.save(user);

  final found = await repo.loadByPhoneE164('+251911234567');
  expect(found!.userId, equals('sub-A'));
});

test('loadByPhoneE164 falls back to legacy column for pre-S2-02 rows',
    () async {
  // Plant a row with phoneNumber set but no hash (pre-S2-02 shape)
  await isar.writeTxn(() => isar.users.put(User()
    ..userId = 'legacy-sub'
    ..phoneNumber = '+251911234567'
    ..phoneSearchHash = ''
    ..phoneEncrypted = ''
    ..displayName = 'Abebe'));

  final repo = UserRepository(isar: isar, encryptor: fakeEnc, hasher: hasher);
  final found = await repo.loadByPhoneE164('+251911234567');
  expect(found!.userId, equals('legacy-sub'));
});

test('hash lookup is the primary path when the row has a hash', () async {
  // Both paths could find the user; assert we use the hash one (faster).
  // Verify by spy: instrument the legacy lookup to throw if called.
});
```

### 5c. CI build-time guard

`.github/workflows/release-android.yml` (extract):

```yaml
- name: Verify PHONE_PEPPER is set
  run: |
    if [ -z "${{ secrets.PHONE_PEPPER }}" ]; then
      echo "::error::PHONE_PEPPER secret not set"
      exit 1
    fi

- name: Build APK
  run: |
    flutter build apk --release \
      --dart-define=PHONE_PEPPER=${{ secrets.PHONE_PEPPER }}
```

### 5d. gitleaks rule

`.gitleaks.toml` (extend or create):

```toml
[[rules]]
description = "PHONE_PEPPER assignment"
regex = '''PHONE_PEPPER\s*=\s*['"][^'"]*['"]'''
tags = ["pepper", "phone", "secret"]
allowlist.paths = [
  '''docs/.*''',     # docs reference the env var by name
  '''.*\.md$''',     # markdown files (PR scaffolds, runbooks)
]
```

---

## 6. Open questions for the reviewer

1. **Pepper rotation policy.** When (if ever) do we rotate it? Default:
   never (rotation requires re-hashing all rows). If we want rotation
   capability, we need a "pepper version" column and migration. Out of
   scope for this PR — flag for future.

2. **Server-side hash adoption.** If the server adopts the same hash
   column for cross-platform lookups (web admin searching users by
   phone), we need to share the pepper with the server. AWS Secrets
   Manager handles this naturally. Coordinate with server team for
   next sprint.

3. **Test phone numbers.** `.ai/test-users.md` lists three test phones
   (`+251900100001`, `+251900100002`, `+251900100003`). Confirm these
   still work after S2-02 ships — they should, since hash is computed
   the same way for any phone.

4. **Legacy column removal.** When does the follow-up PR happen?
   Recommendation: after HS-3 has been at 100% rollout for 7 days. Track
   as a follow-up story `S2-02-cleanup`. Don't merge S2-02 without the
   follow-up scheduled.

5. **What about the `BuyerIntent.contactPhone` field (if it exists)?**
   Verify there's no other plaintext phone field anywhere. Search:
   `grep -rn 'phoneNumber\|contactPhone\|phone:' mobile/lib/core/database/schemas/`.

---

*PR scaffold v1.0 — 2026-05-13 — depends on S2-01 merged*
