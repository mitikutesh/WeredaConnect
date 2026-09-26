using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;

namespace WeredaConnect.Server.Data;

/// <summary>
/// DynamoDB implementation of <see cref="IAuthStateRepository"/> on the
/// rate-limits table. Low-level API only (AOT safe). Every attribute is stored
/// as a string; "expiresAt" carries the epoch-seconds TTL the table expires on.
/// </summary>
public sealed class AuthStateRepository : IAuthStateRepository
{
    private const string TtlAttribute = "expiresAt";
    private static readonly HashSet<string> ReservedAttributes = new(StringComparer.Ordinal)
    {
        "pk", "sk", TtlAttribute
    };

    private readonly IAmazonDynamoDB _dynamoDb;
    private readonly string _tableName;

    public AuthStateRepository(IAmazonDynamoDB dynamoDb, string tableName)
    {
        _dynamoDb = dynamoDb ?? throw new ArgumentNullException(nameof(dynamoDb));
        _tableName = tableName ?? throw new ArgumentNullException(nameof(tableName));
    }

    public async Task<AuthStateEntry?> GetAsync(string key)
    {
        var response = await _dynamoDb.GetItemAsync(new GetItemRequest
        {
            TableName = _tableName,
            Key = KeyOf(key),
            ConsistentRead = true
        });

        return response.Item == null || response.Item.Count == 0 ? null : FromItem(key, response.Item);
    }

    public Task PutAsync(string key, IReadOnlyDictionary<string, string> attributes, int ttlSeconds)
        => _dynamoDb.PutItemAsync(new PutItemRequest
        {
            TableName = _tableName,
            Item = ToItem(key, attributes, ttlSeconds)
        });

    public async Task<bool> PutIfAbsentAsync(string key, IReadOnlyDictionary<string, string> attributes, int ttlSeconds)
    {
        try
        {
            await _dynamoDb.PutItemAsync(new PutItemRequest
            {
                TableName = _tableName,
                Item = ToItem(key, attributes, ttlSeconds),
                ConditionExpression = "attribute_not_exists(pk)"
            });
            return true;
        }
        catch (ConditionalCheckFailedException)
        {
            return false;
        }
    }

    public async Task<AuthStateEntry?> ConsumeAsync(string key)
    {
        var response = await _dynamoDb.DeleteItemAsync(new DeleteItemRequest
        {
            TableName = _tableName,
            Key = KeyOf(key),
            ReturnValues = ReturnValue.ALL_OLD
        });

        return response.Attributes == null || response.Attributes.Count == 0
            ? null
            : FromItem(key, response.Attributes);
    }

    public Task DeleteAsync(string key)
        => _dynamoDb.DeleteItemAsync(new DeleteItemRequest
        {
            TableName = _tableName,
            Key = KeyOf(key)
        });

    private static Dictionary<string, AttributeValue> KeyOf(string key) => new()
    {
        { "pk", new AttributeValue { S = key } },
        { "sk", new AttributeValue { S = key } }
    };

    private static Dictionary<string, AttributeValue> ToItem(
        string key, IReadOnlyDictionary<string, string> attributes, int ttlSeconds)
    {
        var item = KeyOf(key);
        item[TtlAttribute] = new AttributeValue
        {
            N = DateTimeOffset.UtcNow.AddSeconds(ttlSeconds).ToUnixTimeSeconds().ToString()
        };
        foreach (var (name, value) in attributes)
        {
            if (ReservedAttributes.Contains(name))
            {
                throw new ArgumentException($"Attribute name '{name}' is reserved.", nameof(attributes));
            }
            item[name] = new AttributeValue { S = value };
        }
        return item;
    }

    /// <summary>Null when the row's TTL has already elapsed (TTL deletion lags).</summary>
    private static AuthStateEntry? FromItem(string key, Dictionary<string, AttributeValue> item)
    {
        var expiresAt = item.TryGetValue(TtlAttribute, out var ttl) && long.TryParse(ttl.N, out var parsed)
            ? parsed
            : 0L;
        if (expiresAt <= DateTimeOffset.UtcNow.ToUnixTimeSeconds())
        {
            return null;
        }

        var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, value) in item)
        {
            if (!ReservedAttributes.Contains(name) && value.S != null)
            {
                attributes[name] = value.S;
            }
        }

        return new AuthStateEntry { Key = key, Attributes = attributes, ExpiresAt = expiresAt };
    }
}
