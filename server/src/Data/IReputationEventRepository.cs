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

    /// <summary>ESC-29: one row by its key, strongly consistent; null when the user has no such row.</summary>
    Task<ReputationEvent?> GetEventAsync(string userId, string eventId);

    /// <summary>
    /// ESC-29 (reputation-api.md section 6): store the party's appeal on an active event that has
    /// none yet. <paramref name="openReview"/> is true for a row already in an admin review
    /// (<c>DELIVERY_OVERDUE</c>): its <c>appealStatus</c> and queue position stay. False when the
    /// row is missing, no longer active or already appealed (nothing written).
    /// </summary>
    Task<bool> TryAppealAsync(string userId, string eventId, string text, string? photoRef, DateTime at, bool openReview);

    /// <summary>How many rows sit on <c>AppealStatusIndex</c> with <paramref name="appealStatus"/> since before <paramref name="appealedBefore"/>.</summary>
    Task<int> CountOpenAppealsAsync(string appealStatus, string appealedBefore);

    /// <summary>
    /// ESC-32 (reputation-api.md 9.2): one page of the open appeals with
    /// <paramref name="appealStatus"/> (<c>pending</c> or <c>review</c>) from <c>AppealStatusIndex</c>,
    /// oldest <c>appealedAt</c> first. One Query, never a Scan. <c>Next</c> is null on the last page.
    /// </summary>
    Task<(IReadOnlyList<ReputationEvent> Rows, ReputationAppealCursor? Next)> QueryAppealsAsync(
        string appealStatus, int limit, ReputationAppealCursor? after);
}

/// <summary>ESC-32: where the next page of the appeals queue starts (the index key of the last row read).</summary>
public sealed record ReputationAppealCursor(string AppealedAt, string UserId, string EventId);

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

    /// <summary>ESC-32, admin actions: changes of existing rows (a decision, a lift, a replaced override), each under its own condition.</summary>
    public IReadOnlyList<ReputationRowUpdate> RowUpdates { get; init; } = Array.Empty<ReputationRowUpdate>();

    /// <summary>How many items the transaction holds.</summary>
    public int ItemCount => (NewEvent != null ? 1 : 0) + 1 + NewSystemRows.Count + (EarnBack != null ? 1 : 0)
                            + (HealAt != null ? 1 : 0) + Transitions.Count + Deletes.Count + RowUpdates.Count;
}

/// <summary>
/// ESC-32 (reputation-architecture.md 6.5): an admin's change of one existing row, written in the
/// same transaction as the users map. The condition is always the status read
/// (<see cref="FromStatus"/>), plus whatever else is set below; the row's <c>version</c> goes up by
/// one. A failed condition cancels the whole transaction (the caller re-reads and decides again),
/// so two admins can never both apply a change to the same row.
/// </summary>
public sealed record ReputationRowUpdate
{
    public required string EventId { get; init; }

    /// <summary>Condition: the row's <c>status</c> as read.</summary>
    public required string FromStatus { get; init; }

    /// <summary>Condition, when set: the row's <c>version</c> as read (an earn-back moves it).</summary>
    public int? SeenVersion { get; init; }

    /// <summary>Condition, when set: the row's <c>appealStatus</c> as read (<c>pending</c> or <c>review</c>).</summary>
    public string? RequiredAppealStatus { get; init; }

    /// <summary>
    /// Condition, when true: <c>appealTextAt</c> is still <see cref="SeenAppealTextAt"/> (absent when
    /// null). The party's appeal does not move the row's version, so an uphold checks this to never
    /// close an appeal whose text the admin did not see.
    /// </summary>
    public bool CheckAppealTextAt { get; init; }

    public string? SeenAppealTextAt { get; init; }

    /// <summary>Condition, when true: the row has no <c>until</c> (a restriction still in review).</summary>
    public bool RequireNoUntil { get; init; }

    /// <summary>The new <c>status</c>, or null to keep it.</summary>
    public string? ToStatus { get; init; }

    /// <summary>A decision: <c>excused</c> or <c>upheld</c>, with when, who and the note to the party.</summary>
    public string? AppealDecision { get; init; }

    public string? DecidedAt { get; init; }
    public string? DecidedBy { get; init; }
    public string? DecisionNote { get; init; }

    /// <summary>Remove <c>appealStatus</c> and <c>appealedAt</c>: the row leaves <c>AppealStatusIndex</c>.</summary>
    public bool CloseAppeal { get; init; }

    /// <summary>Set <c>until</c> and the <c>DueIndex</c> keys to it (the end of a reviewed restriction).</summary>
    public DateTime? SetUntil { get; init; }

    public string? LiftedAt { get; init; }
    public string? LiftedBy { get; init; }

    /// <summary>Remove <c>dueDay</c> and <c>dueAt</c>: the row will not change state again.</summary>
    public bool LeaveDueIndex { get; init; }

    /// <summary>The row as it reads after this update.</summary>
    public ReputationEvent Apply(ReputationEvent row)
    {
        var after = row with
        {
            Status = ToStatus ?? row.Status,
            AppealDecision = AppealDecision ?? row.AppealDecision,
            DecidedAt = DecidedAt ?? row.DecidedAt,
            DecidedBy = DecidedBy ?? row.DecidedBy,
            DecisionNote = DecisionNote ?? row.DecisionNote,
            LiftedAt = LiftedAt ?? row.LiftedAt,
            LiftedBy = LiftedBy ?? row.LiftedBy,
            Version = row.Version + 1
        };
        if (CloseAppeal)
        {
            after = after with { AppealStatus = null, AppealedAt = null };
        }

        if (SetUntil is { } until)
        {
            after = after with
            {
                Until = until,
                DueDay = Helpers.ReputationPolicy.Day(until),
                DueAt = Helpers.ReputationPolicy.Iso(until)
            };
        }

        if (LeaveDueIndex)
        {
            after = after with { DueDay = null, DueAt = null };
        }

        return after;
    }

    /// <summary>Whether <paramref name="row"/> (as stored now) still meets every condition.</summary>
    public bool Holds(ReputationEvent? row)
        => row != null && row.Status == FromStatus
           && (SeenVersion is not { } seen || row.Version == seen)
           && (RequiredAppealStatus == null || row.AppealStatus == RequiredAppealStatus)
           && (!CheckAppealTextAt || (row.AppealTextAt ?? string.Empty) == (SeenAppealTextAt ?? string.Empty))
           && (!RequireNoUntil || row.Until == null);
}

/// <summary>The earn-back target's update: <c>earnedBack + 1</c>, conditioned on the version read.</summary>
public sealed record ReputationEarnBackUpdate(string EventId, int SeenVersion);

/// <summary>
/// A daily-job change of one row: <c>FromStatus</c> to <c>ToStatus</c> (conditioned on the status
/// read), or, with <c>ToStatus</c> null, only leaving the <c>DueIndex</c>.
/// </summary>
public sealed record ReputationRowTransition(string EventId, string FromStatus, string? ToStatus);
