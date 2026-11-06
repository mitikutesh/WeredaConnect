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
    /// <param name="idempotencyKey">The idempotency key after Helpers.SyncIdempotencyKey.ForCaller scoped it to the caller.</param>
    /// <returns>True if the key exists (already processed).</returns>
    Task<bool> IdempotencyKeyExistsAsync(string idempotencyKey);

    /// <summary>
    /// The payload fingerprint stored with a processed key (SIM-28,
    /// Helpers.SyncPayloadFingerprint). Null or empty for a record written before
    /// fingerprints were stored, or when the key has no record.
    /// </summary>
    /// <param name="idempotencyKey">The idempotency key after Helpers.SyncIdempotencyKey.ForCaller scoped it to the caller.</param>
    Task<string?> GetPayloadFingerprintAsync(string idempotencyKey);

    /// <summary>
    /// Store an idempotency key after successful processing.
    /// The key auto-expires after 7 days via DynamoDB TTL.
    /// </summary>
    /// <param name="idempotencyKey">The idempotency key after Helpers.SyncIdempotencyKey.ForCaller scoped it to the caller.</param>
    /// <param name="operationId">The operation ID for audit trail.</param>
    /// <param name="entityType">The entity type processed.</param>
    /// <param name="result">The processing result (success, conflict, error).</param>
    /// <param name="payloadFingerprint">The fingerprint of the operation the key was sent with
    /// (SIM-28), compared when the key comes again. Null or empty stores none.</param>
    Task StoreIdempotencyKeyAsync(
        string idempotencyKey, string operationId, string entityType, string result, string? payloadFingerprint);
}
