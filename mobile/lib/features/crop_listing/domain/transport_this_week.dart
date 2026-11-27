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
    if (!_canLeaveFrom(trip, fromKey, today)) return false;
    final day = startOfDayInEthiopia(trip.departureDate);
    if (!day.isBefore(weekEnd)) return false;
    return toKey.isEmpty ||
        compareWoredaKeys(normalizeWoreda(trip.toWoreda), toKey) !=
            WoredaMatch.none;
  }).toList()
    ..sort((a, b) => a.departureDate.compareTo(b.departureDate));
  return fitting;
}

/// An open trip with room left that leaves [fromKey]'s wereda on [today] or
/// later, whatever its destination.
bool _canLeaveFrom(Trip trip, String fromKey, DateTime today) {
  if (trip.status != TripStatus.open) return false;
  if (startOfDayInEthiopia(trip.departureDate).isBefore(today)) return false;
  if (tripRoomLeft(trip) <= 0) return false;
  return compareWoredaKeys(normalizeWoreda(trip.fromWoreda), fromKey) !=
      WoredaMatch.none;
}

/// How well the open [trips] serve [listing] for a buyer who takes delivery
/// at [deliveryWoreda] (Market board: "sorted by the best match for
/// Merkato"). Lower is better: 0 when a trip this week can carry it there
/// ([tripsThisWeekFor], the amber transport pill), 1 when some open trip
/// with room still leaves its wereda, later or for another place, 2 when
/// none does.
int transportMatchRank(
  CropListing listing,
  Iterable<Trip> trips,
  String? deliveryWoreda,
  DateTime now,
) {
  if (tripsThisWeekFor(listing, trips, deliveryWoreda, now).isNotEmpty) {
    return 0;
  }
  final fromKey = normalizeWoreda(listing.locationName);
  if (fromKey.isEmpty) return 2;
  final today = startOfDayInEthiopia(now.toUtc());
  return trips.any((trip) => _canLeaveFrom(trip, fromKey, today)) ? 1 : 2;
}

/// [listings] by [transportMatchRank], best first, the given order kept
/// inside each rank. Every listing stays: the buyer sees them all, only the
/// order changes.
List<CropListing> sortedByTransportMatch(
  List<CropListing> listings,
  Iterable<Trip> trips,
  String? deliveryWoreda,
  DateTime now,
) {
  final ranks = [<CropListing>[], <CropListing>[], <CropListing>[]];
  for (final listing in listings) {
    ranks[transportMatchRank(listing, trips, deliveryWoreda, now)].add(listing);
  }
  return [for (final rank in ranks) ...rank];
}
