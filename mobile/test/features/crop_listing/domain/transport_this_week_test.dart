import 'package:flutter_test/flutter_test.dart';

import 'package:wereda_connect/core/models/crop_listing.dart';
import 'package:wereda_connect/core/models/trip.dart';
import 'package:wereda_connect/features/crop_listing/domain/transport_this_week.dart';

/// Buyer · Find crops board: which open trips could carry a listing this
/// week ("Transport Thu, 18 q room", "2 trips this week", "No trip yet").
void main() {
  // Wednesday 29 Oct 2026, 09:00 in Addis Ababa.
  final now = DateTime.utc(2026, 10, 29, 6);

  CropListing listing({String from = 'Meki'}) => CropListing()
    ..listingId = 'l1'
    ..farmerId = 'f1'
    ..farmerName = 'Almaz'
    ..cropType = 'wheat'
    ..quantity = 28
    ..pricePerKg = 42
    ..location = (LatLng()
      ..latitude = 8.15
      ..longitude = 38.82)
    ..locationName = from
    ..description = ''
    ..status = ListingStatus.active
    ..createdAt = now
    ..updatedAt = now
    ..version = 1;

  Trip trip(
    String id, {
    String from = 'Meki',
    String to = 'Addis Ababa',
    int inDays = 1,
    double? remaining,
    TripStatus status = TripStatus.open,
  }) =>
      Trip()
        ..tripId = id
        ..truckerId = 't1'
        ..fromWoreda = from
        ..toWoreda = to
        // Local midnight of the day, as the app stores departure dates.
        ..departureDate = DateTime.utc(2026, 10, 28, 21).add(
          Duration(days: inDays),
        )
        ..capacity = 50
        ..remainingQuintals = remaining
        ..pricePerQuintal = 150
        ..status = status
        ..matchedListings = <String>[]
        ..createdAt = now
        ..updatedAt = now
        ..version = 1;

  test('keeps open trips from the wereda to the buyer, soonest first', () {
    final found = tripsThisWeekFor(
      listing(),
      [
        trip('later', inDays: 3),
        trip('sooner'),
        trip('other-route', to: 'Bahir Dar'),
        trip('other-origin', from: 'Gonder'),
      ],
      'Addis Ababa',
      now,
    );
    expect(found.map((t) => t.tripId), ['sooner', 'later']);
  });

  test('today counts; past, next week, full or not open do not', () {
    final found = tripsThisWeekFor(
      listing(),
      [
        trip('today', inDays: 0),
        trip('yesterday', inDays: -1),
        trip('in-a-week', inDays: 7),
        trip('full', remaining: 0),
        trip('started', status: TripStatus.inTransit),
      ],
      'Addis Ababa',
      now,
    );
    expect(found.map((t) => t.tripId), ['today']);
  });

  test('without a buyer wereda any destination counts', () {
    final found = tripsThisWeekFor(
      listing(),
      [trip('a', to: 'Bahir Dar'), trip('b')],
      null,
      now,
    );
    expect(found, hasLength(2));
  });

  test('a listing without a place has none', () {
    expect(
      tripsThisWeekFor(listing(from: ''), [trip('a')], null, now),
      isEmpty,
    );
  });

  // Market board: "Every farmer's listing, sorted by the best match for
  // Merkato."
  group('transportMatchRank', () {
    final trips = [
      trip('meki-this-week'),
      trip('koka-later', from: 'Koka', inDays: 12),
      trip('bichena-elsewhere', from: 'Bichena', to: 'Adama'),
    ];

    test('a trip this week to the buyer first, any other open trip next', () {
      expect(transportMatchRank(listing(), trips, 'Addis Ababa', now), 0);
      expect(
        transportMatchRank(listing(from: 'Koka'), trips, 'Addis Ababa', now),
        1,
      );
      expect(
        transportMatchRank(listing(from: 'Bichena'), trips, 'Addis Ababa', now),
        1,
      );
      expect(
        transportMatchRank(listing(from: 'Dessie'), trips, 'Addis Ababa', now),
        2,
      );
    });

    test('without a buyer place any destination this week ranks first', () {
      expect(
        transportMatchRank(listing(from: 'Bichena'), trips, null, now),
        0,
      );
    });

    test('past, full or started trips and a listing without a place rank last',
        () {
      final useless = [
        trip('yesterday', from: 'Koka', inDays: -1),
        trip('full', from: 'Koka', remaining: 0),
        trip('started', from: 'Koka', status: TripStatus.inTransit),
      ];
      expect(
        transportMatchRank(listing(from: 'Koka'), useless, 'Addis Ababa', now),
        2,
      );
      expect(transportMatchRank(listing(from: ''), trips, null, now), 2);
    });

    test('the sort keeps every listing, best first, the order inside a rank',
        () {
      final given = [
        listing(from: 'Dessie')..listingId = 'a',
        listing(from: 'Koka')..listingId = 'b',
        listing()..listingId = 'c',
        listing(from: 'Dessie')..listingId = 'd',
        listing()..listingId = 'e',
        listing(from: 'Bichena')..listingId = 'f',
      ];
      final sorted = sortedByTransportMatch(given, trips, 'Addis Ababa', now);
      expect(
        sorted.map((l) => l.listingId),
        ['c', 'e', 'b', 'f', 'a', 'd'],
      );
      // Unchanged input: the screen keeps the server's list as it was.
      expect(given.map((l) => l.listingId), ['a', 'b', 'c', 'd', 'e', 'f']);
    });
  });
}
