using System.Globalization;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Models;

namespace WeredaConnect.Server.Services;

/// <summary>
/// FUP-37 (escrow-protocol.md §Additions in 2.23.0): reconcile live order checkouts with Telebirr's
/// queryOrder, so a payment whose notification never arrived (or failed verification) is funded or
/// alarmed, never failed silently by the order cron's 24 h <c>payment_failed</c>. Run by escrow-cron
/// (<see cref="Action"/>, every 15 minutes on a live deployment: that Lambda has the merchant key and
/// the allow-listed egress).
///
/// Two kinds of <c>payment_submitted</c> orders are asked about, nothing else:
/// <list type="bullet">
/// <item><b>About to fail</b> (payment clock past T2 minus <see cref="DueLead"/>, every checkout window
/// closed, no settling verdict yet): every checkout of the order's current payment attempt that reached
/// Telebirr is queried. A paid one is recorded in the webhook inbox exactly as a verified
/// <c>Completed</c> notification is (the D8 order hold first, then a pending inbox row with the same
/// key), so the inbox sweep funds the order through the one funding path, once; a late notification
/// of the same payment finds the row. The verdict lands on the checkout slot
/// (<see cref="OrderCheckoutQuery"/>); the order cron fails an order with a checkout only on a
/// settling verdict (<see cref="OrderCheckoutSlot.QuerySettles"/>). An answer that decides nothing
/// (gateway error, still payable, refunded, unknown status, a paid answer without its payment id) logs
/// <see cref="UnresolvedToken"/> (paged) and the order waits, asked again every run; a day past the
/// payment deadline the reconciliation gives up (alarmed, audited) and the order may fail.</item>
/// <item><b>A stuck checkout</b>: the slot is <c>unknown</c> (the preOrder's answer was lost), or still
/// <c>opening</c> with its mapping written, and its window is open. One query, at most every
/// <see cref="UnknownSlotRequery"/>: closed or failed at Telebirr frees the buyer to open a new
/// checkout at once; paid goes to the inbox; anything else keeps the slot blocked until its window
/// ends, as before.</item>
/// </list>
/// Bounded: at most <see cref="MaxQueriesPerRun"/> queries per run and <see cref="MaxAttemptsPerOrder"/>
/// checkouts per order; an order cut off by the bound gets no verdict and is taken again next run.
/// </summary>
public sealed class OrderPaymentReconciler
{
    /// <summary>The escrow-cron action.</summary>
    public const string Action = "order_payment_reconcile";

    /// <summary>A checkout whose payment state queryOrder could not settle (paged, money alarm).</summary>
    public const string UnresolvedToken = "ORDER_PAYMENT_QUERY_UNRESOLVED";

    /// <summary>A paid checkout found by queryOrder and handed to the webhook inbox (info).</summary>
    public const string ReconciledToken = "ORDER_PAYMENT_RECONCILED";

    /// <summary>One line per run with its counts.</summary>
    public const string RunToken = "ORDER_PAYMENT_RECONCILE_RUN";

    /// <summary>queryOrder calls per run at most.</summary>
    internal const int MaxQueriesPerRun = 40;

    /// <summary>Checkouts of one payment attempt queried at most; more is alarmed as unresolved.</summary>
    internal const int MaxAttemptsPerOrder = 10;

    /// <summary>Mapping rows read per order at most, walking back from the slot's attempt.</summary>
    internal const int MaxMappingReadsPerOrder = 50;

    /// <summary>An order is asked about from this long before its payment deadline (T2).</summary>
    internal static readonly TimeSpan DueLead = TimeSpan.FromHours(1);

    /// <summary>A stuck checkout is asked about once its slot is this old.</summary>
    internal static readonly TimeSpan UnknownSlotMinAge = TimeSpan.FromMinutes(3);

    /// <summary>A stuck checkout is asked about again at most this often.</summary>
    internal static readonly TimeSpan UnknownSlotRequery = TimeSpan.FromMinutes(30);

    /// <summary>How long past the payment deadline an unresolved order waits before the reconciliation gives up.</summary>
    internal static readonly TimeSpan GiveUpAfter = TimeSpan.FromHours(24);

    private const string MappingFailedStatus = "failed";
    private const string OrderEscrowPrefix = "escrow-order-";

    private readonly IOrderRepository _orders;
    private readonly IOrderCheckoutRepository _checkouts;
    private readonly IWebhookInboxRepository _inbox;
    private readonly ITelebirrGateway _gateway;
    private readonly IEscrowAuditRepository _audit;
    private readonly TimeProvider _clock;

    public OrderPaymentReconciler(
        IOrderRepository orders, IOrderCheckoutRepository checkouts, IWebhookInboxRepository inbox,
        ITelebirrGateway gateway, IEscrowAuditRepository audit, TimeProvider? clock = null)
    {
        _orders = orders ?? throw new ArgumentNullException(nameof(orders));
        _checkouts = checkouts ?? throw new ArgumentNullException(nameof(checkouts));
        _inbox = inbox ?? throw new ArgumentNullException(nameof(inbox));
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>One run's state: its time check, logger and counts.</summary>
    private sealed record Run(Func<bool> MayContinue, ILambdaContext Context, OrderPaymentReconcileCounts Counts)
    {
        /// <summary>True while another queryOrder call fits this run.</summary>
        public bool MayQuery()
        {
            if (Counts.Queries < MaxQueriesPerRun && MayContinue())
            {
                return true;
            }

            Counts.Truncated = true;
            return false;
        }
    }

    /// <summary>
    /// Walk the <c>payment_submitted</c> orders (oldest first) once. <paramref name="mayContinue"/> is
    /// the run's time check, asked before each order and each query. Never throws: a failed read of
    /// one order is counted and the walk goes on; a failed index read ends the run (<c>QueryFailed</c>).
    /// </summary>
    public async Task<OrderPaymentReconcileCounts> RunAsync(Func<bool> mayContinue, ILambdaContext context)
    {
        var run = new Run(mayContinue, context, new OrderPaymentReconcileCounts());
        List<Dictionary<string, object>> orders;
        try
        {
            orders = await _orders.QueryByStatusForCronAsync("payment_submitted", Iso(Now - UnknownSlotMinAge))
                     ?? new List<Dictionary<string, object>>();
        }
        catch (Exception ex)
        {
            run.Counts.QueryFailed = true;
            context.Logger.LogError($"Order payment reconcile: the payment_submitted orders could not be read [{ex.GetType().Name}].");
            return run.Counts;
        }

        foreach (var order in orders)
        {
            if (!run.MayQuery())
            {
                break;
            }

            run.Counts.Seen++;
            try
            {
                await ReconcileOrderAsync(order, run);
            }
            catch (Exception ex)
            {
                // A read or write that failed: no verdict, so the order cron keeps waiting and the next run asks again.
                run.Counts.Errors++;
                context.Logger.LogWarning(
                    $"Order payment reconcile: order {MoneyAlarmLog.LogId(GetString(order, "orderId"))} not finished this run [{ex.GetType().Name}].");
            }
        }

        var c = run.Counts;
        context.Logger.LogInformation(
            $"{RunToken} seen={c.Seen} queries={c.Queries} paid={c.Paid} notPaid={c.NotPaid} unresolved={c.Unresolved} "
            + $"gaveUp={c.GaveUp} slotsClosed={c.SlotsClosed} errors={c.Errors} truncated={(c.Truncated ? "true" : "false")}");
        return c;
    }

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    private async Task ReconcileOrderAsync(Dictionary<string, object> order, Run run)
    {
        var orderId = GetString(order, "orderId");
        var cycle = GetString(order, "paymentSubmittedAt");
        var clockStart = ParseUtc(cycle) ?? ParseUtc(GetString(order, "createdAt"));
        if (orderId.Length == 0 || clockStart == null)
        {
            return;
        }

        var slot = await _checkouts.GetSlotAsync(orderId);
        if (slot == null)
        {
            // No checkout was ever opened: nothing can have been paid through Telebirr.
            return;
        }

        var now = Now;
        var deadline = clockStart.Value + OrderTimings.Current.PaymentVerification;
        if (now >= deadline - DueLead)
        {
            // A checkout still open may still be paid; a settled attempt needs nothing more.
            if (!slot.QuerySettles(cycle) && now >= slot.OpenUntilUtc)
            {
                await ReconcileDueAsync(orderId, cycle, deadline, slot, now, run);
            }

            return;
        }

        if (IsStuck(slot, now))
        {
            await ReconcileStuckSlotAsync(orderId, cycle, slot, now, run);
        }
    }

    // ─── About to fail ───────────────────────────────────────────────────

    private async Task ReconcileDueAsync(
        string orderId, string cycle, DateTime deadline, OrderCheckoutSlot slot, DateTime now, Run run)
    {
        var (checkouts, complete) = await CurrentCycleCheckoutsAsync(orderId, cycle, slot);
        var paid = false;
        var unresolved = 0;
        foreach (var mapping in checkouts)
        {
            if (!run.MayQuery())
            {
                // Cut off: no verdict, so the order waits and the next run asks again.
                return;
            }

            var answer = await QueryAsync(mapping.MerchOrderId, run);
            if (OutOfTime(answer, run))
            {
                return;
            }

            if (answer.State == TelebirrPaymentState.NotPaid)
            {
                continue;
            }

            if (answer.State == TelebirrPaymentState.Paid)
            {
                // A paid answer that could not reach the inbox was alarmed there.
                if (await RecordPaidAsync(orderId, mapping, answer, now, run))
                {
                    paid = true;
                }
                else
                {
                    unresolved++;
                }

                continue;
            }

            unresolved++;
            LogUnresolved(run, orderId, mapping.MerchOrderId, answer, "query");
        }

        if (!complete)
        {
            unresolved++;
            run.Context.Logger.LogError(
                $"{UnresolvedToken} orderId={MoneyAlarmLog.LogId(orderId)} reason=too_many_attempts attempt={slot.Attempt}");
        }

        string verdict;
        if (paid)
        {
            // Any other attempt that decided nothing was alarmed above; the paid one funds the order.
            verdict = OrderCheckoutQuery.Paid;
            run.Counts.Paid++;
        }
        else if (unresolved > 0 && now > deadline + GiveUpAfter)
        {
            verdict = OrderCheckoutQuery.GaveUp;
            run.Counts.GaveUp++;
            var hours = ((now - deadline).TotalHours).ToString("F0", CultureInfo.InvariantCulture);
            run.Context.Logger.LogError(
                $"{UnresolvedToken} orderId={MoneyAlarmLog.LogId(orderId)} reason=gave_up hoursPastDeadline={hours} "
                + "(the order may now fail; check its checkouts in the merchant portal and refund a paid one by hand)");
            await TryAuditAsync(orderId, "order_payment_query_gave_up",
                $"orderId={orderId},attempt={slot.Attempt},unresolved={unresolved},hoursPastDeadline={hours}", run);
        }
        else if (unresolved > 0)
        {
            verdict = OrderCheckoutQuery.Unresolved;
            run.Counts.Unresolved++;
        }
        else
        {
            verdict = OrderCheckoutQuery.NotPaid;
            run.Counts.NotPaid++;
        }

        if (!await _checkouts.RecordQueryVerdictAsync(orderId, slot.Attempt, verdict, cycle, now))
        {
            run.Context.Logger.LogInformation(
                $"Order payment reconcile: the checkout of order {MoneyAlarmLog.LogId(orderId)} moved on before its verdict ({verdict}); asked again next run.");
        }
    }

    /// <summary>
    /// The checkouts of the order's current payment attempt that reached Telebirr, newest first:
    /// walking back from the slot's attempt, a missing mapping is an attempt that stopped before
    /// Telebirr (skipped), a mapping of another payment attempt ends the walk (attempt numbers only
    /// grow), and a preOrder Telebirr refused (<c>failed</c>) created nothing there. Incomplete when
    /// the bound was reached first.
    /// </summary>
    private async Task<(List<OrderCheckoutMapping> Checkouts, bool Complete)> CurrentCycleCheckoutsAsync(
        string orderId, string cycle, OrderCheckoutSlot slot)
    {
        var checkouts = new List<OrderCheckoutMapping>();
        var reads = 0;
        for (var attempt = slot.Attempt; attempt >= 1; attempt--)
        {
            if (reads++ >= MaxMappingReadsPerOrder)
            {
                return (checkouts, false);
            }

            var mapping = await _checkouts.GetMappingAsync(EscrowService.BuildOrderMerchOrderId(orderId, attempt));
            if (mapping == null || !string.Equals(mapping.OrderId, orderId, StringComparison.Ordinal))
            {
                continue;
            }

            if (!SameCycle(mapping, cycle))
            {
                return (checkouts, true);
            }

            if (string.Equals(mapping.Status, MappingFailedStatus, StringComparison.Ordinal))
            {
                continue;
            }

            if (checkouts.Count == MaxAttemptsPerOrder)
            {
                return (checkouts, false);
            }

            checkouts.Add(mapping);
        }

        return (checkouts, true);
    }

    // ─── A stuck checkout ────────────────────────────────────────────────

    private static bool IsStuck(OrderCheckoutSlot slot, DateTime now)
        => slot.Outcome is OrderCheckoutOutcome.Unknown or OrderCheckoutOutcome.Opening
           && slot.IsOpen(now)
           && now - slot.OpenedAtUtc >= UnknownSlotMinAge
           && (slot.QueriedAtUtc == null || now - slot.QueriedAtUtc.Value >= UnknownSlotRequery);

    private async Task ReconcileStuckSlotAsync(string orderId, string cycle, OrderCheckoutSlot slot, DateTime now, Run run)
    {
        var mapping = await _checkouts.GetMappingAsync(slot.MerchOrderId);
        if (mapping == null || !string.Equals(mapping.OrderId, orderId, StringComparison.Ordinal) || !SameCycle(mapping, cycle))
        {
            // An opening slot with no mapping never reached Telebirr: the buyer's retry finishes it.
            return;
        }

        if (!run.MayQuery())
        {
            return;
        }

        var answer = await QueryAsync(mapping.MerchOrderId, run);
        if (OutOfTime(answer, run))
        {
            return;
        }

        if (answer.State == TelebirrPaymentState.NotPaid)
        {
            // Closed or failed at Telebirr: no payment can come, so the buyer may open a new checkout now.
            await _checkouts.UpdateSlotOutcomeAsync(orderId, slot.Attempt, OrderCheckoutOutcome.Failed);
            run.Counts.SlotsClosed++;
            run.Context.Logger.LogInformation(
                $"Order payment reconcile: checkout {MoneyAlarmLog.LogId(mapping.MerchOrderId)} of order {MoneyAlarmLog.LogId(orderId)} "
                + $"was {MoneyAlarmLog.LogId(slot.Outcome)} and is {MoneyAlarmLog.LogId(answer.Status)} at Telebirr; the slot is failed.");
            await TryAuditAsync(orderId, "order_checkout_reconciled",
                $"orderId={orderId},merchOrderId={mapping.MerchOrderId},attempt={slot.Attempt},from={slot.Outcome},telebirrStatus={answer.Status}", run);
            return;
        }

        if (answer.State == TelebirrPaymentState.Paid)
        {
            await RecordPaidAsync(orderId, mapping, answer, now, run);
        }
        else
        {
            // Not paged here: the slot keeps blocking until its window ends, as before, and the
            // order's own check before T2 alarms if it is still unsettled then.
            run.Context.Logger.LogWarning(
                $"Order payment reconcile: checkout {MoneyAlarmLog.LogId(mapping.MerchOrderId)} of order {MoneyAlarmLog.LogId(orderId)} "
                + $"stays {MoneyAlarmLog.LogId(slot.Outcome)} (state={answer.State} status={MoneyAlarmLog.LogId(answer.Status)} "
                + $"error={MoneyAlarmLog.LogId(answer.ErrorCode)}) until {Iso(slot.OpenUntilUtc)}.");
        }

        // Paces the next query; never a settling verdict (the check before T2 asks every checkout).
        await _checkouts.RecordQueryVerdictAsync(orderId, slot.Attempt, OrderCheckoutQuery.Unresolved, cycle, now);
    }

    // ─── Shared ──────────────────────────────────────────────────────────

    /// <summary>
    /// The run's own deadline stopped the call before it was sent (<see cref="TelebirrGatewayErrors.NoTime"/>):
    /// Telebirr was not asked, so nothing is alarmed or recorded and the order is taken again next run.
    /// </summary>
    private static bool OutOfTime(TelebirrQueryOrderResult answer, Run run)
    {
        if (answer.ErrorCode != TelebirrGatewayErrors.NoTime)
        {
            return false;
        }

        run.Counts.Truncated = true;
        return true;
    }

    private async Task<TelebirrQueryOrderResult> QueryAsync(string merchOrderId, Run run)
    {
        run.Counts.Queries++;
        try
        {
            return await _gateway.QueryOrderAsync(merchOrderId) ?? TelebirrQueryOrderResult.Failed("TELEBIRR_QUERY_NO_ANSWER");
        }
        catch (Exception ex)
        {
            return TelebirrQueryOrderResult.Failed($"GATEWAY_EXCEPTION:{ex.GetType().Name}");
        }
    }

    /// <summary>
    /// Hand a paid checkout to the webhook inbox exactly as a verified <c>Completed</c> notification
    /// is (ESC-45 D8 and D4): hold the order for this payment attempt, then write the pending inbox row
    /// under the notification's own key, <c>webhook-inbox:{merch}:{payment}:Completed</c>. The inbox sweep
    /// applies it through the webhook's funding path (amount, currency and attempt checks, the funding
    /// write conditional on <c>payment_submitted</c>, the escrow, stock and audit rows); a row that is
    /// already there (the notification, or an earlier run) is left as it is. True when the payment is in
    /// the inbox; false (alarmed) when it could not be put there.
    /// </summary>
    private async Task<bool> RecordPaidAsync(
        string orderId, OrderCheckoutMapping mapping, TelebirrQueryOrderResult answer, DateTime now, Run run)
    {
        if (string.IsNullOrEmpty(answer.PaymentOrderId))
        {
            LogUnresolved(run, orderId, mapping.MerchOrderId, answer, "paid_without_payment_order_id");
            return false;
        }

        try
        {
            await _orders.TryHoldForPaymentAsync(
                orderId, WebhookInbox.Iso(now + WebhookInbox.ReplayHorizon), mapping.PaymentSubmittedAt);
        }
        catch (Exception ex)
        {
            // The pending inbox row below keeps the order out of payment_failed on its own.
            run.Context.Logger.LogWarning(
                $"Order payment reconcile: hold not written for order {MoneyAlarmLog.LogId(orderId)} [{ex.GetType().Name}].");
        }

        var biz = new TelebirrWebhookBiz
        {
            MerchOrderId = mapping.MerchOrderId,
            PaymentOrderId = answer.PaymentOrderId,
            TotalAmount = answer.TotalAmount,
            TransCurrency = answer.TransCurrency,
            TradeStatus = TelebirrQueryOrder.CompletedStatus,
            TransId = answer.TransId,
        };
        var key = WebhookInboxRepository.KeyOf(mapping.MerchOrderId, answer.PaymentOrderId, TelebirrQueryOrder.CompletedStatus);
        bool recorded;
        try
        {
            recorded = await _inbox.RecordAsync(key, OrderEscrowPrefix + orderId, orderId, mapping.MerchOrderId, biz, now);
        }
        catch (Exception ex)
        {
            LogUnresolved(run, orderId, mapping.MerchOrderId, answer, $"inbox_write_{ex.GetType().Name}");
            return false;
        }

        var ids = $"orderId={MoneyAlarmLog.LogId(orderId)} merchOrderId={MoneyAlarmLog.LogId(mapping.MerchOrderId)} "
                  + $"paymentOrderId={MoneyAlarmLog.LogId(answer.PaymentOrderId)}";
        if (!recorded)
        {
            run.Context.Logger.LogInformation($"Order payment reconcile: the payment is already in the webhook inbox: {ids}.");
            return true;
        }

        run.Context.Logger.LogInformation(
            $"{ReconciledToken} {ids} status={MoneyAlarmLog.LogId(answer.Status)} (no notification applied it; the inbox sweep funds the order).");
        await TryAuditAsync(orderId, "order_payment_reconciled",
            $"orderId={orderId},merchOrderId={mapping.MerchOrderId},paymentOrderId={answer.PaymentOrderId},"
            + $"amount={answer.TotalAmount},telebirrStatus={answer.Status},source=queryOrder", run, answer.PaymentOrderId);
        return true;
    }

    private static void LogUnresolved(Run run, string orderId, string merchOrderId, TelebirrQueryOrderResult answer, string reason)
        => run.Context.Logger.LogError(
            $"{UnresolvedToken} orderId={MoneyAlarmLog.LogId(orderId)} merchOrderId={MoneyAlarmLog.LogId(merchOrderId)} "
            + $"state={answer.State} status={MoneyAlarmLog.LogId(answer.Status)} error={MoneyAlarmLog.LogId(answer.ErrorCode)} "
            + $"paymentOrderId={MoneyAlarmLog.LogId(answer.PaymentOrderId)} reason={MoneyAlarmLog.LogId(reason)}");

    /// <summary>An escrow-audit row on the order's escrow id. Best effort: the log line is the other record.</summary>
    private async Task TryAuditAsync(string orderId, string action, string metadata, Run run, string? telebirrRef = null)
    {
        var entry = new Dictionary<string, object>
        {
            { "auditId", $"audit-{Guid.NewGuid():N}" },
            { "escrowId", OrderEscrowPrefix + orderId },
            { "action", action },
            { "fromState", "" },
            { "toState", "" },
            { "actor", "system" },
            { "timestamp", Iso(Now) },
            { "metadata", metadata },
        };
        if (!string.IsNullOrEmpty(telebirrRef))
        {
            entry["telebirrRef"] = telebirrRef;
        }

        try
        {
            await _audit.AppendAsync(entry);
        }
        catch (Exception ex)
        {
            run.Context.Logger.LogWarning(
                $"Order payment reconcile: audit row {action} not written for order {MoneyAlarmLog.LogId(orderId)} [{ex.GetType().Name}].");
        }
    }

    private static bool SameCycle(OrderCheckoutMapping mapping, string cycle)
        => string.Equals(mapping.PaymentSubmittedAt ?? "", cycle, StringComparison.Ordinal);

    private static string Iso(DateTime utc) => WebhookInbox.Iso(utc);

    private static DateTime? ParseUtc(string value)
        => DateTime.TryParse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : null;

    private static string GetString(Dictionary<string, object> item, string key)
        => item.TryGetValue(key, out var value) ? value?.ToString() ?? "" : "";
}

/// <summary>What one <see cref="OrderPaymentReconciler.RunAsync"/> did.</summary>
public sealed class OrderPaymentReconcileCounts
{
    /// <summary>Orders looked at.</summary>
    public int Seen { get; set; }

    /// <summary>queryOrder calls made.</summary>
    public int Queries { get; set; }

    /// <summary>Orders with a paid checkout handed to the inbox (verdict <c>paid</c>).</summary>
    public int Paid { get; set; }

    /// <summary>Orders whose every checkout is closed at Telebirr (verdict <c>not_paid</c>).</summary>
    public int NotPaid { get; set; }

    /// <summary>Orders left waiting on an answer that decided nothing (alarmed).</summary>
    public int Unresolved { get; set; }

    /// <summary>Orders given up on a day past their deadline (alarmed, audited).</summary>
    public int GaveUp { get; set; }

    /// <summary>Stuck checkouts freed because Telebirr has them closed.</summary>
    public int SlotsClosed { get; set; }

    /// <summary>Orders not finished because a read or write failed.</summary>
    public int Errors { get; set; }

    /// <summary>The run stopped at its query or time bound with orders left.</summary>
    public bool Truncated { get; set; }

    /// <summary>The payment_submitted orders could not be read.</summary>
    public bool QueryFailed { get; set; }
}
