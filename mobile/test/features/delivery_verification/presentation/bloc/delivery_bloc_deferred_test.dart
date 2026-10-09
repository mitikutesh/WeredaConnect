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
}
