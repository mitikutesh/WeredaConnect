import 'package:flutter/material.dart';
import 'package:intl/intl.dart';

import 'package:wereda_connect/core/utils/ethiopia_time.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

/// "Payout expected on (date)" for a payee (FRD-13), shared by the escrow
/// detail and the payment list. The date is the day in Ethiopia, whatever the
/// device timezone (SIM-50). Says when, never why.
class PayoutExpectedText extends StatelessWidget {
  final DateTime payoutAt;

  /// Centered under the large status badge on the detail screen.
  final bool center;

  const PayoutExpectedText({
    required this.payoutAt,
    this.center = false,
    super.key,
  });

  static final _dateFormat = DateFormat.yMMMd();

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final color = theme.colorScheme.onSurface.withValues(alpha: 0.7);
    final l10n = AppLocalizations.of(context)!;
    final text = l10n.escrowPayoutExpectedOn(
      _dateFormat.format(dayInEthiopia(payoutAt)),
    );

    return Row(
      mainAxisAlignment:
          center ? MainAxisAlignment.center : MainAxisAlignment.start,
      mainAxisSize: center ? MainAxisSize.max : MainAxisSize.min,
      children: [
        Icon(Icons.event_available, size: 16, color: color),
        const SizedBox(width: 6),
        Flexible(
          child: Text(
            text,
            style: theme.textTheme.bodySmall?.copyWith(color: color),
          ),
        ),
      ],
    );
  }
}
