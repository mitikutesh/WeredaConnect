import 'package:wereda_connect/core/models/delivery.dart';
import 'package:wereda_connect/core/models/trip.dart';
import 'package:wereda_connect/features/delivery_verification/domain/repositories/base_delivery_repository.dart';
import 'package:wereda_connect/features/live_trip/domain/check_in_rules.dart';
import 'package:wereda_connect/features/live_trip/domain/trip_progress.dart';
import 'package:wereda_connect/features/trip_listing/domain/repositories/base_trip_listing_repository.dart';
import 'package:wereda_connect/shared/services/place_repository.dart';

/// Reads a trip's progress through the delivery and trip repositories: the
/// local Isar copy on mobile, the API on web (LT-08).
///
/// The drop-off reference is the trip's destination place from the bundled
/// place list (its `toPlaceId`, else its `toWoreda` name), the same planned
/// point the server's track uses until LT-12 gives each order its own. A trip
/// whose destination has no coordinates has no drop-off point, so the phone
/// stays hourly.
class RepositoryTripProgressReader implements TripProgressReader {
  RepositoryTripProgressReader({
    required BaseDeliveryRepository deliveries,
    required BaseTripListingRepository trips,
    PlaceRepository? places,
  })  : _deliveries = deliveries,
        _trips = trips,
        _places = places ?? PlaceRepository.instance;

  final BaseDeliveryRepository _deliveries;
  final BaseTripListingRepository _trips;
  final PlaceRepository _places;

  /// Destination point per trip (a trip's route does not change once booked).
  final Map<String, GeoPoint?> _dropoffs = {};

  @override
  Future<TripProgress?> read(String tripId, {required String userId}) async {
    final deliveries =
        await _deliveries.getDeliveriesByTrip(tripId, userId: userId);
    final dropoff = await _dropoffOf(tripId);
    return TripProgress([
      for (final delivery in deliveries)
        TripLeg(
          deliveryId: delivery.deliveryId,
          status: delivery.status,
          dropoff: dropoff,
          pickedUpAt: delivery.pickedUpAt?.toUtc(),
          leftRoadAt: _leftRoadAt(delivery),
        ),
    ]);
  }

  @override
  Future<String?> tripIdOfDelivery(
    String deliveryId, {
    required String userId,
  }) async {
    final delivery =
        await _deliveries.getDeliveryById(deliveryId, userId: userId);
    return delivery?.tripId;
  }

  /// `confirmedAt` for a delivered or disputed delivery. The phone keeps no
  /// `cancelledAt`, so a cancelled one uses its last update.
  static DateTime? _leftRoadAt(Delivery delivery) => switch (delivery.status) {
        DeliveryStatus.delivered ||
        DeliveryStatus.disputed =>
          (delivery.confirmedAt ?? delivery.updatedAt).toUtc(),
        DeliveryStatus.cancelled => delivery.updatedAt.toUtc(),
        _ => null,
      };

  Future<GeoPoint?> _dropoffOf(String tripId) async {
    if (_dropoffs.containsKey(tripId)) return _dropoffs[tripId];
    final Trip? trip = await _trips.getTripById(tripId);
    if (trip == null) return null;
    await _places.ensureLoaded();
    final placeId = trip.toPlaceId;
    final place = (placeId != null ? _places.byId(placeId) : null) ??
        _places.findByDisplayName(trip.toWoreda);
    final lat = place?.lat;
    final lng = place?.lng;
    final point =
        lat != null && lng != null ? (latitude: lat, longitude: lng) : null;
    _dropoffs[tripId] = point;
    return point;
  }
}
