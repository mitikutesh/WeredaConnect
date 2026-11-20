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

/// A delivery on the road as a card (LT-11, design canvas page M): for the
/// buyer the raised "On the road now" card of the deliveries tab (the map
/// with how current the check-in is and "Open map", then the crop, the
/// farmer and the driver, and the arrival window); for the farmer "Your
/// wheat is on the road" on sand (the Farmer home board: the ribbon, then
/// the buyer with the arrival window and the age of the last check-in). Never
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
    // "Almaz Bekele, Meki · Dawit Tesfaye" (the deliveries board).
    final subtitle = farmer
        ? partyDisplayName(delivery.buyerName, l10n)
        : '${pickupPlace == null || pickupPlace.isEmpty ? farmerName : '$farmerName, $pickupPlace'}'
            ' · ${partyDisplayName(delivery.truckerName, l10n)}';
    final onTap = widget.onTap ??
        () => openLiveTrip(context, delivery, widget.currentUserId);

    if (widget.compact) {
      return Material(
        color: AppTheme.surfaceWhite,
        shape: RoundedRectangleBorder(
          borderRadius: BorderRadius.circular(18),
          side: BorderSide(
            color: widget.selected ? context.roleColors.primary : AppTheme.line,
            width: widget.selected ? 2.0 : 1.0,
          ),
        ),
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

    Widget titleLine(double size) => Row(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Padding(
              padding: EdgeInsets.only(top: size >= 19 ? 3 : 1),
              child: const FwSprout(size: 20, color: AppTheme.ink),
            ),
            const SizedBox(width: 6),
            Expanded(
              child: Text(
                title,
                maxLines: 2,
                overflow: TextOverflow.ellipsis,
                style: TextStyle(
                  fontSize: size,
                  fontWeight: FontWeight.w800,
                  letterSpacing: size >= 19 ? -0.4 : -0.15,
                  height: 1.25,
                  color: AppTheme.ink,
                ),
              ),
            ),
          ],
        );

    if (farmer) {
      // Farmer home board: a flat sand card with the status pill in the
      // farmer's tint, the route ribbon, then the buyer with the arrival
      // window in the role colour and the age of the last check-in.
      final age = track == null ? null : trackFixAge(track, view.now);
      return FwCard.sand(
        key: Key('on_the_road_${delivery.deliveryId}'),
        onTap: onTap,
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            Row(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Expanded(child: titleLine(15)),
                const SizedBox(width: 12),
                band == TrackFreshness.late
                    ? FwPill(
                        label: l10n.liveTripStatusOnTheRoad,
                        background: AppTheme.orangeTint,
                        foreground: AppTheme.orangeInk,
                      )
                    : FwPill(label: l10n.liveTripStatusOnTheRoad),
              ],
            ),
            const SizedBox(height: 10),
            RouteRibbon(
              stops: ribbonStopsOf(
                context,
                delivery: delivery,
                track: track,
                places: _places,
                dropoffLabel: l10n.liveTripDropoff,
              ),
              reached: 1,
            ),
            const SizedBox(height: 10),
            Row(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Expanded(
                  child: Text.rich(
                    _arrivesSpan(context, subtitle, eta),
                    style: const TextStyle(
                      fontSize: 13,
                      height: 1.35,
                      color: AppTheme.inkSoft,
                    ),
                  ),
                ),
                if (age != null && band != null) ...[
                  const SizedBox(width: 12),
                  Text(
                    band == TrackFreshness.late
                        ? l10n.liveTripNoCheckInFor(liveTripDuration(l10n, age))
                        : checkInAge(l10n, age),
                    style: TextStyle(
                      fontSize: 13,
                      color: band == TrackFreshness.late
                          ? AppTheme.orangeInk
                          : AppTheme.inkSoft,
                    ),
                  ),
                ],
              ],
            ),
          ],
        ),
      );
    }

    // Buyer deliveries board, "On the road now": the raised card the tab
    // leads with. Its top is the map with the age of the last check-in and
    // "Open map" over it; under it the crop, the farmer and the driver,
    // and "Arriving" over the window in the role colour.
    final following = track != null && trackFollowsTruck(track);
    final largeText = MediaQuery.textScalerOf(context).scale(10) > 13;
    final what = Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        titleLine(19),
        const SizedBox(height: 3),
        Text(
          subtitle,
          maxLines: 2,
          overflow: TextOverflow.ellipsis,
          style: const TextStyle(
            fontSize: 14,
            height: 1.35,
            color: AppTheme.slate,
          ),
        ),
        if (!following && track != null) ...[
          const SizedBox(height: 8),
          CheckInChip(
            track: track,
            now: view.now,
            places: _places,
            compact: true,
          ),
        ],
      ],
    );
    // Amharic and Oromo windows run long (with the small 24-hour range):
    // they wrap in their own column.
    final arrival = eta == null
        ? null
        : Column(
            crossAxisAlignment:
                largeText ? CrossAxisAlignment.start : CrossAxisAlignment.end,
            children: [
              Text(
                l10n.liveTripStatusArriving,
                style: const TextStyle(fontSize: 13, color: AppTheme.slate),
              ),
              const SizedBox(height: 1),
              ClockText(
                clock.window(eta.from, eta.to),
                key: Key('on_the_road_window_${delivery.deliveryId}'),
                textAlign: largeText ? TextAlign.start : TextAlign.end,
                style: TextStyle(
                  fontSize: 17,
                  fontWeight: FontWeight.w800,
                  letterSpacing: -0.2,
                  color: context.roleColors.primary,
                ),
              ),
            ],
          );
    return FwCard(
      key: Key('on_the_road_${delivery.deliveryId}'),
      raised: true,
      padding: EdgeInsets.zero,
      onTap: onTap,
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          if (following)
            LiveTripMap(
              track: track,
              now: view.now,
              places: _places,
              height: 134,
              borderRadius: 0,
              showChip: false,
              overlay: [
                Positioned(
                  left: 12,
                  top: 12,
                  right: 12,
                  child: Row(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    mainAxisAlignment: MainAxisAlignment.spaceBetween,
                    children: [
                      Flexible(child: _mapChip(context, track, view.now)),
                      const SizedBox(width: 8),
                      const _OpenMapPill(),
                    ],
                  ),
                ),
              ],
            ),
          Padding(
            padding: const EdgeInsets.fromLTRB(16, 16, 16, 18),
            // Side by side as on the board; with large text the arrival
            // moves under the crop instead of breaking both mid-word.
            child: largeText
                ? Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      what,
                      if (arrival != null) ...[
                        const SizedBox(height: 10),
                        arrival,
                      ],
                    ],
                  )
                : Row(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Expanded(flex: 3, child: what),
                      if (arrival != null) ...[
                        const SizedBox(width: 12),
                        Flexible(flex: 2, child: arrival),
                      ],
                    ],
                  ),
          ),
        ],
      ),
    );
  }

  /// "Checked in 28 min ago" over the card's map: white with a black dot
  /// in the hourly rhythm, the band's own chip when late, paused or scans
  /// only.
  Widget _mapChip(BuildContext context, DeliveryTrack track, DateTime now) {
    final band = trackFreshness(track, now);
    if (band != TrackFreshness.fresh && band != TrackFreshness.normal) {
      return CheckInChip(
        track: track,
        now: now,
        places: _places,
        compact: true,
      );
    }
    final text = checkInChipText(context, track, now, _places, band!);
    return DotPill(
      label: text.compact,
      border: AppTheme.line,
      dotColor: AppTheme.ink,
      fontSize: 13,
      fontWeight: FontWeight.w700,
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
                      color: AppTheme.ink,
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

  /// "Selam Traders · Arrives 13:20-14:00": the window bold in the role
  /// colour, its small 24-hour range after it on the Ethiopian clock.
  static TextSpan _arrivesSpan(
    BuildContext context,
    String who,
    TrackEta? eta,
  ) {
    if (eta == null) return TextSpan(text: who);
    final l10n = AppLocalizations.of(context)!;
    final label = _sentence(
      clockFormatOf(context).window(eta.from, eta.to),
      l10n.liveTripArrives,
    );
    final time24 = label.time24;
    return TextSpan(
      children: [
        TextSpan(text: '$who · ${label.before}'),
        TextSpan(
          text: label.time,
          style: TextStyle(
            fontWeight: FontWeight.w800,
            color: context.roleColors.primary,
          ),
        ),
        if (time24 != null)
          TextSpan(
            text: ' ($time24)',
            style: const TextStyle(fontSize: 13 * ClockText.smallScale),
          ),
        if (label.after.isNotEmpty) TextSpan(text: label.after),
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

/// "Open map" over the on the road card's map: a black pill with an arrow.
/// The whole card is the button; this only says what a tap does.
class _OpenMapPill extends StatelessWidget {
  const _OpenMapPill();

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    return Container(
      constraints: const BoxConstraints(minHeight: 32),
      padding: const EdgeInsets.fromLTRB(12, 4, 10, 4),
      decoration: const ShapeDecoration(
        color: AppTheme.ink,
        shape: StadiumBorder(),
      ),
      child: Row(
        mainAxisSize: MainAxisSize.min,
        children: [
          Text(
            l10n.delivOpenMap,
            style: const TextStyle(
              fontSize: 13,
              fontWeight: FontWeight.w700,
              color: Colors.white,
            ),
          ),
          const SizedBox(width: 5),
          const Icon(Icons.north_east, size: 15, color: Colors.white),
        ],
      ),
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
          valueColor: AlwaysStoppedAnimation<Color>(context.roleColors.primary),
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
