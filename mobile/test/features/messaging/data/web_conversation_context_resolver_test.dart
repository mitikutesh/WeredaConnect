import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/network/api_client.dart';
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
  });
}
