import 'dart:async';

import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/errors/app_error.dart';
import 'package:wereda_connect/core/models/order.dart';
import 'package:wereda_connect/core/network/api_exception.dart';
import 'package:wereda_connect/features/order/domain/repositories/base_order_repository.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_bloc.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_event.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_state.dart';

import '../../../../helpers/wait_for.dart';

class _MockOrderRepository extends Mock implements BaseOrderRepository {}

Order _order({OrderStatus status = OrderStatus.funded, int version = 1}) =>
    Order()
      ..orderId = 'o-1'
      ..listingId = 'l-1'
      ..buyerId = 'buyer-1'
      ..farmerId = 'farmer-1'
      ..cropType = 'teff'
      ..quantity = 10
      ..pricePerUnit = 85
      ..totalPrice = 850
      ..status = status
      ..createdAt = DateTime(2026, 11, 2)
      ..version = version;

/// ESC-23: the order detail reloads the stored order behind the one on
/// screen once the server's copy has reached the device. That reload shows
/// no loading state and never replaces the order with an error.
void main() {
  late _MockOrderRepository repo;
  late OrderBloc bloc;
  late List<OrderState> states;
  late StreamSubscription<OrderState> sub;
  late int reads;

  setUp(() {
    repo = _MockOrderRepository();
    bloc = OrderBloc(repository: repo);
    states = [];
    reads = 0;
    sub = bloc.stream.listen(states.add);
  });

  tearDown(() async {
    await sub.cancel();
    await bloc.close();
  });

  const quiet = LoadOrderById(orderId: 'o-1', quiet: true);

  /// Waits until the bloc has read the order [count] times and the handler
  /// has had its turn to emit.
  Future<void> settle(int count) async {
    await waitForOrFail(() => reads >= count);
    await Future<void>.delayed(Duration.zero);
  }

  /// Stubs the read of the order, counting the calls.
  void stubRead(Future<Order?> Function() answer) {
    when(() => repo.getOrderById('o-1')).thenAnswer((_) {
      reads++;
      return answer();
    });
  }

  test('a plain load still goes through loading', () async {
    when(() => repo.getOrderById('o-1')).thenAnswer((_) async => _order());

    bloc.add(const LoadOrderById(orderId: 'o-1'));
    await waitForStates(states, 2);

    expect(states.first, isA<OrderLoading>());
    expect(states.last, isA<SingleOrderLoaded>());
  });

  test('a quiet reload emits the order without a loading state', () async {
    when(() => repo.getOrderById('o-1')).thenAnswer(
      (_) async => _order(status: OrderStatus.accepted, version: 2),
    );

    bloc.add(quiet);
    await waitForStates(states, 1);
    await Future<void>.delayed(Duration.zero);

    expect(states, hasLength(1));
    final loaded = states.single as SingleOrderLoaded;
    expect(loaded.order.status, OrderStatus.accepted);
    expect(loaded.order.version, 2);
  });

  test('a quiet reload that finds no order changes nothing', () async {
    stubRead(() async => null);

    bloc.add(quiet);
    await settle(1);

    expect(states, isEmpty);
    expect(bloc.state, isA<OrderInitial>());
  });

  test('a quiet reload that fails changes nothing', () async {
    stubRead(() async => throw const NetworkException());

    bloc.add(quiet);
    await settle(1);

    expect(states, isEmpty);
    expect(bloc.state, isA<OrderInitial>());
  });

  test('a quiet reload never ends the loading state of a running action',
      () async {
    final accepting = Completer<void>();
    when(() => repo.acceptOrder('o-1', 'farmer-1'))
        .thenAnswer((_) => accepting.future);
    stubRead(() async => _order());

    bloc.add(const AcceptOrder(orderId: 'o-1', farmerId: 'farmer-1'));
    await waitForStates(states, 1);
    expect(states.single, isA<OrderLoading>());

    bloc.add(quiet);
    await settle(1);
    expect(states, hasLength(1), reason: 'the action still runs');

    accepting.complete();
    await waitForStates(states, 2);
    expect(states.last, isA<OrderAccepted>());
  });

  test('a plain load of a missing order is still an error', () async {
    when(() => repo.getOrderById('o-1')).thenAnswer((_) async => null);

    bloc.add(const LoadOrderById(orderId: 'o-1'));
    await waitForStates(states, 2);

    expect((states.last as OrderError).code, AppErrorCode.notFound);
  });

  test('quiet is part of the event identity', () {
    expect(quiet, isNot(const LoadOrderById(orderId: 'o-1')));
    expect(quiet.props, ['o-1', true]);
  });
}
