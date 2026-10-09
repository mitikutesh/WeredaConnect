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
    dio.interceptors.add(
      InterceptorsWrapper(
        onRequest: (options, handler) {
          if (options.path == '/sync') syncPosts++;
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
}
