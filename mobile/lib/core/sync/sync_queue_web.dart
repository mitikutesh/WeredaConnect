import 'dart:async';

import 'package:wereda_connect/core/models/sync_operation.dart';

/// Web stub for SyncQueue — no Isar persistence on web.
class SyncQueue {
  SyncQueue(dynamic isar);

  Stream<void> get onEnqueued => const Stream.empty();

  static const int maxQueueSize = 1000;
  static const int maxRetries = 3;

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

  Future<void> markSynced(String operationId) async {}

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
}

/// Exception thrown when the sync queue is full.
class SyncQueueFullException implements Exception {
  final String message;
  const SyncQueueFullException(this.message);

  @override
  String toString() => 'SyncQueueFullException: $message';
}
