import 'package:flutter_test/flutter_test.dart';
import 'package:wereda_connect/core/models/order.dart';
import 'package:wereda_connect/core/models/order_status_wire.dart';

void main() {
  group('orderStatusToWire (camelCase -> snake_case)', () {
    test('converts the multi-word statuses', () {
      expect(orderStatusToWire('pendingPayment'), 'pending_payment');
      expect(orderStatusToWire('paymentSubmitted'), 'payment_submitted');
      expect(orderStatusToWire('pickupConfirmed'), 'pickup_confirmed');
      expect(orderStatusToWire('inTransit'), 'in_transit');
      expect(orderStatusToWire('paymentFailed'), 'payment_failed');
      expect(orderStatusToWire('disputeResolved'), 'dispute_resolved');
    });

    test('leaves single-word statuses unchanged', () {
      expect(orderStatusToWire('created'), 'created');
      expect(orderStatusToWire('funded'), 'funded');
      expect(orderStatusToWire('cancelled'), 'cancelled');
    });
  });

  group('orderStatusFromWire (snake_case -> camelCase)', () {
    test('converts the multi-word statuses', () {
      expect(orderStatusFromWire('pending_payment'), 'pendingPayment');
      expect(orderStatusFromWire('payment_submitted'), 'paymentSubmitted');
      expect(orderStatusFromWire('in_transit'), 'inTransit');
      expect(orderStatusFromWire('dispute_resolved'), 'disputeResolved');
    });

    test('leaves single-word statuses unchanged', () {
      expect(orderStatusFromWire('created'), 'created');
      expect(orderStatusFromWire('delivered'), 'delivered');
    });
  });

  test('every OrderStatus round-trips through the wire form', () {
    for (final status in OrderStatus.values) {
      expect(
        orderStatusFromWire(orderStatusToWire(status.name)),
        status.name,
        reason: 'round-trip changed ${status.name}',
      );
    }
  });
}
