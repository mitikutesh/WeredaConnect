import 'dart:async';

import 'package:shared_preferences/shared_preferences.dart';

import 'package:wereda_connect/core/models/app_notification.dart';
import 'package:wereda_connect/core/network/api_client.dart';
import 'package:wereda_connect/features/notifications/data/notification_api.dart';
import 'package:wereda_connect/features/notifications/domain/base_notification_repository.dart';

/// ESC-21: the inbox on web. No Isar: the entries live for the session (the
/// first pull of a session reads the newest page), and the read marker, the
/// newest entry time the user has seen, is kept in shared preferences. An
/// entry an incremental pull brings after the bell was opened is unseen
/// whatever its time: rows reach the inbox out of event order.
class WebNotificationRepository extends BaseNotificationRepository {
  WebNotificationRepository({required ApiClient api})
      : _api = NotificationApi(api);

  final NotificationApi _api;
  final _entries = <String, Map<String, AppNotification>>{};
  final _cursors = <String, String>{};

  /// Ids an incremental pull added since the user last opened the bell.
  final _arrivedSinceRead = <String, Set<String>>{};
  final _changes = StreamController<void>.broadcast();

  static String readMarkerKey(String userId) =>
      'notifications_read_through_$userId';

  @override
  Stream<void> get changes => _changes.stream;

  /// When each user's last pull started.
  final _lastPull = <String, DateTime>{};

  @override
  Future<void> pull(String userId, {bool force = false}) async {
    if (userId.isEmpty) return;
    final now = DateTime.now();
    final last = _lastPull[userId];
    if (!force &&
        last != null &&
        now.difference(last) < BaseNotificationRepository.minPullInterval) {
      return;
    }
    _lastPull[userId] = now;
    final since = _cursors[userId];
    final ({List<Map<String, dynamic>> rows, String? cursor}) result;
    try {
      result = await _api.fetch(since: since);
    } on Object {
      // A failed pull does not hold the next one back.
      _lastPull.remove(userId);
      rethrow;
    }
    final held = _entries.putIfAbsent(userId, () => {});
    final arrived = _arrivedSinceRead.putIfAbsent(userId, () => {});
    var added = 0;
    for (final row in result.rows) {
      final entry = parseNotification(row, userId);
      if (entry == null || held.containsKey(entry.notificationId)) continue;
      held[entry.notificationId] = entry;
      if (since != null) arrived.add(entry.notificationId);
      added++;
    }
    final cursor = result.cursor;
    if (cursor != null) _cursors[userId] = cursor;
    if (added > 0) _changes.add(null);
  }

  @override
  Future<List<AppNotification>> recent(String userId, {int limit = 50}) async {
    final marker = await _readThrough(userId);
    final list = (_entries[userId]?.values.toList() ?? <AppNotification>[])
      ..sort((a, b) => b.createdAt.compareTo(a.createdAt));
    return [
      for (final entry in list.take(limit))
        entry..read = !_isUnread(userId, entry, marker),
    ];
  }

  @override
  Future<int> unreadCount(String userId) async {
    final marker = await _readThrough(userId);
    return _entries[userId]
            ?.values
            .where((e) => _isUnread(userId, e, marker))
            .length ??
        0;
  }

  bool _isUnread(String userId, AppNotification entry, DateTime? marker) =>
      marker == null ||
      entry.createdAt.isAfter(marker) ||
      (_arrivedSinceRead[userId]?.contains(entry.notificationId) ?? false);

  @override
  Future<void> markAllRead(String userId) async {
    final entries = _entries[userId]?.values;
    if (entries == null || entries.isEmpty) return;
    final newest =
        entries.map((e) => e.createdAt).reduce((a, b) => a.isAfter(b) ? a : b);
    final prefs = await SharedPreferences.getInstance();
    await prefs.setString(readMarkerKey(userId), newest.toIso8601String());
    _arrivedSinceRead[userId]?.clear();
    _changes.add(null);
  }

  Future<DateTime?> _readThrough(String userId) async {
    final prefs = await SharedPreferences.getInstance();
    return DateTime.tryParse(prefs.getString(readMarkerKey(userId)) ?? '');
  }
}
