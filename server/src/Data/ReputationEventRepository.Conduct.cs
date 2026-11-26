using System.Globalization;
using Amazon.DynamoDBv2.Model;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Models;

namespace WeredaConnect.Server.Data;

/// <summary>
/// GCR-03 (reputation-api.md 1.9.0 section 1.8): the conduct stamps in the reputation events table, slot
/// <c>HANDOFF#{orderId}</c> under the party's <c>userId</c>, so the key is the idempotency key
/// <c>{orderId}:{partyId}</c>. A stamp has no <c>dueDay</c> and no <c>appealStatus</c>, so neither index sees it, and
/// <see cref="BuildEventsQuery"/> filters it out of every event read.
/// </summary>
public sealed partial class ReputationEventRepository : IConductStampRepository
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<ConductStamp>> QueryStampsAsync(string userId, int cap)
    {
        var rows = new List<ConductStamp>();
        var request = BuildStampsQuery(userId);
        Dictionary<string, AttributeValue>? startKey = null;
        do
        {
            request.ExclusiveStartKey = startKey;
            var page = await _dynamoDb.QueryAsync(request);
            foreach (var item in page.Items)
            {
                if (rows.Count >= cap)
                {
                    return rows;
                }

                if (Str(item, "code") == ReputationCodes.HandoffQuality)
                {
                    rows.Add(StampFromItem(item));
                }
            }

            startKey = page.LastEvaluatedKey is { Count: > 0 } next ? next : null;
        }
        while (startKey != null && rows.Count < cap);

        return rows;
    }

    /// <summary>The user's <c>HANDOFF#</c> slots, strongly consistent. Exposed for the request-shape tests.</summary>
    internal QueryRequest BuildStampsQuery(string userId) => new()
    {
        TableName = _eventsTable,
        KeyConditionExpression = "userId = :u AND begins_with(eventId, :slot)",
        ExpressionAttributeValues = new Dictionary<string, AttributeValue>
        {
            [":u"] = S(userId),
            [":slot"] = S(ReputationSlots.Handoff)
        },
        ConsistentRead = true
    };

    /// <inheritdoc />
    /// <remarks>
    /// A Query on the full key rather than a GetItem: the two Lambdas that write stamps (reputation-stream and sync)
    /// hold Query, PutItem and UpdateItem on the table, no GetItem.
    /// </remarks>
    public async Task<ConductStamp?> GetStampAsync(string userId, string orderId)
    {
        var response = await _dynamoDb.QueryAsync(BuildStampQuery(userId, orderId));
        return response.Items.FirstOrDefault() is { Count: > 0 } item && Str(item, "code") == ReputationCodes.HandoffQuality
            ? StampFromItem(item)
            : null;
    }

    /// <summary>One stamp by its key, strongly consistent. Exposed for the request-shape tests.</summary>
    internal QueryRequest BuildStampQuery(string userId, string orderId) => new()
    {
        TableName = _eventsTable,
        KeyConditionExpression = "userId = :u AND eventId = :e",
        ExpressionAttributeValues = new Dictionary<string, AttributeValue>
        {
            [":u"] = S(userId),
            [":e"] = S(ReputationSlots.Handoff + orderId)
        },
        ConsistentRead = true
    };

    /// <inheritdoc />
    public Task<ConductWriteOutcome> PutNewAsync(ConductStamp stamp)
        => WriteStampAsync(BuildStampWrite(stamp, replace: false));

    /// <inheritdoc />
    public Task<ConductWriteOutcome> ReplaceOnceAsync(ConductStamp stamp)
        => WriteStampAsync(BuildStampWrite(stamp, replace: true));

    private async Task<ConductWriteOutcome> WriteStampAsync(TransactWriteItemsRequest request)
    {
        try
        {
            await _dynamoDb.TransactWriteItemsAsync(request);
            return ConductWriteOutcome.Written;
        }
        catch (TransactionCanceledException ex)
        {
            var reasons = ex.CancellationReasons ?? new List<CancellationReason>();
            if (IsCode(reasons, 0, "ConditionalCheckFailed"))
            {
                return ConductWriteOutcome.Exists;
            }

            if (IsCode(reasons, 1, "ConditionalCheckFailed"))
            {
                return ConductWriteOutcome.Skipped;
            }

            throw;
        }
    }

    /// <summary>
    /// One stamp write: the row (a first stamp only into a free slot; the re-evaluation only over a stamp not yet
    /// re-evaluated), then a check that the user's row exists and is not being deleted, as every reputation writer
    /// refuses a deleting account (account deletion removes the whole partition). Exposed for the request-shape tests.
    /// </summary>
    internal TransactWriteItemsRequest BuildStampWrite(ConductStamp stamp, bool replace)
    {
        var put = new Put
        {
            TableName = _eventsTable,
            Item = StampToItem(stamp),
            ConditionExpression = replace
                ? "attribute_exists(eventId) AND attribute_not_exists(#re)"
                : "attribute_not_exists(eventId)"
        };
        if (replace)
        {
            put.ExpressionAttributeNames = new Dictionary<string, string> { ["#re"] = "reevaluatedAt" };
        }

        return new TransactWriteItemsRequest
        {
            TransactItems = new List<TransactWriteItem>
            {
                new() { Put = put },
                new()
                {
                    ConditionCheck = new ConditionCheck
                    {
                        TableName = _usersTable,
                        Key = new Dictionary<string, AttributeValue> { ["userId"] = S(stamp.UserId) },
                        ConditionExpression = "attribute_exists(userId) AND attribute_not_exists(#ds)",
                        ExpressionAttributeNames = new Dictionary<string, string> { ["#ds"] = "deletionState" }
                    }
                }
            }
        };
    }

    // ===== marshalling =====

    /// <summary>A stamp as a DynamoDB item. Absent values are left out.</summary>
    internal static Dictionary<string, AttributeValue> StampToItem(ConductStamp stamp)
    {
        var item = new Dictionary<string, AttributeValue>
        {
            ["userId"] = S(stamp.UserId),
            ["eventId"] = S(stamp.EventId),
            ["code"] = S(ReputationCodes.HandoffQuality),
            ["party"] = S(stamp.Party),
            ["orderId"] = S(stamp.OrderId),
            ["farmerId"] = S(stamp.FarmerId),
            ["buyerId"] = S(stamp.BuyerId),
            ["offered"] = N(stamp.Offered),
            ["earned"] = N(stamp.Earned),
            ["counted"] = new() { BOOL = stamp.Counted },
            ["occurredAt"] = S(ReputationPolicy.Iso(stamp.OccurredAt)),
            ["recordedAt"] = S(ReputationPolicy.Iso(stamp.RecordedAt)),
            ["version"] = N(stamp.Version),
            ["purgeAt"] = N(stamp.PurgeAt)
        };

        // An empty list is not a value DynamoDB takes: a stamp without steps leaves the attribute out.
        if (stamp.Steps.Count > 0)
        {
            item["steps"] = new AttributeValue
            {
                L = stamp.Steps.Select(step =>
                {
                    var map = new Dictionary<string, AttributeValue>
                    {
                        ["id"] = S(step.Id),
                        ["result"] = S(step.Result)
                    };
                    Put(map, "reason", step.Reason);
                    return new AttributeValue { M = map };
                }).ToList()
            };
        }

        Put(item, "truckerId", stamp.TruckerId);
        Put(item, "notCountedReason", stamp.NotCountedReason);
        PutTime(item, "reevaluatedAt", stamp.ReevaluatedAt);
        if (stamp.OrderTotal is { } total)
        {
            item["orderTotal"] = new AttributeValue { N = total.ToString(CultureInfo.InvariantCulture) };
        }

        return item;
    }

    /// <summary>A DynamoDB item as a stamp.</summary>
    internal static ConductStamp StampFromItem(Dictionary<string, AttributeValue> item) => new()
    {
        UserId = Str(item, "userId") ?? string.Empty,
        OrderId = Str(item, "orderId") ?? string.Empty,
        Party = Str(item, "party") ?? string.Empty,
        FarmerId = Str(item, "farmerId") ?? string.Empty,
        BuyerId = Str(item, "buyerId") ?? string.Empty,
        TruckerId = Str(item, "truckerId"),
        Offered = Int(item, "offered") ?? 0,
        Earned = Int(item, "earned") ?? 0,
        Steps = item.TryGetValue("steps", out var steps) && steps.L is { } list
            ? list.Where(v => v.M is { Count: > 0 })
                .Select(v => new ConductStep(Str(v.M, "id") ?? string.Empty, Str(v.M, "result") ?? string.Empty, Str(v.M, "reason")))
                .ToList()
            : new List<ConductStep>(),
        Counted = item.TryGetValue("counted", out var counted) && counted.IsBOOLSet && counted.BOOL,
        NotCountedReason = Str(item, "notCountedReason"),
        OrderTotal = Dec(item, "orderTotal"),
        OccurredAt = Time(item, "occurredAt") ?? DateTime.MinValue,
        RecordedAt = Time(item, "recordedAt") ?? DateTime.MinValue,
        ReevaluatedAt = Time(item, "reevaluatedAt"),
        Version = Int(item, "version") ?? 1,
        PurgeAt = Long(item, "purgeAt") ?? 0
    };
}
