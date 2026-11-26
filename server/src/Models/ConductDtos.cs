namespace WeredaConnect.Server.Models;

/// <summary>
/// GCR-03 (reputation-api.md 1.9.0 section 1.8): one step of a party's conduct stamp. <see cref="Result"/> is
/// <c>earned</c>, <c>missed</c> or <c>left_out</c>; <see cref="Reason"/> says why a step was left out (absent otherwise).
/// </summary>
public sealed record ConductStep(string Id, string Result, string? Reason = null);

/// <summary>GCR-03: what <see cref="Helpers.ConductScoring.Evaluate"/> gives one party of one order.</summary>
public sealed record ConductResult(string Party, int Offered, int Earned, IReadOnlyList<ConductStep> Steps);

/// <summary>
/// GCR-03: the <c>HANDOFF_QUALITY</c> row of one party of one settled order in the reputation events table
/// (key <c>userId</c> = the party, <c>eventId</c> = <c>HANDOFF#{orderId}</c>, so one row per
/// <c>{orderId}:{partyId}</c>). Stored apart from the penalty rows: no reader of <see cref="ReputationEvent"/>
/// ever sees it (<c>ReputationEventRepository.QueryEventsAsync</c> leaves it out).
/// </summary>
public sealed record ConductStamp
{
    public required string UserId { get; init; }
    public required string OrderId { get; init; }

    /// <summary><c>farmer</c>, <c>trucker</c> or <c>buyer</c>: the user's role in the order.</summary>
    public required string Party { get; init; }

    /// <summary>The order's parties (the pair cap reads them). Admin-only.</summary>
    public required string FarmerId { get; init; }

    public required string BuyerId { get; init; }

    /// <summary>Absent on a self-pickup.</summary>
    public string? TruckerId { get; init; }

    public int Offered { get; init; }
    public int Earned { get; init; }
    public IReadOnlyList<ConductStep> Steps { get; init; } = Array.Empty<ConductStep>();

    /// <summary>Whether the order counts toward the party's number (section 1.8, "Which orders count").</summary>
    public bool Counted { get; init; }

    /// <summary>Why it does not count: <c>disputed</c>, <c>below_minimum</c>, <c>fraud_signal</c>, <c>fraud_unchecked</c>, <c>pair_cap</c>.</summary>
    public string? NotCountedReason { get; init; }

    public decimal? OrderTotal { get; init; }

    /// <summary>When the order settled (the order's <c>updatedAt</c> at the settle).</summary>
    public DateTime OccurredAt { get; init; }

    /// <summary>When the stamp was first written (the re-evaluation window starts here).</summary>
    public DateTime RecordedAt { get; init; }

    /// <summary>Set by the one re-evaluation a late rating or photo may cause.</summary>
    public DateTime? ReevaluatedAt { get; init; }

    public int Version { get; init; } = 1;

    /// <summary>The TTL (epoch seconds).</summary>
    public long PurgeAt { get; init; }

    public string EventId => ReputationSlots.Handoff + OrderId;

    /// <summary>The order's parties other than this row's user.</summary>
    public IEnumerable<string> Counterparties
        => new[] { FarmerId, TruckerId, BuyerId }
            .Where(id => !string.IsNullOrEmpty(id) && id != UserId)
            .Select(id => id!);
}

/// <summary>How a conduct stamp write ended.</summary>
public enum ConductWriteOutcome
{
    /// <summary>The row was written.</summary>
    Written,

    /// <summary>A replay: the row already exists (a first stamp), or was already re-evaluated or is gone (a re-evaluation).</summary>
    Exists,

    /// <summary>The user has no users row, or is being deleted: nothing written, as for every reputation writer.</summary>
    Skipped
}
