import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:isar/isar.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/database/schemas/crop_listing.dart';
import 'package:wereda_connect/core/database/schemas/delivery.dart';
import 'package:wereda_connect/core/database/schemas/message.dart';
import 'package:wereda_connect/core/database/schemas/order.dart';
import 'package:wereda_connect/core/database/schemas/sync_operation.dart';
import 'package:wereda_connect/core/database/schemas/trip.dart';
import 'package:wereda_connect/core/network/api_client.dart';
import 'package:wereda_connect/core/network/api_exception.dart';
import 'package:wereda_connect/core/sync/isar_marketplace_sync.dart';
import 'package:wereda_connect/core/sync/sync_queue.dart';
import 'package:wereda_connect/features/crop_listing/data/repositories/crop_listing_repository.dart';
import 'package:wereda_connect/features/delivery_verification/data/repositories/delivery_repository.dart';
import 'package:wereda_connect/features/order/data/repositories/order_repository.dart';
import 'package:wereda_connect/features/trip_listing/data/repositories/trip_listing_repository.dart';

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
      [
        OrderSchema,
        DeliverySchema,
        SyncOperationSchema,
        MessageSchema,
        CropListingSchema,
        TripSchema,
      ],
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

    // Reviewer repro: pay then cancel offline, both refused, GET 503. Each
    // optimistic write bumped the version (3 -> 4 -> 5), so restoring only
    // one op's base (4) left the row ahead of the server and the pull guard
    // skipped it forever.
    group('two refused changes and a failed read', () {
      Future<(SyncOperation, SyncOperation)> payThenCancelRefused() async {
        await putOrder(status: OrderStatus.created);
        await repo.submitPayment('order-1', 'REF-1');
        // Idempotency keys have millisecond resolution.
        await Future<void>.delayed(const Duration(milliseconds: 2));
        await repo.cancelOrder('order-1', 'buyer-1');
        expect((await load())!.version, 5);
        final ops = await opsFor('order-1');
        ops.sort((a, b) => a.createdAt.compareTo(b.createdAt));
        for (final op in ops) {
          await queue.markAsRejected(op.operationId);
        }
        answerOrder(() async => throw const ServerException('busy', 503));
        answerOrderList([_serverOrder(status: 'created')]);
        return (ops.first, ops.last);
      }

      Future<void> expectPullOverwrites() async {
        expect((await load())!.version, 3);
        await sync.pullOrders(userId: 'buyer-1', role: 'buyer');
        final order = (await load())!;
        expect(order.status, OrderStatus.created);
        expect(order.version, 3);
      }

      test('reverted with the first op (same batch)', () async {
        final (pay, _) = await payThenCancelRefused();

        await sync.revertRejected(pay, 'ORDER_VALIDATION: no');

        await expectPullOverwrites();
      });

      test('reverted with the later op (another batch)', () async {
        final (_, cancel) = await payThenCancelRefused();

        await sync.revertRejected(cancel, 'ORDER_VALIDATION: no');

        await expectPullOverwrites();
      });
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

    test(
        'other entity types, and a trip refused by a code that is not its '
        'own, are left alone', () async {
      final tripOp = await queue.enqueue(
        userId: 'trucker-1',
        entityType: 'trip',
        entityId: 'trip-1',
        action: 'update',
        data: '{"version":1}',
        operationId: 'op-trip',
      );
      final ratingOp = await queue.enqueue(
        userId: 'buyer-1',
        entityType: 'rating',
        entityId: 'rating-1',
        action: 'create',
        data: '{"version":1}',
        operationId: 'op-rating',
      );

      await sync.revertRejected(tripOp, 'ORDER_VALIDATION: no');
      await sync.revertRejected(ratingOp, 'APP_UPDATE_REQUIRED: 2.0.0');

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

  // ESC-08: the trucker picked up while the farmer cancelled offline; the
  // server refuses the cancel for good and the device must show the pickup.
  group('revertRejected for a delivery cancel refused after pickup', () {
    const deliveryId = '550e8400-e29b-41d4-a716-446655440000';
    const refusal = 'DELIVERY_CANCEL_NOT_ALLOWED: A delivery can be cancelled '
        'only before pickup (status: pickedUp).';

    Future<Delivery?> loadDelivery() =>
        isar.deliverys.filter().deliveryIdEqualTo(deliveryId).findFirst();

    /// The farmer cancels offline (local `cancelled` at version 2, op queued
    /// with base version 1), then the server refuses it for good.
    Future<SyncOperation> cancelThenReject() async {
      await isar.writeTxn(() async {
        await isar.deliverys.put(
          Delivery()
            ..deliveryId = deliveryId
            ..tripId = 'trip-1'
            ..listingId = 'listing-1'
            ..farmerId = 'farmer-1'
            ..farmerName = 'Farmer'
            ..truckerId = 'trucker-1'
            ..truckerName = 'Trucker'
            ..buyerId = 'buyer-1'
            ..buyerName = 'Buyer'
            ..qrToken = 'qr'
            ..status = DeliveryStatus.pending
            ..createdAt = DateTime.utc(2026, 10, 9)
            ..updatedAt = DateTime.utc(2026, 10, 9)
            ..version = 1,
        );
      });
      await DeliveryRepository(isar: isar, syncQueue: queue)
          .cancelDelivery(deliveryId: deliveryId, userId: 'farmer-1');
      final op = (await opsFor(deliveryId)).single;
      await queue.markAsRejected(op.operationId);
      return op;
    }

    test('goes back to the server delivery', () async {
      final op = await cancelThenReject();
      when(() => api.get<Map<String, dynamic>>('/deliveries/$deliveryId'))
          .thenAnswer(
        (_) async => {
          'delivery': {
            'deliveryId': deliveryId,
            'farmerId': 'farmer-1',
            'truckerId': 'trucker-1',
            'buyerId': 'buyer-1',
            'status': 'pickedUp',
            'version': 2,
          },
        },
      );

      await sync.revertRejected(op, refusal);

      final delivery = await loadDelivery();
      expect(delivery!.status, DeliveryStatus.pickedUp);
      expect(delivery.version, 2);
    });

    test('a failed read puts the base version back and keeps the row',
        () async {
      final op = await cancelThenReject();
      when(() => api.get<Map<String, dynamic>>('/deliveries/$deliveryId'))
          .thenThrow(const NetworkException());

      await sync.revertRejected(op, refusal);

      final delivery = await loadDelivery();
      expect(delivery, isNotNull);
      expect(delivery!.version, 1);
    });

    test('another op still on its way settles the row instead', () async {
      final op = await cancelThenReject();
      await queue.enqueue(
        userId: 'farmer-1',
        entityType: 'delivery',
        entityId: deliveryId,
        action: 'update-later',
        data: '{"qrNonce":"n","version":2}',
        operationId: 'op-later',
      );

      await sync.revertRejected(op, refusal);

      expect((await loadDelivery())!.status, DeliveryStatus.cancelled);
      verifyNever(
        () => api.get<Map<String, dynamic>>('/deliveries/$deliveryId'),
      );
    });
  });

  // ESC-07: the buyer disputed offline, but the payment was already being
  // settled, so the server refused the dispute for good and wrote nothing.
  // The device must show the delivery as delivered again.
  group('revertRejected for a refused delivery dispute', () {
    const deliveryId = '6ba7b810-9dad-11d1-80b4-00c04fd430c8';
    const refusal = 'DELIVERY_DISPUTE_REFUSED: The payment for this delivery '
        'is being settled, so it can no longer be disputed. Contact support.';

    Future<Delivery?> loadDelivery() =>
        isar.deliverys.filter().deliveryIdEqualTo(deliveryId).findFirst();

    test('goes back to the server delivery', () async {
      final confirmedAt = DateTime.now().toUtc().subtract(
            const Duration(hours: 2),
          );
      await isar.writeTxn(() async {
        await isar.deliverys.put(
          Delivery()
            ..deliveryId = deliveryId
            ..tripId = 'trip-1'
            ..listingId = 'listing-1'
            ..farmerId = 'farmer-1'
            ..farmerName = 'Farmer'
            ..truckerId = 'trucker-1'
            ..truckerName = 'Trucker'
            ..buyerId = 'buyer-1'
            ..buyerName = 'Buyer'
            ..qrToken = 'qr'
            ..status = DeliveryStatus.delivered
            ..confirmedAt = confirmedAt
            ..createdAt = DateTime.utc(2026, 10, 9)
            ..updatedAt = DateTime.utc(2026, 10, 9)
            ..version = 3,
        );
      });
      await DeliveryRepository(isar: isar, syncQueue: queue).raiseDispute(
        deliveryId: deliveryId,
        userId: 'buyer-1',
        reason: 'Half the sacks were wet.',
      );
      expect((await loadDelivery())!.status, DeliveryStatus.disputed);
      final op = (await opsFor(deliveryId)).single;
      await queue.markAsRejected(op.operationId);
      when(() => api.get<Map<String, dynamic>>('/deliveries/$deliveryId'))
          .thenAnswer(
        (_) async => {
          'delivery': {
            'deliveryId': deliveryId,
            'farmerId': 'farmer-1',
            'truckerId': 'trucker-1',
            'buyerId': 'buyer-1',
            'status': 'delivered',
            'confirmedAt': confirmedAt.toIso8601String(),
            'version': 3,
          },
        },
      );

      await sync.revertRejected(op, refusal);

      final delivery = await loadDelivery();
      expect(delivery!.status, DeliveryStatus.delivered);
      expect(delivery.version, 3);
    });
  });

  group('pullOrder (ESC-44)', () {
    test('a queued payment: null, and no request', () async {
      await putOrder(status: OrderStatus.created);
      await repo.submitPayment('order-1', null, paymentMethod: 'telebirr');

      expect(await sync.pullOrder('order-1'), isNull);
      verifyNever(() => api.get<Map<String, dynamic>>(any()));
      expect((await load())!.status, OrderStatus.paymentSubmitted);
    });

    test('nothing queued: returns the server order and caches it', () async {
      await putOrder(status: OrderStatus.paymentSubmitted, version: 4);
      answerOrder(
        () async => {'order': _serverOrder(version: 5)},
      );

      final server = await sync.pullOrder('order-1');

      expect(server!.status, OrderStatus.funded);
      final local = await load();
      expect(local!.status, OrderStatus.funded);
      expect(local.version, 5);
    });

    test('an older server copy (stale read) never replaces the row', () async {
      await putOrder(status: OrderStatus.paymentSubmitted, version: 4);
      answerOrder(
        () async => {'order': _serverOrder(status: 'created')},
      );

      final server = await sync.pullOrder('order-1');

      expect(server!.status, OrderStatus.created);
      final local = await load();
      expect(local!.status, OrderStatus.paymentSubmitted);
      expect(local.version, 4);
    });

    test('the server pickup deadline is stored (ESC-06)', () async {
      await putOrder(version: 4);
      answerOrder(
        () async => {
          'order': _serverOrder(status: 'accepted', version: 5)
            ..['pickupDeadlineAt'] = '2026-10-12T08:00:00.0000000Z',
        },
      );

      await sync.pullOrder('order-1');

      final local = await load();
      // Isar hands DateTimes back in local time: compare the instant.
      expect(local!.pickupDeadlineAt!.toUtc(), DateTime.utc(2026, 10, 12, 8));
    });

    test('errors reach the caller', () async {
      await putOrder();
      answerOrder(() async => throw const NotFoundException());

      await expectLater(
        sync.pullOrder('order-1'),
        throwsA(isA<NotFoundException>()),
      );
    });
  });

  // ESC-48: the parties' phones travel on the order, only while it is open.
  group('order contact (ESC-48)', () {
    Map<String, dynamic> openOrder({
      String status = 'accepted',
      int version = 4,
    }) =>
        _serverOrder(status: status, version: version)
          ..['truckerId'] = 'trucker-1'
          ..['contactOpen'] = status == 'accepted'
          ..addAll(
            status == 'accepted'
                ? {
                    'buyerPhone': '+251911000001',
                    'farmerPhone': '+251911000002',
                    'truckerPhone': '+251911000003',
                  }
                : {},
          );

    test('a pull stores the flag and the phones, a closed pull clears them',
        () async {
      await putOrder();
      answerOrderList([openOrder()]);

      await sync.pullOrders(userId: 'buyer-1', role: 'buyer');

      var local = await load();
      expect(local!.contactOpen, isTrue);
      expect(local.farmerPhone, '+251911000002');
      expect(local.truckerPhone, '+251911000003');

      // The buyer cancelled after a transport failure on another device.
      answerOrder(
        () async => {'order': openOrder(status: 'cancelled', version: 5)},
      );
      await sync.pullOrder('order-1');

      local = await load();
      expect(local!.contactOpen, isFalse);
      expect(local.buyerPhone, isNull);
      expect(local.farmerPhone, isNull);
      expect(local.truckerPhone, isNull);
    });

    Future<void> putOpenOrder() => isar.writeTxn(() async {
          final order = (await load())!
            ..contactOpen = true
            ..buyerPhone = '+251911000001'
            ..farmerPhone = '+251911000002';
          await isar.orders.put(order);
        });

    test('a local reject drops the phones at once, offline', () async {
      await putOrder();
      await putOpenOrder();

      await repo.rejectOrder('order-1', 'farmer-1');

      final local = await load();
      expect(local!.contactOpen, isFalse);
      expect(local.buyerPhone, isNull);
      expect(local.farmerPhone, isNull);
    });

    test('a local accept leaves the flag to the next pull', () async {
      await putOrder();
      await isar.writeTxn(() async {
        await isar.orders.put((await load())!..contactOpen = false);
      });

      await repo.acceptOrder('order-1', 'farmer-1');

      final local = await load();
      expect(local!.status, OrderStatus.accepted);
      expect(local.contactOpen, isNull);
    });
  });

  group('revertRejected for a message (ESC-48)', () {
    Future<SyncOperation> sentMessage() async {
      await isar.writeTxn(() async {
        await isar.messages.put(
          Message()
            ..messageId = 'msg-1'
            ..conversationId = 'crop:crop-1:buyer-1:farmer-1'
            ..contextType = 'crop'
            ..contextId = 'crop-1'
            ..senderId = 'buyer-1'
            ..recipientId = 'farmer-1'
            ..body = 'hello'
            ..createdAt = DateTime.utc(2026, 10)
            ..version = 1,
        );
      });
      return queue.enqueue(
        userId: 'buyer-1',
        entityType: 'message',
        entityId: 'msg-1',
        action: 'create',
        data: '{"messageId":"msg-1"}',
        operationId: 'op-msg',
      );
    }

    Future<Message?> loadMessage() =>
        isar.messages.filter().messageIdEqualTo('msg-1').findFirst();

    test('a message refused because contact is not open is removed', () async {
      final op = await sentMessage();

      await sync.revertRejected(
        op,
        'MESSAGE_CONTACT_LOCKED: Contact opens after payment is held in escrow '
        'and the farmer accepts the order.',
      );

      expect(await loadMessage(), isNull);
      verifyZeroInteractions(api);
    });

    test('any other message refusal keeps it', () async {
      final op = await sentMessage();

      await sync.revertRejected(op, 'MESSAGE_FORBIDDEN: no');

      expect(await loadMessage(), isNotNull);
    });
  });

  // ESC-12 part B (sync-protocol.md 1.17.0): a crop listing or trip change
  // refused by reputation, bookings, active orders or the minimum app
  // version goes back to the server's row, with the order rules.
  group('revertRejected for a refused crop listing (part B)', () {
    late CropListingRepository crops;

    setUp(() {
      crops = CropListingRepository(isar: isar, syncQueue: queue);
    });

    /// A listing as `GET /crops/{id}` and `GET /crops` return it (flat).
    Map<String, dynamic> serverCrop({int version = 3}) => {
          'cropId': 'crop-1',
          'farmerId': 'farmer-1',
          'farmerName': 'Farmer',
          'farmerPhone': '+251900100001',
          'cropType': 'teff',
          'woreda': 'Debre Birhan',
          'quantityKg': 10,
          'pricePerKg': 45,
          'status': 'available',
          'createdAt': '2026-10-01T08:00:00.000Z',
          'updatedAt': '2026-10-01T08:00:00.000Z',
          'version': version,
        };

    Future<CropListing?> loadCrop() =>
        isar.cropListings.filter().listingIdEqualTo('crop-1').findFirst();

    Future<void> putCrop({int version = 3}) => isar.writeTxn(() async {
          await isar.cropListings.put(
            CropListing()
              ..listingId = 'crop-1'
              ..farmerId = 'farmer-1'
              ..cropType = 'teff'
              ..quantity = 10
              ..pricePerKg = 45
              ..location = (LatLng()
                ..latitude = 9.6
                ..longitude = 39.5)
              ..locationName = 'Debre Birhan'
              ..description = ''
              ..status = ListingStatus.active
              ..createdAt = DateTime.utc(2026, 10)
              ..updatedAt = DateTime.utc(2026, 10)
              ..version = version,
          );
        });

    /// The farmer removes the listing offline (local `expired` at version
    /// 4, op with base version 3), then the server refuses it for good.
    Future<SyncOperation> deleteThenReject() async {
      await putCrop();
      await crops.deleteListing(listingId: 'crop-1', farmerId: 'farmer-1');
      final op = (await opsFor('crop-1')).single;
      await queue.markAsRejected(op.operationId);
      return op;
    }

    void answerCrop(Future<Map<String, dynamic>> Function() answer) {
      when(() => api.get<Map<String, dynamic>>('/crops/crop-1'))
          .thenAnswer((_) => answer());
    }

    void answerCropList(List<Map<String, dynamic>> list) {
      when(
        () => api.get<Map<String, dynamic>>(
          '/crops',
          queryParameters: any(named: 'queryParameters'),
        ),
      ).thenAnswer((_) async => {'crops': list});
    }

    for (final message in [
      'CROP_HAS_ACTIVE_ORDERS: This listing has active orders.',
      'APP_UPDATE_REQUIRED: minimum 2.0.0',
    ]) {
      test(
          'a refused delete goes back to the server listing and version '
          '(${message.split(':').first})', () async {
        final op = await deleteThenReject();
        expect((await loadCrop())!.status, ListingStatus.expired);
        answerCrop(() async => {'crop': serverCrop()});

        await sync.revertRejected(op, message);

        final crop = (await loadCrop())!;
        expect(crop.status, ListingStatus.active);
        expect(crop.version, 3);
        expect(await isar.cropListings.count(), 1);
      });
    }

    for (final message in [
      'REPUTATION_RESTRICTED: restricted',
      'APP_UPDATE_REQUIRED: minimum 2.0.0',
    ]) {
      test(
          'a refused create removes the local listing without a read '
          '(${message.split(':').first})', () async {
        await putCrop(version: 1);
        final op = await queue.enqueue(
          userId: 'farmer-1',
          entityType: 'crop_listing',
          entityId: 'crop-1',
          action: 'create',
          data: '{"listingId":"crop-1","version":1}',
          operationId: 'op-create',
        );
        await queue.markAsRejected(op.operationId);

        await sync.revertRejected(op, message);

        expect(await loadCrop(), isNull);
        verifyZeroInteractions(api);
      });
    }

    test('404 (gone) and 403 (not the owner) remove the local listing',
        () async {
      for (final error in <Exception>[
        const NotFoundException('Crop listing has been deleted.'),
        const AuthException('Forbidden', 403, 'CROP_FORBIDDEN'),
      ]) {
        await isar.writeTxn(() => isar.clear());
        final op = await deleteThenReject();
        answerCrop(() async => throw error);

        await sync.revertRejected(op, 'CROP_HAS_ACTIVE_ORDERS: x');

        expect(await loadCrop(), isNull, reason: '$error');
      }
    });

    test(
        'a failed read lowers the version, and the next crop pull, not '
        'throttled, overwrites the row', () async {
      for (final error in <Exception>[
        const NetworkException(),
        const AuthException('Unauthorized'),
        const ServerException('boom', 500),
      ]) {
        await isar.writeTxn(() => isar.clear());
        sync = IsarMarketplaceSync(api: api, isar: isar);
        answerCropList([]);
        // A recent pull would normally throttle the next one for 30s.
        await sync.pullCrops();

        final op = await deleteThenReject();
        answerCrop(() async => throw error);

        await sync.revertRejected(op, 'CROP_HAS_ACTIVE_ORDERS: x');

        final kept = (await loadCrop())!;
        expect(kept.version, 3, reason: '$error');

        answerCropList([serverCrop()]);
        await sync.pullCrops();

        expect(
          (await loadCrop())!.status,
          ListingStatus.active,
          reason: '$error',
        );
      }
    });

    test('another op still on its way settles the row instead', () async {
      final op = await deleteThenReject();
      await queue.enqueue(
        userId: 'farmer-1',
        entityType: 'crop_listing',
        entityId: 'crop-1',
        action: 'update',
        data: '{"version":4}',
        operationId: 'op-later',
      );

      await sync.revertRejected(op, 'CROP_HAS_ACTIVE_ORDERS: x');

      expect((await loadCrop())!.status, ListingStatus.expired);
      verifyZeroInteractions(api);
    });

    test('any other refusal leaves the listing alone', () async {
      final op = await deleteThenReject();

      for (final message in [
        'FRAUD_VELOCITY_EXCEEDED: slow down',
        'CROP_VALIDATION: The farmer profile could not be read.',
        'TRIP_HAS_BOOKINGS: x',
        '',
      ]) {
        await sync.revertRejected(op, message);
      }

      expect((await loadCrop())!.status, ListingStatus.expired);
      verifyZeroInteractions(api);
    });
  });

  group('revertRejected for a refused trip (part B)', () {
    late TripListingRepository trips;

    setUp(() {
      trips = TripListingRepository(isar: isar, syncQueue: queue);
    });

    /// A trip as `GET /trips/{id}` and `GET /trips` return it.
    Map<String, dynamic> serverTrip({int version = 3}) => {
          'tripId': 'trip-1',
          'truckerId': 'trucker-1',
          'truckerName': 'Trucker',
          'truckerPhone': '+251900100002',
          'fromWoreda': 'Bishoftu',
          'toWoreda': 'Addis Ababa',
          'departureDate': '2026-10-30T06:00:00.000Z',
          'capacity': 40,
          'pricePerQuintal': 120,
          'status': 'open',
          'matchedListings': <String>[],
          'createdAt': '2026-10-01T08:00:00.000Z',
          'updatedAt': '2026-10-01T08:00:00.000Z',
          'version': version,
        };

    Future<Trip?> loadTrip() =>
        isar.trips.filter().tripIdEqualTo('trip-1').findFirst();

    Future<void> putTrip({int version = 3}) => isar.writeTxn(() async {
          await isar.trips.put(
            Trip()
              ..tripId = 'trip-1'
              ..truckerId = 'trucker-1'
              ..truckerName = 'Trucker'
              ..truckerPhone = '+251900100002'
              ..fromWoreda = 'Bishoftu'
              ..toWoreda = 'Addis Ababa'
              ..departureDate = DateTime.utc(2026, 10, 30, 6)
              ..capacity = 40
              ..pricePerQuintal = 120
              ..status = TripStatus.open
              ..matchedListings = []
              ..createdAt = DateTime.utc(2026, 10)
              ..updatedAt = DateTime.utc(2026, 10)
              ..version = version,
          );
        });

    /// The trucker cancels offline (local `cancelled` at version 4, op with
    /// base version 3), then the server refuses it for good.
    Future<SyncOperation> cancelThenReject() async {
      await putTrip();
      await trips.cancelTrip(tripId: 'trip-1', truckerId: 'trucker-1');
      final op = (await opsFor('trip-1')).single;
      await queue.markAsRejected(op.operationId);
      return op;
    }

    void answerTrip(Future<Map<String, dynamic>> Function() answer) {
      when(() => api.get<Map<String, dynamic>>('/trips/trip-1'))
          .thenAnswer((_) => answer());
    }

    void answerTripList(List<Map<String, dynamic>> list) {
      when(
        () => api.get<Map<String, dynamic>>(
          '/trips',
          queryParameters: any(named: 'queryParameters'),
        ),
      ).thenAnswer((_) async => {'trips': list});
    }

    for (final message in [
      'TRIP_HAS_BOOKINGS: This trip has booked orders.',
      'APP_UPDATE_REQUIRED: minimum 2.0.0',
    ]) {
      test(
          'a refused cancel goes back to the server trip and version '
          '(${message.split(':').first})', () async {
        final op = await cancelThenReject();
        expect((await loadTrip())!.status, TripStatus.cancelled);
        answerTrip(() async => {'trip': serverTrip()});

        await sync.revertRejected(op, message);

        final trip = (await loadTrip())!;
        expect(trip.status, TripStatus.open);
        expect(trip.version, 3);
        expect(await isar.trips.count(), 1);
      });
    }

    for (final message in [
      'REPUTATION_RESTRICTED: restricted',
      'APP_UPDATE_REQUIRED: minimum 2.0.0',
    ]) {
      test(
          'a refused create removes the local trip without a read '
          '(${message.split(':').first})', () async {
        await putTrip(version: 1);
        final op = await queue.enqueue(
          userId: 'trucker-1',
          entityType: 'trip',
          entityId: 'trip-1',
          action: 'create',
          data: '{"tripId":"trip-1","version":1}',
          operationId: 'op-create',
        );
        await queue.markAsRejected(op.operationId);

        await sync.revertRejected(op, message);

        expect(await loadTrip(), isNull);
        verifyZeroInteractions(api);
      });
    }

    test('404 (cancelled or gone) and 403 (not the owner) remove the trip',
        () async {
      for (final error in <Exception>[
        const NotFoundException('Trip listing has been cancelled.'),
        const AuthException('Forbidden', 403, 'TRIP_FORBIDDEN'),
      ]) {
        await isar.writeTxn(() => isar.clear());
        final op = await cancelThenReject();
        answerTrip(() async => throw error);

        await sync.revertRejected(op, 'TRIP_HAS_BOOKINGS: x');

        expect(await loadTrip(), isNull, reason: '$error');
      }
    });

    test(
        'a failed read lowers the version, and the next trip pull, not '
        'throttled, overwrites the row', () async {
      for (final error in <Exception>[
        const NetworkException(),
        const ServerException('boom', 503),
      ]) {
        await isar.writeTxn(() => isar.clear());
        sync = IsarMarketplaceSync(api: api, isar: isar);
        answerTripList([]);
        await sync.pullTrips();

        final op = await cancelThenReject();
        answerTrip(() async => throw error);

        await sync.revertRejected(op, 'TRIP_HAS_BOOKINGS: x');

        expect((await loadTrip())!.version, 3, reason: '$error');

        answerTripList([serverTrip()]);
        await sync.pullTrips();

        expect((await loadTrip())!.status, TripStatus.open, reason: '$error');
      }
    });

    test('another op still on its way settles the row instead', () async {
      final op = await cancelThenReject();
      await queue.enqueue(
        userId: 'trucker-1',
        entityType: 'trip',
        entityId: 'trip-1',
        action: 'update',
        data: '{"version":4}',
        operationId: 'op-later',
      );

      await sync.revertRejected(op, 'TRIP_HAS_BOOKINGS: x');

      expect((await loadTrip())!.status, TripStatus.cancelled);
      verifyZeroInteractions(api);
    });

    test('any other refusal leaves the trip alone', () async {
      final op = await cancelThenReject();

      for (final message in [
        'TRIP_VALIDATION: bad',
        'TRIP_CAPACITY_EXCEEDED: x',
        'CROP_HAS_ACTIVE_ORDERS: x',
        'FRAUD_ACCOUNT_HELD: held',
      ]) {
        await sync.revertRejected(op, message);
      }

      expect((await loadTrip())!.status, TripStatus.cancelled);
      verifyZeroInteractions(api);
    });
  });
}
