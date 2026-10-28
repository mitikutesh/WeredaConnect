import 'package:flutter/material.dart';
import 'package:intl/intl.dart';

import 'package:wereda_connect/core/models/delivery.dart';
import 'package:wereda_connect/core/models/order.dart';
import 'package:wereda_connect/core/models/place.dart';
import 'package:wereda_connect/core/utils/ethiopian_clock.dart';
import 'package:wereda_connect/core/utils/party_name.dart';
import 'package:wereda_connect/features/crop_listing/domain/models/crop_type.dart';
import 'package:wereda_connect/features/delivery_verification/data/delivery_constants.dart';
import 'package:wereda_connect/features/live_trip/domain/live_trip_rules.dart';
import 'package:wereda_connect/features/live_trip/domain/models/delivery_track.dart';
import 'package:wereda_connect/features/live_trip/presentation/live_trip_data.dart';
import 'package:wereda_connect/features/live_trip/presentation/live_trip_text.dart';
import 'package:wereda_connect/features/live_trip/presentation/widgets/live_trip_parts.dart';
import 'package:wereda_connect/features/live_trip/presentation/widgets/track_builder.dart';
import 'package:wereda_connect/features/order/presentation/open_order_detail.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';
import 'package:wereda_connect/shared/widgets/clock_text.dart';
import 'package:wereda_connect/shared/widgets/offline_banner.dart';
import 'package:wereda_connect/shared/widgets/status_chip.dart';
import 'package:wereda_connect/shared/widgets/wc_card.dart';

/// The farmer's order on the road (LT-11, the Farmer · Order on the road
/// board): "Your wheat is on the road", the map from the farmer's own pickup
/// (other stops are grey rings, never named), how current it is, the arrival
/// window, the payment steps, the buyer and the trucker, and the order
/// details. The farmer sees only this order's delivery: the server answers
/// the track to the delivery's parties only, and this screen opens only for
/// the delivery's farmer.
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
    final l10n = AppLocalizations.of(context)!;
    return Scaffold(
      appBar: AppBar(title: Text(l10n.liveTripTitle)),
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
    super.key,
  });

  final Delivery delivery;
  final String currentUserId;
  final List<Place>? places;
  final DateTime Function()? clock;

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
          padding: const EdgeInsets.fromLTRB(16, 16, 16, 32),
          children: _content(context, view),
        ),
      ),
    );
  }

  List<Widget> _content(BuildContext context, TrackView view) {
    final l10n = AppLocalizations.of(context)!;
    final clock = clockFormatOf(context);
    final theme = Theme.of(context);
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

    return [
      ...trackReadNotice(context, view),
      Row(
        children: [
          Expanded(
            child: ClockText(
              title,
              style: theme.textTheme.titleLarge
                  ?.copyWith(fontWeight: FontWeight.w700),
            ),
          ),
          StatusChip(
            label: l10n.farmer,
            kind: StatusKind.neutral,
            icon: Icons.agriculture,
          ),
        ],
      ),
      Text(
        order == null
            ? buyer
            : l10n.liveTripFarmerTo(
                l10n.quantityKg(birrText(order.quantity)),
                buyer,
              ),
        style: theme.textTheme.bodyMedium?.copyWith(color: AppTheme.textMuted),
      ),
      const SizedBox(height: 16),
      if (onRoad) ...[
        LiveTripMap(track: track, now: view.now, places: _places),
        const SizedBox(height: 12),
        ArrivalWindowBlock(
          track: track,
          now: view.now,
          prefix: l10n.liveTripArrives,
        ),
        const SizedBox(height: 8),
        Text(
          l10n.liveTripGreyRingsNote,
          style: theme.textTheme.bodySmall?.copyWith(color: AppTheme.textMuted),
        ),
      ] else if (ended) ...[
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
          alignment: Alignment.centerLeft,
          child: StatusChip(
            label: l10n.liveTripSharingEnded,
            kind: StatusKind.success,
            icon: Icons.location_off_outlined,
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
      const SizedBox(height: 20),
      _PaymentSteps(delivery: delivery, order: order, now: view.now),
      const SizedBox(height: 20),
      WcCard(
        child: Column(
          children: [
            PartyContactTile(
              name: delivery.buyerName,
              roleLabel: l10n.buyer,
              partyId: delivery.buyerId,
              viewerId: widget.currentUserId,
              order: order,
            ),
            const Divider(height: 24),
            PartyContactTile(
              name: delivery.truckerName,
              roleLabel: l10n.trucker,
              partyId: delivery.truckerId,
              viewerId: widget.currentUserId,
              order: order,
            ),
          ],
        ),
      ),
      if (delivery.orderId?.isNotEmpty ?? false) ...[
        const SizedBox(height: 12),
        WcCard(
          key: const Key('farmer_trip_order_link'),
          onTap: () => openOrderDetail(context, delivery.orderId!),
          padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 12),
          child: Row(
            children: [
              const Icon(Icons.receipt_long_outlined, color: AppTheme.primary),
              const SizedBox(width: 12),
              Expanded(
                child: Text(
                  l10n.liveTripOrderDetailsLink,
                  style: const TextStyle(fontWeight: FontWeight.w600),
                ),
              ),
              const Icon(Icons.chevron_right, color: AppTheme.textMuted),
            ],
          ),
        ),
      ],
    ];
  }
}

/// The farmer's money in four steps: the buyer paid into escrow, the farmer
/// handed over (QR scanned), the buyer confirms delivery, the 48 h check
/// window and then the payout.
class _PaymentSteps extends StatelessWidget {
  const _PaymentSteps({
    required this.delivery,
    required this.order,
    required this.now,
  });

  final Delivery delivery;
  final Order? order;
  final DateTime now;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final clock = clockFormatOf(context);
    final theme = Theme.of(context);
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
        when: confirmedAt == null
            ? null
            : ClockLabel.text(clock.dateTime(confirmedAt, date: date)),
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
    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        Row(
          children: [
            Expanded(
              child: Text(
                l10n.liveTripYourPayment,
                style: theme.textTheme.titleMedium
                    ?.copyWith(fontWeight: FontWeight.w700),
              ),
            ),
            if (payout != null)
              Text(
                l10n.etbCurrency(birrText(payout)),
                style: theme.textTheme.titleMedium?.copyWith(
                  fontWeight: FontWeight.w700,
                  color: AppTheme.money,
                ),
              ),
          ],
        ),
        const SizedBox(height: 8),
        for (final step in steps)
          Padding(
            padding: const EdgeInsets.symmetric(vertical: 4),
            child: Row(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Icon(
                  step.done ? Icons.check_circle : Icons.radio_button_unchecked,
                  size: 20,
                  color:
                      step.done ? AppTheme.statusSuccess : AppTheme.textMuted,
                ),
                const SizedBox(width: 10),
                Expanded(
                  child: Text(
                    step.label,
                    style: theme.textTheme.bodyMedium?.copyWith(
                      color: step.done ? AppTheme.textDark : AppTheme.textMuted,
                    ),
                  ),
                ),
                if (step.when != null)
                  ClockText(
                    step.when!,
                    style: theme.textTheme.bodySmall
                        ?.copyWith(color: AppTheme.textMuted),
                  ),
              ],
            ),
          ),
      ],
    );
  }
}
