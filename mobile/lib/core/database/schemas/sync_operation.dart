import 'package:isar/isar.dart';

part 'sync_operation.g.dart';

/// Represents a pending sync operation in the offline-first queue.
/// All local changes are queued here and synced when connectivity is available.
///
/// Offline-first pattern:
/// - Every create/update/delete operation is enqueued
/// - Idempotency key prevents duplicate operations on retry
/// - Failed operations are retried with exponential backoff
@collection
class SyncOperation {
  /// Isar auto-generated ID (internal use only)
  Id id = Isar.autoIncrement;

  /// Server-assigned operation ID (UUID)
  @Index(unique: true)
  late String operationId;

  /// Idempotency key to prevent duplicate operations
  /// Format: '{entityType}:{entityId}:{action}:{timestamp}'
  /// Example: 'crop_listing:123:update:1234567890'
  @Index(unique: true)
  late String idempotencyKey;

  /// Type of entity being synced
  /// Values: 'user', 'crop_listing', 'trip', 'delivery', 'payment'
  @Index()
  late String entityType;

  /// ID of the entity being synced
  late String entityId;

  /// Action being performed
  /// Values: 'create', 'update', 'delete'
  late String action;

  /// Serialized JSON data of the entity
  /// Stored as string to avoid Isar serialization complexity
  /// Deserialize using JsonSerializer when syncing
  late String data;

  /// Current status of the sync operation
  @Index()
  @Enumerated(EnumType.name)
  late SyncStatus status;

  /// Number of retry attempts (max 3 before marking as failed)
  /// Used for exponential backoff: delay = 2^retryCount seconds
  late int retryCount;

  /// Timestamp when operation was created (UTC)
  late DateTime createdAt;
}

/// Status of a sync operation
enum SyncStatus {
  /// Waiting in queue, not yet attempted
  pending,

  /// Currently being synced to server
  syncing,

  /// Successfully synced to server
  synced,

  /// Failed after max retries (requires manual intervention)
  failed,
}
