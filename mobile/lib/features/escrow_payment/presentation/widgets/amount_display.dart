import 'package:flutter/material.dart';
import 'package:intl/intl.dart';

import 'package:wereda_connect/shared/l10n/app_localizations.dart';

/// Large ETB-formatted amount display for accessibility.
///
/// Formats amounts with thousands separator (e.g., "ETB 15,000.00").
/// Supports two sizes for list items (default) and detail screens (large).
class AmountDisplay extends StatelessWidget {
  final double amount;
  final TextStyle? style;
  final Color? color;
  final bool large;

  const AmountDisplay({
    required this.amount,
    this.style,
    this.color,
    this.large = false,
    super.key,
  });

  static final _formatter = NumberFormat('#,##0.00', 'en_US');

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final formatted = _formatter.format(amount);

    final defaultStyle = large
        ? theme.textTheme.headlineMedium?.copyWith(
            fontWeight: FontWeight.bold,
            color: color ?? theme.colorScheme.onSurface,
          )
        : theme.textTheme.titleMedium?.copyWith(
            fontWeight: FontWeight.w600,
            color: color ?? theme.colorScheme.onSurface,
          );

    final l10n = AppLocalizations.of(context)!;
    return Text(l10n.escrowEtbAmount(formatted), style: style ?? defaultStyle);
  }
}
