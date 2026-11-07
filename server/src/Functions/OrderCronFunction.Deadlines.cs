using Amazon.DynamoDBv2.Model;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Services;

namespace WeredaConnect.Server.Functions;

/// <summary>
/// ESC-16 (spec 4.5): the deadline sweep of <c>commitment_deadlines</c> and the daily
/// <c>deadline_repair</c> walk. Every order write keeps the order's next deadline on the row
/// (<see cref="OrderDeadlines"/>, written by <see cref="OrderRepository"/>), so the sweep reads only the
/// orders that are due, from the sparse <c>DeadlineIndex</c>, instead of walking the <c>funded</c>,
/// <c>accepted</c> and <c>awaiting_transport</c> partitions every run. The decisions are the ESC-15
/// ones, unchanged (<c>DecideFundedAsync</c> and the two after it).
/// </summary>
public partial class OrderCronFunction
{
    private const string RepairAction = "deadline_repair";

    /// <summary>ESC-16: rows per page of the deadline index (the Query's limit).</summary>
    internal const int DeadlinePageSize = 100;

    /// <summary>ESC-16: pages the sweep reads at most per run; the rest waits for the next run, 15 minutes later.</summary>
    internal const int DeadlineMaxPages = 10;

    /// <summary>ESC-16: the sweep's share of the 5 minute Lambda, so the steps after it still have time.</summary>
    internal static readonly TimeSpan DeadlineSweepTime = TimeSpan.FromMinutes(3);

    /// <summary>ESC-16: the repair walk's share of the Lambda.</summary>
    internal static readonly TimeSpan RepairWalkTime = TimeSpan.FromMinutes(4);

    /// <summary>
    /// ESC-16: when a row that must wait (an unreadable delivery, goods on the move) or that was due
    /// for something nothing could apply is looked at again. Its deadline moves this far ahead, so
    /// it never holds up the rows behind it.
    /// </summary>
    internal static readonly TimeSpan DeadlineRetryAfter = TimeSpan.FromHours(1);

    /// <summary>ESC-16: the statuses whose orders can carry a deadline of the index (the repair walk reads them).</summary>
    private static readonly string[] DeadlineStatuses = { "funded", OrderCommitment.AwaitingTransportStatus, "accepted" };

    /// <summary>A row due by its own state that the sweep could not act on (a rule mismatch to look into).</summary>
    internal const string DeadlineStuckToken = "ORDER_DEADLINE_STUCK";

    /// <summary>A row whose decision threw; the rest of the run went on and the row is tried again next run.</summary>
    internal const string DeadlineRowFailedToken = "ORDER_DEADLINE_ROW_FAILED";

    /// <summary>ESC-16: makes the run budget at the start of a sweep or walk (tests inject a hand-driven clock).</summary>
    internal Func<CronRunBudget> BudgetFactory { get; init; } = CronRunBudget.StartNew;

    /// <summary>
    /// ESC-16: the orders whose next deadline has passed, from <c>DeadlineIndex</c>, oldest first: at
    /// most <see cref="DeadlineMaxPages"/> pages of <see cref="DeadlinePageSize"/> within
    /// <see cref="DeadlineSweepTime"/>. Each is read again (strongly consistent; the index copy may lag)
    /// and decided on that row. A transition or a marker written by the decision also moves the
    /// deadline (the write stamps it); a lost race leaves it to the write that won; a row with nothing
    /// to apply gets its correct deadline back; a row that must wait comes back
    /// <see cref="DeadlineRetryAfter"/> later. No Scan.
    /// </summary>
    private async Task SweepDueDeadlinesAsync(
        CommitmentRun run, EscrowService escrow, ICropRepository crops, ITripRepository trips)
    {
        // The budget starts with the sweep, so its share counts from the start of the run.
        var slice = BudgetFactory().SliceUntil(DeadlineSweepTime);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        Dictionary<string, AttributeValue>? startKey = null;
        for (var page = 0; page < DeadlineMaxPages; page++)
        {
            var (rows, next) = await _orderRepo.QueryDueDeadlinesAsync(run.NowIso, DeadlinePageSize, startKey);
            foreach (var indexed in rows ?? new List<Dictionary<string, object>>())
            {
                var orderId = GetString(indexed, "orderId");
                if (orderId.Length == 0 || !seen.Add(orderId))
                {
                    continue;
                }

                if (!slice.MayStartRow(run.Context))
                {
                    run.StopReason = slice.StopReason;
                    return;
                }

                run.Due++;
                await DecideDueOrderAsync(orderId, run, escrow, crops, trips);
            }

            if (next == null)
            {
                return;
            }

            startKey = next;
        }

        run.StopReason = "pages";
    }

    /// <summary>One due order: read it again, decide it, then settle its deadline (see <see cref="SweepDueDeadlinesAsync"/>).</summary>
    private async Task DecideDueOrderAsync(
        string orderId, CommitmentRun run, EscrowService escrow, ICropRepository crops, ITripRepository trips)
    {
        try
        {
            var order = await _orderRepo.GetByIdAsync(orderId, consistentRead: true);
            if (order == null)
            {
                // Deleted: its index entry went with the row.
                return;
            }

            var outcome = GetString(order, "status") switch
            {
                "funded" => await DecideFundedAsync(order, orderId, run, escrow, crops, trips),
                "accepted" => await DecideAcceptedAsync(order, orderId, run, trips),
                OrderCommitment.AwaitingTransportStatus => await DecideAwaitingTransportAsync(order, orderId, run, escrow, crops),
                _ => DeadlineOutcome.None
            };

            switch (outcome)
            {
                case DeadlineOutcome.Applied:
                case DeadlineOutcome.Conflict:
                    // The write that changed the row (ours or the one that won the race) stamped its next deadline.
                    return;
                case DeadlineOutcome.Waiting:
                    await RetryLaterAsync(order, orderId, run);
                    return;
                default:
                    await CorrectDeadlineAsync(order, orderId, run);
                    return;
            }
        }
        catch (Exception ex)
        {
            // The row stays due and is tried again next run; it never stops the rows behind it.
            run.RowErrors++;
            run.Context.Logger.LogError(
                $"{DeadlineRowFailedToken} orderId={MoneyAlarmLog.LogId(orderId)} reason=[{ex.GetType().Name}]; the rest of the run goes on.");
        }
    }

    /// <summary>
    /// A due row with nothing to apply: the index copy was stale, or its deadline was a retry. It gets
    /// the deadline its state gives (or none). A row still due by its own state, which the decision
    /// could not act on, is logged <see cref="DeadlineStuckToken"/> and looked at again later instead
    /// of every run.
    /// </summary>
    private async Task CorrectDeadlineAsync(Dictionary<string, object> order, string orderId, CommitmentRun run)
    {
        var next = OrderDeadlines.Next(order);
        if (next is { } due && due.At < run.Now)
        {
            run.Context.Logger.LogWarning(
                $"{DeadlineStuckToken} orderId={MoneyAlarmLog.LogId(orderId)} kind={due.Kind} at={due.AtIso}: "
                + $"due but nothing applied; looked at again in {OrderTimings.HoursText(DeadlineRetryAfter)}h.");
            await SetDeadlineAsync(order, orderId, new OrderDeadline(run.Now + DeadlineRetryAfter, due.Kind), run, retry: true);
            return;
        }

        if (OrderDeadlines.IsStored(order, next))
        {
            return;
        }

        await SetDeadlineAsync(order, orderId, next, run, retry: false);
    }

    /// <summary>A row that must wait comes back <see cref="DeadlineRetryAfter"/> later; with nothing pending it leaves the index.</summary>
    private async Task RetryLaterAsync(Dictionary<string, object> order, string orderId, CommitmentRun run)
    {
        if (OrderDeadlines.Next(order) is not { } next)
        {
            await CorrectDeadlineAsync(order, orderId, run);
            return;
        }

        await SetDeadlineAsync(order, orderId, new OrderDeadline(run.Now + DeadlineRetryAfter, next.Kind), run, retry: true);
    }

    /// <summary>The deadline write of the sweep: no version bump, only while the row is still the version read.</summary>
    private async Task SetDeadlineAsync(
        Dictionary<string, object> order, string orderId, OrderDeadline? deadline, CommitmentRun run, bool retry)
    {
        if (await _orderRepo.TrySetDeadlineAsync(orderId, GetString(order, "createdAt"), GetInt(order, "version"), deadline))
        {
            if (retry)
            {
                run.Retried++;
            }
            else
            {
                run.Restamped++;
            }

            return;
        }

        // A write since the read moved the version, and stamped its own deadline.
        run.Conflicts++;
        run.Context.Logger.LogInformation(
            $"Commitment deadlines: order {MoneyAlarmLog.LogId(orderId)} changed before its deadline was corrected.");
    }

    /// <summary>
    /// ESC-16: <c>deadline_repair</c>, daily, and the backfill of the orders already in flight when the
    /// index was added (they carry no deadline until a write touches them). It reads the
    /// <c>funded</c>, <c>awaiting_transport</c> and <c>accepted</c> partitions of
    /// <c>StatusCreatedAtIndex</c> (Query, the walk the sweep replaced) within <see cref="RepairWalkTime"/>
    /// and stamps the next deadline on every order whose stored one is missing or not the one its
    /// state gives, conditioned on the version read. It decides nothing: the next sweep acts on what
    /// it stamped. Gated on <c>COMMITMENTS_ENABLED</c> like the sweep. Run it by hand once right after
    /// the deploy that adds the index, so the orders in flight are stamped at once.
    /// </summary>
    private async Task<APIGatewayProxyResponse> HandleDeadlineRepairAsync(ILambdaContext context)
    {
        var now = DateTime.UtcNow;
        int read = 0, stamped = 0, conflicts = 0;
        string? stopReason = null;

        Dictionary<string, string> Summary(string outcome) => new()
        {
            { "action", RepairAction },
            { "outcome", outcome },
            { "read", read.ToString() },
            { "stamped", stamped.ToString() },
            { "conflicts", conflicts.ToString() },
            { "stopReason", stopReason ?? "" },
            { "completedAt", OrderCommitment.Iso(now) }
        };

        if (!CommitmentsEnabled)
        {
            context.Logger.LogInformation("Deadline repair skipped: COMMITMENTS_ENABLED is off.");
            return JsonResponse(Summary("disabled"));
        }

        var slice = BudgetFactory().SliceUntil(RepairWalkTime);
        foreach (var status in DeadlineStatuses)
        {
            var rows = await _orderRepo.QueryByStatusForCronAsync(status) ?? new List<Dictionary<string, object>>();
            foreach (var row in rows)
            {
                if (!slice.MayStartRow(context))
                {
                    stopReason = slice.StopReason;
                    break;
                }

                read++;
                var next = OrderDeadlines.Next(row);
                if (OrderDeadlines.IsStored(row, next))
                {
                    continue;
                }

                if (await _orderRepo.TrySetDeadlineAsync(
                        GetString(row, "orderId"), GetString(row, "createdAt"), GetInt(row, "version"), next))
                {
                    stamped++;
                }
                else
                {
                    // The index copy was behind: the newer write stamped its own deadline.
                    conflicts++;
                }
            }

            if (stopReason != null)
            {
                break;
            }
        }

        context.Logger.LogInformation(
            $"Deadline repair complete: {read} orders read, {stamped} deadlines stamped, {conflicts} changed since read"
            + (stopReason != null ? $", stopped early ({stopReason})." : "."));
        return JsonResponse(Summary(stopReason == null ? "ok" : "partial"));
    }
}
