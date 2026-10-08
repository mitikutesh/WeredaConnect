using Amazon.DynamoDBv2.Model;

namespace WeredaConnect.Server.Data;

/// <summary>
/// Repository interface for escrow persistence in DynamoDB.
/// Supports escrow lifecycle operations: create, get, update status, and queries by various indexes.
/// </summary>
public interface IEscrowRepository
{
    /// <summary>
    /// Create a new escrow record.
    /// </summary>
    Task CreateAsync(Dictionary<string, object> escrow);

    /// <summary>
    /// Get an escrow by ID. Returns null if not found.
    /// </summary>
    Task<Dictionary<string, object>?> GetByIdAsync(string escrowId);

    /// <summary>
    /// Get an escrow by ID with a strongly consistent read when
    /// <paramref name="consistentRead"/> is true. The manual payout queue (ESC-02) uses it
    /// to decide on the row's current state after a conditional write failed or threw.
    /// </summary>
    Task<Dictionary<string, object>?> GetByIdAsync(string escrowId, bool consistentRead);

    /// <summary>
    /// Update escrow status with optimistic concurrency check on the expected current status.
    /// Throws ConditionalCheckFailedException if the current status doesn't match expectedCurrentStatus.
    /// A move to <c>released</c>, <c>refunded</c> or <c>disputed</c> also requires that the escrow
    /// has no open manual payout (ESC-02); a queued escrow settles only through TryCloseManualPayoutAsync.
    /// </summary>
    /// <param name="escrowId">The escrow partition key.</param>
    /// <param name="createdAt">The escrow sort key.</param>
    /// <param name="newStatus">The new status value.</param>
    /// <param name="expectedCurrentStatus">The expected current status for optimistic concurrency. If null, no status check is performed.</param>
    /// <param name="additionalFields">Optional extra fields to update.</param>
    Task UpdateStatusAsync(string escrowId, string createdAt, string newStatus, string? expectedCurrentStatus = null, Dictionary<string, object>? additionalFields = null);

    /// <summary>
    /// Atomically claim settlement (release or refund) for an escrow BEFORE any
    /// irreversible Telebirr call. Sets a <c>settlementLock</c> attribute conditional
    /// on no lock already existing AND the escrow being in a settleable state
    /// (escrowed/disputed) with no open manual payout (ESC-02). Returns false if another settlement already holds the
    /// lock or the escrow is no longer settleable — in which case the caller MUST NOT
    /// call Telebirr. This is the mutual-exclusion guard that prevents a release and a
    /// refund (or two releases) from both moving funds for the same escrow.
    /// </summary>
    /// <param name="op">The operation claiming the lock ("release" or "refund").</param>
    Task<bool> TryAcquireSettlementLockAsync(string escrowId, string createdAt, string op);

    /// <summary>
    /// Release a previously-acquired settlement lock so a transient failure can be
    /// retried. Called after a failed Telebirr call. No-op if the lock is absent.
    /// </summary>
    Task ReleaseSettlementLockAsync(string escrowId, string createdAt);

    /// <summary>
    /// ESC-02: put a settlement the gateway could not make into the manual payout queue.
    /// Marks the row <c>manualPayout = open</c> with the given legs, conditional on the
    /// caller holding the settlement lock for <paramref name="op"/> and no queue entry
    /// existing yet. The lock is deliberately KEPT: nothing else may settle this escrow
    /// until an admin has paid every leg. <paramref name="queuedAt"/> becomes the index sort
    /// key and the timestamp of the queued audit row. Returns false when the condition fails.
    /// </summary>
    Task<bool> TryQueueManualPayoutAsync(
        string escrowId, string createdAt, string op, string reason, string fromStatus,
        IReadOnlyList<ManualPayoutLegSeed> legs, string queuedAt);

    /// <summary>
    /// ESC-02: record an admin-paid leg that is NOT the last pending one. Conditional on
    /// the queue being open for <paramref name="op"/>, this leg still pending and
    /// <paramref name="otherLegMustBePending"/> (when given) still pending, which proves
    /// another leg remains and the escrow must stay in its current status. Returns false
    /// when the condition fails (the caller re-reads and decides again).
    /// </summary>
    Task<bool> TryMarkManualLegPaidAsync(
        string escrowId, string createdAt, string op, string leg, string externalRef,
        string paidBy, string paidAt, string? otherLegMustBePending);

    /// <summary>
    /// ESC-02: record the LAST pending leg and close the queue in one write: the leg
    /// becomes paid, <paramref name="otherLegsMustBePaid"/> are checked paid, the escrow
    /// moves to <paramref name="newStatus"/> (<c>released</c> or <c>refunded</c>) with the
    /// given settlement fields, and the <c>manualPayout</c> marker is removed. Conditional
    /// on the queue being open for <paramref name="op"/> and the status still settleable.
    /// Returns false when the condition fails.
    /// </summary>
    Task<bool> TryCloseManualPayoutAsync(
        string escrowId, string createdAt, string op, string leg, string externalRef,
        string paidBy, string paidAt, IReadOnlyList<string> otherLegsMustBePaid,
        string newStatus, Dictionary<string, object> settlementFields);

    /// <summary>
    /// ESC-02: open manual payouts, oldest first, from the sparse <c>ManualPayoutIndex</c>.
    /// </summary>
    Task<(List<Dictionary<string, object>> Items, Dictionary<string, AttributeValue>? LastEvaluatedKey)> QueryOpenManualPayoutsAsync(
        int limit, Dictionary<string, AttributeValue>? exclusiveStartKey);

    /// <summary>
    /// Get an escrow by its associated delivery ID using the DeliveryIndex GSI.
    /// </summary>
    Task<Dictionary<string, object>?> GetByDeliveryIdAsync(string deliveryId);

    /// <summary>
    /// Query escrows by their associated order ID using the sparse OrderIndex GSI,
    /// newest first. An order can carry several rows over its life (an inert
    /// refunded/cancelled row next to an active one), so this returns a small list
    /// for the caller to filter by status.
    /// </summary>
    Task<List<Dictionary<string, object>>> QueryByOrderIdAsync(string orderId, int limit = 5);

    /// <summary>
    /// Query escrows by buyer ID using the BuyerStatusIndex GSI.
    /// </summary>
    Task<(List<Dictionary<string, object>> Items, Dictionary<string, AttributeValue>? LastEvaluatedKey)> QueryByBuyerAsync(
        string buyerId, string? status, int limit, Dictionary<string, AttributeValue>? exclusiveStartKey);

    /// <summary>
    /// Query escrows by farmer ID using the FarmerStatusIndex GSI.
    /// </summary>
    Task<(List<Dictionary<string, object>> Items, Dictionary<string, AttributeValue>? LastEvaluatedKey)> QueryByFarmerAsync(
        string farmerId, string? status, int limit, Dictionary<string, AttributeValue>? exclusiveStartKey);

    /// <summary>
    /// Query escrows by trucker ID using the TruckerStatusIndex GSI (sparse: only
    /// escrows whose order was bound to a truck carry truckerId). SIM-15.
    /// </summary>
    Task<(List<Dictionary<string, object>> Items, Dictionary<string, AttributeValue>? LastEvaluatedKey)> QueryByTruckerAsync(
        string truckerId, string? status, int limit, Dictionary<string, AttributeValue>? exclusiveStartKey);

    /// <summary>
    /// Query escrows by status for cron/background processing using the StatusExpiresIndex GSI.
    /// Escrows with an open manual payout (ESC-02) are filtered out, and paging continues
    /// through filtered-empty pages, so queued rows can never fill the window.
    /// </summary>
    /// <param name="status">Status to query (e.g. "held", "pending_release").</param>
    /// <param name="expiresAtBefore">Optional upper bound for expiresAt (ISO 8601).</param>
    /// <param name="deliveryConfirmedBefore">Optional upper bound for deliveryConfirmedAt (post-query filter).</param>
    /// <param name="limit">Maximum results to return.</param>
    Task<List<Dictionary<string, object>>> QueryByStatusForCronAsync(
        string status, string? expiresAtBefore = null, string? deliveryConfirmedBefore = null, int limit = 100);
}
