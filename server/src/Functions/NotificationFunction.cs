using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.Services;

namespace WeredaConnect.Server.Functions;

/// <summary>
/// ESC-21: <c>GET /notifications</c>, the caller's inbox (the rows <see cref="OrderNotifyStreamFunction"/>
/// writes), newest first. Read-only; nothing creates or changes a row through the API.
///
/// Query parameters:
/// - <c>limit</c>     1 to 50, default 20 (a value outside the range is clamped, a non-number is a 400)
/// - <c>nextToken</c> continues the previous page of the same query (HMAC-signed; a tampered token is a 400)
/// - <c>since</c>     the <c>cursor</c> of an earlier response; only rows written after it (minus
///                    <see cref="SinceOverlap"/>) come back, newest write first (the client's
///                    incremental pull; the client drops ids it already has)
///
/// Every response carries <c>cursor</c>, the server time the read started, for the next <c>since</c>.
/// Rows arrive out of event order (three streams in parallel, retries up to a day late), so the
/// incremental pull reads by write time, never by the event-time sort key.
///
/// Authorization: the partition key is the authenticated user's id, so a caller can only ever read
/// their own rows.
///
/// Error codes:
/// - 400: NOTIFICATION_VALIDATION   - bad limit, since or nextToken
/// - 401: NOTIFICATION_UNAUTHORIZED - not authenticated
/// - 500: NOTIFICATION_SERVER_ERROR - unexpected server failure
/// </summary>
public class NotificationFunction
{
    public const int DefaultLimit = 20;

    /// <summary>Longest <c>since</c> accepted; a cursor is an ISO time, far shorter.</summary>
    public const int MaxSinceLength = 64;

    /// <summary>
    /// How far before the client's cursor an incremental pull reaches back: a row is stamped with its
    /// write time just before the put lands, and the stream Lambdas' clocks may differ slightly.
    /// </summary>
    public static readonly TimeSpan SinceOverlap = TimeSpan.FromMinutes(2);

    private readonly INotificationRepository _notificationRepo;
    private readonly TimeProvider _clock = TimeProvider.System;

    /// <summary>Default constructor for the Lambda runtime.</summary>
    public NotificationFunction()
    {
        var notificationsTable = Environment.GetEnvironmentVariable("NOTIFICATIONS_TABLE")
                                 ?? throw new InvalidOperationException("NOTIFICATIONS_TABLE is not set.");

        var dynamoDb = new AmazonDynamoDBClient();
        _notificationRepo = new NotificationRepository(dynamoDb, notificationsTable);
    }

    /// <summary>Constructor for testing with a mocked repository.</summary>
    public NotificationFunction(INotificationRepository notificationRepo, TimeProvider? clock = null)
    {
        _notificationRepo = notificationRepo ?? throw new ArgumentNullException(nameof(notificationRepo));
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<APIGatewayProxyResponse> FunctionHandler(
        APIGatewayProxyRequest request,
        ILambdaContext context)
    {
        context.Logger.LogInformation($"NotificationFunction invoked: {request.HttpMethod} {request.Path}");

        if (request.HttpMethod != "GET")
        {
            return LambdaResponse.Error(405, "NOTIFICATION_VALIDATION", "Method not allowed.");
        }

        var authenticatedUserId = AuthHelper.GetUserId(request);
        if (string.IsNullOrEmpty(authenticatedUserId))
        {
            return LambdaResponse.Error(401, "NOTIFICATION_UNAUTHORIZED", "Authentication required.");
        }

        var queryParams = request.QueryStringParameters ?? new Dictionary<string, string>();

        var limit = DefaultLimit;
        if (queryParams.TryGetValue("limit", out var limitStr) && !string.IsNullOrEmpty(limitStr))
        {
            if (!int.TryParse(limitStr, out var requested))
            {
                return LambdaResponse.Error(400, "NOTIFICATION_VALIDATION",
                    $"Invalid limit parameter. Must be a number between 1 and {NotificationRepository.MaxPageSize}.");
            }

            limit = Math.Clamp(requested, 1, NotificationRepository.MaxPageSize);
        }

        string? writtenAfter = null;
        if (queryParams.TryGetValue("since", out var since) && !string.IsNullOrEmpty(since))
        {
            if (since.Length > MaxSinceLength || !OrderCommitment.TryParseUtc(since, out var sinceUtc))
            {
                return LambdaResponse.Error(400, "NOTIFICATION_VALIDATION", "Invalid since parameter.");
            }

            writtenAfter = OrderCommitment.Iso(sinceUtc - SinceOverlap);
        }

        Dictionary<string, AttributeValue>? exclusiveStartKey = null;
        if (queryParams.TryGetValue("nextToken", out var nextToken) && !string.IsNullOrEmpty(nextToken))
        {
            exclusiveStartKey = PaginationTokenHelper.DecodeNextToken(nextToken);
            if (exclusiveStartKey == null || !IsOwnPageKey(exclusiveStartKey, authenticatedUserId, writtenAfter))
            {
                return LambdaResponse.Error(400, "NOTIFICATION_VALIDATION", "Invalid or tampered nextToken parameter.");
            }
        }

        try
        {
            // Taken before the read: a row written while it runs is after the cursor anyway.
            var cursor = OrderCommitment.Iso(_clock.GetUtcNow().UtcDateTime);
            var (items, lastKey) = await _notificationRepo.QueryByUserAsync(
                authenticatedUserId, limit, exclusiveStartKey, writtenAfter);

            var response = new GetNotificationsResponse
            {
                Notifications = items.Select(MapToDto).ToList(),
                NextToken = lastKey != null ? PaginationTokenHelper.EncodeNextToken(lastKey) : null,
                Cursor = cursor
            };
            return LambdaResponse.Ok(response, AppJsonContext.Default.GetNotificationsResponse);
        }
        catch (Exception ex)
        {
            context.Logger.LogError($"Error querying notifications: [{ex.GetType().Name}]");
            return LambdaResponse.Error(500, "NOTIFICATION_SERVER_ERROR", "Failed to query notifications.");
        }
    }

    /// <summary>
    /// Whether a decoded token is a page key of this query on the caller's own inbox (the pagination
    /// secret is shared with other endpoints): exactly the table key, plus <c>writtenAt</c> after
    /// the window start on an incremental pull. Anything else DynamoDB would refuse with a 500.
    /// </summary>
    private static bool IsOwnPageKey(Dictionary<string, AttributeValue> key, string userId, string? writtenAfter)
    {
        if (key.Count != (writtenAfter == null ? 2 : 3)
            || !key.TryGetValue("userId", out var user) || user.S != userId
            || !key.TryGetValue("sortKey", out var sortKey) || string.IsNullOrEmpty(sortKey.S))
        {
            return false;
        }

        return writtenAfter == null
               || (key.TryGetValue("writtenAt", out var written) && written.S != null
                   && string.CompareOrdinal(written.S, writtenAfter) > 0);
    }

    /// <summary>The API shape of one inbox row; the sort key doubles as its stable id.</summary>
    internal static NotificationDto MapToDto(NotificationRow row) => new()
    {
        NotificationId = row.SortKey,
        Type = row.Type,
        Entity = row.Entity,
        EntityId = row.EntityId,
        OrderId = row.OrderId,
        Params = new Dictionary<string, string>(row.Params, StringComparer.Ordinal),
        CreatedAt = row.CreatedAt
    };
}
