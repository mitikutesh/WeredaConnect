namespace WeredaConnect.Server.Data;

/// <summary>
/// Repository interface for escrow idempotency key persistence in DynamoDB.
/// Prevents duplicate escrow operations via idempotency keys with 30-day TTL.
/// </summary>
public interface IEscrowIdempotencyRepository
{
    /// <summary>
    /// Get an existing idempotency record. Returns null if key doesn't exist.
    /// </summary>
    Task<Dictionary<string, object>?> GetAsync(string idempotencyKey);

    /// <summary>
    /// Store a new idempotency record with a 30-day TTL.
    /// Returns false if the key already exists (duplicate detected).
    /// </summary>
    Task<bool> StoreAsync(string idempotencyKey, string escrowId, string status, string? result = null);

    /// <summary>
    /// Update an existing idempotency record's status and optionally its cached result.
    /// </summary>
    Task UpdateAsync(string idempotencyKey, string status, string? result = null);
}
