namespace WeredaConnect.Server.Data;

/// <summary>
/// Repository interface for sync idempotency key tracking in DynamoDB.
/// Prevents duplicate processing of sync operations on client retry.
/// See: specs/contracts/sync-protocol.md
/// </summary>
public interface ISyncMetadataRepository
{
    /// <summary>
    /// Check if an idempotency key has already been processed.
    /// </summary>
    /// <param name="idempotencyKey">The operation's idempotency key.</param>
    /// <returns>True if the key exists (already processed).</returns>
    Task<bool> IdempotencyKeyExistsAsync(string idempotencyKey);

    /// <summary>
    /// Store an idempotency key after successful processing.
    /// The key auto-expires after 7 days via DynamoDB TTL.
    /// </summary>
    /// <param name="idempotencyKey">The operation's idempotency key.</param>
    /// <param name="operationId">The operation ID for audit trail.</param>
    /// <param name="entityType">The entity type processed.</param>
    /// <param name="result">The processing result (success, conflict, error).</param>
    Task StoreIdempotencyKeyAsync(string idempotencyKey, string operationId, string entityType, string result);
}
