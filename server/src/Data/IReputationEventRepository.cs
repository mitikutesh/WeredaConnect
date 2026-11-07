using WeredaConnect.Server.Models;

namespace WeredaConnect.Server.Data;

/// <summary>
/// ESC-28: the <c>{prefix}-reputation-events</c> table and the reputation attributes of the users
/// row (reputation-architecture.md sections 3 and 6). Every write that changes a counted attribute
/// of an event row commits in one transaction with a bump of the users row's
/// <c>reputation.version</c>, so a strongly consistent read of both is a consistent snapshot.
/// </summary>
public interface IReputationEventRepository
{
    /// <summary>The users row's account state, <c>reputation</c> map and rating sum and count (strongly consistent); null when there is no row.</summary>
    Task<ReputationUserState?> GetUserStateAsync(string userId);

    /// <summary>
    /// The user's rows, strongly consistent, all pages up to <paramref name="cap"/> rows.
    /// <c>Truncated</c> is true when more were left unread.
    /// </summary>
    Task<(IReadOnlyList<ReputationEvent> Rows, bool Truncated)> QueryEventsAsync(string userId, int cap);

    /// <summary>One <c>TransactWriteItems</c> (architecture doc 6.2): the event, the users map under its version, and the extras.</summary>
    Task<ReputationCommitOutcome> CommitAsync(ReputationWrite write);

    /// <summary>The keys due on one <c>DueIndex</c> day partition with <c>dueAt</c> at or before <paramref name="dueAtOrBefore"/> (all pages).</summary>
    Task<IReadOnlyList<(string UserId, string EventId)>> QueryDueAsync(string day, string dueAtOrBefore);

    /// <summary>How many rows sit on <c>AppealStatusIndex</c> with <paramref name="appealStatus"/> since before <paramref name="appealedBefore"/>.</summary>
    Task<int> CountOpenAppealsAsync(string appealStatus, string appealedBefore);
}

/// <summary>How a <see cref="IReputationEventRepository.CommitAsync"/> ended.</summary>
public enum ReputationCommitOutcome
{
    /// <summary>Every item was written.</summary>
    Committed,

    /// <summary>The event's slot already exists: a replay, nothing was written.</summary>
    EventExists,

    /// <summary>The users map version (or a row it read) moved: re-read and try again.</summary>
    Conflict
}

/// <summary>The <c>reputation</c> map to write on the users row.</summary>
public sealed record ReputationMapWrite
{
    public required ReputationAggregate Aggregate { get; init; }

    /// <summary>The new version (the version read + 1, or 1 for the first write).</summary>
    public required int Version { get; init; }

    public required DateTime UpdatedAt { get; init; }

    /// <summary>The newest event row written with the map (the read-lag check of 6.1).</summary>
    public string? LastEventId { get; init; }

    public DateTime? LastEventRecordedAt { get; init; }
}

/// <summary>One reputation transaction (architecture doc 6.2 and the daily job of 8.2).</summary>
public sealed record ReputationWrite
{
    public required string UserId { get; init; }

    /// <summary>The map version read; null when the row had no map (first write).</summary>
    public int? ExpectedMapVersion { get; init; }

    public required ReputationMapWrite Map { get; init; }

    /// <summary>The new event row, put with <c>attribute_not_exists(eventId)</c>.</summary>
    public ReputationEvent? NewEvent { get; init; }

    /// <summary>New system rows (a restriction, a tier change), each put with <c>attribute_not_exists(eventId)</c>.</summary>
    public IReadOnlyList<ReputationEvent> NewSystemRows { get; init; } = Array.Empty<ReputationEvent>();

    /// <summary>The earn-back target: <c>earnedBack + 1</c> under its row version and status active.</summary>
    public ReputationEarnBackUpdate? EarnBack { get; init; }

    /// <summary>When set, upsert the day's <c>HEAL#</c> marker for the daily job.</summary>
    public DateTime? HealAt { get; init; }

    /// <summary>Daily job: status changes, each also leaving the <c>DueIndex</c>.</summary>
    public IReadOnlyList<ReputationRowTransition> Transitions { get; init; } = Array.Empty<ReputationRowTransition>();

    /// <summary>Daily job: rows to delete (the <c>HEAL#</c> markers).</summary>
    public IReadOnlyList<string> Deletes { get; init; } = Array.Empty<string>();

    /// <summary>How many items the transaction holds.</summary>
    public int ItemCount => (NewEvent != null ? 1 : 0) + 1 + NewSystemRows.Count + (EarnBack != null ? 1 : 0)
                            + (HealAt != null ? 1 : 0) + Transitions.Count + Deletes.Count;
}

/// <summary>The earn-back target's update: <c>earnedBack + 1</c>, conditioned on the version read.</summary>
public sealed record ReputationEarnBackUpdate(string EventId, int SeenVersion);

/// <summary>
/// A daily-job change of one row: <c>FromStatus</c> to <c>ToStatus</c> (conditioned on the status
/// read), or, with <c>ToStatus</c> null, only leaving the <c>DueIndex</c>.
/// </summary>
public sealed record ReputationRowTransition(string EventId, string FromStatus, string? ToStatus);
