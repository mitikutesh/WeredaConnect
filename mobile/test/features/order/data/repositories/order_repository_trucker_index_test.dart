import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:isar/isar.dart';

import 'package:wereda_connect/core/database/schemas/order.dart';
import 'package:wereda_connect/core/database/schemas/sync_operation.dart';
import 'package:wereda_connect/core/sync/sync_queue.dart';
import 'package:wereda_connect/features/order/data/repositories/order_repository.dart';

/// The `Order` schema as shipped before ESC-22: no `truckerId` index. Same
/// collection id, properties and (de)serializers, so an older build's rows
/// read back unchanged. The (de)serializers are `@protected` on `Schema`;
/// reusing the generated ones is the point here, so the lint is silenced.
final _schemaBeforeTruckerIndex = CollectionSchema<Order>(
  id: OrderSchema.id,
  name: OrderSchema.name,
  properties: OrderSchema.properties,
  // ignore: invalid_use_of_protected_member
  estimateSize: OrderSchema.estimateSize,
  // ignore: invalid_use_of_protected_member
  serialize: OrderSchema.serialize,
  // ignore: invalid_use_of_protected_member
  deserialize: OrderSchema.deserialize,
  // ignore: invalid_use_of_protected_member
  deserializeProp: OrderSchema.deserializeProp,
  idName: OrderSchema.idName,
  indexes: Map.of(OrderSchema.indexes)..remove('truckerId'),
  links: OrderSchema.links,
  embeddedSchemas: OrderSchema.embeddedSchemas,
  getId: OrderSchema.getId,
  getLinks: OrderSchema.getLinks,
  attach: OrderSchema.attach,
  version: OrderSchema.version,
);

Order _order(
  String orderId, {
  required DateTime createdAt,
  String buyerId = 'buyer-1',
  String farmerId = 'farmer-1',
  String? truckerId,
  OrderStatus status = OrderStatus.funded,
}) =>
    Order()
      ..orderId = orderId
      ..listingId = 'listing-$orderId'
      ..buyerId = buyerId
      ..farmerId = farmerId
      ..buyerName = 'Buyer'
      ..farmerName = 'Farmer'
      ..cropType = 'teff'
      ..quantity = 5
      ..pricePerUnit = 60
      ..totalPrice = 300
      ..status = status
      ..createdAt = createdAt
      ..version = 1
      ..tripId = truckerId == null ? null : 'trip-$truckerId'
      ..truckerId = truckerId
      ..truckerName = truckerId == null ? null : 'Trucker';

/// Orders on two truckers' trips, a self-pickup order with no trucker, and
/// an order where trucker-1's id shows up only as the buyer (a stray id
/// match the trucker query must not pick up). Fresh objects per call: Isar
/// stamps the id on each object it stores.
List<Order> _seed() => [
      _order(
        't1-old',
        truckerId: 'trucker-1',
        createdAt: DateTime.utc(2026, 9),
      ),
      _order(
        't1-new',
        truckerId: 'trucker-1',
        status: OrderStatus.accepted,
        createdAt: DateTime.utc(2026, 11),
      ),
      _order(
        't2',
        buyerId: 'buyer-2',
        farmerId: 'farmer-2',
        truckerId: 'trucker-2',
        createdAt: DateTime.utc(2026, 10),
      ),
      _order('pickup', createdAt: DateTime.utc(2026, 10, 2)),
      _order(
        'empty-trucker',
        truckerId: '',
        createdAt: DateTime.utc(2026, 10, 3),
      ),
      _order(
        'as-buyer',
        buyerId: 'trucker-1',
        farmerId: 'farmer-2',
        createdAt: DateTime.utc(2026, 10, 4),
      ),
    ];

List<String> _ids(List<Order> orders) => [for (final o in orders) o.orderId];

/// ESC-22: the trucker's order reads go through the `truckerId` index. The
/// result must stay exactly the trucker's own orders (role visibility).
void main() {
  setUpAll(() async {
    await Isar.initializeIsarCore(download: true);
  });

  test('the Order schema indexes truckerId', () {
    expect(OrderSchema.indexes.keys, contains('truckerId'));
  });

  group('getOrdersByUser', () {
    late Isar isar;
    late Directory tempDir;
    late SyncQueue queue;
    late OrderRepository repo;

    setUp(() async {
      tempDir = Directory.systemTemp.createTempSync('order_trucker_index_');
      isar = await Isar.open(
        [OrderSchema, SyncOperationSchema],
        directory: tempDir.path,
        name: 'order_trucker_index_${DateTime.now().microsecondsSinceEpoch}',
      );
      queue = SyncQueue(isar);
      repo = OrderRepository(isar: isar, syncQueue: queue);
      await isar.writeTxn(() => isar.orders.putAll(_seed()));
    });

    tearDown(() async {
      queue.dispose();
      await isar.close(deleteFromDisk: true);
      if (tempDir.existsSync()) tempDir.deleteSync(recursive: true);
    });

    test('a trucker gets only the orders it carries, newest first', () async {
      final orders = await repo.getOrdersByUser('trucker-1', role: 'trucker');

      expect(_ids(orders), ['t1-new', 't1-old']);
    });

    test('another trucker gets only its own order', () async {
      final orders = await repo.getOrdersByUser('trucker-2', role: 'trucker');

      expect(_ids(orders), ['t2']);
    });

    test('the status filter narrows within the trucker\'s orders', () async {
      final accepted = await repo.getOrdersByUser(
        'trucker-1',
        role: 'trucker',
        statusFilter: OrderStatus.accepted.name,
      );
      final funded = await repo.getOrdersByUser(
        'trucker-1',
        role: 'trucker',
        statusFilter: OrderStatus.funded.name,
      );
      final rejected = await repo.getOrdersByUser(
        'trucker-1',
        role: 'trucker',
        statusFilter: OrderStatus.rejected.name,
      );

      expect(_ids(accepted), ['t1-new']);
      expect(_ids(funded), ['t1-old']);
      expect(rejected, isEmpty);
    });

    test('an unknown trucker id gets nothing', () async {
      expect(
        await repo.getOrdersByUser('trucker-9', role: 'trucker'),
        isEmpty,
      );
    });

    test('the farmer and buyer reads are unchanged', () async {
      final farmer = await repo.getOrdersByUser('farmer-2', role: 'farmer');
      final buyer = await repo.getOrdersByUser('buyer-2', role: 'buyer');

      expect(_ids(farmer), ['as-buyer', 't2']);
      expect(_ids(buyer), ['t2']);
    });
  });

  test('rows written before the index exist are found after the upgrade',
      () async {
    final dir = Directory.systemTemp.createTempSync('order_trucker_upgrade_');
    addTearDown(() {
      if (dir.existsSync()) dir.deleteSync(recursive: true);
    });
    final name =
        'order_trucker_upgrade_${DateTime.now().microsecondsSinceEpoch}';

    final before = await Isar.open(
      [_schemaBeforeTruckerIndex, SyncOperationSchema],
      directory: dir.path,
      name: name,
    );
    await before.writeTxn(() => before.collection<Order>().putAll(_seed()));
    await before.close();

    // The new build opens the same file: Isar builds the new index over the
    // stored rows on open.
    final isar = await Isar.open(
      [OrderSchema, SyncOperationSchema],
      directory: dir.path,
      name: name,
    );
    final queue = SyncQueue(isar);
    addTearDown(() async {
      queue.dispose();
      await isar.close(deleteFromDisk: true);
    });
    final repo = OrderRepository(isar: isar, syncQueue: queue);

    expect(await isar.orders.where().truckerIdEqualTo('trucker-1').count(), 2);
    expect(
      _ids(await repo.getOrdersByUser('trucker-1', role: 'trucker')),
      ['t1-new', 't1-old'],
    );
    expect(
      _ids(await repo.getOrdersByUser('trucker-2', role: 'trucker')),
      ['t2'],
    );
  });
}
