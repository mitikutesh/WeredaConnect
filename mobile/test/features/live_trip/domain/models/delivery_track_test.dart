import 'package:flutter_test/flutter_test.dart';

import 'package:wereda_connect/features/live_trip/domain/models/delivery_track.dart';

import '../../live_trip_fixtures.dart';

/// LT-10: the track answer is parsed by hand with the contract's exact keys
/// (live-trip-api.md section 2).
void main() {
  group('DeliveryTrack.fromJson', () {
    test('reads every field of the contract example', () {
      final track = DeliveryTrack.fromJson(contractTrackJson());

      expect(track.deliveryId, 'del-101');
      expect(track.sharing, TrackSharing.live);
      expect(track.pausedUntil, isNull);
      expect(track.asOf, DateTime.utc(2026, 10, 27, 9, 10));
      expect(track.lastFix!.point, const GeoPoint(8.752, 38.979));
      expect(track.lastFix!.capturedAt, DateTime.utc(2026, 10, 27, 8, 42));
      expect(track.lastFix!.receivedAt, DateTime.utc(2026, 10, 27, 8, 42, 3));
      expect(track.lastFix!.accuracyM, 35);
      expect(track.trail, hasLength(1));
      expect(track.trail.single.source, 'scan');
      expect(track.trail.single.point, const GeoPoint(8.15, 38.82));
      expect(track.pauses.single.from, DateTime.utc(2026, 10, 27, 7, 55));
      expect(track.pauses.single.until, DateTime.utc(2026, 10, 27, 8, 25));
      expect(track.pickup, const GeoPoint(8.15, 38.82));
      expect(track.pickedUpAt, DateTime.utc(2026, 10, 27, 5, 5));
      expect(track.dropoff, const GeoPoint(9.03, 38.74));
      expect(track.dropoffNote, 'Merkato, near Anwar Mosque');
      expect(track.otherStops.single.kind, 'dropoff');
      expect(track.otherStops.single.point, const GeoPoint(8.8, 38.9));
      expect(track.otherStops.single.done, isFalse);
      expect(track.eta!.from, DateTime.utc(2026, 10, 27, 10, 20));
      expect(track.eta!.to, DateTime.utc(2026, 10, 27, 11));
      expect(track.remainingKm, 52.7);
      expect(track.summary, isNull);
    });

    test('an ended answer has no point and a summary', () {
      final track = DeliveryTrack.fromJson(endedTrackJson());

      expect(track.sharing, TrackSharing.ended);
      expect(track.lastFix, isNull);
      expect(track.trail, isEmpty);
      expect(track.pickup, isNull);
      expect(track.pickedUpAt, DateTime.utc(2026, 10, 27, 5, 5));
      expect(track.dropoff, isNull);
      expect(track.dropoffNote, isNull);
      expect(track.eta, isNull);
      expect(track.remainingKm, isNull);
      expect(track.summary!.distanceKm, 96.4);
      expect(track.summary!.durationMin, 335);
      expect(track.summary!.fixes, 7);
    });

    test('reads every sharing state, and an unknown one as unknown', () {
      for (final (wire, sharing) in [
        ('not_started', TrackSharing.notStarted),
        ('live', TrackSharing.live),
        ('paused', TrackSharing.paused),
        ('declined', TrackSharing.declined),
        ('ended', TrackSharing.ended),
        ('teleporting', TrackSharing.unknown),
      ]) {
        final json = contractTrackJson()..['sharing'] = wire;
        expect(DeliveryTrack.fromJson(json).sharing, sharing, reason: wire);
      }
    });

    test('a paused answer carries its end', () {
      final json = contractTrackJson()
        ..['sharing'] = 'paused'
        ..['pausedUntil'] = '2026-10-27T09:40:00.000Z';
      expect(
        DeliveryTrack.fromJson(json).pausedUntil,
        DateTime.utc(2026, 10, 27, 9, 40),
      );
    });

    test('skips a malformed check-in or stop and keeps the others', () {
      final json = contractTrackJson()
        ..['trail'] = [
          {'latitude': 8.1, 'capturedAt': '2026-10-27T05:00:00.000Z'},
          'not a fix',
          {
            'latitude': 8.2,
            'longitude': 38.9,
            'capturedAt': '2026-10-27T06:00:00.000Z',
            'receivedAt': '2026-10-27T06:00:01.000Z',
          },
        ]
        ..['otherStops'] = [
          {'kind': 'pickup', 'latitude': null, 'longitude': 38.9},
          {'kind': 'pickup', 'latitude': 8.4, 'longitude': 38.9, 'done': true},
        ];
      final track = DeliveryTrack.fromJson(json);
      expect(track.trail.single.point, const GeoPoint(8.2, 38.9));
      expect(track.otherStops.single.done, isTrue);
    });

    test('integer coordinates and distances are read as numbers', () {
      final json = contractTrackJson()
        ..['remainingKm'] = 52
        ..['dropoff'] = {'latitude': 9, 'longitude': 39, 'note': null};
      final track = DeliveryTrack.fromJson(json);
      expect(track.remainingKm, 52.0);
      expect(track.dropoff, const GeoPoint(9, 39));
    });

    test('throws a FormatException without the required fields', () {
      for (final key in ['deliveryId', 'sharing', 'asOf']) {
        final json = contractTrackJson()..remove(key);
        expect(
          () => DeliveryTrack.fromJson(json),
          throwsFormatException,
          reason: key,
        );
      }
    });
  });

  group('TrackFix.sentLate', () {
    test('a fix received more than 15 minutes after capture was sent late', () {
      expect(
        fix(8, 38, at(6, 0), receivedAt: at(6, 16)).sentLate,
        isTrue,
      );
      expect(
        fix(8, 38, at(6, 0), receivedAt: at(6, 15)).sentLate,
        isFalse,
      );
      expect(
        TrackFix(point: const GeoPoint(8, 38), capturedAt: at(6, 0)).sentLate,
        isFalse,
      );
    });
  });
}
