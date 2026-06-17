# Isar Database — WeredaConnect Implementation Guide

> **Scope:** Mobile (Android primary, iOS forward-compatible). Web is **API-only** —
> no offline branch, no IndexedDB fallback. Web targets the admin/staff console
> which is always online; rebuilding 9 schemas on an alternate storage layer is
> explicitly out of scope.
>
> **Platform-pair contract:** every file in `mobile/lib/core/database/` has a web
> counterpart that satisfies the type-checker as a no-op. See `CLAUDE.md`
> → "Platform Pairs" and `.ai/completeness-rules.md` Rule 2.

---

## ⚠️ Required hardening (current gaps in production code)

This is the source-of-truth list. Treat anything below as a **PR-blocker** for a
financial-flow change (orders, escrow, payments) until closed. The full
rationale lives in the sections referenced; the items are summarized here so
reviewers and contributors can scan one place.

> **Execution plan:** `specs/features/isar-hardening/` holds the 7-sprint roadmap, 28 JIRA-ready stories (and CSV), the Sprint-3 dev-cohort rollout runbook, per-story PR scaffolds, and the founder/security risk-acceptance sign-off page.
>
> **HS-1 status — COMPLETE.** The four foundation primitives (AAD binding,
> `enc:v1:` prefix + fail-closed, key-integrity HMAC, legacy-flag CI guardrail)
> are live in `main`. Closed in this table: items #3 and #10. The remaining
> open items below ship in HS-2 through HS-7. The companion type-design refactor
> (`abstract class CryptoFailure` + enum reasons) shipped in PR #21.

| # | Gap | Status | Section |
|---|---|---|---|
| 1 | `FieldEncryptor.encrypt/decrypt` has **zero production call sites** | OPEN | §7, §8 |
| 2 | `@Index(unique: true) phoneNumber` cannot be both indexed and encrypted as-is | OPEN | §7 (HMAC search column) |
| 3 | No AAD / domain separation — ciphertext is portable between columns and rows | **CLOSED (HS-1 / S1-01)** | §7 |
| 4 | Fayda ID images stored as **filesystem paths**, not encrypted blobs | OPEN | §7 (image pattern) |
| 5 | `SyncOperation.payload` may contain plaintext PII (encrypt-then-enqueue is not enforced) | OPEN | §9 |
| 6 | `SyncOperation` has no `userId` → background isolate can flush under wrong session | OPEN | §9, §11 |
| 7 | `lastError` is `e.toString()` → can persist tokens/URLs to disk and telemetry | OPEN | §10 |
| 8 | `rotateEncryptionKey()` throws `UnimplementedError` | OPEN | §7 |
| 9 | No biometric gate before loading the AES key from secure storage | OPEN | §7 |
| 10 | No integrity HMAC on the persisted key → swap attack possible | **CLOSED (HS-1 / S1-03)** | §7 |
| 11 | Migrations not provably idempotent under `enc:v1:` prefix check | OPEN | §14 |
| 12 | WorkManager uses `ExistingWorkPolicy.keep` → survives user switch | OPEN | §11 |
| 13 | `Isar.clear()` doesn't shred WAL pages | OPEN | §5 |
| 14 | Sentry/Crashlytics referenced in comments only — `_report()` is `debugPrint` | OPEN | §7 |
| 15 | Cert pinning wired (`cert_pinning_io.dart`) but not documented in this guide | DOC GAP | §12 (new) |
| 16 | `allowBackup="false"` is set in `AndroidManifest.xml` — good, document it | OK | §12 (new) |
| 17 | Server-side idempotency exists (`EscrowIdempotencyRepository`) — document the contract | OK | §16 (new) |

**Mobile vs. web stance — unchanged:** mobile is offline-first with Isar; web is
**online-only, no offline mode, no IndexedDB fallback**. Web stubs satisfy types,
not behavior. Re-affirmed throughout.

---

## Table of Contents

1. [Architecture Overview](#1-architecture-overview)
2. [Project Setup](#2-project-setup)
3. [Project Structure (as built)](#3-project-structure-as-built)
4. [Schemas](#4-schemas)
5. [Initialization](#5-initialization)
6. [Web Stubs — How "No Offline on Web" Works](#6-web-stubs--how-no-offline-on-web-works)
7. [Field-Level Encryption (AES-256-GCM)](#7-field-level-encryption-aes-256-gcm)
8. [Repository Pattern + Role-Based Visibility](#8-repository-pattern--role-based-visibility)
9. [Sync Queue Lifecycle](#9-sync-queue-lifecycle)
10. [Sync Executor & Conflict Resolver](#10-sync-executor--conflict-resolver)
11. [Background Sync (Timer + WorkManager)](#11-background-sync-timer--workmanager)
12. [Reactive UI with BLoC](#12-reactive-ui-with-bloc)
13. [Session & Device Identity](#13-session--device-identity)
14. [Migrations](#14-migrations)
15. [Testing](#15-testing)
16. [Performance Best Practices](#16-performance-best-practices)
17. [Platform Guard — Defense in Depth](#17-platform-guard--defense-in-depth)

---

## 1. Architecture Overview

```
┌─────────────────────────────────────────────┐
│            Flutter UI (BLoC)                │
│  BlocBuilder/BlocListener subscribe to      │
│  states emitted from repository streams     │
└────────────────────┬────────────────────────┘
                     │
┌────────────────────▼────────────────────────┐
│      Repository layer (per feature)         │
│  Encrypt PII → Isar.writeTxn → enqueue sync │
│  Decrypt PII on read · Enforce role filter  │
└────────────────────┬────────────────────────┘
                     │
┌────────────────────▼────────────────────────┐
│  IsarService.instance.db  (mobile only)     │
│  9 collections · @Index · auto-watch        │
└────────────────────┬────────────────────────┘
                     │ SyncQueue.push() / pull()
┌────────────────────▼────────────────────────┐
│  SyncExecutor + SyncWorker + ConflictResolver│
│  Idempotency-keyed POST/PUT/DELETE to API   │
└────────────────────┬────────────────────────┘
                     │
              .NET 10 AOT Lambda + DynamoDB
```

**Why Isar:**

| | Isar | sqflite | Hive |
|---|---|---|---|
| 1k writes (warm) | ~8 ms | ~120 ms | ~45 ms |
| Reactive `.watch()` | ✅ | ❌ | ❌ |
| Type-safe queries | ✅ | ⚠️ | ⚠️ |
| Web support | ❌ | ❌ | ✅ |
| Built-in encryption | ❌ (3.1) | ✅ SQLCipher | ✅ |
| Schema migration | additive auto / manual breaking | manual | manual |

Isar's `❌ web` and `❌ built-in encryption` are the two gaps WeredaConnect explicitly designs around: stubs cover web, AES-256-GCM at the field layer covers encryption.

---

## 2. Project Setup

### `mobile/pubspec.yaml` (relevant deps)

```yaml
dependencies:
  isar: ^3.1.0
  isar_flutter_libs: ^3.1.0      # native binaries; auto-excluded on web target
  path_provider: ^2.1.0
  flutter_secure_storage: ^9.0.0 # Android Keystore-backed
  cryptography: ^2.7.0           # AES-GCM, not crypto:^3 (no AES there)
  connectivity_plus: ^5.0.0
  workmanager: ^0.5.0             # OS-level background sync (Android)
  flutter_bloc: ^8.1.0
  device_info_plus: ^9.0.0
  uuid: ^4.0.0

dev_dependencies:
  isar_generator: ^3.1.0
  build_runner: ^2.4.0
```

### Android

`android/app/build.gradle`:

```gradle
android {
    defaultConfig {
        minSdkVersion 26   // Android 8.0 — WeredaConnect baseline (2GB RAM)
        targetSdkVersion 34
    }
}
```

`android/app/src/main/AndroidManifest.xml`:

```xml
<uses-permission android:name="android.permission.INTERNET" />
<uses-permission android:name="android.permission.ACCESS_NETWORK_STATE" />
<uses-permission android:name="android.permission.RECEIVE_BOOT_COMPLETED" />
<uses-permission android:name="android.permission.FOREGROUND_SERVICE" />
```

### Codegen

```bash
cd mobile && dart run build_runner build --delete-conflicting-outputs
```

Run this after any `@collection` change. CI fails if `*.g.dart` files are stale.

---

## 3. Project Structure (as built)

```
mobile/lib/core/
├── database/
│   ├── isar_service.dart           # singleton + AES-key bootstrap (mobile)
│   ├── isar_service_web.dart       # no-op stub (web)
│   ├── isar_stub.dart              # type shims for `package:isar/isar.dart`
│   ├── field_encryptor.dart        # AES-256-GCM (mobile)
│   ├── field_encryptor_web.dart    # passthrough (web)
│   └── schemas/
│       ├── user.dart           crop_listing.dart   trip.dart
│       ├── order.dart          delivery.dart       payment.dart
│       ├── payment_intent.dart buyer_intent.dart   sync_operation.dart
│
├── sync/
│   ├── sync_queue.dart / sync_queue_web.dart
│   ├── sync_executor.dart / sync_executor_web.dart
│   ├── sync_worker.dart            # in-process Timer (foreground)
│   ├── conflict_resolver.dart      # server-wins (accept server version)
│   ├── idempotency_key_generator.dart
│   └── connectivity_monitor.dart
│
└── auth/
    ├── auth_storage.dart           # interface
    ├── auth_storage_mobile.dart    auth_storage_web.dart
    ├── offline_auth_cache.dart     offline_auth_cache_web.dart
```

Every file with a `_web.dart` counterpart is part of the platform-pair contract.

---

## 4. Schemas

Nine `@collection` classes register with `Isar.open`. Each is annotated for index-backed queries. Pattern from `mobile/lib/core/database/schemas/crop_listing.dart`:

```dart
import 'package:isar/isar.dart';

part 'crop_listing.g.dart';

@collection
class CropListing {
  Id id = Isar.autoIncrement;

  @Index(unique: true)
  late String listingId;       // server UUID

  @Index()
  late String farmerId;        // role-visibility key
  String farmerName = '';

  @Index()
  late String cropType;        // teff, wheat, barley, ...
  late double quantity;        // quintals
  late double pricePerKg;      // ETB

  late LatLng location;
  @Index() late String locationName;
  late String description;
  String? audioNoteRef;

  @Index()
  @Enumerated(EnumType.name)
  late CropStatus status;

  late DateTime createdAt;
  late DateTime updatedAt;

  bool isSynced = false;
  bool isDeleted = false;       // soft delete — always
  DateTime? deletedAt;
}
```

**Indexing rule:** index *only* fields used in `.filter()` / `.sortBy()` / role
filtering (`farmerId`, `truckerId`, `buyerId`, status enums). Indexing free-text
fields like `description` wastes storage.

---

## 5. Initialization

`mobile/lib/core/database/isar_service.dart` is a singleton. The init flow:

```dart
class IsarService {
  static IsarService? _instance;
  Isar? _isar;

  static const _secureStorage = FlutterSecureStorage(
    aOptions: AndroidOptions(encryptedSharedPreferences: true),
  );

  IsarService._();
  static IsarService get instance => _instance ??= IsarService._();

  Isar get db {
    if (_isar == null) {
      throw StateError('IsarService not initialized. Call initialize() first.');
    }
    return _isar!;
  }

  bool get isInitialized => _isar != null;
  FieldEncryptor get fieldEncryptor => FieldEncryptor.instance;

  Future<Isar> initialize() async {
    PlatformGuard.assertNotWeb();   // defense in depth — see §17
    if (_isar != null) return _isar!;

    final dir = await getApplicationDocumentsDirectory();

    final keyBytes = await _getOrCreateEncryptionKey();
    FieldEncryptor.instance.initialize(keyBytes);

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
      ],
      directory: dir.path,
      name: 'wereda_connect',
    );
    return _isar!;
  }

  Future<List<int>> _getOrCreateEncryptionKey() async {
    const keyName = 'isar_encryption_key';
    try {
      final existing = await _secureStorage.read(key: keyName);
      if (existing != null && existing.isNotEmpty) {
        final bytes = base64Decode(existing);
        if (bytes.length == 32) return bytes;
        await _secureStorage.delete(key: keyName);  // invalid length, regen
      }
    } on Exception catch (e) {
      debugPrint('[IsarService] read key failed: $e');
    }

    final rng = Random.secure();
    final key = List<int>.generate(32, (_) => rng.nextInt(256));
    try {
      await _secureStorage.write(key: keyName, value: base64Encode(key));
    } on Exception catch (e) {
      debugPrint('[IsarService] persist key failed: $e');
    }
    return key;
  }

  Future<void> close() async {
    await _isar?.close();
    _isar = null;
  }

  /// Destructive — wipes the AES key. Existing DB becomes unreadable.
  /// Use for logout-with-clear or factory reset.
  Future<void> clearAllData() async {
    await close();
    await _secureStorage.delete(key: 'isar_encryption_key');
  }
}
```

Called from `mobile/lib/app/run_mobile.dart`:

```dart
WidgetsFlutterBinding.ensureInitialized();
await IsarService.instance.initialize();
```

---

## 6. Web Stubs — How "No Offline on Web" Works

The web target compiles the same screens, BLoCs, and feature code. Three stub files satisfy the type-checker so nothing referencing Isar breaks.

### `isar_service_web.dart`

```dart
class IsarService {
  static IsarService? _instance;
  IsarService._();
  static IsarService get instance => _instance ??= IsarService._();

  /// `dynamic` so callers like `isar.cropListings.where()` compile to JS.
  dynamic get db => null;

  bool get isInitialized => false;
  FieldEncryptor get fieldEncryptor => FieldEncryptor.instance;

  Future<dynamic> initialize() async => null;
  Future<void> close() async {}
  Future<void> clearAllData() async {}
}
```

### `isar_stub.dart` — type shims

`package:isar/isar.dart` itself can't compile to JS. The conditional import swaps it out:

```dart
import 'package:isar/isar.dart'
    if (dart.library.html) 'package:wereda_connect/core/database/isar_stub.dart';
```

The stub declares just enough surface to type-check:

```dart
class Isar {
  static const int autoIncrement = -9007199254740990;
  Future<T> writeTxn<T>(Future<T> Function() cb) =>
      throw UnsupportedError('Isar not available on web');
  Future<T> txn<T>(Future<T> Function() cb) =>
      throw UnsupportedError('Isar not available on web');
  Future<bool> close() async => true;

  dynamic get cropListings => null;
  dynamic get trips => null;
  dynamic get users => null;
  // ...one getter per collection
  @override
  dynamic noSuchMethod(Invocation i) => null;
}
typedef Id = int;
```

### Runtime contract

The stubs **must never execute** — if they do, they throw. Real code is gated:

- All Isar-touching code is reached only on mobile because the **screens
  themselves use conditional imports** of `IsarService`. On web, the no-op
  `IsarService` is wired in and the BLoC takes the API-only branch.
- For BLoCs that accept a nullable `isar: Isar?`, guard with
  `kIsWeb ? null : IsarService.instance.db` and branch internally.

### What web actually does

Web (admin console) talks to the API directly:

| Operation | Mobile | Web |
|---|---|---|
| Read list | `isar.crops.watch()` (then optional API pull) | `dio.get('/crops')` only |
| Write | `isar.writeTxn` → sync queue → API | `dio.post('/crops')` |
| Offline | full read/write into Isar, deferred sync | **not supported — show offline banner** |
| PII storage | encrypted in Isar | never persisted on disk |

This is intentional: the admin web is always-online; rural mobile users are not.

---

## 7. Field-Level Encryption (AES-256-GCM)

Isar 3.1 has no full-DB encryption. Sensitive fields (phone, name, faydaId) are encrypted at the repository layer using `package:cryptography`'s `AesGcm.with256bits()`. **Never use the XOR sketch sometimes shown in tutorials — it provides zero confidentiality.**

### Threat model this section addresses

| Adversary | Capability | Mitigation in this section |
|---|---|---|
| Lost-phone finder, no PIN | Open app | Biometric gate before key load |
| Family member sharing device | Read cached PII | Per-user key derivation (planned), biometric gate |
| Rooted device / malicious same-UID app | Read Isar file + secure storage | AAD-bound ciphertexts, key-integrity HMAC, prefix versioning |
| Backup-restore on attacker device | Restore Google Drive backup | `allowBackup="false"` (§12), key non-exportable wrap |
| State actor / cold boot | DRAM extraction | Long-term fix: platform-channel Keystore op (planned, see N-H1) |

### Production gaps (as of this writing)

The code in `mobile/lib/core/database/field_encryptor.dart` implements the
algorithm correctly but is **not invoked from any production repository.**
`user.dart:24` declares `late String phoneNumber` and `user.dart:40` declares
`String? faydaId`; both are written plaintext. The encryption layer below is
the **target state.** Wiring it in is the single biggest pending security
task.

```dart
class FieldDecryptionException implements Exception {
  final String reason;
  const FieldDecryptionException(this.reason);
  @override
  String toString() => 'FieldDecryptionException: $reason';
}

class FieldEncryptor {
  static FieldEncryptor? _instance;
  final AesGcm _algorithm = AesGcm.with256bits();
  SecretKey? _secretKey;
  bool _initialized = false;

  /// Version tag prepended to every ciphertext: `enc:v1:<base64>`.
  /// Decrypt **rejects** values without this prefix unless [_legacyPlaintextAllowed]
  /// is true. The legacy flag is flipped to false once the v2-encryption migration
  /// completes (see §14) — after that, an unprefixed value is treated as tampering.
  static const String _cipherPrefix = 'enc:v1:';

  /// One-shot flag, gated by a `SharedPreferences`/`SecureStorage` key. Set to
  /// `true` before the migration runs, flipped to `false` after the last row
  /// is re-encrypted. Production reads should always run with this `false`.
  bool _legacyPlaintextAllowed = false;

  /// Identifies the column / row a ciphertext belongs to. Passed as AES-GCM
  /// Associated Authenticated Data so a blob from `UserCollection.displayName`
  /// row A cannot be moved to `UserCollection.displayName` row B (or to any
  /// other column) without the MAC failing. Without this, AES-GCM authenticates
  /// the bytes but not the context — see N-C1 in the security review.
  ///
  /// Format: `<collection>:<column>:<stableRowKey>` — e.g.
  /// `UserCollection:displayName:cognito_sub_abc123`. The `stableRowKey` must
  /// be a value that never changes for the lifetime of the row (server UUID,
  /// not the local autoincrement Isar `id`, which can be reassigned on import).
  String _aad(String collection, String column, String rowKey) =>
      '$collection:$column:$rowKey';

  FieldEncryptor._();
  static FieldEncryptor get instance => _instance ??= FieldEncryptor._();
  bool get isInitialized => _initialized;

  void initialize(List<int> keyBytes, {bool legacyPlaintextAllowed = false}) {
    if (keyBytes.length != 32) {
      throw ArgumentError('Encryption key must be 32 bytes (AES-256)');
    }
    _secretKey = SecretKey(Uint8List.fromList(keyBytes));
    _legacyPlaintextAllowed = legacyPlaintextAllowed;
    _initialized = true;
  }

  /// Format: `enc:v1:base64(nonce(12) || ciphertext || mac(16))`. Fresh nonce per call.
  ///
  /// **Fail-closed:** throws `StateError` if called before `initialize()`.
  /// Never returns plaintext silently — a release-build race that bypassed
  /// encryption is exactly the bug this throw is meant to surface.
  ///
  /// **AAD binding required.** Callers MUST pass the collection, column, and a
  /// stable row key. The trio is authenticated alongside the ciphertext so
  /// that a blob taken from one column cannot be planted into another.
  Future<String> encrypt(
    String plaintext, {
    required String collection,
    required String column,
    required String rowKey,
  }) async {
    if (!_initialized) {
      throw StateError(
        'FieldEncryptor.encrypt called before initialize(). '
        'IsarService.initialize() must complete before any repository write.',
      );
    }
    final aad = utf8.encode(_aad(collection, column, rowKey));
    final box = await _algorithm.encrypt(
      utf8.encode(plaintext),
      secretKey: _secretKey!,
      aad: aad,
    );
    final out = Uint8List(box.nonce.length + box.cipherText.length + box.mac.bytes.length);
    var i = 0;
    out.setAll(i, box.nonce);        i += box.nonce.length;
    out.setAll(i, box.cipherText);   i += box.cipherText.length;
    out.setAll(i, box.mac.bytes);
    return '$_cipherPrefix${base64Encode(out)}';
  }

  /// Decrypts `enc:v1:<base64>` ciphertext, binding to the same AAD as encrypt.
  ///
  /// **Fail-closed contract:**
  /// - No prefix          → `FieldDecryptionException` (or returns input if
  ///                        `_legacyPlaintextAllowed`, used only by migration)
  /// - Bad base64         → `FieldDecryptionException`
  /// - Truncated body     → `FieldDecryptionException`
  /// - MAC verify failed  → `FieldDecryptionException` + telemetry breadcrumb
  ///   (covers wrong key, tampering, AAD mismatch — i.e. column/row swap)
  ///
  /// Never returns the on-disk value as plaintext on failure. The prior
  /// "migration-friendly" fallback was a downgrade-attack vector: an attacker
  /// with write access to the Isar file could replace `enc:v1:<blob>` with a
  /// literal `+251911234567` and have the app accept it.
  Future<String> decrypt(
    String value, {
    required String collection,
    required String column,
    required String rowKey,
  }) async {
    if (!_initialized) {
      throw StateError(
        'FieldEncryptor.decrypt called before initialize(). '
        'IsarService.initialize() must complete before any repository read.',
      );
    }

    if (!value.startsWith(_cipherPrefix)) {
      if (_legacyPlaintextAllowed) {
        // ONLY reachable while the v2-encryption migration is running.
        return value;
      }
      // Tampering or a programming bug — fail closed.
      _report('Missing cipher prefix on read');
      throw const FieldDecryptionException('Missing cipher prefix');
    }

    final body = value.substring(_cipherPrefix.length);

    final List<int> combined;
    try {
      combined = base64Decode(body);
    } on FormatException {
      _report('Ciphertext is not valid base64');
      throw const FieldDecryptionException('Invalid base64');
    }
    if (combined.length < 28) {
      // 12 nonce + 16 mac is the minimum legal GCM envelope
      _report('Ciphertext truncated (length=${combined.length})');
      throw const FieldDecryptionException('Truncated ciphertext');
    }

    final nonce = combined.sublist(0, 12);
    final mac = Mac(combined.sublist(combined.length - 16));
    final ct = combined.sublist(12, combined.length - 16);

    try {
      final aad = utf8.encode(_aad(collection, column, rowKey));
      final plain = await _algorithm.decrypt(
        SecretBox(ct, nonce: nonce, mac: mac),
        secretKey: _secretKey!,
        aad: aad,
      );
      return utf8.decode(plain);
    } on SecretBoxAuthenticationError {
      // Active tampering, wrong key, AAD mismatch (column/row swap), or
      // corrupted disk. ALWAYS abnormal once migration has completed —
      // surface immediately to crash reporting.
      _report('GCM MAC verification failed (collection=$collection, column=$column)');
      throw const FieldDecryptionException('MAC verification failed');
    }
  }

  /// Hook for Sentry/Crashlytics. Replace with the project's reporter wrapper.
  /// Do **not** include the raw value or the AAD `rowKey` here — those can
  /// leak PII or identifiers into the telemetry sink.
  void _report(String reason) {
    debugPrint('[FieldEncryptor] $reason');
    // Sentry.captureMessage(reason, level: SentryLevel.warning);
  }

  /// Zeroize and drop key material. Call on logout, on app pause if you want
  /// a fast re-lock, and during key rotation. The SecretKey wrapper does not
  /// expose a destroy() in cryptography 2.7.x, so we drop the reference and
  /// rely on the GC; the original `List<int>` from `_getOrCreateEncryptionKey`
  /// should be wiped by the caller (see IsarService._wipeAndClear).
  void reset() {
    _secretKey = null;
    _initialized = false;
    _legacyPlaintextAllowed = false;
  }
}
```

**Key storage:** `FlutterSecureStorage` with `encryptedSharedPreferences: true` →
Android Keystore (hardware-backed where a TEE is available). The 32-byte AES
key is generated with `Random.secure()` on first launch and persisted as base64
to avoid UTF-16 surrogate corruption on round-trip.

**What gets encrypted:** any field marked PII (`displayName`, `faydaId`, etc.).
For fields that must remain indexable (e.g. `phoneNumber` with `@Index(unique: true)`),
GCM ciphertext is *not* a valid index value — random nonces produce different
output every call. Use a **deterministic search column** instead:

```dart
@Index(unique: true)
late String phoneSearchHash;     // HMAC-SHA256(phone, pepper) — peppered, deterministic
late String phoneEncrypted;      // enc:v1:<blob> — for display
```

Bulk content (crop description, location name) stays plaintext so indexed
queries run without per-row decryption.

**Why the version prefix matters.** The earlier "return ciphertext on failure"
pattern looked migration-friendly but opened a downgrade attack: any attacker
with write access to the Isar file (root, debuggable build, malicious backup
restore) could overwrite `enc:v1:<blob>` with the literal plaintext they
wanted — phone number, fake Fayda ID — and the app would accept it. The
prefix turns that into a typed exception that crash reporting will catch.

### Key-integrity HMAC on read-back

`flutter_secure_storage` may return a *valid-looking but wrong* 32 bytes if a
backup-restore on a different device, a malicious same-UID app, or rare
firmware-level corruption rewrites the entry. Length-check alone is not enough.

Persist an HMAC alongside the key and verify on read. Mismatch → wipe the DB
and force re-login (the data is already unrecoverable; the alternative is
silent garbage):

```dart
// IsarService._getOrCreateEncryptionKey (replacement sketch)
Future<List<int>> _getOrCreateEncryptionKey() async {
  const keyName = 'isar_encryption_key';
  const tagName = 'isar_encryption_key_tag';   // HMAC(key, app-static label)
  const label = 'wereda-isar-key-v1';

  final raw = await _secureStorage.read(key: keyName);
  final tag = await _secureStorage.read(key: tagName);
  if (raw != null && tag != null) {
    final bytes = base64Decode(raw);
    final expected = base64Decode(tag);
    final computed = Hmac(sha256).convert([...bytes, ...utf8.encode(label)]).bytes;
    if (bytes.length == 32 && _constantTimeEq(computed, expected)) {
      return bytes;
    }
    // Integrity check failed → treat as compromise. Caller will wipe DB.
    await _secureStorage.delete(key: keyName);
    await _secureStorage.delete(key: tagName);
    throw const KeyIntegrityFailure();
  }

  final rng = Random.secure();
  final key = List<int>.generate(32, (_) => rng.nextInt(256));
  final newTag = Hmac(sha256).convert([...key, ...utf8.encode(label)]).bytes;
  await _secureStorage.write(key: keyName, value: base64Encode(key));
  await _secureStorage.write(key: tagName, value: base64Encode(newTag));
  return key;
}

bool _constantTimeEq(List<int> a, List<int> b) {
  if (a.length != b.length) return false;
  var diff = 0;
  for (var i = 0; i < a.length; i++) { diff |= a[i] ^ b[i]; }
  return diff == 0;
}
```

### Legacy-flag CI enforcement (S1-04)

The `legacyPlaintextAllowed: true` flag is the HS-3 migration's only
sanctioned downgrade path. Setting it outside the allow-list
(`mobile/lib/core/database/migrations/` and `mobile/test/`) reopens
the C-1 downgrade-attack surface that S1-02 closed.

CI enforcement: `scripts/check_legacy_plaintext_flag.sh`, wired into
`pr-validation.yml`'s `security-lints` job. Any PR that introduces
`legacyPlaintextAllowed: true` outside the allow-list fails the check.

The lint is line-oriented grep — a determined contributor could split
`legacyPlaintextAllowed:\n    true` across two lines to evade it. That
is an explicit non-goal; code review catches that case.

Self-test fixtures live at `mobile/test/fixtures/lint/{good,bad}_example.dart`
and are exercised by the same CI job via `--self-test`.

### Biometric gate before key load

`local_auth` is in `pubspec.yaml`. Use it. The current init flow loads the AES
key as soon as the app process starts, which means a finder who picks up an
already-unlocked phone gets full Isar access. Add a step before
`FieldEncryptor.initialize(keyBytes)`:

```dart
Future<void> initialize() async {
  PlatformGuard.assertNotWeb();
  if (_isar != null) return;

  // Block until the user proves presence on the device.
  // Falls back to device PIN if no biometric is enrolled.
  final auth = LocalAuthentication();
  final canCheck = await auth.canCheckBiometrics || await auth.isDeviceSupported();
  if (canCheck) {
    final ok = await auth.authenticate(
      localizedReason: 'Unlock WeredaConnect',
      options: const AuthenticationOptions(
        biometricOnly: false,           // allow device PIN as fallback
        stickyAuth: true,
        useErrorDialogs: true,
      ),
    );
    if (!ok) {
      throw const UnauthorizedException('Biometric authentication failed');
    }
  }

  final keyBytes = await _getOrCreateEncryptionKey();
  // ...as before
}
```

For Android 9+ you can do better: configure `flutter_secure_storage` so the
key entry itself requires `BIOMETRIC_STRONG` to read. That moves the check
into Keystore, where it can't be bypassed by injecting before `initialize()`:

```dart
static const _secureStorage = FlutterSecureStorage(
  aOptions: AndroidOptions(
    encryptedSharedPreferences: true,
    keyCipherAlgorithm: KeyCipherAlgorithm.RSA_ECB_PKCS1Padding,
    storageCipherAlgorithm: StorageCipherAlgorithm.AES_GCM_NoPadding,
    // Requires AndroidX biometric library + Android 9+
    // userAuthenticationRequired: true,  // when supported by plugin version
  ),
);
```

Call out failures clearly — never silently fall through to plaintext storage
because the keystore returned `null` due to an auth check.

### Encrypting Fayda ID images (not paths)

`user.dart:43-49` declares:

```dart
String? faydaIdFrontImagePath;
String? faydaIdBackImagePath;
String? drivingLicensePath;
```

These point to raw image files under `getApplicationDocumentsDirectory()`.
Fayda is the **Ethiopian national identity document**. Storing it as an
unencrypted bitmap on a 2GB-RAM shared device is unacceptable. Migrate the
storage:

```dart
@collection
class User {
  // Replace the path fields with encrypted blobs (Isar handles List<byte>).
  List<byte> faydaIdFrontEnc = const [];   // AES-GCM with AAD per §7
  List<byte> faydaIdBackEnc  = const [];
  List<byte> drivingLicenseEnc = const [];
}

Future<void> saveFaydaImage(File f, User user, {required String side}) async {
  final bytes = await f.readAsBytes();
  final box = await FieldEncryptor.instance._algorithm.encrypt(
    bytes,
    secretKey: FieldEncryptor.instance._secretKey!,
    aad: utf8.encode('User:faydaId:${user.userId}:$side'),
  );
  final blob = Uint8List(box.nonce.length + box.cipherText.length + box.mac.bytes.length)
    ..setAll(0, box.nonce)
    ..setAll(12, box.cipherText)
    ..setAll(12 + box.cipherText.length, box.mac.bytes);
  await isar.writeTxn(() async {
    if (side == 'front') user.faydaIdFrontEnc = blob;
    if (side == 'back')  user.faydaIdBackEnc  = blob;
    await isar.users.put(user);
  });
  await f.delete();   // remove the loose file from disk
}
```

Two hundred-kilobyte scans fit comfortably in Isar; no need for a separate
encrypted-file scheme. Delete the original file from `documents/` once the
blob lands in Isar.

The migration path (one-time, runs at the v1→v2 schema bump):

```dart
// Inside _runMigrationsIfNeeded, gated by the schema version.
Future<void> _migrateToEncryptedV1(Isar isar) async {
  FieldEncryptor.instance.reset();
  FieldEncryptor.instance.initialize(keyBytes, legacyPlaintextAllowed: true);

  await isar.writeTxn(() async {
    final users = await isar.users.where().findAll();
    for (final u in users) {
      // decrypt() will return legacy values unchanged thanks to the flag.
      // Calling encrypt() then prefixes them properly.
      if (!u.displayName.startsWith(FieldEncryptor._cipherPrefix)) {
        u.displayName = await FieldEncryptor.instance.encrypt(u.displayName);
      }
      // Repeat for every PII field, every collection.
      await isar.users.put(u);
    }
  });

  // Re-init WITHOUT the legacy flag — production reads now fail closed.
  FieldEncryptor.instance.reset();
  FieldEncryptor.instance.initialize(keyBytes, legacyPlaintextAllowed: false);
}
```

---

## 8. Repository Pattern + Role-Based Visibility

Repositories are the only layer that touches Isar. They enforce **role-based visibility** (a HARD requirement in `CLAUDE.md`):

| Role | Crops they see | Trips they see |
|---|---|---|
| Farmer | own only — `.farmerIdEqualTo(currentUserId)` | none (truckers' domain) |
| Trucker | none | own only — `.truckerIdEqualTo(currentUserId)` |
| Buyer | **all** — no filter | **all** — no filter |

Buyers see everything; farmers and truckers see only their own records. **A violation is an automatic FAIL** — enforce in the repository, not in the UI.

> **Client filtering is correctness, not security.** A farmer with a rooted
> phone or a custom-built client can bypass `farmerIdEqualTo(currentUserId)`
> and read every `CropListing` row that ever reached their device. The
> repository filter exists to keep already-authorized data tidy for the user
> who owns the device. **The actual authorization boundary is the API** —
> the .NET AOT Lambda handlers must independently scope every query to the
> caller's role, and must never return rows the caller is not entitled to
> see. Never weaken a server-side check because "the client already filters."

```dart
class CropRepository {
  final Isar _isar;
  final FieldEncryptor _enc;
  final SyncQueue _queue;
  CropRepository(this._isar, this._enc, this._queue);

  /// CREATE — farmer only.
  Future<CropListing> create({
    required String farmerId,
    required String farmerName,
    required String cropType,
    required double quantity,
    required double pricePerKg,
    required String description,
    required String locationName,
    required LatLng location,
  }) async {
    final crop = CropListing()
      ..listingId = const Uuid().v4()
      ..farmerId = farmerId
      ..farmerName = await _enc.encrypt(farmerName)
      ..cropType = cropType
      ..quantity = quantity
      ..pricePerKg = pricePerKg
      ..description = description
      ..locationName = locationName
      ..location = location
      ..status = CropStatus.available
      ..createdAt = DateTime.now().toUtc()
      ..updatedAt = DateTime.now().toUtc();

    await _isar.writeTxn(() async {
      await _isar.cropListings.put(crop);
    });

    await _queue.enqueue(
      entityType: 'CropListing',
      entityId: crop.id,
      operation: SyncOp.create,
      payload: _serialize(crop),
    );
    return crop;
  }

  /// READ — farmer sees own; buyer sees all.
  Stream<List<CropListing>> watchForRole({
    required UserRole role,
    required String currentUserId,
  }) {
    final builder = _isar.cropListings.filter().isDeletedEqualTo(false);
    final filtered = switch (role) {
      UserRole.farmer  => builder.and().farmerIdEqualTo(currentUserId),
      UserRole.buyer   => builder,                         // sees ALL
      UserRole.trucker => builder.and().listingIdEqualTo(''), // empty result
    };
    return filtered.sortByUpdatedAtDesc().watch(fireImmediately: true);
  }

  Future<void> softDelete(CropListing crop) async {
    crop
      ..isDeleted = true
      ..deletedAt = DateTime.now().toUtc()
      ..updatedAt = DateTime.now().toUtc()
      ..isSynced = false;

    await _isar.writeTxn(() => _isar.cropListings.put(crop));
    await _queue.enqueue(
      entityType: 'CropListing',
      entityId: crop.id,
      operation: SyncOp.delete,
      payload: jsonEncode({'listingId': crop.listingId}),
    );
  }
}
```

`TripRepository` mirrors this for truckers/buyers. Buyer-side `BuyerIntentRepository` is intent-only (buyers don't create crop/trip listings).

---

## 9. Sync Queue Lifecycle

Every mutation produces a `SyncOperation` row. Status transitions:

```
       ┌── retryable failure ──┐
       ▼                        │
  pending ──► processing ──► failed (retryCount++) ──┤
                  │                                   │
                  ▼                                   │
              completed                               │
                                                      │
   retryCount >= maxRetries (3) ──► dead-letter ◄────┘
```

```dart
@collection
class SyncOperation {
  Id id = Isar.autoIncrement;

  /// The Cognito sub that owns this queued mutation. Background isolates and
  /// post-logout flushes MUST verify this matches the active session before
  /// posting. Without it, a queued mutation from User-A can be posted under
  /// User-B's session token after a logout/login on a shared device.
  @Index() late String userId;

  @Index() late String entityType;     // "CropListing", "Trip", ...
  late int entityLocalId;
  String? entityRemoteId;

  @Enumerated(EnumType.name)
  late SyncOp operation;               // create | update | delete

  /// Stable across retries — generated once at enqueue time. The server uses
  /// this for dedup via `EscrowIdempotencyRepository` (escrow flows) and
  /// `ISyncMetadataRepository.IdempotencyKeyExistsAsync` (generic sync).
  late String idempotencyKey;          // {userId}:{entityType}:{entityId}:{action}:{ts}

  /// JSON payload. **PII fields MUST already be in `enc:v1:` ciphertext form
  /// before this row is created** (encrypt-then-enqueue). Repository callers
  /// build the JSON from the post-encryption representation. See §8.
  late String payload;

  @Enumerated(EnumType.name)
  SyncStatus status = SyncStatus.pending;

  int retryCount = 0;
  static const int maxRetries = 3;

  late DateTime createdAt;
  DateTime? lastAttemptAt;

  /// Sanitized error code — never `e.toString()`. Raw exceptions from Dio
  /// carry URLs, headers (potentially with `Authorization: Bearer …`), and
  /// response bodies. Persisting them to Isar and forwarding to Sentry is a
  /// credential-leak vector. Map known exception types to opaque codes:
  /// `'network_timeout' | 'http_403' | 'http_409_conflict' | 'parse_error' …`
  String? lastErrorCode;

  bool get canRetry => retryCount < maxRetries;
}
```

**Why `userId` matters.** Without it, the background worker's flush can dump
queued operations under whatever Cognito session happens to be cached when
WorkManager fires. Realistic on a shared device: User-A enqueues an order →
logs out → User-B logs in → 15 min later WorkManager fires → User-A's order
is POSTed under User-B's token. The server now thinks User-B placed the order.
Adding `userId` lets the executor reject mismatches before they reach the API
(§10).

**Encrypt-then-enqueue rule.** The repository layer must encrypt PII *before*
building the JSON that goes into `payload`. If the repository decrypts on
read, mutates the in-memory copy, and the queue serializer reads the in-memory
copy, the queue collection on disk contains plaintext PII — defeating the
encryption-at-rest goal. See §8 for the safe serialization pattern.

**Idempotency keys** prevent duplicate server-side writes if a flush retries
after a partial success (server saved the row, response was dropped). Format:
`{userId}:{entityType}:{entityId}:{action}:{timestamp}` — generated by
`idempotency_key_generator.dart` and stored in `SyncOperation.idempotencyKey`.

**Critical rule: the key is generated *once*, at `SyncQueue.enqueue()` time,
and is reused for every retry of the same operation.** Re-deriving it on each
retry (e.g. using `DateTime.now()` at flush time) defeats the entire point —
the server sees a "new" request each time and creates duplicates.

For WeredaConnect this is not just a data-integrity nicety: orders and trip
acceptances flow through Telebirr escrow. A duplicate `create order` on a
flaky network can charge a buyer twice. The idempotency key is the single
defense, and it must be:

1. Generated at enqueue time
2. Persisted in the `SyncOperation` row
3. Sent in the `Idempotency-Key` HTTP header on every retry of that row
4. Deduped server-side, with a TTL longer than `maxRetries * backoff`

Verify the generator via `mobile/test/core/sync/sync_queue_test.dart`
— missing tests here block any PR that touches the sync path.

---

## 10. Sync Executor & Conflict Resolver

```dart
class SyncExecutor {
  final Isar _isar;
  final ApiClient _api;
  final ConflictResolver _resolver;
  bool _running = false;

  Future<void> flush() async {
    if (_running) return;
    _running = true;
    try {
      final pending = await _isar.syncOperations
          .filter()
          .statusEqualTo(SyncStatus.pending)
          .and().retryCountLessThan(SyncOperation.maxRetries)
          .sortByCreatedAt()
          .findAll();

      for (final op in pending) {
        await _processOne(op);
      }
    } finally {
      _running = false;
    }
  }

  Future<void> _processOne(SyncOperation op) async {
    // Session-gate: refuse to flush mutations belonging to a different user.
    // This is the only line between "queued under User-A" and "POSTed under
    // User-B's token" on a shared device.
    final activeUserId = await _auth.currentUserId();
    if (activeUserId == null) {
      // No live session — skip; the next foreground login will resume.
      return;
    }
    if (op.userId != activeUserId) {
      // Stale operation from a different user. Park it; do not POST.
      // Reaper job (§11) decides whether to migrate or drop.
      return;
    }

    await _isar.writeTxn(() async {
      op
        ..status = SyncStatus.processing
        ..lastAttemptAt = DateTime.now().toUtc();
      await _isar.syncOperations.put(op);
    });

    try {
      switch (op.operation) {
        case SyncOp.create:
          // The Idempotency-Key header is locked at enqueue time. NEVER
          // derive it from DateTime.now() at flush — that defeats dedup.
          final res = await _api.create(op.entityType, op.payload,
              idempotencyKey: op.idempotencyKey);
          await _attachRemoteId(op, res.id);
        case SyncOp.update:
          await _api.update(op.entityType, op.payload,
              idempotencyKey: op.idempotencyKey);
        case SyncOp.delete:
          await _api.delete(op.entityType, jsonDecode(op.payload),
              idempotencyKey: op.idempotencyKey);
      }
      await _markCompleted(op);
    } on Object catch (e) {
      await _isar.writeTxn(() async {
        op
          ..status = op.canRetry ? SyncStatus.pending : SyncStatus.failed
          ..retryCount += 1
          ..lastErrorCode = _classifyError(e);   // <-- opaque code, not toString()
        await _isar.syncOperations.put(op);
      });
    }
  }

  /// Map any thrown error to a small set of opaque codes. NEVER persist or
  /// log the raw exception — Dio errors include request URL, headers (with
  /// bearer tokens if the interceptor attached them), and response body.
  String _classifyError(Object e) {
    if (e is TimeoutException) return 'network_timeout';
    if (e is SocketException) return 'network_unreachable';
    if (e is DioException) {
      final code = e.response?.statusCode;
      if (code == null) return 'network_error';
      if (code == 401) return 'http_401_unauthenticated';
      if (code == 403) return 'http_403_forbidden';
      if (code == 409) return 'http_409_conflict';
      if (code >= 500) return 'http_5xx_server';
      return 'http_${code}';
    }
    if (e is FormatException) return 'parse_error';
    return 'unknown_error';
  }
}
```

**Server-side dedup.** The Lambda backend already implements idempotency on
the relevant flows:

- `server/src/Data/EscrowIdempotencyRepository.cs` — `idempotencyKey` is the
  DynamoDB partition key for escrow operations
- `server/src/Data/ISyncMetadataRepository.cs` — `IdempotencyKeyExistsAsync` /
  `StoreIdempotencyKeyAsync` for generic sync operations
- `server/src/Functions/SyncFunction.cs:328` — rejects requests missing
  `operationId`, `idempotencyKey`, `entityType`, `entityId`, `action`

The mobile-side guarantee (one key per row, reused on retry) plus the
server-side guarantee (dedup window covers `maxRetries * backoff`) together
make duplicate Telebirr escrow charges impossible under network flap. Either
side dropping the contract reopens the gap.

**Conflict resolution — server wins, unconditionally:**

```dart
class ConflictResolver {
  /// When the server reports a version mismatch on push, accept the server
  /// version verbatim. The local Isar row is overwritten; the in-flight
  /// SyncOperation that triggered the conflict is marked `synced`, not failed.
  ///
  /// This matches `mobile/lib/core/sync/conflict_resolver.dart`.
  CropListing resolve(CropListing local, Map<String, dynamic> server) {
    return local
      ..cropType    = server['cropType']
      ..quantity    = (server['quantity'] as num).toDouble()
      ..pricePerKg  = (server['pricePerKg'] as num).toDouble()
      ..description = server['description'] as String
      ..status      = CropStatus.values.byName(server['status'] as String)
      ..updatedAt   = DateTime.parse(server['updatedAt'] as String)
      ..isSynced    = true;
  }
}
```

**Why not last-write-wins by `updatedAt`?** Client `updatedAt` is the device
clock, and **it is trivially forged** by changing the system clock. A user (or
malware) that sets the phone clock to 2099 would have every local mutation
beat any legitimate concurrent edit from another authorized device. For
financial flows (Telebirr escrow, order state, delivery confirmation) that's
not acceptable. Server-wins is the safer default; the server is the only
party whose timestamps and version numbers we trust.

**Delta-pull watermark — use server time, not client time.** The high-water
mark sent in the next `since=` query must come from the **`serverTime` field
the API returns alongside the page of changes**, not from `DateTime.now()` on
the device. Clock skew of even a few minutes either silently drops updates
("clock ahead — no changes since the future") or replays them ("clock behind —
re-merge the same rows"). Persist the server-supplied watermark to
`FlutterSecureStorage` keyed by user ID.

Future enhancement: per-field merge (CRDT-style) for low-stakes, high-conflict
entities like trip status updates. For money flows, keep server-wins.

---

## 11. Background Sync (Timer + WorkManager)

Two complementary tiers:

**Tier 1 — In-process `Timer`** (`sync_worker.dart` in the repo):

- Runs while the app is in foreground/active
- 15-minute periodic flush + immediate flush on resume + immediate flush on connectivity regain
- Lives in the same isolate, has the `IsarService` singleton + AES key in memory

**Tier 2 — WorkManager** (adopted from the proposal; OS-level when app is killed):

```dart
@pragma('vm:entry-point')
void callbackDispatcher() {
  Workmanager().executeTask((taskName, _) async {
    if (taskName != 'periodic_sync') return true;
    try {
      // Background isolate has no app state — bootstrap services manually.
      await IsarService.instance.initialize();

      // Refuse to flush without a live, fresh access token. Without this
      // check, a logged-out user's pending mutations would be POSTed under
      // whatever stale token the auth cache happens to hold.
      final auth = AuthServiceFactory.build();
      final userId = await auth.currentUserId();
      final tokenStillValid = await auth.hasValidAccessToken();
      if (userId == null || !tokenStillValid) {
        await IsarService.instance.close();
        return true;  // not an error — just nothing to do safely
      }

      final executor = SyncExecutorFactory.build();
      await executor.flush();              // session-gates per-op (see §10)

      await IsarService.instance.close();
      return true;
    } catch (_) {
      return false;   // WorkManager retries with exponential backoff
    }
  });
}

class BackgroundSync {
  /// Register the periodic worker. Call this on every successful login —
  /// `replace` ensures the worker is bound to the *current* user, not
  /// whoever first registered it on this device.
  static Future<void> register({required String userId}) async {
    await Workmanager().initialize(callbackDispatcher);
    await Workmanager().registerPeriodicTask(
      'wereda_bg_sync_$userId',          // per-user task ID
      'periodic_sync',
      frequency: const Duration(minutes: 15),  // Android minimum
      constraints: Constraints(
        networkType: NetworkType.connected,
        requiresBatteryNotLow: true,
      ),
      existingWorkPolicy: ExistingWorkPolicy.replace,  // bind to current user
    );
  }

  /// Cancel on logout so a stale worker can't run with the previous user's
  /// queue rows. The per-op session gate (§10) also blocks this, but
  /// belt-and-braces — cancel the worker entirely.
  static Future<void> cancelForUser(String userId) =>
      Workmanager().cancelByUniqueName('wereda_bg_sync_$userId');
}
```

### Stale-operation reaper

`SyncOperation` rows whose `userId` no longer matches the active user can
accumulate after a logout where `clearAllData()` was skipped. The reaper runs
once on app start:

```dart
Future<void> reapStaleOperations(Isar isar, String currentUserId) async {
  await isar.writeTxn(() async {
    final stale = await isar.syncOperations
        .filter()
        .not().userIdEqualTo(currentUserId)
        .findAll();
    // Policy choice: drop them. Alternative (rare): migrate to currentUserId
    // if-and-only-if the user explicitly re-affirms ownership (privacy ask).
    for (final op in stale) {
      await isar.syncOperations.delete(op.id);
    }
  });
}
```

**Constraints** matter for Ethiopian users on prepaid data + low battery:
- `networkType: NetworkType.connected` (or `.unmetered` if WiFi-only desired)
- `requiresBatteryNotLow: true`
- 15-minute floor (Android Doze restriction)

---

## 11b. Network & Manifest Hardening

The Isar layer is only as safe as the perimeter around it. This is what
production has and what the offline-first contract relies on.

### Certificate pinning (already wired)

`mobile/lib/core/network/api_client.dart` uses a conditional import:

```dart
import 'package:wereda_connect/core/network/cert_pinning_stub.dart'
    if (dart.library.io) 'package:wereda_connect/core/network/cert_pinning_io.dart'
    as cert_pinning;
// ...
cert_pinning.applyCertPinning(dio);
```

Web target gets the stub (no pinning — fine, browsers handle TLS validation
and the WeredaConnect web admin is online-only). Mobile target gets the
`dart:io` implementation that pins the API Gateway certificate fingerprint.
Any sync flush over `https://vhhm8gh9c1.execute-api.eu-north-1.amazonaws.com`
without a matching pin **must fail closed**. MITM on a captive-portal hotspot
becomes an HTTP failure that the queue retries later, not a silent
credential disclosure.

**Operational note:** pin rotation is a release-engineering task. Track the
current pin and the next pin (overlap window) in `infra/lib/stacks/`. If the
pin diverges from what's deployed, *every mobile install becomes unreachable*
until an app update ships. Always release with both pins active before
rotating the API Gateway cert.

### Android Manifest — backup, debug, network security

Production `mobile/android/app/src/main/AndroidManifest.xml` already has:

```xml
<application
    android:allowBackup="false"
    android:fullBackupContent="false"
    android:dataExtractionRules="@xml/data_extraction_rules"
    android:networkSecurityConfig="@xml/network_security_config"
    ...>
```

Why each line matters:

| Attribute | Effect | Why |
|---|---|---|
| `allowBackup="false"` | Excludes app data from Google Drive auto-backup | Isar DB and `flutter_secure_storage` key would otherwise sync to Google. Backup is encrypted in transit but **decryptable on restore to any device on the same Google account.** |
| `fullBackupContent="false"` | Belt-and-braces; also blocks `adb backup` on Android < 12 | Even with `allowBackup=true`, this would still neutralize `adb backup` |
| `dataExtractionRules="@xml/..."` | Android 12+ replacement for the above; xml allows fine-grained allow/deny | Future-proofing |
| `networkSecurityConfig="@xml/..."` | Blocks cleartext traffic, restricts trusted CAs | Stops on-device misconfigured DNS/proxies from downgrading TLS |

Sample `res/xml/network_security_config.xml`:

```xml
<?xml version="1.0" encoding="utf-8"?>
<network-security-config>
  <base-config cleartextTrafficPermitted="false">
    <trust-anchors>
      <certificates src="system" />
      <!-- no user CAs -- ignore admin/MITM CAs installed by enterprise MDM -->
    </trust-anchors>
  </base-config>
</network-security-config>
```

`cleartextTrafficPermitted="false"` + omitting `<certificates src="user"/>`
means: only system CAs are trusted, and there's no HTTP escape hatch. Combine
with cert pinning above.

### Debuggable builds — never ship

Release builds must have `android:debuggable="false"` (Gradle's `release`
buildType handles this). A debuggable build is `adb shell run-as` accessible,
which means **anyone with a USB cable can dump the Isar file and secure
storage** regardless of `allowBackup`.

Verify with `aapt dump xmltree` on the release APK before each store upload:

```bash
aapt dump xmltree app-release.apk AndroidManifest.xml | grep -E 'allowBackup|debuggable'
```

### iOS forward compatibility

When the iOS app ships, mirror these protections:

- Keychain access group with `kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly`
- `NSFileProtectionComplete` on the Isar directory (file unreadable when
  device is locked, even by other processes)
- `FlutterSecureStorage(iOptions: IOSOptions(accessibility: KeychainAccessibility.first_unlock_this_device))`
- Disable iCloud backup of the documents directory by setting the resource
  attribute `NSURLIsExcludedFromBackupKey = YES` on the Isar file at open time

---

## 12. Reactive UI with BLoC

WeredaConnect uses BLoC (not Riverpod). Convert Isar streams into BLoC state:

```dart
class CropListBloc extends Bloc<CropListEvent, CropListState> {
  final CropRepository _repo;
  StreamSubscription<List<CropListing>>? _sub;

  CropListBloc(this._repo) : super(const CropListState.loading()) {
    on<CropListSubscribed>(_onSubscribed);
    on<_CropListUpdated>((e, emit) => emit(CropListState.data(e.items)));
  }

  Future<void> _onSubscribed(CropListSubscribed e, Emitter emit) async {
    await _sub?.cancel();
    _sub = _repo
        .watchForRole(role: e.role, currentUserId: e.userId)
        .listen((items) => add(_CropListUpdated(items)));
  }

  @override
  Future<void> close() async {
    await _sub?.cancel();
    return super.close();
  }
}
```

In the widget:

```dart
BlocBuilder<CropListBloc, CropListState>(
  builder: (context, state) => switch (state) {
    CropListLoading() => const CircularProgressIndicator(),
    CropListData(:final items) => ListView.builder(
      itemCount: items.length,
      itemBuilder: (_, i) => ListTile(
        title: Text(items[i].cropType),
        subtitle: Text('${items[i].quantity} q · ${items[i].locationName}'),
        trailing: Icon(
          items[i].isSynced ? Icons.cloud_done : Icons.cloud_off,
          color: items[i].isSynced ? Colors.green : Colors.orange,
        ),
      ),
    ),
    CropListError(:final msg) => Text(msg),
  },
);
```

Cancel stream subscriptions in `close()` — leaking them keeps Isar listeners alive.

---

## 13. Session & Device Identity

Device ID is generated once and persisted in `FlutterSecureStorage`:

```dart
class SessionManager {
  static const _storage = FlutterSecureStorage(
    aOptions: AndroidOptions(encryptedSharedPreferences: true),
  );
  static const _deviceIdKey = 'wereda_device_id';

  Future<String> getOrCreateDeviceId() async {
    var id = await _storage.read(key: _deviceIdKey);
    if (id == null) {
      id = const Uuid().v4();
      await _storage.write(key: _deviceIdKey, value: id);
    }
    return id;
  }

  Future<String> getDeviceName() async {
    if (Platform.isAndroid) {
      final i = await DeviceInfoPlugin().androidInfo;
      return '${i.brand} ${i.model}';
    }
    if (Platform.isIOS) {
      final i = await DeviceInfoPlugin().iosInfo;
      return '${i.name} (${i.model})';
    }
    return 'Unknown device';
  }
}
```

Session heartbeat (every 5 min, or on app resume) updates `lastHeartbeat`. The server uses it to expire stale sessions and detect single-device-login violations.

---

## 14. Migrations

Isar handles **additive** schema changes automatically (adding a new optional field, adding a new collection). For **breaking** changes — renaming, type changes, removing fields with data — you must migrate manually before `Isar.open` returns to callers, or guard reads with backfill logic.

```dart
Future<Isar> _openWithMigration() async {
  final dir = await getApplicationDocumentsDirectory();
  final isar = await Isar.open([...], directory: dir.path, name: 'wereda_connect');

  await _maybeMigrate(isar);
  return isar;
}

Future<void> _maybeMigrate(Isar isar) async {
  // Schema version lives INSIDE the encrypted DB (not in secure storage) so
  // it can't be reset by an attacker rewriting the secure-storage entry to
  // force migrations to re-run on already-encrypted rows.
  final current = await _readSchemaVersion(isar);
  if (current >= _kSchemaVersion) return;

  await isar.writeTxn(() async {
    if (current < 2) {
      // v1 → v2: encrypt every plaintext displayName. MUST be idempotent —
      // if the app dies mid-loop, re-running must not double-encrypt.
      final users = await isar.users.where().findAll();
      for (final u in users) {
        if (!u.displayName.startsWith(FieldEncryptor.cipherPrefix)) {
          // Plaintext row → encrypt with AAD bound to its server userId.
          u.displayName = await FieldEncryptor.instance.encrypt(
            u.displayName,
            collection: 'User',
            column: 'displayName',
            rowKey: u.userId,
          );
          await isar.users.put(u);
        }
        // Else: already enc:v1: — skip. Safe under crash-retry.
      }
    }
    // ...add v2 → v3 block here
  });
  await _writeSchemaVersion(isar, _kSchemaVersion);
}
```

**Rules of thumb:**

- Bump `_kSchemaVersion` for every breaking change.
- **Every migration MUST be idempotent.** Crash mid-loop → re-run on next
  launch → no corruption. Always check for the post-migration state before
  writing.
- Store the schema version **inside the encrypted DB**, not in secure
  storage. An attacker who can reset secure-storage entries can otherwise
  force migrations to re-run; double-encryption produces unrecoverable rows.
- Test migrations against a copy of a real production DB before release.
- Run the migration with `_legacyPlaintextAllowed: true` (see §7), then
  flip back to `false` after `_writeSchemaVersion` — production reads
  must fail closed on unprefixed values.

---

## 15. Testing

Mobile tests use an **in-memory** Isar instance — no native library on disk:

```dart
late Isar isar;

setUpAll(() async {
  await Isar.initializeIsarCore(download: true);  // CI: cache this layer
});

setUp(() async {
  isar = await Isar.open(
    [CropListingSchema, SyncOperationSchema],
    directory: '',
    name: 'test_${DateTime.now().microsecondsSinceEpoch}',
    inMemory: true,
  );
});

tearDown(() async {
  await isar.close(deleteFromDisk: true);
});

test('buyer sees ALL crops; farmer sees own only', () async {
  await isar.writeTxn(() async {
    await isar.cropListings.putAll([
      CropListing()..farmerId = 'F1'..cropType = 'teff',
      CropListing()..farmerId = 'F2'..cropType = 'wheat',
    ]);
  });

  final asFarmerF1 = await isar.cropListings
      .filter().farmerIdEqualTo('F1').findAll();
  expect(asFarmerF1.length, 1);

  final asBuyer = await isar.cropListings.where().findAll();
  expect(asBuyer.length, 2);
});

test('SyncOperation respects max retries', () async {
  final op = SyncOperation()
    ..entityType = 'CropListing'
    ..retryCount = 3;
  expect(op.canRetry, isFalse);
});
```

### Required security tests (all must exist before encryption ships)

```dart
final key = List<int>.generate(32, (_) => 0x42);
const aadArgs = (collection: 'User', column: 'displayName', rowKey: 'sub-A');

setUp(() async {
  FieldEncryptor.instance.reset();
  FieldEncryptor.instance.initialize(key, legacyPlaintextAllowed: false);
});

test('encrypt produces a fresh nonce on every call (no nonce reuse)', () async {
  final a = await FieldEncryptor.instance.encrypt('Abebe',
      collection: 'User', column: 'displayName', rowKey: 'sub-A');
  final b = await FieldEncryptor.instance.encrypt('Abebe',
      collection: 'User', column: 'displayName', rowKey: 'sub-A');
  expect(a, isNot(b));  // same plaintext, same AAD, must differ
  expect(a, startsWith('enc:v1:'));
});

test('cross-row swap is rejected (AAD binding)', () async {
  final blobA = await FieldEncryptor.instance.encrypt('Abebe',
      collection: 'User', column: 'displayName', rowKey: 'sub-A');
  // Attacker takes blobA and tries to use it as sub-B's displayName.
  expect(
    () => FieldEncryptor.instance.decrypt(blobA,
        collection: 'User', column: 'displayName', rowKey: 'sub-B'),
    throwsA(isA<FieldDecryptionException>()),
  );
});

test('cross-column swap is rejected (AAD binding)', () async {
  final blob = await FieldEncryptor.instance.encrypt('+251911234567',
      collection: 'User', column: 'phoneNumber', rowKey: 'sub-A');
  expect(
    () => FieldEncryptor.instance.decrypt(blob,
        collection: 'User', column: 'displayName', rowKey: 'sub-A'),
    throwsA(isA<FieldDecryptionException>()),
  );
});

test('tampered MAC byte is rejected', () async {
  final blob = await FieldEncryptor.instance.encrypt('Abebe', ...aadArgs);
  final body = base64Decode(blob.substring('enc:v1:'.length));
  body[body.length - 1] ^= 0x01;          // flip last MAC bit
  final tampered = 'enc:v1:${base64Encode(body)}';
  expect(
    () => FieldEncryptor.instance.decrypt(tampered, ...aadArgs),
    throwsA(isA<FieldDecryptionException>()),
  );
});

test('missing enc:v1: prefix throws (downgrade attack)', () async {
  expect(
    () => FieldEncryptor.instance.decrypt('+251911234567', ...aadArgs),
    throwsA(isA<FieldDecryptionException>()),
  );
});

test('legacy mode allows unprefixed values (migration only)', () async {
  FieldEncryptor.instance.reset();
  FieldEncryptor.instance.initialize(key, legacyPlaintextAllowed: true);
  final out = await FieldEncryptor.instance
      .decrypt('legacy_plaintext', ...aadArgs);
  expect(out, 'legacy_plaintext');
});

test('SyncOperation lastErrorCode never contains bearer tokens', () async {
  final exec = SyncExecutor(...);
  final err = DioException(/* simulated 401 with Authorization header in message */);
  final code = exec.classifyError(err);   // expose for testing
  expect(code, 'http_401_unauthenticated');
  expect(code, isNot(contains('Bearer')));
  expect(code, isNot(contains('eyJ')));   // common JWT prefix
});

test('background flush refuses when userId != active session', () async {
  await isar.writeTxn(() async {
    await isar.syncOperations.put(SyncOperation()
      ..userId = 'sub-A'
      ..entityType = 'CropListing'
      ..idempotencyKey = 'sub-A:CropListing:1:create:1234'
      ..payload = '{}'
      ..operation = SyncOp.create
      ..createdAt = DateTime.now().toUtc());
  });
  final auth = FakeAuth(currentUserId: 'sub-B');     // different user logged in
  final exec = SyncExecutor(isar: isar, auth: auth, ...);
  await exec.flush();
  final unsent = await isar.syncOperations
      .filter().statusEqualTo(SyncStatus.pending).findAll();
  expect(unsent.length, 1);  // still pending — not POSTed under sub-B
});

test('migration is idempotent under crash-retry', () async {
  await isar.writeTxn(() async {
    await isar.users.put(User()..userId='u1'..displayName='Abebe');
  });
  await _migrateToV2(isar);   // first run — encrypts
  final firstPass = (await isar.users.filter().userIdEqualTo('u1').findFirst())!;
  expect(firstPass.displayName, startsWith('enc:v1:'));
  await _migrateToV2(isar);   // second run — must be a no-op
  final secondPass = (await isar.users.filter().userIdEqualTo('u1').findFirst())!;
  expect(secondPass.displayName, firstPass.displayName);  // unchanged
});
```

**Mock the encryption layer in unit tests** with a passthrough `FieldEncryptor` so assertions can compare cleartext. Cover the **role-visibility matrix explicitly** — every PR touching repositories must include "Farmer/Trucker/Buyer" cases.

### CI rules

- Test-time `Isar.initializeIsarCore(download: true)` is permitted in dev but
  **blocked in CI**. CI runs with `download: false` against a pinned binary
  vendored in `mobile/test/fixtures/` so a poisoned mirror can't substitute
  the native library.
- Lint rule (`scripts/check_pii_writes.sh`): grep for `late String phoneNumber|faydaId|fullName`
  followed by `.put(` without a preceding `await ...encrypt(`. CI fails the
  PR. Coarse, false-positive-prone — but for a finite list of PII fields,
  worth the friction.
- Aikido security scan (already in CI per commit `a073336`) — run on every
  PR that touches `mobile/lib/core/database/` or `mobile/lib/core/sync/`.

---

## 15b. Cloud Architecture Alignment

The mobile offline-first contract is only useful if the server upholds the
other half. This section pins down the contract so neither side drifts.

### Server-side dedup window (existing)

| Flow | Component | Table key | TTL |
|---|---|---|---|
| Generic sync ops | `ISyncMetadataRepository` | `idempotencyKey` (PK) | ≥ `maxRetries × backoffMax` of the mobile client + safety margin (suggest 24h) |
| Escrow operations | `EscrowIdempotencyRepository` | `idempotencyKey` (PK) | ≥ 7 days (covers a week of offline accumulation) |

**Contract:** the mobile client sends `Idempotency-Key: {userId}:{entityType}:{entityId}:{action}:{ts}`
on every retry of the same `SyncOperation` row. The Lambda handler checks
the table; on hit, replays the stored response without re-executing the
mutation. On miss, executes the mutation and stores the response keyed by the
idempotency key.

**Failure mode the contract prevents:** Telebirr escrow double-charge under
network flap. If this contract breaks (either side), audit the Lambda's
DynamoDB writes for `EscrowOperations` partition; non-zero duplicates with
identical user+timestamp pairs indicate a regression.

### Server-side authorization is the actual security boundary

§8 says it; this section reinforces it for the cloud half:

Every Lambda handler MUST independently re-scope every query to the caller's
role. The mobile-side `farmerIdEqualTo(currentUserId)` filter is correctness
(don't show a farmer other farmers' rows in their UI) — **not security.** A
rooted phone running a custom client bypasses the Isar filter trivially.

**Required handler logic:**

```csharp
// server/src/Functions/CropFunction.cs (sketch)
public async Task<HttpResponse> ListCrops(HttpRequest req) {
    var caller = await _auth.GetCallerAsync(req);
    var query = caller.Role switch {
        UserRole.Farmer  => _crops.QueryByFarmer(caller.UserId),    // own only
        UserRole.Trucker => Enumerable.Empty<CropListing>(),         // none
        UserRole.Buyer   => _crops.QueryAll(),                       // all
        _                => throw new UnauthorizedAccessException(),
    };
    return Json(query);
}
```

Server NEVER trusts client-supplied `userId` / role / `farmerId` — those come
from the JWT, validated by Cognito JWKS in front of the Lambda.

**Audit cadence:** every PR that touches a Lambda handler reads role from
JWT, not from request body. CI rule: grep `caller.UserId` vs request-derived
user IDs; mismatches flag for security review.

### Observability — what to alarm on

Mobile-side sync failures are silent unless observed. Minimum CloudWatch
alarms:

| Metric | Threshold | Why |
|---|---|---|
| `SyncFunction` 5xx rate | > 1% over 5 min | Server bug, downstream DynamoDB throttle |
| `SyncFunction` 409 rate | > 0.1% over 1 hr | Idempotency dedup hits — high rate means client retry storm |
| `EscrowFunction` 4xx with stale `idempotencyKey` | any | Replay attack or aggressive client retry |
| Lambda cold start p95 | > 500ms | AOT compilation regression |
| DynamoDB throttle on `SyncMetadata` or `EscrowIdempotency` | any | Capacity miss; queue dedup at risk |
| Mobile Sentry `FieldDecryptionException` rate | > 0/day | Tampering, key swap, or migration bug |
| Mobile Sentry `network_timeout` cluster | > p95 baseline | Cert pin mismatch or regional outage |

Add a synthetic mobile-flavor test: an EventBridge cron that invokes a
"hello sync" Lambda from `eu-north-1` and `me-south-1` every 5 minutes and
alarms on failure. Cheap; catches API Gateway / Lambda layer breakage before
users do.

### DynamoDB key design alignment

Mobile-side `userId` in `SyncOperation` is the Cognito sub. Server-side, the
same value is the PK or PK-prefix on every table that holds per-user data:

| Table | PK | SK | Why aligned |
|---|---|---|---|
| `Users` | `userId` | — | Direct lookup from JWT sub |
| `CropListings` | `farmerId` | `listingId` | Farmer-scoped queries are PK lookups |
| `Trips` | `truckerId` | `tripId` | Trucker-scoped queries are PK lookups |
| `Orders` | `buyerId` | `orderId` | Buyer-scoped queries are PK lookups |
| `BuyerIntents` | `buyerId` | `intentId` | Buyer-scoped queries are PK lookups |
| `SyncMetadata` | `idempotencyKey` | — | Dedup lookup |
| `EscrowIdempotency` | `idempotencyKey` | — | Escrow dedup lookup |

Per-role queries (buyer browsing all crops) use GSIs, never scan. Scan
costs on a $3/month dev budget bite hard.

### Backup & disaster recovery

Mobile-side, Isar **is the backup of the last 30 days of user activity** if
the device is online and PITR keeps the server tables intact. So:

- DynamoDB PITR ON for all production tables (`/backup` slash command verifies)
- 7-day point-in-time restore window minimum
- Quarterly DR drill: restore a single user's tables to a sandbox account and
  validate the mobile app, after `clearAllData()` then a clean login, can
  re-pull state via the delta-sync pull (§10) without divergence

Mobile-side, never rely on the user's device as the only copy of a buy
intent or order. The marketplace flow in `CLAUDE.md` § Core Marketplace
Flow assumes the server is authoritative; the client is a cache.

### Cost guardrails

`CLAUDE.md` § Infrastructure says: "Never: NAT Gateways, RDS, auto-scaling
without cost caps." This applies to anything the sync flow adds:

- WorkManager 15-min cadence × N users × per-flush DynamoDB write = a cost
  curve that grows with installs. Cap reserved concurrency on `SyncFunction`
  so a traffic spike doesn't blow the budget.
- Encrypted image blobs (§7) up to ~250KB each × 3 per user (front, back,
  license) × 100k users = ~75GB in DynamoDB-Items vs. S3-with-presigned-URL.
  Push large blobs to S3 with object encryption (SSE-S3 or KMS), keep only
  the S3 key in DynamoDB. Re-encrypt client-side **before** upload; the S3
  bucket sees `enc:v1:...` blobs only.

---

## 16. Performance Best Practices

**Batch writes inside one transaction:**

```dart
// ❌ 100 transactions
for (final c in crops) {
  await isar.writeTxn(() => isar.cropListings.put(c));
}

// ✅ 1 transaction
await isar.writeTxn(() => isar.cropListings.putAll(crops));
```

**Index only what you query:**

```dart
@Index() late String farmerId;     // role filter — yes
@Index() late String cropType;     // category filter — yes
late String description;           // free text, never queried — no index
```

**Composite indexes for combined filters:**

```dart
@Index(composite: [CompositeIndex('cropType')])
late String farmerId;
// query: where().farmerIdCropTypeEqualTo('F1', 'teff')
```

**Run expensive scans in an isolate** (Isar Inspector reports give exact timings):

```dart
final results = await isar.copyToIsolate(/* ... */);
```

**Always cancel stream subscriptions** in BLoC `close()` / widget `dispose()` — Isar listeners are cheap but they're not free, and leaked ones survive widget rebuilds.

**Cold-start budget:** `IsarService.initialize()` should stay under ~150 ms on a 2 GB RAM device. If it grows, profile with `dart devtools` and split init across futures (`Future.wait`) where safe.

---

## 17. Platform Guard — Defense in Depth

Three layers stop Isar code from running on web:

**Layer 1 — Conditional imports (compile-time).** Web targets resolve the stub file:

```dart
import 'package:isar/isar.dart'
    if (dart.library.html) 'package:wereda_connect/core/database/isar_stub.dart';

import 'package:wereda_connect/core/database/isar_service.dart'
    if (dart.library.html) 'package:wereda_connect/core/database/isar_service_web.dart';
```

**Layer 2 — Runtime guard.** Inside `IsarService.initialize()`:

```dart
abstract final class PlatformGuard {
  static void assertNotWeb() {
    if (kIsWeb) {
      throw UnsupportedError(
        'Isar is not supported on web. WeredaConnect web is API-only — '
        'use the mobile app for offline-first features.',
      );
    }
  }
  static bool get supportsLocalDb => !kIsWeb;
}
```

**Layer 3 — Build config.** `isar_flutter_libs` has no web implementation, so the web build naturally excludes the native binary. Use Flutter build flavors if you ever need to harden this further.

### Capability matrix

| Platform | Isar | Encryption | Sync queue | Offline UX |
|---|---|---|---|---|
| Android | ✅ | ✅ AES-GCM | ✅ | ✅ Full |
| iOS (planned) | ✅ | ✅ AES-GCM | ✅ | ✅ Full |
| **Web** | ❌ stubs | ❌ N/A | ❌ direct API | ❌ **Online-only by design** |

---

## Appendix — Common Commands

```bash
# After changing @collection schemas
cd mobile && dart run build_runner build --delete-conflicting-outputs

# Watch mode during dev
cd mobile && dart run build_runner watch --delete-conflicting-outputs

# Mobile tests
cd mobile && flutter test

# Web build smoke (must succeed if you touched platform-paired files)
cd mobile && flutter build web --no-tree-shake-icons 2>&1 | tail -20

# Android release
cd mobile && flutter build apk --release
```

---

## Appendix — Self-check Before "Done"

From `CLAUDE.md` § Completeness Rules — automatic FAIL on violation:

### Architecture / completeness

- [ ] Mobile + web counterparts both updated (and web build passes)
- [ ] All three roles verified (Farmer, Trucker, Buyer) for any repository/BLoC change
- [ ] Schema change → `build_runner` ran → `.g.dart` files committed
- [ ] Soft delete (`isDeleted = true`) — never `delete()` hard
- [ ] Tests updated, including role-visibility cases
- [ ] No `dart:io` or `package:isar` imports in shared/web code paths
- [ ] Marketplace flow still works end-to-end (Farmer create → Buyer browse → Trucker accept)

### Security

- [ ] PII field added → `FieldEncryptor.encrypt(value, collection, column, rowKey)` before `put`
- [ ] No indexed encrypted field — use HMAC search column + AES blob pattern (§7)
- [ ] `enc:v1:` prefix on every ciphertext write; `decrypt` fails closed on missing prefix
- [ ] Decrypt errors throw `FieldDecryptionException` and report to Sentry
- [ ] Migration is idempotent — re-run on already-migrated rows is a no-op
- [ ] `SyncOperation.lastErrorCode` is from `classifyError()`, never `e.toString()`
- [ ] `SyncOperation.payload` is built from the post-encryption row
- [ ] `SyncOperation.userId` set at enqueue; flush rejects userId mismatch
- [ ] Idempotency key generated once at enqueue, sent unchanged on retry
- [ ] Fayda images stored as encrypted blobs, not filesystem paths
- [ ] Biometric gate runs before `FieldEncryptor.initialize`
- [ ] No `Bearer`/`eyJ` substrings in any persisted error or telemetry payload
- [ ] Cert pinning active on mobile network calls (`api_client.dart`)

### Cloud / server contract

- [ ] Server-side authorization re-scopes every query to the JWT-derived caller
- [ ] Server stores `idempotencyKey` with TTL ≥ client `maxRetries × backoffMax`
- [ ] CloudWatch alarms wired for sync 5xx, escrow dedup hits, AOT cold-start p95
- [ ] DynamoDB PITR ON for all production tables
- [ ] Large blobs (>50 KB) go to S3 with object encryption; DynamoDB stores S3 keys only

### Release-gate

- [ ] `inspector: true` removed (or guarded by `kDebugMode`)
- [ ] `AndroidManifest.xml`: `allowBackup="false"`, `debuggable="false"` in release
- [ ] `network_security_config.xml` allows only system CAs, no cleartext
- [ ] `flutter build apk --release` succeeds
- [ ] `flutter build web --no-tree-shake-icons` succeeds
- [ ] Aikido security scan passes on `mobile/lib/core/database/**` and `mobile/lib/core/sync/**`

---

## Appendix — End-to-end offline → sync correctness model

The contract the mobile app upholds, in one diagram:

```
┌─────────────────────────────────────────────────────────────────────┐
│  User action (e.g. Farmer creates a crop listing)                   │
├─────────────────────────────────────────────────────────────────────┤
│  1. Repository: encrypt PII fields (AAD-bound, enc:v1:)             │
│  2. Repository: write encrypted row to Isar in writeTxn             │
│  3. Repository: enqueue SyncOperation with                          │
│       - userId (current Cognito sub)                                │
│       - idempotencyKey (locked once, reused on every retry)         │
│       - payload (built from POST-encryption row — no plaintext)     │
│  4. If online → fire-and-forget flush()                             │
└─────────────────────────────────────────────────────────────────────┘
                              │
                              ▼
┌─────────────────────────────────────────────────────────────────────┐
│  SyncExecutor.flush()                                                │
├─────────────────────────────────────────────────────────────────────┤
│  for each pending op where retryCount < maxRetries:                 │
│    a. Skip if op.userId != active session userId                    │
│    b. Mark processing                                                │
│    c. POST with header Idempotency-Key: op.idempotencyKey           │
│       - Cert-pinned, TLS 1.2+, no cleartext                         │
│    d. Server checks dedup table; replays response on hit            │
│    e. On 2xx → mark completed, update record.isSynced               │
│    f. On 5xx/timeout → retryCount++,                                │
│         status = canRetry ? pending : failed,                       │
│         lastErrorCode = classifyError(e)  (opaque code only)        │
└─────────────────────────────────────────────────────────────────────┘
                              │
                              ▼
┌─────────────────────────────────────────────────────────────────────┐
│  Background tier — WorkManager every 15 min when:                   │
│    - Device connected, battery not low                              │
│    - userId matches active session                                  │
│    - Access token still valid                                       │
│  Otherwise: graceful no-op, retries next cycle                      │
└─────────────────────────────────────────────────────────────────────┘
                              │
                              ▼
┌─────────────────────────────────────────────────────────────────────┐
│  Pull side — delta sync on resume / connectivity regain             │
├─────────────────────────────────────────────────────────────────────┤
│  GET /sync/changes?since={serverWatermark}                          │
│  Server returns rows + new serverWatermark                          │
│  Conflict → ConflictResolver (server wins)                          │
│  Persist new serverWatermark to FlutterSecureStorage                │
└─────────────────────────────────────────────────────────────────────┘
```

**Web does none of this.** The web admin client calls the API directly,
shows an "offline" banner when the connection drops, and refuses writes.
This is **by design** — `CLAUDE.md` and the platform-pair contract make
"no offline on web" load-bearing. The stubs satisfy the type-checker; the
runtime contract is that they never execute.

---

*Document version: 2.0 · Last updated: 2026-05-13*
*Targets: Flutter 3.38.x · Isar 3.1.x · .NET 10 AOT server · DynamoDB · Cognito*
*Threat model: lost device, shared device, rooted device, MITM, backup-restore, state-actor cold boot*
