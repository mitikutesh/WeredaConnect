import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/network/api_client.dart';
import 'package:wereda_connect/core/network/api_exception.dart';
import 'package:wereda_connect/features/delivery_verification/data/repositories/web_delivery_repository.dart';

class _MockApiClient extends Mock implements ApiClient {}

final _uuidPattern = RegExp(
  r'^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$',
);

void main() {
  late _MockApiClient api;
  late WebDeliveryRepository repository;
  Map<String, dynamic>? sentBody;

  void answerSync(Map<String, dynamic> response) {
    when(
      () => api.post<Map<String, dynamic>>(
        '/sync',
        data: any<dynamic>(named: 'data'),
      ),
    ).thenAnswer((invocation) async {
      sentBody = invocation.namedArguments[#data] as Map<String, dynamic>;
      return response;
    });
  }

  Map<String, dynamic> sentOp() =>
      (sentBody!['operations'] as List).first as Map<String, dynamic>;

  setUp(() {
    api = _MockApiClient();
    repository = WebDeliveryRepository(api: api);
    sentBody = null;
  });

  // SIM-38: the server requires UUID ids, a `dlv_<ms>_<hash>` id was rejected.
  group('delivery ids are UUIDs', () {
    setUp(
      () => answerSync({
        'results': [
          {'status': 'success'},
        ],
      }),
    );

    test('trucker delivery', () async {
      final delivery = await repository.createDelivery(
        tripId: 'b6a4d1f0-1111-4c1e-9a55-000000000001',
        orderId: 'order-1',
        listingId: 'listing-1',
        farmerId: 'farmer-1',
        farmerName: 'Farmer',
        truckerId: 'trucker-1',
        truckerName: 'Trucker',
        buyerId: 'buyer-1',
        buyerName: 'Buyer',
        qrToken: 'qr-1',
      );

      expect(delivery.deliveryId, matches(_uuidPattern));
      expect(sentOp()['entityId'], delivery.deliveryId);
    });

    test('self-pickup delivery', () async {
      final delivery = await repository.createSelfPickupDelivery(
        orderId: 'order-1',
        listingId: 'listing-1',
        farmerId: 'farmer-1',
        farmerName: 'Farmer',
        buyerId: 'buyer-1',
        buyerName: 'Buyer',
      );

      expect(delivery.deliveryId, matches(_uuidPattern));
      expect(sentOp()['entityId'], delivery.deliveryId);
    });
  });

  test('a deferred pickup throws instead of reporting success', () async {
    // The version read-back falls back to 1 when the GET fails.
    when(() => api.get<Map<String, dynamic>>('/deliveries/del-1'))
        .thenThrow(Exception('offline'));
    answerSync({
      'results': [
        {
          'status': 'deferred',
          'message': 'DELIVERY_NONCE_PENDING: Waiting for the QR code.',
        },
      ],
    });

    await expectLater(
      repository.confirmPickup(
        deliveryId: 'del-1',
        truckerId: 'trucker-1',
        scannedQrNonce: 'nonce-1',
      ),
      throwsA(isA<SyncDeferredException>()),
    );
  });

  test('a rejected create throws instead of returning a phantom delivery',
      () async {
    answerSync({
      'results': [
        {
          'status': 'error',
          'message': 'DELIVERY_VALIDATION: tripId must be a valid UUID.',
        },
      ],
    });

    await expectLater(
      repository.createSelfPickupDelivery(
        orderId: 'order-1',
        listingId: 'listing-1',
        farmerId: 'farmer-1',
        farmerName: 'Farmer',
        buyerId: 'buyer-1',
        buyerName: 'Buyer',
      ),
      throwsA(isA<SyncRejectedException>()),
    );
  });
}
