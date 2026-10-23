import 'dart:async';
import 'dart:io';

import 'package:connectivity_plus/connectivity_plus.dart';
import 'package:dio/dio.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http_mock_adapter/http_mock_adapter.dart';
import 'package:isar/isar.dart';

import 'package:wereda_connect/core/database/schemas/sync_operation.dart';
import 'package:wereda_connect/core/errors/app_error.dart';
import 'package:wereda_connect/core/security/security_events.dart';
import 'package:wereda_connect/core/sync/connectivity_monitor.dart';
import 'package:wereda_connect/core/sync/sync_executor.dart';
import 'package:wereda_connect/core/sync/sync_queue.dart';

import '../../helpers/wait_for.dart';

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

const _held = 'FRAUD_ACCOUNT_HELD: Your account is under review. Some actions '
    'are paused. Contact support.';

/// FRD-06: FRAUD_ACCOUNT_HELD (FRD-04) on a queued crop, trip or order
/// operation is never sent again, an order operation is put back, and the
/// user is told the account is under review.
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

  setUp(() async {
    tempDir = Directory.systemTemp.createTempSync('sync_fraud_hold_test_');
    isar = await Isar.open(
      [SyncOperationSchema],
      directory: tempDir.path,
      name: 'sync_fraud_hold_${DateTime.now().microsecondsSinceEpoch}',
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
    executor = SyncExecutor(
      syncQueue: syncQueue,
      connectivityMonitor: monitor,
      dio: dio,
      clientId: 'test-device',
      securityEvents: events,
      onKycRequired: (op, message) async => kycCalls.add((op, message)),
      onPermanentRejection: (op, message) async => reverts.add((op, message)),
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

  void replyWith(List<Map<String, dynamic>> results) {
    adapter.onPost(
      '/sync',
      (server) => server.reply(200, {'syncBatchId': 'b', 'results': results}),
      data: Matchers.any,
    );
  }

  List<SyncOperationRejected> rejections() =>
      emitted.whereType<SyncOperationRejected>().toList();

  for (final (role, entityType, entityId, action, data, reverted) in const [
    (
      'buyer',
      'order',
      'order-1',
      'update',
      '{"status":"payment_submitted","version":2,"paymentMethod":"telebirr"}',
      true,
    ),
    ('buyer', 'order', 'order-1', 'create', '{"status":"created"}', true),
    (
      'farmer',
      'crop_listing',
      'crop-1',
      'create',
      '{"cropType":"teff"}',
      false
    ),
    ('trucker', 'trip', 'trip-1', 'create', '{"origin":"Adama"}', false),
  ]) {
    test('$role: $entityType $action is refused for good and explained',
        () async {
      await syncQueue.enqueue(
        userId: '$role-1',
        entityType: entityType,
        entityId: entityId,
        action: action,
        data: data,
        operationId: 'op-1',
      );
      replyWith([
        {'operationId': 'op-1', 'status': 'error', 'message': _held},
      ]);

      final result = await executor.executeBatch();
      await waitFor(() => rejections().isNotEmpty);

      expect(result.failed, 1);
      expect(result.operationErrors['op-1'], _held);
      // The batch message names the code too.
      expect(result.errorMessage, _held);
      final op = await syncQueue.getOperation('op-1');
      expect(SyncQueue.isRejected(op!), isTrue);
      // A reconnect never sends it again.
      expect(await syncQueue.retryFailed(), 0);
      await executor.executeBatch();
      expect(syncPosts, 1);

      // An order operation goes back to the server's view (ESC-12); crop and
      // trip keep their row like the other FRAUD_* codes.
      expect(
        reverts.map((r) => r.$1.operationId).toList(),
        reverted ? ['op-1'] : <String>[],
      );
      final event = rejections().single;
      expect(event.entityType, entityType);
      expect(event.action, action);
      expect(event.message, _held);
      expect(event.userId, '$role-1');
      expect(
        syncRejectionErrorCode(
          entityType: event.entityType,
          action: event.action,
          message: event.message,
        ),
        AppErrorCode.accountHeld,
      );
      expect(emitted.whereType<StepUpRequired>(), isEmpty);
      expect(kycCalls, isEmpty);
    });
  }
}
