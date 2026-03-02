import 'package:equatable/equatable.dart';

import 'package:wereda_connect/core/database/schemas/order.dart';

/// Base class for order states.
abstract class OrderState extends Equatable {
  const OrderState();

  @override
  List<Object?> get props => [];
}

/// Initial state before any data is loaded.
class OrderInitial extends OrderState {
  const OrderInitial();
}

/// Loading state during data operations.
class OrderLoading extends OrderState {
  const OrderLoading();
}

/// State when orders have been successfully loaded.
class OrdersLoaded extends OrderState {
  final List<Order> orders;
  final String? statusFilter;

  const OrdersLoaded({required this.orders, this.statusFilter});

  @override
  List<Object?> get props => [orders, statusFilter];
}

/// Error state when an operation fails.
class OrderError extends OrderState {
  final String message;

  const OrderError({required this.message});

  @override
  List<Object?> get props => [message];
}

/// State after an order has been accepted.
class OrderAccepted extends OrderState {
  const OrderAccepted();
}

/// State after an order has been rejected.
class OrderRejected extends OrderState {
  const OrderRejected();
}
