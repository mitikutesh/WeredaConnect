/// Web-safe Order model (no Isar dependency).
class Order {
  int id = 0;

  late String orderId;
  late String listingId;
  late String buyerId;
  late String farmerId;
  late String buyerName;
  late String farmerName;
  late String cropType;
  late double quantity;
  late double pricePerUnit;
  late double totalPrice;
  late OrderStatus status;
  late DateTime createdAt;
  DateTime? respondedAt;
  String? rejectionReason;
  late int version;
  String? tripId;
  String? truckerId;
  String? truckerName;
  double? cropCost;
  double? transportCost;
  double? commissionRate;
  double? commissionAmount;
  double? orderTotal;
  String? paymentMethod;
  String? cbeTransactionId;
  DateTime? paymentVerifiedAt;
  DateTime? cancelledAt;
  String? cancelReason;
  DateTime? updatedAt;

  /// ESC-06: the buyer may cancel an accepted order nobody picked up after this (UTC).
  DateTime? pickupDeadlineAt;

  /// ESC-48: whether the parties may reach each other on this order; null when unknown.
  bool? contactOpen;

  /// ESC-48: the parties' phones, only on an order whose contact is open.
  String? buyerPhone;
  String? farmerPhone;
  String? truckerPhone;

  /// ESC-15 commitment fields (server-written; see the Isar schema).
  String? fulfillment;
  String? farmerResponse;
  String? truckerResponse;
  DateTime? farmerDeadlineAt;
  DateTime? truckerDeadlineAt;
  DateTime? buyerChoiceDeadlineAt;
  String? transportFailure;
  String? declineReason;
  String? cancelledBy;
}

/// Status of an order in its lifecycle.
enum OrderStatus {
  created,
  pendingPayment,
  paymentSubmitted,
  funded,
  accepted,
  rejected,
  cancelled,
  pickupConfirmed,
  inTransit,
  delivered,
  disbursing,
  settled,
  paymentFailed,
  disputed,
  disputeResolved,
  awaitingTransport,
  unknown,
}
