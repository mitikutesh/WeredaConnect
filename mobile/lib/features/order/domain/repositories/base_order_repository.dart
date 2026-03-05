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

  Future<void> submitPayment(String orderId, String cbeTransactionId);

  Future<void> cancelOrder(
    String orderId,
    String userId, {
    String? reason,
  });

  Future<void> acceptOrder(String orderId, String farmerId);

  Future<void> rejectOrder(
    String orderId,
    String farmerId, {
    String? reason,
  });
}
