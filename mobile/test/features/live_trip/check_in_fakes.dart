import 'dart:async';

import 'package:wereda_connect/core/models/delivery.dart';
import 'package:wereda_connect/core/models/location_ping.dart';
import 'package:wereda_connect/features/live_trip/domain/base_location_ping_repository.dart';
import 'package:wereda_connect/features/live_trip/domain/check_in_rules.dart';
import 'package:wereda_connect/features/live_trip/domain/location_source.dart';
import 'package:wereda_connect/features/live_trip/domain/trip_progress.dart';

/// Fakes for the check-in engine tests (LT-08). No real timers, no sleeps.

class FakeClock {
  FakeClock(this.now);

  DateTime now;

  void advance(Duration by) => now = now.add(by);
}

class FakeTimer implements Timer {
  FakeTimer(this.delay, this.callback);

  final Duration delay;
  final void Function() callback;
  bool cancelled = false;

  @override
  void cancel() => cancelled = true;

  @override
  bool get isActive => !cancelled;

  @override
  int get tick => 0;
}

class FakeTimers {
  final List<FakeTimer> created = [];

  Timer call(Duration delay, void Function() callback) {
    final timer = FakeTimer(delay, callback);
    created.add(timer);
    return timer;
  }

  /// The timer that is still armed, if any.
  FakeTimer? get armed => created.where((t) => !t.cancelled).lastOrNull;
}

class FakeLocation implements LocationSource {
  FakeLocation(this.clock, {this.supportsBackground = true});

  final FakeClock clock;

  @override
  final bool supportsBackground;

  /// Answers for the next [currentFix] calls; when empty a fix at
  /// [position] taken now.
  final List<CheckInFix?> answers = [];
  GeoPoint position = (latitude: 8.0, longitude: 39.0);
  final List<bool> asked = [];
  bool running = false;
  BackgroundNotice? notice;
  void Function()? onWake;
  int starts = 0;

  /// Start attempts, and how many of the next ones fail (no permission, the
  /// platform refused).
  int startAttempts = 0;
  int failStarts = 0;

  /// The position stream failed: the plugin stopped the service.
  void streamFails() {
    running = false;
    onWake = null;
  }

  CheckInFix fixHere({double accuracyM = 30, bool mocked = false}) =>
      CheckInFix(
        latitude: position.latitude,
        longitude: position.longitude,
        accuracyM: accuracyM,
        capturedAt: clock.now,
        mocked: mocked,
      );

  @override
  Future<CheckInFix?> currentFix({bool askPermission = false}) async {
    asked.add(askPermission);
    if (answers.isNotEmpty) return answers.removeAt(0);
    return fixHere();
  }

  @override
  bool get isBackgroundRunning => running;

  @override
  Future<void> startBackground({
    required BackgroundNotice notice,
    required void Function() onWake,
  }) async {
    if (!supportsBackground || running) return;
    startAttempts++;
    if (failStarts > 0) {
      failStarts--;
      return;
    }
    running = true;
    starts++;
    this.notice = notice;
    this.onWake = onWake;
  }

  @override
  Future<void> stopBackground() async {
    running = false;
    onWake = null;
  }
}

class FakeTrips implements TripProgressReader {
  final Map<String, List<TripLeg>> legs = {};
  final Map<String, String> tripOfDelivery = {};

  void set(String tripId, List<TripLeg> value) => legs[tripId] = value;

  @override
  Future<TripProgress?> read(String tripId, {required String userId}) async {
    final value = legs[tripId];
    return value == null ? null : TripProgress(value);
  }

  @override
  Future<String?> tripIdOfDelivery(
    String deliveryId, {
    required String userId,
  }) async =>
      tripOfDelivery[deliveryId];
}

TripLeg leg(
  String id,
  DeliveryStatus status, {
  GeoPoint? dropoff,
  DateTime? pickedUpAt,
  DateTime? leftRoadAt,
}) =>
    TripLeg(
      deliveryId: id,
      status: status,
      dropoff: dropoff,
      pickedUpAt: pickedUpAt,
      leftRoadAt: leftRoadAt,
    );

class FakePings extends BaseLocationPingRepository {
  final List<LocationPing> rows = [];
  final Set<String> unsent = {};
  final List<String> dropped = [];
  final List<DateTime?> droppedAfter = [];
  PingSendResult nextResult = PingSendResult.queued;
  Set<String> flushEnded = {};
  int flushes = 0;
  DateTime? prunedBefore;

  List<LocationPing> get fixes =>
      rows.where((p) => p.kind == PingKind.fix).toList();

  List<LocationPing> get events =>
      rows.where((p) => p.kind != PingKind.fix).toList();

  @override
  Future<PingSendResult> record(
    LocationPing ping, {
    required String userId,
  }) async {
    if (rows.any((r) => r.pingId == ping.pingId)) {
      throw StateError('duplicate ping id ${ping.pingId}');
    }
    rows.add(ping);
    unsent.add(ping.pingId);
    return nextResult;
  }

  List<LocationPing> _of(String tripId, String userId) =>
      rows.where((r) => r.tripId == tripId && r.truckerId == userId).toList()
        ..sort((a, b) => a.capturedAt.compareTo(b.capturedAt));

  @override
  Future<LocationPing?> latestFix(
    String tripId, {
    required String userId,
  }) async =>
      _of(tripId, userId).where((p) => p.kind == PingKind.fix).lastOrNull;

  @override
  Future<LocationPing?> latest(String tripId, {required String userId}) async =>
      _of(tripId, userId).lastOrNull;

  @override
  Future<List<LocationPing>> pingsFor(
    String tripId, {
    required String userId,
  }) async =>
      _of(tripId, userId);

  @override
  Future<int> unsentCount(String tripId, {required String userId}) async =>
      unsent.where((id) => id.startsWith('$tripId#')).length;

  @override
  Future<int> dropUnsent(
    String tripId, {
    required String userId,
    DateTime? capturedAfter,
  }) async {
    dropped.add(tripId);
    droppedAfter.add(capturedAfter);
    final after = capturedAfter?.millisecondsSinceEpoch;
    final ids = unsent
        .where((id) => id.startsWith('$tripId#'))
        .where(
          (id) => after == null || int.parse(id.split('#').last) > after,
        )
        .toList();
    unsent.removeAll(ids);
    return ids.length;
  }

  @override
  Future<Set<String>> flush({required String userId}) async {
    flushes++;
    final ended = flushEnded;
    flushEnded = {};
    return ended;
  }

  @override
  Future<void> pruneBefore(DateTime cutoff) async => prunedBefore = cutoff;
}
