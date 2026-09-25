import 'package:isar/isar.dart';

part 'payment.g.dart';

/// Represents a payment transaction in the escrow system.
/// Uses Telebirr (Ethiopian mobile money) for settlements.
///
/// Ethiopian context:
/// - Telebirr is the dominant mobile money platform in Ethiopia
/// - Escrow protects farmers from non-payment (common rural issue)
/// - Amount in ETB (Ethiopian Birr)
@collection
class Payment {
  /// Isar auto-generated ID (internal use only)
  Id id = Isar.autoIncrement;

  /// Server-assigned payment ID (maps to escrowId on server)
  @Index(unique: true)
  late String paymentId;

  /// Delivery ID this payment is for
  @Index()
  late String deliveryId;

  /// User ID of the farmer receiving payment
  late String farmerId;

  /// User ID of the buyer making payment
  late String buyerId;

  /// Payment amount in Ethiopian Birr (ETB)
  late double amount;

  /// Platform commission in ETB: max(amount * commissionRate, 5.0)
  late double commission;

  /// Commission rate (server-configured, default 0.025)
  late double commissionRate;

  /// Crop leg the farmer receives on release. With a trucker split:
  /// amount = farmerPayout + truckerPayout + commission.
  late double farmerPayout;

  /// Transport leg paid to the trucker out of [amount]; null when the order
  /// carried no trucker split.
  double? truckerPayout;

  /// Trucker paid out of this escrow (the truck the order was bound to).
  String? truckerId;

  /// Backing order; an order-funded escrow carries it before any delivery.
  String? orderId;

  /// Server flag (SIM-20): the money is still held but on its way back to the
  /// buyer (order cancelled/rejected, or the hold expired and the refund sweep
  /// has not run yet). Such a row must never read as "Funds held safely".
  bool refundPending = false;

  /// Why the refund was scheduled, when the server recorded one.
  String? refundReason;

  /// Telebirr transaction reference
  /// Null until payment is initiated with Telebirr API
  String? telebirrRef;

  /// Current status of the payment
  @Index()
  @Enumerated(EnumType.name)
  late PaymentStatus status;

  /// Timestamp when payment was created (UTC)
  late DateTime createdAt;

  /// Timestamp of last server state change (UTC)
  late DateTime updatedAt;

  /// Auto-refund deadline (createdAt + 7 days)
  DateTime? expiresAt;

  /// Dispute deadline (deliveryConfirmedAt + 48 hours)
  DateTime? disputeWindowEndsAt;

  /// When funds were released to farmer
  DateTime? releasedAt;

  /// When funds were refunded to buyer
  DateTime? refundedAt;

  /// When transaction fully settled
  DateTime? completedAt;

  /// Dispute reason enum value (if disputed)
  String? disputeReason;

  /// Dispute free-text description
  String? disputeDescription;

  /// Version number for optimistic concurrency control
  late int version;

  /// ETB this party takes home from the held [amount]: the buyer sees the full
  /// held amount, the farmer the crop leg, the trucker the transport leg (0 when
  /// the order carried no trucker split). [role] is a `UserRole.name`.
  double payoutFor(String role) => switch (role) {
        'farmer' => farmerPayout,
        'trucker' => truckerPayout ?? 0,
        _ => amount,
      };

  @ignore

  /// Refund on its way: the server flag, or (older servers) an escrowed hold
  /// whose expiry has passed, which the refund sweep returns to the buyer.
  bool get isRefundPending =>
      refundPending ||
      (status == PaymentStatus.escrowed &&
          expiresAt != null &&
          expiresAt!.isBefore(DateTime.now()));
}

/// Status of a payment in its lifecycle
enum PaymentStatus {
  /// Payment intent submitted; Telebirr hold pending
  initiated,

  /// Funds held in platform Telebirr account
  escrowed,

  /// Funds released to farmer (minus commission)
  released,

  /// Transaction fully settled
  completed,

  /// Buyer cancelled before funds were held
  cancelled,

  /// Dispute raised within 48h of delivery
  disputed,

  /// Funds returned to buyer
  refunded,
}
