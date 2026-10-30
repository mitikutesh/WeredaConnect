import 'dart:async';

import 'package:connectivity_plus/connectivity_plus.dart';
import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:intl/intl.dart';

import 'package:wereda_connect/core/models/trip_capacity.dart';
import 'package:wereda_connect/core/models/delivery.dart';
import 'package:wereda_connect/core/sync/connectivity_monitor.dart';
import 'package:wereda_connect/core/utils/ethiopia_time.dart';
import 'package:wereda_connect/core/utils/ethiopian_clock.dart';
import 'package:wereda_connect/core/utils/party_name.dart';
import 'package:wereda_connect/features/crop_listing/domain/models/crop_type.dart';
import 'package:wereda_connect/features/delivery_verification/data/services/device_signing_key_service.dart';
import 'package:wereda_connect/features/delivery_verification/data/services/gps_capture_service.dart';
import 'package:wereda_connect/features/delivery_verification/data/services/photo_manager.dart';
import 'package:wereda_connect/features/delivery_verification/data/services/qr_verification_service.dart';
import 'package:wereda_connect/features/delivery_verification/data/verification_type.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/bloc/delivery_bloc.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/screens/delivery_detail_screen.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/screens/qr_scan_screen.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/widgets/delivery_card.dart';
import 'package:wereda_connect/features/live_trip/data/trip_plan_loader.dart';
import 'package:wereda_connect/features/live_trip/domain/check_in_engine.dart';
import 'package:wereda_connect/features/live_trip/domain/trip_plan.dart';
import 'package:wereda_connect/features/live_trip/presentation/check_in_engine_of.dart';
import 'package:wereda_connect/features/live_trip/presentation/widgets/live_trip_parts.dart';
import 'package:wereda_connect/features/live_trip/presentation/widgets/pause_sheet.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_bloc.dart';
import 'package:wereda_connect/features/trip_listing/presentation/bloc/trip_listing_bloc.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
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
class TripModeScreen extends StatefulWidget {
  const TripModeScreen({
    super.key,
    this.engine,
    this.loader,
    this.connectivity,
    this.now,
    this.onScanPickup,
    this.onOpenDelivery,
  });

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

  /// Open a drop-off's delivery. Defaults to the delivery detail screen.
  final TripStopAction? onOpenDelivery;

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
        );
    _status = _engine?.status ?? const CheckInStatus();
    _statusSub = _engine?.statusStream.listen((status) {
      if (!mounted) return;
      setState(() => _status = status);
      if (status.tripId != _planTripId) _reload();
    });
    _watchNetwork(widget.connectivity ?? PlatformConnectivitySource());
    _planTripId = _status.tripId;
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

  void _reload() {
    final tripId = _status.tripId;
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
        final tripId = _status.tripId;
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

  Future<void> _scanPickup(Delivery delivery) async {
    final open = widget.onScanPickup ?? _openScanScreen;
    await open(context, delivery);
    if (mounted) _reload();
  }

  Future<void> _openDelivery(Delivery delivery) async {
    final open = widget.onOpenDelivery ?? _openDeliveryScreen;
    await open(context, delivery);
    if (mounted) _reload();
  }

  static Future<void> _openScanScreen(BuildContext context, Delivery d) =>
      Navigator.of(context).push<void>(
        MaterialPageRoute(
          builder: (_) => QrScanScreen(
            delivery: d,
            verificationType: VerificationType.pickup,
            qrService: QrVerificationService(
              deviceKeyService: DeviceSigningKeyService(),
            ),
            gpsService: GpsCaptureService(),
            photoManager: PhotoManager(),
          ),
        ),
      );

  static Future<void> _openDeliveryScreen(BuildContext context, Delivery d) =>
      Navigator.of(context).push<void>(
        MaterialPageRoute(
          builder: (_) =>
              DeliveryDetailScreen(delivery: d, currentUserId: d.truckerId),
        ),
      );

  @override
  Widget build(BuildContext context) {
    final clock = clockFormatOf(context);
    final plan = _plan;
    final hasTrip = _status.tripId != null;

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
                          status: _status,
                          clock: clock,
                          busy: _busy,
                          onPause: _pause,
                          onResume: _resume,
                          onCheckIn: () => _checkIn(arrived: false),
                          onShare: _share,
                        ),
                        if (_offline) ...[
                          const SizedBox(height: 16),
                          _OfflineCheckIns(unsent: _status.unsent),
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
                                  place: plan.trip?.toWoreda,
                                  viewerId: _engine?.userId ?? '',
                                  canCheckIn:
                                      _status.mode == SharingMode.live ||
                                          _status.mode == SharingMode.paused,
                                  busy: _busy,
                                  onArrived: () => _checkIn(arrived: true),
                                  onScanPickup: _scanPickup,
                                  onOpenDelivery: _openDelivery,
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
  });

  final CheckInStatus status;
  final ClockFormat clock;
  final bool busy;
  final VoidCallback onPause;
  final VoidCallback onResume;
  final VoidCallback onCheckIn;
  final VoidCallback onShare;

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
          textStyle: const TextStyle(fontSize: 14, fontWeight: FontWeight.w700),
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
            textStyle:
                const TextStyle(fontSize: 13, fontWeight: FontWeight.w700),
            shape: RoundedRectangleBorder(
              borderRadius: BorderRadius.circular(12),
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
            leading: const Icon(Icons.check_circle, color: _onDarkMuted),
            title: l10n.tripModeEndedTitle,
            subtitle: l10n.tripModeEndedText,
          ),
        ],
      SharingMode.off => [
          header(
            leading:
                const Icon(Icons.local_shipping_outlined, color: _onDarkMuted),
            title: l10n.tripModeTitle,
            subtitle: l10n.tripModeNoTrip,
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
String _stopName(TripStop stop, String? place, AppLocalizations l10n) =>
    switch (stop.kind) {
      TripStopKind.pickup => partyDisplayName(stop.delivery.farmerName, l10n),
      TripStopKind.dropoff => [
          partyDisplayName(stop.delivery.buyerName, l10n),
          if (place != null && place.isNotEmpty) place,
        ].join(', '),
    };

/// The next stop with I've arrived, and the QR scan at a pickup or the
/// delivery at a drop-off; a call button for the stop's party once the
/// order's contact is open (the board's card).
class _NextStopCard extends StatelessWidget {
  const _NextStopCard({
    required this.stop,
    required this.place,
    required this.viewerId,
    required this.canCheckIn,
    required this.busy,
    required this.onArrived,
    required this.onScanPickup,
    required this.onOpenDelivery,
  });

  final TripStop stop;
  final String? place;
  final String viewerId;
  final bool canCheckIn;
  final bool busy;
  final VoidCallback onArrived;
  final void Function(Delivery delivery) onScanPickup;
  final void Function(Delivery delivery) onOpenDelivery;

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
                  key: Key(pickup ? 'trip_mode_scan' : 'trip_mode_open'),
                  onPressed: () => pickup
                      ? onScanPickup(stop.delivery)
                      : onOpenDelivery(stop.delivery),
                  icon: Icon(
                    pickup ? Icons.qr_code_scanner : Icons.qr_code_2,
                    size: 18,
                  ),
                  label: Text(
                    pickup ? l10n.deliveryScanQr : l10n.orderOpenDelivery,
                  ),
                  style: FilledButton.styleFrom(
                    backgroundColor: AppTheme.soilDark,
                    foregroundColor: Colors.white,
                    minimumSize: const Size.fromHeight(48),
                    textStyle: const TextStyle(
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
            place: plan.trip?.toWoreda,
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
