using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;

namespace WeredaConnect.Server.Data;

/// <summary>
/// DynamoDB trip listing repository using low-level API for AOT compatibility.
/// Manual marshalling — no Document Model or reflection.
///
/// Table schema (Trips):
///   PK: tripId (STRING)
///   SK: createdAt (STRING)
///   GSI StatusDepartureDateIndex: status (PK) + departureDate (SK)
///   GSI TruckerIdCreatedAtIndex: truckerId (PK) + createdAt (SK)
/// </summary>
public sealed class TripRepository : ITripRepository
{
    private readonly IAmazonDynamoDB _dynamoDb;
    private readonly string _tableName;

    public TripRepository(IAmazonDynamoDB dynamoDb, string tableName)
    {
        _dynamoDb = dynamoDb ?? throw new ArgumentNullException(nameof(dynamoDb));
        _tableName = tableName ?? throw new ArgumentNullException(nameof(tableName));
    }

    /// <inheritdoc />
    public async Task<(List<Dictionary<string, object>> Items, Dictionary<string, AttributeValue>? LastEvaluatedKey)> QueryTripsAsync(
        string? fromWoreda = null,
        string? toWoreda = null,
        string? status = null,
        int limit = 20,
        Dictionary<string, AttributeValue>? exclusiveStartKey = null)
    {
        if (limit < 1 || limit > 50)
        {
            limit = 20;
        }

        // Default status to "open" for GSI query
        var queryStatus = string.IsNullOrEmpty(status) ? "open" : status;

        // Use StatusDepartureDateIndex GSI: PK = status, SK = departureDate
        var request = new QueryRequest
        {
            TableName = _tableName,
            IndexName = "StatusDepartureDateIndex",
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
            ScanIndexForward = true // ascending by departureDate (soonest first)
        };

        if (exclusiveStartKey != null)
        {
            request.ExclusiveStartKey = exclusiveStartKey;
        }

        // Build filter expressions for fromWoreda/toWoreda
        var filterParts = new List<string>();

        // Exclude deleted items
        filterParts.Add("attribute_not_exists(#deletedAt)");
        request.ExpressionAttributeNames["#deletedAt"] = "deletedAt";

        if (!string.IsNullOrEmpty(fromWoreda))
        {
            filterParts.Add("#fromWoreda = :fromWoreda");
            request.ExpressionAttributeNames["#fromWoreda"] = "fromWoreda";
            request.ExpressionAttributeValues[":fromWoreda"] = new AttributeValue { S = fromWoreda };
        }

        if (!string.IsNullOrEmpty(toWoreda))
        {
            filterParts.Add("#toWoreda = :toWoreda");
            request.ExpressionAttributeNames["#toWoreda"] = "toWoreda";
            request.ExpressionAttributeValues[":toWoreda"] = new AttributeValue { S = toWoreda };
        }

        if (filterParts.Count > 0)
        {
            request.FilterExpression = string.Join(" AND ", filterParts);
        }

        var response = await _dynamoDb.QueryAsync(request);

        var items = response.Items
            .Select(CropRepository.FromItem)
            .ToList();

        return (items, response.LastEvaluatedKey.Count > 0 ? response.LastEvaluatedKey : null);
    }

    /// <inheritdoc />
    public async Task<(List<Dictionary<string, object>> Items, Dictionary<string, AttributeValue>? LastEvaluatedKey)> QueryTripsByTruckerAsync(
        string truckerId,
        string? status = null,
        int limit = 20,
        Dictionary<string, AttributeValue>? exclusiveStartKey = null)
    {
        if (limit < 1 || limit > 50)
        {
            limit = 20;
        }

        // Use TruckerIdCreatedAtIndex GSI: PK = truckerId, SK = createdAt
        var request = new QueryRequest
        {
            TableName = _tableName,
            IndexName = "TruckerIdCreatedAtIndex",
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
            ScanIndexForward = false // descending by createdAt (newest first)
        };

        if (exclusiveStartKey != null)
        {
            request.ExclusiveStartKey = exclusiveStartKey;
        }

        // Build filter expressions
        var filterParts = new List<string>();

        // Exclude deleted items
        filterParts.Add("attribute_not_exists(#deletedAt)");
        request.ExpressionAttributeNames["#deletedAt"] = "deletedAt";

        // Optional status filter
        if (!string.IsNullOrEmpty(status))
        {
            filterParts.Add("#status = :status");
            request.ExpressionAttributeNames["#status"] = "status";
            request.ExpressionAttributeValues[":status"] = new AttributeValue { S = status };
        }

        if (filterParts.Count > 0)
        {
            request.FilterExpression = string.Join(" AND ", filterParts);
        }

        var response = await _dynamoDb.QueryAsync(request);

        var items = response.Items
            .Select(CropRepository.FromItem)
            .ToList();

        return (items, response.LastEvaluatedKey.Count > 0 ? response.LastEvaluatedKey : null);
    }

    /// <inheritdoc />
    public async Task<Dictionary<string, object>?> GetByIdAsync(string tripId)
    {
        var item = await GetItemAsync(tripId);
        return item != null ? CropRepository.FromItem(item) : null;
    }

    /// <summary>
    /// Query for an item by tripId (partition key). Since sort key is createdAt,
    /// we query with limit 1 to get the single matching item.
    /// </summary>
    private async Task<Dictionary<string, AttributeValue>?> GetItemAsync(string tripId)
    {
        var request = new QueryRequest
        {
            TableName = _tableName,
            KeyConditionExpression = "tripId = :id",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                { ":id", new AttributeValue { S = tripId } }
            },
            Limit = 1
        };

        var response = await _dynamoDb.QueryAsync(request);
        return response.Items.Count > 0 ? response.Items[0] : null;
    }
}
