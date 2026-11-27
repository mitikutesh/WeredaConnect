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

const _fix = HandoffCheckInFix(latitude: 9.03, longitude: 38.74, accuracyM: 20);

/// ESC-36 (FUP-336): the order bloc carries the trucker's drop-off check-in
/// (with the arrival photo) to the repository, says when it was saved, and
/// explains each refusal with its drop-off code; a refusal from the server
/// (web) reads the order again.
void main() {
  late _MockOrderRepository repo;

  setUpAll(() => registerFallbackValue(_fix));

  setUp(() {
    repo = _MockOrderRepository();
    when(() => repo.getOrderById('o-1')).thenAnswer(
      (_) async => Order()
        ..orderId = 'o-1'
        ..dropoffJudgement = 'pending',
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

  const arrived = CheckInAtDropoff(
    orderId: 'o-1',
    userId: 'trucker-1',
    deliveryId: 'd-1',
    fix: _fix,
    arrivalPhotoRef: '/data/photos/d-1/arrival-1.webp',
  );

  void answer(Future<bool> Function() body) => when(
        () => repo.checkInAtDropoff(
          any(),
          any(),
          deliveryId: any(named: 'deliveryId'),
          fix: any(named: 'fix'),
          arrivalPhotoRef: any(named: 'arrivalPhotoRef'),
        ),
      ).thenAnswer((_) => body());

  test('a sent or queued check-in ends in CheckInSaved', () async {
    answer(() async => true);

    final states = await run(arrived, 2);

    expect(states.first, isA<OrderLoading>());
    expect(states.last, const CheckInSaved(kind: 'arrived_dropoff'));
    verify(
      () => repo.checkInAtDropoff(
        'o-1',
        'trucker-1',
        deliveryId: 'd-1',
        fix: _fix,
        arrivalPhotoRef: '/data/photos/d-1/arrival-1.webp',
      ),
    ).called(1);
  });

  test('a check-in already given says so', () async {
    answer(() async => false);

    final states = await run(arrived, 2);

    expect(
      states.last,
      const CheckInSaved(kind: 'arrived_dropoff', alreadyGiven: true),
    );
  });

  for (final (message, code) in [
    ('CHECKIN_PHOTO_REQUIRED: take it.', AppErrorCode.checkInPhotoRequired),
    ('CHECKIN_TOO_FAR: 7.2 km.', AppErrorCode.dropoffCheckInTooFar),
    (
      'CHECKIN_TOO_FAR: 7.2 km. The attempt was kept for review.',
      AppErrorCode.dropoffCheckInTooFarKept
    ),
    ('CHECKIN_TOO_LATE: kept for review.', AppErrorCode.dropoffCheckInTooLate),
    ('CHECKIN_NOT_OPEN: off the road.', AppErrorCode.dropoffCheckInNotOpen),
    ('CHECKIN_NOT_PARTY: not yours.', AppErrorCode.dropoffCheckInNotParty),
    ('CHECKIN_VALIDATION: bad.', AppErrorCode.checkInNotSaved),
  ]) {
    test('$message: its own error, then the order again', () async {
      answer(() async => throw SyncRejectedException(message));

      final states = await run(arrived, 3);

      expect(states[0], isA<OrderLoading>());
      expect((states[1] as OrderError).code, code);
      expect(
        (states[2] as SingleOrderLoaded).order.dropoffJudgement,
        'pending',
      );
    });
  }

  for (final code in [
    AppErrorCode.dropoffCheckInNotParty,
    AppErrorCode.dropoffCheckInClosed,
    AppErrorCode.dropoffCheckInNotOpen,
    AppErrorCode.dropoffCheckInNeedsLocation,
  ]) {
    test('the local guard (${code.name}) refuses without a read', () async {
      answer(() async => throw CheckInNotAllowedException('no', code));

      final states = await run(arrived, 2);

      expect((states.last as OrderError).code, code);
      verifyNever(() => repo.getOrderById(any()));
    });
  }
}
