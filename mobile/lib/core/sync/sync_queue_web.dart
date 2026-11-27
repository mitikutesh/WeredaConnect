import 'dart:async';

import 'package:dio/dio.dart';

import 'package:wereda_connect/core/models/sync_operation.dart';

/// Web stub for SyncQueue — no Isar persistence on web.
class SyncQueue {
  SyncQueue(dynamic isar);

  Stream<void> get onEnqueued => const Stream.empty();

  static const int maxQueueSize = 1000;
  static const int maxRetries = 3;

  /// The `platform` every web operation names when it is sent (GCR-02).
  /// Web has no queue: [SyncPlatformInterceptor] stamps it.
  static const String platform = 'web';

  Future<SyncOperation> enqueue({
    required String entityType,
    required String entityId,
    required String action,
    required Map<String, dynamic> data,
    String? customIdempotencyKey,
  }) async {
    throw UnsupportedError('SyncQueue not available on web');
  }

  Future<List<SyncOperation>> getPendingOperations({
    int? limit,
    String? onlyEntityType,
    String? exceptEntityType,
  }) async =>
      [];

  Future<int> getPendingCount() async => 0;

  Future<int> getFailedCount() async => 0;

  /// No queue on web, so nothing ever waits or fails in it.
  Stream<({int pending, int failed})> watchCounts() =>
      Stream.value((pending: 0, failed: 0));

  /// No queue on web: a write goes straight to the API, so a logout never
  /// deletes an unsent change (E-1).
  Future<int> getUnsentCountForUser(String userId) async => 0;

  Future<void> markSynced(String operationId) async {}

  /// No queue on web: a handoff photo goes up straight away (FRD-12).
  Future<void> replaceData(String operationId, String data) async {}

  /// No queue on web, so nothing waits.
  Future<bool> hasOpenOperationsFor(String entityType, String entityId) async =>
      false;

  /// No queue on web: a check-in is sent at once (FUP-285).
  Future<List<SyncOperation>> unrejectedOperationsFor(
    String entityType, {
    required String entityIdPrefix,
    String? userId,
  }) async =>
      const [];

  /// No queue on web, so nothing waits (SIM-56).
  Future<bool> hasWaitingOperationsFor(
    String entityType,
    String entityId,
  ) async =>
      false;

  /// No queue on web, so nothing waits (ESC-35).
  Future<List<SyncOperation>> waitingOperationsFor(
    String entityType,
    String entityId,
  ) async =>
      const [];

  /// No queue on web, so it holds no operation (FUP-493).
  Future<List<SyncOperation>> operationsFor(
    String entityType,
    String entityId,
  ) async =>
      const [];

  /// No queue on web, so nothing to drop.
  Future<int> dropWaiting(Iterable<String> operationIds) async => 0;

  Future<void> markFailed(String operationId) async {}

  Future<void> incrementRetry(String operationId) async {}

  Future<void> purgeOldSynced() async {}

  Future<int> retryFailed() async => 0;

  /// No queue on web, so no legacy operation to drop.
  Future<int> dropLegacyCropListingOperations() async => 0;

  /// No queue on web, so nothing is ever rejected.
  static bool isRejected(SyncOperation op) => false;

  Future<int> rejectOpenOperationsFor(
    String entityType,
    String entityId,
  ) async =>
      0;

  /// No queue on web, so nothing is ever reverted.
  Future<int> markRevertedFor(String entityType, String entityId) async => 0;

  /// No queue on web: the web check-in sender keeps its own unsent pings
  /// (LT-08, WebLocationPingRepository).
  Future<int> pendingLocationPingCount({String? tripId}) async => 0;

  /// No queue on web, so nothing to coalesce.
  Future<List<String>> coalesceLocationPings({int? threshold}) async =>
      const [];

  /// No queue on web, so nothing to drop.
  Future<List<String>> dropLocationPingsForTrip(
    String tripId, {
    DateTime? capturedAfter,
  }) async =>
      const [];

  /// Nothing held on web.
  void dispose() {}
}

/// Stamps [SyncQueue.platform] on every operation of a `POST /sync` (GCR-02).
/// Web has no queue: each web repository builds and posts its own batch, so
/// the stamp goes on here, once for all of them. An operation that already
/// names a platform keeps it; the caller's maps are not changed.
class SyncPlatformInterceptor extends Interceptor {
  @override
  void onRequest(RequestOptions options, RequestInterceptorHandler handler) {
    final body = options.data;
    if (options.method == 'POST' &&
        options.path == '/sync' &&
        body is Map<String, dynamic>) {
      final operations = body['operations'];
      if (operations is List) {
        options.data = <String, dynamic>{
          ...body,
          'operations': [
            for (final op in operations)
              op is Map<String, dynamic> && !op.containsKey('platform')
                  ? <String, dynamic>{...op, 'platform': SyncQueue.platform}
                  : op,
          ],
        };
      }
    }
    handler.next(options);
  }
}

/// Exception thrown when the sync queue is full.
class SyncQueueFullException implements Exception {
  final String message;
  const SyncQueueFullException(this.message);

  @override
  String toString() => 'SyncQueueFullException: $message';
}
