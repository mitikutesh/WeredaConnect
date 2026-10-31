import 'package:flutter/material.dart';
import 'package:intl/intl.dart';

import 'package:wereda_connect/core/models/delivery.dart';
import 'package:wereda_connect/core/utils/board_day.dart';
import 'package:wereda_connect/core/utils/ethiopia_time.dart';
import 'package:wereda_connect/core/utils/party_name.dart';
import 'package:wereda_connect/features/crop_listing/domain/models/crop_type.dart';
import 'package:wereda_connect/features/delivery_verification/data/delivery_constants.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';
import 'package:wereda_connect/shared/widgets/clock_text.dart';
import 'package:wereda_connect/shared/widgets/fieldway.dart';

/// A delivery in a list, as the Buyer · Deliveries board draws the waiting
/// and the delivered card: a crop tile, "Teff · 1,500 kg", who and where
/// ("Kebede Alemu, Bichena · pickup Thu 30 Oct"), the buyer's report window
/// with a "Release pending" pill once delivered. The crop line comes from the
/// delivery row itself (delivery-verification-api 1.21.0); a delivery created
/// before that keeps its number as the title.
///
/// The same card serves all three roles: the line under the title names the
/// parties the viewer is not.
class DeliveryCard extends StatelessWidget {
  final Delivery delivery;
  final VoidCallback? onTap;
  final String currentUserId;

  /// Highlighted (the delivery shown beside the list on a wide screen).
  final bool selected;

  const DeliveryCard({
    required this.delivery,
    required this.currentUserId,
    super.key,
    this.onTap,
    this.selected = false,
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
        l10n.delivPickupOn(_shortDay(planned)),
      DeliveryStatus.delivered when windowEnd == null => l10n.delivDeliveredOn(
          _shortDay(delivery.confirmedAt ?? delivery.updatedAt),
        ),
      _ => null,
    };
    final subtitle = whenLine == null ? parties : '$parties · $whenLine';

    // The buyer's report window replaces the parties line, as on the board.
    final showReportUntil = isBuyer && windowEnd != null;
    final releasePending = windowEnd != null && (isBuyer || isFarmer);
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

    return FwCard(
      onTap: onTap,
      padding: const EdgeInsets.all(14),
      borderColor: selected ? AppTheme.fieldGreen : AppTheme.whisperBorder,
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Row(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              DeliveryCropTile(cropKey: cropKey, status: status),
              const SizedBox(width: 12),
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text(
                      title,
                      maxLines: 1,
                      overflow: TextOverflow.ellipsis,
                      style: FwText.cardTitle,
                    ),
                    const SizedBox(height: 3),
                    if (showReportUntil)
                      ClockText(
                        clockFormatOf(context).deadline(
                          windowEnd,
                          date: DateFormat.E(),
                          sentence: l10n.liveTripReportProblemUntil,
                        ),
                        style: FwText.body,
                      )
                    else
                      Text(
                        subtitle,
                        maxLines: 2,
                        overflow: TextOverflow.ellipsis,
                        style: FwText.body,
                      ),
                    if (releasePending) ...[
                      const SizedBox(height: 8),
                      Align(
                        alignment: Alignment.centerLeft,
                        child: FwPill(label: l10n.liveTripReleasePending),
                      ),
                    ],
                  ],
                ),
              ),
              if (statusPill != null) ...[
                const SizedBox(width: 8),
                statusPill,
              ],
            ],
          ),
          if (waitingNote) ...[
            const SizedBox(height: 12),
            Row(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                const Padding(
                  padding: EdgeInsets.only(top: 1),
                  child: Icon(
                    Icons.schedule,
                    size: 16,
                    color: AppTheme.mutedSteel,
                  ),
                ),
                const SizedBox(width: 8),
                Expanded(
                  child: Text(
                    l10n.liveTripMapStartsAtPickup,
                    style: FwText.body.copyWith(height: 1.35),
                  ),
                ),
              ],
            ),
          ],
        ],
      ),
    );
  }

  /// "Thu Oct 30": the weekday, then the short date, on the Ethiopian day.
  static String _shortDay(DateTime at) {
    final day = dayInEthiopia(at);
    return formatBoardDay(day);
  }
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
      child: Icon(icon, size: 22, color: foreground),
    );
  }

  /// The tile's icon and colours: the crop while the delivery lives (in the
  /// crop's board colours, [cropTileColors]), a check once delivered, muted
  /// when cancelled, a warning when disputed.
  static (IconData, Color, Color) _tile(
    String? cropKey,
    DeliveryStatus status,
  ) {
    switch (status) {
      case DeliveryStatus.delivered:
        return (Icons.check, AppTheme.fieldGreenTint, AppTheme.fieldGreen);
      case DeliveryStatus.disputed:
        return (
          Icons.report_problem_outlined,
          AppTheme.statusErrorContainer,
          AppTheme.statusError,
        );
      case DeliveryStatus.cancelled:
        return (Icons.close, AppTheme.segmentTrack, AppTheme.mutedSteel);
      case DeliveryStatus.pending:
      case DeliveryStatus.pickedUp:
      case DeliveryStatus.inTransit:
        final crop = cropKey == null ? null : CropType.tryFromKey(cropKey);
        final icon = crop?.icon ?? Icons.eco_outlined;
        final tile = cropTileColors(cropKey);
        return (icon, tile.background, tile.foreground);
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
