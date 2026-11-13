import 'dart:async';

import 'package:flutter_test/flutter_test.dart';
import 'package:geolocator/geolocator.dart';
import 'package:plugin_platform_interface/plugin_platform_interface.dart';

import 'package:wereda_connect/features/delivery_verification/data/services/gps_capture_service.dart';

/// A location platform whose answers each test sets. A null [answer] or
/// [fix] never comes, like the web plugin's prompt and fix under its
/// microsecond time limit (FUP-441).
class _FakeGeolocator extends GeolocatorPlatform
    with MockPlatformInterfaceMixin {
  /// The permission as checked (on web: the site's state).
  LocationPermission state = LocationPermission.whileInUse;

  /// What a request answers. `geolocator_web` answers deniedForever for any
  /// error of its request, a dismissed prompt included.
  LocationPermission? answer = LocationPermission.whileInUse;

  /// The state once a request ran; unchanged when null.
  LocationPermission? stateAfterRequest;

  Position? fix = _position();
  Object? checkError;
  int checks = 0;
  int requests = 0;
  LocationSettings? settings;

  @override
  Future<bool> isLocationServiceEnabled() async => true;

  @override
  Future<LocationPermission> checkPermission() async {
    checks++;
    final error = checkError;
    if (error != null) throw error;
    return state;
  }

  @override
  Future<LocationPermission> requestPermission() {
    requests++;
    state = stateAfterRequest ?? state;
    final answer = this.answer;
    return answer == null
        ? Completer<LocationPermission>().future
        : Future.value(answer);
  }

  @override
  Future<Position> getCurrentPosition({LocationSettings? locationSettings}) {
    settings = locationSettings;
    final fix = this.fix;
    return fix == null ? Completer<Position>().future : Future.value(fix);
  }
}

Position _position() => Position(
      latitude: 9.03,
      longitude: 38.74,
      timestamp: DateTime.utc(2026, 11, 13, 6),
      accuracy: 12,
      altitude: 0,
      altitudeAccuracy: 0,
      heading: 0,
      headingAccuracy: 0,
      speed: 0,
      speedAccuracy: 0,
    );

void main() {
  group('captureCurrentPosition (FUP-441)', () {
    const limit = Duration(milliseconds: 50);
    late _FakeGeolocator platform;

    setUp(() {
      platform = _FakeGeolocator();
      GeolocatorPlatform.instance = platform;
    });

    GpsCaptureService web() => GpsCaptureService(isWeb: true, timeLimit: limit);
    GpsCaptureService phone() =>
        GpsCaptureService(isWeb: false, timeLimit: limit);

    // A capture that does not end fails the test here, not after hours.
    Future<GpsCaptureResult> capture(GpsCaptureService service) =>
        service.captureCurrentPosition().timeout(const Duration(seconds: 5));

    test('takes a high accuracy fix with its time limit', () async {
      final result = await capture(web());

      expect(result.isAvailable, isTrue);
      expect(result.latitude, 9.03);
      expect(result.longitude, 38.74);
      expect(result.accuracy, 12);
      expect(platform.settings?.accuracy, LocationAccuracy.high);
      expect(platform.settings?.timeLimit, limit);
      expect(platform.requests, 0);
    });

    test('a fix that never comes ends within the limit, web and phone',
        () async {
      platform.fix = null;

      for (final service in [web(), phone()]) {
        final result = await capture(service);
        expect(result.isAvailable, isFalse);
        expect(result.unavailableReason, 'GPS signal not available');
        expect(result.permissionDeniedForever, isFalse);
      }
    });

    test('web: a dismissed prompt is not blocked and is asked again', () async {
      platform
        ..state = LocationPermission.denied
        ..answer = LocationPermission.deniedForever;

      final result = await capture(web());

      expect(result.isAvailable, isFalse);
      expect(result.permissionDeniedForever, isFalse);
      expect(result.unavailableReason, 'Location permission denied');
      expect(platform.checks, 2, reason: 'the site state is read again');

      await capture(web());
      expect(platform.requests, 2);
    });

    test('web: a blocked site still reads as blocked', () async {
      platform
        ..state = LocationPermission.denied
        ..answer = LocationPermission.deniedForever
        ..stateAfterRequest = LocationPermission.deniedForever;

      final result = await capture(web());

      expect(result.isAvailable, isFalse);
      expect(result.permissionDeniedForever, isTrue);
    });

    test('web: a granted site whose first position failed takes the fix',
        () async {
      platform
        ..state = LocationPermission.denied
        ..answer = LocationPermission.deniedForever
        ..stateAfterRequest = LocationPermission.whileInUse;

      final result = await capture(web());

      expect(result.isAvailable, isTrue);
    });

    test('web: a prompt nobody answers ends within the limit, asked again',
        () async {
      platform
        ..state = LocationPermission.denied
        ..answer = null;

      final result = await capture(web());

      expect(result.isAvailable, isFalse);
      expect(result.permissionDeniedForever, isFalse);
      expect(result.unavailableReason, 'Location permission denied');

      await capture(web());
      expect(platform.requests, 2);
    });

    test('phone: a request denied for good is believed as it is', () async {
      platform
        ..state = LocationPermission.denied
        ..answer = LocationPermission.deniedForever;

      final result = await capture(phone());

      expect(result.permissionDeniedForever, isTrue);
      expect(platform.checks, 1);
    });

    test('a failing platform call ends as unavailable, never throws', () async {
      platform.checkError = const PermissionDefinitionsNotFoundException(
        'no permission in the manifest',
      );

      final result = await capture(phone());

      expect(result.isAvailable, isFalse);
      expect(result.unavailableReason, 'GPS signal not available');
    });
  });

  group('GpsCaptureService', () {
    late GpsCaptureService service;

    setUp(() {
      service = GpsCaptureService();
    });

    group('isWithinEthiopianBounds', () {
      test('returns true for Addis Ababa', () {
        expect(service.isWithinEthiopianBounds(9.0, 38.7), isTrue);
      });

      test('returns false for Cairo', () {
        expect(service.isWithinEthiopianBounds(30.0, 31.2), isFalse);
      });

      test('returns true for southern Ethiopia boundary', () {
        // Moyale area (southern border)
        expect(service.isWithinEthiopianBounds(3.5, 39.0), isTrue);
      });

      test('returns true for northern Ethiopia boundary', () {
        // Eritrea border area
        expect(service.isWithinEthiopianBounds(14.5, 38.0), isTrue);
      });

      test('returns false for latitude below southern bound', () {
        expect(service.isWithinEthiopianBounds(2.9, 38.0), isFalse);
      });

      test('returns false for latitude above northern bound', () {
        expect(service.isWithinEthiopianBounds(15.1, 38.0), isFalse);
      });

      test('returns false for longitude below western bound', () {
        expect(service.isWithinEthiopianBounds(9.0, 32.9), isFalse);
      });

      test('returns false for longitude above eastern bound', () {
        expect(service.isWithinEthiopianBounds(9.0, 48.1), isFalse);
      });

      test('returns true for exact boundary values', () {
        // Exact min/max should be inclusive
        expect(service.isWithinEthiopianBounds(3.0, 33.0), isTrue);
        expect(service.isWithinEthiopianBounds(15.0, 48.0), isTrue);
      });
    });

    group('haversineDistance', () {
      test('returns approximately correct distance for Addis to Bahir Dar', () {
        // Addis Ababa: 9.02, 38.75
        // Bahir Dar: 11.60, 37.38
        final distance = service.haversineDistance(9.02, 38.75, 11.60, 37.38);

        // Haversine distance ≈ 315-325km (straight line, not road distance)
        expect(distance, greaterThan(300));
        expect(distance, lessThan(340));
      });

      test('returns 0 for same point', () {
        final distance = service.haversineDistance(9.0, 38.7, 9.0, 38.7);
        expect(distance, 0.0);
      });

      test('returns correct distance for known short distance', () {
        // Two points ~1km apart near Addis
        // 0.01 degrees latitude ≈ 1.11 km
        final distance = service.haversineDistance(9.0, 38.7, 9.01, 38.7);
        expect(distance, closeTo(1.11, 0.1));
      });
    });

    group('checkProximity', () {
      test('returns within range for close points', () {
        // Two points ~1km apart
        final result = service.checkProximity(9.0, 38.7, 9.005, 38.7);

        expect(result.isWithinThreshold, isTrue);
        expect(result.distanceKm, lessThan(5.0));
      });

      test('returns out of range for far points', () {
        // Addis Ababa to Bahir Dar (~500km)
        final result = service.checkProximity(9.02, 38.75, 11.59, 37.39);

        expect(result.isWithinThreshold, isFalse);
        expect(result.distanceKm, greaterThan(5.0));
      });

      test('returns within range for points at exact threshold', () {
        // Points exactly at ~5km distance
        // 5km ≈ 0.045 degrees latitude
        final result = service.checkProximity(9.0, 38.7, 9.044, 38.7);

        expect(result.isWithinThreshold, isTrue);
        expect(result.distanceKm, lessThan(5.1));
      });

      test('uses custom threshold when provided', () {
        // Two points ~1km apart, threshold 0.5km
        final result = service.checkProximity(
          9.0,
          38.7,
          9.01,
          38.7,
          thresholdKm: 0.5,
        );

        expect(result.isWithinThreshold, isFalse);
        expect(result.distanceKm, greaterThan(0.5));
      });
    });
  });
}
