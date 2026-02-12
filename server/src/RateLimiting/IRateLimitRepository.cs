namespace WeredaConnect.Server.RateLimiting;

/// <summary>
/// Repository for rate limit entries in DynamoDB.
/// Uses atomic counters and TTL for auto-cleanup.
/// </summary>
public interface IRateLimitRepository
{
    /// <summary>
    /// Get the current rate limit entry for a key. Returns null if no entry exists.
    /// </summary>
    Task<RateLimitEntry?> GetAsync(string key);

    /// <summary>
    /// Atomically increment the counter for a key.
    /// Creates the entry if it doesn't exist, sets TTL for auto-cleanup.
    /// Returns the new count after increment.
    /// </summary>
    Task<int> IncrementAsync(string key, int ttlSeconds);

    /// <summary>
    /// Set a lockout on a key with a specific unlock time.
    /// </summary>
    Task SetLockAsync(string key, string lockedUntil, int ttlSeconds);

    /// <summary>
    /// Delete a rate limit entry (used to reset counters on success).
    /// </summary>
    Task DeleteAsync(string key);
}