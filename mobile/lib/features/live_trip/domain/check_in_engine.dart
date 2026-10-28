import 'dart:async';

import 'package:equatable/equatable.dart';
import 'package:flutter/foundation.dart';

import 'package:wereda_connect/core/models/location_ping.dart';
import 'package:wereda_connect/features/live_trip/domain/base_location_ping_repository.dart';
import 'package:wereda_connect/features/live_trip/domain/check_in_rules.dart';
import 'package:wereda_connect/features/live_trip/domain/location_source.dart';
import 'package:wereda_connect/features/live_trip/domain/scan_check_in_sink.dart';
import 'package:wereda_connect/features/live_trip/domain/sharing_state.dart';
import 'package:wereda_connect/features/live_trip/domain/trip_progress.dart';

/// How sharing stands for the trip the phone shares for.
enum SharingMode {
  /// No trip, or not a trucker: nothing is shared.
  off,

  /// Fixes flow on the cadence.
  live,

  /// The driver paused: no cadence fix until [CheckInStatus.pausedUntil].
  paused,

  /// The driver chose "Not now, scans only": only QR scans record a fix.
  declined,

  /// Sharing stopped for the trip: every delivery is done, or the server
  /// answered `LOCATION_SHARING_ENDED`.
  ended,
}

/// What the trucker's trip screens show (LT-09).
class CheckInStatus extends Equatable {
  const CheckInStatus({
    this.tripId,
    this.mode = SharingMode.off,
    this.pausedUntil,
    this.lastFixAt,
    this.lastFix,
    this.nextCheckInAt,
    this.unsent = 0,
    this.backgroundRunning = false,
  });

  final String? tripId;
  final SharingMode mode;

  /// While [SharingMode.paused]: when sharing resumes by itself.
  final DateTime? pausedUntil;

  /// The last fix of the trip the phone recorded (any source).
  final DateTime? lastFixAt;
  final GeoPoint? lastFix;

  /// While [SharingMode.live]: when the next cadence fix is due.
  final DateTime? nextCheckInAt;

  /// Check-ins of the trip that still wait on the phone.
  final int unsent;

  /// The Android foreground service (and its notification) runs.
  final bool backgroundRunning;

  @override
  List<Object?> get props => [
        tripId,
        mode,
        pausedUntil,
        lastFixAt,
        lastFix,
        nextCheckInAt,
        unsent,
        backgroundRunning,
      ];
}

/// The outcome of a check-in the driver asked for.
enum CheckInResult {
  /// A fix was recorded (and queued or sent).
  recorded,

  /// No fix in time (no permission, location off, no signal, out of bounds).
  noFix,

  /// The trip does not share its location now (declined, ended, no trip).
  notSharing,
}

/// Schedules the engine's next wake-up; a fake in tests.
typedef CheckInTimerFactory = Timer Function(
  Duration delay,
  void Function() callback,
);

/// The trucker's check-in engine (LT-08, `live-trip.md` section 5).
///
/// After the driver agrees to share ([start]) the phone records a fix at
/// once, then every 60 minutes after the last fix of any source, every 15
/// minutes while the last fix is within 10 km of an undelivered drop-off, on
/// each QR scan ([onScan]) and when the driver taps Check in now
/// ([checkInNow]) or I've arrived ([arrived]). A fix that does not come in
/// time is skipped to the next slot, never retried in a loop. [pause] stops
/// the cadence until a time (at most 18 hours on), [resume] ends it early.
/// Sharing stops ([SharingMode.ended]) when the phone's copy shows every
/// delivery of the trip delivered, cancelled or disputed, or when the server
/// answers `LOCATION_SHARING_ENDED` ([onSharingEnded]).
///
/// On Android the cadence runs in a foreground service with a visible
/// notification, started from the consent action while the app is in the
/// foreground, so the "while in use" location permission is enough. On web
/// there is no background: a timer runs only while the page is visible
/// ([setAppVisible]), plus the manual actions.
///
/// Every action is serialised, so a timer, a background position and a tap
/// never record two fixes for one slot. Shared code: no Isar, no `dart:io`.
class CheckInEngine implements ScanCheckInSink {
  CheckInEngine({
    required BaseLocationPingRepository pings,
    required LocationSource location,
    required TripProgressReader trips,
    required SharingStateStore states,
    BackgroundNotice Function()? notice,
    DateTime Function()? now,
    CheckInTimerFactory? timer,
  })  : _pings = pings,
        _location = location,
        _trips = trips,
        _states = states,
        _notice = notice ?? _defaultNotice,
        _now = now ?? DateTime.now,
        _timerFactory = timer ?? Timer.new;

  final BaseLocationPingRepository _pings;
  final LocationSource _location;
  final TripProgressReader _trips;
  final SharingStateStore _states;
  final BackgroundNotice Function() _notice;
  final DateTime Function() _now;
  final CheckInTimerFactory _timerFactory;

  /// Rows older than this are pruned at sign in (the server keeps 60 days).
  static const retention = Duration(days: 60);

  static BackgroundNotice _defaultNotice() => (
        title: 'Sharing trip location',
        text: 'Buyers and farmers on this trip see where the truck is.',
        channel: 'Trip location',
      );

  final StreamController<CheckInStatus> _statusController =
      StreamController<CheckInStatus>.broadcast();
  CheckInStatus _status = const CheckInStatus();

  String? _userId;
  SharingState _state = const SharingState();
  DateTime? _lastFixAt;
  GeoPoint? _lastFix;
  DateTime? _lastAttemptAt;
  DateTime? _lastCapturedAt;
  List<GeoPoint> _dropoffs = const [];
  bool _visible = true;
  bool _disposed = false;
  Timer? _timer;
  Future<void> _tail = Future<void>.value();

  /// Status updates for the trip screens.
  Stream<CheckInStatus> get statusStream => _statusController.stream;

  /// The latest status.
  CheckInStatus get status => _status;

  /// The phone's sharing state for [tripId] (null: the driver was not asked
  /// yet). LT-09 shows the consent sheet after the trip's first pickup scan
  /// while this is null.
  TripSharing? sharingFor(String tripId) => _state.trips[tripId];

  // ---------------------------------------------------------------------
  // Account
  // ---------------------------------------------------------------------

  /// A trucker signed in (or the app started signed in): load their state
  /// and pick the active trip up again. Call it from the foreground: it may
  /// start the Android foreground service.
  Future<void> signIn(String userId) => _serial(() async {
        if (_userId == userId) {
          await _tick();
          return;
        }
        await _halt();
        _resetSession();
        _userId = userId;
        _state = await _states.load(userId);
        try {
          await _pings.pruneBefore(_now().toUtc().subtract(retention));
        } on Object catch (e) {
          debugPrint('[LiveTrip] prune failed: $e');
        }
        final active = _state.activeTripId;
        if (active != null) await _activate(active);
        // The stop rule first, so a finished trip never flashes the
        // foreground notification.
        await _tick();
        final sharing = _state.active;
        if (sharing != null &&
            sharing.answer == SharingAnswer.shared &&
            !sharing.ended) {
          await _startBackground();
          await _publish();
        }
      });

  /// Signed out, or the account is not a trucker: stop everything. The state
  /// stays for the next sign in, unless [forget] (the account was deleted).
  Future<void> signOut({bool forget = false}) => _serial(() async {
        final userId = _userId;
        await _halt();
        _resetSession();
        _userId = null;
        _state = const SharingState();
        if (forget && userId != null) await _states.forget(userId);
        await _publish();
      });

  // ---------------------------------------------------------------------
  // Driver actions
  // ---------------------------------------------------------------------

  /// The driver agreed to share for [tripId] (the consent action, in the
  /// foreground): record the `start` fix, run the cadence and, on Android,
  /// the foreground service. Refused ([CheckInResult.notSharing]) when the
  /// trip already ended.
  Future<CheckInResult> start(String tripId) => _serial(() async {
        final userId = _userId;
        if (userId == null || tripId.isEmpty) return CheckInResult.notSharing;
        if (_state.trips[tripId]?.ended ?? false) {
          return CheckInResult.notSharing;
        }
        final progress = await _readProgress(tripId);
        if (progress != null && progress.allDone) {
          await _setTrip(
            tripId,
            const TripSharing(answer: SharingAnswer.shared, ended: true),
          );
          await _publish();
          return CheckInResult.notSharing;
        }
        await _setTrip(
          tripId,
          const TripSharing(answer: SharingAnswer.shared),
          activate: true,
        );
        await _activate(tripId, progress: progress);
        // The start fix first: it asks for the location permission, which
        // the foreground service needs.
        final result = await _takeFix(
          tripId,
          CheckInSource.start,
          askPermission: true,
        );
        await _startBackground();
        _schedule();
        await _publish();
        return result;
      });

  /// The driver chose "Not now, scans only" for [tripId]: record `declined`.
  /// No cadence and no foreground service; QR scans still record a fix.
  Future<void> decline(String tripId) => _serial(() async {
        final userId = _userId;
        if (userId == null || tripId.isEmpty) return;
        await _halt();
        await _setTrip(
          tripId,
          const TripSharing(answer: SharingAnswer.declined),
          activate: true,
        );
        await _activate(tripId);
        await _recordEvent(tripId, PingKind.declined, _now());
        await _publish();
      });

  /// Pause the cadence until [until] (cut to 18 hours after the event, the
  /// most the server accepts): record `paused`. QR scans still record their
  /// place. Returns false when the active trip does not share now.
  Future<bool> pause(DateTime until) => _serial(() async {
        final tripId = _state.activeTripId;
        final sharing = _state.active;
        if (_userId == null ||
            tripId == null ||
            sharing == null ||
            sharing.ended ||
            sharing.answer != SharingAnswer.shared) {
          return false;
        }
        final now = _now();
        if (!until.isAfter(now)) {
          throw ArgumentError.value(until, 'until', 'must be in the future');
        }
        final capturedAt = _nextCaptureTime(now);
        var end = toWholeMillisUtc(until);
        final cap = capturedAt.add(CheckInCadence.maxPause);
        if (end.isAfter(cap)) end = cap;
        await _sendEvent(
          tripId,
          PingKind.paused,
          capturedAt,
          pausedUntil: end,
        );
        await _setTrip(tripId, sharing.copyWith(pausedUntil: end));
        _schedule();
        await _publish();
        return true;
      });

  /// End a pause early: record `resumed`, then a fix when one is due.
  Future<void> resume() => _serial(() async {
        final tripId = _state.activeTripId;
        final sharing = _state.active;
        if (_userId == null ||
            tripId == null ||
            sharing == null ||
            sharing.pausedUntil == null) {
          return;
        }
        await _recordEvent(tripId, PingKind.resumed, _now());
        await _setTrip(tripId, sharing.copyWith(clearPause: true));
        await _tick();
      });

  /// Check in now: a `manual` fix (also while paused).
  Future<CheckInResult> checkInNow() =>
      _serial(() => _driverFix(CheckInSource.manual));

  /// I've arrived: an `arrived` fix.
  Future<CheckInResult> arrived() =>
      _serial(() => _driverFix(CheckInSource.arrived));

  /// A pickup or drop-off QR scan of [tripId] succeeded: a `scan` fix when
  /// the driver shares (also while paused) or chose scans only. Resets the
  /// cadence clock. Never throws.
  @override
  Future<void> onScan(String tripId) async {
    try {
      await _serial(() async {
        final sharing = _state.trips[tripId];
        if (_userId == null || tripId.isEmpty || sharing == null) return;
        if (sharing.ended) return;
        if (sharing.answer == SharingAnswer.shared &&
            _state.activeTripId != tripId) {
          _state = _state.withActive(tripId);
          await _saveState();
          await _activate(tripId);
          await _startBackground();
        } else if (_state.activeTripId == tripId) {
          final progress = await _readProgress(tripId);
          if (progress != null) _dropoffs = progress.undeliveredDropoffs;
        }
        await _takeFix(tripId, CheckInSource.scan, askPermission: true);
        _schedule();
        await _publish();
      });
    } on Object catch (e) {
      debugPrint('[LiveTrip] scan check-in failed: $e');
    }
  }

  /// The driver stops sharing for the active trip (it ends there).
  Future<void> stop() => _serial(() async {
        final tripId = _state.activeTripId;
        if (_userId == null || tripId == null) return;
        await _end(tripId);
      });

  /// Look again: the stop rule, a pause that ran out, a fix that is due.
  /// The app calls it when it comes to the foreground and after a delivery
  /// changed; the timer and the foreground service call it on their own.
  Future<void> refresh() => _serial(_tick);

  // ---------------------------------------------------------------------
  // Server answers (sync executor, LT-08)
  // ---------------------------------------------------------------------

  /// The server answered `LOCATION_SHARING_ENDED` for a ping of [tripId].
  Future<void> onSharingEnded(String tripId) => _serial(() async {
        if (_userId == null) return;
        await _end(tripId);
      });

  /// The server refused the pickup of [deliveryId] for good. Unless another
  /// delivery of its trip is on the road, the trip's waiting check-ins are
  /// dropped (they would wait as `deferred` for 7 days) and its answer is
  /// forgotten, so the next successful pickup asks again.
  Future<void> onPickupRejected(String deliveryId) => _serial(() async {
        final userId = _userId;
        if (userId == null) return;
        String? tripId;
        try {
          tripId = await _trips.tripIdOfDelivery(deliveryId, userId: userId);
        } on Object catch (e) {
          debugPrint('[LiveTrip] trip of a refused pickup unknown: $e');
        }
        if (tripId == null || tripId.isEmpty) return;
        final progress = await _readProgress(tripId);
        if (progress != null && progress.anotherOnRoad(deliveryId)) return;
        try {
          await _pings.dropUnsent(tripId, userId: userId);
        } on Object catch (e) {
          debugPrint('[LiveTrip] dropping check-ins failed: $e');
        }
        if (_state.activeTripId == tripId) {
          await _halt();
          _resetSession();
          _state = _state.withActive(null);
        }
        _state = _state.withTrip(tripId, null);
        await _saveState();
        await _publish();
      });

  // ---------------------------------------------------------------------
  // Lifecycle
  // ---------------------------------------------------------------------

  /// The app (web: the page) became visible or hidden. Without a background
  /// service the timer only runs while visible; coming back looks again.
  void setAppVisible(bool visible) {
    if (_visible == visible || _disposed) return;
    _visible = visible;
    if (visible) {
      unawaited(refresh());
    } else if (!_location.supportsBackground) {
      _timer?.cancel();
      _timer = null;
    }
  }

  /// Release the timer, the foreground service and the status stream.
  Future<void> dispose() async {
    if (_disposed) return;
    _disposed = true;
    await _serial(_halt);
    await _statusController.close();
  }

  // ---------------------------------------------------------------------
  // Internals
  // ---------------------------------------------------------------------

  Future<T> _serial<T>(Future<T> Function() task) {
    final completer = Completer<T>();
    _tail = _tail.then((_) async {
      try {
        completer.complete(await task());
      } on Object catch (e, s) {
        completer.completeError(e, s);
      }
    });
    return completer.future;
  }

  void _resetSession() {
    _lastFixAt = null;
    _lastFix = null;
    _lastAttemptAt = null;
    _lastCapturedAt = null;
    _dropoffs = const [];
  }

  /// Load what the phone knows about [tripId] for the cadence.
  Future<void> _activate(String tripId, {TripProgress? progress}) async {
    _resetSession();
    final userId = _userId;
    if (userId == null) return;
    try {
      final fix = await _pings.latestFix(tripId, userId: userId);
      if (fix != null && fix.latitude != null && fix.longitude != null) {
        _lastFixAt = fix.capturedAt.toUtc();
        _lastFix = (latitude: fix.latitude!, longitude: fix.longitude!);
      }
      final latest = await _pings.latest(tripId, userId: userId);
      _lastCapturedAt = latest?.capturedAt.toUtc();
    } on Object catch (e) {
      debugPrint('[LiveTrip] check-in log unreadable: $e');
    }
    final read = progress ?? await _readProgress(tripId);
    if (read != null) _dropoffs = read.undeliveredDropoffs;
  }

  /// One step of the cadence for the active trip.
  Future<void> _tick() async {
    final userId = _userId;
    final tripId = _state.activeTripId;
    if (userId == null || tripId == null || _state.active == null) {
      await _publish();
      return;
    }
    if (_state.active!.ended) {
      await _halt();
      await _publish();
      return;
    }

    Set<String> endedTrips = const {};
    try {
      endedTrips = await _pings.flush(userId: userId);
    } on Object catch (e) {
      debugPrint('[LiveTrip] flush failed: $e');
    }
    for (final ended in endedTrips) {
      await _end(ended);
    }
    if (_state.active?.ended ?? true) {
      await _publish();
      return;
    }

    final progress = await _readProgress(tripId);
    if (progress != null) {
      _dropoffs = progress.undeliveredDropoffs;
      if (progress.allDone) {
        await _end(tripId);
        return;
      }
    }

    var sharing = _state.active!;
    if (sharing.answer != SharingAnswer.shared) {
      await _publish();
      return;
    }

    final now = _now();
    final pausedUntil = sharing.pausedUntil;
    if (pausedUntil != null && !now.isBefore(pausedUntil)) {
      // The pause ran out on the phone: `resumed` at its end.
      await _recordEvent(tripId, PingKind.resumed, pausedUntil);
      sharing = sharing.copyWith(clearPause: true);
      await _setTrip(tripId, sharing);
    }

    if (sharing.pausedUntil == null) {
      final due = _nextDue();
      if (due == null || !now.isBefore(due)) {
        await _takeFix(tripId, CheckInCadence.sourceFor(_interval()));
      }
    }
    _schedule();
    await _publish();
  }

  Future<CheckInResult> _driverFix(String source) async {
    final tripId = _state.activeTripId;
    final sharing = _state.active;
    if (_userId == null ||
        tripId == null ||
        sharing == null ||
        sharing.ended ||
        sharing.answer != SharingAnswer.shared) {
      return CheckInResult.notSharing;
    }
    final progress = await _readProgress(tripId);
    if (progress != null) _dropoffs = progress.undeliveredDropoffs;
    final result = await _takeFix(tripId, source, askPermission: true);
    _schedule();
    await _publish();
    return result;
  }

  /// Take one fix for [tripId] (medium accuracy, 60 second limit) and
  /// record it. [askPermission] for the driver's own actions only.
  Future<CheckInResult> _takeFix(
    String tripId,
    String source, {
    bool askPermission = false,
  }) async {
    final userId = _userId;
    if (userId == null) return CheckInResult.notSharing;
    final isActive = tripId == _state.activeTripId;

    CheckInFix? fix;
    try {
      fix = await _location.currentFix(askPermission: askPermission);
    } on Object catch (e) {
      debugPrint('[LiveTrip] no fix: $e');
    }
    if (fix == null ||
        !FixBounds.accepts(fix.latitude, fix.longitude, fix.accuracyM)) {
      // Skip to the next slot: never retried in a loop.
      if (isActive) _lastAttemptAt = _now();
      return CheckInResult.noFix;
    }

    var captured = fix.capturedAt.toUtc();
    final now = _now().toUtc();
    if (captured.isAfter(now)) captured = now;
    captured = _nextCaptureTime(captured);

    final ping = LocationPing()
      ..pingId = locationPingId(tripId, captured)
      ..tripId = tripId
      ..truckerId = userId
      ..kind = PingKind.fix
      ..source = source
      ..capturedAt = captured
      ..latitude = fix.latitude
      ..longitude = fix.longitude
      ..accuracyM = fix.accuracyM
      ..mocked = fix.mocked;

    if (isActive) {
      _lastFixAt = captured;
      _lastFix = (latitude: fix.latitude, longitude: fix.longitude);
      _lastAttemptAt = null;
    }
    await _send(ping);
    return CheckInResult.recorded;
  }

  Future<void> _recordEvent(String tripId, String kind, DateTime at) =>
      _sendEvent(tripId, kind, _nextCaptureTime(at));

  Future<void> _sendEvent(
    String tripId,
    String kind,
    DateTime capturedAt, {
    DateTime? pausedUntil,
  }) async {
    final userId = _userId;
    if (userId == null) return;
    final ping = LocationPing()
      ..pingId = locationPingId(tripId, capturedAt)
      ..tripId = tripId
      ..truckerId = userId
      ..kind = kind
      ..capturedAt = capturedAt
      ..pausedUntil = pausedUntil;
    await _send(ping);
  }

  Future<void> _send(LocationPing ping) async {
    final userId = _userId;
    if (userId == null) return;
    PingSendResult result;
    try {
      result = await _pings.record(ping, userId: userId);
    } on Object catch (e) {
      debugPrint('[LiveTrip] check-in not recorded: $e');
      result = PingSendResult.dropped;
    }
    if (result == PingSendResult.sharingEnded) await _end(ping.tripId);
  }

  /// A capture time in whole milliseconds, after every earlier one: the
  /// entity id `{tripId}#{ms}` must be unique.
  DateTime _nextCaptureTime(DateTime at) {
    var time = toWholeMillisUtc(at);
    final last = _lastCapturedAt;
    if (last != null && !time.isAfter(last)) {
      time = last.add(const Duration(milliseconds: 1));
    }
    _lastCapturedAt = time;
    return time;
  }

  Duration _interval() => CheckInCadence.intervalAfter(_lastFix, _dropoffs);

  DateTime? _nextDue() => CheckInCadence.nextDue(
        interval: _interval(),
        lastFixAt: _lastFixAt,
        lastAttemptAt: _lastAttemptAt,
      );

  void _schedule() {
    _timer?.cancel();
    _timer = null;
    final sharing = _state.active;
    if (_disposed ||
        _userId == null ||
        sharing == null ||
        sharing.ended ||
        sharing.answer != SharingAnswer.shared) {
      return;
    }
    if (!_visible && !_location.supportsBackground) return;
    final now = _now();
    final at = sharing.pausedUntil ?? _nextDue() ?? now;
    var delay = at.difference(now);
    if (delay.isNegative) delay = Duration.zero;
    _timer = _timerFactory(delay, () => unawaited(refresh()));
  }

  Future<void> _end(String tripId) async {
    final sharing =
        _state.trips[tripId] ?? const TripSharing(answer: SharingAnswer.shared);
    _state = _state.withTrip(
      tripId,
      sharing.copyWith(ended: true, clearPause: true),
    );
    if (_state.activeTripId == tripId) await _halt();
    await _saveState();
    await _publish();
  }

  Future<void> _setTrip(
    String tripId,
    TripSharing sharing, {
    bool activate = false,
  }) async {
    _state = _state.withTrip(tripId, sharing);
    if (activate) _state = _state.withActive(tripId);
    await _saveState();
  }

  Future<void> _saveState() async {
    final userId = _userId;
    if (userId == null) return;
    await _states.save(userId, _state);
  }

  Future<TripProgress?> _readProgress(String tripId) async {
    final userId = _userId;
    if (userId == null) return null;
    try {
      return await _trips.read(tripId, userId: userId);
    } on Object catch (e) {
      debugPrint('[LiveTrip] trip progress unreadable: $e');
      return null;
    }
  }

  Future<void> _startBackground() async {
    if (!_location.supportsBackground || _location.isBackgroundRunning) return;
    try {
      await _location.startBackground(
        notice: _notice(),
        onWake: () => unawaited(
          refresh().catchError((Object e) {
            debugPrint('[LiveTrip] background check-in failed: $e');
          }),
        ),
      );
    } on Object catch (e) {
      debugPrint('[LiveTrip] foreground service did not start: $e');
    }
  }

  Future<void> _halt() async {
    _timer?.cancel();
    _timer = null;
    if (_location.isBackgroundRunning) {
      try {
        await _location.stopBackground();
      } on Object catch (e) {
        debugPrint('[LiveTrip] foreground service did not stop: $e');
      }
    }
  }

  SharingMode _mode() {
    final sharing = _state.active;
    if (_userId == null || sharing == null) return SharingMode.off;
    if (sharing.ended) return SharingMode.ended;
    if (sharing.answer == SharingAnswer.declined) return SharingMode.declined;
    final pausedUntil = sharing.pausedUntil;
    if (pausedUntil != null && _now().isBefore(pausedUntil)) {
      return SharingMode.paused;
    }
    return SharingMode.live;
  }

  Future<void> _publish() async {
    final tripId = _state.activeTripId;
    final userId = _userId;
    var unsent = 0;
    if (tripId != null && userId != null) {
      try {
        unsent = await _pings.unsentCount(tripId, userId: userId);
      } on Object catch (e) {
        debugPrint('[LiveTrip] unsent count unreadable: $e');
      }
    }
    final mode = _mode();
    final next = CheckInStatus(
      tripId: userId == null ? null : tripId,
      mode: mode,
      pausedUntil:
          mode == SharingMode.paused ? _state.active?.pausedUntil : null,
      lastFixAt: _lastFixAt,
      lastFix: _lastFix,
      nextCheckInAt: mode == SharingMode.live ? (_nextDue() ?? _now()) : null,
      unsent: unsent,
      backgroundRunning: _location.isBackgroundRunning,
    );
    if (next == _status) return;
    _status = next;
    if (!_statusController.isClosed) _statusController.add(next);
  }
}
