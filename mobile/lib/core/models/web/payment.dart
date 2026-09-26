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
  double? truckerPayout;
  String? truckerId;
  String? orderId;
  bool refundPending = false;
  String? refundReason;
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

  /// ETB this party takes home from the held [amount]: the buyer sees the full
  /// held amount, the farmer the crop leg, the trucker the transport leg (0 when
  /// the order carried no trucker split). [role] is a `UserRole.name`.
  double payoutFor(String role) => switch (role) {
        'farmer' => farmerPayout,
        'trucker' => truckerPayout ?? 0,
        _ => amount,
      };

  /// Refund on its way: the server flag, or (older servers) an escrowed hold
  /// whose expiry has passed, which the refund sweep returns to the buyer.
  bool get isRefundPending =>
      refundPending ||
      (status == PaymentStatus.escrowed &&
          expiresAt != null &&
          expiresAt!.isBefore(DateTime.now()));
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
