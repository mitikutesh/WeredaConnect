# PR Scaffold — S2-01: Encrypt User PII fields (non-indexed)

> **Status:** Design ready · **Depends on:** HS-1 closed (S1-01..S1-05 merged)
> **Sprint:** HS-2 · **Epic:** E1 — Encrypt at rest
> **Story catalog:** `specs/features/isar-hardening/sprints.md#s2-01`
> **Reviewer needed:** security + 1 mobile dev · **Story size:** 3 pts (see ⚠️ below)

> ⚠️ **Scope discovery.** When the story was first written, it assumed a
> `UserRepository` existed. It doesn't — `User` reads/writes happen in
> three places:
>
> - `mobile/lib/screens/profile_screen.dart`
> - `mobile/lib/core/auth/offline_auth_cache.dart`
> - `mobile/lib/core/sync/conflict_resolver.dart`
>
> This PR creates `UserRepository` and routes all three call sites through
> it. The 3-pt estimate is borderline. If sprint planning shows S2-01
> consuming > 4 pts of actual time, split it into S2-01a (create the
> abstraction) and S2-01b (wire encryption around the now-centralized
> writes). Defer the split decision until day 2.

---

## 0. TL;DR

Encrypt `User.displayName`, `User.fullName`, and `User.faydaId` in Isar
using the AAD-bound `FieldEncryptor` shipped in HS-1. Centralize all
`User` reads and writes behind a new `UserRepository` so there's exactly
one chokepoint where encrypt/decrypt happens.

**Does not touch `phoneNumber`** — that's S2-02 (HMAC search column +
encrypted blob; bigger schema change).

---

## 1. How to ship this

```bash
# Prerequisite: all of HS-1 merged
git fetch origin
git log origin/main --oneline | grep -E 'S1-0[12345]'   # all five

git checkout -b hs2/s2-01-encrypt-user-pii origin/main

# Apply the changes in §5:
#   - mobile/lib/features/user/domain/repositories/base_user_repository.dart  (new)
#   - mobile/lib/features/user/data/repositories/user_repository.dart         (new)
#   - mobile/lib/features/user/data/repositories/web_user_repository.dart     (new)
#   - mobile/lib/screens/profile_screen.dart                  (route through repo)
#   - mobile/lib/core/auth/offline_auth_cache.dart            (route through repo)
#   - mobile/lib/core/sync/conflict_resolver.dart             (route through repo)
#   - mobile/lib/app/run_mobile.dart                          (register repo in DI)
#   - mobile/test/features/user/data/repositories/user_repository_test.dart   (new)

cd mobile
flutter analyze
flutter test test/features/user/
flutter build web --no-tree-shake-icons 2>&1 | tail -5
cd ..

git add ...
git commit -m "$(cat <<'EOF'
feat(mobile): encrypt User PII at rest via new UserRepository

Creates UserRepository (mobile + web variants per platform-pair contract)
and centralizes every User read/write through it. Encrypts displayName,
fullName, and faydaId fields with AAD-bound FieldEncryptor on save;
decrypts on read.

Does not touch phoneNumber — that's S2-02 (HMAC search column + AES
blob).

Three pre-existing call sites refactored to use the repository:
  - profile_screen.dart
  - offline_auth_cache.dart
  - conflict_resolver.dart

Refs: specs/features/isar-hardening/sprints.md#s2-01
EOF
)"
git push -u origin hs2/s2-01-encrypt-user-pii
gh pr create --title "feat(mobile): encrypt User PII via new UserRepository [S2-01]" \
             --body "$(...)"
```

---

## 2. PR description

### Summary

Introduces a `UserRepository` abstraction that owns every `User` Isar
read/write. The repository encrypts three PII fields (`displayName`,
`fullName`, `faydaId`) before `put` and decrypts after `findFirst`/
`findAll`. Pre-existing direct-Isar callers are refactored to go through
the repository.

### Why

`docs/mobile/isar-implementation.md` ⚠️ row #1: `FieldEncryptor` has zero
production call sites. HS-1 made the primitive correct (AAD, prefix,
fail-closed, integrity-tagged). This PR is the first production use.

Three fields chosen for this PR because they're all non-indexed strings —
straightforward to encrypt. The fourth PII field (`phoneNumber`) is
**not** in this PR because it carries a unique index that login depends
on — splitting it needs the HMAC-search-column work in S2-02.

### What changes

#### New files

- **`mobile/lib/features/user/domain/repositories/base_user_repository.dart`**
  Abstract class with `loadByUserId`, `loadByPhone`, `save`, `delete`.
- **`mobile/lib/features/user/data/repositories/user_repository.dart`**
  Isar-backed concrete impl. Encrypt before `put`, decrypt after read.
- **`mobile/lib/features/user/data/repositories/web_user_repository.dart`**
  Web variant. No Isar; reads from API. Per platform-pair contract.
- **`mobile/test/features/user/data/repositories/user_repository_test.dart`**
  Unit tests with `FakeFieldEncryptor` (S2-05 — landed before this PR
  if the merge order is right, else inline a one-off passthrough).

#### Modified files

- **`mobile/lib/screens/profile_screen.dart`** — replace direct
  `isar.users.findFirst()` / `.put()` calls with `userRepo.loadByUserId`
  / `userRepo.save`.
- **`mobile/lib/core/auth/offline_auth_cache.dart`** — same routing
  change. This file caches the User row after login.
- **`mobile/lib/core/sync/conflict_resolver.dart`** — same; the
  resolver applies server data and `.put`s the result.
- **`mobile/lib/app/run_mobile.dart`** — register `UserRepository`
  in `GetIt` (or whichever DI mechanism the project uses; verify).

### What does NOT change

- `User.phoneNumber` field encryption — S2-02.
- `User.faydaIdFrontImagePath` / `faydaIdBackImagePath` —
  filesystem paths, encrypted in S3-02 (image blob migration).
- `CropListing` encryption — S2-04.
- BLoC layer — the BLoCs already work with hydrated `User` objects, so
  routing them through the repo is transparent.

### Backwards compatibility

**Existing rows are plaintext.** This PR writes encrypted; reads accept
both (legacy plaintext returns as-is because the encryptor was init'd
with `legacyPlaintextAllowed: true` — wait, that's the HS-3 migration
mode).

**Decision needed:** does this PR ship with `legacyPlaintextAllowed: true`
in `IsarService.initialize()`?

- **Option A** — Yes. Pros: existing users' rows still readable, no
  migration needed before this PR. Cons: opens the downgrade-attack
  window again for the period between S2-01 ship and HS-3 ship.
- **Option B** — No, but make `UserRepository.loadByUserId` catch
  `FieldDecryptionException` and treat it as "row needs HS-3 migration"
  — show a placeholder ("…") in the UI until HS-3 runs.
- **Option C** — Don't ship S2-01 to production until HS-3 is also
  ready. Treat S2-01 as a staging-only milestone.

**PO call:** Option B. Catch the exception in the repository, log a
Sentry breadcrumb, return a "needs migration" sentinel. The UI shows
"Loading…" instead of garbage. HS-3 lands within a sprint and the
sentinel disappears.

### Test plan

```bash
cd mobile
flutter analyze lib/features/user/ lib/screens/profile_screen.dart \
                lib/core/auth/offline_auth_cache.dart \
                lib/core/sync/conflict_resolver.dart
flutter test test/features/user/
flutter test                          # full suite — confirm no regression
flutter build web --no-tree-shake-icons
```

**Manual test on a real device:**
1. Fresh install → register → confirm profile screen shows the name
2. Kill app, reopen → name still shows (decrypt round-trip works)
3. Use Isar Inspector on the dev build → confirm `displayName` field
   shows `enc:v1:...`, not the cleartext name

### Threat model coverage

| Threat | Before | After |
|---|---|---|
| Device-stolen, attacker reads Isar file | Reads cleartext displayName, fullName, faydaId | Reads `enc:v1:...` blobs — useless without AES key |
| Cross-row AAD attack on these fields | Field encryption not in use | Detected (HS-1 AAD work) |
| Tampered ciphertext on these fields | Field encryption not in use | Detected (HS-1 MAC) |
| Cross-process Isar file read | Reads cleartext | Reads ciphertext |

### Risk

Medium. Three production code paths get refactored. Risk areas:

1. **Conflict resolver path.** On a sync-conflict, the server data
   arrives as JSON, gets applied to the local User, and the result is
   `.put`. We must encrypt the server-supplied displayName/fullName/
   faydaId *after* applying. The reverse order would store plaintext.
2. **Offline auth cache.** This is read on every app launch. A bug
   here = login broken.
3. **Profile screen edit flow.** Updates to displayName must encrypt
   before save AND clear cached decrypted values.

Mitigation: test plan above + a dedicated integration test that opens
the app, logs in, edits the profile, restarts, and verifies the
displayed name is correct.

### Acceptance criteria

From `specs/features/isar-hardening/sprints.md#s2-01`:

- [ ] `UserRepository.save(user)` encrypts each PII field with AAD `User:<column>:<user.userId>` before `put`
- [ ] `UserRepository.load(...)` decrypts on read; returns hydrated User
- [ ] BLoCs / screens consume hydrated objects — no plaintext leaks via streams
- [ ] Tests cover round-trip and all three role views (Farmer, Trucker, Buyer)
- [ ] Old plaintext rows decrypt successfully under `legacyPlaintextAllowed: true` (deferred to S3 migration)
- [ ] **NEW (scope correction):** `UserRepository` abstraction exists and the three pre-existing call sites are routed through it

---

## 3. Reviewer guide

Two priority focus areas:

**Priority 1: every write call site encrypts.**
Grep the diff for `users.put(` and confirm every match is preceded by
an `await encryptor.encrypt(...)` for each PII field. The CI lint
`scripts/check_pii_writes.sh` (S2-03 — likely lands in parallel) is
the long-term guarantee; for this PR, eyeball it.

**Priority 2: AAD `rowKey` is the server `userId`, never the Isar `id`.**
The Isar `id` is local-autoincrement and can change on import. The
server `userId` (Cognito sub) is stable. Reviewers MUST verify the
repository calls pass `user.userId`, not `user.id.toString()`, as the
AAD `rowKey`.

---

## 4. Rollback plan

`git revert <merge-commit>`. The pre-PR direct-Isar paths return.
Encrypted rows already written to disk will be unreadable by the
reverted code (because reverted code doesn't decrypt) — but rows
written *before* S2-01 shipped are still plaintext, so the reverted
code reads those fine. New rows written after revert are plaintext
again.

**The big risk:** a user who logged in / edited profile while S2-01
was live has encrypted rows. After revert, those rows show as
`enc:v1:...` garbage in the UI. Mitigation: ship S2-01 behind a
feature flag (`enableUserPiiEncryption`) for the first 24 hours; flip
the flag to roll back without a code revert.

### Feature-flag implementation

```dart
class UserRepository extends BaseUserRepository {
  final FieldEncryptor _enc;
  final bool _enabled;     // from RemoteConfig
  UserRepository({required FieldEncryptor enc, required bool enabled})
      : _enc = enc, _enabled = enabled;

  @override
  Future<void> save(User user) async {
    if (_enabled) {
      user.displayName = await _enc.encrypt(user.displayName,
          collection: 'User', column: 'displayName', rowKey: user.userId);
      // ... fullName, faydaId same pattern
    }
    await _isar.writeTxn(() => _isar.users.put(user));
  }
}
```

Default: `enabled: true`. Flag exists so the kill switch is available
during the first 24-hour window.

---

## 5. Proposed diffs

### 5a. `mobile/lib/features/user/domain/repositories/base_user_repository.dart` (new)

```dart
import 'package:wereda_connect/core/models/user.dart';

abstract class BaseUserRepository {
  /// Load a user by their server-assigned userId. Returns null if not
  /// found. Decrypts PII fields before returning.
  Future<User?> loadByUserId(String userId);

  /// Load a user by plaintext phone number. Legacy direct-Isar lookup
  /// — replaced by phoneSearchHash-based lookup in S2-02.
  /// Decrypts PII fields before returning.
  Future<User?> loadByPhoneLegacy(String phoneE164);

  /// Save a user, encrypting PII fields before write.
  Future<void> save(User user);

  /// Soft-delete (sets isDeleted = true, doesn't actually remove).
  Future<void> softDelete(String userId);
}
```

### 5b. `mobile/lib/features/user/data/repositories/user_repository.dart` (new)

```dart
import 'package:isar/isar.dart';

import 'package:wereda_connect/core/database/field_encryptor.dart';
import 'package:wereda_connect/core/models/user.dart';
import 'package:wereda_connect/features/user/domain/repositories/base_user_repository.dart';

class UserRepository extends BaseUserRepository {
  final Isar _isar;
  final FieldEncryptor _enc;

  UserRepository({required Isar isar, required FieldEncryptor encryptor})
      : _isar = isar, _enc = encryptor;

  @override
  Future<User?> loadByUserId(String userId) async {
    final raw = await _isar.users.filter().userIdEqualTo(userId).findFirst();
    if (raw == null) return null;
    return _hydrate(raw);
  }

  @override
  Future<User?> loadByPhoneLegacy(String phoneE164) async {
    // S2-02 will replace this with phoneSearchHash lookup.
    final raw = await _isar.users.filter().phoneNumberEqualTo(phoneE164).findFirst();
    if (raw == null) return null;
    return _hydrate(raw);
  }

  @override
  Future<void> save(User user) async {
    await _encryptInPlace(user);
    await _isar.writeTxn(() async {
      await _isar.users.put(user);
    });
  }

  @override
  Future<void> softDelete(String userId) async {
    final raw = await _isar.users.filter().userIdEqualTo(userId).findFirst();
    if (raw == null) return;
    raw.isDeleted = true;
    raw.deletedAt = DateTime.now().toUtc();
    await _isar.writeTxn(() => _isar.users.put(raw));
  }

  /// Encrypts PII fields in place. Uses AAD bound to (User, column, userId).
  Future<void> _encryptInPlace(User user) async {
    final rowKey = user.userId;
    user.displayName = await _enc.encrypt(
      user.displayName,
      collection: 'User', column: 'displayName', rowKey: rowKey,
    );
    if (user.fullName != null) {
      user.fullName = await _enc.encrypt(
        user.fullName!,
        collection: 'User', column: 'fullName', rowKey: rowKey,
      );
    }
    if (user.faydaId != null) {
      user.faydaId = await _enc.encrypt(
        user.faydaId!,
        collection: 'User', column: 'faydaId', rowKey: rowKey,
      );
    }
  }

  /// Decrypts PII fields and returns the hydrated user.
  /// On FieldDecryptionException (corrupted blob, key mismatch), returns
  /// a sentinel User with "…" placeholders so the UI doesn't show
  /// `enc:v1:...` garbage.
  Future<User> _hydrate(User raw) async {
    final rowKey = raw.userId;
    try {
      raw.displayName = await _enc.decrypt(
        raw.displayName,
        collection: 'User', column: 'displayName', rowKey: rowKey,
      );
    } on FieldDecryptionException {
      raw.displayName = '…';
    }
    if (raw.fullName != null) {
      try {
        raw.fullName = await _enc.decrypt(
          raw.fullName!,
          collection: 'User', column: 'fullName', rowKey: rowKey,
        );
      } on FieldDecryptionException {
        raw.fullName = '…';
      }
    }
    if (raw.faydaId != null) {
      try {
        raw.faydaId = await _enc.decrypt(
          raw.faydaId!,
          collection: 'User', column: 'faydaId', rowKey: rowKey,
        );
      } on FieldDecryptionException {
        raw.faydaId = '…';
      }
    }
    return raw;
  }
}
```

### 5c. `mobile/lib/features/user/data/repositories/web_user_repository.dart` (new)

```dart
// Web variant — no Isar, talks to API directly.
import 'package:wereda_connect/core/models/user.dart';
import 'package:wereda_connect/core/network/api_client.dart';
import 'package:wereda_connect/features/user/domain/repositories/base_user_repository.dart';

class WebUserRepository extends BaseUserRepository {
  final ApiClient _api;
  WebUserRepository(this._api);

  @override
  Future<User?> loadByUserId(String userId) => _api.fetchUser(userId);

  @override
  Future<User?> loadByPhoneLegacy(String phoneE164) =>
      _api.fetchUserByPhone(phoneE164);

  @override
  Future<void> save(User user) => _api.saveUser(user);

  @override
  Future<void> softDelete(String userId) => _api.deleteUser(userId);
}
```

### 5d. Call-site refactors

For each of `profile_screen.dart`, `offline_auth_cache.dart`, and
`conflict_resolver.dart`: replace direct `isar.users.put/findFirst`
calls with `userRepo.save/loadByUserId`.

Pattern (illustrative):

```diff
-final raw = await isar.users.filter().userIdEqualTo(sub).findFirst();
-final name = raw?.displayName ?? '';
+final user = await userRepo.loadByUserId(sub);
+final name = user?.displayName ?? '';
```

```diff
-await isar.writeTxn(() => isar.users.put(user));
+await userRepo.save(user);
```

The conflict resolver case is subtler — it applies server fields then
saves:

```diff
 user.displayName = serverData['displayName'] as String;
 user.fullName = serverData['fullName'] as String?;
-await isar.writeTxn(() => isar.users.put(user));
+await userRepo.save(user);   // encrypts before put
```

### 5e. DI registration

In `mobile/lib/app/run_mobile.dart` (or wherever `GetIt` is wired):

```diff
+getIt.registerLazySingleton<BaseUserRepository>(
+  () => UserRepository(
+    isar: IsarService.instance.db,
+    encryptor: FieldEncryptor.instance,
+  ),
+);
```

Web entry (`run_web.dart`):

```diff
+getIt.registerLazySingleton<BaseUserRepository>(
+  () => WebUserRepository(getIt<ApiClient>()),
+);
```

### 5f. Test file (new)

`mobile/test/features/user/data/repositories/user_repository_test.dart`:

```dart
// Smoke tests against in-memory Isar + FakeFieldEncryptor (S2-05).
// If S2-05 hasn't landed yet, inline a minimal passthrough fake.
void main() {
  test('save encrypts displayName before put', () async {
    final repo = UserRepository(isar: isar, encryptor: fakeEnc);
    await repo.save(User()..userId = 'sub-A'..displayName = 'Abebe');
    final raw = await isar.users.filter().userIdEqualTo('sub-A').findFirst();
    expect(raw!.displayName, startsWith('fake:'));    // passthrough prefix
  });

  test('loadByUserId decrypts displayName', () async {
    final repo = UserRepository(isar: isar, encryptor: fakeEnc);
    await repo.save(User()..userId = 'sub-A'..displayName = 'Abebe');
    final loaded = await repo.loadByUserId('sub-A');
    expect(loaded!.displayName, equals('Abebe'));
  });

  test('AAD rowKey is user.userId, not Isar id', () async {
    final repo = UserRepository(isar: isar, encryptor: spyEnc);
    await repo.save(User()..userId = 'sub-A'..displayName = 'Abebe');
    expect(spyEnc.lastRowKey, equals('sub-A'));
    expect(spyEnc.lastRowKey, isNot(equals('1')));    // not the Isar id
  });

  test('decrypt failure returns "…" sentinel, not enc:v1: garbage',
      () async {
    // Plant a corrupted blob into Isar directly, then load.
    final corrupt = User()..userId = 'sub-A'..displayName = 'enc:v1:not-real';
    await isar.writeTxn(() => isar.users.put(corrupt));

    final repo = UserRepository(isar: isar, encryptor: realEnc);
    final loaded = await repo.loadByUserId('sub-A');
    expect(loaded!.displayName, equals('…'));
  });
}
```

---

## 6. Open questions for the reviewer

1. **DI mechanism.** I assumed `GetIt`. If the project uses a different
   pattern (Provider, Riverpod, manual wiring), adjust §5e accordingly.

2. **Sentinel value `…`.** Should it be the U+2026 horizontal ellipsis,
   a localized string ("Loading...", "..."), or a plain `?`? Default:
   U+2026. Reviewer call.

3. **Feature flag default.** §4 proposes `enabled: true` with a remote
   kill switch. Alternative: default `false`, opt-in on a per-user
   basis for dev cohort. PO call.

4. **Is HS-3 already planned for the next sprint?** If yes, the sentinel
   approach is fine — users see `…` for one sprint, then HS-3 fixes
   them. If HS-3 slips, users may see `…` for a long time. May need to
   bring the migration forward.

---

*PR scaffold v1.0 — 2026-05-13 — depends on HS-1 closed*
