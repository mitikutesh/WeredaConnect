import 'package:flutter/foundation.dart' show debugPrint;

import 'package:wereda_connect/core/models/order.dart';
import 'package:wereda_connect/core/models/order_dropoff_note.dart';
import 'package:wereda_connect/core/models/place.dart';
import 'package:wereda_connect/shared/services/place_repository.dart';

/// LT-14 (live-trip.md section 10, order-lifecycle.md 2.9.0): the order's own
/// drop-off point, `deliveryLatitude` and `deliveryLongitude`. The buyer
/// picks a place on Place order (starting from the intent's place or the
/// registered place) and may change it until pickup, by the drop-off note's
/// rules. The live track, the truck's other stops, `truck_nearby` and the
/// delivered GPS check read it before the trip's destination. The client's
/// mirror of the server's `OrderDeliveryLocation`. Safe on web.

/// Ethiopia with a margin, the box the server accepts (the bounds of a
/// `location_ping` and of a trip's destination point).
const double deliveryLocationMinLatitude = 3;
const double deliveryLocationMaxLatitude = 15;
const double deliveryLocationMinLongitude = 32.5;
const double deliveryLocationMaxLongitude = 48.5;

/// A drop-off point (WGS84).
typedef DeliveryPoint = ({double latitude, double longitude});

/// Whether the server takes ([latitude], [longitude]) as a drop-off point.
bool isInDeliveryLocationBox(double latitude, double longitude) =>
    latitude >= deliveryLocationMinLatitude &&
    latitude <= deliveryLocationMaxLatitude &&
    longitude >= deliveryLocationMinLongitude &&
    longitude <= deliveryLocationMaxLongitude;

/// The point of a picked [place], or null when the place list has no
/// coordinates for it or they fall outside the box: the server would refuse
/// them for good, which would remove an order queued offline.
DeliveryPoint? deliveryPointOfPlace(Place? place) {
  final lat = place?.lat;
  final lng = place?.lng;
  if (lat == null || lng == null || !isInDeliveryLocationBox(lat, lng)) {
    return null;
  }
  return (latitude: lat, longitude: lng);
}

/// The order's own drop-off point, or null when it has none (or only half
/// of one).
DeliveryPoint? orderDeliveryPoint(Order order) {
  final lat = order.deliveryLatitude;
  final lng = order.deliveryLongitude;
  return lat == null || lng == null ? null : (latitude: lat, longitude: lng);
}

/// Whether [userId] may change the order's drop-off point now: the drop-off
/// note's rule (its buyer, on a trucked order, before pickup).
bool canEditDeliveryLocation(Order order, String userId) =>
    canEditDropoffNote(order, userId);

/// The place Place order starts from: the intent's place (its place id, else
/// its woreda name) when the order fills an intent, else the buyer's
/// registered place. Null when none is on the place list with a point in the
/// box. [places] must be loaded.
Place? defaultDeliveryPlace(
  PlaceRepository places, {
  String? intentPlaceId,
  String? intentWoreda,
  String? registeredWoreda,
}) {
  Place? usable(Place? place) =>
      deliveryPointOfPlace(place) == null ? null : place;
  Place? byName(String? name) {
    final trimmed = name?.trim() ?? '';
    return trimmed.isEmpty ? null : places.findByDisplayName(trimmed);
  }

  final intentPlace = intentPlaceId == null || intentPlaceId.isEmpty
      ? null
      : places.byId(intentPlaceId);
  return usable(intentPlace) ??
      usable(byName(intentWoreda)) ??
      usable(byName(registeredWoreda));
}

/// The place the order's drop-off point names (the point was taken from a
/// place, so normally that place), or null without a point or before
/// [places] is loaded.
Place? deliveryPlaceOf(Order order, PlaceRepository places) {
  final point = orderDeliveryPoint(order);
  if (point == null || !places.isLoaded) return null;
  return places.nearestTo(point.latitude, point.longitude);
}

/// Loads the bundled place list when one of [orders] has a drop-off point to
/// name and it is not loaded yet. Best effort: a list that cannot be read
/// leaves the names to the fallbacks.
Future<void> loadPlacesToName(
  Iterable<Order?> orders, {
  PlaceRepository? places,
}) async {
  final list = places ?? PlaceRepository.instance;
  if (list.isLoaded ||
      !orders.any((o) => o != null && orderDeliveryPoint(o) != null)) {
    return;
  }
  try {
    await list.ensureLoaded();
  } on Object catch (e) {
    debugPrint('[DeliveryLocation] place list unreadable: $e');
  }
}
