import 'package:flutter_test/flutter_test.dart';

import 'package:wereda_connect/features/live_trip/domain/models/delivery_track.dart';
import 'package:wereda_connect/features/live_trip/domain/track_freshness.dart';

import '../live_trip_fixtures.dart';

/// LT-10: the freshness bands (fresh under 20 min, normal 20 to 75, late
/// over 75 and not paused, paused, scans only, ended) and the halo.
void main() {
  // The corridor's last check-in is at 08:42.
  final lastFixAt = at(8, 42);
  DateTime after(int minutes) => lastFixAt.add(Duration(minutes: minutes));

  group('trackFreshness', () {
    final live = corridorTrack();

    test('live: fresh under 20 minutes, normal to 75, late after', () {
      expect(trackFreshness(live, after(0)), TrackFreshness.fresh);
      expect(trackFreshness(live, after(19)), TrackFreshness.fresh);
      expect(trackFreshness(live, after(20)), TrackFreshness.normal);
      expect(trackFreshness(live, after(75)), TrackFreshness.normal);
      expect(trackFreshness(live, after(76)), TrackFreshness.late);
      expect(trackFreshness(live, after(600)), TrackFreshness.late);
    });

    test('a check-in from a phone clock ahead of ours is fresh', () {
      expect(trackFreshness(live, after(-5)), TrackFreshness.fresh);
    });

    test('live without a check-in yet counts from the pickup', () {
      final track = corridorTrack(trail: const []);
      expect(track.lastFix, isNull);
      expect(trackFreshness(track, at(5, 15)), TrackFreshness.fresh);
      expect(trackFreshness(track, at(7, 0)), TrackFreshness.late);
    });

    test('paused stays paused, never late, until the pause runs out', () {
      final paused = corridorTrack(
        sharing: TrackSharing.paused,
        pausedUntil: after(300),
      );
      expect(trackFreshness(paused, after(10)), TrackFreshness.paused);
      expect(trackFreshness(paused, after(299)), TrackFreshness.paused);
      // The pause ran out since the server answered: judged by the age.
      expect(trackFreshness(paused, after(301)), TrackFreshness.late);
    });

    test('paused without an end stays paused', () {
      final paused = corridorTrack(sharing: TrackSharing.paused);
      expect(trackFreshness(paused, after(500)), TrackFreshness.paused);
    });

    test('declined is scans only, whatever the age', () {
      final declined = corridorTrack(sharing: TrackSharing.declined);
      expect(trackFreshness(declined, after(5)), TrackFreshness.scansOnly);
      expect(trackFreshness(declined, after(500)), TrackFreshness.scansOnly);
    });

    test('ended is ended', () {
      final ended = DeliveryTrack.fromJson(endedTrackJson());
      expect(trackFreshness(ended, after(5)), TrackFreshness.ended);
    });

    test('before pickup and an unknown state have no band', () {
      expect(
        trackFreshness(
          corridorTrack(sharing: TrackSharing.notStarted),
          after(5),
        ),
        isNull,
      );
      expect(
        trackFreshness(corridorTrack(sharing: TrackSharing.unknown), after(5)),
        isNull,
      );
    });
  });

  group('trackFixAge', () {
    test('is the age of the last check-in, never negative', () {
      final live = corridorTrack();
      expect(trackFixAge(live, after(30)), const Duration(minutes: 30));
      expect(trackFixAge(live, after(-3)), Duration.zero);
    });

    test('is null without a check-in or a pickup time', () {
      final json = endedTrackJson()..['pickup'] = <String, dynamic>{};
      final ended = DeliveryTrack.fromJson(json);
      expect(trackFixAge(ended, after(1)), isNull);
    });
  });

  group('trackHaloRadiusKm', () {
    test('grows 2 km plus 40 km an hour, up to 60 km', () {
      expect(trackHaloRadiusKm(TrackFreshness.fresh, Duration.zero), 2);
      expect(
        trackHaloRadiusKm(TrackFreshness.normal, const Duration(minutes: 30)),
        22,
      );
      expect(
        trackHaloRadiusKm(TrackFreshness.late, const Duration(hours: 1)),
        42,
      );
      expect(
        trackHaloRadiusKm(TrackFreshness.late, const Duration(hours: 5)),
        60,
      );
      expect(
        trackHaloRadiusKm(TrackFreshness.scansOnly, const Duration(hours: 1)),
        42,
      );
    });

    test('no halo while paused or after the delivery', () {
      expect(
        trackHaloRadiusKm(TrackFreshness.paused, const Duration(hours: 1)),
        isNull,
      );
      expect(
        trackHaloRadiusKm(TrackFreshness.ended, const Duration(hours: 1)),
        isNull,
      );
    });
  });
}
