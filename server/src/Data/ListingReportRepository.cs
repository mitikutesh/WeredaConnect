using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;

namespace WeredaConnect.Server.Data;

/// <summary>
/// FUP-67: DynamoDB-backed <see cref="IListingReportRepository"/> (PK <c>listingId</c>, SK <c>reporterId</c>,
/// GSI <c>StatusCreatedAtIndex</c>). Low-level client with manual marshalling (AOT: no Document Model).
/// </summary>
public sealed class ListingReportRepository : IListingReportRepository
{
    public const string StatusIndex = "StatusCreatedAtIndex";

    /// <summary>
    /// Pages read by <see cref="ListByListingAsync"/> before it stops. The daily limit keeps a listing far
    /// below one page; the bound only stops a runaway read.
    /// </summary>
    private const int MaxListingPages = 10;

    private readonly IAmazonDynamoDB _dynamoDb;
    private readonly string _tableName;

    public ListingReportRepository(IAmazonDynamoDB dynamoDb, string tableName)
    {
        _dynamoDb = dynamoDb ?? throw new ArgumentNullException(nameof(dynamoDb));
        _tableName = tableName ?? throw new ArgumentNullException(nameof(tableName));
    }

    /// <inheritdoc />
    public async Task<bool> ExistsAsync(string listingId, string reporterId)
    {
        var response = await _dynamoDb.GetItemAsync(new GetItemRequest
        {
            TableName = _tableName,
            Key = KeyOf(listingId, reporterId),
            ProjectionExpression = "#l",
            ExpressionAttributeNames = new Dictionary<string, string> { ["#l"] = "listingId" },
            ConsistentRead = true
        });
        return response.Item is { Count: > 0 };
    }

    /// <inheritdoc />
    public async Task<bool> TryCreateAsync(ListingReportRow row)
    {
        var item = new Dictionary<string, AttributeValue>
        {
            ["listingId"] = S(row.ListingId),
            ["reporterId"] = S(row.ReporterId),
            ["listingType"] = S(row.ListingType),
            ["ownerId"] = S(row.OwnerId),
            ["reason"] = S(row.Reason),
            ["status"] = S(row.Status),
            ["createdAt"] = S(row.CreatedAt),
            ["expiresAt"] = new AttributeValue { N = row.ExpiresAt.ToString(CultureInfo.InvariantCulture) }
        };

        try
        {
            await _dynamoDb.PutItemAsync(new PutItemRequest
            {
                TableName = _tableName,
                Item = item,
                // One report per reporter and listing: a second one keeps the first row as it is.
                ConditionExpression = "attribute_not_exists(reporterId)"
            });
            return true;
        }
        catch (ConditionalCheckFailedException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<(List<ListingReportRow> Rows, Dictionary<string, AttributeValue>? LastEvaluatedKey)> ListByStatusAsync(
        string status, int limit, Dictionary<string, AttributeValue>? exclusiveStartKey)
    {
        var request = new QueryRequest
        {
            TableName = _tableName,
            IndexName = StatusIndex,
            KeyConditionExpression = "#s = :s",
            ExpressionAttributeNames = new Dictionary<string, string> { ["#s"] = "status" },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue> { [":s"] = S(status) },
            // The open queue is worked oldest first; decided reports read newest first.
            ScanIndexForward = status == Helpers.ListingReportRules.StatusOpen,
            Limit = limit
        };

        if (exclusiveStartKey is { Count: > 0 })
        {
            request.ExclusiveStartKey = exclusiveStartKey;
        }

        var response = await _dynamoDb.QueryAsync(request);
        var rows = (response.Items ?? new List<Dictionary<string, AttributeValue>>())
            .Select(FromItem)
            .OfType<ListingReportRow>()
            .ToList();
        var lastKey = response.LastEvaluatedKey is { Count: > 0 } next ? next : null;
        return (rows, lastKey);
    }

    /// <inheritdoc />
    public async Task<List<ListingReportRow>> ListByListingAsync(string listingId)
    {
        var rows = new List<ListingReportRow>();
        Dictionary<string, AttributeValue>? startKey = null;
        var pages = 0;

        do
        {
            var request = new QueryRequest
            {
                TableName = _tableName,
                KeyConditionExpression = "#l = :l",
                ExpressionAttributeNames = new Dictionary<string, string> { ["#l"] = "listingId" },
                ExpressionAttributeValues = new Dictionary<string, AttributeValue> { [":l"] = S(listingId) },
                // A review right after a report must see it.
                ConsistentRead = true
            };

            if (startKey != null)
            {
                request.ExclusiveStartKey = startKey;
            }

            var response = await _dynamoDb.QueryAsync(request);
            foreach (var item in response.Items ?? new List<Dictionary<string, AttributeValue>>())
            {
                if (FromItem(item) is { } row)
                {
                    rows.Add(row);
                }
            }

            startKey = response.LastEvaluatedKey is { Count: > 0 } next ? next : null;
        }
        while (startKey != null && ++pages < MaxListingPages);

        return rows;
    }

    /// <inheritdoc />
    public async Task<bool> TryReviewAsync(
        string listingId, string reporterId, string decision, string reviewedBy, string reviewedAt, string? reviewNote)
    {
        var names = new Dictionary<string, string>
        {
            ["#s"] = "status",
            ["#ra"] = "reviewedAt",
            ["#rb"] = "reviewedBy"
        };
        var values = new Dictionary<string, AttributeValue>
        {
            [":open"] = S(Helpers.ListingReportRules.StatusOpen),
            [":decision"] = S(decision),
            [":ra"] = S(reviewedAt),
            [":rb"] = S(reviewedBy)
        };
        var update = "SET #s = :decision, #ra = :ra, #rb = :rb";
        if (!string.IsNullOrEmpty(reviewNote))
        {
            names["#rn"] = "reviewNote";
            values[":rn"] = S(reviewNote);
            update += ", #rn = :rn";
        }

        try
        {
            await _dynamoDb.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = _tableName,
                Key = KeyOf(listingId, reporterId),
                UpdateExpression = update,
                // Never recreates a row that expired, and never overwrites another admin's decision.
                ConditionExpression = "#s = :open",
                ExpressionAttributeNames = names,
                ExpressionAttributeValues = values
            });
            return true;
        }
        catch (ConditionalCheckFailedException)
        {
            return false;
        }
    }

    /// <summary>A stored row, or null when its keys are missing.</summary>
    internal static ListingReportRow? FromItem(Dictionary<string, AttributeValue> item)
    {
        var listingId = Text(item, "listingId");
        var reporterId = Text(item, "reporterId");
        if (string.IsNullOrEmpty(listingId) || string.IsNullOrEmpty(reporterId))
        {
            return null;
        }

        var expiresAt = item.TryGetValue("expiresAt", out var e)
                        && long.TryParse(e.N, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0;

        return new ListingReportRow(
            listingId,
            reporterId,
            Text(item, "listingType") ?? string.Empty,
            Text(item, "ownerId") ?? string.Empty,
            Text(item, "reason") ?? string.Empty,
            Text(item, "status") ?? string.Empty,
            Text(item, "createdAt") ?? string.Empty,
            expiresAt,
            Text(item, "reviewedAt"),
            Text(item, "reviewedBy"),
            Text(item, "reviewNote"));
    }

    private static Dictionary<string, AttributeValue> KeyOf(string listingId, string reporterId) => new()
    {
        ["listingId"] = S(listingId),
        ["reporterId"] = S(reporterId)
    };

    private static AttributeValue S(string value) => new() { S = value };

    private static string? Text(Dictionary<string, AttributeValue> item, string key)
        => item.TryGetValue(key, out var value) && !string.IsNullOrEmpty(value.S) ? value.S : null;
}
