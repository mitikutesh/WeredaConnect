import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:intl/intl.dart';

import 'package:wereda_connect/core/models/trip.dart';
import 'package:wereda_connect/core/models/trip_capacity.dart';
import 'package:wereda_connect/core/utils/ethiopia_time.dart';
import 'package:wereda_connect/core/utils/party_name.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/bloc/delivery_bloc.dart';
import 'package:wereda_connect/features/live_trip/domain/check_in_engine.dart';
import 'package:wereda_connect/features/live_trip/domain/trip_plan.dart';
import 'package:wereda_connect/features/live_trip/presentation/check_in_engine_of.dart';
import 'package:wereda_connect/features/live_trip/presentation/screens/trip_mode_screen.dart';
import 'package:wereda_connect/features/trip_listing/presentation/bloc/trip_listing_bloc.dart';
import 'package:wereda_connect/features/trip_listing/presentation/screens/create_trip_screen.dart';
import 'package:wereda_connect/features/trip_listing/presentation/screens/trip_detail_screen.dart';
import 'package:wereda_connect/features/trip_listing/presentation/widgets/trip_status_kind.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';
import 'package:wereda_connect/shared/widgets/clock_text.dart';

/// The trip card inside the trucker home's dark header (Trucker · Home
/// board): the route, the sharing state while the phone shares the trip's
/// location (LT-09), how full the truck is, and the next stop or the trip's
/// price. One dark card for every state: a trip on the road, a booked one,
/// an open one, the last delivered one, or no trip at all (a quiet line and
/// the recent routes; the gold Post Trip button floats below).
///
/// Reads the trucker's own trip through the trip repository (Isar on mobile,
/// the API on web) and, for the next stop, the trucker's deliveries through
/// the delivery repository (the same read the Action needed card makes, so
/// the web folds them). A tap opens trip mode while the trip is on the road
/// with sharing, else the trip.
class TruckerHeroCard extends StatefulWidget {
  final String userId;
  final VoidCallback? onRefresh;

  /// Defaults to the app's check-in engine; null when there is none.
  final CheckInEngine? engine;

  const TruckerHeroCard({
    required this.userId,
    super.key,
    this.onRefresh,
    this.engine,
  });

  @override
  State<TruckerHeroCard> createState() => _TruckerHeroCardState();
}

class _TruckerHeroCardState extends State<TruckerHeroCard> {
  Trip? _trip;
  TripStop? _next;
  List<({String from, String to})> _recentRoutes = [];
  bool _loading = true;
  CheckInEngine? _engine;
  CheckInStatus _status = const CheckInStatus();
  StreamSubscription<CheckInStatus>? _statusSub;

  @override
  void initState() {
    super.initState();
    _engine = widget.engine ?? checkInEngineOf(context);
    _status = _engine?.status ?? const CheckInStatus();
    _statusSub = _engine?.statusStream.listen((status) {
      if (!mounted) return;
      final tripChanged = status.tripId != _status.tripId;
      setState(() => _status = status);
      if (tripChanged) _loadData();
    });
    _loadData();
  }

  @override
  void dispose() {
    _statusSub?.cancel();
    super.dispose();
  }

  /// The phone shares (or scans) [trip] now: the engine's trip is this one
  /// and sharing has not ended.
  bool _sharing(Trip trip) =>
      _status.tripId == trip.tripId &&
      switch (_status.mode) {
        SharingMode.live || SharingMode.paused || SharingMode.declined => true,
        SharingMode.off || SharingMode.ended => false,
      };

  Future<void> _loadData() async {
    try {
      final repo = context.read<TripListingBloc>().repository;
      // The trip the phone shares comes first: the most relevant trip may be
      // a newer open one while another is still on the road.
      Trip? trip;
      final sharedTripId = _status.tripId;
      if (sharedTripId != null && _status.mode != SharingMode.ended) {
        try {
          trip = await repo.getTripById(sharedTripId);
        } on Object {
          trip = null;
        }
        if (trip != null && trip.truckerId != widget.userId) trip = null;
      }
      trip ??= await repo.getMostRelevantTrip(widget.userId);
      final routes = await repo.getRecentRoutes(widget.userId);
      if (!mounted) return;
      final next = trip == null ? null : await _nextStop(trip);
      if (!mounted) return;
      setState(() {
        _trip = trip;
        _next = next;
        _recentRoutes = routes;
        _loading = false;
      });
    } on Object {
      if (mounted) setState(() => _loading = false);
    }
  }

  /// The first stop of [trip] not done yet, from the trucker's deliveries;
  /// null before the trip is on the road, when the deliveries cannot be
  /// read, or when every stop is done.
  Future<TripStop?> _nextStop(Trip trip) async {
    if (trip.status != TripStatus.inTransit && !_sharing(trip)) return null;
    final DeliveryBloc bloc;
    try {
      bloc = context.read<DeliveryBloc>();
    } on ProviderNotFoundException {
      return null;
    }
    try {
      final deliveries = await bloc.repository.getDeliveriesByUser(
        widget.userId,
        role: 'trucker',
      );
      return TripPlan.build(
        trip: trip,
        deliveries: [
          for (final d in deliveries)
            if (d.tripId == trip.tripId && d.truckerId == widget.userId) d,
        ],
      ).next;
    } on Object catch (e) {
      debugPrint('[TruckerHeroCard] deliveries unreadable: $e');
      return null;
    }
  }

  Future<void> _open(Widget screen) async {
    await Navigator.of(context).push<void>(
      MaterialPageRoute(builder: (_) => screen),
    );
    if (!mounted) return;
    widget.onRefresh?.call();
    _loadData();
  }

  @override
  Widget build(BuildContext context) {
    if (_loading) {
      return const _DarkCard(
        child: SizedBox(
          height: 56,
          child: Center(
            child: SizedBox(
              width: 18,
              height: 18,
              child: CircularProgressIndicator(
                strokeWidth: 2,
                color: AppTheme.soilMuted,
              ),
            ),
          ),
        ),
      );
    }

    final trip = _trip;
    if (trip == null || trip.status == TripStatus.cancelled) {
      return _NoTripCard(
        recentRoutes: _recentRoutes,
        onOpen: _open,
      );
    }

    final l10n = AppLocalizations.of(context)!;
    final clock = clockFormatOf(context);
    final sharing = _sharing(trip);

    // The right slot: the sharing state while the phone shares, else the
    // trip's state (its day while it is open).
    final Widget slot;
    if (sharing) {
      slot = switch (_status.mode) {
        SharingMode.live => _SlotText(
            l10n.tripModeSharingLive,
            color: AppTheme.soilGreen,
            dot: true,
          ),
        SharingMode.paused => _SlotText(
            '${l10n.liveTripPaused} · ${clock.time(_status.pausedUntil!)}',
          ),
        _ => _SlotText(l10n.tripModeScansOnlyTitle),
      };
    } else {
      slot = switch (trip.status) {
        TripStatus.open => _SlotText(_departureDay(l10n, trip)),
        TripStatus.delivered => _SlotText(
            tripStatusLabel(trip.status, l10n),
            color: AppTheme.soilGreen,
          ),
        _ => _SlotText(tripStatusLabel(trip.status, l10n)),
      };
    }

    final next = _next;
    final price = l10n.tripPricePerQuintalValue(
      trip.pricePerQuintal.toStringAsFixed(0),
    );
    final String line;
    if (trip.status == TripStatus.delivered) {
      line = l10n.postNextTrip;
    } else if (next != null) {
      line = switch (next.kind) {
        TripStopKind.pickup => l10n.homeNextPickup(
            partyDisplayName(next.delivery.farmerName, l10n),
          ),
        TripStopKind.dropoff => l10n.homeNextDropoff(
            partyDisplayName(next.delivery.buyerName, l10n),
          ),
      };
    } else if (trip.status == TripStatus.matched) {
      line = l10n.homeTripMatchedLine;
    } else {
      line = price;
    }

    final Widget destination = sharing
        ? const TripModeScreen()
        : trip.status == TripStatus.delivered
            ? CreateTripScreen.fromTrip(trip)
            : TripDetailScreen(trip: trip);

    return _DarkCard(
      onTap: () => _open(destination),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Row(
            children: [
              Expanded(
                child: Text(
                  '${trip.fromWoreda} → ${trip.toWoreda}',
                  maxLines: 1,
                  overflow: TextOverflow.ellipsis,
                  style: const TextStyle(
                    fontSize: 16,
                    fontWeight: FontWeight.w800,
                    letterSpacing: -0.2,
                    color: Colors.white,
                  ),
                ),
              ),
              const SizedBox(width: 8),
              slot,
            ],
          ),
          if (trip.capacity > 0) ...[
            const SizedBox(height: 10),
            _TripLoadBar(trip: trip),
          ],
          const SizedBox(height: 10),
          Row(
            children: [
              Expanded(
                child: Text(
                  line,
                  maxLines: 1,
                  overflow: TextOverflow.ellipsis,
                  style: const TextStyle(
                    fontSize: 13,
                    color: AppTheme.soilMuted,
                  ),
                ),
              ),
              const Icon(Icons.chevron_right, size: 18, color: Colors.white),
            ],
          ),
        ],
      ),
    );
  }

  /// "Today", or the trip's departure day ("Oct 30").
  static String _departureDay(AppLocalizations l10n, Trip trip) {
    final departure = dayInEthiopia(trip.departureDate);
    final today = dayInEthiopia(DateTime.now().toUtc());
    return DateUtils.isSameDay(departure, today)
        ? l10n.homeToday
        : DateFormat.MMMd().format(departure);
  }
}

/// The header's card: soil ground, radius 18, white ink.
class _DarkCard extends StatelessWidget {
  final Widget child;
  final VoidCallback? onTap;

  const _DarkCard({required this.child, this.onTap});

  @override
  Widget build(BuildContext context) {
    return Material(
      color: AppTheme.soilCard,
      shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(18)),
      clipBehavior: Clip.antiAlias,
      child: InkWell(
        key: const Key('trucker_hero_card'),
        onTap: onTap,
        child: Padding(padding: const EdgeInsets.all(16), child: child),
      ),
    );
  }
}

/// The small state text at the card's top right, with a green dot while the
/// phone shares its location.
class _SlotText extends StatelessWidget {
  final String text;
  final Color color;
  final bool dot;

  const _SlotText(
    this.text, {
    this.color = AppTheme.soilMuted,
    this.dot = false,
  });

  @override
  Widget build(BuildContext context) {
    return Row(
      mainAxisSize: MainAxisSize.min,
      children: [
        if (dot) ...[
          Container(
            width: 8,
            height: 8,
            decoration: BoxDecoration(color: color, shape: BoxShape.circle),
          ),
          const SizedBox(width: 6),
        ],
        Text(
          text,
          maxLines: 1,
          overflow: TextOverflow.ellipsis,
          style: TextStyle(
            fontSize: 12,
            fontWeight: FontWeight.w700,
            color: color,
          ),
        ),
      ],
    );
  }
}

/// How full the truck is (the board's "Space on today's trip"): booked
/// quintals out of the capacity, as a gold bar on the dark card.
class _TripLoadBar extends StatelessWidget {
  final Trip trip;

  const _TripLoadBar({required this.trip});

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final capacity = trip.capacity;
    final loaded = (capacity - tripRoomLeft(trip)).clamp(0, capacity);
    final text = l10n.tripLoadedOf(
      formatQuintals(loaded.toDouble()),
      formatQuintals(capacity),
    );

    return Semantics(
      label: '${l10n.tripSpaceTitle}: $text',
      excludeSemantics: true,
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Row(
            children: [
              Expanded(
                child: Text(
                  l10n.tripSpaceTitle,
                  maxLines: 1,
                  overflow: TextOverflow.ellipsis,
                  style: const TextStyle(
                    fontSize: 13,
                    color: AppTheme.soilMuted,
                  ),
                ),
              ),
              const SizedBox(width: 8),
              Text(
                text,
                style: const TextStyle(
                  fontSize: 13,
                  fontWeight: FontWeight.w700,
                  color: Colors.white,
                ),
              ),
            ],
          ),
          const SizedBox(height: 8),
          ClipRRect(
            borderRadius: BorderRadius.circular(999),
            child: LinearProgressIndicator(
              value: loaded / capacity,
              minHeight: 8,
              backgroundColor: AppTheme.soilTrack,
              valueColor:
                  const AlwaysStoppedAnimation<Color>(AppTheme.accentGold),
            ),
          ),
        ],
      ),
    );
  }
}

/// No trip posted (or the last one cancelled): a quiet line and the recent
/// routes as chips that prefill a new trip. The card itself opens Create
/// trip too.
class _NoTripCard extends StatelessWidget {
  final List<({String from, String to})> recentRoutes;
  final Future<void> Function(Widget screen) onOpen;

  const _NoTripCard({required this.recentRoutes, required this.onOpen});

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;

    return _DarkCard(
      onTap: () => onOpen(const CreateTripScreen()),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Row(
            children: [
              const Icon(
                Icons.local_shipping_outlined,
                size: 28,
                color: AppTheme.soilMuted,
              ),
              const SizedBox(width: 12),
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text(
                      l10n.noActiveTrip,
                      style: const TextStyle(
                        fontSize: 16,
                        fontWeight: FontWeight.w800,
                        letterSpacing: -0.2,
                        color: Colors.white,
                      ),
                    ),
                    const SizedBox(height: 2),
                    Text(
                      l10n.homeTripNoneHint,
                      style: const TextStyle(
                        fontSize: 13,
                        color: AppTheme.soilMuted,
                      ),
                    ),
                  ],
                ),
              ),
            ],
          ),
          if (recentRoutes.isNotEmpty) ...[
            const SizedBox(height: 12),
            Text(
              l10n.recentRoutes,
              style: const TextStyle(
                fontSize: 12,
                fontWeight: FontWeight.w700,
                color: AppTheme.soilMuted,
              ),
            ),
            const SizedBox(height: 8),
            Wrap(
              spacing: 8,
              runSpacing: 8,
              children: [
                for (final route in recentRoutes)
                  Material(
                    color: AppTheme.soilTrack,
                    borderRadius: BorderRadius.circular(10),
                    child: InkWell(
                      borderRadius: BorderRadius.circular(10),
                      onTap: () => onOpen(
                        CreateTripScreen(
                          fromWoreda: route.from,
                          toWoreda: route.to,
                        ),
                      ),
                      child: Padding(
                        padding: const EdgeInsets.symmetric(
                          horizontal: 12,
                          vertical: 8,
                        ),
                        child: Text(
                          '${route.from} → ${route.to}',
                          style: const TextStyle(
                            fontSize: 12,
                            fontWeight: FontWeight.w700,
                            color: Colors.white,
                          ),
                        ),
                      ),
                    ),
                  ),
              ],
            ),
          ],
        ],
      ),
    );
  }
}
