import 'package:flutter_test/flutter_test.dart';

import 'package:wereda_connect/core/database/schemas/payment.dart' as isar;
import 'package:wereda_connect/core/models/payment.dart';
import 'package:wereda_connect/core/models/payment_status_wire.dart';
import 'package:wereda_connect/core/models/web/payment.dart' as web;

/// FRD-06: the escrow status is an open set (escrow-protocol.md 1.7.0): a
/// value the app does not know never throws and never reads as a state that
/// offers actions.
void main() {
  group('paymentStatusFromWire', () {
    for (final status in PaymentStatus.values) {
      if (status == PaymentStatus.unknown) continue;
      test('${status.name} reads as itself', () {
        expect(paymentStatusFromWire(status.name), status);
      });
    }

    test('held (FRD-03) reads as held', () {
      expect(paymentStatusFromWire('held'), PaymentStatus.held);
    });

    test('dispute_resolved reads as disputed: the money is still frozen', () {
      expect(paymentStatusFromWire('dispute_resolved'), PaymentStatus.disputed);
    });

    for (final wire in ['frozen_for_audit', 'HELD', '', null]) {
      test('"$wire" reads as unknown, never initiated', () {
        expect(paymentStatusFromWire(wire), PaymentStatus.unknown);
      });
    }
  });

  test('the Isar and web enums carry the same values, appended in order', () {
    const names = [
      'initiated',
      'escrowed',
      'released',
      'completed',
      'cancelled',
      'disputed',
      'refunded',
      'held',
      'unknown',
    ];
    // Isar stores the name, so earlier values keep their place.
    expect(isar.PaymentStatus.values.map((s) => s.name).toList(), names);
    expect(web.PaymentStatus.values.map((s) => s.name).toList(), names);
  });
}
