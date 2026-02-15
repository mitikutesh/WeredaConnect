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
}
