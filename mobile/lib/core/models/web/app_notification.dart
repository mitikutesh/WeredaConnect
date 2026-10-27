/// Web-safe AppNotification model (no Isar dependency). Mirrors
/// [core/database/schemas/app_notification.dart]; web keeps the inbox in
/// memory for the session and the read marker in shared preferences.
class AppNotification {
  int id = 0;

  late String notificationId;
  late String userId;
  late String type;
  late String entity;
  late String entityId;
  String? orderId;
  late String params;
  late DateTime createdAt;
  bool read = false;
}
