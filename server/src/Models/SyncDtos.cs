namespace WeredaConnect.Server.Models;

/// <summary>
/// Sync batch request from mobile client.
/// Contains one or more operations to process atomically per-operation.
/// See: specs/contracts/sync-protocol.md
/// </summary>
/// <remarks>
/// No member is <c>required</c> (sync-protocol 1.64.0, FUP-148), as on the escrow request bodies
/// (SIM-27, FUP-127): a missing, null or blank member is answered by the handler with
/// <c>400 SYNC_VALIDATION</c> and that member as <c>field</c>, not "Invalid JSON in request body.".
/// The source-generated reader does not run the initializers of init properties: an absent member
/// arrives as null, so the handler checks them before anything reads them. The initializers only
/// serve code that builds a request itself.
/// </remarks>
public sealed record SyncRequest
{
    /// <summary>
    /// Unique identifier for this sync batch attempt (UUID v4).
    /// </summary>
    public string SyncBatchId { get; init; } = "";

    /// <summary>
    /// Device identifier for the originating client.
    /// </summary>
    public string ClientId { get; init; } = "";

    /// <summary>
    /// List of operations to process.
    /// </summary>
    public List<SyncOperationRequest> Operations { get; init; } = [];
}

/// <summary>
/// A single sync operation within a batch request.
/// </summary>
/// <remarks>
/// No member is <c>required</c> (sync-protocol 1.64.0, FUP-148), see <see cref="SyncRequest"/>. An
/// operation missing one of the five identifying members is answered on its own, as an
/// <c>error</c> result <c>SYNC_VALIDATION: {member} is required.</c>; the rest of the batch goes on.
/// </remarks>
public sealed record SyncOperationRequest
{
    /// <summary>
    /// Unique identifier for this operation (UUID v4).
    /// </summary>
    public string OperationId { get; init; } = "";

    /// <summary>
    /// Idempotency key to prevent duplicate processing on retry.
    /// Format: {userId}:{entityType}:{entityId}:{action}:{timestamp}
    /// The server scopes it to the authenticated caller before lookup and storage
    /// (Helpers.SyncIdempotencyKey), so the {userId} segment is not trusted.
    /// </summary>
    public string IdempotencyKey { get; init; } = "";

    /// <summary>
    /// Entity type being synced: crop_listing, trip, delivery, payment, user.
    /// </summary>
    public string EntityType { get; init; } = "";

    /// <summary>
    /// Unique identifier of the entity.
    /// </summary>
    public string EntityId { get; init; } = "";

    /// <summary>
    /// Action to perform: create, update, delete.
    /// </summary>
    public string Action { get; init; } = "";

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
