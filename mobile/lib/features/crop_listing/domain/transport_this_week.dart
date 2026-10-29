import 'package:wereda_connect/core/models/crop_listing.dart';
import 'package:wereda_connect/core/models/trip.dart';
import 'package:wereda_connect/core/models/trip_capacity.dart';
import 'package:wereda_connect/core/utils/ethiopia_time.dart';
import 'package:wereda_connect/shared/services/place_normalizer.dart';

/// Open trips that could carry [listing] this week (Buyer · Find crops board:
/// "Transport Thu, 18 q room", "2 trips this week", "No trip yet"): leaving
/// the listing's wereda, going to [deliveryWoreda] when the buyer has one,
/// departing today or within the next 7 days in Ethiopia, with room left.
/// Soonest first. The route names match as in the matching service.
List<Trip> tripsThisWeekFor(
  CropListing listing,
  Iterable<Trip> trips,
  String? deliveryWoreda,
  DateTime now,
) {
  final fromKey = normalizeWoreda(listing.locationName);
  if (fromKey.isEmpty) return const [];
  final toKey =
      deliveryWoreda == null ? '' : normalizeWoreda(deliveryWoreda.trim());
  final today = startOfDayInEthiopia(now.toUtc());
  final weekEnd = today.add(const Duration(days: 7));

  final fitting = trips.where((trip) {
    if (trip.status != TripStatus.open) return false;
    final day = startOfDayInEthiopia(trip.departureDate);
    if (day.isBefore(today) || !day.isBefore(weekEnd)) return false;
    if (tripRoomLeft(trip) <= 0) return false;
    if (compareWoredaKeys(normalizeWoreda(trip.fromWoreda), fromKey) ==
        WoredaMatch.none) {
      return false;
    }
    return toKey.isEmpty ||
        compareWoredaKeys(normalizeWoreda(trip.toWoreda), toKey) !=
            WoredaMatch.none;
  }).toList()
    ..sort((a, b) => a.departureDate.compareTo(b.departureDate));
  return fitting;
}
