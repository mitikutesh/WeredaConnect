import 'package:flutter/material.dart';

import 'package:wereda_connect/core/database/schemas/payment_intent.dart';
import 'package:wereda_connect/features/escrow_payment/presentation/widgets/amount_display.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

/// Card showing a queued payment intent with cancel button.
///
/// Displayed in the pending section of PaymentHistoryScreen for intents
/// waiting to sync when connectivity is restored.
class PendingIntentCard extends StatelessWidget {
  final PaymentIntent intent;
  final VoidCallback? onCancel;

  const PendingIntentCard({
    required this.intent,
    this.onCancel,
    super.key,
  });

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);

    return Card(
      margin: const EdgeInsets.symmetric(horizontal: 16, vertical: 4),
      color: theme.colorScheme.surfaceContainerHighest.withValues(alpha: 0.5),
      child: Padding(
        padding: const EdgeInsets.all(16),
        child: Row(
          children: [
            // Sync icon
            Container(
              width: 40,
              height: 40,
              decoration: BoxDecoration(
                color: Colors.orange.withValues(alpha: 0.1),
                shape: BoxShape.circle,
              ),
              child: const Icon(Icons.sync, color: Colors.orange, size: 20),
            ),
            const SizedBox(width: 12),
            // Details
            Expanded(
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  AmountDisplay(amount: intent.amount),
                  const SizedBox(height: 2),
                  Text(
                    '${AppLocalizations.of(context)!.escrowDeliveryShort(intent.deliveryId.length > 8 ? intent.deliveryId.substring(0, 8) : intent.deliveryId)} · ${AppLocalizations.of(context)!.escrowQueuedStatus}',
                    style: theme.textTheme.bodySmall?.copyWith(
                      color: theme.colorScheme.onSurface.withValues(alpha: 0.6),
                    ),
                  ),
                  if (intent.lastError != null)
                    Text(
                      intent.lastError!,
                      style: theme.textTheme.bodySmall?.copyWith(
                        color: Colors.red.shade700,
                      ),
                      maxLines: 1,
                      overflow: TextOverflow.ellipsis,
                    ),
                ],
              ),
            ),
            // Cancel button
            if (intent.status == IntentStatus.pendingSync && onCancel != null)
              TextButton(
                onPressed: onCancel,
                style: TextButton.styleFrom(
                  foregroundColor: Colors.red,
                  minimumSize: const Size(48, 48),
                ),
                child: Text(AppLocalizations.of(context)!.escrowCancelIntent),
              ),
          ],
        ),
      ),
    );
  }
}
