import 'dart:math' as math;

import 'package:wereda_connect/features/live_trip/domain/models/delivery_track.dart';

/// How current the truck's position is (specs/features/live-trip.md section
/// 1): the freshness chip and the map style follow it.
enum TrackFreshness {
  /// The last check-in is under 20 minutes old.
  fresh,

  /// The last check-in is 20 to 75 minutes old.
  normal,

  /// The last check-in is over 75 minutes old and sharing is not paused
  /// (usually no network on the road).
  late,

  /// The driver paused sharing.
  paused,

  /// The driver declined sharing: only the QR scans send a position.
  scansOnly,

  /// The delivery ended (delivered, cancelled or disputed).
  ended,
}

/// A check-in younger than this is [TrackFreshness.fresh].
const Duration trackFreshUnder = Duration(minutes: 20);

/// A check-in older than this is [TrackFreshness.late].
const Duration trackLateOver = Duration(minutes: 75);

/// The age of the truck's position at [now]: from the last check-in, else
/// from the pickup (the first check-in may still be on its way). Null when
/// the track has neither. Never negative (phone clocks drift).
Duration? trackFixAge(DeliveryTrack track, DateTime now) {
  final reference = track.lastFix?.capturedAt ?? track.pickedUpAt;
  if (reference == null) return null;
  final age = now.difference(reference);
  return age.isNegative ? Duration.zero : age;
}

/// The freshness band of [track] at [now]. Null before pickup
/// (`not_started`: there is no map yet) and for a sharing state this app
/// version does not know.
///
/// The age is counted on this device's clock, so a snapshot shown offline
/// turns late by itself. A pause that ran out since the server answered is
/// judged by the age like live sharing.
TrackFreshness? trackFreshness(DeliveryTrack track, DateTime now) {
  switch (track.sharing) {
    case TrackSharing.notStarted:
    case TrackSharing.unknown:
      return null;
    case TrackSharing.ended:
      return TrackFreshness.ended;
    case TrackSharing.declined:
      return TrackFreshness.scansOnly;
    case TrackSharing.paused:
      final until = track.pausedUntil;
      if (until == null || now.isBefore(until)) return TrackFreshness.paused;
    case TrackSharing.live:
      break;
  }
  final age = trackFixAge(track, now);
  if (age == null) return TrackFreshness.normal;
  if (age < trackFreshUnder) return TrackFreshness.fresh;
  if (age > trackLateOver) return TrackFreshness.late;
  return TrackFreshness.normal;
}

/// The halo around the truck marker: it grows with the age of the check-in
/// because the truck may have moved since, `min(2 + 40 x age in hours, 60)`
/// km. Null when no halo is drawn (paused: the truck stands still; ended).
double? trackHaloRadiusKm(TrackFreshness band, Duration fixAge) {
  if (band == TrackFreshness.paused || band == TrackFreshness.ended) {
    return null;
  }
  final hours = fixAge.inSeconds / Duration.secondsPerHour;
  return math.min(2 + 40 * hours, 60);
}
