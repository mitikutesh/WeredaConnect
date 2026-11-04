import 'dart:convert';

import 'package:flutter/foundation.dart';
import 'package:flutter_secure_storage/flutter_secure_storage.dart';

import 'package:wereda_connect/core/models/delivery.dart';

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
/// delivery, and drops them once they cannot be needed: the delivery is
/// done ([forget]) or the account signed out ([forgetAll]).
abstract interface class DropoffHandoffStore {
  Future<KeptHandoff?> read(String userId, String deliveryId);

  Future<void> keep(String userId, String deliveryId, KeptHandoff handoff);

  /// Drop [userId]'s handoffs of [deliveryIds] (delivered, cancelled or
  /// disputed: no buyer scans them any more).
  Future<void> forget(String userId, Iterable<String> deliveryIds);

  /// Drop every kept handoff on this device (sign out).
  Future<void> forgetAll();
}

/// Whether a delivery's handoff can be dropped: it left the road for good.
bool handoffNoLongerNeeded(DeliveryStatus status) =>
    status == DeliveryStatus.delivered ||
    status == DeliveryStatus.cancelled ||
    status == DeliveryStatus.disputed;

/// The app's store: one encrypted entry per trucker in secure storage (the
/// Android keystore; on web the browser's storage, as the QR signing key),
/// the newest [maxDeliveries] kept. Never in memory only: the drop-off can be
/// days after the pickup and the app restarts in between. It never logs what
/// it holds.
class SecureDropoffHandoffStore implements DropoffHandoffStore {
  SecureDropoffHandoffStore({FlutterSecureStorage? storage})
      : _storage = storage ??
            const FlutterSecureStorage(
              aOptions: AndroidOptions(encryptedSharedPreferences: true),
            );

  final FlutterSecureStorage _storage;

  /// A truck carries a handful of orders; older ones are long delivered.
  static const maxDeliveries = 20;

  static const _keyPrefix = 'dropoff_handoffs_';

  static String _key(String userId) => '$_keyPrefix$userId';

  /// [userId]'s entries. Throws when the storage cannot be read (the caller
  /// must then write nothing, or it would wipe the other entries); an entry
  /// that is not JSON holds nothing usable and reads as empty.
  Future<Map<String, Object?>> _load(String userId) async {
    final raw = await _storage.read(key: _key(userId));
    if (raw == null) return {};
    Object? decoded;
    try {
      decoded = jsonDecode(raw);
    } on FormatException {
      decoded = null;
    }
    return decoded is Map<String, Object?> ? decoded : {};
  }

  @override
  Future<KeptHandoff?> read(String userId, String deliveryId) async {
    try {
      return KeptHandoff.fromJson((await _load(userId))[deliveryId]);
    } on Object {
      debugPrint('[Delivery] kept handoff unreadable');
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
    final Map<String, Object?> all;
    try {
      all = await _load(userId);
    } on Object {
      // Writing now would replace the other kept handoffs with this one.
      debugPrint('[Delivery] handoff not kept: storage unreadable');
      return;
    }
    // Re-inserted last, so the oldest entries are the first ones.
    all
      ..remove(deliveryId)
      ..[deliveryId] = handoff.toJson();
    while (all.length > maxDeliveries) {
      all.remove(all.keys.first);
    }
    await _write(userId, all);
  }

  @override
  Future<void> forget(String userId, Iterable<String> deliveryIds) async {
    final ids = deliveryIds.toSet();
    if (ids.isEmpty) return;
    final Map<String, Object?> all;
    try {
      all = await _load(userId);
    } on Object {
      debugPrint('[Delivery] kept handoffs not cleared: storage unreadable');
      return;
    }
    final before = all.length;
    all.removeWhere((id, _) => ids.contains(id));
    if (all.length == before) return;
    await _write(userId, all);
  }

  @override
  Future<void> forgetAll() async {
    try {
      final keys = (await _storage.readAll())
          .keys
          .where((k) => k.startsWith(_keyPrefix))
          .toList();
      for (final key in keys) {
        await _storage.delete(key: key);
      }
    } on Object {
      debugPrint('[Delivery] kept handoffs not cleared');
    }
  }

  Future<void> _write(String userId, Map<String, Object?> all) async {
    try {
      if (all.isEmpty) {
        await _storage.delete(key: _key(userId));
      } else {
        await _storage.write(key: _key(userId), value: jsonEncode(all));
      }
    } on Object {
      debugPrint('[Delivery] kept handoffs not saved');
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

  @override
  Future<void> forget(String userId, Iterable<String> deliveryIds) async {
    for (final id in deliveryIds) {
      _kept.remove('$userId/$id');
    }
  }

  @override
  Future<void> forgetAll() async => _kept.clear();
}
