namespace WeredaConnect.Server.Data;

/// <summary>
/// Repository interface for escrow audit trail persistence in DynamoDB.
/// APPEND-ONLY: supports only insert and query — no update or delete.
/// </summary>
public interface IEscrowAuditRepository
{
    /// <summary>
    /// Append a new audit entry for an escrow transaction.
    /// </summary>
    Task AppendAsync(Dictionary<string, object> auditEntry);

    /// <summary>
    /// ESC-02: append an entry whose key (<c>escrowId</c> + <c>timestamp</c>) is deterministic,
    /// only if no entry with that key exists. Returns false when it already exists, so a
    /// replay can re-ensure an audit row without writing it twice.
    /// </summary>
    Task<bool> AppendIfAbsentAsync(Dictionary<string, object> auditEntry);

    /// <summary>
    /// Get the full audit trail for an escrow, ordered by timestamp.
    /// </summary>
    Task<List<Dictionary<string, object>>> GetAuditTrailAsync(string escrowId);

    /// <summary>
    /// FUP-02: the full audit trail, read strongly consistent when <paramref name="consistentRead"/>
    /// is true, so a replay right after a write sees the row it wrote.
    /// </summary>
    Task<List<Dictionary<string, object>>> GetAuditTrailAsync(string escrowId, bool consistentRead);

    /// <summary>
    /// Get the most recent audit entries across ALL escrows (newest first),
    /// up to [limit]. Backed by a bounded table Scan for the admin audit
    /// dashboard at MVP scale; see EscrowAuditRepository for the scan cap and
    /// the GSI scale path.
    /// </summary>
    Task<List<Dictionary<string, object>>> GetRecentAcrossEscrowsAsync(int limit);

    /// <summary>
    /// FUP-02: one page of a table Scan (at most <paramref name="pageSize"/> rows evaluated)
    /// filtered to rows whose action is one of <paramref name="actions"/>. Returns the distinct
    /// escrow ids in scan order, each with the timestamp of its last matching row on the page,
    /// and the page's LastEvaluatedKey (null when the scan is done). The table has no index on
    /// the action, so the admin queue pages through the whole table.
    /// </summary>
    Task<EscrowAuditKeyPage> ScanKeysWithActionsAsync(
        IReadOnlyCollection<string> actions,
        Dictionary<string, Amazon.DynamoDBv2.Model.AttributeValue>? exclusiveStartKey,
        int pageSize);

    /// <summary>
    /// FUP-02: append an audit entry and put a claim row in another table in one transaction,
    /// each only if absent. The claim makes the entry exclusive across concurrent writers whose
    /// entries have different timestamps. Nothing is written unless both are.
    /// </summary>
    Task<AuditClaimWriteResult> AppendWithClaimAsync(
        Dictionary<string, object> auditEntry, string claimTable, Dictionary<string, object> claimItem);
}

/// <summary>FUP-02: an escrow id found on a filtered Scan page and the timestamp of its last matching row there.</summary>
public sealed record EscrowAuditKeyHit(string EscrowId, string LastTimestamp);

/// <summary>FUP-02: the escrow ids found on one filtered Scan page, and where the scan stopped.</summary>
public sealed record EscrowAuditKeyPage(
    List<EscrowAuditKeyHit> Hits,
    Dictionary<string, Amazon.DynamoDBv2.Model.AttributeValue>? LastEvaluatedKey);

/// <summary>FUP-02: outcome of <see cref="IEscrowAuditRepository.AppendWithClaimAsync"/>.</summary>
public enum AuditClaimWriteResult
{
    /// <summary>Both rows were written.</summary>
    Written,

    /// <summary>The claim row exists: another writer holds it. Nothing was written.</summary>
    ClaimTaken,

    /// <summary>An audit row already has this key (same timestamp). Nothing was written.</summary>
    EntryExists,

    /// <summary>Another transaction on the same rows was in flight. Nothing was written.</summary>
    Contended
}
