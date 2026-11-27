import 'package:flutter_test/flutter_test.dart';

import 'package:wereda_connect/core/database/schemas/payment.dart';
import 'package:wereda_connect/features/escrow_payment/domain/payout_expectation.dart';

Payment _payment(
  PaymentStatus status, {
  DateTime? releaseNotBefore,
  bool refundPending = false,
  DateTime? expiresAt,
  DateTime? deliveryConfirmedAt,
  double? truckerPayout,
}) {
  final now = DateTime.now().toUtc();
  return Payment()
    ..paymentId = 'esc-1'
    ..deliveryId = 'del-1'
    ..farmerId = 'farmer-001'
    ..buyerId = 'buyer-001'
    ..truckerId = truckerPayout == null ? null : 'trucker-001'
    ..truckerPayout = truckerPayout
    ..amount = 1500
    ..commission = 37.5
    ..commissionRate = 0.025
    ..farmerPayout = 1462.5
    ..status = status
    ..refundPending = refundPending
    ..createdAt = now
    ..updatedAt = now
    ..expiresAt = expiresAt ?? now.add(const Duration(days: 7))
    ..deliveryConfirmedAt = deliveryConfirmedAt
    ..releaseNotBefore = releaseNotBefore
    ..version = 1;
}

/// FRD-13: the expected payout day is shown only while the money is held for
/// the payee and the server sent a date.
void main() {
  final at = DateTime.utc(2026, 10, 26, 12);
  final before = at.subtract(const Duration(hours: 1));

  test('an escrowed payment with a date expects its payout then', () {
    expect(
      expectedPayoutAt(
        _payment(PaymentStatus.escrowed, releaseNotBefore: at),
        now: before,
      ),
      at,
    );
  });

  test('a date that has passed while still escrowed: nothing to show', () {
    final payment = _payment(PaymentStatus.escrowed, releaseNotBefore: at);

    expect(expectedPayoutAt(payment, now: at), isNull);
    expect(
      expectedPayoutAt(payment, now: at.add(const Duration(minutes: 5))),
      isNull,
    );
  });

  test('no date (the buyer, or an older server): nothing to show', () {
    expect(
      expectedPayoutAt(_payment(PaymentStatus.escrowed), now: before),
      isNull,
    );
  });

  test('a refund on its way: nothing to show', () {
    expect(
      expectedPayoutAt(
        _payment(
          PaymentStatus.escrowed,
          releaseNotBefore: at,
          refundPending: true,
        ),
        now: before,
      ),
      isNull,
    );
  });

  test('a confirmed hold past its expiry still expects its payout (FUP-97)',
      () {
    final expired = DateTime.now().toUtc().subtract(const Duration(hours: 1));

    expect(
      expectedPayoutAt(
        _payment(
          PaymentStatus.escrowed,
          releaseNotBefore: at,
          expiresAt: expired,
          deliveryConfirmedAt: expired.subtract(const Duration(days: 1)),
        ),
        now: before,
      ),
      at,
    );
    // Unconfirmed, the same past expiry is a refund on its way.
    expect(
      expectedPayoutAt(
        _payment(
          PaymentStatus.escrowed,
          releaseNotBefore: at,
          expiresAt: expired,
        ),
        now: before,
      ),
      isNull,
    );
  });

  for (final status in PaymentStatus.values.where(
    (s) => s != PaymentStatus.escrowed,
  )) {
    test('${status.name}: nothing to show', () {
      expect(
        expectedPayoutAt(_payment(status, releaseNotBefore: at), now: before),
        isNull,
      );
    });
  }

  /// FUP-113: the order detail shows the date only to the escrow's own
  /// payees, whatever the payment carries.
  group('expectedPayoutFor', () {
    final trucked = _payment(
      PaymentStatus.escrowed,
      releaseNotBefore: at,
      truckerPayout: 300,
    );

    DateTime? payoutFor(Payment payment, String role, String userId) =>
        expectedPayoutFor(payment, role: role, userId: userId, now: before);

    test('the escrow farmer sees the date', () {
      expect(payoutFor(trucked, 'farmer', 'farmer-001'), at);
    });

    test('the escrow trucker with a transport leg sees the date', () {
      expect(payoutFor(trucked, 'trucker', 'trucker-001'), at);
    });

    test('the buyer never does, even on a copy that carries a date', () {
      expect(payoutFor(trucked, 'buyer', 'buyer-001'), isNull);
    });

    test('another farmer or trucker does not', () {
      expect(payoutFor(trucked, 'farmer', 'farmer-002'), isNull);
      expect(payoutFor(trucked, 'trucker', 'trucker-002'), isNull);
    });

    test('a role that does not match the leg does not', () {
      expect(payoutFor(trucked, 'trucker', 'farmer-001'), isNull);
      expect(payoutFor(trucked, 'farmer', 'trucker-001'), isNull);
    });

    test('a trucker without a transport leg does not', () {
      final selfPickup = _payment(
        PaymentStatus.escrowed,
        releaseNotBefore: at,
      )..truckerId = 'trucker-001';
      final zeroLeg = _payment(
        PaymentStatus.escrowed,
        releaseNotBefore: at,
        truckerPayout: 0,
      );

      expect(payoutFor(selfPickup, 'trucker', 'trucker-001'), isNull);
      expect(payoutFor(zeroLeg, 'trucker', 'trucker-001'), isNull);
    });

    test('no signed-in user does not', () {
      final noFarmer = _payment(
        PaymentStatus.escrowed,
        releaseNotBefore: at,
      )..farmerId = '';

      expect(payoutFor(noFarmer, 'farmer', ''), isNull);
    });

    test('the payee rules of expectedPayoutAt still apply', () {
      expect(
        expectedPayoutFor(
          trucked,
          role: 'farmer',
          userId: 'farmer-001',
          now: at,
        ),
        isNull,
      );
      expect(
        payoutFor(
          _payment(PaymentStatus.held, releaseNotBefore: at),
          'farmer',
          'farmer-001',
        ),
        isNull,
      );
    });
  });
}
