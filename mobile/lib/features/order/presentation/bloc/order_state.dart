import 'package:equatable/equatable.dart';

import 'package:wereda_connect/core/models/order.dart';

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

/// State after a new order has been created.
class OrderCreated extends OrderState {
  final Order order;

  const OrderCreated({required this.order});

  @override
  List<Object?> get props => [order];
}

/// State after payment has been submitted.
class PaymentSubmitted extends OrderState {
  const PaymentSubmitted();
}

/// State after an order has been cancelled.
class OrderCancelled extends OrderState {
  const OrderCancelled();
}

/// State when a single order has been loaded.
class SingleOrderLoaded extends OrderState {
  final Order order;

  const SingleOrderLoaded({required this.order});

  @override
  List<Object?> get props => [order];
}
