import 'dart:convert';

import 'package:flutter_test/flutter_test.dart';

import 'package:wereda_connect/core/models/crop_listing.dart';
import 'package:wereda_connect/core/models/trip.dart';
import 'package:wereda_connect/features/crop_listing/domain/trip_origin.dart';
import 'package:wereda_connect/shared/services/place_repository.dart';

/// B-2: the market a buyer opens from a trip holds the listings at the place
/// the truck starts from, matched by place id, not by the name as typed.
void main() {
  final places = PlaceRepository()
    ..loadFromStringForTest(
      jsonEncode([
        {
          'id': 'gn-bishoftu',
          'en': 'Bishoftu',
          'am': 'ቢሾፍቱ',
          'alts': ['Debre Zeit'],
          'population': 100000,
        },
        {'id': 'gn-adama', 'en': 'Nazrēt', 'display': 'Adama'},
        {'id': 'gn-adama-zone', 'en': 'Adama Zone'},
      ]),
    );
  final notLoaded = PlaceRepository();

  CropListing listing(String place, {String? placeId}) => CropListing()
    ..listingId = 'listing-1'
    ..locationName = place
    ..placeId = placeId;

  Trip trip(String from, {String? fromPlaceId}) => Trip()
    ..fromWoreda = from
    ..fromPlaceId = fromPlaceId;

  test('the same place under another name or script matches', () {
    final truck = trip('Bishoftu');
    expect(listingAtTripOrigin(listing('Debre Zeit'), truck, places), isTrue);
    expect(listingAtTripOrigin(listing('ቢሾፍቱ'), truck, places), isTrue);
    expect(listingAtTripOrigin(listing('bishoftu'), truck, places), isTrue);
  });

  test('another place does not', () {
    expect(
      listingAtTripOrigin(listing('Adama'), trip('Bishoftu'), places),
      isFalse,
    );
  });

  test('two known places are told apart by id, not by a shared word', () {
    // "Adama" and "Adama Zone" overlap by words but are two places.
    expect(
      listingAtTripOrigin(listing('Adama Zone'), trip('Adama'), places),
      isFalse,
    );
  });

  test('a picked place id wins over the name', () {
    expect(
      listingAtTripOrigin(
        listing('Bishoftu town', placeId: 'gn-bishoftu'),
        trip('Debre Zeit'),
        places,
      ),
      isTrue,
    );
  });

  test('an off-list name falls back to the matching service rule', () {
    // "Adama, Oromia" is not in the place list; its region suffix is cut.
    expect(
      listingAtTripOrigin(listing('Adama, Oromia'), trip('Adama'), places),
      isTrue,
    );
    expect(
      listingAtTripOrigin(listing('Gambo'), trip('Ambo'), places),
      isFalse,
    );
  });

  test('before the place list loads, names are compared', () {
    expect(
      listingAtTripOrigin(listing('Bishoftu'), trip('Bishoftu'), notLoaded),
      isTrue,
    );
    expect(
      listingAtTripOrigin(listing('Adama'), trip('Bishoftu'), notLoaded),
      isFalse,
    );
  });
}
