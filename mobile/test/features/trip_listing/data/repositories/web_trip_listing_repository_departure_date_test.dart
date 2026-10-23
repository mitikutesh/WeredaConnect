import 'package:flutter_test/flutter_test.dart';
import 'package:intl/intl.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/network/api_client.dart';
import 'package:wereda_connect/core/utils/ethiopia_time.dart';
import 'package:wereda_connect/features/trip_listing/data/repositories/web_trip_listing_repository.dart';

class _MockApiClient extends Mock implements ApiClient {}

/// SIM-50: the server stores the trucker's pick (25 Oct in Addis Ababa) as
/// the UTC instant 2026-10-24T21:00:00.000Z. Web showed it as 24 Oct.
void main() {
  const stored = '2026-10-24T21:00:00.000Z';

  Map<String, dynamic> tripsResponse() => {
        'trips': [
          {
            'tripId': 'trip-1',
            'truckerId': 'trucker-1',
            'truckerName': 'Trucker',
            'fromWoreda': 'Adama',
            'toWoreda': 'Bishoftu',
            'departureDate': stored,
            'capacity': 50,
            'pricePerQuintal': 200,
            'status': 'open',
            'createdAt': '2026-10-20T08:00:00.000Z',
            'updatedAt': '2026-10-20T08:00:00.000Z',
            'version': 1,
          },
        ],
      };

  late _MockApiClient api;
  late WebTripListingRepository repository;

  setUp(() {
    api = _MockApiClient();
    repository = WebTripListingRepository(api: api);
  });

  void expectPickedDay(DateTime departure) {
    // The parse keeps the exact instant: matching compares it with now and
    // with the buyer's deadline.
    expect(departure.isAtSameMomentAs(DateTime.parse(stored)), isTrue);
    expect(dayInEthiopia(departure), DateTime.utc(2026, 10, 25));
    expect(
      DateFormat.yMMMd().format(dayInEthiopia(departure)),
      'Oct 25, 2026',
    );
  }

  test('trucker My Trips shows the day the trucker picked', () async {
    when(
      () => api.get<Map<String, dynamic>>(
        '/trips',
        queryParameters: {'truckerId': 'trucker-1'},
      ),
    ).thenAnswer((_) async => tripsResponse());

    final trips = await repository.getTripsByTrucker('trucker-1');

    expect(trips, hasLength(1));
    expectPickedDay(trips.single.departureDate);
  });

  test('buyer browse shows the day the trucker picked', () async {
    when(
      () => api.get<Map<String, dynamic>>(
        '/trips',
        queryParameters: {'status': 'open'},
      ),
    ).thenAnswer((_) async => tripsResponse());

    final trips = await repository.getOpenTrips();

    expect(trips, hasLength(1));
    expectPickedDay(trips.single.departureDate);
  });
}
