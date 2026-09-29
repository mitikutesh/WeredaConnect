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

  group('findActiveDeliveryForOrder', () {
    Map<String, dynamic> row(
      String id,
      String orderId,
      String status, {
      String createdAt = '2026-09-29T10:00:00Z',
    }) =>
        {
          'deliveryId': id,
          'orderId': orderId,
          'status': status,
          'farmerId': 'farmer-1',
          'buyerId': 'buyer-1',
          'createdAt': createdAt,
        };

    test('stops at deliveries older than the order (one request, not ten)',
        () async {
      var requests = 0;
      when(
        () => api.get<Map<String, dynamic>>(
          '/deliveries',
          queryParameters: any(named: 'queryParameters'),
        ),
      ).thenAnswer((_) async {
        requests++;
        return {
          'deliveries': [
            row('d-new', 'order-9', 'pending'),
            row(
              'd-old',
              'order-8',
              'delivered',
              createdAt: '2026-09-20T10:00:00Z',
            ),
          ],
          'nextToken': 'more',
        };
      });

      final found = await repository.findActiveDeliveryForOrder(
        'order-1',
        userId: 'buyer-1',
        role: 'buyer',
        orderCreatedAt: DateTime.utc(2026, 9, 28),
      );

      expect(found, isNull);
      expect(requests, 1);
    });

    test('keeps paging while the page is newer than the order', () async {
      var requests = 0;
      when(
        () => api.get<Map<String, dynamic>>(
          '/deliveries',
          queryParameters: any(named: 'queryParameters'),
        ),
      ).thenAnswer((_) async {
        requests++;
        return requests == 1
            ? {
                'deliveries': [row('d-new', 'order-9', 'pending')],
                'nextToken': 'more',
              }
            : {
                'deliveries': [row('d-live', 'order-1', 'pending')],
              };
      });

      final found = await repository.findActiveDeliveryForOrder(
        'order-1',
        userId: 'buyer-1',
        role: 'buyer',
        orderCreatedAt: DateTime.utc(2026, 9, 28),
      );

      expect(found?.deliveryId, 'd-live');
      expect(requests, 2);
    });

    test('pages past the first page and skips cancelled deliveries', () async {
      final queries = <Map<String, dynamic>>[];
      when(
        () => api.get<Map<String, dynamic>>(
          '/deliveries',
          queryParameters: any(named: 'queryParameters'),
        ),
      ).thenAnswer((invocation) async {
        final q = Map<String, dynamic>.from(
          invocation.namedArguments[#queryParameters] as Map,
        );
        queries.add(q);
        if (q['nextToken'] == null) {
          return {
            'deliveries': [
              row('d-cancelled', 'order-1', 'cancelled'),
              row('d-other', 'order-2', 'pending'),
            ],
            'nextToken': 'page-2',
          };
        }
        return {
          'deliveries': [row('d-live', 'order-1', 'pending')],
        };
      });

      final found = await repository.findActiveDeliveryForOrder(
        'order-1',
        userId: 'farmer-1',
        role: 'farmer',
      );

      expect(found?.deliveryId, 'd-live');
      expect(queries, hasLength(2));
      // Scoped to the caller's own deliveries, at the largest page size.
      expect(queries.first['farmerId'], 'farmer-1');
      expect(queries.first['limit'], 50);
      expect(queries.last['nextToken'], 'page-2');
    });

    test('a buyer uses the participant query and gets null when none match',
        () async {
      Map<String, dynamic>? query;
      when(
        () => api.get<Map<String, dynamic>>(
          '/deliveries',
          queryParameters: any(named: 'queryParameters'),
        ),
      ).thenAnswer((invocation) async {
        query = Map<String, dynamic>.from(
          invocation.namedArguments[#queryParameters] as Map,
        );
        return {
          'deliveries': [row('d-cancelled', 'order-1', 'cancelled')],
        };
      });

      final found = await repository.findActiveDeliveryForOrder(
        'order-1',
        userId: 'buyer-1',
        role: 'buyer',
      );

      expect(found, isNull);
      expect(query!['mine'], 'true');
      expect(query!.containsKey('farmerId'), isFalse);
    });
  });

  // The farmer's QR shown on web must reach the server, or the buyer's scan
  // stays deferred forever (web has no sync queue to carry it later).
  group('persistQrNonce', () {
    setUp(() {
      when(() => api.get<Map<String, dynamic>>('/deliveries/del-1')).thenAnswer(
        (_) async => {
          'delivery': {
            'deliveryId': 'del-1',
            'status': 'pending',
            'deliveryType': 'self_pickup',
            'version': 3,
          },
        },
      );
    });

    test('registers the nonce as a delivery update at the current version',
        () async {
      answerSync({
        'results': [
          {'status': 'success'},
        ],
      });

      await repository.persistQrNonce(
        deliveryId: 'del-1',
        qrNonce: 'nonce-1',
        userId: 'farmer-1',
      );

      final op = sentOp();
      expect(op['entityType'], 'delivery');
      expect(op['action'], 'update');
      expect(op['entityId'], 'del-1');
      expect(op['data'], {'qrNonce': 'nonce-1', 'version': 3});
    });

    test('a refused nonce throws so the QR screen keeps the code hidden',
        () async {
      answerSync({
        'results': [
          {
            'status': 'error',
            'message': 'DELIVERY_UNAUTHORIZED: You can only update your own '
                'deliveries.',
          },
        ],
      });

      await expectLater(
        repository.persistQrNonce(
          deliveryId: 'del-1',
          qrNonce: 'nonce-1',
          userId: 'farmer-1',
        ),
        throwsA(isA<SyncRejectedException>()),
      );
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
