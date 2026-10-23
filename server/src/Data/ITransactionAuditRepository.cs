namespace WeredaConnect.Server.Data;

/// <summary>
/// Append-only, cross-entity transaction audit trail. Records order, delivery,
/// and admin-action lifecycle transitions (the legs that previously had no audit)
/// so the admin monitor can show a whole transaction's who/what/when/where.
/// The escrow leg keeps its dedicated <see cref="IEscrowAuditRepository"/>.
///
/// Table (TransactionAudit): PK transactionId (= orderId anchor, or the entity id
/// when the order is unknown), SK timestamp. GSI EntityIndex: entityId + timestamp.
/// APPEND-ONLY — writes and queries only, never update/delete.
/// </summary>
public interface ITransactionAuditRepository
{
    /// <summary>Append one immutable audit entry.</summary>
    Task AppendAsync(Dictionary<string, object> entry);

    /// <summary>
    /// ESC-47: append an entry whose key (<c>transactionId</c> + <c>timestamp</c>) is deterministic,
    /// only if no entry with that key exists. Returns false when it already exists, so a repair
    /// can re-ensure an audit row without writing it twice.
    /// </summary>
    Task<bool> AppendIfAbsentAsync(Dictionary<string, object> entry);

    /// <summary>All events for a transaction (PK), oldest first.</summary>
    Task<List<Dictionary<string, object>>> GetByTransactionAsync(string transactionId);

    /// <summary>All events for a single entity (orderId/deliveryId) via EntityIndex, oldest first.</summary>
    Task<List<Dictionary<string, object>>> GetByEntityAsync(string entityId);

    /// <summary>Recent events across all transactions (newest first) for the admin audit log.</summary>
    Task<List<Dictionary<string, object>>> GetRecentAsync(int limit);
}
