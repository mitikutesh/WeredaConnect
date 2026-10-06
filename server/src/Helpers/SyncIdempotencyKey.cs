namespace WeredaConnect.Server.Helpers;

/// <summary>
/// Scopes a client-sent sync idempotency key to the authenticated caller
/// (SIM-48, ESC-11, SEC-03). The SyncMetadata table is keyed by this scoped
/// value, so another user can no longer pre-claim your key and get your
/// operation answered <c>duplicate</c> (and dropped from your queue).
/// </summary>
/// <remarks>
/// <para>
/// Rule: a key that already starts with <c>{callerId}:</c> (the documented
/// format <c>{userId}:{entityType}:{entityId}:{action}:{timestamp}</c> that the
/// mobile sync queue sends) is used unchanged; any other key (web keys such as
/// <c>web_create_{id}</c>, malformed keys, a key carrying another user's prefix)
/// becomes <c>{callerId}:{clientKey}</c>.
/// </para>
/// <para>
/// Every scoped key therefore starts with <c>{callerId}:</c>. A Cognito sub
/// never contains <c>:</c>, so the first segment always names the caller and the
/// same client key sent by two users always maps to two different stored keys.
/// Documented-format keys are stored exactly as before, so records written before
/// this change still answer a straddling retry with <c>duplicate</c>.
/// </para>
/// <para>
/// Within one caller, <c>x</c> and <c>{callerId}:x</c> share a stored key. That only
/// lets a caller collide with itself, never with someone else.
/// </para>
/// See: specs/contracts/sync-protocol.md (Idempotency Key Conventions).
/// </remarks>
public static class SyncIdempotencyKey
{
    private const char Separator = ':';

    /// <summary>
    /// Returns the caller-scoped storage key for <paramref name="clientKey"/>, or
    /// null when <paramref name="callerId"/> cannot scope a key (null, empty, or
    /// containing <c>:</c>). Callers must fail closed on null.
    /// </summary>
    public static string? ForCaller(string? callerId, string clientKey)
    {
        if (string.IsNullOrEmpty(callerId) || callerId.Contains(Separator))
        {
            return null;
        }

        var prefix = callerId + Separator;
        return clientKey.StartsWith(prefix, StringComparison.Ordinal)
            ? clientKey
            : prefix + clientKey;
    }
}
