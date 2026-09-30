using Amazon.DynamoDBv2.Model;
using WeredaConnect.Server.Services;

namespace WeredaConnect.Server.Data;

/// <summary>
/// Repository interface for order persistence in DynamoDB.
/// Supports sync operations with version-based conflict detection.
/// </summary>
public interface IOrderRepository
{
    /// <summary>
    /// Create a new order. Fails if orderId already exists.
    /// </summary>
    Task<AuthResult<Dictionary<string, object>>> CreateAsync(
        string orderId,
        Dictionary<string, object> data);

    /// <summary>
    /// Update an existing order with optimistic concurrency.
    /// </summary>
    Task<AuthResult<Dictionary<string, object>>> UpdateAsync(
        string orderId,
        Dictionary<string, object> data,
        int expectedVersion);

    /// <summary>
    /// Get an order by ID. Returns null if not found.
    /// </summary>
    Task<Dictionary<string, object>?> GetByIdAsync(string orderId);

    /// <summary>
    /// Get an order by ID with a strongly consistent read when
    /// <paramref name="consistentRead"/> is true. Money paths that classify a payment
    /// against the order's current state use it so they never act on a stale copy.
    /// </summary>
    Task<Dictionary<string, object>?> GetByIdAsync(string orderId, bool consistentRead);

    /// <summary>
    /// ESC-01: fund an order in one conditional write. The write only succeeds while the
    /// order is still <c>payment_submitted</c>; it sets <c>status = funded</c> plus
    /// <paramref name="fields"/> and increments the version. Unlike
    /// <see cref="UpdateAsync"/> it does not depend on the caller's version, so an
    /// unrelated buyer update (payment method, reference) cannot make a verified payment
    /// fail. Returns the updated order, or null when the order is missing or no longer
    /// <c>payment_submitted</c> (the caller re-reads and classifies).
    /// </summary>
    Task<Dictionary<string, object>?> TryMarkFundedAsync(string orderId, Dictionary<string, object> fields);

    /// <summary>
    /// SIM-09: atomically record <paramref name="deliveryId"/> as the order's single active
    /// delivery. True when the slot was free or already held by this delivery (idempotent
    /// retry); false when another delivery holds it or the order does not exist.
    /// </summary>
    Task<bool> TryClaimDeliverySlotAsync(string orderId, string deliveryId);

    /// <summary>
    /// SIM-09: free the order's delivery slot when <paramref name="deliveryId"/> is cancelled
    /// so a replacement delivery can be created. No-op when another delivery holds it.
    /// </summary>
    Task ReleaseDeliverySlotAsync(string orderId, string deliveryId);

    /// <summary>
    /// Query orders with optional filters using GSIs.
    /// </summary>
    Task<(List<Dictionary<string, object>> Items, Dictionary<string, AttributeValue>? LastEvaluatedKey)> QueryOrdersAsync(
        string? buyerId = null,
        string? farmerId = null,
        string? truckerId = null,
        string? status = null,
        string? listingId = null,
        int limit = 20,
        Dictionary<string, AttributeValue>? exclusiveStartKey = null);

    /// <summary>
    /// Query orders by status for cron processing (e.g. auto-cancel expired).
    /// </summary>
    Task<List<Dictionary<string, object>>> QueryByStatusForCronAsync(string status, string? createdBefore = null);
}
