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
    /// SIM-13: give the listing back the stock a funded order took, when that order is
    /// rejected, cancelled or refunded. Idempotent per <paramref name="orderId"/> and
    /// race-safe. Returns true only when stock was added by this call.
    /// </summary>
    Task<bool> TryRestoreStockAsync(string cropId, string orderId, double quintals);

    /// <summary>
    /// ESC-01: take a funded order's stock off the listing (clamped at 0; the listing
    /// becomes <c>reserved</c> at 0). Idempotent per <paramref name="orderId"/> through the
    /// listing's <c>stockDecrementedFor</c> set and race-safe (version CAS with retry), so
    /// a payment redelivery can safely repeat it.
    /// </summary>
    Task<StockDecrementResult> TryDecrementStockAsync(string cropId, string orderId, double quintals);

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

/// <summary>
/// Outcome of <see cref="ICropRepository.TryDecrementStockAsync"/>. <see cref="Failed"/> is the
/// zero value so an unset result never reads as success.
/// </summary>
public enum StockDecrementResult
{
    /// <summary>The quantity was not positive, or the write kept conflicting.</summary>
    Failed = 0,

    /// <summary>This call took the stock off the listing.</summary>
    Decremented,

    /// <summary>The stock was already taken for this order; nothing changed.</summary>
    AlreadyApplied,

    /// <summary>The listing does not exist.</summary>
    ListingNotFound,
}
