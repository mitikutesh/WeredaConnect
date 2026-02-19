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
}
