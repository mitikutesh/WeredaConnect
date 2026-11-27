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
            + $"copies={tally.Copied} leftover={tally.Leftover} failed={tally.Failed}");
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
                tally.Copied += result.Copied;
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
        public int Copied;
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
/// FUP-161: each receive long-polls for <see cref="ReceiveWaitSeconds"/> and the run ends only
/// after <see cref="EmptyRepliesToStop"/> empty replies in a row. A short poll samples only some
/// SQS servers and can answer empty while a small queue holds messages, which left parked records
/// waiting for a later hour.
/// </summary>
public sealed class ReputationUnparkJob
{
    public const string SummaryToken = "REPUTATION_UNPARK";
    public const int MaxMessagesPerRun = 100;

    /// <summary>FUP-161: the long poll of one receive, in seconds (an empty queue answers after it).</summary>
    public const int ReceiveWaitSeconds = 1;

    /// <summary>FUP-161: empty replies in a row that end the run (the first empty reply is tried once more).</summary>
    public const int EmptyRepliesToStop = 2;

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
        int received = 0, replayed = 0, failed = 0, emptyInARow = 0;
        // Each receive waits at most ReceiveWaitSeconds, far inside the slice's 30 second reserve.
        while (received < MaxMessagesPerRun && slice.MayStartRow(context))
        {
            var response = await _sqs.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = _queueUrl,
                MaxNumberOfMessages = Math.Min(10, MaxMessagesPerRun - received),
                WaitTimeSeconds = ReceiveWaitSeconds
            });
            var messages = response.Messages ?? new List<Message>();
            if (messages.Count == 0)
            {
                if (++emptyInARow >= EmptyRepliesToStop)
                {
                    break;
                }

                continue;
            }

            emptyInARow = 0;

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
/// and flagged ratings, so nothing is counted twice.
/// FUP-162: users without a <c>woreda</c> are not on the index. After the walk, a one-off filtered
/// Scan of the users table (manual action only, never a request path) finds the farmers and
/// truckers still without <c>ratingsAggregatedAt</c> and backfills them the same way. The Scan keeps
/// to the run's time budget: a run that stops in it answers <c>scanCursor</c>, which the next run
/// takes as <c>scanFrom</c>. The rating create stamps an unstamped ratee too
/// (<see cref="StampSmallAsync"/>, through <see cref="RatingAggregateHeal"/>).
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

    /// <param name="scanFrom">FUP-162: the <c>scanCursor</c> of an earlier run that stopped in the Scan; null starts it from the beginning.</param>
    public async Task<Dictionary<string, string>> RunAsync(
        DateTime now, CronRunSlice slice, ILambdaContext context, string? scanFrom = null)
    {
        int users = 0, ratings = 0, skipped = 0, leftover = 0, failed = 0;
        var walked = new HashSet<string>(StringComparer.Ordinal);
        foreach (var role in new[] { "farmer", "trucker" })
        {
            foreach (var user in await QueryRoleAsync(role))
            {
                walked.Add(user.UserId);
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

        // FUP-162: then the farmers and truckers the index does not list, once the walk is done.
        var scan = leftover == 0
            ? await ScanUnindexedAsync(now, slice, context, scanFrom, walked)
            : new UnindexedScan(0, 0, 0, Stopped: true, Cursor: scanFrom);
        users += scan.Users;
        ratings += scan.Ratings;
        failed += scan.Failed;

        context.Logger.LogInformation(
            $"{SummaryToken} users={users} ratings={ratings} skipped={skipped} leftover={leftover} failed={failed} "
            + $"unindexed={scan.Users} scanDone={(scan.Stopped ? "false" : "true")}");
        var summary = new Dictionary<string, string>
        {
            ["action"] = "reputation_backfill_ratings",
            ["outcome"] = leftover == 0 && failed == 0 && !scan.Stopped ? "ok" : "partial",
            ["users"] = users.ToString(CultureInfo.InvariantCulture),
            ["ratings"] = ratings.ToString(CultureInfo.InvariantCulture),
            ["skipped"] = skipped.ToString(CultureInfo.InvariantCulture),
            ["leftover"] = leftover.ToString(CultureInfo.InvariantCulture),
            ["failed"] = failed.ToString(CultureInfo.InvariantCulture),
            ["unindexed"] = scan.Users.ToString(CultureInfo.InvariantCulture)
        };
        if (scan.Stopped && !string.IsNullOrEmpty(scan.Cursor))
        {
            summary["scanCursor"] = scan.Cursor;
        }

        return summary;
    }

    /// <summary>What the Scan of <see cref="ScanUnindexedAsync"/> did; <c>Cursor</c> is where a stopped Scan resumes.</summary>
    private readonly record struct UnindexedScan(int Users, int Ratings, int Failed, bool Stopped, string? Cursor);

    /// <summary>
    /// FUP-162: the one-off Scan of the users table for the farmers and truckers without
    /// <c>ratingsAggregatedAt</c> and not being deleted (in practice the rows without a
    /// <c>woreda</c>, which <c>RoleWaredaIndex</c> does not list). Only this manual action runs it. Each
    /// page costs read capacity for the whole page, matching or not (about one read unit per 8 KB,
    /// eventually consistent), so a run reads the table once. Users the walk already tried are
    /// skipped. Before each page and each user the time budget is checked; on a stop the cursor is the
    /// last user handled (or where this run started), so a resumed Scan neither skips nor repeats one.
    /// </summary>
    private async Task<UnindexedScan> ScanUnindexedAsync(
        DateTime now, CronRunSlice slice, ILambdaContext context, string? scanFrom, HashSet<string> walked)
    {
        int users = 0, ratings = 0, failed = 0;
        var position = string.IsNullOrEmpty(scanFrom) ? null : scanFrom;
        var request = new ScanRequest
        {
            TableName = _usersTable,
            ProjectionExpression = "userId, #role",
            FilterExpression = "#role IN (:farmer, :trucker) AND attribute_not_exists(#raa) AND attribute_not_exists(#ds)",
            ExpressionAttributeNames = new Dictionary<string, string>
            {
                ["#role"] = "role",
                ["#raa"] = "ratingsAggregatedAt",
                ["#ds"] = "deletionState"
            },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                [":farmer"] = new() { S = "farmer" },
                [":trucker"] = new() { S = "trucker" }
            }
        };

        while (true)
        {
            if (!slice.MayStartRow(context))
            {
                return new UnindexedScan(users, ratings, failed, Stopped: true, Cursor: position);
            }

            request.ExclusiveStartKey = position == null
                ? null
                : new Dictionary<string, AttributeValue> { ["userId"] = new() { S = position } };
            var page = await _dynamoDb.ScanAsync(request);
            foreach (var item in page.Items)
            {
                if (!item.TryGetValue("userId", out var id) || string.IsNullOrEmpty(id.S))
                {
                    continue;
                }

                if (!walked.Contains(id.S))
                {
                    if (!slice.MayStartRow(context))
                    {
                        return new UnindexedScan(users, ratings, failed, Stopped: true, Cursor: position);
                    }

                    try
                    {
                        ratings += await BackfillUserAsync(id.S, item["role"].S, now);
                        users++;
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        context.Logger.LogError(
                            $"Rating backfill failed for user {MoneyAlarmLog.LogId(id.S)}: [{ex.GetType().Name}]");
                    }
                }

                position = id.S;
            }

            if (page.LastEvaluatedKey is not { Count: > 0 } next
                || !next.TryGetValue("userId", out var last) || string.IsNullOrEmpty(last.S))
            {
                return new UnindexedScan(users, ratings, failed, Stopped: false, Cursor: null);
            }

            position = last.S;
        }
    }

    /// <summary>Backfill one user; returns how many ratings were flagged. A user whose condition fails (role changed, stamped meanwhile) throws and is retried by a rerun.</summary>
    internal async Task<int> BackfillUserAsync(string userId, string role, DateTime now)
    {
        var pending = (await QueryUnflaggedRatingsAsync(userId, cap: null))!; // unbounded: never null
        await AddAndStampAsync(userId, role, now, pending);
        return pending.Count;
    }

    /// <summary>FUP-162: the most index pages the bounded read of <see cref="StampSmallAsync"/> reads.</summary>
    internal const int MaxSmallPages = 5;

    /// <summary>
    /// FUP-162: <see cref="BackfillUserAsync"/> for a request path, bounded. The read takes pages of
    /// <paramref name="maxRatings"/> + 1 rows, at most <see cref="MaxSmallPages"/> of them. A user with
    /// more than <paramref name="maxRatings"/> ratings without <c>inAggregate</c>, or more rows than
    /// that read covers, is left alone (false, nothing written; the order-cron action stamps them);
    /// otherwise one transaction when <paramref name="maxRatings"/> is at most
    /// <see cref="RatingsPerChunk"/>. Throws as <see cref="BackfillUserAsync"/> does: a row stamped
    /// meanwhile cancels the transaction.
    /// </summary>
    internal async Task<bool> StampSmallAsync(string userId, string role, DateTime now, int maxRatings)
    {
        var pending = await QueryUnflaggedRatingsAsync(userId, cap: maxRatings + 1);
        if (pending == null || pending.Count > maxRatings)
        {
            return false;
        }

        await AddAndStampAsync(userId, role, now, pending);
        return true;
    }

    /// <summary>The chunked transactions of one user (architecture doc 12.2 step 3); the last chunk stamps <c>ratingsAggregatedAt</c>.</summary>
    private async Task AddAndStampAsync(
        string userId, string role, DateTime now, List<(string RatingId, int Stars, string RateeRole)> pending)
    {
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

    /// <summary>
    /// The user's ratings without <c>inAggregate</c>, all pages. FUP-162: with a <paramref name="cap"/>,
    /// pages of <paramref name="cap"/> rows and at most <see cref="MaxSmallPages"/> of them; null when
    /// the cap is reached with rows left, or the pages run out first.
    /// </summary>
    private async Task<List<(string RatingId, int Stars, string RateeRole)>?> QueryUnflaggedRatingsAsync(string userId, int? cap)
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
        if (cap != null)
        {
            request.Limit = cap.Value;
        }

        var pages = 0;
        Dictionary<string, AttributeValue>? startKey = null;
        do
        {
            if (cap != null && pages > 0 && (ratings.Count >= cap || pages >= MaxSmallPages))
            {
                return null;
            }

            request.ExclusiveStartKey = startKey;
            var page = await _dynamoDb.QueryAsync(request);
            pages++;
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

/// <summary>
/// FUP-160: the one-off stamp of the owner fields (reputation-architecture.md 12.3), run by hand
/// once per environment after <c>reputationPenalties</c> goes on and before
/// <c>reputationPublicTags</c>. The copies are written only while <c>reputationPenalties</c> is on,
/// so a crop or trip created before that has none until its owner's next change. The owners come
/// from the active partitions of the status indexes buyer browse reads (crops
/// <c>StatusCreatedAtIndex</c> <c>available</c> and <c>reserved</c>, trips
/// <c>StatusDepartureDateIndex</c> <c>open</c> and <c>matched</c>; Query, no Scan), so an owner
/// without a <c>woreda</c> is reached too and a user with no active row costs nothing. Each owner
/// gets <see cref="ReputationService.StampOwnerCopiesAsync"/>: the copy refresh of 6.3, which writes
/// only rows that do not already hold the values, so a rerun writes nothing. Owners are taken in
/// user id order; a run out of time returns the first owner it did not reach as <c>next</c>, and
/// <c>{"startAt": next}</c> goes on from there.
/// </summary>
public sealed class OwnerCopyStampJob
{
    public const string SummaryToken = "REPUTATION_OWNER_STAMP";
    public const string Action = "reputation_stamp_owner_copies";
    public const string CropStatusIndex = "StatusCreatedAtIndex";
    public const string TripStatusIndex = "StatusDepartureDateIndex";

    /// <summary>The longest <c>startAt</c> taken (a user id from a previous run's <c>next</c>).</summary>
    public const int MaxStartAtLength = 128;

    private readonly ReputationService _service;
    private readonly IAmazonDynamoDB _dynamoDb;
    private readonly string _cropsTable;
    private readonly string _tripsTable;

    public OwnerCopyStampJob(ReputationService service, IAmazonDynamoDB dynamoDb, string cropsTable, string tripsTable)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _dynamoDb = dynamoDb ?? throw new ArgumentNullException(nameof(dynamoDb));
        _cropsTable = cropsTable ?? throw new ArgumentNullException(nameof(cropsTable));
        _tripsTable = tripsTable ?? throw new ArgumentNullException(nameof(tripsTable));
    }

    /// <summary>Whether <paramref name="value"/> can be a <c>startAt</c> (a user id: no blank, no control character, not too long).</summary>
    public static bool IsValidStartAt(string? value)
        => !string.IsNullOrWhiteSpace(value) && value.Length <= MaxStartAtLength && !value.Any(char.IsControl);

    public async Task<Dictionary<string, string>> RunAsync(string? startAt, CronRunSlice slice, ILambdaContext context)
    {
        var owners = await ActiveOwnersAsync();
        int users = 0, rows = 0, skipped = 0, leftover = 0, failed = 0;
        string? next = null;
        foreach (var userId in owners)
        {
            if (startAt != null && string.CompareOrdinal(userId, startAt) < 0)
            {
                continue;
            }

            if (!slice.MayStartRow(context))
            {
                next ??= userId;
                leftover++;
                continue;
            }

            try
            {
                if (await _service.StampOwnerCopiesAsync(userId) is { } written)
                {
                    users++;
                    rows += written;
                }
                else
                {
                    // No users row, or the account is being deleted: nothing to copy.
                    skipped++;
                }
            }
            catch (Exception ex)
            {
                // A rerun (or a run from an earlier startAt) tries the owner again.
                failed++;
                context.Logger.LogError(
                    $"Owner stamp failed for user {MoneyAlarmLog.LogId(userId)}: [{ex.GetType().Name}]");
            }
        }

        context.Logger.LogInformation(
            $"{SummaryToken} owners={owners.Count} users={users} rows={rows} skipped={skipped} leftover={leftover} failed={failed} "
            + $"from={(startAt == null ? "start" : "startAt")}");
        var result = new Dictionary<string, string>
        {
            ["action"] = Action,
            ["outcome"] = leftover == 0 && failed == 0 ? "ok" : "partial",
            ["owners"] = owners.Count.ToString(CultureInfo.InvariantCulture),
            ["users"] = users.ToString(CultureInfo.InvariantCulture),
            ["rows"] = rows.ToString(CultureInfo.InvariantCulture),
            ["skipped"] = skipped.ToString(CultureInfo.InvariantCulture),
            ["leftover"] = leftover.ToString(CultureInfo.InvariantCulture),
            ["failed"] = failed.ToString(CultureInfo.InvariantCulture)
        };
        if (next != null)
        {
            result["next"] = next;
        }

        return result;
    }

    /// <summary>The distinct owners of the active crop and trip rows, in ordinal user id order.</summary>
    private async Task<SortedSet<string>> ActiveOwnersAsync()
    {
        var owners = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var status in ReputationCopyRepository.ActiveCropStatuses)
        {
            await CollectOwnersAsync(_cropsTable, CropStatusIndex, status, "farmerId", owners);
        }

        foreach (var status in ReputationCopyRepository.ActiveTripStatuses)
        {
            await CollectOwnersAsync(_tripsTable, TripStatusIndex, status, "truckerId", owners);
        }

        return owners;
    }

    private async Task CollectOwnersAsync(string table, string index, string status, string ownerAttribute, SortedSet<string> owners)
    {
        var request = new QueryRequest
        {
            TableName = table,
            IndexName = index,
            KeyConditionExpression = "#status = :status",
            ProjectionExpression = "#owner",
            ExpressionAttributeNames = new Dictionary<string, string> { ["#status"] = "status", ["#owner"] = ownerAttribute },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue> { [":status"] = new() { S = status } }
        };

        Dictionary<string, AttributeValue>? startKey = null;
        do
        {
            request.ExclusiveStartKey = startKey;
            var page = await _dynamoDb.QueryAsync(request);
            foreach (var item in page.Items)
            {
                if (item.TryGetValue(ownerAttribute, out var owner) && !string.IsNullOrEmpty(owner.S))
                {
                    owners.Add(owner.S);
                }
            }

            startKey = page.LastEvaluatedKey is { Count: > 0 } next ? next : null;
        }
        while (startKey != null);
    }
}
