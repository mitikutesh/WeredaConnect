import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:isar/isar.dart';

import 'package:wereda_connect/core/database/schemas/order.dart';
import 'package:wereda_connect/core/database/schemas/sync_operation.dart';
import 'package:wereda_connect/core/sync/sync_queue.dart';
import 'package:wereda_connect/features/order/data/repositories/order_repository.dart';

/// E: a payment submission the KYC gate rejected must not leave the order
/// stuck in paymentSubmitted.
void main() {
  late Isar isar;
  late Directory tempDir;
  late OrderRepository repo;

  setUpAll(() async {
    await Isar.initializeIsarCore(download: true);
  });

  setUp(() async {
    tempDir = Directory.systemTemp.createTempSync('order_kyc_revert_test_');
    isar = await Isar.open(
      [OrderSchema, SyncOperationSchema],
      directory: tempDir.path,
      name: 'order_kyc_${DateTime.now().microsecondsSinceEpoch}',
    );
    repo = OrderRepository(isar: isar, syncQueue: SyncQueue(isar));
    await isar.writeTxn(() async {
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
          ..status = OrderStatus.accepted
          ..createdAt = DateTime.utc(2026, 9, 20)
          ..respondedAt = DateTime.utc(2026, 9, 21)
          ..version = 3,
      );
    });
  });

  tearDown(() async {
    await isar.close(deleteFromDisk: true);
    if (tempDir.existsSync()) tempDir.deleteSync(recursive: true);
  });

  Future<Order> load() async =>
      (await isar.orders.filter().orderIdEqualTo('order-1').findFirst())!;

  test('submit then KYC rejection goes back to accepted at the old version',
      () async {
    await repo.submitPayment('order-1', 'MOCK-CBE-1');
    expect((await load()).status, OrderStatus.paymentSubmitted);
    expect((await load()).version, 4);

    await repo.revertRejectedPaymentSubmission('order-1');

    final order = await load();
    expect(order.status, OrderStatus.accepted);
    expect(order.version, 3);
    expect(order.cbeTransactionId, isNull);
    expect(order.paymentMethod, isNull);
  });

  test('an order paid right after placing goes back to created', () async {
    await isar.writeTxn(() async {
      final order = await load();
      order
        ..status = OrderStatus.created
        ..respondedAt = null;
      await isar.orders.put(order);
    });

    await repo.submitPayment('order-1', 'MOCK-CBE-2');
    await repo.revertRejectedPaymentSubmission('order-1');

    final order = await load();
    expect(order.status, OrderStatus.created);
    expect(order.version, 3);
  });

  test('an order that already moved on is left alone', () async {
    await isar.writeTxn(() async {
      final order = await load();
      order.status = OrderStatus.funded;
      await isar.orders.put(order);
    });

    await repo.revertRejectedPaymentSubmission('order-1');

    expect((await load()).status, OrderStatus.funded);
    expect((await load()).version, 3);
  });
}
