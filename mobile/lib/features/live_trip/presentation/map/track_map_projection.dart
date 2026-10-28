import 'dart:math' as math;
import 'dart:ui';

import 'package:wereda_connect/features/live_trip/domain/models/delivery_track.dart';

/// Kilometres per degree of latitude (and of longitude at the equator).
const double kmPerDegree = 111.32;

/// A latitude and longitude box.
class GeoBounds {
  const GeoBounds({
    required this.south,
    required this.west,
    required this.north,
    required this.east,
  });

  final double south;
  final double west;
  final double north;
  final double east;

  bool contains(GeoPoint p) =>
      p.latitude >= south &&
      p.latitude <= north &&
      p.longitude >= west &&
      p.longitude <= east;

  @override
  String toString() => 'GeoBounds($south, $west, $north, $east)';
}

/// An equirectangular projection fitted to a set of points (the trail, the
/// stops and the drop-off) inside a canvas, with padding (LT-10, spec
/// section 8). Longitudes are scaled by the cosine of the mean latitude of
/// the points' bounds, so a kilometre is as long east-west as north-south;
/// at Ethiopia's latitudes the error over a trip is far under a pixel.
class TrackProjection {
  TrackProjection._({
    required this.size,
    required double centerLatitude,
    required double centerLongitude,
    required double cosLatitude,
    required double scale,
  })  : _centerLatitude = centerLatitude,
        _centerLongitude = centerLongitude,
        _cos = cosLatitude,
        _scale = scale;

  /// Fits [points] into [size], keeping [padding] pixels free on every side.
  /// Points closer together than [minSpanKm] (or a single point) get a view
  /// [minSpanKm] wide centred on them, so a truck that just left shows its
  /// surroundings. Null when there is no point to show (before pickup, or an
  /// ended track, which carries no coordinates).
  static TrackProjection? fit(
    Iterable<GeoPoint> points,
    Size size, {
    double padding = 28,
    double minSpanKm = 20,
  }) {
    if (points.isEmpty || size.isEmpty) return null;
    var south = double.infinity;
    var north = double.negativeInfinity;
    var west = double.infinity;
    var east = double.negativeInfinity;
    for (final p in points) {
      south = math.min(south, p.latitude);
      north = math.max(north, p.latitude);
      west = math.min(west, p.longitude);
      east = math.max(east, p.longitude);
    }
    final centerLatitude = (south + north) / 2;
    final centerLongitude = (west + east) / 2;
    final cosLatitude = math.cos(centerLatitude * math.pi / 180);
    final minSpan = minSpanKm / kmPerDegree;
    final spanX = math.max((east - west) * cosLatitude, minSpan);
    final spanY = math.max(north - south, minSpan);
    final usableWidth = math.max(size.width - 2 * padding, 1.0);
    final usableHeight = math.max(size.height - 2 * padding, 1.0);
    final scale = math.min(usableWidth / spanX, usableHeight / spanY);
    return TrackProjection._(
      size: size,
      centerLatitude: centerLatitude,
      centerLongitude: centerLongitude,
      cosLatitude: cosLatitude,
      scale: scale,
    );
  }

  /// The canvas the projection was fitted to.
  final Size size;

  final double _centerLatitude;
  final double _centerLongitude;
  final double _cos;

  /// Pixels per degree of latitude.
  final double _scale;

  /// Pixels per kilometre, in both directions.
  double get pixelsPerKm => _scale / kmPerDegree;

  /// Where [p] falls on the canvas.
  Offset project(GeoPoint p) => Offset(
        size.width / 2 + (p.longitude - _centerLongitude) * _cos * _scale,
        size.height / 2 - (p.latitude - _centerLatitude) * _scale,
      );

  /// The point under [offset].
  GeoPoint unproject(Offset offset) => GeoPoint(
        _centerLatitude - (offset.dy - size.height / 2) / _scale,
        _centerLongitude + (offset.dx - size.width / 2) / (_cos * _scale),
      );

  /// What the whole canvas shows, padding included: the "padded bounds"
  /// that pick the town labels.
  GeoBounds get bounds {
    final topLeft = unproject(Offset.zero);
    final bottomRight = unproject(Offset(size.width, size.height));
    return GeoBounds(
      south: bottomRight.latitude,
      west: topLeft.longitude,
      north: topLeft.latitude,
      east: bottomRight.longitude,
    );
  }
}
