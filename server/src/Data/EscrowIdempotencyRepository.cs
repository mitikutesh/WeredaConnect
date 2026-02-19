using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;

namespace WeredaConnect.Server.Data;

/// <summary>
/// DynamoDB escrow idempotency repository using low-level API for AOT compatibility.
/// Prevents duplicate escrow operations via idempotency keys with 30-day TTL.
///
/// Table schema (EscrowIdempotency):
///   PK: idempotencyKey (STRING)
///   TTL: expiresAt (NUMBER — Unix epoch seconds)
/// </summary>
public sealed class EscrowIdempotencyRepository : IEscrowIdempotencyRepository
{
    private static readonly int TtlDays = 30;

    private readonly IAmazonDynamoDB _dynamoDb;
    private readonly string _tableName;

    public EscrowIdempotencyRepository(IAmazonDynamoDB dynamoDb, string tableName)
    {
        _dynamoDb = dynamoDb ?? throw new ArgumentNullException(nameof(dynamoDb));
        _tableName = tableName ?? throw new ArgumentNullException(nameof(tableName));
    }

    /// <inheritdoc />
    public async Task<Dictionary<string, object>?> GetAsync(string idempotencyKey)
    {
        var request = new GetItemRequest
        {
            TableName = _tableName,
            Key = new Dictionary<string, AttributeValue>
            {
                { "idempotencyKey", new AttributeValue { S = idempotencyKey } }
            }
        };

        var response = await _dynamoDb.GetItemAsync(request);
        return response.Item.Count > 0 ? CropRepository.FromItem(response.Item) : null;
    }

    /// <inheritdoc />
    public async Task<bool> StoreAsync(string idempotencyKey, string escrowId, string status, string? result = null)
    {
        var expiresAt = DateTimeOffset.UtcNow.AddDays(TtlDays).ToUnixTimeSeconds();
        var now = DateTime.UtcNow.ToString("o");

        var item = new Dictionary<string, AttributeValue>
        {
            { "idempotencyKey", new AttributeValue { S = idempotencyKey } },
            { "escrowId", new AttributeValue { S = escrowId } },
            { "status", new AttributeValue { S = status } },
            { "createdAt", new AttributeValue { S = now } },
            { "expiresAt", new AttributeValue { N = expiresAt.ToString() } }
        };

        if (result != null)
        {
            item["result"] = new AttributeValue { S = result };
        }

        var request = new PutItemRequest
        {
            TableName = _tableName,
            Item = item,
            ConditionExpression = "attribute_not_exists(idempotencyKey)"
        };

        try
        {
            await _dynamoDb.PutItemAsync(request);
            return true;
        }
        catch (ConditionalCheckFailedException)
        {
            return false; // Key already exists — duplicate detected
        }
    }

    /// <inheritdoc />
    public async Task UpdateAsync(string idempotencyKey, string status, string? result = null)
    {
        var now = DateTime.UtcNow.ToString("o");

        var updateParts = new List<string> { "#status = :status", "#updated = :updated" };
        var attrNames = new Dictionary<string, string>
        {
            { "#status", "status" },
            { "#updated", "updatedAt" }
        };
        var attrValues = new Dictionary<string, AttributeValue>
        {
            { ":status", new AttributeValue { S = status } },
            { ":updated", new AttributeValue { S = now } }
        };

        if (result != null)
        {
            updateParts.Add("#result = :result");
            attrNames["#result"] = "result";
            attrValues[":result"] = new AttributeValue { S = result };
        }

        var request = new UpdateItemRequest
        {
            TableName = _tableName,
            Key = new Dictionary<string, AttributeValue>
            {
                { "idempotencyKey", new AttributeValue { S = idempotencyKey } }
            },
            UpdateExpression = "SET " + string.Join(", ", updateParts),
            ExpressionAttributeNames = attrNames,
            ExpressionAttributeValues = attrValues
        };

        await _dynamoDb.UpdateItemAsync(request);
    }
}
