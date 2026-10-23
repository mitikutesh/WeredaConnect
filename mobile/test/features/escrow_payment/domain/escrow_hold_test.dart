import 'package:flutter_test/flutter_test.dart';

import 'package:wereda_connect/core/models/order.dart';
import 'package:wereda_connect/core/models/payment.dart';
import 'package:wereda_connect/features/escrow_payment/domain/escrow_hold.dart';

/// FRD-06: which escrow and order statuses the hold rules treat as what.
void main() {
  test('the order escrow id is deterministic', () {
    expect(orderEscrowId('ord-1'), 'escrow-order-ord-1');
  });

  test('only statuses that can hold money show Under Review', () {
    expect(orderHoldableStatuses, {
      OrderStatus.funded,
      OrderStatus.accepted,
      OrderStatus.pickupConfirmed,
      OrderStatus.inTransit,
      OrderStatus.delivered,
      OrderStatus.disputed,
      OrderStatus.disputeResolved,
      OrderStatus.cancelled,
      OrderStatus.rejected,
    });
    // No money yet, or the money already moved on.
    for (final status in const [
      OrderStatus.created,
      OrderStatus.pendingPayment,
      OrderStatus.paymentSubmitted,
      OrderStatus.paymentFailed,
      OrderStatus.disbursing,
      OrderStatus.settled,
    ]) {
      expect(orderHoldableStatuses, isNot(contains(status)), reason: '$status');
    }
  });

  group('escrowAlreadyPaid (no second "Pay for delivery")', () {
    const paid = {
      PaymentStatus.escrowed,
      PaymentStatus.released,
      PaymentStatus.completed,
      PaymentStatus.disputed,
      PaymentStatus.held,
      PaymentStatus.unknown,
    };
    for (final status in PaymentStatus.values) {
      test('${status.name}: ${paid.contains(status) ? 'paid' : 'not paid'}',
          () {
        expect(escrowAlreadyPaid(status), paid.contains(status));
      });
    }
  });
}
