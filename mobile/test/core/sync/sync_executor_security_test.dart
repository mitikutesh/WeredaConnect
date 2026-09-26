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

class FakeConnectivitySource implements ConnectivitySource {
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

void main() {
  late Isar isar;
  late Directory tempDir;
  late SyncQueue syncQueue;
  late FakeConnectivitySource source;
  late Dio dio;
  late DioAdapter adapter;
  late ConnectivityMonitor monitor;
  late SecurityEvents events;
  late List<SecurityEvent> emitted;
  late StreamSubscription<SecurityEvent> sub;
  late List<(SyncOperation, String)> kycCalls;
  late SyncExecutor executor;

  setUpAll(() async {
    await Isar.initializeIsarCore(download: true);
  });

  setUp(() async {
    tempDir = Directory.systemTemp.createTempSync('sync_security_test_');
    isar = await Isar.open(
      [SyncOperationSchema],
      directory: tempDir.path,
      name: 'sync_security_${DateTime.now().microsecondsSinceEpoch}',
    );
    syncQueue = SyncQueue(isar);
    source = FakeConnectivitySource();
    dio = Dio(BaseOptions(baseUrl: 'https://api.test.com'));
    adapter = DioAdapter(dio: dio);
    monitor = ConnectivityMonitor(source: source, dio: dio);
    events = SecurityEvents.forTesting();
    emitted = [];
    sub = events.events.listen(emitted.add);
    kycCalls = [];
    executor = SyncExecutor(
      syncQueue: syncQueue,
      connectivityMonitor: monitor,
      dio: dio,
      clientId: 'test-device',
      securityEvents: events,
      onKycRequired: (op, message) async => kycCalls.add((op, message)),
    );
  });

  tearDown(() async {
    await sub.cancel();
    executor.dispose();
    monitor.dispose();
    await source.controller.close();
    await isar.close(deleteFromDisk: true);
    if (tempDir.existsSync()) tempDir.deleteSync(recursive: true);
  });

  Future<void> enqueueOrderPayment(String operationId, String orderId) {
    return syncQueue.enqueue(
      userId: 'buyer-1',
      entityType: 'order',
      entityId: orderId,
      action: 'update',
      data: '{"status":"payment_submitted","version":2}',
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

  test('AUTH_040 keeps the op pending, burns no retry, asks for step-up',
      () async {
    await enqueueOrderPayment('op-1', 'order-1');
    replyWith([
      {
        'operationId': 'op-1',
        'status': 'error',
        'message': 'AUTH_040: step-up required',
      },
    ]);

    final result = await executor.executeBatch();
    await Future<void>.delayed(Duration.zero);

    expect(result.stepUpRequired, 1);
    expect(result.failed, 0);
    expect(result.hasErrors, isFalse);
    final op = await syncQueue.getOperation('op-1');
    expect(op!.status, SyncStatus.pending);
    expect(op.retryCount, 0);
    expect(emitted.whereType<StepUpRequired>(), hasLength(1));
    expect(kycCalls, isEmpty);
  });

  test('after the user cancels step-up, later syncs do not re-prompt',
      () async {
    await enqueueOrderPayment('op-1', 'order-1');
    replyWith([
      {
        'operationId': 'op-1',
        'status': 'error',
        'message': 'AUTH_040: step-up required',
      },
    ]);

    await executor.executeBatch();
    await Future<void>.delayed(Duration.zero);
    expect(emitted.whereType<StepUpRequired>(), hasLength(1));

    // The listener reports the cancelled dialog.
    events.stepUpDeclined();
    final again = await executor.executeBatch();
    await Future<void>.delayed(Duration.zero);

    expect(again.stepUpRequired, 1);
    expect(emitted.whereType<StepUpRequired>(), hasLength(1));
    final op = await syncQueue.getOperation('op-1');
    expect(op!.status, SyncStatus.pending);
  });

  test('syncNow stops when only step-up ops remain (no retry storm)', () async {
    await enqueueOrderPayment('op-1', 'order-1');
    adapter.onGet('/health', (server) => server.reply(200, {'ok': true}));
    var calls = 0;
    adapter.onPost(
      '/sync',
      (server) {
        calls++;
        server.reply(200, {
          'results': [
            {
              'operationId': 'op-1',
              'status': 'error',
              'message': 'AUTH_040: step-up required',
            },
          ],
        });
      },
      data: Matchers.any,
    );

    final result = await executor.syncNow();

    expect(calls, 1);
    expect(result.stepUpRequired, 1);
    final op = await syncQueue.getOperation('op-1');
    expect(op!.status, SyncStatus.pending);
  });

  test('a completed step-up re-triggers sync of the held op', () async {
    await enqueueOrderPayment('op-1', 'order-1');
    adapter.onGet('/health', (server) => server.reply(200, {'ok': true}));
    replyWith([
      {'operationId': 'op-1', 'status': 'success', 'serverVersion': 3},
    ]);
    executor.start();

    events.stepUpCompleted();
    // Let the listener's syncNow run to completion.
    for (var i = 0; i < 20; i++) {
      await Future<void>.delayed(const Duration(milliseconds: 10));
      final op = await syncQueue.getOperation('op-1');
      if (op!.status == SyncStatus.synced) break;
    }

    final op = await syncQueue.getOperation('op-1');
    expect(op!.status, SyncStatus.synced);
  });

  test('ORDER_KYC_REQUIRED rejects the op, reverts via handler, prompts KYC',
      () async {
    await enqueueOrderPayment('op-1', 'order-7');
    replyWith([
      {
        'operationId': 'op-1',
        'status': 'error',
        'message': 'ORDER_KYC_REQUIRED: Fayda verification needed',
      },
    ]);

    final result = await executor.executeBatch();
    await Future<void>.delayed(Duration.zero);

    expect(result.failed, 1);
    expect(result.operationErrors['op-1'], startsWith('ORDER_KYC_REQUIRED'));
    final op = await syncQueue.getOperation('op-1');
    expect(op!.status, SyncStatus.failed);
    // Rejected, not retryable: retryFailed() must skip it.
    expect(await syncQueue.retryFailed(), 0);
    expect(kycCalls.single.$1.entityId, 'order-7');
    final kyc = emitted.whereType<KycRequired>().single;
    expect(kyc.orderId, 'order-7');
    expect(kyc.isPhoneNotVerified, isFalse);
  });

  test('KycRequired tells a phone problem apart', () {
    const event = KycRequired(
      orderId: 'o',
      message: 'ORDER_KYC_REQUIRED: phone not verified',
    );
    expect(event.isPhoneNotVerified, isTrue);
  });
}
