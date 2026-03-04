import 'package:wereda_connect/core/models/trip.dart';

/// Abstract base for trip listing repositories.
///
/// Extracted from TripListingRepository so both mobile (Isar) and
/// web (HTTP-only) implementations can share the same BLoC interface.
abstract class BaseTripListingRepository {
  Future<Trip> createTrip({
    required String truckerId,
    required String truckerName,
    required String truckerPhone,
    required String fromWoreda,
    required String toWoreda,
    required DateTime departureDate,
    required double capacity,
    required double pricePerQuintal,
    String? vehiclePlate,
    String? vehicleDescription,
  });

  Future<List<Trip>> getTripsByTrucker(String truckerId);

  Future<List<Trip>> getOpenTrips({
    String? fromWoreda,
    String? toWoreda,
  });

  Future<void> cancelTrip({
    required String tripId,
    required String truckerId,
  });

  Future<Trip> startTrip({
    required String tripId,
    required String truckerId,
  });

  Future<Trip> markDelivered({
    required String tripId,
    required String truckerId,
  });

  Future<List<({String from, String to})>> getRecentRoutes(
    String truckerId, {
    int limit = 5,
  });

  Future<Trip?> getMostRelevantTrip(String truckerId);
}
