import 'package:flutter_secure_storage/flutter_secure_storage.dart';
import 'package:wereda_connect/core/auth/auth_storage.dart';

/// Mobile auth storage backed by FlutterSecureStorage.
///
/// Uses Android EncryptedSharedPreferences / iOS Keychain.
class MobileAuthStorage implements AuthStorage {
  static const _storage = FlutterSecureStorage(
    aOptions: AndroidOptions(encryptedSharedPreferences: true),
  );

  const MobileAuthStorage();

  @override
  Future<String?> read({required String key}) => _storage.read(key: key);

  @override
  Future<void> write({required String key, required String value}) =>
      _storage.write(key: key, value: value);

  @override
  Future<void> delete({required String key}) => _storage.delete(key: key);
}
