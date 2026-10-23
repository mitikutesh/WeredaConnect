import 'package:flutter/material.dart';
import 'package:intl/intl.dart';

import 'package:wereda_connect/core/models/order.dart';
import 'package:wereda_connect/core/models/payment.dart';
import 'package:wereda_connect/features/crop_listing/domain/models/crop_type.dart';
import 'package:wereda_connect/features/escrow_payment/presentation/widgets/escrow_status_badge.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';
import 'package:wereda_connect/shared/widgets/status_chip.dart';

/// Card displaying a single order in a list.
///
/// Shows crop type, quantity, buyer/farmer name, total price,
/// status badge, and timestamp.
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

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final l10n = AppLocalizations.of(context)!;
    final dateFormat = DateFormat.yMMMd();

    return Card(
      margin: EdgeInsets.zero,
      child: InkWell(
        onTap: onTap,
        borderRadius: BorderRadius.circular(12),
        child: Padding(
          padding: const EdgeInsets.all(16),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Row(
                children: [
                  Expanded(
                    child: Column(
                      crossAxisAlignment: CrossAxisAlignment.start,
                      children: [
                        Text(
                          '${CropType.labelFor(order.cropType, l10n)} · ${order.quantity.toStringAsFixed(0)} kg',
                          style: theme.textTheme.titleMedium?.copyWith(
                            fontWeight: FontWeight.bold,
                          ),
                        ),
                        const SizedBox(height: 4),
                        Text(
                          isFarmerView
                              ? '${l10n.buyer}: ${order.buyerName}'
                              : '${l10n.farmer}: ${order.farmerName}',
                          style: theme.textTheme.bodyMedium,
                        ),
                        const SizedBox(height: 4),
                        Text(
                          '${(order.orderTotal ?? order.totalPrice).toStringAsFixed(0)} ETB · ${dateFormat.format(order.createdAt)}',
                          style: theme.textTheme.bodySmall?.copyWith(
                            color: theme.colorScheme.onSurface
                                .withValues(alpha: 0.6),
                          ),
                        ),
                        if (order.transportCost != null &&
                            order.transportCost! > 0)
                          Padding(
                            padding: const EdgeInsets.only(top: 2),
                            child: Text(
                              '${l10n.transportCost}: ${order.transportCost!.toStringAsFixed(0)} ETB',
                              style: theme.textTheme.bodySmall?.copyWith(
                                color: theme.colorScheme.onSurface
                                    .withValues(alpha: 0.5),
                              ),
                            ),
                          ),
                      ],
                    ),
                  ),
                  Column(
                    crossAxisAlignment: CrossAxisAlignment.end,
                    children: [
                      _OrderStatusChip(status: order.status, l10n: l10n),
                      if (underReview) ...[
                        const SizedBox(height: 4),
                        const EscrowStatusBadge(status: PaymentStatus.held),
                      ],
                    ],
                  ),
                ],
              ),
              // Show accept/reject buttons only once the order is funded (buyer
              // has paid into escrow). This matches the order detail screen, the
              // repositories, and the server's funded→accepted transition — at
              // `created` the farmer can't act yet, so the action would no-op
              // (mobile) or be rejected by the server (web).
              if (isFarmerView &&
                  order.status == OrderStatus.funded &&
                  (onAccept != null || onReject != null)) ...[
                const SizedBox(height: 12),
                Row(
                  mainAxisAlignment: MainAxisAlignment.end,
                  children: [
                    if (onReject != null)
                      Flexible(
                        child: OutlinedButton(
                          onPressed: onReject,
                          style: OutlinedButton.styleFrom(
                            foregroundColor: AppTheme.onStatusErrorContainer,
                            side: const BorderSide(
                              color: AppTheme.onStatusErrorContainer,
                            ),
                          ),
                          child: Text(l10n.rejectOrder),
                        ),
                      ),
                    if (onAccept != null && !underReview) ...[
                      const SizedBox(width: 8),
                      Flexible(
                        child: FilledButton(
                          onPressed: acceptEnabled ? onAccept : null,
                          child: Text(l10n.acceptOrder),
                        ),
                      ),
                    ],
                  ],
                ),
              ],
            ],
          ),
        ),
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
  };
}

class _OrderStatusChip extends StatelessWidget {
  final OrderStatus status;
  final AppLocalizations l10n;

  const _OrderStatusChip({required this.status, required this.l10n});

  @override
  Widget build(BuildContext context) {
    return StatusChip(
      label: orderStatusLabel(status, l10n),
      kind: orderStatusKind(status),
    );
  }
}
