import 'package:equatable/equatable.dart';

import 'package:wereda_connect/core/database/schemas/delivery.dart';

/// Base class for delivery states.
abstract class DeliveryState extends Equatable {
  const DeliveryState();

  @override
  List<Object?> get props => [];
}

/// Initial state before any data is loaded.
class DeliveryInitial extends DeliveryState {
  const DeliveryInitial();
}

/// Loading state during data operations.
class DeliveryLoading extends DeliveryState {
  const DeliveryLoading();
}

/// State when deliveries have been successfully loaded.
class DeliveriesLoaded extends DeliveryState {
  final List<Delivery> deliveries;
  final String? statusFilter;

  const DeliveriesLoaded({
    required this.deliveries,
    this.statusFilter,
  });

  @override
  List<Object?> get props => [deliveries, statusFilter];
}

/// State after a delivery has been successfully created.
class DeliveryCreated extends DeliveryState {
  final Delivery delivery;

  const DeliveryCreated({required this.delivery});

  @override
  List<Object?> get props => [delivery];
}

/// State after pickup has been confirmed.
class PickupConfirmed extends DeliveryState {
  const PickupConfirmed();
}

/// State after delivery has been confirmed.
class DeliveryConfirmed extends DeliveryState {
  const DeliveryConfirmed();
}

/// Error state when an operation fails.
///
/// [code] is a stable error code for l10n mapping in the presentation layer.
/// [message] is a fallback English message (used only if l10n mapping is missing).
class DeliveryError extends DeliveryState {
  final String message;
  final String? code;

  const DeliveryError({required this.message, this.code});

  @override
  List<Object?> get props => [message, code];
}

/// Single delivery loaded for detail view.
class DeliveryDetailLoaded extends DeliveryState {
  final Delivery delivery;

  const DeliveryDetailLoaded({required this.delivery});

  @override
  List<Object?> get props => [delivery];
}

/// Delivery marked as in transit.
class MarkedInTransit extends DeliveryState {
  const MarkedInTransit();
}

/// Dispute raised successfully.
class DisputeRaised extends DeliveryState {
  const DisputeRaised();
}

/// Delivery cancelled successfully.
class DeliveryCancelled extends DeliveryState {
  const DeliveryCancelled();
}
