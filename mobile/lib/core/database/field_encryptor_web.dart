/// Web stub for FieldEncryptor — no-op implementation.
///
/// On web, there is no Isar database and no PII stored on disk,
/// so field-level encryption is not needed. This stub satisfies
/// the import so screens compile for web.
class FieldEncryptor {
  static FieldEncryptor? _instance;

  FieldEncryptor._();

  static FieldEncryptor get instance {
    _instance ??= FieldEncryptor._();
    return _instance!;
  }

  bool get isInitialized => false;

  /// Signature parity with mobile. `legacyPlaintextAllowed` is accepted
  /// but ignored — web has no Isar / no on-disk PII, so the HS-3 migration
  /// escape hatch is irrelevant here.
  void initialize(
    List<int> keyBytes, {
    bool legacyPlaintextAllowed = false,
  }) {
    // No-op on web — no Isar, no PII on disk
  }

  /// Returns plaintext unchanged (no encryption on web).
  ///
  /// Signature parity with the mobile implementation. The AAD parameters
  /// are accepted but ignored — web has no Isar, no on-disk PII, so AAD
  /// binding has nothing to protect against.
  Future<String> encrypt(
    String plaintext, {
    required String collection,
    required String column,
    required String rowKey,
  }) async => plaintext;

  /// Returns ciphertext unchanged (no decryption on web).
  ///
  /// Signature parity with the mobile implementation. See [encrypt] note.
  Future<String> decrypt(
    String ciphertext, {
    required String collection,
    required String column,
    required String rowKey,
  }) async => ciphertext;

  void reset() {}

  static void resetInstance() {
    _instance = null;
  }
}
