import 'package:flutter/material.dart';
import 'package:intl/intl.dart';

import 'package:wereda_connect/core/models/delivery.dart';
import 'package:wereda_connect/core/utils/party_name.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/widgets/delivery_status_indicator.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';

/// Card displaying a single delivery in a list.
///
/// Shows status icon, delivery ID, participant names, date,
/// and status chip. Follows the TripCard pattern for consistency.
class DeliveryCard extends StatelessWidget {
  final Delivery delivery;
  final VoidCallback? onTap;
  final String currentUserId;

  const DeliveryCard({
    required this.delivery,
    required this.currentUserId,
    super.key,
    this.onTap,
  });

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final l10n = AppLocalizations.of(context)!;
    final dateFormat = DateFormat.yMMMd();

    return Card(
      margin: const EdgeInsets.symmetric(horizontal: 16, vertical: 6),
      child: InkWell(
        onTap: onTap,
        borderRadius: BorderRadius.circular(12),
        child: Padding(
          padding: const EdgeInsets.all(16),
          child: Row(
            children: [
              DeliveryStatusIndicator(status: delivery.status),
              const SizedBox(width: 16),
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text(
                      l10n.deliveryIdLabel(
                        delivery.deliveryId.substring(0, 8),
                      ),
                      style: theme.textTheme.titleMedium?.copyWith(
                        fontWeight: FontWeight.bold,
                      ),
                    ),
                    const SizedBox(height: 4),
                    Text(
                      '${partyDisplayName(delivery.farmerName, l10n)} \u2192 ${partyDisplayName(delivery.buyerName, l10n)}',
                      style: theme.textTheme.bodyMedium,
                    ),
                    const SizedBox(height: 4),
                    Text(
                      '${partyDisplayName(delivery.truckerName, l10n)} \u00b7 ${dateFormat.format(delivery.createdAt)}',
                      style: theme.textTheme.bodySmall?.copyWith(
                        color:
                            theme.colorScheme.onSurface.withValues(alpha: 0.6),
                      ),
                    ),
                  ],
                ),
              ),
              _DeliveryStatusChip(status: delivery.status),
            ],
          ),
        ),
      ),
    );
  }
}

/// Localized display label for a delivery status, shared by the status chip,
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

class _DeliveryStatusChip extends StatelessWidget {
  final DeliveryStatus status;

  const _DeliveryStatusChip({required this.status});

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final label = deliveryStatusLabel(status, l10n);
    final color = switch (status) {
      DeliveryStatus.pending => AppTheme.statusWarning,
      DeliveryStatus.pickedUp => AppTheme.statusInfo,
      DeliveryStatus.inTransit => AppTheme.statusTransit,
      DeliveryStatus.delivered => AppTheme.statusSuccess,
      DeliveryStatus.disputed => AppTheme.statusError,
      DeliveryStatus.cancelled => AppTheme.textMuted,
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
