import 'package:wereda_connect/core/models/crop_listing.dart';
import 'package:wereda_connect/core/models/delivery.dart';

/// Abstract base for delivery repositories.
///
/// Extracted from DeliveryRepository so both mobile (Isar) and
/// web (HTTP-only) implementations can share the same BLoC interface.
abstract class BaseDeliveryRepository {
  Future<Delivery> createDelivery({
    required String tripId,
    required String orderId,
    required String listingId,
    required String farmerId,
    required String farmerName,
    required String truckerId,
    required String truckerName,
    required String buyerId,
    required String buyerName,
    required String qrToken,
  });

  /// Buyer creates a SELF-PICKUP delivery (no trucker): the buyer collects at the
  /// farm and confirms the handoff by scanning the farmer's QR. Requires the linked
  /// [orderId] (the order must carry no transport/trucker — enforced server-side).
  Future<Delivery> createSelfPickupDelivery({
    required String orderId,
    required String listingId,
    required String farmerId,
    required String farmerName,
    required String buyerId,
    required String buyerName,
  });

  Future<void> persistQrNonce({
    required String deliveryId,
    required String qrNonce,
    required String userId,
  });

  /// Registers the trucker's drop-off nonce (ESC-35): a `/sync` delivery
  /// update carrying only `dropoffQrNonce`, which the server keeps in the QR
  /// secret store and checks the buyer's confirm against. A registration
  /// never replaces a code already stored (another phone's, or one made
  /// later); [renew] (the trucker's New drop-off code, `dropoffQrRenew`)
  /// does. Sent again for the same nonce it changes nothing. Only the
  /// delivery's trucker sends it.
  Future<DropoffCodeRegistration> persistDropoffQrNonce({
    required String deliveryId,
    required String nonce,
    required String truckerId,
    bool renew = false,
  });

  /// [scannedQrNonce] from a scan, or [scannedQrCode], the typed fallback
  /// (T-1, sent as `scannedQrCode` and checked by the server).
  Future<void> confirmPickup({
    required String deliveryId,
    required String truckerId,
    LatLng? gpsPickup,
    String? photoRef,
    String? scannedQrNonce,
    String? scannedQrCode,
  });

  /// [scannedQrNonce] from a scan, or [scannedQrCode], the typed fallback.
  Future<void> confirmDelivery({
    required String deliveryId,
    required String buyerId,
    LatLng? gpsDropoff,
    String? photoRef,
    String? scannedQrNonce,
    String? scannedQrCode,
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

  /// The order's live (not cancelled) delivery that [userId] is a party to,
  /// or null. [role] is 'farmer', 'trucker' or 'buyer'. [orderCreatedAt]
  /// lets a remote lookup stop at deliveries older than the order.
  Future<Delivery?> findActiveDeliveryForOrder(
    String orderId, {
    required String userId,
    required String role,
    DateTime? orderCreatedAt,
  });
}

/// What became of a drop-off code sent to the server (ESC-35).
enum DropoffCodeRegistration {
  /// Stored, or queued on a phone (the queue gets it there).
  sent,

  /// The server keeps another drop-off code for the delivery (made on
  /// another phone or browser): this one does not work.
  superseded,

  /// The server could not keep it (no pickup code on record).
  notStored,

  /// It did not get through (offline web, or refused).
  failed,
}
