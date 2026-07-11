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

    // The rate-limits table's DynamoDB TTL is configured on the "expiresAt"
    // attribute (infra/lib/stacks/database-stack.ts). DynamoDB does NOT allow
    // renaming a table's TTL attribute in place ("Cannot change time-to-live
    // attribute name"), so the code writes the attribute the table already
    // expires on rather than trying to flip the table to "ttl". Rows written by
    // older builds used "ttl"; FromItem reads that as a fallback so those rows
    // still self-heal and get physically cleaned up once rewritten.
    private const string TtlAttribute = "expiresAt";
    private const string LegacyTtlAttribute = "ttl";

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
                { "pk", new AttributeValue { S = key } },
                { "sk", new AttributeValue { S = key } }
            }
        };

        var response = await _dynamoDb.GetItemAsync(request);

        if (response.Item == null || response.Item.Count == 0)
        {
            return null;
        }

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
                { "pk", new AttributeValue { S = key } },
                { "sk", new AttributeValue { S = key } }
            },
            // Anchor the TTL to the FIRST request in the window (if_not_exists),
            // not the latest. Bumping it on every increment turns the intended
            // fixed window into a sliding one that never resets under sustained
            // traffic, so the counter grows unbounded. The window closes
            // ttlSeconds after it opened; the service treats a past TTL as a
            // reset (see RateLimitService.IsWindowExpired) rather than relying on
            // DynamoDB's best-effort TTL deletion.
            UpdateExpression = "ADD #count :inc SET #ttl = if_not_exists(#ttl, :ttl)",
            ExpressionAttributeNames = new Dictionary<string, string>
            {
                { "#count", "count" },
                { "#ttl", TtlAttribute }
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
                { "pk", new AttributeValue { S = key } },
                { "sk", new AttributeValue { S = key } }
            },
            UpdateExpression = "SET #lockedUntil = :lockedUntil, #ttl = :ttl",
            ExpressionAttributeNames = new Dictionary<string, string>
            {
                { "#lockedUntil", "lockedUntil" },
                { "#ttl", TtlAttribute }
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
                { "pk", new AttributeValue { S = key } },
                { "sk", new AttributeValue { S = key } }
            }
        };

        await _dynamoDb.DeleteItemAsync(request);
    }

    internal static RateLimitEntry FromItem(Dictionary<string, AttributeValue> item)
    {
        return new RateLimitEntry
        {
            Pk = item["pk"].S,
            // TryParse (not Parse) throughout: a single malformed row — e.g. an
            // attribute stored as the wrong type, so `.N` is null — must never
            // throw and turn every rate-limit check for this key into a 500 on the
            // auth/sync path. Unparseable/missing values degrade to safe defaults.
            Count = item.TryGetValue("count", out var count) && int.TryParse(count.N, out var c) ? c : 0,
            LockedUntil = item.TryGetValue("lockedUntil", out var locked) && locked.S != null ? locked.S : null,
            // Prefer the current attribute; fall back to the legacy "ttl" so rows
            // written by older builds still report their expiry and self-heal.
            Ttl = item.TryGetValue(TtlAttribute, out var ttl) && long.TryParse(ttl.N, out var expiresAt) ? expiresAt
                : item.TryGetValue(LegacyTtlAttribute, out var legacy) && long.TryParse(legacy.N, out var legacyTtl) ? legacyTtl
                : 0
        };
    }
}
