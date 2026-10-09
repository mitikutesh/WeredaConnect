using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using WeredaConnect.Server.Services;

namespace WeredaConnect.Server.Data;

/// <summary>
/// DynamoDB order repository using low-level API for AOT compatibility.
/// Manual marshalling — no Document Model or reflection.
/// Version-based optimistic concurrency for sync conflict detection.
///
/// Table schema (Orders):
///   PK: orderId (STRING)
///   SK: createdAt (STRING)
///   GSI BuyerIdCreatedAtIndex: buyerId + createdAt
///   GSI FarmerIdCreatedAtIndex: farmerId + createdAt
///   GSI TruckerIdCreatedAtIndex: truckerId + createdAt
///   GSI StatusCreatedAtIndex: status + createdAt
///   GSI ListingIdIndex: listingId + createdAt
///   GSI TripIdIndex: tripId + createdAt
/// </summary>
public sealed class OrderRepository : IOrderRepository
{
    private readonly IAmazonDynamoDB _dynamoDb;
    private readonly string _tableName;

    public OrderRepository(IAmazonDynamoDB dynamoDb, string tableName)
    {
        _dynamoDb = dynamoDb ?? throw new ArgumentNullException(nameof(dynamoDb));
        _tableName = tableName ?? throw new ArgumentNullException(nameof(tableName));
    }

    /// <inheritdoc />
    public async Task<AuthResult<Dictionary<string, object>>> CreateAsync(
        string orderId,
        Dictionary<string, object> data)
    {
        var now = DateTime.UtcNow.ToString("o");

        var item = new Dictionary<string, AttributeValue>
        {
            { "orderId", new AttributeValue { S = orderId } },
            { "createdAt", new AttributeValue { S = now } },
            { "updatedAt", new AttributeValue { S = now } },
            { "version", new AttributeValue { N = "1" } }
        };

        // Copy data fields into the item
        foreach (var kvp in data)
        {
            if (kvp.Key is "orderId" or "version" or "createdAt" or "updatedAt")
            {
                continue;
            }

            item[kvp.Key] = DynamoDbHelper.ToAttributeValue(kvp.Value);
        }

        var request = new PutItemRequest
        {
            TableName = _tableName,
            Item = item,
            ConditionExpression = "attribute_not_exists(orderId)"
        };

        try
        {
            if (await DynamoDbHelper.PartitionExistsAsync(_dynamoDb, _tableName, "orderId", orderId))
            {
                throw new ConditionalCheckFailedException("Order already exists.");
            }

            await _dynamoDb.PutItemAsync(request);

            var result = DynamoDbHelper.FromItem(item);
            return AuthResult<Dictionary<string, object>>.Success(result);
        }
        catch (ConditionalCheckFailedException)
        {
            return AuthResult<Dictionary<string, object>>.Failure(
                409, "SYNC_CONFLICT", "Order already exists.", "orderId");
        }
    }

    /// <inheritdoc />
    public async Task<AuthResult<Dictionary<string, object>>> UpdateAsync(
        string orderId,
        Dictionary<string, object> data,
        int expectedVersion)
    {
        // First, get the current item to know its sort key
        var existing = await GetItemAsync(orderId);
        if (existing == null)
        {
            return AuthResult<Dictionary<string, object>>.Failure(
                404, "SYNC_VALIDATION", "Order not found.", "orderId");
        }

        var createdAt = existing["createdAt"].S;
        var now = DateTime.UtcNow.ToString("o");
        var newVersion = expectedVersion + 1;

        // Build update expression from data fields
        var updateParts = new List<string> { "#updated = :updated", "#v = :newVersion" };
        var attrNames = new Dictionary<string, string>
        {
            { "#updated", "updatedAt" },
            { "#v", "version" }
        };
        var attrValues = new Dictionary<string, AttributeValue>
        {
            { ":updated", new AttributeValue { S = now } },
            { ":newVersion", new AttributeValue { N = newVersion.ToString() } },
            { ":expectedVersion", new AttributeValue { N = expectedVersion.ToString() } }
        };

        var i = 0;
        foreach (var kvp in data)
        {
            if (kvp.Key is "orderId" or "version" or "createdAt" or "updatedAt")
            {
                continue;
            }

            var nameAlias = $"#f{i}";
            var valueAlias = $":f{i}";
            attrNames[nameAlias] = kvp.Key;
            attrValues[valueAlias] = DynamoDbHelper.ToAttributeValue(kvp.Value);
            updateParts.Add($"{nameAlias} = {valueAlias}");
            i++;
        }

        var request = new UpdateItemRequest
        {
            TableName = _tableName,
            Key = new Dictionary<string, AttributeValue>
            {
                { "orderId", new AttributeValue { S = orderId } },
                { "createdAt", new AttributeValue { S = createdAt } }
            },
            UpdateExpression = "SET " + string.Join(", ", updateParts),
            ConditionExpression = "#v = :expectedVersion",
            ExpressionAttributeNames = attrNames,
            ExpressionAttributeValues = attrValues,
            ReturnValues = ReturnValue.ALL_NEW
        };

        try
        {
            var response = await _dynamoDb.UpdateItemAsync(request);
            var result = DynamoDbHelper.FromItem(response.Attributes);
            return AuthResult<Dictionary<string, object>>.Success(result);
        }
        catch (ConditionalCheckFailedException)
        {
            var serverData = DynamoDbHelper.FromItem(existing);
            return AuthResult<Dictionary<string, object>>.Failure(
                new AuthResultError
                {
                    HttpStatus = 409,
                    Code = "SYNC_CONFLICT",
                    Message = $"Version conflict: server has version {existing["version"].N}, client expected {expectedVersion}."
                });
        }
    }

    /// <inheritdoc />
    public async Task<Dictionary<string, object>?> GetByIdAsync(string orderId)
    {
        var item = await GetItemAsync(orderId);
        return item != null ? DynamoDbHelper.FromItem(item) : null;
    }

    /// <inheritdoc />
    public async Task<Dictionary<string, object>?> GetByIdAsync(string orderId, bool consistentRead)
    {
        var item = await GetItemAsync(orderId, consistentRead);
        return item != null ? DynamoDbHelper.FromItem(item) : null;
    }

    /// <inheritdoc />
    public async Task<Dictionary<string, object>?> TryMarkFundedAsync(
        string orderId, Dictionary<string, object> fields)
    {
        var existing = await GetItemAsync(orderId, consistentRead: true);
        if (existing == null)
        {
            return null;
        }

        var setParts = new List<string> { "#status = :funded", "#updated = :updated" };
        var attrNames = new Dictionary<string, string>
        {
            { "#status", "status" },
            { "#updated", "updatedAt" },
            { "#v", "version" }
        };
        var attrValues = new Dictionary<string, AttributeValue>
        {
            { ":funded", new AttributeValue { S = "funded" } },
            { ":submitted", new AttributeValue { S = "payment_submitted" } },
            { ":updated", new AttributeValue { S = DateTime.UtcNow.ToString("o") } },
            { ":one", new AttributeValue { N = "1" } }
        };

        var i = 0;
        foreach (var kvp in fields)
        {
            if (kvp.Key is "orderId" or "createdAt" or "updatedAt" or "version" or "status")
            {
                continue;
            }

            attrNames[$"#f{i}"] = kvp.Key;
            attrValues[$":f{i}"] = DynamoDbHelper.ToAttributeValue(kvp.Value);
            setParts.Add($"#f{i} = :f{i}");
            i++;
        }

        var request = new UpdateItemRequest
        {
            TableName = _tableName,
            Key = new Dictionary<string, AttributeValue>
            {
                { "orderId", new AttributeValue { S = orderId } },
                { "createdAt", new AttributeValue { S = existing["createdAt"].S } }
            },
            UpdateExpression = "SET " + string.Join(", ", setParts) + " ADD #v :one",
            ConditionExpression = "#status = :submitted",
            ExpressionAttributeNames = attrNames,
            ExpressionAttributeValues = attrValues,
            ReturnValues = ReturnValue.ALL_NEW
        };

        try
        {
            var response = await _dynamoDb.UpdateItemAsync(request);
            return DynamoDbHelper.FromItem(response.Attributes);
        }
        catch (ConditionalCheckFailedException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<(List<Dictionary<string, object>> Items, Dictionary<string, AttributeValue>? LastEvaluatedKey)> QueryOrdersAsync(
        string? buyerId = null,
        string? farmerId = null,
        string? truckerId = null,
        string? status = null,
        string? listingId = null,
        int limit = 20,
        Dictionary<string, AttributeValue>? exclusiveStartKey = null)
    {
        if (limit < 1 || limit > 50)
        {
            limit = 20;
        }

        // Use most specific GSI available. When a status filter is supplied
        // alongside a role id, it is applied as a FilterExpression on top of the
        // role GSI query — otherwise the status was silently dropped and every
        // status tab returned the user's full order list (web bug).
        if (!string.IsNullOrEmpty(buyerId))
        {
            return await QueryByGsiAsync("BuyerIdCreatedAtIndex", "buyerId", buyerId, limit, exclusiveStartKey, status);
        }

        if (!string.IsNullOrEmpty(farmerId))
        {
            return await QueryByGsiAsync("FarmerIdCreatedAtIndex", "farmerId", farmerId, limit, exclusiveStartKey, status);
        }

        if (!string.IsNullOrEmpty(truckerId))
        {
            return await QueryByGsiAsync("TruckerIdCreatedAtIndex", "truckerId", truckerId, limit, exclusiveStartKey, status);
        }

        if (!string.IsNullOrEmpty(status))
        {
            return await QueryByGsiAsync("StatusCreatedAtIndex", "status", status, limit, exclusiveStartKey);
        }

        if (!string.IsNullOrEmpty(listingId))
        {
            return await QueryByGsiAsync("ListingIdIndex", "listingId", listingId, limit, exclusiveStartKey);
        }

        // No filter — scan (limited use for admin)
        var scanRequest = new ScanRequest
        {
            TableName = _tableName,
            Limit = limit
        };

        if (exclusiveStartKey != null)
        {
            scanRequest.ExclusiveStartKey = exclusiveStartKey;
        }

        var scanResponse = await _dynamoDb.ScanAsync(scanRequest);
        var items = scanResponse.Items.Select(DynamoDbHelper.FromItem).ToList();
        return (items, scanResponse.LastEvaluatedKey.Count > 0 ? scanResponse.LastEvaluatedKey : null);
    }

    /// <inheritdoc />
    public async Task<List<Dictionary<string, object>>> QueryByStatusForCronAsync(string status, string? createdBefore = null)
    {
        var request = new QueryRequest
        {
            TableName = _tableName,
            IndexName = "StatusCreatedAtIndex",
            KeyConditionExpression = "#status = :status",
            ExpressionAttributeNames = new Dictionary<string, string>
            {
                { "#status", "status" }
            },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                { ":status", new AttributeValue { S = status } }
            },
            ScanIndexForward = true // oldest first for cron
        };

        if (!string.IsNullOrEmpty(createdBefore))
        {
            request.KeyConditionExpression += " AND createdAt < :createdBefore";
            request.ExpressionAttributeValues[":createdBefore"] = new AttributeValue { S = createdBefore };
        }

        var result = new List<Dictionary<string, object>>();
        QueryResponse? response = null;

        do
        {
            if (response?.LastEvaluatedKey?.Count > 0)
            {
                request.ExclusiveStartKey = response.LastEvaluatedKey;
            }

            response = await _dynamoDb.QueryAsync(request);
            result.AddRange(response.Items.Select(DynamoDbHelper.FromItem));
        }
        while (response.LastEvaluatedKey?.Count > 0);

        return result;
    }

    /// <inheritdoc />
    public async Task<DeliverySlotWriteResult> TryCreateDeliveryWithSlotAsync(
        string orderId,
        string deliveriesTable,
        Dictionary<string, AttributeValue> deliveryItem,
        DeliverySlotHolder? replacing = null)
    {
        var existing = await GetItemAsync(orderId);
        if (existing == null)
        {
            return DeliverySlotWriteResult.OrderNotFound;
        }

        var values = new Dictionary<string, AttributeValue>
        {
            { ":did", new AttributeValue { S = deliveryItem["deliveryId"].S } },
            { ":now", new AttributeValue { S = DateTime.UtcNow.ToString("o") } }
        };

        string slotCondition;
        if (replacing == null)
        {
            slotCondition = "attribute_not_exists(#slot) OR #slot = :did";
        }
        else
        {
            values[":stale"] = new AttributeValue { S = replacing.DeliveryId };
            if (replacing.ClaimedAt == null)
            {
                slotCondition = "#slot = :stale AND attribute_not_exists(#claimedAt)";
            }
            else
            {
                values[":seen"] = new AttributeValue { S = replacing.ClaimedAt };
                slotCondition = "#slot = :stale AND #claimedAt = :seen";
            }
        }

        // Deliveries have no order index, so the order row carries the single-active-
        // delivery invariant as a conditional write, in the same transaction as the row it
        // names. The version is deliberately not bumped: the claim is not a client-visible
        // edit of the order.
        var request = new TransactWriteItemsRequest
        {
            TransactItems = new List<TransactWriteItem>
            {
                new()
                {
                    Update = new Update
                    {
                        TableName = _tableName,
                        Key = new Dictionary<string, AttributeValue>
                        {
                            { "orderId", new AttributeValue { S = orderId } },
                            { "createdAt", new AttributeValue { S = existing["createdAt"].S } }
                        },
                        UpdateExpression = "SET #slot = :did, #claimedAt = :now",
                        ConditionExpression = $"attribute_exists(orderId) AND ({slotCondition})",
                        ExpressionAttributeNames = new Dictionary<string, string>
                        {
                            { "#slot", "activeDeliveryId" },
                            { "#claimedAt", "activeDeliveryClaimedAt" }
                        },
                        ExpressionAttributeValues = values
                    }
                },
                new()
                {
                    Put = new Put
                    {
                        TableName = deliveriesTable,
                        Item = deliveryItem,
                        ConditionExpression = "attribute_not_exists(deliveryId)"
                    }
                }
            }
        };

        try
        {
            await _dynamoDb.TransactWriteItemsAsync(request);
            return DeliverySlotWriteResult.Written;
        }
        catch (TransactionCanceledException ex)
        {
            // Reasons are per item, in request order. An existing row wins over a taken
            // slot: the caller answers it like any create of an existing delivery.
            if (IsConditionFailure(ex.CancellationReasons, 1))
            {
                return DeliverySlotWriteResult.DeliveryExists;
            }

            if (IsConditionFailure(ex.CancellationReasons, 0))
            {
                return DeliverySlotWriteResult.SlotTaken;
            }

            // A conflicting transaction or throttling: nothing was written, so the caller's
            // failure is safe to retry.
            throw;
        }
    }

    private static bool IsConditionFailure(List<CancellationReason>? reasons, int index) =>
        reasons != null && reasons.Count > index && reasons[index]?.Code == "ConditionalCheckFailed";

    /// <inheritdoc />
    public async Task ReleaseDeliverySlotAsync(string orderId, string deliveryId)
    {
        var existing = await GetItemAsync(orderId);
        if (existing == null)
        {
            return;
        }

        var request = new UpdateItemRequest
        {
            TableName = _tableName,
            Key = new Dictionary<string, AttributeValue>
            {
                { "orderId", new AttributeValue { S = orderId } },
                { "createdAt", new AttributeValue { S = existing["createdAt"].S } }
            },
            UpdateExpression = "REMOVE #slot, #claimedAt",
            ConditionExpression = "#slot = :did",
            ExpressionAttributeNames = new Dictionary<string, string>
            {
                { "#slot", "activeDeliveryId" },
                { "#claimedAt", "activeDeliveryClaimedAt" }
            },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                { ":did", new AttributeValue { S = deliveryId } }
            }
        };

        try
        {
            await _dynamoDb.UpdateItemAsync(request);
        }
        catch (ConditionalCheckFailedException)
        {
            // Another delivery holds the slot (or it is already free): nothing to release.
        }
    }

    /// <summary>
    /// Query for an item by orderId (partition key). Since sort key is createdAt,
    /// we query with limit 1 to get the single matching item.
    /// </summary>
    private async Task<Dictionary<string, AttributeValue>?> GetItemAsync(string orderId, bool consistentRead = false)
    {
        var request = new QueryRequest
        {
            TableName = _tableName,
            KeyConditionExpression = "orderId = :id",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                { ":id", new AttributeValue { S = orderId } }
            },
            Limit = 1,
            ConsistentRead = consistentRead
        };

        var response = await _dynamoDb.QueryAsync(request);
        return response.Items.Count > 0 ? response.Items[0] : null;
    }

    /// <summary>
    /// Query a GSI by partition key with optional pagination and an optional
    /// status filter.
    /// </summary>
    /// <remarks>
    /// When <paramref name="statusFilter"/> is supplied it is applied as a
    /// DynamoDB FilterExpression on the non-key <c>status</c> attribute (a
    /// reserved word, hence the <c>#status</c> alias). Note: a FilterExpression
    /// is applied AFTER <c>Limit</c>, so a single page may contain fewer than
    /// <c>limit</c> matching items even when more exist; callers can follow the
    /// returned nextToken. This is acceptable here because a single user's order
    /// set is small.
    /// </remarks>
    private async Task<(List<Dictionary<string, object>> Items, Dictionary<string, AttributeValue>? LastEvaluatedKey)> QueryByGsiAsync(
        string indexName,
        string partitionKeyName,
        string partitionKeyValue,
        int limit,
        Dictionary<string, AttributeValue>? exclusiveStartKey,
        string? statusFilter = null)
    {
        var request = new QueryRequest
        {
            TableName = _tableName,
            IndexName = indexName,
            KeyConditionExpression = $"#{partitionKeyName} = :val",
            ExpressionAttributeNames = new Dictionary<string, string>
            {
                { $"#{partitionKeyName}", partitionKeyName }
            },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                { ":val", new AttributeValue { S = partitionKeyValue } }
            },
            ScanIndexForward = false, // newest first
            Limit = limit
        };

        // Apply the status filter unless status IS the partition key (the
        // status-only GSI already constrains it via the key condition).
        if (!string.IsNullOrEmpty(statusFilter) && partitionKeyName != "status")
        {
            request.FilterExpression = "#status = :status";
            request.ExpressionAttributeNames["#status"] = "status";
            request.ExpressionAttributeValues[":status"] = new AttributeValue { S = statusFilter };
        }

        if (exclusiveStartKey != null)
        {
            request.ExclusiveStartKey = exclusiveStartKey;
        }

        var response = await _dynamoDb.QueryAsync(request);
        var items = response.Items.Select(DynamoDbHelper.FromItem).ToList();
        return (items, response.LastEvaluatedKey.Count > 0 ? response.LastEvaluatedKey : null);
    }
}
