import 'package:wereda_connect/core/database/base_field_encryptor.dart';

/// Web stub for FieldEncryptor — no-op implementation.
///
/// On web, there is no Isar database and no PII stored on disk,
/// so field-level encryption is not needed. This stub satisfies
/// the import so screens compile for web, and extends
/// [BaseFieldEncryptor] so repositories typed against the abstract
/// surface work on both platforms.
class FieldEncryptor extends BaseFieldEncryptor {
  static FieldEncryptor? _instance;

  bool _initialized = false;

  FieldEncryptor._();

  static FieldEncryptor get instance {
    _instance ??= FieldEncryptor._();
    return _instance!;
  }

  /// Tracks whether [initialize] has been called, for parity with mobile.
  /// Even though encrypt/decrypt are no-op passthroughs on web, callers
  /// that gate on `isInitialized` (e.g. readiness checks during app
  /// bootstrap) should see the same flag transitions on both platforms —
  /// otherwise web tests could mask a missing `initialize()` call that
  /// would be a `StateError` on mobile.
  @override
  bool get isInitialized => _initialized;

  /// Signature parity with mobile. The key length is validated (matches
  /// mobile's 32-byte AES-256 check) so a wrong-length key fails on both
  /// platforms identically, and `isInitialized` flips to `true` for
  /// readiness-check parity. `legacyPlaintextAllowed` is accepted but
  /// ignored — web has no Isar / no on-disk PII, so the HS-3 migration
  /// escape hatch is irrelevant here.
  @override
  void initialize(
    List<int> keyBytes, {
    bool legacyPlaintextAllowed = false,
  }) {
    if (keyBytes.length != 32) {
      throw ArgumentError('Encryption key must be 32 bytes (AES-256)');
    }
    _initialized = true;
    // encrypt/decrypt remain no-op on web — no Isar, no PII on disk.
  }

  /// Returns plaintext unchanged (no encryption on web).
  ///
  /// Signature parity with the mobile implementation. The AAD parameters
  /// are accepted but ignored — web has no Isar, no on-disk PII, so AAD
  /// binding has nothing to protect against.
  @override
  Future<String> encrypt(
    String plaintext, {
    required String collection,
    required String column,
    required String rowKey,
  }) async =>
      plaintext;

  /// Returns ciphertext unchanged (no decryption on web).
  ///
  /// Signature parity with the mobile implementation. See [encrypt] note.
  @override
  Future<String> decrypt(
    String ciphertext, {
    required String collection,
    required String column,
    required String rowKey,
  }) async =>
      ciphertext;

  @override
  void reset() {
    _initialized = false;
  }

  static void resetInstance() {
    _instance = null;
  }
}
