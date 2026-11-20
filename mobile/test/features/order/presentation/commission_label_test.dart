import 'dart:ui';

import 'package:flutter_test/flutter_test.dart';

import 'package:wereda_connect/features/order/presentation/commission_label.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

/// The commission is negotiable per order: the label shows the order's own
/// rate and never assumes 5%.
void main() {
  final en = lookupAppLocalizations(const Locale('en'));
  final am = lookupAppLocalizations(const Locale('am'));

  test('the order\'s rate, as a percent', () {
    expect(commissionLabelFor(en, 0.05), 'Platform Commission · 5%');
    expect(commissionLabelFor(en, 0.025), 'Platform Commission · 2.5%');
    expect(commissionLabelFor(en, 0.0375), 'Platform Commission · 3.75%');
  });

  test('a rate agreed at zero is shown as 0%', () {
    expect(commissionLabelFor(en, 0), 'Platform Commission · 0%');
  });

  test('no rate: the label alone', () {
    expect(commissionLabelFor(en, null), 'Platform Commission');
    expect(commissionLabelFor(en, double.nan), 'Platform Commission');
    expect(commissionLabelFor(en, -0.01), 'Platform Commission');
  });

  test('every language keeps its own word and Latin digits', () {
    expect(
      commissionLabelFor(am, 0.03),
      '${am.escrowPlatformCommission} · 3%',
    );
  });
}
