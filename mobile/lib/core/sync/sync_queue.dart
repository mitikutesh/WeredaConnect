import 'dart:async';
import 'dart:convert';

import 'package:flutter/foundation.dart';
import 'package:isar/isar.dart';

import 'package:wereda_connect/core/models/sync_operation.dart';
import 'package:wereda_connect/core/sync/idempotency_key_generator.dart';
import 'package:wereda_connect/core/sync/location_ping_rules.dart';

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
  /// - [keyTimestamp]: the time in the idempotency key, when it is not the
  ///   enqueue time (a check-in uses its capture time, live-trip-api.md
  ///   section 1)
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
    DateTime? keyTimestamp,
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
      timestamp: keyTimestamp ?? now,
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
  /// [onlyEntityType] keeps one entity type, [exceptEntityType] leaves one out
  /// (the sync executor sends check-ins after every other operation, LT-08).
  Future<List<SyncOperation>> getPendingOperations({
    int limit = 50,
    String? onlyEntityType,
    String? exceptEntityType,
  }) async {
    return _isar.syncOperations
        .filter()
        .statusEqualTo(SyncStatus.pending)
        .optional(
          onlyEntityType != null,
          (q) => q.entityTypeEqualTo(onlyEntityType!),
        )
        .optional(
          exceptEntityType != null,
          (q) => q.not().entityTypeEqualTo(exceptEntityType!),
        )
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
    String? onlyEntityType,
    String? exceptEntityType,
  }) async {
    if (userId == null || userId.isEmpty) {
      return getPendingOperations(
        limit: limit,
        onlyEntityType: onlyEntityType,
        exceptEntityType: exceptEntityType,
      );
    }
    return _isar.syncOperations
        .filter()
        .statusEqualTo(SyncStatus.pending)
        .idempotencyKeyStartsWith('$userId:')
        .optional(
          onlyEntityType != null,
          (q) => q.entityTypeEqualTo(onlyEntityType!),
        )
        .optional(
          exceptEntityType != null,
          (q) => q.not().entityTypeEqualTo(exceptEntityType!),
        )
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
  /// Used for permanent server rejections (fraud detection, validation errors,
  /// every `ORDER_*` code but `ORDER_KYC_REQUIRED`) that will never succeed on
  /// retry. Sets [retryCount] above [maxRetries] so [retryFailed] skips these
  /// operations.
  Future<void> markAsRejected(String operationId) async {
    final operation = await _findByOperationId(operationId);
    if (operation == null) return;

    await _isar.writeTxn(() async {
      operation.retryCount = maxRetries + 1;
      operation.status = SyncStatus.failed;
      await _isar.syncOperations.put(operation);
    });
  }

  /// Whether [op] was rejected by the server for good ([markAsRejected]), as
  /// opposed to a transient failure that ran out of retries.
  static bool isRejected(SyncOperation op) =>
      op.status == SyncStatus.failed && op.retryCount > maxRetries;

  /// Flag every rejected operation of one entity as reverted: its local row
  /// was put back to the server's view (or removed), so nothing is left for
  /// the user to act on and [getFailedCount] leaves them out. Called by the
  /// sync executor once the revert handler returned (sync-protocol.md
  /// 1.17.0). Operations still on their way, or that ran out of retries, are
  /// not touched: their own outcome settles the row.
  ///
  /// Returns the number of operations flagged.
  Future<int> markRevertedFor(String entityType, String entityId) async {
    final ops = await _isar.syncOperations
        .filter()
        .entityTypeEqualTo(entityType)
        .entityIdEqualTo(entityId)
        .statusEqualTo(SyncStatus.failed)
        .findAll();
    final toFlag = ops.where((op) => isRejected(op) && !op.reverted).toList();
    if (toFlag.isEmpty) return 0;

    await _isar.writeTxn(() async {
      for (final op in toFlag) {
        op.reverted = true;
        await _isar.syncOperations.put(op);
      }
    });
    return toFlag.length;
  }

  /// Reject every not-yet-synced operation for one entity.
  ///
  /// Used after the server refused that entity's `create`: it never stored the
  /// entity, so a queued update or delete for it can only get a transient
  /// "not found" and would be retried forever.
  ///
  /// Returns the number of operations rejected.
  Future<int> rejectOpenOperationsFor(
    String entityType,
    String entityId,
  ) async {
    final open = await _isar.syncOperations
        .filter()
        .entityTypeEqualTo(entityType)
        .entityIdEqualTo(entityId)
        .not()
        .statusEqualTo(SyncStatus.synced)
        .findAll();
    final toReject = open.where((op) => !isRejected(op)).toList();
    if (toReject.isEmpty) return 0;

    await _isar.writeTxn(() async {
      for (final op in toReject) {
        op
          ..status = SyncStatus.failed
          ..retryCount = maxRetries + 1;
        await _isar.syncOperations.put(op);
      }
    });
    return toReject.length;
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

  /// Get the count of failed operations: those that ran out of retries and
  /// those the server rejected for good, except rejected order operations,
  /// match events, check-ins and operations flagged reverted
  /// ([markRevertedFor]). A rejected order change was undone (the order went
  /// back to the server's view) and explained, a reverted crop or trip change
  /// likewise, a match event is background analytics the user never made,
  /// and a refused check-in only leaves a gap in the trail (sync-protocol.md
  /// Location pings), so nothing is left for the user to act on. Any other
  /// rejected change still sits in local data that never reached the server,
  /// so it keeps counting.
  Future<int> getFailedCount() async {
    return _isar.syncOperations
        .filter()
        .statusEqualTo(SyncStatus.failed)
        // Not `revertedEqualTo(false)`: a row written before the field
        // existed stores no byte for it, which a filter reads as null.
        .not()
        .revertedEqualTo(true)
        .group(
          (q) => q.retryCountLessThan(maxRetries, include: true).or().group(
                (r) => r
                    .not()
                    .entityTypeEqualTo('order')
                    .and()
                    .not()
                    .entityTypeEqualTo('match_event')
                    .and()
                    .not()
                    .entityTypeEqualTo(locationPingEntityType),
              ),
        )
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

  /// The check-ins that still wait to reach the server (pending or on their
  /// way), of one trip when [tripId] is given (LT-08: "N check-ins wait on
  /// the phone").
  Future<int> pendingLocationPingCount({String? tripId}) async {
    return _isar.syncOperations
        .filter()
        .entityTypeEqualTo(locationPingEntityType)
        .group(
          (q) => q
              .statusEqualTo(SyncStatus.pending)
              .or()
              .statusEqualTo(SyncStatus.syncing),
        )
        .optional(
          tripId != null,
          (q) => q.entityIdStartsWith('$tripId#'),
        )
        .count();
  }

  /// Coalesce the unsent check-ins when more than [threshold] wait
  /// (live-trip.md section 5.3): per trip and clock hour at most one `fix`
  /// stays, plus every sharing event, so a long dead zone does not flood the
  /// queue. Only `pending` operations are touched: one on its way gets its
  /// own answer.
  ///
  /// Returns the entity ids (ping ids) of the operations removed.
  Future<List<String>> coalesceLocationPings({
    int threshold = locationPingCoalesceThreshold,
  }) async {
    final unsent = await _isar.syncOperations
        .filter()
        .entityTypeEqualTo(locationPingEntityType)
        .statusEqualTo(SyncStatus.pending)
        .findAll();
    if (unsent.length <= threshold) return const [];

    final candidates = <UnsentLocationPing<SyncOperation>>[];
    for (final op in unsent) {
      final ping = _unsentPingOf(op);
      if (ping != null) candidates.add(ping);
    }
    final drop = locationPingsToCoalesce(candidates, threshold: threshold);
    if (drop.isEmpty) return const [];

    await _isar.writeTxn(() async {
      await _isar.syncOperations.deleteAll([for (final op in drop) op.id]);
    });
    debugPrint('[SYNC] coalesced ${drop.length} queued check-ins');
    return [for (final op in drop) op.entityId];
  }

  /// Remove the check-ins of [tripId] that still wait to be sent: the trip's
  /// pickup was refused for good, so they could only wait as `deferred` for
  /// 7 days (LT-08). Operations on their way are left to their answer.
  ///
  /// Returns the entity ids (ping ids) of the operations removed.
  Future<List<String>> dropLocationPingsForTrip(String tripId) async {
    final ops = await _isar.syncOperations
        .filter()
        .entityTypeEqualTo(locationPingEntityType)
        .entityIdStartsWith('$tripId#')
        .group(
          (q) => q.statusEqualTo(SyncStatus.pending).or().group(
                (r) => r
                    .statusEqualTo(SyncStatus.failed)
                    .retryCountLessThan(maxRetries, include: true),
              ),
        )
        .findAll();
    if (ops.isEmpty) return const [];

    await _isar.writeTxn(() async {
      await _isar.syncOperations.deleteAll([for (final op in ops) op.id]);
    });
    return [for (final op in ops) op.entityId];
  }

  static UnsentLocationPing<SyncOperation>? _unsentPingOf(SyncOperation op) {
    try {
      final data = jsonDecode(op.data);
      if (data is! Map) return null;
      final tripId = data['tripId'];
      final kind = data['kind'];
      final capturedAt = DateTime.tryParse('${data['capturedAt']}');
      if (tripId is! String || kind is! String || capturedAt == null) {
        return null;
      }
      final source = data['source'];
      return UnsentLocationPing(
        key: op,
        tripId: tripId,
        kind: kind,
        source: source is String ? source : null,
        capturedAt: capturedAt,
      );
    } on FormatException {
      return null;
    }
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

  /// Entity type of the listing side effect that builds before
  /// sync-protocol.md 1.20.0 queued on delivery create and confirm (FUP-18).
  static const String _legacyCropListingEntityType = 'cropListing';

  /// Remove every queued `cropListing` operation, whatever its status.
  ///
  /// Older builds queued one next to a delivery create (listing `reserved`)
  /// and confirm (`sold`). The server routes `crop_listing` only and answers
  /// this one with an error the client retries, so it was sent three times
  /// per sync and put back by [retryFailed] on every reconnect. It could never
  /// succeed: the trucker and the buyer may not update the farmer's listing,
  /// and the server owns its stock. The user never made it, so it is removed
  /// rather than rejected (a rejected one would count as failed forever).
  /// While queued it kept its listing row out of the crop pull's stale
  /// delete, so once it is gone a listing the server took off the market is
  /// dropped by the next pull.
  ///
  /// Called once per app start, before the sync executor runs. Returns the
  /// number of operations removed.
  Future<int> dropLegacyCropListingOperations() async {
    final removed = await _isar.writeTxn(
      () => _isar.syncOperations
          .filter()
          .entityTypeEqualTo(_legacyCropListingEntityType)
          .deleteAll(),
    );
    if (removed > 0) {
      debugPrint('[SYNC] dropped $removed legacy cropListing operations');
    }
    return removed;
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
