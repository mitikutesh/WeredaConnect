import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/database/schemas/delivery.dart';
import 'package:wereda_connect/core/network/api_exception.dart';
import 'package:wereda_connect/features/delivery_verification/data/services/dropoff_handoff_store.dart';
import 'package:wereda_connect/features/delivery_verification/data/services/dropoff_qr_issuer.dart';
import 'package:wereda_connect/features/delivery_verification/data/services/handoff_code.dart';
import 'package:wereda_connect/features/delivery_verification/data/services/qr_verification_service.dart';
import 'package:wereda_connect/features/delivery_verification/domain/repositories/base_delivery_repository.dart';

class _MockRepository extends Mock implements BaseDeliveryRepository {}

class _MockQrService extends Mock implements QrVerificationService {}

class _FakeDelivery extends Fake implements Delivery {}

Delivery _delivery() => Delivery()
  ..deliveryId = 'delivery-12345678'
  ..tripId = 'trip-1'
  ..listingId = 'l-1'
  ..farmerId = 'f-1'
  ..farmerName = 'Almaz'
  ..truckerId = 'trucker-1'
  ..truckerName = 'Dawit'
  ..buyerId = 'b-1'
  ..buyerName = 'Selam'
  ..qrToken = 'qr'
  ..status = DeliveryStatus.pickedUp
  ..createdAt = DateTime.utc(2026, 11, 12)
  ..updatedAt = DateTime.utc(2026, 11, 12)
  ..version = 2;

/// ESC-35: the trucker's own drop-off code is made, kept and registered.
void main() {
  late _MockRepository repository;
  late _MockQrService qr;
  late MemoryDropoffHandoffStore store;
  late DropoffQrIssuer issuer;
  final delivery = _delivery();

  setUpAll(() => registerFallbackValue(_FakeDelivery()));

  setUp(() {
    repository = _MockRepository();
    qr = _MockQrService();
    store = MemoryDropoffHandoffStore();
    issuer =
        DropoffQrIssuer(qrService: qr, repository: repository, store: store);
    when(() => qr.generateQrToken(any(), dropoff: true))
        .thenAnswer((_) async => ('drop-token', 'd.nonce-1'));
    when(
      () => repository.persistDropoffQrNonce(
        deliveryId: any(named: 'deliveryId'),
        nonce: any(named: 'nonce'),
        truckerId: any(named: 'truckerId'),
        renew: any(named: 'renew'),
      ),
    ).thenAnswer((_) async => DropoffCodeRegistration.sent);
  });

  void verifySent(String nonce, {required bool renew}) => verify(
        () => repository.persistDropoffQrNonce(
          deliveryId: delivery.deliveryId,
          nonce: nonce,
          truckerId: 'trucker-1',
          renew: renew,
        ),
      ).called(1);

  test('a code made at pickup is kept and registered, never replacing one',
      () async {
    final (kept, registration) = await issuer.issue(delivery);

    expect(registration, DropoffCodeRegistration.sent);
    expect(kept.token, 'drop-token');
    expect(kept.nonce, 'd.nonce-1');
    expect(
      kept.code,
      HandoffCode.derive(nonce: 'd.nonce-1', deliveryId: delivery.deliveryId),
    );
    final read = await store.read('trucker-1', delivery.deliveryId);
    expect(read?.nonce, 'd.nonce-1');
    verifySent('d.nonce-1', renew: false);
  });

  test('New drop-off code asks the server to replace', () async {
    await issuer.issue(delivery, renew: true);

    verifySent('d.nonce-1', renew: true);
  });

  test('a kept code sent again never replaces; the answer is passed on',
      () async {
    when(
      () => repository.persistDropoffQrNonce(
        deliveryId: any(named: 'deliveryId'),
        nonce: any(named: 'nonce'),
        truckerId: any(named: 'truckerId'),
        renew: any(named: 'renew'),
      ),
    ).thenAnswer((_) async => DropoffCodeRegistration.superseded);

    final registration = await issuer.register(
      delivery,
      const KeptHandoff(token: 'tok', code: '757555978', nonce: 'd.kept'),
    );

    expect(registration, DropoffCodeRegistration.superseded);
    verifySent('d.kept', renew: false);
  });

  test('a registration that does not get through still keeps the code',
      () async {
    when(
      () => repository.persistDropoffQrNonce(
        deliveryId: any(named: 'deliveryId'),
        nonce: any(named: 'nonce'),
        truckerId: any(named: 'truckerId'),
        renew: any(named: 'renew'),
      ),
    ).thenThrow(const NetworkException('offline'));

    final (kept, registration) = await issuer.issue(delivery);

    expect(registration, DropoffCodeRegistration.failed);
    final read = await store.read('trucker-1', delivery.deliveryId);
    expect(read?.nonce, kept.nonce);
  });

  test('a farmer\'s token kept before has nothing to register', () async {
    final registration = await issuer.register(
      delivery,
      const KeptHandoff(token: 'farmer-token', code: '757555978'),
    );

    expect(registration, DropoffCodeRegistration.notStored);
    verifyNever(
      () => repository.persistDropoffQrNonce(
        deliveryId: any(named: 'deliveryId'),
        nonce: any(named: 'nonce'),
        truckerId: any(named: 'truckerId'),
        renew: any(named: 'renew'),
      ),
    );
  });
}
