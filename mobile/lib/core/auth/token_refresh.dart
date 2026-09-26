import 'package:dio/dio.dart';
import 'package:flutter/foundation.dart';
import 'package:flutter_secure_storage/flutter_secure_storage.dart';

import 'package:wereda_connect/core/auth/models/auth_tokens.dart';
import 'package:wereda_connect/core/auth/offline_auth_cache.dart'
    if (dart.library.html) 'package:wereda_connect/core/auth/offline_auth_cache_web.dart';
import 'package:wereda_connect/core/auth/session_signals.dart';
import 'package:wereda_connect/core/network/api_config.dart';
import 'package:wereda_connect/core/network/json_body.dart';

/// Persists refreshed tokens to the offline cache (tokens + 48h timestamp).
typedef RefreshedTokensWriter = Future<void> Function(
  String phoneNumber,
  AuthTokens tokens,
);

/// Builds the [AuthInterceptor] refresh callback shared by `main.dart` and
/// `main_web.dart` (they used to carry two hand-rolled copies).
///
/// On success it writes the active token slots the interceptor reads and
/// refreshes the offline cache through [OfflineAuthCache.updateTokens], which
/// also restarts the 48h offline session clock. The old copies wrote the
/// cached tokens by hand and skipped that timestamp, so an actively used
/// session still expired 48h after the original login.
///
/// When the server rejects the refresh token (400/401) it returns null (the
/// interceptor then clears the tokens) and raises
/// [SessionSignals.sessionExpired] so the AuthBloc logs the user out.
/// Transient failures are rethrown and keep the refresh token.
Future<String?> Function(String refreshToken) buildRefreshTokenCallback({
  required Dio refreshDio,
  required FlutterSecureStorage storage,
  RefreshedTokensWriter? writeCachedTokens,
  SessionSignals? signals,
}) {
  final writer = writeCachedTokens ??
      (String phone, AuthTokens tokens) =>
          OfflineAuthCache().updateTokens(phone, tokens);
  final sessionSignals = signals ?? SessionSignals.instance;

  return (String refreshToken) async {
    try {
      final response = await refreshDio.post<dynamic>(
        ApiConfig.refreshEndpoint,
        data: {'refreshToken': refreshToken},
      );
      // On web, response.data may be a String. Decode if needed.
      final data = requireJsonMap(response.data, what: 'refresh response');
      final tokenData = data['tokens'] as Map<String, dynamic>;
      final expiresIn = (tokenData['expiresIn'] as num?)?.toInt() ?? 3600;
      final tokens = AuthTokens(
        accessToken: tokenData['accessToken'] as String,
        idToken: tokenData['idToken'] as String,
        refreshToken: tokenData['refreshToken'] as String? ?? refreshToken,
        expiresAt: DateTime.now().add(Duration(seconds: expiresIn)),
      );

      // Active slots read by the AuthInterceptor. The ID token is the bearer
      // (carries custom:role); the access token is kept for server-side
      // revocation on logout.
      await storage.write(key: 'access_token', value: tokens.accessToken);
      await storage.write(key: 'id_token', value: tokens.idToken);
      await storage.write(key: 'refresh_token', value: tokens.refreshToken);

      // Offline cache + 48h session timestamp, so AuthBloc sees fresh tokens.
      final phone = await storage.read(key: 'current_user_phone');
      if (phone != null && phone.isNotEmpty) {
        try {
          await writer(phone, tokens);
        } on Object catch (e) {
          debugPrint('[TokenRefresh] offline cache update failed: $e');
        }
      }

      // The ID token becomes the new bearer for retried requests.
      return tokens.idToken;
    } on DioException catch (e) {
      final statusCode = e.response?.statusCode;
      if (statusCode == 400 || statusCode == 401) {
        sessionSignals.sessionExpired();
        return null;
      }
      rethrow;
    }
  };
}
