using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;

namespace WeredaConnect.Server.Data;

/// <summary>
/// LT-05: DynamoDB-backed <see cref="ITripLocationRepository"/> (PK <c>tripId</c>, SK <c>sortKey</c>).
/// Low-level client with manual marshalling, like every other repository (AOT: no Document Model).
/// </summary>
public sealed class TripLocationRepository : ITripLocationRepository
{
    /// <summary>Pages read by <see cref="ListSortKeysAsync"/> before it stops (an hour holds a few dozen rows).</summary>
    private const int MaxPages = 10;

    private readonly IAmazonDynamoDB _dynamoDb;
    private readonly string _tableName;

    public TripLocationRepository(IAmazonDynamoDB dynamoDb, string tableName)
    {
        _dynamoDb = dynamoDb ?? throw new ArgumentNullException(nameof(dynamoDb));
        _tableName = tableName ?? throw new ArgumentNullException(nameof(tableName));
    }

    /// <inheritdoc />
    public async Task<bool> TryPutAsync(TripLocationRow row)
    {
        var item = new Dictionary<string, AttributeValue>
        {
            ["tripId"] = S(row.TripId),
            ["sortKey"] = S(row.SortKey),
            ["truckerId"] = S(row.TruckerId),
            ["receivedAt"] = S(row.ReceivedAt),
            ["expiresAt"] = N(row.ExpiresAt.ToString(CultureInfo.InvariantCulture))
        };

        if (row.Latitude is { } latitude)
        {
            item["latitude"] = Num(latitude);
        }

        if (row.Longitude is { } longitude)
        {
            item["longitude"] = Num(longitude);
        }

        if (row.AccuracyM is { } accuracy)
        {
            item["accuracyM"] = Num(accuracy);
        }

        if (!string.IsNullOrEmpty(row.Source))
        {
            item["source"] = S(row.Source);
        }

        if (row.Mocked is { } mocked)
        {
            item["mocked"] = new AttributeValue { BOOL = mocked };
        }

        if (!string.IsNullOrEmpty(row.PausedUntil))
        {
            item["pausedUntil"] = S(row.PausedUntil);
        }

        try
        {
            await _dynamoDb.PutItemAsync(new PutItemRequest
            {
                TableName = _tableName,
                Item = item,
                // The sort key is the capture time and the kind, so a resent ping writes the same key
                // and lands here: the first row stays, the repeat is an idempotent success.
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
    public async Task<List<string>> ListSortKeysAsync(string tripId, string sortKeyPrefix)
    {
        var keys = new List<string>();
        Dictionary<string, AttributeValue>? startKey = null;
        var pages = 0;

        do
        {
            var request = new QueryRequest
            {
                TableName = _tableName,
                KeyConditionExpression = "#t = :t AND begins_with(#sk, :prefix)",
                ExpressionAttributeNames = new Dictionary<string, string>
                {
                    ["#t"] = "tripId",
                    ["#sk"] = "sortKey"
                },
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    [":t"] = S(tripId),
                    [":prefix"] = S(sortKeyPrefix)
                },
                ProjectionExpression = "#sk",
                // A resend right after the first write must see it (the repeat check and the count).
                ConsistentRead = true
            };

            if (startKey != null)
            {
                request.ExclusiveStartKey = startKey;
            }

            var response = await _dynamoDb.QueryAsync(request);
            foreach (var item in response.Items ?? new List<Dictionary<string, AttributeValue>>())
            {
                if (item.TryGetValue("sortKey", out var sk) && !string.IsNullOrEmpty(sk.S))
                {
                    keys.Add(sk.S);
                }
            }

            startKey = response.LastEvaluatedKey is { Count: > 0 } next ? next : null;
        }
        while (startKey != null && ++pages < MaxPages);

        return keys;
    }

    private static AttributeValue S(string value) => new() { S = value };

    private static AttributeValue N(string value) => new() { N = value };

    // Fixed-point text: a tiny accuracy would otherwise print in exponent form ("1E-05").
    private static AttributeValue Num(double value) => N(value.ToString("0.##########", CultureInfo.InvariantCulture));
}
