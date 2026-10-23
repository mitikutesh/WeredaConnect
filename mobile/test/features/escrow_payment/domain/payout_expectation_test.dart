import 'package:flutter_test/flutter_test.dart';

import 'package:wereda_connect/core/database/schemas/payment.dart';
import 'package:wereda_connect/features/escrow_payment/domain/payout_expectation.dart';

Payment _payment(
  PaymentStatus status, {
  DateTime? releaseNotBefore,
  bool refundPending = false,
}) {
  final now = DateTime.now().toUtc();
  return Payment()
    ..paymentId = 'esc-1'
    ..deliveryId = 'del-1'
    ..farmerId = 'farmer-001'
    ..buyerId = 'buyer-001'
    ..amount = 1500
    ..commission = 37.5
    ..commissionRate = 0.025
    ..farmerPayout = 1462.5
    ..status = status
    ..refundPending = refundPending
    ..createdAt = now
    ..updatedAt = now
    ..expiresAt = now.add(const Duration(days: 7))
    ..releaseNotBefore = releaseNotBefore
    ..version = 1;
}

/// FRD-13: the expected payout day is shown only while the money is held for
/// the payee and the server sent a date.
void main() {
  final at = DateTime.utc(2026, 10, 26, 12);

  test('an escrowed payment with a date expects its payout then', () {
    expect(
      expectedPayoutAt(_payment(PaymentStatus.escrowed, releaseNotBefore: at)),
      at,
    );
  });

  test('no date (the buyer, or an older server): nothing to show', () {
    expect(expectedPayoutAt(_payment(PaymentStatus.escrowed)), isNull);
  });

  test('a refund on its way: nothing to show', () {
    expect(
      expectedPayoutAt(
        _payment(
          PaymentStatus.escrowed,
          releaseNotBefore: at,
          refundPending: true,
        ),
      ),
      isNull,
    );
  });

  for (final status in PaymentStatus.values.where(
    (s) => s != PaymentStatus.escrowed,
  )) {
    test('${status.name}: nothing to show', () {
      expect(
        expectedPayoutAt(_payment(status, releaseNotBefore: at)),
        isNull,
      );
    });
  }
}
