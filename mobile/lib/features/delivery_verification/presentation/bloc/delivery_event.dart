import 'package:equatable/equatable.dart';

import 'package:wereda_connect/core/database/schemas/crop_listing.dart';

/// Base class for delivery events.
abstract class DeliveryEvent extends Equatable {
  const DeliveryEvent();

  @override
  List<Object?> get props => [];
}

/// Load deliveries for a trip, scoped by userId.
class LoadDeliveries extends DeliveryEvent {
  final String tripId;
  final String userId;

  const LoadDeliveries({required this.tripId, required this.userId});

  @override
  List<Object?> get props => [tripId, userId];
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
  final String? scannedQrNonce;

  const ConfirmPickup({
    required this.deliveryId,
    required this.truckerId,
    this.gpsPickup,
    this.photoRef,
    this.scannedQrNonce,
  });

  @override
  List<Object?> get props =>
      [deliveryId, truckerId, gpsPickup, photoRef, scannedQrNonce];
}

/// Confirm delivery received by buyer.
class ConfirmDelivery extends DeliveryEvent {
  final String deliveryId;
  final String buyerId;
  final LatLng? gpsDropoff;
  final String? photoRef;
  final String? scannedQrNonce;

  const ConfirmDelivery({
    required this.deliveryId,
    required this.buyerId,
    this.gpsDropoff,
    this.photoRef,
    this.scannedQrNonce,
  });

  @override
  List<Object?> get props =>
      [deliveryId, buyerId, gpsDropoff, photoRef, scannedQrNonce];
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

/// Filter deliveries by status, scoped by userId.
class FilterDeliveries extends DeliveryEvent {
  final String tripId;
  final String userId;
  final String? statusFilter;

  const FilterDeliveries({
    required this.tripId,
    required this.userId,
    this.statusFilter,
  });

  @override
  List<Object?> get props => [tripId, userId, statusFilter];
}

/// Load all deliveries for the authenticated user across all trips.
/// Filters by role (farmer/trucker/buyer) when specified.
class LoadUserDeliveries extends DeliveryEvent {
  final String userId;
  final String? role; // 'farmer', 'trucker', 'buyer', or null for all

  const LoadUserDeliveries({required this.userId, this.role});

  @override
  List<Object?> get props => [userId, role];
}

/// Load a single delivery by ID for detail screen, scoped by userId.
class LoadDeliveryDetail extends DeliveryEvent {
  final String deliveryId;
  final String userId;

  const LoadDeliveryDetail({
    required this.deliveryId,
    required this.userId,
  });

  @override
  List<Object?> get props => [deliveryId, userId];
}

/// Mark a delivery as in transit (pickedUp → inTransit).
class MarkInTransit extends DeliveryEvent {
  final String deliveryId;
  final String truckerId;

  const MarkInTransit({
    required this.deliveryId,
    required this.truckerId,
  });

  @override
  List<Object?> get props => [deliveryId, truckerId];
}

/// Filter user deliveries by status.
class FilterUserDeliveries extends DeliveryEvent {
  final String userId;
  final String? role;
  final String? statusFilter;

  const FilterUserDeliveries({
    required this.userId,
    this.role,
    this.statusFilter,
  });

  @override
  List<Object?> get props => [userId, role, statusFilter];
}
