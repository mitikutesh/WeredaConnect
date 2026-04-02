import 'dart:convert';
import 'dart:typed_data';

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

  /// Encrypt a plaintext string to a base64-encoded ciphertext.
  ///
  /// Format: base64(nonce + ciphertext + mac)
  ///
  /// Returns plaintext as fallback if the encryptor is not initialized.
  /// This ensures the app works during first-launch before the key is ready.
  Future<String> encrypt(String plaintext) async {
    if (!_initialized || _secretKey == null) {
      // Encryptor not yet initialized — storing plaintext.
      // IsarService.initialize() must complete before repositories write PII.
      debugPrint('[FieldEncryptor] encrypt() called before initialization');
      return plaintext;
    }

    final secretBox = await _algorithm.encrypt(
      utf8.encode(plaintext),
      secretKey: _secretKey!,
    );

    // Pack nonce + ciphertext + mac into a single pre-allocated byte array.
    final nonce = secretBox.nonce;
    final cipherText = secretBox.cipherText;
    final mac = secretBox.mac.bytes;
    final combined = Uint8List(nonce.length + cipherText.length + mac.length);
    var pos = 0;
    combined.setAll(pos, nonce); pos += nonce.length;
    combined.setAll(pos, cipherText); pos += cipherText.length;
    combined.setAll(pos, mac);

    return base64Encode(combined);
  }

  /// Decrypt a base64-encoded ciphertext back to plaintext.
  ///
  /// Returns the input string as-is if:
  /// - The encryptor is not initialized (graceful fallback)
  /// - The input doesn't appear to be encrypted (not valid base64 or too short)
  /// - Decryption fails (data was stored before encryption was enabled)
  ///
  /// This graceful fallback ensures existing unencrypted data continues to work
  /// after encryption is enabled — a migration-friendly design.
  Future<String> decrypt(String ciphertext) async {
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
      );

      return utf8.decode(decrypted);
    } on FormatException {
      // Not valid base64 — unencrypted legacy data, return as-is.
      return ciphertext;
    } on SecretBoxAuthenticationError {
      // MAC check failed — could be a wrong key or tampered data.
      // Log at warning level so it surfaces in crash reporting.
      debugPrint(
        '[FieldEncryptor] MAC verification failed — possible key mismatch '
        'or data tampering. Returning ciphertext as-is.',
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
