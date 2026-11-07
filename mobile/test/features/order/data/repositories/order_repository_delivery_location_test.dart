import 'dart:convert';
import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:isar/isar.dart';

import 'package:wereda_connect/core/database/schemas/order.dart';
import 'package:wereda_connect/core/database/schemas/sync_operation.dart';
import 'package:wereda_connect/core/errors/app_error.dart';
import 'package:wereda_connect/core/models/order_commitment.dart';
import 'package:wereda_connect/core/models/order_delivery_location.dart';
import 'package:wereda_connect/core/sync/sync_queue.dart';
import 'package:wereda_connect/features/order/data/repositories/order_repository.dart';

/// A queue that refuses every new operation, like a full queue.
class _FullSyncQueue extends SyncQueue {
  _FullSyncQueue(super.isar);

  @override
  Future<SyncOperation> enqueue({
    required String userId,
    required String entityType,
    required String entityId,
    required String action,
    required String data,
    required String operationId,
    DateTime? keyTimestamp,
  }) async =>
      throw const SyncQueueFullException('full');
}

const DeliveryPoint _adama = (latitude: 8.5423, longitude: 39.2687);
const DeliveryPoint _mojo = (latitude: 8.5872, longitude: 39.1213);

/// LT-14 on mobile (Isar and the sync queue): the order's own drop-off point
/// goes out with the order create when it is inside Ethiopia's box; the buyer
/// changes it offline-first until pickup with the pre-change version; nobody
/// else can; and a server row without it (an uncommitted trucker's) clears
/// the copy.
void main() {
  late Isar isar;
  late Directory tempDir;
  late SyncQueue queue;
  late OrderRepository repo;

  setUpAll(() async {
    await Isar.initializeIsarCore(download: true);
  });

  setUp(() async {
    tempDir = Directory.systemTemp.createTempSync('order_delivery_loc_test_');
    isar = await Isar.open(
      [OrderSchema, SyncOperationSchema],
      directory: tempDir.path,
      name: 'order_delivery_loc_${DateTime.now().microsecondsSinceEpoch}',
    );
    queue = SyncQueue(isar);
    repo = OrderRepository(isar: isar, syncQueue: queue);
  });

  tearDown(() async {
    queue.dispose();
    await isar.close(deleteFromDisk: true);
    if (tempDir.existsSync()) tempDir.deleteSync(recursive: true);
  });

  Future<void> putOrder(OrderStatus status, {bool trip = true}) =>
      isar.writeTxn(() async {
        await isar.orders.put(
          Order()
            ..orderId = 'order-1'
            ..listingId = 'listing-1'
            ..buyerId = 'buyer-1'
            ..farmerId = 'farmer-1'
            ..buyerName = 'Buyer'
            ..farmerName = 'Farmer'
            ..cropType = 'teff'
            ..quantity = 500
            ..pricePerUnit = 60
            ..totalPrice = 30000
            ..status = status
            ..createdAt = DateTime.utc(2026, 10)
            ..version = 3
            ..tripId = trip ? 'trip-1' : null
            ..truckerId = trip ? 'trucker-1' : null
            ..deliveryLatitude = _adama.latitude
            ..deliveryLongitude = _adama.longitude,
        );
      });

  Future<Order> loadId(String orderId) async =>
      (await isar.orders.filter().orderIdEqualTo(orderId).findFirst())!;

  Future<Order> load() => loadId('order-1');

  Future<List<SyncOperation>> ops() => isar.syncOperations.where().findAll();

  Map<String, dynamic> dataOf(SyncOperation op) =>
      jsonDecode(op.data) as Map<String, dynamic>;

  Future<Order> create(DeliveryPoint? point) => repo.createOrder(
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

  group('order create', () {
    test('saves and queues the point as numbers', () async {
      final order = await create(_adama);

      expect(orderDeliveryPoint(order), _adama);
      expect(orderDeliveryPoint(await loadId(order.orderId)), _adama);
      final data = dataOf((await ops()).single);
      expect(data['deliveryLatitude'], 8.5423);
      expect(data['deliveryLongitude'], 39.2687);
    });

    test('without a point, neither field is sent', () async {
      final order = await create(null);

      expect(orderDeliveryPoint(await loadId(order.orderId)), isNull);
      final data = dataOf((await ops()).single);
      expect(data.containsKey('deliveryLatitude'), isFalse);
      expect(data.containsKey('deliveryLongitude'), isFalse);
    });

    test(
        'a point outside Ethiopia is never sent (the server would refuse it '
        'for good)', () async {
      final order = await create((latitude: 51.5, longitude: -0.12));

      expect(orderDeliveryPoint(await loadId(order.orderId)), isNull);
      expect(
        dataOf((await ops()).single).containsKey('deliveryLatitude'),
        isFalse,
      );
    });
  });

  group('the buyer changes it before pickup', () {
    for (final status in [
      OrderStatus.created,
      OrderStatus.funded,
      OrderStatus.awaitingTransport,
      OrderStatus.accepted,
    ]) {
      test(status.name, () async {
        await putOrder(status);

        await repo.updateDeliveryLocation('order-1', 'buyer-1', _mojo);

        final order = await load();
        expect(orderDeliveryPoint(order), _mojo);
        expect(order.version, 4);
        final op = (await ops()).single;
        expect(op.entityType, 'order');
        expect(op.action, 'update');
        final data = dataOf(op);
        expect(data['deliveryLatitude'], 8.5872);
        expect(data['deliveryLongitude'], 39.1213);
        // The pre-change version: the server checks it.
        expect(data['version'], 3);
        expect(data.containsKey('status'), isFalse);
        expect(data.containsKey('dropoffNote'), isFalse);
      });
    }

    test('the same point again is a no-op', () async {
      await putOrder(OrderStatus.accepted);

      await repo.updateDeliveryLocation('order-1', 'buyer-1', _adama);

      expect((await load()).version, 3);
      expect(await ops(), isEmpty);
    });
  });

  group('refused without writing or queueing', () {
    Future<void> expectRefused(Future<void> Function() action) async {
      await expectLater(
        action(),
        throwsA(isA<OrderActionNotAllowedException>()),
      );
      final order = await load();
      expect(orderDeliveryPoint(order), _adama);
      expect(order.version, 3);
      expect(await ops(), isEmpty);
    }

    for (final status in [
      OrderStatus.pickupConfirmed,
      OrderStatus.inTransit,
      OrderStatus.delivered,
      OrderStatus.cancelled,
    ]) {
      test('after pickup or on an ended order: ${status.name}', () async {
        await putOrder(status);
        await expectRefused(
          () => repo.updateDeliveryLocation('order-1', 'buyer-1', _mojo),
        );
      });
    }

    test('the farmer and the trucker', () async {
      await putOrder(OrderStatus.accepted);
      await expectRefused(
        () => repo.updateDeliveryLocation('order-1', 'farmer-1', _mojo),
      );
      await expectRefused(
        () => repo.updateDeliveryLocation('order-1', 'trucker-1', _mojo),
      );
    });

    test('a self-pickup order has no drop-off', () async {
      await putOrder(OrderStatus.accepted, trip: false);
      await expectRefused(
        () => repo.updateDeliveryLocation('order-1', 'buyer-1', _mojo),
      );
    });

    test('a point outside Ethiopia', () async {
      await putOrder(OrderStatus.accepted);
      await expectRefused(
        () => repo.updateDeliveryLocation(
          'order-1',
          'buyer-1',
          (latitude: 0, longitude: 0),
        ),
      );
    });
  });

  test('a failed enqueue puts the point back and rethrows', () async {
    repo = OrderRepository(isar: isar, syncQueue: _FullSyncQueue(isar));
    await putOrder(OrderStatus.accepted);

    await expectLater(
      repo.updateDeliveryLocation('order-1', 'buyer-1', _mojo),
      throwsA(isA<SyncQueueFullException>()),
    );

    final order = await load();
    expect(orderDeliveryPoint(order), _adama);
    expect(order.version, 3);
  });

  test('a server row reads the point; one without it clears the copy',
      () async {
    await putOrder(OrderStatus.funded);
    final order = await load();

    applyOrderCommitmentFields(order, {'status': 'funded'});
    expect(orderDeliveryPoint(order), isNull);

    applyOrderCommitmentFields(
      order,
      {'deliveryLatitude': 8.5872, 'deliveryLongitude': 39.1213},
    );
    expect(orderDeliveryPoint(order), _mojo);

    // An older server sent numbers as text.
    applyOrderCommitmentFields(
      order,
      {'deliveryLatitude': '8.5423', 'deliveryLongitude': '39.2687'},
    );
    expect(orderDeliveryPoint(order), _adama);
  });
}
