import 'package:intl/intl.dart';

import 'package:wereda_connect/shared/l10n/app_localizations.dart';

/// The commission row's label with the order's own rate: "Platform
/// Commission · 5%". The rate is the order's, never a constant: it is set
/// when the order is made and an admin may agree another one before the
/// buyer pays (order-lifecycle.md). A rate of 0 is a real agreement and
/// shows as 0%; without a rate (a legacy row, a payment with no order) the
/// label names no rate.
String commissionLabelFor(AppLocalizations l10n, double? rate) {
  if (rate == null || rate < 0 || rate.isNaN) {
    return l10n.escrowPlatformCommission;
  }
  return '${l10n.escrowPlatformCommission} · '
      '${NumberFormat('0.##', 'en_US').format(rate * 100)}%';
}
