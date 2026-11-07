import 'dart:async';

import 'package:connectivity_plus/connectivity_plus.dart';
import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:intl/intl.dart';

import 'package:wereda_connect/core/models/trip_capacity.dart';
import 'package:wereda_connect/core/models/delivery.dart';
import 'package:wereda_connect/core/models/order_delivery_location.dart';
import 'package:wereda_connect/core/sync/connectivity_monitor.dart';
import 'package:wereda_connect/core/utils/ethiopia_time.dart';
import 'package:wereda_connect/core/utils/ethiopian_clock.dart';
import 'package:wereda_connect/core/utils/party_name.dart';
import 'package:wereda_connect/features/crop_listing/domain/models/crop_type.dart';
import 'package:wereda_connect/features/delivery_verification/data/services/dropoff_handoff_store.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/bloc/delivery_bloc.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/handoff_routes.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/widgets/delivery_card.dart';
import 'package:wereda_connect/features/live_trip/data/trip_plan_loader.dart';
import 'package:wereda_connect/features/live_trip/domain/check_in_engine.dart';
import 'package:wereda_connect/features/live_trip/domain/sharing_state.dart';
import 'package:wereda_connect/features/live_trip/domain/trip_plan.dart';
import 'package:wereda_connect/features/live_trip/presentation/check_in_engine_of.dart';
import 'package:wereda_connect/features/live_trip/presentation/widgets/live_trip_parts.dart';
import 'package:wereda_connect/features/live_trip/presentation/widgets/pause_sheet.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_bloc.dart';
import 'package:wereda_connect/features/trip_listing/presentation/bloc/trip_listing_bloc.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/services/place_repository.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';
import 'package:wereda_connect/shared/widgets/clock_text.dart';
import 'package:wereda_connect/shared/widgets/fieldway.dart';

/// Opens a screen for one delivery of the trip (tests replace it).
typedef TripStopAction = Future<void> Function(
  BuildContext context,
  Delivery delivery,
);

/// Trip mode (LT-09, live-trip.md section 4; the Trucker · Trip mode board):
/// the trucker's own screen for the one trip the phone shares for. A dark
/// header holds the trip (day, orders, load, route) and the sharing card
/// with the last and next check-in, Pause and Check in now; the light page
/// under it the next stop with I've arrived, the stop list with names (the
/// trucker is a party to every order), the pay for the trip, and while
/// offline how many check-ins wait on the phone. Every time is on the
/// viewer's clock (LT-03).
///
/// It reads the check-in engine (the same one on web, where check-ins run
/// only while the page is visible) and the trip through the app's
/// repositories. A position never appears here or on any trip row.
///
/// [tripId] opens a trip the phone does not share for (T-2: the home card of
/// a trip with a load on board or a pickup due); without it the screen
/// follows the engine's trip. A sharing trip can be stopped (T-5).
class TripModeScreen extends StatefulWidget {
  const TripModeScreen({
    super.key,
    this.tripId,
    this.engine,
    this.loader,
    this.connectivity,
    this.now,
    this.onScanPickup,
    this.onShowDropoffQr,
  });

  /// The trip to show; defaults to the one the engine shares for.
  final String? tripId;

  /// Defaults to the app's engine.
  final CheckInEngine? engine;

  /// Defaults to one over the app-wide blocs' repositories.
  final TripPlanLoader? loader;

  /// Defaults to the platform's connectivity.
  final ConnectivitySource? connectivity;

  /// The clock the pause sheet counts from.
  final DateTime Function()? now;

  /// Scan the farmer's QR at a pickup. Defaults to the QR scan screen.
  final TripStopAction? onScanPickup;

  /// Show the drop-off QR to the buyer (T-3). Defaults to [openDropoffQr].
  final TripStopAction? onShowDropoffQr;

  @override
  State<TripModeScreen> createState() => _TripModeScreenState();
}

class _TripModeScreenState extends State<TripModeScreen> {
  CheckInEngine? _engine;
  late final TripPlanLoader _loader;
  CheckInStatus _status = const CheckInStatus();
  StreamSubscription<CheckInStatus>? _statusSub;
  StreamSubscription<List<ConnectivityResult>>? _netSub;
  bool _offline = false;

  TripPlan? _plan;
  bool _loading = false;
  bool _loadFailed = false;
  String? _planTripId;

  /// A check-in, a pause or a start the trucker asked for is running (a fix
  /// can take up to a minute). Local, so no shared state grays a button.
  bool _busy = false;

  DateTime _now() => (widget.now ?? DateTime.now)();

  @override
  void initState() {
    super.initState();
    _engine = widget.engine ?? checkInEngineOf(context);
    _loader = widget.loader ??
        TripPlanLoader(
          trips: context.read<TripListingBloc>().repository,
          deliveries: context.read<DeliveryBloc>().repository,
          orders: context.read<OrderBloc>().repository,
          handoffs: SecureDropoffHandoffStore(),
        );
    _status = _engine?.status ?? const CheckInStatus();
    _statusSub = _engine?.statusStream.listen((status) {
      if (!mounted) return;
      setState(() => _status = status);
      if (_shownTripId != _planTripId) _reload();
    });
    _watchNetwork(widget.connectivity ?? PlatformConnectivitySource());
    _planTripId = _shownTripId;
    if (_planTripId != null) {
      _loading = true;
      unawaited(_fetch(_planTripId!));
    }
  }

  void _watchNetwork(ConnectivitySource source) {
    bool offline(List<ConnectivityResult> r) =>
        r.isNotEmpty && r.every((c) => c == ConnectivityResult.none);
    source.checkConnectivity().then((r) {
      if (mounted) setState(() => _offline = offline(r));
    }).catchError((Object e) {
      debugPrint('[LiveTrip] connectivity unknown: $e');
    });
    _netSub = source.onConnectivityChanged.listen(
      (r) {
        if (mounted) setState(() => _offline = offline(r));
      },
      onError: (Object e) => debugPrint('[LiveTrip] connectivity: $e'),
    );
  }

  @override
  void dispose() {
    _statusSub?.cancel();
    _netSub?.cancel();
    super.dispose();
  }

  /// The trip on screen: the one asked for, else the engine's.
  String? get _shownTripId => widget.tripId ?? _status.tripId;

  /// The sharing card's state for the trip on screen: the engine's while it
  /// is that trip's, else what the phone keeps for it (stopped, ended or
  /// scans only), else not shared yet.
  CheckInStatus get _shownStatus {
    final tripId = widget.tripId;
    if (tripId == null || _status.tripId == tripId) return _status;
    final sharing = _engine?.sharingFor(tripId);
    if (sharing != null && sharing.ended) {
      return CheckInStatus(
        tripId: tripId,
        mode: SharingMode.ended,
        stopped: sharing.stopped,
      );
    }
    if (sharing?.answer == SharingAnswer.declined) {
      return CheckInStatus(tripId: tripId, mode: SharingMode.declined);
    }
    return CheckInStatus(tripId: tripId);
  }

  void _reload() {
    final tripId = _shownTripId;
    _planTripId = tripId;
    if (tripId == null) {
      setState(() {
        _plan = null;
        _loading = false;
        _loadFailed = false;
      });
      return;
    }
    setState(() {
      _loading = true;
      _loadFailed = false;
    });
    unawaited(_fetch(tripId));
  }

  Future<void> _fetch(String tripId) async {
    final userId = _engine?.userId;
    try {
      if (userId == null) throw StateError('no trucker signed in');
      final plan = await _loader.load(tripId, userId: userId);
      // LT-14: the bundled place list names each order's own drop-off.
      await loadPlacesToName(plan.stops.map((s) => s.order));
      if (!mounted || _planTripId != tripId) return;
      setState(() {
        _plan = plan;
        _loading = false;
      });
    } on Object catch (e) {
      debugPrint('[LiveTrip] trip plan unreadable: $e');
      if (!mounted || _planTripId != tripId) return;
      setState(() {
        _loading = false;
        _loadFailed = true;
      });
    }
  }

  Future<void> _refresh() async {
    final engine = _engine;
    if (engine != null) await engine.refresh();
    if (mounted) _reload();
  }

  void _snack(String text) {
    ScaffoldMessenger.maybeOf(context)
      ?..hideCurrentSnackBar()
      ..showSnackBar(SnackBar(content: Text(text)));
  }

  String _resultText(CheckInResult result, AppLocalizations l10n) =>
      switch (result) {
        CheckInResult.recorded => l10n.tripModeCheckInSaved,
        CheckInResult.noFix => l10n.tripModeNoFix,
        CheckInResult.notSharing => l10n.tripModeNotSharing,
      };

  Future<void> _run(Future<void> Function(CheckInEngine engine) action) async {
    final engine = _engine;
    if (engine == null || _busy) return;
    setState(() => _busy = true);
    try {
      await action(engine);
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  Future<void> _checkIn({required bool arrived}) => _run((engine) async {
        final l10n = AppLocalizations.of(context)!;
        final result = await (arrived ? engine.arrived() : engine.checkInNow());
        if (mounted) _snack(_resultText(result, l10n));
      });

  Future<void> _pause() async {
    final choice = await showPauseSheet(context, now: _now());
    if (choice == null || !mounted) return;
    await _run((engine) async {
      final l10n = AppLocalizations.of(context)!;
      final until = choice.untilFrom(_now());
      if (until == null) return;
      final paused = await engine.pause(until);
      if (!paused && mounted) _snack(l10n.tripModeNotSharing);
    });
  }

  Future<void> _resume() => _run((engine) => engine.resume());

  Future<void> _share() => _run((engine) async {
        final tripId = _shownTripId;
        if (tripId == null) return;
        final l10n = AppLocalizations.of(context)!;
        final result = await engine.start(tripId);
        if (!mounted) return;
        _snack(
          result == CheckInResult.notSharing
              ? l10n.tripModeNotSharing
              : l10n.liveTripSharingStarted,
        );
      });

  /// Stop sharing (T-5), after the trucker confirmed what it costs.
  Future<void> _stop() async {
    final l10n = AppLocalizations.of(context)!;
    final confirmed = await showDialog<bool>(
      context: context,
      builder: (dialogContext) => AlertDialog(
        title: Text(l10n.tripModeStopSharingTitle),
        content: Text(l10n.tripModeStopSharingBody),
        actions: [
          TextButton(
            key: const Key('trip_mode_stop_cancel'),
            onPressed: () => Navigator.of(dialogContext).pop(false),
            child: Text(l10n.cancel),
          ),
          TextButton(
            key: const Key('trip_mode_stop_confirm'),
            onPressed: () => Navigator.of(dialogContext).pop(true),
            style: TextButton.styleFrom(
              foregroundColor: AppTheme.statusError,
            ),
            child: Text(l10n.tripModeStopSharing),
          ),
        ],
      ),
    );
    if (confirmed != true || !mounted) return;
    await _run((engine) => engine.stop());
  }

  Future<void> _scanPickup(Delivery delivery) async {
    final open = widget.onScanPickup ?? openPickupScan;
    await open(context, delivery);
    if (mounted) _reload();
  }

  Future<void> _showDropoffQr(Delivery delivery) async {
    final open = widget.onShowDropoffQr ??
        (BuildContext context, Delivery d) =>
            openDropoffQr(context, d, userId: _engine?.userId ?? '');
    await open(context, delivery);
    if (mounted) _reload();
  }

  @override
  Widget build(BuildContext context) {
    final clock = clockFormatOf(context);
    final plan = _plan;
    final hasTrip = _shownTripId != null;
    final status = _shownStatus;

    // The Trucker · Trip mode board: the trip and its sharing on a dark
    // header the full width, the stops below on the light page whose top
    // corners round over the dark ground.
    return Scaffold(
      backgroundColor: AppTheme.soilDark,
      body: SafeArea(
        child: RefreshIndicator(
          onRefresh: _refresh,
          child: ListView(
            padding: EdgeInsets.zero,
            children: [
              Container(
                color: AppTheme.soilDark,
                child: FwContentWidth(
                  child: Padding(
                    padding: const EdgeInsets.fromLTRB(20, 20, 20, 50),
                    child: Column(
                      crossAxisAlignment: CrossAxisAlignment.stretch,
                      children: [
                        _TripHeader(plan: plan),
                        const SizedBox(height: 16),
                        _SharingCard(
                          status: status,
                          clock: clock,
                          busy: _busy,
                          onPause: _pause,
                          onResume: _resume,
                          onCheckIn: () => _checkIn(arrived: false),
                          onShare: _share,
                          onStop: _stop,
                        ),
                        if (_offline) ...[
                          const SizedBox(height: 16),
                          _OfflineCheckIns(unsent: status.unsent),
                        ],
                      ],
                    ),
                  ),
                ),
              ),
              Transform.translate(
                offset: const Offset(0, -28),
                child: Container(
                  constraints: const BoxConstraints(minHeight: 400),
                  decoration: const BoxDecoration(
                    color: AppTheme.chalkBackground,
                    borderRadius: BorderRadius.vertical(
                      top: Radius.circular(28),
                    ),
                  ),
                  child: FwContentWidth(
                    child: Padding(
                      padding: const EdgeInsets.fromLTRB(20, 20, 20, 24),
                      child: Column(
                        crossAxisAlignment: CrossAxisAlignment.stretch,
                        children: [
                          if (hasTrip) ...[
                            if (_loading && plan == null)
                              const Padding(
                                padding: EdgeInsets.all(24),
                                child:
                                    Center(child: CircularProgressIndicator()),
                              )
                            else if (_loadFailed && plan == null)
                              _LoadFailed(onRetry: _reload)
                            else if (plan != null) ...[
                              if (plan.next != null) ...[
                                _NextStopCard(
                                  stop: plan.next!,
                                  place: _stopPlace(
                                    plan.next!,
                                    plan.trip?.toWoreda,
                                    context,
                                  ),
                                  viewerId: _engine?.userId ?? '',
                                  canCheckIn: status.mode == SharingMode.live ||
                                      status.mode == SharingMode.paused,
                                  busy: _busy,
                                  onArrived: () => _checkIn(arrived: true),
                                  onScanPickup: _scanPickup,
                                  onShowDropoffQr: _showDropoffQr,
                                ),
                                const SizedBox(height: 14),
                              ],
                              _StopList(plan: plan, clock: clock),
                              if (plan.pay != null) ...[
                                const SizedBox(height: 14),
                                _PayRow(pay: plan.pay!),
                              ],
                            ],
                          ],
                        ],
                      ),
                    ),
                  ),
                ),
              ),
            ],
          ),
        ),
      ),
    );
  }
}

/// The back button beside "Meki → Addis Ababa" with the day, the number of
/// orders and the load (the board's header); the screen's name until the
/// trip is read.
class _TripHeader extends StatelessWidget {
  const _TripHeader({required this.plan});

  final TripPlan? plan;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final trip = plan?.trip;
    String? sub;
    String title = l10n.tripModeTitle;
    if (trip != null) {
      final departure = dayInEthiopia(trip.departureDate);
      final today = dayInEthiopia(DateTime.now().toUtc());
      final day = DateUtils.isSameDay(departure, today)
          ? l10n.homeToday
          : DateFormat.MMMd().format(departure);
      final capacity = trip.capacity;
      final loaded = (capacity - tripRoomLeft(trip)).clamp(0, capacity);
      sub = [
        day,
        l10n.tripModeOrders(plan!.orderCount),
        if (capacity > 0)
          l10n.tripLoadedOf(
            formatQuintals(loaded.toDouble()),
            formatQuintals(capacity),
          ),
      ].join(' · ');
      title = '${trip.fromWoreda} → ${trip.toWoreda}';
    }
    return Row(
      children: [
        const LiveTripBackButton(
          background: AppTheme.soilCard,
          foreground: Colors.white,
          border: AppTheme.soilCard,
        ),
        const SizedBox(width: 12),
        Expanded(
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              if (sub != null)
                Text(
                  sub,
                  maxLines: 1,
                  overflow: TextOverflow.ellipsis,
                  style: const TextStyle(
                    fontSize: 13,
                    color: AppTheme.soilMuted,
                  ),
                ),
              Semantics(
                header: true,
                child: Text(
                  title,
                  maxLines: 1,
                  overflow: TextOverflow.ellipsis,
                  style: const TextStyle(
                    fontSize: 24,
                    fontWeight: FontWeight.w800,
                    letterSpacing: -0.7,
                    height: 1.15,
                    color: Colors.white,
                  ),
                ),
              ),
            ],
          ),
        ),
      ],
    );
  }
}

/// Sharing state with the last and next check-in, and the actions for it
/// (the board's card set into the dark header).
class _SharingCard extends StatelessWidget {
  const _SharingCard({
    required this.status,
    required this.clock,
    required this.busy,
    required this.onPause,
    required this.onResume,
    required this.onCheckIn,
    required this.onShare,
    required this.onStop,
  });

  final CheckInStatus status;
  final ClockFormat clock;
  final bool busy;
  final VoidCallback onPause;
  final VoidCallback onResume;
  final VoidCallback onCheckIn;
  final VoidCallback onShare;
  final VoidCallback onStop;

  static const _onDark = Colors.white;
  static const _onDarkMuted = AppTheme.soilMuted;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    const titleStyle = TextStyle(
      fontSize: 15,
      fontWeight: FontWeight.w700,
      color: _onDark,
    );
    const smallStyle = TextStyle(fontSize: 12, color: _onDarkMuted);

    Widget header({
      required Widget leading,
      required String title,
      required String subtitle,
      Widget? action,
    }) =>
        Row(
          children: [
            leading,
            const SizedBox(width: 12),
            Expanded(
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Text(title, style: titleStyle),
                  const SizedBox(height: 2),
                  Text(subtitle, style: smallStyle),
                ],
              ),
            ),
            if (action != null) ...[const SizedBox(width: 8), action],
          ],
        );

    final privacy = Row(
      children: [
        const Icon(Icons.visibility_outlined, size: 16, color: _onDarkMuted),
        const SizedBox(width: 8),
        Expanded(child: Text(l10n.tripModePrivacy, style: smallStyle)),
      ],
    );

    ButtonStyle gold() => FilledButton.styleFrom(
          backgroundColor: AppTheme.accentSoft,
          foregroundColor: AppTheme.soilDark,
          minimumSize: const Size.fromHeight(44),
          textStyle: const TextStyle(
            fontFamily: AppTheme.fontFamily,
            fontFamilyFallback: AppTheme.ethiopicFontFallback,
            fontSize: 14,
            fontWeight: FontWeight.w700,
          ),
          shape: RoundedRectangleBorder(
            borderRadius: BorderRadius.circular(12),
          ),
        );

    final checkIn = FilledButton.icon(
      key: const Key('trip_mode_check_in'),
      onPressed: busy ? null : onCheckIn,
      icon: busy
          ? const SizedBox(
              width: 18,
              height: 18,
              child: CircularProgressIndicator(strokeWidth: 2),
            )
          : const Icon(Icons.my_location, size: 18),
      label: Text(l10n.tripModeCheckInNow),
      style: gold(),
    );

    OutlinedButton darkButton(
      Key key,
      IconData icon,
      String label,
      VoidCallback onTap,
    ) =>
        OutlinedButton.icon(
          key: key,
          onPressed: busy ? null : onTap,
          icon: Icon(icon, size: 16),
          label: Text(label),
          style: OutlinedButton.styleFrom(
            // The theme's outlined buttons are white; this one sits on the
            // dark card.
            backgroundColor: Colors.transparent,
            foregroundColor: _onDark,
            side: const BorderSide(color: Color(0xFF4A5A52)),
            minimumSize: const Size(48, 44),
            padding: const EdgeInsets.symmetric(horizontal: 14),
            textStyle: const TextStyle(
              fontFamily: AppTheme.fontFamily,
              fontFamilyFallback: AppTheme.ethiopicFontFallback,
              fontSize: 13,
              fontWeight: FontWeight.w700,
            ),
            shape: RoundedRectangleBorder(
              borderRadius: BorderRadius.circular(12),
            ),
          ),
        );

    // T-5: stop sharing for this trip, under the check-in.
    final stop = TextButton.icon(
      key: const Key('trip_mode_stop'),
      onPressed: busy ? null : onStop,
      icon: const Icon(Icons.location_off_outlined, size: 18),
      label: Text(l10n.tripModeStopSharing),
      style: TextButton.styleFrom(
        foregroundColor: _onDarkMuted,
        minimumSize: const Size.fromHeight(44),
        textStyle: const TextStyle(
          fontFamily: AppTheme.fontFamily,
          fontFamilyFallback: AppTheme.ethiopicFontFallback,
          fontSize: 13,
          fontWeight: FontWeight.w700,
        ),
      ),
    );

    // The live dot with its halo.
    const liveDot = SizedBox(
      width: 22,
      height: 22,
      child: Stack(
        alignment: Alignment.center,
        children: [
          DecoratedBox(
            decoration: BoxDecoration(
              shape: BoxShape.circle,
              color: Color(0x408FC5A4),
            ),
            child: SizedBox(width: 22, height: 22),
          ),
          DecoratedBox(
            decoration: BoxDecoration(
              shape: BoxShape.circle,
              color: AppTheme.soilGreen,
            ),
            child: SizedBox(width: 12, height: 12),
          ),
        ],
      ),
    );

    final children = switch (status.mode) {
      SharingMode.live => [
          header(
            leading: liveDot,
            title: l10n.tripModeSharingLive,
            subtitle: _lastAndNext(l10n),
            action: darkButton(
              const Key('trip_mode_pause'),
              Icons.pause,
              l10n.tripModePause,
              onPause,
            ),
          ),
          const SizedBox(height: 12),
          privacy,
          const SizedBox(height: 12),
          checkIn,
          const SizedBox(height: 4),
          stop,
        ],
      SharingMode.paused => [
          header(
            leading: const Icon(Icons.pause_circle, color: _onDarkMuted),
            title: l10n.tripModePausedTitle,
            subtitle: l10n.tripModePausedUntil(clock.time(status.pausedUntil!)),
            action: darkButton(
              const Key('trip_mode_resume'),
              Icons.play_arrow,
              l10n.tripModeResume,
              onResume,
            ),
          ),
          const SizedBox(height: 12),
          privacy,
          const SizedBox(height: 12),
          checkIn,
          const SizedBox(height: 4),
          stop,
        ],
      SharingMode.declined => [
          header(
            leading: const Icon(Icons.qr_code_2, color: _onDarkMuted),
            title: l10n.tripModeScansOnlyTitle,
            subtitle: l10n.tripModeScansOnlyText,
          ),
          const SizedBox(height: 12),
          FilledButton.icon(
            key: const Key('trip_mode_share'),
            onPressed: busy ? null : onShare,
            icon: const Icon(Icons.share_location, size: 18),
            label: Text(l10n.liveTripConsentShare),
            style: gold(),
          ),
        ],
      SharingMode.ended => [
          header(
            leading: Icon(
              status.stopped ? Icons.location_off_outlined : Icons.check_circle,
              color: _onDarkMuted,
            ),
            title: status.stopped
                ? l10n.tripModeStoppedTitle
                : l10n.tripModeEndedTitle,
            subtitle: status.stopped
                ? l10n.tripModeStoppedText
                : l10n.tripModeEndedText,
          ),
        ],
      SharingMode.off => [
          header(
            leading:
                const Icon(Icons.local_shipping_outlined, color: _onDarkMuted),
            title: l10n.tripModeTitle,
            // A trip opened from the home card that shares nothing yet.
            subtitle: status.tripId != null
                ? l10n.tripModeNotSharingYet
                : l10n.tripModeNoTrip,
          ),
        ],
    };

    return Container(
      key: Key('trip_mode_sharing_${status.mode.name}'),
      padding: const EdgeInsets.all(16),
      decoration: BoxDecoration(
        color: AppTheme.soilCard,
        borderRadius: BorderRadius.circular(18),
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: children,
      ),
    );
  }

  /// "Last check-in 11:42 · next by 12:42".
  String _lastAndNext(AppLocalizations l10n) {
    final last = status.lastFixAt;
    final next = status.nextCheckInAt;
    final parts = [
      last == null
          ? l10n.tripModeNoCheckInYet
          : l10n.tripModeLastCheckIn(clock.time(last)),
      if (next != null) l10n.tripModeNextBy(clock.time(next)),
    ];
    return parts.join(' · ');
  }
}

/// "Crop · 1,000 kg" of a stop's order, or null when the order is unknown.
String? _cargo(TripStop stop, AppLocalizations l10n) {
  final order = stop.order;
  if (order == null) return null;
  final kg = NumberFormat('#,##0').format(order.quantity);
  return '${CropType.labelFor(order.cropType, l10n)} · '
      '${l10n.quantityKg(kg)}';
}

/// Who and where a stop is: the farmer at a pickup, the buyer (and the
/// trip's destination) at a drop-off.
/// LT-14: the town a drop-off stop names: its order's own drop-off place
/// when the server sent the point (to the committed trucker) and the place
/// list is loaded, else the trip's destination ([tripPlace]).
String? _stopPlace(TripStop stop, String? tripPlace, BuildContext context) {
  final point = stop.dropoffPoint;
  final places = PlaceRepository.instance;
  if (point != null && places.isLoaded) {
    final place = places.nearestTo(point.latitude, point.longitude);
    if (place != null) {
      return place.displayName(Localizations.localeOf(context).languageCode);
    }
  }
  return tripPlace;
}

String _stopName(TripStop stop, String? place, AppLocalizations l10n) =>
    switch (stop.kind) {
      TripStopKind.pickup => partyDisplayName(stop.delivery.farmerName, l10n),
      TripStopKind.dropoff => [
          partyDisplayName(stop.delivery.buyerName, l10n),
          if (place != null && place.isNotEmpty) place,
        ].join(', '),
    };

/// The next stop with I've arrived, and the QR scan at a pickup or the
/// drop-off QR for the buyer (T-3); a call button for the stop's party once
/// the order's contact is open (the board's card).
class _NextStopCard extends StatelessWidget {
  const _NextStopCard({
    required this.stop,
    required this.place,
    required this.viewerId,
    required this.canCheckIn,
    required this.busy,
    required this.onArrived,
    required this.onScanPickup,
    required this.onShowDropoffQr,
  });

  final TripStop stop;
  final String? place;
  final String viewerId;
  final bool canCheckIn;
  final bool busy;
  final VoidCallback onArrived;
  final void Function(Delivery delivery) onScanPickup;
  final void Function(Delivery delivery) onShowDropoffQr;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final pickup = stop.kind == TripStopKind.pickup;
    final cargo = _cargo(stop, l10n);
    final order = stop.order;
    final partyId = pickup ? stop.delivery.farmerId : stop.delivery.buyerId;
    final partyName =
        pickup ? stop.delivery.farmerName : stop.delivery.buyerName;
    final canCall = order != null && canContactParty(order, viewerId, partyId);

    return Container(
      key: const Key('trip_mode_next_stop'),
      padding: const EdgeInsets.all(16),
      decoration: BoxDecoration(
        color: AppTheme.surfaceWhite,
        borderRadius: BorderRadius.circular(18),
        border: Border.all(color: AppTheme.whisperBorder),
        boxShadow: const [
          BoxShadow(
            color: Color(0x0F15201A),
            blurRadius: 18,
            offset: Offset(0, 6),
          ),
        ],
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Row(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text(
                      (pickup
                              ? l10n.tripModeNextPickup
                              : l10n.tripModeNextDropoff)
                          .toUpperCase(),
                      style: const TextStyle(
                        fontSize: 11,
                        fontWeight: FontWeight.w800,
                        letterSpacing: 0.9,
                        color: AppTheme.money,
                      ),
                    ),
                    const SizedBox(height: 2),
                    Text(
                      _stopName(stop, place, l10n),
                      style: const TextStyle(
                        fontSize: 18,
                        fontWeight: FontWeight.w800,
                        color: AppTheme.soilDark,
                      ),
                    ),
                    // LT-12: the buyer's landmark, where the town says too
                    // little ("Addis Ababa").
                    if (stop.dropoffNote case final note?)
                      _DropoffNoteLine(
                        key: const Key('trip_mode_next_dropoff_note'),
                        note: note,
                        fontSize: 14,
                      ),
                    if (cargo != null)
                      Text(
                        cargo,
                        style: const TextStyle(
                          fontSize: 13,
                          color: AppTheme.mutedSteel,
                        ),
                      ),
                  ],
                ),
              ),
              if (canCall) ...[
                const SizedBox(width: 10),
                LiveTripSquareAction(
                  key: const Key('trip_mode_call'),
                  icon: Icons.phone_outlined,
                  semanticLabel: l10n.call,
                  background: AppTheme.surfaceWhite,
                  foreground: AppTheme.soilDark,
                  border: AppTheme.whisperBorder,
                  onTap: () => showPartyContact(
                    context,
                    order: order,
                    partyId: partyId,
                    name: partyName,
                    roleLabel: pickup ? l10n.farmer : l10n.buyer,
                  ),
                ),
              ],
            ],
          ),
          const SizedBox(height: 12),
          Row(
            children: [
              if (canCheckIn) ...[
                Expanded(
                  child: OutlinedButton(
                    key: const Key('trip_mode_arrived'),
                    onPressed: busy ? null : onArrived,
                    style: OutlinedButton.styleFrom(
                      foregroundColor: AppTheme.soilDark,
                      side: const BorderSide(color: Color(0xFFD6D3C8)),
                      minimumSize: const Size.fromHeight(48),
                      textStyle: const TextStyle(
                        fontFamily: AppTheme.fontFamily,
                        fontFamilyFallback: AppTheme.ethiopicFontFallback,
                        fontSize: 14,
                        fontWeight: FontWeight.w700,
                      ),
                      shape: RoundedRectangleBorder(
                        borderRadius: BorderRadius.circular(14),
                      ),
                    ),
                    child: Text(l10n.tripModeArrived),
                  ),
                ),
                const SizedBox(width: 8),
              ],
              Expanded(
                child: FilledButton.icon(
                  key: Key(pickup ? 'trip_mode_scan' : 'trip_mode_dropoff_qr'),
                  onPressed: () => pickup
                      ? onScanPickup(stop.delivery)
                      : onShowDropoffQr(stop.delivery),
                  icon: Icon(
                    pickup ? Icons.qr_code_scanner : Icons.qr_code_2,
                    size: 18,
                  ),
                  label: Text(
                    pickup ? l10n.deliveryScanQr : l10n.deliveryShowDropoffQr,
                  ),
                  style: FilledButton.styleFrom(
                    backgroundColor: AppTheme.soilDark,
                    foregroundColor: Colors.white,
                    minimumSize: const Size.fromHeight(48),
                    textStyle: const TextStyle(
                      fontFamily: AppTheme.fontFamily,
                      fontFamilyFallback: AppTheme.ethiopicFontFallback,
                      fontSize: 14,
                      fontWeight: FontWeight.w700,
                    ),
                    shape: RoundedRectangleBorder(
                      borderRadius: BorderRadius.circular(14),
                    ),
                  ),
                ),
              ),
            ],
          ),
        ],
      ),
    );
  }
}

/// The pickups and drop-offs of the trip on one line (the board): done
/// ones ticked in green, the next one gold, the rest hollow, the line
/// green along the done stops, gold up to the next and dashed beyond.
class _StopList extends StatelessWidget {
  const _StopList({required this.plan, required this.clock});

  final TripPlan plan;
  final ClockFormat clock;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final next = plan.next;
    final stops = plan.stops;

    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Semantics(
          header: true,
          child: Text(
            l10n.tripModeStops,
            style: const TextStyle(
              fontSize: 15,
              fontWeight: FontWeight.w800,
              color: AppTheme.soilDark,
            ),
          ),
        ),
        const SizedBox(height: 8),
        for (final (i, stop) in stops.indexed)
          _StopRow(
            stop: stop,
            place: _stopPlace(stop, plan.trip?.toWoreda, context),
            isNext: identical(stop, next),
            lineBelow: i == stops.length - 1
                ? null
                : stop.isDone
                    ? (identical(stops[i + 1], next)
                        ? _StopLine.gold
                        : _StopLine.green)
                    : _StopLine.dashed,
            clock: clock,
          ),
      ],
    );
  }
}

enum _StopLine { green, gold, dashed }

class _StopRow extends StatelessWidget {
  const _StopRow({
    required this.stop,
    required this.place,
    required this.isNext,
    required this.lineBelow,
    required this.clock,
  });

  final TripStop stop;
  final String? place;
  final bool isNext;

  /// The line down to the next row; none under the last stop.
  final _StopLine? lineBelow;
  final ClockFormat clock;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final name = _stopName(stop, place, l10n);
    final title = stop.kind == TripStopKind.pickup
        ? l10n.tripModeStopPickup(name)
        : l10n.tripModeStopDropoff(name);

    final doneAt = stop.doneAt;
    final String? detail;
    if (isNext) {
      detail = l10n.tripModeNextLabel;
    } else if (stop.isDone && stop.kind == TripStopKind.pickup) {
      detail = [
        _cargo(stop, l10n),
        if (doneAt != null) l10n.tripModeScannedAt(clock.time(doneAt)),
      ].nonNulls.join(' · ');
    } else if (stop.isDone) {
      detail =
          stop.delivery.status == DeliveryStatus.delivered && doneAt != null
              ? l10n.tripModeDeliveredAt(clock.time(doneAt))
              : deliveryStatusLabel(stop.delivery.status, l10n);
    } else {
      detail = _cargo(stop, l10n);
    }

    final Widget marker;
    if (stop.isDone) {
      marker = Container(
        width: 22,
        height: 22,
        decoration: const BoxDecoration(
          shape: BoxShape.circle,
          color: AppTheme.fieldGreen,
        ),
        child: const Icon(Icons.check, size: 14, color: Colors.white),
      );
    } else if (isNext) {
      marker = Container(
        width: 22,
        height: 22,
        alignment: Alignment.center,
        decoration: const BoxDecoration(
          shape: BoxShape.circle,
          color: AppTheme.accentSoft,
        ),
        child: Container(
          width: 8,
          height: 8,
          decoration: const BoxDecoration(
            shape: BoxShape.circle,
            color: AppTheme.soilDark,
          ),
        ),
      );
    } else {
      marker = Container(
        width: 22,
        height: 22,
        decoration: BoxDecoration(
          shape: BoxShape.circle,
          color: AppTheme.surfaceWhite,
          border: Border.all(color: const Color(0xFFA7ADA9), width: 2),
        ),
      );
    }

    final line = lineBelow;
    return IntrinsicHeight(
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          SizedBox(
            width: 22,
            child: Column(
              children: [
                marker,
                if (line != null)
                  Expanded(
                    child: Center(
                      child: line == _StopLine.dashed
                          ? const CustomPaint(
                              size: Size(2, double.infinity),
                              painter: _StopDashPainter(),
                            )
                          : Container(
                              width: 2,
                              color: line == _StopLine.gold
                                  ? AppTheme.accentSoft
                                  : AppTheme.fieldGreen,
                            ),
                    ),
                  ),
              ],
            ),
          ),
          const SizedBox(width: 12),
          Expanded(
            child: Container(
              constraints: BoxConstraints(minHeight: line == null ? 40 : 56),
              padding: EdgeInsets.only(bottom: line == null ? 0 : 10),
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Text(
                    title,
                    style: TextStyle(
                      fontSize: 14,
                      fontWeight: isNext ? FontWeight.w800 : FontWeight.w700,
                      color: AppTheme.soilDark,
                    ),
                  ),
                  // LT-12: the buyer's landmark under a drop-off still to do.
                  if (stop.dropoffNote case final note?)
                    _DropoffNoteLine(
                      key: const Key('trip_mode_dropoff_note'),
                      note: note,
                      fontSize: 12,
                    ),
                  if (detail != null && detail.isNotEmpty) ...[
                    const SizedBox(height: 2),
                    Text(
                      detail,
                      style: TextStyle(
                        fontSize: 12,
                        color: isNext ? AppTheme.money : AppTheme.mutedSteel,
                        fontWeight: isNext ? FontWeight.w700 : FontWeight.w500,
                      ),
                    ),
                  ],
                ],
              ),
            ),
          ),
        ],
      ),
    );
  }
}

/// LT-12: the buyer's drop-off landmark on a trucker's stop ("Merkato, near
/// Anwar Mosque"), after a pin. The server sends it to the trucker only once
/// committed.
class _DropoffNoteLine extends StatelessWidget {
  const _DropoffNoteLine({
    required this.note,
    required this.fontSize,
    super.key,
  });

  final String note;
  final double fontSize;

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.only(top: 2),
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Padding(
            padding: const EdgeInsets.only(top: 1),
            child: Icon(
              Icons.place_outlined,
              size: fontSize + 2,
              color: AppTheme.dropoffBlue,
            ),
          ),
          const SizedBox(width: 4),
          Expanded(
            child: Text(
              note,
              style: TextStyle(
                fontSize: fontSize,
                fontWeight: FontWeight.w600,
                color: AppTheme.dropoffBlue,
              ),
            ),
          ),
        ],
      ),
    );
  }
}

/// The dashed line under the next stop (the road still ahead).
class _StopDashPainter extends CustomPainter {
  const _StopDashPainter();

  @override
  void paint(Canvas canvas, Size size) {
    final paint = Paint()
      ..color = const Color(0xFFC9C6BB)
      ..strokeWidth = 2;
    for (var y = 0.0; y < size.height; y += 8) {
      canvas.drawLine(
        Offset(size.width / 2, y),
        Offset(size.width / 2, (y + 4).clamp(0, size.height)),
        paint,
      );
    }
  }

  @override
  bool shouldRepaint(covariant CustomPainter oldDelegate) => false;
}

/// The trucker's pay for the trip (the board's cream card).
class _PayRow extends StatelessWidget {
  const _PayRow({required this.pay});

  final double pay;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    return Container(
      key: const Key('trip_mode_pay'),
      padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 14),
      decoration: BoxDecoration(
        color: AppTheme.actionCream,
        borderRadius: BorderRadius.circular(16),
      ),
      child: Row(
        children: [
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(
                  l10n.tripModePayTitle,
                  style: const TextStyle(
                    fontSize: 14,
                    fontWeight: FontWeight.w700,
                    color: AppTheme.soilDark,
                  ),
                ),
                const SizedBox(height: 2),
                Text(
                  l10n.tripModePayNote,
                  style: const TextStyle(
                    fontSize: 12,
                    fontWeight: FontWeight.w600,
                    color: AppTheme.actionInk,
                  ),
                ),
              ],
            ),
          ),
          const SizedBox(width: 12),
          Text(
            l10n.etbCurrency(NumberFormat('#,##0').format(pay)),
            style: const TextStyle(
              fontSize: 18,
              fontWeight: FontWeight.w800,
              color: AppTheme.soilDark,
            ),
          ),
        ],
      ),
    );
  }
}

/// Offline: how many check-ins wait on the phone (the engine's count), the
/// board's cream card in the dark header.
class _OfflineCheckIns extends StatelessWidget {
  const _OfflineCheckIns({required this.unsent});

  final int unsent;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    return Container(
      key: const Key('trip_mode_offline'),
      padding: const EdgeInsets.all(16),
      decoration: BoxDecoration(
        color: AppTheme.actionCream,
        borderRadius: BorderRadius.circular(18),
      ),
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          const Padding(
            padding: EdgeInsets.only(top: 1),
            child: Icon(Icons.cloud_off, size: 22, color: AppTheme.actionInk),
          ),
          const SizedBox(width: 12),
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(
                  l10n.tripModeOfflineTitle,
                  style: const TextStyle(
                    fontSize: 15,
                    fontWeight: FontWeight.w800,
                    color: AppTheme.soilDark,
                  ),
                ),
                const SizedBox(height: 2),
                Text(
                  l10n.tripModeOfflineWaiting(unsent),
                  style: const TextStyle(
                    fontSize: 13,
                    height: 1.45,
                    color: AppTheme.actionInk,
                  ),
                ),
              ],
            ),
          ),
        ],
      ),
    );
  }
}

class _LoadFailed extends StatelessWidget {
  const _LoadFailed({required this.onRetry});

  final VoidCallback onRetry;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    return Column(
      children: [
        Text(l10n.tripModeLoadFailed, textAlign: TextAlign.center),
        const SizedBox(height: 8),
        TextButton(onPressed: onRetry, child: Text(l10n.retry)),
      ],
    );
  }
}
