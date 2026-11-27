import 'package:wereda_connect/features/live_trip/domain/models/track_result.dart';
import 'package:wereda_connect/features/live_trip/domain/track_poller.dart';

/// Reads where the truck of a delivery is (`GET /deliveries/{id}/track`,
/// live-trip-api.md section 2), for the delivery's buyer, farmer and trucker
/// only (the server answers anyone else `404`).
///
/// Mobile (`TrackRepository`) and web (`WebTrackRepository`) implement it.
/// Both keep the last good answer per delivery for offline display.
abstract class BaseTrackRepository {
  /// Asks the server for the track of [deliveryId] as [userId]. Never throws:
  /// a failed read answers the stored snapshot (if any) with the reason.
  /// A `404` keeps the stored snapshot but reports [TrackFailure.notFound];
  /// a `500 TRACK_UNAVAILABLE` keeps it and reports
  /// [TrackFailure.unavailable].
  Future<TrackResult> fetchTrack(String deliveryId, {required String userId});

  /// The stored snapshot of [deliveryId] for [userId], without the network.
  Future<TrackSnapshot?> lastSnapshot(
    String deliveryId, {
    required String userId,
  });

  /// A [TrackPoller] over this repository for one track screen.
  TrackPoller poller(String deliveryId, {required String userId}) =>
      TrackPoller(repository: this, deliveryId: deliveryId, userId: userId);
}
