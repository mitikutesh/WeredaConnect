using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Models;

namespace WeredaConnect.Server.Data;

/// <summary>
/// ESC-28: DynamoDB implementation of <see cref="IReputationEventRepository"/> with the low-level
/// API (AOT safe, manual marshalling).
///
/// Table <c>{prefix}-reputation-events</c>: PK <c>userId</c>, SK <c>eventId</c> (the slot or the
/// system row key), TTL <c>purgeAt</c>; sparse indexes <c>AppealStatusIndex</c> (appealStatus,
/// appealedAt) and <c>DueIndex</c> (dueDay, dueAt, keys only). The users row is read for its
/// account state and written only on the <c>reputation</c> path, never its own
/// <c>version</c> or <c>updatedAt</c> (user-entity.md 2.0.0 invariant 12).
/// </summary>
public sealed class ReputationEventRepository : IReputationEventRepository
{
    public const string DueIndex = "DueIndex";
    public const string AppealStatusIndex = "AppealStatusIndex";

    private readonly IAmazonDynamoDB _dynamoDb;
    private readonly string _eventsTable;
    private readonly string _usersTable;

    public ReputationEventRepository(IAmazonDynamoDB dynamoDb, string eventsTable, string usersTable)
    {
        _dynamoDb = dynamoDb ?? throw new ArgumentNullException(nameof(dynamoDb));
        _eventsTable = eventsTable ?? throw new ArgumentNullException(nameof(eventsTable));
        _usersTable = usersTable ?? throw new ArgumentNullException(nameof(usersTable));
    }

    /// <inheritdoc />
    public async Task<ReputationUserState?> GetUserStateAsync(string userId)
    {
        var response = await _dynamoDb.GetItemAsync(new GetItemRequest
        {
            TableName = _usersTable,
            Key = new Dictionary<string, AttributeValue> { ["userId"] = S(userId) },
            ConsistentRead = true,
            ProjectionExpression = "#uid, #role, #ds, #susp, #rep, #rs, #rc, #raa",
            ExpressionAttributeNames = new Dictionary<string, string>
            {
                ["#uid"] = "userId",
                ["#role"] = "role",
                ["#ds"] = "deletionState",
                ["#susp"] = "suspended",
                ["#rep"] = "reputation",
                ["#rs"] = "ratingSum",
                ["#rc"] = "ratingCount",
                ["#raa"] = "ratingsAggregatedAt"
            }
        });

        if (response.Item is not { Count: > 0 } item)
        {
            return null;
        }

        var map = item.TryGetValue("reputation", out var rep) && rep.M is { Count: > 0 } m ? m : null;
        return new ReputationUserState
        {
            UserId = userId,
            Role = Str(item, "role") ?? string.Empty,
            DeletionState = Str(item, "deletionState"),
            Suspended = item.TryGetValue("suspended", out var susp) && susp.IsBOOLSet && susp.BOOL,
            MapVersion = map != null ? Int(map, "version") ?? 0 : null,
            StoredTier = map != null ? Str(map, "tier") : null,
            StoredEffectiveRating = map != null ? Dec(map, "effectiveRating") : null,
            StoredReliabilityPct = map != null ? Int(map, "reliabilityPct") : null,
            StoredActivePoints = map != null ? Int(map, "activePoints") : null,
            LastEventId = map != null ? Str(map, "lastEventId") : null,
            LastEventRecordedAt = map != null ? Time(map, "lastEventRecordedAt") : null,
            RatingSum = Long(item, "ratingSum") ?? 0,
            RatingCount = Long(item, "ratingCount") ?? 0,
            RatingsAggregatedAt = Str(item, "ratingsAggregatedAt")
        };
    }

    /// <inheritdoc />
    public async Task<(IReadOnlyList<ReputationEvent> Rows, bool Truncated)> QueryEventsAsync(string userId, int cap)
    {
        var rows = new List<ReputationEvent>();
        var request = new QueryRequest
        {
            TableName = _eventsTable,
            KeyConditionExpression = "userId = :u",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue> { [":u"] = S(userId) },
            // Strongly consistent so that the users version check covers what was read (6.1).
            ConsistentRead = true
        };

        Dictionary<string, AttributeValue>? startKey = null;
        do
        {
            request.ExclusiveStartKey = startKey;
            var page = await _dynamoDb.QueryAsync(request);
            foreach (var item in page.Items)
            {
                if (rows.Count >= cap)
                {
                    return (rows, true);
                }

                rows.Add(FromItem(item));
            }

            startKey = page.LastEvaluatedKey is { Count: > 0 } next ? next : null;
            if (startKey != null && rows.Count >= cap)
            {
                return (rows, true);
            }
        }
        while (startKey != null);

        return (rows, false);
    }

    /// <inheritdoc />
    public async Task<ReputationCommitOutcome> CommitAsync(ReputationWrite write)
    {
        var request = BuildTransaction(write);
        try
        {
            await _dynamoDb.TransactWriteItemsAsync(request);
            return ReputationCommitOutcome.Committed;
        }
        catch (TransactionCanceledException ex)
        {
            var reasons = ex.CancellationReasons ?? new List<CancellationReason>();
            if (write.NewEvent != null && IsCode(reasons, 0, "ConditionalCheckFailed"))
            {
                return ReputationCommitOutcome.EventExists;
            }

            if (reasons.Any(r => r?.Code is "ConditionalCheckFailed" or "TransactionConflict"))
            {
                return ReputationCommitOutcome.Conflict;
            }

            throw;
        }
    }

    /// <summary>The transaction of one write, items in a fixed order (the event first). Exposed for the request-shape tests.</summary>
    internal TransactWriteItemsRequest BuildTransaction(ReputationWrite write)
    {
        var items = new List<TransactWriteItem>();
        if (write.NewEvent != null)
        {
            items.Add(PutNew(write.NewEvent));
        }

        items.Add(new TransactWriteItem { Update = MapUpdate(write) });

        foreach (var row in write.NewSystemRows)
        {
            items.Add(PutNew(row));
        }

        if (write.EarnBack is { } earnBack)
        {
            items.Add(new TransactWriteItem
            {
                Update = new Update
                {
                    TableName = _eventsTable,
                    Key = EventKey(write.UserId, earnBack.EventId),
                    UpdateExpression = "SET #eb = #eb + :one, #v = #v + :one",
                    ConditionExpression = "#v = :seen AND #st = :active",
                    ExpressionAttributeNames = new Dictionary<string, string>
                    {
                        ["#eb"] = "earnedBack",
                        ["#v"] = "version",
                        ["#st"] = "status"
                    },
                    ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                    {
                        [":one"] = N(1),
                        [":seen"] = N(earnBack.SeenVersion),
                        [":active"] = S(ReputationStatuses.Active)
                    }
                }
            });
        }

        if (write.HealAt is { } healAt)
        {
            items.Add(new TransactWriteItem { Update = HealUpsert(_eventsTable, write.UserId, healAt) });
        }

        foreach (var transition in write.Transitions)
        {
            items.Add(new TransactWriteItem { Update = TransitionUpdate(write.UserId, transition) });
        }

        foreach (var eventId in write.Deletes)
        {
            items.Add(new TransactWriteItem
            {
                Delete = new Delete { TableName = _eventsTable, Key = EventKey(write.UserId, eventId) }
            });
        }

        return new TransactWriteItemsRequest { TransactItems = items };
    }

    /// <summary>
    /// The day's <c>HEAL#{yyyy-mm-dd}</c> marker (architecture doc 3.2): upserted by any writer that
    /// leaves a best-effort step behind, found by the daily job on <c>DueIndex</c>. Shared with the
    /// rating create transaction (<see cref="RatingRepository"/>).
    /// </summary>
    internal static Update HealUpsert(string eventsTable, string userId, DateTime at)
    {
        var day = ReputationPolicy.Day(at);
        return new Update
        {
            TableName = eventsTable,
            Key = EventKey(userId, ReputationSlots.Heal + day),
            UpdateExpression = "SET #code = :heal, #dd = :day, #da = if_not_exists(#da, :at), #pa = :purge",
            ExpressionAttributeNames = new Dictionary<string, string>
            {
                ["#code"] = "code",
                ["#dd"] = "dueDay",
                ["#da"] = "dueAt",
                ["#pa"] = "purgeAt"
            },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                [":heal"] = S(ReputationCodes.Heal),
                [":day"] = S(day),
                [":at"] = S(ReputationPolicy.Iso(at)),
                [":purge"] = N(new DateTimeOffset(DateTime.SpecifyKind(at, DateTimeKind.Utc)).Add(ReputationPolicy.HealRetention).ToUnixTimeSeconds())
            }
        };
    }

    private Update MapUpdate(ReputationWrite write)
    {
        var names = new Dictionary<string, string>
        {
            ["#rep"] = "reputation",
            ["#ds"] = "deletionState"
        };
        var values = new Dictionary<string, AttributeValue>
        {
            [":map"] = new() { M = MapItem(write.Map) }
        };

        string versionCondition;
        if (write.ExpectedMapVersion is { } expected)
        {
            names["#v"] = "version";
            values[":v"] = N(expected);
            versionCondition = "#rep.#v = :v";
        }
        else
        {
            versionCondition = "attribute_not_exists(#rep)";
        }

        return new Update
        {
            TableName = _usersTable,
            Key = new Dictionary<string, AttributeValue> { ["userId"] = S(write.UserId) },
            // Only the reputation path: the row's own version and updatedAt stay (invariant 12).
            UpdateExpression = "SET #rep = :map",
            ConditionExpression = $"attribute_exists(userId) AND attribute_not_exists(#ds) AND {versionCondition}",
            ExpressionAttributeNames = names,
            ExpressionAttributeValues = values
        };
    }

    private Update TransitionUpdate(string userId, ReputationRowTransition transition)
    {
        var names = new Dictionary<string, string>
        {
            ["#st"] = "status",
            ["#dd"] = "dueDay",
            ["#da"] = "dueAt"
        };
        var values = new Dictionary<string, AttributeValue> { [":from"] = S(transition.FromStatus) };
        string set;
        if (transition.ToStatus is { } to)
        {
            names["#v"] = "version";
            values[":to"] = S(to);
            values[":one"] = N(1);
            set = "SET #st = :to, #v = if_not_exists(#v, :one) + :one ";
        }
        else
        {
            set = string.Empty;
        }

        return new Update
        {
            TableName = _eventsTable,
            Key = EventKey(userId, transition.EventId),
            UpdateExpression = set + "REMOVE #dd, #da",
            ConditionExpression = "#st = :from",
            ExpressionAttributeNames = names,
            ExpressionAttributeValues = values
        };
    }

    private TransactWriteItem PutNew(ReputationEvent row) => new()
    {
        Put = new Put
        {
            TableName = _eventsTable,
            Item = ToItem(row),
            ConditionExpression = "attribute_not_exists(eventId)"
        }
    };

    /// <inheritdoc />
    public async Task<IReadOnlyList<(string UserId, string EventId)>> QueryDueAsync(string day, string dueAtOrBefore)
    {
        var keys = new List<(string, string)>();
        var request = new QueryRequest
        {
            TableName = _eventsTable,
            IndexName = DueIndex,
            KeyConditionExpression = "#dd = :day AND #da <= :at",
            ExpressionAttributeNames = new Dictionary<string, string> { ["#dd"] = "dueDay", ["#da"] = "dueAt" },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                [":day"] = S(day),
                [":at"] = S(dueAtOrBefore)
            }
        };

        Dictionary<string, AttributeValue>? startKey = null;
        do
        {
            request.ExclusiveStartKey = startKey;
            var page = await _dynamoDb.QueryAsync(request);
            foreach (var item in page.Items)
            {
                if (Str(item, "userId") is { } userId && Str(item, "eventId") is { } eventId)
                {
                    keys.Add((userId, eventId));
                }
            }

            startKey = page.LastEvaluatedKey is { Count: > 0 } next ? next : null;
        }
        while (startKey != null);

        return keys;
    }

    /// <inheritdoc />
    public async Task<int> CountOpenAppealsAsync(string appealStatus, string appealedBefore)
    {
        var request = new QueryRequest
        {
            TableName = _eventsTable,
            IndexName = AppealStatusIndex,
            KeyConditionExpression = "#as = :s AND #aa < :before",
            ExpressionAttributeNames = new Dictionary<string, string> { ["#as"] = "appealStatus", ["#aa"] = "appealedAt" },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                [":s"] = S(appealStatus),
                [":before"] = S(appealedBefore)
            },
            Select = Select.COUNT
        };

        var count = 0;
        Dictionary<string, AttributeValue>? startKey = null;
        do
        {
            request.ExclusiveStartKey = startKey;
            var page = await _dynamoDb.QueryAsync(request);
            count += page.Count;
            startKey = page.LastEvaluatedKey is { Count: > 0 } next ? next : null;
        }
        while (startKey != null);

        return count;
    }

    // ===== marshalling =====

    /// <summary>The <c>reputation</c> map item (user-entity.md 2.0.0 section 2). Absent values are left out.</summary>
    internal static Dictionary<string, AttributeValue> MapItem(ReputationMapWrite map)
    {
        var a = map.Aggregate;
        var item = new Dictionary<string, AttributeValue>
        {
            ["activePoints"] = N(a.ActivePoints),
            ["tier"] = S(a.Tier),
            ["completedOrders90d"] = N(a.CompletedOrders90d),
            ["faultCancels90d"] = N(a.FaultCancels90d),
            ["noShows90d"] = N(a.NoShows90d),
            ["version"] = N(map.Version),
            ["updatedAt"] = S(ReputationPolicy.Iso(map.UpdatedAt))
        };

        if (a.ReliabilityPct is { } reliability)
        {
            item["reliabilityPct"] = N(reliability);
        }

        if (a.EffectiveRating is { } rating)
        {
            item["effectiveRating"] = new AttributeValue { N = rating.ToString("0.0", CultureInfo.InvariantCulture) };
        }

        if (a.Restriction is { } restriction)
        {
            var r = new Dictionary<string, AttributeValue>
            {
                ["cause"] = S(restriction.Cause ?? ReputationCauses.Points)
            };
            if (restriction.Since is { } since)
            {
                r["since"] = S(ReputationPolicy.Iso(since));
            }

            if (restriction.Until is { } until)
            {
                r["until"] = S(ReputationPolicy.Iso(until));
            }

            item["restriction"] = new AttributeValue { M = r };
        }

        if (a.Override is { } overrideRow)
        {
            var o = new Dictionary<string, AttributeValue> { ["tag"] = S(overrideRow.Tag ?? string.Empty) };
            if (overrideRow.Reason is { } reason)
            {
                o["reason"] = S(reason);
            }

            if (overrideRow.By is { } by)
            {
                o["by"] = S(by);
            }

            if (overrideRow.Until is { } until)
            {
                o["until"] = S(ReputationPolicy.Iso(until));
            }

            item["override"] = new AttributeValue { M = o };
        }

        if (a.NextChangeAt is { } next)
        {
            item["nextChangeAt"] = S(ReputationPolicy.Iso(next));
        }

        if (a.LastEventAt is { } last)
        {
            item["lastEventAt"] = S(ReputationPolicy.Iso(last));
        }

        if (!string.IsNullOrEmpty(map.LastEventId))
        {
            item["lastEventId"] = S(map.LastEventId);
        }

        if (map.LastEventRecordedAt is { } recorded)
        {
            item["lastEventRecordedAt"] = S(ReputationPolicy.Iso(recorded));
        }

        return item;
    }

    /// <summary>An event or system row as a DynamoDB item.</summary>
    internal static Dictionary<string, AttributeValue> ToItem(ReputationEvent e)
    {
        var item = new Dictionary<string, AttributeValue>
        {
            ["userId"] = S(e.UserId),
            ["eventId"] = S(e.EventId),
            ["code"] = S(e.Code),
            ["status"] = S(e.Status),
            ["occurredAt"] = S(ReputationPolicy.Iso(e.OccurredAt)),
            ["recordedAt"] = S(ReputationPolicy.Iso(e.RecordedAt)),
            ["version"] = N(e.Version),
            ["purgeAt"] = N(e.PurgeAt)
        };

        if (ReputationCodes.OrderEvents.Contains(e.Code))
        {
            item["basePoints"] = N(e.BasePoints);
            item["points"] = N(e.Points);
            item["earnedBack"] = N(e.EarnedBack);
        }

        Put(item, "party", e.Party);
        Put(item, "zeroReason", e.ZeroReason);
        Put(item, "orderId", e.OrderId);
        Put(item, "tripId", e.TripId);
        Put(item, "buyerId", e.BuyerId);
        Put(item, "pickupDate", e.PickupDate);
        Put(item, "earnBackEventId", e.EarnBackEventId);
        Put(item, "appealStatus", e.AppealStatus);
        Put(item, "appealedAt", e.AppealedAt);
        Put(item, "dueDay", e.DueDay);
        Put(item, "dueAt", e.DueAt);
        Put(item, "sourceSeq", e.SourceSeq);
        Put(item, "cause", e.Cause);
        Put(item, "tag", e.Tag);
        Put(item, "reason", e.Reason);
        Put(item, "by", e.By);
        Put(item, "fromTier", e.FromTier);
        Put(item, "toTier", e.ToTier);
        PutTime(item, "pointsExpireAt", e.PointsExpireAt);
        PutTime(item, "since", e.Since);
        PutTime(item, "until", e.Until);
        PutTime(item, "restrictedUntil", e.RestrictedUntil);

        if (e.OrderTotal is { } total)
        {
            item["orderTotal"] = new AttributeValue { N = total.ToString(CultureInfo.InvariantCulture) };
        }

        if (e.ActivePointsAfter is { } after)
        {
            item["activePointsAfter"] = N(after);
        }

        return item;
    }

    /// <summary>A DynamoDB item as an event or system row.</summary>
    internal static ReputationEvent FromItem(Dictionary<string, AttributeValue> item) => new()
    {
        UserId = Str(item, "userId") ?? string.Empty,
        EventId = Str(item, "eventId") ?? string.Empty,
        Code = Str(item, "code") ?? string.Empty,
        Party = Str(item, "party") ?? string.Empty,
        BasePoints = Int(item, "basePoints") ?? 0,
        Points = Int(item, "points") ?? 0,
        ZeroReason = Str(item, "zeroReason"),
        EarnedBack = Int(item, "earnedBack") ?? 0,
        Status = Str(item, "status") ?? ReputationStatuses.Active,
        OrderId = Str(item, "orderId"),
        TripId = Str(item, "tripId"),
        BuyerId = Str(item, "buyerId"),
        OrderTotal = Dec(item, "orderTotal"),
        PickupDate = Str(item, "pickupDate"),
        OccurredAt = Time(item, "occurredAt") ?? DateTime.MinValue,
        RecordedAt = Time(item, "recordedAt") ?? DateTime.MinValue,
        PointsExpireAt = Time(item, "pointsExpireAt"),
        EarnBackEventId = Str(item, "earnBackEventId"),
        AppealStatus = Str(item, "appealStatus"),
        AppealedAt = Str(item, "appealedAt"),
        DueDay = Str(item, "dueDay"),
        DueAt = Str(item, "dueAt"),
        Version = Int(item, "version") ?? 1,
        ActivePointsAfter = Int(item, "activePointsAfter"),
        SourceSeq = Str(item, "sourceSeq"),
        Cause = Str(item, "cause"),
        Since = Time(item, "since"),
        Until = Time(item, "until"),
        Tag = Str(item, "tag"),
        Reason = Str(item, "reason"),
        By = Str(item, "by"),
        FromTier = Str(item, "fromTier"),
        ToTier = Str(item, "toTier"),
        RestrictedUntil = Time(item, "restrictedUntil"),
        PurgeAt = Long(item, "purgeAt") ?? 0
    };

    internal static Dictionary<string, AttributeValue> EventKey(string userId, string eventId) => new()
    {
        ["userId"] = S(userId),
        ["eventId"] = S(eventId)
    };

    private static bool IsCode(List<CancellationReason> reasons, int index, string code)
        => reasons.Count > index && reasons[index]?.Code == code;

    private static void Put(Dictionary<string, AttributeValue> item, string key, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            item[key] = S(value);
        }
    }

    private static void PutTime(Dictionary<string, AttributeValue> item, string key, DateTime? value)
    {
        if (value is { } time)
        {
            item[key] = S(ReputationPolicy.Iso(time));
        }
    }

    private static AttributeValue S(string value) => new() { S = value };

    private static AttributeValue N(long value) => new() { N = value.ToString(CultureInfo.InvariantCulture) };

    private static string? Str(Dictionary<string, AttributeValue> item, string key)
        => item.TryGetValue(key, out var v) && !string.IsNullOrEmpty(v.S) ? v.S : null;

    private static int? Int(Dictionary<string, AttributeValue> item, string key)
        => item.TryGetValue(key, out var v) && v.N != null
           && decimal.TryParse(v.N, NumberStyles.Number, CultureInfo.InvariantCulture, out var n)
            ? (int)n
            : null;

    private static long? Long(Dictionary<string, AttributeValue> item, string key)
        => item.TryGetValue(key, out var v) && v.N != null
           && decimal.TryParse(v.N, NumberStyles.Number, CultureInfo.InvariantCulture, out var n)
            ? (long)n
            : null;

    private static decimal? Dec(Dictionary<string, AttributeValue> item, string key)
        => item.TryGetValue(key, out var v) && v.N != null
           && decimal.TryParse(v.N, NumberStyles.Number, CultureInfo.InvariantCulture, out var n)
            ? n
            : null;

    private static DateTime? Time(Dictionary<string, AttributeValue> item, string key)
        => ReputationPolicy.TryParseUtc(Str(item, key), out var t) ? t : null;
}
