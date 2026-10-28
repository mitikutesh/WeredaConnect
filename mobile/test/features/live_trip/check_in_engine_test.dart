import 'package:flutter_test/flutter_test.dart';

import 'package:wereda_connect/core/models/delivery.dart';
import 'package:wereda_connect/features/live_trip/domain/base_location_ping_repository.dart';
import 'package:wereda_connect/features/live_trip/domain/check_in_engine.dart';
import 'package:wereda_connect/features/live_trip/domain/check_in_rules.dart';
import 'package:wereda_connect/features/live_trip/domain/location_source.dart';
import 'package:wereda_connect/features/live_trip/domain/sharing_state.dart';

import 'check_in_fakes.dart';

/// LT-08: the trucker's check-in engine (live-trip.md section 5), on a fake
/// clock with fake timers.
void main() {
  const trucker = 'trucker-1';
  const trip = 'trip-1';
  // Addis Ababa, the trip's drop-off.
  const dropoff = (latitude: 9.03, longitude: 38.74);
  // About 5.6 km from the drop-off, and about 120 km from it.
  const nearDropoff = (latitude: 9.08, longitude: 38.74);
  const farAway = (latitude: 8.0, longitude: 39.0);

  late FakeClock clock;
  late FakeTimers timers;
  late FakeLocation location;
  late FakeTrips trips;
  late FakePings pings;
  late MemorySharingStateStore states;
  late CheckInEngine engine;

  CheckInEngine build({bool background = true}) {
    location = FakeLocation(clock, supportsBackground: background);
    return CheckInEngine(
      pings: pings,
      location: location,
      trips: trips,
      states: states,
      notice: () => (title: 'T', text: 'X', channel: 'C'),
      now: () => clock.now,
      timer: timers.call,
    );
  }

  setUp(() async {
    clock = FakeClock(DateTime.utc(2026, 10, 28, 6));
    timers = FakeTimers();
    trips = FakeTrips()
      ..set(trip, [
        leg('d-1', DeliveryStatus.pickedUp, dropoff: dropoff),
        leg('d-2', DeliveryStatus.pending, dropoff: dropoff),
      ]);
    pings = FakePings();
    states = MemorySharingStateStore();
    engine = build();
    await engine.signIn(trucker);
  });

  tearDown(() => engine.dispose());

  Future<void> at(Duration sinceStart) async {
    clock.now = DateTime.utc(2026, 10, 28, 6).add(sinceStart);
    await engine.refresh();
  }

  group('start', () {
    test('records the start fix, runs the service and waits an hour', () async {
      final result = await engine.start(trip);

      expect(result, CheckInResult.recorded);
      final fix = pings.fixes.single;
      expect(fix.source, CheckInSource.start);
      expect(fix.tripId, trip);
      expect(fix.truckerId, trucker);
      expect(fix.pingId, '$trip#${clock.now.millisecondsSinceEpoch}');
      expect(location.asked, [true]);
      expect(location.running, isTrue);
      expect(location.notice?.title, 'T');
      expect(timers.armed?.delay, const Duration(minutes: 60));
      expect(engine.status.mode, SharingMode.live);
      expect(engine.status.tripId, trip);
      expect(
        engine.status.nextCheckInAt,
        clock.now.add(const Duration(minutes: 60)),
      );
      expect(engine.sharingFor(trip)?.answer, SharingAnswer.shared);
    });

    test('a trip whose deliveries are all done is not started', () async {
      trips.set(trip, [leg('d-1', DeliveryStatus.delivered)]);

      final result = await engine.start(trip);

      expect(result, CheckInResult.notSharing);
      expect(pings.rows, isEmpty);
      expect(location.running, isFalse);
      expect(engine.sharingFor(trip)?.ended, isTrue);
    });

    test('nothing without a signed-in trucker', () async {
      await engine.signOut();
      expect(await engine.start(trip), CheckInResult.notSharing);
      expect(pings.rows, isEmpty);
    });
  });

  group('cadence', () {
    setUp(() async {
      location.position = farAway;
      await engine.start(trip);
    });

    test('a fix 60 minutes after the last one, not before', () async {
      await at(const Duration(minutes: 59));
      expect(pings.fixes, hasLength(1));

      await at(const Duration(minutes: 60));
      expect(pings.fixes, hasLength(2));
      expect(pings.fixes.last.source, CheckInSource.hourly);
      expect(location.asked.last, isFalse);
      expect(timers.armed?.delay, const Duration(minutes: 60));
    });

    test('a scan resets the clock', () async {
      clock.advance(const Duration(minutes: 30));
      await engine.onScan(trip);
      expect(pings.fixes.last.source, CheckInSource.scan);

      await at(const Duration(minutes: 60));
      expect(pings.fixes, hasLength(2));

      await at(const Duration(minutes: 90));
      expect(pings.fixes, hasLength(3));
      expect(pings.fixes.last.source, CheckInSource.hourly);
    });

    test('Check in now and I have arrived record and reset the clock',
        () async {
      clock.advance(const Duration(minutes: 20));
      expect(await engine.checkInNow(), CheckInResult.recorded);
      clock.advance(const Duration(minutes: 5));
      expect(await engine.arrived(), CheckInResult.recorded);

      expect(
        pings.fixes.map((p) => p.source),
        [CheckInSource.start, CheckInSource.manual, CheckInSource.arrived],
      );
      expect(location.asked.last, isTrue);
      expect(
        engine.status.nextCheckInAt,
        clock.now.add(const Duration(minutes: 60)),
      );
    });

    test('every 15 minutes within 10 km of an undelivered drop-off', () async {
      location.position = nearDropoff;
      await at(const Duration(minutes: 60));
      expect(timers.armed?.delay, const Duration(minutes: 15));

      await at(const Duration(minutes: 75));
      expect(pings.fixes.last.source, CheckInSource.nearStop);
      expect(pings.fixes, hasLength(3));
    });

    test('a delivered drop-off no longer counts', () async {
      trips.set(trip, [
        leg('d-1', DeliveryStatus.delivered, dropoff: dropoff),
        leg('d-2', DeliveryStatus.inTransit),
      ]);
      location.position = nearDropoff;

      await at(const Duration(minutes: 60));

      expect(pings.fixes.last.source, CheckInSource.hourly);
      expect(timers.armed?.delay, const Duration(minutes: 60));
    });

    test('without a drop-off reference the phone stays hourly', () async {
      trips.set(trip, [leg('d-1', DeliveryStatus.inTransit)]);
      location.position = nearDropoff;

      await at(const Duration(minutes: 60));

      expect(timers.armed?.delay, const Duration(minutes: 60));
    });

    test('no fix in time skips to the next slot, never a loop', () async {
      location.answers.add(null);
      final callsBefore = location.asked.length;

      await at(const Duration(minutes: 60));

      expect(location.asked.length, callsBefore + 1);
      expect(pings.fixes, hasLength(1));
      expect(timers.armed?.delay, const Duration(minutes: 60));

      // Still nothing due a minute later.
      await at(const Duration(minutes: 61));
      expect(location.asked.length, callsBefore + 1);

      await at(const Duration(minutes: 120));
      expect(pings.fixes, hasLength(2));
    });

    test('a fix the server would refuse is not sent', () async {
      location.answers.add(
        CheckInFix(
          latitude: 20,
          longitude: 38,
          accuracyM: 30,
          capturedAt: clock.now,
        ),
      );
      expect(await engine.checkInNow(), CheckInResult.noFix);
      location.answers.add(location.fixHere(accuracyM: 6000));
      expect(await engine.checkInNow(), CheckInResult.noFix);
      expect(pings.fixes, hasLength(1));
    });

    test('a mock location is flagged and the accuracy kept', () async {
      location.answers.add(location.fixHere(accuracyM: 42.6, mocked: true));

      await engine.checkInNow();

      final fix = pings.fixes.last;
      expect(fix.mocked, isTrue);
      expect(fix.accuracyM, 42.6);
      expect(locationPingData(fix)['mocked'], isTrue);
      expect(locationPingData(fix)['accuracyM'], 43);
    });

    test('a position from the foreground service wakes the cadence', () async {
      clock.advance(const Duration(minutes: 61));
      final callsBefore = location.asked.length;

      location.onWake!();
      await engine.refresh();

      // The due fix is a medium accuracy fix of its own.
      expect(pings.fixes, hasLength(2));
      expect(location.asked.length, callsBefore + 1);

      // A wake-up when nothing is due takes no fix.
      location.onWake!();
      await engine.refresh();
      expect(pings.fixes, hasLength(2));
    });

    test('two pings in one millisecond get distinct ids', () async {
      await engine.checkInNow();
      await engine.checkInNow();

      final ids = pings.rows.map((p) => p.pingId).toSet();
      expect(ids, hasLength(pings.rows.length));
      expect(
        pings.rows.last.capturedAt.difference(pings.rows[1].capturedAt),
        const Duration(milliseconds: 1),
      );
    });
  });

  group('pause', () {
    setUp(() async {
      location.position = farAway;
      await engine.start(trip);
    });

    test('no fixes until it ends, then resumed and a fix', () async {
      clock.advance(const Duration(minutes: 50));
      final until = clock.now.add(const Duration(minutes: 30));
      expect(await engine.pause(until), isTrue);

      final paused = pings.events.single;
      expect(paused.kind, PingKind.paused);
      expect(paused.pausedUntil, until);
      expect(engine.status.mode, SharingMode.paused);
      expect(engine.status.pausedUntil, until);
      expect(timers.armed?.delay, const Duration(minutes: 30));

      await at(const Duration(minutes: 70));
      expect(pings.fixes, hasLength(1));

      await at(const Duration(minutes: 85));
      final resumed = pings.events.last;
      expect(resumed.kind, PingKind.resumed);
      expect(resumed.capturedAt, until);
      expect(pings.fixes, hasLength(2));
      expect(pings.fixes.last.source, CheckInSource.hourly);
      expect(engine.status.mode, SharingMode.live);
      expect(engine.sharingFor(trip)?.pausedUntil, isNull);
    });

    test('at most 18 hours after the event', () async {
      await engine.pause(clock.now.add(const Duration(hours: 24)));

      final paused = pings.events.single;
      expect(
        paused.pausedUntil!.difference(paused.capturedAt),
        CheckInCadence.maxPause,
      );
    });

    test('an end in the past is refused', () async {
      expect(
        () => engine.pause(clock.now.subtract(const Duration(minutes: 1))),
        throwsArgumentError,
      );
      expect(pings.events, isEmpty);
    });

    test('resume ends it early with a resumed event', () async {
      await engine.pause(clock.now.add(const Duration(hours: 2)));
      clock.advance(const Duration(minutes: 10));

      await engine.resume();

      expect(pings.events.map((p) => p.kind), [
        PingKind.paused,
        PingKind.resumed,
      ]);
      expect(pings.events.last.capturedAt, clock.now);
      expect(engine.status.mode, SharingMode.live);
    });

    test('QR scans still record their place while paused', () async {
      await engine.pause(clock.now.add(const Duration(hours: 2)));
      clock.advance(const Duration(minutes: 10));

      await engine.onScan(trip);

      expect(pings.fixes.last.source, CheckInSource.scan);
      expect(engine.status.mode, SharingMode.paused);
    });
  });

  group('declined', () {
    test('records declined; scans only, no cadence, no service', () async {
      await engine.decline(trip);

      expect(pings.events.single.kind, PingKind.declined);
      expect(location.running, isFalse);
      expect(timers.armed, isNull);
      expect(engine.status.mode, SharingMode.declined);
      expect(await engine.checkInNow(), CheckInResult.notSharing);

      await engine.onScan(trip);
      expect(pings.fixes.single.source, CheckInSource.scan);

      await at(const Duration(hours: 3));
      expect(pings.fixes, hasLength(1));
    });
  });

  group('stop', () {
    setUp(() => engine.start(trip));

    test('when every delivery is delivered, cancelled or disputed', () async {
      trips.set(trip, [
        leg('d-1', DeliveryStatus.delivered),
        leg('d-2', DeliveryStatus.cancelled),
        leg('d-3', DeliveryStatus.disputed),
      ]);

      await at(const Duration(minutes: 60));

      expect(engine.status.mode, SharingMode.ended);
      expect(location.running, isFalse);
      expect(timers.armed, isNull);
      expect(pings.fixes, hasLength(1));
      expect(engine.sharingFor(trip)?.ended, isTrue);
      expect(await engine.checkInNow(), CheckInResult.notSharing);
    });

    test('not while one delivery is still on the road', () async {
      trips.set(trip, [
        leg('d-1', DeliveryStatus.delivered),
        leg('d-2', DeliveryStatus.inTransit),
      ]);

      await at(const Duration(minutes: 60));

      expect(engine.status.mode, SharingMode.live);
      expect(pings.fixes, hasLength(2));
    });

    test('not when the phone knows no delivery of the trip', () async {
      trips.set(trip, []);
      await at(const Duration(minutes: 60));
      expect(engine.status.mode, SharingMode.live);
    });

    test('LOCATION_SHARING_ENDED from the sync executor', () async {
      await engine.onSharingEnded(trip);

      expect(engine.status.mode, SharingMode.ended);
      expect(location.running, isFalse);
    });

    test('LOCATION_SHARING_ENDED in a direct answer (web)', () async {
      pings.nextResult = PingSendResult.sharingEnded;

      await engine.checkInNow();

      expect(engine.status.mode, SharingMode.ended);
      expect(location.running, isFalse);
    });

    test('LOCATION_SHARING_ENDED from a retry of waiting pings (web)',
        () async {
      pings.flushEnded = {trip};

      await at(const Duration(minutes: 5));

      expect(engine.status.mode, SharingMode.ended);
    });

    test('the driver stops sharing', () async {
      await engine.stop();
      expect(engine.status.mode, SharingMode.ended);
      expect(location.running, isFalse);
    });
  });

  group('refused pickup', () {
    setUp(() async {
      trips.tripOfDelivery['d-1'] = trip;
      await engine.start(trip);
    });

    test('drops the waiting check-ins and asks again later', () async {
      trips.set(trip, [
        leg('d-1', DeliveryStatus.pickedUp),
        leg('d-2', DeliveryStatus.pending),
      ]);

      await engine.onPickupRejected('d-1');

      expect(pings.dropped, [trip]);
      expect(await pings.unsentCount(trip, userId: trucker), 0);
      expect(engine.sharingFor(trip), isNull);
      expect(engine.status.mode, SharingMode.off);
      expect(location.running, isFalse);
    });

    test('keeps those captured before another delivery left the road',
        () async {
      // The start fix at 06:00; d-2 was delivered at 06:30.
      clock.advance(const Duration(minutes: 45));
      await engine.checkInNow();
      final delivered = DateTime.utc(2026, 10, 28, 6, 30);
      trips.set(trip, [
        leg('d-1', DeliveryStatus.pickedUp),
        leg(
          'd-2',
          DeliveryStatus.delivered,
          pickedUpAt: DateTime.utc(2026, 10, 28, 5),
          leftRoadAt: delivered,
        ),
      ]);

      await engine.onPickupRejected('d-1');

      expect(pings.droppedAfter, [delivered]);
      // The 06:00 fix stays, the 06:45 one goes.
      expect(await pings.unsentCount(trip, userId: trucker), 1);
      expect(
        pings.unsent.single,
        '$trip#${DateTime.utc(2026, 10, 28, 6).millisecondsSinceEpoch}',
      );
    });

    test('a delivery cancelled before pickup keeps nothing', () async {
      trips.set(trip, [
        leg('d-1', DeliveryStatus.pickedUp),
        leg(
          'd-2',
          DeliveryStatus.cancelled,
          leftRoadAt: DateTime.utc(2026, 10, 28, 7),
        ),
      ]);

      await engine.onPickupRejected('d-1');

      expect(pings.droppedAfter, [null]);
      expect(await pings.unsentCount(trip, userId: trucker), 0);
    });

    test('nothing is dropped when the deliveries cannot be read', () async {
      trips.legs.remove(trip);

      await engine.onPickupRejected('d-1');

      expect(pings.dropped, isEmpty);
      expect(engine.status.mode, SharingMode.live);
    });

    test('keeps them while another delivery is on the road', () async {
      trips.set(trip, [
        leg('d-1', DeliveryStatus.pickedUp),
        leg('d-2', DeliveryStatus.inTransit),
      ]);

      await engine.onPickupRejected('d-1');

      expect(pings.dropped, isEmpty);
      expect(engine.status.mode, SharingMode.live);
    });
  });

  group('scans', () {
    test('a trip not asked about yet records nothing', () async {
      await engine.onScan(trip);
      expect(pings.rows, isEmpty);
    });

    test('a scan of a shared trip that is not active picks it up', () async {
      await engine.start(trip);
      trips.set('trip-2', [leg('d-9', DeliveryStatus.pickedUp)]);
      await engine.start('trip-2');

      await engine.onScan(trip);

      expect(engine.status.tripId, trip);
      expect(pings.fixes.last.tripId, trip);
      expect(pings.fixes.last.source, CheckInSource.scan);
    });
  });

  group('foreground service recovery', () {
    setUp(() => engine.start(trip));

    test('a failed stream is started again on the next tick', () async {
      location.streamFails();

      await at(const Duration(minutes: 5));

      expect(location.running, isTrue);
      expect(location.starts, 2);
      expect(engine.status.backgroundRunning, isTrue);
    });

    test('a start that fails is tried again with a growing backoff', () async {
      location
        ..streamFails()
        ..failStarts = 2;

      await at(const Duration(minutes: 5));
      expect(location.startAttempts, 2);
      expect(location.running, isFalse);

      // Within the first minute of backoff: no new try.
      await at(const Duration(minutes: 5, seconds: 30));
      expect(location.startAttempts, 2);

      await at(const Duration(minutes: 6));
      expect(location.startAttempts, 3);
      expect(location.running, isFalse);

      // The backoff doubled to two minutes.
      await at(const Duration(minutes: 7));
      expect(location.startAttempts, 3);
      await at(const Duration(minutes: 8));
      expect(location.startAttempts, 4);
      expect(location.running, isTrue);
    });

    test('not from the background; coming back tries at once', () async {
      location
        ..streamFails()
        ..failStarts = 1;
      await at(const Duration(minutes: 5));
      expect(location.startAttempts, 2);

      engine.setAppVisible(false);
      await at(const Duration(minutes: 30));
      expect(location.startAttempts, 2);

      location.failStarts = 1;
      engine.setAppVisible(true);
      await engine.refresh();
      expect(location.startAttempts, 3);

      // Coming back again tries again, backoff or not.
      engine.setAppVisible(false);
      engine.setAppVisible(true);
      await engine.refresh();
      expect(location.startAttempts, 4);
      expect(location.running, isTrue);
    });

    test('no restart once sharing ended or for a declined trip', () async {
      await engine.stop();
      location.streamFails();
      await at(const Duration(minutes: 5));
      expect(location.running, isFalse);

      await engine.decline('trip-2');
      await at(const Duration(minutes: 10));
      expect(location.running, isFalse);
    });
  });

  group('account and lifecycle', () {
    test('sign in picks the active trip up again', () async {
      await engine.start(trip);
      clock.advance(const Duration(minutes: 30));
      await engine.dispose();

      engine = build();
      await engine.signIn(trucker);

      expect(engine.status.mode, SharingMode.live);
      expect(location.running, isTrue);
      expect(pings.fixes, hasLength(1));
      expect(timers.armed?.delay, const Duration(minutes: 30));
      expect(pings.prunedBefore, isNotNull);
    });

    test('a pause that ran out while the app was closed resumes', () async {
      await engine.start(trip);
      final until = clock.now.add(const Duration(minutes: 30));
      await engine.pause(until);
      await engine.dispose();

      clock.advance(const Duration(hours: 2));
      engine = build();
      await engine.signIn(trucker);

      expect(pings.events.last.kind, PingKind.resumed);
      expect(pings.events.last.capturedAt, until);
      expect(pings.fixes, hasLength(2));
    });

    test('an ended trip does not start the service at sign in', () async {
      await engine.start(trip);
      await engine.onSharingEnded(trip);
      await engine.dispose();

      engine = build();
      await engine.signIn(trucker);

      expect(engine.status.mode, SharingMode.ended);
      expect(location.running, isFalse);
    });

    test('sign out halts everything; scans are ignored', () async {
      await engine.start(trip);

      await engine.signOut();

      expect(location.running, isFalse);
      expect(timers.armed, isNull);
      expect(engine.status.mode, SharingMode.off);
      await engine.onScan(trip);
      expect(pings.fixes, hasLength(1));
    });

    test('a deleted account forgets its state', () async {
      await engine.start(trip);
      await engine.signOut(forget: true);

      expect((await states.load(trucker)).trips, isEmpty);
    });

    test('web: the timer runs only while the page is visible', () async {
      await engine.dispose();
      engine = build(background: false);
      await engine.signIn(trucker);
      await engine.start(trip);
      expect(location.running, isFalse);
      expect(timers.armed?.delay, const Duration(minutes: 60));

      engine.setAppVisible(false);
      expect(timers.armed, isNull);
      clock.advance(const Duration(minutes: 90));
      await engine.refresh();
      // A refresh while hidden (a pending network answer) records the due
      // fix but arms no timer.
      expect(timers.armed, isNull);

      engine.setAppVisible(true);
      await engine.refresh();
      expect(timers.armed?.delay, const Duration(minutes: 60));
    });

    test('web: coming back to the page catches up a due fix', () async {
      await engine.dispose();
      engine = build(background: false);
      await engine.signIn(trucker);
      await engine.start(trip);
      engine.setAppVisible(false);

      clock.advance(const Duration(minutes: 75));
      engine.setAppVisible(true);
      await engine.refresh();

      expect(pings.fixes, hasLength(2));
      expect(pings.fixes.last.source, CheckInSource.hourly);
    });

    test('the status counts the check-ins that wait', () async {
      await engine.start(trip);
      await engine.checkInNow();
      expect(engine.status.unsent, 2);
    });
  });
}
