import 'package:flutter/widgets.dart';
import 'package:flutter_test/flutter_test.dart';

import 'package:wereda_connect/core/errors/app_error.dart';
import 'package:wereda_connect/core/network/api_exception.dart';
import 'package:wereda_connect/features/escrow_payment/data/repositories/escrow_result.dart';
import 'package:wereda_connect/features/escrow_payment/presentation/escrow_error_l10n.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_checkout_state.dart';
import 'package:wereda_connect/features/order/presentation/order_checkout_l10n.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

const _held = 'FRAUD_ACCOUNT_HELD: Your account is under review. Some actions '
    'are paused. Contact support.';

/// FRD-06: FRAUD_ACCOUNT_HELD has its own text on every path, in three
/// languages, and the AUTH_041 prompt names no amount.
void main() {
  group('web path (mapErrorToCode)', () {
    test('a refused /sync call reads as account held', () {
      expect(
        mapErrorToCode(const SyncRejectedException(_held)),
        AppErrorCode.accountHeld,
      );
    });

    test('a REST 403 with the code is not a plain "no permission"', () {
      expect(
        mapErrorToCode(
          const AuthException('under review', 403, fraudAccountHeldCode),
        ),
        AppErrorCode.accountHeld,
      );
      expect(
        mapErrorToCode(const AuthException('nope', 403)),
        AppErrorCode.forbidden,
      );
    });

    test('the other fraud codes are not mistaken for it', () {
      expect(
        mapErrorToCode(
          const SyncRejectedException('FRAUD_DUPLICATE_LISTING: duplicate'),
        ),
        isNot(AppErrorCode.accountHeld),
      );
    });
  });

  group('mobile queue (syncRejectionErrorCode)', () {
    for (final (entityType, action) in const [
      ('order', 'create'),
      ('order', 'update'),
      ('crop_listing', 'create'),
      ('trip', 'create'),
      ('payment_intent', 'initiate'),
    ]) {
      test('$entityType $action reads as account held', () {
        expect(
          syncRejectionErrorCode(
            entityType: entityType,
            action: action,
            message: _held,
          ),
          AppErrorCode.accountHeld,
        );
      });
    }
  });

  group('escrow REST code', () {
    test('escrowCodeOf keeps FRAUD_ACCOUNT_HELD instead of "sign in"', () {
      expect(
        escrowCodeOf(
          const AuthException('held', 403, fraudAccountHeldCode),
          'ESCROW_005',
        ),
        fraudAccountHeldCode,
      );
      expect(
        escrowCodeOf(const AuthException('m', 403, 'AUTH_025'), 'ESCROW_005'),
        'ESCROW_005',
      );
    });
  });

  group('text', () {
    final en = lookupAppLocalizations(const Locale('en'));
    for (final locale in const [Locale('en'), Locale('am'), Locale('om')]) {
      final l10n = lookupAppLocalizations(locale);
      final code = locale.languageCode;

      test('$code: account held has its own text on every surface', () {
        final text = localizeErrorCode(AppErrorCode.accountHeld, l10n);
        expect(text, l10n.fraudAccountHeld);
        expect(text, isNot(l10n.errorOccurred));
        expect(text, isNot(contains('FRAUD')));
        expect(
          localizeEscrowError(l10n, fraudAccountHeldCode, 'raw text'),
          l10n.fraudAccountHeld,
        );
        expect(
          localizeCheckoutIssue(l10n, CheckoutIssue.accountHeld),
          l10n.fraudAccountHeld,
        );
        if (code != 'en') expect(text, isNot(en.fraudAccountHeld));
      });

      test('$code: the Fayda step-up prompt names no amount', () {
        final text = l10n.faydaStepUpMessage;
        expect(text, isNot(contains('10,000')));
        expect(text, isNot(contains('10000')));
        if (code != 'en') expect(text, isNot(en.faydaStepUpMessage));
      });
    }
  });
}
