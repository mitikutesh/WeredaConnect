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

  /// ESC-06: after this time (UTC) an accepted order nobody picked up counts
  /// as a transport failure and its buyer may cancel it for a full refund.
  /// Stamped by the server when the farmer accepts; null before that.
  DateTime? pickupDeadlineAt;

  /// ESC-48: whether the parties may reach each other on this order (call,
  /// copy, message), as the server computed it on the last read. Null when
  /// unknown (server before order-lifecycle 1.18.0, a local change not yet
  /// pulled): the open status set decides then (`isOrderContactOpen`).
  bool? contactOpen;

  /// ESC-48: the parties' phone numbers (E.164), sent by the server only to a
  /// party of an order whose contact is open; null otherwise.
  String? buyerPhone;
  String? farmerPhone;
  String? truckerPhone;

  /// ESC-15: `trucker` (trip order) or `self_pickup`, stamped by the server
  /// when the money is held; null before (see order_commitment.dart).
  String? fulfillment;

  /// ESC-15: the farmer's answer to the paid order: `pending`, `accepted`,
  /// `declined` or `expired`. Server-written; null on a row it never stamped.
  String? farmerResponse;

  /// ESC-15: the trucker's answer: `none` (self-pickup), `not_asked` (the
  /// farmer has not accepted yet), `pending`, `accepted`, `declined`,
  /// `expired`, `cancelled` or `failed_capacity`. Server-written.
  String? truckerResponse;

  /// ESC-15: the trip's departure on a trip order, stamped at funding (UTC).
  /// Null before funding and on a self-pickup order.
  DateTime? pickupDate;

  /// ESC-15: when the farmer's and the trucker's answers are due (UTC).
  DateTime? farmerDeadlineAt;
  DateTime? truckerDeadlineAt;

  /// ESC-15: in `awaitingTransport`, until when the buyer may decide (UTC).
  DateTime? buyerChoiceDeadlineAt;

  /// ESC-15: why the transport failed (`trucker_declined`, `trucker_expired`,
  /// `trucker_cancelled`, `trip_cancelled`, `trip_full`).
  String? transportFailure;

  /// ESC-15: the decline reason code the farmer or trucker gave.
  String? declineReason;

  /// ESC-15: who ended the order: `buyer`, `farmer`, `trucker` or `system`.
  String? cancelledBy;

  /// The listing's place name at create (order-lifecycle 2.2.0): where the
  /// goods are collected. Server-written; null on an older order or when the
  /// listing had none (the trip's route stands in).
  String? pickupPlace;

  /// The buyer's registered woreda at create (order-lifecycle 2.2.0): where
  /// the goods go. Server-written; null on an older order.
  String? dropoffPlace;
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

  /// ESC-15: the transport fell through after payment; the buyer decides
  /// (cancel for a full refund). Server-managed.
  awaitingTransport,

  /// A status this app version does not know (a newer server). Shown as
  /// "Updating" with no actions until the app is updated.
  unknown,
}
