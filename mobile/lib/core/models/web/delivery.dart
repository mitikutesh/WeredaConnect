import 'package:wereda_connect/core/models/crop_listing.dart';

/// Web-safe Delivery model (no Isar dependency).
class Delivery {
  int id = 0;

  late String deliveryId;
  late String tripId;
  late String listingId;
  late String farmerId;
  late String farmerName;
  late String truckerId;
  late String truckerName;
  late String buyerId;
  late String buyerName;
  late String qrToken;
  String? qrNonce;

  /// `null`/absent = normal trucker delivery; `self_pickup` = buyer collects at the farm.
  String? deliveryType;

  /// The order this delivery fulfils (SIM-14); null only from a server predating the field.
  String? orderId;

  /// Server-stamped from the order, listing and trip on create (1.21.0);
  /// null on older deliveries.
  String? cropType;
  double? quantity;
  String? pickupPlace;
  DateTime? pickupPlannedAt;
  LatLng? gpsPickup;
  LatLng? gpsDropoff;
  String? pickupPhotoRef;
  String? dropoffPhotoRef;
  late DeliveryStatus status;
  DateTime? pickedUpAt;
  DateTime? confirmedAt;
  String? disputeReason;
  late DateTime createdAt;
  late DateTime updatedAt;
  late int version;
}

/// Status of a delivery in its lifecycle.
enum DeliveryStatus {
  pending,
  pickedUp,
  inTransit,
  delivered,
  disputed,
  cancelled,
}
