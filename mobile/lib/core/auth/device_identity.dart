import 'package:flutter/foundation.dart';
import 'package:flutter_secure_storage/flutter_secure_storage.dart';
import 'package:uuid/uuid.dart';

import 'package:wereda_connect/core/auth/device_name_stub.dart'
    if (dart.library.html) 'package:wereda_connect/core/auth/device_name_web.dart';

/// Stable per-install device identity sent on login, register and verify-otp
/// so the server can track devices and flag a sign-in from a new one.
///
/// The id is a random UUID v4 generated once and kept in secure storage under
/// [deviceIdKey]; it carries no hardware identifier. The device name is a
/// short, PII-free description ("Android phone", "Chrome on Windows").
class DeviceIdentity {
  DeviceIdentity({FlutterSecureStorage? storage, String? deviceName})
      : _storage = storage ??
            const FlutterSecureStorage(
              aOptions: AndroidOptions(encryptedSharedPreferences: true),
            ),
        _deviceNameOverride = deviceName;

  /// Secure storage key holding the install's device id.
  static const deviceIdKey = 'device_id';

  /// Request header carrying the device id.
  static const deviceIdHeader = 'X-Device-Id';

  /// Request header carrying the short device description.
  static const deviceNameHeader = 'X-Device-Name';

  final FlutterSecureStorage _storage;
  final String? _deviceNameOverride;
  String? _cachedId;

  /// The install's device id, created on first use.
  Future<String> deviceId() async {
    final cached = _cachedId;
    if (cached != null) return cached;
    try {
      final stored = await _storage.read(key: deviceIdKey);
      if (stored != null && stored.isNotEmpty) {
        return _cachedId = stored;
      }
      final created = const Uuid().v4();
      await _storage.write(key: deviceIdKey, value: created);
      return _cachedId = created;
    } on Object catch (e) {
      // Secure storage unavailable: use an in-memory id for this run so the
      // request still carries a header. Never block login on this.
      debugPrint('[DeviceIdentity] secure storage failed: $e');
      return _cachedId = const Uuid().v4();
    }
  }

  /// Short, PII-free description of this device or browser.
  String get deviceName => _deviceNameOverride ?? describeDevice();

  /// Headers to attach to login / register / verify-otp requests.
  Future<Map<String, String>> headers() async => {
        deviceIdHeader: await deviceId(),
        deviceNameHeader: deviceName,
      };
}
