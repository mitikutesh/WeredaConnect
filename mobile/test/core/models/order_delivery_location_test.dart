import 'dart:convert';

import 'package:flutter_test/flutter_test.dart';

import 'package:wereda_connect/core/models/order.dart';
import 'package:wereda_connect/core/models/order_delivery_location.dart';
import 'package:wereda_connect/core/models/place.dart';
import 'package:wereda_connect/shared/services/place_repository.dart';

/// A few towns of the bundled list, with their centroids.
final String _placesJson = jsonEncode([
  {
    'id': 'gn-344979',
    'en': 'Addis Ababa',
    'am': 'አዲስ አበባ',
    'region': 'Addis Ababa',
    'lat': 9.02497,
    'lng': 38.74689,
    'population': 2757729,
  },
  {
    'id': 'gn-344979b',
    'en': 'Adama',
    'alts': ['Nazret'],
    'region': 'Oromia',
    'lat': 8.54,
    'lng': 39.27,
    'population': 220212,
  },
  {
    'id': 'gn-331180',
    'en': 'Mojo',
    'region': 'Oromia',
    'lat': 8.58712,
    'lng': 39.12131,
    'population': 39316,
  },
  // A place without coordinates: never a drop-off point.
  {'id': 'gn-1', 'en': 'Nowhere Kebele', 'region': 'Amhara'},
]);

PlaceRepository _places() =>
    PlaceRepository()..loadFromStringForTest(_placesJson);

Order _order({
  OrderStatus status = OrderStatus.accepted,
  bool trip = true,
  double? lat,
  double? lng,
}) =>
    Order()
      ..orderId = 'order-1'
      ..buyerId = 'buyer-1'
      ..farmerId = 'farmer-1'
      ..status = status
      ..tripId = trip ? 'trip-1' : null
      ..truckerId = trip ? 'trucker-1' : null
      ..deliveryLatitude = lat
      ..deliveryLongitude = lng;

/// LT-14: the client's rules for the order's own drop-off point: the box the
/// server accepts, which picked places give a point, who may change it, the
/// place Place order starts from, and the name a point shows.
void main() {
  group('the box', () {
    test('Ethiopia with a margin, edges included', () {
      expect(isInDeliveryLocationBox(9.02, 38.75), isTrue);
      expect(isInDeliveryLocationBox(3, 48.5), isTrue);
      expect(isInDeliveryLocationBox(15, 32.5), isTrue);
      expect(isInDeliveryLocationBox(2.99, 38.75), isFalse);
      expect(isInDeliveryLocationBox(9.02, 48.51), isFalse);
      expect(isInDeliveryLocationBox(51.5, -0.12), isFalse);
    });
  });

  group('the point of a place', () {
    test('a place with coordinates gives its centroid', () {
      final adama = _places().byId('gn-344979b');
      expect(deliveryPointOfPlace(adama), (latitude: 8.54, longitude: 39.27));
    });

    test('no place, no coordinates, or outside the box gives none', () {
      expect(deliveryPointOfPlace(null), isNull);
      expect(deliveryPointOfPlace(_places().byId('gn-1')), isNull);
      expect(
        deliveryPointOfPlace(
          const Place(id: 'gn-x', en: 'London', lat: 51.5, lng: -0.12),
        ),
        isNull,
      );
    });
  });

  group('the order', () {
    test('has a point only with both fields', () {
      expect(
        orderDeliveryPoint(_order(lat: 8.54, lng: 39.27)),
        (latitude: 8.54, longitude: 39.27),
      );
      expect(orderDeliveryPoint(_order(lat: 8.54)), isNull);
      expect(orderDeliveryPoint(_order()), isNull);
    });

    test('only its buyer changes it, on a trucked order, before pickup', () {
      expect(canEditDeliveryLocation(_order(), 'buyer-1'), isTrue);
      expect(
        canEditDeliveryLocation(_order(status: OrderStatus.funded), 'buyer-1'),
        isTrue,
      );
      expect(canEditDeliveryLocation(_order(), 'farmer-1'), isFalse);
      expect(canEditDeliveryLocation(_order(), 'trucker-1'), isFalse);
      expect(canEditDeliveryLocation(_order(trip: false), 'buyer-1'), isFalse);
      expect(
        canEditDeliveryLocation(
          _order(status: OrderStatus.inTransit),
          'buyer-1',
        ),
        isFalse,
      );
    });

    test('names its point after the nearest place, once the list is loaded',
        () {
      final order = _order(lat: 8.58712, lng: 39.12131);

      expect(deliveryPlaceOf(order, PlaceRepository()), isNull);
      expect(deliveryPlaceOf(order, _places())?.id, 'gn-331180');
      expect(deliveryPlaceOf(_order(), _places()), isNull);
    });
  });

  group('the place Place order starts from', () {
    test("the intent's place id first", () {
      expect(
        defaultDeliveryPlace(
          _places(),
          intentPlaceId: 'gn-331180',
          intentWoreda: 'Adama',
          registeredWoreda: 'Addis Ababa',
        )?.id,
        'gn-331180',
      );
    });

    test("then the intent's woreda name, by any of its names", () {
      expect(
        defaultDeliveryPlace(
          _places(),
          intentWoreda: 'Nazret',
          registeredWoreda: 'Addis Ababa',
        )?.id,
        'gn-344979b',
      );
    });

    test('then the registered place', () {
      expect(
        defaultDeliveryPlace(_places(), registeredWoreda: ' Addis Ababa ')?.id,
        'gn-344979',
      );
    });

    test('an off-list or pointless place is skipped', () {
      expect(
        defaultDeliveryPlace(
          _places(),
          intentWoreda: 'Nowhere Kebele',
          registeredWoreda: 'Mojo',
        )?.id,
        'gn-331180',
      );
      expect(
        defaultDeliveryPlace(_places(), registeredWoreda: 'Off-list town'),
        isNull,
      );
      expect(defaultDeliveryPlace(_places()), isNull);
    });
  });

  group('the nearest place', () {
    test('is the closest town with coordinates, within 30 km', () {
      final places = _places();
      expect(places.nearestTo(8.55, 39.26)?.en, 'Adama');
      expect(places.nearestTo(9.03, 38.74)?.en, 'Addis Ababa');
      // Djibouti city, far from every town on the list.
      expect(places.nearestTo(11.59, 43.15), isNull);
      expect(PlaceRepository().nearestTo(9.03, 38.74), isNull);
    });
  });

  test('loadPlacesToName loads only for an order with a point', () async {
    final places = PlaceRepository();

    await loadPlacesToName([_order(), null], places: places);
    expect(places.isLoaded, isFalse);
  });
}
