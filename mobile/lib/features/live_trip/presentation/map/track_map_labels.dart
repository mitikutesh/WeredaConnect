import 'dart:math' as math;
import 'dart:ui';

import 'package:wereda_connect/core/models/place.dart';
import 'package:wereda_connect/features/delivery_verification/data/services/gps_distance_service.dart';
import 'package:wereda_connect/features/live_trip/domain/models/delivery_track.dart';
import 'package:wereda_connect/features/live_trip/presentation/map/track_map_projection.dart';

/// Most town labels on a track map (spec section 8).
const int maxTrackMapLabels = 10;

/// A place and its great circle distance from a point.
class NearestPlace {
  const NearestPlace({required this.place, required this.distanceKm});

  final Place place;
  final double distanceKm;
}

/// The place of [places] nearest to [point] (the trip log names each
/// check-in after it), or null when no place has coordinates or the nearest
/// is farther than [maxKm]. Label it with `Place.displayName(languageCode)`.
NearestPlace? nearestPlace(
  Iterable<Place> places,
  GeoPoint point, {
  double? maxKm,
}) {
  final cosLatitude = math.cos(point.latitude * math.pi / 180);
  Place? best;
  var bestScore = double.infinity;
  for (final place in places) {
    final lat = place.lat;
    final lng = place.lng;
    if (lat == null || lng == null) continue;
    // Ranking on the flat approximation is exact enough at these distances
    // and avoids trigonometry per place.
    final dx = (lng - point.longitude) * cosLatitude;
    final dy = lat - point.latitude;
    final score = dx * dx + dy * dy;
    if (score < bestScore) {
      bestScore = score;
      best = place;
    }
  }
  if (best == null) return null;
  final distanceKm = GpsDistanceService.haversineDistance(
        point.latitude,
        point.longitude,
        best.lat!,
        best.lng!,
      ) /
      1000;
  if (maxKm != null && distanceKm > maxKm) return null;
  return NearestPlace(place: best, distanceKm: distanceKm);
}

/// The towns that may get a label on a map showing [bounds], in the order
/// the labels are placed: the town nearest to the truck's last check-in,
/// then the towns inside [bounds] with a known population, largest first,
/// then the town nearest to each earlier check-in, newest first. [fixes] are
/// in capture order. Each town comes once; only towns inside [bounds]; at
/// most [limit].
List<Place> trackLabelCandidates({
  required Iterable<Place> places,
  required GeoBounds bounds,
  required List<GeoPoint> fixes,
  int limit = 40,
}) {
  final all = places.toList(growable: false);
  final seen = <String>{};
  final out = <Place>[];

  bool add(Place place) {
    final lat = place.lat;
    final lng = place.lng;
    if (lat == null || lng == null) return false;
    if (!bounds.contains(GeoPoint(lat, lng))) return false;
    if (!seen.add(place.id)) return false;
    out.add(place);
    return out.length >= limit;
  }

  Place? nearestTo(GeoPoint fix) => nearestPlace(all, fix)?.place;

  if (fixes.isNotEmpty) {
    final last = nearestTo(fixes.last);
    if (last != null && add(last)) return out;
  }

  final towns = [
    for (final place in all)
      if (place.population > 0 &&
          place.lat != null &&
          place.lng != null &&
          bounds.contains(GeoPoint(place.lat!, place.lng!)))
        place,
  ]..sort((a, b) {
      final byPopulation = b.population.compareTo(a.population);
      return byPopulation != 0 ? byPopulation : a.id.compareTo(b.id);
    });
  for (final town in towns) {
    if (add(town)) return out;
  }

  for (final fix in fixes.reversed.skip(1)) {
    final near = nearestTo(fix);
    if (near != null && add(near)) return out;
  }
  return out;
}

/// A label to draw: [text] next to the dot at [anchor], inside [rect].
class TrackLabel {
  const TrackLabel({
    required this.text,
    required this.anchor,
    required this.rect,
    this.place,
  });

  final String text;
  final Offset anchor;
  final Rect rect;
  final Place? place;
}

/// A label that may be drawn, in placing order.
class TrackLabelCandidate {
  const TrackLabelCandidate({
    required this.text,
    required this.anchor,
    this.place,
  });

  final String text;
  final Offset anchor;
  final Place? place;
}

/// Greedy label placement: in order, a label goes right of its dot, else
/// left of it; it is dropped when its dot is off the canvas, when it would
/// leave the canvas, or when it would touch a label already placed or a
/// [reserved] area (pins, check-ins, the truck). A dot under a reserved area
/// (a town under the drop-off pin) gets its label just past that area.
/// At most [maxLabels]. [measure] gives the size of a label's text.
List<TrackLabel> placeTrackLabels({
  required List<TrackLabelCandidate> candidates,
  required Size canvas,
  required Size Function(String text) measure,
  List<Rect> reserved = const [],
  int maxLabels = maxTrackMapLabels,
  double gap = 2,
  double dotClearance = 4,
}) {
  final bounds = Offset.zero & canvas;
  final taken = [...reserved];
  final placed = <TrackLabel>[];
  for (final candidate in candidates) {
    if (placed.length >= maxLabels) break;
    if (candidate.text.isEmpty || !bounds.contains(candidate.anchor)) continue;
    final textSize = measure(candidate.text);
    final top = candidate.anchor.dy - textSize.height / 2;
    var start = candidate.anchor.dx + dotClearance;
    var end = candidate.anchor.dx - dotClearance;
    for (final area in reserved) {
      if (!area.contains(candidate.anchor)) continue;
      start = math.max(start, area.right + gap);
      end = math.min(end, area.left - gap);
    }
    final right = Rect.fromLTWH(start, top, textSize.width, textSize.height);
    final left = Rect.fromLTWH(
      end - textSize.width,
      top,
      textSize.width,
      textSize.height,
    );
    for (final rect in [right, left]) {
      if (!_inside(bounds, rect)) continue;
      final padded = rect.inflate(gap);
      if (taken.any(padded.overlaps)) continue;
      taken.add(rect);
      placed.add(
        TrackLabel(
          text: candidate.text,
          anchor: candidate.anchor,
          rect: rect,
          place: candidate.place,
        ),
      );
      break;
    }
  }
  return placed;
}

/// The town labels of a map: [trackLabelCandidates] for what [projection]
/// shows, named in [languageCode], placed by [placeTrackLabels].
List<TrackLabel> selectTrackLabels({
  required Iterable<Place> places,
  required TrackProjection projection,
  required List<GeoPoint> fixes,
  required String languageCode,
  required Size Function(String text) measure,
  List<Rect> reserved = const [],
  int maxLabels = maxTrackMapLabels,
}) {
  final towns = trackLabelCandidates(
    places: places,
    bounds: projection.bounds,
    fixes: fixes,
  );
  return placeTrackLabels(
    candidates: [
      for (final town in towns)
        TrackLabelCandidate(
          text: town.displayName(languageCode),
          anchor: projection.project(GeoPoint(town.lat!, town.lng!)),
          place: town,
        ),
    ],
    canvas: projection.size,
    measure: measure,
    reserved: reserved,
    maxLabels: maxLabels,
  );
}

bool _inside(Rect outer, Rect inner) =>
    inner.left >= outer.left &&
    inner.top >= outer.top &&
    inner.right <= outer.right &&
    inner.bottom <= outer.bottom;
