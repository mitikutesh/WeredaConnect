using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Services;

namespace WeredaConnect.Server.Data;

/// <summary>
/// DynamoDB-backed <see cref="IMatchEventRepository"/>.
///
/// Table key: PK = intentId, SK = eventId. Items carry a numeric `ttl`
/// attribute (epoch seconds) so DynamoDB expires them after
/// <see cref="RetentionDays"/> — the log is a rolling training window, not an
/// archive. Manual marshalling via <see cref="DynamoDbHelper"/> (AOT-safe).
/// </summary>
public class MatchEventRepository : IMatchEventRepository
{
    private readonly IAmazonDynamoDB _dynamoDb;
    private readonly string _tableName;

    /// <summary>Rolling retention for match events (DynamoDB TTL).</summary>
    public const int RetentionDays = 90;

    public MatchEventRepository(IAmazonDynamoDB dynamoDb, string tableName)
    {
        _dynamoDb = dynamoDb;
        _tableName = tableName;
    }

    /// <inheritdoc />
    public async Task<AuthResult<bool>> CreateAsync(
        string eventId,
        string intentId,
        string buyerId,
        Dictionary<string, object> data)
    {
        var now = DateTime.UtcNow;
        var ttl = new DateTimeOffset(now).AddDays(RetentionDays).ToUnixTimeSeconds();

        var item = new Dictionary<string, AttributeValue>
        {
            { "intentId", new AttributeValue { S = intentId } },
            { "eventId", new AttributeValue { S = eventId } },
            { "buyerId", new AttributeValue { S = buyerId } },
            { "receivedAt", new AttributeValue { S = now.ToString("o") } },
            { "ttl", new AttributeValue { N = ttl.ToString() } },
        };

        foreach (var kvp in data)
        {
            if (kvp.Key is "intentId" or "eventId" or "buyerId" or "receivedAt" or "ttl")
            {
                continue;
            }

            item[kvp.Key] = DynamoDbHelper.ToAttributeValue(kvp.Value);
        }

        try
        {
            await _dynamoDb.PutItemAsync(new PutItemRequest
            {
                TableName = _tableName,
                Item = item,
                ConditionExpression = "attribute_not_exists(intentId)",
            });
        }
        catch (ConditionalCheckFailedException)
        {
            // Append-only log: a duplicate eventId is a client retry, not a
            // conflict — succeed without overwriting the original event.
        }

        return AuthResult<bool>.Success(true);
    }
}
