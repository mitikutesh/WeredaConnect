import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/errors/app_error.dart';
import 'package:wereda_connect/core/network/api_exception.dart';
import 'package:wereda_connect/features/order/domain/repositories/base_order_repository.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_bloc.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_event.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_state.dart';

class _MockOrderRepository extends Mock implements BaseOrderRepository {}

/// Polls until [states] holds [count] entries (5s cap) instead of sleeping a
/// fixed time; the expects below then report what actually arrived.
Future<void> waitForStates(List<OrderState> states, int count) async {
  final deadline = DateTime.now().add(const Duration(seconds: 5));
  while (states.length < count && DateTime.now().isBefore(deadline)) {
    await Future<void>.delayed(const Duration(milliseconds: 5));
  }
}

/// ESC-12: a refused order action ends in an explained OrderError, never in
/// the success state that pops the screen (web throws from /sync, mobile from
/// the local guards and a failed enqueue).
void main() {
  late _MockOrderRepository repo;

  setUp(() {
    repo = _MockOrderRepository();
  });

  /// Every order action, with the repository call it makes.
  final actions = <String, (OrderEvent, void Function(Object error))>{
    'accept': (
      const AcceptOrder(orderId: 'o-1', farmerId: 'farmer-1'),
      (error) => when(() => repo.acceptOrder(any(), any()))
          .thenAnswer((_) async => throw error),
    ),
    'reject': (
      const RejectOrder(orderId: 'o-1', farmerId: 'farmer-1'),
      (error) => when(
            () => repo.rejectOrder(any(), any(), reason: any(named: 'reason')),
          ).thenAnswer((_) async => throw error),
    ),
    'cancel': (
      const CancelOrder(orderId: 'o-1', userId: 'buyer-1'),
      (error) => when(
            () => repo.cancelOrder(any(), any(), reason: any(named: 'reason')),
          ).thenAnswer((_) async => throw error),
    ),
    'submitPayment': (
      const SubmitPayment(orderId: 'o-1', cbeTransactionId: 'REF-1'),
      (error) => when(
            () => repo.submitPayment(
              any(),
              any(),
              paymentMethod: any(named: 'paymentMethod'),
            ),
          ).thenAnswer((_) async => throw error),
    ),
  };

  Future<List<OrderState>> run(String action, Object error) async {
    final (event, stub) = actions[action]!;
    stub(error);
    final bloc = OrderBloc(repository: repo);
    final states = <OrderState>[];
    final sub = bloc.stream.listen(states.add);
    bloc.add(event);
    await waitForStates(states, 2);
    await Future<void>.delayed(Duration.zero);
    await sub.cancel();
    await bloc.close();
    return states;
  }

  void expectError(List<OrderState> states, AppErrorCode code) {
    expect(states, hasLength(2));
    expect(states.first, isA<OrderLoading>());
    expect(states.last, isA<OrderError>());
    expect((states.last as OrderError).code, code);
  }

  for (final code in [
    'ORDER_VALIDATION',
    'ORDER_UNAUTHORIZED',
    'ORDER_DEADLINE_PASSED',
    'ORDER_CANCEL_NOT_ALLOWED',
    'ORDER_ACTION_NOT_ALLOWED',
    'ORDER_TRIP_NOT_ELIGIBLE',
    'ORDER_TRANSPORT_ATTEMPTS_EXCEEDED',
  ]) {
    group(code, () {
      for (final action in actions.keys) {
        test('$action ends in orderRefused, never success', () async {
          final states = await run(
            action,
            SyncRejectedException('$code: refused'),
          );
          expectError(states, AppErrorCode.orderRefused);
        });
      }
    });
  }

  group('other refusals', () {
    for (final action in actions.keys) {
      test('$action: a version race on web is a conflict', () async {
        expectError(
          await run(action, const SyncConflictException('Version conflict.')),
          AppErrorCode.conflict,
        );
      });

      test('$action: a local guard refusal is orderRefused', () async {
        expectError(
          await run(action, const OrderActionNotAllowedException('no')),
          AppErrorCode.orderRefused,
        );
      });

      test('$action: a missing order is notFound', () async {
        expectError(
          await run(action, const NotFoundException('Order o-1 not found.')),
          AppErrorCode.notFound,
        );
      });
    }

    test('a KYC refusal of the payment keeps its own code', () async {
      expectError(
        await run(
          'submitPayment',
          const SyncRejectedException('ORDER_KYC_REQUIRED: buyer_fayda: x'),
        ),
        AppErrorCode.kycRequired,
      );
    });
  });
}
