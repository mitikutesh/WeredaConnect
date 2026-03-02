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
