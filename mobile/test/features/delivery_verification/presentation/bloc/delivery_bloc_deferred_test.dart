import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/network/api_exception.dart';
import 'package:wereda_connect/features/delivery_verification/data/delivery_validation_exception.dart';
import 'package:wereda_connect/features/delivery_verification/domain/repositories/base_delivery_repository.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/bloc/delivery_bloc.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/bloc/delivery_event.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/bloc/delivery_state.dart';

class _MockRepository extends Mock implements BaseDeliveryRepository {}

void main() {
  // Web has no sync queue: a pickup the server defers (farmer QR nonce not
  // synced) must surface as an error, not as PickupConfirmed.
  test('a deferred pickup emits the QR-pending error, not PickupConfirmed',
      () async {
    final repository = _MockRepository();
    when(
      () => repository.confirmPickup(
        deliveryId: any(named: 'deliveryId'),
        truckerId: any(named: 'truckerId'),
        gpsPickup: any(named: 'gpsPickup'),
        photoRef: any(named: 'photoRef'),
        scannedQrNonce: any(named: 'scannedQrNonce'),
      ),
    ).thenThrow(
      const SyncDeferredException('DELIVERY_NONCE_PENDING: Waiting.'),
    );
    final bloc = DeliveryBloc(repository: repository);
    addTearDown(bloc.close);

    final states = <DeliveryState>[];
    final sub = bloc.stream.listen(states.add);
    addTearDown(sub.cancel);

    bloc.add(
      const ConfirmPickup(deliveryId: 'del-1', truckerId: 'trucker-1'),
    );
    await Future<void>.delayed(Duration.zero);
    await Future<void>.delayed(Duration.zero);

    expect(states.whereType<PickupConfirmed>(), isEmpty);
    final error = states.whereType<DeliveryError>().single;
    expect(error.code, DeliveryErrorCode.qrPending);
  });

  // ESC-08: web sends the cancel straight to /sync, so the server's refusal
  // after pickup reaches the bloc and must get its own message.
  test('a cancel refused after pickup emits the cancel-not-allowed error',
      () async {
    final repository = _MockRepository();
    when(
      () => repository.cancelDelivery(
        deliveryId: any(named: 'deliveryId'),
        userId: any(named: 'userId'),
      ),
    ).thenThrow(
      const SyncRejectedException(
        'DELIVERY_CANCEL_NOT_ALLOWED: A delivery can be cancelled only before '
        'pickup (status: pickedUp).',
      ),
    );
    final bloc = DeliveryBloc(repository: repository);
    addTearDown(bloc.close);

    final done = expectLater(
      bloc.stream,
      emitsThrough(
        isA<DeliveryError>().having(
          (e) => e.code,
          'code',
          DeliveryErrorCode.cancelNotAllowed,
        ),
      ),
    );
    bloc.add(
      const CancelDelivery(
        deliveryId: 'del-1',
        userId: 'farmer-1',
        tripId: 'trip-1',
      ),
    );
    await done;
  });

  // ESC-07: web sends the dispute straight to /sync too: a dispute the server
  // could not freeze the payment for, or one after the 48-hour window, gets
  // its own message instead of the generic error.
  for (final (message, code) in [
    (
      'DELIVERY_DISPUTE_REFUSED: The payment for this delivery is being '
          'settled, so it can no longer be disputed. Contact support.',
      DeliveryErrorCode.disputeRefused,
    ),
    (
      'DELIVERY_DISPUTE_EXPIRED: Dispute window (48 hours) has expired.',
      DeliveryErrorCode.disputeExpired,
    ),
  ]) {
    test('a refused dispute emits $code', () async {
      final repository = _MockRepository();
      when(
        () => repository.raiseDispute(
          deliveryId: any(named: 'deliveryId'),
          userId: any(named: 'userId'),
          reason: any(named: 'reason'),
        ),
      ).thenThrow(SyncRejectedException(message));
      final bloc = DeliveryBloc(repository: repository);
      addTearDown(bloc.close);

      final done = expectLater(
        bloc.stream,
        emitsThrough(
          isA<DeliveryError>().having((e) => e.code, 'code', code),
        ),
      );
      bloc.add(
        const RaiseDispute(
          deliveryId: 'del-1',
          userId: 'buyer-1',
          tripId: 'trip-1',
          reason: 'wet bags',
        ),
      );
      await done;
    });
  }
}
