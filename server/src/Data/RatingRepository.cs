using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using WeredaConnect.Server.Services;

namespace WeredaConnect.Server.Data;

/// <summary>
/// DynamoDB rating repository using the low-level API for AOT compatibility.
/// Manual marshalling — no Document Model or reflection.
///
/// Table schema (Ratings):
///   PK: ratingId (STRING, "{orderId}:{rateeId}") — sole key, no sort key
///   GSI RateeIdCreatedAtIndex: rateeId + createdAt
///
/// Ratings are create-only and immutable; the deterministic ratingId + the
/// create-time <c>attribute_not_exists(ratingId)</c> condition guarantee one
/// rating per (order, ratee). (A sort key would break this: a re-submission
/// with a fresh createdAt would be a distinct item and slip past the guard.)
///
/// ESC-28 (rating-api.md 1.2.0, "Stored aggregate"): with the users table set, a create is one
/// transaction that puts the rating with <c>inAggregate: true</c>, <c>ADD</c>s its stars to the
/// ratee's <c>ratingSum</c> and <c>ratingCount</c> (conditioned on the ratee's role and a live
/// account) and upserts the ratee's <c>HEAL#</c> marker for the reputation daily job.
/// </summary>
public sealed class RatingRepository : IRatingRepository
{
    private readonly IAmazonDynamoDB _dynamoDb;
    private readonly string _tableName;

    private const string RateeIndex = "RateeIdCreatedAtIndex";

    /// <summary>Safety cap on pages walked by <see cref="GetSummaryAsync"/>.</summary>
    private const int MaxQueryPages = 50;

    /// <summary>ESC-28: the users table of the stored aggregate; null keeps the plain put of 1.1.0.</summary>
    private readonly string? _usersTable;

    /// <summary>ESC-28: the reputation-events table the heal marker goes to; null leaves it out.</summary>
    private readonly string? _reputationEventsTable;

    /// <summary>The server-only flag on a rating row (rating-api.md 1.2.0); never returned to clients.</summary>
    public const string InAggregateField = "inAggregate";

    public RatingRepository(
        IAmazonDynamoDB dynamoDb, string tableName, string? usersTable = null, string? reputationEventsTable = null)
    {
        _dynamoDb = dynamoDb ?? throw new ArgumentNullException(nameof(dynamoDb));
        _tableName = tableName ?? throw new ArgumentNullException(nameof(tableName));
        _usersTable = string.IsNullOrEmpty(usersTable) ? null : usersTable;
        _reputationEventsTable = string.IsNullOrEmpty(reputationEventsTable) ? null : reputationEventsTable;
    }

    /// <inheritdoc />
    public async Task<AuthResult<Dictionary<string, object>>> CreateAsync(
        string ratingId,
        Dictionary<string, object> data)
    {
        var nowUtc = DateTime.UtcNow;
        var now = nowUtc.ToString("o");

        var item = new Dictionary<string, AttributeValue>
        {
            { "ratingId", new AttributeValue { S = ratingId } },
            { "createdAt", new AttributeValue { S = now } },
            { "updatedAt", new AttributeValue { S = now } },
            { "version", new AttributeValue { N = "1" } }
        };

        foreach (var kvp in data)
        {
            if (kvp.Key is "ratingId" or "version" or "createdAt" or "updatedAt")
            {
                continue;
            }

            item[kvp.Key] = DynamoDbHelper.ToAttributeValue(kvp.Value);
        }

        item.Remove(InAggregateField); // server-only: never taken from the payload

        if (_usersTable != null
            && data.TryGetValue("rateeId", out var rateeObj) && rateeObj?.ToString() is { Length: > 0 } rateeId
            && data.TryGetValue("rateeRole", out var roleObj) && roleObj?.ToString() is { Length: > 0 } rateeRole
            && data.TryGetValue("stars", out var starsObj)
            && int.TryParse(starsObj?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var stars))
        {
            return await CreateWithAggregateAsync(item, rateeId, rateeRole, stars, nowUtc);
        }

        return await PutAsync(item);
    }

    /// <summary>
    /// rating-api.md 1.2.0 "Stored aggregate" steps 1 and 2: the rating, the ratee's sum and count and
    /// the heal marker in one transaction. The rating's own condition failing is the usual conflict;
    /// only the users condition failing (the ratee's role changed since the order, or the account is
    /// being deleted) puts the rating alone with <c>inAggregate: false</c>, so the stored aggregate of
    /// the current role stays exact. Any other failure throws: nothing was written, the client retries.
    /// </summary>
    private async Task<AuthResult<Dictionary<string, object>>> CreateWithAggregateAsync(
        Dictionary<string, AttributeValue> item, string rateeId, string rateeRole, int stars, DateTime nowUtc)
    {
        item[InAggregateField] = new AttributeValue { BOOL = true, IsBOOLSet = true };
        var items = new List<TransactWriteItem>
        {
            new()
            {
                Put = new Put
                {
                    TableName = _tableName,
                    Item = item,
                    ConditionExpression = "attribute_not_exists(ratingId)"
                }
            },
            new()
            {
                Update = new Update
                {
                    TableName = _usersTable,
                    Key = new Dictionary<string, AttributeValue> { { "userId", new AttributeValue { S = rateeId } } },
                    // Top-level numbers: ADD creates them on a row from before the backfill, and the
                    // reputation map's version is never touched (no conflict with its writers).
                    UpdateExpression = "ADD #rs :stars, #rc :one",
                    ConditionExpression = "attribute_exists(userId) AND #role = :role AND attribute_not_exists(#ds)",
                    ExpressionAttributeNames = new Dictionary<string, string>
                    {
                        { "#rs", "ratingSum" },
                        { "#rc", "ratingCount" },
                        { "#role", "role" },
                        { "#ds", "deletionState" }
                    },
                    ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                    {
                        { ":stars", new AttributeValue { N = stars.ToString(CultureInfo.InvariantCulture) } },
                        { ":one", new AttributeValue { N = "1" } },
                        { ":role", new AttributeValue { S = rateeRole } }
                    }
                }
            }
        };

        if (_reputationEventsTable != null)
        {
            items.Add(new TransactWriteItem { Update = ReputationEventRepository.HealUpsert(_reputationEventsTable, rateeId, nowUtc) });
        }

        try
        {
            await _dynamoDb.TransactWriteItemsAsync(new TransactWriteItemsRequest { TransactItems = items });
            return AuthResult<Dictionary<string, object>>.Success(ClientView(item));
        }
        catch (TransactionCanceledException ex)
        {
            var reasons = ex.CancellationReasons ?? new List<CancellationReason>();
            if (reasons.Count > 0 && reasons[0]?.Code == "ConditionalCheckFailed")
            {
                return AuthResult<Dictionary<string, object>>.Failure(
                    409, "SYNC_CONFLICT", "Rating already exists.", "ratingId");
            }

            if (reasons.Count > 1 && reasons[1]?.Code == "ConditionalCheckFailed")
            {
                item[InAggregateField] = new AttributeValue { BOOL = false, IsBOOLSet = true };
                return await PutAsync(item);
            }

            throw;
        }
    }

    /// <summary>The 1.1.0 conditional put (also the role-changed fallback of the aggregate path).</summary>
    private async Task<AuthResult<Dictionary<string, object>>> PutAsync(Dictionary<string, AttributeValue> item)
    {
        var request = new PutItemRequest
        {
            TableName = _tableName,
            Item = item,
            ConditionExpression = "attribute_not_exists(ratingId)"
        };

        try
        {
            await _dynamoDb.PutItemAsync(request);
            return AuthResult<Dictionary<string, object>>.Success(ClientView(item));
        }
        catch (ConditionalCheckFailedException)
        {
            return AuthResult<Dictionary<string, object>>.Failure(
                409, "SYNC_CONFLICT", "Rating already exists.", "ratingId");
        }
    }

    /// <summary>The created rating as the sync response returns it: without the server-only flag.</summary>
    private static Dictionary<string, object> ClientView(Dictionary<string, AttributeValue> item)
    {
        var view = DynamoDbHelper.FromItem(item);
        view.Remove(InAggregateField);
        return view;
    }

    /// <inheritdoc />
    public async Task<Dictionary<string, object>?> GetByIdAsync(string ratingId)
    {
        // PK-only table → a direct GetItem on the partition key.
        var response = await _dynamoDb.GetItemAsync(new GetItemRequest
        {
            TableName = _tableName,
            Key = new Dictionary<string, AttributeValue>
            {
                { "ratingId", new AttributeValue { S = ratingId } }
            }
        });

        return response.Item is { Count: > 0 }
            ? DynamoDbHelper.FromItem(response.Item)
            : null;
    }

    /// <inheritdoc />
    public async Task<(List<Dictionary<string, object>> Items, Dictionary<string, AttributeValue>? LastEvaluatedKey)> QueryByRateeAsync(
        string rateeId,
        int limit = 20,
        Dictionary<string, AttributeValue>? exclusiveStartKey = null,
        string? rateeRole = null)
    {
        if (limit < 1 || limit > 50)
        {
            limit = 20;
        }

        var request = new QueryRequest
        {
            TableName = _tableName,
            IndexName = RateeIndex,
            KeyConditionExpression = "#ratee = :val",
            ExpressionAttributeNames = new Dictionary<string, string>
            {
                { "#ratee", "rateeId" }
            },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                { ":val", new AttributeValue { S = rateeId } }
            },
            ScanIndexForward = false, // newest first
            Limit = limit
        };

        if (exclusiveStartKey != null)
        {
            request.ExclusiveStartKey = exclusiveStartKey;
        }

        ApplyRoleFilter(request, rateeRole);

        var response = await _dynamoDb.QueryAsync(request);
        var items = response.Items.Select(DynamoDbHelper.FromItem).ToList();
        return (items, response.LastEvaluatedKey.Count > 0 ? response.LastEvaluatedKey : null);
    }

    /// <inheritdoc />
    public async Task<(double Average, int Count)> GetSummaryAsync(string rateeId, string? rateeRole = null)
    {
        var request = new QueryRequest
        {
            TableName = _tableName,
            IndexName = RateeIndex,
            KeyConditionExpression = "#ratee = :val",
            ExpressionAttributeNames = new Dictionary<string, string>
            {
                { "#ratee", "rateeId" }
            },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                { ":val", new AttributeValue { S = rateeId } }
            }
        };

        ApplyRoleFilter(request, rateeRole);

        var total = 0L;
        var count = 0;
        QueryResponse? response = null;
        var pages = 0;

        do
        {
            if (response?.LastEvaluatedKey?.Count > 0)
            {
                request.ExclusiveStartKey = response.LastEvaluatedKey;
            }

            response = await _dynamoDb.QueryAsync(request);
            foreach (var raw in response.Items)
            {
                if (raw.TryGetValue("stars", out var starsAttr) && starsAttr.N != null
                    && int.TryParse(starsAttr.N, NumberStyles.Integer, CultureInfo.InvariantCulture, out var stars))
                {
                    total += stars;
                    count++;
                }
            }

            pages++;
        }
        while (response.LastEvaluatedKey?.Count > 0 && pages < MaxQueryPages);

        var average = count > 0 ? (double)total / count : 0.0;
        return (average, count);
    }

    /// <summary>
    /// ESC-29 (rating-api.md 1.2.0): only the ratings received in one role (the index projects
    /// <c>ALL</c>, so the filter sees <c>rateeRole</c>). No role, no filter.
    /// </summary>
    private static void ApplyRoleFilter(QueryRequest request, string? rateeRole)
    {
        if (string.IsNullOrEmpty(rateeRole))
        {
            return;
        }

        request.FilterExpression = "#rateeRole = :rateeRole";
        request.ExpressionAttributeNames["#rateeRole"] = "rateeRole";
        request.ExpressionAttributeValues[":rateeRole"] = new AttributeValue { S = rateeRole };
    }
}
