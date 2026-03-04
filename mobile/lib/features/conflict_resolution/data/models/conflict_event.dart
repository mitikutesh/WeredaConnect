import 'package:equatable/equatable.dart';

import 'package:wereda_connect/core/models/sync_operation.dart';

/// Represents a sync conflict that was auto-resolved by the server.
///
/// Carries enough data for the UI to show the user what changed.
/// The conflict has ALREADY been resolved (server wins) by the time
/// this event reaches the UI — this is purely informational.
class ConflictEvent extends Equatable {
  /// The sync operation that conflicted.
  final SyncOperation operation;

  /// The server's authoritative data that was applied locally.
  final Map<String, dynamic> serverData;

  /// The server's version number that was applied.
  final int serverVersion;

  /// Entity type for display (crop_listing, delivery, trip).
  final String entityType;

  /// Entity ID for lookup.
  final String entityId;

  /// UTC timestamp when the conflict was resolved.
  final DateTime resolvedAt;

  const ConflictEvent({
    required this.operation,
    required this.serverData,
    required this.serverVersion,
    required this.entityType,
    required this.entityId,
    required this.resolvedAt,
  });

  @override
  List<Object?> get props => [
        operation,
        serverData,
        serverVersion,
        entityType,
        entityId,
        resolvedAt,
      ];
}
