using Amazon.DynamoDBv2.Model;
using WeredaConnect.Server.Services;

namespace WeredaConnect.Server.Data;

/// <summary>
/// Repository interface for rating persistence in DynamoDB.
/// Ratings are create-only and immutable (one per order+ratee).
/// </summary>
public interface IRatingRepository
{
    /// <summary>
    /// Create a new rating. Fails with SYNC_CONFLICT if the ratingId already
    /// exists (enforces one rating per order+ratee via the deterministic id).
    /// </summary>
    Task<AuthResult<Dictionary<string, object>>> CreateAsync(
        string ratingId,
        Dictionary<string, object> data);

    /// <summary>Get a rating by its id. Returns null if not found.</summary>
    Task<Dictionary<string, object>?> GetByIdAsync(string ratingId);

    /// <summary>
    /// Query ratings received by a ratee (newest first), paginated.
    /// </summary>
    Task<(List<Dictionary<string, object>> Items, Dictionary<string, AttributeValue>? LastEvaluatedKey)> QueryByRateeAsync(
        string rateeId,
        int limit = 20,
        Dictionary<string, AttributeValue>? exclusiveStartKey = null);

    /// <summary>
    /// Compute the aggregate (average + count) of all ratings for a ratee by
    /// walking every page. Used for the summary figure.
    /// </summary>
    Task<(double Average, int Count)> GetSummaryAsync(string rateeId);
}
