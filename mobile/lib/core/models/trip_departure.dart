import 'package:wereda_connect/core/models/trip.dart';
import 'package:wereda_connect/core/models/trip_capacity.dart';
import 'package:wereda_connect/core/utils/ethiopia_time.dart';

/// L-7 (trip-listing-api.md 2.2.0): the earliest departure still listed as
/// open, the start of today in Ethiopia. A trip's departureDate is the
/// instant of Ethiopia midnight on the day the trucker picked, and trucks run
/// late, so a trip counts for its whole day (FUP-85) and the server's browse
/// uses the same cutoff. [now] is the device clock: the app keeps no server
/// time offset.
DateTime tripListingCutoff(DateTime now) => startOfDayInEthiopia(now.toUtc());

/// Whether [trip] has left: its departure day in Ethiopia is over.
bool tripHasLeft(Trip trip, DateTime now) =>
    trip.departureDate.isBefore(tripListingCutoff(now));

/// FUP-122 (trip-listing-api.md 2.3.0): the `GET /trips` field saying how many
/// hours ahead a trip must depart for an order create to name it. The server
/// sends 24 while it runs the commitments (`COMMITMENTS_ENABLED`) and 0 when
/// it does not; a server from before 2.3.0 sends nothing.
const String orderLeadHoursField = 'orderLeadHours';

/// [orderLeadHoursField] of a `GET /trips` page, or null when the page has
/// none or it is not a number of at least 0 (unknown: matching then keeps
/// only the FUP-85 day rule). A fraction is rounded up.
int? orderLeadHoursFromWire(Map<String, dynamic> page) {
  final raw = page[orderLeadHoursField];
  if (raw is! num || !raw.isFinite || raw < 0) return null;
  return raw.ceil();
}

/// Whether an order create would refuse [trip] for the commitments lead: it
/// departs less than [orderLeadHours] hours after [now]. The same comparison
/// as the server (`departureDate < now + lead`, `BindOrderToTrip`), so a trip
/// exactly [orderLeadHours] ahead is still taken. No lead (null or 0) refuses
/// nothing here, and [tripHasLeft] applies either way.
bool tripInsideOrderLead(Trip trip, DateTime now, int? orderLeadHours) {
  if (orderLeadHours == null || orderLeadHours <= 0) return false;
  return trip.departureDate
      .isBefore(now.toUtc().add(Duration(hours: orderLeadHours)));
}

/// A trucker's own trips for My Trips (L-7): the ones still ahead first, by
/// departure (a trip on the road left earliest, so it leads), then the rest
/// (a passed date, delivered, cancelled) newest departure first. Nothing is
/// dropped: the trucker sees every trip.
List<Trip> upcomingTripsFirst(Iterable<Trip> trips, DateTime now) {
  bool ahead(Trip trip) =>
      trip.status == TripStatus.inTransit ||
      ((trip.status == TripStatus.open || trip.status == TripStatus.matched) &&
          !tripHasLeft(trip, now));
  int newestFirst(Trip a, Trip b) => b.createdAt.compareTo(a.createdAt);
  final upcoming = [
    for (final trip in trips)
      if (ahead(trip)) trip,
  ]..sort((a, b) {
      final byDeparture = a.departureDate.compareTo(b.departureDate);
      return byDeparture != 0 ? byDeparture : newestFirst(a, b);
    });
  final past = [
    for (final trip in trips)
      if (!ahead(trip)) trip,
  ]..sort((a, b) {
      final byDeparture = b.departureDate.compareTo(a.departureDate);
      return byDeparture != 0 ? byDeparture : newestFirst(a, b);
    });
  return [...upcoming, ...past];
}

/// Why a trip cannot carry a new order (B-2), worst first.
enum TripBookingProblem {
  /// No longer `open` (full, on the road, delivered or cancelled).
  closed,

  /// Its departure day is over ([tripHasLeft]).
  left,

  /// Less room left than the order needs.
  full,
}

/// Why [trip] cannot take an order of [quintals] now, or null when it can.
/// Without [quintals] any free room will do (the quantity is not chosen
/// yet). The server decides for good (`ORDER_TRIP_NOT_ELIGIBLE`,
/// `TRIP_CAPACITY_EXCEEDED`); this check lets the phone say so offline.
TripBookingProblem? tripBookingProblem(
  Trip trip, {
  required DateTime now,
  double? quintals,
}) {
  if (trip.status != TripStatus.open) return TripBookingProblem.closed;
  if (tripHasLeft(trip, now)) return TripBookingProblem.left;
  final room = tripRoomLeft(trip);
  // The same 0.001 slack as the place-order quantity note.
  final fits = quintals == null ? room > 0 : quintals <= room + 0.001;
  return fits ? null : TripBookingProblem.full;
}
