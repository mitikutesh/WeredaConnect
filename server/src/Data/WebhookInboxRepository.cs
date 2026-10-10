using System.Globalization;
using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using WeredaConnect.Server.Models;

namespace WeredaConnect.Server.Data;

/// <summary>
/// DynamoDB implementation of <see cref="IWebhookInboxRepository"/> over the escrow idempotency
/// table. Low-level API with manual marshalling (AOT-safe); the stored payment fields are one JSON
/// string (<c>notificationBiz</c>, source-generated).
///
/// Row: idempotencyKey, escrowId, orderId, result (= merchOrderId), status, createdAt, updatedAt,
/// expiresAt (TTL, 30 days), and while pending or exhausted: inboxReplay (GSI partition),
/// inboxReplayAt (GSI sort key), replayAttempts, lastReplayAt, notificationBiz.
/// </summary>
public sealed class WebhookInboxRepository : IWebhookInboxRepository
{
    private const int TtlDays = 30;
    private const string BizAttribute = "notificationBiz";

    private readonly IAmazonDynamoDB _dynamoDb;
    private readonly string _tableName;

    public WebhookInboxRepository(IAmazonDynamoDB dynamoDb, string tableName)
    {
        _dynamoDb = dynamoDb ?? throw new ArgumentNullException(nameof(dynamoDb));
        _tableName = tableName ?? throw new ArgumentNullException(nameof(tableName));
    }

    /// <summary>The inbox key of one verified notification.</summary>
    public static string KeyOf(string merchOrderId, string paymentOrderId, string tradeStatus)
        => $"webhook-inbox:{merchOrderId}:{paymentOrderId}:{tradeStatus}";

    /// <inheritdoc />
    public async Task<WebhookInboxRow?> GetAsync(string key)
    {
        var response = await _dynamoDb.GetItemAsync(new GetItemRequest
        {
            TableName = _tableName,
            Key = Key(key),
            ConsistentRead = true
        });

        return response.Item is { Count: > 0 } item ? FromItem(item) : null;
    }

    /// <inheritdoc />
    public async Task<bool> RecordAsync(
        string key, string escrowId, string orderId, string merchOrderId, TelebirrWebhookBiz biz, DateTime receivedAtUtc)
    {
        var item = new Dictionary<string, AttributeValue>
        {
            { "idempotencyKey", new AttributeValue { S = key } },
            { "escrowId", new AttributeValue { S = escrowId } },
            { "orderId", new AttributeValue { S = orderId } },
            { "result", new AttributeValue { S = merchOrderId } },
            { "status", new AttributeValue { S = WebhookInbox.Received } },
            { "createdAt", new AttributeValue { S = WebhookInbox.Iso(receivedAtUtc) } },
            { "expiresAt", new AttributeValue { N = ExpiresAt(receivedAtUtc) } },
            { WebhookInbox.Marker, new AttributeValue { S = WebhookInbox.Pending } },
            { WebhookInbox.ReplayAt, new AttributeValue { S = WebhookInbox.Iso(receivedAtUtc + WebhookInbox.FirstReplayDelay) } },
            { WebhookInbox.Attempts, new AttributeValue { N = "0" } },
            { BizAttribute, new AttributeValue { S = JsonSerializer.Serialize(biz, AppJsonContext.Default.TelebirrWebhookBiz) } }
        };

        try
        {
            await _dynamoDb.PutItemAsync(new PutItemRequest
            {
                TableName = _tableName,
                Item = item,
                ConditionExpression = "attribute_not_exists(idempotencyKey)"
            });
            return true;
        }
        catch (ConditionalCheckFailedException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<bool> FinalizeAsync(string key, string outcome)
    {
        try
        {
            await _dynamoDb.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = _tableName,
                Key = Key(key),
                UpdateExpression = "SET #status = :status, #updated = :now REMOVE #marker, #at, #biz",
                ConditionExpression = "attribute_exists(idempotencyKey)",
                ExpressionAttributeNames = new Dictionary<string, string>
                {
                    { "#status", "status" }, { "#updated", "updatedAt" },
                    { "#marker", WebhookInbox.Marker }, { "#at", WebhookInbox.ReplayAt }, { "#biz", BizAttribute }
                },
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    { ":status", new AttributeValue { S = outcome } },
                    { ":now", new AttributeValue { S = WebhookInbox.Iso(DateTime.UtcNow) } }
                }
            });
            return true;
        }
        catch (ConditionalCheckFailedException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<bool> MarkUnprocessedAsync(string key)
    {
        try
        {
            await _dynamoDb.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = _tableName,
                Key = Key(key),
                UpdateExpression = "SET #status = :unprocessed, #updated = :now",
                ConditionExpression = "#marker = :pending",
                ExpressionAttributeNames = new Dictionary<string, string>
                {
                    { "#status", "status" }, { "#updated", "updatedAt" }, { "#marker", WebhookInbox.Marker }
                },
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    { ":unprocessed", new AttributeValue { S = WebhookInbox.Unprocessed } },
                    { ":pending", new AttributeValue { S = WebhookInbox.Pending } },
                    { ":now", new AttributeValue { S = WebhookInbox.Iso(DateTime.UtcNow) } }
                }
            });
            return true;
        }
        catch (ConditionalCheckFailedException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public Task<(IReadOnlyList<WebhookInboxRow> Rows, Dictionary<string, AttributeValue>? Next)> QueryDueAsync(
        DateTime nowUtc, int limit, Dictionary<string, AttributeValue>? startKey)
        => QueryPartitionAsync("#marker = :pending AND #at <= :now", limit, startKey,
            new Dictionary<string, AttributeValue> { { ":now", new AttributeValue { S = WebhookInbox.Iso(nowUtc) } } });

    /// <inheritdoc />
    public Task<(IReadOnlyList<WebhookInboxRow> Rows, Dictionary<string, AttributeValue>? Next)> QueryPendingAsync(
        int limit, Dictionary<string, AttributeValue>? startKey)
        => QueryPartitionAsync("#marker = :pending", limit, startKey, new Dictionary<string, AttributeValue>());

    /// <inheritdoc />
    public async Task<bool> TryClaimAsync(
        string key, string seenReplayAt, int seenAttempts, DateTime nextReplayAtUtc, DateTime nowUtc)
    {
        try
        {
            await _dynamoDb.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = _tableName,
                Key = Key(key),
                UpdateExpression = "SET #at = :next, #attempts = :claimed, #last = :now",
                ConditionExpression = "#marker = :pending AND #at = :seenAt AND #attempts = :seenAttempts",
                ExpressionAttributeNames = new Dictionary<string, string>
                {
                    { "#marker", WebhookInbox.Marker }, { "#at", WebhookInbox.ReplayAt },
                    { "#attempts", WebhookInbox.Attempts }, { "#last", "lastReplayAt" }
                },
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    { ":pending", new AttributeValue { S = WebhookInbox.Pending } },
                    { ":seenAt", new AttributeValue { S = seenReplayAt } },
                    { ":seenAttempts", new AttributeValue { N = seenAttempts.ToString(CultureInfo.InvariantCulture) } },
                    { ":claimed", new AttributeValue { N = (seenAttempts + 1).ToString(CultureInfo.InvariantCulture) } },
                    { ":next", new AttributeValue { S = WebhookInbox.Iso(nextReplayAtUtc) } },
                    { ":now", new AttributeValue { S = WebhookInbox.Iso(nowUtc) } }
                }
            });
            return true;
        }
        catch (ConditionalCheckFailedException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<bool> ExhaustAsync(string key, int seenAttempts)
    {
        try
        {
            await _dynamoDb.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = _tableName,
                Key = Key(key),
                UpdateExpression = "SET #marker = :exhausted, #status = :unprocessed, #updated = :now",
                ConditionExpression = "#marker = :pending AND #attempts = :seenAttempts",
                ExpressionAttributeNames = new Dictionary<string, string>
                {
                    { "#marker", WebhookInbox.Marker }, { "#attempts", WebhookInbox.Attempts },
                    { "#status", "status" }, { "#updated", "updatedAt" }
                },
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    { ":pending", new AttributeValue { S = WebhookInbox.Pending } },
                    { ":exhausted", new AttributeValue { S = WebhookInbox.Exhausted } },
                    { ":unprocessed", new AttributeValue { S = WebhookInbox.Unprocessed } },
                    { ":seenAttempts", new AttributeValue { N = seenAttempts.ToString(CultureInfo.InvariantCulture) } },
                    { ":now", new AttributeValue { S = WebhookInbox.Iso(DateTime.UtcNow) } }
                }
            });
            return true;
        }
        catch (ConditionalCheckFailedException)
        {
            return false;
        }
    }

    private async Task<(IReadOnlyList<WebhookInboxRow> Rows, Dictionary<string, AttributeValue>? Next)> QueryPartitionAsync(
        string keyCondition, int limit, Dictionary<string, AttributeValue>? startKey, Dictionary<string, AttributeValue> values)
    {
        values[":pending"] = new AttributeValue { S = WebhookInbox.Pending };
        var names = new Dictionary<string, string> { { "#marker", WebhookInbox.Marker } };
        if (keyCondition.Contains("#at", StringComparison.Ordinal))
        {
            names["#at"] = WebhookInbox.ReplayAt;
        }

        var request = new QueryRequest
        {
            TableName = _tableName,
            IndexName = WebhookInbox.IndexName,
            KeyConditionExpression = keyCondition,
            ExpressionAttributeNames = names,
            ExpressionAttributeValues = values,
            Limit = Math.Clamp(limit, 1, 100),
            ScanIndexForward = true
        };
        if (startKey != null)
        {
            request.ExclusiveStartKey = startKey;
        }

        var response = await _dynamoDb.QueryAsync(request);
        var rows = (response.Items ?? new List<Dictionary<string, AttributeValue>>()).Select(FromItem).ToList();
        return (rows, response.LastEvaluatedKey is { Count: > 0 } next ? next : null);
    }

    private static WebhookInboxRow FromItem(Dictionary<string, AttributeValue> item)
    {
        TelebirrWebhookBiz? biz = null;
        if (GetS(item, BizAttribute) is { Length: > 0 } json)
        {
            try
            {
                biz = JsonSerializer.Deserialize(json, AppJsonContext.Default.TelebirrWebhookBiz);
            }
            catch (JsonException)
            {
                biz = null;
            }
        }

        return new WebhookInboxRow(
            Key: GetS(item, "idempotencyKey") ?? "",
            EscrowId: GetS(item, "escrowId") ?? "",
            Status: GetS(item, "status") ?? "",
            OrderId: GetS(item, "orderId"),
            MerchOrderId: GetS(item, "result"),
            Marker: GetS(item, WebhookInbox.Marker),
            ReplayAt: GetS(item, WebhookInbox.ReplayAt),
            Attempts: int.TryParse(GetN(item, WebhookInbox.Attempts), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0,
            CreatedAt: GetS(item, "createdAt"),
            Biz: biz);
    }

    private static string ExpiresAt(DateTime receivedAtUtc)
        => new DateTimeOffset(receivedAtUtc.ToUniversalTime(), TimeSpan.Zero).AddDays(TtlDays).ToUnixTimeSeconds()
            .ToString(CultureInfo.InvariantCulture);

    private static Dictionary<string, AttributeValue> Key(string key) => new()
    {
        { "idempotencyKey", new AttributeValue { S = key } }
    };

    private static string? GetS(Dictionary<string, AttributeValue> item, string name)
        => item.TryGetValue(name, out var value) ? value.S : null;

    private static string? GetN(Dictionary<string, AttributeValue> item, string name)
        => item.TryGetValue(name, out var value) ? value.N : null;
}
