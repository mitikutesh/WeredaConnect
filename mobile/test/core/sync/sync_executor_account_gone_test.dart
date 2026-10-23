import 'dart:async';
import 'dart:io';

import 'package:connectivity_plus/connectivity_plus.dart';
import 'package:dio/dio.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http_mock_adapter/http_mock_adapter.dart';
import 'package:isar/isar.dart';

import 'package:wereda_connect/core/auth/session_signals.dart';
import 'package:wereda_connect/core/database/schemas/sync_operation.dart';
import 'package:wereda_connect/core/sync/connectivity_monitor.dart';
import 'package:wereda_connect/core/sync/sync_executor.dart';
import 'package:wereda_connect/core/sync/sync_queue.dart';

class _OnlineSource implements ConnectivitySource {
  @override
  Stream<List<ConnectivityResult>> get onConnectivityChanged =>
      const Stream.empty();

  @override
  Future<List<ConnectivityResult>> checkConnectivity() async =>
      [ConnectivityResult.wifi];
}

Map<String, dynamic> _gone(String state) => {
      'error': {
        'code': 'ACCOUNT_004',
        'message': 'This account has been deleted.',
        'deletionState': state,
      },
    };

/// ACC-03 (sync-protocol.md 1.26.0): a batch refused with 410 ACCOUNT_004
/// is terminal for the client. Nothing burns a retry, the sync stops, and
/// the AuthBloc hears about it through [SessionSignals.accountGone].
void main() {
  late Isar isar;
  late Directory dir;
  late SyncQueue queue;
  late Dio dio;
  late DioAdapter adapter;
  late SessionSignals signals;
  late List<String> signalled;
  late StreamSubscription<String> signalSub;
  late int syncRequests;
  late String currentUser;
  late SyncExecutor executor;

  setUpAll(() async {
    await Isar.initializeIsarCore(download: true);
  });

  setUp(() async {
    dir = Directory.systemTemp.createTempSync('sync_account_gone_test_');
    isar = await Isar.open(
      [SyncOperationSchema],
      directory: dir.path,
      name: 'account_gone_${DateTime.now().microsecondsSinceEpoch}',
    );
    queue = SyncQueue(isar);
    dio = Dio(BaseOptions(baseUrl: 'https://api.test'));
    adapter = DioAdapter(dio: dio);
    adapter.onGet('/health', (server) => server.reply(200, {'ok': true}));
    syncRequests = 0;
    dio.interceptors.add(
      InterceptorsWrapper(
        onRequest: (options, handler) {
          if (options.path == SyncExecutor.syncEndpoint) syncRequests++;
          handler.next(options);
        },
      ),
    );
    signals = SessionSignals.forTesting();
    signalled = [];
    signalSub = signals.onAccountGone.listen(signalled.add);
    currentUser = 'user-1';
    executor = SyncExecutor(
      syncQueue: queue,
      connectivityMonitor:
          ConnectivityMonitor(dio: dio, source: _OnlineSource()),
      dio: dio,
      clientId: 'test',
      getCurrentUserId: () async => currentUser,
      sessionSignals: signals,
    );
  });

  tearDown(() async {
    await signalSub.cancel();
    executor.dispose();
    await isar.close(deleteFromDisk: true);
    if (dir.existsSync()) dir.deleteSync(recursive: true);
  });

  Future<void> enqueue(String userId, String operationId) => queue.enqueue(
        userId: userId,
        entityType: 'cropListing',
        entityId: 'c-$operationId',
        action: 'create',
        data: '{}',
        operationId: operationId,
      );

  void refuseSync(String state) => adapter.onPost(
        SyncExecutor.syncEndpoint,
        (server) => server.reply(410, _gone(state)),
        data: Matchers.any,
      );

  Future<SyncOperation> op(String operationId) async =>
      (await queue.getOperation(operationId))!;

  for (final state in ['completed', 'cognito_pending']) {
    test('$state: kept without a retry, never sent again, signalled', () async {
      await enqueue('user-1', 'op-1');
      refuseSync(state);

      final first = await executor.executeBatch();
      final second = await executor.executeBatch();
      await Future<void>.delayed(Duration.zero);

      expect(first.status, SyncBatchStatus.accountDeleted);
      expect(first.failed, 0);
      expect(second.status, SyncBatchStatus.accountDeleted);
      expect(syncRequests, 1, reason: 'terminal: not sent again');
      expect((await op('op-1')).status, SyncStatus.pending);
      expect((await op('op-1')).retryCount, 0);
      expect(signalled, [state]);
    });
  }

  test(
      'in_progress: kept without a retry, this sync stops, a later one '
      'may send again (the deletion can abort)', () async {
    await enqueue('user-1', 'op-1');
    refuseSync('in_progress');

    final first = await executor.executeBatch();
    await executor.executeBatch();
    await Future<void>.delayed(Duration.zero);

    expect(first.status, SyncBatchStatus.accountDeleted);
    expect(syncRequests, 2);
    expect((await op('op-1')).status, SyncStatus.pending);
    expect((await op('op-1')).retryCount, 0);
    expect(signalled, ['in_progress', 'in_progress']);
  });

  test('syncNow stops at the refusal: no backoff, no second batch', () async {
    await enqueue('user-1', 'op-1');
    refuseSync('completed');

    final result = await executor
        .syncNow()
        .timeout(const Duration(seconds: 2), onTimeout: () => fail('backoff'));
    await executor.syncNow();

    expect(result.status, SyncBatchStatus.accountDeleted);
    expect(syncRequests, 1);
    expect((await op('op-1')).retryCount, 0);
  });

  test('another account on the same phone keeps syncing', () async {
    await enqueue('user-1', 'op-1');
    refuseSync('completed');
    await executor.executeBatch();

    currentUser = 'user-2';
    await enqueue('user-2', 'op-2');
    adapter.onPost(
      SyncExecutor.syncEndpoint,
      (server) => server.reply(200, {
        'syncBatchId': 'b',
        'results': [
          {'operationId': 'op-2', 'status': 'success', 'serverVersion': 1},
        ],
      }),
      data: Matchers.any,
    );

    final result = await executor.executeBatch();

    expect(result.status, isNot(SyncBatchStatus.accountDeleted));
    expect(result.succeeded, 1);
    expect(syncRequests, 2);
  });
}
