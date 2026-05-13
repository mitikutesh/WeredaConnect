/// Contract for field-level encryption. Three implementations:
///
/// - `FieldEncryptor` (mobile, `field_encryptor.dart`) — real AES-GCM
///   via `package:cryptography`, with AAD binding, `enc:v1:` prefix,
///   and fail-closed decrypt (HS-1: S1-01, S1-02).
/// - `FieldEncryptor` (web stub, `field_encryptor_web.dart`) — no-op
///   passthrough; web has no Isar and no PII at rest.
/// - `FakeFieldEncryptor` (`mobile/test/fakes/fake_field_encryptor.dart`)
///   — deterministic test double. AAD trio is embedded in the
///   "ciphertext" so tests can assert AAD propagation cleanly without
///   pulling in real cryptography.
///
/// Repository constructors take this abstract type so production
/// wiring injects `FieldEncryptor.instance` and tests inject the fake.
abstract class BaseFieldEncryptor {
  /// Whether [initialize] has been called and the encryptor is ready.
  bool get isInitialized;

  /// Configure the encryptor with a 32-byte AES-256 key. Must be called
  /// before [encrypt] or [decrypt]. The optional `legacyPlaintextAllowed`
  /// flag is the HS-3 v1→v2 migration escape hatch — production reads
  /// MUST run with `false` (the default). See `field_encryptor.dart` for
  /// the production behavior and `mobile/test/fixtures/lint/` for the
  /// CI enforcement that S1-04 added.
  void initialize(
    List<int> keyBytes, {
    bool legacyPlaintextAllowed = false,
  });

  /// Encrypt `plaintext` with AAD bound to the `(collection, column, rowKey)`
  /// trio. Output format and failure semantics are implementation-defined
  /// — see each concrete subtype.
  Future<String> encrypt(
    String plaintext, {
    required String collection,
    required String column,
    required String rowKey,
  });

  /// Decrypt `value` produced by an [encrypt] call with the same AAD trio.
  /// Output and failure semantics are implementation-defined.
  Future<String> decrypt(
    String value, {
    required String collection,
    required String column,
    required String rowKey,
  });

  /// Clear in-memory state. After [reset], [initialize] must be called
  /// again before encrypt/decrypt will work.
  void reset();
}
