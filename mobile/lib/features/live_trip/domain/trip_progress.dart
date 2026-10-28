import 'package:wereda_connect/core/models/delivery.dart';
import 'package:wereda_connect/features/live_trip/domain/check_in_rules.dart';

/// One delivery of a trip as the check-in engine sees it.
class TripLeg {
  const TripLeg({
    required this.deliveryId,
    required this.status,
    this.dropoff,
  });

  final String deliveryId;
  final DeliveryStatus status;

  /// Where it is dropped off, when the app knows (the trip's destination
  /// place until LT-12 gives each order its own point).
  final GeoPoint? dropoff;

  /// Delivered, cancelled or disputed: off the road for good.
  bool get isDone =>
      status == DeliveryStatus.delivered ||
      status == DeliveryStatus.cancelled ||
      status == DeliveryStatus.disputed;

  /// Picked up and not handed over yet.
  bool get isOnRoad =>
      status == DeliveryStatus.pickedUp || status == DeliveryStatus.inTransit;
}

/// Where the deliveries of a trip stand, from the phone's own copy.
class TripProgress {
  const TripProgress(this.legs);

  final List<TripLeg> legs;

  /// Every delivery is delivered, cancelled or disputed: sharing stops
  /// (`live-trip.md` section 5.2). A trip with no delivery the phone knows
  /// of is not finished: the phone just cannot tell.
  bool get allDone => legs.isNotEmpty && legs.every((l) => l.isDone);

  /// The drop-off points of the deliveries not done yet.
  List<GeoPoint> get undeliveredDropoffs => [
        for (final leg in legs)
          if (!leg.isDone && leg.dropoff != null) leg.dropoff!,
      ];

  /// Whether a delivery other than [deliveryId] is on the road.
  bool anotherOnRoad(String deliveryId) =>
      legs.any((l) => l.deliveryId != deliveryId && l.isOnRoad);
}

/// Reads a trip's progress for the check-in engine (the local deliveries on
/// mobile, the API on web).
abstract interface class TripProgressReader {
  /// The trip's deliveries as [userId] sees them, or null when they cannot
  /// be read now.
  Future<TripProgress?> read(String tripId, {required String userId});

  /// The trip a delivery belongs to, or null when unknown.
  Future<String?> tripIdOfDelivery(
    String deliveryId, {
    required String userId,
  });
}
