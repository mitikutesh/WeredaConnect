import 'dart:convert';
import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:isar/isar.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/database/schemas/order.dart';
import 'package:wereda_connect/core/database/schemas/sync_operation.dart';
import 'package:wereda_connect/core/network/api_client.dart';
import 'package:wereda_connect/core/sync/sync_queue.dart';
import 'package:wereda_connect/features/order/data/repositories/order_repository.dart';
import 'package:wereda_connect/features/order/data/repositories/web_order_repository.dart';

class _MockApiClient extends Mock implements ApiClient {}

/// SIM-30 on mobile (Isar copy) and web (the order the create returns): the
/// money of a new order is the server's, each leg to the santim. 170 kg at
/// 18.99 ETB/kg on a 255.55 ETB/quintal trip: crop 3228.30, transport 434.44,
/// commission 161.42 (161.41499999999999 in doubles), total 3824.16.
void main() {
  const transportCost = 170 / 100 * 255.55;

  void expectServerMoney(Order order) {
    expect(order.cropCost, 3228.30);
    expect(order.transportCost, 434.44);
    expect(order.commissionAmount, 161.42);
    expect(order.orderTotal, 3824.16);
  }

  group('mobile (Isar)', () {
    late Isar isar;
    late Directory tempDir;
    late SyncQueue queue;
    late OrderRepository repo;

    setUpAll(() async {
      await Isar.initializeIsarCore(download: true);
    });

    setUp(() async {
      tempDir = Directory.systemTemp.createTempSync('order_money_test_');
      isar = await Isar.open(
        [OrderSchema, SyncOperationSchema],
        directory: tempDir.path,
        name: 'order_money_${DateTime.now().microsecondsSinceEpoch}',
      );
      queue = SyncQueue(isar);
      repo = OrderRepository(isar: isar, syncQueue: queue);
    });

    tearDown(() async {
      queue.dispose();
      await isar.close(deleteFromDisk: true);
      if (tempDir.existsSync()) tempDir.deleteSync(recursive: true);
    });

    test('the local copy and the queued create carry santim amounts', () async {
      final order = await repo.createOrder(
        listingId: 'listing-1',
        buyerId: 'buyer-1',
        farmerId: 'farmer-1',
        buyerName: 'Buyer',
        farmerName: 'Farmer',
        cropType: 'teff',
        quantity: 170,
        pricePerUnit: 18.99,
        tripId: 'trip-1',
        truckerId: 'trucker-1',
        truckerName: 'Trucker',
        transportCost: transportCost,
      );

      expectServerMoney(order);
      final data = jsonDecode(
        (await isar.syncOperations.where().findAll()).single.data,
      ) as Map<String, dynamic>;
      expect(data['commissionAmount'], 161.42);
      expect(data['orderTotal'], 3824.16);
      // The client's transport cost goes out as computed: the server checks
      // it against the trip price within 0.01 and stores its own rounding.
      expect(data['transportCost'], transportCost);
    });
  });

  group('web', () {
    late _MockApiClient api;
    late WebOrderRepository repo;

    setUp(() {
      api = _MockApiClient();
      repo = WebOrderRepository(api: api);
      when(
        () => api.post<Map<String, dynamic>>(
          '/sync',
          data: any<dynamic>(named: 'data'),
        ),
      ).thenAnswer(
        (_) async => {
          'results': [
            {'status': 'success'},
          ],
        },
      );
    });

    test('the returned order carries santim amounts', () async {
      final order = await repo.createOrder(
        listingId: 'listing-1',
        buyerId: 'buyer-1',
        farmerId: 'farmer-1',
        buyerName: 'Buyer',
        farmerName: 'Farmer',
        cropType: 'teff',
        quantity: 170,
        pricePerUnit: 18.99,
        tripId: 'trip-1',
        truckerId: 'trucker-1',
        truckerName: 'Trucker',
        transportCost: transportCost,
      );

      expectServerMoney(order);
      expect(order.totalPrice, 3824.16);
    });
  });
}
