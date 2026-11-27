using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;

namespace WeredaConnect.Server.Data;

/// <summary>
/// ESC-21: DynamoDB-backed <see cref="INotificationRepository"/> (PK <c>userId</c>, SK <c>sortKey</c>).
/// Low-level client with manual marshalling, like every other repository (AOT: no Document Model).
/// Item: <c>userId</c>, <c>sortKey</c>, <c>type</c>, <c>entity</c>, <c>entityId</c>, <c>orderId</c>
/// (absent when unknown), <c>params</c> (map of strings), <c>createdAt</c>, <c>writtenAt</c>
/// (<see cref="WrittenAtIndex"/>, a local index), <c>expiresAt</c> (TTL, epoch seconds).
/// </summary>
public sealed class NotificationRepository : INotificationRepository
{
    /// <summary>Largest page <see cref="QueryByUserAsync"/> returns; the API clamps to the same range.</summary>
    public const int MaxPageSize = 50;

    /// <summary>Local secondary index: PK <c>userId</c>, SK <c>writtenAt</c>, all attributes.</summary>
    public const string WrittenAtIndex = "WrittenAtIndex";

    private readonly IAmazonDynamoDB _dynamoDb;
    private readonly string _tableName;

    public NotificationRepository(IAmazonDynamoDB dynamoDb, string tableName)
    {
        _dynamoDb = dynamoDb ?? throw new ArgumentNullException(nameof(dynamoDb));
        _tableName = tableName ?? throw new ArgumentNullException(nameof(tableName));
    }

    /// <inheritdoc />
    public async Task<bool> TryPutAsync(NotificationRow row)
    {
        var item = new Dictionary<string, AttributeValue>
        {
            ["userId"] = S(row.UserId),
            ["sortKey"] = S(row.SortKey),
            ["type"] = S(row.Type),
            ["entity"] = S(row.Entity),
            ["entityId"] = S(row.EntityId),
            ["params"] = new AttributeValue
            {
                M = row.Params.ToDictionary(kv => kv.Key, kv => S(kv.Value), StringComparer.Ordinal),
                IsMSet = true
            },
            ["createdAt"] = S(row.CreatedAt),
            ["writtenAt"] = S(row.WrittenAt.Length > 0 ? row.WrittenAt : row.CreatedAt),
            ["expiresAt"] = new AttributeValue { N = row.ExpiresAt.ToString(CultureInfo.InvariantCulture) }
        };

        if (!string.IsNullOrEmpty(row.OrderId))
        {
            item["orderId"] = S(row.OrderId);
        }

        try
        {
            await _dynamoDb.PutItemAsync(new PutItemRequest
            {
                TableName = _tableName,
                Item = item,
                // The sort key embeds createdAt, type and entityId, so a stream retry that derives the
                // same event writes the same key and lands here: the inbox is the idempotency record.
                ConditionExpression = "attribute_not_exists(sortKey)"
            });
            return true;
        }
        catch (ConditionalCheckFailedException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<(List<NotificationRow> Items, Dictionary<string, AttributeValue>? LastEvaluatedKey)> QueryByUserAsync(
        string userId, int limit, Dictionary<string, AttributeValue>? exclusiveStartKey = null, string? writtenAfter = null)
    {
        var request = new QueryRequest
        {
            TableName = _tableName,
            KeyConditionExpression = "userId = :u",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue> { [":u"] = S(userId) },
            // Newest first: both sort keys start with an ISO time.
            ScanIndexForward = false,
            Limit = Math.Clamp(limit, 1, MaxPageSize)
        };

        if (!string.IsNullOrEmpty(writtenAfter))
        {
            // A local index supports strongly consistent reads, so a row written before the
            // caller's cursor is never missed for lag.
            request.IndexName = WrittenAtIndex;
            request.KeyConditionExpression = "userId = :u AND writtenAt > :after";
            request.ExpressionAttributeValues[":after"] = S(writtenAfter);
            request.ConsistentRead = true;
        }

        if (exclusiveStartKey is { Count: > 0 })
        {
            request.ExclusiveStartKey = exclusiveStartKey;
        }

        var response = await _dynamoDb.QueryAsync(request);

        var items = (response.Items ?? new List<Dictionary<string, AttributeValue>>())
            .Select(FromItem)
            .ToList();
        var lastKey = response.LastEvaluatedKey is { Count: > 0 } ? response.LastEvaluatedKey : null;
        return (items, lastKey);
    }

    private static AttributeValue S(string value) => new() { S = value };

    private static string Str(Dictionary<string, AttributeValue> item, string key)
        => item.TryGetValue(key, out var v) && v.S != null ? v.S : string.Empty;

    private static NotificationRow FromItem(Dictionary<string, AttributeValue> item)
    {
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
        if (item.TryGetValue("params", out var map) && map.M != null)
        {
            foreach (var kv in map.M)
            {
                if (kv.Value.S != null)
                {
                    parameters[kv.Key] = kv.Value.S;
                }
            }
        }

        var orderId = Str(item, "orderId");
        var expiresAt = item.TryGetValue("expiresAt", out var ttl)
                        && long.TryParse(ttl.N, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds)
            ? seconds
            : 0L;

        return new NotificationRow(
            Str(item, "userId"),
            Str(item, "sortKey"),
            Str(item, "type"),
            Str(item, "entity"),
            Str(item, "entityId"),
            orderId.Length > 0 ? orderId : null,
            parameters,
            Str(item, "createdAt"),
            expiresAt,
            Str(item, "writtenAt"));
    }
}
