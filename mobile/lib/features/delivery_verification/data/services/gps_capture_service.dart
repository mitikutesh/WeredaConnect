import 'dart:async';
import 'dart:math';

import 'package:geolocator/geolocator.dart';

import 'package:wereda_connect/features/delivery_verification/data/delivery_constants.dart';

/// GPS coordinate capture with Ethiopian bounds validation
/// and graceful degradation for offline-first delivery verification.
///
/// GPS is evidence, not sole verification. Coordinates are stored
/// per-delivery for audit and proximity checks. Spoofing detection
/// is deferred to F-14 (Fraud Detection).
class GpsCaptureService {
  /// Capture current GPS position.
  ///
  /// Returns [GpsCaptureResult.success] with coordinates and accuracy,
  /// or [GpsCaptureResult.unavailable] with a descriptive reason.
  ///
  /// Failure modes:
  /// - Location services disabled
  /// - Permission denied
  /// - Permission permanently denied
  /// - Timeout (15 seconds)
  Future<GpsCaptureResult> captureCurrentPosition() async {
    final serviceEnabled = await Geolocator.isLocationServiceEnabled();
    if (!serviceEnabled) {
      return const GpsCaptureResult.unavailable(
        'Location services disabled',
      );
    }

    var permission = await Geolocator.checkPermission();
    if (permission == LocationPermission.denied) {
      permission = await Geolocator.requestPermission();
      if (permission == LocationPermission.denied) {
        return const GpsCaptureResult.unavailable(
          'Location permission denied',
        );
      }
    }
    if (permission == LocationPermission.deniedForever) {
      return const GpsCaptureResult.unavailable(
        'Location permission permanently denied',
      );
    }

    try {
      final position = await Geolocator.getCurrentPosition(
        locationSettings: const LocationSettings(
          accuracy: LocationAccuracy.high,
          timeLimit: DeliveryConstants.gpsCaptureTimeout,
        ),
      );
      return GpsCaptureResult.success(
        latitude: position.latitude,
        longitude: position.longitude,
        accuracy: position.accuracy,
      );
    } on LocationServiceDisabledException {
      return const GpsCaptureResult.unavailable(
        'Location services disabled',
      );
    } on TimeoutException {
      return const GpsCaptureResult.unavailable('GPS signal not available');
    } on Exception {
      return const GpsCaptureResult.unavailable('GPS signal not available');
    }
  }

  /// Validate coordinates are within Ethiopian bounds.
  ///
  /// Bounds: 3-15 N latitude, 33-48 E longitude.
  /// Returns false for coordinates outside Ethiopia.
  bool isWithinEthiopianBounds(double latitude, double longitude) {
    return latitude >= DeliveryConstants.minLatitude &&
        latitude <= DeliveryConstants.maxLatitude &&
        longitude >= DeliveryConstants.minLongitude &&
        longitude <= DeliveryConstants.maxLongitude;
  }

  /// Calculate Haversine distance between two points in km.
  ///
  /// Used for 5km proximity check at pickup/dropoff locations.
  double haversineDistance(
    double lat1,
    double lon1,
    double lat2,
    double lon2,
  ) {
    const earthRadiusKm = 6371.0;
    final dLat = _toRadians(lat2 - lat1);
    final dLon = _toRadians(lon2 - lon1);
    final a = sin(dLat / 2) * sin(dLat / 2) +
        cos(_toRadians(lat1)) *
            cos(_toRadians(lat2)) *
            sin(dLon / 2) *
            sin(dLon / 2);
    final c = 2 * atan2(sqrt(a), sqrt(1 - a));
    return earthRadiusKm * c;
  }

  /// Check if captured position is within threshold of reference point.
  ///
  /// [thresholdKm] defaults to 5.0 km per delivery verification contract.
  ProximityResult checkProximity(
    double capturedLat,
    double capturedLon,
    double refLat,
    double refLon, {
    double thresholdKm = DeliveryConstants.gpsProximityThresholdKm,
  }) {
    final distance =
        haversineDistance(capturedLat, capturedLon, refLat, refLon);
    return ProximityResult(
      isWithinThreshold: distance <= thresholdKm,
      distanceKm: distance,
    );
  }

  double _toRadians(double degrees) => degrees * pi / 180.0;
}

/// GPS capture result with graceful degradation.
class GpsCaptureResult {
  final bool isAvailable;
  final double? latitude;
  final double? longitude;

  /// Accuracy in meters.
  final double? accuracy;
  final String? unavailableReason;

  const GpsCaptureResult.success({
    required this.latitude,
    required this.longitude,
    required this.accuracy,
  })  : isAvailable = true,
        unavailableReason = null;

  const GpsCaptureResult.unavailable(this.unavailableReason)
      : isAvailable = false,
        latitude = null,
        longitude = null,
        accuracy = null;
}

/// Proximity check result.
class ProximityResult {
  final bool isWithinThreshold;
  final double distanceKm;

  const ProximityResult({
    required this.isWithinThreshold,
    required this.distanceKm,
  });
}
