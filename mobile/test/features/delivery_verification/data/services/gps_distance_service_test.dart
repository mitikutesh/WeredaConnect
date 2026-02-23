import 'package:flutter_test/flutter_test.dart';

import 'package:wereda_connect/features/delivery_verification/data/services/gps_distance_service.dart';

void main() {
  group('GpsDistanceService', () {
    group('haversineDistance', () {
      test('Addis Ababa to Bahir Dar straight-line is approximately 322 km',
          () {
        // Addis Ababa: 9.0192, 38.7525
        // Bahir Dar:   11.5742, 37.3614
        // Note: road distance is ~451 km, but Haversine gives the
        // great-circle (straight-line) distance which is ~322 km.
        final distance = GpsDistanceService.haversineDistance(
          9.0192,
          38.7525,
          11.5742,
          37.3614,
        );
        expect(distance, closeTo(322000, 5000));
      });

      test('same point returns 0', () {
        final distance = GpsDistanceService.haversineDistance(
          9.0192,
          38.7525,
          9.0192,
          38.7525,
        );
        expect(distance, equals(0.0));
      });

      test('antipodal points return approximately half Earth circumference',
          () {
        // North pole to south pole (two antipodal points on the axis).
        final distance = GpsDistanceService.haversineDistance(
          90.0,
          0.0,
          -90.0,
          0.0,
        );
        // Half Earth circumference ~= pi * 6371000 ≈ 20015 km.
        const halfCircumference = 3.14159265358979 * 6371000;
        expect(distance, closeTo(halfCircumference, 100));
      });

      test('short distance within Addis Ababa (Bole to Merkato)', () {
        // Bole area:    9.0100, 38.7800
        // Merkato area: 9.0280, 38.7400
        final distance = GpsDistanceService.haversineDistance(
          9.0100,
          38.7800,
          9.0280,
          38.7400,
        );
        // ~4.5 km — should be between 4 km and 5.5 km.
        expect(distance, greaterThan(4000));
        expect(distance, lessThan(5500));
      });

      test('symmetry: distance(A, B) equals distance(B, A)', () {
        final ab = GpsDistanceService.haversineDistance(
          9.0192,
          38.7525,
          11.5742,
          37.3614,
        );
        final ba = GpsDistanceService.haversineDistance(
          11.5742,
          37.3614,
          9.0192,
          38.7525,
        );
        expect(ab, equals(ba));
      });

      test('across the prime meridian', () {
        // London: 51.5074, -0.1278
        // Paris:  48.8566, 2.3522
        final distance = GpsDistanceService.haversineDistance(
          51.5074,
          -0.1278,
          48.8566,
          2.3522,
        );
        // London to Paris ~= 343 km.
        expect(distance, closeTo(343000, 5000));
      });
    });

    group('isWithinRadius', () {
      test('returns true for points within default 5 km radius', () {
        // Two points ~2 km apart in Addis Ababa.
        final result = GpsDistanceService.isWithinRadius(
          9.0192,
          38.7525,
          9.0100,
          38.7600,
        );
        expect(result, isTrue);
      });

      test('returns false for points outside default 5 km radius', () {
        // Addis Ababa to Bahir Dar — well over 5 km.
        final result = GpsDistanceService.isWithinRadius(
          9.0192,
          38.7525,
          11.5742,
          37.3614,
        );
        expect(result, isFalse);
      });

      test('returns true for same point (distance = 0)', () {
        final result = GpsDistanceService.isWithinRadius(
          9.0192,
          38.7525,
          9.0192,
          38.7525,
        );
        expect(result, isTrue);
      });

      test('returns true when exactly at the boundary', () {
        // Use a custom radius equal to the computed distance.
        final distance = GpsDistanceService.haversineDistance(
          9.0192,
          38.7525,
          9.0280,
          38.7400,
        );
        final result = GpsDistanceService.isWithinRadius(
          9.0192,
          38.7525,
          9.0280,
          38.7400,
          radiusMeters: distance,
        );
        expect(result, isTrue);
      });

      test('custom radius: 1 km', () {
        // Two points ~4.5 km apart — should be outside 1 km.
        final result = GpsDistanceService.isWithinRadius(
          9.0100,
          38.7800,
          9.0280,
          38.7400,
          radiusMeters: 1000,
        );
        expect(result, isFalse);
      });

      test('custom radius: 500 km', () {
        // Addis Ababa to Bahir Dar (~451 km) — should be inside 500 km.
        final result = GpsDistanceService.isWithinRadius(
          9.0192,
          38.7525,
          11.5742,
          37.3614,
          radiusMeters: 500000,
        );
        expect(result, isTrue);
      });
    });
  });
}
