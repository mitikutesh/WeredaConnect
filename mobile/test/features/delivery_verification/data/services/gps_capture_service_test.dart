import 'package:flutter_test/flutter_test.dart';

import 'package:wereda_connect/features/delivery_verification/data/services/gps_capture_service.dart';

void main() {
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
