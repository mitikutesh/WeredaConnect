import 'package:wereda_connect/core/models/buyer_intent.dart';

/// SIM-56: the buyer's own requests as `GET /intents` returns them
/// (push-alerts-api.md 1.5.0). Shared by the Isar and the web repository so
/// both read the server's rows alike.
abstract final class IntentWire {
  /// The read: the caller's active requests, keyed by the token, never a
  /// parameter.
  static const String path = '/intents';

  /// The requests in a `GET /intents` body, each owned by [buyerId] (the
  /// server returns only the caller's rows and does not repeat the id). A
  /// row without an id or a crop is skipped; a body of another shape is no
  /// rows. The server-only rows come back active with no match count yet:
  /// the matcher fills it in.
  static List<BuyerIntent> parseList(Object? body, {required String buyerId}) {
    if (body is! Map) return const [];
    final rows = body['intents'];
    if (rows is! List) return const [];
    return [
      for (final row in rows)
        if (row is Map) _parse(row, buyerId),
    ].whereType<BuyerIntent>().toList();
  }

  static BuyerIntent? _parse(Map<dynamic, dynamic> row, String buyerId) {
    final intentId = row['intentId'];
    final cropType = row['cropType'];
    if (intentId is! String || intentId.isEmpty) return null;
    if (cropType is! String || cropType.isEmpty) return null;
    final createdAt = _date(row['createdAt']) ?? DateTime.now().toUtc();
    return BuyerIntent()
      ..intentId = intentId
      ..buyerId = buyerId
      ..cropType = cropType
      ..quantityNeeded = _number(row['quantityNeeded'])
      ..maxPricePerKg = _number(row['maxPricePerKg'])
      ..deliveryWoreda = row['deliveryWoreda'] as String? ?? ''
      ..neededByDate = _date(row['neededByDate'])
      ..status = BuyerIntentStatus.active
      ..createdAt = createdAt
      ..updatedAt = _date(row['updatedAt']) ?? createdAt
      ..matchCount = 0
      ..version = (row['version'] as num?)?.toInt() ?? 1;
  }

  static double _number(Object? value) => switch (value) {
        final num n => n.toDouble(),
        final String s => double.tryParse(s) ?? 0,
        _ => 0,
      };

  static DateTime? _date(Object? value) =>
      value is String ? DateTime.tryParse(value)?.toUtc() : null;
}
