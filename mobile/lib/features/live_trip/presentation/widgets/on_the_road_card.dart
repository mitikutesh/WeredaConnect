import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';

import 'package:wereda_connect/core/models/delivery.dart';
import 'package:wereda_connect/core/models/order.dart';
import 'package:wereda_connect/core/models/place.dart';
import 'package:wereda_connect/core/utils/ethiopian_clock.dart';
import 'package:wereda_connect/core/utils/party_name.dart';
import 'package:wereda_connect/features/crop_listing/domain/models/crop_type.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/bloc/delivery_bloc.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/widgets/delivery_card.dart';
import 'package:wereda_connect/features/live_trip/domain/live_trip_rules.dart';
import 'package:wereda_connect/features/live_trip/domain/models/delivery_track.dart';
import 'package:wereda_connect/features/live_trip/domain/track_freshness.dart';
import 'package:wereda_connect/features/live_trip/presentation/live_trip_data.dart';
import 'package:wereda_connect/features/live_trip/presentation/live_trip_text.dart';
import 'package:wereda_connect/features/live_trip/presentation/open_live_trip.dart';
import 'package:wereda_connect/features/live_trip/presentation/widgets/live_trip_parts.dart';
import 'package:wereda_connect/features/live_trip/presentation/widgets/track_builder.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';
import 'package:wereda_connect/shared/widgets/clock_text.dart';
import 'package:wereda_connect/shared/widgets/fieldway.dart';
import 'package:wereda_connect/shared/widgets/status_chip.dart';

/// A delivery on the road as a card (LT-11): for the buyer the "On the road
/// now" card of the deliveries tab (crop, farmer and driver, the route
/// ribbon, how current the check-in is, the arrival window); for the farmer
/// "Your wheat is on the road" (the Farmer home board: the ribbon, then the
/// buyer with the arrival window and the age of the last check-in). Never
/// for the trucker, and never on trip rows, browse screens or crop listings:
/// live location stays behind the delivery's track.
///
/// [compact] is the web deliveries board's list card beside the live trip
/// pane: the title and the parties with the arrival window and "arriving"
/// on the right, a thin bar for the share of the way driven, then the
/// check-in chip; no crop tile and no route ribbon.
class OnTheRoadCard extends StatefulWidget {
  const OnTheRoadCard({
    required this.delivery,
    required this.currentUserId,
    this.onTap,
    this.selected = false,
    this.compact = false,
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

  /// The web board's list card (see the class note).
  final bool compact;
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
    // The row carries its crop and kg (delivery-verification-api 1.21.0);
    // older rows take them from the order read.
    final cropKey = delivery.cropType ?? order?.cropType;
    final crop = cropKey == null || cropKey.isEmpty
        ? null
        : CropType.labelFor(cropKey, l10n);
    final quantity = delivery.quantity ?? order?.quantity;
    final eta = track?.eta;
    final band = track == null ? null : trackFreshness(track, view.now);
    final farmer = viewer == LiveTripViewer.farmer;
    final farmerName = partyDisplayName(delivery.farmerName, l10n);
    final pickupPlace = delivery.pickupPlace;

    final title = farmer
        ? (crop == null
            ? l10n.liveTripFarmerTitleNoCrop
            : l10n.liveTripFarmerTitle(crop))
        : crop == null
            ? l10n.liveTripFrom(farmerName)
            : quantity == null
                ? crop
                : '$crop · ${l10n.quantityKg(birrText(quantity))}';
    // "From Almaz Bekele, Meki · Dawit Tesfaye" (the deliveries board).
    final subtitle = farmer
        ? partyDisplayName(delivery.buyerName, l10n)
        : '${l10n.liveTripFrom(pickupPlace == null || pickupPlace.isEmpty ? farmerName : '$farmerName, $pickupPlace')}'
            ' · ${partyDisplayName(delivery.truckerName, l10n)}';
    // The buyer's card carries no status pill (the board); a late truck
    // still says so.
    final showStatus = farmer || band == TrackFreshness.late;

    final shell = RoundedRectangleBorder(
      borderRadius: BorderRadius.circular(18),
      side: BorderSide(
        color: widget.selected ? AppTheme.fieldGreen : AppTheme.whisperBorder,
        width: widget.selected ? 2.0 : 1.0,
      ),
    );
    final onTap = widget.onTap ??
        () => openLiveTrip(context, delivery, widget.currentUserId);

    if (widget.compact) {
      return Material(
        color: AppTheme.surfaceWhite,
        shape: shell,
        child: InkWell(
          key: Key('on_the_road_${delivery.deliveryId}'),
          borderRadius: BorderRadius.circular(18),
          onTap: onTap,
          child: Padding(
            padding: const EdgeInsets.all(14),
            child: _compactBody(
              l10n,
              clock,
              title: title,
              subtitle: subtitle,
              eta: eta,
              track: track,
              now: view.now,
            ),
          ),
        ),
      );
    }

    return Material(
      color: AppTheme.surfaceWhite,
      shape: shell,
      child: InkWell(
        key: Key('on_the_road_${delivery.deliveryId}'),
        borderRadius: BorderRadius.circular(18),
        onTap: onTap,
        child: Padding(
          padding: const EdgeInsets.all(16),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              Row(
                children: [
                  if (!farmer) ...[
                    DeliveryCropTile(cropKey: cropKey, status: delivery.status),
                    const SizedBox(width: 12),
                  ],
                  Expanded(
                    child: Column(
                      crossAxisAlignment: CrossAxisAlignment.start,
                      children: [
                        Text(
                          title,
                          maxLines: 1,
                          overflow: TextOverflow.ellipsis,
                          style: farmer
                              ? const TextStyle(
                                  fontSize: 15,
                                  fontWeight: FontWeight.w800,
                                  letterSpacing: -0.2,
                                  color: AppTheme.soilDark,
                                )
                              : FwText.cardTitle,
                        ),
                        if (!farmer) ...[
                          const SizedBox(height: 3),
                          Text(
                            subtitle,
                            maxLines: 1,
                            overflow: TextOverflow.ellipsis,
                            style: FwText.body,
                          ),
                        ],
                      ],
                    ),
                  ),
                  if (showStatus) ...[
                    const SizedBox(width: 8),
                    StatusChip(
                      label: l10n.liveTripStatusOnTheRoad,
                      kind: band == TrackFreshness.late
                          ? StatusKind.warning
                          : StatusKind.transit,
                    ),
                  ],
                ],
              ),
              const SizedBox(height: 14),
              RouteRibbon(
                stops: ribbonStopsOf(
                  context,
                  delivery: delivery,
                  track: track,
                  places: _places,
                  // The farmer's goods go to the buyer; the buyer reads "You".
                  dropoffLabel:
                      farmer ? l10n.liveTripDropoff : l10n.liveTripYou,
                ),
                reached: 1,
              ),
              const SizedBox(height: 12),
              if (farmer)
                // "Selam Traders · Arrives 13:20-14:00", the check-in age right.
                Row(
                  children: [
                    Expanded(
                      child: ClockText(
                        eta == null
                            ? ClockLabel.text(subtitle)
                            : _sentence(
                                clock.window(eta.from, eta.to),
                                l10n.liveTripArrives,
                              ).prefixed('$subtitle · '),
                        maxLines: 2,
                        style: const TextStyle(
                          fontSize: 12,
                          fontWeight: FontWeight.w500,
                          color: AppTheme.mutedSteel,
                        ),
                      ),
                    ),
                    if (track != null) ...[
                      const SizedBox(width: 8),
                      Flexible(
                        child: CheckInChip(
                          track: track,
                          now: view.now,
                          places: _places,
                          compact: true,
                        ),
                      ),
                    ],
                  ],
                )
              else
                Row(
                  children: [
                    // Two thirds for the freshness chip, so "Checked in 28
                    // min ago" stays whole in the web list's 320 px card.
                    Expanded(
                      flex: 2,
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
                          clock.window(eta.from, eta.to),
                          textAlign: TextAlign.end,
                          style: theme.textTheme.titleSmall?.copyWith(
                            fontWeight: FontWeight.w700,
                            color: AppTheme.primaryDeep,
                          ),
                        ),
                      ),
                    ],
                    const SizedBox(width: 4),
                    const Icon(
                      Icons.chevron_right,
                      size: 20,
                      color: AppTheme.mutedSteel,
                    ),
                  ],
                ),
            ],
          ),
        ),
      ),
    );
  }

  /// The web board's list card: "Wheat · 2,000 kg" over the parties, the
  /// window with "arriving" under it on the right, the bar, the chip.
  Widget _compactBody(
    AppLocalizations l10n,
    ClockFormat clock, {
    required String title,
    required String subtitle,
    required TrackEta? eta,
    required DeliveryTrack? track,
    required DateTime now,
  }) {
    final share = track == null ? null : tripProgressShare(track);
    return Column(
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
                    title,
                    maxLines: 1,
                    overflow: TextOverflow.ellipsis,
                    style: FwText.cardTitle.copyWith(
                      fontSize: 15,
                      fontWeight: FontWeight.w700,
                    ),
                  ),
                  const SizedBox(height: 2),
                  Text(
                    subtitle,
                    maxLines: 1,
                    overflow: TextOverflow.ellipsis,
                    style: FwText.body.copyWith(fontSize: 12),
                  ),
                ],
              ),
            ),
            if (eta != null) ...[
              const SizedBox(width: 8),
              Column(
                crossAxisAlignment: CrossAxisAlignment.end,
                children: [
                  ClockText(
                    clock.window(eta.from, eta.to),
                    textAlign: TextAlign.end,
                    style: const TextStyle(
                      fontSize: 14,
                      fontWeight: FontWeight.w800,
                      color: AppTheme.soilDark,
                    ),
                  ),
                  Text(
                    l10n.delivArrivingCaption,
                    style: FwText.body.copyWith(fontSize: 11),
                  ),
                ],
              ),
            ],
          ],
        ),
        if (share != null) ...[
          const SizedBox(height: 10),
          TripProgressBar(
            key: Key('trip_progress_${widget.delivery.deliveryId}'),
            share: share,
          ),
        ],
        if (track != null) ...[
          const SizedBox(height: 10),
          Align(
            alignment: AlignmentDirectional.centerStart,
            child: CheckInChip(
              track: track,
              now: now,
              places: _places,
              compact: true,
            ),
          ),
        ],
      ],
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

/// The thin bar of the web deliveries board's list card: [share] of the way
/// driven in field green on the segment track, 6 px, pill ends.
class TripProgressBar extends StatelessWidget {
  const TripProgressBar({required this.share, super.key});

  /// 0 at pickup to 1 at the drop-off.
  final double share;

  @override
  Widget build(BuildContext context) {
    return ClipRRect(
      borderRadius: BorderRadius.circular(999),
      child: SizedBox(
        height: 6,
        child: LinearProgressIndicator(
          value: share.clamp(0.0, 1.0),
          minHeight: 6,
          backgroundColor: AppTheme.segmentTrack,
          valueColor: const AlwaysStoppedAnimation<Color>(AppTheme.fieldGreen),
        ),
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
