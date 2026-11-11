using System.Text.Json;
using System.Text;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Models;

namespace WeredaConnect.Server.Tests.Services;

/// <summary>
/// ESC-28: an in-memory reputation-events table and users rows with the conditions of the real
/// transaction (reputation-architecture.md 6.2): the slot is put only when absent, the users map only
/// under the version read (and never on a deleting account), the earn-back target only under its row
/// version and status active, a transition only from the status read. One failed condition writes
/// nothing (all or nothing, like <c>TransactWriteItems</c>).
/// </summary>
internal sealed class FakeReputationRepository : IReputationEventRepository
{
    public sealed class User
    {
        public string Role { get; set; } = "farmer";
        public string? DeletionState { get; set; }
        public bool Suspended { get; set; }
        public ReputationMapWrite? Map { get; set; }
        public long RatingSum { get; set; }
        public long RatingCount { get; set; }

        /// <summary>Set like a registered or backfilled row; null is a row the backfill skipped.</summary>
        public string? RatingsAggregatedAt { get; set; } = "2026-11-01T00:00:00.000Z";
    }

    public Dictionary<string, User> Users { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, SortedDictionary<string, ReputationEvent>> Rows { get; } = new(StringComparer.Ordinal);

    /// <summary>Every write that committed, in order.</summary>
    public List<ReputationWrite> Committed { get; } = new();

    public int Attempts { get; private set; }

    /// <summary>Runs before a commit is judged (once it returns, the commit checks the state): a concurrent writer.</summary>
    public Func<ReputationWrite, Task>? BeforeCommit { get; set; }

    /// <summary>The Query hides this row for <see cref="HiddenReads"/> reads (a read that lags the users row).</summary>
    public string? Hidden { get; set; }

    public int HiddenReads { get; set; }

    /// <summary>When set, every read of the users row throws it (a throttle, an outage).</summary>
    public Exception? ReadFailure { get; set; }

    public User AddUser(string userId, string role = "farmer")
    {
        var user = new User { Role = role };
        Users[userId] = user;
        return user;
    }

    public IReadOnlyList<ReputationEvent> RowsOf(string userId)
        => Rows.TryGetValue(userId, out var rows) ? rows.Values.ToList() : new List<ReputationEvent>();

    public ReputationEvent Row(string userId, string eventId) => Rows[userId][eventId];

    public void Put(ReputationEvent row)
    {
        if (!Rows.TryGetValue(row.UserId, out var rows))
        {
            Rows[row.UserId] = rows = new SortedDictionary<string, ReputationEvent>(StringComparer.Ordinal);
        }

        rows[row.EventId] = row;
    }

    public Task<ReputationUserState?> GetUserStateAsync(string userId)
    {
        if (ReadFailure != null)
        {
            throw ReadFailure;
        }

        if (!Users.TryGetValue(userId, out var user))
        {
            return Task.FromResult<ReputationUserState?>(null);
        }

        var map = user.Map;
        return Task.FromResult<ReputationUserState?>(new ReputationUserState
        {
            UserId = userId,
            Role = user.Role,
            DeletionState = user.DeletionState,
            Suspended = user.Suspended,
            MapVersion = map?.Version,
            StoredTier = map?.Aggregate.Tier,
            StoredEffectiveRating = map?.Aggregate.EffectiveRating,
            StoredReliabilityPct = map?.Aggregate.ReliabilityPct,
            StoredActivePoints = map?.Aggregate.ActivePoints,
            LastEventId = map?.LastEventId,
            LastEventRecordedAt = map?.LastEventRecordedAt,
            RatingSum = user.RatingSum,
            RatingCount = user.RatingCount,
            RatingsAggregatedAt = user.RatingsAggregatedAt,
            Snapshot = map == null
                ? null
                : new ReputationSnapshot
                {
                    Version = map.Version,
                    Tier = map.Aggregate.Tier,
                    ActivePoints = map.Aggregate.ActivePoints,
                    CompletedOrders90d = map.Aggregate.CompletedOrders90d,
                    FaultCancels90d = map.Aggregate.FaultCancels90d,
                    NoShows90d = map.Aggregate.NoShows90d,
                    ReliabilityPct = map.Aggregate.ReliabilityPct,
                    EffectiveRating = map.Aggregate.EffectiveRating,
                    HasRestriction = map.Aggregate.Restriction != null,
                    RestrictionCause = map.Aggregate.Restriction?.Cause,
                    RestrictionSince = map.Aggregate.Restriction?.Since,
                    RestrictionUntil = map.Aggregate.Restriction?.Until,
                    OverrideTag = map.Aggregate.Override?.Tag,
                    OverrideReason = map.Aggregate.Override?.Reason,
                    OverrideUntil = map.Aggregate.Override?.Until,
                    NextChangeAt = map.Aggregate.NextChangeAt
                }
        });
    }

    public Task<(IReadOnlyList<ReputationEvent> Rows, bool Truncated)> QueryEventsAsync(string userId, int cap)
    {
        var rows = RowsOf(userId).ToList();
        if (Hidden != null && HiddenReads > 0)
        {
            rows.RemoveAll(r => r.EventId == Hidden);
            HiddenReads--;
        }

        var truncated = rows.Count > cap;
        return Task.FromResult<(IReadOnlyList<ReputationEvent>, bool)>((rows.Take(cap).ToList(), truncated));
    }

    public async Task<ReputationCommitOutcome> CommitAsync(ReputationWrite write)
    {
        Attempts++;
        if (BeforeCommit is { } before)
        {
            BeforeCommit = null;
            await before(write);
        }

        var rows = Rows.TryGetValue(write.UserId, out var existing)
            ? existing
            : new SortedDictionary<string, ReputationEvent>(StringComparer.Ordinal);

        if (write.NewEvent != null && rows.ContainsKey(write.NewEvent.EventId))
        {
            return ReputationCommitOutcome.EventExists;
        }

        if (!Users.TryGetValue(write.UserId, out var user) || user.DeletionState != null
            || user.Map?.Version != write.ExpectedMapVersion)
        {
            return ReputationCommitOutcome.Conflict;
        }

        if (write.NewSystemRows.Any(r => rows.ContainsKey(r.EventId)))
        {
            return ReputationCommitOutcome.Conflict;
        }

        if (write.EarnBack is { } earnBack
            && (!rows.TryGetValue(earnBack.EventId, out var target) || target.Version != earnBack.SeenVersion
                || target.Status != ReputationStatuses.Active))
        {
            return ReputationCommitOutcome.Conflict;
        }

        if (write.Transitions.Any(t => !rows.TryGetValue(t.EventId, out var row) || row.Status != t.FromStatus))
        {
            return ReputationCommitOutcome.Conflict;
        }

        // All conditions hold: apply every item.
        if (write.NewEvent != null)
        {
            Put(write.NewEvent);
        }

        user.Map = write.Map;
        foreach (var row in write.NewSystemRows)
        {
            Put(row);
        }

        if (write.EarnBack is { } eb)
        {
            var earned = Rows[write.UserId][eb.EventId];
            Put(earned with { EarnedBack = earned.EarnedBack + 1, Version = earned.Version + 1 });
        }

        if (write.HealAt is { } healAt)
        {
            var key = ReputationSlots.Heal + ReputationPolicy.Day(healAt);
            var rowsNow = Rows.TryGetValue(write.UserId, out var r) ? r : null;
            if (rowsNow == null || !rowsNow.ContainsKey(key))
            {
                Put(new ReputationEvent
                {
                    UserId = write.UserId,
                    EventId = key,
                    Code = ReputationCodes.Heal,
                    DueDay = ReputationPolicy.Day(healAt),
                    DueAt = ReputationPolicy.Iso(healAt)
                });
            }
        }

        foreach (var t in write.Transitions)
        {
            var row = Rows[write.UserId][t.EventId];
            Put(t.ToStatus == null
                ? row with { DueDay = null, DueAt = null }
                : row with { Status = t.ToStatus, DueDay = null, DueAt = null, Version = row.Version + 1 });
        }

        foreach (var eventId in write.Deletes)
        {
            Rows[write.UserId].Remove(eventId);
        }

        Committed.Add(write);
        return ReputationCommitOutcome.Committed;
    }

    public Task<IReadOnlyList<(string UserId, string EventId)>> QueryDueAsync(string day, string dueAtOrBefore)
        => Task.FromResult<IReadOnlyList<(string, string)>>(Rows.Values
            .SelectMany(r => r.Values)
            .Where(r => r.DueDay == day && string.CompareOrdinal(r.DueAt, dueAtOrBefore) <= 0)
            .Select(r => (r.UserId, r.EventId))
            .ToList());

    public Task<int> CountOpenAppealsAsync(string appealStatus, string appealedBefore)
        => Task.FromResult(Rows.Values.SelectMany(r => r.Values)
            .Count(r => r.AppealStatus == appealStatus && string.CompareOrdinal(r.AppealedAt, appealedBefore) < 0));
}

/// <summary>A clock a test moves by hand.</summary>
internal sealed class ManualClock : TimeProvider
{
    public ManualClock(DateTime nowUtc) => Now = DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc);

    public DateTime Now { get; set; }

    public override DateTimeOffset GetUtcNow() => new(DateTime.SpecifyKind(Now, DateTimeKind.Utc));
}

/// <summary>Builds orders stream records in the DynamoDB JSON the Lambda receives.</summary>
internal static class OrderRecords
{
    public const string OrdersArn = "arn:aws:dynamodb:eu-north-1:123:table/wereda-connect-dev-orders/stream/2026";

    /// <summary>One MODIFY record with the given old and new attributes (strings, or numbers when the value is a decimal).</summary>
    public static string Modify(
        IReadOnlyDictionary<string, object> oldImage,
        IReadOnlyDictionary<string, object> newImage,
        string sequence = "100",
        double approximateTime = 1_794_000_000)
        => $"{{\"eventID\":\"e-{sequence}\",\"eventName\":\"MODIFY\",\"eventSourceARN\":\"{OrdersArn}\","
           + $"\"dynamodb\":{{\"Keys\":{{\"orderId\":{{\"S\":\"{Get(newImage, "orderId")}\"}}}},"
           + $"\"SequenceNumber\":\"{sequence}\",\"ApproximateCreationDateTime\":{approximateTime.ToString(System.Globalization.CultureInfo.InvariantCulture)},"
           + $"\"OldImage\":{Image(oldImage)},\"NewImage\":{Image(newImage)}}}}}";

    public static string Batch(params string[] records) => $"{{\"Records\":[{string.Join(",", records)}]}}";

    public static string Image(IReadOnlyDictionary<string, object> attributes)
    {
        var sb = new StringBuilder("{");
        var first = true;
        foreach (var (key, value) in attributes)
        {
            if (!first)
            {
                sb.Append(',');
            }

            first = false;
            sb.Append('"').Append(key).Append("\":");
            sb.Append(value is decimal or int or double
                ? $"{{\"N\":\"{Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)}\"}}"
                : $"{{\"S\":{JsonSerializer.Serialize(value.ToString(), AppJsonContext.Default.String)}}}");
        }

        return sb.Append('}').ToString();
    }

    private static string Get(IReadOnlyDictionary<string, object> image, string key)
        => image.TryGetValue(key, out var value) ? value.ToString() ?? string.Empty : string.Empty;

    public static Stream AsStream(string json) => new MemoryStream(Encoding.UTF8.GetBytes(json));
}
