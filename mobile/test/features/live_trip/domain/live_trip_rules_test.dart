import 'package:flutter_test/flutter_test.dart';

import 'package:wereda_connect/core/database/schemas/delivery.dart';
import 'package:wereda_connect/features/live_trip/domain/live_trip_rules.dart';
import 'package:wereda_connect/features/live_trip/domain/models/delivery_track.dart';

import '../live_trip_fixtures.dart';
import '../live_trip_harness.dart';

/// LT-11: who gets the live trip screens and when the buyer is "arriving".
void main() {
  group('liveTripViewerOf', () {
    test('the buyer and the farmer of a trucked delivery get a viewer', () {
      final delivery = liveDelivery();
      expect(liveTripViewerOf(delivery, buyerId), LiveTripViewer.buyer);
      expect(liveTripViewerOf(delivery, farmerId), LiveTripViewer.farmer);
    });

    test('the trucker gets none: no buyer map from these screens', () {
      expect(liveTripViewerOf(liveDelivery(), truckerId), isNull);
    });

    test('a stranger, a signed-out viewer and another farmer get none', () {
      expect(liveTripViewerOf(liveDelivery(), 'someone-else'), isNull);
      expect(liveTripViewerOf(liveDelivery(), ''), isNull);
      expect(
        liveTripViewerOf(liveDelivery(farmer: 'farmer-002'), farmerId),
        isNull,
      );
    });

    test('a self-pickup has no truck and so no live trip', () {
      final delivery = liveDelivery()
        ..deliveryType = 'self_pickup'
        ..truckerId = '';
      expect(liveTripViewerOf(delivery, buyerId), isNull);
      expect(isOnTheRoad(delivery), isFalse);
      expect(hasLiveTrip(delivery), isFalse);
    });
  });

  test('on the road is picked up or in transit, waiting is pending', () {
    for (final status in DeliveryStatus.values) {
      final delivery = liveDelivery(status: status);
      expect(
        isOnTheRoad(delivery),
        status == DeliveryStatus.pickedUp || status == DeliveryStatus.inTransit,
        reason: status.name,
      );
      expect(
        isWaitingForPickup(delivery),
        status == DeliveryStatus.pending,
        reason: status.name,
      );
      expect(
        hasLiveTrip(delivery),
        status != DeliveryStatus.cancelled,
        reason: status.name,
      );
    }
  });

  group('isArriving', () {
    DeliveryTrack near({
      TrackSharing sharing = TrackSharing.live,
      double? remainingKm,
      GeoPoint? dropoff = const GeoPoint(8.55, 39.27),
    }) =>
        DeliveryTrack(
          deliveryId: 'del-101',
          sharing: sharing,
          asOf: asOf,
          lastFix: fix(8.57, 39.25, at(9, 0)),
          trail: [fix(8.88, 38.79, at(5, 5), source: 'scan')],
          dropoff: dropoff,
          remainingKm: remainingKm,
        );

    test('within 5 km of the drop-off by the last check-in', () {
      // About 3 km from the drop-off point.
      expect(distanceToDropoffKm(near()), lessThan(5));
      expect(isArriving(near()), isTrue);
    });

    test('farther away it is not, unless the inbox says so', () {
      final far = corridorTrack();
      expect(distanceToDropoffKm(far), greaterThan(5));
      expect(isArriving(far), isFalse);
      expect(isArriving(far, nearbyNotified: true), isTrue);
    });

    test('without a drop-off point the distance left decides', () {
      expect(isArriving(near(dropoff: null, remainingKm: 4.2)), isTrue);
      expect(isArriving(near(dropoff: null, remainingKm: 12)), isFalse);
      expect(isArriving(near(dropoff: null)), isFalse);
    });

    test('never before pickup or after delivery', () {
      for (final sharing in [TrackSharing.notStarted, TrackSharing.ended]) {
        expect(
          isArriving(near(sharing: sharing), nearbyNotified: true),
          isFalse,
          reason: sharing.name,
        );
      }
      expect(isArriving(near(sharing: TrackSharing.paused)), isTrue);
    });
  });

  test('the truck_nearby entry is matched on its delivery', () {
    final entries = [truckNearbyEntry('del-101')];
    expect(hasTruckNearbyEntry(entries, 'del-101'), isTrue);
    expect(hasTruckNearbyEntry(entries, 'del-202'), isFalse);
    expect(
      hasTruckNearbyEntry(
        [truckNearbyEntry('del-101')..type = 'delivered'],
        'del-101',
      ),
      isFalse,
    );
  });

  test('the trail distance adds the legs between check-ins', () {
    final km = trailDistanceKm(corridorTrack());
    // Akaki to near Mojo along four check-ins.
    expect(km, inInclusiveRange(40, 60));
    expect(
      trailDistanceKm(corridorTrack(trail: [fix(8.88, 38.79, at(5, 5))])),
      0,
    );
  });

  group('tripProgressShare', () {
    test('the trail over the trail plus the straight line left', () {
      final track = corridorTrack();
      final driven = trailDistanceKm(track);
      final left = distanceToDropoffKm(track)!;
      expect(tripProgressShare(track), closeTo(driven / (driven + left), 1e-9));
      expect(tripProgressShare(track), inInclusiveRange(0.5, 0.85));
    });

    test('the server\'s kilometres left win over the straight line', () {
      final track = recentTrack();
      final driven = trailDistanceKm(track);
      expect(tripProgressShare(track), closeTo(driven / (driven + 47.2), 1e-9));
    });

    test('nothing driven at pickup, no share without a distance left', () {
      expect(
        tripProgressShare(corridorTrack(trail: [fix(8.88, 38.79, at(5, 5))])),
        0,
      );
      expect(
        tripProgressShare(
          DeliveryTrack(
            deliveryId: 'del-101',
            sharing: TrackSharing.live,
            asOf: asOf,
            lastFix: corridorTrail.last,
            trail: corridorTrail,
          ),
        ),
        isNull,
      );
    });
  });

  group('tripLogEntries', () {
    test('the first scan is the pickup, a later scan another stop', () {
      final track = corridorTrack(
        trail: [
          fix(8.88, 38.79, at(5, 5), source: 'scan'),
          fix(8.83, 38.86, at(5, 40), source: 'scan'),
          fix(8.80, 38.90, at(6, 5)),
        ],
      );
      final entries = tripLogEntries(track);
      expect(entries.map((e) => e.kind), [
        TripLogKind.pickup,
        TripLogKind.otherStopScan,
        TripLogKind.checkIn,
      ]);
      expect(entries.map((e) => e.latest), [false, false, true]);
    });

    test('pauses sit between the check-ins by time', () {
      final track = DeliveryTrack(
        deliveryId: 'del-101',
        sharing: TrackSharing.live,
        asOf: asOf,
        trail: corridorTrail,
        lastFix: corridorTrail.last,
        pauses: [TrackPause(from: at(7, 30), until: at(8, 0))],
      );
      final kinds = tripLogEntries(track).map((e) => e.kind).toList();
      expect(kinds, [
        TripLogKind.pickup,
        TripLogKind.checkIn,
        TripLogKind.checkIn,
        TripLogKind.paused,
        TripLogKind.checkIn,
      ]);
    });

    test('the last check-in is listed once', () {
      final track = corridorTrack();
      expect(
        tripLogEntries(track).where((e) => e.kind != TripLogKind.paused),
        hasLength(corridorTrail.length),
      );
    });
  });

  // Buyer · Trip log board: "About 20 min, then on to you" for a stop before
  // the buyer's drop-off, at the pace of the server's own estimate.
  group('minutesToStopAhead', () {
    const ahead = TrackStop(
      kind: 'dropoff',
      point: GeoPoint(8.6, 39.18),
      done: false,
    );

    test('a stop before the drop-off, at the arrival window pace', () {
      // About 18.1 km (x 1.3) of the 47.2 km left, 90 minutes to the middle
      // of the 10:20 to 11:00 window: 34.5, rounded up to 35.
      expect(minutesToStopAhead(withEta(corridorTrack()), ahead, asOf), 35);
    });

    test('a stop past the drop-off is not ahead', () {
      const beyond = TrackStop(
        kind: 'dropoff',
        point: GeoPoint(8.2, 39.9),
        done: false,
      );
      expect(
        minutesToStopAhead(withEta(corridorTrack()), beyond, asOf),
        isNull,
      );
    });

    test('no estimate without the window or a done stop', () {
      expect(minutesToStopAhead(corridorTrack(), ahead, asOf), isNull);
      const done = TrackStop(
        kind: 'dropoff',
        point: GeoPoint(8.6, 39.18),
        done: true,
      );
      expect(minutesToStopAhead(withEta(corridorTrack()), done, asOf), isNull);
    });

    test('never under 5 minutes', () {
      const near = TrackStop(
        kind: 'pickup',
        point: GeoPoint(8.641, 39.061),
        done: false,
      );
      expect(minutesToStopAhead(withEta(corridorTrack()), near, asOf), 5);
    });
  });
}
