# PR Scaffold — S2-05: Repo-layer encrypted-passthrough mock

> **Status:** Design ready · **Depends on:** S1-01 merged (just the AAD signatures)
> **Sprint:** HS-2 · **Epic:** E1 — Encrypt at rest
> **Story catalog:** `specs/features/isar-hardening/sprints.md#s2-05`
> **Reviewer needed:** 1 mobile dev (QA optional) · **Story size:** 2 pts
> **Can ship in parallel with:** S2-01, S2-02, S2-04 (it unblocks their tests)

Test infrastructure. Adds a `FakeFieldEncryptor` test double so
repository unit tests can run fast without real crypto, and so test
assertions can compare cleartext directly.

---

## 0. TL;DR

`FakeFieldEncryptor` is a `BaseFieldEncryptor` implementation that:

- `encrypt(plaintext)` → `'fake:<aad>:<plaintext>'`
- `decrypt('fake:<aad>:<plaintext>')` → `'<plaintext>'`

Easy to assert against. Round-trip is fast. AAD trio is embedded in the
"ciphertext" so tests can verify AAD propagation as well.

A pilot refactor on `UserRepository` makes it take `BaseFieldEncryptor`
via constructor (instead of `FieldEncryptor.instance`) so the fake can
be injected.

---

## 1. How to ship this

```bash
git fetch origin
git log origin/main --oneline | grep S1-01

git checkout -b hs2/s2-05-fake-field-encryptor origin/main

# Apply changes in §5:
#   - mobile/lib/core/database/base_field_encryptor.dart   (new abstract)
#   - mobile/lib/core/database/field_encryptor.dart        (implements base)
#   - mobile/lib/core/database/field_encryptor_web.dart    (implements base)
#   - mobile/test/fakes/fake_field_encryptor.dart           (new)
#   - mobile/lib/features/user/data/repositories/user_repository.dart  (DI refactor)

cd mobile && flutter analyze && flutter test
cd ..

git add ...
git commit -m "test(mobile): FakeFieldEncryptor + DI refactor on UserRepository"
git push -u origin hs2/s2-05-fake-field-encryptor
gh pr create --title "test(mobile): FakeFieldEncryptor test double [S2-05]"
```

---

## 2. PR description

### Summary

Introduces:

1. A new `BaseFieldEncryptor` abstract class extracted from the existing
   `FieldEncryptor` concrete. Same signatures (encrypt/decrypt with AAD,
   `initialize`, `reset`).
2. `FakeFieldEncryptor` test double implementing `BaseFieldEncryptor`
   with deterministic passthrough behavior.
3. `UserRepository` refactored to accept `BaseFieldEncryptor` via
   constructor (instead of reaching for the global `FieldEncryptor.instance`).

The pilot is `UserRepository` only — S2-02 and S2-04 follow the same
pattern when they merge.

### Why

Real `FieldEncryptor` is fast on a device but pokey in CI (AES-GCM
crypto adds ~ms-per-call latency × hundreds of test cases = visible
overhead). More importantly, tests that want to assert "this PII
field was encrypted" need to look for an `enc:v1:` prefix, which
couples the test to the encryption format. With a fake passthrough,
tests assert against the **business behavior** (the encrypt path was
called with the right AAD) — not the algorithm.

### What changes

- **`mobile/lib/core/database/base_field_encryptor.dart`** (new): abstract
  base class. Two implementations: production `FieldEncryptor` (mobile),
  pass-through `FieldEncryptor` (web). Tests use a third: `FakeFieldEncryptor`.
- **`mobile/lib/core/database/field_encryptor.dart`**: now extends
  `BaseFieldEncryptor`. No behavioral change.
- **`mobile/lib/core/database/field_encryptor_web.dart`**: same.
- **`mobile/test/fakes/fake_field_encryptor.dart`** (new): the fake.
- **`mobile/lib/features/user/data/repositories/user_repository.dart`**:
  constructor refactored to take `BaseFieldEncryptor` instead of
  reaching for `FieldEncryptor.instance`.
- **`mobile/lib/app/run_mobile.dart`** / `run_web.dart`: DI wiring
  injects the concrete encryptor.

### What does NOT change

- Production behavior — identical encryption.
- `CropListingRepository` — refactored in S2-04 when it lands.
- Any other call site of `FieldEncryptor.instance` outside the pilot
  scope — those refactor in their own stories.

### Backwards compatibility

The `BaseFieldEncryptor` abstract class is the new contract. The two
production implementations satisfy it. Anything else that imported
`FieldEncryptor` continues to work — `FieldEncryptor` is still the
concrete class; we just extracted a base.

### Test plan

```bash
cd mobile
flutter analyze
flutter test test/features/user/                  # uses the fake now
flutter test                                       # full suite — no regression
```

### Risk

Very low. Abstract-base extraction is a mechanical refactor.
`UserRepository` getting a constructor parameter is a small change with
a clear DI seam.

### Acceptance criteria

From `specs/features/isar-hardening/sprints.md#s2-05`:

- [ ] `FakeFieldEncryptor.encrypt` returns `'fake:$plaintext'`
- [ ] `FakeFieldEncryptor.decrypt` strips the `'fake:'` prefix
- [ ] Injectable via constructor on every repo (no global singleton override)
- [ ] One pilot repo (`UserRepository`) refactored to take it via constructor — pattern for others

---

## 3. Reviewer guide

1. **`BaseFieldEncryptor` should have the same signatures as the
   concrete classes from S1-01.** Confirm the abstract matches the
   production-mobile signature exactly, including the AAD named params.

2. **`FakeFieldEncryptor.encrypt` format includes AAD.** Tests can
   assert that AAD was propagated by checking the output for the
   expected `(collection, column, rowKey)`. Confirm the format choice
   is documented in the fake's class comment.

3. **No global singleton fallback.** `UserRepository` must take the
   encryptor via constructor with no default that falls back to
   `FieldEncryptor.instance`. A default fallback defeats the DI
   purpose.

---

## 4. Rollback plan

`git revert <merge-commit>`. Repositories that took the encryptor via
constructor revert to `FieldEncryptor.instance`. No production behavior
loss; tests slow down again.

---

## 5. Proposed diffs

### 5a. `mobile/lib/core/database/base_field_encryptor.dart` (new)

```dart
/// Contract for field-level encryption. Three implementations:
/// - FieldEncryptor (mobile): real AES-GCM via package:cryptography
/// - FieldEncryptor (web stub): passthrough no-op
/// - FakeFieldEncryptor (tests): deterministic, AAD-visible
abstract class BaseFieldEncryptor {
  bool get isInitialized;

  void initialize(List<int> keyBytes, {bool legacyPlaintextAllowed = false});

  Future<String> encrypt(
    String plaintext, {
    required String collection,
    required String column,
    required String rowKey,
  });

  Future<String> decrypt(
    String value, {
    required String collection,
    required String column,
    required String rowKey,
  });

  void reset();
}
```

### 5b. `mobile/lib/core/database/field_encryptor.dart`

```diff
-class FieldEncryptor {
+class FieldEncryptor extends BaseFieldEncryptor {
   // ... existing body unchanged ...
 }
```

### 5c. `mobile/lib/core/database/field_encryptor_web.dart`

```diff
-class FieldEncryptor {
+class FieldEncryptor extends BaseFieldEncryptor {
   // ... existing body unchanged, signatures already match ...
 }
```

### 5d. `mobile/test/fakes/fake_field_encryptor.dart` (new)

```dart
import 'package:wereda_connect/core/database/base_field_encryptor.dart';

/// Deterministic test double for [BaseFieldEncryptor].
///
/// `encrypt(plaintext, collection, column, rowKey)` returns
///   `'fake:$collection:$column:$rowKey:$plaintext'`
///
/// `decrypt(...)` strips the prefix and returns the plaintext.
/// Mismatched AAD (decrypt with a different collection/column/rowKey
/// than was used for encrypt) throws `ArgumentError` — mirrors the
/// real encryptor's AAD-mismatch failure.
///
/// Use in repository unit tests so assertions can compare cleartext
/// without depending on the real `enc:v1:` ciphertext format.
class FakeFieldEncryptor extends BaseFieldEncryptor {
  bool _initialized = false;

  @override
  bool get isInitialized => _initialized;

  @override
  void initialize(List<int> keyBytes, {bool legacyPlaintextAllowed = false}) {
    _initialized = true;
  }

  @override
  Future<String> encrypt(
    String plaintext, {
    required String collection,
    required String column,
    required String rowKey,
  }) async {
    return 'fake:$collection:$column:$rowKey:$plaintext';
  }

  @override
  Future<String> decrypt(
    String value, {
    required String collection,
    required String column,
    required String rowKey,
  }) async {
    final prefix = 'fake:$collection:$column:$rowKey:';
    if (!value.startsWith(prefix)) {
      throw ArgumentError(
        'FakeFieldEncryptor: AAD mismatch. Expected prefix "$prefix", '
        'got "${value.length > 40 ? '${value.substring(0, 40)}…' : value}".',
      );
    }
    return value.substring(prefix.length);
  }

  @override
  void reset() {
    _initialized = false;
  }
}
```

### 5e. `mobile/lib/features/user/data/repositories/user_repository.dart`

```diff
-class UserRepository extends BaseUserRepository {
-  final Isar _isar;
-  final FieldEncryptor _enc;
-
-  UserRepository({required Isar isar, required FieldEncryptor encryptor})
-      : _isar = isar, _enc = encryptor;
+class UserRepository extends BaseUserRepository {
+  final Isar _isar;
+  final BaseFieldEncryptor _enc;
+
+  UserRepository({required Isar isar, required BaseFieldEncryptor encryptor})
+      : _isar = isar, _enc = encryptor;
   // ... rest unchanged
 }
```

### 5f. DI wiring (`mobile/lib/app/run_mobile.dart`)

```diff
+getIt.registerLazySingleton<BaseFieldEncryptor>(
+  () => FieldEncryptor.instance,
+);
 getIt.registerLazySingleton<BaseUserRepository>(
   () => UserRepository(
     isar: IsarService.instance.db,
-    encryptor: FieldEncryptor.instance,
+    encryptor: getIt<BaseFieldEncryptor>(),
   ),
 );
```

---

## 6. Open questions for the reviewer

1. **Should `FakeFieldEncryptor` live under `mobile/test/fakes/` or
   `mobile/lib/testing/`?** Default: `mobile/test/fakes/`. Pro: test-only
   code stays out of the app bundle. Con: harder to share with widget
   tests in `mobile/test/widget/`. Reviewer call.

2. **`encrypt` output format — should it include AAD verbosely?**
   Current: `fake:$collection:$column:$rowKey:$plaintext`. Verbose;
   tests can assert AAD propagation. Alternative: just `fake:$plaintext`
   (matches the sprints.md acceptance criterion literally, but loses
   AAD-propagation testability). Default: verbose.

3. **Should we refactor all repos in this PR or just the pilot?**
   Story says pilot only. Reviewer pushback welcome — refactoring all
   in one PR avoids three more rounds of churn but inflates the
   diff. Default: pilot only.

---

*PR scaffold v1.0 — 2026-05-13 — depends on S1-01 merged*
