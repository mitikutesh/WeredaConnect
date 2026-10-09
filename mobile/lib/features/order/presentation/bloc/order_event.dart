import 'package:equatable/equatable.dart';

/// Base class for order events.
abstract class OrderEvent extends Equatable {
  const OrderEvent();

  @override
  List<Object?> get props => [];
}

/// Load orders for the authenticated user.
class LoadUserOrders extends OrderEvent {
  final String userId;
  final String? role;

  const LoadUserOrders({required this.userId, this.role});

  @override
  List<Object?> get props => [userId, role];
}

/// Filter orders by status.
class FilterOrders extends OrderEvent {
  final String userId;
  final String? role;
  final String? statusFilter;

  const FilterOrders({
    required this.userId,
    this.role,
    this.statusFilter,
  });

  @override
  List<Object?> get props => [userId, role, statusFilter];
}

/// Accept an order (farmer action).
class AcceptOrder extends OrderEvent {
  final String orderId;
  final String farmerId;

  const AcceptOrder({required this.orderId, required this.farmerId});

  @override
  List<Object?> get props => [orderId, farmerId];
}

/// Reject an order (farmer action).
class RejectOrder extends OrderEvent {
  final String orderId;
  final String farmerId;
  final String? reason;

  const RejectOrder({
    required this.orderId,
    required this.farmerId,
    this.reason,
  });

  @override
  List<Object?> get props => [orderId, farmerId, reason];
}

/// Create a new order (buyer action).
class CreateOrder extends OrderEvent {
  final String listingId;
  final String buyerId;
  final String farmerId;
  final String buyerName;
  final String farmerName;
  final String cropType;
  final double quantity;
  final double pricePerUnit;
  final String? tripId;
  final String? truckerId;
  final String? truckerName;
  final double? transportCost;
  final double? commissionRate;

  const CreateOrder({
    required this.listingId,
    required this.buyerId,
    required this.farmerId,
    required this.buyerName,
    required this.farmerName,
    required this.cropType,
    required this.quantity,
    required this.pricePerUnit,
    this.tripId,
    this.truckerId,
    this.truckerName,
    this.transportCost,
    this.commissionRate,
  });

  @override
  List<Object?> get props => [
        listingId,
        buyerId,
        farmerId,
        buyerName,
        farmerName,
        cropType,
        quantity,
        pricePerUnit,
        tripId,
        truckerId,
        truckerName,
        transportCost,
        commissionRate,
      ];
}

/// Submit payment proof for an order (buyer action).
///
/// [paymentMethod] selects the rail: `cbe_transfer` or `telebirr`. In demo
/// builds [cbeTransactionId] carries the payment reference for either rail (a
/// mock reference is generated when the buyer doesn't enter a real one). Live
/// Telebirr payments send none: the server's checkout matches them (ESC-44).
class SubmitPayment extends OrderEvent {
  final String orderId;
  final String? cbeTransactionId;
  final String paymentMethod;

  const SubmitPayment({
    required this.orderId,
    this.cbeTransactionId,
    this.paymentMethod = 'cbe_transfer',
  });

  @override
  List<Object?> get props => [orderId, cbeTransactionId, paymentMethod];
}

/// Cancel an order (buyer or farmer action).
class CancelOrder extends OrderEvent {
  final String orderId;
  final String userId;
  final String? reason;

  const CancelOrder({
    required this.orderId,
    required this.userId,
    this.reason,
  });

  @override
  List<Object?> get props => [orderId, userId, reason];
}

/// Load a single order by its ID.
class LoadOrderById extends OrderEvent {
  final String orderId;

  const LoadOrderById({required this.orderId});

  @override
  List<Object?> get props => [orderId];
}
