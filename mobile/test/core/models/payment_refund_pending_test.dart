import 'package:flutter_test/flutter_test.dart';

import 'package:wereda_connect/core/database/schemas/payment.dart' as isar;
import 'package:wereda_connect/core/models/web/payment.dart' as web;

/// SIM-20 and FUP-97: the client's own reading of "Refund pending". The
/// server flag wins; without it an escrowed hold past its expiry is on its
/// way back to the buyer, unless its delivery was confirmed: then the money
/// waits for its payout and the refund sweep never takes it.
void main() {
  final past = DateTime.now().toUtc().subtract(const Duration(hours: 1));
  final future = DateTime.now().toUtc().add(const Duration(days: 1));

  isar.Payment isarPayment({
    isar.PaymentStatus status = isar.PaymentStatus.escrowed,
    bool refundPending = false,
    DateTime? expiresAt,
    DateTime? deliveryConfirmedAt,
  }) =>
      isar.Payment()
        ..status = status
        ..refundPending = refundPending
        ..expiresAt = expiresAt
        ..deliveryConfirmedAt = deliveryConfirmedAt;

  web.Payment webPayment({
    web.PaymentStatus status = web.PaymentStatus.escrowed,
    bool refundPending = false,
    DateTime? expiresAt,
    DateTime? deliveryConfirmedAt,
  }) =>
      web.Payment()
        ..status = status
        ..refundPending = refundPending
        ..expiresAt = expiresAt
        ..deliveryConfirmedAt = deliveryConfirmedAt;

  group('isRefundPending on the Isar model (mobile)', () {
    test('an unconfirmed hold past its expiry is pending', () {
      expect(isarPayment(expiresAt: past).isRefundPending, isTrue);
    });

    test('a confirmed hold past its expiry waits for its payout', () {
      expect(
        isarPayment(expiresAt: past, deliveryConfirmedAt: past).isRefundPending,
        isFalse,
      );
    });

    test('the server flag still wins on a confirmed hold', () {
      expect(
        isarPayment(
          refundPending: true,
          expiresAt: past,
          deliveryConfirmedAt: past,
        ).isRefundPending,
        isTrue,
      );
    });

    test('a hold before its expiry is not pending', () {
      expect(isarPayment(expiresAt: future).isRefundPending, isFalse);
    });

    test('money no longer held is never pending', () {
      expect(
        isarPayment(status: isar.PaymentStatus.released, expiresAt: past)
            .isRefundPending,
        isFalse,
      );
    });
  });

  group('isRefundPending on the web model', () {
    test('an unconfirmed hold past its expiry is pending', () {
      expect(webPayment(expiresAt: past).isRefundPending, isTrue);
    });

    test('a confirmed hold past its expiry waits for its payout', () {
      expect(
        webPayment(expiresAt: past, deliveryConfirmedAt: past).isRefundPending,
        isFalse,
      );
    });

    test('the server flag still wins on a confirmed hold', () {
      expect(
        webPayment(
          refundPending: true,
          expiresAt: past,
          deliveryConfirmedAt: past,
        ).isRefundPending,
        isTrue,
      );
    });

    test('a hold before its expiry is not pending', () {
      expect(webPayment(expiresAt: future).isRefundPending, isFalse);
    });

    test('money no longer held is never pending', () {
      expect(
        webPayment(status: web.PaymentStatus.released, expiresAt: past)
            .isRefundPending,
        isFalse,
      );
    });
  });
}
