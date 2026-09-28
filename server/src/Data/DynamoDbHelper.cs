using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;

namespace WeredaConnect.Server.Data;

/// <summary>
/// Shared DynamoDB marshalling helpers used across all repository classes.
/// AOT-compatible — no reflection.
/// </summary>
internal static class DynamoDbHelper
{
    /// <summary>
    /// True when any row already has this partition key. Tables keyed (id, createdAt)
    /// need this before a create: createdAt is stamped fresh on every create, so the
    /// put's attribute_not_exists never sees an existing row with the same id and a
    /// replayed create would add a duplicate row (SIM-39).
    /// Check-then-put is not atomic: two creates with the same id but different
    /// idempotency keys landing within milliseconds can still both write. Accepted:
    /// only the caller's own entity is duplicated, and an atomic claim needs either a
    /// lock row (which the admin/order table scans would count) or a cross-table
    /// transaction.
    /// </summary>
    internal static async Task<bool> PartitionExistsAsync(
        IAmazonDynamoDB dynamoDb, string tableName, string keyName, string keyValue)
    {
        var response = await dynamoDb.QueryAsync(new QueryRequest
        {
            TableName = tableName,
            KeyConditionExpression = "#k = :v",
            ExpressionAttributeNames = new Dictionary<string, string> { ["#k"] = keyName },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                [":v"] = new AttributeValue { S = keyValue }
            },
            ProjectionExpression = "#k",
            ConsistentRead = true,
            Limit = 1
        });
        return response?.Items is { Count: > 0 };
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
            System.Text.Json.JsonElement je => JsonElementToAttributeValue(je),
            _ => new AttributeValue { S = value.ToString() ?? "" }
        };
    }

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
}
