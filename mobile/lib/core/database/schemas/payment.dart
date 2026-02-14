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

  /// Server-assigned payment ID (UUID)
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
  /// Calculated as: (quantity * pricePerKg) + (quantity * pricePerQuintal / 100)
  late double amount;

  /// Telebirr transaction reference
  /// Null until payment is initiated with Telebirr API
  String? telebirrRef;

  /// Current status of the payment
  @Index()
  @Enumerated(EnumType.name)
  late PaymentStatus status;

  /// Timestamp when payment was created (UTC)
  late DateTime createdAt;

  /// Version number for optimistic concurrency control
  late int version;
}

/// Status of a payment in its lifecycle
enum PaymentStatus {
  /// Payment held in escrow (buyer deposited, farmer not paid yet)
  escrowed,

  /// Payment released to farmer (delivery confirmed)
  released,

  /// Payment disputed (quality/quantity issue)
  disputed,

  /// Payment refunded to buyer (delivery failed/cancelled)
  refunded,
}
