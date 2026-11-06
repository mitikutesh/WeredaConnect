using System.Globalization;
using System.Text.RegularExpressions;
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
///                                    caller with createdAt &gt; T (oldest first).
///                                    T is empty (full pull), yyyy-MM-dd, or a UTC
///                                    yyyy-MM-ddTHH:mm:ss[.fraction]Z timestamp.
///                                    T is ignored when conversationId is present.
/// - GET /messages/{id}             - a single message by id
///
/// Authorization: a caller may only see messages they sent or received. The
/// thread read filters returned items to the caller's own messages, so guessing
/// another pair's conversationId yields nothing. FUP-124: on order and delivery
/// threads the order's trucker reads only while committed to the transport
/// (<see cref="ContactReveal.MayReadThreads"/>), on all three reads.
///
/// Error codes:
/// - 400: MESSAGE_VALIDATION - Invalid/missing parameters (including a since
///                             value that is not one of the accepted forms)
/// - 401: UNAUTHORIZED       - Not authenticated
/// - 403: MESSAGE_FORBIDDEN  - Message not addressed to / from the caller
/// - 404: MESSAGE_NOT_FOUND  - Message does not exist
/// - 500: MESSAGE_SERVER_ERROR - Unexpected server failure
/// </summary>
public partial class MessageFunction
{
    private readonly IMessageRepository _messageRepo;

    // FUP-124: the trucker read rule reads the order, and a delivery thread's delivery first.
    private readonly IOrderRepository? _orderRepo;
    private readonly IDeliveryRepository? _deliveryRepo;

    /// <summary>How many order or delivery rows one request reads at a time.</summary>
    private const int ContextReadBatchSize = 8;

    // Whole-string match: `\z` (not `$`) so a trailing newline is rejected.
    // Only UTC `Z` timestamps, because the cursor is compared as text against
    // stored UTC `Z` createdAt values.
    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}(T\d{2}:\d{2}:\d{2}(\.\d{1,7})?Z)?\z")]
    private static partial Regex SinceCursorRegex();

    /// <summary>Default constructor for the Lambda runtime.</summary>
    public MessageFunction()
    {
        var messagesTable = Environment.GetEnvironmentVariable("MESSAGES_TABLE")
                            ?? throw new InvalidOperationException("MESSAGES_TABLE is not set.");

        var dynamoDb = new AmazonDynamoDBClient();
        _messageRepo = new MessageRepository(dynamoDb, messagesTable);

        // Optional so a code deploy ahead of the infra one keeps buyers and farmers reading; a
        // trucker's order or delivery message then answers 500 (see OrderRepo), never the message.
        var ordersTable = Environment.GetEnvironmentVariable("ORDERS_TABLE");
        _orderRepo = string.IsNullOrEmpty(ordersTable) ? null : new OrderRepository(dynamoDb, ordersTable);
        var deliveriesTable = Environment.GetEnvironmentVariable("DELIVERIES_TABLE");
        _deliveryRepo = string.IsNullOrEmpty(deliveriesTable) ? null : new DeliveryRepository(dynamoDb, deliveriesTable);
    }

    /// <summary>Constructor for testing with mocked dependencies.</summary>
    public MessageFunction(
        IMessageRepository messageRepo,
        IOrderRepository? orderRepo = null,
        IDeliveryRepository? deliveryRepo = null)
    {
        _messageRepo = messageRepo ?? throw new ArgumentNullException(nameof(messageRepo));
        _orderRepo = orderRepo;
        _deliveryRepo = deliveryRepo;
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
        var role = AuthHelper.GetRole(request);

        try
        {
            if (hasConversation)
            {
                return await HandleThreadAsync(conversationId!, authenticatedUserId, role, queryParams);
            }

            if (hasSince || queryParams.ContainsKey("since"))
            {
                if (!IsValidSinceCursor(since))
                {
                    return LambdaResponse.Error(400, "MESSAGE_VALIDATION",
                        "since must be an ISO 8601 UTC timestamp (e.g. 2026-07-09T10:00:00.000Z), " +
                        "a yyyy-MM-dd date, or empty for a full pull.");
                }

                return await HandlePullAsync(authenticatedUserId, role, since);
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
        string? role,
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
        var ownMessages = items
            .Select(MapToMessageDto)
            .Where(m => m.SenderId == authenticatedUserId || m.RecipientId == authenticatedUserId)
            .ToList();

        // FUP-124: a thread the caller may no longer read (a trucker who is not committed) is an
        // empty last page, as for a non-participant, so no later page is offered either.
        var messages = await FilterReadableAsync(ownMessages, authenticatedUserId, role);
        var refused = messages.Count < ownMessages.Count;

        var response = new GetMessagesResponse
        {
            Messages = messages,
            NextToken = lastKey != null && !refused ? PaginationTokenHelper.EncodeNextToken(lastKey) : null
        };
        return LambdaResponse.Ok(response, AppJsonContext.Default.GetMessagesResponse);
    }

    /// <summary>
    /// True when <paramref name="since"/> is a usable pull cursor: null or empty
    /// (full pull), a yyyy-MM-dd date, or a UTC yyyy-MM-ddTHH:mm:ss[.f{1,7}]Z
    /// timestamp that is also a real instant (rejects 2026-02-30, 25:00:00).
    /// The value is passed to the repository unchanged, so it stays
    /// byte-identical to what the client sent.
    /// </summary>
    internal static bool IsValidSinceCursor(string? since)
    {
        if (string.IsNullOrEmpty(since))
        {
            return true;
        }

        return SinceCursorRegex().IsMatch(since)
               && DateTime.TryParse(
                   since,
                   CultureInfo.InvariantCulture,
                   DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                   out _);
    }

    /// <summary>Return everything to/from the caller since a timestamp (sync-down pull).</summary>
    private async Task<APIGatewayProxyResponse> HandlePullAsync(
        string authenticatedUserId,
        string? role,
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

        // FUP-124: without the order and delivery messages the caller may no longer read.
        var readable = await FilterReadableAsync(byId.Values.ToList(), authenticatedUserId, role);
        var messages = readable
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
            // FUP-124: a trucker who is not committed to the order is no longer a participant either.
            if ((dto.SenderId != authenticatedUserId && dto.RecipientId != authenticatedUserId)
                || (await FilterReadableAsync([dto], authenticatedUserId, AuthHelper.GetRole(request))).Count == 0)
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

    /// <summary>
    /// FUP-124: <paramref name="messages"/> (the caller's own) without the order and delivery
    /// messages the caller may no longer read (<see cref="ContactReveal.MayReadThreads"/>): a
    /// trucker who declined, let the request expire, dropped out or was never asked reads none of
    /// that order's threads. A delivery thread follows the delivery's order; a delivery without an
    /// order, or an order or delivery that is gone, is closed to the trucker, as for a new message.
    /// A buyer's or a farmer's token skips the check (an order's trucker is always a trucker, since
    /// only truckers create trips), so their reads are unchanged and cost nothing more. Any other
    /// token reads each distinct delivery and order once. A failed read throws (the handler answers
    /// 500 and the client retries) instead of dropping the messages, so a pull cursor never moves
    /// past a message the caller may read.
    /// </summary>
    private async Task<List<MessageDto>> FilterReadableAsync(List<MessageDto> messages, string userId, string? role)
    {
        if (role is "buyer" or "farmer")
        {
            return messages;
        }

        var contexts = messages.Where(IsOrderThread).Select(ContextOf).Distinct().ToList();
        if (contexts.Count == 0)
        {
            return messages;
        }

        var deliveries = await LoadEachAsync(
            contexts.Where(c => c.Type == "delivery").Select(c => c.Id),
            id => DeliveryRepo().GetByIdAsync(id));
        var orderIdOfDelivery = deliveries.ToDictionary(
            d => d.Key,
            d => d.Value != null && d.Value.TryGetValue("orderId", out var orderId) ? orderId?.ToString() : null,
            StringComparer.Ordinal);

        var orders = await LoadEachAsync(
            contexts.Where(c => c.Type == "order").Select(c => c.Id)
                .Concat(orderIdOfDelivery.Values.OfType<string>().Where(id => id.Length > 0)),
            id => OrderRepo().GetByIdAsync(id));

        var readable = contexts
            .Where(c =>
            {
                var orderId = c.Type == "order" ? c.Id : orderIdOfDelivery.GetValueOrDefault(c.Id);
                return !string.IsNullOrEmpty(orderId)
                       && orders.GetValueOrDefault(orderId) is { } order
                       && ContactReveal.MayReadThreads(order, userId);
            })
            .ToHashSet();

        return messages.Where(m => !IsOrderThread(m) || readable.Contains(ContextOf(m))).ToList();
    }

    private static bool IsOrderThread(MessageDto message) => message.ContextType is "order" or "delivery";

    private static (string Type, string Id) ContextOf(MessageDto message) => (message.ContextType, message.ContextId);

    /// <summary>
    /// Reads each distinct id once, <see cref="ContextReadBatchSize"/> at a time; a missing row maps
    /// to null. The first failed read throws.
    /// </summary>
    private static async Task<Dictionary<string, Dictionary<string, object>?>> LoadEachAsync(
        IEnumerable<string> ids,
        Func<string, Task<Dictionary<string, object>?>> load)
    {
        var rows = new Dictionary<string, Dictionary<string, object>?>(StringComparer.Ordinal);
        foreach (var batch in ids.Distinct(StringComparer.Ordinal).Chunk(ContextReadBatchSize))
        {
            var loaded = await Task.WhenAll(batch.Select(load));
            for (var i = 0; i < batch.Length; i++)
            {
                rows[batch[i]] = loaded[i];
            }
        }

        return rows;
    }

    /// <summary>The orders table; unset (an infra deploy still due) is a failure, never a refusal.</summary>
    private IOrderRepository OrderRepo()
        => _orderRepo ?? throw new InvalidOperationException("ORDERS_TABLE is not set.");

    /// <summary>The deliveries table; unset (an infra deploy still due) is a failure, never a refusal.</summary>
    private IDeliveryRepository DeliveryRepo()
        => _deliveryRepo ?? throw new InvalidOperationException("DELIVERIES_TABLE is not set.");

    /// <summary>
    /// Map a DynamoDB item dictionary to a MessageDto. Phone numbers are never returned
    /// (ESC-48): rows written before it still hold them, and the parties take a number from
    /// the order only while its contact is open.
    /// </summary>
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
            RecipientName = GetStringOrNull(item, "recipientName"),
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
        if (!item.TryGetValue(key, out var value))
        {
            return 0;
        }

        return value switch
        {
            int i => i,
            string s => int.TryParse(s, out var result) ? result : 0,
            _ => 0
        };
    }
}
