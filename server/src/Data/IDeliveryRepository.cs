using Amazon.DynamoDBv2.Model;

namespace WeredaConnect.Server.Data;

/// <summary>
/// Repository interface for delivery persistence in DynamoDB.
/// Read-only queries for the GET /deliveries endpoints.
/// Create/update/delete operations are handled through the sync endpoint.
/// </summary>
public interface IDeliveryRepository
{
    /// <summary>
    /// Query deliveries with optional filters using GSIs.
    /// </summary>
    /// <param name="status">Filter by status (default: pending). Used as GSI partition key.</param>
    /// <param name="tripId">Filter by trip ID. Uses TripIdIndex GSI.</param>
    /// <param name="listingId">Filter by listing ID (post-query filter).</param>
    /// <param name="limit">Maximum number of results to return (1-50).</param>
    /// <param name="exclusiveStartKey">Pagination token (DynamoDB exclusive start key).</param>
    /// <returns>List of deliveries and optional pagination token.</returns>
    Task<(List<Dictionary<string, object>> Items, Dictionary<string, AttributeValue>? LastEvaluatedKey)> QueryDeliveriesAsync(
        string? status = null,
        string? tripId = null,
        string? listingId = null,
        int limit = 20,
        Dictionary<string, AttributeValue>? exclusiveStartKey = null);

    /// <summary>
    /// Get a delivery by ID. Returns null if not found.
    /// </summary>
    Task<Dictionary<string, object>?> GetByIdAsync(string deliveryId);
}
