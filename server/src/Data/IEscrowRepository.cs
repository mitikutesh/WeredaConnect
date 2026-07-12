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
    /// Update escrow status with optimistic concurrency check on the expected current status.
    /// Throws ConditionalCheckFailedException if the current status doesn't match expectedCurrentStatus.
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
    /// (escrowed/disputed). Returns false if another settlement already holds the
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
    /// Query escrows by status for cron/background processing using the StatusExpiresIndex GSI.
    /// </summary>
    /// <param name="status">Status to query (e.g. "held", "pending_release").</param>
    /// <param name="expiresAtBefore">Optional upper bound for expiresAt (ISO 8601).</param>
    /// <param name="deliveryConfirmedBefore">Optional upper bound for deliveryConfirmedAt (post-query filter).</param>
    /// <param name="limit">Maximum results to return.</param>
    Task<List<Dictionary<string, object>>> QueryByStatusForCronAsync(
        string status, string? expiresAtBefore = null, string? deliveryConfirmedBefore = null, int limit = 100);
}
