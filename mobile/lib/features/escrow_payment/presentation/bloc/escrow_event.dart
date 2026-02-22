import 'package:equatable/equatable.dart';

/// Events for the Escrow BLoC.
abstract class EscrowEvent extends Equatable {
  const EscrowEvent();

  @override
  List<Object?> get props => [];
}

/// Buyer initiates payment for a delivery.
/// Triggers online API call or offline intent queue.
class InitiatePayment extends EscrowEvent {
  final String deliveryId;
  final String buyerId;
  final String farmerId;
  final double amount;

  const InitiatePayment({
    required this.deliveryId,
    required this.buyerId,
    required this.farmerId,
    required this.amount,
  });

  @override
  List<Object?> get props => [deliveryId, buyerId, farmerId, amount];
}

/// Load a single escrow for the status screen.
class LoadEscrowDetail extends EscrowEvent {
  final String escrowId;

  const LoadEscrowDetail({required this.escrowId});

  @override
  List<Object?> get props => [escrowId];
}

/// Load escrow by delivery ID (from delivery detail screen).
class LoadEscrowByDelivery extends EscrowEvent {
  final String deliveryId;

  const LoadEscrowByDelivery({required this.deliveryId});

  @override
  List<Object?> get props => [deliveryId];
}

/// Load user's payment history.
class LoadPaymentHistory extends EscrowEvent {
  final String userId;
  final String? statusFilter;
  final String? roleFilter;

  const LoadPaymentHistory({
    required this.userId,
    this.statusFilter,
    this.roleFilter,
  });

  @override
  List<Object?> get props => [userId, statusFilter, roleFilter];
}

/// Refresh single escrow from server.
class RefreshEscrow extends EscrowEvent {
  final String escrowId;

  const RefreshEscrow({required this.escrowId});

  @override
  List<Object?> get props => [escrowId];
}

/// Refresh payment history from server (pull-to-refresh).
class RefreshPaymentHistory extends EscrowEvent {
  final String userId;
  final String? statusFilter;
  final String? roleFilter;

  const RefreshPaymentHistory({
    required this.userId,
    this.statusFilter,
    this.roleFilter,
  });

  @override
  List<Object?> get props => [userId, statusFilter, roleFilter];
}

/// Buyer or farmer raises a dispute on an escrowed payment.
class RaisePaymentDispute extends EscrowEvent {
  final String escrowId;
  final String disputedBy;
  final String reason;
  final String? description;
  final List<String>? evidenceRefs;

  const RaisePaymentDispute({
    required this.escrowId,
    required this.disputedBy,
    required this.reason,
    this.description,
    this.evidenceRefs,
  });

  @override
  List<Object?> get props =>
      [escrowId, disputedBy, reason, description, evidenceRefs];
}

/// Cancel a queued payment intent (before it syncs).
class CancelPaymentIntent extends EscrowEvent {
  final String idempotencyKey;

  const CancelPaymentIntent({required this.idempotencyKey});

  @override
  List<Object?> get props => [idempotencyKey];
}

/// Filter payment history by status/role.
class FilterPaymentHistory extends EscrowEvent {
  final String userId;
  final String? statusFilter;
  final String? roleFilter;

  const FilterPaymentHistory({
    required this.userId,
    this.statusFilter,
    this.roleFilter,
  });

  @override
  List<Object?> get props => [userId, statusFilter, roleFilter];
}

/// Load pending (queued) payment intents.
class LoadPendingIntents extends EscrowEvent {
  const LoadPendingIntents();
}
