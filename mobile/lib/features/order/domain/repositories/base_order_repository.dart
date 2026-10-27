import 'package:wereda_connect/core/models/order.dart';

/// Abstract base for order repositories.
///
/// Extracted from OrderRepository so both mobile (Isar) and
/// web (HTTP-only) implementations can share the same BLoC interface.
abstract class BaseOrderRepository {
  Future<List<Order>> getOrdersByFarmer(
    String farmerId, {
    String? statusFilter,
  });

  Future<List<Order>> getOrdersByBuyer(
    String buyerId, {
    String? statusFilter,
  });

  Future<List<Order>> getOrdersByUser(
    String userId, {
    String? role,
    String? statusFilter,
  });

  Future<Order?> getOrderById(String orderId);

  Future<Order> createOrder({
    required String listingId,
    required String buyerId,
    required String farmerId,
    required String buyerName,
    required String farmerName,
    required String cropType,
    required double quantity,
    required double pricePerUnit,
    String? tripId,
    String? truckerId,
    String? truckerName,
    double? transportCost,
    double? commissionRate,
  });

  /// The buyer's `payment_submitted`. [cbeTransactionId] is the demo
  /// transfer reference; live Telebirr payments send none (ESC-44), they are
  /// matched by the server's checkout. A retry after `payment_failed` is the
  /// same single change (order-lifecycle 1.13.0).
  Future<void> submitPayment(
    String orderId,
    String? cbeTransactionId, {
    String paymentMethod = 'cbe_transfer',
  });

  Future<void> cancelOrder(
    String orderId,
    String userId, {
    String? reason,
  });

  /// The farmer's yes to a funded order (`farmerResponse: accept`). A
  /// self-pickup order is agreed at once; a trip order stays `funded` while
  /// its trucker is asked (ESC-15, sequential).
  Future<void> acceptOrder(String orderId, String farmerId);

  /// The farmer's no (`farmerResponse: decline`): the buyer is refunded.
  Future<void> rejectOrder(
    String orderId,
    String farmerId, {
    String? reason,
  });

  /// ESC-15: the trucker's answer to a transport request
  /// (`truckerResponse: accept | decline`), only once the farmer accepted. A
  /// decline on an `accepted` order before pickup is the trucker's drop-out
  /// (the server asks for a fresh sign-in). [declineReason] is one of
  /// `commitmentDeclineReasons`.
  Future<void> respondAsTrucker(
    String orderId,
    String truckerId, {
    required bool accept,
    String? declineReason,
  });
}
