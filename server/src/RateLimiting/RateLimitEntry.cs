namespace WeredaConnect.Server.RateLimiting;

/// <summary>
/// Represents a rate limit counter stored in DynamoDB.
/// Each entry tracks attempts for a specific action+identifier combination.
/// DynamoDB TTL auto-deletes expired entries.
/// </summary>
public sealed record RateLimitEntry
{
    /// <summary>
    /// Partition key: {ACTION}#{identifier}
    /// Examples: LOGIN#+251912345678, REGISTER#+251912345678#2026-02-12, OTP#+251912345678
    /// </summary>
    public required string Pk { get; init; }

    /// <summary>
    /// Number of attempts in the current window.
    /// </summary>
    public required int Count { get; init; }

    /// <summary>
    /// ISO 8601 timestamp when the account is locked until (login lockout only).
    /// Null if not locked.
    /// </summary>
    public string? LockedUntil { get; init; }

    /// <summary>
    /// DynamoDB TTL (Unix epoch seconds). Entry auto-deleted after this time.
    /// </summary>
    public required long Ttl { get; init; }
}