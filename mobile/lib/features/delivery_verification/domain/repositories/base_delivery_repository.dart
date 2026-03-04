import 'package:wereda_connect/core/models/crop_listing.dart';
import 'package:wereda_connect/core/models/delivery.dart';

/// Abstract base for delivery repositories.
///
/// Extracted from DeliveryRepository so both mobile (Isar) and
/// web (HTTP-only) implementations can share the same BLoC interface.
abstract class BaseDeliveryRepository {
  Future<Delivery> createDelivery({
    required String tripId,
    required String listingId,
    required String farmerId,
    required String farmerName,
    required String truckerId,
    required String truckerName,
    required String buyerId,
    required String buyerName,
    required String qrToken,
  });

  Future<void> persistQrNonce({
    required String deliveryId,
    required String qrNonce,
    required String userId,
  });

  Future<void> confirmPickup({
    required String deliveryId,
    required String truckerId,
    LatLng? gpsPickup,
    String? photoRef,
    String? scannedQrNonce,
  });

  Future<void> confirmDelivery({
    required String deliveryId,
    required String buyerId,
    LatLng? gpsDropoff,
    String? photoRef,
    String? scannedQrNonce,
  });

  Future<void> markInTransit({
    required String deliveryId,
    required String truckerId,
  });

  Future<void> raiseDispute({
    required String deliveryId,
    required String userId,
    required String reason,
  });

  Future<void> cancelDelivery({
    required String deliveryId,
    required String userId,
  });

  Future<List<Delivery>> getDeliveriesByTrip(
    String tripId, {
    required String userId,
  });

  Future<List<Delivery>> getDeliveriesByStatus(
    DeliveryStatus status, {
    required String userId,
  });

  Future<Delivery?> getDeliveryById(
    String deliveryId, {
    required String userId,
  });

  Future<List<Delivery>> getDeliveriesByUser(
    String userId, {
    String? role,
    String? statusFilter,
  });
}
