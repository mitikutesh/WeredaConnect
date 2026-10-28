import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';

import 'package:wereda_connect/core/models/delivery.dart';
import 'package:wereda_connect/core/models/order.dart';
import 'package:wereda_connect/core/models/place.dart';
import 'package:wereda_connect/core/utils/ethiopian_clock.dart';
import 'package:wereda_connect/core/utils/party_name.dart';
import 'package:wereda_connect/features/crop_listing/domain/models/crop_type.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/bloc/delivery_bloc.dart';
import 'package:wereda_connect/features/live_trip/domain/live_trip_rules.dart';
import 'package:wereda_connect/features/live_trip/domain/track_freshness.dart';
import 'package:wereda_connect/features/live_trip/presentation/live_trip_data.dart';
import 'package:wereda_connect/features/live_trip/presentation/live_trip_text.dart';
import 'package:wereda_connect/features/live_trip/presentation/open_live_trip.dart';
import 'package:wereda_connect/features/live_trip/presentation/widgets/live_trip_parts.dart';
import 'package:wereda_connect/features/live_trip/presentation/widgets/track_builder.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';
import 'package:wereda_connect/shared/widgets/clock_text.dart';
import 'package:wereda_connect/shared/widgets/status_chip.dart';

/// A delivery on the road as a card (LT-11): for the buyer the "On the road
/// now" card of the deliveries tab (crop, farmer and driver, the route
/// ribbon, how current the check-in is, the arrival window); for the farmer
/// "Your wheat is on the road" (buyer, arrival window, age). Never for the
/// trucker, and never on trip rows, browse screens or crop listings: live
/// location stays behind the delivery's track.
class OnTheRoadCard extends StatefulWidget {
  const OnTheRoadCard({
    required this.delivery,
    required this.currentUserId,
    this.onTap,
    this.selected = false,
    this.places,
    this.clock,
    super.key,
  });

  final Delivery delivery;
  final String currentUserId;

  /// Defaults to opening the viewer's live trip screen.
  final VoidCallback? onTap;

  /// Highlighted (the delivery shown beside the list on a wide screen).
  final bool selected;
  final List<Place>? places;
  final DateTime Function()? clock;

  @override
  State<OnTheRoadCard> createState() => _OnTheRoadCardState();
}

class _OnTheRoadCardState extends State<OnTheRoadCard> {
  Order? _order;
  List<Place> _places = const [];

  @override
  void initState() {
    super.initState();
    final given = widget.places;
    if (given != null) {
      _places = given;
    } else {
      loadPlaces().then((places) {
        if (mounted) setState(() => _places = places);
      });
    }
    loadDeliveryOrder(context, widget.delivery).then((order) {
      if (mounted) setState(() => _order = order);
    });
  }

  @override
  Widget build(BuildContext context) {
    final viewer = liveTripViewerOf(widget.delivery, widget.currentUserId);
    if (viewer == null) return const SizedBox.shrink();
    return TrackBuilder(
      deliveryId: widget.delivery.deliveryId,
      userId: widget.currentUserId,
      clock: widget.clock,
      builder: (context, view) => _card(context, viewer, view),
    );
  }

  Widget _card(BuildContext context, LiveTripViewer viewer, TrackView view) {
    final l10n = AppLocalizations.of(context)!;
    final clock = clockFormatOf(context);
    final theme = Theme.of(context);
    final delivery = widget.delivery;
    final order = _order;
    final track = view.track;
    final crop = order == null ? null : CropType.labelFor(order.cropType, l10n);
    final eta = track?.eta;
    final band = track == null ? null : trackFreshness(track, view.now);
    final farmer = viewer == LiveTripViewer.farmer;

    final title = farmer
        ? (crop == null
            ? l10n.liveTripFarmerTitleNoCrop
            : l10n.liveTripFarmerTitle(crop))
        : order == null
            ? l10n.liveTripFrom(partyDisplayName(delivery.farmerName, l10n))
            : '$crop · ${l10n.quantityKg(birrText(order.quantity))}';
    final subtitle = farmer
        ? partyDisplayName(delivery.buyerName, l10n)
        : '${l10n.liveTripFrom(partyDisplayName(delivery.farmerName, l10n))}'
            ' · ${partyDisplayName(delivery.truckerName, l10n)}';

    return Material(
      color: widget.selected
          ? AppTheme.primary.withValues(alpha: 0.06)
          : AppTheme.surfaceWhite,
      shape: RoundedRectangleBorder(
        borderRadius: BorderRadius.circular(12),
        side: BorderSide(
          color: widget.selected ? AppTheme.primary : AppTheme.borderLight,
          width: widget.selected ? 1.5 : 1,
        ),
      ),
      child: InkWell(
        key: Key('on_the_road_${delivery.deliveryId}'),
        borderRadius: BorderRadius.circular(12),
        onTap: widget.onTap ??
            () => openLiveTrip(context, delivery, widget.currentUserId),
        child: Padding(
          padding: const EdgeInsets.all(16),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              Row(
                children: [
                  const Icon(Icons.local_shipping, color: AppTheme.primary),
                  const SizedBox(width: 10),
                  Expanded(
                    child: Column(
                      crossAxisAlignment: CrossAxisAlignment.start,
                      children: [
                        Text(
                          title,
                          maxLines: 1,
                          overflow: TextOverflow.ellipsis,
                          style: theme.textTheme.titleSmall
                              ?.copyWith(fontWeight: FontWeight.w700),
                        ),
                        Text(
                          subtitle,
                          maxLines: 1,
                          overflow: TextOverflow.ellipsis,
                          style: theme.textTheme.bodySmall
                              ?.copyWith(color: AppTheme.textMuted),
                        ),
                      ],
                    ),
                  ),
                  StatusChip(
                    label: l10n.liveTripStatusOnTheRoad,
                    kind: band == TrackFreshness.late
                        ? StatusKind.warning
                        : StatusKind.transit,
                  ),
                ],
              ),
              if (!farmer) ...[
                const SizedBox(height: 14),
                RouteRibbon(
                  stops: ribbonStopsOf(
                    context,
                    delivery: delivery,
                    track: track,
                    places: _places,
                    dropoffLabel: l10n.liveTripYou,
                  ),
                  reached: 1,
                ),
              ],
              const SizedBox(height: 12),
              Row(
                children: [
                  Expanded(
                    child: track == null
                        ? const SizedBox.shrink()
                        : CheckInChip(
                            track: track,
                            now: view.now,
                            places: _places,
                            compact: true,
                          ),
                  ),
                  if (eta != null) ...[
                    const SizedBox(width: 8),
                    // Amharic and Oromo windows run long (with the small
                    // 24-hour range): they wrap instead of overflowing.
                    Flexible(
                      child: ClockText(
                        farmer
                            ? _sentence(
                                clock.window(eta.from, eta.to),
                                l10n.liveTripArrives,
                              )
                            : clock.window(eta.from, eta.to),
                        textAlign: TextAlign.end,
                        style: theme.textTheme.titleSmall?.copyWith(
                          fontWeight: FontWeight.w700,
                          color: AppTheme.primaryDeep,
                        ),
                      ),
                    ),
                  ],
                ],
              ),
            ],
          ),
        ),
      ),
    );
  }

  static ClockLabel _sentence(ClockLabel window, String Function(String) put) {
    const slot = '\u{FFFC}';
    final whole = put(slot);
    final at = whole.indexOf(slot);
    if (at < 0) return window;
    return ClockLabel(
      before: whole.substring(0, at),
      time: window.time,
      time24: window.time24,
      after: whole.substring(at + slot.length),
    );
  }
}

/// Under a buyer's delivery that waits for pickup: the map starts there.
class WaitingForPickupNote extends StatelessWidget {
  const WaitingForPickupNote({super.key});

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    return Padding(
      padding: const EdgeInsets.fromLTRB(16, 0, 16, 4),
      child: Row(
        children: [
          const Icon(Icons.map_outlined, size: 16, color: AppTheme.textMuted),
          const SizedBox(width: 6),
          Expanded(
            child: Text(
              l10n.liveTripMapStartsAtPickup,
              style: const TextStyle(fontSize: 12, color: AppTheme.textMuted),
            ),
          ),
        ],
      ),
    );
  }
}

/// The farmer's home card (the Farmer home board): their own deliveries a
/// truck carries now, each as an [OnTheRoadCard]. Nothing when none is on
/// the road, and nothing for any other role. Reads the farmer's own
/// deliveries from the delivery repository (Isar on mobile, `GET
/// /deliveries?farmerId=` on web), not the shared list bloc's state.
class FarmerOnTheRoadSection extends StatefulWidget {
  const FarmerOnTheRoadSection({
    required this.userId,
    this.padding = EdgeInsets.zero,
    super.key,
  });

  final String userId;

  /// Around the cards; left out with them when none is on the road.
  final EdgeInsets padding;

  @override
  State<FarmerOnTheRoadSection> createState() => _FarmerOnTheRoadSectionState();
}

class _FarmerOnTheRoadSectionState extends State<FarmerOnTheRoadSection> {
  List<Delivery> _onTheRoad = const [];

  @override
  void initState() {
    super.initState();
    _load();
  }

  Future<void> _load() async {
    if (widget.userId.isEmpty) return;
    final DeliveryBloc bloc;
    try {
      bloc = context.read<DeliveryBloc>();
    } on ProviderNotFoundException {
      return;
    }
    try {
      final deliveries = await bloc.repository.getDeliveriesByUser(
        widget.userId,
        role: 'farmer',
      );
      if (!mounted) return;
      setState(() {
        _onTheRoad = [
          for (final d in deliveries)
            if (d.farmerId == widget.userId && isOnTheRoad(d)) d,
        ];
      });
    } on Object catch (e) {
      debugPrint('[FarmerOnTheRoad] deliveries read failed: $e');
    }
  }

  @override
  Widget build(BuildContext context) {
    if (_onTheRoad.isEmpty) return const SizedBox.shrink();
    return Padding(
      padding: widget.padding,
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          for (var i = 0; i < _onTheRoad.length; i++) ...[
            if (i > 0) const SizedBox(height: 10),
            OnTheRoadCard(
              delivery: _onTheRoad[i],
              currentUserId: widget.userId,
            ),
          ],
        ],
      ),
    );
  }
}
