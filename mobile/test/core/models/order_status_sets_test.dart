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
}
