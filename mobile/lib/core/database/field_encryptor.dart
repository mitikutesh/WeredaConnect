import 'dart:convert';

import 'package:cryptography/cryptography.dart';
import 'package:flutter/foundation.dart';

import 'package:wereda_connect/core/database/base_field_encryptor.dart';
import 'package:wereda_connect/core/database/crypto_failure.dart';

/// Closed set of decrypt failure modes. Each mode carries a fixed
/// human-readable message; nothing dynamic is interpolated.
enum FieldDecryptionReason {
  missingPrefix('Missing cipher prefix'),
  invalidBase64('Invalid base64'),
  truncatedCiphertext('Truncated ciphertext'),
  macVerificationFailed('MAC verification failed');

  const FieldDecryptionReason(this.message);
  final String message;
}

/// Thrown by [FieldEncryptor.decrypt] when a ciphertext fails any of:
/// - missing `enc:v1:` version prefix (downgrade attack or legacy data)
/// - invalid base64 body
/// - truncated body (< 12-byte nonce + 16-byte MAC = 28 bytes)
/// - GCM MAC verification failure (wrong key, tampering, or AAD mismatch)
///
/// Callers MUST treat this as abnormal. Never catch-and-fallback to the
/// raw input value — doing so reintroduces the downgrade-attack surface
/// this exception was added to close.
///
/// Implements [CryptoFailure] so callers can catch the shared supertype
/// alongside [KeyIntegrityFailure] when the handling is identical.
class FieldDecryptionException implements CryptoFailure {
  final FieldDecryptionReason kind;
  const FieldDecryptionException(this.kind);
  @override
  String get reason => kind.message;
  @override
  String toString() => 'FieldDecryptionException: ${kind.message}';
}

/// Provides AES-256-GCM field-level encryption for PII fields stored in Isar.
///
/// Isar 3.1 does not support built-in encryption, so sensitive fields
/// (phone number, name, faydaId) are encrypted/decrypted at the repository
/// layer before writing to / after reading from the database.
///
/// Security design:
/// - AES-256-GCM with authenticated encryption (confidentiality + integrity)
/// - Fresh 12-byte nonce generated per encryption call
/// - AAD bound to (collection, column, rowKey) — see [BaseFieldEncryptor.buildAad]
/// - `enc:v1:` version prefix on every ciphertext — see [cipherPrefix]
/// - Fail-closed: decrypt throws [FieldDecryptionException] on any anomaly
/// - Key derived from FlutterSecureStorage (Android Keystore-backed)
///
/// Ethiopian context:
/// - Protects user PII on shared devices common in rural communities
/// - Works entirely offline — no network calls needed
/// - Minimal CPU overhead on low-end Android 8+ devices
class FieldEncryptor extends BaseFieldEncryptor {
  static FieldEncryptor? _instance;

  final AesGcm _algorithm = AesGcm.with256bits();
  SecretKey? _secretKey;
  bool _initialized = false;

  /// Version tag prepended to every ciphertext written by [encrypt].
  /// [decrypt] rejects anything without this prefix unless
  /// [_legacyPlaintextAllowed] is set — see [initialize].
  ///
  /// Public so the HS-3 migration can skip already-prefixed rows for
  /// idempotency under crash-retry.
  static const String cipherPrefix = 'enc:v1:';

  /// MIGRATION-ONLY escape hatch. When `true`, [decrypt] returns unprefixed
  /// inputs unchanged so the HS-3 migration can read existing plaintext
  /// rows once before re-encrypting them. Production reads MUST always run
  /// with this `false`. Cleared by [reset].
  bool _legacyPlaintextAllowed = false;

  FieldEncryptor._();

  /// Get the singleton instance of FieldEncryptor.
  static FieldEncryptor get instance {
    _instance ??= FieldEncryptor._();
    return _instance!;
  }

  /// Whether the encryptor has been initialized with a key.
  @override
  bool get isInitialized => _initialized;

  /// Initialize the encryptor with the encryption key bytes.
  ///
  /// Must be called once during app startup (from `IsarService.initialize()`).
  /// The [keyBytes] should be the 32-byte AES-256 key from secure storage.
  ///
  /// [legacyPlaintextAllowed] is the HS-3 v1→v2 migration escape hatch. When
  /// `true`, [decrypt] returns unprefixed inputs as-is. Migration code flips
  /// it back to `false` after the last row is re-encrypted. Default `false`
  /// so production reads always fail closed.
  @override
  void initialize(
    List<int> keyBytes, {
    bool legacyPlaintextAllowed = false,
  }) {
    if (keyBytes.length != 32) {
      throw ArgumentError('Encryption key must be 32 bytes (AES-256)');
    }
    _secretKey = SecretKey(Uint8List.fromList(keyBytes));
    _legacyPlaintextAllowed = legacyPlaintextAllowed;
    _initialized = true;
  }

  /// Encrypt a plaintext string to `enc:v1:base64(nonce + ct + mac)`.
  ///
  /// AAD is bound to the `(collection, column, rowKey)` trio — see
  /// [BaseFieldEncryptor.buildAad]. A fresh 12-byte nonce is generated
  /// for every call.
  ///
  /// **Fail-closed:** throws [StateError] if [initialize] has not run.
  /// Throws [ArgumentError] if any AAD component is empty or contains ':'.
  @override
  Future<String> encrypt(
    String plaintext, {
    required String collection,
    required String column,
    required String rowKey,
  }) async {
    if (!_initialized || _secretKey == null) {
      throw StateError(
        'FieldEncryptor.encrypt called before initialize(). '
        'IsarService.initialize() must complete before any repository write.',
      );
    }

    final secretBox = await _algorithm.encrypt(
      utf8.encode(plaintext),
      secretKey: _secretKey!,
      aad: utf8.encode(
        BaseFieldEncryptor.buildAad(collection, column, rowKey),
      ),
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

    return '$cipherPrefix${base64Encode(combined)}';
  }

  /// Decrypt `enc:v1:base64(nonce + ct + mac)` back to plaintext.
  ///
  /// Caller MUST supply the same `(collection, column, rowKey)` trio used at
  /// encrypt time — AAD mismatch fails the MAC check.
  ///
  /// **Fail-closed contract:**
  /// - Uninitialized        → [StateError]
  /// - No `enc:v1:` prefix  → [FieldDecryptionException] (unless legacy mode)
  /// - Bad base64           → [FieldDecryptionException]
  /// - Truncated body       → [FieldDecryptionException]
  /// - MAC verify failed    → [FieldDecryptionException]
  ///
  /// Throws [ArgumentError] if any AAD component is empty or contains ':'.
  @override
  Future<String> decrypt(
    String value, {
    required String collection,
    required String column,
    required String rowKey,
  }) async {
    if (!_initialized || _secretKey == null) {
      throw StateError(
        'FieldEncryptor.decrypt called before initialize(). '
        'IsarService.initialize() must complete before any repository read.',
      );
    }

    // buildAad ArgumentError must escape the failure branches below.
    final aadBytes = utf8.encode(
      BaseFieldEncryptor.buildAad(collection, column, rowKey),
    );

    if (!value.startsWith(cipherPrefix)) {
      if (_legacyPlaintextAllowed) {
        // ONLY reachable while the HS-3 v2-encryption migration is running.
        return value;
      }
      _report('Missing cipher prefix on read for $collection:$column');
      throw const FieldDecryptionException(FieldDecryptionReason.missingPrefix);
    }

    final body = value.substring(cipherPrefix.length);
    final List<int> combined;
    try {
      combined = base64Decode(body);
    } on FormatException {
      _report('Ciphertext is not valid base64 for $collection:$column');
      throw const FieldDecryptionException(FieldDecryptionReason.invalidBase64);
    }
    // GCM envelope: 12-byte nonce + ciphertext + 16-byte MAC, min 28 bytes.
    if (combined.length < 28) {
      _report(
        'Ciphertext truncated (length=${combined.length}) for '
        '$collection:$column',
      );
      throw const FieldDecryptionException(
        FieldDecryptionReason.truncatedCiphertext,
      );
    }

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
      // collection/column are validated colon-free by buildAad above.
      _report('GCM MAC verification failed for $collection:$column');
      throw const FieldDecryptionException(
        FieldDecryptionReason.macVerificationFailed,
      );
    }
  }

  /// Telemetry hook. S6-01 wires this to Sentry.captureMessage at warning
  /// level. Do NOT include the raw value or the AAD `rowKey` here — those
  /// can leak PII or identifiers into the telemetry sink.
  void _report(String reason) {
    debugPrint('[FieldEncryptor] $reason');
    // S6-01: Sentry.captureMessage(reason, level: SentryLevel.warning);
  }

  /// Reset the encryptor (for testing, logout, or key rotation).
  ///
  /// Clears the in-memory key, the initialized flag, and the legacy-plaintext
  /// flag. After calling, [initialize] must run again before encrypt/decrypt
  /// will work — they will throw [StateError] until then.
  @override
  void reset() {
    _secretKey = null;
    _initialized = false;
    _legacyPlaintextAllowed = false;
  }

  /// Reset the singleton instance (for testing only).
  static void resetInstance() {
    _instance?.reset();
    _instance = null;
  }
}
