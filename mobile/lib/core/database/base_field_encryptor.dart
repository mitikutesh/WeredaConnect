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
  /// Validate the `(collection, column, rowKey)` AAD trio and return the
  /// canonical AAD string `'$collection:$column:$rowKey'`.
  ///
  /// Components must be non-empty and must not contain `':'` — both are
  /// rejected with [ArgumentError]. Without these checks, distinct trios
  /// could collide on the same AAD string (e.g. `('User:X', 'y', 'z')`
  /// and `('User', 'X:y', 'z')` both produce `'User:X:y:z'`) and silently
  /// defeat the cross-row / cross-column swap detection that GCM AAD
  /// binding provides.
  ///
  /// All concrete implementations MUST call this from both [encrypt] and
  /// [decrypt] so the substitution-attack defense holds uniformly across
  /// production, web stub, and test fakes.
  static String buildAad(String collection, String column, String rowKey) {
    for (final (name, value) in [
      ('collection', collection),
      ('column', column),
      ('rowKey', rowKey),
    ]) {
      if (value.isEmpty) {
        throw ArgumentError.value(
          value,
          name,
          'AAD component must not be empty',
        );
      }
      if (value.contains(':')) {
        throw ArgumentError.value(
          value,
          name,
          "AAD component must not contain ':'",
        );
      }
    }
    return '$collection:$column:$rowKey';
  }

  /// Whether [initialize] has been called and the encryptor is ready.
  bool get isInitialized;

  /// Configure the encryptor with a 32-byte AES-256 key. Must be called
  /// before [encrypt] or [decrypt]. The optional `legacyPlaintextAllowed`
  /// flag is the HS-3 v1→v2 migration escape hatch — production reads
  /// MUST run with `false` (the default). See `field_encryptor.dart` for
  /// the production behavior and `mobile/test/fixtures/lint/` for the
  /// CI enforcement that S1-04 added.
  void initialize(List<int> keyBytes, {bool legacyPlaintextAllowed = false});

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
