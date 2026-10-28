import 'package:flutter_test/flutter_test.dart';
import 'package:shared_preferences/shared_preferences.dart';

import 'package:wereda_connect/features/live_trip/data/track_snapshot_store.dart';
import 'package:wereda_connect/features/live_trip/domain/models/delivery_track.dart';

import '../live_trip_fixtures.dart';

/// LT-10: the offline snapshot per user and delivery in the key-value store.
void main() {
  late TrackSnapshotStore store;
  final fetchedAt = DateTime.utc(2026, 10, 27, 9, 10);

  setUp(() {
    SharedPreferences.setMockInitialValues({});
    store = TrackSnapshotStore();
  });

  test('keeps the raw answer and its fetch time per user and delivery',
      () async {
    await store.write('buyer-1', 'del-101', contractTrackJson(), fetchedAt);

    final snapshot = await store.read('buyer-1', 'del-101');
    expect(snapshot!.fetchedAt, fetchedAt);
    expect(snapshot.track.sharing, TrackSharing.live);
    expect(snapshot.track.remainingKm, 52.7);
    expect(await store.read('buyer-1', 'del-202'), isNull);
    expect(await store.read('farmer-1', 'del-101'), isNull);

    final prefs = await SharedPreferences.getInstance();
    expect(prefs.getKeys(), {'live_track_v1:buyer-1:del-101'});
  });

  test('forgetUser removes only that user\'s snapshots', () async {
    await store.write('buyer-1', 'del-1', contractTrackJson(), fetchedAt);
    await store.write('buyer-1', 'del-2', contractTrackJson(), fetchedAt);
    await store.write('farmer-1', 'del-1', contractTrackJson(), fetchedAt);
    final prefs = await SharedPreferences.getInstance();
    await prefs.setString('msg_read_x', 'kept');

    await TrackSnapshotStore.forgetStoredUser('buyer-1');

    expect(await store.read('buyer-1', 'del-1'), isNull);
    expect(await store.read('buyer-1', 'del-2'), isNull);
    expect(await store.read('farmer-1', 'del-1'), isNotNull);
    expect(prefs.getString('msg_read_x'), 'kept');
  });

  test('keeps at most 20 snapshots per user, dropping the oldest fetch',
      () async {
    for (var i = 0; i < TrackSnapshotStore.maxPerUser + 2; i++) {
      await store.write(
        'buyer-1',
        'del-$i',
        contractTrackJson(),
        fetchedAt.add(Duration(minutes: i)),
      );
    }
    await store.write('farmer-1', 'del-x', contractTrackJson(), fetchedAt);

    expect(await store.read('buyer-1', 'del-0'), isNull);
    expect(await store.read('buyer-1', 'del-1'), isNull);
    expect(await store.read('buyer-1', 'del-2'), isNotNull);
    expect(await store.read('buyer-1', 'del-21'), isNotNull);
    expect(await store.read('farmer-1', 'del-x'), isNotNull);
  });

  test('an unreadable entry is dropped', () async {
    SharedPreferences.setMockInitialValues({
      'live_track_v1:buyer-1:del-1': 'not json',
      'live_track_v1:buyer-1:del-2': '{"fetchedAt":"x","track":{}}',
      'live_track_v1:buyer-1:del-3':
          '{"fetchedAt":"2026-10-27T09:10:00.000Z","track":{"sharing":"live"}}',
    });
    final prefs = await SharedPreferences.getInstance();

    expect(await store.read('buyer-1', 'del-1'), isNull);
    expect(await store.read('buyer-1', 'del-2'), isNull);
    expect(await store.read('buyer-1', 'del-3'), isNull);
    expect(prefs.getKeys(), isEmpty);
  });
}
