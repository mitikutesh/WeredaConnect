import 'dart:async';
import 'dart:convert';
import 'dart:io';

import 'package:connectivity_plus/connectivity_plus.dart';
import 'package:dio/dio.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http_mock_adapter/http_mock_adapter.dart';
import 'package:isar/isar.dart';

import 'package:wereda_connect/core/database/schemas/order.dart';
import 'package:wereda_connect/core/database/schemas/sync_operation.dart';
import 'package:wereda_connect/core/errors/app_error.dart';
import 'package:wereda_connect/core/models/order_check_in.dart';
import 'package:wereda_connect/core/security/security_events.dart';
import 'package:wereda_connect/core/sync/connectivity_monitor.dart';
import 'package:wereda_connect/core/sync/handoff_check_in_rules.dart';
import 'package:wereda_connect/core/sync/sync_executor.dart';
import 'package:wereda_connect/core/sync/sync_queue.dart';
import 'package:wereda_connect/features/order/data/repositories/order_repository.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

class _FakeConnectivitySource implements ConnectivitySource {
  // Closed in tearDown.
  // ignore: close_sinks
  final controller = StreamController<List<ConnectivityResult>>.broadcast();

  @override
  Stream<List<ConnectivityResult>> get onConnectivityChanged =>
      controller.stream;

  @override
  Future<List<ConnectivityResult>> checkConnectivity() async =>
      [ConnectivityResult.wifi];
}

const _fix = HandoffCheckInFix(
  latitude: 8.7523,
  longitude: 38.9785,
  accuracyM: 35,
);

/// ESC-34 on the phone (FUP-285): a check-in is queued with exactly the
/// server's shape, its mark is the queued operation, and every refusal is
/// permanent: the operation is rejected, never sent again, its mark goes,
/// the order is read again and the user is told why in their language.
void main() {
  final en = lookupAppLocalizations(const Locale('en'));
  late Isar isar;
  late Directory tempDir;
  late SyncQueue queue;
  late OrderRepository repo;
  late _FakeConnectivitySource source;
  late Dio dio;
  late DioAdapter adapter;
  late ConnectivityMonitor monitor;
  late SecurityEvents events;
  late List<SecurityEvent> emitted;
  late StreamSubscription<SecurityEvent> sub;
  late List<(String, String)> reverts;
  late SyncExecutor executor;
  late List<Map<String, dynamic>> sent;
  late Map<String, Object> answer;
  late bool offline;

  setUpAll(() async {
    await Isar.initializeIsarCore(download: true);
  });

  setUp(() async {
    tempDir = Directory.systemTemp.createTempSync('order_check_in_test_');
    isar = await Isar.open(
      [OrderSchema, SyncOperationSchema],
      directory: tempDir.path,
      name: 'order_check_in_${DateTime.now().microsecondsSinceEpoch}',
    );
    queue = SyncQueue(isar);
    repo = OrderRepository(isar: isar, syncQueue: queue);
    source = _FakeConnectivitySource();
    dio = Dio(BaseOptions(baseUrl: 'https://api.test.com'));
    adapter = DioAdapter(dio: dio);
    monitor = ConnectivityMonitor(source: source, dio: dio);
    events = SecurityEvents.forTesting();
    emitted = [];
    sub = events.events.listen(emitted.add);
    reverts = [];
    sent = [];
    answer = {'status': 'success', 'serverVersion': 3};
    offline = false;
    executor = SyncExecutor(
      syncQueue: queue,
      connectivityMonitor: monitor,
      dio: dio,
      clientId: 'mobile-1',
      securityEvents: events,
      onPermanentRejection: (op, message) async =>
          reverts.add((op.entityId, message)),
    );
    adapter.onPost(
      '/sync',
      (server) => server.replyCallback(200, (options) {
        if (offline) {
          throw DioException.connectionError(
            requestOptions: options,
            reason: 'offline',
          );
        }
        final body = Map<String, dynamic>.from(options.data as Map);
        final ops = (body['operations'] as List).cast<Map<String, dynamic>>();
        sent.addAll(ops);
        return {
          'syncBatchId': body['syncBatchId'],
          'results': [
            for (final op in ops) {'operationId': op['operationId'], ...answer},
          ],
        };
      }),
      data: Matchers.any,
    );
  });

  tearDown(() async {
    await sub.cancel();
    executor.dispose();
    monitor.dispose();
    await source.controller.close();
    queue.dispose();
    await isar.close(deleteFromDisk: true);
    if (tempDir.existsSync()) tempDir.deleteSync(recursive: true);
  });

  /// An accepted order stamped `pending`, its window open now.
  Future<void> putOrder({
    String? judgement = 'pending',
    bool selfPickup = false,
    Duration deadlineIn = const Duration(hours: 6),
  }) {
    final now = DateTime.now().toUtc();
    return isar.writeTxn(() async {
      await isar.orders.filter().orderIdEqualTo('ord-1').deleteAll();
      await isar.orders.put(
        Order()
          ..orderId = 'ord-1'
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
          ..createdAt = now.subtract(const Duration(days: 2))
          ..version = 3
          ..tripId = selfPickup ? null : 'trip-1'
          ..truckerId = selfPickup ? null : 'trucker-1'
          ..fulfillment = selfPickup ? 'self_pickup' : 'trucker'
          ..pickupDate =
              selfPickup ? null : now.subtract(const Duration(hours: 1))
          ..pickupDeadlineAt = now.add(deadlineIn)
          ..pickupJudgement = judgement,
      );
    });
  }

  Future<List<SyncOperation>> checkInOps() => isar.syncOperations
      .filter()
      .entityTypeEqualTo(handoffCheckInEntityType)
      .findAll();

  /// The text the app shows for the batch's refusal events.
  List<String> rejectionTexts() => [
        for (final event in emitted.whereType<SyncOperationRejected>())
          localizeErrorCode(
            syncRejectionErrorCode(
              entityType: event.entityType,
              action: event.action,
              message: event.message,
            ),
            en,
          ),
      ];

  group('queued', () {
    test("the trucker's arrival goes out with exactly the server's shape",
        () async {
      await putOrder();

      await repo.checkIn(
        'ord-1',
        'trucker-1',
        kind: CheckInKind.arrivedPickup,
        fix: _fix,
      );

      final op = (await checkInOps()).single;
      expect(op.entityType, 'handoff_check_in');
      expect(op.entityId, 'ord-1#arrived_pickup');
      expect(op.action, 'create');
      expect(op.idempotencyKey, startsWith('trucker-1:'));
      final data = jsonDecode(op.data) as Map<String, dynamic>;
      expect(data.keys.toSet(), {
        'orderId',
        'kind',
        'capturedAt',
        'latitude',
        'longitude',
        'accuracyM',
      });
      expect(data['orderId'], 'ord-1');
      expect(data['kind'], 'arrived_pickup');
      expect(data['latitude'], 8.7523);
      expect(DateTime.parse(data['capturedAt'] as String).isUtc, isTrue);

      // Nothing is written to the order; its mark is the queued operation.
      final order = (await repo.getOrderById('ord-1'))!;
      expect(order.version, 3);
      expect(order.checkIns, isEmpty);
      final mark = order.queuedCheckIns['arrived_pickup']!;
      expect(mark.sent, isFalse);
      expect(mark.madeAt, op.createdAt.toUtc());

      await executor.executeBatch();
      expect(sent.single['data'], data);
      expect(sent.single['entityType'], 'handoff_check_in');
      final afterSync = (await repo.getOrderById('ord-1'))!;
      expect(afterSync.queuedCheckIns['arrived_pickup']!.sent, isTrue);
    });

    test("the farmer's crops are ready without a fix, or with a bad one",
        () async {
      await putOrder();

      await repo.checkIn(
        'ord-1',
        'farmer-1',
        kind: CheckInKind.cropsReady,
        fix: const HandoffCheckInFix(latitude: 51.5, longitude: -0.12),
      );

      final data =
          jsonDecode((await checkInOps()).single.data) as Map<String, dynamic>;
      expect(data.keys.toSet(), {'orderId', 'kind', 'capturedAt'});
      expect(data['kind'], 'crops_ready');
    });

    test('the self-pickup buyer arrives', () async {
      await putOrder(selfPickup: true);

      await repo.checkIn(
        'ord-1',
        'buyer-1',
        kind: CheckInKind.arrivedPickup,
        fix: _fix,
      );

      expect((await checkInOps()).single.entityId, 'ord-1#arrived_pickup');
    });

    test('a second tap queues nothing more, and says so', () async {
      await putOrder();
      expect(
        await repo.checkIn('ord-1', 'farmer-1', kind: CheckInKind.cropsReady),
        isTrue,
      );
      expect(
        await repo.checkIn('ord-1', 'farmer-1', kind: CheckInKind.cropsReady),
        isFalse,
      );

      expect(await checkInOps(), hasLength(1));
    });

    test("on a shared phone, only the signed-in user's marks show", () async {
      await putOrder();
      await repo.checkIn(
        'ord-1',
        'trucker-1',
        kind: CheckInKind.arrivedPickup,
        fix: _fix,
      );
      String? signedIn = 'farmer-1';
      final shared = OrderRepository(
        isar: isar,
        syncQueue: queue,
        currentUserId: () async => signedIn,
      );

      // The farmer signed in after the trucker: the trucker's queued
      // arrival is not shown as if the server had it.
      expect((await shared.getOrderById('ord-1'))!.queuedCheckIns, isEmpty);
      signedIn = 'trucker-1';
      expect(
        (await shared.getOrderById('ord-1'))!.queuedCheckIns.keys,
        ['arrived_pickup'],
      );
      signedIn = null;
      expect((await shared.getOrderById('ord-1'))!.queuedCheckIns, isEmpty);
    });

    test("another user's queued check-in does not block this user's", () async {
      await putOrder(selfPickup: true);
      // A check-in of this kind queued under another account on this phone
      // (the order changed hands, or an older build's operation).
      await queue.enqueue(
        userId: 'someone-else',
        entityType: handoffCheckInEntityType,
        entityId: handoffCheckInEntityId('ord-1', 'arrived_pickup'),
        action: 'create',
        data: '{}',
        operationId: 'op-other',
      );

      expect(
        await repo.checkIn(
          'ord-1',
          'buyer-1',
          kind: CheckInKind.arrivedPickup,
          fix: _fix,
        ),
        isTrue,
      );
      expect(await checkInOps(), hasLength(2));
    });

    test('refused on the phone: no fix, the wrong party, no stamp, too late',
        () async {
      await putOrder();
      Future<void> tryIt(String user, String kind, {HandoffCheckInFix? fix}) =>
          repo.checkIn('ord-1', user, kind: kind, fix: fix);

      await expectLater(
        tryIt('trucker-1', CheckInKind.arrivedPickup),
        throwsA(
          isA<CheckInNotAllowedException>().having(
            (e) => e.code,
            'code',
            AppErrorCode.checkInNeedsLocation,
          ),
        ),
      );
      await expectLater(
        tryIt('buyer-1', CheckInKind.arrivedPickup, fix: _fix),
        throwsA(isA<OrderActionNotAllowedException>()),
      );

      await putOrder(judgement: null);
      await expectLater(
        tryIt('farmer-1', CheckInKind.cropsReady),
        throwsA(isA<OrderActionNotAllowedException>()),
      );

      await putOrder(deadlineIn: const Duration(minutes: -1));
      await expectLater(
        tryIt('farmer-1', CheckInKind.cropsReady),
        throwsA(
          isA<CheckInNotAllowedException>().having(
            (e) => e.code,
            'code',
            AppErrorCode.checkInClosed,
          ),
        ),
      );

      expect(await checkInOps(), isEmpty);
    });
  });

  group('every CHECKIN_* refusal is permanent and says why', () {
    final cases = <String, String>{
      'CHECKIN_VALIDATION: latitude must be between 3 and 15.':
          en.errorCheckInValidation,
      "CHECKIN_NOT_PARTY: Only the order's farmer says the crops are ready.":
          en.errorCheckInNotParty,
      'CHECKIN_NOT_OPEN: This order is no longer waiting for its pickup.':
          en.errorCheckInNotOpen,
      'CHECKIN_TOO_EARLY: Check-ins open at 2026-11-19T06:00:00Z.':
          en.errorCheckInTooEarly,
      'CHECKIN_TOO_FAR: You are 7.2 km from the farm (at most 5 km).':
          en.errorCheckInTooFar,
      'CHECKIN_TOO_LATE: The pickup deadline passed before this check-in '
          'reached the server. It was kept for review.': en.errorCheckInTooLate,
      // pickupClassification off: the entity does not exist on the server.
      "Unsupported entity type: 'handoff_check_in'.": en.errorCheckInNotOpen,
    };

    for (final MapEntry(key: message, value: text) in cases.entries) {
      test(message.split(':').first, () async {
        await putOrder();
        await repo.checkIn(
          'ord-1',
          'trucker-1',
          kind: CheckInKind.arrivedPickup,
          fix: _fix,
        );
        answer = {'status': 'error', 'message': message};

        await executor.executeBatch();

        final op = (await checkInOps()).single;
        expect(SyncQueue.isRejected(op), isTrue, reason: 'never retried');
        expect(op.reverted, isTrue, reason: 'leaves the Sync failed count');
        expect(await queue.getFailedCount(), 0);
        expect(reverts, [('ord-1#arrived_pickup', message)]);
        expect(rejectionTexts(), [text]);
        // The mark went back: the button is offered again.
        final order = (await repo.getOrderById('ord-1'))!;
        expect(order.queuedCheckIns, isEmpty);
        expect(
          checkInChanceFor(order, 'trucker-1', DateTime.now().toUtc()),
          CheckInChance.open,
        );

        // Reconnecting does not send it again.
        expect(await queue.retryFailed(), 0);
        sent.clear();
        await executor.executeBatch();
        expect(sent, isEmpty);
      });
    }
  });

  test('a transient error keeps the check-in and its mark', () async {
    await putOrder();
    await repo.checkIn('ord-1', 'farmer-1', kind: CheckInKind.cropsReady);
    answer = {
      'status': 'error',
      'message': 'The order could not be read. Please try again later.',
    };

    await executor.executeBatch();

    final op = (await checkInOps()).single;
    expect(SyncQueue.isRejected(op), isFalse);
    expect(op.status, SyncStatus.pending);
    expect(op.retryCount, 1);
    expect(reverts, isEmpty);
    expect(emitted.whereType<SyncOperationRejected>(), isEmpty);
    final order = (await repo.getOrderById('ord-1'))!;
    expect(order.queuedCheckIns['crops_ready']!.sent, isFalse);
  });

  test(
      'queued offline before the deadline, synced after it: the late text, '
      'and the mark goes', () async {
    await putOrder();
    await repo.checkIn(
      'ord-1',
      'trucker-1',
      kind: CheckInKind.arrivedPickup,
      fix: _fix,
    );

    // No network: the check-in waits, the mark says so.
    offline = true;
    final result = await executor.executeBatch();
    expect(result.status, SyncBatchStatus.networkError);
    var order = (await repo.getOrderById('ord-1'))!;
    expect(order.queuedCheckIns['arrived_pickup']!.sent, isFalse);
    expect((await checkInOps()).single.retryCount, 0);

    // Back online after the deadline: the server kept it as late evidence.
    offline = false;
    answer = {
      'status': 'error',
      'message': 'CHECKIN_TOO_LATE: The pickup deadline passed before this '
          'check-in reached the server. It was kept for review.',
    };
    await executor.executeBatch();

    expect(rejectionTexts(), [en.errorCheckInTooLate]);
    expect(en.errorCheckInTooLate, contains('kept for review'));
    expect(reverts.single.$1, 'ord-1#arrived_pickup');
    order = (await repo.getOrderById('ord-1'))!;
    expect(order.queuedCheckIns, isEmpty);
  });
}
