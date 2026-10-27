import 'package:wereda_connect/core/models/trip.dart';

/// ESC-15: the room left on [trip] in quintals. The server keeps
/// `remainingQuintals` once the trip has a booking; a trip nobody booked
/// (or a row from before bookings) has its whole [Trip.capacity] free.
double tripRoomLeft(Trip trip) => trip.remainingQuintals ?? trip.capacity;

/// Whether paid orders are booked on [trip] (its date, price and route are
/// then frozen, trip-listing-api.md 2.0.0).
bool tripHasBookings(Trip trip) => (trip.bookedQuintals ?? 0) > 0;

/// A quintal amount from the wire: a JSON number, or a numeric string on a
/// raw `/sync` row; null when absent or unreadable.
double? quintalsFromWire(Object? value) => switch (value) {
      final num n => n.toDouble(),
      final String s => double.tryParse(s),
      _ => null,
    };

/// A quintal amount for display, without a needless ".0".
String formatQuintals(double value) => value == value.roundToDouble()
    ? value.toStringAsFixed(0)
    : value.toStringAsFixed(1);
