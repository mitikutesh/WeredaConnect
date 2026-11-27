import 'dart:async';

import 'package:flutter/material.dart';
import 'package:provider/provider.dart';

import 'package:wereda_connect/features/live_trip/domain/models/delivery_track.dart';
import 'package:wereda_connect/features/live_trip/domain/models/track_result.dart';
import 'package:wereda_connect/features/live_trip/domain/repositories/base_track_repository.dart';
import 'package:wereda_connect/features/live_trip/domain/track_poller.dart';

/// What a [TrackBuilder] hands its builder.
@immutable
class TrackView {
  const TrackView({
    required this.result,
    required this.now,
    required this.refresh,
  });

  /// The newest read: the stored snapshot first, then each answer. Null
  /// before the first one arrives (or when no track repository is above the
  /// widget).
  final TrackResult? result;

  /// The device clock (UTC), ticking while the screen is shown so ages and
  /// freshness stay current.
  final DateTime now;

  /// Reads the track again now (the person asked).
  final Future<void> Function() refresh;

  DeliveryTrack? get track => result?.track;
  TrackSnapshot? get snapshot => result?.snapshot;
  TrackFailure? get failure => result?.failure;

  /// Waiting for the first answer.
  bool get loading => result == null;
}

/// Follows one delivery's track while it is on screen (LT-11): a
/// [TrackPoller] over the app's [BaseTrackRepository] that reads only while
/// this widget's route is the current one and the app is in the foreground,
/// and a clock that ticks every [tick] meanwhile. A pushed screen on top, a
/// hidden browser tab or a phone in the pocket stop both; [dispose] ends
/// them. The stored snapshot shows at once, so an offline screen is never
/// blank.
class TrackBuilder extends StatefulWidget {
  const TrackBuilder({
    required this.deliveryId,
    required this.userId,
    required this.builder,
    this.repository,
    this.clock,
    this.tick = const Duration(minutes: 1),
    super.key,
  });

  final String deliveryId;
  final String userId;
  final Widget Function(BuildContext context, TrackView view) builder;

  /// Overrides the repository read from the widget tree.
  final BaseTrackRepository? repository;

  /// The device clock (UTC); [DateTime.now] by default.
  final DateTime Function()? clock;

  /// How often the clock ticks while shown.
  final Duration tick;

  @override
  State<TrackBuilder> createState() => _TrackBuilderState();
}

class _TrackBuilderState extends State<TrackBuilder> {
  TrackPoller? _poller;
  StreamSubscription<TrackResult>? _results;
  AppLifecycleListener? _lifecycle;
  Timer? _ticker;
  TrackResult? _result;
  late DateTime _now;
  bool _routeShown = true;
  bool _appShown = true;
  bool _started = false;

  DateTime _clock() => (widget.clock ?? DateTime.now)().toUtc();

  @override
  void initState() {
    super.initState();
    _now = _clock();
    _appShown = _inForeground(WidgetsBinding.instance.lifecycleState);
    _lifecycle = AppLifecycleListener(
      onStateChange: (state) {
        _appShown = _inForeground(state);
        _syncVisibility(rebuild: true);
      },
    );
  }

  @override
  void didChangeDependencies() {
    super.didChangeDependencies();
    // Rebuilt by the framework when the route stops or starts being the top
    // one (ModalRoute.of depends on isCurrent).
    _routeShown = ModalRoute.of(context)?.isCurrent ?? true;
    if (!_started) _start();
    _syncVisibility();
  }

  @override
  void didUpdateWidget(TrackBuilder oldWidget) {
    super.didUpdateWidget(oldWidget);
    if (oldWidget.deliveryId != widget.deliveryId ||
        oldWidget.userId != widget.userId ||
        oldWidget.repository != widget.repository) {
      _stop();
      _result = null;
      _start();
      _syncVisibility();
    }
  }

  static bool _inForeground(AppLifecycleState? state) =>
      state == null ||
      state == AppLifecycleState.resumed ||
      state == AppLifecycleState.inactive;

  void _start() {
    _started = true;
    BaseTrackRepository? repository = widget.repository;
    if (repository == null) {
      try {
        repository = context.read<BaseTrackRepository>();
      } on ProviderNotFoundException {
        repository = null;
      }
    }
    if (repository == null || widget.userId.isEmpty) return;
    final poller = repository.poller(widget.deliveryId, userId: widget.userId);
    _poller = poller;
    _results = poller.results.listen((result) {
      if (!mounted) return;
      setState(() {
        _result = result;
        _now = _clock();
      });
    });
  }

  void _stop() {
    _ticker?.cancel();
    _ticker = null;
    _results?.cancel();
    _results = null;
    _poller?.dispose();
    _poller = null;
  }

  /// Starts or stops the poller and the clock. [rebuild]: called outside a
  /// build (the app came back), so the new clock needs a rebuild.
  void _syncVisibility({bool rebuild = false}) {
    final shown = _routeShown && _appShown;
    _poller?.setVisible(shown);
    if (!shown) {
      _ticker?.cancel();
      _ticker = null;
      return;
    }
    if (_ticker != null) return;
    _ticker = Timer.periodic(widget.tick, (_) {
      if (mounted) setState(() => _now = _clock());
    });
    // Coming back after a while: the ages are current at once.
    _now = _clock();
    if (rebuild && mounted) setState(() {});
  }

  Future<void> _refresh() async {
    final poller = _poller;
    if (poller == null) return;
    await poller.refresh();
  }

  @override
  void dispose() {
    _lifecycle?.dispose();
    _stop();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) => widget.builder(
        context,
        TrackView(result: _result, now: _now, refresh: _refresh),
      );
}
