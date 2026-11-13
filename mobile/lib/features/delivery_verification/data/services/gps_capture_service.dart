import 'dart:async';
import 'dart:math';

import 'package:flutter/foundation.dart';
import 'package:geolocator/geolocator.dart';

import 'package:wereda_connect/core/location/location_permission.dart';
import 'package:wereda_connect/features/delivery_verification/data/delivery_constants.dart';

/// GPS coordinate capture with Ethiopian bounds validation
/// and graceful degradation for offline-first delivery verification.
///
/// GPS is evidence, not sole verification. Coordinates are stored
/// per-delivery for audit and proximity checks. Spoofing detection
/// is deferred to F-14 (Fraud Detection).
class GpsCaptureService {
  /// [isWeb] and [timeLimit] are for tests.
  GpsCaptureService({
    bool isWeb = kIsWeb,
    Duration timeLimit = DeliveryConstants.gpsCaptureTimeout,
  })  : _isWeb = isWeb,
        _timeLimit = timeLimit;

  final bool _isWeb;
  final Duration _timeLimit;

  /// Capture current GPS position. Never throws.
  ///
  /// Returns [GpsCaptureResult.success] with coordinates and accuracy,
  /// or [GpsCaptureResult.unavailable] with a descriptive reason.
  ///
  /// Failure modes:
  /// - Location services disabled
  /// - Permission denied (on web also a dismissed prompt, or one nobody
  ///   answers in time: the next capture asks again)
  /// - Permission permanently denied (on web: the site is blocked)
  /// - Timeout (15 seconds)
  ///
  /// Each wait ends within the time limit here in Dart (FUP-441): the fix,
  /// and on web the permission prompt ([requestLocationPermission]).
  /// `geolocator_web` hands the browser the limit in microseconds where it
  /// reads milliseconds (15 seconds become about 4.2 hours), so the plugin's
  /// own limit does not hold there.
  Future<GpsCaptureResult> captureCurrentPosition() async {
    try {
      return await _capture();
    } on Object catch (e) {
      debugPrint('[GpsCapture] location failed: $e');
      return const GpsCaptureResult.unavailable('GPS signal not available');
    }
  }

  Future<GpsCaptureResult> _capture() async {
    final serviceEnabled = await Geolocator.isLocationServiceEnabled();
    if (!serviceEnabled) {
      return const GpsCaptureResult.unavailable(
        'Location services disabled',
      );
    }

    var permission = await Geolocator.checkPermission();
    if (permission == LocationPermission.denied) {
      permission = await requestLocationPermission(
        isWeb: _isWeb,
        limit: _timeLimit,
      );
      if (permission == LocationPermission.denied) {
        return const GpsCaptureResult.unavailable(
          'Location permission denied',
        );
      }
    }
    if (permission == LocationPermission.deniedForever) {
      return const GpsCaptureResult.unavailable(
        GpsCaptureResult.permanentlyDeniedReason,
      );
    }

    try {
      final position = await Geolocator.getCurrentPosition(
        locationSettings: LocationSettings(
          accuracy: LocationAccuracy.high,
          timeLimit: _timeLimit,
        ),
      ).timeout(_timeLimit);
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

  /// Open this app's page in the phone's settings, where a location
  /// permission denied for good can be allowed again (FUP-285). False when
  /// it did not open (web has none: the browser's site settings hold it).
  Future<bool> openAppSettings() async {
    try {
      return await Geolocator.openAppSettings();
    } on Object {
      return false;
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

  /// The [unavailableReason] when the location permission was denied for
  /// good: only the app settings (the browser's site settings on web) can
  /// allow it again.
  static const permanentlyDeniedReason =
      'Location permission permanently denied';

  /// Whether the permission was denied for good ([permanentlyDeniedReason]).
  bool get permissionDeniedForever =>
      unavailableReason == permanentlyDeniedReason;
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
