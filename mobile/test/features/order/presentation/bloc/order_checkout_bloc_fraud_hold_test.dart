import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/models/order.dart';
import 'package:wereda_connect/core/security/security_events.dart';
import 'package:wereda_connect/features/escrow_payment/data/repositories/escrow_result.dart';
import 'package:wereda_connect/features/escrow_payment/domain/repositories/base_escrow_repository.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_checkout_bloc.dart';

class _MockEscrow extends Mock implements BaseEscrowRepository {}

/// FRD-06: a 403 FRAUD_ACCOUNT_HELD on the order checkout stops with its own
/// issue ("your account is under review"), not "not authorized".
void main() {
  test('FRAUD_ACCOUNT_HELD stops the checkout as account held', () async {
    final escrow = _MockEscrow();
    final order = Order()
      ..orderId = 'order-1'
      ..listingId = 'listing-1'
      ..buyerId = 'buyer-1'
      ..farmerId = 'farmer-1'
      ..cropType = 'teff'
      ..quantity = 100
      ..pricePerUnit = 24.5
      ..totalPrice = 2450
      ..orderTotal = 2448.75
      ..status = OrderStatus.paymentSubmitted
      ..createdAt = DateTime(2026, 10, 9)
      ..version = 2;
    when(() => escrow.openOrderCheckout('order-1')).thenAnswer(
      (_) async => const OrderCheckoutFailed('FRAUD_ACCOUNT_HELD', 403, 'held'),
    );
    final bloc = OrderCheckoutBloc(
      orderId: 'order-1',
      escrow: escrow,
      readServerOrder: (_) async => order,
      securityEvents: SecurityEvents.forTesting(),
      now: () => DateTime(2026, 10, 9, 13),
      monotonic: () => Duration.zero,
    );
    addTearDown(bloc.close);

    bloc.add(const CheckoutStarted());
    await expectLater(
      bloc.stream,
      emitsInOrder([
        isA<CheckoutOpening>(),
        const CheckoutStopped(CheckoutIssue.accountHeld),
      ]),
    );
  });
}
