/// Web-safe PaymentIntent model (no Isar dependency).
class PaymentIntent {
  int id = 0;

  late String idempotencyKey;
  late String deliveryId;
  late String buyerId;
  late String farmerId;
  late double amount;
  late IntentStatus status;
  late DateTime createdAt;
  int retryCount = 0;
  DateTime? lastAttemptAt;
  String? lastError;
}

/// Sync status of a payment intent in the offline queue.
enum IntentStatus {
  pendingSync,
  syncing,
  completed,
  failed,
  cancelled,
}
