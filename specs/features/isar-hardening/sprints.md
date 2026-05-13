# Isar Hardening — Sprint Stories (JIRA-ready)

> See `README.md` for epics, risks, and rationale. This file is the
> story catalog — 28 stories across 7 sprints, formatted for direct
> import into JIRA / Linear / GitHub Issues.
> Tabular form: `stories.csv` (same content, bulk-import-ready).

**Story-ID convention:** `S{sprint}-{order}` e.g. `S1-01`, `S3-02`.
**Definition of Ready (all stories):** acceptance checklist agreed, test
approach defined, no unanswered PO question.
**Definition of Done (all stories):** acceptance checklist 100%, PR merged
to `main`, tests green in CI, security scan (Aikido) passing on touched
paths, doc cross-reference updated where relevant.

---

## Sprint 1 — Foundation: encryptor v1 + AAD (15 pts)

**Sprint goal:** Ship the encryption primitive with AAD binding, prefix
versioning, and key-integrity HMAC. No production caller wired yet.

**Demo:** Run the §15 security test suite live; show `decrypt(blob_A, column='B')` throwing `FieldDecryptionException`.

---

### S1-01 — AAD-bound encrypt/decrypt API

**As** a security engineer
**I want** `FieldEncryptor.encrypt/decrypt` to require `(collection, column, rowKey)` AAD
**So that** ciphertexts cannot be swapped between columns or rows

**Points:** 5  ·  **Labels:** mobile, security, encryption  ·  **Epic:** E1
**Blocked by:** —  ·  **Blocks:** S1-02, S1-04, S2-01, S2-02
**Files:**
- `mobile/lib/core/database/field_encryptor.dart`
- `mobile/lib/core/database/field_encryptor_web.dart` (signature parity)

**Acceptance:**
- [ ] `encrypt(plaintext, {required String collection, required String column, required String rowKey})`
- [ ] `decrypt(value, {required String collection, required String column, required String rowKey})`
- [ ] AAD = `utf8.encode('$collection:$column:$rowKey')`, passed to `AesGcm.encrypt/decrypt`
- [ ] Old single-arg signatures removed — calls without AAD fail to compile
- [ ] Web stub matches signature, returns input unchanged
- [ ] `mobile/lib/core/database/field_encryptor.dart` ≤ 200 lines

**Test approach:** §15 cross-row swap test + cross-column swap test. Both must throw `FieldDecryptionException`.
**Risk:** `cryptography: ^2.7.0` AAD parameter name drift across patch versions → pin exact (`cryptography: 2.7.0`).

---

### S1-02 — `enc:v1:` prefix + fail-closed decrypt

**As** a security engineer
**I want** every ciphertext prefixed `enc:v1:` and `decrypt` to reject anything missing the prefix
**So that** an attacker who overwrites a ciphertext blob with literal plaintext cannot trigger a downgrade

**Points:** 3  ·  **Labels:** mobile, security  ·  **Epic:** E1
**Blocked by:** S1-01  ·  **Blocks:** S1-04
**Files:** `mobile/lib/core/database/field_encryptor.dart`

**Acceptance:**
- [ ] `static const String _cipherPrefix = 'enc:v1:'`
- [ ] `encrypt()` returns `'enc:v1:${base64Encode(...)}'`
- [ ] `decrypt()` throws `FieldDecryptionException('Missing cipher prefix')` on input without prefix (unless `legacyPlaintextAllowed` is true)
- [ ] `decrypt()` throws `FieldDecryptionException('Invalid base64')` on malformed body
- [ ] `decrypt()` throws `FieldDecryptionException('Truncated ciphertext')` if combined length < 28
- [ ] `decrypt()` throws `FieldDecryptionException('MAC verification failed')` on `SecretBoxAuthenticationError`
- [ ] No code path returns the input value as plaintext on failure

**Test approach:** Tampered-MAC test, missing-prefix test, truncated-body test from §15.
**Risk:** None significant.

---

### S1-03 — Key-integrity HMAC on read-back

**As** a security engineer
**I want** the AES key verified by `HMAC-SHA256(key, label)` on every read from secure storage
**So that** a swapped/corrupted keystore entry triggers `KeyIntegrityFailure` instead of silently decrypting to garbage

**Points:** 3  ·  **Labels:** mobile, security  ·  **Epic:** E1
**Blocked by:** —  ·  **Blocks:** S1-04
**Files:** `mobile/lib/core/database/isar_service.dart`

**Acceptance:**
- [ ] Persist `Hmac(sha256).convert([...key, ...utf8.encode('wereda-isar-key-v1')]).bytes` alongside key
- [ ] Constant-time comparison on read (`_constantTimeEq`)
- [ ] Mismatch → throw `KeyIntegrityFailure` (new exception type)
- [ ] Caller in `initialize()` catches `KeyIntegrityFailure` → call `clearAllData()` → re-throw to force re-login
- [ ] Length check (32 bytes) remains as cheap pre-check

**Test approach:**
- Test: write key + tag, mutate one byte of key in secure storage, read-back throws
- Test: tag missing → treated as no-entry, regenerates fresh
- Test: constant-time compare returns false on length mismatch without early-return on diff

**Risk:** None — purely additive.

---

### S1-04 — `legacyPlaintextAllowed` migration toggle

**As** a dev
**I want** a one-shot `legacyPlaintextAllowed` parameter on `FieldEncryptor.initialize()`
**So that** the S3 migration can decrypt unprefixed legacy values without weakening production reads

**Points:** 2  ·  **Labels:** mobile, security, migration  ·  **Epic:** E1
**Blocked by:** S1-01, S1-02  ·  **Blocks:** S3-01
**Files:** `mobile/lib/core/database/field_encryptor.dart`

**Acceptance:**
- [ ] `initialize(List<int> keyBytes, {bool legacyPlaintextAllowed = false})`
- [ ] Default `false` — production reads fail closed
- [ ] When `true`, `decrypt(value, ...)` with missing prefix returns `value` unchanged
- [ ] `reset()` clears the flag
- [ ] Doc comment: "MIGRATION ONLY — production reads must run with `false`"

**Test approach:** Legacy-mode test from §15 (decrypt of unprefixed input returns input).
**Risk:** Engineer flips to `true` in production by accident → CI lint flags any `legacyPlaintextAllowed: true` outside `*_migration.dart`.

---

### S1-05 — Security test suite live

**As** QA
**I want** the eight §15 security tests live and green
**So that** every PR catches AAD / prefix / MAC regressions

**Points:** 2  ·  **Labels:** mobile, qa, security  ·  **Epic:** E1
**Blocked by:** S1-01..S1-04  ·  **Blocks:** —
**Files:** `mobile/test/core/database/field_encryptor_test.dart` (new)

**Acceptance:**
- [ ] Nonce-freshness property test (same plaintext + AAD → different ciphertext)
- [ ] Cross-row AAD swap test
- [ ] Cross-column AAD swap test
- [ ] Tampered-MAC byte-flip test
- [ ] Missing-prefix test
- [ ] Legacy-mode allow test
- [ ] All tests run in `flutter test` and in CI

**Risk:** Test-time `Isar.initializeIsarCore(download: true)` — these tests don't open Isar so download isn't required, but assert the test config doesn't introduce it.

---

## Sprint 2 — Wire encryption into User + Crop repositories (18 pts)

**Sprint goal:** Every PII read/write in `User`, `CropListing` goes through `FieldEncryptor`. Indexed phone gets `phoneSearchHash` + `phoneEncrypted` split. Production behavior changes — needs careful PR review.

**Demo:** Register a test farmer, kill app, open `wereda_connect.isar` in Isar Inspector → display name is `enc:v1:...`. Login by phone still works.

---

### S2-01 — Encrypt User PII fields (non-indexed)

**As** a farmer
**I want** `displayName`, `fullName`, and `faydaId` encrypted in Isar
**So that** a stolen device doesn't expose my profile

**Points:** 3  ·  **Labels:** mobile, security, user  ·  **Epic:** E1
**Blocked by:** S1-01, S1-04  ·  **Blocks:** S3-01
**Files:**
- `mobile/lib/core/database/schemas/user.dart` (no schema change — fields stay `String`)
- `mobile/lib/data/repositories/user_repository.dart`

**Acceptance:**
- [ ] `UserRepository.save(user)` encrypts each PII field with AAD `User:<column>:<user.userId>` before `put`
- [ ] `UserRepository.load(...)` decrypts on read; returns hydrated User
- [ ] BLoCs / screens consume hydrated objects — no plaintext leaks via streams
- [ ] Tests cover round-trip and all three role views (Farmer, Trucker, Buyer)
- [ ] Old plaintext rows decrypt successfully under `legacyPlaintextAllowed: true` (deferred to S3 migration)

**Test approach:** Mock `FieldEncryptor` with passthrough for unit tests; integration test with real encryptor on in-memory Isar.
**Risk:** PII fields used in `.filter()` queries — verify none of `displayName`, `fullName`, `faydaId` are in a query builder anywhere (grep audit).

---

### S2-02 — Split `phoneNumber` into `phoneSearchHash` + `phoneEncrypted`

**As** an engineer
**I want** phone numbers searchable while encrypted, via HMAC search column + AES blob
**So that** login-by-phone works without exposing the number at rest

**Points:** 8  ·  **Labels:** mobile, security, schema, breaking  ·  **Epic:** E1
**Blocked by:** S2-01  ·  **Blocks:** S3-01
**Files:**
- `mobile/lib/core/database/schemas/user.dart`
- `mobile/lib/data/repositories/user_repository.dart`
- `mobile/lib/features/auth/bloc/auth_bloc.dart` (login lookup)
- All consumers of `User.phoneNumber`

**Acceptance:**
- [ ] New fields: `@Index(unique: true) late String phoneSearchHash;` and `late String phoneEncrypted;`
- [ ] `phoneSearchHash = base64(HMAC-SHA256(phoneE164, pepper))` — pepper from `String.fromEnvironment('PHONE_PEPPER')`
- [ ] CI build fails if `PHONE_PEPPER` env not set in release build
- [ ] Old `phoneNumber` field retained but deprecated — populated by every write for back-compat (deleted in S3 migration)
- [ ] Login flow looks up by `phoneSearchHash`, falls back to `phoneNumber` if hash empty (S3 migration backfills hash)
- [ ] `build_runner` regen committed; `flutter build web --no-tree-shake-icons` passes

**Test approach:**
- Login-by-phone happy path test
- Lookup-by-hash test on a row with hash but no plaintext phone (post-migration shape)
- Lookup-by-phone test on a legacy row with phone but no hash (pre-migration shape)
- Snapshot test of `User.g.dart` to catch unintended regeneration

**Risk:** **Highest in S2.** Two failure modes —
1. Pepper rotation requires re-migration; document in S2 runbook before merge.
2. If `phoneSearchHash` index unique constraint violates on an existing user, login breaks. Migration must populate hash before this story merges. PO call: S2 ships behind a feature flag (`enablePhoneHashLookup`), flipped on after S3 migration verified.

---

### S2-03 — CI lint: no plaintext PII writes

**As** a security reviewer
**I want** CI to reject any PR adding `late String phoneNumber|faydaId|fullName` followed by `.put(` without `encrypt(`
**So that** the same regression that put us in this position cannot recur silently

**Points:** 3  ·  **Labels:** ci, security, lint  ·  **Epic:** E1
**Blocked by:** —  ·  **Blocks:** —
**Files:**
- `scripts/check_pii_writes.sh` (new)
- `.github/workflows/aikido.yml` (or equivalent) — wire to PR check

**Acceptance:**
- [ ] Script greps `mobile/lib/` for the offending pattern
- [ ] Exits 1 on match, prints offending file:line
- [ ] Allow-list: declarations annotated with `// @pii-encrypted-at-write` are skipped
- [ ] Runs on every PR via existing CI workflow
- [ ] False-positive escape hatch documented

**Test approach:** Synthetic test repo with one good file + one bad file; assert script catches bad, passes good.
**Risk:** False positives. The allow-list comment is intentionally explicit so reviewers see it.

---

### S2-04 — Encrypt CropListing PII

**As** a farmer
**I want** `farmerName` and `description` encrypted in Isar
**So that** my listings don't expose my identity on a stolen device

**Points:** 2  ·  **Labels:** mobile, security, crop  ·  **Epic:** E1
**Blocked by:** S2-01  ·  **Blocks:** S3-01
**Files:** `mobile/lib/data/repositories/crop_repository.dart`

**Acceptance:**
- [ ] `CropRepository.create/update` encrypts `farmerName`, optionally `description`
- [ ] Decryption on read for all role views (farmer-own, buyer-all)
- [ ] `locationName` stays plaintext — used in indexed filter
- [ ] Browse screens show plaintext after decryption; no `enc:v1:` leaks into UI

**Test approach:** Integration test with all three role views.
**Risk:** None significant.

---

### S2-05 — Repo-layer encrypted-passthrough mock

**As** a dev
**I want** a `FakeFieldEncryptor` test double
**So that** existing repo unit tests don't need crypto setup boilerplate

**Points:** 2  ·  **Labels:** mobile, qa  ·  **Epic:** E1
**Blocked by:** S1-01  ·  **Blocks:** —
**Files:** `mobile/test/fakes/fake_field_encryptor.dart` (new)

**Acceptance:**
- [ ] `FakeFieldEncryptor.encrypt` returns `'fake:$plaintext'`
- [ ] `FakeFieldEncryptor.decrypt` strips the `'fake:'` prefix
- [ ] Injectable via constructor on every repo (no global singleton override)
- [ ] One pilot repo (`UserRepository`) refactored to take it via constructor — pattern for others

**Risk:** None — purely additive test infrastructure.

---

## Sprint 3 — Migration v1→v2 (16 pts)

**Sprint goal:** Ship a once-only migration that encrypts all existing plaintext PII. Idempotent under crash-retry. Dev-cohort gated before production rollout (see `sprint-3-rollout.md`).

**Demo:** Private — live migration on a snapshot of a dev-cohort device.

---

### S3-01 — `_migrateToV2` encrypts plaintext PII

**As** an existing user
**I want** my plaintext PII automatically encrypted on next app launch
**So that** I get the security improvements with no manual action

**Points:** 8  ·  **Labels:** mobile, security, migration  ·  **Epic:** E1
**Blocked by:** S1-04, S2-01, S2-02, S2-04  ·  **Blocks:** S3-02
**Files:** `mobile/lib/core/database/migrations/migrate_v2_encryption.dart` (new)

**Acceptance:**
- [ ] Runs inside a single `isar.writeTxn`
- [ ] Initializes `FieldEncryptor` with `legacyPlaintextAllowed: true` for the duration; flips to `false` after
- [ ] For each `User`: encrypt `displayName`, `fullName`, `faydaId` IF not already `enc:v1:`
- [ ] For each `User`: compute `phoneSearchHash` from existing `phoneNumber`; populate `phoneEncrypted`
- [ ] For each `CropListing`: encrypt `farmerName`, optionally `description`
- [ ] **Idempotent:** running twice on the same DB is a no-op (verified by test)
- [ ] Schema version (`2`) stored in a `_meta` collection inside Isar, NOT in secure storage
- [ ] Migration completes in < 500ms for 1k rows on a Tecno Spark 8 — show "Updating storage…" splash if longer

**Test approach:**
- Pre-migration fixture: 100 plaintext User rows
- Run migration → assert all `displayName` start with `enc:v1:`
- Run migration again → assert nothing changes
- Crash-mid-loop simulation: kill at row 50, restart → completes correctly

**Risk:** **Critical — unrecoverable on corrupt outcome.** Gated by dev-cohort plan.

---

### S3-02 — Migrate Fayda images to encrypted blobs

**As** an existing farmer
**I want** my Fayda ID scans migrated from filesystem paths to encrypted blobs in Isar
**So that** a forensic dump of my device doesn't expose the national ID document

**Points:** 5  ·  **Labels:** mobile, security, migration, fayda  ·  **Epic:** E1
**Blocked by:** S3-01  ·  **Blocks:** —
**Files:**
- `mobile/lib/core/database/schemas/user.dart` (add `faydaIdFrontEnc`, `faydaIdBackEnc`, `drivingLicenseEnc` as `List<byte>`)
- `mobile/lib/core/database/migrations/migrate_v2_encryption.dart`
- All consumers that read `faydaIdFrontImagePath` etc.

**Acceptance:**
- [ ] New fields populated by migration: read file → encrypt with AAD `User:faydaId:<userId>:front` → store as blob
- [ ] Old file deleted from `documents/` after successful blob write (atomic within txn boundary where possible)
- [ ] Old path fields cleared (`null`)
- [ ] Migration handles missing files gracefully (user never uploaded ID)
- [ ] UI image widgets switch to reading from blob (memory image)

**Test approach:** Fixture with 3 users — 1 has both images, 1 has front only, 1 has none. Migrate → assert blobs present where expected, files removed.
**Risk:** **Defer to S6 if S3 slips.** Image migration is high-value but not on the critical path of the encryption-at-rest objective.

---

### S3-03 — Schema version inside encrypted DB

**As** a security engineer
**I want** `_kSchemaVersion` stored inside Isar (in a `_meta` collection), not in `FlutterSecureStorage`
**So that** an attacker who can reset secure-storage entries cannot force migrations to re-run on already-encrypted rows

**Points:** 3  ·  **Labels:** mobile, security, migration  ·  **Epic:** E1
**Blocked by:** S3-01  ·  **Blocks:** —
**Files:**
- `mobile/lib/core/database/schemas/meta.dart` (new)
- `mobile/lib/core/database/isar_service.dart` (`_runMigrationsIfNeeded` reads from `_meta`)

**Acceptance:**
- [ ] `@collection class Meta { Id id = 1; late int schemaVersion; }` (singleton row, fixed id)
- [ ] `_runMigrationsIfNeeded` reads/writes schema version via `Meta` collection
- [ ] Old `_schemaVersionKey` in secure storage deleted on first run after this story
- [ ] Test: tamper with secure storage `isar_schema_version` → migration still recognizes correct version from Isar

**Risk:** None significant.

---

## Sprint 4 — Sync queue session gating + idempotency wiring (17 pts)

**Sprint goal:** Background isolate cannot post one user's data under another's session. Idempotency keys flow from enqueue to server header.

**Demo:** Live shared-device demo: User-A creates an order offline → signs out → User-B signs in → trigger sync → Lambda log shows zero order POSTed under User-B.

---

### S4-01 — `SyncOperation.userId` field + session gate

**As** a buyer
**I want** my queued mutations to be POSTed only under my session, never the next user's
**So that** a shared device cannot leak my financial actions

**Points:** 5  ·  **Labels:** mobile, security, sync  ·  **Epic:** E2
**Blocked by:** —  ·  **Blocks:** S4-04
**Files:**
- `mobile/lib/core/models/sync_operation.dart`
- `mobile/lib/core/sync/sync_queue.dart`
- `mobile/lib/core/sync/sync_executor.dart`

**Acceptance:**
- [ ] `SyncOperation` gains `@Index() late String userId`
- [ ] `SyncQueue.enqueue(...)` requires `userId` (compile-fail without)
- [ ] `SyncExecutor._processOne(op)` calls `auth.currentUserId()` and returns early if `op.userId != activeUserId`
- [ ] Test from §15 "background flush refuses when userId != active session" green
- [ ] Telemetry breadcrumb (not error) when a skip occurs

**Risk:** Backward-compat with pre-migration queue rows that have no `userId`. PO call: drop them via a one-shot reaper (S4-04).

---

### S4-02 — Idempotency-Key header end-to-end

**As** a buyer
**I want** Telebirr escrow charges never duplicated on network flap
**So that** a flaky connection doesn't cost me twice

**Points:** 5  ·  **Labels:** mobile, server-contract, payments  ·  **Epic:** E2
**Blocked by:** —  ·  **Blocks:** —
**Files:**
- `mobile/lib/core/sync/idempotency_key_generator.dart` (verify behavior, add tests if missing)
- `mobile/lib/core/network/api_client.dart` (add `Idempotency-Key` header to relevant verbs)
- `mobile/lib/core/sync/sync_executor.dart` (pass `op.idempotencyKey` through)

**Acceptance:**
- [ ] Idempotency key generated once at `enqueue` time, stored on the `SyncOperation` row
- [ ] Retry uses the same key — never regenerated from `DateTime.now()`
- [ ] `Idempotency-Key: {userId}:{entityType}:{entityId}:{action}:{ts}` header on every POST/PUT/DELETE
- [ ] Unit test asserts key stability across simulated retries
- [ ] Integration test against staging: kill mid-POST, retry → server's `EscrowIdempotencyRepository` shows single row

**Test approach:** Hit staging escrow endpoint twice with same idempotency key → second call returns first call's stored response, no duplicate write.
**Risk:** Server already supports this (`EscrowIdempotencyRepository`, `ISyncMetadataRepository.IdempotencyKeyExistsAsync`). Client side just needs the header in the right place. Validate against the contract in `server/src/Functions/SyncFunction.cs:328`.

---

### S4-03 — Error classification (no token leaks)

**As** ops
**I want** `SyncOperation.lastErrorCode` to be an opaque code, never `e.toString()`
**So that** we never persist or telemeter `Authorization: Bearer …`

**Points:** 3  ·  **Labels:** mobile, security, observability  ·  **Epic:** E2
**Blocked by:** —  ·  **Blocks:** —
**Files:**
- `mobile/lib/core/models/sync_operation.dart` (rename `lastError` → `lastErrorCode`, type `String?` still)
- `mobile/lib/core/sync/sync_executor.dart` (add `_classifyError`)

**Acceptance:**
- [ ] `_classifyError(Object e)` returns one of: `network_timeout`, `network_unreachable`, `network_error`, `http_401_unauthenticated`, `http_403_forbidden`, `http_409_conflict`, `http_5xx_server`, `http_<code>`, `parse_error`, `unknown_error`
- [ ] No raw exception string ever assigned to `lastErrorCode`
- [ ] Test from §15: simulate Dio 401 with token in error message → stored code is `'http_401_unauthenticated'`, contains no `Bearer` substring, contains no `eyJ` JWT prefix
- [ ] Existing `lastError` field migrated to `lastErrorCode` (one-shot migration; pre-existing rows get `unknown_error`)

**Risk:** None significant.

---

### S4-04 — Stale-operation reaper on app start

**As** an engineer
**I want** queue rows belonging to a no-longer-active user dropped on app start
**So that** orphan mutations never get a chance to run under a different session

**Points:** 2  ·  **Labels:** mobile, security, sync  ·  **Epic:** E2
**Blocked by:** S4-01  ·  **Blocks:** —
**Files:**
- `mobile/lib/core/sync/sync_queue.dart` (`reapStaleOperations` method)
- `mobile/lib/app/run_mobile.dart` (call after auth resolved)

**Acceptance:**
- [ ] `await syncQueue.reapStaleOperations(currentUserId)` runs after `IsarService.initialize()` and after login resolved
- [ ] Deletes all `SyncOperation` rows where `userId != currentUserId`
- [ ] Pre-S4-01 rows (no `userId`) also deleted (treated as stale)
- [ ] Logged at info level for ops visibility

**Test approach:** Seed Isar with mixed-user ops, login as one user, call reaper, assert only that user's ops remain.
**Risk:** None.

---

### S4-05 — Connectivity-driven flush on regain

**As** an engineer
**I want** an immediate flush when the device comes back online, gated by an active session
**So that** users don't wait 15 minutes for queued actions to ship

**Points:** 2  ·  **Labels:** mobile, sync  ·  **Epic:** E2
**Blocked by:** S4-01, S4-02  ·  **Blocks:** —
**Files:** `mobile/lib/core/sync/connectivity_monitor.dart` or equivalent

**Acceptance:**
- [ ] Listener on `Connectivity().onConnectivityChanged`
- [ ] On transition to online: check `auth.hasValidAccessToken()`, then call `syncExecutor.flush()`
- [ ] Single-flight (no concurrent flushes if already running — `SyncExecutor._running` guard)
- [ ] Telemetry breadcrumb on each flush trigger source (timer / resume / connectivity)

**Risk:** Listener leaks if not cancelled on app dispose — wire to `WidgetsBindingObserver` shutdown.

---

## Sprint 5 — Biometric gate + WorkManager hardening (14 pts)

**Sprint goal:** Lost-phone-finder cannot open the app. Background sync rebinds to current user on every login.

**Demo:** Lock phone, hand to colleague, watch them fail to bypass the biometric prompt and reach the dashboard.

---

### S5-01 — Biometric authentication before key load

**As** a farmer
**I want** fingerprint or PIN required to unlock the app
**So that** a lost phone doesn't leak my data

**Points:** 5  ·  **Labels:** mobile, security, ux  ·  **Epic:** E3
**Blocked by:** —  ·  **Blocks:** S5-04
**Files:**
- `mobile/lib/core/security/biometric_gate.dart` (new)
- `mobile/lib/core/database/isar_service.dart` (`initialize` calls gate)

**Acceptance:**
- [ ] `LocalAuthentication.authenticate(localizedReason: ..., options: AuthenticationOptions(biometricOnly: false, stickyAuth: true))`
- [ ] Runs BEFORE `FieldEncryptor.initialize(keyBytes)`
- [ ] On failure: throw `UnauthorizedException`, do not load key
- [ ] On no-biometric-available + no-PIN-set: surface a first-run flow inviting the user to set up a PIN (S5-02), fall back to no-gate with persistent warning banner
- [ ] Localization: prompt in Amharic, Afan Oromo, English

**Test approach:** Manual on a device + emulator integration test that mocks `LocalAuthentication`.
**Risk:** Rural users on cheap Androids may not have biometric or PIN. **PO call: graceful fallback with banner is acceptable for v1; track these installs as a separate Sentry tag, revisit if > 10%.**

---

### S5-02 — No-PIN first-run flow

**As** a rural user on a device with no biometric/PIN
**I want** a one-time invitation to set up a device PIN
**So that** I can opt in to the biometric gate without being locked out

**Points:** 3  ·  **Labels:** mobile, ux, security  ·  **Epic:** E3
**Blocked by:** S5-01  ·  **Blocks:** —
**Files:** `mobile/lib/features/onboarding/screens/setup_pin_screen.dart` (new)

**Acceptance:**
- [ ] Shown once on first launch when `LocalAuthentication.isDeviceSupported() == false` or no biometrics enrolled
- [ ] Two CTAs: "Set up PIN now" (launches Android `Settings.ACTION_SECURITY_SETTINGS`) and "Skip for now"
- [ ] Skip persists a flag in `FlutterSecureStorage` so the prompt doesn't re-show every launch
- [ ] Persistent warning banner on home screen until PIN is set
- [ ] Localized

**Risk:** None significant.

---

### S5-03 — WorkManager per-user binding

**As** a dev
**I want** WorkManager tasks scoped per-user with `ExistingWorkPolicy.replace` on every login
**So that** a worker registered under User-A doesn't run for User-B after a re-login

**Points:** 3  ·  **Labels:** mobile, sync  ·  **Epic:** E3
**Blocked by:** S4-01  ·  **Blocks:** —
**Files:** `mobile/lib/core/sync/background_sync.dart` (refactor existing worker registration)

**Acceptance:**
- [ ] `BackgroundSync.register(userId)` uses task ID `wereda_bg_sync_<userId>`
- [ ] `existingWorkPolicy: ExistingWorkPolicy.replace`
- [ ] `BackgroundSync.cancelForUser(userId)` cancels by unique name
- [ ] Called on every successful login + on every logout
- [ ] WorkManager callback re-validates `userId` and `hasValidAccessToken()` before flushing (already in §11 callbackDispatcher)

**Risk:** Test on real Android device — emulator WorkManager behavior diverges.

---

### S5-04 — `clearAllData()` shreds the DB file

**As** ops
**I want** `IsarService.clearAllData()` to delete the Isar file from disk, not just truncate collections
**So that** logout is forensically clean

**Points:** 3  ·  **Labels:** mobile, security  ·  **Epic:** E3
**Blocked by:** S5-01  ·  **Blocks:** —
**Files:** `mobile/lib/core/database/isar_service.dart`

**Acceptance:**
- [ ] `clearAllData()` order: (1) close DB, (2) `File('$dir/wereda_connect.isar').delete()`, (3) `File('$dir/wereda_connect.isar.lock').delete()` if present, (4) delete encryption key from secure storage, (5) `FieldEncryptor.reset()`
- [ ] Idempotent — running on a missing file doesn't throw
- [ ] Test asserts file absent after call
- [ ] Test asserts a fresh `initialize()` after `clearAllData` creates a brand-new file with a brand-new key

**Risk:** None significant.

---

## Sprint 6 — Observability + release tooling (13 pts)

**Sprint goal:** When something breaks in prod, we know within minutes. Migrations have a kill switch.

**Demo:** Open CloudWatch dashboard. Trigger controlled failure → SNS alert fires within 5 min.

---

### S6-01 — Sentry/Crashlytics wiring for crypto failures

**As** ops
**I want** every `FieldDecryptionException` reported to Sentry at warning level
**So that** tampering, migration bugs, or key-swap attempts surface in dashboards

**Points:** 3  ·  **Labels:** mobile, observability  ·  **Epic:** E3
**Blocked by:** —  ·  **Blocks:** —
**Files:**
- `mobile/lib/core/database/field_encryptor.dart` (replace `_report` stub)
- `mobile/lib/main.dart` (Sentry init)
- `mobile/pubspec.yaml` (add `sentry_flutter`)

**Acceptance:**
- [ ] Sentry SDK initialized in `main.dart` with DSN from build env
- [ ] `_report(reason)` calls `Sentry.captureMessage(reason, level: SentryLevel.warning)`
- [ ] No raw plaintext or `rowKey` values in the message — only the abstract `reason`
- [ ] Sentry dashboard: alert if `FieldDecryptionException` > 0 per day after S3 migration tail

**Risk:** Sentry quota. ~50 events/day expected steady-state in migration tail; free tier covers.

---

### S6-02 — CloudWatch alarms for sync surface

**As** ops
**I want** alarms on the metrics in §15b
**So that** server-side regressions are caught before user reports

**Points:** 5  ·  **Labels:** infra, observability, cdk  ·  **Epic:** E3
**Blocked by:** —  ·  **Blocks:** —
**Files:** `infra/lib/stacks/observability-stack.ts` (new or extend existing)

**Acceptance:**
- [ ] Alarm: `SyncFunction` 5xx rate > 1% over 5 min
- [ ] Alarm: `SyncFunction` 409 rate > 0.1% over 1 hr (idempotency dedup storm)
- [ ] Alarm: `EscrowFunction` 4xx with stale `idempotencyKey` — any occurrence
- [ ] Alarm: Lambda cold-start p95 > 500ms
- [ ] Alarm: DynamoDB throttle on `SyncMetadata` or `EscrowIdempotency` — any
- [ ] All wired to a single SNS topic; subscribers: PO email + ops Slack webhook
- [ ] `infra/lib/stacks/` synth clean

**Risk:** None significant.

---

### S6-03 — Migration kill switch (remote config)

**As** a PO
**I want** a remote flag to disable the v2 migration if the dev cohort uncovers a critical bug
**So that** I can stop the rollout without shipping a new app build

**Points:** 3  ·  **Labels:** mobile, ops, migration  ·  **Epic:** E3
**Blocked by:** S3-01  ·  **Blocks:** —
**Files:**
- `mobile/lib/core/config/remote_config.dart` (new — simple HTTPS fetch of a JSON blob)
- `mobile/lib/core/database/isar_service.dart` (`_runMigrationsIfNeeded` reads the flag)

**Acceptance:**
- [ ] On app start, fetch `https://api.weredaconnect.com/config/mobile.json` with 2s timeout
- [ ] On success: cache locally in secure storage with 1-hour TTL
- [ ] On failure: use cached value, else default `{migration_disabled: false}`
- [ ] If `migration_disabled == true`: skip `_migrateToV2`, log warning, continue with legacy reads enabled
- [ ] Flag can also be flipped by editing a hardcoded constant in a hotfix build

**Test approach:** Integration test that toggles the flag and asserts migration skipped.
**Risk:** None significant — the flag is fail-safe (default off blocks nothing).

---

### S6-04 — Synthetic mobile-flavor sync test

**As** ops
**I want** an EventBridge cron invoking a "hello sync" Lambda every 5 minutes
**So that** API Gateway / Lambda regressions are caught before users

**Points:** 2  ·  **Labels:** infra, observability  ·  **Epic:** E3
**Blocked by:** —  ·  **Blocks:** —
**Files:** `infra/lib/stacks/synthetic-monitoring-stack.ts` (new)

**Acceptance:**
- [ ] EventBridge rule: `rate(5 minutes)` → Lambda
- [ ] Lambda makes a real POST to `/sync/changes` with a synthetic test JWT
- [ ] Response code != 200 → publish to SNS topic from S6-02
- [ ] Estimated cost < $1/month

**Risk:** Cost — at 12 invocations/hour × 24 × 30 = 8640 invocations/month. Well within free tier.

---

## Sprint 7 — Long-term key hygiene (12 pts, slippable)

**Sprint goal:** Compromise recovery is possible. Heap-resident key bytes minimized.

**Demo:** Run rotation drill — rotate key → assert all rows readable → rotate again.

---

### S7-01 — Implement `rotateEncryptionKey()`

**As** a security engineer
**I want** `IsarService.rotateEncryptionKey()` to actually work
**So that** a leaked key has a recovery path other than `clearAllData()`

**Points:** 8  ·  **Labels:** mobile, security  ·  **Epic:** E4
**Blocked by:** S3-01, S5-04  ·  **Blocks:** —
**Files:** `mobile/lib/core/database/isar_service.dart`

**Acceptance:**
- [ ] Generate new 32-byte key + HMAC tag
- [ ] In a single `writeTxn`: for every PII field on every row, decrypt with old key + AAD, re-encrypt with new key + AAD
- [ ] On success: write new key + tag, delete old key, `FieldEncryptor.reset()` + reinit with new key
- [ ] On any failure: roll back txn, retain old key, surface error to caller
- [ ] Rotation drill test in CI: rotate twice in a row, assert data readable both times
- [ ] Drill takes < 5s for 10k rows on a low-end device (Tecno Spark 8)

**Risk:** Failure mid-rotation = potentially mixed-key rows. Mitigation: all-or-nothing transaction. Test the failure path explicitly.

---

### S7-02 — Android Keystore platform channel (stretch)

**As** a security engineer
**I want** AES encrypt/decrypt routed through Android Keystore via a Kotlin platform channel
**So that** raw key bytes never enter the Dart heap and cold-boot DRAM extraction is mitigated

**Points:** 5  ·  **Labels:** mobile, android, security, stretch  ·  **Epic:** E4
**Blocked by:** S7-01  ·  **Blocks:** —
**Files:**
- `mobile/android/app/src/main/kotlin/.../KeystoreCrypto.kt` (new)
- `mobile/lib/core/database/keystore_crypto_channel.dart` (new)
- `mobile/lib/core/database/field_encryptor.dart` (delegate)

**Acceptance:**
- [ ] Kotlin: `KeyGenParameterSpec` with `KEY_PROPERTIES_KEY_ALGORITHM_AES`, `BLOCK_MODE_GCM`, `ENCRYPTION_PADDING_NONE`, `setUserAuthenticationRequired(false)` (gated by biometric at app-open instead)
- [ ] Dart side never sees raw key bytes — only the alias `wereda_aes_key_v1`
- [ ] `FieldEncryptor.encrypt/decrypt` calls the platform channel for AES, keeps AAD/prefix logic in Dart
- [ ] Fallback to in-Dart AES if API level < 23 (or any platform-channel failure) with telemetry warning
- [ ] iOS NOT in scope this story — iOS lands when iOS GA does

**Risk:** API divergence across Android OEM variants. Test on at least 3 OEM devices.
**PO call:** Can slip to Q4. Document explicitly if deferred.

---

## Cross-sprint dependency map

```
S1-01 ─┬─► S1-02 ─┬─► S1-04 ──► S3-01 ──┬─► S3-02
       └─► S1-05  └─► S2-01 ──► S2-02 ──┘
                  └─► S2-04 ──┐
                              ├─► S3-01 ──► S6-03
                              └─► S2-05

S4-01 ──► S4-04 ──► S4-05
S4-01 ──► S5-03
S4-02 (parallel)
S4-03 (parallel)

S5-01 ──► S5-02
S5-01 ──► S5-04

S6-01, S6-02, S6-04 (all parallel)
S3-01 ──► S6-03

S3-01, S5-04 ──► S7-01 ──► S7-02 (stretch)
```

---

## JIRA / Linear import notes

- Each story has a 1:1 mapping to an issue.
- Use the `Story-ID` (e.g. `S2-02`) as the issue key suffix or label.
- The `Files:` block can be copy-pasted into the issue description verbatim.
- `Labels:` includes domain tags (`mobile`, `security`, `infra`, `qa`, `ux`) and epic shortcuts (`epic-e1` … `epic-e4`).
- `Blocked by` / `Blocks` map directly to JIRA "is blocked by" links.

---

*Story catalog v1.0 — 2026-05-13*
