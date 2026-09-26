import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

import 'package:wereda_connect/features/escrow_payment/presentation/escrow_error_l10n.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

void main() {
  group('localizeEscrowError (SIM-24)', () {
    test(
        'ESCROW_024 has its own message, distinct from ESCROW_009, in all '
        'three languages', () async {
      for (final code in ['en', 'am', 'om']) {
        final l10n = await AppLocalizations.delegate.load(Locale(code));
        final funded = localizeEscrowError(l10n, 'ESCROW_024', 'raw');
        final duplicate = localizeEscrowError(l10n, 'ESCROW_009', 'raw');

        expect(funded, l10n.escrowErrorOrderAlreadyFunded, reason: code);
        expect(duplicate, l10n.escrowErrorAlreadyExists, reason: code);
        expect(funded, isNot(duplicate), reason: code);
        expect(funded, isNot(l10n.escrowErrorGeneric), reason: code);
      }
    });

    test('the SIM-04 initiate codes no longer fall through to generic',
        () async {
      final l10n = await AppLocalizations.delegate.load(const Locale('en'));
      expect(
        localizeEscrowError(l10n, 'ESCROW_025', 'raw'),
        l10n.escrowErrorUnauthorized,
      );
      expect(
        localizeEscrowError(l10n, 'ESCROW_026', 'raw'),
        l10n.escrowErrorUnauthorized,
      );
      expect(
        localizeEscrowError(l10n, 'ESCROW_028', 'raw'),
        l10n.escrowErrorInvalidAmount,
      );
      expect(
        localizeEscrowError(l10n, 'ESCROW_999', 'raw'),
        l10n.escrowErrorGeneric,
      );
    });
  });
}
