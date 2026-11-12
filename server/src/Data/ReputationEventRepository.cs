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
            RatingsAggregatedAt = Str(item, "ratingsAggregatedAt"),
            Snapshot = SnapshotFromMap(map)
        };
    }

    /// <summary>ESC-29: the stored <c>reputation</c> map of a users row item, or null when it has none.</summary>
    internal static ReputationSnapshot? SnapshotFromItem(Dictionary<string, AttributeValue> userItem)
        => SnapshotFromMap(userItem.TryGetValue("reputation", out var rep) && rep.M is { Count: > 0 } m ? m : null);

    private static ReputationSnapshot? SnapshotFromMap(Dictionary<string, AttributeValue>? map)
    {
        if (map == null)
        {
            return null;
        }

        var restriction = map.TryGetValue("restriction", out var r) && r.M is { Count: > 0 } rm ? rm : null;
        var overrideMap = map.TryGetValue("override", out var o) && o.M is { Count: > 0 } om ? om : null;
        return new ReputationSnapshot
        {
            Version = Int(map, "version") ?? 0,
            Tier = Str(map, "tier") ?? ReputationTiers.Good,
            ActivePoints = Int(map, "activePoints") ?? 0,
            CompletedOrders90d = Int(map, "completedOrders90d") ?? 0,
            FaultCancels90d = Int(map, "faultCancels90d") ?? 0,
            NoShows90d = Int(map, "noShows90d") ?? 0,
            ReliabilityPct = Int(map, "reliabilityPct"),
            EffectiveRating = Dec(map, "effectiveRating"),
            HasRestriction = restriction != null,
            RestrictionCause = restriction != null ? Str(restriction, "cause") : null,
            RestrictionSince = restriction != null ? Time(restriction, "since") : null,
            RestrictionUntil = restriction != null ? Time(restriction, "until") : null,
            OverrideTag = overrideMap != null ? Str(overrideMap, "tag") : null,
            OverrideReason = overrideMap != null ? Str(overrideMap, "reason") : null,
            OverrideUntil = overrideMap != null ? Time(overrideMap, "until") : null,
            NextChangeAt = Time(map, "nextChangeAt")
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

        foreach (var update in write.RowUpdates)
        {
            items.Add(new TransactWriteItem { Update = RowUpdate(write.UserId, update) });
        }

        return new TransactWriteItemsRequest { TransactItems = items };
    }

    /// <summary>
    /// ESC-32: an admin's change of one row (<see cref="ReputationRowUpdate"/>). The row must exist
    /// with the status read; the other conditions are added only when asked for. No empty value is
    /// ever written: an attribute with nothing to store is left out of the expression.
    /// </summary>
    private Update RowUpdate(string userId, ReputationRowUpdate update)
    {
        var names = new Dictionary<string, string> { ["#st"] = "status", ["#v"] = "version" };
        var values = new Dictionary<string, AttributeValue>
        {
            [":from"] = S(update.FromStatus),
            [":one"] = N(1)
        };
        var conditions = new List<string> { "attribute_exists(eventId)", "#st = :from" };
        var sets = new List<string> { "#v = if_not_exists(#v, :one) + :one" };
        var removes = new List<string>();

        void Set(string placeholder, string attribute, string? value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return;
            }

            names["#" + placeholder] = attribute;
            values[":" + placeholder] = S(value);
            sets.Add($"#{placeholder} = :{placeholder}");
        }

        void Remove(string placeholder, string attribute)
        {
            names["#" + placeholder] = attribute;
            removes.Add("#" + placeholder);
        }

        if (update.SeenVersion is { } seen)
        {
            values[":seen"] = N(seen);
            conditions.Add("#v = :seen");
        }

        if (update.RequiredAppealStatus is { } appealStatus)
        {
            names["#as"] = "appealStatus";
            values[":as"] = S(appealStatus);
            conditions.Add("#as = :as");
        }

        if (update.CheckAppealTextAt)
        {
            names["#textAt"] = "appealTextAt";
            if (string.IsNullOrEmpty(update.SeenAppealTextAt))
            {
                conditions.Add("attribute_not_exists(#textAt)");
            }
            else
            {
                values[":textAt"] = S(update.SeenAppealTextAt);
                conditions.Add("#textAt = :textAt");
            }
        }

        if (update.RequireNoUntil)
        {
            names["#until"] = "until";
            conditions.Add("attribute_not_exists(#until)");
        }

        if (update.ToStatus is { } to)
        {
            values[":to"] = S(to);
            sets.Add("#st = :to");
        }

        Set("decision", "appealDecision", update.AppealDecision);
        Set("decidedAt", "decidedAt", update.DecidedAt);
        Set("decidedBy", "decidedBy", update.DecidedBy);
        Set("note", "decisionNote", update.DecisionNote);
        Set("liftedAt", "liftedAt", update.LiftedAt);
        Set("liftedBy", "liftedBy", update.LiftedBy);

        if (update.CloseAppeal)
        {
            if (!names.ContainsKey("#as"))
            {
                names["#as"] = "appealStatus";
            }

            removes.Add("#as");
            Remove("aa", "appealedAt");
        }

        if (update.SetUntil is { } until && !update.LeaveDueIndex)
        {
            names["#until"] = "until";
            values[":until"] = S(ReputationPolicy.Iso(until));
            sets.Add("#until = :until");
            Set("dd", "dueDay", ReputationPolicy.Day(until));
            Set("da", "dueAt", ReputationPolicy.Iso(until));
        }
        else if (update.LeaveDueIndex)
        {
            Remove("dd", "dueDay");
            Remove("da", "dueAt");
        }

        return new Update
        {
            TableName = _eventsTable,
            Key = EventKey(userId, update.EventId),
            UpdateExpression = "SET " + string.Join(", ", sets)
                               + (removes.Count > 0 ? " REMOVE " + string.Join(", ", removes) : string.Empty),
            ConditionExpression = string.Join(" AND ", conditions),
            ExpressionAttributeNames = names,
            ExpressionAttributeValues = values
        };
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

    /// <inheritdoc />
    public async Task<(IReadOnlyList<ReputationEvent> Rows, ReputationAppealCursor? Next)> QueryAppealsAsync(
        string appealStatus, int limit, ReputationAppealCursor? after)
    {
        var page = await _dynamoDb.QueryAsync(BuildAppealsQuery(appealStatus, limit, after));
        var rows = page.Items.Select(FromItem).ToList();
        var next = page.LastEvaluatedKey is { Count: > 0 } last
                   && Str(last, "appealedAt") is { } at && Str(last, "userId") is { } userId && Str(last, "eventId") is { } eventId
            ? new ReputationAppealCursor(at, userId, eventId)
            : null;
        return (rows, next);
    }

    /// <summary>The appeals queue page (reputation-api.md 9.2): one partition of <c>AppealStatusIndex</c>, oldest first.</summary>
    internal QueryRequest BuildAppealsQuery(string appealStatus, int limit, ReputationAppealCursor? after) => new()
    {
        TableName = _eventsTable,
        IndexName = AppealStatusIndex,
        KeyConditionExpression = "#as = :s",
        ExpressionAttributeNames = new Dictionary<string, string> { ["#as"] = "appealStatus" },
        ExpressionAttributeValues = new Dictionary<string, AttributeValue> { [":s"] = S(appealStatus) },
        ScanIndexForward = true,
        Limit = limit,
        ExclusiveStartKey = after == null
            ? null
            : new Dictionary<string, AttributeValue>
            {
                ["appealStatus"] = S(appealStatus),
                ["appealedAt"] = S(after.AppealedAt),
                ["userId"] = S(after.UserId),
                ["eventId"] = S(after.EventId)
            }
    };

    /// <inheritdoc />
    public async Task<ReputationEvent?> GetEventAsync(string userId, string eventId)
    {
        var response = await _dynamoDb.GetItemAsync(new GetItemRequest
        {
            TableName = _eventsTable,
            Key = EventKey(userId, eventId),
            ConsistentRead = true
        });
        return response.Item is { Count: > 0 } item ? FromItem(item) : null;
    }

    /// <inheritdoc />
    public async Task<bool> TryAppealAsync(string userId, string eventId, string text, string? photoRef, DateTime at, bool openReview)
    {
        try
        {
            await _dynamoDb.UpdateItemAsync(BuildAppeal(userId, eventId, text, photoRef, at, openReview));
            return true;
        }
        catch (ConditionalCheckFailedException)
        {
            return false;
        }
    }

    /// <summary>
    /// The appeal of one event (reputation-api.md section 6): the text, the optional photo and,
    /// unless the row is already in an admin review, <c>appealStatus: pending</c> with
    /// <c>appealedAt</c> (the row joins <c>AppealStatusIndex</c>). One appeal per event: the row
    /// must exist, be active and have no appeal text. No counted attribute changes, so neither the
    /// row's nor the users map's version moves.
    /// </summary>
    internal UpdateItemRequest BuildAppeal(string userId, string eventId, string text, string? photoRef, DateTime at, bool openReview)
    {
        var iso = ReputationPolicy.Iso(at);
        var names = new Dictionary<string, string>
        {
            ["#text"] = "appealText",
            ["#textAt"] = "appealTextAt",
            ["#st"] = "status"
        };
        var values = new Dictionary<string, AttributeValue>
        {
            [":text"] = S(text),
            [":at"] = S(iso),
            [":active"] = S(ReputationStatuses.Active)
        };
        var set = new List<string> { "#text = :text", "#textAt = :at" };
        if (!string.IsNullOrEmpty(photoRef))
        {
            names["#photo"] = "appealPhotoRef";
            values[":photo"] = S(photoRef);
            set.Add("#photo = :photo");
        }

        if (!openReview)
        {
            names["#as"] = "appealStatus";
            names["#aa"] = "appealedAt";
            values[":pending"] = S("pending");
            set.Add("#as = :pending");
            set.Add("#aa = :at");
        }

        return new UpdateItemRequest
        {
            TableName = _eventsTable,
            Key = EventKey(userId, eventId),
            UpdateExpression = "SET " + string.Join(", ", set),
            ConditionExpression = "attribute_exists(eventId) AND attribute_not_exists(#text) AND #st = :active",
            ExpressionAttributeNames = names,
            ExpressionAttributeValues = values
        };
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
        Put(item, "appealText", e.AppealText);
        Put(item, "appealPhotoRef", e.AppealPhotoRef);
        Put(item, "appealTextAt", e.AppealTextAt);
        Put(item, "appealDecision", e.AppealDecision);
        Put(item, "decidedAt", e.DecidedAt);
        Put(item, "decisionNote", e.DecisionNote);
        Put(item, "decidedBy", e.DecidedBy);
        Put(item, "liftedAt", e.LiftedAt);
        Put(item, "liftedBy", e.LiftedBy);
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
        AppealText = Str(item, "appealText"),
        AppealPhotoRef = Str(item, "appealPhotoRef"),
        AppealTextAt = Str(item, "appealTextAt"),
        AppealDecision = Str(item, "appealDecision"),
        DecidedAt = Str(item, "decidedAt"),
        DecisionNote = Str(item, "decisionNote"),
        DecidedBy = Str(item, "decidedBy"),
        LiftedAt = Str(item, "liftedAt"),
        LiftedBy = Str(item, "liftedBy"),
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
