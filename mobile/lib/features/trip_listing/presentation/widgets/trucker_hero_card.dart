import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';

import 'package:wereda_connect/core/models/trip.dart';
import 'package:wereda_connect/core/models/trip_capacity.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/features/trip_listing/presentation/bloc/trip_listing_bloc.dart';
import 'package:wereda_connect/features/trip_listing/presentation/bloc/trip_listing_event.dart';
import 'package:wereda_connect/features/trip_listing/presentation/bloc/trip_listing_state.dart';
import 'package:wereda_connect/features/trip_listing/presentation/screens/create_trip_screen.dart';
import 'package:wereda_connect/features/trip_listing/presentation/screens/trip_detail_screen.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';
import 'package:wereda_connect/shared/widgets/wc_card.dart';

/// Context-aware hero card for the trucker home screen.
///
/// Queries Isar for the trucker's highest-priority trip and renders
/// one of 5 card variants: IDLE, POSTED, MATCHED, IN_TRANSIT, DELIVERED.
/// All interactive elements use 56dp minimum touch targets.
class TruckerHeroCard extends StatefulWidget {
  final String userId;
  final VoidCallback? onRefresh;

  const TruckerHeroCard({
    required this.userId,
    super.key,
    this.onRefresh,
  });

  @override
  State<TruckerHeroCard> createState() => _TruckerHeroCardState();
}

class _TruckerHeroCardState extends State<TruckerHeroCard> {
  Trip? _trip;
  List<({String from, String to})> _recentRoutes = [];
  bool _loading = true;

  @override
  void initState() {
    super.initState();
    _loadData();
  }

  Future<void> _loadData() async {
    try {
      final repo = context.read<TripListingBloc>().repository;
      final trip = await repo.getMostRelevantTrip(widget.userId);
      final routes = await repo.getRecentRoutes(widget.userId);
      if (mounted) {
        setState(() {
          _trip = trip;
          _recentRoutes = routes;
          _loading = false;
        });
      }
    } on Object {
      if (mounted) {
        setState(() => _loading = false);
      }
    }
  }

  @override
  Widget build(BuildContext context) {
    if (_loading) {
      return const WcCard(
        child: Center(
          child: Padding(
            padding: EdgeInsets.all(24),
            child: CircularProgressIndicator(),
          ),
        ),
      );
    }

    final trip = _trip;

    // IDLE — no trips
    if (trip == null) {
      return _IdleCard(
        recentRoutes: _recentRoutes,
        userId: widget.userId,
        onRefresh: widget.onRefresh,
      );
    }

    return switch (trip.status) {
      TripStatus.open => _PostedCard(trip: trip),
      TripStatus.matched => _MatchedCard(
          trip: trip,
          onRefresh: widget.onRefresh,
        ),
      TripStatus.inTransit => _InTransitCard(
          trip: trip,
          onRefresh: widget.onRefresh,
        ),
      TripStatus.delivered => _DeliveredCard(
          trip: trip,
          recentRoutes: _recentRoutes,
          userId: widget.userId,
          onRefresh: widget.onRefresh,
        ),
      TripStatus.cancelled => _IdleCard(
          recentRoutes: _recentRoutes,
          userId: widget.userId,
          onRefresh: widget.onRefresh,
        ),
    };
  }
}

/// How full the truck is (the Trucker · Home board's "Space on today's
/// trip"): booked quintals out of the capacity, as a bar.
class _TripLoadBar extends StatelessWidget {
  final Trip trip;

  const _TripLoadBar({required this.trip});

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final capacity = trip.capacity;
    if (capacity <= 0) return const SizedBox.shrink();
    final loaded = (capacity - tripRoomLeft(trip)).clamp(0, capacity);
    final text = l10n.tripLoadedOf(
      formatQuintals(loaded.toDouble()),
      formatQuintals(capacity),
    );

    return Semantics(
      label: '${l10n.tripSpaceTitle}: $text',
      excludeSemantics: true,
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Text(
            l10n.tripSpaceTitle,
            style: const TextStyle(
              fontSize: 12,
              fontWeight: FontWeight.w700,
              color: AppTheme.mutedSteel,
            ),
          ),
          const SizedBox(height: 6),
          ClipRRect(
            borderRadius: BorderRadius.circular(999),
            child: LinearProgressIndicator(
              value: loaded / capacity,
              minHeight: 8,
              backgroundColor: AppTheme.whisperBorder,
              valueColor: const AlwaysStoppedAnimation<Color>(
                Color(0xFFD9922B),
              ),
            ),
          ),
          const SizedBox(height: 6),
          Text(
            text,
            style: const TextStyle(
              fontSize: 13,
              fontWeight: FontWeight.w600,
              color: AppTheme.soilDark,
            ),
          ),
        ],
      ),
    );
  }
}

/// IDLE state — no active trips, prompt to create one.
class _IdleCard extends StatelessWidget {
  final List<({String from, String to})> recentRoutes;
  final String userId;
  final VoidCallback? onRefresh;

  const _IdleCard({
    required this.recentRoutes,
    required this.userId,
    this.onRefresh,
  });

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;

    return WcCard(
      child: Column(
        children: [
          const Icon(
            Icons.local_shipping_outlined,
            size: 48,
            color: AppTheme.textMuted,
          ),
          const SizedBox(height: 12),
          Text(
            l10n.noActiveTrip,
            style: const TextStyle(
              fontSize: 16,
              color: AppTheme.textMuted,
            ),
          ),
          const SizedBox(height: 16),
          if (recentRoutes.isNotEmpty) ...[
            _RecentRouteChips(
              routes: recentRoutes,
              userId: userId,
              onRefresh: onRefresh,
            ),
            const SizedBox(height: 12),
          ],
          SizedBox(
            width: double.infinity,
            child: ElevatedButton.icon(
              onPressed: () async {
                await Navigator.of(context).push<void>(
                  MaterialPageRoute(
                    builder: (_) => const CreateTripScreen(),
                  ),
                );
                onRefresh?.call();
              },
              style: ElevatedButton.styleFrom(
                backgroundColor: AppTheme.accentGold,
                foregroundColor: Colors.white,
                minimumSize: const Size.fromHeight(56),
                shape: RoundedRectangleBorder(
                  borderRadius: BorderRadius.circular(8),
                ),
              ),
              icon: const Icon(Icons.add),
              label: Text(
                l10n.postATrip,
                style: const TextStyle(fontSize: 16),
              ),
            ),
          ),
        ],
      ),
    );
  }
}

/// POSTED state — trip is open and visible to buyers.
class _PostedCard extends StatelessWidget {
  final Trip trip;

  const _PostedCard({required this.trip});

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;

    return WcCard(
      onTap: () {
        Navigator.of(context).push<void>(
          MaterialPageRoute(
            builder: (_) => TripDetailScreen(trip: trip),
          ),
        );
      },
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          // No status pill: the whole card opens the trip, and an "Open"
          // label here read as a second button.
          _RouteDisplay(from: trip.fromWoreda, to: trip.toWoreda),
          const SizedBox(height: 12),
          Text(
            '${l10n.etbCurrency(trip.pricePerQuintal.toStringAsFixed(0))}/${l10n.quintals}',
            style: const TextStyle(
              fontSize: 20,
              fontWeight: FontWeight.bold,
              color: AppTheme.primaryGreen,
            ),
          ),
          const SizedBox(height: 12),
          _TripLoadBar(trip: trip),
        ],
      ),
    );
  }
}

/// MATCHED state — trip matched, show Start Trip button.
class _MatchedCard extends StatelessWidget {
  final Trip trip;
  final VoidCallback? onRefresh;

  const _MatchedCard({required this.trip, this.onRefresh});

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;

    return WcCard(
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Row(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Expanded(
                child: _RouteDisplay(
                  from: trip.fromWoreda,
                  to: trip.toWoreda,
                ),
              ),
              const SizedBox(width: 8),
              Container(
                padding: const EdgeInsets.symmetric(
                  horizontal: 10,
                  vertical: 4,
                ),
                decoration: BoxDecoration(
                  color: AppTheme.statusInfo.withValues(alpha: 0.15),
                  borderRadius: BorderRadius.circular(12),
                ),
                child: Text(
                  l10n.tripStatusMatched,
                  style: const TextStyle(
                    color: AppTheme.statusInfo,
                    fontWeight: FontWeight.w600,
                    fontSize: 12,
                  ),
                ),
              ),
            ],
          ),
          const SizedBox(height: 12),
          Text(
            '${l10n.etbCurrency(trip.pricePerQuintal.toStringAsFixed(0))}/${l10n.quintals}',
            style: const TextStyle(
              fontSize: 20,
              fontWeight: FontWeight.bold,
              color: AppTheme.primaryGreen,
            ),
          ),
          const SizedBox(height: 12),
          _TripLoadBar(trip: trip),
          const SizedBox(height: 16),
          SizedBox(
            width: double.infinity,
            child: ElevatedButton.icon(
              onPressed: () {
                _confirmStartTrip(context, l10n);
              },
              style: ElevatedButton.styleFrom(
                backgroundColor: AppTheme.statusSuccess,
                foregroundColor: Colors.white,
                minimumSize: const Size.fromHeight(56),
                shape: RoundedRectangleBorder(
                  borderRadius: BorderRadius.circular(8),
                ),
              ),
              icon: const Icon(Icons.play_arrow),
              label: Text(
                l10n.startTrip,
                style: const TextStyle(fontSize: 16),
              ),
            ),
          ),
        ],
      ),
    );
  }

  void _confirmStartTrip(BuildContext context, AppLocalizations l10n) {
    _showHeroConfirmation(
      context,
      title: l10n.startTrip,
      body: l10n.startTripConfirmation,
      confirmLabel: l10n.startTrip,
      tripId: trip.tripId,
      eventFactory: (truckerId) =>
          StartTrip(tripId: trip.tripId, truckerId: truckerId),
      onRefresh: onRefresh,
    );
  }
}

/// IN_TRANSIT state — show destination and Mark Delivered button.
class _InTransitCard extends StatelessWidget {
  final Trip trip;
  final VoidCallback? onRefresh;

  const _InTransitCard({required this.trip, this.onRefresh});

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;

    return WcCard(
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Text(
            l10n.headingTo(trip.toWoreda),
            style: const TextStyle(
              fontSize: 20,
              fontWeight: FontWeight.bold,
              color: AppTheme.textDark,
            ),
          ),
          const SizedBox(height: 8),
          _RouteDisplay(from: trip.fromWoreda, to: trip.toWoreda),
          const SizedBox(height: 12),
          Text(
            '${l10n.etbCurrency(trip.pricePerQuintal.toStringAsFixed(0))}/${l10n.quintals}',
            style: const TextStyle(
              fontSize: 20,
              fontWeight: FontWeight.bold,
              color: AppTheme.primaryGreen,
            ),
          ),
          const SizedBox(height: 12),
          _TripLoadBar(trip: trip),
          const SizedBox(height: 16),
          SizedBox(
            width: double.infinity,
            child: ElevatedButton.icon(
              onPressed: () {
                _confirmMarkDelivered(context, l10n);
              },
              style: ElevatedButton.styleFrom(
                backgroundColor: AppTheme.statusSuccess,
                foregroundColor: Colors.white,
                minimumSize: const Size.fromHeight(56),
                shape: RoundedRectangleBorder(
                  borderRadius: BorderRadius.circular(8),
                ),
              ),
              icon: const Icon(Icons.check_circle_outline),
              label: Text(
                l10n.markDelivered,
                style: const TextStyle(fontSize: 16),
              ),
            ),
          ),
        ],
      ),
    );
  }

  void _confirmMarkDelivered(BuildContext context, AppLocalizations l10n) {
    _showHeroConfirmation(
      context,
      title: l10n.markDelivered,
      body: l10n.markDeliveredConfirmation,
      confirmLabel: l10n.markDelivered,
      tripId: trip.tripId,
      eventFactory: (truckerId) =>
          MarkTripDelivered(tripId: trip.tripId, truckerId: truckerId),
      onRefresh: onRefresh,
    );
  }
}

/// DELIVERED state — trip complete, prompt to post next trip.
class _DeliveredCard extends StatelessWidget {
  final Trip trip;
  final List<({String from, String to})> recentRoutes;
  final String userId;
  final VoidCallback? onRefresh;

  const _DeliveredCard({
    required this.trip,
    required this.recentRoutes,
    required this.userId,
    this.onRefresh,
  });

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;

    return WcCard(
      child: Column(
        children: [
          const Icon(
            Icons.check_circle,
            size: 48,
            color: AppTheme.statusSuccess,
          ),
          const SizedBox(height: 8),
          Text(
            l10n.tripDeliveredSuccess,
            style: const TextStyle(
              fontSize: 16,
              fontWeight: FontWeight.w600,
              color: AppTheme.textDark,
            ),
          ),
          const SizedBox(height: 4),
          Text(
            '${trip.fromWoreda} → ${trip.toWoreda}',
            style: const TextStyle(
              fontSize: 14,
              color: AppTheme.textMuted,
            ),
          ),
          const SizedBox(height: 16),
          if (recentRoutes.isNotEmpty) ...[
            _RecentRouteChips(
              routes: recentRoutes,
              userId: userId,
              onRefresh: onRefresh,
            ),
            const SizedBox(height: 12),
          ],
          SizedBox(
            width: double.infinity,
            child: ElevatedButton.icon(
              onPressed: () async {
                await Navigator.of(context).push<void>(
                  MaterialPageRoute(
                    builder: (_) => CreateTripScreen.fromTrip(trip),
                  ),
                );
                onRefresh?.call();
              },
              style: ElevatedButton.styleFrom(
                backgroundColor: AppTheme.accentGold,
                foregroundColor: Colors.white,
                minimumSize: const Size.fromHeight(56),
                shape: RoundedRectangleBorder(
                  borderRadius: BorderRadius.circular(8),
                ),
              ),
              icon: const Icon(Icons.replay),
              label: Text(
                l10n.postNextTrip,
                style: const TextStyle(fontSize: 16),
              ),
            ),
          ),
        ],
      ),
    );
  }
}

/// Compact route display: green dot → dashed → red dot.
/// Shared confirmation dialog for hero card trip actions.
void _showHeroConfirmation(
  BuildContext context, {
  required String title,
  required String body,
  required String confirmLabel,
  required String tripId,
  required TripListingEvent Function(String truckerId) eventFactory,
  VoidCallback? onRefresh,
}) {
  final l10n = AppLocalizations.of(context)!;
  showDialog<void>(
    context: context,
    builder: (dialogContext) {
      return AlertDialog(
        title: Text(title),
        content: Text(body),
        actions: [
          TextButton(
            onPressed: () => Navigator.of(dialogContext).pop(),
            child: Text(l10n.cancel),
          ),
          TextButton(
            onPressed: () {
              Navigator.of(dialogContext).pop();
              final authState = context.read<AuthBloc>().state;
              if (authState is AuthAuthenticated) {
                final bloc = context.read<TripListingBloc>()
                  ..add(eventFactory(authState.userId));
                // Refresh only AFTER the status transition commits — calling
                // onRefresh synchronously races the bloc's write and re-reads
                // the stale status, which never self-corrects offline.
                final refresh = onRefresh;
                if (refresh != null) {
                  bloc.stream
                      .firstWhere(
                        (s) =>
                            s is TripStarted ||
                            s is TripDelivered ||
                            s is TripListingError,
                      )
                      .then((_) => refresh());
                }
              }
            },
            style:
                TextButton.styleFrom(foregroundColor: AppTheme.statusSuccess),
            child: Text(confirmLabel),
          ),
        ],
      );
    },
  );
}

class _RouteDisplay extends StatelessWidget {
  final String from;
  final String to;

  const _RouteDisplay({required this.from, required this.to});

  @override
  Widget build(BuildContext context) {
    return Row(
      children: [
        Column(
          children: [
            Container(
              width: 10,
              height: 10,
              decoration: const BoxDecoration(
                color: AppTheme.statusSuccess,
                shape: BoxShape.circle,
              ),
            ),
            Container(
              width: 2,
              height: 20,
              color: AppTheme.borderLight,
            ),
            Container(
              width: 10,
              height: 10,
              decoration: const BoxDecoration(
                color: AppTheme.statusError,
                shape: BoxShape.circle,
              ),
            ),
          ],
        ),
        const SizedBox(width: 8),
        Expanded(
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Text(
                from,
                style: const TextStyle(
                  fontWeight: FontWeight.w600,
                  color: AppTheme.textDark,
                ),
                overflow: TextOverflow.ellipsis,
              ),
              const SizedBox(height: 10),
              Text(
                to,
                style: const TextStyle(
                  fontWeight: FontWeight.w600,
                  color: AppTheme.textDark,
                ),
                overflow: TextOverflow.ellipsis,
              ),
            ],
          ),
        ),
      ],
    );
  }
}

/// Chips for recent routes (shown in IDLE and DELIVERED states).
class _RecentRouteChips extends StatelessWidget {
  final List<({String from, String to})> routes;
  final String userId;
  final VoidCallback? onRefresh;

  const _RecentRouteChips({
    required this.routes,
    required this.userId,
    this.onRefresh,
  });

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;

    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Text(
          l10n.recentRoutes,
          style: const TextStyle(
            fontSize: 12,
            fontWeight: FontWeight.w600,
            color: AppTheme.textMuted,
          ),
        ),
        const SizedBox(height: 8),
        Wrap(
          spacing: 8,
          runSpacing: 8,
          children: routes.map((route) {
            return ActionChip(
              label: Text('${route.from} → ${route.to}'),
              materialTapTargetSize: MaterialTapTargetSize.padded,
              onPressed: () async {
                await Navigator.of(context).push<void>(
                  MaterialPageRoute(
                    builder: (_) => CreateTripScreen(
                      fromWoreda: route.from,
                      toWoreda: route.to,
                    ),
                  ),
                );
                onRefresh?.call();
              },
            );
          }).toList(),
        ),
      ],
    );
  }
}
