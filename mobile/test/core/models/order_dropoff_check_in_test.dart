import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

import 'package:wereda_connect/core/database/schemas/order.dart';
import 'package:wereda_connect/core/errors/app_error.dart';
import 'package:wereda_connect/core/models/order_check_in.dart';
import 'package:wereda_connect/core/models/order_dropoff_check_in.dart';
import 'package:wereda_connect/core/network/api_exception.dart';
import 'package:wereda_connect/core/sync/handoff_check_in_rules.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

const _fix =
    HandoffCheckInFix(latitude: 9.031, longitude: 38.74, accuracyM: 20);

final _now = DateTime.utc(2026, 11, 22, 9);

/// A trucked order on the road, stamped for the drop-off judgement, T8 a day
/// ahead.
Order _order({
  String? judgement = 'pending',
  OrderStatus status = OrderStatus.inTransit,
  bool selfPickup = false,
  DateTime? deadline,
}) =>
    Order()
      ..orderId = 'ord-1'
      ..listingId = 'listing-1'
      ..buyerId = 'buyer-1'
      ..farmerId = 'farmer-1'
      ..buyerName = 'Buyer'
      ..farmerName = 'Farmer'
      ..cropType = 'teff'
      ..quantity = 500
      ..pricePerUnit = 60
      ..totalPrice = 30000
      ..status = status
      ..createdAt = _now.subtract(const Duration(days: 4))
      ..version = 6
      ..tripId = selfPickup ? null : 'trip-1'
      ..truckerId = selfPickup ? null : 'trucker-1'
      ..fulfillment = selfPickup ? 'self_pickup' : 'trucker'
      ..dropoffJudgement = judgement
      ..dropoffDeadlineAt = deadline ?? _now.add(const Duration(days: 1));

OrderCheckIn _dropoff({
  DateTime? receivedAt,
  bool late = false,
}) =>
    OrderCheckIn()
      ..kind = CheckInKind.arrivedDropoff
      ..receivedAt = receivedAt ?? _now.subtract(const Duration(hours: 2))
      ..distanceChecked = true
      ..isLate = late;

Matcher _refusedWith(AppErrorCode code) => throwsA(
      isA<CheckInNotAllowedException>().having((e) => e.code, 'code', code),
    );

/// ESC-36, the client part (FUP-336): the drop-off fields of an order, the
/// trucker's chance to check in, the local guard, T9, and the drop-off texts
/// of every `CHECKIN_*` refusal.
void main() {
  final en = lookupAppLocalizations(const Locale('en'));

  group('the order read', () {
    test('parses the drop-off judgement, T8, the reason and the check-in', () {
      final order = Order();
      applyOrderCheckInFields(order, {
        'dropoffJudgement': 'pending',
        'dropoffJudgedAt': '2026-11-23T10:00:00.000Z',
        'dropoffDeadlineAt': '2026-11-24T08:00:00+03:00',
        'disputeReason': 'delivery_overdue',
        'checkIns': {
          'arrived_dropoff': {
            'receivedAt': '2026-11-22T06:10:00.000Z',
            'capturedAt': '2026-11-22T06:09:30.000Z',
            'distanceChecked': true,
            'late': false,
          },
        },
      });

      expect(order.dropoffJudgement, 'pending');
      expect(order.dropoffJudgedAt, DateTime.utc(2026, 11, 23, 10));
      expect(order.dropoffDeadlineAt, DateTime.utc(2026, 11, 24, 5));
      expect(order.dropoffDeadlineAt!.isUtc, isTrue);
      expect(order.disputeReason, 'delivery_overdue');
      final checkIn = dropoffCheckInOf(order)!;
      expect(checkIn.receivedAt, DateTime.utc(2026, 11, 22, 6, 10));
      expect(checkIn.distanceChecked, isTrue);
      expect(checkIn.isLate, isFalse);
    });

    test('a row without them clears the device copy', () {
      final order = _order()
        ..disputeReason = 'silent_buyer'
        ..checkIns = [_dropoff()];
      applyOrderCheckInFields(order, {'dropoffJudgement': ''});

      expect(order.dropoffJudgement, isNull);
      expect(order.dropoffDeadlineAt, isNull);
      expect(order.dropoffJudgedAt, isNull);
      expect(order.disputeReason, isNull);
      expect(order.checkIns, isEmpty);
    });
  });

  group('the trucker\'s chance', () {
    test('open on the road before T8, for the trucker only', () {
      final order = _order();
      expect(
        dropoffCheckInChanceFor(order, 'trucker-1', _now),
        CheckInChance.open,
      );
      expect(
        dropoffCheckInChanceFor(order, 'buyer-1', _now),
        CheckInChance.none,
      );
      expect(
        dropoffCheckInChanceFor(order, 'farmer-1', _now),
        CheckInChance.none,
      );
      expect(
        dropoffCheckInChanceFor(
          _order(status: OrderStatus.pickupConfirmed),
          'trucker-1',
          _now,
        ),
        CheckInChance.open,
      );
    });

    test('given once the server has it or it waits on the phone', () {
      expect(
        dropoffCheckInChanceFor(
          _order()..checkIns = [_dropoff()],
          'trucker-1',
          _now,
        ),
        CheckInChance.given,
      );
      expect(
        dropoffCheckInChanceFor(
          _order()
            ..queuedCheckIns = {
              CheckInKind.arrivedDropoff: QueuedCheckIn(madeAt: _now),
            },
          'trucker-1',
          _now,
        ),
        CheckInChance.given,
      );
      // The arrival at the farm is another kind.
      expect(
        dropoffCheckInChanceFor(
          _order()
            ..checkIns = [
              OrderCheckIn()
                ..kind = CheckInKind.arrivedPickup
                ..receivedAt = _now,
            ],
          'trucker-1',
          _now,
        ),
        CheckInChance.open,
      );
    });

    test('closed after T8, none when not stamped, judged or off the road', () {
      expect(
        dropoffCheckInChanceFor(
          _order(deadline: _now.subtract(const Duration(minutes: 1))),
          'trucker-1',
          _now,
        ),
        CheckInChance.closed,
      );
      for (final order in [
        _order(judgement: null),
        _order(judgement: DropoffJudgementOutcome.deliveryOverdue),
        _order(status: OrderStatus.delivered),
        _order(status: OrderStatus.accepted),
        _order(selfPickup: true),
      ]) {
        expect(
          dropoffCheckInChanceFor(order, 'trucker-1', _now),
          CheckInChance.none,
        );
      }
    });
  });

  group('the local guard', () {
    test('lets an open check-in with a usable fix go', () {
      expect(
        checkDropoffCheckInAllowed(_order(), 'trucker-1', _fix, _now),
        isTrue,
      );
    });

    test('a repeat sends nothing', () {
      expect(
        checkDropoffCheckInAllowed(
          _order()..checkIns = [_dropoff()],
          'trucker-1',
          _fix,
          _now,
        ),
        isFalse,
      );
    });

    test('refuses with the drop-off texts', () {
      expect(
        () => checkDropoffCheckInAllowed(_order(), 'buyer-1', _fix, _now),
        _refusedWith(AppErrorCode.dropoffCheckInNotParty),
      );
      expect(
        () => checkDropoffCheckInAllowed(
          _order(deadline: _now.subtract(const Duration(seconds: 1))),
          'trucker-1',
          _fix,
          _now,
        ),
        _refusedWith(AppErrorCode.dropoffCheckInClosed),
      );
      expect(
        () => checkDropoffCheckInAllowed(
          _order(judgement: null),
          'trucker-1',
          _fix,
          _now,
        ),
        _refusedWith(AppErrorCode.dropoffCheckInNotOpen),
      );
      expect(
        () => checkDropoffCheckInAllowed(_order(), 'trucker-1', null, _now),
        _refusedWith(AppErrorCode.dropoffCheckInNeedsLocation),
      );
      expect(
        () => checkDropoffCheckInAllowed(
          _order(),
          'trucker-1',
          const HandoffCheckInFix(latitude: 51.5, longitude: -0.12),
          _now,
        ),
        _refusedWith(AppErrorCode.dropoffCheckInNeedsLocation),
      );
    });
  });

  group('T9, when the delivery may be confirmed for the buyer', () {
    test('48 hours after a counted check-in, while pending', () {
      final received = _now.subtract(const Duration(hours: 2));
      final order = _order()..checkIns = [_dropoff(receivedAt: received)];

      expect(countedDropoffCheckIn(order), isNotNull);
      expect(
        silentBuyerConfirmsAt(order),
        received.add(const Duration(hours: 48)),
      );
    });

    test('none for a late check-in, one after T8, or a judged order', () {
      expect(
        silentBuyerConfirmsAt(_order()..checkIns = [_dropoff(late: true)]),
        isNull,
      );
      expect(
        silentBuyerConfirmsAt(
          _order(deadline: _now.subtract(const Duration(hours: 3)))
            ..checkIns = [_dropoff()],
        ),
        isNull,
      );
      expect(
        silentBuyerConfirmsAt(
          _order(judgement: DropoffJudgementOutcome.autoConfirmed)
            ..checkIns = [_dropoff()],
        ),
        isNull,
      );
      expect(silentBuyerConfirmsAt(_order()), isNull);
      // The buyer confirmed: the cron follows it at its deadline, and
      // nothing is confirmed for anyone meanwhile.
      expect(
        silentBuyerConfirmsAt(
          _order(status: OrderStatus.delivered)..checkIns = [_dropoff()],
        ),
        isNull,
      );
    });
  });

  group('refusals of a drop-off check-in', () {
    const tooFar = 'CHECKIN_TOO_FAR: You are 7.2 km from the drop-off '
        '(at most 5 km).';
    const tooFarKept = '$tooFar The attempt was kept for review.';
    const refusals = {
      'CHECKIN_PHOTO_REQUIRED: Take the arrival photo first, then check in '
          'at the drop-off.': AppErrorCode.checkInPhotoRequired,
      tooFar: AppErrorCode.dropoffCheckInTooFar,
      tooFarKept: AppErrorCode.dropoffCheckInTooFarKept,
      'CHECKIN_TOO_LATE: The delivery deadline passed before this check-in '
              'reached the server. It was kept for review.':
          AppErrorCode.dropoffCheckInTooLate,
      'CHECKIN_NOT_OPEN: This delivery is no longer on the road.':
          AppErrorCode.dropoffCheckInNotOpen,
      'CHECKIN_NOT_PARTY: Only the order\'s trucker checks in at the '
          'drop-off.': AppErrorCode.dropoffCheckInNotParty,
      // FUP-373: the drop-off flag rolled back while the pickup flag stays
      // on: not open, not the update-the-app text.
      'CHECKIN_VALIDATION: arrived_dropoff is not taken yet (it comes with '
          'the drop-off check-in).': AppErrorCode.dropoffCheckInNotOpen,
      'CHECKIN_VALIDATION: latitude and longitude are required for '
          'arrived_dropoff.': AppErrorCode.checkInNotSaved,
      "Unsupported entity type: 'handoff_check_in'.":
          AppErrorCode.dropoffCheckInNotOpen,
    };

    test('each code has its own drop-off text (web, by the kind)', () {
      for (final MapEntry(key: message, value: code) in refusals.entries) {
        expect(
          checkInErrorCode(message, kind: CheckInKind.arrivedDropoff),
          code,
          reason: message,
        );
      }
    });

    test('each code has its own drop-off text (the phone, by the entity id)',
        () {
      for (final MapEntry(key: message, value: code) in refusals.entries) {
        expect(
          syncRejectionErrorCode(
            entityType: handoffCheckInEntityType,
            action: 'create',
            message: message,
            entityId: 'ord-1#arrived_dropoff',
          ),
          code,
          reason: message,
        );
      }
    });

    test('the arrival at the farm keeps its own texts', () {
      expect(
        syncRejectionErrorCode(
          entityType: handoffCheckInEntityType,
          action: 'create',
          message: 'CHECKIN_TOO_FAR: You are 6.0 km from the farm.',
          entityId: 'ord-1#arrived_pickup',
        ),
        AppErrorCode.checkInTooFar,
      );
      expect(
        syncRejectionErrorCode(
          entityType: handoffCheckInEntityType,
          action: 'create',
          message: 'CHECKIN_TOO_LATE: The pickup deadline passed.',
        ),
        AppErrorCode.checkInTooLate,
      );
      // A web refusal without its kind: the photo code is the drop-off's.
      expect(
        mapErrorToCode(
          const SyncRejectedException('CHECKIN_PHOTO_REQUIRED: Take it.'),
        ),
        AppErrorCode.checkInPhotoRequired,
      );
    });

    test('says to take the photo first, and that a far one was kept', () {
      expect(
        localizeErrorCode(AppErrorCode.checkInPhotoRequired, en),
        en.errorCheckInPhotoRequired,
      );
      expect(en.errorCheckInPhotoRequired, contains('photo first'));
      expect(
        localizeErrorCode(AppErrorCode.dropoffCheckInTooFarKept, en),
        contains('kept for review'),
      );
      expect(
        localizeErrorCode(AppErrorCode.dropoffCheckInTooFar, en),
        contains('Check in again'),
      );
      // FUP-373: about the check-in and the order's drop-off point, never
      // that the trucker was far away (the buyer's pin may be wrong, FUP-204).
      for (final text in [
        localizeErrorCode(AppErrorCode.dropoffCheckInTooFar, en),
        localizeErrorCode(AppErrorCode.dropoffCheckInTooFarKept, en),
        en.dropoffJudgementOutOfRange,
      ]) {
        expect(text, contains("check-in was more than 5 km from the order's"));
        expect(text, isNot(contains('You are')));
        expect(text, isNot(contains('tried to check in but')));
      }
      expect(
        localizeErrorCode(AppErrorCode.dropoffCheckInTooLate, en),
        contains('kept for review'),
      );
      for (final code in [
        AppErrorCode.dropoffCheckInNotOpen,
        AppErrorCode.dropoffCheckInNotParty,
        AppErrorCode.dropoffCheckInClosed,
        AppErrorCode.dropoffCheckInNeedsLocation,
      ]) {
        expect(localizeErrorCode(code, en), isNot(en.errorOccurred));
      }
    });

    test('every drop-off text exists in am and om', () {
      for (final locale in const [Locale('am'), Locale('om')]) {
        final l10n = lookupAppLocalizations(locale);
        for (final code in [
          AppErrorCode.checkInPhotoRequired,
          AppErrorCode.dropoffCheckInNotOpen,
          AppErrorCode.dropoffCheckInNotParty,
          AppErrorCode.dropoffCheckInTooFar,
          AppErrorCode.dropoffCheckInTooFarKept,
          AppErrorCode.dropoffCheckInTooLate,
          AppErrorCode.dropoffCheckInClosed,
          AppErrorCode.dropoffCheckInNeedsLocation,
        ]) {
          final text = localizeErrorCode(code, l10n);
          expect(text, isNotEmpty);
          expect(text, isNot(localizeErrorCode(code, en)));
        }
      }
    });
  });
}
