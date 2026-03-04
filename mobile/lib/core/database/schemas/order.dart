import 'package:isar/isar.dart';

part 'order.g.dart';

/// Represents a purchase order from a buyer to a farmer.
/// Tracks the lifecycle of a trade request before it becomes a delivery.
///
/// Ethiopian context:
/// - Buyers browse listings and place orders
/// - Farmers review and accept/reject orders
/// - Accepted orders progress to delivery scheduling
@collection
class Order {
  /// Isar auto-generated ID (internal use only)
  Id id = Isar.autoIncrement;

  /// Server-assigned order ID (UUID)
  @Index(unique: true)
  late String orderId;

  /// Crop listing this order is for
  @Index()
  late String listingId;

  /// Buyer who placed the order
  @Index()
  late String buyerId;

  /// Farmer who owns the listing
  @Index()
  late String farmerId;

  /// Buyer's display name
  late String buyerName;

  /// Farmer's display name
  late String farmerName;

  /// Type of crop being ordered
  late String cropType;

  /// Quantity requested (in kg)
  late double quantity;

  /// Price per unit (ETB/kg)
  late double pricePerUnit;

  /// Total price (quantity * pricePerUnit)
  late double totalPrice;

  /// Current status of the order
  @Index()
  @Enumerated(EnumType.name)
  late OrderStatus status;

  /// When this order was created (UTC)
  late DateTime createdAt;

  /// When the farmer responded (accepted/rejected) (UTC)
  DateTime? respondedAt;

  /// Reason for rejection, if status is rejected
  String? rejectionReason;

  /// Version number for optimistic concurrency control
  late int version;

  /// Trip assigned for delivery
  @Index()
  String? tripId;

  /// Trucker assigned for delivery
  String? truckerId;

  /// Trucker's display name
  String? truckerName;

  /// Cost of the crop (quantity * pricePerUnit)
  double? cropCost;

  /// Transport cost from the assigned trip
  double? transportCost;

  /// Platform commission rate (e.g. 0.05 for 5%)
  double? commissionRate;

  /// Commission amount (cropCost * commissionRate)
  double? commissionAmount;

  /// Full order total (cropCost + transportCost + commissionAmount)
  double? orderTotal;

  /// Payment method (e.g. "cbe_transfer")
  String? paymentMethod;

  /// CBE transaction reference entered by buyer
  String? cbeTransactionId;

  /// When payment was verified (UTC)
  DateTime? paymentVerifiedAt;

  /// When the order was cancelled (UTC)
  DateTime? cancelledAt;

  /// Reason for cancellation
  String? cancelReason;

  /// When the order was last updated (UTC)
  DateTime? updatedAt;
}

/// Status of an order in its lifecycle
enum OrderStatus {
  /// Order placed by buyer, waiting for farmer response
  created,

  /// Awaiting buyer payment
  pendingPayment,

  /// Buyer submitted payment proof
  paymentSubmitted,

  /// Payment verified and funds held
  funded,

  /// Farmer accepted the order
  accepted,

  /// Farmer rejected the order
  rejected,

  /// Order cancelled by buyer or farmer
  cancelled,

  /// Trucker confirmed pickup from farmer
  pickupConfirmed,

  /// Goods in transit
  inTransit,

  /// Goods delivered to buyer
  delivered,

  /// Platform is disbursing funds
  disbursing,

  /// All parties paid, order complete
  settled,

  /// Payment verification failed
  paymentFailed,

  /// Order is under dispute
  disputed,

  /// Dispute has been resolved
  disputeResolved,
}
