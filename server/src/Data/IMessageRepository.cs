using Amazon.DynamoDBv2.Model;
using WeredaConnect.Server.Services;

namespace WeredaConnect.Server.Data;

/// <summary>
/// Repository interface for chat message persistence in DynamoDB.
/// Messages are create-only and immutable.
/// </summary>
public interface IMessageRepository
{
    /// <summary>
    /// Create a message. Idempotent: fails with SYNC_CONFLICT if the messageId
    /// already exists (attribute_not_exists guard), so a network retry of the
    /// same client message never writes a duplicate row.
    /// </summary>
    Task<AuthResult<Dictionary<string, object>>> CreateAsync(
        string messageId,
        Dictionary<string, object> data);

    /// <summary>Get a message by its id. Returns null if not found.</summary>
    Task<Dictionary<string, object>?> GetByIdAsync(string messageId);

    /// <summary>
    /// Query a conversation thread (newest first), paginated, via
    /// ConversationIdCreatedAtIndex.
    /// </summary>
    Task<(List<Dictionary<string, object>> Items, Dictionary<string, AttributeValue>? LastEvaluatedKey)> QueryByConversationAsync(
        string conversationId,
        int limit = 30,
        Dictionary<string, AttributeValue>? exclusiveStartKey = null);

    /// <summary>
    /// Query messages addressed to a recipient with createdAt &gt; since
    /// (oldest first), via RecipientIdCreatedAtIndex. Used by the sync-down pull.
    /// </summary>
    Task<List<Dictionary<string, object>>> QueryByRecipientSinceAsync(
        string recipientId,
        string? since,
        int limit = 100);

    /// <summary>
    /// Query messages sent by a user with createdAt &gt; since (oldest first),
    /// via SenderIdCreatedAtIndex. Used to reconstruct a user's own sent history
    /// on a fresh device / web client.
    /// </summary>
    Task<List<Dictionary<string, object>>> QueryBySenderSinceAsync(
        string senderId,
        string? since,
        int limit = 100);
}
