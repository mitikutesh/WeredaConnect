import 'package:equatable/equatable.dart';

import 'package:wereda_connect/core/database/schemas/crop_listing.dart';

/// Base class for delivery events.
abstract class DeliveryEvent extends Equatable {
  const DeliveryEvent();

  @override
  List<Object?> get props => [];
}

/// Load deliveries for a trip.
class LoadDeliveries extends DeliveryEvent {
  final String tripId;

  const LoadDeliveries({required this.tripId});

  @override
  List<Object?> get props => [tripId];
}

/// Create a new delivery.
class CreateDelivery extends DeliveryEvent {
  final String tripId;
  final String listingId;
  final String farmerId;
  final String farmerName;
  final String truckerId;
  final String truckerName;
  final String buyerId;
  final String buyerName;
  final String qrToken;

  const CreateDelivery({
    required this.tripId,
    required this.listingId,
    required this.farmerId,
    required this.farmerName,
    required this.truckerId,
    required this.truckerName,
    required this.buyerId,
    required this.buyerName,
    required this.qrToken,
  });

  @override
  List<Object?> get props => [
        tripId,
        listingId,
        farmerId,
        farmerName,
        truckerId,
        truckerName,
        buyerId,
        buyerName,
        qrToken,
      ];
}

/// Confirm pickup of crops by trucker.
class ConfirmPickup extends DeliveryEvent {
  final String deliveryId;
  final String truckerId;
  final LatLng? gpsPickup;
  final String? photoRef;

  const ConfirmPickup({
    required this.deliveryId,
    required this.truckerId,
    this.gpsPickup,
    this.photoRef,
  });

  @override
  List<Object?> get props => [deliveryId, truckerId, gpsPickup, photoRef];
}

/// Confirm delivery received by buyer.
class ConfirmDelivery extends DeliveryEvent {
  final String deliveryId;
  final String buyerId;
  final LatLng? gpsDropoff;
  final String? photoRef;

  const ConfirmDelivery({
    required this.deliveryId,
    required this.buyerId,
    this.gpsDropoff,
    this.photoRef,
  });

  @override
  List<Object?> get props => [deliveryId, buyerId, gpsDropoff, photoRef];
}

/// Raise a dispute on a delivery.
class RaiseDispute extends DeliveryEvent {
  final String deliveryId;
  final String userId;
  final String reason;
  final String tripId;

  const RaiseDispute({
    required this.deliveryId,
    required this.userId,
    required this.reason,
    required this.tripId,
  });

  @override
  List<Object?> get props => [deliveryId, userId, reason, tripId];
}

/// Cancel a delivery.
class CancelDelivery extends DeliveryEvent {
  final String deliveryId;
  final String userId;
  final String tripId;

  const CancelDelivery({
    required this.deliveryId,
    required this.userId,
    required this.tripId,
  });

  @override
  List<Object?> get props => [deliveryId, userId, tripId];
}

/// Filter deliveries by status.
class FilterDeliveries extends DeliveryEvent {
  final String tripId;
  final String? statusFilter;

  const FilterDeliveries({
    required this.tripId,
    this.statusFilter,
  });

  @override
  List<Object?> get props => [tripId, statusFilter];
}
