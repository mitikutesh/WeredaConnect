import 'package:flutter_test/flutter_test.dart';

import 'package:wereda_connect/core/models/order_pricing.dart';

/// [OrderPricing] is the client copy of the server's order money formula
/// (SyncFunction order create, specs/contracts/order-lifecycle.md). Previews
/// built on it must show exactly what the server charges.
void main() {
  test('default rate is 0.05, the server default', () {
    expect(defaultOrderCommissionRate, 0.05);
    expect(const OrderPricing(cropCost: 1).commissionRate, 0.05);
  });

  test('commission is charged on the crop cost only (SIM-53)', () {
    // The SIM-53 order: 17000 ETB of crop plus 500 ETB of transport.
    const pricing = OrderPricing(cropCost: 17000, transportCost: 500);

    expect(pricing.commissionAmount, 850);
    expect(pricing.orderTotal, 18350);
    expect(
      pricing.orderTotal,
      isNot(18375),
      reason: 'commission on crop plus transport is not what the server '
          'charges',
    );
  });

  test('no trip: total is crop plus 5% commission', () {
    const pricing = OrderPricing(cropCost: 1000);

    expect(pricing.transportCost, 0);
    expect(pricing.commissionAmount, 50);
    expect(pricing.orderTotal, 1050);
  });

  test('mirrors the server create test numbers', () {
    // SyncFunctionTests: cropCost 2275, transport 60, default rate 0.05. Same
    // expressions in the same order as the server, so the doubles are equal.
    const pricing = OrderPricing(cropCost: 2275, transportCost: 60);

    expect(pricing.commissionAmount, 2275 * 0.05);
    expect(pricing.orderTotal, 2275 + 60 + 2275 * 0.05);
    expect(pricing.orderTotal, closeTo(2448.75, 1e-9));
  });

  test('a custom rate passes through', () {
    const pricing = OrderPricing(
      cropCost: 1000,
      transportCost: 200,
      commissionRate: 0.1,
    );

    expect(pricing.commissionAmount, 100);
    expect(pricing.orderTotal, 1300);
  });
}
