using System.Globalization;
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
///   GSI StatusCreatedAtIndex: status + createdAt
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
    /// Handles string, number, boolean types (AOT-safe, no reflection).
    /// </summary>
    internal static AttributeValue ToAttributeValue(object value)
    {
        return value switch
        {
            string s => new AttributeValue { S = s },
            int n => new AttributeValue { N = n.ToString(CultureInfo.InvariantCulture) },
            long n => new AttributeValue { N = n.ToString(CultureInfo.InvariantCulture) },
            double d => new AttributeValue { N = d.ToString(CultureInfo.InvariantCulture) },
            float f => new AttributeValue { N = f.ToString(CultureInfo.InvariantCulture) },
            decimal m => new AttributeValue { N = m.ToString(CultureInfo.InvariantCulture) },
            bool b => new AttributeValue { BOOL = b },
            null => new AttributeValue { NULL = true },
            // For System.Text.Json deserialized numbers (JsonElement)
            System.Text.Json.JsonElement je => JsonElementToAttributeValue(je),
            _ => new AttributeValue { S = value.ToString() ?? "" }
        };
    }

    /// <summary>
    /// Convert a JsonElement to AttributeValue (handles STJ deserialization).
    /// </summary>
    private static AttributeValue JsonElementToAttributeValue(System.Text.Json.JsonElement element)
    {
        return element.ValueKind switch
        {
            System.Text.Json.JsonValueKind.String => new AttributeValue { S = element.GetString() ?? "" },
            System.Text.Json.JsonValueKind.Number => new AttributeValue { N = element.GetRawText() },
            System.Text.Json.JsonValueKind.True => new AttributeValue { BOOL = true },
            System.Text.Json.JsonValueKind.False => new AttributeValue { BOOL = false },
            System.Text.Json.JsonValueKind.Null => new AttributeValue { NULL = true },
            _ => new AttributeValue { S = element.GetRawText() }
        };
    }

    /// <summary>
    /// Convert a DynamoDB item to a plain dictionary (AOT-safe, no reflection).
    /// </summary>
    internal static Dictionary<string, object> FromItem(Dictionary<string, AttributeValue> item)
    {
        var result = new Dictionary<string, object>();

        foreach (var kvp in item)
        {
            if (kvp.Value.S != null)
            {
                result[kvp.Key] = kvp.Value.S;
            }
            else if (kvp.Value.N != null)
            {
                result[kvp.Key] = kvp.Value.N;
            }
            else if (kvp.Value.IsBOOLSet)
            {
                result[kvp.Key] = kvp.Value.BOOL;
            }
            else if (kvp.Value.NULL)
            {
                continue; // skip nulls
            }
            else if (kvp.Value.M != null && kvp.Value.M.Count > 0)
            {
                result[kvp.Key] = FromItem(kvp.Value.M); // recursive Map handling
            }
            else if (kvp.Value.L != null && kvp.Value.L.Count > 0)
            {
                result[kvp.Key] = FromList(kvp.Value.L);
            }
        }

        return result;
    }

    /// <summary>
    /// Convert a DynamoDB List attribute to a plain list (AOT-safe, no reflection).
    /// </summary>
    internal static List<object> FromList(List<AttributeValue> list)
    {
        var result = new List<object>();

        foreach (var attr in list)
        {
            if (attr.S != null)
            {
                result.Add(attr.S);
            }
            else if (attr.N != null)
            {
                result.Add(attr.N);
            }
            else if (attr.IsBOOLSet)
            {
                result.Add(attr.BOOL);
            }
            else if (attr.NULL)
            {
                continue; // skip nulls
            }
            else if (attr.M != null && attr.M.Count > 0)
            {
                result.Add(FromItem(attr.M)); // recursive Map handling
            }
            else if (attr.L != null && attr.L.Count > 0)
            {
                result.Add(FromList(attr.L)); // recursive List handling
            }
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<(List<Dictionary<string, object>> Items, Dictionary<string, AttributeValue>? LastEvaluatedKey)> QueryCropsAsync(
        string? woreda = null,
        string? cropType = null,
        string? status = null,
        int limit = 20,
        Dictionary<string, AttributeValue>? exclusiveStartKey = null)
    {
        // Validate limit
        if (limit < 1 || limit > 100)
        {
            limit = 20;
        }

        // Build the query based on available filters
        // Note: For this implementation, we'll use a Scan with filters since we don't have
        // a specific GSI configured yet. In production, you'd want a GSI on status+createdAt
        // or woreda+createdAt for better performance.

        var request = new ScanRequest
        {
            TableName = _tableName,
            Limit = limit
        };

        if (exclusiveStartKey != null)
        {
            request.ExclusiveStartKey = exclusiveStartKey;
        }

        // Build filter expression
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

        if (!string.IsNullOrEmpty(cropType))
        {
            filterParts.Add("#cropType = :cropType");
            attrNames["#cropType"] = "cropType";
            attrValues[":cropType"] = new AttributeValue { S = cropType };
        }

        if (!string.IsNullOrEmpty(status))
        {
            filterParts.Add("#status = :status");
            attrNames["#status"] = "status";
            attrValues[":status"] = new AttributeValue { S = status };
        }

        if (filterParts.Count > 0)
        {
            request.FilterExpression = string.Join(" AND ", filterParts);
            request.ExpressionAttributeNames = attrNames;

            if (attrValues.Count > 0)
            {
                request.ExpressionAttributeValues = attrValues;
            }
        }

        var response = await _dynamoDb.ScanAsync(request);

        var items = response.Items
            .Select(FromItem)
            .ToList();

        return (items, response.LastEvaluatedKey.Count > 0 ? response.LastEvaluatedKey : null);
    }
}
