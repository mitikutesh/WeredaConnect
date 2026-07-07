using Amazon.DynamoDBv2.Model;
using WeredaConnect.Server.Services;

namespace WeredaConnect.Server.Data;

/// <summary>
/// Repository interface for crop listing persistence in DynamoDB.
/// Supports sync operations with version-based conflict detection.
/// </summary>
public interface ICropRepository
{
    /// <summary>
    /// Create a new crop listing. Fails if cropId already exists.
    /// </summary>
    Task<AuthResult<Dictionary<string, object>>> CreateAsync(
        string cropId,
        Dictionary<string, object> data);

    /// <summary>
    /// Update an existing crop listing with optimistic concurrency.
    /// </summary>
    Task<AuthResult<Dictionary<string, object>>> UpdateAsync(
        string cropId,
        Dictionary<string, object> data,
        int expectedVersion);

    /// <summary>
    /// Soft-delete a crop listing (set deletedAt timestamp).
    /// </summary>
    Task<AuthResult<Dictionary<string, object>>> DeleteAsync(
        string cropId,
        int expectedVersion);

    /// <summary>
    /// Get a crop listing by ID. Returns null if not found.
    /// </summary>
    Task<Dictionary<string, object>?> GetByIdAsync(string cropId);

    /// <summary>
    /// Query crop listings with optional filters.
    /// </summary>
    /// <param name="farmerId">Filter by farmer ID.</param>
    /// <param name="woreda">Filter by woreda (administrative district).</param>
    /// <param name="cropType">Filter by crop type.</param>
    /// <param name="status">Filter by status.</param>
    /// <param name="limit">Maximum number of results to return (1-100).</param>
    /// <param name="exclusiveStartKey">Pagination token (DynamoDB exclusive start key).</param>
    /// <param name="maxPricePerKg">
    /// Price ceiling pushed into the crop-type GSI query (pricePerKg is in the
    /// index projection), so over-budget crops are skipped before the full-item
    /// fetch and each candidate page holds only price-eligible listings. Only
    /// honoured on the cropType query path (the matching path).
    /// </param>
    /// <returns>List of crop listings and optional pagination token.</returns>
    Task<(List<Dictionary<string, object>> Items, Dictionary<string, AttributeValue>? LastEvaluatedKey)> QueryCropsAsync(
        string? farmerId = null,
        string? woreda = null,
        string? cropType = null,
        string? status = null,
        int limit = 20,
        Dictionary<string, AttributeValue>? exclusiveStartKey = null,
        double? maxPricePerKg = null);
}
