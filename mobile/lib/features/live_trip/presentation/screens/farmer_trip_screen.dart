import 'package:flutter/material.dart';
import 'package:intl/intl.dart';

import 'package:wereda_connect/core/models/delivery.dart';
import 'package:wereda_connect/core/models/order.dart';
import 'package:wereda_connect/core/models/place.dart';
import 'package:wereda_connect/core/models/user.dart';
import 'package:wereda_connect/core/utils/ethiopia_time.dart';
import 'package:wereda_connect/core/utils/ethiopian_clock.dart';
import 'package:wereda_connect/core/utils/party_name.dart';
import 'package:wereda_connect/features/crop_listing/domain/models/crop_type.dart';
import 'package:wereda_connect/features/delivery_verification/data/delivery_constants.dart';
import 'package:wereda_connect/features/live_trip/domain/live_trip_rules.dart';
import 'package:wereda_connect/features/live_trip/domain/models/delivery_track.dart';
import 'package:wereda_connect/features/live_trip/domain/track_freshness.dart';
import 'package:wereda_connect/features/live_trip/presentation/live_trip_data.dart';
import 'package:wereda_connect/features/live_trip/presentation/live_trip_text.dart';
import 'package:wereda_connect/features/live_trip/presentation/screens/live_trip_screen.dart';
import 'package:wereda_connect/features/live_trip/presentation/widgets/live_trip_parts.dart';
import 'package:wereda_connect/features/live_trip/presentation/widgets/track_builder.dart';
import 'package:wereda_connect/features/order/presentation/open_order_detail.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';
import 'package:wereda_connect/shared/widgets/clock_text.dart';
import 'package:wereda_connect/shared/widgets/offline_banner.dart';
import 'package:wereda_connect/shared/widgets/wc_card.dart';

/// The farmer's order on the road (LT-11, the Farmer · Order on the road
/// board): the Farmer pill, "Your wheat is on the road", the map in a card
/// from the farmer's own pickup (other stops are grey rings, never named)
/// with the last check-in and the arrival window under it, the payment
/// steps, the buyer and the trucker, and the order details. The farmer sees
/// only this order's delivery: the server answers the track to the
/// delivery's parties only, and this screen opens only for the delivery's
/// farmer.
class FarmerTripScreen extends StatelessWidget {
  const FarmerTripScreen({
    required this.delivery,
    required this.currentUserId,
    this.places,
    this.clock,
    super.key,
  });

  final Delivery delivery;
  final String currentUserId;
  final List<Place>? places;
  final DateTime Function()? clock;

  @override
  Widget build(BuildContext context) {
    return Scaffold(
      backgroundColor: AppTheme.chalkBackground,
      body: SafeArea(
        child: Column(
          children: [
            const OfflineBanner(),
            Expanded(
              child: FarmerTripView(
                delivery: delivery,
                currentUserId: currentUserId,
                places: places,
                clock: clock,
                leading: const LiveTripBackButton(),
              ),
            ),
          ],
        ),
      ),
    );
  }
}

/// The body of [FarmerTripScreen], also embedded beside the deliveries list
/// on a wide screen. Only the delivery's farmer gets it.
class FarmerTripView extends StatefulWidget {
  const FarmerTripView({
    required this.delivery,
    required this.currentUserId,
    this.places,
    this.clock,
    this.leading,
    super.key,
  });

  final Delivery delivery;
  final String currentUserId;
  final List<Place>? places;
  final DateTime Function()? clock;

  /// The back button of a pushed screen; null when embedded beside a list.
  final Widget? leading;

  @override
  State<FarmerTripView> createState() => _FarmerTripViewState();
}

class _FarmerTripViewState extends State<FarmerTripView> {
  Order? _order;
  List<Place> _places = const [];

  @override
  void initState() {
    super.initState();
    _load();
  }

  @override
  void didUpdateWidget(FarmerTripView oldWidget) {
    super.didUpdateWidget(oldWidget);
    if (oldWidget.delivery.deliveryId != widget.delivery.deliveryId) {
      _order = null;
      _load();
    }
  }

  void _load() {
    final given = widget.places;
    if (given != null) {
      _places = given;
    } else {
      loadPlaces().then((places) {
        if (mounted) setState(() => _places = places);
      });
    }
    final delivery = widget.delivery;
    loadDeliveryOrder(context, delivery).then((order) {
      if (mounted && widget.delivery.deliveryId == delivery.deliveryId) {
        setState(() => _order = order);
      }
    });
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final delivery = widget.delivery;
    if (liveTripViewerOf(delivery, widget.currentUserId) !=
        LiveTripViewer.farmer) {
      return Center(
        child: Padding(
          padding: const EdgeInsets.all(24),
          child: Text(l10n.unauthorizedAccess, textAlign: TextAlign.center),
        ),
      );
    }
    return TrackBuilder(
      deliveryId: delivery.deliveryId,
      userId: widget.currentUserId,
      clock: widget.clock,
      builder: (context, view) => RefreshIndicator(
        onRefresh: view.refresh,
        child: ListView(
          padding: const EdgeInsets.fromLTRB(20, 0, 20, 32),
          children: _content(context, view),
        ),
      ),
    );
  }

  List<Widget> _content(BuildContext context, TrackView view) {
    final l10n = AppLocalizations.of(context)!;
    final clock = clockFormatOf(context);
    final delivery = widget.delivery;
    final order = _order;
    final track = view.track;
    final ended = track?.sharing == TrackSharing.ended ||
        delivery.status == DeliveryStatus.delivered ||
        delivery.status == DeliveryStatus.disputed;
    final onRoad = !ended && track != null && trackFollowsTruck(track);
    final crop = order == null ? null : CropType.labelFor(order.cropType, l10n);
    final confirmedAt = delivery.confirmedAt;
    final title = ended
        ? (confirmedAt != null && delivery.status == DeliveryStatus.delivered
            ? clock.deadline(confirmedAt, sentence: l10n.liveTripDeliveredAt)
            : ClockLabel.text(l10n.liveTripEnded))
        : ClockLabel.text(
            crop == null
                ? l10n.liveTripFarmerTitleNoCrop
                : l10n.liveTripFarmerTitle(crop),
          );
    final buyer = partyDisplayName(delivery.buyerName, l10n);
    final eta = track?.eta;
    final expectedToday = onRoad &&
        eta != null &&
        DateUtils.isSameDay(dayInEthiopia(eta.from), dayInEthiopia(view.now));

    return [
      LiveTripTopRow(
        leading: widget.leading,
        trailing: DotPill(
          label: l10n.farmer,
          background: AppTheme.fieldGreenTint,
          foreground: AppTheme.fieldGreen,
          icon: Icons.eco_outlined,
          height: 32,
          radius: 999,
          fontSize: 13,
        ),
      ),
      ...trackReadNotice(context, view),
      ClockText(
        title,
        style: const TextStyle(
          fontSize: 24,
          fontWeight: FontWeight.w800,
          letterSpacing: -0.7,
          height: 1.15,
          color: AppTheme.soilDark,
        ),
      ),
      const SizedBox(height: 2),
      Text(
        order == null
            ? buyer
            : l10n.liveTripFarmerTo(
                l10n.quantityKg(birrText(order.quantity)),
                buyer,
              ),
        style: const TextStyle(fontSize: 13, color: AppTheme.mutedSteel),
      ),
      const SizedBox(height: 12),
      if (onRoad)
        _MapCard(track: track, now: view.now, places: _places)
      else if (ended) ...[
        WcCard(
          child: RouteRibbon(
            stops: ribbonStopsOf(
              context,
              delivery: delivery,
              track: track,
              places: _places,
              dropoffLabel: l10n.liveTripDropoff,
            ),
            reached: 1,
            ended: true,
          ),
        ),
        const SizedBox(height: 8),
        Align(
          alignment: AlignmentDirectional.centerStart,
          child: DotPill(
            label: l10n.liveTripSharingEnded,
            background: AppTheme.dropoffBlueTint,
            foreground: AppTheme.dropoffBlue,
            icon: Icons.check,
            height: 32,
            radius: 999,
          ),
        ),
      ] else
        WcCard(
          color: AppTheme.statusNeutralContainer,
          child: Row(
            children: [
              const Icon(Icons.map_outlined, color: AppTheme.textMuted),
              const SizedBox(width: 12),
              Expanded(child: Text(l10n.liveTripMapStartsAtPickup)),
            ],
          ),
        ),
      const SizedBox(height: 12),
      _PaymentSteps(
        delivery: delivery,
        order: order,
        now: view.now,
        expectedToday: expectedToday,
      ),
      const SizedBox(height: 12),
      WcCard(
        padding: const EdgeInsets.symmetric(horizontal: 16),
        child: Column(
          children: [
            _ContactRow(
              PartyContactTile(
                name: delivery.buyerName,
                roleLabel: l10n.buyer,
                partyId: delivery.buyerId,
                viewerId: widget.currentUserId,
                order: order,
                role: UserRole.buyer,
                avatarSize: 38,
              ),
              divider: true,
            ),
            _ContactRow(
              PartyContactTile(
                name: delivery.truckerName,
                roleLabel: l10n.trucker,
                partyId: delivery.truckerId,
                viewerId: widget.currentUserId,
                order: order,
                role: UserRole.trucker,
                avatarSize: 38,
              ),
            ),
          ],
        ),
      ),
      if (delivery.orderId?.isNotEmpty ?? false) ...[
        const SizedBox(height: 12),
        Material(
          key: const Key('farmer_trip_order_link'),
          color: AppTheme.surfaceWhite,
          shape: RoundedRectangleBorder(
            borderRadius: BorderRadius.circular(16),
            side: const BorderSide(color: AppTheme.whisperBorder),
          ),
          clipBehavior: Clip.antiAlias,
          child: InkWell(
            onTap: () => openOrderDetail(context, delivery.orderId!),
            child: Container(
              height: 52,
              padding: const EdgeInsets.symmetric(horizontal: 16),
              child: Row(
                children: [
                  Expanded(
                    child: Text(
                      l10n.liveTripOrderDetailsLink,
                      maxLines: 1,
                      overflow: TextOverflow.ellipsis,
                      style: const TextStyle(
                        fontSize: 14,
                        fontWeight: FontWeight.w700,
                        color: AppTheme.soilDark,
                      ),
                    ),
                  ),
                  const Icon(
                    Icons.chevron_right,
                    size: 20,
                    color: AppTheme.soilDark,
                  ),
                ],
              ),
            ),
          ),
        ),
      ],
    ];
  }
}

/// One party row of the contact card: 60 tall, a hairline under it when
/// another row follows.
class _ContactRow extends StatelessWidget {
  const _ContactRow(this.tile, {this.divider = false});

  final Widget tile;
  final bool divider;

  @override
  Widget build(BuildContext context) {
    return Container(
      constraints: const BoxConstraints(minHeight: 60),
      padding: const EdgeInsets.symmetric(vertical: 6),
      decoration: divider
          ? const BoxDecoration(
              border: Border(bottom: BorderSide(color: AppTheme.segmentTrack)),
            )
          : null,
      child: tile,
    );
  }
}

/// The map in a card with the last check-in and the arrival window under
/// it, and the grey rings note (the board's "Where the truck is").
class _MapCard extends StatelessWidget {
  const _MapCard({
    required this.track,
    required this.now,
    required this.places,
  });

  final DeliveryTrack track;
  final DateTime now;
  final List<Place> places;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final band = trackFreshness(track, now);
    final rhythm =
        band == TrackFreshness.fresh || band == TrackFreshness.normal;
    final Widget chip;
    if (band != null && rhythm) {
      // "11:42 · Bishoftu · 28 min ago" with a dot, as on the board.
      final text = checkInChipText(context, track, now, places, band);
      chip = DotPill(
        label: text.subtitle == null
            ? text.title
            : '${text.title} · ${text.subtitle}',
        border: AppTheme.whisperBorder,
        dotColor: band == TrackFreshness.fresh
            ? AppTheme.fieldGreen
            : AppTheme.mutedSteel,
      );
    } else {
      chip = CheckInChip(track: track, now: now, places: places, compact: true);
    }
    return Container(
      clipBehavior: Clip.antiAlias,
      decoration: BoxDecoration(
        color: AppTheme.surfaceWhite,
        borderRadius: BorderRadius.circular(18),
        border: Border.all(color: AppTheme.whisperBorder),
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          LiveTripMap(
            track: track,
            now: now,
            places: places,
            height: 200,
            borderRadius: 0,
            showChip: false,
          ),
          Padding(
            padding: const EdgeInsets.fromLTRB(14, 12, 14, 0),
            // The window moves under the chip when both do not fit a line.
            child: Wrap(
              alignment: WrapAlignment.spaceBetween,
              crossAxisAlignment: WrapCrossAlignment.center,
              spacing: 8,
              runSpacing: 6,
              children: [
                chip,
                ArrivalWindowBlock(
                  track: track,
                  now: now,
                  prefix: l10n.liveTripArrives,
                  compact: true,
                ),
              ],
            ),
          ),
          Padding(
            padding: const EdgeInsets.fromLTRB(14, 8, 14, 12),
            child: Text(
              l10n.liveTripGreyRingsNote,
              style: const TextStyle(
                fontSize: 12,
                height: 1.4,
                color: AppTheme.mutedSteel,
              ),
            ),
          ),
        ],
      ),
    );
  }
}

/// The farmer's money in four steps (the board's "Your payment" card): the
/// buyer paid into escrow, the farmer handed over (QR scanned), the buyer
/// confirms delivery, the 48 h check window and then the payout.
class _PaymentSteps extends StatelessWidget {
  const _PaymentSteps({
    required this.delivery,
    required this.order,
    required this.now,
    this.expectedToday = false,
  });

  final Delivery delivery;
  final Order? order;
  final DateTime now;

  /// The truck arrives today: said beside "Buyer confirms delivery".
  final bool expectedToday;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final clock = clockFormatOf(context);
    final o = order;
    final payout = o == null ? null : (o.cropCost ?? o.totalPrice);
    final pickedUpAt = delivery.pickedUpAt;
    final confirmedAt = delivery.confirmedAt;
    final delivered = delivery.status == DeliveryStatus.delivered ||
        delivery.status == DeliveryStatus.disputed;
    final settled = o?.status == OrderStatus.settled;
    final windowEnd = confirmedAt?.add(DeliveryConstants.disputeWindow);
    final date = DateFormat.MMMd();
    final steps = <({String label, ClockLabel? when, bool done})>[
      (
        label: l10n.liveTripPayStepPaid,
        when: o?.paymentVerifiedAt == null
            ? null
            : ClockLabel.text(
                clock.dateTime(o!.paymentVerifiedAt!, date: date),
              ),
        done: true,
      ),
      (
        label: l10n.liveTripPayStepHandedOver,
        when: pickedUpAt == null
            ? null
            : ClockLabel.text(clock.dateTime(pickedUpAt, date: date)),
        done: pickedUpAt != null,
      ),
      (
        label: l10n.liveTripPayStepBuyerConfirms,
        when: confirmedAt != null
            ? ClockLabel.text(clock.dateTime(confirmedAt, date: date))
            : expectedToday && pickedUpAt != null
                ? ClockLabel.text(l10n.liveTripExpectedToday)
                : null,
        done: delivered,
      ),
      (
        label: l10n.liveTripPayStepPaidOut,
        when: windowEnd == null || settled
            ? null
            : clock.deadline(windowEnd, date: date),
        done: settled,
      ),
    ];
    final current = steps.indexWhere((s) => !s.done);
    Widget dot(Color? fill) => Container(
          width: 10,
          height: 10,
          decoration: BoxDecoration(
            shape: BoxShape.circle,
            color: fill,
            border: fill == null
                ? Border.all(color: const Color(0xFFA7ADA9), width: 2)
                : null,
          ),
        );
    return WcCard(
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Row(
            crossAxisAlignment: CrossAxisAlignment.end,
            children: [
              Expanded(
                child: Text(
                  l10n.liveTripYourPayment,
                  style: const TextStyle(
                    fontSize: 15,
                    fontWeight: FontWeight.w800,
                    color: AppTheme.soilDark,
                  ),
                ),
              ),
              if (payout != null)
                Text(
                  l10n.etbCurrency(birrText(payout)),
                  style: const TextStyle(
                    fontSize: 20,
                    fontWeight: FontWeight.w800,
                    color: AppTheme.soilDark,
                  ),
                ),
            ],
          ),
          const SizedBox(height: 10),
          for (final (i, step) in steps.indexed)
            Padding(
              padding: const EdgeInsets.symmetric(vertical: 4.5),
              child: Row(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Padding(
                    padding: const EdgeInsets.only(top: 4),
                    child: dot(
                      step.done
                          ? AppTheme.fieldGreen
                          : i == current
                              ? AppTheme.accentSoft
                              : null,
                    ),
                  ),
                  const SizedBox(width: 10),
                  // The label gets the larger share, the time wraps in its
                  // own when the clock words are long (am, om).
                  Expanded(
                    flex: 5,
                    child: Text(
                      step.label,
                      style: TextStyle(
                        fontSize: 13,
                        color: step.done || i == current
                            ? AppTheme.soilDark
                            : AppTheme.unselectedNav,
                        fontWeight:
                            i == current ? FontWeight.w700 : FontWeight.w500,
                      ),
                    ),
                  ),
                  if (step.when != null) ...[
                    const SizedBox(width: 8),
                    Flexible(
                      flex: 3,
                      child: ClockText(
                        step.when!,
                        textAlign: TextAlign.end,
                        style: TextStyle(
                          fontSize: i == current ? 13 : 12,
                          fontWeight:
                              i == current ? FontWeight.w700 : FontWeight.w500,
                          color: i == current
                              ? AppTheme.soilDark
                              : AppTheme.mutedSteel,
                        ),
                      ),
                    ),
                  ],
                ],
              ),
            ),
        ],
      ),
    );
  }
}
