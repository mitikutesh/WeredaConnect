/// Web-safe Payment model (no Isar dependency).
class Payment {
  int id = 0;

  late String paymentId;
  late String deliveryId;
  late String farmerId;
  late String buyerId;
  late double amount;
  late double commission;
  late double commissionRate;
  late double farmerPayout;
  String? telebirrRef;
  late PaymentStatus status;
  late DateTime createdAt;
  late DateTime updatedAt;
  DateTime? expiresAt;
  DateTime? disputeWindowEndsAt;
  DateTime? releasedAt;
  DateTime? refundedAt;
  DateTime? completedAt;
  String? disputeReason;
  String? disputeDescription;
  late int version;
}

/// Status of a payment in its lifecycle.
enum PaymentStatus {
  initiated,
  escrowed,
  released,
  completed,
  cancelled,
  disputed,
  refunded,
}
