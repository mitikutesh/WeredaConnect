import 'package:flutter/material.dart';

import 'package:wereda_connect/core/models/payment.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/widgets/status_chip.dart';

/// Maps escrow payment states onto the shared six-kind status vocabulary.
StatusKind escrowStatusKind(PaymentStatus status) {
  return switch (status) {
    PaymentStatus.initiated => StatusKind.neutral,
    PaymentStatus.escrowed => StatusKind.warning,
    PaymentStatus.released => StatusKind.info,
    PaymentStatus.completed => StatusKind.success,
    PaymentStatus.cancelled => StatusKind.neutral,
    PaymentStatus.disputed => StatusKind.error,
    PaymentStatus.refunded => StatusKind.info,
  };
}

/// Color-coded status badge for escrow payment status.
///
/// Colors come from the shared status vocabulary; icons stay
/// escrow-specific (shield = funds held, send = released, …).
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
    final kind = escrowStatusKind(status);
    final icon = _statusIcon(status);
    final label = _statusLabel(status, l10n);

    if (large) {
      final fg = kind.foreground;
      return Container(
        width: double.infinity,
        padding: const EdgeInsets.symmetric(vertical: 16, horizontal: 24),
        decoration: BoxDecoration(
          color: kind.background,
          borderRadius: BorderRadius.circular(12),
          border: Border.all(color: fg.withValues(alpha: 0.25)),
        ),
        child: Column(
          children: [
            Icon(icon, size: 48, color: fg),
            const SizedBox(height: 8),
            Text(
              label.toUpperCase(),
              style: TextStyle(
                color: fg,
                fontSize: 18,
                fontWeight: FontWeight.bold,
                letterSpacing: 1.2,
              ),
            ),
            const SizedBox(height: 4),
            Text(
              _statusDescription(status, l10n),
              style: TextStyle(
                color: fg.withValues(alpha: 0.8),
                fontSize: 14,
              ),
            ),
          ],
        ),
      );
    }

    return StatusChip(label: label, kind: kind, icon: icon);
  }

  static IconData _statusIcon(PaymentStatus status) {
    return switch (status) {
      PaymentStatus.initiated => Icons.hourglass_empty,
      PaymentStatus.escrowed => Icons.shield,
      PaymentStatus.released => Icons.send,
      PaymentStatus.completed => Icons.check_circle,
      PaymentStatus.cancelled => Icons.cancel,
      PaymentStatus.disputed => Icons.warning,
      PaymentStatus.refunded => Icons.replay,
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
