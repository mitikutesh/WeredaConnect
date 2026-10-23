using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;

namespace WeredaConnect.Server.Data;

/// <summary>
/// DynamoDB transaction audit repository using the low-level API for AOT
/// compatibility. Manual marshalling — no Document Model or reflection.
/// APPEND-ONLY: only PutItem, Query, and (bounded) Scan.
///
/// Table (TransactionAudit):
///   PK: transactionId (STRING)
///   SK: timestamp (STRING, ISO-8601 → ordinal compare == chronological)
///   GSI EntityIndex: entityId (PK) + timestamp (SK)
/// </summary>
public sealed class TransactionAuditRepository : ITransactionAuditRepository
{
    private readonly IAmazonDynamoDB _dynamoDb;
    private readonly string _tableName;

    /// <summary>
    /// Upper bound on items read by the cross-transaction recent-audit Scan.
    /// Caps cost at MVP scale; migrate the global feed to a GSI (constant or
    /// date-bucketed PK + timestamp SK) if the table outgrows this.
    /// </summary>
    private const int MaxScanItems = 2000;

    public TransactionAuditRepository(IAmazonDynamoDB dynamoDb, string tableName)
    {
        _dynamoDb = dynamoDb ?? throw new ArgumentNullException(nameof(dynamoDb));
        _tableName = tableName ?? throw new ArgumentNullException(nameof(tableName));
    }

    /// <inheritdoc />
    public async Task AppendAsync(Dictionary<string, object> entry)
    {
        await _dynamoDb.PutItemAsync(new PutItemRequest
        {
            TableName = _tableName,
            Item = ToItem(entry)
        });
    }

    /// <inheritdoc />
    public async Task<bool> AppendIfAbsentAsync(Dictionary<string, object> entry)
    {
        try
        {
            await _dynamoDb.PutItemAsync(new PutItemRequest
            {
                TableName = _tableName,
                Item = ToItem(entry),
                // Still append-only: an existing row with this key is never overwritten.
                ConditionExpression = "attribute_not_exists(transactionId)"
            });
            return true;
        }
        catch (ConditionalCheckFailedException)
        {
            return false;
        }
    }

    private static Dictionary<string, AttributeValue> ToItem(Dictionary<string, object> entry)
    {
        var item = new Dictionary<string, AttributeValue>();
        foreach (var kvp in entry)
        {
            if (kvp.Value == null)
            {
                continue;
            }
            item[kvp.Key] = DynamoDbHelper.ToAttributeValue(kvp.Value);
        }

        return item;
    }

    /// <inheritdoc />
    public async Task<List<Dictionary<string, object>>> GetByTransactionAsync(string transactionId)
    {
        return await QueryAllAsync(
            keyCondition: "transactionId = :id",
            indexName: null,
            idValue: transactionId);
    }

    /// <inheritdoc />
    public async Task<List<Dictionary<string, object>>> GetByEntityAsync(string entityId)
    {
        return await QueryAllAsync(
            keyCondition: "entityId = :id",
            indexName: "EntityIndex",
            idValue: entityId);
    }

    private async Task<List<Dictionary<string, object>>> QueryAllAsync(
        string keyCondition, string? indexName, string idValue)
    {
        var allItems = new List<Dictionary<string, object>>();
        Dictionary<string, AttributeValue>? lastKey = null;

        do
        {
            var request = new QueryRequest
            {
                TableName = _tableName,
                IndexName = indexName,
                KeyConditionExpression = keyCondition,
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    { ":id", new AttributeValue { S = idValue } }
                },
                ScanIndexForward = true // oldest first
            };

            if (lastKey != null)
            {
                request.ExclusiveStartKey = lastKey;
            }

            var response = await _dynamoDb.QueryAsync(request);
            allItems.AddRange(response.Items.Select(DynamoDbHelper.FromItem));
            lastKey = response.LastEvaluatedKey is { Count: > 0 } ? response.LastEvaluatedKey : null;
        } while (lastKey != null);

        return allItems;
    }

    /// <inheritdoc />
    public async Task<List<Dictionary<string, object>>> GetRecentAsync(int limit)
    {
        // MVP-scale: no global-feed GSI, so Scan then sort newest-first in memory,
        // bounded by MaxScanItems. Mirrors EscrowAuditRepository.
        var items = new List<Dictionary<string, object>>();
        Dictionary<string, AttributeValue>? lastKey = null;
        var capped = false;

        do
        {
            var request = new ScanRequest { TableName = _tableName };
            if (lastKey != null)
            {
                request.ExclusiveStartKey = lastKey;
            }

            var response = await _dynamoDb.ScanAsync(request);
            items.AddRange(response.Items.Select(DynamoDbHelper.FromItem));
            lastKey = response.LastEvaluatedKey is { Count: > 0 } ? response.LastEvaluatedKey : null;

            if (items.Count >= MaxScanItems)
            {
                capped = true;
                break;
            }
        } while (lastKey != null);

        items.Sort((a, b) => string.CompareOrdinal(GetTimestamp(b), GetTimestamp(a)));

        if (capped)
        {
            Console.WriteLine(
                $"[TransactionAuditRepository] Recent-audit Scan hit the {MaxScanItems}-item cap; " +
                "older entries may be omitted. Migrate the global audit feed to a GSI.");
        }

        return limit < items.Count ? items.GetRange(0, limit) : items;
    }

    private static string GetTimestamp(Dictionary<string, object> item) =>
        item.TryGetValue("timestamp", out var v) ? v?.ToString() ?? string.Empty : string.Empty;
}
