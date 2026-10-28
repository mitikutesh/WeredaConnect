import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/network/api_exception.dart';
import 'package:wereda_connect/features/delivery_verification/domain/repositories/base_delivery_repository.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/bloc/delivery_bloc.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/bloc/delivery_event.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/bloc/delivery_state.dart';
import 'package:wereda_connect/features/live_trip/domain/scan_check_in_sink.dart';

class _MockRepository extends Mock implements BaseDeliveryRepository {}

class _Sink implements ScanCheckInSink {
  final List<String> scans = [];

  @override
  Future<void> onScan(String tripId) async => scans.add(tripId);
}

/// LT-08: a successful pickup or drop-off scan tells the check-in engine.
void main() {
  late _MockRepository repository;
  late _Sink sink;
  late DeliveryBloc bloc;

  setUp(() {
    repository = _MockRepository();
    sink = _Sink();
    bloc = DeliveryBloc(repository: repository, scanCheckIns: sink);
    when(
      () => repository.confirmPickup(
        deliveryId: any(named: 'deliveryId'),
        truckerId: any(named: 'truckerId'),
        gpsPickup: any(named: 'gpsPickup'),
        photoRef: any(named: 'photoRef'),
        scannedQrNonce: any(named: 'scannedQrNonce'),
      ),
    ).thenAnswer((_) async {});
    when(
      () => repository.confirmDelivery(
        deliveryId: any(named: 'deliveryId'),
        buyerId: any(named: 'buyerId'),
        gpsDropoff: any(named: 'gpsDropoff'),
        photoRef: any(named: 'photoRef'),
        scannedQrNonce: any(named: 'scannedQrNonce'),
      ),
    ).thenAnswer((_) async {});
  });

  tearDown(() => bloc.close());

  test('a confirmed pickup records a scan check-in for its trip', () async {
    final done = expectLater(bloc.stream, emitsThrough(isA<PickupConfirmed>()));
    bloc.add(
      const ConfirmPickup(
        deliveryId: 'del-1',
        truckerId: 'trucker-1',
        tripId: 'trip-1',
      ),
    );
    await done;
    expect(sink.scans, ['trip-1']);
  });

  test('a confirmed drop-off does too (the engine ignores other phones)',
      () async {
    final done =
        expectLater(bloc.stream, emitsThrough(isA<DeliveryConfirmed>()));
    bloc.add(
      const ConfirmDelivery(
        deliveryId: 'del-1',
        buyerId: 'buyer-1',
        tripId: 'trip-1',
      ),
    );
    await done;
    expect(sink.scans, ['trip-1']);
  });

  test('a failed pickup records nothing', () async {
    when(
      () => repository.confirmPickup(
        deliveryId: any(named: 'deliveryId'),
        truckerId: any(named: 'truckerId'),
        gpsPickup: any(named: 'gpsPickup'),
        photoRef: any(named: 'photoRef'),
        scannedQrNonce: any(named: 'scannedQrNonce'),
      ),
    ).thenThrow(const SyncDeferredException('DELIVERY_NONCE_PENDING: wait.'));
    final done = expectLater(bloc.stream, emitsThrough(isA<DeliveryError>()));
    bloc.add(
      const ConfirmPickup(
        deliveryId: 'del-1',
        truckerId: 'trucker-1',
        tripId: 'trip-1',
      ),
    );
    await done;
    expect(sink.scans, isEmpty);
  });

  test('a self-pickup without a trip records nothing', () async {
    final done =
        expectLater(bloc.stream, emitsThrough(isA<DeliveryConfirmed>()));
    bloc.add(
      const ConfirmDelivery(
        deliveryId: 'del-1',
        buyerId: 'buyer-1',
        tripId: '',
      ),
    );
    await done;
    expect(sink.scans, isEmpty);
  });
}
