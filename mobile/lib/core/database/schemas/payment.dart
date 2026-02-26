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

  /// Net amount farmer receives: amount - commission
  late double farmerPayout;

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
