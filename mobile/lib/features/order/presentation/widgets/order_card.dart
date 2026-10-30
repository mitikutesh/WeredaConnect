import 'package:flutter/material.dart';
import 'package:intl/intl.dart';

import 'package:wereda_connect/core/models/order.dart';
import 'package:wereda_connect/core/models/order_commitment.dart';
import 'package:wereda_connect/core/models/payment.dart';
import 'package:wereda_connect/core/utils/ethiopia_time.dart';
import 'package:wereda_connect/core/utils/etb_format.dart';
import 'package:wereda_connect/core/utils/party_name.dart';
import 'package:wereda_connect/features/crop_listing/domain/models/crop_type.dart';
import 'package:wereda_connect/features/escrow_payment/presentation/widgets/escrow_status_badge.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';
import 'package:wereda_connect/shared/widgets/fieldway.dart';
import 'package:wereda_connect/shared/widgets/status_chip.dart';

/// One order in a list, laid out like the order boards' summary: crop and
/// quantity as the title, the other party under it, the amount on the right
/// with the order day, then the status pill. A farmer's unanswered funded
/// order ends with Reject and Accept, the boards' two-button row.
class OrderCard extends StatelessWidget {
  final Order order;
  final VoidCallback? onTap;
  final VoidCallback? onAccept;
  final VoidCallback? onReject;
  final bool isFarmerView;

  /// The order's escrow is held for review (FRD-06): an "Under Review" chip,
  /// and no Accept, which the server refuses while it is held (Reject stays).
  final bool underReview;

  /// False while the list has not yet learned which orders are under
  /// review: Accept shows disabled until then.
  final bool acceptEnabled;

  const OrderCard({
    required this.order,
    super.key,
    this.onTap,
    this.onAccept,
    this.onReject,
    this.isFarmerView = false,
    this.underReview = false,
    this.acceptEnabled = true,
  });

  static final NumberFormat _kgFormat = NumberFormat('#,##0', 'en_US');

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final day = DateFormat.yMMMd().format(dayInEthiopia(order.createdAt));
    final amount = l10n.etbCurrency(
      formatEtbAmount(order.orderTotal ?? order.totalPrice),
    );
    final transport = order.transportCost;
    // Show accept/reject buttons only once the order is funded (buyer
    // has paid into escrow). This matches the order detail screen, the
    // repositories, and the server's funded→accepted transition: at
    // `created` the farmer can't act yet, so the action would no-op
    // (mobile) or be rejected by the server (web). ESC-15: a trip
    // order stays funded after the farmer's accept while the trucker
    // is asked, so the buttons go once the farmer answered.
    final showActions = isFarmerView &&
        order.status == OrderStatus.funded &&
        farmerResponseOf(order) == CommitmentResponse.pending &&
        (onAccept != null || onReject != null);

    return FwCard(
      onTap: onTap,
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Row(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text(
                      '${CropType.labelFor(order.cropType, l10n)} · '
                      '${l10n.quantityKg(_kgFormat.format(order.quantity))}',
                      style: FwText.cardTitle,
                      maxLines: 2,
                      overflow: TextOverflow.ellipsis,
                    ),
                    const SizedBox(height: 3),
                    Text(
                      isFarmerView
                          ? '${l10n.buyer}: ${partyDisplayName(order.buyerName, l10n)}'
                          : '${l10n.farmer}: ${partyDisplayName(order.farmerName, l10n)}',
                      style: const TextStyle(
                        fontSize: 12,
                        fontWeight: FontWeight.w500,
                        color: AppTheme.mutedSteel,
                      ),
                      maxLines: 1,
                      overflow: TextOverflow.ellipsis,
                    ),
                  ],
                ),
              ),
              const SizedBox(width: 10),
              Column(
                crossAxisAlignment: CrossAxisAlignment.end,
                children: [
                  Text(
                    amount,
                    style: const TextStyle(
                      fontSize: 16,
                      fontWeight: FontWeight.w800,
                      color: AppTheme.soilDark,
                    ),
                  ),
                  const SizedBox(height: 2),
                  Text(
                    day,
                    style: const TextStyle(
                      fontSize: 11,
                      fontWeight: FontWeight.w500,
                      color: AppTheme.mutedSteel,
                    ),
                  ),
                ],
              ),
            ],
          ),
          if (transport != null && transport > 0) ...[
            const SizedBox(height: 6),
            Text(
              '${l10n.transportCost}: ${l10n.etbCurrency(formatEtbAmount(transport))}',
              style: const TextStyle(
                fontSize: 12,
                fontWeight: FontWeight.w500,
                color: AppTheme.mutedSteel,
              ),
            ),
          ],
          const SizedBox(height: 10),
          Wrap(
            spacing: 6,
            runSpacing: 6,
            children: [
              StatusChip(
                label: orderStatusLabel(order.status, l10n),
                kind: orderStatusKind(order.status),
              ),
              if (underReview)
                const EscrowStatusBadge(status: PaymentStatus.held),
            ],
          ),
          if (showActions) ...[
            const SizedBox(height: 12),
            Row(
              children: [
                if (onReject != null)
                  Expanded(
                    child: OutlinedButton(
                      onPressed: onReject,
                      style: OutlinedButton.styleFrom(
                        foregroundColor: AppTheme.soilDark,
                        side: const BorderSide(color: Color(0xFFD6D3C8)),
                        minimumSize: const Size.fromHeight(48),
                        shape: RoundedRectangleBorder(
                          borderRadius: BorderRadius.circular(14),
                        ),
                        textStyle: const TextStyle(
                          fontSize: 14,
                          fontWeight: FontWeight.w700,
                        ),
                      ),
                      child: Text(l10n.rejectOrder),
                    ),
                  ),
                if (onAccept != null && !underReview) ...[
                  if (onReject != null) const SizedBox(width: 8),
                  Expanded(
                    child: FilledButton(
                      onPressed: acceptEnabled ? onAccept : null,
                      style: FilledButton.styleFrom(
                        backgroundColor: AppTheme.soilDark,
                        foregroundColor: Colors.white,
                        minimumSize: const Size.fromHeight(48),
                        shape: RoundedRectangleBorder(
                          borderRadius: BorderRadius.circular(14),
                        ),
                        textStyle: const TextStyle(
                          fontSize: 14,
                          fontWeight: FontWeight.w700,
                        ),
                      ),
                      child: Text(l10n.acceptOrder),
                    ),
                  ),
                ],
              ],
            ),
          ],
        ],
      ),
    );
  }
}

/// Maps order lifecycle states onto the shared six-kind status vocabulary.
StatusKind orderStatusKind(OrderStatus status) {
  return switch (status) {
    OrderStatus.created => StatusKind.neutral,
    OrderStatus.pendingPayment => StatusKind.warning,
    OrderStatus.paymentSubmitted => StatusKind.warning,
    OrderStatus.funded => StatusKind.info,
    OrderStatus.accepted => StatusKind.info,
    OrderStatus.rejected => StatusKind.error,
    OrderStatus.cancelled => StatusKind.neutral,
    OrderStatus.pickupConfirmed => StatusKind.transit,
    OrderStatus.inTransit => StatusKind.transit,
    OrderStatus.delivered => StatusKind.success,
    OrderStatus.disbursing => StatusKind.info,
    OrderStatus.settled => StatusKind.success,
    OrderStatus.paymentFailed => StatusKind.error,
    OrderStatus.disputed => StatusKind.error,
    OrderStatus.disputeResolved => StatusKind.neutral,
    OrderStatus.awaitingTransport => StatusKind.warning,
    OrderStatus.unknown => StatusKind.neutral,
  };
}

/// Localized display label for an order status, shared by the status chip
/// and the Home recent-activity feed.
String orderStatusLabel(OrderStatus status, AppLocalizations l10n) {
  return switch (status) {
    OrderStatus.created => l10n.statusCreated,
    OrderStatus.pendingPayment => l10n.statusPendingPayment,
    OrderStatus.paymentSubmitted => l10n.statusPaymentSubmitted,
    OrderStatus.funded => l10n.statusFunded,
    OrderStatus.accepted => l10n.orderAccepted,
    OrderStatus.rejected => l10n.orderRejected,
    OrderStatus.cancelled => l10n.orderCancelled,
    OrderStatus.pickupConfirmed => l10n.statusPickupConfirmed,
    OrderStatus.inTransit => l10n.statusInTransit,
    OrderStatus.delivered => l10n.statusDelivered,
    OrderStatus.disbursing => l10n.statusDisbursing,
    OrderStatus.settled => l10n.statusSettled,
    OrderStatus.paymentFailed => l10n.statusPaymentFailed,
    OrderStatus.disputed => l10n.statusDisputed,
    OrderStatus.disputeResolved => l10n.statusDisputeResolved,
    OrderStatus.awaitingTransport => l10n.statusAwaitingTransport,
    OrderStatus.unknown => l10n.orderStatusUpdating,
  };
}
