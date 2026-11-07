using WeredaConnect.Server.Data;
using WeredaConnect.Server.Functions;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Models;

namespace WeredaConnect.Server.Services;

/// <summary>FRD-15: what one relationship scan did (the order-cron run summary).</summary>
internal sealed class RelationshipScanSummary
{
    /// <summary>Order rows the status Queries returned (funded or not).</summary>
    public int OrdersRead { get; set; }

    /// <summary>Distinct funded orders kept for the checks (at most <c>FRAUD_RELATIONSHIP_MAX_ORDERS</c>).</summary>
    public int Funded { get; set; }

    /// <summary>Funded orders checked before the run ended.</summary>
    public int Examined { get; set; }

    /// <summary>Signals to write, one per hit and party.</summary>
    public int Hits { get; set; }

    /// <summary>Signals handed to the writer (its hour key may still drop one an earlier run of this hour wrote).</summary>
    public int Written { get; set; }

    /// <summary>Signals already on the user's partition from an earlier run (any review status).</summary>
    public int AlreadyRecorded { get; set; }

    /// <summary>Signals of a user and type the writer already took this hour: left to the next run.</summary>
    public int Deferred { get; set; }

    /// <summary>Orders whose reads failed plus signals whose existence check or write failed.</summary>
    public int Failed { get; set; }

    /// <summary>The funded-order cap was reached: orders past it were not read this run.</summary>
    public bool Truncated { get; set; }

    /// <summary>Why the run stopped before the end: <c>time</c> or <c>lambda</c> (null when it finished).</summary>
    public string? StopReason { get; set; }

    public Dictionary<string, string> ToDictionary(string outcome) => new()
    {
        { "outcome", outcome },
        { "ordersRead", Text(OrdersRead) },
        { "funded", Text(Funded) },
        { "examined", Text(Examined) },
        { "hits", Text(Hits) },
        { "written", Text(Written) },
        { "alreadyRecorded", Text(AlreadyRecorded) },
        { "deferred", Text(Deferred) },
        { "failed", Text(Failed) },
        { "truncated", Truncated ? "true" : "false" },
        { "stopReason", StopReason ?? "-" }
    };

    private static string Text(int value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>
/// FRD-15 (fraud-detection-architecture.md section 15.2): the hourly relationship scan. Reads the orders
/// created in the last <c>FRAUD_RELATIONSHIP_LOOKBACK_HOURS</c> on <c>StatusCreatedAtIndex</c> (one key-bounded
/// Query per status that can hold a verified payment, newest first), keeps the funded ones up to
/// <c>FRAUD_RELATIONSHIP_MAX_ORDERS</c>, and checks each: the buyer's and the farmer's users rows (one GetItem
/// each per run) for one identity or a shared install, and the buyer's orders on <c>BuyerIdCreatedAtIndex</c>
/// (one read per buyer per run) for a repeated pair. No Scan, no new index, no graph store.
/// <para>
/// Each hit is written on both parties through <see cref="IFraudDetectionService.RecordFraudSignalAsync"/>,
/// with the order as the entity. Idempotent across runs: an order stays in the lookback for many hourly runs,
/// so before a write <see cref="IFraudDetectionService.HasSignalAsync"/> looks for a row of the same type on
/// the same order (an approved one included), and a rerun in the same hour meets the writer's hour key. The
/// writer keeps one row per user, type and hour, so a second hit of the same user and type in one run is
/// deferred: the next run, which still sees the order, writes it.
/// </para>
/// </summary>
internal sealed class RelationshipPatternService
{
    internal const string ScanToken = "FRAUD_RELATIONSHIP_SCAN";
    internal const string TruncatedToken = "FRAUD_RELATIONSHIP_SCAN_TRUNCATED";
    internal const string CheckFailedToken = "FRAUD_RELATIONSHIP_CHECK_FAILED";
    internal const string SignalFailedToken = "FRAUD_RELATIONSHIP_SIGNAL_FAILED";

    private readonly IOrderRepository _orders;
    private readonly IUserRepository _users;
    private readonly IFraudDetectionService _fraud;
    private readonly RelationshipPatternSettings? _settings;

    /// <param name="settings">Fixed settings for tests; null reads the environment once per run.</param>
    public RelationshipPatternService(
        IOrderRepository orders, IUserRepository users, IFraudDetectionService fraud,
        RelationshipPatternSettings? settings = null)
    {
        _orders = orders ?? throw new ArgumentNullException(nameof(orders));
        _users = users ?? throw new ArgumentNullException(nameof(users));
        _fraud = fraud ?? throw new ArgumentNullException(nameof(fraud));
        _settings = settings;
    }

    /// <summary>
    /// One scan. Stops early when <paramref name="slice"/> says so (before each page and each order). A failed
    /// status Query throws (the caller answers 500); a failed read of one order's users or history, or a failed
    /// write, is counted, logged and skipped.
    /// </summary>
    public async Task<RelationshipScanSummary> RunAsync(DateTime nowUtc, CronRunSlice slice, ILambdaContext context)
    {
        var settings = _settings ?? RelationshipPatternSettings.FromEnvironment();
        var run = new Run(nowUtc, settings, slice, context);

        var funded = await ReadFundedOrdersAsync(run);
        foreach (var order in funded)
        {
            if (!run.MayContinue())
            {
                break;
            }

            run.Summary.Examined++;
            await CheckOrderAsync(order, run);
        }

        if (run.Summary.Truncated)
        {
            context.Logger.LogWarning(
                $"{TruncatedToken} funded={run.Summary.Funded} cap={settings.MaxOrders} lookbackHours={settings.LookbackHours}");
        }

        var s = run.Summary;
        context.Logger.LogInformation(
            $"{ScanToken} ordersRead={s.OrdersRead} funded={s.Funded} examined={s.Examined} hits={s.Hits} "
            + $"written={s.Written} alreadyRecorded={s.AlreadyRecorded} deferred={s.Deferred} failed={s.Failed} "
            + $"truncated={(s.Truncated ? "true" : "false")} stop={s.StopReason ?? "-"}");
        return s;
    }

    /// <summary>
    /// The funded orders created in the lookback, newest first within each status, each order once (an order
    /// that changed status between two Queries can appear in both partitions).
    /// </summary>
    private async Task<List<Dictionary<string, object>>> ReadFundedOrdersAsync(Run run)
    {
        var since = run.Now.AddHours(-run.Settings.LookbackHours).ToString("o", System.Globalization.CultureInfo.InvariantCulture);
        var funded = new List<Dictionary<string, object>>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var status in RelationshipPatternRules.ScannedOrderStatuses)
        {
            Dictionary<string, Amazon.DynamoDBv2.Model.AttributeValue>? start = null;
            do
            {
                if (!run.MayContinue())
                {
                    return funded;
                }

                var (items, next) = await _orders.QueryByStatusCreatedSinceAsync(
                    status, since, RelationshipPatternSettings.StatusPageSize, start);
                run.Summary.OrdersRead += items.Count;
                foreach (var order in items)
                {
                    var orderId = RelationshipPatternRules.Str(order, "orderId");
                    if (orderId.Length == 0 || !RelationshipPatternRules.IsFunded(order) || !seen.Add(orderId))
                    {
                        continue;
                    }

                    if (funded.Count >= run.Settings.MaxOrders)
                    {
                        run.Summary.Truncated = true;
                        return funded;
                    }

                    funded.Add(order);
                    run.Summary.Funded = funded.Count;
                }

                start = next;
            }
            while (start != null);
        }

        return funded;
    }

    private async Task CheckOrderAsync(Dictionary<string, object> order, Run run)
    {
        var orderId = RelationshipPatternRules.Str(order, "orderId");
        var buyerId = RelationshipPatternRules.Str(order, "buyerId");
        var farmerId = RelationshipPatternRules.Str(order, "farmerId");
        if (buyerId.Length == 0 || farmerId.Length == 0)
        {
            return;
        }

        IReadOnlyList<RelationshipHit> hits;
        try
        {
            var buyer = await UserAsync(buyerId, run);
            var farmer = buyerId == farmerId ? buyer : await UserAsync(farmerId, run);
            var history = await BuyerHistoryAsync(buyerId, run);
            var pairCount = RelationshipPatternRules.PairOrderCount(history, order, farmerId, run.Settings);
            hits = RelationshipPatternRules.Evaluate(orderId, buyerId, farmerId, buyer, farmer, pairCount, run.Settings,
                RelationshipPatternRules.FarmerRefused(order));
        }
        catch (Exception ex)
        {
            run.Summary.Failed++;
            run.Context.Logger.LogWarning(
                $"{CheckFailedToken} orderId={MoneyAlarmLog.LogId(orderId)} error={MoneyAlarmLog.LogId(ex.GetType().Name)}");
            return;
        }

        foreach (var hit in hits)
        {
            foreach (var userId in hit.UserIds)
            {
                run.Summary.Hits++;
                await WriteAsync(hit, userId, run);
            }
        }
    }

    private async Task WriteAsync(RelationshipHit hit, string userId, Run run)
    {
        if (run.WrittenThisRun.Contains((userId, hit.SignalType)))
        {
            // The writer keeps one row per user, type and hour: this one would be dropped. The order stays in
            // the lookback, so the next run writes it.
            run.Summary.Deferred++;
            return;
        }

        try
        {
            if (await _fraud.HasSignalAsync(userId, hit.SignalType, hit.OrderId))
            {
                run.Summary.AlreadyRecorded++;
                return;
            }

            await _fraud.RecordFraudSignalAsync(userId, hit.SignalType, hit.OrderId, "order", hit.Description);
            run.WrittenThisRun.Add((userId, hit.SignalType));
            run.Summary.Written++;
        }
        catch (Exception ex)
        {
            run.Summary.Failed++;
            run.Context.Logger.LogWarning(
                $"{SignalFailedToken} type={hit.SignalType} orderId={MoneyAlarmLog.LogId(hit.OrderId)} "
                + $"userId={MoneyAlarmLog.LogId(userId)} error={MoneyAlarmLog.LogId(ex.GetType().Name)}");
        }
    }

    /// <summary>The users row, read once per run (a missing row is remembered as null; a failed read is not).</summary>
    private async Task<UserDto?> UserAsync(string userId, Run run)
    {
        if (run.Users.TryGetValue(userId, out var known))
        {
            return known;
        }

        var user = await _users.GetUserByIdAsync(userId);
        run.Users[userId] = user;
        return user;
    }

    /// <summary>
    /// The buyer's orders created in the pair window before the oldest order the lookback can hold (each pair
    /// count ends at its examined order), newest first on <c>BuyerIdCreatedAtIndex</c>, at most
    /// <see cref="RelationshipPatternSettings.HistoryMaxPages"/> pages; read once per buyer per run.
    /// </summary>
    private async Task<IReadOnlyList<Dictionary<string, object>>> BuyerHistoryAsync(string buyerId, Run run)
    {
        if (run.Histories.TryGetValue(buyerId, out var known))
        {
            return known;
        }

        var since = run.Now.AddHours(-run.Settings.LookbackHours).AddDays(-run.Settings.PairWindowDays);
        var rows = new List<Dictionary<string, object>>();
        Dictionary<string, Amazon.DynamoDBv2.Model.AttributeValue>? start = null;
        for (var page = 0; page < RelationshipPatternSettings.HistoryMaxPages; page++)
        {
            // QueryOrdersAsync falls back to a table Scan when every id it gets is empty: buyerId is not.
            var (items, next) = await _orders.QueryOrdersAsync(
                buyerId: buyerId, limit: RelationshipPatternSettings.HistoryPageSize, exclusiveStartKey: start);
            var older = false;
            foreach (var item in items)
            {
                if (TransactionPatternRules.ParseUtc(RelationshipPatternRules.Str(item, "createdAt")) is { } createdAt
                    && createdAt < since)
                {
                    older = true;
                    break;
                }

                rows.Add(item);
            }

            if (older || next == null)
            {
                break;
            }

            start = next;
        }

        run.Histories[buyerId] = rows;
        return rows;
    }

    private sealed class Run(DateTime now, RelationshipPatternSettings settings, CronRunSlice slice, ILambdaContext context)
    {
        public DateTime Now { get; } = now;
        public RelationshipPatternSettings Settings { get; } = settings;
        public ILambdaContext Context { get; } = context;
        public RelationshipScanSummary Summary { get; } = new();
        public Dictionary<string, UserDto?> Users { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, IReadOnlyList<Dictionary<string, object>>> Histories { get; } = new(StringComparer.Ordinal);
        public HashSet<(string UserId, string SignalType)> WrittenThisRun { get; } = new();

        public bool MayContinue()
        {
            if (slice.MayStartRow(Context))
            {
                return true;
            }

            Summary.StopReason ??= slice.StopReason;
            return false;
        }
    }
}
