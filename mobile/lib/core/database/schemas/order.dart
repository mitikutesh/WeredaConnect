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
}

/// Status of an order in its lifecycle
enum OrderStatus {
  /// Order placed by buyer, waiting for farmer response
  pending,

  /// Farmer accepted the order
  accepted,

  /// Farmer rejected the order
  rejected,

  /// Buyer cancelled the order
  cancelled,

  /// Order fulfilled (delivery completed)
  completed,
}
