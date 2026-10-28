import 'package:flutter_test/flutter_test.dart';

import 'package:wereda_connect/core/sync/location_ping_rules.dart';

UnsentLocationPing<String> _ping(
  String key,
  DateTime at, {
  String kind = 'fix',
  String? source = 'hourly',
  String tripId = 'trip-1',
}) =>
    UnsentLocationPing(
      key: key,
      tripId: tripId,
      kind: kind,
      source: kind == 'fix' ? source : null,
      capturedAt: at,
    );

/// LT-08, live-trip.md section 5.3: more than 24 unsent pings keep at most
/// one fix per trip and clock hour, plus every event.
void main() {
  final base = DateTime.utc(2026, 10, 28, 6);

  test('24 or fewer unsent pings are all kept', () {
    final pings = [
      for (var i = 0; i < 24; i++)
        _ping('p$i', base.add(Duration(minutes: 5 * i))),
    ];
    expect(locationPingsToCoalesce(pings), isEmpty);
  });

  test('over 24: one fix per clock hour stays, every event stays', () {
    // 30 fixes, 15 minutes apart, over 8 clock hours, plus a pause.
    final pings = [
      for (var i = 0; i < 30; i++)
        _ping('p$i', base.add(Duration(minutes: 15 * i))),
      _ping('pause', base.add(const Duration(minutes: 20)), kind: 'paused'),
      _ping('resume', base.add(const Duration(minutes: 40)), kind: 'resumed'),
    ];

    final drop = locationPingsToCoalesce(pings);
    final kept = pings.where((p) => !drop.contains(p.key)).toList();

    expect(kept.where((p) => p.kind == 'fix'), hasLength(8));
    expect(
      kept.where((p) => p.kind != 'fix').map((p) => p.key),
      ['pause', 'resume'],
    );
    // The latest hourly fix of each hour stays (06:45, 07:45, ...).
    expect(kept.first.key, 'p3');
  });

  test('a scan or the start fix outranks an hourly fix in its hour', () {
    final pings = [
      for (var i = 0; i < 24; i++) _ping('h$i', base.add(Duration(hours: i))),
      _ping('scan', base.add(const Duration(minutes: 10)), source: 'scan'),
      _ping('late', base.add(const Duration(minutes: 50))),
    ];

    final drop = locationPingsToCoalesce(pings);

    expect(drop, containsAll(<String>['h0', 'late']));
    expect(drop, isNot(contains('scan')));
    expect(drop, hasLength(2));
  });

  test('the hour is per trip', () {
    final pings = [
      for (var i = 0; i < 13; i++)
        _ping('a$i', base.add(Duration(minutes: i)), tripId: 'trip-a'),
      for (var i = 0; i < 13; i++)
        _ping('b$i', base.add(Duration(minutes: i)), tripId: 'trip-b'),
    ];

    final drop = locationPingsToCoalesce(pings);

    expect(drop, hasLength(24));
    expect(drop, isNot(contains('a12')));
    expect(drop, isNot(contains('b12')));
  });

  test('the trip id is read back from the entity id', () {
    expect(tripIdOfLocationPing('trip-101#1793090520000'), 'trip-101');
    expect(tripIdOfLocationPing('no-separator'), isNull);
  });
}
