import 'dart:io';

import 'package:flutter_test/flutter_test.dart';

import 'package:wereda_connect/features/trip_listing/data/trip_destination.dart';
import 'package:wereda_connect/shared/services/place_repository.dart';

/// trip-listing-api 2.1.0: a trip carries its destination's point so the
/// live track has a drop-off point and the buyer an arrival window.
void main() {
  final places = PlaceRepository()
    ..loadFromStringForTest(
      File('test/fixtures/woredas_sample.json').readAsStringSync(),
    );

  test('a picked place gives its coordinates', () {
    expect(tripDestinationFields('gn-adama', places: places), {
      'destinationLatitude': 8.54,
      'destinationLongitude': 39.27,
    });
  });

  test('a typed destination or an unknown place sends nothing', () {
    expect(tripDestinationFields(null, places: places), isEmpty);
    expect(tripDestinationFields('', places: places), isEmpty);
    expect(tripDestinationFields('gn-nowhere', places: places), isEmpty);
  });
}
