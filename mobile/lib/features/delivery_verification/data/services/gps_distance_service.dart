import 'dart:math';

/// Pure Dart Haversine distance calculation utility.
///
/// Used for GPS proximity validation at pickup and delivery points.
/// The spec requires a 5km radius check, but this is warning-only
/// (not blocking) since GPS accuracy is limited in rural Ethiopia.
///
/// No external dependencies -- pure math only.
class GpsDistanceService {
  /// Earth radius in meters (WGS-84 mean radius).
  static const double earthRadiusMeters = 6371000;

  /// Calculate the great-circle distance between two points using the
  /// Haversine formula.
  ///
  /// Parameters:
  /// - [lat1], [lon1]: Latitude/longitude of point 1 in decimal degrees.
  /// - [lat2], [lon2]: Latitude/longitude of point 2 in decimal degrees.
  ///
  /// Returns the distance in **meters**.
  static double haversineDistance(
    double lat1,
    double lon1,
    double lat2,
    double lon2,
  ) {
    final dLat = _toRadians(lat2 - lat1);
    final dLon = _toRadians(lon2 - lon1);

    final a = sin(dLat / 2) * sin(dLat / 2) +
        cos(_toRadians(lat1)) *
            cos(_toRadians(lat2)) *
            sin(dLon / 2) *
            sin(dLon / 2);
    final c = 2 * atan2(sqrt(a), sqrt(1 - a));

    return earthRadiusMeters * c;
  }

  /// Check whether two GPS points are within [radiusMeters] of each other.
  ///
  /// Defaults to 5000 meters (5 km) per the delivery verification spec.
  /// Returns `true` if the distance is less than or equal to [radiusMeters].
  static bool isWithinRadius(
    double lat1,
    double lon1,
    double lat2,
    double lon2, {
    double radiusMeters = 5000,
  }) {
    return haversineDistance(lat1, lon1, lat2, lon2) <= radiusMeters;
  }

  static double _toRadians(double degrees) => degrees * pi / 180.0;
}
