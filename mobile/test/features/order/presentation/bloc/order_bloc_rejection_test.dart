import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/errors/app_error.dart';
import 'package:wereda_connect/core/network/api_client.dart';
import 'package:wereda_connect/core/network/api_exception.dart';
import 'package:wereda_connect/features/order/data/repositories/web_order_repository.dart';
import 'package:wereda_connect/features/order/domain/repositories/base_order_repository.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_bloc.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_event.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_state.dart';

import '../../../../helpers/wait_for.dart';

class _MockOrderRepository extends Mock implements BaseOrderRepository {}

class _MockApiClient extends Mock implements ApiClient {}

const _createOrder = CreateOrder(
  listingId: 'crop-1',
  buyerId: 'buyer-1',
  farmerId: 'farmer-1',
  buyerName: 'Buyer',
  farmerName: 'Farmer',
  cropType: 'teff',
  quantity: 5,
  pricePerUnit: 60,
  tripId: 'trip-1',
  truckerId: 'trucker-1',
  truckerName: 'Trucker',
  transportCost: 100,
);

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
    'ORDER_ACTION_NOT_ALLOWED',
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

  // ESC-06: the cancel refusal explains when an accepted order can be cancelled.
  group('ORDER_CANCEL_NOT_ALLOWED', () {
    for (final action in actions.keys) {
      test('$action ends in orderCancelNotAllowed, never success', () async {
        final states = await run(
          action,
          const SyncRejectedException('ORDER_CANCEL_NOT_ALLOWED: refused'),
        );
        expectError(states, AppErrorCode.orderCancelNotAllowed);
      });
    }

    test('the local cancel guard reads the same', () async {
      expectError(
        await run('cancel', const OrderCancelNotAllowedException('not yet')),
        AppErrorCode.orderCancelNotAllowed,
      );
    });
  });

  // ESC-12 part B (sync-protocol.md 1.17.0): the spec section 8 codes say
  // what happened instead of the generic refusal (web throws them from /sync).
  for (final (code, expected) in [
    ('ORDER_DEADLINE_PASSED', AppErrorCode.deadlinePassed),
    ('ORDER_TRIP_NOT_ELIGIBLE', AppErrorCode.tripNotEligible),
    (
      'ORDER_TRANSPORT_ATTEMPTS_EXCEEDED',
      AppErrorCode.transportAttemptsExceeded,
    ),
    ('TRIP_CAPACITY_EXCEEDED', AppErrorCode.tripCapacityExceeded),
    ('REPUTATION_RESTRICTED', AppErrorCode.reputationRestricted),
    ('APP_UPDATE_REQUIRED', AppErrorCode.appUpdateRequired),
  ]) {
    group(code, () {
      for (final action in actions.keys) {
        test('$action ends in ${expected.name}, never success', () async {
          final states = await run(
            action,
            SyncRejectedException('$code: refused'),
          );
          expectError(states, expected);
        });
      }
    });
  }

  group('a refused create (buyer, web)', () {
    Future<List<OrderState>> createWith(BaseOrderRepository repository) async {
      final bloc = OrderBloc(repository: repository);
      final states = <OrderState>[];
      final sub = bloc.stream.listen(states.add);
      bloc.add(_createOrder);
      await waitForStates(states, 2);
      await Future<void>.delayed(Duration.zero);
      await sub.cancel();
      await bloc.close();
      return states;
    }

    Future<List<OrderState>> create(Object error) async {
      when(
        () => repo.createOrder(
          listingId: any(named: 'listingId'),
          buyerId: any(named: 'buyerId'),
          farmerId: any(named: 'farmerId'),
          buyerName: any(named: 'buyerName'),
          farmerName: any(named: 'farmerName'),
          cropType: any(named: 'cropType'),
          quantity: any(named: 'quantity'),
          pricePerUnit: any(named: 'pricePerUnit'),
          tripId: any(named: 'tripId'),
          truckerId: any(named: 'truckerId'),
          truckerName: any(named: 'truckerName'),
          transportCost: any(named: 'transportCost'),
          commissionRate: any(named: 'commissionRate'),
        ),
      ).thenAnswer((_) async => throw error);
      return createWith(repo);
    }

    // Spec A7: on a create the restricted party is the farmer or trucker.
    test('REPUTATION_RESTRICTED names the owner, not the buyer', () async {
      expectError(
        await create(
          const SyncRejectedException('REPUTATION_RESTRICTED: owner'),
        ),
        AppErrorCode.counterpartyRestricted,
      );
    });

    // The whole web chain: /sync refuses the create, the real repository
    // throws from ensureSyncSucceeded, the bloc names the owner.
    test(
        'through WebOrderRepository: REPUTATION_RESTRICTED names the owner, '
        'nothing is read after', () async {
      final api = _MockApiClient();
      when(
        () => api.post<Map<String, dynamic>>(
          '/sync',
          data: any<dynamic>(named: 'data'),
        ),
      ).thenAnswer(
        (_) async => {
          'results': [
            {
              'status': 'error',
              'message': 'REPUTATION_RESTRICTED: The listing owner cannot '
                  'take new orders.',
            },
          ],
        },
      );

      expectError(
        await createWith(WebOrderRepository(api: api)),
        AppErrorCode.counterpartyRestricted,
      );
      verify(
        () => api.post<Map<String, dynamic>>(
          '/sync',
          data: any<dynamic>(named: 'data'),
        ),
      ).called(1);
      verifyNoMoreInteractions(api);
    });

    for (final (code, expected) in [
      ('TRIP_CAPACITY_EXCEEDED', AppErrorCode.tripCapacityExceeded),
      ('ORDER_TRIP_NOT_ELIGIBLE', AppErrorCode.tripNotEligible),
      ('APP_UPDATE_REQUIRED', AppErrorCode.appUpdateRequired),
      ('ORDER_VALIDATION', AppErrorCode.orderRefused),
    ]) {
      test('$code ends in ${expected.name}, never OrderCreated', () async {
        expectError(
          await create(SyncRejectedException('$code: refused')),
          expected,
        );
      });
    }
  });

  // FUP-20: a live bank transfer is refused; the buyer is told to pay with
  // Telebirr instead of seeing the generic refusal.
  group('ORDER_PAYMENT_METHOD_UNAVAILABLE', () {
    test('submitPayment ends in paymentMethodUnavailable, never success',
        () async {
      final states = await run(
        'submitPayment',
        const SyncRejectedException(
          'ORDER_PAYMENT_METHOD_UNAVAILABLE: Bank transfer (cbe_transfer) is '
          'not available yet. Pay with Telebirr.',
        ),
      );
      expectError(states, AppErrorCode.paymentMethodUnavailable);
    });
  });

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
