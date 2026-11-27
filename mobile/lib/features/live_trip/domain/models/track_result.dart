import 'package:flutter/foundation.dart';

import 'package:wereda_connect/features/live_trip/domain/models/delivery_track.dart';

/// The last good track answer of a delivery and when this device fetched it.
/// Kept for offline display: the map shows it with its [ageAt].
@immutable
class TrackSnapshot {
  const TrackSnapshot({required this.track, required this.fetchedAt});

  final DeliveryTrack track;

  /// When the answer arrived on this device (UTC).
  final DateTime fetchedAt;

  /// How old the snapshot is at [now]; never negative.
  Duration ageAt(DateTime now) {
    final age = now.difference(fetchedAt);
    return age.isNegative ? Duration.zero : age;
  }
}

/// Why a track read did not bring a new answer.
enum TrackFailure {
  /// No network (or it timed out). The snapshot, if any, is shown.
  offline,

  /// `404 DELIVERY_NOT_FOUND`: the viewer is not a party to the delivery, or
  /// it is gone. The stored snapshot is kept, but the screen should say the
  /// trip cannot be found.
  notFound,

  /// `500 TRACK_UNAVAILABLE` (or another 5xx): the server could not read the
  /// track right now. The snapshot is kept.
  unavailable,

  /// Anything else (a refused session, an unexpected answer).
  failed,
}

/// One read of a delivery's track: the track to show and, when the read did
/// not refresh it, why.
@immutable
class TrackResult {
  const TrackResult({this.snapshot, this.failure, this.fromNetwork = false});

  /// The newest answer this device has: the one just fetched, or the stored
  /// one when the read failed. Null when there is none yet.
  final TrackSnapshot? snapshot;

  /// Why this read did not bring a new answer; null when it did, and for the
  /// stored snapshot shown while the first read is under way.
  final TrackFailure? failure;

  /// Whether [snapshot] came from the server in this read.
  final bool fromNetwork;

  DeliveryTrack? get track => snapshot?.track;
}
