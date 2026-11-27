using Amazon.DynamoDBv2.Model;

namespace WeredaConnect.Server.Data;

/// <summary>
/// ESC-21: one inbox row per (user, event). Table <c>{prefix}-notifications</c>: PK <c>userId</c>,
/// SK <c>sortKey</c> = <c>{createdAt ISO}#{type}#{entityId}</c>, so a user's rows read newest first
/// and a stream retry writes the same key (the put is conditional, which makes the inbox the
/// pipeline's idempotency record). TTL on <c>expiresAt</c> (epoch seconds, 90 days).
/// <c>WrittenAt</c> is when the row was first written (ISO, the <c>WrittenAtIndex</c> sort key):
/// rows arrive out of event order (three streams in parallel, retries up to a day late), so the
/// client's incremental pull reads by write time, never by <c>sortKey</c>.
/// </summary>
public sealed record NotificationRow(
    string UserId,
    string SortKey,
    string Type,
    string Entity,
    string EntityId,
    string? OrderId,
    IReadOnlyDictionary<string, string> Params,
    string CreatedAt,
    long ExpiresAt,
    string WrittenAt = "");

public interface INotificationRepository
{
    /// <summary>Puts the row unless one with the same key exists. True when it was new.</summary>
    Task<bool> TryPutAsync(NotificationRow row);

    /// <summary>
    /// A page of the user's rows. Without <paramref name="writtenAfter"/>: newest event first (the
    /// inbox as shown). With it (an ISO time): only rows written after it, newest write first, read
    /// strongly consistent from <c>WrittenAtIndex</c> (the client's incremental pull).
    /// <paramref name="exclusiveStartKey"/> continues a page of the same query.
    /// </summary>
    Task<(List<NotificationRow> Items, Dictionary<string, AttributeValue>? LastEvaluatedKey)> QueryByUserAsync(
        string userId, int limit, Dictionary<string, AttributeValue>? exclusiveStartKey = null, string? writtenAfter = null);
}
