import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';
import 'package:wereda_connect/core/errors/app_error.dart';
import 'package:wereda_connect/core/models/order_delivery_location.dart';
import 'package:wereda_connect/core/network/api_client.dart';
import 'package:wereda_connect/core/network/api_exception.dart';
import 'package:wereda_connect/features/order/data/repositories/web_order_repository.dart';

class _MockApiClient extends Mock implements ApiClient {}

const DeliveryPoint _adama = (latitude: 8.5423, longitude: 39.2687);
const DeliveryPoint _mojo = (latitude: 8.5872, longitude: 39.1213);

/// LT-14 on web (no local copy, direct `/sync`): the order's own drop-off
/// point goes out with the order create, the buyer's change carries the
/// order's current version, the mobile guards hold, and the server's refusal
/// after pickup comes back as its own code.
void main() {
  late _MockApiClient api;
  late WebOrderRepository repo;
  Map<String, dynamic>? sent;
  var answer = <String, dynamic>{'status': 'success'};

  Map<String, dynamic> orderDto({
    String status = 'accepted',
    bool point = true,
    bool trip = true,
  }) =>
      {
        'orderId': 'ord-1',
        'listingId': 'listing-1',
        'buyerId': 'buyer-1',
        'farmerId': 'farmer-1',
        'buyerName': 'Buyer',
        'farmerName': 'Farmer',
        'cropType': 'teff',
        'quantity': 500.0,
        'pricePerUnit': 60.0,
        'status': status,
        'createdAt': '2026-10-30T08:00:00Z',
        'version': 4,
        if (trip) 'tripId': 'trip-1',
        if (trip) 'truckerId': 'trucker-1',
        if (point) 'deliveryLatitude': 8.5423,
        if (point) 'deliveryLongitude': 39.2687,
      };

  Map<String, dynamic> sentData() =>
      ((sent!['operations'] as List).single as Map<String, dynamic>)['data']
          as Map<String, dynamic>;

  void serverHas(Map<String, dynamic> order) =>
      when(() => api.get<Map<String, dynamic>>('/orders/ord-1'))
          .thenAnswer((_) async => {'order': order});

  setUp(() {
    api = _MockApiClient();
    repo = WebOrderRepository(api: api);
    sent = null;
    answer = {'status': 'success'};
    when(
      () => api.post<Map<String, dynamic>>(
        '/sync',
        data: any<dynamic>(named: 'data'),
      ),
    ).thenAnswer((invocation) async {
      sent = invocation.namedArguments[#data] as Map<String, dynamic>;
      return {
        'results': [answer],
      };
    });
  });

  test('parses the point of a server order', () async {
    serverHas(orderDto());

    final order = await repo.getOrderById('ord-1');

    expect(orderDeliveryPoint(order!), _adama);
  });

  test('a server order without the point has none', () async {
    serverHas(orderDto(point: false));

    expect(orderDeliveryPoint((await repo.getOrderById('ord-1'))!), isNull);
  });

  group('order create', () {
    Future<void> create(DeliveryPoint? point) => repo.createOrder(
          listingId: 'listing-1',
          buyerId: 'buyer-1',
          farmerId: 'farmer-1',
          buyerName: 'Buyer',
          farmerName: 'Farmer',
          cropType: 'teff',
          quantity: 500,
          pricePerUnit: 60,
          tripId: 'trip-1',
          truckerId: 'trucker-1',
          truckerName: 'Trucker',
          transportCost: 600,
          deliveryPoint: point,
        );

    test('sends the point as numbers', () async {
      await create(_adama);

      expect(sentData()['deliveryLatitude'], 8.5423);
      expect(sentData()['deliveryLongitude'], 39.2687);
    });

    test('leaves out no point and a point outside Ethiopia', () async {
      await create(null);
      expect(sentData().containsKey('deliveryLatitude'), isFalse);

      await create((latitude: 51.5, longitude: -0.12));
      expect(sentData().containsKey('deliveryLatitude'), isFalse);
      expect(sentData().containsKey('deliveryLongitude'), isFalse);
    });
  });

  group('the buyer changes it', () {
    test('with the current version, before pickup', () async {
      serverHas(orderDto());

      await repo.updateDeliveryLocation('ord-1', 'buyer-1', _mojo);

      final op = (sent!['operations'] as List).single as Map<String, dynamic>;
      expect(op['entityType'], 'order');
      expect(op['action'], 'update');
      expect(sentData(), {
        'deliveryLatitude': 8.5872,
        'deliveryLongitude': 39.1213,
        'version': 4,
      });
    });

    test('on an order that had none', () async {
      serverHas(orderDto(status: 'funded', point: false));

      await repo.updateDeliveryLocation('ord-1', 'buyer-1', _adama);

      expect(sentData()['deliveryLatitude'], 8.5423);
    });

    test('the same point again sends nothing', () async {
      serverHas(orderDto());

      await repo.updateDeliveryLocation('ord-1', 'buyer-1', _adama);

      expect(sent, isNull);
    });

    test('a refusal after pickup reads as its own code', () async {
      serverHas(orderDto());
      answer = {
        'status': 'error',
        'message': 'ORDER_DELIVERY_LOCATION_LOCKED: The drop-off place can '
            'only be changed before pickup.',
      };

      await expectLater(
        repo.updateDeliveryLocation('ord-1', 'buyer-1', _mojo),
        throwsA(
          isA<SyncRejectedException>().having(
            mapErrorToCode,
            'code',
            AppErrorCode.deliveryLocationLocked,
          ),
        ),
      );
    });
  });

  group('refused before anything is sent', () {
    test('after pickup', () async {
      serverHas(orderDto(status: 'in_transit'));

      await expectLater(
        repo.updateDeliveryLocation('ord-1', 'buyer-1', _mojo),
        throwsA(isA<OrderActionNotAllowedException>()),
      );
      expect(sent, isNull);
    });

    test('the farmer or the trucker', () async {
      serverHas(orderDto());

      for (final caller in ['farmer-1', 'trucker-1']) {
        await expectLater(
          repo.updateDeliveryLocation('ord-1', caller, _mojo),
          throwsA(isA<OrderActionNotAllowedException>()),
        );
      }
      expect(sent, isNull);
    });

    test('a self-pickup order', () async {
      serverHas(orderDto(trip: false));

      await expectLater(
        repo.updateDeliveryLocation('ord-1', 'buyer-1', _mojo),
        throwsA(isA<OrderActionNotAllowedException>()),
      );
      expect(sent, isNull);
    });

    test('a point outside Ethiopia', () async {
      serverHas(orderDto());

      await expectLater(
        repo.updateDeliveryLocation(
          'ord-1',
          'buyer-1',
          (latitude: 0, longitude: 0),
        ),
        throwsA(isA<OrderActionNotAllowedException>()),
      );
      expect(sent, isNull);
    });
  });
}
