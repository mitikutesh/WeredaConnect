import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/errors/app_error.dart';
import 'package:wereda_connect/core/models/order.dart';
import 'package:wereda_connect/core/models/order_check_in.dart';
import 'package:wereda_connect/core/network/api_exception.dart';
import 'package:wereda_connect/features/order/domain/repositories/base_order_repository.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_bloc.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_event.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_state.dart';

import '../../../../helpers/wait_for.dart';

class _MockOrderRepository extends Mock implements BaseOrderRepository {}

const _fix = HandoffCheckInFix(latitude: 8.75, longitude: 38.97, accuracyM: 20);

/// ESC-34 (FUP-285): the order bloc carries a check-in to the repository,
/// says when it was saved, and explains each refusal with its own code; a
/// refusal from the server (web) reads the order again so a late check-in
/// kept for review shows.
void main() {
  late _MockOrderRepository repo;

  setUpAll(() => registerFallbackValue(_fix));

  setUp(() {
    repo = _MockOrderRepository();
    when(() => repo.getOrderById('o-1')).thenAnswer(
      (_) async => Order()
        ..orderId = 'o-1'
        ..pickupJudgement = 'trucker_no_show',
    );
  });

  Future<List<OrderState>> run(OrderEvent event, int count) async {
    final bloc = OrderBloc(repository: repo);
    final states = <OrderState>[];
    final sub = bloc.stream.listen(states.add);
    bloc.add(event);
    await waitForStates(states, count);
    await Future<void>.delayed(Duration.zero);
    await sub.cancel();
    await bloc.close();
    return states;
  }

  const arrival = CheckInAtPickup(
    orderId: 'o-1',
    userId: 'trucker-1',
    kind: CheckInKind.arrivedPickup,
    fix: _fix,
  );

  void answer(Future<void> Function() body) => when(
        () => repo.checkIn(
          any(),
          any(),
          kind: any(named: 'kind'),
          fix: any(named: 'fix'),
        ),
      ).thenAnswer((_) => body());

  test('a sent or queued check-in ends in CheckInSaved', () async {
    answer(() async {});

    final states = await run(arrival, 2);

    expect(states.first, isA<OrderLoading>());
    expect(states.last, const CheckInSaved(kind: 'arrived_pickup'));
    verify(
      () => repo.checkIn(
        'o-1',
        'trucker-1',
        kind: 'arrived_pickup',
        fix: _fix,
      ),
    ).called(1);
  });

  for (final (message, code) in [
    ('CHECKIN_VALIDATION: bad.', AppErrorCode.checkInNotSaved),
    ('CHECKIN_NOT_PARTY: not yours.', AppErrorCode.checkInNotParty),
    ('CHECKIN_NOT_OPEN: closed.', AppErrorCode.checkInNotOpen),
    ('CHECKIN_TOO_EARLY: opens later.', AppErrorCode.checkInTooEarly),
    ('CHECKIN_TOO_FAR: 7 km.', AppErrorCode.checkInTooFar),
    ('CHECKIN_TOO_LATE: kept for review.', AppErrorCode.checkInTooLate),
    (
      "Unsupported entity type: 'handoff_check_in'.",
      AppErrorCode.checkInNotOpen
    ),
  ]) {
    test('${message.split(':').first}: its own error, then the order again',
        () async {
      answer(() async => throw SyncRejectedException(message));

      final states = await run(arrival, 3);

      expect(states[0], isA<OrderLoading>());
      expect((states[1] as OrderError).code, code);
      expect(
        (states[2] as SingleOrderLoaded).order.pickupJudgement,
        'trucker_no_show',
      );
    });
  }

  test('a local guard refuses without a read', () async {
    answer(
      () async => throw const OrderActionNotAllowedException('no fix'),
    );

    final states = await run(arrival, 2);

    expect((states.last as OrderError).code, AppErrorCode.orderRefused);
    verifyNever(() => repo.getOrderById(any()));
  });
}
