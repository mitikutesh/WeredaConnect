import 'dart:convert';

import 'package:flutter/foundation.dart';
import 'package:shared_preferences/shared_preferences.dart';

import 'package:wereda_connect/features/live_trip/domain/models/delivery_track.dart';
import 'package:wereda_connect/features/live_trip/domain/models/track_result.dart';

/// The last good track answer per delivery, for offline display (LT-10).
///
/// Kept in the app's key-value store (SharedPreferences: app-private storage
/// on Android, localStorage on web), so mobile and web behave the same and
/// no Isar schema is needed for a read-only view. One entry per user and
/// delivery, `live_track_v1:{userId}:{deliveryId}` holding the raw response
/// body and when it was fetched, so a newer server field survives a reload.
/// At most [maxPerUser] entries per user (the oldest fetch goes first), and
/// logout and account deletion remove the user's entries
/// ([forgetStoredUser]), like the other per-user data on a shared phone.
class TrackSnapshotStore {
  TrackSnapshotStore({Future<SharedPreferences> Function()? preferences})
      : _preferences = preferences ?? SharedPreferences.getInstance;

  final Future<SharedPreferences> Function() _preferences;

  static const String keyPrefix = 'live_track_v1:';

  /// Most snapshots kept per user. A buyer follows a few deliveries at once.
  static const int maxPerUser = 20;

  static String _userPrefix(String userId) => '$keyPrefix$userId:';

  static String _key(String userId, String deliveryId) =>
      '${_userPrefix(userId)}$deliveryId';

  /// The stored snapshot of [deliveryId] for [userId], or null. An entry that
  /// no longer parses is removed.
  Future<TrackSnapshot?> read(String userId, String deliveryId) async {
    final prefs = await _preferences();
    final key = _key(userId, deliveryId);
    final raw = prefs.getString(key);
    if (raw == null) return null;
    final snapshot = _decode(raw);
    if (snapshot == null) await prefs.remove(key);
    return snapshot;
  }

  /// Stores [body] (the response as received) fetched at [fetchedAt].
  Future<void> write(
    String userId,
    String deliveryId,
    Map<String, dynamic> body,
    DateTime fetchedAt,
  ) async {
    final prefs = await _preferences();
    await prefs.setString(
      _key(userId, deliveryId),
      jsonEncode({
        'fetchedAt': fetchedAt.toUtc().toIso8601String(),
        'track': body,
      }),
    );
    await _trim(prefs, userId);
  }

  /// Removes every snapshot of [userId] (logout, account deleted).
  Future<void> forgetUser(String userId) async {
    final prefs = await _preferences();
    final prefix = _userPrefix(userId);
    for (final key in prefs.getKeys().toList()) {
      if (key.startsWith(prefix)) await prefs.remove(key);
    }
  }

  /// [forgetUser] on the app's own store: the default the AuthBloc calls.
  static Future<void> forgetStoredUser(String userId) =>
      TrackSnapshotStore().forgetUser(userId);

  Future<void> _trim(SharedPreferences prefs, String userId) async {
    final prefix = _userPrefix(userId);
    final keys = prefs.getKeys().where((k) => k.startsWith(prefix)).toList();
    if (keys.length <= maxPerUser) return;
    final fetched = <String, DateTime>{
      for (final key in keys) key: _fetchedAt(prefs.getString(key)),
    };
    keys.sort((a, b) => fetched[a]!.compareTo(fetched[b]!));
    for (final key in keys.take(keys.length - maxPerUser)) {
      await prefs.remove(key);
    }
  }

  static DateTime _fetchedAt(String? raw) {
    if (raw != null) {
      try {
        final decoded = jsonDecode(raw);
        if (decoded is Map && decoded['fetchedAt'] is String) {
          final at = DateTime.tryParse(decoded['fetchedAt'] as String);
          if (at != null) return at.toUtc();
        }
      } on FormatException {
        // An unreadable entry sorts first, so it goes first.
      }
    }
    return DateTime.fromMillisecondsSinceEpoch(0, isUtc: true);
  }

  static TrackSnapshot? _decode(String raw) {
    try {
      final decoded = jsonDecode(raw);
      if (decoded is! Map) return null;
      final fetchedAt = DateTime.tryParse('${decoded['fetchedAt']}');
      final track = decoded['track'];
      if (fetchedAt == null || track is! Map<String, dynamic>) return null;
      return TrackSnapshot(
        track: DeliveryTrack.fromJson(track),
        fetchedAt: fetchedAt.toUtc(),
      );
    } on FormatException catch (e) {
      debugPrint('[TrackSnapshotStore] dropping an unreadable snapshot: $e');
      return null;
    }
  }
}
