import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/models/user.dart';
import 'package:wereda_connect/core/network/api_client.dart';
import 'package:wereda_connect/core/network/api_exception.dart';
import 'package:wereda_connect/features/messaging/data/web_conversation_context_resolver.dart';

class _MockApiClient extends Mock implements ApiClient {}

void main() {
  group('WebConversationContextResolver', () {
    late _MockApiClient api;
    late WebConversationContextResolver resolver;

    setUp(() {
      api = _MockApiClient();
      resolver = WebConversationContextResolver(api: api);
    });

    void stubGet(String path, Map<String, dynamic> response) {
      when(() => api.get<Map<String, dynamic>>(path))
          .thenAnswer((_) async => response);
    }

    test('crop context parses the API envelope (quantityKg shape)', () async {
      stubGet('/crops/crop-1', {
        'crop': {'cropId': 'crop-1', 'cropType': 'teff', 'quantityKg': 100},
      });

      final info = await resolver.resolve('crop', 'crop-1');
      expect(info, isNotNull);
      expect(info!.cropTypeKey, 'teff');
      expect(info.quantityKg, 100);
    });

    test('order context reads the denormalized crop off the order', () async {
      stubGet('/orders/order-1', {
        'order': {'orderId': 'order-1', 'cropType': 'wheat', 'quantity': 40},
      });

      final info = await resolver.resolve('order', 'order-1');
      expect(info, isNotNull);
      expect(info!.cropTypeKey, 'wheat');
      expect(info.quantityKg, 40);
    });

    test('delivery context chains delivery → listing → crop', () async {
      stubGet('/deliveries/delivery-1', {
        'delivery': {'deliveryId': 'delivery-1', 'listingId': 'crop-9'},
      });
      stubGet('/crops/crop-9', {
        'crop': {'cropId': 'crop-9', 'cropType': 'maize', 'quantityKg': 75},
      });

      final info = await resolver.resolve('delivery', 'delivery-1');
      expect(info, isNotNull);
      expect(info!.cropTypeKey, 'maize');
      expect(info.quantityKg, 75);
    });

    test('a role-gated chained lookup (403) falls back to null', () async {
      // e.g. a trucker resolving a delivery's crop: GET /crops/{id} is
      // forbidden for truckers — the tag then shows the generic label.
      stubGet('/deliveries/delivery-2', {
        'delivery': {'deliveryId': 'delivery-2', 'listingId': 'crop-9'},
      });
      when(() => api.get<Map<String, dynamic>>('/crops/crop-9'))
          .thenThrow(Exception('403 CROP_FORBIDDEN'));

      expect(await resolver.resolve('delivery', 'delivery-2'), isNull);
    });

    test('a trucker never asks for crops, directly or via a delivery (SIM-21)',
        () async {
      resolver = WebConversationContextResolver(
        api: api,
        roleOf: () => UserRole.trucker,
      );

      expect(await resolver.resolve('crop', 'crop-1'), isNull);
      expect(await resolver.resolve('delivery', 'delivery-1'), isNull);
      verifyNever(() => api.get<Map<String, dynamic>>(any()));
    });

    test('a buyer still resolves crops with the role hook wired', () async {
      resolver = WebConversationContextResolver(
        api: api,
        roleOf: () => UserRole.buyer,
      );
      stubGet('/crops/crop-1', {
        'crop': {'cropId': 'crop-1', 'cropType': 'teff', 'quantityKg': 100},
      });

      final info = await resolver.resolve('crop', 'crop-1');
      expect(info?.cropTypeKey, 'teff');
    });

    test('a 403 is remembered: the same context is not fetched again',
        () async {
      when(() => api.get<Map<String, dynamic>>('/crops/crop-x'))
          .thenThrow(const AuthException('Forbidden', 403));

      expect(await resolver.resolve('crop', 'crop-x'), isNull);
      expect(await resolver.resolve('crop', 'crop-x'), isNull);
      verify(() => api.get<Map<String, dynamic>>('/crops/crop-x')).called(1);
    });

    test('a transient failure is retried on the next resolve', () async {
      when(() => api.get<Map<String, dynamic>>('/crops/crop-t'))
          .thenThrow(const NetworkException());

      expect(await resolver.resolve('crop', 'crop-t'), isNull);
      expect(await resolver.resolve('crop', 'crop-t'), isNull);
      verify(() => api.get<Map<String, dynamic>>('/crops/crop-t')).called(2);
    });

    test('concurrent resolves of one context share a single lookup', () async {
      when(() => api.get<Map<String, dynamic>>('/crops/crop-1')).thenAnswer(
        (_) => Future.delayed(
          const Duration(milliseconds: 20),
          () => {
            'crop': {'cropId': 'crop-1', 'cropType': 'teff', 'quantityKg': 1},
          },
        ),
      );

      final results = await Future.wait([
        resolver.resolve('crop', 'crop-1'),
        resolver.resolve('crop', 'crop-1'),
      ]);

      expect(results[0]?.cropTypeKey, 'teff');
      expect(results[1]?.cropTypeKey, 'teff');
      verify(() => api.get<Map<String, dynamic>>('/crops/crop-1')).called(1);
    });

    test('trip context parses the route', () async {
      stubGet('/trips/trip-1', {
        'trip': {
          'tripId': 'trip-1',
          'fromWoreda': 'Adama',
          'toWoreda': 'Bishoftu',
        },
      });

      final info = await resolver.resolve('trip', 'trip-1');
      expect(info, isNotNull);
      expect(info!.fromWoreda, 'Adama');
      expect(info.toWoreda, 'Bishoftu');
    });
    // ESC-48: a thread's contact follows its order, read from the server.
    group('resolveContact', () {
      Map<String, dynamic> serverOrder(
        String status, {
        bool contactOpen = true,
      }) =>
          {
            'order': {
              'orderId': 'order-1',
              'buyerId': 'buyer-1',
              'farmerId': 'farmer-1',
              'truckerId': 'trucker-1',
              'status': status,
              'contactOpen': contactOpen,
              if (contactOpen) 'farmerPhone': '+251911000002',
              if (contactOpen) 'buyerPhone': '+251911000001',
            },
          };

      test('crop and trip threads are closed without a request', () async {
        expect(
          (await resolver.resolveContact('crop', 'crop-1', 'farmer-1')).open,
          isFalse,
        );
        expect(
          (await resolver.resolveContact('trip', 'trip-1', 'trucker-1')).open,
          isFalse,
        );
        verifyZeroInteractions(api);
      });

      test('an open order gives the counterpart\'s number', () async {
        stubGet('/orders/order-1', serverOrder('in_transit'));

        final contact =
            await resolver.resolveContact('order', 'order-1', 'farmer-1');
        expect(contact.open, isTrue);
        expect(contact.phone, '+251911000002');
      });

      test('the server flag closes an open status', () async {
        stubGet(
          '/orders/order-1',
          serverOrder('in_transit', contactOpen: false),
        );

        final contact =
            await resolver.resolveContact('order', 'order-1', 'buyer-1');
        expect(contact.open, isFalse);
        expect(contact.phone, isNull);
      });

      test('a delivery thread reads its order', () async {
        stubGet('/deliveries/delivery-1', {
          'delivery': {'deliveryId': 'delivery-1', 'orderId': 'order-1'},
        });
        stubGet('/orders/order-1', serverOrder('funded', contactOpen: false));

        final contact =
            await resolver.resolveContact('delivery', 'delivery-1', 'farmer-1');
        expect(contact.open, isFalse);
      });

      test('a failed read is unknown, not closed', () async {
        when(() => api.get<Map<String, dynamic>>('/orders/order-1'))
            .thenThrow(const NetworkException());

        final contact =
            await resolver.resolveContact('order', 'order-1', 'farmer-1');
        expect(contact.open, isNull);
        expect(contact.phone, isNull);
      });
    });
  });
}
