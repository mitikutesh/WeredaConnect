using WeredaConnect.Server.Models;

namespace WeredaConnect.Server.Data;

/// <summary>
/// GCR-03 (reputation-api.md 1.9.0 section 1.8): the <c>HANDOFF_QUALITY</c> rows of the reputation events table.
/// Written apart from the reputation transaction: a stamp changes no aggregate, so the users row's
/// <c>reputation</c> map and its version are never written; the users row is only checked (it exists and is not
/// being deleted) in the same transaction as the row.
/// </summary>
public interface IConductStampRepository
{
    /// <summary>The user's stamps (the <c>HANDOFF#</c> slots only), strongly consistent, up to <paramref name="cap"/> rows.</summary>
    Task<IReadOnlyList<ConductStamp>> QueryStampsAsync(string userId, int cap);

    /// <summary>
    /// GCR-04 (reputation-api.md 1.10.0 section 1.9): the user's counted stamps whose <c>occurredAt</c> is after
    /// <paramref name="sinceUtc"/>, for the read-time figure. Eventually consistent (a read path; nothing is written
    /// after it), up to <paramref name="cap"/> rows; <c>Truncated</c> when more remained.
    /// </summary>
    Task<(IReadOnlyList<ConductStamp> Rows, bool Truncated)> QueryCountedStampsAsync(string userId, DateTime sinceUtc, int cap);

    /// <summary>One stamp, strongly consistent; null when there is none.</summary>
    Task<ConductStamp?> GetStampAsync(string userId, string orderId);

    /// <summary>The first stamp: written only when the slot is free (<see cref="ConductWriteOutcome.Exists"/> otherwise).</summary>
    Task<ConductWriteOutcome> PutNewAsync(ConductStamp stamp);

    /// <summary>
    /// The one re-evaluation: replaces the stamp only while it exists and carries no <c>reevaluatedAt</c>
    /// (<see cref="ConductWriteOutcome.Exists"/> otherwise), so a second late arrival writes nothing.
    /// </summary>
    Task<ConductWriteOutcome> ReplaceOnceAsync(ConductStamp stamp);
}
