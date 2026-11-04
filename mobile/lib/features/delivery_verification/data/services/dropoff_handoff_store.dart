import 'dart:convert';

import 'package:flutter/foundation.dart';
import 'package:flutter_secure_storage/flutter_secure_storage.dart';

/// What the trucker's phone keeps from a pickup handoff for the drop-off
/// (usability audit T-3): the farmer's QR token as it was scanned, with its
/// typed code, or only the code when the trucker typed it at the farm.
///
/// The buyer's confirm checks the same pickup nonce the trucker scanned
/// (one nonce for both legs, each leg used once), so showing this token is
/// how the buyer scans at the drop-off. A separate trucker nonce is a
/// follow-up.
@immutable
class KeptHandoff {
  const KeptHandoff({this.token, this.code});

  /// The farmer's QR token, rendered again as the drop-off QR; null when the
  /// pickup was confirmed with a typed code.
  final String? token;

  /// The nine-digit typed code of the same nonce.
  final String? code;

  bool get isEmpty =>
      (token == null || token!.isEmpty) && (code == null || code!.isEmpty);

  Map<String, Object> toJson() => {
        if (token != null) 't': token!,
        if (code != null) 'c': code!,
      };

  static KeptHandoff? fromJson(Object? json) {
    if (json is! Map) return null;
    final token = json['t'];
    final code = json['c'];
    final kept = KeptHandoff(
      token: token is String ? token : null,
      code: code is String ? code : null,
    );
    return kept.isEmpty ? null : kept;
  }
}

/// Keeps a trucker's [KeptHandoff]s across restarts, per account and
/// delivery.
abstract interface class DropoffHandoffStore {
  Future<KeptHandoff?> read(String userId, String deliveryId);

  Future<void> keep(String userId, String deliveryId, KeptHandoff handoff);
}

/// The app's store: one encrypted entry per trucker in secure storage (the
/// Android keystore; on web the browser's storage, as the QR signing key),
/// the newest [maxDeliveries] kept. Never in memory only: the drop-off can be
/// days after the pickup and the app restarts in between.
class SecureDropoffHandoffStore implements DropoffHandoffStore {
  SecureDropoffHandoffStore({FlutterSecureStorage? storage})
      : _storage = storage ??
            const FlutterSecureStorage(
              aOptions: AndroidOptions(encryptedSharedPreferences: true),
            );

  final FlutterSecureStorage _storage;

  /// A truck carries a handful of orders; older ones are long delivered.
  static const maxDeliveries = 20;

  static String _key(String userId) => 'dropoff_handoffs_$userId';

  Future<Map<String, Object?>> _load(String userId) async {
    final raw = await _storage.read(key: _key(userId));
    if (raw == null) return {};
    final decoded = jsonDecode(raw);
    return decoded is Map<String, Object?> ? decoded : {};
  }

  @override
  Future<KeptHandoff?> read(String userId, String deliveryId) async {
    try {
      return KeptHandoff.fromJson((await _load(userId))[deliveryId]);
    } on Object catch (e) {
      debugPrint('[Delivery] kept handoff unreadable: $e');
      return null;
    }
  }

  @override
  Future<void> keep(
    String userId,
    String deliveryId,
    KeptHandoff handoff,
  ) async {
    if (handoff.isEmpty) return;
    try {
      Map<String, Object?> all;
      try {
        all = await _load(userId);
      } on Object {
        all = {};
      }
      // Re-inserted last, so the oldest entries are the first ones.
      all
        ..remove(deliveryId)
        ..[deliveryId] = handoff.toJson();
      while (all.length > maxDeliveries) {
        all.remove(all.keys.first);
      }
      await _storage.write(key: _key(userId), value: jsonEncode(all));
    } on Object catch (e) {
      debugPrint('[Delivery] handoff not kept: $e');
    }
  }
}

/// In-memory store for tests.
class MemoryDropoffHandoffStore implements DropoffHandoffStore {
  final Map<String, KeptHandoff> _kept = {};

  @override
  Future<KeptHandoff?> read(String userId, String deliveryId) async =>
      _kept['$userId/$deliveryId'];

  @override
  Future<void> keep(
    String userId,
    String deliveryId,
    KeptHandoff handoff,
  ) async {
    if (!handoff.isEmpty) _kept['$userId/$deliveryId'] = handoff;
  }
}
