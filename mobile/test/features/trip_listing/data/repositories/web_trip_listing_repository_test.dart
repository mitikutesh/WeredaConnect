import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/network/api_client.dart';
import 'package:wereda_connect/core/network/api_exception.dart';
import 'package:wereda_connect/core/models/trip.dart';
import 'package:wereda_connect/features/trip_listing/data/repositories/web_trip_listing_repository.dart';

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

    test('returns null on failure (offline / 404)', () async {
      when(() => api.get<Map<String, dynamic>>('/trips/trip-x'))
          .thenThrow(Exception('offline'));
      expect(await repository.getTripById('trip-x'), isNull);
    });
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
  });
}
