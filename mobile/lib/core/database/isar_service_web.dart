import 'package:wereda_connect/core/database/field_encryptor.dart'
    if (dart.library.html) 'package:wereda_connect/core/database/field_encryptor_web.dart';

/// Web stub for IsarService — no Isar dependency.
///
/// On web, there is no local database. All data goes through the API.
/// This stub satisfies the import so screens compile for web.
class IsarService {
  static IsarService? _instance;

  IsarService._();

  static IsarService get instance {
    _instance ??= IsarService._();
    return _instance!;
  }

  /// Returns dynamic so that Isar collection calls (e.g. isar.users)
  /// become dynamic dispatch and compile on web without .g.dart extensions.
  dynamic get db => null;

  bool get isInitialized => false;

  /// Get the FieldEncryptor instance (no-op on web — returns plaintext).
  FieldEncryptor get fieldEncryptor => FieldEncryptor.instance;

  Future<dynamic> initialize() async => null;

  Future<void> close() async {}

  Future<void> clearAllData() async {}
}
