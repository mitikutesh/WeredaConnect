using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;

namespace WeredaConnect.Server.Data;

/// <summary>
/// DynamoDB repository for sync idempotency keys.
/// Uses low-level API for AOT compatibility.
///
/// Table schema (SyncMetadata):
///   PK: idempotencyKey (STRING)
///   SK: timestamp (STRING)
///   TTL: expiresAt (NUMBER) — auto-deletes after 7 days
///
/// See: specs/contracts/sync-protocol.md
/// </summary>
public sealed class SyncMetadataRepository : ISyncMetadataRepository
{
    private readonly IAmazonDynamoDB _dynamoDb;
    private readonly string _tableName;

    /// <summary>
    /// Idempotency keys expire after 7 days per sync protocol spec.
    /// </summary>
    private static readonly TimeSpan KeyTtl = TimeSpan.FromDays(7);

    public SyncMetadataRepository(IAmazonDynamoDB dynamoDb, string tableName)
    {
        _dynamoDb = dynamoDb ?? throw new ArgumentNullException(nameof(dynamoDb));
        _tableName = tableName ?? throw new ArgumentNullException(nameof(tableName));
    }

    /// <inheritdoc />
    public async Task<bool> IdempotencyKeyExistsAsync(string idempotencyKey)
    {
        var request = new QueryRequest
        {
            TableName = _tableName,
            KeyConditionExpression = "idempotencyKey = :key",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                { ":key", new AttributeValue { S = idempotencyKey } }
            },
            Limit = 1,
            Select = Select.COUNT
        };

        var response = await _dynamoDb.QueryAsync(request);
        return response.Count > 0;
    }

    /// <inheritdoc />
    public async Task StoreIdempotencyKeyAsync(
        string idempotencyKey,
        string operationId,
        string entityType,
        string result)
    {
        var now = DateTime.UtcNow;
        var expiresAt = now.Add(KeyTtl);
        var expiresAtEpoch = new DateTimeOffset(expiresAt).ToUnixTimeSeconds();

        var item = new Dictionary<string, AttributeValue>
        {
            { "idempotencyKey", new AttributeValue { S = idempotencyKey } },
            { "timestamp", new AttributeValue { S = now.ToString("o") } },
            { "operationId", new AttributeValue { S = operationId } },
            { "entityType", new AttributeValue { S = entityType } },
            { "result", new AttributeValue { S = result } },
            { "expiresAt", new AttributeValue { N = expiresAtEpoch.ToString() } }
        };

        var request = new PutItemRequest
        {
            TableName = _tableName,
            Item = item
        };

        await _dynamoDb.PutItemAsync(request);
    }
}
