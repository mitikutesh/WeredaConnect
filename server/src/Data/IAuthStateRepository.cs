namespace WeredaConnect.Server.Data;

/// <summary>
/// A short-lived authentication state row (OTP challenge, OIDC login state,
/// consumed verification-token id). Rows expire through the table's TTL and
/// are additionally treated as absent once <see cref="ExpiresAt"/> has passed,
/// because DynamoDB TTL deletion is best-effort.
/// </summary>
public sealed record AuthStateEntry
{
    public required string Key { get; init; }
    public required IReadOnlyDictionary<string, string> Attributes { get; init; }
    public required long ExpiresAt { get; init; }

    public string? Get(string name) =>
        Attributes.TryGetValue(name, out var value) ? value : null;
}

/// <summary>
/// Key/value store for transient auth state. Backed by the rate-limits table
/// (pk = sk = key, TTL on "expiresAt") so no new table or grants are needed.
/// Keys are namespaced by the caller, e.g. "AUTHSTATE#OTP#+2519...".
/// </summary>
public interface IAuthStateRepository
{
    Task<AuthStateEntry?> GetAsync(string key);

    Task PutAsync(string key, IReadOnlyDictionary<string, string> attributes, int ttlSeconds);

    /// <summary>
    /// Write the row only when no row with this key exists. Returns false when
    /// the key is already present (used for single-use ids).
    /// </summary>
    Task<bool> PutIfAbsentAsync(string key, IReadOnlyDictionary<string, string> attributes, int ttlSeconds);

    /// <summary>
    /// Atomically delete the row and return its previous contents, or null when
    /// it did not exist or had already expired. Guarantees single use.
    /// </summary>
    Task<AuthStateEntry?> ConsumeAsync(string key);

    Task DeleteAsync(string key);
}
