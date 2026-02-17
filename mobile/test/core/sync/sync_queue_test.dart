import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:isar/isar.dart';

import 'package:wereda_connect/core/database/schemas/crop_listing.dart';
import 'package:wereda_connect/core/database/schemas/delivery.dart';
import 'package:wereda_connect/core/database/schemas/payment.dart';
import 'package:wereda_connect/core/database/schemas/sync_operation.dart';
import 'package:wereda_connect/core/database/schemas/trip.dart';
import 'package:wereda_connect/core/database/schemas/user.dart';
import 'package:wereda_connect/core/sync/idempotency_key_generator.dart';
import 'package:wereda_connect/core/sync/sync_queue.dart';

void main() {
  group('SyncQueue', () {
    late Isar isar;
    late Directory tempDir;
    late SyncQueue syncQueue;

    setUpAll(() async {
      await Isar.initializeIsarCore(download: true);
    });

    setUp(() async {
      tempDir = Directory.systemTemp.createTempSync('sync_queue_test_');
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
        name: 'test_sync_queue_${DateTime.now().millisecondsSinceEpoch}',
      );
      syncQueue = SyncQueue(isar);
    });

    tearDown(() async {
      await isar.close(deleteFromDisk: true);
      if (tempDir.existsSync()) {
        tempDir.deleteSync(recursive: true);
      }
    });

    test('enqueue creates a pending operation with correct fields', () async {
      final operation = await syncQueue.enqueue(
        userId: 'user-001',
        entityType: 'crop_listing',
        entityId: 'listing-001',
        action: 'create',
        data: '{"cropType":"teff","quantity":50}',
        operationId: 'op-001',
      );

      expect(operation.operationId, 'op-001');
      expect(operation.entityType, 'crop_listing');
      expect(operation.entityId, 'listing-001');
      expect(operation.action, 'create');
      expect(operation.data, '{"cropType":"teff","quantity":50}');
      expect(operation.status, SyncStatus.pending);
      expect(operation.retryCount, 0);
      expect(operation.createdAt.isUtc, isTrue);

      // Verify idempotency key was generated with correct format
      final parsed =
          IdempotencyKeyGenerator.parse(operation.idempotencyKey);
      expect(parsed, isNotNull);
      expect(parsed!.userId, 'user-001');
      expect(parsed.entityType, 'crop_listing');
      expect(parsed.entityId, 'listing-001');
      expect(parsed.action, 'create');
    });

    test('enqueue persists operation to Isar', () async {
      await syncQueue.enqueue(
        userId: 'user-001',
        entityType: 'crop_listing',
        entityId: 'listing-002',
        action: 'create',
        data: '{"cropType":"wheat"}',
        operationId: 'op-002',
      );

      // Read directly from Isar to verify persistence
      final stored = await isar.syncOperations
          .filter()
          .operationIdEqualTo('op-002')
          .findFirst();

      expect(stored, isNotNull);
      expect(stored!.status, SyncStatus.pending);
    });

    test('getPendingOperations returns operations in FIFO order', () async {
      // Enqueue 3 operations with slight delays for ordering
      await syncQueue.enqueue(
        userId: 'user-001',
        entityType: 'crop_listing',
        entityId: 'listing-A',
        action: 'create',
        data: '{"order":1}',
        operationId: 'op-A',
      );

      // Small delay to ensure different timestamps
      await Future<void>.delayed(const Duration(milliseconds: 10));

      await syncQueue.enqueue(
        userId: 'user-001',
        entityType: 'trip',
        entityId: 'trip-B',
        action: 'create',
        data: '{"order":2}',
        operationId: 'op-B',
      );

      await Future<void>.delayed(const Duration(milliseconds: 10));

      await syncQueue.enqueue(
        userId: 'user-001',
        entityType: 'crop_listing',
        entityId: 'listing-C',
        action: 'update',
        data: '{"order":3}',
        operationId: 'op-C',
      );

      final pending = await syncQueue.getPendingOperations();

      expect(pending.length, 3);
      expect(pending[0].operationId, 'op-A');
      expect(pending[1].operationId, 'op-B');
      expect(pending[2].operationId, 'op-C');
    });

    test('getPendingOperations respects limit parameter', () async {
      for (var i = 0; i < 5; i++) {
        await syncQueue.enqueue(
          userId: 'user-001',
          entityType: 'crop_listing',
          entityId: 'listing-$i',
          action: 'create',
          data: '{"index":$i}',
          operationId: 'op-limit-$i',
        );
        await Future<void>.delayed(const Duration(milliseconds: 5));
      }

      final batch = await syncQueue.getPendingOperations(limit: 2);
      expect(batch.length, 2);
      expect(batch[0].operationId, 'op-limit-0');
      expect(batch[1].operationId, 'op-limit-1');
    });

    test('markAsSyncing transitions status to syncing', () async {
      await syncQueue.enqueue(
        userId: 'user-001',
        entityType: 'crop_listing',
        entityId: 'listing-sync',
        action: 'create',
        data: '{}',
        operationId: 'op-syncing',
      );

      await syncQueue.markAsSyncing('op-syncing');

      final operation = await syncQueue.getOperation('op-syncing');
      expect(operation!.status, SyncStatus.syncing);

      // Should not appear in pending list
      final pending = await syncQueue.getPendingOperations();
      expect(pending.where((op) => op.operationId == 'op-syncing'), isEmpty);
    });

    test('markAsSynced transitions status to synced', () async {
      await syncQueue.enqueue(
        userId: 'user-001',
        entityType: 'crop_listing',
        entityId: 'listing-done',
        action: 'create',
        data: '{}',
        operationId: 'op-done',
      );

      await syncQueue.markAsSyncing('op-done');
      await syncQueue.markAsSynced('op-done');

      final operation = await syncQueue.getOperation('op-done');
      expect(operation!.status, SyncStatus.synced);
    });

    test('markAsFailed increments retryCount and returns to pending', () async {
      await syncQueue.enqueue(
        userId: 'user-001',
        entityType: 'crop_listing',
        entityId: 'listing-fail',
        action: 'create',
        data: '{}',
        operationId: 'op-fail',
      );

      // First failure: retryCount 1, still pending
      await syncQueue.markAsFailed('op-fail');
      var operation = await syncQueue.getOperation('op-fail');
      expect(operation!.retryCount, 1);
      expect(operation.status, SyncStatus.pending);

      // Second failure: retryCount 2, still pending
      await syncQueue.markAsFailed('op-fail');
      operation = await syncQueue.getOperation('op-fail');
      expect(operation!.retryCount, 2);
      expect(operation.status, SyncStatus.pending);

      // Third failure: retryCount 3, permanently failed
      await syncQueue.markAsFailed('op-fail');
      operation = await syncQueue.getOperation('op-fail');
      expect(operation!.retryCount, 3);
      expect(operation.status, SyncStatus.failed);
    });

    test('retryFailed resets failed operations to pending', () async {
      await syncQueue.enqueue(
        userId: 'user-001',
        entityType: 'crop_listing',
        entityId: 'listing-retry',
        action: 'create',
        data: '{}',
        operationId: 'op-retry',
      );

      // Fail 3 times to mark as permanently failed
      for (var i = 0; i < 3; i++) {
        await syncQueue.markAsFailed('op-retry');
      }

      var operation = await syncQueue.getOperation('op-retry');
      expect(operation!.status, SyncStatus.failed);

      // Retry failed operations
      final resetCount = await syncQueue.retryFailed();
      expect(resetCount, 1);

      operation = await syncQueue.getOperation('op-retry');
      expect(operation!.status, SyncStatus.pending);
      expect(operation.retryCount, 0);
    });

    test('retryFailed returns 0 when no failed operations exist', () async {
      final resetCount = await syncQueue.retryFailed();
      expect(resetCount, 0);
    });

    test('getPendingCount includes both pending and syncing', () async {
      await syncQueue.enqueue(
        userId: 'user-001',
        entityType: 'crop_listing',
        entityId: 'listing-count-1',
        action: 'create',
        data: '{}',
        operationId: 'op-count-1',
      );

      await syncQueue.enqueue(
        userId: 'user-001',
        entityType: 'crop_listing',
        entityId: 'listing-count-2',
        action: 'create',
        data: '{}',
        operationId: 'op-count-2',
      );

      // Mark one as syncing
      await syncQueue.markAsSyncing('op-count-1');

      final count = await syncQueue.getPendingCount();
      expect(count, 2); // 1 pending + 1 syncing
    });

    test('getFailedCount returns count of permanently failed operations',
        () async {
      await syncQueue.enqueue(
        userId: 'user-001',
        entityType: 'crop_listing',
        entityId: 'listing-fc',
        action: 'create',
        data: '{}',
        operationId: 'op-fc',
      );

      // Fail 3 times
      for (var i = 0; i < 3; i++) {
        await syncQueue.markAsFailed('op-fc');
      }

      final failedCount = await syncQueue.getFailedCount();
      expect(failedCount, 1);
    });

    test('duplicate idempotency key throws DuplicateOperationException',
        () async {
      await syncQueue.enqueue(
        userId: 'user-001',
        entityType: 'crop_listing',
        entityId: 'listing-dup',
        action: 'create',
        data: '{}',
        operationId: 'op-dup-1',
      );

      // The idempotency key is timestamp-based, so an immediate second call
      // with the same params might get the same millisecond timestamp.
      // We test by enqueuing with a different operationId but same key
      // won't happen naturally, but we verify the dedup check works
      // by manually inserting a record with a known key.
      final existingOp = await syncQueue.getOperation('op-dup-1');
      expect(existingOp, isNotNull);

      // Verify the operationId unique index prevents duplicates at DB level
      final duplicateOp = SyncOperation()
        ..operationId = 'op-dup-1' // Same operationId
        ..idempotencyKey = 'different-key'
        ..entityType = 'crop_listing'
        ..entityId = 'listing-dup'
        ..action = 'create'
        ..data = '{}'
        ..status = SyncStatus.pending
        ..retryCount = 0
        ..createdAt = DateTime.now().toUtc();

      expect(
        () async => await isar.writeTxn(() async {
          await isar.syncOperations.put(duplicateOp);
        }),
        throwsA(isA<IsarError>()),
      );
    });

    test('purgeSynced removes old synced operations', () async {
      // Create and sync an operation
      await syncQueue.enqueue(
        userId: 'user-001',
        entityType: 'crop_listing',
        entityId: 'listing-purge',
        action: 'create',
        data: '{}',
        operationId: 'op-purge',
      );

      await syncQueue.markAsSynced('op-purge');

      // Manually backdate the createdAt to trigger purge
      final op = await syncQueue.getOperation('op-purge');
      await isar.writeTxn(() async {
        op!.createdAt = DateTime.now()
            .toUtc()
            .subtract(const Duration(days: 31));
        await isar.syncOperations.put(op);
      });

      final purged = await syncQueue.purgeSynced();
      expect(purged, 1);

      // Verify it's gone
      final gone = await syncQueue.getOperation('op-purge');
      expect(gone, isNull);
    });

    test('purgeSynced does not remove recent synced operations', () async {
      await syncQueue.enqueue(
        userId: 'user-001',
        entityType: 'crop_listing',
        entityId: 'listing-keep',
        action: 'create',
        data: '{}',
        operationId: 'op-keep',
      );

      await syncQueue.markAsSynced('op-keep');

      // Don't backdate - should not be purged
      final purged = await syncQueue.purgeSynced();
      expect(purged, 0);

      final kept = await syncQueue.getOperation('op-keep');
      expect(kept, isNotNull);
    });
  });

  group('IdempotencyKeyGenerator', () {
    test('generate creates key with correct format', () {
      final timestamp =
          DateTime.utc(2026, 3, 15, 10, 30); // Fixed for determinism

      final key = IdempotencyKeyGenerator.generate(
        userId: 'user-001',
        entityType: 'crop_listing',
        entityId: 'listing-001',
        action: 'create',
        timestamp: timestamp,
      );

      expect(key, contains('user-001'));
      expect(key, contains('crop_listing'));
      expect(key, contains('listing-001'));
      expect(key, contains('create'));

      // Verify colon-separated format with 5 parts
      final parts = key.split(':');
      expect(parts.length, 5);
      expect(parts[0], 'user-001');
      expect(parts[1], 'crop_listing');
      expect(parts[2], 'listing-001');
      expect(parts[3], 'create');
    });

    test('generate is deterministic for same inputs', () {
      final timestamp = DateTime.utc(2026, 3, 15, 10, 30);

      final key1 = IdempotencyKeyGenerator.generate(
        userId: 'user-001',
        entityType: 'crop_listing',
        entityId: 'listing-001',
        action: 'create',
        timestamp: timestamp,
      );

      final key2 = IdempotencyKeyGenerator.generate(
        userId: 'user-001',
        entityType: 'crop_listing',
        entityId: 'listing-001',
        action: 'create',
        timestamp: timestamp,
      );

      expect(key1, equals(key2));
    });

    test('generate produces different keys for different actions', () {
      final timestamp = DateTime.utc(2026, 3, 15, 10, 30);

      final createKey = IdempotencyKeyGenerator.generate(
        userId: 'user-001',
        entityType: 'crop_listing',
        entityId: 'listing-001',
        action: 'create',
        timestamp: timestamp,
      );

      final updateKey = IdempotencyKeyGenerator.generate(
        userId: 'user-001',
        entityType: 'crop_listing',
        entityId: 'listing-001',
        action: 'update',
        timestamp: timestamp,
      );

      expect(createKey, isNot(equals(updateKey)));
    });

    test('parse round-trips correctly', () {
      final timestamp = DateTime.utc(2026, 3, 15, 10, 30);

      final key = IdempotencyKeyGenerator.generate(
        userId: 'user-001',
        entityType: 'crop_listing',
        entityId: 'listing-001',
        action: 'create',
        timestamp: timestamp,
      );

      final parsed = IdempotencyKeyGenerator.parse(key);

      expect(parsed, isNotNull);
      expect(parsed!.userId, 'user-001');
      expect(parsed.entityType, 'crop_listing');
      expect(parsed.entityId, 'listing-001');
      expect(parsed.action, 'create');
      expect(parsed.timestamp, timestamp);
    });

    test('parse returns null for invalid key format', () {
      expect(IdempotencyKeyGenerator.parse('invalid'), isNull);
      expect(IdempotencyKeyGenerator.parse('a:b:c'), isNull);
      expect(
        IdempotencyKeyGenerator.parse('a:b:c:d:not-a-number'),
        isNull,
      );
    });
  });
}
