import 'dart:async';
import 'dart:convert';
import 'dart:math';

import 'package:connectivity_plus/connectivity_plus.dart';
import 'package:dio/dio.dart';

import 'package:wereda_connect/core/database/schemas/sync_operation.dart';
import 'package:wereda_connect/core/sync/conflict_resolver.dart';
import 'package:wereda_connect/core/sync/connectivity_monitor.dart';
import 'package:wereda_connect/core/sync/sync_queue.dart';
import 'package:wereda_connect/features/conflict_resolution/data/models/conflict_event.dart';
import 'package:wereda_connect/features/conflict_resolution/data/services/conflict_notification_service.dart';

/// Callback for handling sync conflicts.
///
/// When the server reports a version conflict (client was behind),
/// this callback is invoked with the conflicting operation and the
/// server's authoritative data. The default behavior when no handler
/// is provided is "server wins" — the operation is marked as synced.
///
/// Parameters:
/// - [operation]: The local sync operation that conflicted
/// - [serverData]: The server's current entity data
/// - [serverVersion]: The server's current entity version
typedef ConflictHandler = Future<void> Function(
  SyncOperation operation,
  Map<String, dynamic> serverData,
  int serverVersion,
);

/// Status of a sync batch execution.
enum SyncBatchStatus {
  /// All operations in the batch succeeded or resolved conflicts.
  success,

  /// Some operations succeeded, some failed.
  partial,

  /// No pending operations in the queue.
  empty,

  /// Sync was skipped because another sync is already in progress.
  skipped,

  /// No internet access detected (health check failed).
  noInternet,

  /// Network error during sync (timeout, connection error).
  networkError,

  /// Authentication error (token refresh also failed).
  authError,

  /// Rate limited by server (429).
  rateLimited,

  /// Unknown or unrecoverable error.
  error,
}

/// Result of processing a sync batch.
///
/// Tracks how many operations succeeded, conflicted (resolved), or
/// failed within a batch. The [status] field provides a high-level
/// summary for UI display.
class SyncBatchResult {
  final int total;
  final int succeeded;
  final int conflicted;
  final int failed;
  final SyncBatchStatus status;
  final String? errorMessage;

  const SyncBatchResult({
    this.total = 0,
    this.succeeded = 0,
    this.conflicted = 0,
    this.failed = 0,
    this.status = SyncBatchStatus.success,
    this.errorMessage,
  });

  const SyncBatchResult.empty()
      : total = 0,
        succeeded = 0,
        conflicted = 0,
        failed = 0,
        status = SyncBatchStatus.empty,
        errorMessage = null;

  const SyncBatchResult.skipped()
      : total = 0,
        succeeded = 0,
        conflicted = 0,
        failed = 0,
        status = SyncBatchStatus.skipped,
        errorMessage = null;

  const SyncBatchResult.noInternet()
      : total = 0,
        succeeded = 0,
        conflicted = 0,
        failed = 0,
        status = SyncBatchStatus.noInternet,
        errorMessage = null;

  /// Whether any operations in the batch failed or the batch itself errored.
  bool get hasErrors =>
      failed > 0 ||
      status == SyncBatchStatus.networkError ||
      status == SyncBatchStatus.authError ||
      status == SyncBatchStatus.rateLimited ||
      status == SyncBatchStatus.error;

  /// Whether all operations in the batch were processed (success or conflict resolved).
  bool get allSucceeded => total > 0 && succeeded + conflicted == total;

  /// Merge two batch results for aggregation across multiple batches.
  SyncBatchResult merge(SyncBatchResult other) {
    if (status == SyncBatchStatus.empty ||
        status == SyncBatchStatus.skipped ||
        status == SyncBatchStatus.noInternet) {
      return other;
    }
    if (other.status == SyncBatchStatus.empty) return this;

    final newTotal = total + other.total;
    final newSucceeded = succeeded + other.succeeded;
    final newConflicted = conflicted + other.conflicted;
    final newFailed = failed + other.failed;

    return SyncBatchResult(
      total: newTotal,
      succeeded: newSucceeded,
      conflicted: newConflicted,
      failed: newFailed,
      status: newFailed > 0 ? SyncBatchStatus.partial : SyncBatchStatus.success,
      errorMessage: other.errorMessage ?? errorMessage,
    );
  }

  @override
  String toString() => 'SyncBatchResult(status: $status, total: $total, '
      'succeeded: $succeeded, conflicted: $conflicted, failed: $failed)';
}

/// Orchestrates syncing queued operations to the server.
///
/// The SyncExecutor is the core of the offline-first sync engine.
/// It listens to connectivity changes via [ConnectivityMonitor] and
/// processes the [SyncQueue] when internet access is available.
///
/// Architecture:
/// 1. [ConnectivityMonitor] detects connectivity change
/// 2. SyncExecutor verifies actual internet access (health check)
/// 3. Fetches up to [batchSize] pending operations from [SyncQueue]
/// 4. Sends batch POST /sync to server (per `specs/contracts/sync-protocol.md`)
/// 5. Processes per-operation results (success, conflict, error)
/// 6. Updates queue status accordingly
///
/// Error handling per operation result:
/// - `success` → mark synced, remove from queue
/// - `conflict` → invoke [ConflictHandler] (server wins default), mark synced
/// - `duplicate` → already processed, mark synced
/// - `error` → mark failed (retry up to 3 times via [SyncQueue])
///
/// Error handling per HTTP error:
/// - Network/timeout → revert to pending with retry count increment
/// - 401 → revert (AuthInterceptor should have refreshed token)
/// - 429 → revert (rate limited, try again later)
/// - Other → mark failed with retry count increment
///
/// Ethiopian context:
/// - Batch processing minimizes HTTP requests (expensive mobile data)
/// - Debounced connectivity prevents rapid sync trigger/abort on 3G
/// - 30s timeouts account for slow rural connectivity
///
/// See: `specs/contracts/sync-protocol.md`
class SyncExecutor {
  final SyncQueue _syncQueue;
  final ConnectivityMonitor _connectivityMonitor;
  final Dio _dio;
  final String _clientId;
  final ConflictHandler? _onConflict;
  final ConflictResolver? _conflictResolver;
  final ConflictNotificationService? _conflictNotificationService;

  StreamSubscription<List<ConnectivityResult>>? _connectivitySubscription;
  bool _isSyncing = false;

  /// Stream controller for sync batch results (for UI observation).
  final StreamController<SyncBatchResult> _resultController =
      StreamController<SyncBatchResult>.broadcast();

  /// Maximum operations per sync batch.
  /// 50 operations keeps request size reasonable on slow 3G networks.
  static const int batchSize = 50;

  /// API endpoint for sync operations.
  static const String syncEndpoint = '/sync';

  /// Brief delay between consecutive batch processing within a single
  /// syncNow() call, to avoid overwhelming the server.
  static const Duration batchDelay = Duration(seconds: 1);

  /// Retry delays for exponential backoff within a single syncNow() call.
  /// After each batch-level failure, wait progressively longer before retrying.
  static const List<Duration> retryDelays = [
    Duration(seconds: 5),
    Duration(seconds: 15),
    Duration(seconds: 45),
  ];

  /// Creates a SyncExecutor.
  ///
  /// Parameters:
  /// - [syncQueue]: Isar-backed queue of pending sync operations
  /// - [connectivityMonitor]: Network connectivity monitor
  /// - [dio]: Dio HTTP client (should have AuthInterceptor attached)
  /// - [clientId]: Unique device identifier for the sync protocol
  /// - [onConflict]: Optional callback for conflict resolution (legacy, use conflictResolver)
  /// - [conflictResolver]: Service for resolving conflicts (updates local Isar with server data)
  SyncExecutor({
    required SyncQueue syncQueue,
    required ConnectivityMonitor connectivityMonitor,
    required Dio dio,
    required String clientId,
    ConflictHandler? onConflict,
    ConflictResolver? conflictResolver,
    ConflictNotificationService? conflictNotificationService,
  })  : _syncQueue = syncQueue,
        _connectivityMonitor = connectivityMonitor,
        _dio = dio,
        _clientId = clientId,
        _onConflict = onConflict,
        _conflictResolver = conflictResolver,
        _conflictNotificationService = conflictNotificationService;

  /// Stream of sync batch results for UI indicators.
  ///
  /// Emits a [SyncBatchResult] after each batch is processed.
  /// Useful for showing sync progress, error counts, etc.
  Stream<SyncBatchResult> get onSyncResult => _resultController.stream;

  /// Whether the executor is currently processing operations.
  bool get isSyncing => _isSyncing;

  /// Start listening to connectivity changes and auto-syncing.
  ///
  /// When connectivity is detected (debounced), triggers [syncNow].
  /// Call [stop] to stop listening.
  void start() {
    _connectivitySubscription?.cancel();
    _connectivitySubscription =
        _connectivityMonitor.onConnectivityChanged.listen(
      (results) {
        if (_connectivityMonitor.isConnectedFromResults(results)) {
          syncNow();
        }
      },
    );
  }

  /// Trigger an immediate sync attempt.
  ///
  /// Returns early with [SyncBatchStatus.skipped] if already syncing.
  /// Verifies actual internet access before processing.
  /// Processes all pending batches until the queue is empty or
  /// an unrecoverable error occurs.
  ///
  /// Returns an aggregated [SyncBatchResult] across all batches processed.
  Future<SyncBatchResult> syncNow() async {
    if (_isSyncing) {
      return const SyncBatchResult.skipped();
    }

    _isSyncing = true;
    var aggregated = const SyncBatchResult.empty();

    try {
      // Quick check: if OS reports no network, skip the expensive HTTP call
      if (!await _connectivityMonitor.isConnected()) {
        const result = SyncBatchResult.noInternet();
        _resultController.add(result);
        return result;
      }

      // Verify actual internet access (ping /health)
      final hasInternet = await _connectivityMonitor.hasInternetAccess();
      if (!hasInternet) {
        const result = SyncBatchResult.noInternet();
        _resultController.add(result);
        return result;
      }

      var consecutiveFailures = 0;

      // Process batches until queue is empty or max retries exceeded
      while (consecutiveFailures < retryDelays.length) {
        final result = await executeBatch();
        aggregated = aggregated.merge(result);

        if (!_resultController.isClosed) {
          _resultController.add(result);
        }

        // Queue is empty
        if (result.status == SyncBatchStatus.empty) break;

        // Batch succeeded — reset failure counter, check for more
        if (!result.hasErrors) {
          consecutiveFailures = 0;

          final remaining = await _syncQueue.getPendingCount();
          if (remaining == 0) break;

          await Future<void>.delayed(batchDelay);
          continue;
        }

        // Auth or rate limit errors — stop immediately, don't retry
        if (result.status == SyncBatchStatus.authError ||
            result.status == SyncBatchStatus.rateLimited) {
          break;
        }

        // Other errors — apply exponential backoff
        if (consecutiveFailures < retryDelays.length) {
          await Future<void>.delayed(retryDelays[consecutiveFailures]);
        }
        consecutiveFailures++;
      }

      return aggregated;
    } on Object catch (e) {
      final result = SyncBatchResult(
        status: SyncBatchStatus.error,
        errorMessage: e.toString(),
      );
      if (!_resultController.isClosed) {
        _resultController.add(result);
      }
      return result;
    } finally {
      _isSyncing = false;
    }
  }

  /// Execute a single batch of sync operations.
  ///
  /// Fetches up to [batchSize] pending operations, sends them as a batch
  /// POST /sync request, and processes per-operation results.
  ///
  /// Returns [SyncBatchResult] with counts of succeeded/conflicted/failed.
  Future<SyncBatchResult> executeBatch() async {
    final pending = await _syncQueue.getPendingOperations();
    if (pending.isEmpty) return const SyncBatchResult.empty();

    // Mark all as syncing to prevent concurrent processing
    for (final op in pending) {
      await _syncQueue.markAsSyncing(op.operationId);
    }

    // Build batch request per sync-protocol.md
    final syncBatchId = generateBatchId();
    final requestBody = _buildSyncRequest(syncBatchId, pending);

    try {
      final response = await _dio.post<Map<String, dynamic>>(
        syncEndpoint,
        data: requestBody,
      );

      if (response.data == null) {
        await _markBatchFailed(pending);
        return SyncBatchResult(
          total: pending.length,
          failed: pending.length,
          status: SyncBatchStatus.error,
          errorMessage: 'Empty response from server',
        );
      }

      return await _processResponse(pending, response.data!);
    } on DioException catch (e) {
      return await _handleDioError(e, pending);
    } on Object catch (e) {
      await _markBatchFailed(pending);
      return SyncBatchResult(
        total: pending.length,
        failed: pending.length,
        status: SyncBatchStatus.error,
        errorMessage: e.toString(),
      );
    }
  }

  /// Build the sync batch request body per sync-protocol.md.
  Map<String, dynamic> _buildSyncRequest(
    String syncBatchId,
    List<SyncOperation> operations,
  ) {
    return {
      'syncBatchId': syncBatchId,
      'clientId': _clientId,
      'operations': operations
          .map(
            (op) => {
              'operationId': op.operationId,
              'idempotencyKey': op.idempotencyKey,
              'entityType': op.entityType,
              'entityId': op.entityId,
              'action': op.action,
              'data': jsonDecode(op.data),
              'timestamp': op.createdAt.toIso8601String(),
            },
          )
          .toList(),
    };
  }

  /// Process the server's batch response and update queue statuses.
  Future<SyncBatchResult> _processResponse(
    List<SyncOperation> operations,
    Map<String, dynamic> response,
  ) async {
    final results =
        (response['results'] as List?)?.cast<Map<String, dynamic>>() ?? [];

    var succeeded = 0;
    var conflicted = 0;
    var failed = 0;

    // Build lookup map for O(1) access by operationId
    final opMap = {for (final op in operations) op.operationId: op};

    // Track which operations received a response
    final processed = <String>{};

    for (final result in results) {
      final operationId = result['operationId'] as String?;
      if (operationId == null) continue;

      processed.add(operationId);
      final operation = opMap[operationId];
      if (operation == null) continue;

      final status = result['status'] as String?;
      final serverData = result['serverData'] as Map<String, dynamic>?;
      final serverVersion = result['serverVersion'] as int?;

      switch (status) {
        case 'success':
          await _syncQueue.markAsSynced(operationId);
          succeeded++;

        case 'conflict':
          // Server wins — resolve conflict by updating local Isar
          if (serverData != null) {
            // Use ConflictResolver if available (preferred)
            if (_conflictResolver != null) {
              try {
                await _conflictResolver.resolve(
                  operation: operation,
                  serverData: serverData,
                  serverVersion: serverVersion ?? 0,
                );

                // Notify UI about the resolved conflict
                _conflictNotificationService?.notify(
                  ConflictEvent(
                    operation: operation,
                    serverData: serverData,
                    serverVersion: serverVersion ?? 0,
                    entityType: operation.entityType,
                    entityId: operation.entityId,
                    resolvedAt: DateTime.now().toUtc(),
                  ),
                );
              } on ConflictResolutionException {
                // If conflict resolution fails, mark as failed for retry
                await _syncQueue.markAsFailed(operationId);
                failed++;
                continue;
              }
            }
            // Fall back to legacy conflict handler if provided
            else if (_onConflict != null) {
              await _onConflict(operation, serverData, serverVersion ?? 0);
            }
          }
          await _syncQueue.markAsSynced(operationId);
          conflicted++;

        case 'duplicate':
          // Already processed by server — safe to remove from queue
          await _syncQueue.markAsSynced(operationId);
          succeeded++;

        default:
          // 'error' or unknown status
          await _syncQueue.markAsFailed(operationId);
          failed++;
      }
    }

    // Operations not in the response were not processed by the server
    for (final op in operations) {
      if (!processed.contains(op.operationId)) {
        await _syncQueue.markAsFailed(op.operationId);
        failed++;
      }
    }

    return SyncBatchResult(
      total: operations.length,
      succeeded: succeeded,
      conflicted: conflicted,
      failed: failed,
      status: failed > 0 ? SyncBatchStatus.partial : SyncBatchStatus.success,
    );
  }

  /// Handle Dio-level errors (network, auth, rate limit, server errors).
  Future<SyncBatchResult> _handleDioError(
    DioException error,
    List<SyncOperation> operations,
  ) async {
    switch (error.type) {
      case DioExceptionType.connectionTimeout:
      case DioExceptionType.sendTimeout:
      case DioExceptionType.receiveTimeout:
      case DioExceptionType.connectionError:
        await _markBatchFailed(operations);
        return SyncBatchResult(
          total: operations.length,
          failed: operations.length,
          status: SyncBatchStatus.networkError,
          errorMessage: error.message,
        );

      case DioExceptionType.badResponse:
        final statusCode = error.response?.statusCode;

        if (statusCode == 401 || statusCode == 403) {
          // AuthInterceptor should have handled refresh.
          // If we still got 401, the refresh failed.
          await _markBatchFailed(operations);
          return SyncBatchResult(
            total: operations.length,
            failed: operations.length,
            status: SyncBatchStatus.authError,
            errorMessage: 'Authentication failed',
          );
        }

        if (statusCode == 429) {
          await _markBatchFailed(operations);
          return SyncBatchResult(
            total: operations.length,
            failed: operations.length,
            status: SyncBatchStatus.rateLimited,
            errorMessage: 'Rate limited',
          );
        }

        // 4xx client errors or 5xx server errors
        await _markBatchFailed(operations);
        return SyncBatchResult(
          total: operations.length,
          failed: operations.length,
          status: SyncBatchStatus.error,
          errorMessage: error.message,
        );

      default:
        await _markBatchFailed(operations);
        return SyncBatchResult(
          total: operations.length,
          failed: operations.length,
          status: SyncBatchStatus.error,
          errorMessage: error.message,
        );
    }
  }

  /// Mark all operations in a batch as failed (increments retry count).
  Future<void> _markBatchFailed(List<SyncOperation> operations) async {
    for (final op in operations) {
      await _syncQueue.markAsFailed(op.operationId);
    }
  }

  /// Generate a UUID v4 batch ID without external dependencies.
  ///
  /// Uses [Random.secure] for cryptographic randomness.
  /// Format: 8-4-4-4-12 hex characters (standard UUID v4).
  static String generateBatchId() {
    final random = Random.secure();
    final values = List<int>.generate(16, (_) => random.nextInt(256));

    // Set version (4) and variant (1) bits per RFC 4122
    values[6] = (values[6] & 0x0f) | 0x40;
    values[8] = (values[8] & 0x3f) | 0x80;

    final hex = values.map((b) => b.toRadixString(16).padLeft(2, '0')).join();
    return '${hex.substring(0, 8)}-${hex.substring(8, 12)}-'
        '${hex.substring(12, 16)}-${hex.substring(16, 20)}-'
        '${hex.substring(20)}';
  }

  /// Stop listening to connectivity changes.
  ///
  /// Does not cancel any in-flight sync. Call [dispose] to fully clean up.
  void stop() {
    _connectivitySubscription?.cancel();
    _connectivitySubscription = null;
  }

  /// Release all resources held by this executor.
  ///
  /// Stops connectivity listening and closes the result stream.
  void dispose() {
    stop();
    _resultController.close();
  }
}
