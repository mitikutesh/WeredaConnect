import 'dart:async';

import 'package:firebase_core/firebase_core.dart';
import 'package:firebase_messaging/firebase_messaging.dart';

import 'package:wereda_connect/core/network/api_client.dart';
import 'package:wereda_connect/core/utils/entity_id.dart';

/// Registers the device's FCM token with the backend (`POST /device-tokens`) so
/// the server can deliver push notifications (new-message alerts, buyer match
/// alerts and ESC-21 order events) to this device, listens for foreground
/// pushes, and opens the order of a tapped order push.
///
/// Best-effort and fully optional: if Firebase isn't configured (no
/// `google-services.json`, or the SNS platform app isn't set up) it silently
/// no-ops, preserving offline-first behavior. Android only — iOS/APNs is
/// deferred. On web this file is never compiled (the web entrypoint
/// `main_web.dart` doesn't reference it).
class PushNotificationService {
  PushNotificationService({required ApiClient api}) : _api = api;

  final ApiClient _api;

  /// Live subscriptions — static because every auth transition constructs a
  /// fresh service; a second `init()` must REPLACE the listeners, not stack
  /// them (N logins would otherwise mean N callbacks per push).
  static StreamSubscription<RemoteMessage>? _onMessageSubscription;
  static StreamSubscription<RemoteMessage>? _onOpenedSubscription;
  static StreamSubscription<String>? _tokenRefreshSubscription;

  /// The push that cold-started the app is opened once per launch: a later
  /// sign-in (another user on the device) must not open it again.
  static bool _initialMessageHandled = false;

  /// Whether a push payload is a chat-message alert. The same FCM pipe also
  /// carries buyer-match alerts; MessageStreamFunction stamps its payloads
  /// with `data.type == "message"`.
  static bool isMessagePush(Map<String, dynamic> data) =>
      data['type'] == 'message';

  /// Whether a push payload is an order event (notifications-api.md
  /// section 2): `data.type == "order"`, the event in `data.event`.
  static bool isOrderPush(Map<String, dynamic> data) => data['type'] == 'order';

  /// The order a tapped push should open, or null (a chat or match push, an
  /// order event without an order, or an id that is not a plain token).
  static String? orderIdToOpen(Map<String, dynamic> data) {
    if (!isOrderPush(data)) return null;
    final orderId = data['orderId'];
    return orderId is String && isPlainEntityId(orderId) ? orderId : null;
  }

  /// Initialize Firebase, request the notification permission (Android 13+),
  /// register the current token, and re-register on rotation. Call after the
  /// user is authenticated (the `/device-tokens` endpoint requires auth).
  ///
  /// [onMessagePush] fires when a chat-message push arrives while the app is
  /// in the FOREGROUND (Android suppresses the tray notification then, so
  /// this is the only signal) — the caller nudges the inbox to pull
  /// immediately, which drives the in-app banner and unread badges.
  ///
  /// [onOrderPush] fires for a foreground order push with its `orderId` (or
  /// null): the caller pulls the notification inbox and the order.
  /// [onOpenOrder] fires when the user taps an order push, from the
  /// background or after a cold start, with the order to open.
  Future<void> init({
    void Function()? onMessagePush,
    void Function(String? orderId)? onOrderPush,
    void Function(String orderId)? onOpenOrder,
  }) async {
    try {
      await Firebase.initializeApp();
      final messaging = FirebaseMessaging.instance;

      // Android 13+ runtime notification permission.
      await messaging.requestPermission();

      // The listeners and the cold-start tap come before the token: a slow
      // registration on 3G, or a token that cannot be read, must not delay
      // or drop opening the order the user tapped.
      await _onMessageSubscription?.cancel();
      _onMessageSubscription = FirebaseMessaging.onMessage.listen((message) {
        if (isMessagePush(message.data)) onMessagePush?.call();
        if (isOrderPush(message.data)) {
          onOrderPush?.call(orderIdToOpen(message.data));
        }
      });

      // ESC-21: a tapped order push opens its order.
      await _onOpenedSubscription?.cancel();
      _onOpenedSubscription =
          FirebaseMessaging.onMessageOpenedApp.listen((message) {
        final orderId = orderIdToOpen(message.data);
        if (orderId != null) onOpenOrder?.call(orderId);
      });
      if (!_initialMessageHandled) {
        _initialMessageHandled = true;
        final initial = await messaging.getInitialMessage();
        final initialOrderId =
            initial == null ? null : orderIdToOpen(initial.data);
        if (initialOrderId != null) onOpenOrder?.call(initialOrderId);
      }

      // Re-register whenever FCM rotates the token.
      await _tokenRefreshSubscription?.cancel();
      _tokenRefreshSubscription = messaging.onTokenRefresh.listen(_register);
      final token = await messaging.getToken();
      if (token != null) await _register(token);
    } on Object catch (_) {
      // Firebase not configured / unavailable — push disabled, app continues.
    }
  }

  /// This device's current FCM token, or null when push isn't set up.
  /// Used at logout to ask the server to stop pushing to this device.
  static Future<String?> currentToken() async {
    try {
      if (Firebase.apps.isEmpty) return null;
      return await FirebaseMessaging.instance.getToken();
    } on Object catch (_) {
      return null;
    }
  }

  /// Drop this device's FCM token once the account is deleted: FCM stops
  /// delivering to it, and the next sign-in registers a fresh one. The
  /// server removes its own copy as part of the deletion.
  static Future<void> deleteToken() async {
    try {
      if (Firebase.apps.isEmpty) return;
      await _tokenRefreshSubscription?.cancel();
      _tokenRefreshSubscription = null;
      await FirebaseMessaging.instance.deleteToken();
    } on Object catch (_) {
      // Push not set up or unreachable: nothing to drop.
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
