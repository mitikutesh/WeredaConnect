using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using WeredaConnect.Server.Services;

namespace WeredaConnect.Server.Data;

/// <summary>
/// DynamoDB message repository using the low-level API for AOT compatibility.
/// Manual marshalling — no Document Model or reflection.
///
/// Table schema (Messages):
///   PK: messageId (STRING, client-generated UUID) — sole key, no sort key
///   GSI ConversationIdCreatedAtIndex: conversationId + createdAt
///   GSI RecipientIdCreatedAtIndex:    recipientId + createdAt
///   GSI SenderIdCreatedAtIndex:       senderId + createdAt
///
/// Messages are create-only and immutable; the client-generated messageId + the
/// create-time <c>attribute_not_exists(messageId)</c> condition make the create
/// idempotent across network retries (a re-submission of the same client message
/// is a no-op conflict, not a duplicate row).
/// </summary>
public sealed class MessageRepository : IMessageRepository
{
    private readonly IAmazonDynamoDB _dynamoDb;
    private readonly string _tableName;

    private const string ConversationIndex = "ConversationIdCreatedAtIndex";
    private const string RecipientIndex = "RecipientIdCreatedAtIndex";
    private const string SenderIndex = "SenderIdCreatedAtIndex";

    public MessageRepository(IAmazonDynamoDB dynamoDb, string tableName)
    {
        _dynamoDb = dynamoDb ?? throw new ArgumentNullException(nameof(dynamoDb));
        _tableName = tableName ?? throw new ArgumentNullException(nameof(tableName));
    }

    /// <inheritdoc />
    public async Task<AuthResult<Dictionary<string, object>>> CreateAsync(
        string messageId,
        Dictionary<string, object> data)
    {
        // createdAt is the client's compose time so offline messages sort by when
        // they were written, not when they synced. Fall back to now if missing.
        var createdAt = data.TryGetValue("createdAt", out var caObj)
                        && caObj?.ToString() is { Length: > 0 } ca
            ? ca
            : DateTime.UtcNow.ToString("o");

        var item = new Dictionary<string, AttributeValue>
        {
            { "messageId", new AttributeValue { S = messageId } },
            { "createdAt", new AttributeValue { S = createdAt } },
            { "version", new AttributeValue { N = "1" } }
        };

        foreach (var kvp in data)
        {
            if (kvp.Key is "messageId" or "version" or "createdAt")
            {
                continue;
            }

            item[kvp.Key] = DynamoDbHelper.ToAttributeValue(kvp.Value);
        }

        var request = new PutItemRequest
        {
            TableName = _tableName,
            Item = item,
            ConditionExpression = "attribute_not_exists(messageId)"
        };

        try
        {
            await _dynamoDb.PutItemAsync(request);
            return AuthResult<Dictionary<string, object>>.Success(DynamoDbHelper.FromItem(item));
        }
        catch (ConditionalCheckFailedException)
        {
            return AuthResult<Dictionary<string, object>>.Failure(
                409, "SYNC_CONFLICT", "Message already exists.", "messageId");
        }
    }

    /// <inheritdoc />
    public async Task<Dictionary<string, object>?> GetByIdAsync(string messageId)
    {
        var response = await _dynamoDb.GetItemAsync(new GetItemRequest
        {
            TableName = _tableName,
            Key = new Dictionary<string, AttributeValue>
            {
                { "messageId", new AttributeValue { S = messageId } }
            }
        });

        return response.Item is { Count: > 0 }
            ? DynamoDbHelper.FromItem(response.Item)
            : null;
    }

    /// <inheritdoc />
    public async Task<(List<Dictionary<string, object>> Items, Dictionary<string, AttributeValue>? LastEvaluatedKey)> QueryByConversationAsync(
        string conversationId,
        int limit = 30,
        Dictionary<string, AttributeValue>? exclusiveStartKey = null)
    {
        if (limit < 1 || limit > 100)
        {
            limit = 30;
        }

        var request = new QueryRequest
        {
            TableName = _tableName,
            IndexName = ConversationIndex,
            KeyConditionExpression = "#c = :val",
            ExpressionAttributeNames = new Dictionary<string, string>
            {
                { "#c", "conversationId" }
            },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                { ":val", new AttributeValue { S = conversationId } }
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
    public Task<List<Dictionary<string, object>>> QueryByRecipientSinceAsync(
        string recipientId, string? since, int limit = 100)
        => QuerySinceAsync(RecipientIndex, "recipientId", recipientId, since, limit);

    /// <inheritdoc />
    public Task<List<Dictionary<string, object>>> QueryBySenderSinceAsync(
        string senderId, string? since, int limit = 100)
        => QuerySinceAsync(SenderIndex, "senderId", senderId, since, limit);

    /// <summary>
    /// Query one participant GSI for items with createdAt &gt; since (oldest
    /// first, so the client can advance its cursor monotonically).
    /// </summary>
    private async Task<List<Dictionary<string, object>>> QuerySinceAsync(
        string indexName, string keyAttr, string keyValue, string? since, int limit)
    {
        if (limit < 1 || limit > 200)
        {
            limit = 100;
        }

        var values = new Dictionary<string, AttributeValue>
        {
            { ":val", new AttributeValue { S = keyValue } }
        };

        var keyCondition = "#k = :val";
        if (!string.IsNullOrEmpty(since))
        {
            keyCondition += " AND #ca > :since";
            values[":since"] = new AttributeValue { S = since };
        }

        var request = new QueryRequest
        {
            TableName = _tableName,
            IndexName = indexName,
            KeyConditionExpression = keyCondition,
            ExpressionAttributeNames = new Dictionary<string, string>
            {
                { "#k", keyAttr },
                { "#ca", "createdAt" }
            },
            ExpressionAttributeValues = values,
            ScanIndexForward = true, // oldest first
            Limit = limit
        };

        var response = await _dynamoDb.QueryAsync(request);
        return response.Items.Select(DynamoDbHelper.FromItem).ToList();
    }
}
