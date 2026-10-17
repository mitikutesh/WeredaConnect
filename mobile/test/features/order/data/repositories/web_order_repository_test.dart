import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';
import 'package:wereda_connect/core/models/order.dart';
import 'package:wereda_connect/core/network/api_client.dart';
import 'package:wereda_connect/core/network/api_exception.dart';
import 'package:wereda_connect/features/order/data/repositories/web_order_repository.dart';

class MockApiClient extends Mock implements ApiClient {}

void main() {
  late MockApiClient api;
  late WebOrderRepository repo;

  setUp(() {
    api = MockApiClient();
    repo = WebOrderRepository(api: api);
  });

  Map<String, dynamic> orderDto({
    String orderId = 'ord-1',
    String status = 'created',
  }) =>
      {
        'orderId': orderId,
        'listingId': 'listing-1',
        'buyerId': 'buyer-1',
        'farmerId': 'farmer-1',
        'buyerName': 'Buyer',
        'farmerName': 'Farmer',
        'cropType': 'teff',
        'quantity': 2900.0,
        'pricePerUnit': 1050.0,
        'totalPrice': 3045000.0,
        'status': status,
        'createdAt': '2026-06-19T10:00:00Z',
        'version': 1,
      };

  group('WebOrderRepository.getOrderById response-shape handling', () {
    // Regression: the server returns GetOrderResponse as {"order": {...}}
    // (camelCase). The old client only checked for a top-level `orderId` or
    // `item` key, so it returned null → the detail screen showed
    // "An error occurred" for every order.
    test('parses the {"order": {...}} envelope the server actually returns',
        () async {
      when(() => api.get<Map<String, dynamic>>('/orders/ord-1'))
          .thenAnswer((_) async => {'order': orderDto()});

      final order = await repo.getOrderById('ord-1');

      expect(order, isNotNull);
      expect(order!.orderId, 'ord-1');
      expect(order.cropType, 'teff');
    });

    test('parses the server pickup deadline (ESC-06)', () async {
      when(() => api.get<Map<String, dynamic>>('/orders/ord-pd')).thenAnswer(
        (_) async => {
          'order': orderDto(orderId: 'ord-pd', status: 'accepted')
            ..['pickupDeadlineAt'] = '2026-10-12T08:00:00.0000000Z',
        },
      );

      final order = await repo.getOrderById('ord-pd');

      expect(order!.pickupDeadlineAt, DateTime.utc(2026, 10, 12, 8));
    });

    test('parses the contact flag and the parties\' phones (ESC-48)', () async {
      when(() => api.get<Map<String, dynamic>>('/orders/ord-c')).thenAnswer(
        (_) async => {
          'order': orderDto(orderId: 'ord-c', status: 'accepted')
            ..['contactOpen'] = true
            ..['buyerPhone'] = '+251911000001'
            ..['farmerPhone'] = '+251911000002'
            ..['truckerPhone'] = '+251911000003',
        },
      );

      final order = await repo.getOrderById('ord-c');

      expect(order!.contactOpen, isTrue);
      expect(order.buyerPhone, '+251911000001');
      expect(order.farmerPhone, '+251911000002');
      expect(order.truckerPhone, '+251911000003');
    });

    test('a closed order carries no phones (ESC-48)', () async {
      when(() => api.get<Map<String, dynamic>>('/orders/ord-f')).thenAnswer(
        (_) async => {
          'order': orderDto(orderId: 'ord-f', status: 'funded')
            ..['contactOpen'] = false,
        },
      );

      final order = await repo.getOrderById('ord-f');

      expect(order!.contactOpen, isFalse);
      expect(order.farmerPhone, isNull);
    });

    test('still accepts the legacy {"item": {...}} envelope', () async {
      when(() => api.get<Map<String, dynamic>>('/orders/ord-2'))
          .thenAnswer((_) async => {'item': orderDto(orderId: 'ord-2')});

      final order = await repo.getOrderById('ord-2');

      expect(order?.orderId, 'ord-2');
    });

    test('still accepts a bare order object', () async {
      when(() => api.get<Map<String, dynamic>>('/orders/ord-3'))
          .thenAnswer((_) async => orderDto(orderId: 'ord-3'));

      final order = await repo.getOrderById('ord-3');

      expect(order?.orderId, 'ord-3');
    });

    test('returns null for an unrecognizable response', () async {
      when(() => api.get<Map<String, dynamic>>('/orders/ord-4'))
          .thenAnswer((_) async => {'unexpected': true});

      expect(await repo.getOrderById('ord-4'), isNull);
    });
  });

  group('WebOrderRepository order-status wire mapping', () {
    // Regression: the client sent camelCase `paymentSubmitted`, but the server
    // vocabulary is snake_case (`payment_submitted`) and rejected the transition
    // inside an HTTP-200 batch → "An error occurred" on Submit Payment.
    test('submitPayment sends snake_case payment_submitted to /sync', () async {
      Map<String, dynamic>? sentBody;
      when(() => api.get<Map<String, dynamic>>('/orders/ord-1'))
          .thenAnswer((_) async => {'order': orderDto()});
      when(
        () => api.post<Map<String, dynamic>>(
          '/sync',
          data: any<dynamic>(named: 'data'),
        ),
      ).thenAnswer((invocation) async {
        sentBody = invocation.namedArguments[#data] as Map<String, dynamic>;
        return {
          'results': [
            {'status': 'success'},
          ],
        };
      });

      await repo.submitPayment(
        'ord-1',
        'MOCK-TBR-1',
        paymentMethod: 'telebirr',
      );

      final op =
          (sentBody!['operations'] as List).first as Map<String, dynamic>;
      final data = op['data'] as Map<String, dynamic>;
      expect(data['status'], 'payment_submitted');
      expect(data['paymentMethod'], 'telebirr');
    });

    // SIM-08: the server rejects a versionless order update, so every web
    // order transition reads the current version first and sends it.
    test('order updates send the current server version', () async {
      Map<String, dynamic>? sentBody;
      when(() => api.get<Map<String, dynamic>>('/orders/ord-7')).thenAnswer(
        (_) async => {
          'order': {...orderDto(orderId: 'ord-7'), 'version': 4},
        },
      );
      when(
        () => api.post<Map<String, dynamic>>(
          '/sync',
          data: any<dynamic>(named: 'data'),
        ),
      ).thenAnswer((invocation) async {
        sentBody = invocation.namedArguments[#data] as Map<String, dynamic>;
        return {
          'results': [
            {'status': 'success'},
          ],
        };
      });

      await repo.cancelOrder('ord-7', 'buyer-1', reason: 'late');

      final op =
          (sentBody!['operations'] as List).first as Map<String, dynamic>;
      final data = op['data'] as Map<String, dynamic>;
      expect(data['status'], 'cancelled');
      expect(data['version'], 4);
    });

    test('parses a snake_case payment_submitted status from the server',
        () async {
      when(() => api.get<Map<String, dynamic>>('/orders/ord-ps')).thenAnswer(
        (_) async =>
            {'order': orderDto(orderId: 'ord-ps', status: 'payment_submitted')},
      );

      final order = await repo.getOrderById('ord-ps');

      expect(order?.status, OrderStatus.paymentSubmitted);
    });

    // Regression: the multi-word status FILTER was sent as the camelCase enum
    // name, but the server matches stored snake_case, so the "In Transit" tab
    // returned nothing on web. It must be translated to the wire token.
    test('list status filter is sent to the server as a snake_case wire token',
        () async {
      Map<String, dynamic>? sentParams;
      when(
        () => api.get<Map<String, dynamic>>(
          '/orders',
          queryParameters: any<Map<String, dynamic>>(named: 'queryParameters'),
        ),
      ).thenAnswer((invocation) async {
        sentParams =
            invocation.namedArguments[#queryParameters] as Map<String, dynamic>;
        return {'orders': <dynamic>[]};
      });

      await repo.getOrdersByBuyer('buyer-1', statusFilter: 'inTransit');

      expect(sentParams!['status'], 'in_transit');
    });
  });

  group('WebOrderRepository status filtering (defensive, client-side)', () {
    // Regression: a lagging server that ignores the `status` query param
    // returns the user's full order list for every tab, so "Pending" orders
    // also appeared under "Accepted". The client must filter regardless.
    test('drops orders whose status does not match the requested filter',
        () async {
      when(
        () => api.get<Map<String, dynamic>>(
          '/orders',
          queryParameters: any(named: 'queryParameters'),
        ),
      ).thenAnswer(
        (_) async => {
          'orders': [
            orderDto(orderId: 'a'),
            orderDto(orderId: 'b', status: 'accepted'),
            orderDto(orderId: 'c'),
          ],
        },
      );

      final accepted =
          await repo.getOrdersByFarmer('farmer-1', statusFilter: 'accepted');

      expect(accepted.map((o) => o.orderId), ['b']);
    });

    test('returns everything when no filter is supplied', () async {
      when(
        () => api.get<Map<String, dynamic>>(
          '/orders',
          queryParameters: any(named: 'queryParameters'),
        ),
      ).thenAnswer(
        (_) async => {
          'orders': [
            orderDto(orderId: 'a'),
            orderDto(orderId: 'b', status: 'accepted'),
          ],
        },
      );

      final all = await repo.getOrdersByFarmer('farmer-1');

      expect(all.length, 2);
    });
  });

  // ESC-12: web has no queue, so a refused action or a version race must fail
  // instead of looking done.
  group('WebOrderRepository refused actions', () {
    void answerSync(Map<String, dynamic> result) {
      when(() => api.get<Map<String, dynamic>>('/orders/ord-1'))
          .thenAnswer((_) async => {'order': orderDto(status: 'funded')});
      when(
        () => api.post<Map<String, dynamic>>(
          '/sync',
          data: any<dynamic>(named: 'data'),
        ),
      ).thenAnswer(
        (_) async => {
          'results': [result],
        },
      );
    }

    test('a version race on accept throws SyncConflictException', () async {
      answerSync({
        'status': 'conflict',
        'serverVersion': 2,
        'message': 'Version conflict.',
      });

      await expectLater(
        repo.acceptOrder('ord-1', 'farmer-1'),
        throwsA(isA<SyncConflictException>()),
      );
    });

    test('an ORDER_* refusal throws with the server message', () async {
      answerSync({
        'status': 'error',
        'message': 'ORDER_VALIDATION: This order is under review and cannot '
            'be accepted. Contact support.',
      });

      await expectLater(
        repo.acceptOrder('ord-1', 'farmer-1'),
        throwsA(
          isA<SyncRejectedException>().having(
            (e) => e.message,
            'message',
            startsWith('ORDER_VALIDATION'),
          ),
        ),
      );
    });
  });

  group('WebOrderRepository payments (ESC-44)', () {
    Map<String, dynamic>? sentBody;

    void stubSync() {
      sentBody = null;
      when(
        () => api.post<Map<String, dynamic>>(
          '/sync',
          data: any<dynamic>(named: 'data'),
        ),
      ).thenAnswer((invocation) async {
        sentBody = invocation.namedArguments[#data] as Map<String, dynamic>;
        return {
          'results': [
            {'status': 'success'},
          ],
        };
      });
    }

    List<Map<String, dynamic>> sentOps() => [
          for (final op in sentBody!['operations'] as List)
            op as Map<String, dynamic>,
        ];

    test('a live Telebirr payment sends no reference', () async {
      when(() => api.get<Map<String, dynamic>>('/orders/ord-1'))
          .thenAnswer((_) async => {'order': orderDto()});
      stubSync();

      await repo.submitPayment('ord-1', null, paymentMethod: 'telebirr');

      final data = sentOps().single['data'] as Map<String, dynamic>;
      expect(data['status'], 'payment_submitted');
      expect(data.containsKey('cbeTransactionId'), isFalse);
    });

    test('a retry after payment_failed is one payment_submitted change',
        () async {
      when(() => api.get<Map<String, dynamic>>('/orders/ord-1')).thenAnswer(
        (_) async => {
          'order': {...orderDto(status: 'payment_failed'), 'version': 6},
        },
      );
      stubSync();

      await repo.submitPayment('ord-1', null, paymentMethod: 'telebirr');

      final ops = sentOps();
      expect(ops, hasLength(1));
      final data = ops.single['data'] as Map<String, dynamic>;
      expect(data['status'], 'payment_submitted');
      expect(data['version'], 6);
    });

    test('already payment_submitted on the server: nothing is sent', () async {
      when(() => api.get<Map<String, dynamic>>('/orders/ord-1')).thenAnswer(
        (_) async => {'order': orderDto(status: 'payment_submitted')},
      );
      stubSync();

      await repo.submitPayment('ord-1', null, paymentMethod: 'telebirr');

      expect(sentBody, isNull);
    });

    test('fetchOrder lets errors through; getOrderById swallows them',
        () async {
      when(() => api.get<Map<String, dynamic>>('/orders/ord-1'))
          .thenThrow(const NetworkException('offline'));

      await expectLater(
        repo.fetchOrder('ord-1'),
        throwsA(isA<NetworkException>()),
      );
      expect(await repo.getOrderById('ord-1'), isNull);
    });
  });
}
