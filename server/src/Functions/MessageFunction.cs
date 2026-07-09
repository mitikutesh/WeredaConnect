using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Models;

namespace WeredaConnect.Server.Functions;

/// <summary>
/// GET /messages and GET /messages/{id} Lambda handler. Read-only access to
/// chat messages. Creates go through the /sync endpoint (message entity).
///
/// Endpoints:
/// - GET /messages?conversationId=X - a conversation thread (newest first)
/// - GET /messages?since=T          - sync-down pull of messages to/from the
///                                    caller with createdAt &gt; T (oldest first)
/// - GET /messages/{id}             - a single message by id
///
/// Authorization: a caller may only see messages they sent or received. The
/// thread read filters returned items to the caller's own messages, so guessing
/// another pair's conversationId yields nothing.
///
/// Error codes:
/// - 400: MESSAGE_VALIDATION - Invalid/missing parameters
/// - 401: UNAUTHORIZED       - Not authenticated
/// - 403: MESSAGE_FORBIDDEN  - Message not addressed to / from the caller
/// - 404: MESSAGE_NOT_FOUND  - Message does not exist
/// - 500: MESSAGE_SERVER_ERROR - Unexpected server failure
/// </summary>
public class MessageFunction
{
    private readonly IMessageRepository _messageRepo;

    /// <summary>Default constructor for the Lambda runtime.</summary>
    public MessageFunction()
    {
        var messagesTable = Environment.GetEnvironmentVariable("MESSAGES_TABLE")
                            ?? throw new InvalidOperationException("MESSAGES_TABLE is not set.");

        var dynamoDb = new AmazonDynamoDBClient();
        _messageRepo = new MessageRepository(dynamoDb, messagesTable);
    }

    /// <summary>Constructor for testing with mocked dependencies.</summary>
    public MessageFunction(IMessageRepository messageRepo)
    {
        _messageRepo = messageRepo ?? throw new ArgumentNullException(nameof(messageRepo));
    }

    public async Task<APIGatewayProxyResponse> FunctionHandler(
        APIGatewayProxyRequest request,
        ILambdaContext context)
    {
        context.Logger.LogInformation($"MessageFunction invoked: {request.HttpMethod} {request.Path}");

        return request.HttpMethod switch
        {
            "GET" when request.PathParameters?.ContainsKey("id") == true
                => await HandleGetMessageByIdAsync(request, context),
            "GET" => await HandleGetMessagesAsync(request, context),
            _ => LambdaResponse.Error(405, "MESSAGE_VALIDATION", "Method not allowed.")
        };
    }

    /// <summary>
    /// GET /messages — a conversation thread (?conversationId=) or a sync-down
    /// pull (?since=). One of the two query parameters is required.
    /// </summary>
    private async Task<APIGatewayProxyResponse> HandleGetMessagesAsync(
        APIGatewayProxyRequest request,
        ILambdaContext context)
    {
        var authenticatedUserId = AuthHelper.GetUserId(request);
        if (string.IsNullOrEmpty(authenticatedUserId))
        {
            return LambdaResponse.Error(401, "UNAUTHORIZED", "Authentication required.");
        }

        var queryParams = request.QueryStringParameters ?? new Dictionary<string, string>();

        var hasConversation = queryParams.TryGetValue("conversationId", out var conversationId)
                              && !string.IsNullOrEmpty(conversationId);
        var hasSince = queryParams.TryGetValue("since", out var since);

        try
        {
            if (hasConversation)
            {
                return await HandleThreadAsync(conversationId!, authenticatedUserId, queryParams);
            }

            if (hasSince || queryParams.ContainsKey("since"))
            {
                return await HandlePullAsync(authenticatedUserId, since);
            }

            return LambdaResponse.Error(400, "MESSAGE_VALIDATION",
                "conversationId or since query parameter is required.");
        }
        catch (Exception ex)
        {
            context.Logger.LogError($"Error querying messages: [{ex.GetType().Name}]");
            return LambdaResponse.Error(500, "MESSAGE_SERVER_ERROR", "Failed to query messages.");
        }
    }

    /// <summary>Return a conversation thread page, filtered to the caller's own messages.</summary>
    private async Task<APIGatewayProxyResponse> HandleThreadAsync(
        string conversationId,
        string authenticatedUserId,
        IDictionary<string, string> queryParams)
    {
        var limit = 30;
        if (queryParams.TryGetValue("limit", out var limitStr))
        {
            if (!int.TryParse(limitStr, out limit) || limit < 1 || limit > 100)
            {
                return LambdaResponse.Error(400, "MESSAGE_VALIDATION",
                    "Invalid limit parameter. Must be between 1 and 100.");
            }
        }

        Dictionary<string, AttributeValue>? exclusiveStartKey = null;
        if (queryParams.TryGetValue("nextToken", out var nextToken) && !string.IsNullOrEmpty(nextToken))
        {
            exclusiveStartKey = PaginationTokenHelper.DecodeNextToken(nextToken);
            if (exclusiveStartKey == null)
            {
                return LambdaResponse.Error(400, "MESSAGE_VALIDATION", "Invalid or tampered nextToken parameter.");
            }
        }

        var (items, lastKey) = await _messageRepo.QueryByConversationAsync(
            conversationId, limit, exclusiveStartKey);

        // Authorization + defense in depth: only surface messages the caller is a
        // party to. A non-participant guessing a conversationId gets an empty page.
        var messages = items
            .Select(MapToMessageDto)
            .Where(m => m.SenderId == authenticatedUserId || m.RecipientId == authenticatedUserId)
            .ToList();

        var response = new GetMessagesResponse
        {
            Messages = messages,
            NextToken = lastKey != null ? PaginationTokenHelper.EncodeNextToken(lastKey) : null
        };
        return LambdaResponse.Ok(response, AppJsonContext.Default.GetMessagesResponse);
    }

    /// <summary>Return everything to/from the caller since a timestamp (sync-down pull).</summary>
    private async Task<APIGatewayProxyResponse> HandlePullAsync(
        string authenticatedUserId,
        string? since)
    {
        var inbound = await _messageRepo.QueryByRecipientSinceAsync(authenticatedUserId, since);
        var outbound = await _messageRepo.QueryBySenderSinceAsync(authenticatedUserId, since);

        // Merge and de-duplicate by messageId (a message can't be both to and
        // from the same user, but merge defensively), oldest first so the client
        // can advance its `since` cursor to the last createdAt it received.
        var byId = new Dictionary<string, MessageDto>();
        foreach (var item in inbound.Concat(outbound))
        {
            var dto = MapToMessageDto(item);
            byId[dto.MessageId] = dto;
        }

        var messages = byId.Values
            .OrderBy(m => m.CreatedAt, StringComparer.Ordinal)
            .ToList();

        var response = new GetMessagesResponse { Messages = messages, NextToken = null };
        return LambdaResponse.Ok(response, AppJsonContext.Default.GetMessagesResponse);
    }

    /// <summary>GET /messages/{id} — a single message (caller must be a party to it).</summary>
    private async Task<APIGatewayProxyResponse> HandleGetMessageByIdAsync(
        APIGatewayProxyRequest request,
        ILambdaContext context)
    {
        if (!request.PathParameters.TryGetValue("id", out var messageId) || string.IsNullOrEmpty(messageId))
        {
            return LambdaResponse.Error(400, "MESSAGE_VALIDATION", "Missing message ID in path.");
        }

        var authenticatedUserId = AuthHelper.GetUserId(request);
        if (string.IsNullOrEmpty(authenticatedUserId))
        {
            return LambdaResponse.Error(401, "UNAUTHORIZED", "Authentication required.");
        }

        try
        {
            var message = await _messageRepo.GetByIdAsync(messageId);
            if (message == null)
            {
                return LambdaResponse.Error(404, "MESSAGE_NOT_FOUND", "Message not found.");
            }

            var dto = MapToMessageDto(message);
            if (dto.SenderId != authenticatedUserId && dto.RecipientId != authenticatedUserId)
            {
                return LambdaResponse.Error(403, "MESSAGE_FORBIDDEN",
                    "You are not a participant in this conversation.");
            }

            var response = new GetMessageResponse { Message = dto };
            return LambdaResponse.Ok(response, AppJsonContext.Default.GetMessageResponse);
        }
        catch (Exception ex)
        {
            context.Logger.LogError($"Error getting message {messageId}: [{ex.GetType().Name}]");
            return LambdaResponse.Error(500, "MESSAGE_SERVER_ERROR", "Failed to retrieve message.");
        }
    }

    /// <summary>Map a DynamoDB item dictionary to a MessageDto.</summary>
    internal static MessageDto MapToMessageDto(Dictionary<string, object> item)
    {
        return new MessageDto
        {
            MessageId = GetString(item, "messageId"),
            ConversationId = GetString(item, "conversationId"),
            ContextType = GetString(item, "contextType"),
            ContextId = GetString(item, "contextId"),
            SenderId = GetString(item, "senderId"),
            RecipientId = GetString(item, "recipientId"),
            SenderName = GetStringOrNull(item, "senderName"),
            SenderPhone = GetStringOrNull(item, "senderPhone"),
            RecipientName = GetStringOrNull(item, "recipientName"),
            RecipientPhone = GetStringOrNull(item, "recipientPhone"),
            Body = GetString(item, "body"),
            CreatedAt = GetString(item, "createdAt"),
            Version = GetInt(item, "version")
        };
    }

    private static string GetString(Dictionary<string, object> item, string key)
        => item.TryGetValue(key, out var value) ? value.ToString() ?? "" : "";

    private static string? GetStringOrNull(Dictionary<string, object> item, string key)
        => item.TryGetValue(key, out var value) ? value.ToString() : null;

    private static int GetInt(Dictionary<string, object> item, string key)
    {
        if (!item.TryGetValue(key, out var value)) return 0;
        return value switch
        {
            int i => i,
            string s => int.TryParse(s, out var result) ? result : 0,
            _ => 0
        };
    }
}
