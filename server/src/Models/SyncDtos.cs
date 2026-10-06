namespace WeredaConnect.Server.Models;

/// <summary>
/// Sync batch request from mobile client.
/// Contains one or more operations to process atomically per-operation.
/// See: specs/contracts/sync-protocol.md
/// </summary>
public sealed record SyncRequest
{
    /// <summary>
    /// Unique identifier for this sync batch attempt (UUID v4).
    /// </summary>
    public required string SyncBatchId { get; init; }

    /// <summary>
    /// Device identifier for the originating client.
    /// </summary>
    public required string ClientId { get; init; }

    /// <summary>
    /// List of operations to process.
    /// </summary>
    public required List<SyncOperationRequest> Operations { get; init; }
}

/// <summary>
/// A single sync operation within a batch request.
/// </summary>
public sealed record SyncOperationRequest
{
    /// <summary>
    /// Unique identifier for this operation (UUID v4).
    /// </summary>
    public required string OperationId { get; init; }

    /// <summary>
    /// Idempotency key to prevent duplicate processing on retry.
    /// Format: {userId}:{entityType}:{entityId}:{action}:{timestamp}
    /// The server scopes it to the authenticated caller before lookup and storage
    /// (Helpers.SyncIdempotencyKey), so the {userId} segment is not trusted.
    /// </summary>
    public required string IdempotencyKey { get; init; }

    /// <summary>
    /// Entity type being synced: crop_listing, trip, delivery, payment, user.
    /// </summary>
    public required string EntityType { get; init; }

    /// <summary>
    /// Unique identifier of the entity.
    /// </summary>
    public required string EntityId { get; init; }

    /// <summary>
    /// Action to perform: create, update, delete.
    /// </summary>
    public required string Action { get; init; }

    /// <summary>
    /// Entity data payload (schema depends on entityType).
    /// </summary>
    public Dictionary<string, object>? Data { get; init; }

    /// <summary>
    /// ISO 8601 UTC timestamp of when the operation was created on the client.
    /// Informational only — not used for conflict resolution.
    /// </summary>
    public string? Timestamp { get; init; }

    /// <summary>
    /// The entity version the client held when it made the change (optimistic
    /// concurrency, sync-protocol.md). Optional: the shipped clients send the same
    /// value as data.version; when both are present this one wins (SIM-08).
    /// </summary>
    public int? ClientVersion { get; init; }
}

/// <summary>
/// Sync batch response to mobile client.
/// Contains per-operation results.
/// </summary>
public sealed record SyncResponse
{
    /// <summary>
    /// Echo of the request batch ID for correlation.
    /// </summary>
    public required string SyncBatchId { get; init; }

    /// <summary>
    /// Per-operation results.
    /// </summary>
    public required List<SyncOperationResult> Results { get; init; }
}

/// <summary>
/// Result of processing a single sync operation.
/// </summary>
public sealed record SyncOperationResult
{
    /// <summary>
    /// Echo of the operation ID for correlation.
    /// </summary>
    public required string OperationId { get; init; }

    /// <summary>
    /// Result status: success, conflict, duplicate, error.
    /// </summary>
    public required string Status { get; init; }

    /// <summary>
    /// Current server version of the entity (after processing).
    /// </summary>
    public int? ServerVersion { get; init; }

    /// <summary>
    /// Server's current entity data (returned on conflict).
    /// </summary>
    public Dictionary<string, object>? ServerData { get; init; }

    /// <summary>
    /// Human-readable message (for errors and conflicts).
    /// </summary>
    public string? Message { get; init; }
}
