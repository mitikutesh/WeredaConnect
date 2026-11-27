import 'dart:async';
import 'dart:io';

import 'package:connectivity_plus/connectivity_plus.dart';
import 'package:dio/dio.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http_mock_adapter/http_mock_adapter.dart';
import 'package:isar/isar.dart';

import 'package:wereda_connect/core/database/schemas/crop_listing.dart';
import 'package:wereda_connect/core/database/schemas/delivery.dart';
import 'package:wereda_connect/core/database/schemas/payment.dart';
import 'package:wereda_connect/core/database/schemas/sync_operation.dart';
import 'package:wereda_connect/core/database/schemas/trip.dart';
import 'package:wereda_connect/core/database/schemas/user.dart';
import 'package:wereda_connect/core/sync/connectivity_monitor.dart';
import 'package:wereda_connect/core/sync/sync_executor.dart';
import 'package:wereda_connect/core/sync/sync_queue.dart';

class _OnlineSource implements ConnectivitySource {
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

/// The executor's preflight (crop-listing-api.md 1.6.0): a crop create waits
/// until its photos are uploaded, and so does everything queued behind it for
/// that listing, without using a retry or failing the run.
void main() {
  late Isar isar;
  late Directory tempDir;
  late SyncQueue queue;
  late _OnlineSource source;
  late Dio dio;
  late DioAdapter adapter;
  late ConnectivityMonitor monitor;

  setUpAll(() async {
    await Isar.initializeIsarCore(download: true);
  });

  setUp(() async {
    tempDir = Directory.systemTemp.createTempSync('sync_preflight_test_');
    isar = await Isar.open(
      [
        UserSchema,
        CropListingSchema,
        TripSchema,
        DeliverySchema,
        PaymentSchema,
        SyncOperationSchema,
      ],
      directory: tempDir.path,
      name: 'test_preflight_${DateTime.now().microsecondsSinceEpoch}',
    );
    queue = SyncQueue(isar);
    source = _OnlineSource();
    dio = Dio(BaseOptions(baseUrl: 'https://api.test.com'));
    adapter = DioAdapter(dio: dio);
    monitor = ConnectivityMonitor(source: source, dio: dio);
  });

  tearDown(() async {
    monitor.dispose();
    await source.controller.close();
    await isar.close(deleteFromDisk: true);
    if (tempDir.existsSync()) tempDir.deleteSync(recursive: true);
  });

  SyncExecutor executorWith(OperationPreflight preflight) => SyncExecutor(
        syncQueue: queue,
        connectivityMonitor: monitor,
        dio: dio,
        clientId: 'test-device',
        preflight: preflight,
      );

  Future<void> enqueue(
    String operationId,
    String entityType,
    String entityId,
    String action,
  ) =>
      queue.enqueue(
        userId: 'farmer-1',
        entityType: entityType,
        entityId: entityId,
        action: action,
        data: '{"x":1}',
        operationId: operationId,
      );

  Map<String, dynamic> success(List<String> ids) => {
        'results': [
          for (final id in ids)
            {'operationId': id, 'status': 'success', 'serverVersion': 1},
        ],
      };

  test('holds a refused create and the operations behind it for that listing',
      () async {
    await enqueue('op-create', 'crop_listing', 'listing-1', 'create');
    await enqueue('op-update', 'crop_listing', 'listing-1', 'update');
    await enqueue('op-trip', 'trip', 'trip-1', 'create');
    final asked = <String>[];
    final executor = executorWith((op) async {
      asked.add(op.operationId);
      return op.entityType != 'crop_listing';
    });
    addTearDown(executor.dispose);
    Object? sent;
    adapter.onPost(
      '/sync',
      (server) => server.reply(200, success(['op-trip'])),
      data: Matchers.any,
    );
    dio.interceptors.add(
      InterceptorsWrapper(
        onRequest: (options, handler) {
          sent = options.data;
          handler.next(options);
        },
      ),
    );

    final result = await executor.executeBatch();

    expect(result.succeeded, 1);
    expect(result.deferred, 2);
    expect(result.total, 3);
    expect(result.hasErrors, isFalse);
    // The update of the held listing is not even asked about.
    expect(asked, ['op-create', 'op-trip']);
    final operations = (sent! as Map<String, dynamic>)['operations'] as List;
    expect(operations.map((o) => (o as Map)['operationId']), ['op-trip']);
    for (final id in ['op-create', 'op-update']) {
      final op = await queue.getOperation(id);
      expect(op!.status, SyncStatus.pending);
      expect(op.retryCount, 0);
    }
  });

  test(
      'only held operations: nothing is sent and the batch counts them deferred',
      () async {
    await enqueue('op-create', 'crop_listing', 'listing-1', 'create');
    final executor = executorWith((_) async => false);
    addTearDown(executor.dispose);

    final result = await executor.executeBatch();

    expect(result.deferred, 1);
    expect(result.succeeded, 0);
    expect(result.hasErrors, isFalse);
    expect((await queue.getOperation('op-create'))!.status, SyncStatus.pending);
  });

  test('a preflight that throws holds the operation', () async {
    await enqueue('op-create', 'crop_listing', 'listing-1', 'create');
    final executor = executorWith((_) async => throw StateError('disk full'));
    addTearDown(executor.dispose);

    final result = await executor.executeBatch();

    expect(result.deferred, 1);
    expect((await queue.getOperation('op-create'))!.status, SyncStatus.pending);
  });

  test('one sync run asks once; the next run asks again', () async {
    await enqueue('op-create', 'crop_listing', 'listing-1', 'create');
    adapter.onGet('/health', (server) => server.reply(200, {'status': 'ok'}));
    var asked = 0;
    final executor = executorWith((_) async {
      asked++;
      return false;
    });
    addTearDown(executor.dispose);

    final first = await executor.syncNow();
    expect(first.deferred, greaterThan(0));
    expect(asked, 1);

    await executor.syncNow();
    expect(asked, 2);
  });

  test('a ready preflight lets the create go', () async {
    await enqueue('op-create', 'crop_listing', 'listing-1', 'create');
    final executor = executorWith((_) async => true);
    addTearDown(executor.dispose);
    adapter.onPost(
      '/sync',
      (server) => server.reply(200, success(['op-create'])),
      data: Matchers.any,
    );

    final result = await executor.executeBatch();

    expect(result.succeeded, 1);
    expect(result.deferred, 0);
    expect((await queue.getOperation('op-create'))!.status, SyncStatus.synced);
  });
}
