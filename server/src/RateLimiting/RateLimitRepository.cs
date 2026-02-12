using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;

namespace WeredaConnect.Server.RateLimiting;

/// <summary>
/// DynamoDB implementation of rate limit repository.
/// Uses low-level API for AOT compatibility (no reflection).
/// Atomic counters via UpdateItem ADD expression.
/// TTL-based auto-cleanup for expired entries.
/// </summary>
public sealed class RateLimitRepository : IRateLimitRepository
{
    private readonly IAmazonDynamoDB _dynamoDb;
    private readonly string _tableName;

    public RateLimitRepository(IAmazonDynamoDB dynamoDb, string tableName)
    {
        _dynamoDb = dynamoDb ?? throw new ArgumentNullException(nameof(dynamoDb));
        _tableName = tableName ?? throw new ArgumentNullException(nameof(tableName));
    }

    public async Task<RateLimitEntry?> GetAsync(string key)
    {
        var request = new GetItemRequest
        {
            TableName = _tableName,
            Key = new Dictionary<string, AttributeValue>
            {
                { "pk", new AttributeValue { S = key } }
            }
        };

        var response = await _dynamoDb.GetItemAsync(request);

        if (response.Item == null || response.Item.Count == 0)
            return null;

        return FromItem(response.Item);
    }

    public async Task<int> IncrementAsync(string key, int ttlSeconds)
    {
        var ttl = DateTimeOffset.UtcNow.AddSeconds(ttlSeconds).ToUnixTimeSeconds();

        var request = new UpdateItemRequest
        {
            TableName = _tableName,
            Key = new Dictionary<string, AttributeValue>
            {
                { "pk", new AttributeValue { S = key } }
            },
            UpdateExpression = "ADD #count :inc SET #ttl = :ttl",
            ExpressionAttributeNames = new Dictionary<string, string>
            {
                { "#count", "count" },
                { "#ttl", "ttl" }
            },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                { ":inc", new AttributeValue { N = "1" } },
                { ":ttl", new AttributeValue { N = ttl.ToString() } }
            },
            ReturnValues = ReturnValue.ALL_NEW
        };

        var response = await _dynamoDb.UpdateItemAsync(request);
        return int.Parse(response.Attributes["count"].N);
    }

    public async Task SetLockAsync(string key, string lockedUntil, int ttlSeconds)
    {
        var ttl = DateTimeOffset.UtcNow.AddSeconds(ttlSeconds).ToUnixTimeSeconds();

        var request = new UpdateItemRequest
        {
            TableName = _tableName,
            Key = new Dictionary<string, AttributeValue>
            {
                { "pk", new AttributeValue { S = key } }
            },
            UpdateExpression = "SET #lockedUntil = :lockedUntil, #ttl = :ttl",
            ExpressionAttributeNames = new Dictionary<string, string>
            {
                { "#lockedUntil", "lockedUntil" },
                { "#ttl", "ttl" }
            },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                { ":lockedUntil", new AttributeValue { S = lockedUntil } },
                { ":ttl", new AttributeValue { N = ttl.ToString() } }
            }
        };

        await _dynamoDb.UpdateItemAsync(request);
    }

    public async Task DeleteAsync(string key)
    {
        var request = new DeleteItemRequest
        {
            TableName = _tableName,
            Key = new Dictionary<string, AttributeValue>
            {
                { "pk", new AttributeValue { S = key } }
            }
        };

        await _dynamoDb.DeleteItemAsync(request);
    }

    internal static RateLimitEntry FromItem(Dictionary<string, AttributeValue> item)
    {
        return new RateLimitEntry
        {
            Pk = item["pk"].S,
            Count = item.TryGetValue("count", out var count) ? int.Parse(count.N) : 0,
            LockedUntil = item.TryGetValue("lockedUntil", out var locked) && locked.S != null ? locked.S : null,
            Ttl = item.TryGetValue("ttl", out var ttl) ? long.Parse(ttl.N) : 0
        };
    }
}