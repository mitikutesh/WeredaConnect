import 'dart:convert';
import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:isar/isar.dart';

import 'package:wereda_connect/core/database/schemas/order.dart';
import 'package:wereda_connect/core/database/schemas/sync_operation.dart';
import 'package:wereda_connect/core/errors/app_error.dart';
import 'package:wereda_connect/core/network/api_exception.dart';
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

/// ESC-12: the order actions refuse out loud instead of reporting a success
/// that never reaches the server.
void main() {
  late Isar isar;
  late Directory tempDir;
  late SyncQueue queue;
  late OrderRepository repo;

  setUpAll(() async {
    await Isar.initializeIsarCore(download: true);
  });

  setUp(() async {
    tempDir = Directory.systemTemp.createTempSync('order_guards_test_');
    isar = await Isar.open(
      [OrderSchema, SyncOperationSchema],
      directory: tempDir.path,
      name: 'order_guards_${DateTime.now().microsecondsSinceEpoch}',
    );
    queue = SyncQueue(isar);
    repo = OrderRepository(isar: isar, syncQueue: queue);
  });

  tearDown(() async {
    queue.dispose();
    await isar.close(deleteFromDisk: true);
    if (tempDir.existsSync()) tempDir.deleteSync(recursive: true);
  });

  Future<void> putOrder(OrderStatus status, {DateTime? pickupDeadlineAt}) =>
      isar.writeTxn(() async {
        await isar.orders.put(
          Order()
            ..pickupDeadlineAt = pickupDeadlineAt
            ..orderId = 'order-1'
            ..listingId = 'listing-1'
            ..buyerId = 'buyer-1'
            ..farmerId = 'farmer-1'
            ..buyerName = 'Buyer'
            ..farmerName = 'Farmer'
            ..cropType = 'teff'
            ..quantity = 5
            ..pricePerUnit = 60
            ..totalPrice = 300
            ..status = status
            ..createdAt = DateTime.utc(2026, 10)
            ..version = 3,
        );
      });

  Future<Order> load() async =>
      (await isar.orders.filter().orderIdEqualTo('order-1').findFirst())!;

  Future<int> queued() => isar.syncOperations.count();

  /// Nothing was written or queued.
  Future<void> expectUntouched(OrderStatus status) async {
    final order = await load();
    expect(order.status, status);
    expect(order.version, 3);
    expect(await queued(), 0);
  }

  group('a missing order throws NotFoundException', () {
    final actions = <String, Future<void> Function(OrderRepository)>{
      'submitPayment': (r) => r.submitPayment('order-x', 'REF-1'),
      'cancelOrder': (r) => r.cancelOrder('order-x', 'buyer-1'),
      'acceptOrder': (r) => r.acceptOrder('order-x', 'farmer-1'),
      'rejectOrder': (r) => r.rejectOrder('order-x', 'farmer-1'),
    };
    for (final entry in actions.entries) {
      test(entry.key, () async {
        await expectLater(
          entry.value(repo),
          throwsA(isA<NotFoundException>()),
        );
        expect(await queued(), 0);
      });
    }
  });

  group('the wrong party or status throws OrderActionNotAllowedException', () {
    test('accept or reject by someone who is not the farmer', () async {
      await putOrder(OrderStatus.funded);

      await expectLater(
        repo.acceptOrder('order-1', 'farmer-2'),
        throwsA(isA<OrderActionNotAllowedException>()),
      );
      await expectLater(
        repo.rejectOrder('order-1', 'buyer-1'),
        throwsA(isA<OrderActionNotAllowedException>()),
      );
      await expectUntouched(OrderStatus.funded);
    });

    test('accept or reject an order that is not funded', () async {
      await putOrder(OrderStatus.created);

      await expectLater(
        repo.acceptOrder('order-1', 'farmer-1'),
        throwsA(isA<OrderActionNotAllowedException>()),
      );
      await expectLater(
        repo.rejectOrder('order-1', 'farmer-1'),
        throwsA(isA<OrderActionNotAllowedException>()),
      );
      await expectUntouched(OrderStatus.created);
    });

    test('cancel by someone who is neither buyer nor farmer', () async {
      await putOrder(OrderStatus.created);

      await expectLater(
        repo.cancelOrder('order-1', 'trucker-1'),
        throwsA(isA<OrderActionNotAllowedException>()),
      );
      await expectUntouched(OrderStatus.created);
    });

    test('the refusal reads as orderRefused', () {
      expect(
        mapErrorToCode(const OrderActionNotAllowedException('x')),
        AppErrorCode.orderRefused,
      );
    });
  });

  group('repeating what the order already shows is a no-op success', () {
    final cases = <OrderStatus, Future<void> Function(OrderRepository)>{
      OrderStatus.accepted: (r) => r.acceptOrder('order-1', 'farmer-1'),
      OrderStatus.rejected: (r) => r.rejectOrder('order-1', 'farmer-1'),
      OrderStatus.cancelled: (r) => r.cancelOrder('order-1', 'buyer-1'),
      OrderStatus.paymentSubmitted: (r) => r.submitPayment('order-1', 'REF-1'),
    };
    for (final entry in cases.entries) {
      test(entry.key.name, () async {
        await putOrder(entry.key);

        await entry.value(repo);

        await expectUntouched(entry.key);
      });
    }
  });

  group('a failed enqueue puts the order back and rethrows', () {
    setUp(() {
      repo = OrderRepository(isar: isar, syncQueue: _FullSyncQueue(isar));
    });

    final cases =
        <String, (OrderStatus, Future<void> Function(OrderRepository))>{
      'submitPayment': (
        OrderStatus.created,
        (r) => r.submitPayment('order-1', 'REF-1', paymentMethod: 'telebirr'),
      ),
      'cancelOrder': (
        OrderStatus.created,
        (r) => r.cancelOrder('order-1', 'buyer-1', reason: 'changed mind'),
      ),
      'acceptOrder': (
        OrderStatus.funded,
        (r) => r.acceptOrder('order-1', 'farmer-1'),
      ),
      'rejectOrder': (
        OrderStatus.funded,
        (r) => r.rejectOrder('order-1', 'farmer-1', reason: 'no stock'),
      ),
    };
    for (final entry in cases.entries) {
      test(entry.key, () async {
        final (status, action) = entry.value;
        await putOrder(status);

        await expectLater(
          action(repo),
          throwsA(isA<SyncQueueFullException>()),
        );

        final order = await load();
        expect(order.status, status);
        expect(order.version, 3);
        expect(order.respondedAt, isNull);
        expect(order.rejectionReason, isNull);
        expect(order.cancelledAt, isNull);
        expect(order.cancelReason, isNull);
        expect(order.cbeTransactionId, isNull);
        expect(order.paymentMethod, isNull);
        expect(await queued(), 0);
      });
    }
  });

  test('a valid accept still writes and queues as before', () async {
    await putOrder(OrderStatus.funded);

    await repo.acceptOrder('order-1', 'farmer-1');

    final order = await load();
    expect(order.status, OrderStatus.accepted);
    expect(order.version, 4);
    final op = (await isar.syncOperations.where().findAll()).single;
    expect(op.data, contains('"version":3'));
    expect(op.status, SyncStatus.pending);
  });

  group('payments (ESC-44)', () {
    Future<Map<String, dynamic>> onlyQueuedPayload() async {
      final ops = await isar.syncOperations.where().findAll();
      expect(ops, hasLength(1));
      return jsonDecode(ops.single.data) as Map<String, dynamic>;
    }

    test('a live Telebirr payment sends no reference', () async {
      await putOrder(OrderStatus.created);

      await repo.submitPayment('order-1', null, paymentMethod: 'telebirr');

      final data = await onlyQueuedPayload();
      expect(data['status'], 'payment_submitted');
      expect(data['paymentMethod'], 'telebirr');
      expect(data.containsKey('cbeTransactionId'), isFalse);
      final order = await load();
      expect(order.status, OrderStatus.paymentSubmitted);
      expect(order.cbeTransactionId, isNull);
    });

    test('a retry after payment_failed is one payment_submitted change',
        () async {
      await putOrder(OrderStatus.paymentFailed);

      await repo.submitPayment('order-1', null, paymentMethod: 'telebirr');

      final data = await onlyQueuedPayload();
      expect(data['status'], 'payment_submitted');
      // The version the client had: the server checks it.
      expect(data['version'], 3);
      final order = await load();
      expect(order.status, OrderStatus.paymentSubmitted);
      expect(order.version, 4);
    });
  });

  group('cancel of an accepted order (ESC-06)', () {
    Future<Map<String, dynamic>> onlyQueuedPayload() async {
      final ops = await isar.syncOperations.where().findAll();
      expect(ops, hasLength(1));
      return jsonDecode(ops.single.data) as Map<String, dynamic>;
    }

    test('before the pickup deadline it is refused locally', () async {
      await putOrder(
        OrderStatus.accepted,
        pickupDeadlineAt: DateTime.now().toUtc().add(const Duration(hours: 3)),
      );

      Object? error;
      try {
        await repo.cancelOrder('order-1', 'buyer-1');
      } on Object catch (e) {
        error = e;
      }

      expect(error, isA<OrderCancelNotAllowedException>());
      expect(mapErrorToCode(error!), AppErrorCode.orderCancelNotAllowed);
      await expectUntouched(OrderStatus.accepted);
    });

    test('without a deadline (accepted before ESC-06) it is refused locally',
        () async {
      await putOrder(OrderStatus.accepted);

      await expectLater(
        repo.cancelOrder('order-1', 'buyer-1'),
        throwsA(isA<OrderCancelNotAllowedException>()),
      );
      await expectUntouched(OrderStatus.accepted);
    });

    test('after the deadline it writes and queues the cancel', () async {
      await putOrder(
        OrderStatus.accepted,
        pickupDeadlineAt: DateTime.now().toUtc().subtract(
              const Duration(minutes: 5),
            ),
      );

      await repo.cancelOrder('order-1', 'buyer-1', reason: 'No truck came');

      final data = await onlyQueuedPayload();
      expect(data['status'], 'cancelled');
      expect(data['cancelReason'], 'No truck came');
      expect(data['version'], 3);
      expect((await load()).status, OrderStatus.cancelled);
    });
  });
}
