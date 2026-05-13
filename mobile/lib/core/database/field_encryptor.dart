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
  /// the local autoincrement Isar `id`. Components must be non-empty and
  /// must not contain ':' — both are rejected with `ArgumentError`, because
  /// otherwise distinct trios could collide on the same AAD string and
  /// silently defeat swap detection.
  String _aad(String collection, String column, String rowKey) {
    for (final (name, value) in [
      ('collection', collection), ('column', column), ('rowKey', rowKey),
    ]) {
      if (value.isEmpty) {
        throw ArgumentError.value(
          value, name, 'AAD component must not be empty',
        );
      }
      if (value.contains(':')) {
        throw ArgumentError.value(
          value, name, "AAD component must not contain ':'",
        );
      }
    }
    return '$collection:$column:$rowKey';
  }

  /// Encrypt a plaintext string to a base64-encoded ciphertext.
  ///
  /// Format: base64(nonce + ciphertext + mac), with AAD bound to the trio
  /// `(collection, column, rowKey)`. See [_aad] for the rationale.
  ///
  /// Returns plaintext as fallback if the encryptor is not initialized.
  /// Throws [ArgumentError] if any AAD component is empty or contains ':'.
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
  /// - The input is not valid base64 (legacy plaintext row)
  /// - The decoded body is too short to be AES-GCM ciphertext
  /// - MAC verification fails (wrong key, tampered data, or AAD mismatch)
  ///
  /// Throws [ArgumentError] if any AAD component is empty or contains ':'.
  Future<String> decrypt(
    String ciphertext, {
    required String collection,
    required String column,
    required String rowKey,
  }) async {
    if (!_initialized || _secretKey == null) {
      return ciphertext;
    }

    // _aad ArgumentError must escape the fail-open branches below.
    final aadBytes = utf8.encode(_aad(collection, column, rowKey));

    // Narrow base64-only try so utf8.decode of forged-MAC-valid bytes
    // can't masquerade as "legacy plaintext row". TODO(S1-02): remove.
    final List<int> combined;
    try {
      combined = base64Decode(ciphertext);
    } on FormatException {
      return ciphertext; // legacy plaintext row
    }

    // GCM envelope: 12-byte nonce + ciphertext + 16-byte MAC, min 28 bytes.
    if (combined.length < 28) return ciphertext;

    final nonce = combined.sublist(0, 12);
    final mac = combined.sublist(combined.length - 16);
    final encrypted = combined.sublist(12, combined.length - 16);

    try {
      final decrypted = await _algorithm.decrypt(
        SecretBox(encrypted, nonce: nonce, mac: Mac(mac)),
        secretKey: _secretKey!,
        aad: aadBytes,
      );
      return utf8.decode(decrypted);
    } on SecretBoxAuthenticationError {
      // MAC fail = wrong key, tampered data, or AAD mismatch (col/row swap).
      // S6-01 wires this to Sentry; debugPrint is silenced in release.
      // collection/column are validated colon-free by _aad before reaching here.
      debugPrint('[FieldEncryptor] MAC verification failed for '
          '$collection:$column — key mismatch, AAD mismatch, or tampering.');
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
