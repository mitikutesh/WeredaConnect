import 'dart:convert';
import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:isar/isar.dart';

import 'package:wereda_connect/core/database/schemas/order.dart';
import 'package:wereda_connect/core/database/schemas/sync_operation.dart';
import 'package:wereda_connect/core/errors/app_error.dart';
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

/// ESC-15: the farmer is asked first, then the trucker. The repository sends
/// the commands (sync-protocol.md 1.29.0) and shows the sequential result
/// until the next pull; on a server without commitments (no `fulfillment`
/// stamp) the farmer's accept is final as before.
void main() {
  late Isar isar;
  late Directory tempDir;
  late SyncQueue queue;
  late OrderRepository repo;

  setUpAll(() async {
    await Isar.initializeIsarCore(download: true);
  });

  setUp(() async {
    tempDir = Directory.systemTemp.createTempSync('order_commitment_test_');
    isar = await Isar.open(
      [OrderSchema, SyncOperationSchema],
      directory: tempDir.path,
      name: 'order_commitment_${DateTime.now().microsecondsSinceEpoch}',
    );
    queue = SyncQueue(isar);
    repo = OrderRepository(isar: isar, syncQueue: queue);
  });

  tearDown(() async {
    queue.dispose();
    await isar.close(deleteFromDisk: true);
    if (tempDir.existsSync()) tempDir.deleteSync(recursive: true);
  });

  /// A paid trip order. [commitments]: the server stamped it at funding.
  Future<void> putOrder(
    OrderStatus status, {
    bool trip = true,
    bool commitments = true,
    String? farmerResponse,
    String? truckerResponse,
    DateTime? pickupDeadlineAt,
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
            ..truckerName = trip ? 'Trucker' : null
            ..fulfillment =
                commitments ? (trip ? 'trucker' : 'self_pickup') : null
            ..farmerResponse = farmerResponse
            ..truckerResponse = truckerResponse
            ..pickupDeadlineAt = pickupDeadlineAt
            ..contactOpen = true
            ..truckerPhone = '+251900100002',
        );
      });

  Future<Order> load() async =>
      (await isar.orders.filter().orderIdEqualTo('order-1').findFirst())!;

  Future<Map<String, dynamic>> onlyQueuedPayload() async {
    final ops = await isar.syncOperations.where().findAll();
    expect(ops, hasLength(1));
    return jsonDecode(ops.single.data) as Map<String, dynamic>;
  }

  Future<void> expectUntouched(OrderStatus status) async {
    final order = await load();
    expect(order.status, status);
    expect(order.version, 3);
    expect(await isar.syncOperations.count(), 0);
  }

  group('farmer', () {
    test('accepting a trip order sends the command and asks the trucker',
        () async {
      await putOrder(
        OrderStatus.funded,
        farmerResponse: 'pending',
        truckerResponse: 'not_asked',
      );

      await repo.acceptOrder('order-1', 'farmer-1');

      final data = await onlyQueuedPayload();
      expect(data['farmerResponse'], 'accept');
      expect(data.containsKey('status'), isFalse);
      expect(data['version'], 3);
      final order = await load();
      expect(order.status, OrderStatus.funded);
      expect(order.farmerResponse, 'accepted');
      expect(order.truckerResponse, 'pending');
      expect(order.version, 4);
    });

    test('accepting a self-pickup order agrees it at once', () async {
      await putOrder(
        OrderStatus.funded,
        trip: false,
        farmerResponse: 'pending',
      );

      await repo.acceptOrder('order-1', 'farmer-1');

      expect((await onlyQueuedPayload())['farmerResponse'], 'accept');
      expect((await load()).status, OrderStatus.accepted);
    });

    test('without commitments on the server the accept is final as before',
        () async {
      await putOrder(OrderStatus.funded, commitments: false);

      await repo.acceptOrder('order-1', 'farmer-1');

      final order = await load();
      expect(order.status, OrderStatus.accepted);
      expect(order.truckerResponse, isNull);
    });

    test('accepting after the trip filled up lands with the buyer', () async {
      await putOrder(
        OrderStatus.funded,
        farmerResponse: 'pending',
        truckerResponse: 'failed_capacity',
      );

      await repo.acceptOrder('order-1', 'farmer-1');

      expect((await load()).status, OrderStatus.awaitingTransport);
    });

    test('a second accept while the trucker is asked is a no-op', () async {
      await putOrder(
        OrderStatus.funded,
        farmerResponse: 'accepted',
        truckerResponse: 'pending',
      );

      await repo.acceptOrder('order-1', 'farmer-1');

      await expectUntouched(OrderStatus.funded);
    });

    test('declining sends the decline command and ends the order', () async {
      await putOrder(
        OrderStatus.funded,
        farmerResponse: 'pending',
        truckerResponse: 'not_asked',
      );

      await repo.rejectOrder('order-1', 'farmer-1', reason: 'No stock');

      final data = await onlyQueuedPayload();
      expect(data['farmerResponse'], 'decline');
      expect(data['rejectionReason'], 'No stock');
      expect(data.containsKey('status'), isFalse);
      final order = await load();
      expect(order.status, OrderStatus.rejected);
      expect(order.farmerResponse, 'declined');
      expect(order.cancelledBy, 'farmer');
    });

    test('declining with a reason sends the reason code', () async {
      await putOrder(
        OrderStatus.funded,
        farmerResponse: 'pending',
        truckerResponse: 'not_asked',
      );

      await repo.rejectOrder(
        'order-1',
        'farmer-1',
        declineReason: 'crop_not_ready',
      );

      final data = await onlyQueuedPayload();
      expect(data['farmerResponse'], 'decline');
      expect(data['declineReason'], 'crop_not_ready');
      expect((await load()).declineReason, 'crop_not_ready');
    });

    test('a failed enqueue puts the answer back', () async {
      repo = OrderRepository(isar: isar, syncQueue: _FullSyncQueue(isar));
      await putOrder(
        OrderStatus.funded,
        farmerResponse: 'pending',
        truckerResponse: 'not_asked',
      );

      await expectLater(
        repo.acceptOrder('order-1', 'farmer-1'),
        throwsA(isA<SyncQueueFullException>()),
      );

      final order = await load();
      expect(order.farmerResponse, 'pending');
      expect(order.truckerResponse, 'not_asked');
      expect(order.version, 3);
    });
  });

  group('trucker', () {
    test('cannot answer before the farmer accepted', () async {
      await putOrder(
        OrderStatus.funded,
        farmerResponse: 'pending',
        truckerResponse: 'not_asked',
      );

      for (final accept in [true, false]) {
        await expectLater(
          repo.respondAsTrucker('order-1', 'trucker-1', accept: accept),
          throwsA(isA<OrderActionNotAllowedException>()),
        );
      }
      await expectUntouched(OrderStatus.funded);
    });

    test('someone else cannot answer for the trucker', () async {
      await putOrder(
        OrderStatus.funded,
        farmerResponse: 'accepted',
        truckerResponse: 'pending',
      );

      for (final userId in ['farmer-1', 'buyer-1', 'trucker-2', '']) {
        await expectLater(
          repo.respondAsTrucker('order-1', userId, accept: true),
          throwsA(isA<OrderActionNotAllowedException>()),
        );
      }
      await expectUntouched(OrderStatus.funded);
    });

    test('accepting after the farmer agrees the order', () async {
      await putOrder(
        OrderStatus.funded,
        farmerResponse: 'accepted',
        truckerResponse: 'pending',
      );

      await repo.respondAsTrucker('order-1', 'trucker-1', accept: true);

      final data = await onlyQueuedPayload();
      expect(data['truckerResponse'], 'accept');
      expect(data['version'], 3);
      expect(data.containsKey('declineReason'), isFalse);
      final order = await load();
      expect(order.status, OrderStatus.accepted);
      expect(order.truckerResponse, 'accepted');
    });

    test('declining with a reason leaves the order with the buyer', () async {
      await putOrder(
        OrderStatus.funded,
        farmerResponse: 'accepted',
        truckerResponse: 'pending',
      );

      await repo.respondAsTrucker(
        'order-1',
        'trucker-1',
        accept: false,
        declineReason: 'capacity',
      );

      final data = await onlyQueuedPayload();
      expect(data['truckerResponse'], 'decline');
      expect(data['declineReason'], 'capacity');
      final order = await load();
      expect(order.status, OrderStatus.awaitingTransport);
      expect(order.truckerResponse, 'declined');
      expect(order.transportFailure, 'trucker_declined');
      expect(order.declineReason, 'capacity');
      // The trucker leaves the contact at once.
      expect(order.contactOpen, isFalse);
      expect(order.truckerPhone, isNull);
    });

    test('dropping out of an accepted order before pickup', () async {
      await putOrder(
        OrderStatus.accepted,
        farmerResponse: 'accepted',
        truckerResponse: 'accepted',
      );

      await repo.respondAsTrucker(
        'order-1',
        'trucker-1',
        accept: false,
        declineReason: 'date',
      );

      expect((await onlyQueuedPayload())['truckerResponse'], 'decline');
      final order = await load();
      expect(order.status, OrderStatus.awaitingTransport);
      expect(order.truckerResponse, 'cancelled');
      expect(order.transportFailure, 'trucker_cancelled');
    });

    test('no drop-out on a server without commitments', () async {
      await putOrder(OrderStatus.accepted, commitments: false);

      await expectLater(
        repo.respondAsTrucker('order-1', 'trucker-1', accept: false),
        throwsA(isA<OrderActionNotAllowedException>()),
      );
      await expectUntouched(OrderStatus.accepted);
    });

    test('a repeated accept is a no-op', () async {
      await putOrder(
        OrderStatus.accepted,
        farmerResponse: 'accepted',
        truckerResponse: 'accepted',
      );

      await repo.respondAsTrucker('order-1', 'trucker-1', accept: true);

      await expectUntouched(OrderStatus.accepted);
    });
  });

  group('cancel rules', () {
    test('the buyer cannot cancel a funded order while the parties decide',
        () async {
      await putOrder(OrderStatus.funded, farmerResponse: 'pending');

      Object? error;
      try {
        await repo.cancelOrder('order-1', 'buyer-1');
      } on Object catch (e) {
        error = e;
      }

      expect(error, isA<OrderCancelNotAllowedException>());
      expect(mapErrorToCode(error!), AppErrorCode.orderCancelNotAllowed);
      await expectUntouched(OrderStatus.funded);
    });

    test('without commitments the buyer may still cancel a funded order',
        () async {
      await putOrder(OrderStatus.funded, commitments: false);

      await repo.cancelOrder('order-1', 'buyer-1');

      expect((await onlyQueuedPayload())['status'], 'cancelled');
    });

    test('the buyer cancels after the transport fell through', () async {
      await putOrder(
        OrderStatus.awaitingTransport,
        farmerResponse: 'accepted',
        truckerResponse: 'declined',
      );

      await repo.cancelOrder('order-1', 'buyer-1');

      expect((await load()).status, OrderStatus.cancelled);
    });

    test('the farmer cancels an awaiting or an accepted order', () async {
      for (final status in [
        OrderStatus.awaitingTransport,
        OrderStatus.accepted,
      ]) {
        await isar.writeTxn(() async {
          await isar.syncOperations.clear();
          await isar.orders.clear();
        });
        await putOrder(status, farmerResponse: 'accepted');

        await repo.cancelOrder('order-1', 'farmer-1');

        expect((await onlyQueuedPayload())['status'], 'cancelled');
      }
    });

    test('without commitments the farmer cannot cancel an accepted order',
        () async {
      await putOrder(OrderStatus.accepted, commitments: false);

      await expectLater(
        repo.cancelOrder('order-1', 'farmer-1'),
        throwsA(isA<OrderCancelNotAllowedException>()),
      );
      await expectUntouched(OrderStatus.accepted);
    });
  });
}
