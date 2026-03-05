import 'dart:async';

import 'package:wereda_connect/core/models/sync_operation.dart';

/// Callback for handling sync conflicts (web stub — unused).
typedef ConflictHandler = Future<void> Function(
  SyncOperation operation,
  Map<String, dynamic> serverData,
  int serverVersion,
);

/// Callback to obtain current user ID (web stub — unused).
typedef GetCurrentUserIdCallback = Future<String?> Function();

/// Status of a sync batch execution.
enum SyncBatchStatus {
  success,
  partial,
  empty,
  skipped,
  noInternet,
  networkError,
  authError,
  rateLimited,
  error,
}

/// Result of processing a sync batch.
class SyncBatchResult {
  final int total;
  final int succeeded;
  final int conflicted;
  final int failed;
  final SyncBatchStatus status;
  final String? errorMessage;
  final Map<String, String> operationErrors;

  const SyncBatchResult({
    this.total = 0,
    this.succeeded = 0,
    this.conflicted = 0,
    this.failed = 0,
    this.status = SyncBatchStatus.empty,
    this.errorMessage,
    this.operationErrors = const {},
  });
}

/// Web stub for SyncExecutor — no Isar or offline sync on web.
class SyncExecutor {
  SyncExecutor({
    dynamic syncQueue,
    dynamic connectivityMonitor,
    dynamic dio,
    String clientId = '',
    ConflictHandler? onConflict,
    dynamic conflictResolver,
    dynamic conflictNotificationService,
    void Function()? onSyncComplete,
    GetCurrentUserIdCallback? getCurrentUserId,
  });

  Stream<SyncBatchResult> get onSyncResult => const Stream.empty();

  bool get isSyncing => false;

  void start() {}

  void stop() {}

  Future<SyncBatchResult> syncNow() async => const SyncBatchResult();

  void dispose() {}
}
