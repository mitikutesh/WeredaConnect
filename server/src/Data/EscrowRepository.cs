using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;

namespace WeredaConnect.Server.Data;

/// <summary>
/// DynamoDB escrow repository using low-level API for AOT compatibility.
/// Manual marshalling — no Document Model or reflection.
///
/// Table schema (Escrows):
///   PK: escrowId (STRING)
///   SK: createdAt (STRING)
///   GSI DeliveryIndex: deliveryId (PK) + createdAt (SK)
///   GSI BuyerStatusIndex: buyerId (PK) + createdAt (SK)
///   GSI FarmerStatusIndex: farmerId (PK) + createdAt (SK)
///   GSI StatusExpiresIndex: status (PK) + expiresAt (SK)
/// </summary>
public sealed class EscrowRepository : IEscrowRepository
{
    private readonly IAmazonDynamoDB _dynamoDb;
    private readonly string _tableName;

    public EscrowRepository(IAmazonDynamoDB dynamoDb, string tableName)
    {
        _dynamoDb = dynamoDb ?? throw new ArgumentNullException(nameof(dynamoDb));
        _tableName = tableName ?? throw new ArgumentNullException(nameof(tableName));
    }

    /// <inheritdoc />
    public async Task CreateAsync(Dictionary<string, object> escrow)
    {
        var item = new Dictionary<string, AttributeValue>();

        foreach (var kvp in escrow)
        {
            if (kvp.Value == null)
            {
                continue; // skip nulls
            }

            item[kvp.Key] = CropRepository.ToAttributeValue(kvp.Value);
        }

        var request = new PutItemRequest
        {
            TableName = _tableName,
            Item = item,
            ConditionExpression = "attribute_not_exists(escrowId)"
        };

        await _dynamoDb.PutItemAsync(request);
    }

    /// <inheritdoc />
    public async Task<Dictionary<string, object>?> GetByIdAsync(string escrowId)
    {
        var item = await GetItemAsync(escrowId);
        return item != null ? CropRepository.FromItem(item) : null;
    }

    /// <inheritdoc />
    public async Task UpdateStatusAsync(string escrowId, string createdAt, string newStatus, string? expectedCurrentStatus = null, Dictionary<string, object>? additionalFields = null)
    {
        var now = DateTime.UtcNow.ToString("o");

        var updateParts = new List<string> { "#status = :newStatus", "#updated = :updated" };
        var attrNames = new Dictionary<string, string>
        {
            { "#status", "status" },
            { "#updated", "updatedAt" }
        };
        var attrValues = new Dictionary<string, AttributeValue>
        {
            { ":newStatus", new AttributeValue { S = newStatus } },
            { ":updated", new AttributeValue { S = now } }
        };

        if (additionalFields != null)
        {
            var i = 0;
            foreach (var kvp in additionalFields)
            {
                if (kvp.Key is "escrowId" or "createdAt" or "status" or "updatedAt")
                {
                    continue;
                }

                if (kvp.Value == null)
                {
                    continue;
                }

                var nameAlias = $"#f{i}";
                var valueAlias = $":f{i}";
                attrNames[nameAlias] = kvp.Key;
                attrValues[valueAlias] = CropRepository.ToAttributeValue(kvp.Value);
                updateParts.Add($"{nameAlias} = {valueAlias}");
                i++;
            }
        }

        var request = new UpdateItemRequest
        {
            TableName = _tableName,
            Key = new Dictionary<string, AttributeValue>
            {
                { "escrowId", new AttributeValue { S = escrowId } },
                { "createdAt", new AttributeValue { S = createdAt } }
            },
            UpdateExpression = "SET " + string.Join(", ", updateParts),
            ExpressionAttributeNames = attrNames,
            ExpressionAttributeValues = attrValues
        };

        // Optimistic concurrency: verify expected current status before update
        if (!string.IsNullOrEmpty(expectedCurrentStatus))
        {
            request.ConditionExpression = "#status = :expectedStatus";
            request.ExpressionAttributeValues[":expectedStatus"] = new AttributeValue { S = expectedCurrentStatus };
        }

        await _dynamoDb.UpdateItemAsync(request);
    }

    /// <inheritdoc />
    public async Task<bool> TryAcquireSettlementLockAsync(string escrowId, string createdAt, string op)
    {
        var now = DateTime.UtcNow.ToString("o");
        var request = new UpdateItemRequest
        {
            TableName = _tableName,
            Key = new Dictionary<string, AttributeValue>
            {
                { "escrowId", new AttributeValue { S = escrowId } },
                { "createdAt", new AttributeValue { S = createdAt } }
            },
            UpdateExpression = "SET settlementLock = :op, #updated = :now",
            // Acquire only if no settlement is already in progress AND the escrow is
            // still in a settleable state. This is the atomic guard that runs BEFORE
            // any Telebirr call, so only the winner ever moves funds.
            ConditionExpression =
                "attribute_not_exists(settlementLock) AND (#status = :escrowed OR #status = :disputed)",
            ExpressionAttributeNames = new Dictionary<string, string>
            {
                { "#status", "status" },
                { "#updated", "updatedAt" }
            },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                { ":op", new AttributeValue { S = op } },
                { ":now", new AttributeValue { S = now } },
                { ":escrowed", new AttributeValue { S = "escrowed" } },
                { ":disputed", new AttributeValue { S = "disputed" } }
            }
        };

        try
        {
            await _dynamoDb.UpdateItemAsync(request);
            return true;
        }
        catch (ConditionalCheckFailedException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public async Task ReleaseSettlementLockAsync(string escrowId, string createdAt)
    {
        var now = DateTime.UtcNow.ToString("o");
        var request = new UpdateItemRequest
        {
            TableName = _tableName,
            Key = new Dictionary<string, AttributeValue>
            {
                { "escrowId", new AttributeValue { S = escrowId } },
                { "createdAt", new AttributeValue { S = createdAt } }
            },
            UpdateExpression = "REMOVE settlementLock SET #updated = :now",
            ExpressionAttributeNames = new Dictionary<string, string>
            {
                { "#updated", "updatedAt" }
            },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                { ":now", new AttributeValue { S = now } }
            }
        };

        await _dynamoDb.UpdateItemAsync(request);
    }

    /// <inheritdoc />
    public async Task<Dictionary<string, object>?> GetByDeliveryIdAsync(string deliveryId)
    {
        var request = new QueryRequest
        {
            TableName = _tableName,
            IndexName = "DeliveryIndex",
            KeyConditionExpression = "#deliveryId = :deliveryId",
            ExpressionAttributeNames = new Dictionary<string, string>
            {
                { "#deliveryId", "deliveryId" }
            },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                { ":deliveryId", new AttributeValue { S = deliveryId } }
            },
            Limit = 1,
            ScanIndexForward = false
        };

        var response = await _dynamoDb.QueryAsync(request);
        return response.Items.Count > 0 ? CropRepository.FromItem(response.Items[0]) : null;
    }

    /// <inheritdoc />
    public async Task<List<Dictionary<string, object>>> QueryByOrderIdAsync(string orderId, int limit = 5)
    {
        var request = new QueryRequest
        {
            TableName = _tableName,
            IndexName = "OrderIndex",
            KeyConditionExpression = "#orderId = :orderId",
            ExpressionAttributeNames = new Dictionary<string, string>
            {
                { "#orderId", "orderId" }
            },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                { ":orderId", new AttributeValue { S = orderId } }
            },
            Limit = limit,
            ScanIndexForward = false
        };

        var response = await _dynamoDb.QueryAsync(request);
        return response.Items.Select(CropRepository.FromItem).ToList();
    }

    /// <inheritdoc />
    public async Task<(List<Dictionary<string, object>> Items, Dictionary<string, AttributeValue>? LastEvaluatedKey)> QueryByBuyerAsync(
        string buyerId, string? status, int limit, Dictionary<string, AttributeValue>? exclusiveStartKey)
    {
        if (limit < 1 || limit > 50)
        {
            limit = 20;
        }

        var request = new QueryRequest
        {
            TableName = _tableName,
            IndexName = "BuyerStatusIndex",
            KeyConditionExpression = "#buyerId = :buyerId",
            ExpressionAttributeNames = new Dictionary<string, string>
            {
                { "#buyerId", "buyerId" }
            },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                { ":buyerId", new AttributeValue { S = buyerId } }
            },
            Limit = limit,
            ScanIndexForward = false
        };

        if (exclusiveStartKey != null)
        {
            request.ExclusiveStartKey = exclusiveStartKey;
        }

        if (!string.IsNullOrEmpty(status))
        {
            request.FilterExpression = "#status = :status";
            request.ExpressionAttributeNames["#status"] = "status";
            request.ExpressionAttributeValues[":status"] = new AttributeValue { S = status };
        }

        var response = await _dynamoDb.QueryAsync(request);
        var items = response.Items.Select(CropRepository.FromItem).ToList();
        return (items, response.LastEvaluatedKey.Count > 0 ? response.LastEvaluatedKey : null);
    }

    /// <inheritdoc />
    public async Task<(List<Dictionary<string, object>> Items, Dictionary<string, AttributeValue>? LastEvaluatedKey)> QueryByFarmerAsync(
        string farmerId, string? status, int limit, Dictionary<string, AttributeValue>? exclusiveStartKey)
    {
        if (limit < 1 || limit > 50)
        {
            limit = 20;
        }

        var request = new QueryRequest
        {
            TableName = _tableName,
            IndexName = "FarmerStatusIndex",
            KeyConditionExpression = "#farmerId = :farmerId",
            ExpressionAttributeNames = new Dictionary<string, string>
            {
                { "#farmerId", "farmerId" }
            },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                { ":farmerId", new AttributeValue { S = farmerId } }
            },
            Limit = limit,
            ScanIndexForward = false
        };

        if (exclusiveStartKey != null)
        {
            request.ExclusiveStartKey = exclusiveStartKey;
        }

        if (!string.IsNullOrEmpty(status))
        {
            request.FilterExpression = "#status = :status";
            request.ExpressionAttributeNames["#status"] = "status";
            request.ExpressionAttributeValues[":status"] = new AttributeValue { S = status };
        }

        var response = await _dynamoDb.QueryAsync(request);
        var items = response.Items.Select(CropRepository.FromItem).ToList();
        return (items, response.LastEvaluatedKey.Count > 0 ? response.LastEvaluatedKey : null);
    }

    /// <inheritdoc />
    public async Task<List<Dictionary<string, object>>> QueryByStatusForCronAsync(
        string status, string? expiresAtBefore = null, string? deliveryConfirmedBefore = null, int limit = 100)
    {
        if (limit < 1 || limit > 500)
        {
            limit = 100;
        }

        var keyCondition = "#status = :status";
        var attrNames = new Dictionary<string, string>
        {
            { "#status", "status" }
        };
        var attrValues = new Dictionary<string, AttributeValue>
        {
            { ":status", new AttributeValue { S = status } }
        };

        // Use expiresAt sort key condition on the GSI when provided
        if (!string.IsNullOrEmpty(expiresAtBefore))
        {
            keyCondition += " AND #expiresAt <= :expiresAtBefore";
            attrNames["#expiresAt"] = "expiresAt";
            attrValues[":expiresAtBefore"] = new AttributeValue { S = expiresAtBefore };
        }

        var request = new QueryRequest
        {
            TableName = _tableName,
            IndexName = "StatusExpiresIndex",
            KeyConditionExpression = keyCondition,
            ExpressionAttributeNames = attrNames,
            ExpressionAttributeValues = attrValues,
            Limit = limit,
            ScanIndexForward = true
        };

        // Post-query filter for deliveryConfirmedBefore
        if (!string.IsNullOrEmpty(deliveryConfirmedBefore))
        {
            request.FilterExpression = "#confirmedAt <= :confirmedBefore";
            request.ExpressionAttributeNames["#confirmedAt"] = "deliveryConfirmedAt";
            request.ExpressionAttributeValues[":confirmedBefore"] = new AttributeValue { S = deliveryConfirmedBefore };
        }

        var allItems = new List<Dictionary<string, object>>();
        Dictionary<string, AttributeValue>? lastKey = null;

        // Paginate through all results up to the limit
        do
        {
            if (lastKey != null)
            {
                request.ExclusiveStartKey = lastKey;
            }

            var response = await _dynamoDb.QueryAsync(request);
            allItems.AddRange(response.Items.Select(CropRepository.FromItem));
            lastKey = response.LastEvaluatedKey.Count > 0 ? response.LastEvaluatedKey : null;
        } while (lastKey != null && allItems.Count < limit);

        return allItems.Take(limit).ToList();
    }

    /// <summary>
    /// Query for an item by escrowId (partition key). Since sort key is createdAt,
    /// we query with limit 1 to get the single matching item.
    /// </summary>
    private async Task<Dictionary<string, AttributeValue>?> GetItemAsync(string escrowId)
    {
        var request = new QueryRequest
        {
            TableName = _tableName,
            KeyConditionExpression = "escrowId = :id",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                { ":id", new AttributeValue { S = escrowId } }
            },
            Limit = 1
        };

        var response = await _dynamoDb.QueryAsync(request);
        return response.Items.Count > 0 ? response.Items[0] : null;
    }
}
