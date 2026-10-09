import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:isar/isar.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/database/schemas/order.dart';
import 'package:wereda_connect/core/database/schemas/sync_operation.dart';
import 'package:wereda_connect/core/network/api_client.dart';
import 'package:wereda_connect/core/network/api_exception.dart';
import 'package:wereda_connect/core/sync/isar_marketplace_sync.dart';
import 'package:wereda_connect/core/sync/sync_queue.dart';
import 'package:wereda_connect/features/order/data/repositories/order_repository.dart';

class _MockApiClient extends Mock implements ApiClient {}

/// An order as `GET /orders/{id}` and `GET /orders` return it.
Map<String, dynamic> _serverOrder({
  String orderId = 'order-1',
  String status = 'funded',
  int version = 3,
}) =>
    {
      'orderId': orderId,
      'listingId': 'listing-1',
      'buyerId': 'buyer-1',
      'farmerId': 'farmer-1',
      'buyerName': 'Buyer',
      'farmerName': 'Farmer',
      'cropType': 'teff',
      'quantity': 5,
      'pricePerUnit': 60,
      'totalPrice': 300,
      'status': status,
      'createdAt': '2026-10-01T08:00:00.000Z',
      'version': version,
    };

/// ESC-12: a refused order change goes back to the server's view (server
/// wins), and a rejected op no longer keeps `pullOrders` away from the row.
void main() {
  late Isar isar;
  late Directory tempDir;
  late _MockApiClient api;
  late SyncQueue queue;
  late OrderRepository repo;
  late IsarMarketplaceSync sync;

  setUpAll(() async {
    await Isar.initializeIsarCore(download: true);
  });

  setUp(() async {
    tempDir = Directory.systemTemp.createTempSync('marketplace_revert_test_');
    isar = await Isar.open(
      [OrderSchema, SyncOperationSchema],
      directory: tempDir.path,
      name: 'marketplace_revert_${DateTime.now().microsecondsSinceEpoch}',
    );
    api = _MockApiClient();
    queue = SyncQueue(isar);
    repo = OrderRepository(isar: isar, syncQueue: queue);
    sync = IsarMarketplaceSync(api: api, isar: isar);
  });

  tearDown(() async {
    queue.dispose();
    await isar.close(deleteFromDisk: true);
    if (tempDir.existsSync()) tempDir.deleteSync(recursive: true);
  });

  Future<void> putOrder({
    String orderId = 'order-1',
    OrderStatus status = OrderStatus.funded,
    int version = 3,
  }) =>
      isar.writeTxn(() async {
        await isar.orders.put(
          Order()
            ..orderId = orderId
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
            ..version = version,
        );
      });

  Future<Order?> load([String orderId = 'order-1']) =>
      isar.orders.filter().orderIdEqualTo(orderId).findFirst();

  Future<List<SyncOperation>> opsFor(String orderId) =>
      isar.syncOperations.filter().entityIdEqualTo(orderId).findAll();

  /// The farmer accepts offline (local `accepted` at version 4, op queued
  /// with base version 3), then the server refuses it for good.
  Future<SyncOperation> acceptThenReject() async {
    await putOrder();
    await repo.acceptOrder('order-1', 'farmer-1');
    final op = (await opsFor('order-1')).single;
    await queue.markAsRejected(op.operationId);
    return op;
  }

  void answerOrder(Future<Map<String, dynamic>> Function() answer) {
    when(() => api.get<Map<String, dynamic>>('/orders/order-1'))
        .thenAnswer((_) => answer());
  }

  void answerOrderList(List<Map<String, dynamic>> orders) {
    when(
      () => api.get<Map<String, dynamic>>(
        '/orders',
        queryParameters: any(named: 'queryParameters'),
      ),
    ).thenAnswer((_) async => {'orders': orders});
  }

  group('revertRejected', () {
    test('an update goes back to the server order and version', () async {
      final op = await acceptThenReject();
      expect((await load())!.status, OrderStatus.accepted);
      answerOrder(() async => {'order': _serverOrder()});

      await sync.revertRejected(op, 'ORDER_VALIDATION: under review');

      final order = (await load())!;
      expect(order.status, OrderStatus.funded);
      expect(order.version, 3);
      expect(order.respondedAt, isNull);
    });

    test('404 (gone) and 403 (not a party) remove the local order', () async {
      for (final error in <Exception>[
        const NotFoundException('Order not found.'),
        const AuthException('Forbidden', 403, 'ORDER_FORBIDDEN'),
      ]) {
        await isar.writeTxn(() => isar.clear());
        final op = await acceptThenReject();
        answerOrder(() async => throw error);

        await sync.revertRejected(op, 'ORDER_UNAUTHORIZED: no');

        expect(await load(), isNull, reason: '$error');
      }
    });

    test(
        'a failed read (offline, 401, 5xx) lowers the version so the next '
        'pull, not throttled, overwrites the row', () async {
      for (final error in <Exception>[
        const NetworkException(),
        const AuthException('Unauthorized'),
        const ServerException('boom', 500),
      ]) {
        await isar.writeTxn(() => isar.clear());
        sync = IsarMarketplaceSync(api: api, isar: isar);
        answerOrderList([]);
        // A recent pull would normally throttle the next one for 30s.
        await sync.pullOrders(userId: 'farmer-1', role: 'farmer');

        final op = await acceptThenReject();
        answerOrder(() async => throw error);

        await sync.revertRejected(op, 'ORDER_VALIDATION: no');

        final kept = (await load())!;
        expect(kept, isNotNull, reason: '$error');
        expect(kept.version, 3, reason: '$error');

        answerOrderList([_serverOrder()]);
        await sync.pullOrders(userId: 'farmer-1', role: 'farmer');

        expect((await load())!.status, OrderStatus.funded, reason: '$error');
      }
    });

    test('a refused create removes the local order without a read', () async {
      await putOrder(status: OrderStatus.created, version: 1);
      final op = await queue.enqueue(
        userId: 'buyer-1',
        entityType: 'order',
        entityId: 'order-1',
        action: 'create',
        data: '{"orderId":"order-1","version":1}',
        operationId: 'op-create',
      );
      await queue.markAsRejected(op.operationId);

      await sync.revertRejected(op, 'ORDER_VALIDATION: Insufficient stock.');

      expect(await load(), isNull);
      verifyNever(() => api.get<Map<String, dynamic>>('/orders/order-1'));
    });

    test('another op still on its way settles the row instead', () async {
      final op = await acceptThenReject();
      await queue.enqueue(
        userId: 'farmer-1',
        entityType: 'order',
        entityId: 'order-1',
        action: 'update-cancel',
        data: '{"status":"cancelled","version":4}',
        operationId: 'op-cancel',
      );

      await sync.revertRejected(op, 'ORDER_VALIDATION: no');

      expect((await load())!.status, OrderStatus.accepted);
      verifyNever(() => api.get<Map<String, dynamic>>('/orders/order-1'));
    });

    test('other entity types are left alone', () async {
      final op = await queue.enqueue(
        userId: 'trucker-1',
        entityType: 'trip',
        entityId: 'trip-1',
        action: 'update',
        data: '{"version":1}',
        operationId: 'op-trip',
      );

      await sync.revertRejected(op, 'ORDER_VALIDATION: no');

      verifyZeroInteractions(api);
    });
  });

  test(
      'pullOrders overwrites a row whose only op was rejected, and still '
      'protects rows with an op on its way', () async {
    await putOrder(orderId: 'rejected', status: OrderStatus.accepted);
    await putOrder(orderId: 'pending', status: OrderStatus.accepted);
    await putOrder(orderId: 'exhausted', status: OrderStatus.accepted);
    for (final id in ['rejected', 'pending', 'exhausted']) {
      await queue.enqueue(
        userId: 'farmer-1',
        entityType: 'order',
        entityId: id,
        action: 'update',
        data: '{"status":"accepted","version":3}',
        operationId: 'op-$id',
      );
    }
    await queue.markAsRejected('op-rejected');
    await queue.markAsSyncing('op-exhausted');
    for (var i = 0; i < SyncQueue.maxRetries; i++) {
      await queue.markAsFailed('op-exhausted');
    }
    answerOrderList([
      for (final id in ['rejected', 'pending', 'exhausted'])
        _serverOrder(orderId: id),
    ]);

    await sync.pullOrders(userId: 'farmer-1', role: 'farmer');

    expect((await load('rejected'))!.status, OrderStatus.funded);
    expect((await load('pending'))!.status, OrderStatus.accepted);
    expect((await load('exhausted'))!.status, OrderStatus.accepted);
  });
}
