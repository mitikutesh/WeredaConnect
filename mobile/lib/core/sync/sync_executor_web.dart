import 'dart:async';

import 'package:wereda_connect/core/models/sync_operation.dart';

/// Callback for handling sync conflicts (web stub, unused).
typedef ConflictHandler = Future<void> Function(
  SyncOperation operation,
  Map<String, dynamic> serverData,
  int serverVersion,
);

/// Callback to obtain current user ID (web stub, unused).
typedef GetCurrentUserIdCallback = Future<String?> Function();

/// Called when the KYC gate rejected an operation (web stub, unused).
typedef KycRejectionHandler = Future<void> Function(
  SyncOperation operation,
  String message,
);

/// Called when the server refused an operation for good (web stub, unused).
typedef PermanentRejectionHandler = Future<void> Function(
  SyncOperation operation,
  String message,
);

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
  accountDeleted,
}

/// Result of processing a sync batch.
class SyncBatchResult {
  final int total;
  final int succeeded;
  final int conflicted;
  final int failed;
  final int deferred;
  final int stepUpRequired;
  final SyncBatchStatus status;
  final String? errorMessage;
  final Map<String, String> operationErrors;

  const SyncBatchResult({
    this.total = 0,
    this.succeeded = 0,
    this.conflicted = 0,
    this.failed = 0,
    this.deferred = 0,
    this.stepUpRequired = 0,
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
    dynamic securityEvents,
    KycRejectionHandler? onKycRequired,
    PermanentRejectionHandler? onPermanentRejection,
  });

  Stream<SyncBatchResult> get onSyncResult => const Stream.empty();

  bool get isSyncing => false;

  void start() {}

  void stop() {}

  Future<SyncBatchResult> syncNow() async => const SyncBatchResult();

  void dispose() {}
}
