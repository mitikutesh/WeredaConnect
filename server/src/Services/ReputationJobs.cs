using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.SQS;
using Amazon.SQS.Model;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Functions;
using WeredaConnect.Server.Helpers;

namespace WeredaConnect.Server.Services;

/// <summary>
/// ESC-28: the order-cron's daily reputation job (reputation-architecture.md section 8). It finds
/// its work on the sparse <c>DueIndex</c> (one Query per day partition, no Scan) and settles each
/// user with <see cref="ReputationService.SettleDueAsync"/>: points and completions expire,
/// restrictions and admin overrides end, <c>HEAL#</c> markers are consumed and the aggregate is
/// written fresh. A user the run has no time for stays due for the next run.
/// </summary>
public sealed class ReputationDailyJob
{
    public const string SummaryToken = "REPUTATION_DAILY";
    public const string ReviewOverdueToken = "REPUTATION_REVIEW_OVERDUE";
    public const string AppealOverdueToken = "REPUTATION_APPEAL_OVERDUE";

    /// <summary>Day partitions read back from today on a normal run (a missed run is caught up).</summary>
    public const int DefaultLookbackDays = 7;

    /// <summary>The longest manual catch-up (<c>{"catchUpDays": n}</c>).</summary>
    public const int MaxCatchUpDays = 100;

    /// <summary>How far ahead <c>reputation_recompute_all</c> walks the index (every active row is due within the points life).</summary>
    public const int RecomputeAheadDays = 100;

    /// <summary>An open appeal or review older than this is logged for the admins (alarmed).</summary>
    public static readonly TimeSpan QueueAgeLimit = TimeSpan.FromDays(7);

    private readonly ReputationService _service;
    private readonly IReputationEventRepository _repo;

    public ReputationDailyJob(ReputationService service, IReputationEventRepository repo)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _repo = repo ?? throw new ArgumentNullException(nameof(repo));
    }

    /// <summary>The daily run: the due users of the last <paramref name="lookbackDays"/> days and today, then the queue age check.</summary>
    public async Task<Dictionary<string, string>> RunDailyAsync(DateTime now, int lookbackDays, CronRunSlice slice, ILambdaContext context)
    {
        var nowIso = ReputationPolicy.Iso(now);
        var users = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var d = now.Date.AddDays(-lookbackDays); d <= now.Date; d = d.AddDays(1))
        {
            foreach (var (userId, _) in await _repo.QueryDueAsync(ReputationPolicy.Day(d), nowIso))
            {
                if (seen.Add(userId))
                {
                    users.Add(userId);
                }
            }
        }

        var tally = await SettleAsync(users, now, force: false, slice, context);
        await LogQueueAgeAsync(now, context);

        context.Logger.LogInformation(
            $"{SummaryToken} users={tally.Users} expired={tally.Expired} ended={tally.Ended} healed={tally.Healed} "
            + $"copies=0 leftover={tally.Leftover} failed={tally.Failed}");
        return tally.ToDictionary("reputation_daily", slice.StopReason == null ? "ok" : "partial");
    }

    /// <summary>
    /// The manual recompute after a policy change (8.3): every user with a row due from today to
    /// <see cref="RecomputeAheadDays"/> days ahead (every user with an active row has one) is
    /// recomputed once and their map written with the live settings.
    /// </summary>
    public async Task<Dictionary<string, string>> RecomputeAllAsync(DateTime now, CronRunSlice slice, ILambdaContext context)
    {
        var users = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var d = now.Date; d <= now.Date.AddDays(RecomputeAheadDays); d = d.AddDays(1))
        {
            foreach (var (userId, _) in await _repo.QueryDueAsync(ReputationPolicy.Day(d), "9999-12-31T23:59:59.999Z"))
            {
                if (seen.Add(userId))
                {
                    users.Add(userId);
                }
            }
        }

        var tally = await SettleAsync(users, now, force: true, slice, context);
        context.Logger.LogInformation(
            $"REPUTATION_RECOMPUTE users={tally.Users} written={tally.Written} leftover={tally.Leftover} failed={tally.Failed}");
        return tally.ToDictionary("reputation_recompute_all", slice.StopReason == null ? "ok" : "partial");
    }

    private async Task<Tally> SettleAsync(List<string> users, DateTime now, bool force, CronRunSlice slice, ILambdaContext context)
    {
        var tally = new Tally();
        foreach (var userId in users)
        {
            if (!slice.MayStartRow(context))
            {
                tally.Leftover++;
                continue;
            }

            try
            {
                var result = await _service.SettleDueAsync(userId, now, force, context.Logger);
                if (!result.Processed)
                {
                    continue;
                }

                tally.Users++;
                tally.Expired += result.Expired;
                tally.Ended += result.Ended;
                tally.Healed += result.Healed;
                tally.Written += result.Written ? 1 : 0;
            }
            catch (Exception ex)
            {
                // The user's rows stay due: the next run tries again.
                tally.Failed++;
                context.Logger.LogError(
                    $"Reputation daily step failed for user {MoneyAlarmLog.LogId(userId)}: [{ex.GetType().Name}]");
            }
        }

        return tally;
    }

    /// <summary>8.2 step 3: open reviews and appeals older than 7 days, alarmed through their log tokens.</summary>
    private async Task LogQueueAgeAsync(DateTime now, ILambdaContext context)
    {
        var before = ReputationPolicy.Iso(now - QueueAgeLimit);
        foreach (var (status, token) in new[] { ("review", ReviewOverdueToken), ("pending", AppealOverdueToken) })
        {
            try
            {
                var count = await _repo.CountOpenAppealsAsync(status, before);
                if (count > 0)
                {
                    context.Logger.LogWarning($"{token} count={count}");
                }
            }
            catch (Exception ex)
            {
                context.Logger.LogError($"Reputation queue age check failed for {status}: [{ex.GetType().Name}]");
            }
        }
    }

    private sealed class Tally
    {
        public int Users;
        public int Expired;
        public int Ended;
        public int Healed;
        public int Written;
        public int Leftover;
        public int Failed;

        public Dictionary<string, string> ToDictionary(string action, string outcome) => new()
        {
            ["action"] = action,
            ["outcome"] = outcome,
            ["users"] = Users.ToString(CultureInfo.InvariantCulture),
            ["expired"] = Expired.ToString(CultureInfo.InvariantCulture),
            ["ended"] = Ended.ToString(CultureInfo.InvariantCulture),
            ["healed"] = Healed.ToString(CultureInfo.InvariantCulture),
            ["written"] = Written.ToString(CultureInfo.InvariantCulture),
            ["leftover"] = Leftover.ToString(CultureInfo.InvariantCulture),
            ["failed"] = Failed.ToString(CultureInfo.InvariantCulture)
        };
    }
}

/// <summary>
/// ESC-28: the order-cron's hourly <c>reputation_unpark</c> (reputation-architecture.md 7.5).
/// Receives up to <see cref="MaxMessagesPerRun"/> parked records, re-runs the derivation and the
/// record step on each and deletes the ones that succeed. A message that keeps failing returns
/// after its visibility timeout; after 5 receives the queue's redrive policy moves it to the
/// parked DLQ (alarmed). Replays are safe by the slot ids.
/// </summary>
public sealed class ReputationUnparkJob
{
    public const string SummaryToken = "REPUTATION_UNPARK";
    public const int MaxMessagesPerRun = 100;

    private readonly ReputationRecordProcessor _processor;
    private readonly IAmazonSQS _sqs;
    private readonly string _queueUrl;

    public ReputationUnparkJob(ReputationRecordProcessor processor, IAmazonSQS sqs, string queueUrl)
    {
        _processor = processor ?? throw new ArgumentNullException(nameof(processor));
        _sqs = sqs ?? throw new ArgumentNullException(nameof(sqs));
        _queueUrl = queueUrl ?? throw new ArgumentNullException(nameof(queueUrl));
    }

    public async Task<Dictionary<string, string>> RunAsync(CronRunSlice slice, ILambdaContext context)
    {
        int received = 0, replayed = 0, failed = 0;
        while (received < MaxMessagesPerRun && slice.MayStartRow(context))
        {
            var response = await _sqs.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = _queueUrl,
                MaxNumberOfMessages = Math.Min(10, MaxMessagesPerRun - received),
                WaitTimeSeconds = 0
            });
            var messages = response.Messages ?? new List<Message>();
            if (messages.Count == 0)
            {
                break;
            }

            foreach (var message in messages)
            {
                received++;
                try
                {
                    using var doc = JsonDocument.Parse(message.Body);
                    await _processor.HandleRecordAsync(doc.RootElement, context.Logger);
                    await _sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = _queueUrl, ReceiptHandle = message.ReceiptHandle });
                    replayed++;
                }
                catch (Exception ex)
                {
                    // Left in the queue: it comes back after its visibility timeout.
                    failed++;
                    context.Logger.LogError($"Reputation unpark failed for a message: [{ex.GetType().Name}]");
                }
            }
        }

        context.Logger.LogInformation($"{SummaryToken} received={received} replayed={replayed} failed={failed}");
        return new Dictionary<string, string>
        {
            ["action"] = "reputation_unpark",
            ["outcome"] = slice.StopReason == null ? "ok" : "partial",
            ["received"] = received.ToString(CultureInfo.InvariantCulture),
            ["replayed"] = replayed.ToString(CultureInfo.InvariantCulture),
            ["failed"] = failed.ToString(CultureInfo.InvariantCulture)
        };
    }
}

/// <summary>
/// ESC-28: the one-time rating aggregate backfill (reputation-architecture.md 12.2, rating-api.md
/// 1.2.0 "Stored aggregate" step 4), run by hand once per environment after the rating create
/// transaction is live. No Scan: farmers and truckers come from <c>RoleWaredaIndex</c>, their
/// ratings from <c>RateeIdCreatedAtIndex</c>. Per user, chunks of up to 99 ratings: <c>ADD</c> the
/// stars of the ratings in the user's role, <c>SET inAggregate</c> on each rating conditioned on it
/// being absent, and stamp <c>ratingsAggregatedAt</c> on the last chunk. A rerun skips stamped users
/// and flagged ratings, so nothing is counted twice. Users without a <c>woreda</c> are not on the
/// index; their reads keep falling back to the ratings walk.
/// </summary>
public sealed class RatingAggregateBackfill
{
    public const string SummaryToken = "REPUTATION_BACKFILL";
    public const string RoleIndex = "RoleWaredaIndex";
    public const string RateeIndex = "RateeIdCreatedAtIndex";

    /// <summary>Ratings per transaction (with the users row: 100 items).</summary>
    public const int RatingsPerChunk = 99;

    private readonly IAmazonDynamoDB _dynamoDb;
    private readonly string _usersTable;
    private readonly string _ratingsTable;

    public RatingAggregateBackfill(IAmazonDynamoDB dynamoDb, string usersTable, string ratingsTable)
    {
        _dynamoDb = dynamoDb ?? throw new ArgumentNullException(nameof(dynamoDb));
        _usersTable = usersTable ?? throw new ArgumentNullException(nameof(usersTable));
        _ratingsTable = ratingsTable ?? throw new ArgumentNullException(nameof(ratingsTable));
    }

    public async Task<Dictionary<string, string>> RunAsync(DateTime now, CronRunSlice slice, ILambdaContext context)
    {
        int users = 0, ratings = 0, skipped = 0, leftover = 0, failed = 0;
        foreach (var role in new[] { "farmer", "trucker" })
        {
            foreach (var user in await QueryRoleAsync(role))
            {
                if (user.Stamped || user.Deleting)
                {
                    skipped++;
                    continue;
                }

                if (!slice.MayStartRow(context))
                {
                    leftover++;
                    continue;
                }

                try
                {
                    ratings += await BackfillUserAsync(user.UserId, role, now);
                    users++;
                }
                catch (Exception ex)
                {
                    failed++;
                    context.Logger.LogError(
                        $"Rating backfill failed for user {MoneyAlarmLog.LogId(user.UserId)}: [{ex.GetType().Name}]");
                }
            }
        }

        context.Logger.LogInformation(
            $"{SummaryToken} users={users} ratings={ratings} skipped={skipped} leftover={leftover} failed={failed}");
        return new Dictionary<string, string>
        {
            ["action"] = "reputation_backfill_ratings",
            ["outcome"] = leftover == 0 && failed == 0 ? "ok" : "partial",
            ["users"] = users.ToString(CultureInfo.InvariantCulture),
            ["ratings"] = ratings.ToString(CultureInfo.InvariantCulture),
            ["skipped"] = skipped.ToString(CultureInfo.InvariantCulture),
            ["leftover"] = leftover.ToString(CultureInfo.InvariantCulture),
            ["failed"] = failed.ToString(CultureInfo.InvariantCulture)
        };
    }

    /// <summary>Backfill one user; returns how many ratings were flagged. A user whose condition fails (role changed, stamped meanwhile) throws and is retried by a rerun.</summary>
    internal async Task<int> BackfillUserAsync(string userId, string role, DateTime now)
    {
        var pending = await QueryUnflaggedRatingsAsync(userId);
        var chunks = pending.Count == 0
            ? new List<List<(string RatingId, int Stars, string RateeRole)>> { new() }
            : pending.Chunk(RatingsPerChunk).Select(c => c.ToList()).ToList();

        for (var i = 0; i < chunks.Count; i++)
        {
            var chunk = chunks[i];
            var last = i == chunks.Count - 1;
            var inRole = chunk.Where(r => r.RateeRole == role).ToList();
            var items = chunk.Select(r => new TransactWriteItem
            {
                Update = new Update
                {
                    TableName = _ratingsTable,
                    Key = new Dictionary<string, AttributeValue> { ["ratingId"] = new() { S = r.RatingId } },
                    UpdateExpression = "SET #ia = :ia",
                    ConditionExpression = "attribute_not_exists(#ia)",
                    ExpressionAttributeNames = new Dictionary<string, string> { ["#ia"] = "inAggregate" },
                    ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                    {
                        [":ia"] = new() { BOOL = r.RateeRole == role, IsBOOLSet = true }
                    }
                }
            }).ToList();

            var values = new Dictionary<string, AttributeValue>
            {
                [":sum"] = new() { N = inRole.Sum(r => r.Stars).ToString(CultureInfo.InvariantCulture) },
                [":count"] = new() { N = inRole.Count.ToString(CultureInfo.InvariantCulture) },
                [":role"] = new() { S = role }
            };
            var update = "ADD #rs :sum, #rc :count";
            if (last)
            {
                update += " SET #raa = :now";
                values[":now"] = new() { S = ReputationPolicy.Iso(now) };
            }

            items.Add(new TransactWriteItem
            {
                Update = new Update
                {
                    TableName = _usersTable,
                    Key = new Dictionary<string, AttributeValue> { ["userId"] = new() { S = userId } },
                    UpdateExpression = update,
                    ConditionExpression =
                        "attribute_exists(userId) AND attribute_not_exists(#raa) AND attribute_not_exists(#ds) AND #role = :role",
                    ExpressionAttributeNames = new Dictionary<string, string>
                    {
                        ["#rs"] = "ratingSum",
                        ["#rc"] = "ratingCount",
                        ["#raa"] = "ratingsAggregatedAt",
                        ["#ds"] = "deletionState",
                        ["#role"] = "role"
                    },
                    ExpressionAttributeValues = values
                }
            });

            await _dynamoDb.TransactWriteItemsAsync(new TransactWriteItemsRequest { TransactItems = items });
        }

        return pending.Count;
    }

    private async Task<List<(string UserId, bool Stamped, bool Deleting)>> QueryRoleAsync(string role)
    {
        var users = new List<(string, bool, bool)>();
        var request = new QueryRequest
        {
            TableName = _usersTable,
            IndexName = RoleIndex,
            KeyConditionExpression = "#role = :role",
            ProjectionExpression = "userId, #raa, #ds",
            ExpressionAttributeNames = new Dictionary<string, string>
            {
                ["#role"] = "role",
                ["#raa"] = "ratingsAggregatedAt",
                ["#ds"] = "deletionState"
            },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue> { [":role"] = new() { S = role } }
        };

        Dictionary<string, AttributeValue>? startKey = null;
        do
        {
            request.ExclusiveStartKey = startKey;
            var page = await _dynamoDb.QueryAsync(request);
            foreach (var item in page.Items)
            {
                if (item.TryGetValue("userId", out var id) && !string.IsNullOrEmpty(id.S))
                {
                    users.Add((id.S, item.ContainsKey("ratingsAggregatedAt"), item.ContainsKey("deletionState")));
                }
            }

            startKey = page.LastEvaluatedKey is { Count: > 0 } next ? next : null;
        }
        while (startKey != null);

        return users;
    }

    private async Task<List<(string RatingId, int Stars, string RateeRole)>> QueryUnflaggedRatingsAsync(string userId)
    {
        var ratings = new List<(string, int, string)>();
        var request = new QueryRequest
        {
            TableName = _ratingsTable,
            IndexName = RateeIndex,
            KeyConditionExpression = "rateeId = :u",
            FilterExpression = "attribute_not_exists(#ia)",
            ProjectionExpression = "ratingId, stars, rateeRole",
            ExpressionAttributeNames = new Dictionary<string, string> { ["#ia"] = "inAggregate" },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue> { [":u"] = new() { S = userId } }
        };

        Dictionary<string, AttributeValue>? startKey = null;
        do
        {
            request.ExclusiveStartKey = startKey;
            var page = await _dynamoDb.QueryAsync(request);
            foreach (var item in page.Items)
            {
                if (item.TryGetValue("ratingId", out var id) && !string.IsNullOrEmpty(id.S)
                    && item.TryGetValue("stars", out var stars) && int.TryParse(stars.N, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
                    && n is >= 1 and <= 5)
                {
                    ratings.Add((id.S, n, item.TryGetValue("rateeRole", out var r) ? r.S ?? string.Empty : string.Empty));
                }
            }

            startKey = page.LastEvaluatedKey is { Count: > 0 } next ? next : null;
        }
        while (startKey != null);

        return ratings;
    }
}
