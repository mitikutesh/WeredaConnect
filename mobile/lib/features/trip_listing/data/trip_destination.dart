import 'package:wereda_connect/shared/services/place_repository.dart';

/// The trip's destination coordinates for its create payload (trip-listing-api
/// 2.1.0): the live track's drop-off point and the buyer's arrival window are
/// worked out from them. From the place the trucker picked in Create trip;
/// empty when the destination was typed freely or the place list has no
/// coordinates for it, and the server then simply has no drop-off point.
Map<String, double> tripDestinationFields(
  String? toPlaceId, {
  PlaceRepository? places,
}) {
  if (toPlaceId == null || toPlaceId.isEmpty) return const {};
  final place = (places ?? PlaceRepository.instance).byId(toPlaceId);
  final lat = place?.lat;
  final lng = place?.lng;
  if (lat == null || lng == null) return const {};
  return {'destinationLatitude': lat, 'destinationLongitude': lng};
}
