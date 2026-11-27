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
    // SyncFunctionTests: cropCost 2275, transport 60, default rate 0.05.
    const pricing = OrderPricing(cropCost: 2275, transportCost: 60);

    expect(pricing.commissionAmount, 113.75);
    expect(pricing.orderTotal, 2448.75);
  });

  group('santim rounding (SIM-30)', () {
    test('170 kg x 18.99 on a 255.55 trip matches the server to the santim',
        () {
      // SyncFunctionTests.OrderCreate_FloatNoise_...: in doubles the crop is
      // 3228.2999999999997, the commission 161.41499999999999 and the
      // transport 434.43499999999995; exact they are 3228.30, 161.415 and
      // 434.435, which the server rounds half away from zero.
      const pricing = OrderPricing(
        cropCost: 170 * 18.99,
        transportCost: 170 / 100 * 255.55,
      );

      expect(pricing.cropCostEtb, 3228.30);
      expect(pricing.transportCostEtb, 434.44);
      expect(pricing.commissionAmount, 161.42);
      expect(pricing.orderTotal, 3824.16);
      expect(pricing.orderTotal.toString(), '3824.16');
    });

    test('19 ETB of crop gives a 0.95 commission, not 0.9500000000000001', () {
      const pricing = OrderPricing(cropCost: 19);

      expect(pricing.commissionAmount.toString(), '0.95');
      expect(pricing.orderTotal, 19.95);
    });

    test('the legs always add up to the total', () {
      for (final (kg, price, perQuintal) in [
        (1.7, 18.99, 255.55),
        (33.0, 18.99, 120.0),
        (2.0, 21.15, 99.99),
        (1234.5, 12.34, 310.1),
      ]) {
        final pricing = OrderPricing(
          cropCost: kg * price,
          transportCost: kg / 100 * perQuintal,
        );
        final santim = (pricing.cropCostEtb * 100).round() +
            (pricing.transportCostEtb * 100).round() +
            (pricing.commissionAmount * 100).round();
        expect((pricing.orderTotal * 100).round(), santim, reason: '$kg kg');
      }
    });

    test('roundEtb takes halves away from zero after dropping float noise', () {
      expect(roundEtb(0.145), 0.15); // 0.14499999999999999 in doubles
      expect(roundEtb(1.035), 1.04);
      expect(roundEtb(2 * 1.45 * 0.05), 0.15);
      expect(roundEtb(2.114), 2.11);
      expect(roundEtb(-0.145), -0.15);
      expect(roundEtb(0), 0);
    });
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
