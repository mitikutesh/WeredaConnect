import 'dart:async';

import 'package:flutter_test/flutter_test.dart';

import 'package:wereda_connect/features/live_trip/domain/models/delivery_track.dart';
import 'package:wereda_connect/features/live_trip/domain/models/track_result.dart';
import 'package:wereda_connect/features/live_trip/domain/repositories/base_track_repository.dart';
import 'package:wereda_connect/features/live_trip/domain/track_poller.dart';

import '../live_trip_fixtures.dart';

/// A clock and one-shot timers that move only when the test says so.
class _FakeTime {
  DateTime now = DateTime.utc(2026, 10, 27, 9);
  final List<_FakeTimer> _timers = [];

  Timer start(Duration duration, void Function() callback) {
    final timer = _FakeTimer(now.add(duration), callback);
    _timers.add(timer);
    return timer;
  }

  /// Active timers and when they fire.
  List<DateTime> get pending => [
        for (final t in _timers)
          if (t.isActive) t.due,
      ];

  /// Moves the clock by [by], firing every timer that falls due on the way.
  Future<void> elapse(Duration by) async {
    final end = now.add(by);
    while (true) {
      final due = _timers
          .where((t) => t.isActive && !t.due.isAfter(end))
          .toList()
        ..sort((a, b) => a.due.compareTo(b.due));
      if (due.isEmpty) break;
      now = due.first.due;
      due.first.fire();
      await pumpEventQueue();
    }
    now = end;
    await pumpEventQueue();
  }
}

class _FakeTimer implements Timer {
  _FakeTimer(this.due, this._callback);

  final DateTime due;
  final void Function() _callback;
  bool _active = true;

  void fire() {
    _active = false;
    _callback();
  }

  @override
  void cancel() => _active = false;

  @override
  bool get isActive => _active;

  @override
  int get tick => 0;
}

/// Counts reads; each read can be held open with [hold].
class _FakeRepository extends BaseTrackRepository {
  _FakeRepository(this.time);

  final _FakeTime time;
  int reads = 0;
  TrackSnapshot? stored;
  TrackFailure? failWith;
  TrackSharing sharing = TrackSharing.live;
  Completer<void>? hold;

  @override
  Future<TrackResult> fetchTrack(
    String deliveryId, {
    required String userId,
  }) async {
    reads++;
    final gate = hold;
    if (gate != null) await gate.future;
    if (failWith != null) {
      return TrackResult(snapshot: stored, failure: failWith);
    }
    final snapshot = TrackSnapshot(
      track: corridorTrack(sharing: sharing),
      fetchedAt: time.now,
    );
    stored = snapshot;
    return TrackResult(snapshot: snapshot, fromNetwork: true);
  }

  @override
  Future<TrackSnapshot?> lastSnapshot(
    String deliveryId, {
    required String userId,
  }) async =>
      stored;
}

void main() {
  late _FakeTime time;
  late _FakeRepository repository;
  late TrackPoller poller;
  late List<TrackResult> results;

  setUp(() {
    time = _FakeTime();
    repository = _FakeRepository(time);
    poller = TrackPoller(
      repository: repository,
      deliveryId: 'del-101',
      userId: 'buyer-1',
      clock: () => time.now,
      timerFactory: time.start,
    );
    results = [];
    poller.results.listen(results.add);
  });

  tearDown(() => poller.dispose());

  test('reads at once when shown, then every 5 minutes, never sooner',
      () async {
    poller.setVisible(true);
    await pumpEventQueue();
    expect(repository.reads, 1);
    expect(results.last.fromNetwork, isTrue);

    await time.elapse(const Duration(minutes: 4, seconds: 59));
    expect(repository.reads, 1);

    await time.elapse(const Duration(seconds: 1));
    expect(repository.reads, 2);

    await time.elapse(const Duration(minutes: 15));
    expect(repository.reads, 5);
  });

  test('stops while hidden and does not read early when shown again', () async {
    poller.setVisible(true);
    await pumpEventQueue();
    await time.elapse(const Duration(minutes: 2));

    poller.setVisible(false);
    expect(time.pending, isEmpty);
    await time.elapse(const Duration(minutes: 20));
    expect(repository.reads, 1);

    // 22 minutes since the last read: shown again reads at once.
    poller.setVisible(true);
    await pumpEventQueue();
    expect(repository.reads, 2);

    // Hidden and shown within the interval: waits for the rest of it.
    await time.elapse(const Duration(minutes: 1));
    poller.setVisible(false);
    poller.setVisible(true);
    await pumpEventQueue();
    expect(repository.reads, 2);
    expect(time.pending, [time.now.add(const Duration(minutes: 4))]);
    await time.elapse(const Duration(minutes: 4));
    expect(repository.reads, 3);
  });

  test('refresh reads at once and restarts the 5 minutes', () async {
    poller.setVisible(true);
    await pumpEventQueue();
    await time.elapse(const Duration(minutes: 3));

    final result = await poller.refresh();
    expect(repository.reads, 2);
    expect(result.fromNetwork, isTrue);

    await time.elapse(const Duration(minutes: 4));
    expect(repository.reads, 2);
    await time.elapse(const Duration(minutes: 1));
    expect(repository.reads, 3);
  });

  test('refresh while hidden reads once and schedules nothing', () async {
    await poller.refresh();
    expect(repository.reads, 1);
    expect(time.pending, isEmpty);
  });

  test('a read under way is shared, never doubled', () async {
    repository.hold = Completer<void>();
    poller.setVisible(true);
    await pumpEventQueue();
    expect(poller.isFetching, isTrue);

    final first = poller.refresh();
    final second = poller.refresh();
    expect(repository.reads, 1);

    repository.hold!.complete();
    expect(identical(await first, await second), isTrue);
    expect(poller.isFetching, isFalse);
    expect(repository.reads, 1);
  });

  test('shows the stored snapshot first, then the read', () async {
    final stored = TrackSnapshot(
      track: corridorTrack(),
      fetchedAt: time.now.subtract(const Duration(hours: 2)),
    );
    repository
      ..stored = stored
      ..hold = Completer<void>();

    poller.setVisible(true);
    await pumpEventQueue();
    expect(results, hasLength(1));
    expect(results.single.snapshot, same(stored));
    expect(results.single.fromNetwork, isFalse);
    expect(results.single.failure, isNull);
    expect(results.single.snapshot!.ageAt(time.now), const Duration(hours: 2));

    repository.hold!.complete();
    await pumpEventQueue();
    expect(results, hasLength(2));
    expect(results.last.fromNetwork, isTrue);
    expect(poller.latest, same(results.last));
  });

  test('offline keeps polling every 5 minutes with the snapshot', () async {
    repository
      ..stored = TrackSnapshot(track: corridorTrack(), fetchedAt: time.now)
      ..failWith = TrackFailure.offline;

    poller.setVisible(true);
    await pumpEventQueue();
    await time.elapse(const Duration(minutes: 10));

    expect(repository.reads, 3);
    expect(results.last.failure, TrackFailure.offline);
    expect(results.last.snapshot, isNotNull);
  });

  test('an ended track stops the automatic reads', () async {
    repository.sharing = TrackSharing.ended;
    poller.setVisible(true);
    await pumpEventQueue();
    await time.elapse(const Duration(minutes: 30));
    expect(repository.reads, 1);
    expect(time.pending, isEmpty);

    // The person can still ask.
    await poller.refresh();
    expect(repository.reads, 2);
  });

  test('not found stops the automatic reads', () async {
    repository.failWith = TrackFailure.notFound;
    poller.setVisible(true);
    await pumpEventQueue();
    await time.elapse(const Duration(minutes: 30));
    expect(repository.reads, 1);
    expect(results.last.failure, TrackFailure.notFound);
  });

  test('a shorter interval is raised to 5 minutes', () {
    final fast = TrackPoller(
      repository: repository,
      deliveryId: 'del-101',
      userId: 'buyer-1',
      interval: const Duration(seconds: 30),
    );
    addTearDown(fast.dispose);
    expect(fast.interval, trackPollInterval);
  });

  test('dispose cancels the timer and drops a late answer', () async {
    poller.setVisible(true);
    await pumpEventQueue();
    repository.hold = Completer<void>();
    await time.elapse(const Duration(minutes: 5));
    expect(repository.reads, 2);

    await poller.dispose();
    repository.hold!.complete();
    await pumpEventQueue();
    expect(results, hasLength(1));
    expect(time.pending, isEmpty);
  });

  test('the repository builds a poller for its delivery', () async {
    final built = repository.poller('del-7', userId: 'farmer-1');
    addTearDown(built.dispose);
    expect(built.deliveryId, 'del-7');
    expect(built.userId, 'farmer-1');
    expect(built.interval, trackPollInterval);
  });
}
