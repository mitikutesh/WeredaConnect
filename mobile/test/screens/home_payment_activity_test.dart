import 'package:flutter/widgets.dart';
import 'package:flutter_test/flutter_test.dart';

import 'package:wereda_connect/core/models/payment.dart';
import 'package:wereda_connect/screens/home_screen.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

/// FRD-06: the Home activity feed names a payment's status in the user's
/// language; it showed the enum name ("held payment", "unknown payment").
void main() {
  for (final locale in const [Locale('en'), Locale('am'), Locale('om')]) {
    final l10n = lookupAppLocalizations(locale);
    final code = locale.languageCode;

    test('$code: held reads Under Review', () {
      expect(
        paymentActivityTitle(l10n, PaymentStatus.held),
        l10n.paymentStatusTitle(l10n.escrowStatusHeld),
      );
    });

    test('$code: unknown reads Status Unknown', () {
      expect(
        paymentActivityTitle(l10n, PaymentStatus.unknown),
        l10n.paymentStatusTitle(l10n.escrowStatusUnknown),
      );
    });

    test('$code: no status shows its enum name', () {
      for (final status in PaymentStatus.values) {
        expect(
          paymentActivityTitle(l10n, status),
          isNot(l10n.paymentStatusTitle(status.name)),
          reason: status.name,
        );
      }
    });
  }

  test('en reads naturally', () {
    final en = lookupAppLocalizations(const Locale('en'));
    expect(
      paymentActivityTitle(en, PaymentStatus.held),
      'Under Review payment',
    );
  });
}
