import 'dart:async';

import 'package:flutter/foundation.dart';

import 'package:wereda_connect/features/live_trip/domain/models/delivery_track.dart';
import 'package:wereda_connect/features/live_trip/domain/models/track_result.dart';
import 'package:wereda_connect/features/live_trip/domain/repositories/base_track_repository.dart';

/// Starts a one-shot timer; [Timer.new] in the app, a fake clock in tests.
typedef TrackTimerFactory = Timer Function(
  Duration duration,
  void Function() callback,
);

/// The shortest time between two automatic reads of a track
/// (live-trip-api.md section 2: at most every 5 minutes while visible).
const Duration trackPollInterval = Duration(minutes: 5);

/// Polls one delivery's track while a track screen is visible (LT-10).
///
/// The screen calls [setVisible] as it shows and hides (route visibility,
/// app lifecycle): visible starts polling, hidden stops it. Automatic reads
/// are at least [interval] apart, also across a hide and show; [refresh] is
/// the person's explicit "refresh" and reads at once. A read that is still
/// under way is shared, never doubled. On the first show the stored snapshot
/// is emitted at once (offline display), then the server is asked. After an
/// `ended` answer or a `404` there is nothing more to follow, so automatic
/// reads stop (a [refresh] still reads).
///
/// A poller lives as long as its screen: create one per screen and
/// [dispose] it with the screen.
class TrackPoller {
  TrackPoller({
    required BaseTrackRepository repository,
    required this.deliveryId,
    required this.userId,
    Duration interval = trackPollInterval,
    DateTime Function()? clock,
    TrackTimerFactory? timerFactory,
  })  : _repository = repository,
        interval = interval < trackPollInterval ? trackPollInterval : interval,
        _clock = clock ?? _utcNow,
        _timerFactory = timerFactory ?? Timer.new;

  static DateTime _utcNow() => DateTime.now().toUtc();

  final BaseTrackRepository _repository;
  final String deliveryId;
  final String userId;

  /// The time between automatic reads, never under [trackPollInterval].
  final Duration interval;

  final DateTime Function() _clock;
  final TrackTimerFactory _timerFactory;

  final StreamController<TrackResult> _results =
      StreamController<TrackResult>.broadcast();

  Timer? _timer;
  Future<TrackResult>? _inFlight;
  DateTime? _lastReadAt;
  TrackResult? _latest;
  bool _visible = false;
  bool _finished = false;
  bool _disposed = false;

  /// Every result: the stored snapshot first, then each read.
  Stream<TrackResult> get results => _results.stream;

  /// The newest result, or null before the first one.
  TrackResult? get latest => _latest;

  bool get isVisible => _visible;

  /// Whether a read is under way.
  bool get isFetching => _inFlight != null;

  /// The screen showed ([visible] true) or hid. Showing reads at once when
  /// the last read is at least [interval] old, else when it will be.
  void setVisible(bool visible) {
    if (_disposed || visible == _visible) return;
    _visible = visible;
    if (!visible) {
      _cancelTimer();
      return;
    }
    if (_latest == null) unawaited(_restore());
    _schedule();
  }

  /// Reads now (the person asked), unless a read is under way, which is then
  /// returned. The next automatic read is [interval] after this one.
  Future<TrackResult> refresh() => _read();

  /// Stops polling for good and closes [results].
  Future<void> dispose() async {
    if (_disposed) return;
    _disposed = true;
    _visible = false;
    _cancelTimer();
    await _results.close();
  }

  void _cancelTimer() {
    _timer?.cancel();
    _timer = null;
  }

  void _schedule() {
    _cancelTimer();
    if (_disposed || !_visible || _finished || _inFlight != null) return;
    final last = _lastReadAt;
    final wait =
        last == null ? Duration.zero : last.add(interval).difference(_clock());
    if (wait <= Duration.zero) {
      unawaited(_read());
      return;
    }
    _timer = _timerFactory(wait, () {
      _timer = null;
      unawaited(_read());
    });
  }

  Future<TrackResult> _read() {
    final pending = _inFlight;
    if (pending != null) return pending;
    if (_disposed) {
      return Future.value(
        _latest ?? const TrackResult(failure: TrackFailure.failed),
      );
    }
    _cancelTimer();
    _lastReadAt = _clock();
    final read = _fetch();
    _inFlight = read;
    return read;
  }

  Future<TrackResult> _fetch() async {
    TrackResult result;
    try {
      result = await _repository.fetchTrack(deliveryId, userId: userId);
    } on Exception catch (e) {
      // fetchTrack answers failures as results; this is a safety net.
      debugPrint('[TrackPoller] read failed: $e');
      result = TrackResult(
        snapshot: _latest?.snapshot,
        failure: TrackFailure.failed,
      );
    }
    _inFlight = null;
    if (_disposed) return result;
    _latest = result;
    _finished = result.failure == TrackFailure.notFound ||
        (result.fromNetwork && result.track?.sharing == TrackSharing.ended);
    _results.add(result);
    _schedule();
    return result;
  }

  Future<void> _restore() async {
    TrackSnapshot? stored;
    try {
      stored = await _repository.lastSnapshot(deliveryId, userId: userId);
    } on Exception catch (e) {
      debugPrint('[TrackPoller] stored snapshot unreadable: $e');
      return;
    }
    if (_disposed || _latest != null || stored == null) return;
    final result = TrackResult(snapshot: stored);
    _latest = result;
    _results.add(result);
  }
}
