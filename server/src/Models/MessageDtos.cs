namespace WeredaConnect.Server.Models;

/// <summary>
/// GET /messages response: a page of messages (a conversation thread, or a
/// sync-down pull of everything addressed to / sent by the caller).
/// </summary>
public sealed record GetMessagesResponse
{
    /// <summary>The page of messages.</summary>
    public required List<MessageDto> Messages { get; init; }

    /// <summary>Pagination token for the next page (null if none).</summary>
    public string? NextToken { get; init; }
}

/// <summary>
/// GET /messages/{id} response for a single message.
/// </summary>
public sealed record GetMessageResponse
{
    /// <summary>The message details.</summary>
    public required MessageDto Message { get; init; }
}

/// <summary>
/// A single chat message. Immutable, create-only. A conversation is a
/// client-side view: messages grouped by the deterministic
/// <see cref="ConversationId"/> "{contextType}:{contextId}:{userIdLow}:{userIdHigh}".
/// Sender/recipient name + phone are denormalized authoritatively by the server
/// (from the users table) so either party can Call/Copy the other's number.
/// </summary>
public sealed record MessageDto
{
    /// <summary>Client-generated UUID; the table's partition key.</summary>
    public required string MessageId { get; init; }

    /// <summary>Deterministic conversation grouping key.</summary>
    public required string ConversationId { get; init; }

    /// <summary>Context this conversation is about: crop | order | trip | delivery.</summary>
    public required string ContextType { get; init; }

    /// <summary>Id of the context entity (cropId / orderId / tripId / deliveryId).</summary>
    public required string ContextId { get; init; }

    /// <summary>User who sent the message.</summary>
    public required string SenderId { get; init; }

    /// <summary>The counterpart the message is addressed to.</summary>
    public required string RecipientId { get; init; }

    /// <summary>Sender display name (server-denormalized).</summary>
    public string? SenderName { get; init; }

    /// <summary>Sender phone in E.164 (server-denormalized, for Call/Copy).</summary>
    public string? SenderPhone { get; init; }

    /// <summary>Recipient display name (server-denormalized).</summary>
    public string? RecipientName { get; init; }

    /// <summary>Recipient phone in E.164 (server-denormalized, for Call/Copy).</summary>
    public string? RecipientPhone { get; init; }

    /// <summary>Message text.</summary>
    public required string Body { get; init; }

    /// <summary>ISO 8601 UTC timestamp (client compose time; immutable).</summary>
    public required string CreatedAt { get; init; }

    /// <summary>Version for optimistic concurrency control (always 1).</summary>
    public required int Version { get; init; }
}
