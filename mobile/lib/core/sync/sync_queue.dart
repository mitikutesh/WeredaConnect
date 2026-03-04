import 'dart:async';
import 'dart:convert';

import 'package:flutter/foundation.dart';
import 'package:isar/isar.dart';

import 'package:wereda_connect/core/database/schemas/sync_operation.dart';
import 'package:wereda_connect/core/sync/idempotency_key_generator.dart';

/// Manages the offline sync queue using Isar as persistent storage.
///
/// All user actions that modify server state (create, update, delete)
/// are enqueued here and processed when connectivity is available.
///
/// Offline-first guarantees:
/// - Operations survive app restarts and device reboots (Isar persistence)
/// - Idempotency keys prevent duplicate processing on retry
/// - Failed operations are retried with exponential backoff
/// - Queue is bounded to [maxQueueSize] to prevent unbounded growth
///
/// See: `specs/contracts/sync-protocol.md`
class SyncQueue {
  final Isar _isar;

  /// Notifies listeners whenever a new operation is enqueued.
  /// SyncExecutor subscribes to this to trigger immediate sync.
  final StreamController<void> _enqueueController =
      StreamController<void>.broadcast();

  /// Stream that fires whenever a new operation is enqueued.
  Stream<void> get onEnqueued => _enqueueController.stream;

  /// Maximum operations in the queue before rejecting new ones.
  /// Prevents unbounded growth when offline for extended periods.
  static const int maxQueueSize = 1000;

  /// Maximum retry attempts before marking as permanently failed.
  static const int maxRetries = 3;

  /// Age threshold for purging synced operations (30 days).
  static const Duration purgeThreshold = Duration(days: 30);

  SyncQueue(this._isar);

  /// Enqueue a new sync operation.
  ///
  /// Creates a [SyncOperation] with status [SyncStatus.pending] and
  /// persists it to Isar. Generates a deterministic idempotency key
  /// to prevent duplicate operations.
  ///
  /// Parameters:
  /// - [userId]: The authenticated user performing the action
  /// - [entityType]: Type of entity ('crop_listing', 'trip', etc.)
  /// - [entityId]: The entity's unique identifier
  /// - [action]: The operation ('create', 'update', 'delete')
  /// - [data]: Serialized JSON payload of the entity
  /// - [operationId]: Pre-generated UUID v4 for the operation
  ///
  /// Returns the created [SyncOperation].
  ///
  /// Throws [SyncQueueFullException] if queue exceeds [maxQueueSize].
  /// Throws [DuplicateOperationException] if idempotency key already exists.
  Future<SyncOperation> enqueue({
    required String userId,
    required String entityType,
    required String entityId,
    required String action,
    required String data,
    required String operationId,
  }) async {
    // Check queue capacity
    final currentCount = await getPendingCount();
    if (currentCount >= maxQueueSize) {
      throw const SyncQueueFullException(
        'Sync queue is full ($maxQueueSize operations). '
        'Please wait for connectivity to sync pending operations.',
      );
    }

    final now = DateTime.now().toUtc();

    // Generate deterministic idempotency key
    final idempotencyKey = IdempotencyKeyGenerator.generate(
      userId: userId,
      entityType: entityType,
      entityId: entityId,
      action: action,
      timestamp: now,
    );

    // Check for duplicate idempotency key
    final existing = await _isar.syncOperations
        .filter()
        .idempotencyKeyEqualTo(idempotencyKey)
        .findFirst();

    if (existing != null) {
      throw DuplicateOperationException(
        'Operation with idempotency key already exists: $idempotencyKey',
      );
    }

    final operation = SyncOperation()
      ..operationId = operationId
      ..idempotencyKey = idempotencyKey
      ..entityType = entityType
      ..entityId = entityId
      ..action = action
      ..data = data
      ..status = SyncStatus.pending
      ..retryCount = 0
      ..createdAt = now;

    await _isar.writeTxn(() async {
      await _isar.syncOperations.put(operation);
    });

    if (!_enqueueController.isClosed) {
      _enqueueController.add(null);
    }

    return operation;
  }

  /// Get all pending operations ordered by creation time (FIFO).
  ///
  /// Returns operations with status [SyncStatus.pending], oldest first.
  /// The [limit] parameter controls batch size (default 50 for data efficiency).
  Future<List<SyncOperation>> getPendingOperations({int limit = 50}) async {
    return _isar.syncOperations
        .filter()
        .statusEqualTo(SyncStatus.pending)
        .sortByCreatedAt()
        .limit(limit)
        .findAll();
  }

  /// Get pending operations belonging to a specific user.
  ///
  /// Filters by the userId prefix in the idempotency key (format: `{userId}:...`).
  /// If [userId] is null or empty, falls back to [getPendingOperations] (all users).
  ///
  /// This prevents User B's sync session from processing User A's offline
  /// operations on shared devices — a critical security requirement.
  Future<List<SyncOperation>> getPendingOperationsForUser({
    required String? userId,
    int limit = 50,
  }) async {
    if (userId == null || userId.isEmpty) {
      return getPendingOperations(limit: limit);
    }
    return _isar.syncOperations
        .filter()
        .statusEqualTo(SyncStatus.pending)
        .idempotencyKeyStartsWith('$userId:')
        .sortByCreatedAt()
        .limit(limit)
        .findAll();
  }

  /// Mark an operation as currently being synced.
  ///
  /// Prevents the same operation from being picked up by concurrent
  /// sync attempts. Should be called before sending to the server.
  Future<void> markAsSyncing(String operationId) async {
    await _updateStatus(operationId, SyncStatus.syncing);
  }

  /// Mark an operation as successfully synced.
  ///
  /// The operation remains in the database for audit purposes
  /// and is purged after [purgeThreshold] by [purgeSynced].
  Future<void> markAsSynced(String operationId) async {
    await _updateStatus(operationId, SyncStatus.synced);
  }

  /// Revert an operation from syncing back to pending without incrementing retry count.
  ///
  /// Use for transient failures unrelated to the operation itself
  /// (e.g., auth errors, rate limiting) so they don't burn retry attempts.
  Future<void> revertToPending(String operationId) async {
    await _updateStatus(operationId, SyncStatus.pending);
  }

  /// Mark an operation as failed and increment retry count.
  ///
  /// If [retryCount] reaches [maxRetries], the operation is marked
  /// as permanently [SyncStatus.failed]. Otherwise it returns to
  /// [SyncStatus.pending] for retry.
  Future<void> markAsFailed(String operationId) async {
    final operation = await _findByOperationId(operationId);
    if (operation == null) return;

    final newRetryCount = operation.retryCount + 1;

    await _isar.writeTxn(() async {
      operation.retryCount = newRetryCount;
      operation.status =
          newRetryCount >= maxRetries ? SyncStatus.failed : SyncStatus.pending;
      await _isar.syncOperations.put(operation);
    });
  }

  /// Mark an operation as permanently rejected by the server.
  ///
  /// Used for permanent server rejections (fraud detection, validation errors)
  /// that will never succeed on retry. Sets [retryCount] above [maxRetries]
  /// so [retryFailed] skips these operations.
  Future<void> markAsRejected(String operationId) async {
    final operation = await _findByOperationId(operationId);
    if (operation == null) return;

    await _isar.writeTxn(() async {
      operation.retryCount = maxRetries + 1;
      operation.status = SyncStatus.failed;
      await _isar.syncOperations.put(operation);
    });
  }

  /// Reset permanently failed operations back to pending for retry.
  ///
  /// Only resets operations that failed due to transient errors (retry
  /// exhaustion). Operations rejected by the server (fraud, validation)
  /// have [retryCount] > [maxRetries] and are skipped.
  ///
  /// Returns the number of operations reset.
  Future<int> retryFailed() async {
    final failedOps = await _isar.syncOperations
        .filter()
        .statusEqualTo(SyncStatus.failed)
        .retryCountLessThan(maxRetries, include: true)
        .findAll();

    if (failedOps.isEmpty) return 0;

    await _isar.writeTxn(() async {
      for (final op in failedOps) {
        op.status = SyncStatus.pending;
        op.retryCount = 0;
        await _isar.syncOperations.put(op);
      }
    });

    return failedOps.length;
  }

  /// Recover operations stuck in [SyncStatus.syncing] from a previous crash.
  ///
  /// If the app is killed during sync, operations remain in `syncing` status
  /// and are never picked up by [getPendingOperations] (which queries `pending`)
  /// or [retryFailed] (which queries `failed`). This method resets them to
  /// `pending` without incrementing [retryCount] so they are retried normally.
  ///
  /// Returns the number of operations recovered.
  Future<int> recoverStaleSyncing() async {
    final staleOps = await _isar.syncOperations
        .filter()
        .statusEqualTo(SyncStatus.syncing)
        .findAll();

    if (staleOps.isEmpty) return 0;

    await _isar.writeTxn(() async {
      for (final op in staleOps) {
        op.status = SyncStatus.pending;
        op.retryCount = 0;
        await _isar.syncOperations.put(op);
      }
    });

    return staleOps.length;
  }

  /// Get the count of pending operations (pending + syncing).
  ///
  /// Includes both [SyncStatus.pending] and [SyncStatus.syncing]
  /// operations since syncing operations are still in-flight.
  Future<int> getPendingCount() async {
    final pendingCount = await _isar.syncOperations
        .filter()
        .statusEqualTo(SyncStatus.pending)
        .count();
    final syncingCount = await _isar.syncOperations
        .filter()
        .statusEqualTo(SyncStatus.syncing)
        .count();
    return pendingCount + syncingCount;
  }

  /// Get the count of permanently failed operations.
  Future<int> getFailedCount() async {
    return _isar.syncOperations
        .filter()
        .statusEqualTo(SyncStatus.failed)
        .count();
  }

  /// Purge synced operations older than [purgeThreshold].
  ///
  /// Removes successfully synced operations to prevent database bloat.
  /// Returns the number of operations purged.
  Future<int> purgeSynced() async {
    final cutoff = DateTime.now().toUtc().subtract(purgeThreshold);

    final staleSynced = await _isar.syncOperations
        .filter()
        .statusEqualTo(SyncStatus.synced)
        .createdAtLessThan(cutoff)
        .findAll();

    if (staleSynced.isEmpty) return 0;

    final ids = staleSynced.map((op) => op.id).toList();
    await _isar.writeTxn(() async {
      await _isar.syncOperations.deleteAll(ids);
    });

    return ids.length;
  }

  /// Get an operation by its operationId.
  Future<SyncOperation?> getOperation(String operationId) async {
    return _findByOperationId(operationId);
  }

  /// One-time migration: fix trip sync operations that contain fields
  /// rejected by the server's whitelist (tripId, createdAt, updatedAt).
  ///
  /// These operations were created before the fix in trip_listing_repository
  /// and will never succeed because the server rejects unknown fields.
  /// This strips the bad fields from the JSON payload and resets the
  /// operations to pending so they get retried with valid data.
  ///
  /// Returns the number of operations fixed.
  Future<int> migrateBadTripSyncData() async {
    // Find all trip operations that are failed or pending
    final ops = await _isar.syncOperations
        .filter()
        .entityTypeEqualTo('trip')
        .group(
          (q) => q
              .statusEqualTo(SyncStatus.failed)
              .or()
              .statusEqualTo(SyncStatus.pending),
        )
        .findAll();

    if (ops.isEmpty) return 0;

    var fixed = 0;

    await _isar.writeTxn(() async {
      for (final op in ops) {
        final data = jsonDecode(op.data) as Map<String, dynamic>;

        // Check if data contains the problematic fields
        if (!data.containsKey('tripId') && !data.containsKey('createdAt')) {
          continue;
        }

        // Strip server-managed fields that cause TRIP_VALIDATION errors
        data.remove('tripId');
        data.remove('createdAt');
        data.remove('updatedAt');

        op.data = jsonEncode(data);
        op.status = SyncStatus.pending;
        op.retryCount = 0;
        await _isar.syncOperations.put(op);
        fixed++;
      }
    });

    if (fixed > 0) {
      debugPrint('[SYNC] migrated $fixed trip operations with bad fields');
    }

    return fixed;
  }

  /// Release resources held by this queue.
  void dispose() {
    _enqueueController.close();
  }

  // -- Private helpers --

  Future<SyncOperation?> _findByOperationId(String operationId) async {
    return _isar.syncOperations
        .filter()
        .operationIdEqualTo(operationId)
        .findFirst();
  }

  Future<void> _updateStatus(String operationId, SyncStatus status) async {
    final operation = await _findByOperationId(operationId);
    if (operation == null) return;

    await _isar.writeTxn(() async {
      operation.status = status;
      await _isar.syncOperations.put(operation);
    });
  }
}

/// Thrown when the sync queue exceeds its maximum capacity.
class SyncQueueFullException implements Exception {
  final String message;
  const SyncQueueFullException(this.message);

  @override
  String toString() => 'SyncQueueFullException: $message';
}

/// Thrown when an operation with the same idempotency key already exists.
class DuplicateOperationException implements Exception {
  final String message;
  const DuplicateOperationException(this.message);

  @override
  String toString() => 'DuplicateOperationException: $message';
}
