/// Generates deterministic idempotency keys for sync operations.
///
/// Idempotency keys ensure that the same logical operation is not
/// processed twice by the server, even if the client retries.
///
/// Format: `{userId}:{entityType}:{entityId}:{action}:{timestamp}`
///
/// The key is deterministic — the same user performing the same action
/// on the same entity at the same time always produces the same key.
/// This prevents duplicate sync operations on retry.
///
/// See: `specs/contracts/sync-protocol.md`
class IdempotencyKeyGenerator {
  /// Generates an idempotency key for a sync operation.
  ///
  /// Parameters:
  /// - [userId]: The authenticated user's ID
  /// - [entityType]: Entity being modified ('crop_listing', 'trip', etc.)
  /// - [entityId]: The entity's unique identifier
  /// - [action]: The operation ('create', 'update', 'delete')
  /// - [timestamp]: UTC timestamp of the operation (milliseconds since epoch)
  ///
  /// Returns a deterministic string key.
  static String generate({
    required String userId,
    required String entityType,
    required String entityId,
    required String action,
    required DateTime timestamp,
  }) {
    final epochMs = timestamp.toUtc().millisecondsSinceEpoch;
    return '$userId:$entityType:$entityId:$action:$epochMs';
  }

  /// Parses an idempotency key back into its components.
  ///
  /// Returns null if the key format is invalid.
  static IdempotencyKeyComponents? parse(String key) {
    final parts = key.split(':');
    if (parts.length != 5) return null;

    final epochMs = int.tryParse(parts[4]);
    if (epochMs == null) return null;

    return IdempotencyKeyComponents(
      userId: parts[0],
      entityType: parts[1],
      entityId: parts[2],
      action: parts[3],
      timestamp: DateTime.fromMillisecondsSinceEpoch(epochMs, isUtc: true),
    );
  }
}

/// Parsed components of an idempotency key.
class IdempotencyKeyComponents {
  final String userId;
  final String entityType;
  final String entityId;
  final String action;
  final DateTime timestamp;

  const IdempotencyKeyComponents({
    required this.userId,
    required this.entityType,
    required this.entityId,
    required this.action,
    required this.timestamp,
  });
}
