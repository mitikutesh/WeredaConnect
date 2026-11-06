using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;

namespace WeredaConnect.Server.Data;

/// <summary>
/// DynamoDB repository for sync idempotency keys.
/// Uses low-level API for AOT compatibility.
///
/// Table schema (SyncMetadata):
///   PK: idempotencyKey (STRING), caller-scoped by SyncFunction via
///       Helpers.SyncIdempotencyKey.ForCaller (always starts with "{sub}:")
///   SK: timestamp (STRING)
///   TTL: expiresAt (NUMBER) — auto-deletes after 7 days
///   payloadFingerprint (STRING, SIM-28): the operation the key was sent with;
///       absent on records written before it was stored
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

    /// <summary>The attribute that holds the operation's fingerprint (SIM-28).</summary>
    internal const string PayloadFingerprintAttribute = "payloadFingerprint";

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
    public async Task<string?> GetPayloadFingerprintAsync(string idempotencyKey)
    {
        var request = new QueryRequest
        {
            TableName = _tableName,
            KeyConditionExpression = "idempotencyKey = :key",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                { ":key", new AttributeValue { S = idempotencyKey } }
            },
            ProjectionExpression = PayloadFingerprintAttribute,
            Limit = 1
        };

        var response = await _dynamoDb.QueryAsync(request);
        return response.Items is { Count: > 0 }
               && response.Items[0].TryGetValue(PayloadFingerprintAttribute, out var fingerprint)
            ? fingerprint.S
            : null;
    }

    /// <inheritdoc />
    public async Task StoreIdempotencyKeyAsync(
        string idempotencyKey,
        string operationId,
        string entityType,
        string result,
        string? payloadFingerprint)
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
        if (!string.IsNullOrEmpty(payloadFingerprint))
        {
            item[PayloadFingerprintAttribute] = new AttributeValue { S = payloadFingerprint };
        }

        var request = new PutItemRequest
        {
            TableName = _tableName,
            Item = item
        };

        await _dynamoDb.PutItemAsync(request);
    }
}
