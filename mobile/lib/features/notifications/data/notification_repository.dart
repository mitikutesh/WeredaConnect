import 'dart:async';

import 'package:isar/isar.dart';
import 'package:shared_preferences/shared_preferences.dart';

import 'package:wereda_connect/core/models/app_notification.dart';
import 'package:wereda_connect/core/network/api_client.dart';
import 'package:wereda_connect/features/notifications/data/notification_api.dart';
import 'package:wereda_connect/features/notifications/domain/base_notification_repository.dart';

/// ESC-21: the inbox on mobile, kept in Isar so it shows offline. The cursor
/// of the last pull is kept in shared preferences per user.
class NotificationRepository extends BaseNotificationRepository {
  NotificationRepository({required Isar isar, required ApiClient api})
      : _isar = isar,
        _api = NotificationApi(api);

  final Isar _isar;
  final NotificationApi _api;
  final _changes = StreamController<void>.broadcast();

  /// Entries kept per user on the device; older ones are dropped after a
  /// pull (the server keeps 90 days).
  static const maxKept = 200;

  static String cursorKey(String userId) => 'notifications_cursor_$userId';

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
    try {
      await _pull(userId);
    } on Object {
      // A failed pull does not hold the next one back.
      _lastPull.remove(userId);
      rethrow;
    }
  }

  Future<void> _pull(String userId) async {
    final prefs = await SharedPreferences.getInstance();
    // No entries on the device (first sign-in, or after a logout cleared
    // them): read the newest page instead of only what is new.
    final held =
        await _isar.appNotifications.filter().userIdEqualTo(userId).count();
    final since = held == 0 ? null : prefs.getString(cursorKey(userId));
    final result = await _api.fetch(since: since);

    var added = 0;
    await _isar.writeTxn(() async {
      for (final row in result.rows) {
        final entry = parseNotification(row, userId);
        if (entry == null) continue;
        final existing = await _isar.appNotifications
            .getByNotificationIdUserId(entry.notificationId, userId);
        if (existing != null) continue;
        await _isar.appNotifications.put(entry);
        added++;
      }
      final kept = await _isar.appNotifications
          .filter()
          .userIdEqualTo(userId)
          .sortByCreatedAtDesc()
          .offset(maxKept)
          .idProperty()
          .findAll();
      if (kept.isNotEmpty) await _isar.appNotifications.deleteAll(kept);
    });
    final cursor = result.cursor;
    if (cursor != null) await prefs.setString(cursorKey(userId), cursor);
    if (added > 0) _changes.add(null);
  }

  @override
  Future<List<AppNotification>> recent(String userId, {int limit = 50}) =>
      _isar.appNotifications
          .filter()
          .userIdEqualTo(userId)
          .sortByCreatedAtDesc()
          .limit(limit)
          .findAll();

  @override
  Future<int> unreadCount(String userId) => _isar.appNotifications
      .filter()
      .userIdEqualTo(userId)
      .readEqualTo(false)
      .count();

  @override
  Future<void> markAllRead(String userId) async {
    var changed = false;
    await _isar.writeTxn(() async {
      final unread = await _isar.appNotifications
          .filter()
          .userIdEqualTo(userId)
          .readEqualTo(false)
          .findAll();
      for (final entry in unread) {
        entry.read = true;
      }
      await _isar.appNotifications.putAll(unread);
      changed = unread.isNotEmpty;
    });
    if (changed) _changes.add(null);
  }
}
