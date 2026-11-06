import 'dart:convert';
import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:isar/isar.dart';

import 'package:wereda_connect/core/database/schemas/order.dart';
import 'package:wereda_connect/core/database/schemas/sync_operation.dart';
import 'package:wereda_connect/core/errors/app_error.dart';
import 'package:wereda_connect/core/models/order_commitment.dart';
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

/// LT-12 on mobile (Isar and the sync queue): the drop-off landmark goes out
/// with the order create, trimmed and only when set; the buyer changes it
/// offline-first until pickup with the pre-change version; nobody else can;
/// and a server row without it (an uncommitted trucker's) clears the copy.
void main() {
  late Isar isar;
  late Directory tempDir;
  late SyncQueue queue;
  late OrderRepository repo;

  setUpAll(() async {
    await Isar.initializeIsarCore(download: true);
  });

  setUp(() async {
    tempDir = Directory.systemTemp.createTempSync('order_dropoff_note_test_');
    isar = await Isar.open(
      [OrderSchema, SyncOperationSchema],
      directory: tempDir.path,
      name: 'order_dropoff_note_${DateTime.now().microsecondsSinceEpoch}',
    );
    queue = SyncQueue(isar);
    repo = OrderRepository(isar: isar, syncQueue: queue);
  });

  tearDown(() async {
    queue.dispose();
    await isar.close(deleteFromDisk: true);
    if (tempDir.existsSync()) tempDir.deleteSync(recursive: true);
  });

  Future<void> putOrder(
    OrderStatus status, {
    String? note = 'Merkato',
    bool trip = true,
  }) =>
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
            ..dropoffNote = note,
        );
      });

  Future<Order> loadId(String orderId) async =>
      (await isar.orders.filter().orderIdEqualTo(orderId).findFirst())!;

  Future<Order> load() => loadId('order-1');

  Future<List<SyncOperation>> ops() => isar.syncOperations.where().findAll();

  Map<String, dynamic> dataOf(SyncOperation op) =>
      jsonDecode(op.data) as Map<String, dynamic>;

  Future<Order> create(String? note) => repo.createOrder(
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
        dropoffNote: note,
      );

  group('order create', () {
    test('saves and queues the note trimmed', () async {
      final order = await create('  Merkato, near Anwar Mosque ');

      expect(order.dropoffNote, 'Merkato, near Anwar Mosque');
      expect(
        (await loadId(order.orderId)).dropoffNote,
        'Merkato, near Anwar Mosque',
      );
      final data = dataOf((await ops()).single);
      expect(data['dropoffNote'], 'Merkato, near Anwar Mosque');
    });

    test('a blank note is not sent', () async {
      final order = await create('   ');

      expect((await loadId(order.orderId)).dropoffNote, isNull);
      expect(dataOf((await ops()).single).containsKey('dropoffNote'), isFalse);
    });

    test('an Amharic note is kept as typed', () async {
      await create('መርካቶ፣ አንዋር መስጊድ አጠገብ');

      expect(
        dataOf((await ops()).single)['dropoffNote'],
        'መርካቶ፣ አንዋር መስጊድ አጠገብ',
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

        await repo.updateDropoffNote('order-1', 'buyer-1', ' Kality gate 2 ');

        final order = await load();
        expect(order.dropoffNote, 'Kality gate 2');
        expect(order.version, 4);
        final op = (await ops()).single;
        expect(op.entityType, 'order');
        expect(op.action, 'update');
        final data = dataOf(op);
        expect(data['dropoffNote'], 'Kality gate 2');
        // The pre-change version: the server checks it.
        expect(data['version'], 3);
        expect(data.containsKey('status'), isFalse);
      });
    }

    test('clearing sends an empty note', () async {
      await putOrder(OrderStatus.accepted);

      await repo.updateDropoffNote('order-1', 'buyer-1', '  ');

      expect((await load()).dropoffNote, isNull);
      expect(dataOf((await ops()).single)['dropoffNote'], '');
    });

    test('the same note again is a no-op', () async {
      await putOrder(OrderStatus.accepted);

      await repo.updateDropoffNote('order-1', 'buyer-1', ' Merkato ');

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
      expect(order.dropoffNote, 'Merkato');
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
          () => repo.updateDropoffNote('order-1', 'buyer-1', 'Kality'),
        );
      });
    }

    test('the farmer and the trucker', () async {
      await putOrder(OrderStatus.accepted);
      await expectRefused(
        () => repo.updateDropoffNote('order-1', 'farmer-1', 'Kality'),
      );
      await expectRefused(
        () => repo.updateDropoffNote('order-1', 'trucker-1', 'Kality'),
      );
    });

    test('a self-pickup order has no drop-off', () async {
      await putOrder(OrderStatus.accepted, trip: false);
      await expectRefused(
        () => repo.updateDropoffNote('order-1', 'buyer-1', 'Kality'),
      );
    });
  });

  test('a failed enqueue puts the note back and rethrows', () async {
    repo = OrderRepository(isar: isar, syncQueue: _FullSyncQueue(isar));
    await putOrder(OrderStatus.accepted);

    await expectLater(
      repo.updateDropoffNote('order-1', 'buyer-1', 'Kality'),
      throwsA(isA<SyncQueueFullException>()),
    );

    final order = await load();
    expect(order.dropoffNote, 'Merkato');
    expect(order.version, 3);
  });

  test('a server row without the note clears the device copy', () async {
    await putOrder(OrderStatus.funded);
    final order = await load();

    applyOrderCommitmentFields(order, {'status': 'funded'});

    expect(order.dropoffNote, isNull);
    applyOrderCommitmentFields(order, {'dropoffNote': 'Kality'});
    expect(order.dropoffNote, 'Kality');
  });
}
