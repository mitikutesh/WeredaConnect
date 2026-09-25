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
        var version = existing.TryGetValue("version", out var vAttr)
            && int.TryParse(vAttr.N, out var parsed) ? parsed : 1;
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
            { ":newVersion", new AttributeValue { N = (version + 1).ToString() } },
            { ":disputedBy", new AttributeValue { S = disputedBy } }
        };
        var setParts = "#status = :disputed, #updated = :updated, #v = :newVersion, #disputedBy = :disputedBy";
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
            UpdateExpression = "SET " + setParts,
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

    /// <summary>
    /// Query for an item by deliveryId (partition key). Since sort key is createdAt,
    /// we query with limit 1 to get the single matching item.
    /// </summary>
    private async Task<Dictionary<string, AttributeValue>?> GetItemAsync(string deliveryId)
    {
        var request = new QueryRequest
        {
            TableName = _tableName,
            KeyConditionExpression = "deliveryId = :id",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                { ":id", new AttributeValue { S = deliveryId } }
            },
            Limit = 1
        };

        var response = await _dynamoDb.QueryAsync(request);
        return response.Items.Count > 0 ? response.Items[0] : null;
    }
}
