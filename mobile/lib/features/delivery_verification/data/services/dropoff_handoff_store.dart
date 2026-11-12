import 'dart:convert';

import 'package:flutter/foundation.dart';
import 'package:flutter_secure_storage/flutter_secure_storage.dart';

import 'package:wereda_connect/core/models/delivery.dart';

/// What the trucker's phone keeps for the drop-off.
///
/// Since ESC-35 it is the trucker's own drop-off code: a QR token with its
/// [nonce] (registered with the server, which checks the buyer's confirm
/// against it) and its typed code. Before, it was the farmer's QR token as
/// scanned at pickup, or only the code typed at the farm (usability audit
/// T-3, one nonce for both legs); a phone may still hold one of those for a
/// delivery picked up on an older version, and it still works.
@immutable
class KeptHandoff {
  const KeptHandoff({this.token, this.code, this.nonce});

  /// The QR token shown as the drop-off QR; null when only a code was kept
  /// (a pickup typed at the farm before ESC-35).
  final String? token;

  /// The nine-digit typed code of the same nonce.
  final String? code;

  /// The trucker's own drop-off nonce (ESC-35); null for the farmer's
  /// token kept before.
  final String? nonce;

  /// Whether this is the trucker's own drop-off code.
  bool get isTruckers => nonce != null && nonce!.isNotEmpty;

  bool get isEmpty =>
      (token == null || token!.isEmpty) && (code == null || code!.isEmpty);

  Map<String, Object> toJson() => {
        if (token != null) 't': token!,
        if (code != null) 'c': code!,
        if (nonce != null) 'n': nonce!,
      };

  static KeptHandoff? fromJson(Object? json) {
    if (json is! Map) return null;
    final token = json['t'];
    final code = json['c'];
    final nonce = json['n'];
    final kept = KeptHandoff(
      token: token is String ? token : null,
      code: code is String ? code : null,
      nonce: nonce is String ? nonce : null,
    );
    return kept.isEmpty ? null : kept;
  }
}

/// Keeps a trucker's [KeptHandoff]s across restarts, per account and
/// delivery, and drops them once they cannot be needed: the delivery is
/// done ([forget]) or the user signed out on purpose ([forgetUser]).
abstract interface class DropoffHandoffStore {
  Future<KeptHandoff?> read(String userId, String deliveryId);

  Future<void> keep(String userId, String deliveryId, KeptHandoff handoff);

  /// Drop [userId]'s handoffs of [deliveryIds] (delivered, cancelled or
  /// disputed: no buyer scans them any more).
  Future<void> forget(String userId, Iterable<String> deliveryIds);

  /// Drop [userId]'s kept handoffs, and only theirs: another trucker on a
  /// shared phone keeps their drop-off codes (an explicit sign out or a
  /// deleted account, never a lapsed session: `signOutForgetter`).
  Future<void> forgetUser(String userId);
}

/// Whether a delivery's handoff can be dropped: it left the road for good.
bool handoffNoLongerNeeded(DeliveryStatus status) =>
    status == DeliveryStatus.delivered ||
    status == DeliveryStatus.cancelled ||
    status == DeliveryStatus.disputed;

/// The app's store: one encrypted secure-storage entry per trucker and
/// delivery (the Android keystore; on web the browser's storage, as the QR
/// signing key). Never in memory only: the drop-off can be days after the
/// pickup and the app restarts in between.
///
/// One key per delivery, so keeping one handoff and dropping another never
/// read and rewrite a shared value: the scan screen, trip mode and the
/// check-in engine each use their own store, at the same time. Each trucker
/// keeps entries younger than [maxAge] (an older one's nonce has expired on
/// the server), the newest [maxDeliveries] at most. It never logs what it
/// holds: a failure logs the error's type only.
class SecureDropoffHandoffStore implements DropoffHandoffStore {
  SecureDropoffHandoffStore({
    FlutterSecureStorage? storage,
    DateTime Function()? now,
  })  : _storage = storage ??
            const FlutterSecureStorage(
              aOptions: AndroidOptions(encryptedSharedPreferences: true),
            ),
        _now = now ?? DateTime.now;

  final FlutterSecureStorage _storage;
  final DateTime Function() _now;

  /// A truck carries a handful of orders; older ones are long delivered.
  static const maxDeliveries = 20;

  /// The server takes a QR nonce for 14 days from when it was stored (the
  /// farmer's first QR, or the trucker's drop-off code; ESC-35,
  /// `delivery-verification-api.md` QR Secret Storage): a handoff kept
  /// longer can no longer confirm anything.
  static const maxAge = Duration(days: 14);

  static const _keyPrefix = 'dropoff_handoff|';

  /// The ids are percent-encoded, so neither can hold the `|` separator and
  /// one user's prefix never matches another user's keys.
  static String _userPrefix(String userId) =>
      '$_keyPrefix${Uri.encodeComponent(userId)}|';

  static String _key(String userId, String deliveryId) =>
      '${_userPrefix(userId)}${Uri.encodeComponent(deliveryId)}';

  @override
  Future<KeptHandoff?> read(String userId, String deliveryId) async {
    try {
      final raw = await _storage.read(key: _key(userId, deliveryId));
      return raw == null ? null : KeptHandoff.fromJson(jsonDecode(raw));
    } on Object catch (e) {
      debugPrint('[Delivery] kept handoff unreadable: ${e.runtimeType}');
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
      await _storage.write(
        key: _key(userId, deliveryId),
        value: jsonEncode({
          ...handoff.toJson(),
          'at': _now().millisecondsSinceEpoch,
        }),
      );
    } on Object catch (e) {
      debugPrint('[Delivery] handoff not kept: ${e.runtimeType}');
      return;
    }
    await _trim(userId);
  }

  /// Drop the trucker's entries older than [maxAge], then the oldest past
  /// [maxDeliveries]. Only entries it read are dropped, so a storage it
  /// cannot read loses nothing.
  Future<void> _trim(String userId) async {
    final Map<String, String> all;
    try {
      all = await _storage.readAll();
    } on Object catch (e) {
      debugPrint('[Delivery] kept handoffs not counted: ${e.runtimeType}');
      return;
    }
    final prefix = _userPrefix(userId);
    final oldest = _now().subtract(maxAge).millisecondsSinceEpoch;
    final mine = [
      for (final e in all.entries)
        if (e.key.startsWith(prefix)) (key: e.key, at: _keptAt(e.value)),
    ];
    final live = <({String key, int at})>[];
    for (final entry in mine) {
      if (entry.at < oldest) {
        await _delete(entry.key);
      } else {
        live.add(entry);
      }
    }
    if (live.length <= maxDeliveries) return;
    live.sort((a, b) => a.at.compareTo(b.at));
    for (final old in live.take(live.length - maxDeliveries)) {
      await _delete(old.key);
    }
  }

  /// When an entry was kept; an unreadable entry counts as the oldest (and
  /// past [maxAge]).
  static int _keptAt(String raw) {
    try {
      final at = (jsonDecode(raw) as Map)['at'];
      return at is int ? at : 0;
    } on Object {
      return 0;
    }
  }

  @override
  Future<void> forget(String userId, Iterable<String> deliveryIds) async {
    for (final id in deliveryIds.toSet()) {
      await _delete(_key(userId, id));
    }
  }

  @override
  Future<void> forgetUser(String userId) async {
    final Map<String, String> all;
    try {
      all = await _storage.readAll();
    } on Object catch (e) {
      debugPrint('[Delivery] kept handoffs not cleared: ${e.runtimeType}');
      return;
    }
    // A copy of the keys: a storage may hand back its own live map.
    final prefix = _userPrefix(userId);
    final keys = all.keys.where((k) => k.startsWith(prefix)).toList();
    for (final key in keys) {
      await _delete(key);
    }
  }

  Future<void> _delete(String key) async {
    try {
      await _storage.delete(key: key);
    } on Object catch (e) {
      debugPrint('[Delivery] kept handoff not cleared: ${e.runtimeType}');
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
  Future<void> forgetUser(String userId) async =>
      _kept.removeWhere((key, _) => key.startsWith('$userId/'));
}
