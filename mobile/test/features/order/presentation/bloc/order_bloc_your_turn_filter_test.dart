import 'dart:async';

import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/models/order.dart';
import 'package:wereda_connect/core/models/order_commitment.dart';
import 'package:wereda_connect/features/order/domain/repositories/base_order_repository.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_bloc.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_event.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_state.dart';

import '../../../../helpers/wait_for.dart';

class _MockOrderRepository extends Mock implements BaseOrderRepository {}

Order _order(String id, {String? farmerResponse}) => Order()
  ..orderId = id
  ..listingId = 'l-1'
  ..buyerId = 'buyer-1'
  ..farmerId = 'farmer-1'
  ..cropType = 'teff'
  ..quantity = 10
  ..pricePerUnit = 85
  ..totalPrice = 850
  ..status = OrderStatus.funded
  ..farmerResponse = farmerResponse
  ..createdAt = DateTime(2026, 11, 2)
  ..version = 1;

/// The farmer's "Waiting for you" filter is what Home's Pending Orders tile
/// counts: funded orders still waiting for the farmer's answer, not every
/// funded order and not the `pending` (unpaid) status.
void main() {
  late _MockOrderRepository repo;
  late OrderBloc bloc;
  late List<OrderState> states;
  late StreamSubscription<OrderState> sub;

  setUp(() {
    repo = _MockOrderRepository();
    bloc = OrderBloc(repository: repo);
    states = [];
    sub = bloc.stream.listen(states.add);
  });

  tearDown(() async {
    await sub.cancel();
    await bloc.close();
  });

  test('reads the funded orders and keeps those the farmer must answer',
      () async {
    when(
      () => repo.getOrdersByUser(
        'farmer-1',
        role: 'farmer',
        statusFilter: 'funded',
      ),
    ).thenAnswer(
      (_) async => [
        _order('waiting', farmerResponse: CommitmentResponse.pending),
        _order('unstamped'),
        _order('answered', farmerResponse: CommitmentResponse.accepted),
      ],
    );

    bloc.add(
      const FilterOrders(
        userId: 'farmer-1',
        role: 'farmer',
        statusFilter: ordersFilterYourTurn,
      ),
    );
    await waitForOrFail(() => states.whereType<OrdersLoaded>().isNotEmpty);

    final loaded = states.whereType<OrdersLoaded>().single;
    expect(
      loaded.orders.map((o) => o.orderId),
      ['waiting', 'unstamped'],
    );
    expect(loaded.statusFilter, ordersFilterYourTurn);
    verifyNever(
      () => repo.getOrdersByUser(
        any(),
        role: any(named: 'role'),
        statusFilter: ordersFilterYourTurn,
      ),
    );
  });
}
