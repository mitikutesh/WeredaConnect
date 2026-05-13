import 'dart:convert';

import 'package:cryptography/cryptography.dart';
import 'package:flutter/foundation.dart';

/// Provides AES-256-GCM field-level encryption for PII fields stored in Isar.
///
/// Isar 3.1 does not support built-in encryption, so sensitive fields
/// (phone number, name, faydaId) are encrypted/decrypted at the repository
/// layer before writing to / after reading from the database.
///
/// Security design:
/// - AES-256-GCM provides authenticated encryption (confidentiality + integrity)
/// - A fresh 12-byte nonce is generated per encryption call (prepended to ciphertext)
/// - The encryption key is derived from FlutterSecureStorage (Android Keystore-backed)
/// - Ciphertext is base64-encoded for safe storage in Isar String fields
///
/// Ethiopian context:
/// - Protects user PII on shared devices common in rural communities
/// - Works entirely offline — no network calls needed
/// - Minimal CPU overhead on low-end Android 8+ devices
class FieldEncryptor {
  static FieldEncryptor? _instance;

  final AesGcm _algorithm = AesGcm.with256bits();
  SecretKey? _secretKey;
  bool _initialized = false;

  // Private constructor for singleton pattern
  FieldEncryptor._();

  /// Get the singleton instance of FieldEncryptor.
  static FieldEncryptor get instance {
    _instance ??= FieldEncryptor._();
    return _instance!;
  }

  /// Whether the encryptor has been initialized with a key.
  bool get isInitialized => _initialized;

  /// Initialize the encryptor with the encryption key bytes.
  ///
  /// Must be called once during app startup (from IsarService.initialize()).
  /// The [keyBytes] should be the 32-byte AES-256 key from secure storage.
  void initialize(List<int> keyBytes) {
    if (keyBytes.length != 32) {
      throw ArgumentError('Encryption key must be 32 bytes (AES-256)');
    }
    _secretKey = SecretKey(Uint8List.fromList(keyBytes));
    _initialized = true;
  }

  /// Build the AAD (Associated Authenticated Data) for a given field.
  ///
  /// AES-GCM authenticates this string alongside the ciphertext: a blob
  /// encrypted for `(User, displayName, sub-A)` will fail MAC verification
  /// if decrypted with `(User, displayName, sub-B)` or
  /// `(User, phoneNumber, sub-A)`. Without AAD, ciphertexts are portable
  /// between columns and rows, which is a substitution-attack surface for
  /// any attacker with write access to the Isar file.
  ///
  /// `rowKey` MUST be a stable identifier (server UUID, Cognito sub) — not
  /// the local autoincrement Isar `id`, which can be reassigned on import.
  String _aad(String collection, String column, String rowKey) =>
      '$collection:$column:$rowKey';

  /// Encrypt a plaintext string to a base64-encoded ciphertext.
  ///
  /// Format: base64(nonce + ciphertext + mac), with AAD bound to the trio
  /// `(collection, column, rowKey)`. See [_aad] for the rationale.
  ///
  /// Returns plaintext as fallback if the encryptor is not initialized.
  /// This ensures the app works during first-launch before the key is ready.
  /// Note: fail-closed behavior on uninitialized encryptor lands in S1-02.
  Future<String> encrypt(
    String plaintext, {
    required String collection,
    required String column,
    required String rowKey,
  }) async {
    if (!_initialized || _secretKey == null) {
      // Encryptor not yet initialized — storing plaintext.
      // IsarService.initialize() must complete before repositories write PII.
      // TODO(S1-02): replace this fail-open path with a StateError throw.
      debugPrint('[FieldEncryptor] encrypt() called before initialization');
      return plaintext;
    }

    final secretBox = await _algorithm.encrypt(
      utf8.encode(plaintext),
      secretKey: _secretKey!,
      aad: utf8.encode(_aad(collection, column, rowKey)),
    );

    // Pack nonce + ciphertext + mac into a single pre-allocated byte array.
    final nonce = secretBox.nonce;
    final cipherText = secretBox.cipherText;
    final mac = secretBox.mac.bytes;
    final combined = Uint8List(nonce.length + cipherText.length + mac.length);
    var pos = 0;
    combined.setAll(pos, nonce);
    pos += nonce.length;
    combined.setAll(pos, cipherText);
    pos += cipherText.length;
    combined.setAll(pos, mac);

    return base64Encode(combined);
  }

  /// Decrypt a base64-encoded ciphertext back to plaintext.
  ///
  /// Caller MUST supply the same `(collection, column, rowKey)` trio used at
  /// encrypt time — AAD mismatch fails the MAC check.
  ///
  /// Returns the input string as-is if:
  /// - The encryptor is not initialized (graceful fallback)
  /// - The input doesn't appear to be encrypted (not valid base64 or too short)
  /// - Decryption fails (data was stored before encryption was enabled,
  ///   or the AAD trio doesn't match)
  ///
  /// This graceful fallback ensures existing unencrypted data continues to
  /// work after encryption is enabled — a migration-friendly design.
  /// Note: this fail-open behavior is itself a downgrade-attack vector and
  /// is replaced with `enc:v1:` prefix + fail-closed in S1-02.
  Future<String> decrypt(
    String ciphertext, {
    required String collection,
    required String column,
    required String rowKey,
  }) async {
    if (!_initialized || _secretKey == null) {
      return ciphertext;
    }

    try {
      final combined = base64Decode(ciphertext);

      // AES-GCM: 12-byte nonce + ciphertext + 16-byte MAC
      // Minimum length: 12 (nonce) + 0 (empty plaintext) + 16 (MAC) = 28
      if (combined.length < 28) {
        return ciphertext; // Too short to be encrypted data
      }

      final nonce = combined.sublist(0, 12);
      final mac = combined.sublist(combined.length - 16);
      final encrypted = combined.sublist(12, combined.length - 16);

      final secretBox = SecretBox(
        encrypted,
        nonce: nonce,
        mac: Mac(mac),
      );

      final decrypted = await _algorithm.decrypt(
        secretBox,
        secretKey: _secretKey!,
        aad: utf8.encode(_aad(collection, column, rowKey)),
      );

      return utf8.decode(decrypted);
    } on FormatException {
      // Not valid base64 — unencrypted legacy data, return as-is.
      return ciphertext;
    } on SecretBoxAuthenticationError {
      // MAC check failed — wrong key, tampered data, or AAD mismatch
      // (column/row swap). Log so it surfaces in crash reporting.
      debugPrint(
        '[FieldEncryptor] MAC verification failed for '
        '$collection:$column — possible key mismatch, AAD mismatch, or '
        'tampering. Returning ciphertext as-is.',
      );
      return ciphertext;
    }
  }

  /// Reset the encryptor (for testing or key rotation).
  ///
  /// After calling this, [initialize] must be called again before
  /// encryption/decryption will work (they will fall back to plaintext).
  void reset() {
    _secretKey = null;
    _initialized = false;
  }

  /// Reset the singleton instance (for testing only).
  static void resetInstance() {
    _instance?.reset();
    _instance = null;
  }
}
