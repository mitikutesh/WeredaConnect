import 'package:firebase_core/firebase_core.dart';
import 'package:firebase_messaging/firebase_messaging.dart';

import 'package:wereda_connect/core/network/api_client.dart';

/// Registers the device's FCM token with the backend (`POST /device-tokens`) so
/// the server can deliver push notifications (new-message alerts and buyer match
/// alerts) to this device.
///
/// Best-effort and fully optional: if Firebase isn't configured (no
/// `google-services.json`, or the SNS platform app isn't set up) it silently
/// no-ops, preserving offline-first behavior. Android only — iOS/APNs is
/// deferred. On web this file is never compiled (the web entrypoint
/// `main_web.dart` doesn't reference it).
class PushNotificationService {
  PushNotificationService({required ApiClient api}) : _api = api;

  final ApiClient _api;

  /// Initialize Firebase, request the notification permission (Android 13+),
  /// register the current token, and re-register on rotation. Call after the
  /// user is authenticated (the `/device-tokens` endpoint requires auth).
  Future<void> init() async {
    try {
      await Firebase.initializeApp();
      final messaging = FirebaseMessaging.instance;

      // Android 13+ runtime notification permission.
      await messaging.requestPermission();

      final token = await messaging.getToken();
      if (token != null) await _register(token);

      // Re-register whenever FCM rotates the token.
      messaging.onTokenRefresh.listen(_register);
    } on Object catch (_) {
      // Firebase not configured / unavailable — push disabled, app continues.
    }
  }

  Future<void> _register(String token) async {
    try {
      await _api.post<Map<String, dynamic>>(
        '/device-tokens',
        data: {'token': token, 'platform': 'android'},
      );
    } on Object catch (_) {
      // Non-fatal: the in-app unread badge still surfaces new messages.
    }
  }
}
