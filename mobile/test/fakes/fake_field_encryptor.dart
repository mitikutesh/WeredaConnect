import 'package:wereda_connect/core/database/base_field_encryptor.dart';

/// Deterministic test double for [BaseFieldEncryptor].
///
/// Designed to be drop-in for any repository constructor that takes
/// [BaseFieldEncryptor]. Encrypt outputs a readable string with the
/// AAD trio embedded; decrypt strips the wrapper. AAD-mismatch on
/// decrypt throws `ArgumentError` — mirrors the real production
/// failure shape so tests assert "AAD propagation happened" without
/// pulling in `package:cryptography`.
///
/// Behavior:
/// - `encrypt(p, c, col, rk)` → `'fake:c:col:rk:p'`
/// - `decrypt('fake:c:col:rk:p', c, col, rk)` → `'p'`
/// - `decrypt('fake:c:col:rk:p', X, col, rk)` where X ≠ c → throws
///
/// The shape lets tests assert with substring matches (e.g.
/// `expect(stored, contains('fake:User:displayName:sub-A:'))`) which
/// pins the AAD without coupling to the real `enc:v1:` byte format.
///
/// NOT thread-safe — fine for single-isolate test runs.
class FakeFieldEncryptor extends BaseFieldEncryptor {
  bool _initialized = false;

  /// The legacy flag is recorded but the fake doesn't change behavior
  /// based on it — tests verifying the flag's effect should use the
  /// real `FieldEncryptor`. Exposed via [legacyFlag] for assertions.
  bool _legacy = false;

  /// Whether the most recent [initialize] was called with
  /// `legacyPlaintextAllowed: true`. Exposed for tests that want to
  /// assert the migration code paths pass the flag through.
  bool get legacyFlag => _legacy;

  @override
  bool get isInitialized => _initialized;

  @override
  void initialize(
    List<int> keyBytes,
    {bool legacyPlaintextAllowed = false}
  ) {
    _initialized = true;
    _legacy = legacyPlaintextAllowed;
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
      // Mirror the real FieldEncryptor's AAD-mismatch failure: throw a
      // typed exception so tests catch propagation errors loudly.
      throw ArgumentError.value(
        value.length > 60 ? '${value.substring(0, 60)}…' : value,
        'value',
        'FakeFieldEncryptor: AAD mismatch — expected prefix "$prefix"',
      );
    }
    return value.substring(prefix.length);
  }

  @override
  void reset() {
    _initialized = false;
    _legacy = false;
  }
}
