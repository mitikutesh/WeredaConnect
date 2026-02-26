import 'package:equatable/equatable.dart';

import 'package:wereda_connect/core/database/schemas/payment.dart';
import 'package:wereda_connect/core/database/schemas/payment_intent.dart';

/// States for the Escrow BLoC.
abstract class EscrowState extends Equatable {
  const EscrowState();

  @override
  List<Object?> get props => [];
}

/// Initial state before any data is loaded.
class EscrowInitial extends EscrowState {
  const EscrowInitial();
}

/// Loading state during data operations.
class EscrowLoading extends EscrowState {
  const EscrowLoading();
}

/// Single escrow loaded (for status/detail screen).
class EscrowDetailLoaded extends EscrowState {
  final Payment payment;

  const EscrowDetailLoaded({required this.payment});

  @override
  List<Object?> get props => [payment];
}

/// Payment history loaded.
class PaymentHistoryLoaded extends EscrowState {
  final List<Payment> payments;
  final List<PaymentIntent> pendingIntents;
  final String? statusFilter;
  final String? roleFilter;

  const PaymentHistoryLoaded({
    required this.payments,
    this.pendingIntents = const [],
    this.statusFilter,
    this.roleFilter,
  });

  @override
  List<Object?> get props =>
      [payments, pendingIntents, statusFilter, roleFilter];
}

/// Payment initiation succeeded (server confirmed).
class PaymentInitiated extends EscrowState {
  final Payment payment;

  const PaymentInitiated({required this.payment});

  @override
  List<Object?> get props => [payment];
}

/// Payment intent queued (offline, will sync later).
class PaymentQueued extends EscrowState {
  final PaymentIntent intent;

  const PaymentQueued({required this.intent});

  @override
  List<Object?> get props => [intent];
}

/// Dispute raised successfully.
class DisputeRaisedSuccess extends EscrowState {
  final Payment payment;

  const DisputeRaisedSuccess({required this.payment});

  @override
  List<Object?> get props => [payment];
}

/// Payment intent cancelled successfully.
class IntentCancelled extends EscrowState {
  const IntentCancelled();
}

/// Error state.
class EscrowError extends EscrowState {
  final String code;
  final String message;

  const EscrowError({required this.code, required this.message});

  @override
  List<Object?> get props => [code, message];
}
