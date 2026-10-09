import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

import 'package:wereda_connect/core/errors/app_error.dart';
import 'package:wereda_connect/core/network/api_exception.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

/// ESC-12: permanent order rejections, web conflicts and local guard refusals
/// each get a code the screens can explain in all three languages.
void main() {
  // Emitted today, plus the planned ORDER_* codes (spec section 8), which the
  // prefix rule already covers with the generic text.
  const orderCodes = [
    'ORDER_VALIDATION',
    'ORDER_UNAUTHORIZED',
    'ORDER_DEADLINE_PASSED',
    'ORDER_CANCEL_NOT_ALLOWED',
    'ORDER_ACTION_NOT_ALLOWED',
    'ORDER_TRIP_NOT_ELIGIBLE',
    'ORDER_TRANSPORT_ATTEMPTS_EXCEEDED',
  ];

  group('mapErrorToCode', () {
    for (final code in orderCodes) {
      test('$code is orderRefused', () {
        expect(
          mapErrorToCode(SyncRejectedException('$code: refused')),
          AppErrorCode.orderRefused,
        );
      });
    }

    test('a web conflict is a conflict, whatever its message', () {
      expect(
        mapErrorToCode(const SyncConflictException('Version conflict.')),
        AppErrorCode.conflict,
      );
      expect(
        mapErrorToCode(const SyncConflictException('ORDER_VALIDATION: x')),
        AppErrorCode.conflict,
      );
    });

    test('local guard refusals and an existing rating have their own codes',
        () {
      expect(
        mapErrorToCode(const OrderActionNotAllowedException('not yours')),
        AppErrorCode.orderRefused,
      );
      expect(
        mapErrorToCode(const AlreadyRatedException()),
        AppErrorCode.alreadyRated,
      );
    });

    test('KYC, step-up and other prefixes keep their codes', () {
      expect(
        mapErrorToCode(
          const SyncRejectedException('ORDER_KYC_REQUIRED: buyer_fayda: x'),
        ),
        AppErrorCode.kycRequired,
      );
      expect(
        mapErrorToCode(const SyncRejectedException('AUTH_040: step-up')),
        AppErrorCode.stepUpRequired,
      );
      expect(
        mapErrorToCode(const SyncRejectedException('Order not found.')),
        AppErrorCode.server,
      );
      expect(
        mapErrorToCode(const SyncRejectedException('FRAUD_VELOCITY_EXCEEDED')),
        AppErrorCode.server,
      );
      expect(
        mapErrorToCode(const SyncDeferredException('DELIVERY_NONCE_PENDING')),
        AppErrorCode.server,
      );
    });
  });

  group('syncRejectionErrorCode', () {
    test('a refused order create removed the order', () {
      expect(
        syncRejectionErrorCode(
          entityType: 'order',
          action: 'create',
          message: 'ORDER_VALIDATION: Insufficient stock.',
        ),
        AppErrorCode.orderNotPlaced,
      );
    });

    test('any other refused order change was undone', () {
      for (final message in [
        'ORDER_VALIDATION: Invalid status transition.',
        'FRAUD_PAYMENT: blocked',
        '', // the whole batch was refused (HTTP 400/422)
      ]) {
        expect(
          syncRejectionErrorCode(
            entityType: 'order',
            action: 'update',
            message: message,
          ),
          AppErrorCode.orderRefused,
          reason: message,
        );
      }
    });

    // ESC-08: the local cancel was undone, so say why, not "not saved".
    test('a delivery cancel refused after pickup has its own reason', () {
      for (final action in ['update', 'delete']) {
        expect(
          syncRejectionErrorCode(
            entityType: 'delivery',
            action: action,
            message: 'DELIVERY_CANCEL_NOT_ALLOWED: only before pickup.',
          ),
          AppErrorCode.deliveryCancelNotAllowed,
          reason: action,
        );
      }
    });

    test('any other refused change was not saved', () {
      for (final (entityType, message) in [
        ('crop_listing', 'FRAUD_VELOCITY_EXCEEDED: slow down'),
        ('trip', ''),
        ('delivery', 'DELIVERY_ORDER_REQUIRED: missing orderId'),
        ('rating', 'SYNC_VALIDATION: bad'),
      ]) {
        expect(
          syncRejectionErrorCode(
            entityType: entityType,
            action: 'create',
            message: message,
          ),
          AppErrorCode.changeNotSaved,
          reason: entityType,
        );
      }
    });
  });

  test('the new codes read in en, am and om, never as the generic error',
      () async {
    for (final locale in ['en', 'am', 'om']) {
      final l10n = await AppLocalizations.delegate.load(Locale(locale));
      for (final code in [
        AppErrorCode.orderRefused,
        AppErrorCode.orderNotPlaced,
        AppErrorCode.alreadyRated,
        AppErrorCode.changeNotSaved,
        AppErrorCode.deliveryCancelNotAllowed,
      ]) {
        final text = localizeErrorCode(code, l10n);
        expect(text, isNotEmpty, reason: '$locale $code');
        expect(text, isNot(l10n.errorOccurred), reason: '$locale $code');
      }
      expect(
        localizeErrorCode(AppErrorCode.orderRefused, l10n),
        l10n.errorOrderRefused,
      );
      expect(
        localizeErrorCode(AppErrorCode.orderNotPlaced, l10n),
        l10n.errorOrderNotPlaced,
      );
      expect(
        localizeErrorCode(AppErrorCode.alreadyRated, l10n),
        l10n.alreadyRated,
      );
      expect(
        localizeErrorCode(AppErrorCode.changeNotSaved, l10n),
        l10n.errorChangeNotSaved,
      );
      expect(
        localizeErrorCode(AppErrorCode.deliveryCancelNotAllowed, l10n),
        l10n.deliveryErrorCancelNotAllowed,
      );
    }
  });

  group('live Telebirr total refusal (ESC-44)', () {
    const message = 'ORDER_VALIDATION: This order total (50.00 ETB) is '
        'outside what one Telebirr payment can take (100 to 500000 ETB).';

    test('web: the inline error has its own code', () {
      expect(
        mapErrorToCode(const SyncRejectedException(message)),
        AppErrorCode.paymentTotalOutOfRange,
      );
      expect(
        mapErrorToCode(const SyncRejectedException('ORDER_VALIDATION: other')),
        AppErrorCode.orderRefused,
      );
    });

    test('mobile: the rejection snackbar has its own code', () {
      expect(
        syncRejectionErrorCode(
          entityType: 'order',
          action: 'update',
          message: message,
        ),
        AppErrorCode.paymentTotalOutOfRange,
      );
      expect(
        syncRejectionErrorCode(
          entityType: 'order',
          action: 'create',
          message: message,
        ),
        AppErrorCode.orderNotPlaced,
      );
    });

    test('says why in all three languages', () async {
      for (final locale in ['en', 'am', 'om']) {
        final l10n = await AppLocalizations.delegate.load(Locale(locale));
        final text =
            localizeErrorCode(AppErrorCode.paymentTotalOutOfRange, l10n);
        expect(text, l10n.paymentTotalOutOfRange, reason: locale);
        expect(text, isNot(l10n.errorOrderRefused), reason: locale);
      }
    });
  });
}
