using Amazon.DynamoDBv2.Model;
using WeredaConnect.Server.Services;

namespace WeredaConnect.Server.Data;

/// <summary>
/// Repository for buyer purchase intents in DynamoDB. Intents are synced from the
/// mobile app (entityType "buyer_intent" via /sync) so the backend can match new
/// crop listings against them for push alerts.
///
/// Table key: PK = buyerId, SK = intentId.
/// GSI "CropTypeStatusIndex" (PK = cropType) drives match-on-insert lookups.
/// </summary>
public interface IIntentRepository
{
    /// <summary>Create a new intent. Fails if (buyerId, intentId) already exists.</summary>
    Task<AuthResult<Dictionary<string, object>>> CreateAsync(
        string intentId,
        string buyerId,
        Dictionary<string, object> data);

    /// <summary>Update an existing intent with optimistic concurrency.</summary>
    Task<AuthResult<Dictionary<string, object>>> UpdateAsync(
        string intentId,
        string buyerId,
        Dictionary<string, object> data,
        int expectedVersion);

    /// <summary>Hard-delete an intent.</summary>
    Task<AuthResult<bool>> DeleteAsync(string intentId, string buyerId);

    /// <summary>Get an intent by its (buyerId, intentId) key. Null if not found.</summary>
    Task<Dictionary<string, object>?> GetByIdAsync(string intentId, string buyerId);

    /// <summary>
    /// All active intents whose wanted crop type equals <paramref name="cropType"/>.
    /// Used by the match-on-insert stream Lambda.
    /// </summary>
    Task<List<Dictionary<string, object>>> QueryActiveByCropTypeAsync(string cropType);
}
