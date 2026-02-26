import 'package:flutter/material.dart';

import 'package:wereda_connect/core/database/schemas/payment.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

/// Color-coded status badge for escrow payment status.
///
/// Supports two sizes:
/// - Default: compact chip with icon + label (for list cards)
/// - Large: centered card with icon, label, and description (for detail screen)
class EscrowStatusBadge extends StatelessWidget {
  final PaymentStatus status;
  final bool large;

  const EscrowStatusBadge({
    required this.status,
    this.large = false,
    super.key,
  });

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final (color, icon) = _statusVisual(status);
    final label = _statusLabel(status, l10n);

    if (large) {
      return Container(
        width: double.infinity,
        padding: const EdgeInsets.symmetric(vertical: 16, horizontal: 24),
        decoration: BoxDecoration(
          color: color.withValues(alpha: 0.1),
          borderRadius: BorderRadius.circular(16),
          border: Border.all(color: color.withValues(alpha: 0.3)),
        ),
        child: Column(
          children: [
            Icon(icon, size: 48, color: color),
            const SizedBox(height: 8),
            Text(
              label.toUpperCase(),
              style: TextStyle(
                color: color,
                fontSize: 18,
                fontWeight: FontWeight.bold,
                letterSpacing: 1.2,
              ),
            ),
            const SizedBox(height: 4),
            Text(
              _statusDescription(status, l10n),
              style: TextStyle(
                color: color.withValues(alpha: 0.8),
                fontSize: 14,
              ),
            ),
          ],
        ),
      );
    }

    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 6),
      decoration: BoxDecoration(
        color: color.withValues(alpha: 0.1),
        borderRadius: BorderRadius.circular(12),
        border: Border.all(color: color.withValues(alpha: 0.3)),
      ),
      child: Row(
        mainAxisSize: MainAxisSize.min,
        children: [
          Icon(icon, size: 16, color: color),
          const SizedBox(width: 6),
          Text(
            label,
            style: TextStyle(
              color: color,
              fontSize: 12,
              fontWeight: FontWeight.w600,
            ),
          ),
        ],
      ),
    );
  }

  static (Color, IconData) _statusVisual(PaymentStatus status) {
    return switch (status) {
      PaymentStatus.initiated => (Colors.grey, Icons.hourglass_empty),
      PaymentStatus.escrowed => (Colors.amber.shade700, Icons.shield),
      PaymentStatus.released => (Colors.blue, Icons.send),
      PaymentStatus.completed => (Colors.green, Icons.check_circle),
      PaymentStatus.cancelled => (Colors.grey, Icons.cancel),
      PaymentStatus.disputed => (Colors.red.shade700, Icons.warning),
      PaymentStatus.refunded => (Colors.teal, Icons.replay),
    };
  }

  static String _statusLabel(PaymentStatus status, AppLocalizations l10n) {
    return switch (status) {
      PaymentStatus.initiated => l10n.escrowStatusInitiated,
      PaymentStatus.escrowed => l10n.escrowStatusEscrowed,
      PaymentStatus.released => l10n.escrowStatusReleased,
      PaymentStatus.completed => l10n.escrowStatusCompleted,
      PaymentStatus.cancelled => l10n.escrowStatusCancelled,
      PaymentStatus.disputed => l10n.escrowStatusDisputed,
      PaymentStatus.refunded => l10n.escrowStatusRefunded,
    };
  }

  static String _statusDescription(
    PaymentStatus status,
    AppLocalizations l10n,
  ) {
    return switch (status) {
      PaymentStatus.initiated => l10n.escrowDescInitiated,
      PaymentStatus.escrowed => l10n.escrowDescEscrowed,
      PaymentStatus.released => l10n.escrowDescReleased,
      PaymentStatus.completed => l10n.escrowDescCompleted,
      PaymentStatus.cancelled => l10n.escrowDescCancelled,
      PaymentStatus.disputed => l10n.escrowDescDisputed,
      PaymentStatus.refunded => l10n.escrowDescRefunded,
    };
  }
}
