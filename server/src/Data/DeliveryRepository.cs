using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;

namespace WeredaConnect.Server.Data;

/// <summary>
/// DynamoDB delivery repository using low-level API for AOT compatibility.
/// Manual marshalling — no Document Model or reflection.
///
/// Table schema (Deliveries):
///   PK: deliveryId (STRING)
///   SK: createdAt (STRING)
///   GSI StatusCreatedAtIndex: status (PK) + createdAt (SK)
///   GSI TripIdIndex: tripId (PK) + createdAt (SK)
///   GSI ParticipantIndex: participantId (PK, = buyerId) + createdAt (SK)
///   GSI FarmerIdCreatedAtIndex: farmerId (PK) + createdAt (SK)
/// </summary>
public sealed class DeliveryRepository : IDeliveryRepository
{
    private readonly IAmazonDynamoDB _dynamoDb;
    private readonly string _tableName;

    public DeliveryRepository(IAmazonDynamoDB dynamoDb, string tableName)
    {
        _dynamoDb = dynamoDb ?? throw new ArgumentNullException(nameof(dynamoDb));
        _tableName = tableName ?? throw new ArgumentNullException(nameof(tableName));
    }

    /// <inheritdoc />
    public async Task<(List<Dictionary<string, object>> Items, Dictionary<string, AttributeValue>? LastEvaluatedKey)> QueryDeliveriesAsync(
        string? status = null,
        string? tripId = null,
        string? listingId = null,
        int limit = 20,
        Dictionary<string, AttributeValue>? exclusiveStartKey = null)
    {
        if (limit < 1 || limit > 50)
        {
            limit = 20;
        }

        // If tripId filter is provided, use TripIdIndex GSI
        if (!string.IsNullOrEmpty(tripId))
        {
            return await QueryByTripIdAsync(tripId, status, limit, exclusiveStartKey);
        }

        // Default: use StatusCreatedAtIndex GSI
        var queryStatus = string.IsNullOrEmpty(status) ? "pending" : status;

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
                { ":status", new AttributeValue { S = queryStatus } }
            },
            Limit = limit,
            ScanIndexForward = false // newest first
        };

        if (exclusiveStartKey != null)
        {
            request.ExclusiveStartKey = exclusiveStartKey;
        }

        // Exclude deleted items
        var filterParts = new List<string> { "attribute_not_exists(#deletedAt)" };
        request.ExpressionAttributeNames["#deletedAt"] = "deletedAt";

        if (!string.IsNullOrEmpty(listingId))
        {
            filterParts.Add("#listingId = :listingId");
            request.ExpressionAttributeNames["#listingId"] = "listingId";
            request.ExpressionAttributeValues[":listingId"] = new AttributeValue { S = listingId };
        }

        request.FilterExpression = string.Join(" AND ", filterParts);

        var response = await _dynamoDb.QueryAsync(request);
        var items = response.Items.Select(CropRepository.FromItem).ToList();
        return (items, response.LastEvaluatedKey.Count > 0 ? response.LastEvaluatedKey : null);
    }

    private async Task<(List<Dictionary<string, object>> Items, Dictionary<string, AttributeValue>? LastEvaluatedKey)> QueryByTripIdAsync(
        string tripId,
        string? status,
        int limit,
        Dictionary<string, AttributeValue>? exclusiveStartKey)
    {
        var request = new QueryRequest
        {
            TableName = _tableName,
            IndexName = "TripIdIndex",
            KeyConditionExpression = "#tripId = :tripId",
            ExpressionAttributeNames = new Dictionary<string, string>
            {
                { "#tripId", "tripId" }
            },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                { ":tripId", new AttributeValue { S = tripId } }
            },
            Limit = limit,
            ScanIndexForward = false
        };

        if (exclusiveStartKey != null)
        {
            request.ExclusiveStartKey = exclusiveStartKey;
        }

        var filterParts = new List<string> { "attribute_not_exists(#deletedAt)" };
        request.ExpressionAttributeNames["#deletedAt"] = "deletedAt";

        if (!string.IsNullOrEmpty(status))
        {
            filterParts.Add("#status = :status");
            request.ExpressionAttributeNames["#status"] = "status";
            request.ExpressionAttributeValues[":status"] = new AttributeValue { S = status };
        }

        request.FilterExpression = string.Join(" AND ", filterParts);

        var response = await _dynamoDb.QueryAsync(request);
        var items = response.Items.Select(CropRepository.FromItem).ToList();
        return (items, response.LastEvaluatedKey.Count > 0 ? response.LastEvaluatedKey : null);
    }

    /// <inheritdoc />
    public async Task<(List<Dictionary<string, object>> Items, Dictionary<string, AttributeValue>? LastEvaluatedKey)> QueryByParticipantAsync(
        string participantId,
        int limit = 50,
        Dictionary<string, AttributeValue>? exclusiveStartKey = null,
        string? status = null)
    {
        if (limit < 1 || limit > 50)
        {
            limit = 50;
        }

        var request = new QueryRequest
        {
            TableName = _tableName,
            IndexName = "ParticipantIndex",
            KeyConditionExpression = "#pid = :pid",
            ExpressionAttributeNames = new Dictionary<string, string>
            {
                { "#pid", "participantId" },
                { "#deletedAt", "deletedAt" }
            },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                { ":pid", new AttributeValue { S = participantId } }
            },
            Limit = limit,
            ScanIndexForward = false // newest first
        };

        var filterParts = new List<string> { "attribute_not_exists(#deletedAt)" };
        if (!string.IsNullOrEmpty(status))
        {
            filterParts.Add("#status = :status");
            request.ExpressionAttributeNames["#status"] = "status";
            request.ExpressionAttributeValues[":status"] = new AttributeValue { S = status };
        }

        request.FilterExpression = string.Join(" AND ", filterParts);

        if (exclusiveStartKey != null)
        {
            request.ExclusiveStartKey = exclusiveStartKey;
        }

        var response = await _dynamoDb.QueryAsync(request);
        var items = response.Items.Select(CropRepository.FromItem).ToList();
        return (items, response.LastEvaluatedKey.Count > 0 ? response.LastEvaluatedKey : null);
    }

    /// <inheritdoc />
    public async Task<(List<Dictionary<string, object>> Items, Dictionary<string, AttributeValue>? LastEvaluatedKey)> QueryByFarmerAsync(
        string farmerId,
        string? status = null,
        int limit = 20,
        Dictionary<string, AttributeValue>? exclusiveStartKey = null)
    {
        if (limit < 1 || limit > 50)
        {
            limit = 20;
        }

        var request = new QueryRequest
        {
            TableName = _tableName,
            IndexName = "FarmerIdCreatedAtIndex",
            KeyConditionExpression = "#farmerId = :farmerId",
            ExpressionAttributeNames = new Dictionary<string, string>
            {
                { "#farmerId", "farmerId" },
                { "#deletedAt", "deletedAt" }
            },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                { ":farmerId", new AttributeValue { S = farmerId } }
            },
            Limit = limit,
            ScanIndexForward = false // newest first
        };

        // Always exclude soft-deleted deliveries; apply the optional status
        // filter on the non-key attribute (a FilterExpression, since status is
        // not part of this GSI's key).
        var filterParts = new List<string> { "attribute_not_exists(#deletedAt)" };
        if (!string.IsNullOrEmpty(status))
        {
            filterParts.Add("#status = :status");
            request.ExpressionAttributeNames["#status"] = "status";
            request.ExpressionAttributeValues[":status"] = new AttributeValue { S = status };
        }

        request.FilterExpression = string.Join(" AND ", filterParts);

        if (exclusiveStartKey != null)
        {
            request.ExclusiveStartKey = exclusiveStartKey;
        }

        var response = await _dynamoDb.QueryAsync(request);
        var items = response.Items.Select(CropRepository.FromItem).ToList();
        return (items, response.LastEvaluatedKey.Count > 0 ? response.LastEvaluatedKey : null);
    }

    /// <inheritdoc />
    public async Task<(List<Dictionary<string, object>> Items, Dictionary<string, AttributeValue>? LastEvaluatedKey)> QueryByTruckerAsync(
        string truckerId,
        string? status = null,
        int limit = 20,
        Dictionary<string, AttributeValue>? exclusiveStartKey = null)
    {
        if (limit < 1 || limit > 50)
        {
            limit = 20;
        }

        var request = new QueryRequest
        {
            TableName = _tableName,
            IndexName = "TruckerIdCreatedAtIndex",
            KeyConditionExpression = "#truckerId = :truckerId",
            ExpressionAttributeNames = new Dictionary<string, string>
            {
                { "#truckerId", "truckerId" },
                { "#deletedAt", "deletedAt" }
            },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                { ":truckerId", new AttributeValue { S = truckerId } }
            },
            Limit = limit,
            ScanIndexForward = false // newest first
        };

        // Always exclude soft-deleted deliveries; apply the optional status
        // filter on the non-key attribute (a FilterExpression, since status is
        // not part of this GSI's key).
        var filterParts = new List<string> { "attribute_not_exists(#deletedAt)" };
        if (!string.IsNullOrEmpty(status))
        {
            filterParts.Add("#status = :status");
            request.ExpressionAttributeNames["#status"] = "status";
            request.ExpressionAttributeValues[":status"] = new AttributeValue { S = status };
        }

        request.FilterExpression = string.Join(" AND ", filterParts);

        if (exclusiveStartKey != null)
        {
            request.ExclusiveStartKey = exclusiveStartKey;
        }

        var response = await _dynamoDb.QueryAsync(request);
        var items = response.Items.Select(CropRepository.FromItem).ToList();
        return (items, response.LastEvaluatedKey.Count > 0 ? response.LastEvaluatedKey : null);
    }

    /// <inheritdoc />
    public async Task<Dictionary<string, object>?> GetByIdAsync(string deliveryId)
    {
        var item = await GetItemAsync(deliveryId);
        return item != null ? CropRepository.FromItem(item) : null;
    }

    /// <inheritdoc />
    public async Task<Dictionary<string, object>?> GetByIdAsync(string deliveryId, bool consistentRead)
    {
        var item = await GetItemAsync(deliveryId, consistentRead);
        return item != null ? CropRepository.FromItem(item) : null;
    }

    /// <inheritdoc />
    public async Task<bool> TryMarkDisputedAsync(string deliveryId, string disputedBy, string? reason)
    {
        var existing = await GetItemAsync(deliveryId);
        if (existing == null)
        {
            return false;
        }

        // The sync handler keys deliveries on deliveryId + tripId (self-pickup stores a
        // synthetic tripId), so the same key is used here.
        var tripId = existing.TryGetValue("tripId", out var tripAttr) ? tripAttr.S ?? "" : "";
        var now = DateTime.UtcNow.ToString("o");

        var attrNames = new Dictionary<string, string>
        {
            { "#status", "status" }, { "#updated", "updatedAt" }, { "#v", "version" },
            { "#disputedBy", "disputedBy" }
        };
        var attrValues = new Dictionary<string, AttributeValue>
        {
            { ":disputed", new AttributeValue { S = "disputed" } },
            { ":delivered", new AttributeValue { S = "delivered" } },
            { ":updated", new AttributeValue { S = now } },
            { ":one", new AttributeValue { N = "1" } },
            { ":disputedBy", new AttributeValue { S = disputedBy } }
        };
        var setParts = "#status = :disputed, #updated = :updated, #disputedBy = :disputedBy";
        if (!string.IsNullOrWhiteSpace(reason))
        {
            attrNames["#reason"] = "disputeReason";
            attrValues[":reason"] = new AttributeValue { S = reason };
            setParts += ", #reason = :reason";
        }

        var request = new UpdateItemRequest
        {
            TableName = _tableName,
            Key = new Dictionary<string, AttributeValue>
            {
                { "deliveryId", new AttributeValue { S = deliveryId } },
                { "tripId", new AttributeValue { S = tripId } }
            },
            // ESC-07: ADD, never a version from the read above. The condition is the status
            // only, so a concurrent update could otherwise end up at the same version and a
            // client CAS at that stale version would succeed. A row without a version gets 1.
            UpdateExpression = "SET " + setParts + " ADD #v :one",
            ConditionExpression = "#status = :delivered",
            ExpressionAttributeNames = attrNames,
            ExpressionAttributeValues = attrValues
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
    public async Task<bool> TryRecordDisputeResolutionAsync(string deliveryId, string resolution, string resolvedAt)
    {
        var existing = await GetItemAsync(deliveryId, consistentRead: true);
        if (existing == null)
        {
            return false;
        }

        var tripId = existing.TryGetValue("tripId", out var tripAttr) ? tripAttr.S ?? "" : "";
        var request = new UpdateItemRequest
        {
            TableName = _tableName,
            Key = new Dictionary<string, AttributeValue>
            {
                { "deliveryId", new AttributeValue { S = deliveryId } },
                { "tripId", new AttributeValue { S = tripId } }
            },
            UpdateExpression = "SET #resolution = :resolution, #resolvedAt = :resolvedAt, #updated = :updated ADD #v :one",
            // No version condition: the status and the missing verdict are the guard, so a
            // version bumped by the disputed write just before cannot fail this stamp.
            ConditionExpression = "#status = :disputed AND attribute_not_exists(#resolution)",
            ExpressionAttributeNames = new Dictionary<string, string>
            {
                { "#status", "status" }, { "#resolution", "disputeResolution" },
                { "#resolvedAt", "disputeResolvedAt" }, { "#updated", "updatedAt" }, { "#v", "version" }
            },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                { ":disputed", new AttributeValue { S = "disputed" } },
                { ":resolution", new AttributeValue { S = resolution } },
                { ":resolvedAt", new AttributeValue { S = resolvedAt } },
                { ":updated", new AttributeValue { S = DateTime.UtcNow.ToString("o") } },
                { ":one", new AttributeValue { N = "1" } }
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
    public async Task<bool> TryCancelPendingAsync(string deliveryId)
    {
        var existing = await GetItemAsync(deliveryId, consistentRead: true);
        if (existing == null)
        {
            return false;
        }

        var tripId = existing.TryGetValue("tripId", out var tripAttr) ? tripAttr.S ?? "" : "";
        var now = DateTime.UtcNow.ToString("o");
        var request = new UpdateItemRequest
        {
            TableName = _tableName,
            Key = new Dictionary<string, AttributeValue>
            {
                { "deliveryId", new AttributeValue { S = deliveryId } },
                { "tripId", new AttributeValue { S = tripId } }
            },
            UpdateExpression = "SET #status = :cancelled, #updated = :now ADD #v :one",
            ConditionExpression = "#status = :pending",
            ExpressionAttributeNames = new Dictionary<string, string>
            {
                { "#status", "status" }, { "#updated", "updatedAt" }, { "#v", "version" }
            },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                { ":cancelled", new AttributeValue { S = "cancelled" } },
                { ":pending", new AttributeValue { S = "pending" } },
                { ":now", new AttributeValue { S = now } },
                { ":one", new AttributeValue { N = "1" } }
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
    public async Task<bool> TryCloseForVerdictAsync(
        string deliveryId, string fromStatus, string toStatus, string resolution, string resolvedAt, string? cancelReason)
    {
        var existing = await GetItemAsync(deliveryId, consistentRead: true);
        if (existing == null)
        {
            return false;
        }

        var tripId = existing.TryGetValue("tripId", out var tripAttr) ? tripAttr.S ?? "" : "";
        var now = DateTime.UtcNow.ToString("o");
        var names = new Dictionary<string, string>
        {
            { "#status", "status" }, { "#updated", "updatedAt" }, { "#v", "version" },
            { "#resolution", "disputeResolution" }, { "#resolvedAt", "disputeResolvedAt" }
        };
        var values = new Dictionary<string, AttributeValue>
        {
            { ":to", new AttributeValue { S = toStatus } },
            { ":from", new AttributeValue { S = fromStatus } },
            { ":resolution", new AttributeValue { S = resolution } },
            { ":resolvedAt", new AttributeValue { S = resolvedAt } },
            { ":now", new AttributeValue { S = now } },
            { ":one", new AttributeValue { N = "1" } }
        };
        var set = "#status = :to, #updated = :now, #resolution = :resolution, #resolvedAt = :resolvedAt";
        if (toStatus == "cancelled")
        {
            names["#cancelReason"] = "cancelReason";
            names["#cancelledAt"] = "cancelledAt";
            values[":cancelReason"] = new AttributeValue { S = cancelReason ?? "" };
            set += ", #cancelReason = :cancelReason, #cancelledAt = :now";
        }
        else
        {
            // The goods count as delivered; keep a real confirmation time if the handoff had one.
            names["#confirmedAt"] = "confirmedAt";
            set += ", #confirmedAt = if_not_exists(#confirmedAt, :now)";
        }

        var request = new UpdateItemRequest
        {
            TableName = _tableName,
            Key = new Dictionary<string, AttributeValue>
            {
                { "deliveryId", new AttributeValue { S = deliveryId } },
                { "tripId", new AttributeValue { S = tripId } }
            },
            UpdateExpression = "SET " + set + " ADD #v :one",
            ConditionExpression = "#status = :from AND attribute_not_exists(#resolution)",
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
    public async Task<bool> TryMarkInTransitAsync(string deliveryId, string tripId, int expectedVersion)
    {
        var request = new UpdateItemRequest
        {
            TableName = _tableName,
            Key = new Dictionary<string, AttributeValue>
            {
                { "deliveryId", new AttributeValue { S = deliveryId } },
                { "tripId", new AttributeValue { S = tripId } }
            },
            UpdateExpression = "SET #status = :inTransit, #updated = :now, #v = :next",
            ConditionExpression = "#status = :pickedUp AND #v = :expected",
            ExpressionAttributeNames = new Dictionary<string, string>
            {
                { "#status", "status" }, { "#updated", "updatedAt" }, { "#v", "version" }
            },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                { ":inTransit", new AttributeValue { S = "inTransit" } },
                { ":pickedUp", new AttributeValue { S = "pickedUp" } },
                { ":now", new AttributeValue { S = DateTime.UtcNow.ToString("o") } },
                { ":expected", new AttributeValue { N = expectedVersion.ToString(System.Globalization.CultureInfo.InvariantCulture) } },
                { ":next", new AttributeValue { N = (expectedVersion + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) } }
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
    public async Task<bool> TryStampNearbyAsync(
        string deliveryId, string tripId, string notifiedAt, double distanceKm, int? minutes)
    {
        var names = new Dictionary<string, string>
        {
            { "#status", "status" }, { "#at", "nearbyNotifiedAt" }, { "#km", "nearbyDistanceKm" }
        };
        var values = new Dictionary<string, AttributeValue>
        {
            { ":pickedUp", new AttributeValue { S = "pickedUp" } },
            { ":inTransit", new AttributeValue { S = "inTransit" } },
            { ":at", new AttributeValue { S = notifiedAt } },
            { ":km", new AttributeValue { N = distanceKm.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) } }
        };
        var set = "#at = :at, #km = :km";
        if (minutes is { } m)
        {
            names["#min"] = "nearbyMinutes";
            values[":min"] = new AttributeValue { N = m.ToString(System.Globalization.CultureInfo.InvariantCulture) };
            set += ", #min = :min";
        }

        var request = new UpdateItemRequest
        {
            TableName = _tableName,
            Key = new Dictionary<string, AttributeValue>
            {
                { "deliveryId", new AttributeValue { S = deliveryId } },
                { "tripId", new AttributeValue { S = tripId } }
            },
            // No version and no updatedAt: a server-only stamp must not make a party's queued update conflict.
            UpdateExpression = "SET " + set,
            ConditionExpression = "#status IN (:pickedUp, :inTransit) AND attribute_not_exists(#at)",
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

    /// <summary>
    /// Query for an item by deliveryId (partition key). Since sort key is createdAt,
    /// we query with limit 1 to get the single matching item.
    /// </summary>
    private async Task<Dictionary<string, AttributeValue>?> GetItemAsync(string deliveryId, bool consistentRead = false)
    {
        var request = new QueryRequest
        {
            TableName = _tableName,
            KeyConditionExpression = "deliveryId = :id",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                { ":id", new AttributeValue { S = deliveryId } }
            },
            Limit = 1,
            ConsistentRead = consistentRead
        };

        var response = await _dynamoDb.QueryAsync(request);
        return response.Items.Count > 0 ? response.Items[0] : null;
    }
}
