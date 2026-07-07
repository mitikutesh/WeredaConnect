using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using WeredaConnect.Server.Services;

namespace WeredaConnect.Server.Data;

/// <summary>
/// DynamoDB crop listing repository using low-level API for AOT compatibility.
/// Manual marshalling — no Document Model or reflection.
/// Version-based optimistic concurrency for sync conflict detection.
///
/// Table schema (Crops):
///   PK: cropId (STRING)
///   SK: createdAt (STRING)
///   GSI FarmerIdCreatedAtIndex: farmerId + createdAt
///   GSI StatusCreatedAtIndex: status + createdAt
///   GSI CropTypeCreatedAtIndex: cropType + createdAt
/// </summary>
public sealed class CropRepository : ICropRepository
{
    private readonly IAmazonDynamoDB _dynamoDb;
    private readonly string _tableName;

    public CropRepository(IAmazonDynamoDB dynamoDb, string tableName)
    {
        _dynamoDb = dynamoDb ?? throw new ArgumentNullException(nameof(dynamoDb));
        _tableName = tableName ?? throw new ArgumentNullException(nameof(tableName));
    }

    /// <inheritdoc />
    public async Task<AuthResult<Dictionary<string, object>>> CreateAsync(
        string cropId,
        Dictionary<string, object> data)
    {
        var now = DateTime.UtcNow.ToString("o");

        var item = new Dictionary<string, AttributeValue>
        {
            { "cropId", new AttributeValue { S = cropId } },
            { "createdAt", new AttributeValue { S = now } },
            { "updatedAt", new AttributeValue { S = now } },
            { "version", new AttributeValue { N = "1" } }
        };

        // Copy data fields into the item
        foreach (var kvp in data)
        {
            if (kvp.Key is "cropId" or "version" or "createdAt" or "updatedAt")
            {
                continue;
            }

            item[kvp.Key] = ToAttributeValue(kvp.Value);
        }

        var request = new PutItemRequest
        {
            TableName = _tableName,
            Item = item,
            ConditionExpression = "attribute_not_exists(cropId)"
        };

        try
        {
            await _dynamoDb.PutItemAsync(request);

            var result = FromItem(item);
            return AuthResult<Dictionary<string, object>>.Success(result);
        }
        catch (ConditionalCheckFailedException)
        {
            return AuthResult<Dictionary<string, object>>.Failure(
                409, "SYNC_CONFLICT", "Crop listing already exists.", "cropId");
        }
    }

    /// <inheritdoc />
    public async Task<AuthResult<Dictionary<string, object>>> UpdateAsync(
        string cropId,
        Dictionary<string, object> data,
        int expectedVersion)
    {
        // First, get the current item to know its sort key
        var existing = await GetItemAsync(cropId);
        if (existing == null)
        {
            return AuthResult<Dictionary<string, object>>.Failure(
                404, "SYNC_VALIDATION", "Crop listing not found.", "cropId");
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
            if (kvp.Key is "cropId" or "version" or "createdAt" or "updatedAt")
            {
                continue;
            }

            var nameAlias = $"#f{i}";
            var valueAlias = $":f{i}";
            attrNames[nameAlias] = kvp.Key;
            attrValues[valueAlias] = ToAttributeValue(kvp.Value);
            updateParts.Add($"{nameAlias} = {valueAlias}");
            i++;
        }

        var request = new UpdateItemRequest
        {
            TableName = _tableName,
            Key = new Dictionary<string, AttributeValue>
            {
                { "cropId", new AttributeValue { S = cropId } },
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
            var result = FromItem(response.Attributes);
            return AuthResult<Dictionary<string, object>>.Success(result);
        }
        catch (ConditionalCheckFailedException)
        {
            // Version mismatch — return server's current data
            var serverData = FromItem(existing);
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
    public async Task<AuthResult<Dictionary<string, object>>> DeleteAsync(
        string cropId,
        int expectedVersion)
    {
        var existing = await GetItemAsync(cropId);
        if (existing == null)
        {
            return AuthResult<Dictionary<string, object>>.Failure(
                404, "SYNC_VALIDATION", "Crop listing not found.", "cropId");
        }

        var createdAt = existing["createdAt"].S;
        var now = DateTime.UtcNow.ToString("o");
        var newVersion = expectedVersion + 1;

        // Soft delete: set deletedAt timestamp and status to deleted
        var request = new UpdateItemRequest
        {
            TableName = _tableName,
            Key = new Dictionary<string, AttributeValue>
            {
                { "cropId", new AttributeValue { S = cropId } },
                { "createdAt", new AttributeValue { S = createdAt } }
            },
            UpdateExpression = "SET #deleted = :deleted, #status = :status, #updated = :updated, #v = :newVersion",
            ConditionExpression = "#v = :expectedVersion",
            ExpressionAttributeNames = new Dictionary<string, string>
            {
                { "#deleted", "deletedAt" },
                { "#status", "status" },
                { "#updated", "updatedAt" },
                { "#v", "version" }
            },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                { ":deleted", new AttributeValue { S = now } },
                { ":status", new AttributeValue { S = "deleted" } },
                { ":updated", new AttributeValue { S = now } },
                { ":newVersion", new AttributeValue { N = newVersion.ToString() } },
                { ":expectedVersion", new AttributeValue { N = expectedVersion.ToString() } }
            },
            ReturnValues = ReturnValue.ALL_NEW
        };

        try
        {
            var response = await _dynamoDb.UpdateItemAsync(request);
            var result = FromItem(response.Attributes);
            return AuthResult<Dictionary<string, object>>.Success(result);
        }
        catch (ConditionalCheckFailedException)
        {
            return AuthResult<Dictionary<string, object>>.Failure(
                409, "SYNC_CONFLICT", "Version conflict on delete.", "version");
        }
    }

    /// <inheritdoc />
    public async Task<Dictionary<string, object>?> GetByIdAsync(string cropId)
    {
        var item = await GetItemAsync(cropId);
        return item != null ? FromItem(item) : null;
    }

    /// <summary>
    /// Query for an item by cropId (partition key). Since sort key is createdAt,
    /// we query with limit 1 to get the single matching item.
    /// </summary>
    private async Task<Dictionary<string, AttributeValue>?> GetItemAsync(string cropId)
    {
        var request = new QueryRequest
        {
            TableName = _tableName,
            KeyConditionExpression = "cropId = :id",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                { ":id", new AttributeValue { S = cropId } }
            },
            Limit = 1
        };

        var response = await _dynamoDb.QueryAsync(request);
        return response.Items.Count > 0 ? response.Items[0] : null;
    }

    /// <summary>
    /// Convert an object value to a DynamoDB AttributeValue.
    /// Delegates to <see cref="DynamoDbHelper.ToAttributeValue"/>.
    /// </summary>
    internal static AttributeValue ToAttributeValue(object value)
        => DynamoDbHelper.ToAttributeValue(value);

    /// <summary>
    /// Convert a DynamoDB item to a plain dictionary.
    /// Delegates to <see cref="DynamoDbHelper.FromItem"/>.
    /// </summary>
    internal static Dictionary<string, object> FromItem(Dictionary<string, AttributeValue> item)
        => DynamoDbHelper.FromItem(item);

    /// <summary>
    /// Convert a DynamoDB List attribute to a plain list.
    /// Delegates to <see cref="DynamoDbHelper.FromList"/>.
    /// </summary>
    internal static List<object> FromList(List<AttributeValue> list)
        => DynamoDbHelper.FromList(list);

    /// <inheritdoc />
    public async Task<(List<Dictionary<string, object>> Items, Dictionary<string, AttributeValue>? LastEvaluatedKey)> QueryCropsAsync(
        string? farmerId = null,
        string? woreda = null,
        string? cropType = null,
        string? status = null,
        int limit = 20,
        Dictionary<string, AttributeValue>? exclusiveStartKey = null,
        double? maxPricePerKg = null)
    {
        // Validate limit
        if (limit < 1 || limit > 100)
        {
            limit = 20;
        }

        // Route to the best GSI based on which filter is provided.
        // Priority: farmerId > status > cropType > fallback SCAN.
        // Each GSI path uses KeyConditionExpression for the partition key
        // and pushes remaining filters into FilterExpression.
        if (!string.IsNullOrEmpty(farmerId))
        {
            return await QueryByFarmerIdAsync(farmerId, woreda, cropType, status, limit, exclusiveStartKey);
        }

        if (!string.IsNullOrEmpty(status))
        {
            return await QueryByStatusAsync(status, woreda, cropType, limit, exclusiveStartKey);
        }

        if (!string.IsNullOrEmpty(cropType))
        {
            return await QueryByCropTypeAsync(cropType, woreda, limit, exclusiveStartKey, maxPricePerKg);
        }

        // Fallback: unfiltered browse — SCAN (unchanged)
        return await ScanFallbackAsync(woreda, limit, exclusiveStartKey);
    }

    /// <summary>
    /// Query FarmerIdCreatedAtIndex GSI: PK=farmerId, SK=createdAt (descending).
    /// GSI uses INCLUDE projection (only cropType, pricePerKg, deletedAt), so we
    /// do a two-step query: GSI for keys → BatchGetItem for full items.
    /// Remaining filters (woreda, cropType, status) applied as FilterExpression on GSI.
    /// </summary>
    private async Task<(List<Dictionary<string, object>> Items, Dictionary<string, AttributeValue>? LastEvaluatedKey)> QueryByFarmerIdAsync(
        string farmerId,
        string? woreda,
        string? cropType,
        string? status,
        int limit,
        Dictionary<string, AttributeValue>? exclusiveStartKey)
    {
        var filterParts = new List<string>();
        var attrNames = new Dictionary<string, string>();
        var attrValues = new Dictionary<string, AttributeValue>
        {
            { ":farmerId", new AttributeValue { S = farmerId } }
        };

        // Always exclude soft-deleted items
        filterParts.Add("attribute_not_exists(#deletedAt)");
        attrNames["#deletedAt"] = "deletedAt";

        // These filters use attributes available in the GSI INCLUDE projection
        if (!string.IsNullOrEmpty(cropType))
        {
            filterParts.Add("#cropType = :cropType");
            attrNames["#cropType"] = "cropType";
            attrValues[":cropType"] = new AttributeValue { S = cropType };
        }

        var request = new QueryRequest
        {
            TableName = _tableName,
            IndexName = "FarmerIdCreatedAtIndex",
            KeyConditionExpression = "farmerId = :farmerId",
            ScanIndexForward = false, // newest first
            Limit = limit,
            ExpressionAttributeValues = attrValues
        };

        if (filterParts.Count > 0)
        {
            request.FilterExpression = string.Join(" AND ", filterParts);
            request.ExpressionAttributeNames = attrNames;
        }

        if (exclusiveStartKey != null)
        {
            request.ExclusiveStartKey = exclusiveStartKey;
        }

        var gsiResponse = await _dynamoDb.QueryAsync(request);
        var lastKey = gsiResponse.LastEvaluatedKey.Count > 0 ? gsiResponse.LastEvaluatedKey : null;

        if (gsiResponse.Items.Count == 0)
        {
            return (new List<Dictionary<string, object>>(), lastKey);
        }

        // Step 2: BatchGetItem from main table for full items
        var fullItems = await BatchGetFullItemsAsync(gsiResponse.Items);

        // Apply remaining filters that weren't in the GSI projection
        if (!string.IsNullOrEmpty(woreda) || !string.IsNullOrEmpty(status))
        {
            fullItems = fullItems.Where(item =>
            {
                if (!string.IsNullOrEmpty(woreda) && GetStringOrNull(item, "woreda") != woreda)
                    return false;
                if (!string.IsNullOrEmpty(status) && GetStringOrNull(item, "status") != status)
                    return false;
                return true;
            }).ToList();
        }

        return (fullItems, lastKey);
    }

    private static string? GetStringOrNull(Dictionary<string, object> item, string key)
        => item.TryGetValue(key, out var val) ? val?.ToString() : null;

    /// <summary>
    /// Fetch full items from the main table given GSI result items (which contain primary keys).
    /// </summary>
    private async Task<List<Dictionary<string, object>>> BatchGetFullItemsAsync(
        List<Dictionary<string, AttributeValue>> gsiItems)
    {
        // Build keys from GSI items (main table PK=cropId, SK=createdAt)
        var keys = gsiItems
            .Select(item => new Dictionary<string, AttributeValue>
            {
                { "cropId", item.ContainsKey("cropId") ? item["cropId"] : new AttributeValue { S = "" } },
                { "createdAt", item.ContainsKey("createdAt") ? item["createdAt"] : new AttributeValue { S = "" } }
            })
            .Where(k => k["cropId"].S != "")
            .ToList();

        if (keys.Count == 0)
        {
            return new List<Dictionary<string, object>>();
        }

        // BatchGetItem (max 100 items per call, our limit is at most 100)
        var batchRequest = new BatchGetItemRequest
        {
            RequestItems = new Dictionary<string, KeysAndAttributes>
            {
                {
                    _tableName, new KeysAndAttributes { Keys = keys }
                }
            }
        };

        var batchResponse = await _dynamoDb.BatchGetItemAsync(batchRequest);
        var results = new List<Dictionary<string, object>>();

        if (batchResponse.Responses.TryGetValue(_tableName, out var tableItems))
        {
            results = tableItems.Select(FromItem).ToList();
        }

        return results;
    }

    /// <summary>
    /// Query StatusCreatedAtIndex GSI: PK=status, SK=createdAt (descending).
    /// Remaining filters (woreda, cropType) applied as FilterExpression.
    /// </summary>
    private async Task<(List<Dictionary<string, object>> Items, Dictionary<string, AttributeValue>? LastEvaluatedKey)> QueryByStatusAsync(
        string status,
        string? woreda,
        string? cropType,
        int limit,
        Dictionary<string, AttributeValue>? exclusiveStartKey)
    {
        var filterParts = new List<string>();
        var attrNames = new Dictionary<string, string>();
        var attrValues = new Dictionary<string, AttributeValue>
        {
            { ":status", new AttributeValue { S = status } }
        };

        // Always exclude soft-deleted items
        filterParts.Add("attribute_not_exists(#deletedAt)");
        attrNames["#deletedAt"] = "deletedAt";

        if (!string.IsNullOrEmpty(woreda))
        {
            filterParts.Add("#woreda = :woreda");
            attrNames["#woreda"] = "woreda";
            attrValues[":woreda"] = new AttributeValue { S = woreda };
        }

        if (!string.IsNullOrEmpty(cropType))
        {
            filterParts.Add("#cropType = :cropType");
            attrNames["#cropType"] = "cropType";
            attrValues[":cropType"] = new AttributeValue { S = cropType };
        }

        var request = new QueryRequest
        {
            TableName = _tableName,
            IndexName = "StatusCreatedAtIndex",
            KeyConditionExpression = "#st = :status",
            ScanIndexForward = false, // newest first
            Limit = limit,
            ExpressionAttributeNames = new Dictionary<string, string>(attrNames)
            {
                { "#st", "status" }
            },
            ExpressionAttributeValues = attrValues
        };

        if (filterParts.Count > 0)
        {
            request.FilterExpression = string.Join(" AND ", filterParts);
        }

        if (exclusiveStartKey != null)
        {
            request.ExclusiveStartKey = exclusiveStartKey;
        }

        var response = await _dynamoDb.QueryAsync(request);
        var items = response.Items.Select(FromItem).ToList();
        return (items, response.LastEvaluatedKey.Count > 0 ? response.LastEvaluatedKey : null);
    }

    /// <summary>
    /// Query CropTypeCreatedAtIndex GSI: PK=cropType, SK=createdAt (descending).
    /// GSI uses INCLUDE projection (only pricePerKg, deletedAt), so we do a
    /// two-step query: GSI for keys → BatchGetItem for full items.
    /// The price ceiling (when given) is filtered on the projected pricePerKg
    /// BEFORE the full-item fetch, so over-budget crops cost no BatchGet reads
    /// and candidate pages contain only price-eligible listings.
    /// Remaining filter (woreda) applied post-fetch on full items.
    /// </summary>
    private async Task<(List<Dictionary<string, object>> Items, Dictionary<string, AttributeValue>? LastEvaluatedKey)> QueryByCropTypeAsync(
        string cropType,
        string? woreda,
        int limit,
        Dictionary<string, AttributeValue>? exclusiveStartKey,
        double? maxPricePerKg = null)
    {
        var filterParts = new List<string>();
        var attrNames = new Dictionary<string, string>();
        var attrValues = new Dictionary<string, AttributeValue>
        {
            { ":cropType", new AttributeValue { S = cropType } }
        };

        // Always exclude soft-deleted items
        filterParts.Add("attribute_not_exists(#deletedAt)");
        attrNames["#deletedAt"] = "deletedAt";

        if (maxPricePerKg is double maxPrice && maxPrice > 0)
        {
            filterParts.Add("#pricePerKg <= :maxPrice");
            attrNames["#pricePerKg"] = "pricePerKg";
            attrValues[":maxPrice"] = new AttributeValue
            {
                N = maxPrice.ToString(System.Globalization.CultureInfo.InvariantCulture)
            };
        }

        var request = new QueryRequest
        {
            TableName = _tableName,
            IndexName = "CropTypeCreatedAtIndex",
            KeyConditionExpression = "cropType = :cropType",
            ScanIndexForward = false, // newest first
            Limit = limit,
            ExpressionAttributeValues = attrValues
        };

        if (filterParts.Count > 0)
        {
            request.FilterExpression = string.Join(" AND ", filterParts);
            request.ExpressionAttributeNames = attrNames;
        }

        if (exclusiveStartKey != null)
        {
            request.ExclusiveStartKey = exclusiveStartKey;
        }

        var gsiResponse = await _dynamoDb.QueryAsync(request);
        var lastKey = gsiResponse.LastEvaluatedKey.Count > 0 ? gsiResponse.LastEvaluatedKey : null;

        if (gsiResponse.Items.Count == 0)
        {
            return (new List<Dictionary<string, object>>(), lastKey);
        }

        // Step 2: BatchGetItem from main table for full items
        var fullItems = await BatchGetFullItemsAsync(gsiResponse.Items);

        // Apply remaining filters that weren't in the GSI projection
        if (!string.IsNullOrEmpty(woreda))
        {
            fullItems = fullItems.Where(item =>
                GetStringOrNull(item, "woreda") == woreda).ToList();
        }

        return (fullItems, lastKey);
    }

    /// <summary>
    /// Fallback SCAN for unfiltered browse (no farmerId, status, or cropType).
    /// Only woreda filter may be applied. Excludes soft-deleted items.
    /// </summary>
    private async Task<(List<Dictionary<string, object>> Items, Dictionary<string, AttributeValue>? LastEvaluatedKey)> ScanFallbackAsync(
        string? woreda,
        int limit,
        Dictionary<string, AttributeValue>? exclusiveStartKey)
    {
        var filterParts = new List<string>();
        var attrNames = new Dictionary<string, string>();
        var attrValues = new Dictionary<string, AttributeValue>();

        // Exclude deleted items by default
        filterParts.Add("attribute_not_exists(#deletedAt)");
        attrNames["#deletedAt"] = "deletedAt";

        if (!string.IsNullOrEmpty(woreda))
        {
            filterParts.Add("#woreda = :woreda");
            attrNames["#woreda"] = "woreda";
            attrValues[":woreda"] = new AttributeValue { S = woreda };
        }

        var request = new ScanRequest
        {
            TableName = _tableName,
            Limit = limit,
            FilterExpression = string.Join(" AND ", filterParts),
            ExpressionAttributeNames = attrNames
        };

        if (attrValues.Count > 0)
        {
            request.ExpressionAttributeValues = attrValues;
        }

        if (exclusiveStartKey != null)
        {
            request.ExclusiveStartKey = exclusiveStartKey;
        }

        var response = await _dynamoDb.ScanAsync(request);
        var items = response.Items.Select(FromItem).ToList();
        return (items, response.LastEvaluatedKey.Count > 0 ? response.LastEvaluatedKey : null);
    }
}
