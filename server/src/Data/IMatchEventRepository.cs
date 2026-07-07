using WeredaConnect.Server.Services;

namespace WeredaConnect.Server.Data;

/// <summary>
/// Append-only store for buyer match-interaction events (impressions, opens,
/// order conversions). These logs are the training corpus for a future learned
/// ranker (see specs/contracts/match-events.md) — write-only from the app's
/// perspective, expired automatically via DynamoDB TTL.
/// </summary>
public interface IMatchEventRepository
{
    /// <summary>
    /// Persist one match event. Idempotent: re-writing an existing eventId is
    /// reported as success without a second write (events are append-only, so
    /// a duplicate is a retry, never a conflict).
    /// </summary>
    Task<AuthResult<bool>> CreateAsync(
        string eventId,
        string intentId,
        string buyerId,
        Dictionary<string, object> data);
}
