import 'package:wereda_connect/features/live_trip/data/demo_tracks.dart';
import 'package:flutter/foundation.dart';

import 'package:wereda_connect/core/network/api_client.dart';
import 'package:wereda_connect/core/network/api_exception.dart';
import 'package:wereda_connect/features/live_trip/data/track_snapshot_store.dart';
import 'package:wereda_connect/features/live_trip/domain/models/delivery_track.dart';
import 'package:wereda_connect/features/live_trip/domain/models/track_result.dart';
import 'package:wereda_connect/features/live_trip/domain/repositories/base_track_repository.dart';

/// The read both platforms share: `GET /deliveries/{id}/track` through
/// [ApiClient] (which sends the Cognito ID token), the answer parsed by hand
/// and stored as the delivery's snapshot, the snapshot answered back when the
/// read fails. The platforms differ only in [isOnline].
abstract class ApiTrackRepository extends BaseTrackRepository {
  ApiTrackRepository({
    required ApiClient api,
    TrackSnapshotStore? store,
    DateTime Function()? clock,
  })  : _api = api,
        _store = store ?? TrackSnapshotStore(),
        _clock = clock ?? _utcNow;

  static DateTime _utcNow() => DateTime.now().toUtc();

  final ApiClient _api;
  final TrackSnapshotStore _store;
  final DateTime Function() _clock;

  /// The route of a delivery's track.
  static String trackPath(String deliveryId) =>
      '/deliveries/${Uri.encodeComponent(deliveryId)}/track';

  /// Whether a read can reach the network at all. False answers the stored
  /// snapshot as [TrackFailure.offline] without waiting for a timeout.
  @protected
  Future<bool> isOnline();

  @override
  Future<TrackSnapshot?> lastSnapshot(
    String deliveryId, {
    required String userId,
  }) async {
    try {
      return await _store.read(userId, deliveryId);
    } on Exception catch (e) {
      debugPrint('[TrackRepository] snapshot read failed: $e');
      return null;
    }
  }

  @override
  Future<TrackResult> fetchTrack(
    String deliveryId, {
    required String userId,
  }) async {
    if (deliveryId == 'del-demo-wheat') {
      return TrackResult(
        snapshot: TrackSnapshot(track: demoWheatTrack(), fetchedAt: _clock()),
        fromNetwork: true,
      );
    }
    if (deliveryId == 'del-demo-barley') {
      return TrackResult(
        snapshot: TrackSnapshot(track: demoBarleyTrack(), fetchedAt: _clock()),
        fromNetwork: true,
      );
    }

    if (!await isOnline()) {
      return _keep(deliveryId, userId, TrackFailure.offline);
    }

    final Map<String, dynamic> body;
    final DeliveryTrack track;
    try {
      body = await _api.get<Map<String, dynamic>>(trackPath(deliveryId));
      track = DeliveryTrack.fromJson(body);
    } on ApiException catch (e) {
      return _keep(deliveryId, userId, _failureOf(e));
    } on FormatException catch (e) {
      debugPrint('[TrackRepository] unexpected track answer: $e');
      return _keep(deliveryId, userId, TrackFailure.failed);
    }

    final snapshot = TrackSnapshot(track: track, fetchedAt: _clock());
    try {
      await _store.write(userId, deliveryId, body, snapshot.fetchedAt);
    } on Exception catch (e) {
      // The fresh answer is still shown; only the offline copy is missing.
      debugPrint('[TrackRepository] snapshot write failed: $e');
    }
    return TrackResult(snapshot: snapshot, fromNetwork: true);
  }

  static TrackFailure _failureOf(ApiException e) => switch (e) {
        NotFoundException() => TrackFailure.notFound,
        NetworkException() || TimeoutException() => TrackFailure.offline,
        RateLimitException() => TrackFailure.unavailable,
        ServerException(:final statusCode?) when statusCode >= 500 =>
          TrackFailure.unavailable,
        _ => TrackFailure.failed,
      };

  /// The stored snapshot with [failure]. Nothing is removed: a `404` or a
  /// `500` leaves the snapshot as it was.
  Future<TrackResult> _keep(
    String deliveryId,
    String userId,
    TrackFailure failure,
  ) async =>
      TrackResult(
        snapshot: await lastSnapshot(deliveryId, userId: userId),
        failure: failure,
      );
}
