import 'package:wereda_connect/features/live_trip/domain/models/delivery_track.dart';

DeliveryTrack demoWheatTrack() {
  final now = DateTime.now().toUtc();
  return DeliveryTrack(
    deliveryId: 'del-demo-wheat',
    sharing: TrackSharing.live,
    asOf: now,
    pickedUpAt: now.subtract(const Duration(hours: 3, minutes: 37)),
    pickup: const GeoPoint(8.156, 38.824),
    dropoff: const GeoPoint(9.030, 38.740),
    dropoffNote: 'Merkato - you',
    lastFix: TrackFix(
      point: const GeoPoint(8.752, 38.978),
      capturedAt: now.subtract(const Duration(minutes: 28)),
      accuracyM: 25.0,
      source: 'hourly',
    ),
    trail: [
      TrackFix(
        point: const GeoPoint(8.156, 38.824),
        capturedAt: now.subtract(const Duration(hours: 3, minutes: 37)),
        source: 'scan',
      ),
      TrackFix(
        point: const GeoPoint(8.303, 38.956),
        capturedAt: now.subtract(const Duration(hours: 2, minutes: 38)),
        source: 'hourly',
      ),
      TrackFix(
        point: const GeoPoint(8.428, 39.034),
        capturedAt: now.subtract(const Duration(hours: 2, minutes: 2)),
        source: 'near_stop',
      ),
      TrackFix(
        point: const GeoPoint(8.587, 39.123),
        capturedAt: now.subtract(const Duration(hours: 1, minutes: 1)),
        source: 'hourly',
        receivedAt: now.subtract(const Duration(minutes: 40)),
      ),
      TrackFix(
        point: const GeoPoint(8.752, 38.978),
        capturedAt: now.subtract(const Duration(minutes: 28)),
        source: 'hourly',
      ),
    ],
    otherStops: const [
      TrackStop(
        kind: 'dropoff',
        point: GeoPoint(8.428, 39.034),
        done: true,
      ),
      TrackStop(
        kind: 'dropoff',
        point: GeoPoint(8.878, 38.876),
        done: false,
      ),
    ],
    eta: TrackEta(
      from: now.add(const Duration(minutes: 40)),
      to: now.add(const Duration(hours: 1, minutes: 20)),
    ),
    remainingKm: 47.0,
  );
}

DeliveryTrack demoBarleyTrack() {
  final now = DateTime.now().toUtc();
  return DeliveryTrack(
    deliveryId: 'del-demo-barley',
    sharing: TrackSharing.live,
    asOf: now,
    pickedUpAt: now.subtract(const Duration(hours: 4)),
    pickup: const GeoPoint(7.956, 39.120),
    dropoff: const GeoPoint(9.030, 38.740),
    lastFix: TrackFix(
      point: const GeoPoint(8.587, 39.123),
      capturedAt: now.subtract(const Duration(hours: 2, minutes: 15)),
      accuracyM: 30.0,
      source: 'hourly',
    ),
    trail: [
      TrackFix(
        point: const GeoPoint(7.956, 39.120),
        capturedAt: now.subtract(const Duration(hours: 4)),
        source: 'scan',
      ),
      TrackFix(
        point: const GeoPoint(8.587, 39.123),
        capturedAt: now.subtract(const Duration(hours: 2, minutes: 15)),
        source: 'hourly',
      ),
    ],
    eta: TrackEta(
      from: now.add(const Duration(hours: 2)),
      to: now.add(const Duration(hours: 4)),
    ),
    remainingKm: 78.0,
  );
}
