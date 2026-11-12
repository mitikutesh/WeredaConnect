import 'package:equatable/equatable.dart';

import 'package:wereda_connect/core/errors/app_error.dart';
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

/// Error state when an operation fails. [code] drives the localized,
/// user-facing message (via `localizeErrorCode`); [message] is the raw detail
/// kept for logging — do NOT show it directly to users.
class OrderError extends OrderState {
  final String message;
  final AppErrorCode code;

  const OrderError({required this.message, this.code = AppErrorCode.unknown});

  @override
  List<Object?> get props => [message, code];
}

/// State after an order has been accepted.
class OrderAccepted extends OrderState {
  const OrderAccepted();
}

/// State after an order has been rejected.
class OrderRejected extends OrderState {
  const OrderRejected();
}

/// ESC-15: state after the trucker answered a transport request.
class TruckerResponded extends OrderState {
  final bool accepted;

  const TruckerResponded({required this.accepted});

  @override
  List<Object?> get props => [accepted];
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

/// LT-12: state after the buyer's drop-off landmark was saved (sent, or
/// queued on the phone).
class DropoffNoteSaved extends OrderState {
  const DropoffNoteSaved();
}

/// LT-14: state after the order's drop-off point was saved (sent, or queued
/// on the phone).
class DeliveryLocationSaved extends OrderState {
  const DeliveryLocationSaved();
}

/// ESC-34 (FUP-285): state after an arrival check-in was sent (web) or
/// queued on the phone, or, with [alreadyGiven], found already given (a
/// repeat sends nothing).
class CheckInSaved extends OrderState {
  final String kind;
  final bool alreadyGiven;

  const CheckInSaved({required this.kind, this.alreadyGiven = false});

  @override
  List<Object?> get props => [kind, alreadyGiven];
}

/// State when a single order has been loaded.
class SingleOrderLoaded extends OrderState {
  final Order order;

  const SingleOrderLoaded({required this.order});

  @override
  List<Object?> get props => [order];
}
