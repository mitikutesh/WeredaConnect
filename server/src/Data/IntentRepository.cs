using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Services;

namespace WeredaConnect.Server.Data;

/// <summary>
/// DynamoDB-backed <see cref="IIntentRepository"/>.
///
/// Table key: PK = buyerId, SK = intentId. Manual marshalling via
/// <see cref="DynamoDbHelper"/> (no ORM, AOT-safe).
/// </summary>
public class IntentRepository : IIntentRepository
{
    private readonly IAmazonDynamoDB _dynamoDb;
    private readonly string _tableName;

    private const string CropTypeStatusIndex = "CropTypeStatusIndex";

    /// <summary>
    /// Safety cap on pages walked by <see cref="QueryActiveByCropTypeAsync"/>.
    /// A filtered GSI query paginates by scanned items (1 MB/page), so a crop
    /// type with many active intents spans multiple pages — without walking them
    /// all, buyers past the first page would never be alerted. 50 pages is far
    /// above any realistic active-intent count for one crop type.
    /// </summary>
    private const int MaxQueryPages = 50;

    public IntentRepository(IAmazonDynamoDB dynamoDb, string tableName)
    {
        _dynamoDb = dynamoDb;
        _tableName = tableName;
    }

    /// <inheritdoc />
    public async Task<AuthResult<Dictionary<string, object>>> CreateAsync(
        string intentId,
        string buyerId,
        Dictionary<string, object> data)
    {
        var now = DateTime.UtcNow.ToString("o");

        var item = new Dictionary<string, AttributeValue>
        {
            { "buyerId", new AttributeValue { S = buyerId } },
            { "intentId", new AttributeValue { S = intentId } },
            { "createdAt", new AttributeValue { S = now } },
            { "updatedAt", new AttributeValue { S = now } },
            { "version", new AttributeValue { N = "1" } },
        };

        foreach (var kvp in data)
        {
            if (kvp.Key is "buyerId" or "intentId" or "version" or "createdAt" or "updatedAt")
            {
                continue;
            }

            item[kvp.Key] = DynamoDbHelper.ToAttributeValue(kvp.Value);
        }

        var request = new PutItemRequest
        {
            TableName = _tableName,
            Item = item,
            ConditionExpression = "attribute_not_exists(buyerId)",
        };

        try
        {
            await _dynamoDb.PutItemAsync(request);
            return AuthResult<Dictionary<string, object>>.Success(DynamoDbHelper.FromItem(item));
        }
        catch (ConditionalCheckFailedException)
        {
            return AuthResult<Dictionary<string, object>>.Failure(
                409, "SYNC_CONFLICT", "Intent already exists.", "intentId");
        }
    }

    /// <inheritdoc />
    public async Task<AuthResult<Dictionary<string, object>>> UpdateAsync(
        string intentId,
        string buyerId,
        Dictionary<string, object> data,
        int expectedVersion)
    {
        var now = DateTime.UtcNow.ToString("o");
        var newVersion = expectedVersion + 1;

        var setParts = new List<string> { "#updated = :updated", "#v = :newVersion" };
        var attrNames = new Dictionary<string, string>
        {
            { "#updated", "updatedAt" },
            { "#v", "version" },
        };
        var attrValues = new Dictionary<string, AttributeValue>
        {
            { ":updated", new AttributeValue { S = now } },
            { ":newVersion", new AttributeValue { N = newVersion.ToString() } },
            { ":expected", new AttributeValue { N = expectedVersion.ToString() } },
        };

        var i = 0;
        foreach (var kvp in data)
        {
            if (kvp.Key is "buyerId" or "intentId" or "version" or "createdAt" or "updatedAt")
            {
                continue;
            }

            var nameAlias = $"#f{i}";
            var valueAlias = $":v{i}";
            setParts.Add($"{nameAlias} = {valueAlias}");
            attrNames[nameAlias] = kvp.Key;
            attrValues[valueAlias] = DynamoDbHelper.ToAttributeValue(kvp.Value);
            i++;
        }

        var request = new UpdateItemRequest
        {
            TableName = _tableName,
            Key = new Dictionary<string, AttributeValue>
            {
                { "buyerId", new AttributeValue { S = buyerId } },
                { "intentId", new AttributeValue { S = intentId } },
            },
            UpdateExpression = "SET " + string.Join(", ", setParts),
            ConditionExpression = "attribute_exists(buyerId) AND #v = :expected",
            ExpressionAttributeNames = attrNames,
            ExpressionAttributeValues = attrValues,
            ReturnValues = ReturnValue.ALL_NEW,
        };

        try
        {
            var response = await _dynamoDb.UpdateItemAsync(request);
            return AuthResult<Dictionary<string, object>>.Success(
                DynamoDbHelper.FromItem(response.Attributes));
        }
        catch (ConditionalCheckFailedException)
        {
            var existing = await GetByIdAsync(intentId, buyerId);
            if (existing == null)
            {
                return AuthResult<Dictionary<string, object>>.Failure(
                    404, "SYNC_VALIDATION", "Intent not found.", "intentId");
            }

            return AuthResult<Dictionary<string, object>>.Failure(
                409, "SYNC_CONFLICT", "Intent version conflict.", "version");
        }
    }

    /// <inheritdoc />
    public async Task<AuthResult<bool>> DeleteAsync(string intentId, string buyerId)
    {
        await _dynamoDb.DeleteItemAsync(new DeleteItemRequest
        {
            TableName = _tableName,
            Key = new Dictionary<string, AttributeValue>
            {
                { "buyerId", new AttributeValue { S = buyerId } },
                { "intentId", new AttributeValue { S = intentId } },
            },
        });
        return AuthResult<bool>.Success(true);
    }

    /// <inheritdoc />
    public async Task<Dictionary<string, object>?> GetByIdAsync(string intentId, string buyerId)
    {
        var response = await _dynamoDb.GetItemAsync(new GetItemRequest
        {
            TableName = _tableName,
            Key = new Dictionary<string, AttributeValue>
            {
                { "buyerId", new AttributeValue { S = buyerId } },
                { "intentId", new AttributeValue { S = intentId } },
            },
        });

        return response.Item is { Count: > 0 } ? DynamoDbHelper.FromItem(response.Item) : null;
    }

    /// <inheritdoc />
    public async Task<List<Dictionary<string, object>>> QueryActiveByCropTypeAsync(string cropType)
    {
        var results = new List<Dictionary<string, object>>();
        Dictionary<string, AttributeValue>? exclusiveStartKey = null;
        var pages = 0;

        do
        {
            var response = await _dynamoDb.QueryAsync(new QueryRequest
            {
                TableName = _tableName,
                IndexName = CropTypeStatusIndex,
                KeyConditionExpression = "cropType = :ct",
                FilterExpression = "#status = :active",
                ExpressionAttributeNames = new Dictionary<string, string> { { "#status", "status" } },
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    { ":ct", new AttributeValue { S = cropType } },
                    { ":active", new AttributeValue { S = "active" } },
                },
                ExclusiveStartKey = exclusiveStartKey,
            });

            results.AddRange(response.Items.Select(DynamoDbHelper.FromItem));
            exclusiveStartKey = response.LastEvaluatedKey is { Count: > 0 }
                ? response.LastEvaluatedKey
                : null;
        }
        while (exclusiveStartKey != null && ++pages < MaxQueryPages);

        return results;
    }
}
