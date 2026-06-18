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
    /// Get the full audit trail for an escrow, ordered by timestamp.
    /// </summary>
    Task<List<Dictionary<string, object>>> GetAuditTrailAsync(string escrowId);

    /// <summary>
    /// Get the most recent audit entries across ALL escrows (newest first),
    /// up to [limit]. Backed by a bounded table Scan for the admin audit
    /// dashboard at MVP scale; see EscrowAuditRepository for the scan cap and
    /// the GSI scale path.
    /// </summary>
    Task<List<Dictionary<string, object>>> GetRecentAcrossEscrowsAsync(int limit);
}
