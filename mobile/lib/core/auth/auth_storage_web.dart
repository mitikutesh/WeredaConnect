import 'package:web/web.dart' as web;

import 'package:wereda_connect/core/auth/auth_storage.dart';

/// Web auth storage backed by browser sessionStorage.
///
/// Uses sessionStorage (cleared on tab close) for security.
/// Auth tokens should not persist across browser sessions.
class WebAuthStorage implements AuthStorage {
  const WebAuthStorage();

  @override
  Future<String?> read({required String key}) async {
    return web.window.sessionStorage.getItem(key);
  }

  @override
  Future<void> write({required String key, required String value}) async {
    web.window.sessionStorage.setItem(key, value);
  }

  @override
  Future<void> delete({required String key}) async {
    web.window.sessionStorage.removeItem(key);
  }
}
