import 'package:flutter/material.dart';

import 'package:wereda_connect/core/utils/etb_format.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';

/// An ETB amount the way the boards write money: grouped thousands, the
/// unit after the number ("15,000 ETB"), decimals only when there are some.
/// Two sizes: list rows (default, 16/800) and detail screens ([large],
/// 24/800).
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

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final defaultStyle = TextStyle(
      fontSize: large ? 24 : 16,
      fontWeight: FontWeight.w800,
      letterSpacing: large ? -0.5 : -0.2,
      color: color ?? AppTheme.soilDark,
    );
    return Text(
      l10n.etbCurrency(formatEtbAmount(amount)),
      style: style ?? defaultStyle,
    );
  }
}
