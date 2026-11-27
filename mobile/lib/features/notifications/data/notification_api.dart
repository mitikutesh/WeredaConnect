import 'dart:convert';

import 'package:wereda_connect/core/models/app_notification.dart';
import 'package:wereda_connect/core/network/api_client.dart';

/// ESC-21: reads `GET /notifications` (notifications-api.md section 1) for
/// both inbox repositories.
class NotificationApi {
  NotificationApi(this._api);

  final ApiClient _api;

  /// Entries per page (the server allows 1 to 50).
  static const pageLimit = 50;

  /// Pages one incremental pull follows. With `since` the server sends the
  /// newest writes first, so entries beyond these pages (a phone offline for
  /// days) are left to history, as on a first pull: re-reading from the old
  /// cursor would fetch the same newest pages forever.
  static const maxPages = 5;

  /// The entries written after [since] (the `cursor` of an earlier pull),
  /// or the newest page when [since] is null, with the cursor to store: the
  /// first page's.
  Future<({List<Map<String, dynamic>> rows, String? cursor})> fetch({
    String? since,
  }) async {
    final rows = <Map<String, dynamic>>[];
    String? cursor;
    String? nextToken;
    for (var page = 0; page < maxPages; page++) {
      final data = await _api.get<Map<String, dynamic>>(
        '/notifications',
        queryParameters: {
          'limit': '$pageLimit',
          if (since != null) 'since': since,
          if (nextToken != null) 'nextToken': nextToken,
        },
      );
      cursor ??= data['cursor'] as String?;
      final items = data['notifications'];
      if (items is List) {
        rows.addAll(items.whereType<Map<String, dynamic>>());
      }
      nextToken = data['nextToken'] as String?;
      // A first pull reads only the newest page; older entries are history.
      if (nextToken == null || since == null) break;
    }
    return (rows: rows, cursor: cursor ?? since);
  }
}

/// One server inbox row as an [AppNotification] of [userId], or null when a
/// required field is missing.
AppNotification? parseNotification(Map<String, dynamic> m, String userId) {
  final id = m['notificationId'];
  final type = m['type'];
  final createdAt = DateTime.tryParse(m['createdAt'] as String? ?? '');
  if (id is! String || id.isEmpty || type is! String || createdAt == null) {
    return null;
  }
  final raw = m['params'];
  final params = <String, String>{
    if (raw is Map)
      for (final entry in raw.entries)
        if (entry.value != null) '${entry.key}': '${entry.value}',
  };
  final orderId = m['orderId'];
  return AppNotification()
    ..notificationId = id
    ..userId = userId
    ..type = type
    ..entity = m['entity'] as String? ?? ''
    ..entityId = m['entityId'] as String? ?? ''
    ..orderId = orderId is String && orderId.isNotEmpty ? orderId : null
    ..params = jsonEncode(params)
    ..createdAt = createdAt.toUtc();
}

/// The text parameters of [notification]; empty when unreadable.
Map<String, String> notificationParams(AppNotification notification) {
  try {
    final decoded = jsonDecode(notification.params);
    if (decoded is Map) {
      return {
        for (final entry in decoded.entries) '${entry.key}': '${entry.value}',
      };
    }
  } on FormatException {
    // Fall through.
  }
  return const {};
}
