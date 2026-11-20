import 'package:flutter/material.dart';
import 'package:intl/intl.dart';

import 'package:wereda_connect/core/models/delivery.dart';
import 'package:wereda_connect/core/utils/board_day.dart';
import 'package:wereda_connect/core/utils/ethiopia_time.dart';
import 'package:wereda_connect/core/utils/party_name.dart';
import 'package:wereda_connect/features/crop_listing/domain/models/crop_type.dart';
import 'package:wereda_connect/features/delivery_verification/data/delivery_constants.dart';
import 'package:wereda_connect/features/live_trip/presentation/widgets/live_trip_parts.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';
import 'package:wereda_connect/shared/widgets/clock_text.dart';
import 'package:wereda_connect/shared/widgets/dashed_border.dart';
import 'package:wereda_connect/shared/widgets/fieldway.dart';

/// A delivery in a list, as the Buyer deliveries board (design canvas page
/// M) draws the waiting and the delivered card: waiting for pickup inside a
/// dashed edge ("Teff · 1,500 kg", the pickup day, who and where, when the
/// map appears), delivered on sand with the orange "Release pending" pill,
/// "Nothing for you to do" and the buyer's report window. The crop line comes from the
/// delivery row itself (delivery-verification-api 1.21.0); a delivery created
/// before that keeps its number as the title.
///
/// The same card serves all three roles: the line under the title names the
/// parties the viewer is not.
///
/// [compact] is the web deliveries board's list card beside the live trip
/// pane: no crop tile, the title with the pill on its right, one line under
/// it ("Waiting for pickup Thu 30 Oct · map starts at pickup", "Delivered
/// Mon · report a problem until Wed 16:10").
class DeliveryCard extends StatelessWidget {
  final Delivery delivery;
  final VoidCallback? onTap;
  final String currentUserId;

  /// Highlighted (the delivery shown beside the list on a wide screen).
  final bool selected;

  /// The web board's list card (see the class note).
  final bool compact;

  const DeliveryCard({
    required this.delivery,
    required this.currentUserId,
    super.key,
    this.onTap,
    this.selected = false,
    this.compact = false,
  });

  /// The buyer's 48 h to report a problem after delivery (Web · Buyer
  /// deliveries board: "report a problem until Wed 16:10"); null outside it.
  static DateTime? reportWindowEnd(Delivery delivery, DateTime now) {
    final confirmedAt = delivery.confirmedAt;
    if (delivery.status != DeliveryStatus.delivered || confirmedAt == null) {
      return null;
    }
    final end = confirmedAt.add(DeliveryConstants.disputeWindow);
    return now.isBefore(end) ? end : null;
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final now = DateTime.now().toUtc();
    final status = delivery.status;
    final isBuyer = currentUserId == delivery.buyerId;
    final isFarmer = currentUserId == delivery.farmerId;
    final selfPickup =
        delivery.deliveryType == DeliveryConstants.selfPickupDeliveryType ||
            delivery.truckerId.isEmpty;
    final windowEnd = reportWindowEnd(delivery, now);

    // "Teff · 1,500 kg", or the delivery number for rows without a crop.
    final cropKey = delivery.cropType;
    final crop = cropKey == null || cropKey.isEmpty
        ? null
        : CropType.labelFor(cropKey, l10n);
    final quantity = delivery.quantity;
    final id = delivery.deliveryId;
    final title = crop == null
        ? l10n.deliveryIdLabel(id.length > 8 ? id.substring(0, 8) : id)
        : quantity == null
            ? crop
            : '$crop · ${l10n.quantityKg(NumberFormat('#,##0.##').format(quantity))}';

    // Who the viewer deals with, and where the crop is picked up. The
    // buyer's waiting card names the farmer alone, as on the board (the
    // driver appears once the truck is on the road).
    final farmer = partyDisplayName(delivery.farmerName, l10n);
    final place = delivery.pickupPlace;
    final farmerLine =
        place == null || place.isEmpty ? farmer : '$farmer, $place';
    final trucker = partyDisplayName(delivery.truckerName, l10n);
    final buyer = partyDisplayName(delivery.buyerName, l10n);
    final withTrucker = !selfPickup && status != DeliveryStatus.pending;
    final parties = isBuyer
        ? (withTrucker ? '$farmerLine · $trucker' : farmerLine)
        : isFarmer
            ? (selfPickup ? buyer : '$buyer · $trucker')
            : '$farmerLine → $buyer';

    // The stage's own words after the parties.
    final planned = delivery.pickupPlannedAt;
    final whenLine = switch (status) {
      DeliveryStatus.pending when planned != null =>
        l10n.delivPickupOn(boardDayInEthiopia(planned)),
      DeliveryStatus.delivered when windowEnd == null => l10n.delivDeliveredOn(
          boardDayInEthiopia(delivery.confirmedAt ?? delivery.updatedAt),
        ),
      _ => null,
    };
    final subtitle = whenLine == null ? parties : '$parties · $whenLine';

    // Inside the 48 h window the money waits: "Release pending" for every
    // party, and what (not) to do about it.
    final releasePending = windowEnd != null;
    final waitingNote =
        isBuyer && !selfPickup && status == DeliveryStatus.pending;

    final statusPill = switch (status) {
      DeliveryStatus.pickedUp || DeliveryStatus.inTransit => FwPill(
          label: deliveryStatusLabel(status, l10n),
        ),
      DeliveryStatus.disputed => FwPill(
          label: deliveryStatusLabel(status, l10n),
          background: AppTheme.statusErrorContainer,
          foreground: AppTheme.onStatusErrorContainer,
        ),
      DeliveryStatus.cancelled => FwPill(
          label: deliveryStatusLabel(status, l10n),
          background: AppTheme.statusNeutralContainer,
          foreground: AppTheme.onStatusNeutralContainer,
        ),
      _ => null,
    };

    if (compact) {
      return _compact(
        context,
        l10n,
        title: title,
        parties: parties,
        subtitle: subtitle,
        selfPickup: selfPickup,
        windowEnd: windowEnd,
        pill: windowEnd != null && (isBuyer || isFarmer)
            ? FwPill(label: l10n.liveTripReleasePending)
            : statusPill,
      );
    }

    // "Teff · 1,500 kg" behind the crop's sprout, what belongs at the end
    // of the line after it (the date, a pill).
    // A wrap: with large text or a long pill (Afaan Oromoo) the trailing
    // part moves under the title instead of squeezing it.
    Widget titleRow(Widget? trailing) => Wrap(
          alignment: WrapAlignment.spaceBetween,
          crossAxisAlignment: WrapCrossAlignment.center,
          spacing: 12,
          runSpacing: 6,
          children: [
            Row(
              mainAxisSize: MainAxisSize.min,
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                const Padding(
                  padding: EdgeInsets.only(top: 1),
                  child: FwSprout(size: 20, color: AppTheme.ink),
                ),
                const SizedBox(width: 6),
                Flexible(
                  child: Text(
                    title,
                    maxLines: 2,
                    overflow: TextOverflow.ellipsis,
                    style: _titleStyle,
                  ),
                ),
              ],
            ),
            if (trailing != null) trailing,
          ],
        );

    switch (status) {
      // Buyer deliveries board, "Waiting for pickup": nothing moves yet, so
      // the card is only a dashed edge.
      case DeliveryStatus.pending:
        return DashedBorderBox(
          key: const Key('delivery_card_waiting'),
          onTap: onTap,
          padding: const EdgeInsets.fromLTRB(16, 14, 16, 16),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              titleRow(
                planned == null
                    ? null
                    : Text(
                        boardDayInEthiopia(planned),
                        style: const TextStyle(
                          fontSize: 13,
                          fontWeight: FontWeight.w700,
                          color: AppTheme.inkSoft,
                        ),
                      ),
              ),
              const SizedBox(height: 4),
              Text(parties, style: _lineStyle),
              if (waitingNote) ...[
                const SizedBox(height: 8),
                Text(
                  l10n.liveTripMapStartsAtPickup,
                  style: _lineStyle.copyWith(color: AppTheme.ink),
                ),
              ],
            ],
          ),
        );
      // "Delivered": a flat sand card. Inside the window it says the money
      // waits and that there is nothing to do.
      case DeliveryStatus.delivered:
        final clock = clockFormatOf(context);
        return FwCard.sand(
          key: const Key('delivery_card_delivered'),
          onTap: onTap,
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              titleRow(
                releasePending
                    ? DotPill.action(label: l10n.liveTripReleasePending)
                    : null,
              ),
              if (windowEnd != null) ...[
                const SizedBox(height: 6),
                ClockText(
                  clock.deadline(
                    windowEnd,
                    date: DateFormat.E().format,
                    sentence: isBuyer
                        ? l10n.delivNothingToDoReleased
                        : l10n.delivNothingToDoPaid,
                  ),
                  key: const Key('delivery_card_nothing_to_do'),
                  style: const TextStyle(
                    fontSize: 14,
                    height: 1.35,
                    fontWeight: FontWeight.w700,
                    color: AppTheme.ink,
                  ),
                ),
              ],
              const SizedBox(height: 6),
              Row(
                crossAxisAlignment: CrossAxisAlignment.end,
                children: [
                  Expanded(
                    child: isBuyer && windowEnd != null
                        ? ClockText(
                            clock.deadline(
                              windowEnd,
                              date: DateFormat.E().format,
                              sentence: l10n.liveTripReportProblemUntil,
                            ),
                            style: _lineStyle.copyWith(
                              color: AppTheme.inkSoft,
                            ),
                          )
                        : Text(
                            subtitle,
                            style: _lineStyle.copyWith(
                              color: AppTheme.inkSoft,
                            ),
                          ),
                  ),
                  if (onTap != null) ...[
                    const SizedBox(width: 12),
                    Row(
                      mainAxisSize: MainAxisSize.min,
                      children: [
                        Text(
                          l10n.delivOpen,
                          style: const TextStyle(
                            fontSize: 13,
                            fontWeight: FontWeight.w700,
                            color: AppTheme.ink,
                          ),
                        ),
                        const SizedBox(width: 2),
                        const Icon(
                          Icons.chevron_right,
                          size: 18,
                          color: AppTheme.ink,
                        ),
                      ],
                    ),
                  ],
                ],
              ),
            ],
          ),
        );
      // On the road without a map (the trucker's own load, a self-pickup),
      // disputed and cancelled: the paper card with the status at the end.
      case DeliveryStatus.pickedUp ||
            DeliveryStatus.inTransit ||
            DeliveryStatus.disputed ||
            DeliveryStatus.cancelled:
        return FwCard(
          onTap: onTap,
          padding: const EdgeInsets.fromLTRB(16, 14, 16, 16),
          borderColor: selected ? context.roleColors.primary : AppTheme.line,
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              titleRow(statusPill),
              const SizedBox(height: 4),
              Text(subtitle, style: _lineStyle),
            ],
          ),
        );
    }
  }

  static const _titleStyle = TextStyle(
    fontSize: 15,
    fontWeight: FontWeight.w800,
    letterSpacing: -0.15,
    height: 1.25,
    color: AppTheme.ink,
  );

  static const _lineStyle = TextStyle(
    fontSize: 14,
    height: 1.35,
    color: AppTheme.slate,
  );

  /// The web board's list card: the title with [pill] on its right, then
  /// one line. A buyer's waiting truck delivery reads "Waiting for pickup
  /// Thu 30 Oct · map starts at pickup"; a delivered one "Delivered Mon",
  /// with "report a problem until Wed 16:10" for the buyer inside the
  /// window; every other state keeps the phone card's [subtitle].
  Widget _compact(
    BuildContext context,
    AppLocalizations l10n, {
    required String title,
    required String parties,
    required String subtitle,
    required bool selfPickup,
    required DateTime? windowEnd,
    required Widget? pill,
  }) {
    final status = delivery.status;
    final isBuyer = currentUserId == delivery.buyerId;
    final style = FwText.body.copyWith(fontSize: 12);
    final Widget line;
    if (isBuyer && !selfPickup && status == DeliveryStatus.pending) {
      final planned = delivery.pickupPlannedAt;
      final waiting = planned == null
          ? l10n.liveTripWaitingForPickup
          : l10n.delivWaitingForPickupOn(boardDayInEthiopia(planned));
      // Two lines here and below: a day with its Ethiopian date (FUP-545)
      // is longer in Amharic and Afaan Oromoo.
      line = Text(
        '$waiting · ${l10n.delivMapStartsAtPickup}',
        maxLines: 2,
        overflow: TextOverflow.ellipsis,
        style: style,
      );
    } else if (status == DeliveryStatus.delivered) {
      // Inside the 48 h window the weekday alone places the day.
      final at = delivery.confirmedAt ?? delivery.updatedAt;
      final deliveredOn = l10n.delivDeliveredOn(
        windowEnd == null ? boardDayInEthiopia(at) : _weekday(at),
      );
      line = isBuyer && windowEnd != null
          ? ClockText(
              clockFormatOf(context)
                  .deadline(
                    windowEnd,
                    date: DateFormat.E().format,
                    sentence: l10n.delivReportProblemUntil,
                  )
                  .prefixed('$deliveredOn · '),
              maxLines: 1,
              overflow: TextOverflow.ellipsis,
              style: style,
            )
          : Text(
              '$parties · $deliveredOn',
              maxLines: 2,
              overflow: TextOverflow.ellipsis,
              style: style,
            );
    } else {
      line = Text(
        subtitle,
        maxLines: 2,
        overflow: TextOverflow.ellipsis,
        style: style,
      );
    }
    return FwCard(
      onTap: onTap,
      padding: const EdgeInsets.all(14),
      borderColor: selected ? context.roleColors.primary : AppTheme.line,
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Row(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Expanded(
                child: Text(
                  title,
                  maxLines: 1,
                  overflow: TextOverflow.ellipsis,
                  style: FwText.cardTitle.copyWith(
                    fontSize: 15,
                    fontWeight: FontWeight.w700,
                  ),
                ),
              ),
              if (pill != null) ...[
                const SizedBox(width: 8),
                pill,
              ],
            ],
          ),
          const SizedBox(height: 6),
          line,
        ],
      ),
    );
  }

  /// "Mon": the weekday alone, on the Ethiopian day.
  static String _weekday(DateTime at) =>
      DateFormat.E().format(dayInEthiopia(at));
}

/// The 44 px tile that leads a delivery card (the deliveries board): the
/// crop while the delivery lives, a check once delivered, muted when
/// cancelled, a warning when disputed. Shared with the live trip's on the
/// road card.
class DeliveryCropTile extends StatelessWidget {
  const DeliveryCropTile({
    required this.cropKey,
    required this.status,
    super.key,
  });

  final String? cropKey;
  final DeliveryStatus status;

  @override
  Widget build(BuildContext context) {
    final (icon, background, foreground) = _tile(cropKey, status);
    return Container(
      width: 44,
      height: 44,
      decoration: BoxDecoration(
        color: background,
        borderRadius: BorderRadius.circular(14),
      ),
      // The boards' sprout while the crop is on its way.
      child: icon == null
          ? FwSprout(color: foreground)
          : Icon(icon, size: 22, color: foreground),
    );
  }

  /// The tile's icon and colours: the sprout (null icon) while the delivery
  /// lives, in the crop's board colours ([cropTileColors]), a check once
  /// delivered, muted when cancelled, a warning when disputed.
  static (IconData?, Color, Color) _tile(
    String? cropKey,
    DeliveryStatus status,
  ) {
    switch (status) {
      case DeliveryStatus.delivered:
        return (
          Icons.check,
          AppTheme.statusSuccessContainer,
          AppTheme.statusSuccess,
        );
      case DeliveryStatus.disputed:
        return (
          Icons.report_problem_outlined,
          AppTheme.statusErrorContainer,
          AppTheme.statusError,
        );
      case DeliveryStatus.cancelled:
        return (Icons.close, AppTheme.segmentTrack, AppTheme.slate);
      case DeliveryStatus.pending:
      case DeliveryStatus.pickedUp:
      case DeliveryStatus.inTransit:
        final tile = cropTileColors(cropKey);
        return (null, tile.background, tile.foreground);
    }
  }
}

/// Localized display label for a delivery status, shared by the status pill,
/// the Home recent-activity feed and the Home notifications sheet.
String deliveryStatusLabel(DeliveryStatus status, AppLocalizations l10n) {
  return switch (status) {
    DeliveryStatus.pending => l10n.deliveryStatusPending,
    DeliveryStatus.pickedUp => l10n.deliveryStatusPickedUp,
    DeliveryStatus.inTransit => l10n.deliveryStatusInTransit,
    DeliveryStatus.delivered => l10n.deliveryStatusDelivered,
    DeliveryStatus.disputed => l10n.deliveryStatusDisputed,
    DeliveryStatus.cancelled => l10n.deliveryStatusCancelled,
  };
}
