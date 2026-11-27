import 'package:wereda_connect/core/models/crop_listing.dart';
import 'package:wereda_connect/core/models/trip.dart';
import 'package:wereda_connect/shared/services/place_normalizer.dart';
import 'package:wereda_connect/shared/services/place_repository.dart';

/// B-2: whether [listing] is at the place [trip] starts from, for the market
/// a buyer opens from a trip. Compared by place id, so a wereda typed in
/// Amharic, Afaan Oromo or under an older name ("Debre Zeit") still meets
/// "Bishoftu": the picked id when the row has one, else the id the place
/// list finds for the name. When either side has no id (an off-list name,
/// or [places] not loaded yet) the names are compared as the matching
/// service compares them (exact or whole-word, [compareWoredaKeys]).
bool listingAtTripOrigin(
  CropListing listing,
  Trip trip,
  PlaceRepository places,
) {
  final tripPlace =
      trip.fromPlaceId ?? places.findByDisplayName(trip.fromWoreda)?.id;
  final listingPlace =
      listing.placeId ?? places.findByDisplayName(listing.locationName)?.id;
  if (tripPlace != null && listingPlace != null) {
    return tripPlace == listingPlace;
  }
  return compareWoredaKeys(
        normalizeWoreda(listing.locationName),
        normalizeWoreda(trip.fromWoreda),
      ) !=
      WoredaMatch.none;
}
