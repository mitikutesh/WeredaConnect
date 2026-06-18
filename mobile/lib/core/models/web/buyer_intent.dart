/// Web-safe BuyerIntent model (no Isar dependency).
class BuyerIntent {
  int id = 0;

  late String intentId;
  late String buyerId;
  late String cropType;
  late double quantityNeeded;
  late double maxPricePerKg;
  late String deliveryWoreda;
  late BuyerIntentStatus status;
  late DateTime createdAt;
  late DateTime updatedAt;
  int matchCount = 0;
  DateTime? lastMatchedAt;
  DateTime? lastViewedAt;
  int lastSeenMatchCount = 0;
  late int version;
}

/// Status of a buyer intent in its lifecycle.
enum BuyerIntentStatus {
  active,
  fulfilled,
  expired,
}
