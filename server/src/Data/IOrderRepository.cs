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
