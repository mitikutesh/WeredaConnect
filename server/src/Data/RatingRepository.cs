using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using WeredaConnect.Server.Services;

namespace WeredaConnect.Server.Data;

/// <summary>
/// DynamoDB rating repository using the low-level API for AOT compatibility.
/// Manual marshalling — no Document Model or reflection.
///
/// Table schema (Ratings):
///   PK: ratingId (STRING, "{orderId}:{rateeId}") — sole key, no sort key
///   GSI RateeIdCreatedAtIndex: rateeId + createdAt
///
/// Ratings are create-only and immutable; the deterministic ratingId + the
/// create-time <c>attribute_not_exists(ratingId)</c> condition guarantee one
/// rating per (order, ratee). (A sort key would break this: a re-submission
/// with a fresh createdAt would be a distinct item and slip past the guard.)
/// </summary>
public sealed class RatingRepository : IRatingRepository
{
    private readonly IAmazonDynamoDB _dynamoDb;
    private readonly string _tableName;

    private const string RateeIndex = "RateeIdCreatedAtIndex";

    /// <summary>Safety cap on pages walked by <see cref="GetSummaryAsync"/>.</summary>
    private const int MaxQueryPages = 50;

    public RatingRepository(IAmazonDynamoDB dynamoDb, string tableName)
    {
        _dynamoDb = dynamoDb ?? throw new ArgumentNullException(nameof(dynamoDb));
        _tableName = tableName ?? throw new ArgumentNullException(nameof(tableName));
    }

    /// <inheritdoc />
    public async Task<AuthResult<Dictionary<string, object>>> CreateAsync(
        string ratingId,
        Dictionary<string, object> data)
    {
        var now = DateTime.UtcNow.ToString("o");

        var item = new Dictionary<string, AttributeValue>
        {
            { "ratingId", new AttributeValue { S = ratingId } },
            { "createdAt", new AttributeValue { S = now } },
            { "updatedAt", new AttributeValue { S = now } },
            { "version", new AttributeValue { N = "1" } }
        };

        foreach (var kvp in data)
        {
            if (kvp.Key is "ratingId" or "version" or "createdAt" or "updatedAt")
            {
                continue;
            }

            item[kvp.Key] = DynamoDbHelper.ToAttributeValue(kvp.Value);
        }

        var request = new PutItemRequest
        {
            TableName = _tableName,
            Item = item,
            ConditionExpression = "attribute_not_exists(ratingId)"
        };

        try
        {
            await _dynamoDb.PutItemAsync(request);
            return AuthResult<Dictionary<string, object>>.Success(DynamoDbHelper.FromItem(item));
        }
        catch (ConditionalCheckFailedException)
        {
            return AuthResult<Dictionary<string, object>>.Failure(
                409, "SYNC_CONFLICT", "Rating already exists.", "ratingId");
        }
    }

    /// <inheritdoc />
    public async Task<Dictionary<string, object>?> GetByIdAsync(string ratingId)
    {
        // PK-only table → a direct GetItem on the partition key.
        var response = await _dynamoDb.GetItemAsync(new GetItemRequest
        {
            TableName = _tableName,
            Key = new Dictionary<string, AttributeValue>
            {
                { "ratingId", new AttributeValue { S = ratingId } }
            }
        });

        return response.Item is { Count: > 0 }
            ? DynamoDbHelper.FromItem(response.Item)
            : null;
    }

    /// <inheritdoc />
    public async Task<(List<Dictionary<string, object>> Items, Dictionary<string, AttributeValue>? LastEvaluatedKey)> QueryByRateeAsync(
        string rateeId,
        int limit = 20,
        Dictionary<string, AttributeValue>? exclusiveStartKey = null)
    {
        if (limit < 1 || limit > 50)
        {
            limit = 20;
        }

        var request = new QueryRequest
        {
            TableName = _tableName,
            IndexName = RateeIndex,
            KeyConditionExpression = "#ratee = :val",
            ExpressionAttributeNames = new Dictionary<string, string>
            {
                { "#ratee", "rateeId" }
            },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                { ":val", new AttributeValue { S = rateeId } }
            },
            ScanIndexForward = false, // newest first
            Limit = limit
        };

        if (exclusiveStartKey != null)
        {
            request.ExclusiveStartKey = exclusiveStartKey;
        }

        var response = await _dynamoDb.QueryAsync(request);
        var items = response.Items.Select(DynamoDbHelper.FromItem).ToList();
        return (items, response.LastEvaluatedKey.Count > 0 ? response.LastEvaluatedKey : null);
    }

    /// <inheritdoc />
    public async Task<(double Average, int Count)> GetSummaryAsync(string rateeId)
    {
        var request = new QueryRequest
        {
            TableName = _tableName,
            IndexName = RateeIndex,
            KeyConditionExpression = "#ratee = :val",
            ExpressionAttributeNames = new Dictionary<string, string>
            {
                { "#ratee", "rateeId" }
            },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                { ":val", new AttributeValue { S = rateeId } }
            }
        };

        var total = 0L;
        var count = 0;
        QueryResponse? response = null;
        var pages = 0;

        do
        {
            if (response?.LastEvaluatedKey?.Count > 0)
            {
                request.ExclusiveStartKey = response.LastEvaluatedKey;
            }

            response = await _dynamoDb.QueryAsync(request);
            foreach (var raw in response.Items)
            {
                if (raw.TryGetValue("stars", out var starsAttr) && starsAttr.N != null
                    && int.TryParse(starsAttr.N, NumberStyles.Integer, CultureInfo.InvariantCulture, out var stars))
                {
                    total += stars;
                    count++;
                }
            }

            pages++;
        }
        while (response.LastEvaluatedKey?.Count > 0 && pages < MaxQueryPages);

        var average = count > 0 ? (double)total / count : 0.0;
        return (average, count);
    }
}
