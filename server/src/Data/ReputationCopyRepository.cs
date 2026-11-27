using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Models;

namespace WeredaConnect.Server.Data;

/// <summary>
/// ESC-29 (reputation-api.md section 7, reputation-architecture.md 6.3): copies an owner's public
/// reputation onto their active crop or trip rows.
/// </summary>
public interface IReputationCopyWriter
{
    /// <summary>
    /// Write <paramref name="fields"/> onto the owner's active rows (crops <c>available</c> or
    /// <c>reserved</c> for a farmer, trips <c>open</c> or <c>matched</c> for a trucker; any other
    /// role has none), newest first, at most <see cref="ReputationCopyRepository.MaxRows"/>. Rows
    /// that already hold the same values are not written. Returns how many rows were written.
    /// Idempotent: a second call with the same values writes nothing.
    /// </summary>
    Task<int> RefreshOwnerRowsAsync(string userId, string role, OwnerReputationFields fields);
}

/// <summary>
/// DynamoDB implementation with the low-level API (AOT safe). No Scan: the owner's rows come from
/// <c>FarmerIdCreatedAtIndex</c> (an <c>INCLUDE</c> projection without <c>status</c>, so the keys
/// go through one <c>BatchGetItem</c> per 100) or <c>TruckerIdCreatedAtIndex</c> (projection
/// <c>ALL</c>). The copy never touches the row's <c>version</c> or <c>updatedAt</c>, so an owner's
/// offline edit never conflicts with it (decision R24).
/// </summary>
public sealed class ReputationCopyRepository : IReputationCopyWriter
{
    /// <summary>The most rows of one owner a refresh looks at (architecture doc 6.3).</summary>
    public const int MaxRows = 200;

    private const int BatchGetSize = 100;
    private const int BatchGetAttempts = 4;

    // FUP-160: the one-off owner stamp walks the same statuses on the listing status indexes.
    internal static readonly string[] ActiveCropStatuses = { "available", "reserved" };
    internal static readonly string[] ActiveTripStatuses = { "open", "matched" };

    private readonly IAmazonDynamoDB _dynamoDb;
    private readonly string _cropsTable;
    private readonly string _tripsTable;

    public ReputationCopyRepository(IAmazonDynamoDB dynamoDb, string cropsTable, string tripsTable)
    {
        _dynamoDb = dynamoDb ?? throw new ArgumentNullException(nameof(dynamoDb));
        _cropsTable = cropsTable ?? throw new ArgumentNullException(nameof(cropsTable));
        _tripsTable = tripsTable ?? throw new ArgumentNullException(nameof(tripsTable));
    }

    /// <summary>The writer of a Lambda, or null when either table name is not configured.</summary>
    public static ReputationCopyRepository? FromEnvironment(IAmazonDynamoDB dynamoDb)
    {
        var crops = Environment.GetEnvironmentVariable("CROPS_TABLE");
        var trips = Environment.GetEnvironmentVariable("TRIPS_TABLE");
        return string.IsNullOrEmpty(crops) || string.IsNullOrEmpty(trips)
            ? null
            : new ReputationCopyRepository(dynamoDb, crops, trips);
    }

    /// <inheritdoc />
    public async Task<int> RefreshOwnerRowsAsync(string userId, string role, OwnerReputationFields fields)
    {
        if (string.Equals(role, "farmer", StringComparison.OrdinalIgnoreCase))
        {
            var rows = await ActiveCropRowsAsync(userId);
            return await WriteAsync(_cropsTable, "cropId", rows, ActiveCropStatuses, fields);
        }

        if (string.Equals(role, "trucker", StringComparison.OrdinalIgnoreCase))
        {
            var rows = await ActiveTripRowsAsync(userId);
            return await WriteAsync(_tripsTable, "tripId", rows, ActiveTripStatuses, fields);
        }

        return 0;
    }

    /// <summary>The farmer's newest rows that are not deleted, with their status and stored owner fields.</summary>
    private async Task<List<Dictionary<string, AttributeValue>>> ActiveCropRowsAsync(string farmerId)
    {
        var keys = await QueryOwnerAsync(new QueryRequest
        {
            TableName = _cropsTable,
            IndexName = "FarmerIdCreatedAtIndex",
            KeyConditionExpression = "farmerId = :owner",
            // deletedAt is in the INCLUDE projection; status is not.
            FilterExpression = "attribute_not_exists(#deletedAt)",
            ProjectionExpression = "cropId, createdAt",
            ExpressionAttributeNames = new Dictionary<string, string> { ["#deletedAt"] = "deletedAt" },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue> { [":owner"] = S(farmerId) },
            ScanIndexForward = false
        });

        var rows = new List<Dictionary<string, AttributeValue>>();
        foreach (var chunk in keys.Chunk(BatchGetSize))
        {
            var pending = new KeysAndAttributes
            {
                Keys = chunk.Select(k => new Dictionary<string, AttributeValue>
                {
                    ["cropId"] = k["cropId"],
                    ["createdAt"] = k["createdAt"]
                }).ToList(),
                ProjectionExpression = "cropId, createdAt, #status, #tier, #rating, #count, #reliability",
                ExpressionAttributeNames = OwnerFieldNames()
            };

            for (var attempt = 1; pending.Keys.Count > 0; attempt++)
            {
                var response = await _dynamoDb.BatchGetItemAsync(new BatchGetItemRequest
                {
                    RequestItems = new Dictionary<string, KeysAndAttributes> { [_cropsTable] = pending }
                });
                if (response.Responses != null && response.Responses.TryGetValue(_cropsTable, out var got))
                {
                    rows.AddRange(got);
                }

                var left = response.UnprocessedKeys != null && response.UnprocessedKeys.TryGetValue(_cropsTable, out var unprocessed)
                    ? unprocessed.Keys
                    : null;
                if (left == null || left.Count == 0)
                {
                    break;
                }

                if (attempt >= BatchGetAttempts)
                {
                    // A partial copy would look complete: fail, and the heal marker retries it.
                    throw new InvalidOperationException("Crop rows were left unread by BatchGetItem.");
                }

                pending.Keys = left;
                await Task.Delay(TimeSpan.FromMilliseconds(40 * attempt));
            }
        }

        return rows;
    }

    /// <summary>The trucker's newest rows with their status and stored owner fields (the index projects <c>ALL</c>).</summary>
    private Task<List<Dictionary<string, AttributeValue>>> ActiveTripRowsAsync(string truckerId)
    {
        var names = OwnerFieldNames();
        return QueryOwnerAsync(new QueryRequest
        {
            TableName = _tripsTable,
            IndexName = "TruckerIdCreatedAtIndex",
            KeyConditionExpression = "truckerId = :owner",
            ProjectionExpression = "tripId, createdAt, #status, #tier, #rating, #count, #reliability",
            ExpressionAttributeNames = names,
            ExpressionAttributeValues = new Dictionary<string, AttributeValue> { [":owner"] = S(truckerId) },
            ScanIndexForward = false
        });
    }

    /// <summary>All pages of an owner index, newest first, up to <see cref="MaxRows"/> rows.</summary>
    private async Task<List<Dictionary<string, AttributeValue>>> QueryOwnerAsync(QueryRequest request)
    {
        var rows = new List<Dictionary<string, AttributeValue>>();
        Dictionary<string, AttributeValue>? startKey = null;
        do
        {
            request.ExclusiveStartKey = startKey;
            request.Limit = MaxRows;
            var page = await _dynamoDb.QueryAsync(request);
            foreach (var item in page.Items)
            {
                if (rows.Count >= MaxRows)
                {
                    return rows;
                }

                rows.Add(item);
            }

            startKey = page.LastEvaluatedKey is { Count: > 0 } next ? next : null;
        }
        while (startKey != null && rows.Count < MaxRows);

        return rows;
    }

    private async Task<int> WriteAsync(
        string table, string idAttribute, List<Dictionary<string, AttributeValue>> rows, string[] activeStatuses, OwnerReputationFields fields)
    {
        var written = 0;
        foreach (var row in rows)
        {
            var status = row.TryGetValue("status", out var st) ? st.S : null;
            if (status == null || Array.IndexOf(activeStatuses, status) < 0 || HoldsSame(row, fields))
            {
                // Inactive rows are never written (no conditional write is spent on them).
                continue;
            }

            try
            {
                await _dynamoDb.UpdateItemAsync(BuildUpdate(table, idAttribute, row, activeStatuses, fields));
                written++;
            }
            catch (ConditionalCheckFailedException)
            {
                // The row was deleted or left the active statuses since the read: nothing to copy.
            }
        }

        return written;
    }

    /// <summary>
    /// The copy of one row: <c>SET</c> the fields with a value, <c>REMOVE</c> the others, only while
    /// the row still exists in an active status. No <c>version</c>, no <c>updatedAt</c>.
    /// </summary>
    internal static UpdateItemRequest BuildUpdate(
        string table, string idAttribute, Dictionary<string, AttributeValue> row, string[] activeStatuses, OwnerReputationFields fields)
    {
        var names = OwnerFieldNames();
        names["#at"] = OwnerReputationFields.AtField;
        var values = new Dictionary<string, AttributeValue>
        {
            [":tier"] = S(fields.Tier),
            [":count"] = new() { N = fields.RatingCount.ToString(CultureInfo.InvariantCulture) },
            [":at"] = S(ReputationPolicy.Iso(fields.At))
        };
        var set = new List<string> { "#tier = :tier", "#count = :count", "#at = :at" };
        var remove = new List<string>();

        if (fields.EffectiveRating is { } rating)
        {
            values[":rating"] = new AttributeValue { N = rating.ToString("0.0", CultureInfo.InvariantCulture) };
            set.Add("#rating = :rating");
        }
        else
        {
            remove.Add("#rating");
        }

        if (fields.ReliabilityPct is { } reliability)
        {
            values[":reliability"] = new AttributeValue { N = reliability.ToString(CultureInfo.InvariantCulture) };
            set.Add("#reliability = :reliability");
        }
        else
        {
            remove.Add("#reliability");
        }

        var statusKeys = new List<string>();
        for (var i = 0; i < activeStatuses.Length; i++)
        {
            values[$":s{i}"] = S(activeStatuses[i]);
            statusKeys.Add($":s{i}");
        }

        return new UpdateItemRequest
        {
            TableName = table,
            Key = new Dictionary<string, AttributeValue>
            {
                [idAttribute] = row[idAttribute],
                ["createdAt"] = row["createdAt"]
            },
            UpdateExpression = "SET " + string.Join(", ", set) + (remove.Count > 0 ? " REMOVE " + string.Join(", ", remove) : string.Empty),
            ConditionExpression = $"attribute_exists({idAttribute}) AND #status IN ({string.Join(", ", statusKeys)})",
            ExpressionAttributeNames = names,
            ExpressionAttributeValues = values
        };
    }

    /// <summary>Whether the row already holds these values (the copy time aside).</summary>
    internal static bool HoldsSame(Dictionary<string, AttributeValue> row, OwnerReputationFields fields)
        => (row.TryGetValue(OwnerReputationFields.TierField, out var tier) ? tier.S : null) == fields.Tier
           && Number(row, OwnerReputationFields.RatingCountField) == fields.RatingCount
           && Number(row, OwnerReputationFields.EffectiveRatingField) == fields.EffectiveRating
           && Number(row, OwnerReputationFields.ReliabilityPctField) == fields.ReliabilityPct;

    private static decimal? Number(Dictionary<string, AttributeValue> row, string key)
        => row.TryGetValue(key, out var v) && v.N != null
           && decimal.TryParse(v.N, NumberStyles.Number, CultureInfo.InvariantCulture, out var n)
            ? n
            : null;

    private static Dictionary<string, string> OwnerFieldNames() => new()
    {
        ["#status"] = "status",
        ["#tier"] = OwnerReputationFields.TierField,
        ["#rating"] = OwnerReputationFields.EffectiveRatingField,
        ["#count"] = OwnerReputationFields.RatingCountField,
        ["#reliability"] = OwnerReputationFields.ReliabilityPctField
    };

    private static AttributeValue S(string value) => new() { S = value };
}
