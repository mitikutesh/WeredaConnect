using WeredaConnect.Server.Data;
using WeredaConnect.Server.Helpers;

namespace WeredaConnect.Server.Services;

/// <summary>Where the money behind an escrow says its order should be (<see cref="OrderMoneyMirror.TargetFor"/>).</summary>
public enum OrderMoneyTarget
{
    /// <summary>The money has not moved (held, initiated, cancelled, a refund still queued, or unknown).</summary>
    None,

    /// <summary>The release is committed: the farmer leg is paid or queued, a leg may still be owed.</summary>
    Disbursing,

    /// <summary>Every leg is paid: <c>completed</c> with nothing owed and nothing queued.</summary>
    Settled,

    /// <summary>The buyer got the money back: the order must already be closed (checked, never moved).</summary>
    Refunded,

    /// <summary>The escrow carries a dispute verdict: the verdict owns the order (ESC-03), never disbursing or settled.</summary>
    Verdict
}

/// <summary>What <see cref="OrderMoneyMirror.FollowAsync"/> did.</summary>
public enum OrderMoneyOutcome
{
    /// <summary>Nothing to follow: the money has not moved, or no order repository is wired.</summary>
    NotApplicable,

    /// <summary>The escrow names no order (a legacy delivery escrow without one).</summary>
    NoOrder,

    /// <summary>This call moved the order (one or more hops).</summary>
    Moved,

    /// <summary>The order already shows what the money says.</summary>
    AlreadyThere,

    /// <summary>The order cannot show it but agrees with it (closed after a refund, a flagged override, a verdict status).</summary>
    Consistent,

    /// <summary>The order cannot follow money that moved, or is ahead of money that did not (see the reason).</summary>
    Mismatch,

    /// <summary>The order could follow, but a read or a write failed, or kept losing to other writers.</summary>
    Failed
}

/// <summary>The result of one follow: the order as last read, and why when it did not get there.</summary>
public sealed record OrderMoneyResult(
    OrderMoneyOutcome Outcome, OrderMoneyTarget Target, string? OrderId, string? OrderStatus, string? Reason = null);

/// <summary>
/// ESC-09: the order status follows the money (order-lifecycle.md 1.17.0, escrow-protocol.md 1.17.0).
/// Called after every escrow money write (release, trucker leg, manual payout close, crash recovery,
/// stale lock and legacy parks) and by the hourly order-money sweep. Moves the order forward only:
/// <c>delivered -> disbursing</c> once the release is committed, <c>disbursing -> settled</c> once the
/// escrow is <c>completed</c> with nothing owed. An order left at <c>accepted</c>,
/// <c>pickup_confirmed</c> or <c>in_transit</c> behind a confirmed delivery is first advanced to
/// <c>delivered</c>, the system catch-up SIM-11 and ESC-03 already make. Each hop is one
/// status-conditioned write (<see cref="IOrderRepository.TryAdvanceStatusAsync"/>, version + 1) with
/// one transaction-audit row <c>order_updated</c>; a lost race re-reads and decides again. An escrow
/// with a dispute verdict is repaired through <see cref="DisputeVerdictMirror"/> (toward
/// <c>dispute_resolved</c>) and never moved to disbursing or settled. Never throws.
/// </summary>
/// <remarks>
/// <para>
/// Lost answer on a hop: when the SDK retried a hop that had landed, the condition fails, the
/// re-read shows the next status and the mirror goes on; that hop then has no transaction-audit row.
/// </para>
/// <para>
/// Escrow-audit rows are keyed on <c>timestamp</c> (the table's sort key), so every deterministic row
/// needs a slot no other writer uses. The registry (keep it complete when adding one):
/// <list type="bullet">
/// <item><c>disputeResolvedAt + 1/2/3</c> ticks: verdict mismatch, propagation failed, settlement
/// blocked (ESC-03, <see cref="DisputeVerdictMirror.VerdictAuditTimestamp"/>).</item>
/// <item><c>queuedAt - 1</c>: stale lock parked, legacy trucker parked (ESC-05, ESC-04).</item>
/// <item>manual payout close: <c>paidAt</c> paid row, <c>paidAt + 1</c> release, refund or complete row
/// (ESC-02); trucker retry: <c>paidAt + 1</c> complete row (ESC-04).</item>
/// <item><c>holdCapReachedAt</c>-time <c>hold_cap_reached</c> (ESC-06); ESC-10 block rows at their stored reason time.</item>
/// <item><c>createdAt + 7</c>: <see cref="MismatchAuditAction"/>; <c>createdAt + 8</c>:
/// <see cref="AheadAuditAction"/> (ESC-09). No other row uses <c>createdAt</c> plus ticks (creation rows
/// are written at the current time).</item>
/// </list>
/// </para>
/// </remarks>
public sealed class OrderMoneyMirror
{
    /// <summary>Escrow-audit action, once per escrow: the order cannot follow money that moved.</summary>
    public const string MismatchAuditAction = "order_money_mismatch";

    /// <summary>Escrow-audit action, once per escrow: the order is ahead of money that did not move.</summary>
    public const string AheadAuditAction = "order_money_ahead";

    /// <summary>Hops per call: a lagging order goes to delivered, disbursing, settled.</summary>
    private const int MaxHops = 3;

    /// <summary>Lost races per call before it gives up.</summary>
    private const int MaxConflicts = 3;

    /// <summary>
    /// The money hops, the two system edges of <c>OrderTransitions</c> (SyncFunction.cs) the mirror
    /// may take; a test keeps them in that map.
    /// </summary>
    public static readonly IReadOnlyList<(string From, string To)> MoneyHops = new[]
    {
        ("delivered", "disbursing"),
        ("disbursing", "settled")
    };

    /// <summary>Order statuses a confirmed delivery may first advance to <c>delivered</c>.</summary>
    private static readonly HashSet<string> LogisticsStatuses = new(StringComparer.Ordinal)
    {
        "accepted", "pickup_confirmed", "in_transit"
    };

    /// <summary>Order statuses that agree with a refunded escrow.</summary>
    private static readonly HashSet<string> ClosedStatuses = new(StringComparer.Ordinal)
    {
        "cancelled", "rejected", "dispute_resolved"
    };

    private readonly IOrderRepository? _orderRepo;
    private readonly IDeliveryRepository? _deliveryRepo;
    private readonly ITransactionAuditRepository? _txAuditRepo;
    private readonly DisputeVerdictMirror _verdictMirror;

    public OrderMoneyMirror(
        IOrderRepository? orderRepo, IDeliveryRepository? deliveryRepo, ITransactionAuditRepository? txAuditRepo,
        DisputeVerdictMirror verdictMirror)
    {
        _orderRepo = orderRepo;
        _deliveryRepo = deliveryRepo;
        _txAuditRepo = txAuditRepo;
        _verdictMirror = verdictMirror ?? throw new ArgumentNullException(nameof(verdictMirror));
    }

    /// <summary>
    /// The order status the escrow's money calls for. Total over the escrow states (escrow-protocol.md
    /// 1.17.0): a verdict owns its order; <c>completed</c> with no trucker leg owed and nothing queued is
    /// settled; <c>completed</c> still owing (a legacy row before the ESC-04 park), <c>released</c> (with or
    /// without a trucker leg owed or queued) and a release waiting in the manual payout queue are
    /// disbursing; <c>refunded</c> is checked against a closed order; everything else (held, initiated,
    /// cancelled, a refund still queued) has not moved the money.
    /// </summary>
    public static OrderMoneyTarget TargetFor(Dictionary<string, object> escrow)
    {
        if (!string.IsNullOrEmpty(Str(escrow, "disputeResolution")))
        {
            return OrderMoneyTarget.Verdict;
        }

        var status = Str(escrow, "status");
        var queued = string.Equals(Str(escrow, ManualPayoutFields.Marker), ManualPayoutFields.MarkerOpen, StringComparison.Ordinal);
        var queuedRelease = queued && Str(escrow, ManualPayoutFields.Op) == ManualPayoutFields.OpRelease;
        return status switch
        {
            "completed" when !Flag(escrow, TruckerPayoutFields.Pending) && !queued => OrderMoneyTarget.Settled,
            "completed" or "released" => OrderMoneyTarget.Disbursing,
            "escrowed" when queuedRelease => OrderMoneyTarget.Disbursing,
            "refunded" => OrderMoneyTarget.Refunded,
            _ => OrderMoneyTarget.None
        };
    }

    /// <summary>
    /// Move the order behind <paramref name="escrow"/> to where its money says (see the class summary).
    /// <paramref name="orderStatusHint"/> is the status the sweep's index copy showed: with no target, an
    /// order shown <c>disbursing</c> or <c>settled</c> is re-read to tell whether it is ahead of the money.
    /// </summary>
    public async Task<OrderMoneyResult> FollowAsync(
        Dictionary<string, object> escrow, string source, string? orderStatusHint = null, ILambdaContext? context = null)
    {
        var target = TargetFor(escrow);
        try
        {
            if (_orderRepo == null)
            {
                return new(OrderMoneyOutcome.NotApplicable, target, null, null);
            }

            if (target == OrderMoneyTarget.Verdict)
            {
                return await FollowVerdictAsync(escrow, context);
            }

            if (target == OrderMoneyTarget.None && orderStatusHint is not ("disbursing" or "settled"))
            {
                return new(OrderMoneyOutcome.NotApplicable, target, null, null);
            }

            var orderId = await ResolveOrderIdAsync(escrow);
            if (string.IsNullOrEmpty(orderId))
            {
                return new(OrderMoneyOutcome.NoOrder, target, null, null);
            }

            var order = await _orderRepo.GetByIdAsync(orderId, consistentRead: true);
            if (order == null)
            {
                return new(OrderMoneyOutcome.Mismatch, target, orderId, null, "order_missing");
            }

            return target switch
            {
                OrderMoneyTarget.None => Ahead(order, orderId, target),
                OrderMoneyTarget.Refunded => RefundedCheck(order, orderId),
                _ => await MoveAsync(order, orderId, escrow, target, source, context)
            };
        }
        catch (Exception ex)
        {
            return new(OrderMoneyOutcome.Failed, target, Str(escrow, "orderId"), null, $"[{ex.GetType().Name}]");
        }
    }

    /// <summary>
    /// A flagged admin override (ESC-03 verdict refund, ESC-09 refund) leaves the order status alone on
    /// purpose. It only ever excuses a refund: the flag is written before the money moves, so a refund that
    /// then failed (a lost lock, a refusal) must not keep a later release from moving the order.
    /// </summary>
    public static bool IsOverrideFlagged(Dictionary<string, object> order)
        => !string.IsNullOrEmpty(Str(order, OrderMoneyFields.DisputeOverrideAt))
           || !string.IsNullOrEmpty(Str(order, OrderMoneyFields.RefundOverrideAt));

    private async Task<OrderMoneyResult> FollowVerdictAsync(Dictionary<string, object> escrow, ILambdaContext? context)
    {
        var resolution = Str(escrow, "disputeResolution") ?? "";
        if (resolution is not ("farmer_wins" or "buyer_wins")
            || Str(escrow, "status") is not ("released" or "completed" or "refunded"))
        {
            // A legacy verdict without a direction (partial_refund) is settled by an admin by hand; a
            // verdict whose money has not moved yet is the settlement's to mirror (ESC-03 repairs and
            // alarms there, before any money moves).
            return new(OrderMoneyOutcome.NotApplicable, OrderMoneyTarget.Verdict, Str(escrow, "orderId"), null);
        }

        var resolvedAt = Str(escrow, "disputeResolvedAt") ?? Str(escrow, "createdAt") ?? DateTime.UtcNow.ToString("o");
        var result = await _verdictMirror.ApplyAsync(escrow, resolution, resolvedAt, context, cancelBeforePickup: false);
        var orderStatus = result.OrderItem != null ? Str(result.OrderItem, "status") : null;
        if (Str(escrow, "status") == "refunded" && result.OrderItem != null && IsOverrideFlagged(result.OrderItem))
        {
            return new(OrderMoneyOutcome.Consistent, OrderMoneyTarget.Verdict, result.OrderId, orderStatus);
        }

        var outcome = result.Order switch
        {
            VerdictLegOutcome.None when string.IsNullOrEmpty(result.OrderId) => OrderMoneyOutcome.NoOrder,
            VerdictLegOutcome.Resolved => OrderMoneyOutcome.Moved,
            VerdictLegOutcome.AlreadyResolved => OrderMoneyOutcome.AlreadyThere,
            VerdictLegOutcome.Closed or VerdictLegOutcome.Cancelled => OrderMoneyOutcome.Consistent,
            VerdictLegOutcome.Mismatch or VerdictLegOutcome.Missing or VerdictLegOutcome.Deferred
                or VerdictLegOutcome.Skipped => OrderMoneyOutcome.Mismatch,
            VerdictLegOutcome.None => OrderMoneyOutcome.NotApplicable,
            _ => OrderMoneyOutcome.Failed
        };

        // The delivery leg alone may still be missing the verdict; that is the verdict's repair, not a mismatch here.
        return new(outcome, OrderMoneyTarget.Verdict, result.OrderId, orderStatus,
            outcome is OrderMoneyOutcome.Mismatch or OrderMoneyOutcome.Failed ? $"verdict:{result.FailedLegs ?? result.Order.ToString()}" : null);
    }

    private static OrderMoneyResult Ahead(Dictionary<string, object> order, string orderId, OrderMoneyTarget target)
    {
        var status = Str(order, "status") ?? "";
        return status is "disbursing" or "settled"
            ? new(OrderMoneyOutcome.Mismatch, target, orderId, status, "order_ahead")
            : new(OrderMoneyOutcome.NotApplicable, target, orderId, status);
    }

    private static OrderMoneyResult RefundedCheck(Dictionary<string, object> order, string orderId)
    {
        var status = Str(order, "status") ?? "";
        return ClosedStatuses.Contains(status) || IsOverrideFlagged(order)
            ? new(OrderMoneyOutcome.Consistent, OrderMoneyTarget.Refunded, orderId, status)
            : new(OrderMoneyOutcome.Mismatch, OrderMoneyTarget.Refunded, orderId, status, "cannot_follow");
    }

    private async Task<OrderMoneyResult> MoveAsync(
        Dictionary<string, object> order, string orderId, Dictionary<string, object> escrow, OrderMoneyTarget target,
        string source, ILambdaContext? context)
    {
        var goal = target == OrderMoneyTarget.Settled ? "settled" : "disbursing";
        var moved = false;
        var conflicts = 0;
        string? deliveryStatus = null;
        var deliveryRead = false;
        // Every hop and every lost race is one pass, and one more pass sees the goal.
        for (var step = 0; step < MaxHops + MaxConflicts + 1; step++)
        {
            var status = Str(order, "status") ?? "";
            if (status == goal || (goal == "disbursing" && status == "settled" && moved))
            {
                return new(moved ? OrderMoneyOutcome.Moved : OrderMoneyOutcome.AlreadyThere, target, orderId, status);
            }

            if (status == "dispute_resolved")
            {
                return new(moved ? OrderMoneyOutcome.Moved : OrderMoneyOutcome.Consistent, target, orderId, status);
            }

            // The money hops come from MoneyHops (the system edges of OrderTransitions), never past the goal.
            var hop = MoneyHops.FirstOrDefault(h => h.From == status);
            string next;
            if (hop.From != null && (hop.To != "settled" || goal == "settled"))
            {
                next = hop.To;
            }
            else if (status == "settled")
            {
                // Settled while the money still owes a leg: only a hand edit or a legacy row gets here.
                return new(OrderMoneyOutcome.Mismatch, target, orderId, status, "order_ahead");
            }
            else if (LogisticsStatuses.Contains(status))
            {
                if (!deliveryRead)
                {
                    deliveryStatus = await DeliveryStatusAsync(order, escrow);
                    deliveryRead = true;
                }

                if (string.IsNullOrEmpty(Str(escrow, "deliveryConfirmedAt")) && deliveryStatus != "delivered")
                {
                    return new(OrderMoneyOutcome.Mismatch, target, orderId, status, "cannot_follow");
                }

                next = "delivered";
            }
            else
            {
                return new(moved ? OrderMoneyOutcome.Moved : OrderMoneyOutcome.Mismatch, target, orderId, status,
                    moved ? null : "cannot_follow");
            }

            var updated = await _orderRepo!.TryAdvanceStatusAsync(orderId, status, next);
            if (updated != null)
            {
                moved = true;
                await TransactionAuditWriter.EmitAsync(_txAuditRepo, context, orderId, "order", orderId,
                    "order_updated", status, next, AuditContext.ForSystem(),
                    metadata: $"escrowId={Str(escrow, "escrowId")};escrowStatus={Str(escrow, "status")};source={source}");
                order = updated;
                continue;
            }

            // Another writer moved the order (or a hop whose answer was lost landed): decide again.
            if (++conflicts > MaxConflicts)
            {
                return new(OrderMoneyOutcome.Failed, target, orderId, status, "conflict");
            }

            var latest = await _orderRepo.GetByIdAsync(orderId, consistentRead: true);
            if (latest == null)
            {
                return new(OrderMoneyOutcome.Mismatch, target, orderId, null, "order_missing");
            }

            order = latest;
        }

        return new(OrderMoneyOutcome.Failed, target, orderId, Str(order, "status"), "conflict");
    }

    /// <summary>The escrow's order: its own <c>orderId</c>, else its delivery's.</summary>
    private async Task<string?> ResolveOrderIdAsync(Dictionary<string, object> escrow)
    {
        var orderId = Str(escrow, "orderId");
        if (!string.IsNullOrEmpty(orderId))
        {
            return orderId;
        }

        var deliveryId = Str(escrow, "deliveryId");
        if (_deliveryRepo == null || string.IsNullOrEmpty(deliveryId))
        {
            return null;
        }

        var delivery = await _deliveryRepo.GetByIdAsync(deliveryId, consistentRead: true);
        return delivery != null ? Str(delivery, "orderId") : null;
    }

    /// <summary>The status of the order's active delivery (else the escrow's), or null when there is none or it cannot be read.</summary>
    private async Task<string?> DeliveryStatusAsync(Dictionary<string, object> order, Dictionary<string, object> escrow)
    {
        var deliveryId = Str(order, "activeDeliveryId") ?? Str(escrow, "deliveryId");
        if (_deliveryRepo == null || string.IsNullOrEmpty(deliveryId))
        {
            return null;
        }

        var delivery = await _deliveryRepo.GetByIdAsync(deliveryId, consistentRead: true);
        return delivery != null ? Str(delivery, "status") : null;
    }

    private static string? Str(Dictionary<string, object> item, string key)
        => item.TryGetValue(key, out var value) && value != null ? value.ToString() : null;

    private static bool Flag(Dictionary<string, object> item, string key)
        => item.TryGetValue(key, out var value) && value switch
        {
            bool b => b,
            string s => string.Equals(s, "true", StringComparison.OrdinalIgnoreCase),
            _ => false
        };
}
