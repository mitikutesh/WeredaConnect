using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda;
using Amazon.Lambda.Model;
using WeredaConnect.Server.Functions;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Models;

namespace WeredaConnect.Server.Services;

/// <summary>
/// FRD-19: the AI reviewer, as the advisory cron sees it. The production implementation invokes the separate
/// non-AOT reviewer Lambda (<c>server/fraud-ai-reviewer</c>), the only code that talks to Bedrock.
/// </summary>
internal interface IAdvisoryReviewer
{
    /// <summary>The reviewer's answer; throws when the invocation itself failed.</summary>
    Task<AdvisoryReviewReply?> ReviewAsync(AdvisoryReviewRequest request);
}

/// <summary>
/// FRD-19: invokes the reviewer Lambda synchronously (<c>RequestResponse</c>) with the redacted timeline and reads
/// its JSON answer. No SDK retry: a retried invocation would call Bedrock, and bill, twice; the next hourly run
/// retries a user whose signals are still in the lookback.
/// </summary>
internal sealed class LambdaAdvisoryReviewer : IAdvisoryReviewer
{
    private readonly IAmazonLambda _lambda;
    private readonly string _functionName;

    public LambdaAdvisoryReviewer(IAmazonLambda lambda, string functionName)
    {
        _lambda = lambda ?? throw new ArgumentNullException(nameof(lambda));
        _functionName = functionName ?? throw new ArgumentNullException(nameof(functionName));
    }

    /// <summary>The client the cron builds: 40 seconds per call (the reviewer's own timeout is 30), no retry.</summary>
    public static IAmazonLambda CreateClient() => new AmazonLambdaClient(ClientConfig());

    /// <summary>The cron client's settings (the region comes from the Lambda environment).</summary>
    internal static AmazonLambdaConfig ClientConfig() => new()
    {
        Timeout = TimeSpan.FromSeconds(40),
        MaxErrorRetry = 0
    };

    public async Task<AdvisoryReviewReply?> ReviewAsync(AdvisoryReviewRequest request)
    {
        var response = await _lambda.InvokeAsync(new InvokeRequest
        {
            FunctionName = _functionName,
            InvocationType = InvocationType.RequestResponse,
            Payload = JsonSerializer.Serialize(request, AppJsonContext.Default.AdvisoryReviewRequest)
        });

        if (!string.IsNullOrEmpty(response.FunctionError))
        {
            throw new AdvisoryReviewerException($"function_error:{FraudAdvisory.Token(response.FunctionError)}");
        }

        if (response.Payload == null || response.Payload.Length == 0)
        {
            return null;
        }

        response.Payload.Position = 0;
        return await JsonSerializer.DeserializeAsync(response.Payload, AppJsonContext.Default.AdvisoryReviewReply);
    }
}

/// <summary>FRD-19: the reviewer Lambda answered with a function error (it threw, timed out or ran out of memory).</summary>
internal sealed class AdvisoryReviewerException : Exception
{
    public AdvisoryReviewerException(string reason) : base(reason)
    {
        Reason = reason;
    }

    public string Reason { get; }
}

/// <summary>FRD-19: what one advisory run did (the run summary and its log line).</summary>
internal sealed class FraudAdvisorySummary
{
    /// <summary>Distinct users with a new pending signal in the lookback (advisory rows left out).</summary>
    public int Candidates { get; set; }

    /// <summary>Candidates whose score was read.</summary>
    public int Scored { get; set; }

    /// <summary>Scored users the rules alone keep below HIGH: not reviewed.</summary>
    public int BelowHigh { get; set; }

    /// <summary>Users whose score could not be read: not reviewed (never treated as LOW or as HIGH).</summary>
    public int Unscored { get; set; }

    /// <summary>Users who already have today's advisory row.</summary>
    public int AlreadyReviewed { get; set; }

    /// <summary>Reviewer invocations.</summary>
    public int Reviewed { get; set; }

    /// <summary>Advisory rows written.</summary>
    public int Written { get; set; }

    /// <summary>Reviews that failed (read, invocation, answer or write); nothing was written for them.</summary>
    public int Failed { get; set; }

    /// <summary>The per-run review cap stopped the run with candidates left.</summary>
    public bool Capped { get; set; }

    /// <summary>More candidate rows than the index pages read: the rest wait for the next run.</summary>
    public bool CandidatesTruncated { get; set; }

    /// <summary>Why the run stopped before the end: <c>time</c> or <c>lambda</c> (null when it finished).</summary>
    public string? StopReason { get; set; }

    public Dictionary<string, string> ToDictionary(string outcome) => new()
    {
        { "outcome", outcome },
        { "candidates", Text(Candidates) },
        { "scored", Text(Scored) },
        { "belowHigh", Text(BelowHigh) },
        { "unscored", Text(Unscored) },
        { "alreadyReviewed", Text(AlreadyReviewed) },
        { "reviewed", Text(Reviewed) },
        { "written", Text(Written) },
        { "failed", Text(Failed) },
        { "capped", Capped ? "true" : "false" },
        { "candidatesTruncated", CandidatesTruncated ? "true" : "false" },
        { "stopReason", StopReason ?? "-" }
    };

    public string LogLine(string outcome) =>
        $"{FraudAdvisory.RunToken} outcome={outcome} candidates={Candidates} scored={Scored} belowHigh={BelowHigh} "
        + $"unscored={Unscored} alreadyReviewed={AlreadyReviewed} reviewed={Reviewed} written={Written} failed={Failed} "
        + $"capped={(Capped ? "true" : "false")} stopReason={StopReason ?? "-"}";

    private static string Text(int value) => value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// FRD-19 (fraud-detection-architecture.md section 21): the hourly AI advisory run. It never moves money, holds,
/// releases, refunds or blocks anything: its only write is one <c>AI_ADVISORY</c> signal row per reviewed user and
/// day, whose weight the score caps at <c>RISK_ADVISORY_CAP</c> and leaves out of CRITICAL (section 14.2).
/// <list type="number">
/// <item>Candidates: the users with a pending signal created in the last <c>FRAUD_AI_REVIEW_LOOKBACK_HOURS</c>, read
/// on the fraud-signals <c>StatusCreatedAtIndex</c> (key-bounded, newest first, at most 3 pages; no Scan). A user's
/// tier only rises when a signal row is written, so this is every user whose tier may have just reached HIGH.</item>
/// <item>Each candidate's section 14 score; only users the rules alone put at HIGH or CRITICAL go on
/// (<see cref="FraudAdvisory.RulesPutAtHighOrAbove"/>).</item>
/// <item>One review per user and UTC day: a consistent <c>GetItem</c> of today's advisory key skips a user already
/// reviewed, and the put is conditional.</item>
/// <item>The redacted timeline (<see cref="FraudAdvisory.BuildTimeline"/>): one page of the user's signal rows, the
/// users row's role, creation time and Fayda flag (projected; nothing else of the row is read), and at most
/// 20 transaction audit events of each of 8 entities (projected: no IP address, correlation id or metadata).</item>
/// <item>The reviewer Lambda's answer, checked (<see cref="FraudAdvisory.TryReadVerdict"/>), then the row.</item>
/// </list>
/// At most <c>FRAUD_AI_REVIEW_MAX_PER_RUN</c> reviews a run. A failure of one user is counted, logged and skipped;
/// only a failed candidate Query fails the run.
/// </summary>
internal sealed class FraudAdvisoryService
{
    internal const string StatusIndex = "StatusCreatedAtIndex";
    internal const string AuditEntityIndex = "EntityIndex";
    internal const int CandidatePageSize = 100;
    internal const int MaxCandidatePages = 3;
    internal const int SignalPageSize = 100;

    private readonly IAmazonDynamoDB _dynamoDb;
    private readonly string _fraudSignalsTable;
    private readonly string? _usersTable;
    private readonly string? _auditTable;
    private readonly IFraudDetectionService _fraud;
    private readonly IAdvisoryReviewer? _reviewer;
    private readonly FraudAdvisorySettings? _settings;
    private readonly RiskScoreSettings? _riskSettings;

    /// <param name="settings">Fixed settings for tests; null reads the environment once per run.</param>
    /// <param name="riskSettings">Fixed score settings for tests; null reads the environment once per run.</param>
    public FraudAdvisoryService(
        IAmazonDynamoDB dynamoDb,
        string fraudSignalsTable,
        string? usersTable,
        string? auditTable,
        IFraudDetectionService fraud,
        IAdvisoryReviewer? reviewer,
        FraudAdvisorySettings? settings = null,
        RiskScoreSettings? riskSettings = null)
    {
        _dynamoDb = dynamoDb ?? throw new ArgumentNullException(nameof(dynamoDb));
        _fraudSignalsTable = fraudSignalsTable ?? throw new ArgumentNullException(nameof(fraudSignalsTable));
        _usersTable = usersTable;
        _auditTable = auditTable;
        _fraud = fraud ?? throw new ArgumentNullException(nameof(fraud));
        _reviewer = reviewer;
        _settings = settings;
        _riskSettings = riskSettings;
    }

    /// <summary>Where the log lines go (stdout in the Lambda, a list in tests).</summary>
    public Action<string> Log { get; init; } = Console.WriteLine;

    /// <summary>
    /// One run. With the kill switch off it logs <c>FRAUD_AI_REVIEW_OFF</c> and reads nothing; without a reviewer
    /// it logs <c>FRAUD_AI_REVIEW_UNCONFIGURED</c> and reads nothing.
    /// </summary>
    public async Task<(string Outcome, FraudAdvisorySummary Summary)> RunAsync(
        DateTime nowUtc, CronRunSlice slice, ILambdaContext? context)
    {
        var settings = _settings ?? FraudAdvisorySettings.FromEnvironment();
        var summary = new FraudAdvisorySummary();
        if (!settings.Enabled)
        {
            Log($"{FraudAdvisory.OffToken} the kill switch FRAUD_AI_REVIEW_ENABLED is off; nothing read, nothing sent.");
            return ("off", summary);
        }

        if (_reviewer == null || string.IsNullOrEmpty(settings.ReviewerFunction))
        {
            Log($"{FraudAdvisory.UnconfiguredToken} no reviewer function (FRAUD_AI_REVIEWER_FUNCTION); nothing sent.");
            return ("unconfigured", summary);
        }

        var riskSettings = _riskSettings ?? RiskScoreSettings.FromEnvironment();
        var candidates = await ReadCandidatesAsync(nowUtc, settings, summary);
        summary.Candidates = candidates.Count;

        foreach (var userId in candidates)
        {
            if (summary.Reviewed >= settings.MaxReviewsPerRun)
            {
                summary.Capped = true;
                break;
            }

            if (!slice.MayStartRow(context))
            {
                summary.StopReason = slice.StopReason;
                break;
            }

            await ReviewUserAsync(userId, nowUtc, riskSettings, summary);
        }

        var outcome = summary.Failed > 0 ? "partial" : "ok";
        Log(summary.LogLine(outcome));
        return (outcome, summary);
    }

    private async Task ReviewUserAsync(
        string userId, DateTime nowUtc, RiskScoreSettings riskSettings, FraudAdvisorySummary summary)
    {
        RiskScoreResult score;
        try
        {
            score = await _fraud.GetUserRiskScoreAsync(userId);
        }
        catch (Exception ex)
        {
            summary.Unscored++;
            LogFailed(userId, $"score:{ex.GetType().Name}");
            return;
        }

        if (score.Source != RiskScoreSource.Computed)
        {
            summary.Unscored++;
            return;
        }

        summary.Scored++;
        if (!FraudAdvisory.RulesPutAtHighOrAbove(score, riskSettings))
        {
            summary.BelowHigh++;
            return;
        }

        var dayKey = FraudAdvisory.DayKey(nowUtc);
        AdvisoryReviewRequest request;
        try
        {
            if (await HasAdvisoryAsync(userId, dayKey))
            {
                summary.AlreadyReviewed++;
                return;
            }

            request = await BuildRequestAsync(userId, score, riskSettings, nowUtc);
        }
        catch (Exception ex)
        {
            summary.Failed++;
            LogFailed(userId, $"read:{ex.GetType().Name}");
            return;
        }

        AdvisoryReviewReply? reply;
        summary.Reviewed++;
        try
        {
            reply = await _reviewer!.ReviewAsync(request);
        }
        catch (AdvisoryReviewerException ex)
        {
            summary.Failed++;
            LogFailed(userId, ex.Reason);
            return;
        }
        catch (Exception ex)
        {
            summary.Failed++;
            LogFailed(userId, $"invoke:{ex.GetType().Name}");
            return;
        }

        if (!FraudAdvisory.TryReadVerdict(reply, out var concern, out var explanation, out var reason))
        {
            summary.Failed++;
            LogFailed(userId, reason);
            return;
        }

        try
        {
            var weight = FraudAdvisory.WeightFor(concern, riskSettings.AdvisoryCap);
            if (await WriteAdvisoryAsync(userId, dayKey, weight, concern, explanation, reply!.Model, nowUtc))
            {
                summary.Written++;
                Log($"{FraudAdvisory.WrittenToken} userId={MoneyAlarmLog.LogId(userId)} concern={concern} weight={weight} "
                    + $"model={FraudAdvisory.Token(reply.Model)} inputTokens={reply.InputTokens ?? 0} outputTokens={reply.OutputTokens ?? 0}");
            }
            else
            {
                summary.AlreadyReviewed++;
            }
        }
        catch (Exception ex)
        {
            summary.Failed++;
            LogFailed(userId, $"write:{ex.GetType().Name}");
        }
    }

    private void LogFailed(string userId, string reason) =>
        Log($"{FraudAdvisory.FailedToken} userId={MoneyAlarmLog.LogId(userId)} reason={FraudAdvisory.Token(reason, 96)}");

    /// <summary>
    /// The distinct users of the pending signals created since the lookback, newest first, at most
    /// <see cref="FraudAdvisorySettings.MaxCandidates"/>. Advisory rows are left out, so a review never makes its own
    /// user a candidate again.
    /// </summary>
    private async Task<List<string>> ReadCandidatesAsync(
        DateTime nowUtc, FraudAdvisorySettings settings, FraudAdvisorySummary summary)
    {
        var since = nowUtc.AddHours(-settings.LookbackHours).ToString("o", CultureInfo.InvariantCulture);
        var users = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        Dictionary<string, AttributeValue>? startKey = null;
        var pages = 0;
        do
        {
            var response = await _dynamoDb.QueryAsync(new QueryRequest
            {
                TableName = _fraudSignalsTable,
                IndexName = StatusIndex,
                KeyConditionExpression = "#s = :pending AND #c >= :since",
                ProjectionExpression = "#u, #t",
                ExpressionAttributeNames = new Dictionary<string, string>
                {
                    { "#s", "status" },
                    { "#c", "createdAt" },
                    { "#u", "userId" },
                    { "#t", "signalType" }
                },
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    { ":pending", new AttributeValue { S = "pending" } },
                    { ":since", new AttributeValue { S = since } }
                },
                ScanIndexForward = false,
                Limit = CandidatePageSize,
                ExclusiveStartKey = startKey
            });
            pages++;
            foreach (var row in response.Items ?? new List<Dictionary<string, AttributeValue>>())
            {
                var userId = row.TryGetValue("userId", out var u) ? u.S : null;
                var type = row.TryGetValue("signalType", out var t) ? t.S : null;
                if (string.IsNullOrEmpty(userId) || type == FraudDetectionService.AdvisorySignalType || !seen.Add(userId))
                {
                    continue;
                }

                users.Add(userId);
                if (users.Count == settings.MaxCandidates)
                {
                    summary.CandidatesTruncated = true;
                    return users;
                }
            }

            startKey = response.LastEvaluatedKey is { Count: > 0 } next ? next : null;
        }
        while (startKey != null && pages < MaxCandidatePages);

        summary.CandidatesTruncated = startKey != null;
        return users;
    }

    private async Task<bool> HasAdvisoryAsync(string userId, string dayKey)
    {
        var response = await _dynamoDb.GetItemAsync(new GetItemRequest
        {
            TableName = _fraudSignalsTable,
            Key = new Dictionary<string, AttributeValue>
            {
                { "userId", new AttributeValue { S = userId } },
                { "signalKey", new AttributeValue { S = dayKey } }
            },
            ProjectionExpression = "#k",
            ExpressionAttributeNames = new Dictionary<string, string> { { "#k", "signalKey" } },
            ConsistentRead = true
        });
        return response.Item is { Count: > 0 };
    }

    private async Task<AdvisoryReviewRequest> BuildRequestAsync(
        string userId, RiskScoreResult score, RiskScoreSettings riskSettings, DateTime nowUtc)
    {
        var (signalRows, signalsTruncated) = await ReadSignalRowsAsync(userId, riskSettings, nowUtc);
        var userRow = await ReadUserAsync(userId);

        var events = new List<(string, string, IReadOnlyList<Dictionary<string, AttributeValue>>)>();
        var eventsTruncated = false;
        foreach (var (entityType, entityId) in FraudAdvisory.EventEntities(signalRows))
        {
            var (rows, more) = await ReadAuditEventsAsync(entityType, entityId);
            events.Add((entityType, entityId, rows));
            eventsTruncated |= more;
        }

        return FraudAdvisory.BuildTimeline(
            userId, userRow, signalRows, signalsTruncated, events, eventsTruncated, score, riskSettings, nowUtc);
    }

    /// <summary>One page of the user's rows in the scoring window, newest first, only the attributes the timeline keeps.</summary>
    private async Task<(List<Dictionary<string, AttributeValue>> Rows, bool Truncated)> ReadSignalRowsAsync(
        string userId, RiskScoreSettings riskSettings, DateTime nowUtc)
    {
        var since = nowUtc.AddDays(-riskSettings.WindowDays).ToString("o", CultureInfo.InvariantCulture);
        var response = await _dynamoDb.QueryAsync(new QueryRequest
        {
            TableName = _fraudSignalsTable,
            KeyConditionExpression = "#u = :uid AND #k >= :since",
            ProjectionExpression = "#k, #t, #w, #s, #r, #et, #e, #rt, #c",
            ExpressionAttributeNames = new Dictionary<string, string>
            {
                { "#u", "userId" },
                { "#k", "signalKey" },
                { "#t", "signalType" },
                { "#w", "weight" },
                { "#s", "status" },
                { "#r", "resolution" },
                { "#et", "entityType" },
                { "#e", "entityId" },
                { "#rt", "recordType" },
                { "#c", "createdAt" }
            },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                { ":uid", new AttributeValue { S = userId } },
                { ":since", new AttributeValue { S = since } }
            },
            ScanIndexForward = false,
            Limit = SignalPageSize
        });
        return (response.Items ?? new List<Dictionary<string, AttributeValue>>(),
            response.LastEvaluatedKey is { Count: > 0 });
    }

    /// <summary>The users row's role, creation time and Fayda flag; nothing else of it (no phone, name or Fayda id).</summary>
    private async Task<Dictionary<string, AttributeValue>?> ReadUserAsync(string userId)
    {
        if (string.IsNullOrEmpty(_usersTable))
        {
            return null;
        }

        var response = await _dynamoDb.GetItemAsync(new GetItemRequest
        {
            TableName = _usersTable,
            Key = new Dictionary<string, AttributeValue> { { "userId", new AttributeValue { S = userId } } },
            ProjectionExpression = "#r, #c, #f",
            ExpressionAttributeNames = new Dictionary<string, string>
            {
                { "#r", "role" },
                { "#c", "createdAt" },
                { "#f", "faydaVerified" }
            }
        });
        return response.Item is { Count: > 0 } item ? item : null;
    }

    /// <summary>
    /// The newest transaction audit events of one entity, oldest first: an order by its transaction (every leg of
    /// the trade), a delivery or an escrow on <c>EntityIndex</c>. One page, projected to the timeline's fields.
    /// </summary>
    private async Task<(IReadOnlyList<Dictionary<string, AttributeValue>> Rows, bool Truncated)> ReadAuditEventsAsync(
        string entityType, string entityId)
    {
        if (string.IsNullOrEmpty(_auditTable))
        {
            return (Array.Empty<Dictionary<string, AttributeValue>>(), false);
        }

        var byOrder = entityType == "order";
        var response = await _dynamoDb.QueryAsync(new QueryRequest
        {
            TableName = _auditTable,
            IndexName = byOrder ? null : AuditEntityIndex,
            KeyConditionExpression = "#id = :id",
            ProjectionExpression = "#ts, #a, #f, #to, #ac, #ar, #et",
            ExpressionAttributeNames = new Dictionary<string, string>
            {
                { "#id", byOrder ? "transactionId" : "entityId" },
                { "#ts", "timestamp" },
                { "#a", "action" },
                { "#f", "fromState" },
                { "#to", "toState" },
                { "#ac", "actor" },
                { "#ar", "actorRole" },
                { "#et", "entityType" }
            },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                { ":id", new AttributeValue { S = entityId } }
            },
            ScanIndexForward = false,
            Limit = FraudAdvisory.MaxEventsPerEntity
        });
        var rows = (response.Items ?? new List<Dictionary<string, AttributeValue>>()).AsEnumerable().Reverse().ToList();
        return (rows, response.LastEvaluatedKey is { Count: > 0 });
    }

    /// <summary>
    /// The advisory row: a signal row (section 4.3 schema, <c>recordType</c> <c>signal</c>, <c>status</c>
    /// <c>pending</c>, so Fraud Review lists it and an admin can approve or reject it) of type <c>AI_ADVISORY</c>
    /// with the capped weight, the explanation as its description, and <c>aiConcern</c> and <c>aiModel</c>. The put
    /// is conditional on today's key; false when the row was already there.
    /// </summary>
    private async Task<bool> WriteAdvisoryAsync(
        string userId, string dayKey, int weight, string concern, string explanation, string? model, DateTime nowUtc)
    {
        var item = new Dictionary<string, AttributeValue>
        {
            { "userId", new AttributeValue { S = userId } },
            { "signalKey", new AttributeValue { S = dayKey } },
            { "signalId", new AttributeValue { S = FraudDetectionService.BuildSignalId(userId, dayKey) } },
            { "signalType", new AttributeValue { S = FraudDetectionService.AdvisorySignalType } },
            { "recordType", new AttributeValue { S = FraudDetectionService.RecordTypeSignal } },
            { "severity", new AttributeValue { S = FraudDetectionService.SignalSeverity(weight) } },
            { "weight", new AttributeValue { N = weight.ToString(CultureInfo.InvariantCulture) } },
            { "description", new AttributeValue { S = explanation } },
            { "status", new AttributeValue { S = "pending" } },
            { "entityType", new AttributeValue { S = "user" } },
            { "aiConcern", new AttributeValue { S = concern } },
            { "createdAt", new AttributeValue { S = nowUtc.ToString("o", CultureInfo.InvariantCulture) } },
            {
                "expiresAt", new AttributeValue
                {
                    N = new DateTimeOffset(nowUtc).AddDays(FraudDetectionService.SignalTtlDays).ToUnixTimeSeconds()
                        .ToString(CultureInfo.InvariantCulture)
                }
            }
        };
        if (FraudAdvisory.ModelLabel(model) is { } label)
        {
            item["aiModel"] = new AttributeValue { S = label };
        }

        try
        {
            await _dynamoDb.PutItemAsync(new PutItemRequest
            {
                TableName = _fraudSignalsTable,
                Item = item,
                ConditionExpression = "attribute_not_exists(signalKey)"
            });
        }
        catch (ConditionalCheckFailedException)
        {
            return false;
        }

        // FRD-16: the advisory row is a row Fraud Review shows, so it is counted like every signal row.
        Log(FraudDetectionService.SignalRecordedLine(FraudDetectionService.AdvisorySignalType, "user"));
        return true;
    }
}
