using Amazon.DynamoDBv2.Model;
using WeredaConnect.Server.Models;

namespace WeredaConnect.Server.Data;

/// <summary>
/// ESC-45: the Telebirr webhook inbox for order payments, stored in the escrow idempotency table
/// (PK <c>idempotencyKey</c> = <c>webhook-inbox:{merchOrderId}:{paymentOrderId}:{tradeStatus}</c>,
/// 30-day TTL). A row is written for every verified order notification before it is applied and
/// stays in the sparse <see cref="WebhookInbox.IndexName"/> (partition <c>pending</c>) until a final
/// outcome is written, so a notification whose processing failed or was cut off is replayed by
/// the sweep without Telebirr sending it again. After <see cref="WebhookInbox.MaxAttempts"/>
/// replays it moves to the <c>exhausted</c> partition for an admin.
/// </summary>
public interface IWebhookInboxRepository
{
    /// <summary>The row for <paramref name="key"/>, or null.</summary>
    Task<WebhookInboxRow?> GetAsync(string key);

    /// <summary>
    /// Write a new pending row (status <c>received</c>, the verified payment fields, first replay
    /// at <paramref name="receivedAtUtc"/> plus <see cref="WebhookInbox.FirstReplayDelay"/>). False
    /// when a row with the key already exists.
    /// </summary>
    Task<bool> RecordAsync(
        string key, string escrowId, string orderId, string merchOrderId, TelebirrWebhookBiz biz, DateTime receivedAtUtc);

    /// <summary>
    /// Write a final outcome and take the row out of the index (the stored payment fields go
    /// with it). False when the row does not exist.
    /// </summary>
    Task<bool> FinalizeAsync(string key, string outcome);

    /// <summary>Mark a pending row <c>unprocessed</c>; false (no write) when it is no longer pending.</summary>
    Task<bool> MarkUnprocessedAsync(string key);

    /// <summary>A page of pending rows due for a replay at <paramref name="nowUtc"/>, oldest first.</summary>
    Task<(IReadOnlyList<WebhookInboxRow> Rows, Dictionary<string, AttributeValue>? Next)> QueryDueAsync(
        DateTime nowUtc, int limit, Dictionary<string, AttributeValue>? startKey);

    /// <summary>A page of all pending rows, due or not (the order cron skips their orders).</summary>
    Task<(IReadOnlyList<WebhookInboxRow> Rows, Dictionary<string, AttributeValue>? Next)> QueryPendingAsync(
        int limit, Dictionary<string, AttributeValue>? startKey);

    /// <summary>
    /// Claim a due row for one replay: only while it is still pending with the replay time and
    /// attempt count that were read, so two sweeps never replay it in the same round. The claim
    /// counts the attempt and sets the next replay time before anything is processed.
    /// </summary>
    Task<bool> TryClaimAsync(string key, string seenReplayAt, int seenAttempts, DateTime nextReplayAtUtc, DateTime nowUtc);

    /// <summary>
    /// Give up on a row: move it to the <c>exhausted</c> partition, only while it is still pending
    /// with <paramref name="seenAttempts"/> (a finalize that won the race is never undone). False
    /// when the condition failed.
    /// </summary>
    Task<bool> ExhaustAsync(string key, int seenAttempts);
}

/// <summary>Constants of the ESC-45 webhook inbox.</summary>
public static class WebhookInbox
{
    public const string IndexName = "InboxReplayIndex";
    public const string Marker = "inboxReplay";
    public const string ReplayAt = "inboxReplayAt";
    public const string Attempts = "replayAttempts";
    public const string Pending = "pending";
    public const string Exhausted = "exhausted";

    /// <summary>Replays per row before it is given up (exhausted and alarmed).</summary>
    public const int MaxAttempts = 8;

    /// <summary>First replay after receipt: well past the webhook's 15 second run.</summary>
    public static readonly TimeSpan FirstReplayDelay = TimeSpan.FromMinutes(2);

    /// <summary>Status of a row whose processing has not finished.</summary>
    public const string Received = "received";
    public const string Unprocessed = "unprocessed";

    /// <summary>
    /// The wait after failed replay number <paramref name="attemptsSoFar"/> + 1: 5, 10, 20, 40, then
    /// 60 minutes.
    /// </summary>
    public static TimeSpan Backoff(int attemptsSoFar)
        => TimeSpan.FromMinutes(Math.Min(5 * Math.Pow(2, Math.Max(0, attemptsSoFar)), 60));

    /// <summary>
    /// How long after receipt the last scheduled replay runs, plus the sweep's 5 minute cadence and
    /// a margin: what the D8 order hold covers (about 4 h 47 min).
    /// </summary>
    public static readonly TimeSpan ReplayHorizon = ComputeHorizon();

    /// <summary>One ISO-8601 UTC form for every replay and hold time, so string comparisons order correctly.</summary>
    public static string Iso(DateTime utc)
        => utc.ToUniversalTime().ToString("o", System.Globalization.CultureInfo.InvariantCulture);

    private static TimeSpan ComputeHorizon()
    {
        var total = FirstReplayDelay;
        for (var attempt = 0; attempt < MaxAttempts - 1; attempt++)
        {
            total += Backoff(attempt);
        }

        return total + TimeSpan.FromMinutes(5) + TimeSpan.FromMinutes(25);
    }
}

/// <summary>One inbox row as read.</summary>
public sealed record WebhookInboxRow(
    string Key,
    string EscrowId,
    string Status,
    string? OrderId,
    string? MerchOrderId,
    string? Marker,
    string? ReplayAt,
    int Attempts,
    string? CreatedAt,
    TelebirrWebhookBiz? Biz)
{
    /// <summary>A row whose outcome is final: neither received nor unprocessed, and out of the index.</summary>
    public bool IsFinal => Status is not (WebhookInbox.Received or WebhookInbox.Unprocessed);

    /// <summary>An earlier verdict that refused the payment (it may already be refunded by hand).</summary>
    public bool IsRefusal => Status.StartsWith("unmatched:", StringComparison.Ordinal) || Status == "duplicate";
}
