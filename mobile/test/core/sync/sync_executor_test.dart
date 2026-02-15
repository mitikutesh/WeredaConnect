import 'dart:async';
import 'dart:convert';
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
import 'package:wereda_connect/core/sync/conflict_resolver.dart';
import 'package:wereda_connect/core/sync/connectivity_monitor.dart';
import 'package:wereda_connect/core/sync/sync_executor.dart';
import 'package:wereda_connect/core/sync/sync_queue.dart';

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
  group('SyncExecutor', () {
    late Isar isar;
    late Directory tempDir;
    late SyncQueue syncQueue;
    late FakeConnectivitySource fakeSource;
    late Dio dio;
    late DioAdapter dioAdapter;
    late ConnectivityMonitor connectivityMonitor;
    late SyncExecutor executor;

    setUpAll(() async {
      await Isar.initializeIsarCore(download: true);
    });

    setUp(() async {
      tempDir = Directory.systemTemp.createTempSync('sync_executor_test_');
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
        name: 'test_executor_${DateTime.now().millisecondsSinceEpoch}',
      );
      syncQueue = SyncQueue(isar);

      fakeSource = FakeConnectivitySource();
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
    });

    tearDown(() async {
      executor.dispose();
      connectivityMonitor.dispose();
      fakeSource.dispose();
      await isar.close(deleteFromDisk: true);
      if (tempDir.existsSync()) {
        tempDir.deleteSync(recursive: true);
      }
    });

    Future<SyncOperation> _enqueueTestOperation({
      String operationId = 'op-001',
      String entityType = 'crop_listing',
      String entityId = 'listing-001',
      String action = 'create',
      String data = '{"cropType":"teff","quantity":50}',
    }) async {
      return syncQueue.enqueue(
        userId: 'user-001',
        entityType: entityType,
        entityId: entityId,
        action: action,
        data: data,
        operationId: operationId,
      );
    }

    Map<String, dynamic> _buildSuccessResponse(List<String> operationIds) {
      return {
        'syncBatchId': 'batch-001',
        'results': operationIds
            .map((id) => {
                  'operationId': id,
                  'status': 'success',
                  'serverVersion': 1,
                })
            .toList(),
      };
    }

    group('executeBatch', () {
      test('processes pending operations and marks as synced', () async {
        await _enqueueTestOperation(operationId: 'op-001');
        await _enqueueTestOperation(
          operationId: 'op-002',
          entityId: 'listing-002',
        );

        // Mock the /sync endpoint with success for both operations
        dioAdapter.onPost(
          '/sync',
          (server) => server.reply(200, _buildSuccessResponse(['op-001', 'op-002'])),
          data: Matchers.any,
        );

        final result = await executor.executeBatch();

        expect(result.total, 2);
        expect(result.succeeded, 2);
        expect(result.failed, 0);
        expect(result.status, SyncBatchStatus.success);
        expect(result.allSucceeded, isTrue);

        // Verify operations are marked as synced in the queue
        final op1 = await syncQueue.getOperation('op-001');
        final op2 = await syncQueue.getOperation('op-002');
        expect(op1!.status, SyncStatus.synced);
        expect(op2!.status, SyncStatus.synced);
      });

      test('returns empty result when queue has no pending operations',
          () async {
        final result = await executor.executeBatch();

        expect(result.total, 0);
        expect(result.status, SyncBatchStatus.empty);
      });

      test('handles conflict response with server wins', () async {
        final conflictResolutions = <String>[];

        final executorWithConflict = SyncExecutor(
          syncQueue: syncQueue,
          connectivityMonitor: connectivityMonitor,
          dio: dio,
          clientId: 'test-device-001',
          onConflict: (operation, serverData, serverVersion) async {
            conflictResolutions.add(operation.operationId);
          },
        );

        await _enqueueTestOperation(operationId: 'op-conflict');

        dioAdapter.onPost(
          '/sync',
          (server) => server.reply(200, {
            'syncBatchId': 'batch-001',
            'results': [
              {
                'operationId': 'op-conflict',
                'status': 'conflict',
                'serverVersion': 5,
                'serverData': {'cropType': 'wheat', 'quantity': 100},
                'message': 'Client version behind',
              }
            ],
          }),
          data: Matchers.any,
        );

        final result = await executorWithConflict.executeBatch();

        expect(result.total, 1);
        expect(result.conflicted, 1);
        expect(result.succeeded, 0);
        expect(result.failed, 0);
        expect(result.allSucceeded, isTrue);
        expect(conflictResolutions, ['op-conflict']);

        // Operation should be marked as synced (conflict resolved)
        final op = await syncQueue.getOperation('op-conflict');
        expect(op!.status, SyncStatus.synced);

        executorWithConflict.dispose();
      });

      test('resolves conflicts using ConflictResolver service', () async {
        // Create a local CropListing entity to be updated by conflict resolution
        final listing = CropListing()
          ..listingId = 'listing-resolver'
          ..farmerId = 'farmer-001'
          ..cropType = 'teff'
          ..quantity = 10.0
          ..pricePerKg = 25.0
          ..location = (LatLng()
            ..latitude = 9.0
            ..longitude = 38.0)
          ..description = 'Local version'
          ..audioNoteRef = null
          ..status = ListingStatus.draft
          ..createdAt = DateTime.parse('2026-02-14T10:00:00Z')
          ..updatedAt = DateTime.parse('2026-02-14T10:00:00Z')
          ..version = 1;

        await isar.writeTxn(() async {
          await isar.cropListings.put(listing);
        });

        // Create executor with ConflictResolver
        final resolver = ConflictResolver(isar);
        final executorWithResolver = SyncExecutor(
          syncQueue: syncQueue,
          connectivityMonitor: connectivityMonitor,
          dio: dio,
          clientId: 'test-device-001',
          conflictResolver: resolver,
        );

        await _enqueueTestOperation(
          operationId: 'op-resolver',
          entityId: 'listing-resolver',
          action: 'update',
        );

        // Server returns conflict with updated data (version 5)
        dioAdapter.onPost(
          '/sync',
          (server) => server.reply(200, {
            'syncBatchId': 'batch-001',
            'results': [
              {
                'operationId': 'op-resolver',
                'status': 'conflict',
                'serverVersion': 5,
                'serverData': {
                  'listingId': 'listing-resolver',
                  'farmerId': 'farmer-001',
                  'cropType': 'wheat',
                  'quantity': 50.0,
                  'pricePerKg': 30.0,
                  'location': {'latitude': 9.5, 'longitude': 38.5},
                  'description': 'Server version',
                  'audioNoteRef': 'audio/farmer-001/listing-resolver.m4a',
                  'status': 'active',
                  'createdAt': '2026-02-14T10:00:00Z',
                  'updatedAt': '2026-02-15T12:00:00Z',
                },
                'message': 'Client version behind',
              }
            ],
          }),
          data: Matchers.any,
        );

        final result = await executorWithResolver.executeBatch();

        // Verify batch result
        expect(result.total, 1);
        expect(result.conflicted, 1);
        expect(result.succeeded, 0);
        expect(result.failed, 0);
        expect(result.allSucceeded, isTrue);

        // Operation should be marked as synced
        final op = await syncQueue.getOperation('op-resolver');
        expect(op!.status, SyncStatus.synced);

        // Verify local entity was updated with server data
        final updated = await isar.cropListings
            .filter()
            .listingIdEqualTo('listing-resolver')
            .findFirst();

        expect(updated, isNotNull);
        expect(updated!.version, equals(5)); // Server version
        expect(updated.cropType, equals('wheat')); // Server data
        expect(updated.quantity, equals(50.0));
        expect(updated.pricePerKg, equals(30.0));
        expect(updated.description, equals('Server version'));
        expect(
          updated.audioNoteRef,
          equals('audio/farmer-001/listing-resolver.m4a'),
        );
        expect(updated.status, equals(ListingStatus.active));
        expect(updated.location.latitude, equals(9.5));
        expect(updated.location.longitude, equals(38.5));

        executorWithResolver.dispose();
      });

      test('marks operation as failed when ConflictResolver throws', () async {
        // Create executor with ConflictResolver
        final resolver = ConflictResolver(isar);
        final executorWithResolver = SyncExecutor(
          syncQueue: syncQueue,
          connectivityMonitor: connectivityMonitor,
          dio: dio,
          clientId: 'test-device-001',
          conflictResolver: resolver,
        );

        // Enqueue operation for non-existent entity
        await _enqueueTestOperation(
          operationId: 'op-bad-conflict',
          entityId: 'listing-nonexistent',
          action: 'update',
        );

        // Server returns conflict but local entity doesn't exist
        dioAdapter.onPost(
          '/sync',
          (server) => server.reply(200, {
            'syncBatchId': 'batch-001',
            'results': [
              {
                'operationId': 'op-bad-conflict',
                'status': 'conflict',
                'serverVersion': 2,
                'serverData': {
                  'listingId': 'listing-nonexistent',
                  'farmerId': 'farmer-001',
                  'cropType': 'wheat',
                  'quantity': 10.0,
                  'pricePerKg': 25.0,
                  'location': {'latitude': 9.0, 'longitude': 38.0},
                  'description': 'Test',
                  'audioNoteRef': null,
                  'status': 'active',
                  'createdAt': '2026-02-14T10:00:00Z',
                  'updatedAt': '2026-02-14T10:00:00Z',
                },
              }
            ],
          }),
          data: Matchers.any,
        );

        final result = await executorWithResolver.executeBatch();

        // Conflict resolution failed — operation should be marked as failed
        expect(result.total, 1);
        expect(result.failed, 1);
        expect(result.conflicted, 0);

        final op = await syncQueue.getOperation('op-bad-conflict');
        expect(op!.retryCount, 1);

        executorWithResolver.dispose();
      });

      test('handles duplicate response by marking as synced', () async {
        await _enqueueTestOperation(operationId: 'op-dup');

        dioAdapter.onPost(
          '/sync',
          (server) => server.reply(200, {
            'syncBatchId': 'batch-001',
            'results': [
              {
                'operationId': 'op-dup',
                'status': 'duplicate',
                'message': 'Already processed',
              }
            ],
          }),
          data: Matchers.any,
        );

        final result = await executor.executeBatch();

        expect(result.succeeded, 1);
        expect(result.failed, 0);

        final op = await syncQueue.getOperation('op-dup');
        expect(op!.status, SyncStatus.synced);
      });

      test('handles mixed results (partial success)', () async {
        await _enqueueTestOperation(operationId: 'op-ok');
        await _enqueueTestOperation(
          operationId: 'op-fail',
          entityId: 'listing-fail',
        );

        dioAdapter.onPost(
          '/sync',
          (server) => server.reply(200, {
            'syncBatchId': 'batch-001',
            'results': [
              {
                'operationId': 'op-ok',
                'status': 'success',
                'serverVersion': 1,
              },
              {
                'operationId': 'op-fail',
                'status': 'error',
                'message': 'Validation failed',
              },
            ],
          }),
          data: Matchers.any,
        );

        final result = await executor.executeBatch();

        expect(result.total, 2);
        expect(result.succeeded, 1);
        expect(result.failed, 1);
        expect(result.status, SyncBatchStatus.partial);
        expect(result.hasErrors, isTrue);

        final opOk = await syncQueue.getOperation('op-ok');
        final opFail = await syncQueue.getOperation('op-fail');
        expect(opOk!.status, SyncStatus.synced);
        // Failed with retryCount=1, goes back to pending for retry
        expect(opFail!.retryCount, 1);
        expect(opFail.status, SyncStatus.pending);
      });

      test('handles network error by reverting operations', () async {
        await _enqueueTestOperation(operationId: 'op-net');

        dioAdapter.onPost(
          '/sync',
          (server) => server.throws(
            0,
            DioException(
              requestOptions: RequestOptions(path: '/sync'),
              type: DioExceptionType.connectionError,
              message: 'No internet',
            ),
          ),
          data: Matchers.any,
        );

        final result = await executor.executeBatch();

        expect(result.total, 1);
        expect(result.failed, 1);
        expect(result.status, SyncBatchStatus.networkError);

        // Operation should be reverted to pending with incremented retry count
        final op = await syncQueue.getOperation('op-net');
        expect(op!.retryCount, 1);
        expect(op.status, SyncStatus.pending);
      });

      test('handles 401 auth error', () async {
        await _enqueueTestOperation(operationId: 'op-auth');

        dioAdapter.onPost(
          '/sync',
          (server) => server.throws(
            401,
            DioException(
              requestOptions: RequestOptions(path: '/sync'),
              type: DioExceptionType.badResponse,
              response: Response(
                requestOptions: RequestOptions(path: '/sync'),
                statusCode: 401,
                statusMessage: 'Unauthorized',
              ),
            ),
          ),
          data: Matchers.any,
        );

        final result = await executor.executeBatch();

        expect(result.status, SyncBatchStatus.authError);
        expect(result.failed, 1);
      });

      test('handles 429 rate limit', () async {
        await _enqueueTestOperation(operationId: 'op-rate');

        dioAdapter.onPost(
          '/sync',
          (server) => server.throws(
            429,
            DioException(
              requestOptions: RequestOptions(path: '/sync'),
              type: DioExceptionType.badResponse,
              response: Response(
                requestOptions: RequestOptions(path: '/sync'),
                statusCode: 429,
                statusMessage: 'Too Many Requests',
              ),
            ),
          ),
          data: Matchers.any,
        );

        final result = await executor.executeBatch();

        expect(result.status, SyncBatchStatus.rateLimited);
      });

      test('handles timeout error', () async {
        await _enqueueTestOperation(operationId: 'op-timeout');

        dioAdapter.onPost(
          '/sync',
          (server) => server.throws(
            0,
            DioException(
              requestOptions: RequestOptions(path: '/sync'),
              type: DioExceptionType.connectionTimeout,
              message: 'Connection timeout',
            ),
          ),
          data: Matchers.any,
        );

        final result = await executor.executeBatch();

        expect(result.status, SyncBatchStatus.networkError);
      });

      test('marks operations without server response as failed', () async {
        await _enqueueTestOperation(operationId: 'op-missing');

        // Server responds with empty results (didn't process the operation)
        dioAdapter.onPost(
          '/sync',
          (server) => server.reply(200, {
            'syncBatchId': 'batch-001',
            'results': [],
          }),
          data: Matchers.any,
        );

        final result = await executor.executeBatch();

        expect(result.failed, 1);

        final op = await syncQueue.getOperation('op-missing');
        expect(op!.retryCount, 1);
      });

      test('sends correct request format per sync protocol', () async {
        final enqueued = await _enqueueTestOperation(operationId: 'op-format');

        Map<String, dynamic>? capturedRequest;
        dioAdapter.onPost(
          '/sync',
          (server) {
            server.reply(200, _buildSuccessResponse(['op-format']));
          },
          data: Matchers.any,
        );

        // Listen for the actual request via an interceptor
        dio.interceptors.add(
          InterceptorsWrapper(
            onRequest: (options, handler) {
              capturedRequest = options.data as Map<String, dynamic>?;
              handler.next(options);
            },
          ),
        );

        await executor.executeBatch();

        expect(capturedRequest, isNotNull);
        expect(capturedRequest!['clientId'], 'test-device-001');
        expect(capturedRequest!['syncBatchId'], isNotEmpty);

        final operations = capturedRequest!['operations'] as List;
        expect(operations, hasLength(1));

        final op = operations[0] as Map<String, dynamic>;
        expect(op['operationId'], 'op-format');
        expect(op['entityType'], 'crop_listing');
        expect(op['entityId'], 'listing-001');
        expect(op['action'], 'create');
        expect(op['idempotencyKey'], isNotEmpty);
        expect(op['data'], isA<Map<String, dynamic>>());
        expect(op['timestamp'], isNotEmpty);
      });
    });

    group('syncNow', () {
      test('returns skipped when already syncing', () async {
        // Enqueue an operation so sync has work to do
        await _enqueueTestOperation(operationId: 'op-skip');

        // Mock health check to succeed
        dioAdapter.onGet(
          '/health',
          (server) => server.reply(200, {'status': 'ok'}),
        );

        // Mock sync to take a while (simulate slow network)
        dioAdapter.onPost(
          '/sync',
          (server) => server.reply(200, _buildSuccessResponse(['op-skip'])),
          data: Matchers.any,
        );

        // Start first sync (don't await)
        final firstSync = executor.syncNow();

        // Immediately try second sync — should be skipped
        final secondResult = await executor.syncNow();
        expect(secondResult.status, SyncBatchStatus.skipped);

        // Wait for first to complete
        await firstSync;
      });

      test('returns noInternet when health check fails', () async {
        fakeSource.currentResults = [ConnectivityResult.mobile];

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

        final result = await executor.syncNow();

        expect(result.status, SyncBatchStatus.noInternet);
      });

      test('processes all pending batches until queue empty', () async {
        // Enqueue 3 operations
        await _enqueueTestOperation(operationId: 'op-batch-1');
        await _enqueueTestOperation(
          operationId: 'op-batch-2',
          entityId: 'listing-002',
        );
        await _enqueueTestOperation(
          operationId: 'op-batch-3',
          entityId: 'listing-003',
        );

        dioAdapter.onGet(
          '/health',
          (server) => server.reply(200, {'status': 'ok'}),
        );

        dioAdapter.onPost(
          '/sync',
          (server) => server.reply(
            200,
            _buildSuccessResponse(['op-batch-1', 'op-batch-2', 'op-batch-3']),
          ),
          data: Matchers.any,
        );

        final result = await executor.syncNow();

        expect(result.succeeded, 3);
        expect(result.total, 3);
        expect(result.allSucceeded, isTrue);
      });
    });

    group('start', () {
      test('triggers sync on connectivity change', () async {
        // Enqueue an operation
        await _enqueueTestOperation(operationId: 'op-auto');

        dioAdapter.onGet(
          '/health',
          (server) => server.reply(200, {'status': 'ok'}),
        );

        dioAdapter.onPost(
          '/sync',
          (server) => server.reply(200, _buildSuccessResponse(['op-auto'])),
          data: Matchers.any,
        );

        // Start listening
        executor.start();

        // Simulate connectivity change
        fakeSource.controller.add([ConnectivityResult.wifi]);

        // Wait for debounce (2s) + processing time
        await Future<void>.delayed(const Duration(milliseconds: 3000));

        // Verify operation was synced
        final op = await syncQueue.getOperation('op-auto');
        expect(op!.status, SyncStatus.synced);
      });

      test('does not sync on disconnection', () async {
        await _enqueueTestOperation(operationId: 'op-offline');

        executor.start();

        // Simulate disconnection
        fakeSource.controller.add([ConnectivityResult.none]);

        await Future<void>.delayed(const Duration(milliseconds: 3000));

        // Operation should still be pending
        final op = await syncQueue.getOperation('op-offline');
        expect(op!.status, SyncStatus.pending);
      });
    });

    group('stop', () {
      test('stops listening to connectivity changes', () async {
        await _enqueueTestOperation(operationId: 'op-stopped');

        executor.start();
        executor.stop();

        // Simulate connectivity change after stop
        fakeSource.controller.add([ConnectivityResult.wifi]);

        await Future<void>.delayed(const Duration(milliseconds: 3000));

        // Operation should still be pending (sync not triggered)
        final op = await syncQueue.getOperation('op-stopped');
        expect(op!.status, SyncStatus.pending);
      });
    });

    group('onSyncResult', () {
      test('emits batch results to stream listeners', () async {
        await _enqueueTestOperation(operationId: 'op-stream');

        dioAdapter.onGet(
          '/health',
          (server) => server.reply(200, {'status': 'ok'}),
        );

        dioAdapter.onPost(
          '/sync',
          (server) => server.reply(200, _buildSuccessResponse(['op-stream'])),
          data: Matchers.any,
        );

        final results = <SyncBatchResult>[];
        final subscription = executor.onSyncResult.listen(results.add);

        await executor.syncNow();

        // Give stream time to deliver
        await Future<void>.delayed(const Duration(milliseconds: 100));

        expect(results, isNotEmpty);
        expect(results.last.succeeded, greaterThanOrEqualTo(1));

        await subscription.cancel();
      });
    });

    group('generateBatchId', () {
      test('generates valid UUID v4 format', () {
        final id = SyncExecutor.generateBatchId();

        // UUID v4 format: 8-4-4-4-12 hex characters
        expect(
          id,
          matches(
            RegExp(r'^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$'),
          ),
        );
      });

      test('generates unique IDs', () {
        final ids = List.generate(100, (_) => SyncExecutor.generateBatchId());
        expect(ids.toSet().length, 100);
      });
    });
  });

  group('SyncBatchResult', () {
    test('empty result has correct defaults', () {
      const result = SyncBatchResult.empty();
      expect(result.total, 0);
      expect(result.status, SyncBatchStatus.empty);
      expect(result.hasErrors, isFalse);
    });

    test('skipped result has correct status', () {
      const result = SyncBatchResult.skipped();
      expect(result.status, SyncBatchStatus.skipped);
      expect(result.hasErrors, isFalse);
    });

    test('hasErrors returns true for failed operations', () {
      const result = SyncBatchResult(total: 3, succeeded: 2, failed: 1);
      expect(result.hasErrors, isTrue);
    });

    test('hasErrors returns true for network errors', () {
      const result = SyncBatchResult(status: SyncBatchStatus.networkError);
      expect(result.hasErrors, isTrue);
    });

    test('allSucceeded returns true when all operations processed', () {
      const result = SyncBatchResult(
        total: 3,
        succeeded: 2,
        conflicted: 1,
      );
      expect(result.allSucceeded, isTrue);
    });

    test('allSucceeded returns false when operations failed', () {
      const result = SyncBatchResult(
        total: 3,
        succeeded: 2,
        failed: 1,
      );
      expect(result.allSucceeded, isFalse);
    });

    test('merge combines two results', () {
      const a = SyncBatchResult(
        total: 2,
        succeeded: 1,
        conflicted: 1,
      );
      const b = SyncBatchResult(
        total: 3,
        succeeded: 2,
        failed: 1,
      );

      final merged = a.merge(b);
      expect(merged.total, 5);
      expect(merged.succeeded, 3);
      expect(merged.conflicted, 1);
      expect(merged.failed, 1);
      expect(merged.status, SyncBatchStatus.partial);
    });

    test('merge with empty returns the other result', () {
      const result = SyncBatchResult(total: 2, succeeded: 2);
      const empty = SyncBatchResult.empty();

      expect(empty.merge(result).total, 2);
      expect(result.merge(empty).total, 2);
    });
  });
}
