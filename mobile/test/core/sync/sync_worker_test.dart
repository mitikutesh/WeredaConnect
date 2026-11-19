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
import 'package:wereda_connect/core/sync/sync_worker.dart';

/// Fake [ConnectivitySource] for test-driven connectivity events.
class FakeConnectivitySource implements ConnectivitySource {
  final StreamController<List<ConnectivityResult>> controller =
      StreamController<List<ConnectivityResult>>.broadcast();

  List<ConnectivityResult> _currentResults = [ConnectivityResult.none];

  set currentResults(List<ConnectivityResult> results) {
    _currentResults = results;
  }

  @override
  Stream<List<ConnectivityResult>> get onConnectivityChanged =>
      controller.stream;

  @override
  Future<List<ConnectivityResult>> checkConnectivity() async {
    return _currentResults;
  }

  void dispose() {
    controller.close();
  }
}

void main() {
  group('SyncWorker', () {
    late Isar isar;
    late Directory tempDir;
    late SyncQueue syncQueue;
    late FakeConnectivitySource fakeSource;
    late Dio dio;
    late DioAdapter dioAdapter;
    late ConnectivityMonitor connectivityMonitor;
    late SyncExecutor executor;
    late SyncWorker worker;

    setUpAll(() async {
      await Isar.initializeIsarCore(download: true);
    });

    setUp(() async {
      tempDir = Directory.systemTemp.createTempSync('sync_worker_test_');
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
        name: 'test_worker_${DateTime.now().millisecondsSinceEpoch}',
      );
      syncQueue = SyncQueue(isar);

      fakeSource = FakeConnectivitySource();
      fakeSource.currentResults = [ConnectivityResult.wifi];
      dio = Dio(BaseOptions(baseUrl: 'https://api.test.com'));
      dioAdapter = DioAdapter(dio: dio);
      connectivityMonitor = ConnectivityMonitor(
        source: fakeSource,
        dio: dio,
      );

      executor = SyncExecutor(
        syncQueue: syncQueue,
        connectivityMonitor: connectivityMonitor,
        dio: dio,
        clientId: 'test-device-001',
      );

      worker = SyncWorker(
        executor: executor,
        syncQueue: syncQueue,
      );
    });

    tearDown(() async {
      worker.dispose();
      connectivityMonitor.dispose();
      fakeSource.dispose();
      await isar.close(deleteFromDisk: true);
      if (tempDir.existsSync()) {
        tempDir.deleteSync(recursive: true);
      }
    });

    test('start sets isStarted to true', () {
      // Mock health check to avoid actual network call from immediate sync
      dioAdapter.onGet(
        '/health',
        (server) => server.throws(
          0,
          DioException(
            requestOptions: RequestOptions(path: '/health'),
            type: DioExceptionType.connectionError,
          ),
        ),
      );

      worker.start();

      expect(worker.isStarted, isTrue);
    });

    test('start is idempotent', () {
      dioAdapter.onGet(
        '/health',
        (server) => server.throws(
          0,
          DioException(
            requestOptions: RequestOptions(path: '/health'),
            type: DioExceptionType.connectionError,
          ),
        ),
      );

      worker.start();
      worker.start(); // Second call should be safe

      expect(worker.isStarted, isTrue);
    });

    test('stop sets isStarted to false', () {
      dioAdapter.onGet(
        '/health',
        (server) => server.throws(
          0,
          DioException(
            requestOptions: RequestOptions(path: '/health'),
            type: DioExceptionType.connectionError,
          ),
        ),
      );

      worker.start();
      worker.stop();

      expect(worker.isStarted, isFalse);
    });

    test('onAppResumed triggers a sync attempt', () async {
      // Enqueue an operation
      await syncQueue.enqueue(
        userId: 'user-001',
        entityType: 'crop_listing',
        entityId: 'listing-resume',
        action: 'create',
        data: '{"cropType":"teff"}',
        operationId: 'op-resume',
      );

      // Mock health check success
      dioAdapter.onGet(
        '/health',
        (server) => server.reply(200, {'status': 'ok'}),
      );

      // Mock sync endpoint
      dioAdapter.onPost(
        '/sync',
        (server) => server.reply(200, {
          'syncBatchId': 'batch-resume',
          'results': [
            {
              'operationId': 'op-resume',
              'status': 'success',
              'serverVersion': 1,
            }
          ],
        }),
        data: Matchers.any,
      );

      final result = await worker.onAppResumed();

      expect(result.succeeded, 1);

      // Verify the operation was synced
      final op = await syncQueue.getOperation('op-resume');
      expect(op!.status, SyncStatus.synced);
    });

    test('onAppResumed returns noInternet when offline', () async {
      dioAdapter.onGet(
        '/health',
        (server) => server.throws(
          0,
          DioException(
            requestOptions: RequestOptions(path: '/health'),
            type: DioExceptionType.connectionError,
          ),
        ),
      );

      final result = await worker.onAppResumed();

      expect(result.status, SyncBatchStatus.noInternet);
    });

    test('dispose releases resources without errors', () {
      dioAdapter.onGet(
        '/health',
        (server) => server.throws(
          0,
          DioException(
            requestOptions: RequestOptions(path: '/health'),
            type: DioExceptionType.connectionError,
          ),
        ),
      );

      worker.start();
      worker.dispose();

      expect(worker.isStarted, isFalse);
    });

    test('constants have expected values', () {
      expect(SyncWorker.syncInterval, const Duration(minutes: 15));
      expect(SyncWorker.purgeInterval, const Duration(hours: 24));
      // BUG-32: few attempts, growing waits.
      expect(SyncWorker.retryDelays, const [
        Duration(seconds: 10),
        Duration(seconds: 30),
        Duration(minutes: 1),
        Duration(minutes: 2),
        Duration(minutes: 5),
      ]);
    });

    // BUG-32: one failed health check after the signal came back used to
    // leave the queue untouched until the 15 minute timer.
    group('BUG-32: early retries after a sync that did not reach the server',
        () {
      const tick = Duration(milliseconds: 30);
      late int healthChecks;

      /// How many health checks fail before one succeeds.
      late int failFirst;

      SyncWorker retrying({List<Duration> delays = const [tick, tick]}) =>
          worker = SyncWorker(
            executor: executor,
            syncQueue: syncQueue,
            connectivityMonitor: connectivityMonitor,
            retryDelays: delays,
          );

      Future<void> enqueueListing() => syncQueue.enqueue(
            userId: 'user-001',
            entityType: 'crop_listing',
            entityId: 'listing-retry',
            action: 'create',
            data: '{"cropType":"teff"}',
            operationId: 'op-retry',
          );

      Future<SyncStatus> opStatus() async =>
          (await syncQueue.getOperation('op-retry'))!.status;

      setUp(() {
        healthChecks = 0;
        failFirst = 0;
        dio.interceptors.add(
          InterceptorsWrapper(
            onRequest: (options, handler) {
              if (options.path != '/health') return handler.next(options);
              healthChecks++;
              if (healthChecks <= failFirst) {
                return handler.reject(
                  DioException(
                    requestOptions: options,
                    type: DioExceptionType.connectionTimeout,
                  ),
                );
              }
              handler.resolve(
                Response<dynamic>(requestOptions: options, statusCode: 200),
              );
            },
          ),
        );
        dioAdapter.onPost(
          '/sync',
          (server) => server.reply(200, {
            'syncBatchId': 'batch-retry',
            'results': [
              {
                'operationId': 'op-retry',
                'status': 'success',
                'serverVersion': 1,
              }
            ],
          }),
          data: Matchers.any,
        );
      });

      test(
          'a waiting change goes up by itself once the health check passes '
          'again', () async {
        failFirst = 2;
        await enqueueListing();

        retrying().start();
        for (var i = 0; i < 100 && await opStatus() != SyncStatus.synced; i++) {
          await Future<void>.delayed(tick);
        }

        expect(await opStatus(), SyncStatus.synced);
        expect(healthChecks, 3, reason: 'the first sync and two retries');
      });

      test('the retries are bounded: they stop after the last wait', () async {
        failFirst = 1000;
        await enqueueListing();

        retrying().start();
        await Future<void>.delayed(tick * 15);

        expect(healthChecks, 3, reason: 'the first sync and two retries');
        expect(await opStatus(), SyncStatus.pending);
      });

      test('an app resume starts a new round of retries', () async {
        failFirst = 1000;
        await enqueueListing();
        final worker = retrying()..start();
        await Future<void>.delayed(tick * 15);
        expect(healthChecks, 3);

        await worker.onAppResumed();
        await Future<void>.delayed(tick * 15);

        expect(healthChecks, 6);
      });

      test('nothing waiting: no retry', () async {
        failFirst = 1000;

        retrying().start();
        await Future<void>.delayed(tick * 10);

        expect(healthChecks, 1);
      });

      test('the phone is offline: no health check and no retry', () async {
        fakeSource.currentResults = [ConnectivityResult.none];
        await enqueueListing();

        retrying().start();
        await Future<void>.delayed(tick * 10);

        expect(healthChecks, 0);
        expect(await opStatus(), SyncStatus.pending);
      });

      test('stop cancels a retry that was scheduled', () async {
        failFirst = 1000;
        await enqueueListing();
        final worker = retrying(delays: const [Duration(milliseconds: 200)])
          ..start();
        await Future<void>.delayed(tick * 2);
        expect(healthChecks, 1);

        worker.stop();
        await Future<void>.delayed(const Duration(milliseconds: 400));

        expect(healthChecks, 1);
      });

      test('without a connectivity monitor nothing is retried early', () async {
        failFirst = 1000;
        await enqueueListing();

        worker.start();
        await Future<void>.delayed(tick * 10);

        expect(healthChecks, 1);
      });
    });
  });
}
