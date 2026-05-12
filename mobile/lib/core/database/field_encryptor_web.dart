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

  void initialize(List<int> keyBytes) {
    // No-op on web — no Isar, no PII on disk
  }

  /// Returns plaintext unchanged (no encryption on web).
  Future<String> encrypt(String plaintext) async => plaintext;

  /// Returns ciphertext unchanged (no decryption on web).
  Future<String> decrypt(String ciphertext) async => ciphertext;

  void reset() {}

  static void resetInstance() {
    _instance = null;
  }
}
