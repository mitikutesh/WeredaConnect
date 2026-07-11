import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/network/api_client.dart';
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
}
