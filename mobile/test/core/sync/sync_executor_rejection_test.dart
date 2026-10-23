import 'dart:async';
import 'dart:io';

import 'package:connectivity_plus/connectivity_plus.dart';
import 'package:dio/dio.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http_mock_adapter/http_mock_adapter.dart';
import 'package:isar/isar.dart';

import 'package:wereda_connect/core/database/schemas/sync_operation.dart';
import 'package:wereda_connect/core/security/security_events.dart';
import 'package:wereda_connect/core/sync/connectivity_monitor.dart';
import 'package:wereda_connect/core/sync/sync_executor.dart';
import 'package:wereda_connect/core/sync/sync_queue.dart';

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

/// Polls [condition] for up to 5 seconds instead of sleeping a fixed time.
Future<void> _waitFor(bool Function() condition) async {
  final deadline = DateTime.now().add(const Duration(seconds: 5));
  while (!condition() && DateTime.now().isBefore(deadline)) {
    await Future<void>.delayed(const Duration(milliseconds: 5));
  }
}

/// ESC-12: permanent rejections stop retrying, undo the local change once per
/// entity, tell the UI why, and never leave the queue stuck.
void main() {
  late Isar isar;
  late Directory tempDir;
  late SyncQueue syncQueue;
  late _FakeConnectivitySource source;
  late Dio dio;
  late DioAdapter adapter;
  late ConnectivityMonitor monitor;
  late SecurityEvents events;
  late List<SecurityEvent> emitted;
  late StreamSubscription<SecurityEvent> sub;
  late List<(SyncOperation, String)> reverts;
  late List<(SyncOperation, String)> kycCalls;
  late SyncExecutor executor;
  late int syncPosts;
  late List<List<String>> sentOperationIds;

  setUpAll(() async {
    await Isar.initializeIsarCore(download: true);
  });

  SyncExecutor buildExecutor({
    required SecurityEvents securityEvents,
    PermanentRejectionHandler? onPermanentRejection,
  }) =>
      SyncExecutor(
        syncQueue: syncQueue,
        connectivityMonitor: monitor,
        dio: dio,
        clientId: 'test-device',
        securityEvents: securityEvents,
        onKycRequired: (op, message) async => kycCalls.add((op, message)),
        onPermanentRejection: onPermanentRejection ??
            (op, message) async => reverts.add((op, message)),
      );

  setUp(() async {
    tempDir = Directory.systemTemp.createTempSync('sync_rejection_test_');
    isar = await Isar.open(
      [SyncOperationSchema],
      directory: tempDir.path,
      name: 'sync_rejection_${DateTime.now().microsecondsSinceEpoch}',
    );
    syncQueue = SyncQueue(isar);
    source = _FakeConnectivitySource();
    dio = Dio(BaseOptions(baseUrl: 'https://api.test.com'));
    adapter = DioAdapter(dio: dio);
    syncPosts = 0;
    sentOperationIds = [];
    dio.interceptors.add(
      InterceptorsWrapper(
        onRequest: (options, handler) {
          if (options.path == '/sync') {
            syncPosts++;
            final body = options.data as Map<String, dynamic>;
            sentOperationIds.add([
              for (final op in body['operations'] as List)
                (op as Map<String, dynamic>)['operationId'] as String,
            ]);
          }
          handler.next(options);
        },
      ),
    );
    monitor = ConnectivityMonitor(source: source, dio: dio);
    events = SecurityEvents.forTesting();
    emitted = [];
    sub = events.events.listen(emitted.add);
    reverts = [];
    kycCalls = [];
    executor = buildExecutor(securityEvents: events);
  });

  tearDown(() async {
    await sub.cancel();
    executor.dispose();
    monitor.dispose();
    await source.controller.close();
    await isar.close(deleteFromDisk: true);
    if (tempDir.existsSync()) tempDir.deleteSync(recursive: true);
  });

  Future<void> enqueue(
    String operationId, {
    String entityType = 'order',
    String entityId = 'order-1',
    String action = 'update',
    String data = '{"status":"accepted","version":2}',
  }) {
    return syncQueue.enqueue(
      userId: 'farmer-1',
      entityType: entityType,
      entityId: entityId,
      // Ops for one entity use distinct actions: the idempotency key has
      // millisecond resolution.
      action: action,
      data: data,
      operationId: operationId,
    );
  }

  void replyWith(List<Map<String, dynamic>> results) {
    adapter.onPost(
      '/sync',
      (server) => server.reply(200, {'syncBatchId': 'b', 'results': results}),
      data: Matchers.any,
    );
  }

  Map<String, dynamic> error(String operationId, String message) =>
      {'operationId': operationId, 'status': 'error', 'message': message};

  List<SyncOperationRejected> rejections() =>
      emitted.whereType<SyncOperationRejected>().toList();

  Future<bool> isRejected(String operationId) async =>
      SyncQueue.isRejected((await syncQueue.getOperation(operationId))!);

  group('every ORDER_* code but KYC is permanent and reverted', () {
    for (final code in [
      'ORDER_VALIDATION',
      'ORDER_UNAUTHORIZED',
      'ORDER_DEADLINE_PASSED',
      'ORDER_CANCEL_NOT_ALLOWED',
      'ORDER_ACTION_NOT_ALLOWED',
      'ORDER_TRIP_NOT_ELIGIBLE',
      'ORDER_TRANSPORT_ATTEMPTS_EXCEEDED',
    ]) {
      test(code, () async {
        final message = '$code: refused';
        await enqueue('op-1');
        replyWith([error('op-1', message)]);

        final result = await executor.executeBatch();
        await _waitFor(() => rejections().isNotEmpty);

        expect(result.failed, 1);
        expect(result.operationErrors['op-1'], message);
        final op = await syncQueue.getOperation('op-1');
        expect(op!.status, SyncStatus.failed);
        expect(op.retryCount, greaterThan(SyncQueue.maxRetries));
        // A reconnect never resends it.
        expect(await syncQueue.retryFailed(), 0);
        expect(await syncQueue.getFailedCount(), 0);

        expect(reverts, hasLength(1));
        expect(reverts.single.$1.operationId, 'op-1');
        expect(reverts.single.$2, message);
        final event = rejections().single;
        expect(event.entityType, 'order');
        expect(event.entityId, 'order-1');
        expect(event.action, 'update');
        expect(event.message, message);
        expect(kycCalls, isEmpty);
      });
    }
  });

  group('transient failures keep retrying', () {
    for (final message in ['Validation failed', 'Order not found.']) {
      test('unprefixed "$message"', () async {
        await enqueue('op-1');
        replyWith([error('op-1', message)]);

        await executor.executeBatch();

        final op = await syncQueue.getOperation('op-1');
        expect(op!.status, SyncStatus.pending);
        expect(op.retryCount, 1);
        expect(reverts, isEmpty);
        await Future<void>.delayed(Duration.zero);
        expect(rejections(), isEmpty);
      });
    }

    test('HTTP 500 burns a retry, nothing is reverted', () async {
      await enqueue('op-1');
      adapter.onPost(
        '/sync',
        (server) => server.reply(500, {'error': 'boom'}),
        data: Matchers.any,
      );

      await executor.executeBatch();

      final op = await syncQueue.getOperation('op-1');
      expect(op!.status, SyncStatus.pending);
      expect(op.retryCount, 1);
      expect(reverts, isEmpty);
    });

    test('a network error burns no retry, nothing is reverted', () async {
      await enqueue('op-1');
      adapter.onPost(
        '/sync',
        (server) => server.throws(
          0,
          DioException(
            requestOptions: RequestOptions(path: '/sync'),
            type: DioExceptionType.connectionError,
          ),
        ),
        data: Matchers.any,
      );

      await executor.executeBatch();

      final op = await syncQueue.getOperation('op-1');
      expect(op!.status, SyncStatus.pending);
      expect(op.retryCount, 0);
      expect(reverts, isEmpty);
    });
  });

  // ESC-08: a cancel after pickup is refused for good, including the legacy
  // `delete` an older app version left in the queue; the local cancel is
  // undone and explained. Other DELIVERY_* refusals keep retrying.
  group('a delivery cancel refused after pickup is permanent and reverted', () {
    const message = 'DELIVERY_CANCEL_NOT_ALLOWED: A delivery can be cancelled '
        'only before pickup (status: pickedUp).';
    for (final (action, data) in [
      ('update', '{"status":"cancelled","version":1}'),
      (
        'delete',
        '{"deliveryId":"del-1","status":"cancelled",'
            '"updatedAt":"2026-10-09T08:00:00Z","version":1}',
      ),
    ]) {
      test(action, () async {
        await enqueue(
          'op-1',
          entityType: 'delivery',
          entityId: 'del-1',
          action: action,
          data: data,
        );
        replyWith([error('op-1', message)]);

        await executor.executeBatch();
        await _waitFor(() => rejections().isNotEmpty);

        expect(await isRejected('op-1'), isTrue);
        expect(await syncQueue.retryFailed(), 0);
        expect(reverts, hasLength(1));
        expect(reverts.single.$1.entityType, 'delivery');
        expect(reverts.single.$2, message);
        final event = rejections().single;
        expect(event.entityType, 'delivery');
        expect(event.entityId, 'del-1');
        expect(event.message, message);
      });
    }

    test('DELIVERY_UNAUTHORIZED keeps retrying and is not reverted', () async {
      await enqueue(
        'op-1',
        entityType: 'delivery',
        entityId: 'del-1',
        data: '{"status":"cancelled","version":1}',
      );
      replyWith([
        error('op-1', 'DELIVERY_UNAUTHORIZED: Only participants can cancel.'),
      ]);

      await executor.executeBatch();

      final op = await syncQueue.getOperation('op-1');
      expect(op!.status, SyncStatus.pending);
      expect(op.retryCount, 1);
      expect(reverts, isEmpty);
    });
  });

  // ESC-07: a dispute the server could not freeze the payment for, or one
  // after the 48-hour window, is refused for good; the local dispute is undone
  // and explained. A dispute whose freeze was undecided keeps retrying.
  group('a refused or late delivery dispute is permanent and reverted', () {
    for (final message in [
      'DELIVERY_DISPUTE_REFUSED: The payment for this delivery is being '
          'settled, so it can no longer be disputed. Contact support.',
      'DELIVERY_DISPUTE_EXPIRED: Dispute window (48 hours) has expired.',
    ]) {
      test(message.split(':').first, () async {
        await enqueue(
          'op-1',
          entityType: 'delivery',
          entityId: 'del-1',
          data: '{"deliveryId":"del-1","status":"disputed",'
              '"disputeReason":"wet bags","version":3}',
        );
        replyWith([error('op-1', message)]);

        await executor.executeBatch();
        await _waitFor(() => rejections().isNotEmpty);

        expect(await isRejected('op-1'), isTrue);
        expect(await syncQueue.retryFailed(), 0);
        expect(reverts, hasLength(1));
        expect(reverts.single.$1.entityType, 'delivery');
        expect(reverts.single.$2, message);
        expect(rejections().single.message, message);
      });
    }

    test('DELIVERY_DISPUTE_RETRY keeps retrying and is not reverted', () async {
      await enqueue(
        'op-1',
        entityType: 'delivery',
        entityId: 'del-1',
        data: '{"status":"disputed","disputeReason":"wet bags","version":3}',
      );
      replyWith([
        error(
          'op-1',
          'DELIVERY_DISPUTE_RETRY: The dispute could not be recorded right '
              'now. It will be sent again.',
        ),
      ]);

      await executor.executeBatch();

      final op = await syncQueue.getOperation('op-1');
      expect(op!.status, SyncStatus.pending);
      expect(op.retryCount, 1);
      expect(reverts, isEmpty);
    });
  });

  // ESC-48: a message into a thread whose contact is not open was never
  // stored; it is refused for good, its local copy removed, and explained.
  group('a message refused because contact is not open', () {
    const message = 'MESSAGE_CONTACT_LOCKED: Contact opens after payment is '
        'held in escrow and the farmer accepts the order.';

    test('is permanent, undone and announced', () async {
      await enqueue(
        'op-1',
        entityType: 'message',
        entityId: 'msg-1',
        action: 'create',
        data: '{"messageId":"msg-1","contextType":"crop"}',
      );
      replyWith([error('op-1', message)]);

      await executor.executeBatch();
      await _waitFor(() => rejections().isNotEmpty);

      expect(await isRejected('op-1'), isTrue);
      expect(await syncQueue.retryFailed(), 0);
      expect(reverts, hasLength(1));
      expect(reverts.single.$1.entityType, 'message');
      expect(reverts.single.$2, message);
      expect(rejections().single.entityType, 'message');
      expect(rejections().single.message, message);
    });

    test('MESSAGE_FORBIDDEN keeps retrying and is not undone', () async {
      await enqueue(
        'op-1',
        entityType: 'message',
        entityId: 'msg-1',
        action: 'create',
        data: '{"messageId":"msg-1"}',
      );
      replyWith([error('op-1', 'MESSAGE_FORBIDDEN: not a participant')]);

      await executor.executeBatch();

      final op = await syncQueue.getOperation('op-1');
      expect(op!.status, SyncStatus.pending);
      expect(op.retryCount, 1);
      expect(reverts, isEmpty);
    });
  });

  test('ORDER_KYC_REQUIRED keeps its own path', () async {
    await enqueue('op-1', data: '{"status":"payment_submitted","version":2}');
    replyWith([error('op-1', 'ORDER_KYC_REQUIRED: buyer_fayda: Verify')]);

    await executor.executeBatch();
    await _waitFor(() => emitted.isNotEmpty);

    expect(kycCalls, hasLength(1));
    expect(reverts, isEmpty);
    expect(emitted.single, isA<KycRequired>());
  });

  test('AUTH_040 stays pending and is not reverted', () async {
    await enqueue('op-1', data: '{"status":"payment_submitted","version":2}');
    replyWith([error('op-1', 'AUTH_040: step-up required')]);

    await executor.executeBatch();

    expect(
      (await syncQueue.getOperation('op-1'))!.status,
      SyncStatus.pending,
    );
    expect(reverts, isEmpty);
  });

  test(
      'a refused order create rejects the ops queued behind it, in this batch '
      'and later ones, and is reverted once', () async {
    await enqueue('op-create', action: 'create');
    await enqueue('op-pay', data: '{"status":"payment_submitted"}');
    // Queued earlier, ran out of retries: not in this batch, but a reconnect
    // would reset and resend it.
    await enqueue('op-later', action: 'update-later');
    await syncQueue.markAsSyncing('op-later');
    for (var i = 0; i < SyncQueue.maxRetries; i++) {
      await syncQueue.markAsFailed('op-later');
    }
    replyWith([
      error('op-create', 'ORDER_VALIDATION: Insufficient stock.'),
      error('op-pay', 'Order not found.'),
    ]);

    await executor.executeBatch();
    await _waitFor(() => rejections().isNotEmpty);

    for (final id in ['op-create', 'op-pay', 'op-later']) {
      expect(await isRejected(id), isTrue, reason: id);
    }
    expect(await syncQueue.retryFailed(), 0);
    expect(reverts, hasLength(1));
    expect(reverts.single.$1.action, 'create');
    expect(rejections().single.action, 'create');
  });

  test(
      'any refused create rejects its queued ops: a FRAUD_ crop create is '
      'announced (with its owner) and still counted, not reverted', () async {
    await enqueue(
      'op-crop',
      entityType: 'crop_listing',
      entityId: 'crop-1',
      action: 'create',
    );
    await enqueue(
      'op-crop-delete',
      entityType: 'crop_listing',
      entityId: 'crop-1',
      action: 'delete',
    );
    replyWith([
      error('op-crop', 'FRAUD_DUPLICATE_LISTING: duplicate'),
      error('op-crop-delete', 'Crop listing not found.'),
    ]);

    await executor.executeBatch();
    await _waitFor(() => rejections().isNotEmpty);

    expect(await isRejected('op-crop'), isTrue);
    expect(await isRejected('op-crop-delete'), isTrue);
    expect(reverts, isEmpty);
    final event = rejections().single;
    expect(event.entityType, 'crop_listing');
    expect(event.action, 'create');
    expect(event.message, 'FRAUD_DUPLICATE_LISTING: duplicate');
    expect(event.userId, 'farmer-1');
    // The listing stays on the phone but never reached the server: Home
    // keeps showing it as failed.
    expect(await syncQueue.getFailedCount(), 2);
  });

  test('a refused background match event is not announced', () async {
    await enqueue(
      'op-match',
      entityType: 'match_event',
      entityId: 'match-1',
      action: 'create',
    );
    replyWith([error('op-match', 'SYNC_VALIDATION: bad payload')]);

    await executor.executeBatch();
    await Future<void>.delayed(Duration.zero);

    expect(await isRejected('op-match'), isTrue);
    expect(reverts, isEmpty);
    expect(rejections(), isEmpty);
    // Not a "Sync failed" item either: Retry could never send it.
    expect(await syncQueue.getFailedCount(), 0);
  });

  test(
      'a batch is announced together after every revert, even when each '
      'revert waits on a slow read', () async {
    const readDelay = Duration(milliseconds: 400);
    final seenByHandlers = <int>[];
    executor.dispose();
    executor = buildExecutor(
      securityEvents: events,
      onPermanentRejection: (op, message) async {
        seenByHandlers.add(rejections().length);
        await Future<void>.delayed(readDelay); // a 3G GET /orders/{id}
      },
    );
    final arrivals = <DateTime>[];
    final timing = events.events.listen((_) => arrivals.add(DateTime.now()));
    addTearDown(timing.cancel);
    for (final id in ['op-1', 'op-2', 'op-3']) {
      await enqueue(id, entityId: 'order-$id');
    }
    replyWith([
      for (final id in ['op-1', 'op-2', 'op-3'])
        error(id, 'ORDER_VALIDATION: no'),
    ]);

    await executor.executeBatch();
    await _waitFor(() => arrivals.length >= 3);

    // No handler ran after an announcement went out.
    expect(seenByHandlers, [0, 0, 0]);
    expect(rejections(), hasLength(3));
    // The three arrive well inside the listener's 300ms burst window, so
    // they read as one message.
    expect(
      arrivals.last.difference(arrivals.first),
      lessThan(const Duration(milliseconds: 100)),
    );
  });

  test('a FRAUD_ rejection of an order op is reverted and announced', () async {
    await enqueue('op-1', data: '{"status":"payment_submitted","version":2}');
    replyWith([error('op-1', 'FRAUD_PAYMENT: blocked')]);

    await executor.executeBatch();
    await _waitFor(() => rejections().isNotEmpty);

    expect(await isRejected('op-1'), isTrue);
    expect(reverts.single.$2, 'FRAUD_PAYMENT: blocked');
    expect(rejections().single.entityId, 'order-1');
  });

  test(
      'an HTTP 400 batch rejects everything, reverts the order op and the '
      'ops queued behind a refused create', () async {
    await enqueue('op-order');
    await enqueue(
      'op-trip',
      entityType: 'trip',
      entityId: 'trip-1',
      action: 'create',
    );
    await enqueue(
      'op-trip-later',
      entityType: 'trip',
      entityId: 'trip-1',
      action: 'delete',
    );
    await syncQueue.markAsSyncing('op-trip-later');
    for (var i = 0; i < SyncQueue.maxRetries; i++) {
      await syncQueue.markAsFailed('op-trip-later');
    }
    adapter.onPost(
      '/sync',
      (server) => server.reply(400, {'error': 'bad request'}),
      data: Matchers.any,
    );

    await executor.executeBatch();
    await _waitFor(() => rejections().isNotEmpty);

    await _waitFor(() => rejections().length >= 2);

    for (final id in ['op-order', 'op-trip', 'op-trip-later']) {
      expect(await isRejected(id), isTrue, reason: id);
    }
    expect(reverts, hasLength(1));
    expect(reverts.single.$1.operationId, 'op-order');
    expect(reverts.single.$2, isEmpty);
    expect(rejections().map((e) => e.entityId), ['order-1', 'trip-1']);
    // The order was reverted, so only the two trip ops still count.
    expect(await syncQueue.getFailedCount(), 2);
  });

  test('two refused ops for one order are reverted and announced once',
      () async {
    await enqueue('op-1');
    await enqueue('op-2', action: 'update-2');
    replyWith([
      error('op-1', 'ORDER_VALIDATION: no'),
      error('op-2', 'ORDER_VALIDATION: no'),
    ]);

    await executor.executeBatch();
    await _waitFor(() => rejections().isNotEmpty);
    await Future<void>.delayed(Duration.zero);

    expect(reverts, hasLength(1));
    expect(reverts.single.$1.operationId, 'op-1');
    expect(rejections(), hasLength(1));
  });

  test('a handler that throws is logged; the reason is still announced',
      () async {
    executor.dispose();
    executor = buildExecutor(
      securityEvents: events,
      onPermanentRejection: (op, message) async => throw StateError('boom'),
    );
    await enqueue('op-1');
    replyWith([error('op-1', 'ORDER_VALIDATION: no')]);

    final result = await executor.executeBatch();
    await _waitFor(() => rejections().isNotEmpty);

    expect(result.failed, 1);
    expect(await isRejected('op-1'), isTrue);
    expect(rejections(), hasLength(1));
  });

  test(
      'a rejection raised before the UI listens is delivered when it does '
      '(act offline, relaunch online)', () async {
    final quiet = SecurityEvents.forTesting();
    executor.dispose();
    executor = buildExecutor(securityEvents: quiet);
    await enqueue('op-1');
    replyWith([error('op-1', 'ORDER_VALIDATION: no')]);

    await executor.executeBatch();

    final delivered = <SecurityEvent>[];
    final lateSub = quiet.events.listen(delivered.add);
    await _waitFor(() => delivered.isNotEmpty);
    await lateSub.cancel();

    expect(delivered.single, isA<SyncOperationRejected>());
  });

  test('the queue never gets stuck: no backoff, no resend on reconnect',
      () async {
    adapter.onGet('/health', (server) => server.reply(200, {'status': 'ok'}));
    await enqueue('op-bad');
    await enqueue('op-good', entityId: 'order-2');
    replyWith([
      error('op-bad', 'ORDER_VALIDATION: Invalid status transition.'),
      {'operationId': 'op-good', 'status': 'success', 'serverVersion': 3},
    ]);

    // Without the early stop this call would back off 5 seconds first.
    final result = await executor.syncNow().timeout(const Duration(seconds: 4));

    expect(result.succeeded, 1);
    expect(result.failed, 1);
    expect(syncPosts, 1);
    expect(await syncQueue.getPendingCount(), 0);
    expect(await syncQueue.getFailedCount(), 0);

    // A reconnect resets failed ops, then syncs: nothing goes out again.
    expect(await syncQueue.retryFailed(), 0);
    final again = await executor.syncNow();
    expect(again.total, 0);
    expect(syncPosts, 1);
  });

  // ESC-12 part B (sync-protocol.md 1.17.0): the five commitment codes are
  // permanent; a refused crop listing or trip is reverted and leaves the
  // "Sync failed" count.
  group('part B: a refused crop listing is permanent and reverted', () {
    for (final code in [
      'REPUTATION_RESTRICTED',
      'CROP_HAS_ACTIVE_ORDERS',
    ]) {
      for (final (action, data) in [
        ('create', '{"listingId":"crop-1","version":1}'),
        ('delete', '{"listingId":"crop-1","status":"expired","version":2}'),
      ]) {
        test('$code on $action', () async {
          final message = '$code: refused';
          await enqueue(
            'op-1',
            entityType: 'crop_listing',
            entityId: 'crop-1',
            action: action,
            data: data,
          );
          replyWith([error('op-1', message)]);

          final result = await executor.executeBatch();
          await _waitFor(() => rejections().isNotEmpty);

          expect(result.failed, 1);
          expect(await isRejected('op-1'), isTrue);
          expect(await syncQueue.retryFailed(), 0);
          expect(reverts, hasLength(1));
          expect(reverts.single.$1.operationId, 'op-1');
          expect(reverts.single.$2, message);
          expect((await syncQueue.getOperation('op-1'))!.reverted, isTrue);
          expect(await syncQueue.getFailedCount(), 0);
          final event = rejections().single;
          expect(event.entityType, 'crop_listing');
          expect(event.entityId, 'crop-1');
          expect(event.action, action);
          expect(event.message, message);
        });
      }
    }
  });

  group('part B: a refused trip is permanent and reverted', () {
    for (final code in [
      'REPUTATION_RESTRICTED',
      'TRIP_HAS_BOOKINGS',
    ]) {
      for (final (action, data) in [
        ('create', '{"tripId":"trip-1","version":1}'),
        ('update', '{"status":"cancelled","version":2}'),
      ]) {
        test('$code on $action', () async {
          final message = '$code: refused';
          await enqueue(
            'op-1',
            entityType: 'trip',
            entityId: 'trip-1',
            action: action,
            data: data,
          );
          replyWith([error('op-1', message)]);

          final result = await executor.executeBatch();
          await _waitFor(() => rejections().isNotEmpty);

          expect(result.failed, 1);
          expect(await isRejected('op-1'), isTrue);
          expect(await syncQueue.retryFailed(), 0);
          expect(reverts, hasLength(1));
          expect(reverts.single.$1.entityType, 'trip');
          expect(reverts.single.$2, message);
          expect((await syncQueue.getOperation('op-1'))!.reverted, isTrue);
          expect(await syncQueue.getFailedCount(), 0);
          final event = rejections().single;
          expect(event.entityType, 'trip');
          expect(event.entityId, 'trip-1');
          expect(event.action, action);
          expect(event.message, message);
        });
      }
    }
  });

  group('part B: a refused order is permanent and reverted', () {
    for (final (code, action) in [
      ('REPUTATION_RESTRICTED', 'create'), // the owner is restricted
      ('REPUTATION_RESTRICTED', 'update'), // the farmer's accept
      ('TRIP_CAPACITY_EXCEEDED', 'create'),
      ('TRIP_CAPACITY_EXCEEDED', 'update'), // a replacement truck
      ('APP_UPDATE_REQUIRED', 'create'),
      ('APP_UPDATE_REQUIRED', 'update'),
    ]) {
      test('$code on $action', () async {
        final message = '$code: refused';
        await enqueue('op-1', action: action);
        replyWith([error('op-1', message)]);

        await executor.executeBatch();
        await _waitFor(() => rejections().isNotEmpty);

        expect(await isRejected('op-1'), isTrue);
        expect(await syncQueue.retryFailed(), 0);
        expect(reverts.single.$1.entityType, 'order');
        expect(reverts.single.$2, message);
        expect(await syncQueue.getFailedCount(), 0);
        expect(rejections().single.message, message);
        expect(kycCalls, isEmpty);
      });
    }
  });

  test(
      'part B: a refused crop create also rejects and flags the ops queued '
      'behind it, reverted once', () async {
    await enqueue(
      'op-create',
      entityType: 'crop_listing',
      entityId: 'crop-1',
      action: 'create',
    );
    await enqueue(
      'op-delete',
      entityType: 'crop_listing',
      entityId: 'crop-1',
      action: 'delete',
    );
    replyWith([
      error('op-create', 'REPUTATION_RESTRICTED: restricted'),
      error('op-delete', 'Crop listing not found.'),
    ]);

    await executor.executeBatch();
    await _waitFor(() => rejections().isNotEmpty);

    for (final id in ['op-create', 'op-delete']) {
      expect(await isRejected(id), isTrue, reason: id);
      expect((await syncQueue.getOperation(id))!.reverted, isTrue, reason: id);
    }
    expect(reverts, hasLength(1));
    expect(reverts.single.$1.action, 'create');
    expect(await syncQueue.getFailedCount(), 0);
  });

  group('part B: CROP_* and TRIP_* codes the client cannot trust keep retrying',
      () {
    for (final (entityType, entityId, message) in [
      ('crop_listing', 'crop-1', 'CROP_UNAUTHORIZED: Only farmers can list.'),
      (
        'crop_listing',
        'crop-1',
        'CROP_VALIDATION: The farmer profile could not be read. Please try '
            'again later.',
      ),
      ('trip', 'trip-1', 'TRIP_UNAUTHORIZED: Only truckers can add trips.'),
      ('trip', 'trip-1', 'TRIP_VALIDATION: bad'),
    ]) {
      test(message.split(':').first, () async {
        await enqueue(
          'op-1',
          entityType: entityType,
          entityId: entityId,
          action: 'create',
        );
        replyWith([error('op-1', message)]);

        await executor.executeBatch();

        final op = await syncQueue.getOperation('op-1');
        expect(op!.status, SyncStatus.pending);
        expect(op.retryCount, 1);
        expect(op.reverted, isFalse);
        expect(reverts, isEmpty);
        expect(rejections(), isEmpty);
      });
    }
  });

  group(
      'part B: a code meant for another entity is permanent but not reverted '
      'and keeps counting', () {
    for (final (entityType, entityId, message) in [
      ('crop_listing', 'crop-1', 'TRIP_HAS_BOOKINGS: x'),
      ('trip', 'trip-1', 'CROP_HAS_ACTIVE_ORDERS: x'),
      ('trip', 'trip-1', 'TRIP_CAPACITY_EXCEEDED: x'),
      ('crop_listing', 'crop-1', 'FRAUD_VELOCITY_EXCEEDED: slow down'),
    ]) {
      test('$entityType $message', () async {
        await enqueue(
          'op-1',
          entityType: entityType,
          entityId: entityId,
          data: '{"version":2}',
        );
        replyWith([error('op-1', message)]);

        await executor.executeBatch();
        await _waitFor(() => rejections().isNotEmpty);

        expect(await isRejected('op-1'), isTrue);
        expect(reverts, isEmpty);
        expect((await syncQueue.getOperation('op-1'))!.reverted, isFalse);
        expect(await syncQueue.getFailedCount(), 1);
        expect(rejections().single.message, message);
      });
    }
  });

  // Lead decision (spec K1, ESC-25): the minimum-version gate is for order
  // commands. Any other operation is valid as it is, so it waits for the
  // updated build instead of being refused for good.
  group('part B: APP_UPDATE_REQUIRED on anything but an order is held', () {
    const message = 'APP_UPDATE_REQUIRED: minimum 2.0.0';
    for (final (entityType, entityId, action) in [
      ('crop_listing', 'crop-1', 'create'),
      ('crop_listing', 'crop-1', 'delete'),
      ('trip', 'trip-1', 'create'),
      ('trip', 'trip-1', 'update'),
      ('delivery', 'del-1', 'create'),
      ('delivery', 'del-1', 'update'),
      ('message', 'msg-1', 'create'),
      ('rating', 'rating-1', 'create'),
      ('buyer_intent', 'intent-1', 'create'),
    ]) {
      test('$entityType $action: kept pending, never reverted, told once',
          () async {
        await enqueue(
          'op-1',
          entityType: entityType,
          entityId: entityId,
          action: action,
          data: '{"version":1}',
        );
        replyWith([error('op-1', message)]);

        final result = await executor.executeBatch();
        await _waitFor(() => rejections().isNotEmpty);

        final op = (await syncQueue.getOperation('op-1'))!;
        expect(op.status, SyncStatus.pending);
        expect(op.retryCount, 0);
        expect(op.reverted, isFalse);
        expect(result.failed, 0);
        expect(result.deferred, 1);
        expect(reverts, isEmpty);
        expect(await syncQueue.getFailedCount(), 0);
        expect(await syncQueue.getPendingCount(), 1);
        final event = rejections().single;
        expect(event.entityType, entityType);
        expect(event.action, action);
        expect(event.message, message);
        expect(events.appUpdateRequired, isTrue);

        // This build does not send it again.
        final again = await executor.executeBatch();
        expect(again.status, SyncBatchStatus.empty);
        expect(syncPosts, 1);
        expect(rejections(), hasLength(1));
      });
    }

    test('a background match event is held without a message', () async {
      await enqueue(
        'op-match',
        entityType: 'match_event',
        entityId: 'match-1',
        action: 'create',
      );
      replyWith([error('op-match', message)]);

      await executor.executeBatch();
      await Future<void>.delayed(Duration.zero);

      expect(
        (await syncQueue.getOperation('op-match'))!.status,
        SyncStatus.pending,
      );
      expect(rejections(), isEmpty);
      expect(
        (await executor.executeBatch()).status,
        SyncBatchStatus.empty,
      );
      expect(syncPosts, 1);
    });

    test(
        'other operations keep syncing: only the held one is left out of the '
        'next batch', () async {
      await enqueue(
        'op-crop',
        entityType: 'crop_listing',
        entityId: 'crop-1',
        action: 'create',
      );
      replyWith([error('op-crop', message)]);
      await executor.executeBatch();
      await _waitFor(() => rejections().isNotEmpty);

      await enqueue(
        'op-pickup',
        entityType: 'delivery',
        entityId: 'del-1',
        data: '{"status":"pickedUp","version":1}',
      );
      replyWith([
        {'operationId': 'op-pickup', 'status': 'success', 'serverVersion': 2},
      ]);
      final next = await executor.executeBatch();

      expect(next.succeeded, 1);
      expect(sentOperationIds.last, ['op-pickup']);
      expect(
        (await syncQueue.getOperation('op-pickup'))!.status,
        SyncStatus.synced,
      );
      expect(
        (await syncQueue.getOperation('op-crop'))!.status,
        SyncStatus.pending,
      );
    });

    test('syncNow stops at once when only held operations remain', () async {
      adapter.onGet('/health', (server) => server.reply(200, {'status': 'ok'}));
      await enqueue(
        'op-1',
        entityType: 'trip',
        entityId: 'trip-1',
        action: 'create',
      );
      replyWith([error('op-1', message)]);

      // A backoff would take 5 seconds first.
      final result =
          await executor.syncNow().timeout(const Duration(seconds: 4));

      expect(result.deferred, 1);
      expect(result.failed, 0);
      expect(syncPosts, 1);
      // Triggers in the same session (enqueue, reconnect) send nothing.
      expect(await syncQueue.retryFailed(), 0);
      await executor.syncNow().timeout(const Duration(seconds: 4));
      expect(syncPosts, 1);
    });

    test('a new build (a new executor) sends it again', () async {
      await enqueue(
        'op-1',
        entityType: 'trip',
        entityId: 'trip-1',
        action: 'create',
      );
      replyWith([error('op-1', message)]);
      await executor.executeBatch();
      await _waitFor(() => rejections().isNotEmpty);

      executor.dispose();
      executor = buildExecutor(securityEvents: SecurityEvents.forTesting());
      replyWith([
        {'operationId': 'op-1', 'status': 'success', 'serverVersion': 1},
      ]);
      final result = await executor.executeBatch();

      expect(result.succeeded, 1);
      expect(syncPosts, 2);
      expect(
        (await syncQueue.getOperation('op-1'))!.status,
        SyncStatus.synced,
      );
    });
  });

  test(
      'part B: a later op of the entity with a reverting code still reverts '
      'it; the first refusal is what the user is told', () async {
    await enqueue(
      'op-1',
      entityType: 'crop_listing',
      entityId: 'crop-1',
      data: '{"version":2}',
    );
    await enqueue(
      'op-2',
      entityType: 'crop_listing',
      entityId: 'crop-1',
      action: 'delete',
      data: '{"version":2}',
    );
    replyWith([
      error('op-1', 'SYNC_VALIDATION: bad field'),
      error('op-2', 'CROP_HAS_ACTIVE_ORDERS: open orders'),
    ]);

    await executor.executeBatch();
    await _waitFor(() => rejections().isNotEmpty);

    expect(reverts, hasLength(1));
    expect(reverts.single.$1.operationId, 'op-2');
    expect(reverts.single.$2, 'CROP_HAS_ACTIVE_ORDERS: open orders');
    expect(rejections().single.message, 'SYNC_VALIDATION: bad field');
    expect(await syncQueue.getFailedCount(), 0);
  });

  test('part B: APP_UPDATE_REQUIRED raises the app update flag', () async {
    final updates = <void>[];
    final flagSub = events.onAppUpdateRequired.listen(updates.add);
    addTearDown(flagSub.cancel);
    expect(events.appUpdateRequired, isFalse);
    await enqueue(
      'op-1',
      entityType: 'trip',
      entityId: 'trip-1',
      action: 'create',
    );
    replyWith([error('op-1', 'APP_UPDATE_REQUIRED: minimum 2.0.0')]);

    await executor.executeBatch();
    await _waitFor(() => updates.isNotEmpty);

    expect(events.appUpdateRequired, isTrue);
    expect(updates, hasLength(1));
  });

  test(
      'part B: a revert handler that throws leaves the crop op counted as '
      'failed', () async {
    executor.dispose();
    executor = buildExecutor(
      securityEvents: events,
      onPermanentRejection: (op, message) async => throw StateError('boom'),
    );
    await enqueue(
      'op-1',
      entityType: 'crop_listing',
      entityId: 'crop-1',
      action: 'delete',
      data: '{"version":2}',
    );
    replyWith([error('op-1', 'CROP_HAS_ACTIVE_ORDERS: open orders')]);

    await executor.executeBatch();
    await _waitFor(() => rejections().isNotEmpty);

    expect(await isRejected('op-1'), isTrue);
    expect((await syncQueue.getOperation('op-1'))!.reverted, isFalse);
    expect(await syncQueue.getFailedCount(), 1);
    expect(rejections(), hasLength(1));
  });
}
