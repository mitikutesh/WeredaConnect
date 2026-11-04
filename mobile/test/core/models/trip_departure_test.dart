import 'package:flutter_test/flutter_test.dart';

import 'package:wereda_connect/core/models/trip.dart';
import 'package:wereda_connect/core/models/trip_departure.dart';

/// L-7 and B-2: a trip counts for its whole day in Ethiopia (UTC+3) and has
/// left from the next Ethiopia midnight, the same rule as the server's
/// browse cutoff and order guard (trip-listing-api.md 2.2.0).
void main() {
  // A trip picked for 4 Nov is stored as Ethiopia midnight, 21:00 UTC on 3 Nov.
  final for4Nov = DateTime.utc(2026, 11, 3, 21);

  Trip trip({
    String id = 'trip-1',
    DateTime? departure,
    TripStatus status = TripStatus.open,
    double capacity = 50,
    double? remaining,
    DateTime? createdAt,
  }) =>
      Trip()
        ..tripId = id
        ..truckerId = 'trucker-1'
        ..truckerName = 'Dawit'
        ..truckerPhone = ''
        ..fromWoreda = 'Meki'
        ..toWoreda = 'Addis Ababa'
        ..departureDate = departure ?? for4Nov
        ..capacity = capacity
        ..pricePerQuintal = 150
        ..remainingQuintals = remaining
        ..status = status
        ..matchedListings = <String>[]
        ..createdAt = createdAt ?? DateTime.utc(2026, 10, 20)
        ..updatedAt = DateTime.utc(2026, 10, 20)
        ..version = 1;

  group('tripListingCutoff', () {
    test('is the last Ethiopia midnight', () {
      expect(tripListingCutoff(DateTime.utc(2026, 11, 4, 8, 30)), for4Nov);
      expect(tripListingCutoff(DateTime.utc(2026, 11, 3, 21)), for4Nov);
      expect(
        tripListingCutoff(DateTime.utc(2026, 11, 4, 21)),
        DateTime.utc(2026, 11, 4, 21),
      );
    });

    test('reads a local time as its instant', () {
      final local = DateTime.utc(2026, 11, 4, 8, 30).toLocal();
      expect(tripListingCutoff(local), for4Nov);
    });
  });

  group('tripHasLeft', () {
    test('a trip for today stays all day, even past its stored instant', () {
      expect(tripHasLeft(trip(), DateTime.utc(2026, 11, 4, 9)), isFalse);
      expect(
        tripHasLeft(trip(), DateTime.utc(2026, 11, 4, 20, 59, 59)),
        isFalse,
      );
    });

    test('has left from the next Ethiopia midnight', () {
      expect(tripHasLeft(trip(), DateTime.utc(2026, 11, 4, 21)), isTrue);
    });

    test('a later trip has not left', () {
      final later = trip(departure: DateTime.utc(2026, 11, 6, 21));
      expect(tripHasLeft(later, DateTime.utc(2026, 11, 4, 21)), isFalse);
    });
  });

  group('tripBookingProblem', () {
    final now = DateTime.utc(2026, 11, 4, 9);

    test('an open trip on its day with room can take an order', () {
      expect(tripBookingProblem(trip(), now: now), isNull);
      expect(tripBookingProblem(trip(), now: now, quintals: 50), isNull);
    });

    test('a trip that is no longer open is closed, whatever its date', () {
      for (final status in [
        TripStatus.matched,
        TripStatus.inTransit,
        TripStatus.delivered,
        TripStatus.cancelled,
      ]) {
        expect(
          tripBookingProblem(trip(status: status), now: now),
          TripBookingProblem.closed,
          reason: status.name,
        );
      }
    });

    test('a trip whose day is over has left', () {
      expect(
        tripBookingProblem(trip(), now: DateTime.utc(2026, 11, 4, 21)),
        TripBookingProblem.left,
      );
    });

    test('the room left is checked against the quantity', () {
      final booked = trip(remaining: 12);
      expect(tripBookingProblem(booked, now: now, quintals: 12), isNull);
      expect(
        tripBookingProblem(booked, now: now, quintals: 12.5),
        TripBookingProblem.full,
      );
    });

    test('without a quantity any free room will do', () {
      expect(tripBookingProblem(trip(remaining: 0.5), now: now), isNull);
      expect(
        tripBookingProblem(trip(remaining: 0), now: now),
        TripBookingProblem.full,
      );
    });
  });

  group('upcomingTripsFirst', () {
    final now = DateTime.utc(2026, 11, 4, 9); // 4 Nov in Addis Ababa

    test('trips ahead by departure, then the rest newest departure first', () {
      final trips = [
        trip(id: 'passed-1nov', departure: DateTime.utc(2026, 10, 31, 21)),
        trip(id: 'next-week', departure: DateTime.utc(2026, 11, 10, 21)),
        trip(
          id: 'delivered',
          departure: DateTime.utc(2026, 11, 1, 21),
          status: TripStatus.delivered,
        ),
        trip(id: 'today'),
        trip(
          id: 'on-the-road',
          departure: DateTime.utc(2026, 11, 2, 21),
          status: TripStatus.inTransit,
        ),
        trip(
          id: 'cancelled-future',
          departure: DateTime.utc(2026, 11, 8, 21),
          status: TripStatus.cancelled,
        ),
        trip(
          id: 'full-tomorrow',
          departure: DateTime.utc(2026, 11, 4, 21),
          status: TripStatus.matched,
        ),
      ];

      expect(upcomingTripsFirst(trips, now).map((t) => t.tripId), [
        'on-the-road',
        'today',
        'full-tomorrow',
        'next-week',
        'cancelled-future',
        'delivered',
        'passed-1nov',
      ]);
    });

    test('keeps every trip', () {
      final trips = [
        trip(id: 'a', departure: DateTime.utc(2026, 10, 1, 21)),
        trip(id: 'b'),
      ];
      expect(upcomingTripsFirst(trips, now), hasLength(2));
    });

    test('the same departure keeps the newest trip first', () {
      final trips = [
        trip(id: 'older', createdAt: DateTime.utc(2026, 10, 5)),
        trip(id: 'newer', createdAt: DateTime.utc(2026, 10, 6)),
      ];
      expect(
        upcomingTripsFirst(trips, now).map((t) => t.tripId),
        ['newer', 'older'],
      );
    });
  });
}
