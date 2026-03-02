import 'dart:async';

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

  /// Reset permanently failed operations back to pending for retry.
  ///
  /// Resets [retryCount] to 0 and status to [SyncStatus.pending].
  /// Use when connectivity improves or after fixing known issues.
  ///
  /// Returns the number of operations reset.
  Future<int> retryFailed() async {
    final failedOps = await _isar.syncOperations
        .filter()
        .statusEqualTo(SyncStatus.failed)
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
