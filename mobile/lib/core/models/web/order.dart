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
}
