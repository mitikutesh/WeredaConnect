import 'dart:convert';
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
import 'package:wereda_connect/core/models/order_check_in.dart';
import 'package:wereda_connect/core/network/api_client.dart';
import 'package:wereda_connect/core/network/api_exception.dart';
import 'package:wereda_connect/core/sync/handoff_check_in_rules.dart';
import 'package:wereda_connect/core/sync/isar_marketplace_sync.dart';
import 'package:wereda_connect/core/sync/sync_queue.dart';

class _MockApiClient extends Mock implements ApiClient {}

/// An accepted order stamped for the pickup judgement, as `GET /orders`
/// returns it, with the check-ins the server holds.
Map<String, dynamic> _serverOrder({
  String judgement = 'pending',
  Map<String, dynamic>? checkIns,
}) =>
    {
      'orderId': 'order-1',
      'listingId': 'listing-1',
      'buyerId': 'buyer-1',
      'farmerId': 'farmer-1',
      'buyerName': 'Buyer',
      'farmerName': 'Farmer',
      'cropType': 'teff',
      'quantity': 5,
      'pricePerUnit': 60,
      'totalPrice': 300,
      'status': 'accepted',
      'createdAt': '2026-11-10T08:00:00.000Z',
      'version': 4,
      'tripId': 'trip-1',
      'truckerId': 'trucker-1',
      'fulfillment': 'trucker',
      'pickupDeadlineAt': '2026-11-21T06:00:00.000Z',
      'pickupJudgement': judgement,
      if (checkIns != null) 'checkIns': checkIns,
    };

/// ESC-34 on the phone (FUP-285): the pulls read the check-ins (which move
/// no version), a check-in waiting in the queue never holds the order's row
/// back, and a refused one reads the order again.
void main() {
  late Isar isar;
  late Directory tempDir;
  late _MockApiClient api;
  late SyncQueue queue;
  late IsarMarketplaceSync sync;

  setUpAll(() async {
    await Isar.initializeIsarCore(download: true);
  });

  setUp(() async {
    tempDir = Directory.systemTemp.createTempSync('marketplace_check_in_');
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
      name: 'marketplace_check_in_${DateTime.now().microsecondsSinceEpoch}',
    );
    api = _MockApiClient();
    queue = SyncQueue(isar);
    sync = IsarMarketplaceSync(api: api, isar: isar);
  });

  tearDown(() async {
    queue.dispose();
    await isar.close(deleteFromDisk: true);
    if (tempDir.existsSync()) tempDir.deleteSync(recursive: true);
  });

  Future<Order?> load() =>
      isar.orders.filter().orderIdEqualTo('order-1').findFirst();

  void serverHas(Map<String, dynamic> order) {
    when(() => api.get<Map<String, dynamic>>('/orders/order-1'))
        .thenAnswer((_) async => {'order': order});
    when(
      () => api.get<Map<String, dynamic>>(
        '/orders',
        queryParameters: any(named: 'queryParameters'),
      ),
    ).thenAnswer(
      (_) async => {
        'orders': [order],
      },
    );
  }

  Future<SyncOperation> queueCheckIn() => queue.enqueue(
        userId: 'trucker-1',
        entityType: handoffCheckInEntityType,
        entityId: handoffCheckInEntityId('order-1', 'arrived_pickup'),
        action: 'create',
        data: jsonEncode({'orderId': 'order-1', 'kind': 'arrived_pickup'}),
        operationId: 'op-check-in',
      );

  test('a pull at the same version brings the check-ins in', () async {
    serverHas(_serverOrder());
    await sync.pullOrders(userId: 'trucker-1', role: 'trucker');
    expect((await load())!.checkIns, isEmpty);

    serverHas(
      _serverOrder(
        checkIns: {
          'arrived_pickup': {
            'receivedAt': '2026-11-20T05:40:00Z',
            'distanceChecked': true,
            'late': false,
          },
        },
      ),
    );
    await sync.pullOrder('order-1');

    final order = (await load())!;
    expect(order.pickupJudgement, 'pending');
    expect(
      checkInOf(order, 'arrived_pickup')!.receivedAt!.toUtc(),
      DateTime.utc(2026, 11, 20, 5, 40),
    );
  });

  test('a check-in waiting in the queue does not hold the row back', () async {
    await queueCheckIn();
    serverHas(_serverOrder(judgement: 'trucker_no_show'));

    await sync.pullOrders(userId: 'trucker-1', role: 'trucker');

    expect((await load())!.pickupJudgement, 'trucker_no_show');
  });

  test('a refused check-in reads the order: a late one kept shows', () async {
    serverHas(_serverOrder());
    await sync.pullOrder('order-1');
    final op = await queueCheckIn();
    await queue.markAsRejected(op.operationId);
    serverHas(
      _serverOrder(
        judgement: 'trucker_no_show',
        checkIns: {
          'arrived_pickup': {
            'receivedAt': '2026-11-21T07:00:00Z',
            'distanceChecked': true,
            'late': true,
          },
        },
      ),
    );

    await sync.revertRejected(op, 'CHECKIN_TOO_LATE: kept for review.');

    final order = (await load())!;
    expect(order.pickupJudgement, 'trucker_no_show');
    expect(checkInOf(order, 'arrived_pickup')!.isLate, isTrue);
  });

  test('offline, the refused check-in leaves the row as it is', () async {
    serverHas(_serverOrder());
    await sync.pullOrder('order-1');
    final op = await queueCheckIn();
    when(() => api.get<Map<String, dynamic>>('/orders/order-1'))
        .thenThrow(const NetworkException());

    await sync.revertRejected(op, 'CHECKIN_TOO_FAR: 7 km.');

    expect((await load())!.pickupJudgement, 'pending');
  });
}
