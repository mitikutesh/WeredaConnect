import 'package:isar/isar.dart';

part 'app_notification.g.dart';

/// ESC-21: one entry of the user's inbox (`GET /notifications`,
/// notifications-api.md 1.0.0), kept on the device so the history shows
/// offline. The text is not stored: it is rendered from [type] and [params]
/// in the app's language (`notificationText`), so a language switch
/// re-renders the whole list. Read state is local only (the server has no
/// write endpoint).
@collection
class AppNotification {
  Id id = Isar.autoIncrement;

  /// The server's stable id (`{createdAt}#{type}#{entityId}`). Unique per
  /// user: the buyer and the farmer of one order get entries with the same id.
  @Index(unique: true, replace: true, composite: [CompositeIndex('userId')])
  late String notificationId;

  /// The signed-in user the entry belongs to.
  @Index()
  late String userId;

  /// One of the server's notification types (`order_request`, ...).
  late String type;

  /// `order`, `delivery` or `escrow`: the table the change came from.
  late String entity;

  late String entityId;

  /// The order to open on tap, when known.
  String? orderId;

  /// The text parameters as a JSON object of strings (names, kg, ETB, ISO
  /// deadline, reason code). Never a phone number.
  late String params;

  /// When the change happened (UTC).
  @Index()
  late DateTime createdAt;

  /// Whether the user has opened the bell since the entry arrived.
  bool read = false;
}
