using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;

namespace WeredaConnect.Server.Data;

/// <summary>
/// DynamoDB escrow audit repository using low-level API for AOT compatibility.
/// APPEND-ONLY: Only PutItem and Query — NO UpdateItem, NO DeleteItem.
///
/// Table schema (EscrowAudit):
///   PK: escrowId (STRING)
///   SK: timestamp (STRING)
/// </summary>
public sealed class EscrowAuditRepository : IEscrowAuditRepository
{
    private readonly IAmazonDynamoDB _dynamoDb;
    private readonly string _tableName;

    public EscrowAuditRepository(IAmazonDynamoDB dynamoDb, string tableName)
    {
        _dynamoDb = dynamoDb ?? throw new ArgumentNullException(nameof(dynamoDb));
        _tableName = tableName ?? throw new ArgumentNullException(nameof(tableName));
    }

    /// <inheritdoc />
    public async Task AppendAsync(Dictionary<string, object> auditEntry)
    {
        var item = new Dictionary<string, AttributeValue>();

        foreach (var kvp in auditEntry)
        {
            if (kvp.Value == null)
            {
                continue;
            }

            item[kvp.Key] = DynamoDbHelper.ToAttributeValue(kvp.Value);
        }

        var request = new PutItemRequest
        {
            TableName = _tableName,
            Item = item
        };

        await _dynamoDb.PutItemAsync(request);
    }

    /// <inheritdoc />
    public async Task<List<Dictionary<string, object>>> GetAuditTrailAsync(string escrowId)
    {
        var allItems = new List<Dictionary<string, object>>();
        Dictionary<string, AttributeValue>? lastKey = null;

        do
        {
            var request = new QueryRequest
            {
                TableName = _tableName,
                KeyConditionExpression = "escrowId = :escrowId",
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    { ":escrowId", new AttributeValue { S = escrowId } }
                },
                ScanIndexForward = true // oldest first for audit trail
            };

            if (lastKey != null)
            {
                request.ExclusiveStartKey = lastKey;
            }

            var response = await _dynamoDb.QueryAsync(request);
            allItems.AddRange(response.Items.Select(DynamoDbHelper.FromItem));
            lastKey = response.LastEvaluatedKey.Count > 0 ? response.LastEvaluatedKey : null;
        } while (lastKey != null);

        return allItems;
    }
}
