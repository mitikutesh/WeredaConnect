import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';
import 'package:wereda_connect/core/network/api_client.dart';
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
}
