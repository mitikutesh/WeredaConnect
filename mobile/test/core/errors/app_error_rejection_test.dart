import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

import 'package:wereda_connect/core/errors/app_error.dart';
import 'package:wereda_connect/core/network/api_exception.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

/// ESC-12: permanent order rejections, web conflicts and local guard refusals
/// each get a code the screens can explain in all three languages.
void main() {
  // Emitted today, plus ORDER_ACTION_NOT_ALLOWED (spec section 8), whose
  // meaning is the generic text. ORDER_CANCEL_NOT_ALLOWED (ESC-06) and the
  // part B codes have their own texts (below).
  const orderCodes = [
    'ORDER_VALIDATION',
    'ORDER_UNAUTHORIZED',
    'ORDER_ACTION_NOT_ALLOWED',
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

    // ESC-06: an accepted order may be cancelled only after a transport failure.
    test(
        'an order cancel refused before the pickup deadline has its own reason',
        () {
      expect(
        syncRejectionErrorCode(
          entityType: 'order',
          action: 'update',
          message: 'ORDER_CANCEL_NOT_ALLOWED: The crops can still be collected '
              'until 2026-10-12T08:00:00.0000000Z.',
        ),
        AppErrorCode.orderCancelNotAllowed,
      );
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

    // ESC-07: the local dispute was undone, so say why, not "not saved".
    test('a refused or late delivery dispute has its own reason', () {
      expect(
        syncRejectionErrorCode(
          entityType: 'delivery',
          action: 'update',
          message: 'DELIVERY_DISPUTE_REFUSED: The payment for this delivery '
              'is being settled, so it can no longer be disputed.',
        ),
        AppErrorCode.deliveryDisputeRefused,
      );
      expect(
        syncRejectionErrorCode(
          entityType: 'delivery',
          action: 'update',
          message: 'DELIVERY_DISPUTE_EXPIRED: Dispute window (48 hours) has '
              'expired.',
        ),
        AppErrorCode.deliveryDisputeExpired,
      );
    });

    test('web: a refused or late dispute maps to its own code', () {
      expect(
        mapErrorToCode(
          const SyncRejectedException('DELIVERY_DISPUTE_REFUSED: settled.'),
        ),
        AppErrorCode.deliveryDisputeRefused,
      );
      expect(
        mapErrorToCode(
          const SyncRejectedException('DELIVERY_DISPUTE_EXPIRED: too late.'),
        ),
        AppErrorCode.deliveryDisputeExpired,
      );
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
        AppErrorCode.deliveryDisputeRefused,
        AppErrorCode.deliveryDisputeExpired,
        AppErrorCode.orderCancelNotAllowed,
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
      expect(
        localizeErrorCode(AppErrorCode.deliveryDisputeRefused, l10n),
        l10n.deliveryErrorDisputeRefused,
      );
      expect(
        localizeErrorCode(AppErrorCode.deliveryDisputeExpired, l10n),
        l10n.deliveryErrorDisputeExpired,
      );
      expect(
        localizeErrorCode(AppErrorCode.orderCancelNotAllowed, l10n),
        l10n.errorOrderCancelNotAllowed,
      );
    }
  });

  group('order cancel refusal (ESC-06)', () {
    test('web: the refused cancel and the local guard read the same', () {
      expect(
        mapErrorToCode(
          const SyncRejectedException(
            'ORDER_CANCEL_NOT_ALLOWED: The crops were already picked up.',
          ),
        ),
        AppErrorCode.orderCancelNotAllowed,
      );
      expect(
        mapErrorToCode(const OrderCancelNotAllowedException('not yet')),
        AppErrorCode.orderCancelNotAllowed,
      );
      // Still a permanent order refusal: the queue reverts it like any ORDER_*.
      expect(
        isRevertableRejectionMessage('ORDER_CANCEL_NOT_ALLOWED: not yet'),
        isTrue,
      );
    });
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

  group('message refused because contact is not open (ESC-48)', () {
    const message = 'MESSAGE_CONTACT_LOCKED: Contact opens after payment is '
        'held in escrow and the farmer accepts the order.';

    test('web: the refused send has its own code', () {
      expect(
        mapErrorToCode(const SyncRejectedException(message)),
        AppErrorCode.contactLocked,
      );
    });

    test('mobile: the rejection snackbar has its own code', () {
      expect(
        syncRejectionErrorCode(
          entityType: 'message',
          action: 'create',
          message: message,
        ),
        AppErrorCode.contactLocked,
      );
    });

    test(
        'says contact is closed (the refusal does not say why) in all three '
        'languages', () async {
      for (final locale in ['en', 'am', 'om']) {
        final l10n = await AppLocalizations.delegate.load(Locale(locale));
        final text = localizeErrorCode(AppErrorCode.contactLocked, l10n);
        expect(text, l10n.contactClosedNote, reason: locale);
        expect(text, isNot(l10n.contactAfterEscrowNote), reason: locale);
        expect(text, isNot(l10n.errorChangeNotSaved), reason: locale);
      }
    });
  });

  // ESC-12 part B (sync-protocol.md 1.17.0): every spec section 8 code has a
  // text that says what happened and what to do.
  group('part B codes (FUP-01)', () {
    test('web: each refusal maps to its own code', () {
      for (final (message, code) in [
        (
          'REPUTATION_RESTRICTED: restricted',
          AppErrorCode.reputationRestricted
        ),
        ('TRIP_HAS_BOOKINGS: booked', AppErrorCode.tripHasBookings),
        ('TRIP_CAPACITY_EXCEEDED: full', AppErrorCode.tripCapacityExceeded),
        ('CROP_HAS_ACTIVE_ORDERS: open', AppErrorCode.cropHasActiveOrders),
        ('APP_UPDATE_REQUIRED: 1.0.0', AppErrorCode.appUpdateRequired),
        ('ORDER_DEADLINE_PASSED: late', AppErrorCode.deadlinePassed),
        ('ORDER_TRIP_NOT_ELIGIBLE: route', AppErrorCode.tripNotEligible),
        (
          'ORDER_TRANSPORT_ATTEMPTS_EXCEEDED: 3',
          AppErrorCode.transportAttemptsExceeded,
        ),
        ('ORDER_ACTION_NOT_ALLOWED: no', AppErrorCode.orderRefused),
      ]) {
        expect(
          mapErrorToCode(SyncRejectedException(message)),
          code,
          reason: message,
        );
      }
    });

    test('web: a conflict stays a conflict', () {
      expect(
        mapErrorToCode(const SyncConflictException('TRIP_HAS_BOOKINGS: x')),
        AppErrorCode.conflict,
      );
    });

    test('mobile: the snackbar code depends on the entity and action', () {
      for (final (entityType, action, message, code) in [
        // The actor is restricted: a new listing, a new trip, an accept.
        (
          'crop_listing',
          'create',
          'REPUTATION_RESTRICTED: x',
          AppErrorCode.reputationRestricted,
        ),
        (
          'trip',
          'create',
          'REPUTATION_RESTRICTED: x',
          AppErrorCode.reputationRestricted,
        ),
        (
          'order',
          'update',
          'REPUTATION_RESTRICTED: x',
          AppErrorCode.reputationRestricted,
        ),
        // On a buyer's create the owner is restricted (spec A7).
        (
          'order',
          'create',
          'REPUTATION_RESTRICTED: x',
          AppErrorCode.counterpartyRestricted,
        ),
        (
          'trip',
          'update',
          'TRIP_HAS_BOOKINGS: x',
          AppErrorCode.tripHasBookings
        ),
        (
          'trip',
          'delete',
          'TRIP_HAS_BOOKINGS: x',
          AppErrorCode.tripHasBookings
        ),
        (
          'order',
          'create',
          'TRIP_CAPACITY_EXCEEDED: x',
          AppErrorCode.tripCapacityExceeded,
        ),
        (
          'order',
          'update',
          'TRIP_CAPACITY_EXCEEDED: x',
          AppErrorCode.tripCapacityExceeded,
        ),
        (
          'crop_listing',
          'delete',
          'CROP_HAS_ACTIVE_ORDERS: x',
          AppErrorCode.cropHasActiveOrders,
        ),
        (
          'order',
          'update',
          'ORDER_DEADLINE_PASSED: x',
          AppErrorCode.deadlinePassed,
        ),
        (
          'order',
          'create',
          'ORDER_TRIP_NOT_ELIGIBLE: x',
          AppErrorCode.tripNotEligible,
        ),
        (
          'order',
          'update',
          'ORDER_TRIP_NOT_ELIGIBLE: x',
          AppErrorCode.tripNotEligible,
        ),
        (
          'order',
          'update',
          'ORDER_TRANSPORT_ATTEMPTS_EXCEEDED: x',
          AppErrorCode.transportAttemptsExceeded,
        ),
        (
          'order',
          'update',
          'ORDER_ACTION_NOT_ALLOWED: x',
          AppErrorCode.orderRefused
        ),
        // A deadline on a create says the order was removed.
        (
          'order',
          'create',
          'ORDER_DEADLINE_PASSED: x',
          AppErrorCode.orderNotPlaced
        ),
      ]) {
        expect(
          syncRejectionErrorCode(
            entityType: entityType,
            action: action,
            message: message,
          ),
          code,
          reason: '$entityType $action $message',
        );
      }
    });

    // An order command is refused for the app version (undone); anything
    // else is kept queued for the updated build, so it says so.
    test('mobile: APP_UPDATE_REQUIRED says whether the change was kept', () {
      for (final (entityType, action, code) in [
        ('order', 'create', AppErrorCode.appUpdateRequired),
        ('order', 'update', AppErrorCode.appUpdateRequired),
        ('crop_listing', 'create', AppErrorCode.appUpdateRequiredKept),
        ('crop_listing', 'delete', AppErrorCode.appUpdateRequiredKept),
        ('trip', 'update', AppErrorCode.appUpdateRequiredKept),
        ('delivery', 'update', AppErrorCode.appUpdateRequiredKept),
        ('message', 'create', AppErrorCode.appUpdateRequiredKept),
        ('rating', 'create', AppErrorCode.appUpdateRequiredKept),
        ('buyer_intent', 'create', AppErrorCode.appUpdateRequiredKept),
      ]) {
        expect(
          syncRejectionErrorCode(
            entityType: entityType,
            action: action,
            message: 'APP_UPDATE_REQUIRED: minimum 2.0.0',
          ),
          code,
          reason: '$entityType $action',
        );
      }
    });

    test('only the named codes revert a crop listing or a trip', () {
      for (final message in [
        'REPUTATION_RESTRICTED: x',
        'CROP_HAS_ACTIVE_ORDERS: x',
      ]) {
        expect(isRevertableListingRejectionMessage(message), isTrue);
      }
      for (final message in [
        'REPUTATION_RESTRICTED: x',
        'TRIP_HAS_BOOKINGS: x',
      ]) {
        expect(isRevertableTripRejectionMessage(message), isTrue);
      }
      // CROP_VALIDATION and TRIP_VALIDATION also wrap a failed profile read
      // on the server, so a prefix alone is not deterministic, and the same
      // payload goes through once the app is updated.
      for (final message in [
        null,
        '',
        'APP_UPDATE_REQUIRED: x',
        'CROP_VALIDATION: The farmer profile could not be read.',
        'CROP_UNAUTHORIZED: not yours',
        'TRIP_VALIDATION: bad',
        'TRIP_UNAUTHORIZED: not yours',
        'FRAUD_DUPLICATE_LISTING: dup',
        'TRIP_CAPACITY_EXCEEDED: x',
      ]) {
        expect(
          isRevertableListingRejectionMessage(message),
          isFalse,
          reason: '$message',
        );
        expect(
          isRevertableTripRejectionMessage(message),
          isFalse,
          reason: '$message',
        );
      }
      expect(
        isRevertableListingRejectionMessage('TRIP_HAS_BOOKINGS: x'),
        isFalse,
      );
      expect(
        isRevertableTripRejectionMessage('CROP_HAS_ACTIVE_ORDERS: x'),
        isFalse,
      );
    });

    test('each code reads its own text in en, am and om', () async {
      for (final locale in ['en', 'am', 'om']) {
        final l10n = await AppLocalizations.delegate.load(Locale(locale));
        final expected = {
          AppErrorCode.deadlinePassed: l10n.errorDeadlinePassed,
          AppErrorCode.tripNotEligible: l10n.errorTripNotEligible,
          AppErrorCode.transportAttemptsExceeded:
              l10n.errorTransportAttemptsExceeded,
          AppErrorCode.tripCapacityExceeded: l10n.errorTripCapacityExceeded,
          AppErrorCode.reputationRestricted: l10n.errorReputationRestricted,
          AppErrorCode.counterpartyRestricted: l10n.errorCounterpartyRestricted,
          AppErrorCode.tripHasBookings: l10n.errorTripHasBookings,
          AppErrorCode.cropHasActiveOrders: l10n.errorCropHasActiveOrders,
          AppErrorCode.appUpdateRequired: l10n.errorAppUpdateRequired,
          AppErrorCode.appUpdateRequiredKept: l10n.errorAppUpdateRequiredKept,
        };
        for (final MapEntry(key: code, value: text) in expected.entries) {
          final localized = localizeErrorCode(code, l10n);
          expect(localized, text, reason: '$locale $code');
          expect(localized, isNotEmpty, reason: '$locale $code');
          for (final generic in [
            l10n.errorOccurred,
            l10n.errorOrderRefused,
            l10n.errorOrderNotPlaced,
            l10n.errorChangeNotSaved,
          ]) {
            expect(localized, isNot(generic), reason: '$locale $code');
          }
        }
        // No two codes share a sentence.
        expect(expected.values.toSet(), hasLength(expected.length));
      }
    });
  });
}
