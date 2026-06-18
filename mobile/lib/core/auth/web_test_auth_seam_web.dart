import 'dart:convert';
import 'dart:js_interop';

import 'package:flutter_secure_storage/flutter_secure_storage.dart';
import 'package:wereda_connect/core/auth/models/auth_tokens.dart';
// Web-only file: import the web cache directly (the bare offline_auth_cache.dart
// is the Isar/native impl and must never enter the dart2js web build).
import 'package:wereda_connect/core/auth/offline_auth_cache_web.dart';
import 'package:wereda_connect/core/models/user.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_event.dart';

/// Binds `globalThis.__seedAuth` (i.e. `window.__seedAuth`) to a Dart function.
@JS('__seedAuth')
external set _seedAuthGlobal(JSFunction value);

// Same constructor as AuthBloc / OfflineAuthCache so the flutter_secure_storage
// web `publicKey` prefix matches and the app reads back what we write.
const FlutterSecureStorage _storage = FlutterSecureStorage(
  aOptions: AndroidOptions(encryptedSharedPreferences: true),
);

/// Real (web-only) implementation of the F-37 test auth seam. Registers
/// `window.__seedAuth(payloadJson)` — a Promise-returning hook the Playwright
/// webapp e2e tests call to put the app into an authenticated state
/// deterministically (CanvasKit exposes no stable login DOM). It writes a full
/// session through the REAL OfflineAuthCache + session-pointer keys, then fires
/// AuthCheckRequested so go_router leaves /login.
///
/// This is only reachable via `web_test_auth_seam.dart`, which gates it to
/// non-production builds (compile-time allowlist + runtime isProduction check).
void registerWebTestAuthSeamImpl(AuthBloc authBloc) {
  _seedAuthGlobal =
      ((JSString payloadJson) => _seed(authBloc, payloadJson.toDart).toJS).toJS;
}

Future<void> _seed(AuthBloc authBloc, String payloadJson) async {
  final m = jsonDecode(payloadJson) as Map<String, dynamic>;
  final phone = m['phone'] as String;
  final t = m['tokens'] as Map<String, dynamic>;
  final u = m['user'] as Map<String, dynamic>;

  final tokens = AuthTokens(
    accessToken: t['accessToken'] as String,
    refreshToken: (t['refreshToken'] as String?) ?? '',
    idToken: t['idToken'] as String,
    // Future expiry so the restored session is treated as online (not offline).
    expiresAt: DateTime.now().toUtc().add(
          Duration(seconds: (t['expiresIn'] as num?)?.toInt() ?? 900),
        ),
  );

  // getCachedUser rejects an unknown role (returns null, F-34), so map strictly
  // and fail loudly rather than writing a record the app silently bounces.
  final roleStr = (u['role'] as String).toLowerCase();
  final role = UserRole.values.firstWhere(
    (r) => r.name.toLowerCase() == roleStr,
    orElse: () => throw StateError('seedAuth: unknown role "$roleStr"'),
  );

  // Writes cached_tokens_<phone> + cached_user_<phone> via the real cache.
  await OfflineAuthCache().cacheCredentials(
    phoneNumber: phone,
    password: '',
    userId: u['userId'] as String,
    role: role,
    name: (u['displayName'] as String?) ?? '',
    woreda: (u['woreda'] as String?) ?? '',
    tokens: tokens,
    displayName: u['displayName'] as String?,
  );

  // Session pointers the cache does not write (mirrors AuthBloc._saveSession).
  // 'current_user_phone' is AuthBloc._currentUserPhoneKey (private).
  await _storage.write(key: 'current_user_phone', value: phone);
  await _storage.write(
    key: AuthBloc.currentUserIdKey,
    value: u['userId'] as String,
  );

  // Re-run the startup auth check so the router leaves /login.
  authBloc.add(const AuthCheckRequested());
}
