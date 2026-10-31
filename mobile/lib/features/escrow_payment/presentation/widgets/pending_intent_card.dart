import 'package:flutter/material.dart';

import 'package:wereda_connect/core/models/payment_intent.dart';
import 'package:wereda_connect/features/escrow_payment/presentation/widgets/amount_display.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';
import 'package:wereda_connect/shared/widgets/fieldway.dart';

/// A queued payment intent, waiting to sync when connectivity returns, as a
/// white card with a sync tile, the amount, the delivery and a Cancel action.
/// Shown in the pending section of PaymentHistoryScreen.
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
    final l10n = AppLocalizations.of(context)!;
    final deliveryId = intent.deliveryId.length > 8
        ? intent.deliveryId.substring(0, 8)
        : intent.deliveryId;

    return Padding(
      padding: const EdgeInsets.symmetric(horizontal: 20, vertical: 5),
      child: FwCard(
        padding: const EdgeInsets.all(14),
        child: Row(
          children: [
            Container(
              width: 42,
              height: 42,
              decoration: BoxDecoration(
                color: AppTheme.actionCream,
                borderRadius: BorderRadius.circular(12),
              ),
              child: const Icon(
                Icons.sync,
                color: AppTheme.money,
                size: 20,
              ),
            ),
            const SizedBox(width: 12),
            Expanded(
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  AmountDisplay(amount: intent.amount),
                  const SizedBox(height: 2),
                  Text(
                    '${l10n.escrowDeliveryShort(deliveryId)} · ${l10n.escrowQueuedStatus}',
                    style: FwText.body,
                  ),
                  if (intent.lastError != null)
                    Text(
                      intent.lastError!,
                      style: const TextStyle(
                        fontSize: 12,
                        fontWeight: FontWeight.w500,
                        color: AppTheme.statusError,
                      ),
                      maxLines: 1,
                      overflow: TextOverflow.ellipsis,
                    ),
                ],
              ),
            ),
            if (intent.status == IntentStatus.pendingSync && onCancel != null)
              TextButton(
                onPressed: onCancel,
                style: TextButton.styleFrom(
                  foregroundColor: AppTheme.statusError,
                  minimumSize: const Size(48, 48),
                ),
                child: Text(l10n.escrowCancelIntent),
              ),
          ],
        ),
      ),
    );
  }
}
