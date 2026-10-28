import 'dart:convert';
import 'dart:math';

import 'package:crypto/crypto.dart' as crypto;
import 'package:flutter/foundation.dart';

import 'package:flutter_secure_storage/flutter_secure_storage.dart';
import 'package:isar/isar.dart';
import 'package:path_provider/path_provider.dart';

export 'package:isar/isar.dart';

import 'package:wereda_connect/core/database/crypto_failure.dart';
import 'package:wereda_connect/core/database/field_encryptor.dart';
import 'package:wereda_connect/core/database/schemas/app_notification.dart';
import 'package:wereda_connect/core/database/schemas/buyer_intent.dart';
import 'package:wereda_connect/core/database/schemas/conversation_read.dart';
import 'package:wereda_connect/core/database/schemas/crop_listing.dart';
import 'package:wereda_connect/core/database/schemas/delivery.dart';
import 'package:wereda_connect/core/database/schemas/location_ping.dart';
import 'package:wereda_connect/core/database/schemas/message.dart';
import 'package:wereda_connect/core/database/schemas/order.dart';
import 'package:wereda_connect/core/database/schemas/payment.dart';
import 'package:wereda_connect/core/database/schemas/rating.dart';
import 'package:wereda_connect/core/database/schemas/sync_operation.dart';
import 'package:wereda_connect/core/database/schemas/trip.dart';
import 'package:wereda_connect/core/database/schemas/user.dart';
import 'package:wereda_connect/core/database/schemas/payment_intent.dart';

/// Closed set of key-integrity failure modes. Each mode carries a fixed
/// human-readable message; nothing dynamic is interpolated.
enum KeyIntegrityReason {
  hmacMismatch('HMAC tag mismatch on key read');

  const KeyIntegrityReason(this.message);
  final String message;
}

/// Thrown when the AES encryption key fails its integrity check at
/// read-back. The bytes returned by [FlutterSecureStorage] don't match
/// the HMAC tag stored alongside them — i.e. they were tampered with,
/// corrupted, or restored from a different device's backup.
///
/// [IsarService.initialize] catches this exception, calls
/// [IsarService._wipeForIntegrityFailure] (which deletes both the key
/// and tag entries, resets [FieldEncryptor], and nulls the in-memory
/// Isar handle), then rethrows. The recovery path is: a subsequent
/// `initialize()` call lands in the fresh-key branch and proceeds
/// cleanly. The caller is responsible for routing the rethrow to the
/// auth flow (e.g. a "Device security was reset" screen + re-login).
///
/// Implements [CryptoFailure] so callers can catch the shared supertype
/// alongside [FieldDecryptionException] when the handling is identical.
class KeyIntegrityFailure implements CryptoFailure {
  final KeyIntegrityReason kind;
  const KeyIntegrityFailure(this.kind);
  @override
  String get reason => kind.message;
  @override
  String toString() => 'KeyIntegrityFailure: ${kind.message}';
}

/// Service for managing the encrypted Isar database.
///
/// This service handles:
/// - Database initialization with encryption
/// - Encryption key generation and storage in Android Keystore
/// - Opening/closing database connections
/// - Schema management
///
/// Ethiopian context:
/// - Encryption protects user data on shared devices
/// - Keys stored in Android Keystore (hardware-backed on many devices)
/// - Database remains accessible offline once unlocked
class IsarService {
  static IsarService? _instance;
  Isar? _isar;

  // Secure storage for encryption key (backed by Android Keystore)
  static const FlutterSecureStorage _secureStorage = FlutterSecureStorage(
    aOptions: AndroidOptions(
      encryptedSharedPreferences: true,
    ),
  );

  /// Secure-storage key for the AES encryption key bytes.
  static const String _keyName = 'isar_encryption_key';

  /// Secure-storage key for the HMAC integrity tag.
  /// Stored as base64(HMAC-SHA256(key, _hmacLabel)).
  static const String _tagName = 'isar_encryption_key_tag';

  /// LOAD-BEARING. Changing this constant invalidates every existing
  /// user's tag and triggers [KeyIntegrityFailure] on their next launch.
  /// Rotate only as part of a planned key-rotation migration (HS-7 / S7-01).
  static const String _hmacLabel = 'wereda-isar-key-v1';

  // Private constructor for singleton pattern
  IsarService._();

  /// Get the singleton instance of IsarService
  static IsarService get instance {
    _instance ??= IsarService._();
    return _instance!;
  }

  /// Get the active Isar database instance.
  /// Throws [StateError] if database is not initialized.
  Isar get db {
    if (_isar == null) {
      throw StateError(
        'IsarService not initialized. Call initialize() first.',
      );
    }
    return _isar!;
  }

  /// Check if the database is initialized
  bool get isInitialized => _isar != null;

  /// Get the FieldEncryptor instance for encrypting/decrypting PII fields.
  ///
  /// The encryptor is initialized during [initialize()] with the same
  /// AES-256 key stored in Android Keystore. Repositories should use this
  /// to encrypt PII fields (phoneNumber, name, faydaId) before writing
  /// to Isar and decrypt after reading.
  FieldEncryptor get fieldEncryptor => FieldEncryptor.instance;

  /// Initialize the Isar database with encryption.
  ///
  /// This method:
  /// 1. Generates or retrieves the encryption key from secure storage
  /// 2. Opens the Isar database with encryption enabled
  /// 3. Registers all schemas (User, CropListing, Trip, etc.)
  ///
  /// The encryption key is stored in Android Keystore and persists
  /// across app restarts. If the key is lost (e.g., device reset),
  /// the database must be re-initialized with a new key.
  ///
  /// Returns the initialized Isar instance.
  Future<Isar> initialize() async {
    if (_isar != null) {
      return _isar!;
    }

    // Get application directory for database storage
    final dir = await getApplicationDocumentsDirectory();

    // Get or generate encryption key and initialize field-level encryptor.
    // Note: Isar 3.1 does not support built-in encryption.
    // PII fields (phone number, name, faydaId) are encrypted/decrypted
    // at the repository layer using FieldEncryptor with AES-256-GCM.
    final List<int> keyBytes;
    try {
      keyBytes = await _getEncryptionKey();
    } on KeyIntegrityFailure catch (e) {
      // Tampering, corruption, or cross-device restore. The DB is
      // already unrecoverable with the bad key. Wipe persisted state
      // and let the auth flow restart from scratch.
      debugPrint('[IsarService] Key integrity failure: $e — wiping.');
      await _wipeForIntegrityFailure();
      rethrow;
    }
    FieldEncryptor.instance.initialize(keyBytes);

    // Open Isar database
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
        RatingSchema,
        MessageSchema,
        ConversationReadSchema,
        AppNotificationSchema,
        LocationPingSchema,
      ],
      directory: dir.path,
      name: 'wereda_connect',
    );

    return _isar!;
  }

  /// Get or generate the encryption key for the Isar database.
  ///
  /// Key + tag storage flow:
  /// 1. Try to read both `_keyName` and `_tagName` from secure storage.
  /// 2. If both present and valid, recompute the HMAC tag from the key
  ///    bytes and constant-time-compare to the persisted tag.
  /// 3. On match → return the key.
  /// 4. On mismatch → throw [KeyIntegrityFailure]. Caller wipes + re-logins.
  /// 5. If only the key is present (legacy pre-S1-03 install) → write a
  ///    tag for it and continue. Policy A: trust the existing key.
  /// 6. If neither is present → generate a fresh key + tag and persist both.
  ///
  /// Throws [KeyIntegrityFailure] if the persisted key fails its tag check.
  Future<List<int>> _getEncryptionKey() async {
    String? existingKeyB64;
    String? existingTagB64;
    try {
      existingKeyB64 = await _secureStorage.read(key: _keyName);
      existingTagB64 = await _secureStorage.read(key: _tagName);
    } on Exception catch (e) {
      // Reading failed — common on first install or restricted storage.
      // Fall through to regenerate.
      debugPrint('[IsarService] Failed to read secure storage: $e');
    }

    if (existingKeyB64 != null && existingKeyB64.isNotEmpty) {
      final List<int> keyBytes;
      try {
        keyBytes = base64Decode(existingKeyB64);
      } on FormatException {
        // Corrupt key entry — unambiguous corruption, not tampering.
        // Wipe both entries and regenerate; do not raise KeyIntegrityFailure.
        debugPrint(
          '[IsarService] Corrupt key entry (base64) — wiping and regenerating.',
        );
        await _secureStorage.delete(key: _keyName);
        await _secureStorage.delete(key: _tagName);
        return _generateAndPersistKey();
      }

      if (keyBytes.length != 32) {
        // Invalid key length — treat as corruption.
        debugPrint(
          '[IsarService] Invalid key length ${keyBytes.length} '
          '— wiping and regenerating.',
        );
        await _secureStorage.delete(key: _keyName);
        await _secureStorage.delete(key: _tagName);
        return _generateAndPersistKey();
      }

      if (existingTagB64 == null) {
        // Legacy pre-S1-03 install: key present, tag absent. Policy A:
        // trust the existing key and seal it with a fresh tag (one-time,
        // per device).
        // TODO(S6-01): emit Sentry counter for legacy-tag-write events.
        debugPrint(
          '[IsarService] No integrity tag — legacy pre-S1-03 install. '
          'Sealing existing key with a fresh tag (one-time, per device).',
        );
        await _writeTagFor(keyBytes);
        return keyBytes;
      }

      // Both present — verify.
      final List<int> persistedTag;
      try {
        persistedTag = base64Decode(existingTagB64);
      } on FormatException {
        // Corrupt tag entry — symmetric with corrupt-key handling above.
        // A flaky write that produces a truncated tag shouldn't nuke the
        // user's DB; treat as corruption and regenerate.
        debugPrint(
          '[IsarService] Corrupt tag entry (base64) — wiping and regenerating.',
        );
        await _secureStorage.delete(key: _keyName);
        await _secureStorage.delete(key: _tagName);
        return _generateAndPersistKey();
      }
      final expectedTag = _computeTagStatic(keyBytes);
      if (!_constantTimeEqStatic(persistedTag, expectedTag)) {
        throw const KeyIntegrityFailure(KeyIntegrityReason.hmacMismatch);
      }
      return keyBytes;
    }

    // No key entry — first install, or after a prior wipe.
    // Any orphan tag entry is overwritten by _writeTagFor inside
    // _generateAndPersistKey below.
    return _generateAndPersistKey();
  }

  Future<List<int>> _generateAndPersistKey() async {
    final random = Random.secure();
    final key = List<int>.generate(32, (_) => random.nextInt(256));

    // Persist key first. If this fails, the key works for the current
    // session but won't survive a restart — common on Linux CI without
    // a keyring daemon.
    try {
      await _secureStorage.write(key: _keyName, value: base64Encode(key));
    } on Exception catch (e) {
      debugPrint('[IsarService] Failed to persist key: $e');
      return key;
    }

    // Persist tag separately. If the tag write fails AFTER the key
    // write succeeded, roll back the key — otherwise the next launch
    // sees key-without-tag and incorrectly trusts the orphan as a
    // legacy install, minting a fresh tag over potentially-corrupted
    // bytes.
    try {
      await _writeTagFor(key);
    } on Exception catch (e) {
      debugPrint(
        '[IsarService] Key persisted but tag write failed: $e — '
        'rolling back key to avoid orphan-trusted-as-legacy.',
      );
      try {
        await _secureStorage.delete(key: _keyName);
      } on Exception catch (_) {
        // Best-effort rollback; next launch's legacy path handles it.
      }
    }
    return key;
  }

  Future<void> _writeTagFor(List<int> key) async {
    final tag = _computeTagStatic(key);
    await _secureStorage.write(key: _tagName, value: base64Encode(tag));
  }

  /// Test-only: exposes the key-management orchestration so unit tests
  /// can verify the legacy-upgrade / corrupt-recovery / tag-mismatch
  /// branches without opening Isar. Must be paired with a mocked
  /// `FlutterSecureStorage` method-channel handler.
  @visibleForTesting
  Future<List<int>> getEncryptionKeyForTesting() => _getEncryptionKey();

  /// Test-only: exposes the secure-storage keys so tests can assert
  /// what was written without duplicating the constants.
  @visibleForTesting
  static String get keyStorageNameForTesting => _keyName;
  @visibleForTesting
  static String get tagStorageNameForTesting => _tagName;

  /// Computes `HMAC-SHA256(key, _hmacLabel).bytes`.
  @visibleForTesting
  static List<int> computeTagForTesting(List<int> key, {String? label}) =>
      _computeTagStatic(key, label: label);

  static List<int> _computeTagStatic(List<int> key, {String? label}) {
    final hmac = crypto.Hmac(crypto.sha256, key);
    return hmac.convert(utf8.encode(label ?? _hmacLabel)).bytes;
  }

  /// Constant-time list-of-bytes equality. NEVER short-circuits on the
  /// first differing byte — a timing oracle on tag comparison would let
  /// an attacker guess one byte at a time.
  @visibleForTesting
  static bool constantTimeEqForTesting(List<int> a, List<int> b) =>
      _constantTimeEqStatic(a, b);

  static bool _constantTimeEqStatic(List<int> a, List<int> b) {
    if (a.length != b.length) return false;
    var diff = 0;
    for (var i = 0; i < a.length; i++) {
      diff |= a[i] ^ b[i];
    }
    return diff == 0;
  }

  /// Called when the AES key fails its integrity check. Deletes both
  /// secure-storage entries and asks [FieldEncryptor] to drop any
  /// in-memory state. The Isar file itself remains on disk but is now
  /// permanently unreadable; S5-04 will delete the file too once that
  /// story lands.
  Future<void> _wipeForIntegrityFailure() async {
    try {
      await _secureStorage.delete(key: _keyName);
      await _secureStorage.delete(key: _tagName);
    } on Exception catch (e) {
      debugPrint('[IsarService] secure-storage wipe failed: $e');
    }
    FieldEncryptor.instance.reset();
    _isar = null;
  }

  /// Close the database connection.
  ///
  /// Should be called when the app is being disposed or during logout.
  /// The encryption key remains in secure storage and can be reused
  /// on next initialization.
  Future<void> close() async {
    if (_isar != null) {
      await _isar!.close();
      _isar = null;
    }
  }

  /// Clear all data from the database and remove the encryption key.
  ///
  /// WARNING: This is a destructive operation that:
  /// 1. Closes the database
  /// 2. Deletes the encryption key from secure storage
  /// 3. The existing database file becomes unreadable
  ///
  /// This should only be called during:
  /// - User logout (if clearing local data)
  /// - Account deletion
  /// - Factory reset
  ///
  /// After calling this method, initialize() will generate a new key
  /// and create a fresh database.
  Future<void> clearAllData() async {
    // Close database connection
    await close();

    // Delete both the encryption key and its integrity tag (S1-03).
    await _secureStorage.delete(key: _keyName);
    await _secureStorage.delete(key: _tagName);

    // Drop in-memory key material.
    FieldEncryptor.instance.reset();

    // Note: The old database file still exists but is unreadable without the
    // key. S5-04 will physically delete the file too. Isar will create a new
    // database on next initialize().
  }

  /// Rotate the encryption key (advanced operation).
  ///
  /// This is a complex operation that requires:
  /// 1. Reading all data from the current database
  /// 2. Closing the current database
  /// 3. Generating a new encryption key
  /// 4. Creating a new database with the new key
  /// 5. Writing all data to the new database
  ///
  /// This should only be done as part of a scheduled maintenance operation
  /// or in response to a security incident.
  ///
  /// For implementation details, see:
  /// specs/decisions/encryption-strategy.md
  Future<void> rotateEncryptionKey() async {
    throw UnimplementedError(
      'Key rotation not yet implemented. See encryption-strategy.md',
    );
  }
}
