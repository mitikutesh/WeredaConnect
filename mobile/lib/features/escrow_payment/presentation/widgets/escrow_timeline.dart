import 'package:flutter/material.dart';
import 'package:intl/intl.dart';

import 'package:wereda_connect/core/models/payment.dart';
import 'package:wereda_connect/core/utils/ethiopia_time.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

/// Visual timeline of escrow state transitions.
///
/// Shows completed, current, and upcoming steps in the escrow lifecycle.
/// Handles special states like disputed and refunded.
class EscrowTimeline extends StatelessWidget {
  final Payment payment;

  const EscrowTimeline({required this.payment, super.key});

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final l10n = AppLocalizations.of(context)!;
    final steps = _buildSteps(l10n);

    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        for (var i = 0; i < steps.length; i++)
          _TimelineStep(
            label: steps[i].label,
            timestamp: steps[i].timestamp,
            isReached: steps[i].isReached,
            isCurrent: steps[i].isCurrent,
            isLast: i == steps.length - 1,
            theme: theme,
          ),
      ],
    );
  }

  List<_Step> _buildSteps(AppLocalizations l10n) {
    final statusIndex = _statusOrder(payment.status);

    final steps = <_Step>[
      _Step(
        label: l10n.escrowStatusInitiated,
        timestamp: payment.createdAt,
        isReached: statusIndex >= 0,
        isCurrent: payment.status == PaymentStatus.initiated,
      ),
      _Step(
        label: l10n.escrowStatusEscrowed,
        timestamp: statusIndex >= 1 ? payment.updatedAt : null,
        isReached: statusIndex >= 1,
        isCurrent: payment.status == PaymentStatus.escrowed,
      ),
    ];

    // Insert disputed step if applicable
    if (payment.status == PaymentStatus.disputed) {
      steps.add(
        _Step(
          label: l10n.escrowStatusDisputed,
          timestamp: payment.updatedAt,
          isReached: true,
          isCurrent: true,
        ),
      );
    }

    // Held for review (FRD-03): frozen until an admin releases or refunds.
    if (payment.status == PaymentStatus.held) {
      steps.add(
        _Step(
          label: l10n.escrowStatusHeld,
          timestamp: payment.updatedAt,
          isReached: true,
          isCurrent: true,
        ),
      );
    }

    // Insert refunded step if applicable
    if (payment.status == PaymentStatus.refunded) {
      steps.add(
        _Step(
          label: l10n.escrowStatusRefunded,
          timestamp: payment.refundedAt,
          isReached: true,
          isCurrent: true,
        ),
      );
    } else {
      steps.addAll([
        _Step(
          label: l10n.escrowStatusReleased,
          timestamp: payment.releasedAt,
          isReached: statusIndex >= 2,
          isCurrent: payment.status == PaymentStatus.released,
        ),
        _Step(
          label: l10n.escrowStatusCompleted,
          timestamp: payment.completedAt,
          isReached: statusIndex >= 3,
          isCurrent: payment.status == PaymentStatus.completed,
        ),
      ]);
    }

    return steps;
  }

  static int _statusOrder(PaymentStatus status) {
    return switch (status) {
      PaymentStatus.initiated => 0,
      PaymentStatus.escrowed => 1,
      PaymentStatus.released => 2,
      PaymentStatus.completed => 3,
      PaymentStatus.disputed => 1,
      PaymentStatus.cancelled => -1,
      PaymentStatus.refunded => 2,
      PaymentStatus.held => 1,
      // Whatever it is, the escrow was initiated; nothing else is claimed.
      PaymentStatus.unknown => 0,
    };
  }
}

class _Step {
  final String label;
  final DateTime? timestamp;
  final bool isReached;
  final bool isCurrent;

  const _Step({
    required this.label,
    required this.isReached,
    required this.isCurrent,
    this.timestamp,
  });
}

class _TimelineStep extends StatelessWidget {
  final String label;
  final DateTime? timestamp;
  final bool isReached;
  final bool isCurrent;
  final bool isLast;
  final ThemeData theme;

  const _TimelineStep({
    required this.label,
    required this.isReached,
    required this.isCurrent,
    required this.isLast,
    required this.theme,
    this.timestamp,
  });

  @override
  Widget build(BuildContext context) {
    final color = isReached
        ? theme.colorScheme.primary
        : theme.colorScheme.outline.withValues(alpha: 0.3);

    return IntrinsicHeight(
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          // Timeline indicator
          SizedBox(
            width: 32,
            child: Column(
              children: [
                Container(
                  width: isCurrent ? 16 : 12,
                  height: isCurrent ? 16 : 12,
                  decoration: BoxDecoration(
                    shape: BoxShape.circle,
                    color: isReached ? color : Colors.transparent,
                    border: Border.all(color: color, width: 2),
                  ),
                ),
                if (!isLast)
                  Expanded(
                    child: Container(
                      width: 2,
                      constraints: const BoxConstraints(minHeight: 24),
                      color: color,
                    ),
                  ),
              ],
            ),
          ),
          const SizedBox(width: 12),
          // Label and timestamp
          Expanded(
            child: Padding(
              padding: const EdgeInsets.only(bottom: 16),
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Text(
                    label,
                    style: theme.textTheme.bodyLarge?.copyWith(
                      fontWeight:
                          isCurrent ? FontWeight.bold : FontWeight.normal,
                      color: isReached
                          ? theme.colorScheme.onSurface
                          : theme.colorScheme.outline,
                    ),
                  ),
                  if (timestamp != null)
                    Text(
                      _formatTimestamp(timestamp!),
                      style: theme.textTheme.bodySmall?.copyWith(
                        color:
                            theme.colorScheme.onSurface.withValues(alpha: 0.6),
                      ),
                    ),
                ],
              ),
            ),
          ),
        ],
      ),
    );
  }

  String _formatTimestamp(DateTime dt) {
    return DateFormat.yMMMd().add_jm().format(instantInEthiopia(dt));
  }
}
