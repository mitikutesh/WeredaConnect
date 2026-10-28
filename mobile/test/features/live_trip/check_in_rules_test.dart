import 'dart:convert';

import 'package:flutter_test/flutter_test.dart';

import 'package:wereda_connect/core/models/location_ping.dart';
import 'package:wereda_connect/features/live_trip/domain/check_in_rules.dart';

/// LT-08, live-trip.md section 5.2 and live-trip-api.md section 1.
void main() {
  const addis = (latitude: 9.03, longitude: 38.74);

  group('cadence', () {
    test('hourly without a drop-off point or a last fix', () {
      expect(
        CheckInCadence.intervalAfter(addis, const []),
        CheckInCadence.hourly,
      );
      expect(
        CheckInCadence.intervalAfter(null, const [addis]),
        CheckInCadence.hourly,
      );
    });

    test('every 15 minutes within 10 km of an undelivered drop-off', () {
      // About 5.6 km north of the drop-off.
      const near = (latitude: 9.08, longitude: 38.74);
      // About 22 km north.
      const far = (latitude: 9.23, longitude: 38.74);
      expect(
        CheckInCadence.intervalAfter(near, const [addis]),
        CheckInCadence.nearStop,
      );
      expect(
        CheckInCadence.intervalAfter(far, const [addis]),
        CheckInCadence.hourly,
      );
      expect(CheckInCadence.sourceFor(CheckInCadence.nearStop), 'near_stop');
      expect(CheckInCadence.sourceFor(CheckInCadence.hourly), 'hourly');
    });

    test('the next slot counts from the later of the last fix and attempt', () {
      final fix = DateTime.utc(2026, 10, 28, 8);
      final miss = DateTime.utc(2026, 10, 28, 9, 5);
      expect(
        CheckInCadence.nextDue(
          interval: CheckInCadence.hourly,
          lastFixAt: fix,
          lastAttemptAt: miss,
        ),
        DateTime.utc(2026, 10, 28, 10, 5),
      );
      expect(
        CheckInCadence.nextDue(interval: CheckInCadence.hourly),
        isNull,
      );
    });

    test('the fix limits match the server', () {
      expect(FixBounds.accepts(8.75, 38.98, 35), isTrue);
      expect(FixBounds.accepts(2.9, 38.98, 35), isFalse);
      expect(FixBounds.accepts(8.75, 49, 35), isFalse);
      expect(FixBounds.accepts(8.75, 38.98, 5001), isFalse);
      expect(distanceKm(addis, addis), 0);
    });
  });

  group('wire', () {
    final at = toWholeMillisUtc(DateTime.utc(2026, 10, 27, 8, 42, 0, 123, 456));

    test('capture times are whole milliseconds', () {
      expect(at.microsecond, 0);
      expect(at.toIso8601String(), '2026-10-27T08:42:00.123Z');
      expect(
        locationPingId('trip-101', at),
        'trip-101#${at.millisecondsSinceEpoch}',
      );
    });

    test('a fix carries the contract fields, mocked only when true', () {
      final ping = LocationPing()
        ..pingId = locationPingId('trip-101', at)
        ..tripId = 'trip-101'
        ..truckerId = 'trucker-789'
        ..kind = PingKind.fix
        ..source = CheckInSource.hourly
        ..capturedAt = at
        ..latitude = 8.752312345
        ..longitude = 38.978512345
        ..accuracyM = 35.4;

      expect(locationPingData(ping), {
        'tripId': 'trip-101',
        'kind': 'fix',
        'capturedAt': '2026-10-27T08:42:00.123Z',
        'source': 'hourly',
        'latitude': 8.75231,
        'longitude': 38.97851,
        'accuracyM': 35,
      });

      ping.mocked = true;
      expect(locationPingData(ping)['mocked'], isTrue);
      expect(
        locationPingIdempotencyKey('trucker-789', ping),
        'trucker-789:location_ping:trip-101#${at.millisecondsSinceEpoch}'
        ':create:${at.millisecondsSinceEpoch}',
      );
    });

    test('a pause carries pausedUntil and no fix fields', () {
      final ping = LocationPing()
        ..pingId = locationPingId('trip-101', at)
        ..tripId = 'trip-101'
        ..truckerId = 'trucker-789'
        ..kind = PingKind.paused
        ..capturedAt = at
        ..pausedUntil = at.add(const Duration(minutes: 30));

      final data = locationPingData(ping);
      expect(data.keys, ['tripId', 'kind', 'capturedAt', 'pausedUntil']);
      expect(data['pausedUntil'], '2026-10-27T09:12:00.123Z');
      expect(jsonEncode(data), isNot(contains('source')));
    });
  });
}
