import 'package:flutter_test/flutter_test.dart';

import 'package:wereda_connect/core/models/order.dart';
import 'package:wereda_connect/core/models/order_status_sets.dart';

Order _order(OrderStatus status, DateTime? pickupDeadlineAt) => Order()
  ..orderId = 'order-1'
  ..listingId = 'listing-1'
  ..buyerId = 'buyer-1'
  ..farmerId = 'farmer-1'
  ..buyerName = 'Buyer'
  ..farmerName = 'Farmer'
  ..cropType = 'teff'
  ..quantity = 500
  ..pricePerUnit = 45
  ..totalPrice = 22500
  ..status = status
  ..createdAt = DateTime.utc(2026, 10)
  ..version = 1
  ..pickupDeadlineAt = pickupDeadlineAt;

/// ESC-06: when the buyer may cancel an accepted order after a transport failure.
void main() {
  final deadline = DateTime.utc(2026, 10, 12, 8);

  test('an accepted order opens from its pickup deadline on', () {
    final order = _order(OrderStatus.accepted, deadline);

    expect(
      isTransportFailureCancelOpen(
        order,
        deadline.subtract(const Duration(seconds: 1)),
      ),
      isFalse,
    );
    expect(isTransportFailureCancelOpen(order, deadline), isTrue);
    expect(
      isTransportFailureCancelOpen(
        order,
        deadline.add(const Duration(days: 3)),
      ),
      isTrue,
    );
  });

  test('without a deadline, or in any other status, it never opens', () {
    final later = deadline.add(const Duration(days: 30));

    expect(
      isTransportFailureCancelOpen(_order(OrderStatus.accepted, null), later),
      isFalse,
    );
    for (final status in OrderStatus.values.where(
      (s) => s != OrderStatus.accepted,
    )) {
      expect(
        isTransportFailureCancelOpen(_order(status, deadline), later),
        isFalse,
        reason: status.name,
      );
    }
  });

  // ESC-09: the order follows its money after delivered (disbursing, settled).
  test('the goods-arrived statuses are delivered and the money stages after it',
      () {
    expect(deliveredOrderStatuses, {
      OrderStatus.delivered,
      OrderStatus.disbursing,
      OrderStatus.settled,
    });
    expect(
      deliveredOrderStatuses.contains(OrderStatus.disputeResolved),
      isFalse,
    );
    expect(deliveredOrderStatuses.contains(OrderStatus.disputed), isFalse);
  });

  test('the escrow tile still leaves out money that is leaving escrow', () {
    expect(escrowHeldOrderStatuses.contains(OrderStatus.disbursing), isFalse);
    expect(escrowHeldOrderStatuses.contains(OrderStatus.settled), isFalse);
  });

  // ESC-48: when the parties of an order may reach each other.
  group('contact', () {
    Order open({bool? contactOpen = true}) => _order(OrderStatus.accepted, null)
      ..truckerId = 'trucker-1'
      ..contactOpen = contactOpen
      ..buyerPhone = '+251911000001'
      ..farmerPhone = '+251911000002'
      ..truckerPhone = '+251911000003';

    test('opens from the accept and closes when the money goes back', () {
      expect(contactOpenOrderStatuses, {
        // ESC-15: while funded or awaiting transport the farmer's answer decides.
        OrderStatus.funded,
        OrderStatus.awaitingTransport,
        OrderStatus.accepted,
        OrderStatus.pickupConfirmed,
        OrderStatus.inTransit,
        OrderStatus.delivered,
        OrderStatus.disbursing,
        OrderStatus.settled,
        OrderStatus.disputed,
      });
      for (final closed in [
        OrderStatus.created,
        OrderStatus.pendingPayment,
        OrderStatus.paymentSubmitted,
        OrderStatus.paymentFailed,
        OrderStatus.cancelled,
        OrderStatus.rejected,
        OrderStatus.disputeResolved,
        OrderStatus.unknown,
      ]) {
        expect(
          contactOpenOrderStatuses.contains(closed),
          isFalse,
          reason: closed.name,
        );
      }
      expect(
        contactNotYetOpenOrderStatuses.contains(OrderStatus.funded),
        isTrue,
      );
      expect(
        contactNotYetOpenOrderStatuses.contains(OrderStatus.cancelled),
        isFalse,
      );
    });

    test('the server flag decides on an open status', () {
      expect(isOrderContactOpen(open()), isTrue);
      // An admin override refund leaves the status but the server closes it.
      expect(isOrderContactOpen(open(contactOpen: false)), isFalse);
      // Not judged yet (old server, local accept): the status decides.
      expect(isOrderContactOpen(open(contactOpen: null)), isTrue);
    });

    test('ESC-15: a funded order opens at the farmer\'s accept', () {
      final funded = open()..status = OrderStatus.funded;
      // An unstamped funded row counts as the farmer still deciding.
      expect(isOrderContactOpen(funded), isFalse);
      expect(isOrderContactOpen(funded..farmerResponse = 'pending'), isFalse);
      expect(isOrderContactOpen(funded..farmerResponse = 'accepted'), isTrue);
      final awaiting = open()
        ..status = OrderStatus.awaitingTransport
        ..farmerResponse = 'accepted';
      expect(isOrderContactOpen(awaiting), isTrue);
      // The trip was full right after funding: the farmer never accepted.
      expect(isOrderContactOpen(awaiting..farmerResponse = 'pending'), isFalse);
    });

    test('ESC-15: awaiting transport holds the money; unknown counts nowhere',
        () {
      expect(escrowHeldOrderStatuses, contains(OrderStatus.awaitingTransport));
      expect(activeOrderStatuses, contains(OrderStatus.awaitingTransport));
      expect(escrowHeldOrderStatuses, isNot(contains(OrderStatus.unknown)));
      expect(activeOrderStatuses, isNot(contains(OrderStatus.unknown)));
    });

    test('a closed status wins over a stale server flag', () {
      final cancelledLocally = open()..status = OrderStatus.cancelled;
      expect(isOrderContactOpen(cancelledLocally), isFalse);
      expect(orderPartyPhone(cancelledLocally, 'farmer-1'), isNull);
    });

    test('the phone of each party, only while open', () {
      final order = open();
      expect(orderPartyPhone(order, 'buyer-1'), '+251911000001');
      expect(orderPartyPhone(order, 'farmer-1'), '+251911000002');
      expect(orderPartyPhone(order, 'trucker-1'), '+251911000003');
      expect(orderPartyPhone(order, 'stranger'), isNull);
      expect(orderPartyPhone(open(contactOpen: false), 'farmer-1'), isNull);
      expect(orderPartyPhone(order..farmerPhone = '  ', 'farmer-1'), isNull);
    });
  });
}
