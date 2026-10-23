/// Web-safe SyncOperation model (no Isar dependency).
class SyncOperation {
  int id = 0;

  late String operationId;
  late String idempotencyKey;
  late String entityType;
  late String entityId;
  late String action;
  late String data;
  late SyncStatus status;
  late int retryCount;
  late DateTime createdAt;

  /// Mirrors the Isar schema: rejected for good and the local row reverted.
  bool reverted = false;
}

/// Status of a sync operation.
enum SyncStatus {
  pending,
  syncing,
  synced,
  failed,
}
