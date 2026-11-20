import 'package:flutter/material.dart';

import 'package:wereda_connect/core/models/delivery.dart';
import 'package:wereda_connect/core/models/order.dart';
import 'package:wereda_connect/core/models/place.dart';
import 'package:wereda_connect/core/models/user.dart';
import 'package:wereda_connect/core/utils/date_locale.dart';
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
import 'package:wereda_connect/shared/widgets/dashed_border.dart';
import 'package:wereda_connect/shared/widgets/fieldway.dart';
import 'package:wereda_connect/shared/widgets/offline_banner.dart';

/// The farmer's order on the road (LT-11, the Farmer journey board of
/// design canvas page M): the Farmer pill, "Your wheat is on the road", the
/// map in a raised card from the farmer's own pickup, the trail in the
/// farmer's plum (other stops are grey rings, never named) with the last
/// check-in over it and the arrival window under it, the payment steps on
/// sand with "Nothing for you to do now", the buyer and the trucker with
/// Message and Call, and the order details. The farmer sees
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
      backgroundColor: AppTheme.ground,
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

    final place = track?.dropoffNote?.trim() ?? '';
    final to = place.isEmpty ? buyer : '$buyer, $place';
    // What the farmer still has to do: nothing, once the crop is handed
    // over. On the road the buyer must confirm; after that the window runs.
    final windowEnd = confirmedAt?.add(DeliveryConstants.disputeWindow);
    final ClockLabel? nothingToDo;
    if (delivery.status == DeliveryStatus.delivered &&
        windowEnd != null &&
        view.now.isBefore(windowEnd) &&
        order?.status != OrderStatus.settled) {
      nothingToDo = clock.deadline(
        windowEnd,
        date: formatMonthDay,
        sentence: l10n.liveTripFarmerNothingToDoWindow,
      );
    } else if (isOnTheRoad(delivery)) {
      nothingToDo = ClockLabel.text(l10n.liveTripFarmerNothingToDo(buyer));
    } else {
      nothingToDo = null;
    }

    return [
      LiveTripTopRow(
        leading: widget.leading,
        trailing: DotPill(
          label: l10n.farmer,
          background: AppTheme.sand,
          foreground: AppTheme.inkSoft,
          icon: Icons.eco_outlined,
        ),
      ),
      ...trackReadNotice(context, view),
      ClockText(
        title,
        style: const TextStyle(
          fontSize: 28,
          fontWeight: FontWeight.w800,
          letterSpacing: -1,
          height: 1.05,
          color: AppTheme.ink,
        ),
      ),
      const SizedBox(height: 4),
      Row(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          const FwSprout(size: 20, color: AppTheme.slate),
          const SizedBox(width: 5),
          Expanded(
            child: Text(
              order == null
                  ? to
                  : l10n.liveTripFarmerTo(
                      l10n.quantityKg(birrText(order.quantity)),
                      to,
                    ),
              style: const TextStyle(
                fontSize: 14,
                fontWeight: FontWeight.w500,
                color: AppTheme.slate,
              ),
            ),
          ),
        ],
      ),
      const SizedBox(height: 14),
      if (onRoad)
        _MapCard(track: track, now: view.now, places: _places)
      else if (ended) ...[
        FwCard.sand(
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
        const SizedBox(height: 10),
        Align(
          alignment: AlignmentDirectional.centerStart,
          child: DotPill(
            label: l10n.liveTripSharingEnded,
            background: AppTheme.sand,
            foreground: AppTheme.inkSoft,
            icon: Icons.check,
          ),
        ),
      ] else
        DashedBorderBox(
          child: Row(
            children: [
              const Icon(Icons.map_outlined, color: AppTheme.slate),
              const SizedBox(width: 12),
              Expanded(
                child: Text(
                  l10n.liveTripMapStartsAtPickup,
                  style: const TextStyle(
                    fontSize: 14,
                    height: 1.35,
                    color: AppTheme.ink,
                  ),
                ),
              ),
            ],
          ),
        ),
      const SizedBox(height: 14),
      _PaymentSteps(
        delivery: delivery,
        order: order,
        now: view.now,
        expectedToday: expectedToday,
        nothingToDo: nothingToDo,
      ),
      const SizedBox(height: 14),
      FwCard(
        raised: true,
        padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 4),
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
                look: PartyContactLook.boxes,
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
                look: PartyContactLook.boxes,
              ),
            ),
          ],
        ),
      ),
      if (delivery.orderId?.isNotEmpty ?? false) ...[
        const SizedBox(height: 14),
        Material(
          key: const Key('farmer_trip_order_link'),
          color: Colors.transparent,
          shape: const StadiumBorder(
            side: BorderSide(color: AppTheme.fieldBorder),
          ),
          clipBehavior: Clip.antiAlias,
          child: InkWell(
            onTap: () => openOrderDetail(context, delivery.orderId!),
            child: Container(
              constraints: const BoxConstraints(minHeight: 52),
              padding: const EdgeInsets.fromLTRB(20, 6, 18, 6),
              child: Row(
                children: [
                  Expanded(
                    child: Text(
                      l10n.liveTripOrderDetailsLink,
                      maxLines: 2,
                      overflow: TextOverflow.ellipsis,
                      style: const TextStyle(
                        fontSize: 14,
                        fontWeight: FontWeight.w800,
                        color: AppTheme.ink,
                      ),
                    ),
                  ),
                  const Icon(
                    Icons.chevron_right,
                    size: 16,
                    color: AppTheme.ink,
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

/// One party row of the contact card: 66 tall, a hairline under it when
/// another row follows.
class _ContactRow extends StatelessWidget {
  const _ContactRow(this.tile, {this.divider = false});

  final Widget tile;
  final bool divider;

  @override
  Widget build(BuildContext context) {
    return Container(
      constraints: const BoxConstraints(minHeight: 66),
      padding: const EdgeInsets.symmetric(vertical: 8),
      alignment: Alignment.center,
      decoration: divider
          ? const BoxDecoration(
              border: Border(bottom: BorderSide(color: AppTheme.sketch)),
            )
          : null,
      child: tile,
    );
  }
}

/// The map in a raised card (the board's "Where the truck is"): the last
/// check-in in a white pill over it ("11:42 · Bishoftu · 28 min ago"),
/// under it the grey rings note with the arrival window in the farmer's
/// colour at the end.
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
      final text = checkInChipText(context, track, now, places, band);
      chip = DotPill(
        label: text.subtitle == null
            ? text.title
            : '${text.title} · ${text.subtitle}',
        dotColor: AppTheme.ink,
        fontSize: 13,
        fontWeight: FontWeight.w700,
      );
    } else {
      chip = CheckInChip(track: track, now: now, places: places, compact: true);
    }
    return FwCard(
      raised: true,
      padding: EdgeInsets.zero,
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          LiveTripMap(
            track: track,
            now: now,
            places: places,
            height: 160,
            borderRadius: 0,
            showChip: false,
            overlay: [
              Positioned(
                left: 12,
                top: 12,
                right: 12,
                child: Align(
                  alignment: AlignmentDirectional.topStart,
                  child: chip,
                ),
              ),
            ],
          ),
          Padding(
            padding: const EdgeInsets.fromLTRB(16, 12, 16, 14),
            child: Row(
              children: [
                Expanded(
                  child: Text(
                    l10n.liveTripGreyRingsNote,
                    style: const TextStyle(
                      fontSize: 13,
                      height: 1.35,
                      color: AppTheme.slate,
                    ),
                  ),
                ),
                const SizedBox(width: 12),
                // The window wraps in its own share when the clock words
                // are long (am, om).
                Flexible(
                  child: ArrivalWindowBlock(
                    track: track,
                    now: now,
                    prefix: l10n.liveTripArrives,
                    compact: true,
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

/// The farmer's money in four steps (the board's "Your payment" card): the
/// buyer paid into escrow, the farmer handed over (QR scanned), the buyer
/// confirms delivery, the 48 h check window and then the payout.
class _PaymentSteps extends StatelessWidget {
  const _PaymentSteps({
    required this.delivery,
    required this.order,
    required this.now,
    this.expectedToday = false,
    this.nothingToDo,
  });

  final Delivery delivery;
  final Order? order;
  final DateTime now;

  /// The truck arrives today: said beside "Buyer confirms delivery".
  final bool expectedToday;

  /// "Nothing for you to do now. ..." under the steps, when that is so.
  final ClockLabel? nothingToDo;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final clock = clockFormatOf(context);
    final o = order;
    // The farmer's own share: the crop cost, else quantity x unit price;
    // never the order's total, which also holds transport and commission.
    final share =
        o == null ? null : (o.cropCost ?? o.quantity * o.pricePerUnit);
    final payout = share != null && share > 0 ? share : null;
    final pickedUpAt = delivery.pickedUpAt;
    final confirmedAt = delivery.confirmedAt;
    final delivered = delivery.status == DeliveryStatus.delivered ||
        delivery.status == DeliveryStatus.disputed;
    final settled = o?.status == OrderStatus.settled;
    final windowEnd = confirmedAt?.add(DeliveryConstants.disputeWindow);
    const date = formatMonthDay;
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
    // The board: done steps a black dot, the current one orange with a
    // black edge, the ones ahead a hollow ring.
    Widget dot({required bool done, required bool current}) => Container(
          width: 10,
          height: 10,
          decoration: BoxDecoration(
            shape: BoxShape.circle,
            color: done
                ? AppTheme.ink
                : current
                    ? AppTheme.orange
                    : null,
            border: done
                ? null
                : Border.all(
                    color: current ? AppTheme.ink : AppTheme.slate,
                    width: 1.5,
                  ),
          ),
        );
    final note = nothingToDo;
    return FwCard.sand(
      padding: const EdgeInsets.fromLTRB(16, 14, 16, 16),
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
                    letterSpacing: -0.15,
                    color: AppTheme.ink,
                  ),
                ),
              ),
              if (payout != null)
                Text(
                  l10n.etbCurrency(birrText(payout)),
                  style: TextStyle(
                    fontSize: 19,
                    fontWeight: FontWeight.w800,
                    letterSpacing: -0.4,
                    color: context.roleColors.primary,
                  ),
                ),
            ],
          ),
          const SizedBox(height: 6),
          for (final (i, step) in steps.indexed)
            Padding(
              padding: const EdgeInsets.symmetric(vertical: 4),
              child: Row(
                children: [
                  dot(done: step.done, current: i == current),
                  const SizedBox(width: 10),
                  // The label gets the larger share, the time wraps in its
                  // own when the clock words are long (am, om).
                  Expanded(
                    flex: 5,
                    child: Text(
                      step.label,
                      style: TextStyle(
                        fontSize: 13,
                        height: 1.3,
                        color: step.done || i == current
                            ? AppTheme.ink
                            : AppTheme.inkSoft,
                        fontWeight:
                            i == current ? FontWeight.w800 : FontWeight.w400,
                      ),
                    ),
                  ),
                  if (step.when != null) ...[
                    const SizedBox(width: 8),
                    Flexible(
                      flex: 4,
                      child: i == current
                          // What the farmer waits for now: the board's
                          // orange pill ("expected today").
                          ? Container(
                              key: const Key('farmer_trip_current_when'),
                              constraints: const BoxConstraints(minHeight: 26),
                              padding: const EdgeInsets.symmetric(
                                horizontal: 10,
                                vertical: 3,
                              ),
                              decoration: BoxDecoration(
                                color: AppTheme.orange,
                                borderRadius: BorderRadius.circular(14),
                                border: Border.all(
                                  color: AppTheme.ink,
                                  width: 1.5,
                                ),
                              ),
                              child: ClockText(
                                step.when!,
                                textAlign: TextAlign.center,
                                style: const TextStyle(
                                  fontSize: 12,
                                  fontWeight: FontWeight.w800,
                                  color: AppTheme.ink,
                                ),
                              ),
                            )
                          : ClockText(
                              step.when!,
                              textAlign: TextAlign.end,
                              style: const TextStyle(
                                fontSize: 13,
                                color: AppTheme.inkSoft,
                              ),
                            ),
                    ),
                  ],
                ],
              ),
            ),
          if (note != null) ...[
            const SizedBox(height: 6),
            Container(
              padding: const EdgeInsets.only(top: 10),
              decoration: const BoxDecoration(
                border: Border(top: BorderSide(color: AppTheme.sketch)),
              ),
              child: ClockText(
                note,
                key: const Key('farmer_trip_nothing_to_do'),
                style: const TextStyle(
                  fontSize: 14,
                  height: 1.4,
                  color: AppTheme.ink,
                ),
              ),
            ),
          ],
        ],
      ),
    );
  }
}
