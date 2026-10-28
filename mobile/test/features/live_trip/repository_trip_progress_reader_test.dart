import 'dart:convert';

import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/models/delivery.dart';
import 'package:wereda_connect/core/models/trip.dart';
import 'package:wereda_connect/features/delivery_verification/domain/repositories/base_delivery_repository.dart';
import 'package:wereda_connect/features/live_trip/data/repository_trip_progress_reader.dart';
import 'package:wereda_connect/features/trip_listing/domain/repositories/base_trip_listing_repository.dart';
import 'package:wereda_connect/shared/services/place_repository.dart';

class _MockDeliveries extends Mock implements BaseDeliveryRepository {}

class _MockTrips extends Mock implements BaseTripListingRepository {}

Delivery _delivery(String id, DeliveryStatus status) => Delivery()
  ..deliveryId = id
  ..tripId = 'trip-1'
  ..listingId = 'l-1'
  ..farmerId = 'f-1'
  ..farmerName = 'F'
  ..truckerId = 'trucker-1'
  ..truckerName = 'T'
  ..buyerId = 'b-1'
  ..buyerName = 'B'
  ..qrToken = 'qr'
  ..status = status
  ..createdAt = DateTime.utc(2026, 10, 28)
  ..updatedAt = DateTime.utc(2026, 10, 28)
  ..version = 1;

Trip _trip({String? toPlaceId, String toWoreda = 'Adama'}) => Trip()
  ..tripId = 'trip-1'
  ..truckerId = 'trucker-1'
  ..truckerName = 'T'
  ..truckerPhone = ''
  ..fromWoreda = 'Meki'
  ..toWoreda = toWoreda
  ..toPlaceId = toPlaceId
  ..departureDate = DateTime.utc(2026, 10, 28)
  ..capacity = 100
  ..pricePerQuintal = 10
  ..status = TripStatus.inTransit
  ..matchedListings = []
  ..createdAt = DateTime.utc(2026, 10, 28)
  ..updatedAt = DateTime.utc(2026, 10, 28)
  ..version = 1;

/// LT-08: the stop rule and the drop-off reference come from the local
/// deliveries and the trip's destination place.
void main() {
  late _MockDeliveries deliveries;
  late _MockTrips trips;
  late PlaceRepository places;

  setUp(() {
    deliveries = _MockDeliveries();
    trips = _MockTrips();
    places = PlaceRepository()
      ..loadFromStringForTest(
        jsonEncode([
          {
            'id': 'gn-1',
            'en': 'Nazrēt',
            'display': 'Adama',
            'lat': 8.54,
            'lng': 39.27,
          },
          {'id': 'gn-2', 'en': 'Nowhere'},
        ]),
      );
    when(() => deliveries.getDeliveriesByTrip('trip-1', userId: 'trucker-1'))
        .thenAnswer(
      (_) async => [
        _delivery('d-1', DeliveryStatus.delivered),
        _delivery('d-2', DeliveryStatus.inTransit),
      ],
    );
  });

  RepositoryTripProgressReader reader() => RepositoryTripProgressReader(
        deliveries: deliveries,
        trips: trips,
        places: places,
      );

  test('the legs carry the trip destination as their drop-off', () async {
    when(() => trips.getTripById('trip-1'))
        .thenAnswer((_) async => _trip(toPlaceId: 'gn-1'));

    final progress = await reader().read('trip-1', userId: 'trucker-1');

    expect(progress!.legs.map((l) => l.status), [
      DeliveryStatus.delivered,
      DeliveryStatus.inTransit,
    ]);
    expect(progress.allDone, isFalse);
    expect(progress.undeliveredDropoffs, [(latitude: 8.54, longitude: 39.27)]);
    expect(progress.anotherOnRoad('d-1'), isTrue);
    expect(progress.anotherOnRoad('d-2'), isFalse);
  });

  test('the destination name is the fallback; no point means none', () async {
    when(() => trips.getTripById('trip-1')).thenAnswer((_) async => _trip());
    final byName = await reader().read('trip-1', userId: 'trucker-1');
    expect(byName!.undeliveredDropoffs, hasLength(1));

    when(() => trips.getTripById('trip-1'))
        .thenAnswer((_) async => _trip(toPlaceId: 'gn-2', toWoreda: 'x'));
    final none = await reader().read('trip-1', userId: 'trucker-1');
    expect(none!.undeliveredDropoffs, isEmpty);
  });

  test('each leg carries when it was picked up and left the road', () async {
    when(() => trips.getTripById('trip-1')).thenAnswer((_) async => _trip());
    final delivered = _delivery('d-1', DeliveryStatus.delivered)
      ..pickedUpAt = DateTime.utc(2026, 10, 28, 5)
      ..confirmedAt = DateTime.utc(2026, 10, 28, 9);
    final cancelled = _delivery('d-3', DeliveryStatus.cancelled)
      ..updatedAt = DateTime.utc(2026, 10, 28, 10);
    when(() => deliveries.getDeliveriesByTrip('trip-1', userId: 'trucker-1'))
        .thenAnswer(
      (_) async =>
          [delivered, _delivery('d-2', DeliveryStatus.inTransit), cancelled],
    );

    final progress = await reader().read('trip-1', userId: 'trucker-1');

    expect(progress!.legs[0].pickedUpAt, DateTime.utc(2026, 10, 28, 5));
    expect(progress.legs[0].leftRoadAt, DateTime.utc(2026, 10, 28, 9));
    expect(progress.legs[1].leftRoadAt, isNull);
    expect(progress.legs[2].leftRoadAt, DateTime.utc(2026, 10, 28, 10));
    // d-3 was cancelled before pickup: it was never on the road.
    expect(
      progress.lastLeftRoadExcept('d-2'),
      DateTime.utc(2026, 10, 28, 9),
    );
    expect(progress.lastLeftRoadExcept('d-1'), isNull);
  });

  test('the trip of a delivery', () async {
    when(() => deliveries.getDeliveryById('d-2', userId: 'trucker-1'))
        .thenAnswer((_) async => _delivery('d-2', DeliveryStatus.pickedUp));
    expect(
      await reader().tripIdOfDelivery('d-2', userId: 'trucker-1'),
      'trip-1',
    );
  });
}
