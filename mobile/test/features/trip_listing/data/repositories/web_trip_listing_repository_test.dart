import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/network/api_client.dart';
import 'package:wereda_connect/core/network/api_exception.dart';
import 'package:wereda_connect/core/models/trip.dart';
import 'package:wereda_connect/features/trip_listing/data/repositories/web_trip_listing_repository.dart';
import 'package:wereda_connect/shared/services/place_repository.dart';

class _MockApiClient extends Mock implements ApiClient {}

void main() {
  group('WebTripListingRepository.getTripById', () {
    late _MockApiClient api;
    late WebTripListingRepository repository;

    setUp(() {
      api = _MockApiClient();
      repository = WebTripListingRepository(api: api);
    });

    test('parses the {"trip": {...}} envelope', () async {
      when(() => api.get<Map<String, dynamic>>('/trips/trip-1')).thenAnswer(
        (_) async => {
          'trip': {
            'tripId': 'trip-1',
            'truckerId': 'trucker-1',
            'fromWoreda': 'Adama',
            'toWoreda': 'Bishoftu',
            'departureDate': '2026-07-13T00:00:00.000Z',
            'capacity': 50,
            'pricePerQuintal': 200,
            'status': 'open',
            'createdAt': '2026-07-11T00:00:00.000Z',
            'updatedAt': '2026-07-11T00:00:00.000Z',
            'version': 1,
          },
        },
      );

      final trip = await repository.getTripById('trip-1');
      expect(trip, isNotNull);
      expect(trip!.tripId, 'trip-1');
      expect(trip.fromWoreda, 'Adama');
      expect(trip.toWoreda, 'Bishoftu');
    });

    test('ESC-31: reads the owner reputation copy when the row carries it',
        () async {
      Map<String, dynamic> row(Map<String, dynamic> extra) => {
            'trip': {
              'tripId': 'trip-1',
              'truckerId': 'trucker-1',
              'fromWoreda': 'Adama',
              'toWoreda': 'Bishoftu',
              'departureDate': '2026-07-13T00:00:00.000Z',
              'capacity': 50,
              'pricePerQuintal': 200,
              'status': 'open',
              'createdAt': '2026-07-11T00:00:00.000Z',
              'updatedAt': '2026-07-11T00:00:00.000Z',
              'version': 1,
              ...extra,
            },
          };
      when(() => api.get<Map<String, dynamic>>('/trips/trip-1')).thenAnswer(
        (_) async => row({
          'ownerTier': 'good',
          'ownerEffectiveRating': 4.7,
          'ownerRatingCount': 3,
          'ownerReliabilityPct': 91,
          'ownerReputationAt': '2026-11-07T09:00:00.000Z',
        }),
      );
      final trip = (await repository.getTripById('trip-1'))!;
      expect(trip.ownerTier, 'good');
      expect(trip.ownerEffectiveRating, 4.7);
      expect(trip.ownerRatingCount, 3);
      expect(trip.ownerReliabilityPct, 91);
      expect(trip.ownerReputationAt, DateTime.utc(2026, 11, 7, 9));

      // The public reputation flag is off: no owner field, as today.
      when(() => api.get<Map<String, dynamic>>('/trips/trip-1'))
          .thenAnswer((_) async => row(const {}));
      final plain = (await repository.getTripById('trip-1'))!;
      expect(plain.ownerTier, isNull);
      expect(plain.ownerEffectiveRating, isNull);
      expect(plain.ownerRatingCount, isNull);
      expect(plain.ownerReliabilityPct, isNull);
      expect(plain.ownerReputationAt, isNull);
    });

    test('returns null on failure (offline / 404)', () async {
      when(() => api.get<Map<String, dynamic>>('/trips/trip-x'))
          .thenThrow(Exception('offline'));
      expect(await repository.getTripById('trip-x'), isNull);
    });
  });

  // The server answers 20 trips a page: a buyer must see ALL open trips
  // (role rule), a trucker all of their own, so every page is read.
  test('open and own trips follow nextToken to the last page', () async {
    final api = _MockApiClient();
    Map<String, dynamic> trip(String id) => {
          'tripId': id,
          'truckerId': 'trucker-1',
          'fromWoreda': 'Debre Birhan',
          'toWoreda': 'Dire Dawa',
          'departureDate': '2026-11-01T06:00:00.000Z',
          'capacity': 40,
          'pricePerQuintal': 92,
          'status': 'open',
          'createdAt': '2026-10-28T00:00:00.000Z',
          'updatedAt': '2026-10-28T00:00:00.000Z',
          'version': 1,
        };
    when(
      () => api.get<Map<String, dynamic>>(
        '/trips',
        queryParameters: any(named: 'queryParameters'),
      ),
    ).thenAnswer((inv) async {
      final params =
          inv.namedArguments[#queryParameters] as Map<String, dynamic>;
      return switch (params['nextToken']) {
        null => {
            'trips': [trip('p1')],
            'nextToken': 'page-2',
          },
        'page-2' => {
            'trips': [trip('p2')],
            'nextToken': 'page-3',
          },
        _ => {
            'trips': [trip('p3')],
          },
      };
    });
    // Before the trips' day, so the open-trips cutoff (L-7) keeps them.
    final repository = WebTripListingRepository(
      api: api,
      clock: () => DateTime.utc(2026, 10, 28),
    );

    final open = await repository.getOpenTrips();
    expect(open.map((t) => t.tripId), ['p1', 'p2', 'p3']);
    final own = await repository.getTripsByTrucker('trucker-1');
    expect(own.map((t) => t.tripId), ['p1', 'p2', 'p3']);
  });

  // L-7: a buyer is never offered a truck whose departure day is over in
  // Ethiopia, even from a server that still lists it; the trucker's own
  // list keeps it (shown as "Date passed").
  group('open trips from the start of today in Ethiopia', () {
    Map<String, dynamic> trip(String id, String departure) => {
          'tripId': id,
          'truckerId': 'trucker-1',
          'fromWoreda': 'Meki',
          'toWoreda': 'Addis Ababa',
          'departureDate': departure,
          'capacity': 40,
          'pricePerQuintal': 92,
          'status': 'open',
          'createdAt': '2026-10-28T00:00:00.000Z',
          'updatedAt': '2026-10-28T00:00:00.000Z',
          'version': 1,
        };
    final trips = [
      trip('yesterday', '2026-11-02T21:00:00.000Z'), // 3 Nov in Addis
      trip('today', '2026-11-03T21:00:00.000Z'), // 4 Nov
      trip('tomorrow', '2026-11-04T21:00:00.000Z'), // 5 Nov
    ];

    late _MockApiClient api;
    setUp(() {
      api = _MockApiClient();
      when(
        () => api.get<Map<String, dynamic>>(
          '/trips',
          queryParameters: any(named: 'queryParameters'),
        ),
      ).thenAnswer((_) async => {'trips': trips});
    });

    for (final (label, now) in [
      ('just after Ethiopia midnight', DateTime.utc(2026, 11, 3, 21, 0, 1)),
      ('late on the day', DateTime.utc(2026, 11, 4, 20, 59, 59)),
    ]) {
      test('$label: the browse drops yesterday, keeps today', () async {
        final repository = WebTripListingRepository(api: api, clock: () => now);

        final open = await repository.getOpenTrips();

        expect(open.map((t) => t.tripId), ['today', 'tomorrow']);
      });
    }

    test('from the next Ethiopia midnight today has left too', () async {
      final repository = WebTripListingRepository(
        api: api,
        clock: () => DateTime.utc(2026, 11, 4, 21),
      );

      final open = await repository.getOpenTrips();

      expect(open.map((t) => t.tripId), ['tomorrow']);
    });

    test("the trucker's own list keeps every departure", () async {
      final repository = WebTripListingRepository(
        api: api,
        clock: () => DateTime.utc(2026, 11, 4, 12),
      );

      final own = await repository.getTripsByTrucker('trucker-1');

      expect(own.map((t) => t.tripId), ['yesterday', 'today', 'tomorrow']);
    });
  });

  // Create trip offers the first recent route as "Repeat Last Trip", so the
  // web list is newest first whatever order the API answers in.
  test('getRecentRoutes lists the newest route first', () async {
    final api = _MockApiClient();
    Map<String, dynamic> trip(String id, String from, String created) => {
          'tripId': id,
          'truckerId': 'trucker-1',
          'fromWoreda': from,
          'toWoreda': 'Addis Ababa',
          'departureDate': '2026-10-30T00:00:00.000Z',
          'capacity': 50,
          'pricePerQuintal': 150,
          'status': 'open',
          'createdAt': created,
          'updatedAt': created,
          'version': 1,
        };
    when(
      () => api.get<Map<String, dynamic>>(
        '/trips',
        queryParameters: any(named: 'queryParameters'),
      ),
    ).thenAnswer(
      (_) async => {
        'trips': [
          trip('t-old', 'Adama', '2026-10-01T00:00:00.000Z'),
          trip('t-new', 'Meki', '2026-10-28T00:00:00.000Z'),
          trip('t-mid', 'Ziway', '2026-10-15T00:00:00.000Z'),
        ],
      },
    );

    final routes =
        await WebTripListingRepository(api: api).getRecentRoutes('trucker-1');

    expect(routes.map((r) => r.from), ['Meki', 'Ziway', 'Adama']);
  });

  // SIM-38: a delivery's tripId must be a UUID, so a web trip id has to be one.
  test('createTrip uses a UUID trip id', () async {
    final api = _MockApiClient();
    Map<String, dynamic>? sentBody;
    when(
      () => api.post<Map<String, dynamic>>(
        '/sync',
        data: any<dynamic>(named: 'data'),
      ),
    ).thenAnswer((invocation) async {
      sentBody = invocation.namedArguments[#data] as Map<String, dynamic>;
      return {
        'results': [
          {'status': 'success'},
        ],
      };
    });

    final trip = await WebTripListingRepository(api: api).createTrip(
      truckerId: 'trucker-1',
      truckerName: 'Trucker',
      truckerPhone: '+251900100002',
      fromWoreda: 'Adama',
      toWoreda: 'Bishoftu',
      departureDate: DateTime.utc(2026, 10),
      capacity: 50,
      pricePerQuintal: 200,
    );

    expect(
      trip.tripId,
      matches(
        RegExp(
          r'^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$',
        ),
      ),
    );
    final op = (sentBody!['operations'] as List).first as Map<String, dynamic>;
    expect(op['entityId'], trip.tripId);
  });

  // trip-listing-api 2.1.0: the picked destination's point goes with the
  // create, so the live track has a drop-off point.
  test('createTrip sends the picked destination point', () async {
    PlaceRepository.instance.loadFromStringForTest(
      File('test/fixtures/woredas_sample.json').readAsStringSync(),
    );
    final api = _MockApiClient();
    Map<String, dynamic>? sentBody;
    when(
      () => api.post<Map<String, dynamic>>(
        '/sync',
        data: any<dynamic>(named: 'data'),
      ),
    ).thenAnswer((invocation) async {
      sentBody = invocation.namedArguments[#data] as Map<String, dynamic>;
      return {
        'results': [
          {'status': 'success'},
        ],
      };
    });

    await WebTripListingRepository(api: api).createTrip(
      truckerId: 'trucker-1',
      truckerName: 'Trucker',
      truckerPhone: '+251900100002',
      fromWoreda: 'Bishoftu',
      toWoreda: 'Adama',
      departureDate: DateTime.utc(2026, 11, 2),
      capacity: 50,
      pricePerQuintal: 200,
      toPlaceId: 'gn-adama',
    );

    final op = (sentBody!['operations'] as List).first as Map<String, dynamic>;
    final data = op['data'] as Map<String, dynamic>;
    expect(data['destinationLatitude'], 8.54);
    expect(data['destinationLongitude'], 39.27);
  });

  // ESC-12: cancel, start and deliver used to ignore the /sync result, so a
  // refused or raced transition looked done.
  group('trip transitions check the sync result', () {
    late _MockApiClient api;
    late WebTripListingRepository repository;
    late int tripReads;

    setUp(() {
      api = _MockApiClient();
      repository = WebTripListingRepository(api: api);
      tripReads = 0;
      when(() => api.get<Map<String, dynamic>>('/trips/trip-1')).thenAnswer(
        (_) async {
          tripReads++;
          return {
            'trip': {
              'tripId': 'trip-1',
              'truckerId': 'trucker-1',
              'fromWoreda': 'Adama',
              'toWoreda': 'Bishoftu',
              'departureDate': '2026-07-13T00:00:00.000Z',
              'capacity': 50,
              'pricePerQuintal': 200,
              'status': 'matched',
              'createdAt': '2026-07-11T00:00:00.000Z',
              'updatedAt': '2026-07-11T00:00:00.000Z',
              'version': 2,
            },
          };
        },
      );
    });

    void answerSync(Map<String, dynamic> result) {
      when(
        () => api.post<Map<String, dynamic>>(
          '/sync',
          data: any<dynamic>(named: 'data'),
        ),
      ).thenAnswer(
        (_) async => {
          'results': [result],
        },
      );
    }

    final transitions =
        <String, Future<Object?> Function(WebTripListingRepository)>{
      'cancel': (r) => r.cancelTrip(tripId: 'trip-1', truckerId: 'trucker-1'),
      'start': (r) => r.startTrip(tripId: 'trip-1', truckerId: 'trucker-1'),
      'deliver': (r) =>
          r.markDelivered(tripId: 'trip-1', truckerId: 'trucker-1'),
    };

    for (final entry in transitions.entries) {
      test('${entry.key}: a refusal throws and reads nothing after', () async {
        answerSync({
          'status': 'error',
          'message': "TRIP_INVALID_TRANSITION: Cannot go from 'open'.",
        });

        await expectLater(
          entry.value(repository),
          throwsA(isA<SyncRejectedException>()),
        );
        // Only the version read before the post.
        expect(tripReads, 1);
      });

      test('${entry.key}: a version race throws SyncConflictException',
          () async {
        answerSync({'status': 'conflict', 'message': 'Version conflict.'});

        await expectLater(
          entry.value(repository),
          throwsA(isA<SyncConflictException>()),
        );
      });
    }

    test('a successful start still returns the server trip', () async {
      answerSync({'status': 'success', 'serverVersion': 3});

      final trip = await repository.startTrip(
        tripId: 'trip-1',
        truckerId: 'trucker-1',
      );

      expect(trip.tripId, 'trip-1');
      expect(trip.status, TripStatus.matched);
      expect(tripReads, 2);
    });

    // ESC-17 (trip-listing-api.md 2.4.0): the one cancel path. Mobile queues
    // the same operation: an update to cancelled at the version the server
    // holds, never a delete.
    test('cancel sends an update to cancelled at the version read', () async {
      answerSync({'status': 'success', 'serverVersion': 3});

      await repository.cancelTrip(tripId: 'trip-1', truckerId: 'trucker-1');

      final body = verify(
        () => api.post<Map<String, dynamic>>(
          '/sync',
          data: captureAny<dynamic>(named: 'data'),
        ),
      ).captured.single as Map<String, dynamic>;
      final op = (body['operations'] as List).single as Map<String, dynamic>;
      expect(op['entityType'], 'trip');
      expect(op['entityId'], 'trip-1');
      expect(op['action'], 'update');
      final data = op['data'] as Map<String, dynamic>;
      expect(data['status'], 'cancelled');
      expect(data['version'], 2);
    });

    // ESC-15: like mobile, web starts a partly booked open trip; it has no
    // local status gate and leaves `open -> inTransit` to the server, which
    // allows it when the trip has bookings.
    test('start of an open trip with bookings sends inTransit', () async {
      when(() => api.get<Map<String, dynamic>>('/trips/trip-1')).thenAnswer(
        (_) async => {
          'trip': {
            'tripId': 'trip-1',
            'truckerId': 'trucker-1',
            'fromWoreda': 'Adama',
            'toWoreda': 'Bishoftu',
            'departureDate': '2026-07-13T00:00:00.000Z',
            'capacity': 50,
            'pricePerQuintal': 200,
            'status': 'open',
            'bookedQuintals': 20,
            'remainingQuintals': 30,
            'createdAt': '2026-07-11T00:00:00.000Z',
            'updatedAt': '2026-07-11T00:00:00.000Z',
            'version': 2,
          },
        },
      );
      answerSync({'status': 'success', 'serverVersion': 3});

      await repository.startTrip(tripId: 'trip-1', truckerId: 'trucker-1');

      final body = verify(
        () => api.post<Map<String, dynamic>>(
          '/sync',
          data: captureAny<dynamic>(named: 'data'),
        ),
      ).captured.single as Map<String, dynamic>;
      final op = (body['operations'] as List).single as Map<String, dynamic>;
      expect(op['action'], 'update');
      final data = op['data'] as Map<String, dynamic>;
      expect(data['status'], 'inTransit');
      expect(data['version'], 2);
    });
  });

  // ESC-12 part B (sync-protocol.md 1.17.0): web has no queue, so a refusal
  // is a failure that carries the server message for its own text.
  group('WebTripListingRepository part B refusals (FUP-01)', () {
    late _MockApiClient api;
    late WebTripListingRepository repository;

    setUp(() {
      api = _MockApiClient();
      repository = WebTripListingRepository(api: api);
      when(() => api.get<Map<String, dynamic>>('/trips/trip-1')).thenAnswer(
        (_) async => {
          'trip': {
            'tripId': 'trip-1',
            'truckerId': 'trucker-1',
            'fromWoreda': 'Adama',
            'toWoreda': 'Bishoftu',
            'departureDate': '2026-10-30T00:00:00.000Z',
            'capacity': 50,
            'pricePerQuintal': 200,
            'status': 'open',
            'createdAt': '2026-10-01T00:00:00.000Z',
            'updatedAt': '2026-10-01T00:00:00.000Z',
            'version': 2,
          },
        },
      );
    });

    void refuseWith(String message) {
      when(
        () => api.post<Map<String, dynamic>>(
          '/sync',
          data: any<dynamic>(named: 'data'),
        ),
      ).thenAnswer(
        (_) async => {
          'results': [
            {'status': 'error', 'message': message},
          ],
        },
      );
    }

    for (final message in [
      'REPUTATION_RESTRICTED: restricted',
      'APP_UPDATE_REQUIRED: minimum 2.0.0',
    ]) {
      test('create: ${message.split(':').first} throws with the message',
          () async {
        refuseWith(message);

        await expectLater(
          repository.createTrip(
            truckerId: 'trucker-1',
            truckerName: 'Trucker',
            truckerPhone: '+251900100002',
            fromWoreda: 'Adama',
            toWoreda: 'Bishoftu',
            departureDate: DateTime.utc(2026, 10, 30),
            capacity: 50,
            pricePerQuintal: 200,
          ),
          throwsA(
            isA<SyncRejectedException>()
                .having((e) => e.message, 'message', message),
          ),
        );
      });
    }

    for (final message in [
      'TRIP_HAS_BOOKINGS: booked',
      'APP_UPDATE_REQUIRED: minimum 2.0.0',
    ]) {
      test('cancel: ${message.split(':').first} throws with the message',
          () async {
        refuseWith(message);

        await expectLater(
          repository.cancelTrip(tripId: 'trip-1', truckerId: 'trucker-1'),
          throwsA(
            isA<SyncRejectedException>()
                .having((e) => e.message, 'message', message),
          ),
        );
      });
    }
  });
}
