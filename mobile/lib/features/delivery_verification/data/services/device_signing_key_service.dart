import 'dart:convert';
import 'dart:math';

import 'package:flutter_secure_storage/flutter_secure_storage.dart';

/// Per-device 32-byte random signing key for QR HMAC derivation.
///
/// Stored in FlutterSecureStorage so other participants cannot derive
/// the HMAC secret. This prevents QR token forgery without physical
/// proximity (F12-SEC-01 fix).
class DeviceSigningKeyService {
  static const _storageKey = 'device_qr_signing_key';

  final FlutterSecureStorage _storage;

  /// Cached key to avoid repeated secure storage reads.
  String? _cachedKey;

  DeviceSigningKeyService({FlutterSecureStorage? storage})
      : _storage = storage ??
            const FlutterSecureStorage(
              aOptions: AndroidOptions(encryptedSharedPreferences: true),
            );

  /// Get or generate the device signing key (base64url-encoded).
  Future<String> getKey() async {
    if (_cachedKey != null) return _cachedKey!;

    final existing = await _storage.read(key: _storageKey);
    if (existing != null) {
      _cachedKey = existing;
      return existing;
    }

    final random = Random.secure();
    final bytes = List<int>.generate(32, (_) => random.nextInt(256));
    final key = base64Url.encode(bytes);

    await _storage.write(key: _storageKey, value: key);
    _cachedKey = key;
    return key;
  }
}
