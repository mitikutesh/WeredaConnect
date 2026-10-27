import 'package:wereda_connect/core/models/app_notification.dart';

/// ESC-21: the user's inbox of order, delivery and money events
/// (notifications-api.md 1.0.0). Mobile keeps it in Isar so it shows offline;
/// web keeps it for the session. Both pull `GET /notifications` with the
/// cursor of their last pull and drop ids they already have.
abstract class BaseNotificationRepository {
  /// Shortest gap between two pulls of one user unless [pull] is forced:
  /// Home rebuilds the bell on every sync result, and each pull costs data.
  static const minPullInterval = Duration(seconds: 30);

  /// Pulls the entries written since the last pull. Within
  /// [minPullInterval] of the last one it does nothing unless [force] (a
  /// push said something changed). Network and server errors propagate; a
  /// background refresh catches them.
  Future<void> pull(String userId, {bool force = false});

  /// The newest entries of [userId], newest change first.
  Future<List<AppNotification>> recent(String userId, {int limit = 50});

  /// How many entries [userId] has not seen in the bell yet.
  Future<int> unreadCount(String userId);

  /// Marks every entry of [userId] as seen.
  Future<void> markAllRead(String userId);

  /// Fires after a pull added entries or the read state changed.
  Stream<void> get changes;
}
