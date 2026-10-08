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
///   GSI TruckerStatusIndex: truckerId (PK) + createdAt (SK), sparse
///   GSI OrderIndex: orderId (PK) + createdAt (SK), sparse
///   GSI ManualPayoutIndex: manualPayout (PK) + manualPayoutQueuedAt (SK), sparse (ESC-02)
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
    public async Task<Dictionary<string, object>?> GetByIdAsync(string escrowId, bool consistentRead)
    {
        var item = await GetItemAsync(escrowId, consistentRead);
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
        var conditions = new List<string>();
        if (!string.IsNullOrEmpty(expectedCurrentStatus))
        {
            conditions.Add("#status = :expectedStatus");
            request.ExpressionAttributeValues[":expectedStatus"] = new AttributeValue { S = expectedCurrentStatus };
        }

        // ESC-02: a gateway settlement never lands on an escrow waiting in the manual payout
        // queue; only TryCloseManualPayoutAsync settles those (and removes the marker).
        if (newStatus is "released" or "refunded")
        {
            conditions.Add("attribute_not_exists(#manualPayout)");
            request.ExpressionAttributeNames["#manualPayout"] = ManualPayoutFields.Marker;
        }

        if (conditions.Count > 0)
        {
            request.ConditionExpression = string.Join(" AND ", conditions);
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
            // ESC-02: never on an escrow waiting in the manual payout queue.
            ConditionExpression =
                "attribute_not_exists(settlementLock) AND attribute_not_exists(#manualPayout) "
                + "AND (#status = :escrowed OR #status = :disputed)",
            ExpressionAttributeNames = new Dictionary<string, string>
            {
                { "#status", "status" },
                { "#updated", "updatedAt" },
                { "#manualPayout", ManualPayoutFields.Marker }
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
    public async Task<bool> TryQueueManualPayoutAsync(
        string escrowId, string createdAt, string op, string reason, string fromStatus,
        IReadOnlyList<ManualPayoutLegSeed> legs)
    {
        var now = DateTime.UtcNow.ToString("o");
        var sets = new List<string>
        {
            "#marker = :open", "#queuedAt = :now", "#op = :op", "#reason = :reason",
            "#fromStatus = :fromStatus", "#updated = :now"
        };
        var names = new Dictionary<string, string>
        {
            { "#marker", ManualPayoutFields.Marker },
            { "#queuedAt", ManualPayoutFields.QueuedAt },
            { "#op", ManualPayoutFields.Op },
            { "#reason", ManualPayoutFields.Reason },
            { "#fromStatus", ManualPayoutFields.FromStatus },
            { "#updated", "updatedAt" },
            { "#version", "version" },
            { "#lock", "settlementLock" }
        };
        var values = new Dictionary<string, AttributeValue>
        {
            { ":open", new AttributeValue { S = ManualPayoutFields.MarkerOpen } },
            { ":now", new AttributeValue { S = now } },
            { ":op", new AttributeValue { S = op } },
            { ":reason", new AttributeValue { S = reason } },
            { ":fromStatus", new AttributeValue { S = fromStatus } },
            { ":one", new AttributeValue { N = "1" } }
        };

        var i = 0;
        foreach (var leg in legs)
        {
            names[$"#ls{i}"] = ManualPayoutFields.StatusAttr(leg.Leg);
            values[$":ls{i}"] = new AttributeValue { S = leg.Status };
            sets.Add($"#ls{i} = :ls{i}");
            if (!string.IsNullOrEmpty(leg.Ref))
            {
                names[$"#lr{i}"] = ManualPayoutFields.RefAttr(leg.Leg);
                values[$":lr{i}"] = new AttributeValue { S = leg.Ref };
                sets.Add($"#lr{i} = :lr{i}");
            }

            if (!string.IsNullOrEmpty(leg.PaidBy))
            {
                names[$"#lb{i}"] = ManualPayoutFields.PaidByAttr(leg.Leg);
                values[$":lb{i}"] = new AttributeValue { S = leg.PaidBy };
                sets.Add($"#lb{i} = :lb{i}");
            }

            if (!string.IsNullOrEmpty(leg.PaidAt))
            {
                names[$"#la{i}"] = ManualPayoutFields.PaidAtAttr(leg.Leg);
                values[$":la{i}"] = new AttributeValue { S = leg.PaidAt };
                sets.Add($"#la{i} = :la{i}");
            }

            i++;
        }

        var request = new UpdateItemRequest
        {
            TableName = _tableName,
            Key = EscrowKey(escrowId, createdAt),
            UpdateExpression = "SET " + string.Join(", ", sets) + " ADD #version :one",
            // Only the settlement that holds the lock may queue, and only once. The lock
            // stays in place on purpose: a queued escrow must not be settled by anyone else.
            ConditionExpression = "#lock = :op AND attribute_not_exists(#marker)",
            ExpressionAttributeNames = names,
            ExpressionAttributeValues = values
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
    public async Task<bool> TryMarkManualLegPaidAsync(
        string escrowId, string createdAt, string op, string leg, string externalRef,
        string paidBy, string paidAt, string? otherLegMustBePending)
    {
        var names = new Dictionary<string, string>
        {
            { "#marker", ManualPayoutFields.Marker },
            { "#op", ManualPayoutFields.Op },
            { "#legStatus", ManualPayoutFields.StatusAttr(leg) },
            { "#legRef", ManualPayoutFields.RefAttr(leg) },
            { "#legPaidBy", ManualPayoutFields.PaidByAttr(leg) },
            { "#legPaidAt", ManualPayoutFields.PaidAtAttr(leg) },
            { "#updated", "updatedAt" },
            { "#version", "version" }
        };
        var values = new Dictionary<string, AttributeValue>
        {
            { ":open", new AttributeValue { S = ManualPayoutFields.MarkerOpen } },
            { ":op", new AttributeValue { S = op } },
            { ":pending", new AttributeValue { S = ManualPayoutFields.LegPending } },
            { ":paid", new AttributeValue { S = ManualPayoutFields.LegPaid } },
            { ":ref", new AttributeValue { S = externalRef } },
            { ":paidBy", new AttributeValue { S = paidBy } },
            { ":paidAt", new AttributeValue { S = paidAt } },
            { ":one", new AttributeValue { N = "1" } }
        };
        var condition = "#marker = :open AND #op = :op AND #legStatus = :pending";
        if (!string.IsNullOrEmpty(otherLegMustBePending))
        {
            names["#otherStatus"] = ManualPayoutFields.StatusAttr(otherLegMustBePending);
            condition += " AND #otherStatus = :pending";
        }

        var request = new UpdateItemRequest
        {
            TableName = _tableName,
            Key = EscrowKey(escrowId, createdAt),
            UpdateExpression =
                "SET #legStatus = :paid, #legRef = :ref, #legPaidBy = :paidBy, #legPaidAt = :paidAt, #updated = :paidAt "
                + "ADD #version :one",
            ConditionExpression = condition,
            ExpressionAttributeNames = names,
            ExpressionAttributeValues = values
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
    public async Task<bool> TryCloseManualPayoutAsync(
        string escrowId, string createdAt, string op, string leg, string externalRef,
        string paidBy, string paidAt, IReadOnlyList<string> otherLegsMustBePaid,
        string newStatus, Dictionary<string, object> settlementFields)
    {
        var sets = new List<string>
        {
            "#legStatus = :paid", "#legRef = :ref", "#legPaidBy = :paidBy", "#legPaidAt = :paidAt",
            "#status = :newStatus", "#updated = :paidAt"
        };
        var names = new Dictionary<string, string>
        {
            { "#marker", ManualPayoutFields.Marker },
            { "#op", ManualPayoutFields.Op },
            { "#legStatus", ManualPayoutFields.StatusAttr(leg) },
            { "#legRef", ManualPayoutFields.RefAttr(leg) },
            { "#legPaidBy", ManualPayoutFields.PaidByAttr(leg) },
            { "#legPaidAt", ManualPayoutFields.PaidAtAttr(leg) },
            { "#status", "status" },
            { "#updated", "updatedAt" },
            { "#version", "version" }
        };
        var values = new Dictionary<string, AttributeValue>
        {
            { ":open", new AttributeValue { S = ManualPayoutFields.MarkerOpen } },
            { ":op", new AttributeValue { S = op } },
            { ":pending", new AttributeValue { S = ManualPayoutFields.LegPending } },
            { ":paid", new AttributeValue { S = ManualPayoutFields.LegPaid } },
            { ":ref", new AttributeValue { S = externalRef } },
            { ":paidBy", new AttributeValue { S = paidBy } },
            { ":paidAt", new AttributeValue { S = paidAt } },
            { ":newStatus", new AttributeValue { S = newStatus } },
            { ":escrowed", new AttributeValue { S = "escrowed" } },
            { ":disputed", new AttributeValue { S = "disputed" } },
            { ":disputeResolved", new AttributeValue { S = "dispute_resolved" } },
            { ":one", new AttributeValue { N = "1" } }
        };

        var condition = "#marker = :open AND #op = :op AND #legStatus = :pending"
                        + " AND (#status = :escrowed OR #status = :disputed OR #status = :disputeResolved)";
        var i = 0;
        foreach (var other in otherLegsMustBePaid)
        {
            names[$"#other{i}"] = ManualPayoutFields.StatusAttr(other);
            condition += $" AND #other{i} = :paid";
            i++;
        }

        i = 0;
        foreach (var kvp in settlementFields)
        {
            if (kvp.Value == null || kvp.Key is "escrowId" or "createdAt" or "status" or "updatedAt" or "version")
            {
                continue;
            }

            names[$"#f{i}"] = kvp.Key;
            values[$":f{i}"] = CropRepository.ToAttributeValue(kvp.Value);
            sets.Add($"#f{i} = :f{i}");
            i++;
        }

        var request = new UpdateItemRequest
        {
            TableName = _tableName,
            Key = EscrowKey(escrowId, createdAt),
            UpdateExpression = "SET " + string.Join(", ", sets) + " REMOVE #marker ADD #version :one",
            ConditionExpression = condition,
            ExpressionAttributeNames = names,
            ExpressionAttributeValues = values
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
    public async Task<(List<Dictionary<string, object>> Items, Dictionary<string, AttributeValue>? LastEvaluatedKey)> QueryOpenManualPayoutsAsync(
        int limit, Dictionary<string, AttributeValue>? exclusiveStartKey)
    {
        if (limit < 1 || limit > 50)
        {
            limit = 20;
        }

        var request = new QueryRequest
        {
            TableName = _tableName,
            IndexName = ManualPayoutFields.IndexName,
            KeyConditionExpression = "#marker = :open",
            ExpressionAttributeNames = new Dictionary<string, string>
            {
                { "#marker", ManualPayoutFields.Marker }
            },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                { ":open", new AttributeValue { S = ManualPayoutFields.MarkerOpen } }
            },
            Limit = limit,
            ScanIndexForward = true
        };

        if (exclusiveStartKey != null)
        {
            request.ExclusiveStartKey = exclusiveStartKey;
        }

        var response = await _dynamoDb.QueryAsync(request);
        var items = response.Items.Select(CropRepository.FromItem).ToList();
        return (items, response.LastEvaluatedKey.Count > 0 ? response.LastEvaluatedKey : null);
    }

    private static Dictionary<string, AttributeValue> EscrowKey(string escrowId, string createdAt) => new()
    {
        { "escrowId", new AttributeValue { S = escrowId } },
        { "createdAt", new AttributeValue { S = createdAt } }
    };

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
    /// <inheritdoc />
    public async Task<(List<Dictionary<string, object>> Items, Dictionary<string, AttributeValue>? LastEvaluatedKey)> QueryByTruckerAsync(
        string truckerId, string? status, int limit, Dictionary<string, AttributeValue>? exclusiveStartKey)
    {
        if (limit < 1 || limit > 50)
        {
            limit = 20;
        }

        var request = new QueryRequest
        {
            TableName = _tableName,
            IndexName = "TruckerStatusIndex",
            KeyConditionExpression = "#truckerId = :truckerId",
            ExpressionAttributeNames = new Dictionary<string, string>
            {
                { "#truckerId", "truckerId" }
            },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                { ":truckerId", new AttributeValue { S = truckerId } }
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
    private async Task<Dictionary<string, AttributeValue>?> GetItemAsync(string escrowId, bool consistentRead = false)
    {
        var request = new QueryRequest
        {
            TableName = _tableName,
            KeyConditionExpression = "escrowId = :id",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                { ":id", new AttributeValue { S = escrowId } }
            },
            Limit = 1,
            ConsistentRead = consistentRead
        };

        var response = await _dynamoDb.QueryAsync(request);
        return response.Items.Count > 0 ? response.Items[0] : null;
    }
}
