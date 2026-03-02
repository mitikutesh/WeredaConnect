import 'package:flutter/material.dart';
import 'package:intl/intl.dart';

import 'package:wereda_connect/core/database/schemas/order.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

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

  const OrderCard({
    required this.order,
    super.key,
    this.onTap,
    this.onAccept,
    this.onReject,
    this.isFarmerView = false,
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
                          '${order.cropType} — ${order.quantity.toStringAsFixed(0)} kg',
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
                          '${order.totalPrice.toStringAsFixed(0)} ETB · ${dateFormat.format(order.createdAt)}',
                          style: theme.textTheme.bodySmall?.copyWith(
                            color: theme.colorScheme.onSurface
                                .withValues(alpha: 0.6),
                          ),
                        ),
                      ],
                    ),
                  ),
                  _OrderStatusChip(status: order.status, l10n: l10n),
                ],
              ),
              // Show accept/reject buttons for pending orders in farmer view
              if (isFarmerView &&
                  order.status == OrderStatus.pending &&
                  (onAccept != null || onReject != null)) ...[
                const SizedBox(height: 12),
                Row(
                  mainAxisAlignment: MainAxisAlignment.end,
                  children: [
                    if (onReject != null)
                      OutlinedButton(
                        onPressed: onReject,
                        style: OutlinedButton.styleFrom(
                          foregroundColor: Colors.red,
                          side: const BorderSide(color: Colors.red),
                        ),
                        child: Text(l10n.rejectOrder),
                      ),
                    if (onAccept != null) ...[
                      const SizedBox(width: 8),
                      FilledButton(
                        onPressed: onAccept,
                        child: Text(l10n.acceptOrder),
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

class _OrderStatusChip extends StatelessWidget {
  final OrderStatus status;
  final AppLocalizations l10n;

  const _OrderStatusChip({required this.status, required this.l10n});

  @override
  Widget build(BuildContext context) {
    final (color, label) = switch (status) {
      OrderStatus.pending => (Colors.orange, l10n.orderPending),
      OrderStatus.accepted => (Colors.green, l10n.orderAccepted),
      OrderStatus.rejected => (Colors.red, l10n.orderRejected),
      OrderStatus.cancelled => (Colors.grey, l10n.orderCancelled),
      OrderStatus.completed => (Colors.blue, l10n.orderCompleted),
    };

    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 4),
      decoration: BoxDecoration(
        color: color.withValues(alpha: 0.1),
        borderRadius: BorderRadius.circular(12),
        border: Border.all(color: color.withValues(alpha: 0.3)),
      ),
      child: Text(
        label,
        style: TextStyle(
          color: color,
          fontSize: 12,
          fontWeight: FontWeight.w600,
        ),
      ),
    );
  }
}
