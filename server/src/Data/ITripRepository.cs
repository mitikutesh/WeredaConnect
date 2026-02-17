using Amazon.DynamoDBv2.Model;

namespace WeredaConnect.Server.Data;

/// <summary>
/// Repository interface for trip listing persistence in DynamoDB.
/// Read-only queries for the GET /trips endpoints.
/// Create/update/delete operations are handled through the sync endpoint.
/// </summary>
public interface ITripRepository
{
    /// <summary>
    /// Query trip listings with optional filters using the StatusDepartureDateIndex GSI.
    /// </summary>
    /// <param name="fromWoreda">Filter by origin woreda.</param>
    /// <param name="toWoreda">Filter by destination woreda.</param>
    /// <param name="status">Filter by status (default: open). Used as GSI partition key.</param>
    /// <param name="limit">Maximum number of results to return (1-50).</param>
    /// <param name="exclusiveStartKey">Pagination token (DynamoDB exclusive start key).</param>
    /// <returns>List of trip listings and optional pagination token.</returns>
    Task<(List<Dictionary<string, object>> Items, Dictionary<string, AttributeValue>? LastEvaluatedKey)> QueryTripsAsync(
        string? fromWoreda = null,
        string? toWoreda = null,
        string? status = null,
        int limit = 20,
        Dictionary<string, AttributeValue>? exclusiveStartKey = null);

    /// <summary>
    /// Get a trip listing by ID. Returns null if not found.
    /// </summary>
    Task<Dictionary<string, object>?> GetByIdAsync(string tripId);
}
