using System.Globalization;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Helpers;

namespace WeredaConnect.Server.Services;

/// <summary>Outcome of one leg (order or delivery) of <see cref="DisputeVerdictMirror"/>.</summary>
public enum VerdictLegOutcome
{
    /// <summary>Nothing to mirror: no id for this leg, or no repository wired.</summary>
    None,

    /// <summary>This run moved the leg to the verdict.</summary>
    Resolved,

    /// <summary>The leg already showed this verdict; nothing written.</summary>
    AlreadyResolved,

    /// <summary><c>buyer_wins</c> on an order (or delivery) that is already cancelled or rejected: nothing to move.</summary>
    Closed,

    /// <summary><c>buyer_wins</c> before pickup: this run cancelled the order (stock restored) or the pending delivery.</summary>
    Cancelled,

    /// <summary><c>buyer_wins</c> before pickup, but this caller does not cancel: the refund settlement will.</summary>
    Deferred,

    /// <summary>The leg is in a status the verdict cannot follow from (or carries another verdict).</summary>
    Mismatch,

    /// <summary>The escrow names a row that does not exist.</summary>
    Missing,

    /// <summary>Version conflicts on every retry.</summary>
    Conflict,

    /// <summary>A write failed for another reason.</summary>
    Failed,

    /// <summary>Could follow, but nothing was written because the other leg cannot.</summary>
    Skipped,

    /// <summary>A read failed before the legs could be decided; nothing was decided or written.</summary>
    Unknown,

    /// <summary>
    /// A write the refund depends on did not land (the order cancel, or its stock restore, before a
    /// <c>buyer_wins</c> refund). The refund must not go ahead on this run; the next run retries.
    /// </summary>
    Blocked
}

/// <summary>What <see cref="DisputeVerdictMirror.ApplyAsync"/> did to the order and the delivery.</summary>
public sealed record DisputeVerdictMirrorResult
{
    public required VerdictLegOutcome Order { get; init; }
    public required VerdictLegOutcome Delivery { get; init; }
    public string? OrderId { get; init; }
    public string? DeliveryId { get; init; }

    /// <summary>The order as last read (null when there is none); FRD-03 and the override flag use its version.</summary>
    public Dictionary<string, object>? OrderItem { get; init; }

    /// <summary>
    /// <c>order:&lt;reason&gt;,delivery:&lt;reason&gt;</c> for every leg that did not reach the
    /// verdict; null when both did or had nothing to do.
    /// </summary>
    public string? FailedLegs { get; init; }

    /// <summary>
    /// True when a leg cannot follow the verdict: decided before any write, or found on a retry
    /// after a concurrent change. The settlement of the verdict is refused then (ESC-03), and
    /// FRD-03 refuses the refund of a held escrow.
    /// </summary>
    public bool IsMismatch => Order is VerdictLegOutcome.Mismatch or VerdictLegOutcome.Missing
                              || Delivery is VerdictLegOutcome.Mismatch or VerdictLegOutcome.Missing;

    /// <summary>
    /// True when a read failed before the legs were decided. Nothing is known about the order, so
    /// the settlement must not go ahead on this run (it fails closed and the next run retries).
    /// </summary>
    public bool IsUndecided => Order == VerdictLegOutcome.Unknown || Delivery == VerdictLegOutcome.Unknown;

    /// <summary>
    /// True when the settlement must not go ahead on this run: a read failed
    /// (<see cref="IsUndecided"/>), or the cancel-then-refund could not cancel the order or give its
    /// stock back. Fails closed; the next run retries.
    /// </summary>
    public bool BlocksSettlement => IsUndecided || Order == VerdictLegOutcome.Blocked || Delivery == VerdictLegOutcome.Blocked;

    /// <summary>The escrow-audit action that records <see cref="FailedLegs"/>.</summary>
    public string AuditAction => IsMismatch
        ? DisputeVerdictMirror.MismatchAuditAction
        : DisputeVerdictMirror.PropagationFailedAuditAction;
}

/// <summary>
/// ESC-03: move the order and the delivery behind an escrow to an admin dispute verdict
/// (order-lifecycle.md 1.12.0, delivery-verification-api.md 1.8.0). The admin verdict calls it,
/// and the settlement of a <c>dispute_resolved</c> escrow calls it again, before any money
/// moves, to repair a leg that failed the first time. FRD-03 reuses it for the refund of a
/// <c>held</c> escrow (fraud-detection-architecture.md section 18.3).
///
/// Order: <c>delivered</c> to <c>disputed</c> to <c>dispute_resolved</c>, or <c>disputed</c> to
/// <c>dispute_resolved</c>. An order left at <c>accepted</c>, <c>pickup_confirmed</c> or
/// <c>in_transit</c> behind a delivery that is already <c>delivered</c> or <c>disputed</c> is first
/// advanced to <c>delivered</c>. Each hop is a version-checked system write with one
/// transaction-audit row; a version conflict re-reads (strongly consistent) and retries, and the
/// third conflict of a run gives up. Delivery: a <c>delivered</c> delivery is marked
/// <c>disputed</c>, then the verdict is stamped on it (<c>disputeResolution</c>,
/// <c>disputeResolvedAt</c>); the delivery status stays <c>disputed</c>.
///
/// <c>buyer_wins</c> needs no goods to have moved: an order already <c>cancelled</c> or
/// <c>rejected</c> is left as it is, and an order before pickup (<c>funded</c>, or <c>accepted</c>
/// with no delivery picked up) is cancelled by the refund settlement, as the auto-refund sweep's
/// cancel-then-refund does: its pending delivery is cancelled first, then the order
/// (<c>cancelReason: escrow_refunded</c>), then its listing stock is restored. Other callers report
/// such an order as <see cref="VerdictLegOutcome.Deferred"/>. A cancel or restore that does not land
/// is <see cref="VerdictLegOutcome.Blocked"/>: the refund waits for the next run, which finds the
/// order closed and retries the restore before refunding.
///
/// Both legs are decided before anything is written: when either one cannot follow the verdict
/// (<see cref="DisputeVerdictMirrorResult.IsMismatch"/>), or a read failed
/// (<see cref="DisputeVerdictMirrorResult.IsUndecided"/>), nothing is written. The order's active
/// delivery is the one that counts, not an earlier delivery the escrow may still name. Never throws; the
/// caller logs and audits <see cref="DisputeVerdictMirrorResult.FailedLegs"/>.
/// </summary>
public sealed class DisputeVerdictMirror
{
    /// <summary>Version conflicts per run before the order leg gives up.</summary>
    private const int MaxOrderAttempts = 3;

    /// <summary>The longest path: advance to delivered, then disputed, then dispute_resolved.</summary>
    private const int MaxOrderHops = 3;

    private const double KgPerQuintal = 100.0;

    /// <summary>Escrow-audit action when a leg cannot follow the verdict (settlement is refused).</summary>
    public const string MismatchAuditAction = "dispute_verdict_mismatch";

    /// <summary>Escrow-audit action when a leg could follow but its write failed (repaired on a later run).</summary>
    public const string PropagationFailedAuditAction = "dispute_verdict_propagation_failed";

    /// <summary>Escrow-audit action when the settlement failed closed (<see cref="DisputeVerdictMirrorResult.BlocksSettlement"/>).</summary>
    public const string SettlementBlockedAuditAction = "dispute_settlement_blocked";

    /// <summary>
    /// The deterministic escrow-audit timestamp for a failed mirror: the verdict's
    /// <c>disputeResolvedAt</c> plus one tick (mismatch), two (propagation) or three (settlement
    /// blocked). Written with
    /// AppendIfAbsentAsync, so there is one row per escrow and action however often the hourly
    /// sweep retries, and it sorts right after the verdict's own row. Falls back to the start of
    /// the current UTC day when the time cannot be read.
    /// </summary>
    public static string VerdictAuditTimestamp(string? disputeResolvedAt, string action)
    {
        var at = DateTime.TryParse(disputeResolvedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed.ToUniversalTime()
            : DateTime.UtcNow.Date;
        var slot = action switch
        {
            MismatchAuditAction => 1,
            SettlementBlockedAuditAction => 3,
            _ => 2
        };
        return at.AddTicks(slot).ToString("o");
    }

    /// <summary>Order statuses that may be advanced to <c>delivered</c> when the delivery already is.</summary>
    private static readonly HashSet<string> LogisticsOrderStatuses = new(StringComparer.Ordinal)
    {
        "accepted", "pickup_confirmed", "in_transit"
    };

    private readonly IOrderRepository? _orderRepo;
    private readonly IDeliveryRepository? _deliveryRepo;
    private readonly ITransactionAuditRepository? _txAuditRepo;
    private readonly ICropRepository? _cropRepo;

    public DisputeVerdictMirror(
        IOrderRepository? orderRepo, IDeliveryRepository? deliveryRepo, ITransactionAuditRepository? txAuditRepo,
        ICropRepository? cropRepo = null)
    {
        _orderRepo = orderRepo;
        _deliveryRepo = deliveryRepo;
        _txAuditRepo = txAuditRepo;
        _cropRepo = cropRepo;
    }

    /// <param name="escrow">The escrow row (reads <c>escrowId</c>, <c>orderId</c>, <c>deliveryId</c>, <c>disputedBy</c>).</param>
    /// <param name="resolution"><c>farmer_wins</c> or <c>buyer_wins</c>.</param>
    /// <param name="resolvedAt">The verdict time stamped on the delivery.</param>
    /// <param name="cancelBeforePickup">
    /// True only for the refund settlement of a <c>buyer_wins</c> verdict: an order before pickup
    /// is cancelled (and its pending delivery, and its stock restored) instead of deferred.
    /// </param>
    public async Task<DisputeVerdictMirrorResult> ApplyAsync(
        Dictionary<string, object> escrow, string resolution, string resolvedAt, ILambdaContext? context = null,
        bool cancelBeforePickup = false)
    {
        var escrowId = Str(escrow, "escrowId") ?? "";
        var orderId = Str(escrow, "orderId");
        var deliveryId = Str(escrow, "deliveryId");
        Dictionary<string, object>? order = null;
        Dictionary<string, object>? delivery = null;

        try
        {
            if (string.IsNullOrEmpty(orderId) && _deliveryRepo != null && !string.IsNullOrEmpty(deliveryId))
            {
                delivery = await _deliveryRepo.GetByIdAsync(deliveryId, consistentRead: true);
                orderId = delivery != null ? Str(delivery, "orderId") : null;
            }

            if (_orderRepo != null && !string.IsNullOrEmpty(orderId))
            {
                order = await _orderRepo.GetByIdAsync(orderId, consistentRead: true);

                // The order's active delivery is the current one: the escrow may still name an
                // earlier delivery that was cancelled before this one was created.
                var activeDeliveryId = order != null ? Str(order, "activeDeliveryId") : null;
                if (!string.IsNullOrEmpty(activeDeliveryId) && activeDeliveryId != deliveryId)
                {
                    deliveryId = activeDeliveryId;
                    delivery = null;
                }
            }

            if (delivery == null && _deliveryRepo != null && !string.IsNullOrEmpty(deliveryId))
            {
                delivery = await _deliveryRepo.GetByIdAsync(deliveryId, consistentRead: true);
            }
        }
        catch (Exception ex)
        {
            // Nothing is known about the order: the caller must not settle on this run.
            var reason = $"read:[{ex.GetType().Name}]";
            return Result(VerdictLegOutcome.Unknown, reason, VerdictLegOutcome.Unknown, reason, orderId, deliveryId, null);
        }

        // Decide both legs before any write.
        var cancelAllowed = cancelBeforePickup && resolution == "buyer_wins";
        // A delivery the escrow or order names but that could not be read counts as unknown, never as "none".
        var deliveryStatus = delivery != null ? Str(delivery, "status") : string.IsNullOrEmpty(deliveryId) ? null : "unknown";
        var (orderPlan, orderReason) = PlanOrder(order, orderId, deliveryStatus, resolution, cancelAllowed);
        var (deliveryPlan, deliveryReason) = PlanDelivery(delivery, deliveryId, resolution, orderPlan, cancelAllowed);
        if (IsRefusal(orderPlan) || IsRefusal(deliveryPlan))
        {
            return Result(
                IsWritePlan(orderPlan) ? VerdictLegOutcome.Skipped : orderPlan, orderReason,
                IsWritePlan(deliveryPlan) ? VerdictLegOutcome.Skipped : deliveryPlan, deliveryReason,
                orderId, deliveryId, order);
        }

        var transactionId = string.IsNullOrEmpty(orderId) ? escrowId : orderId;
        if (orderPlan == VerdictLegOutcome.Cancelled || deliveryPlan == VerdictLegOutcome.Cancelled
            || (orderPlan == VerdictLegOutcome.Closed && cancelAllowed))
        {
            return await CancelBeforePickupAsync(
                order, orderId, orderPlan, delivery, deliveryId, deliveryPlan, transactionId, context);
        }

        var (orderOutcome, orderFailure) = orderPlan == VerdictLegOutcome.Resolved
            ? await MoveOrderAsync(order!, orderId!, deliveryStatus, resolution, context)
            : (orderPlan, null);
        var (deliveryOutcome, deliveryFailure) = deliveryPlan == VerdictLegOutcome.Resolved
            ? await MoveDeliveryAsync(delivery!, deliveryId!, escrow, resolution, resolvedAt, orderPlan, transactionId, context)
            : (deliveryPlan, null);

        return Result(orderOutcome, orderFailure, deliveryOutcome, deliveryFailure, orderId, deliveryId, order);
    }

    /// <summary>
    /// ESC-03: flag an order whose verdict an admin settled over a mismatch (the override), with a
    /// version-checked write that leaves its status alone. Best-effort; false when the write did
    /// not land after three attempts.
    /// </summary>
    public async Task<bool> FlagOrderAsync(string orderId, Dictionary<string, object> fields)
    {
        if (_orderRepo == null)
        {
            return false;
        }

        try
        {
            for (var attempt = 0; attempt < MaxOrderAttempts; attempt++)
            {
                var order = await _orderRepo.GetByIdAsync(orderId, consistentRead: true);
                if (order == null)
                {
                    return false;
                }

                var update = await _orderRepo.UpdateAsync(orderId, fields, Int(order, "version"));
                if (update.IsSuccess)
                {
                    return true;
                }

                if (update.Error?.HttpStatus != 409)
                {
                    return false;
                }
            }
        }
        catch (Exception)
        {
            // The caller logs that the flag is missing.
        }

        return false;
    }

    private (VerdictLegOutcome Plan, string? Reason) PlanOrder(
        Dictionary<string, object>? order, string? orderId, string? deliveryStatus, string resolution, bool cancelAllowed)
    {
        if (_orderRepo == null || string.IsNullOrEmpty(orderId))
        {
            return (VerdictLegOutcome.None, null);
        }

        if (order == null)
        {
            return (VerdictLegOutcome.Missing, "missing");
        }

        var status = Str(order, "status") ?? "";
        if (status == "dispute_resolved")
        {
            return (VerdictLegOutcome.AlreadyResolved, null);
        }

        if (status is "disputed" or "delivered"
            || (LogisticsOrderStatuses.Contains(status) && deliveryStatus is "delivered" or "disputed"))
        {
            return (VerdictLegOutcome.Resolved, null);
        }

        if (resolution == "buyer_wins")
        {
            if (status is "cancelled" or "rejected")
            {
                return (VerdictLegOutcome.Closed, null);
            }

            // Before pickup: nothing has left the farm, so the buyer is refunded and the order ends.
            if (status == "funded" || (status == "accepted" && deliveryStatus is null or "pending" or "cancelled"))
            {
                return cancelAllowed ? (VerdictLegOutcome.Cancelled, null) : (VerdictLegOutcome.Deferred, null);
            }
        }

        // Goods on the move (pickup_confirmed, in_transit, or a delivery picked up), or a
        // farmer_wins before delivery: the verdict waits for the order.
        return (VerdictLegOutcome.Mismatch, $"mismatch:{status}");
    }

    private (VerdictLegOutcome Plan, string? Reason) PlanDelivery(
        Dictionary<string, object>? delivery, string? deliveryId, string resolution,
        VerdictLegOutcome orderPlan, bool cancelAllowed)
    {
        if (_deliveryRepo == null || string.IsNullOrEmpty(deliveryId))
        {
            return (VerdictLegOutcome.None, null);
        }

        if (delivery == null)
        {
            return (VerdictLegOutcome.Missing, "missing");
        }

        var status = Str(delivery, "status") ?? "";
        if (orderPlan is VerdictLegOutcome.Cancelled or VerdictLegOutcome.Deferred or VerdictLegOutcome.Closed)
        {
            // buyer_wins on an order that ends before pickup: the delivery never left the farm.
            return status switch
            {
                "cancelled" => (VerdictLegOutcome.Closed, null),
                "pending" => cancelAllowed ? (VerdictLegOutcome.Cancelled, null) : (VerdictLegOutcome.Deferred, null),
                _ => (VerdictLegOutcome.Mismatch, $"mismatch:{status}")
            };
        }

        var recorded = Str(delivery, "disputeResolution");
        return status switch
        {
            "disputed" when recorded == null => (VerdictLegOutcome.Resolved, null),
            "disputed" when recorded == resolution => (VerdictLegOutcome.AlreadyResolved, null),
            "disputed" => (VerdictLegOutcome.Mismatch, $"verdict:{recorded}"),
            "delivered" => (VerdictLegOutcome.Resolved, null),
            _ => (VerdictLegOutcome.Mismatch, $"mismatch:{status}")
        };
    }

    /// <summary>The order hops, from wherever the order is now, retried on version conflicts.</summary>
    private async Task<(VerdictLegOutcome, string?)> MoveOrderAsync(
        Dictionary<string, object> order, string orderId, string? deliveryStatus, string resolution, ILambdaContext? context)
    {
        try
        {
            // At most three hops; conflicts are counted on their own, so a conflict on the last
            // hop still gets its retries.
            var conflicts = 0;
            for (var step = 0; step < MaxOrderHops + MaxOrderAttempts; step++)
            {
                var (plan, reason) = PlanOrder(order, orderId, deliveryStatus, resolution, cancelAllowed: false);
                if (plan != VerdictLegOutcome.Resolved)
                {
                    // A concurrent writer finished the job, or moved the order somewhere else.
                    return (plan, reason);
                }

                var status = Str(order, "status") ?? "";
                var next = status switch
                {
                    "delivered" => "disputed",
                    "disputed" => "dispute_resolved",
                    _ => "delivered"
                };

                var update = await _orderRepo!.UpdateAsync(
                    orderId, new Dictionary<string, object> { { "status", next } }, Int(order, "version"));
                if (update.IsSuccess)
                {
                    await TransactionAuditWriter.EmitAsync(_txAuditRepo, context, orderId, "order", orderId,
                        next switch { "disputed" => "dispute", "dispute_resolved" => "dispute_resolved", _ => "order_updated" },
                        status, next, AuditContext.ForSystem(), metadata: $"resolution={resolution}");
                    if (next == "dispute_resolved")
                    {
                        return (VerdictLegOutcome.Resolved, null);
                    }

                    order = update.Value!;
                    continue;
                }

                if (update.Error?.HttpStatus != 409)
                {
                    return (VerdictLegOutcome.Failed, update.Error?.Code ?? "unknown");
                }

                if (++conflicts >= MaxOrderAttempts)
                {
                    return (VerdictLegOutcome.Conflict, "conflict");
                }

                var latest = await _orderRepo.GetByIdAsync(orderId, consistentRead: true);
                if (latest == null)
                {
                    return (VerdictLegOutcome.Missing, "missing");
                }

                order = latest;
            }

            return (VerdictLegOutcome.Conflict, "conflict");
        }
        catch (Exception ex)
        {
            return (VerdictLegOutcome.Failed, $"[{ex.GetType().Name}]");
        }
    }

    private async Task<(VerdictLegOutcome, string?)> MoveDeliveryAsync(
        Dictionary<string, object> delivery, string deliveryId, Dictionary<string, object> escrow,
        string resolution, string resolvedAt, VerdictLegOutcome orderPlan, string transactionId, ILambdaContext? context)
    {
        try
        {
            if (Str(delivery, "status") == "delivered"
                && await _deliveryRepo!.TryMarkDisputedAsync(deliveryId, Str(escrow, "disputedBy") ?? EscrowService.SystemActor, null))
            {
                await TransactionAuditWriter.EmitAsync(_txAuditRepo, context, transactionId, "delivery", deliveryId,
                    "dispute", "delivered", "disputed", AuditContext.ForSystem(), metadata: $"resolution={resolution}");
            }

            if (await _deliveryRepo!.TryRecordDisputeResolutionAsync(deliveryId, resolution, resolvedAt))
            {
                await TransactionAuditWriter.EmitAsync(_txAuditRepo, context, transactionId, "delivery", deliveryId,
                    "dispute_resolved", "disputed", "disputed", AuditContext.ForSystem(), metadata: $"resolution={resolution}");
                return (VerdictLegOutcome.Resolved, null);
            }

            // Lost to a concurrent stamp (same verdict: done), or the delivery moved on.
            var latest = await _deliveryRepo.GetByIdAsync(deliveryId, consistentRead: true);
            var (plan, reason) = PlanDelivery(latest, deliveryId, resolution, orderPlan, cancelAllowed: false);
            return plan == VerdictLegOutcome.Resolved ? (VerdictLegOutcome.Conflict, "conflict") : (plan, reason);
        }
        catch (Exception ex)
        {
            return (VerdictLegOutcome.Failed, $"[{ex.GetType().Name}]");
        }
    }

    /// <summary>
    /// <c>buyer_wins</c> before pickup, in the refund settlement: the pending delivery is cancelled
    /// first (so a pickup racing in leaves the order alone), then the order, then its listing
    /// stock is restored (idempotent per order). The refund follows; if it fails, the next run
    /// finds the order closed and only refunds.
    /// </summary>
    private async Task<DisputeVerdictMirrorResult> CancelBeforePickupAsync(
        Dictionary<string, object>? order, string? orderId, VerdictLegOutcome orderPlan,
        Dictionary<string, object>? delivery, string? deliveryId, VerdictLegOutcome deliveryPlan,
        string transactionId, ILambdaContext? context)
    {
        var deliveryOutcome = deliveryPlan;
        string? deliveryReason = null;
        if (deliveryPlan == VerdictLegOutcome.Cancelled)
        {
            try
            {
                if (await _deliveryRepo!.TryCancelPendingAsync(deliveryId!))
                {
                    await TransactionAuditWriter.EmitAsync(_txAuditRepo, context, transactionId, "delivery", deliveryId!,
                        "delivery_updated", "pending", "cancelled", AuditContext.ForSystem(),
                        metadata: "resolution=buyer_wins;reason=escrow_refunded");
                }
                else
                {
                    // Picked up in between (or already cancelled): decide again before touching the order.
                    var latest = await _deliveryRepo.GetByIdAsync(deliveryId!, consistentRead: true);
                    var latestStatus = latest != null ? Str(latest, "status") : null;
                    if (latestStatus != "cancelled")
                    {
                        return Result(
                            orderPlan == VerdictLegOutcome.Cancelled ? VerdictLegOutcome.Skipped : orderPlan, null,
                            latest == null ? VerdictLegOutcome.Missing : VerdictLegOutcome.Mismatch,
                            latest == null ? "missing" : $"mismatch:{latestStatus}", orderId, deliveryId, order);
                    }

                    deliveryOutcome = VerdictLegOutcome.Closed;
                }
            }
            catch (Exception ex)
            {
                // Nothing was cancelled: the refund must not run ahead of the order on this run.
                var reason = $"read:[{ex.GetType().Name}]";
                return Result(VerdictLegOutcome.Unknown, reason, VerdictLegOutcome.Unknown, reason, orderId, deliveryId, order);
            }
        }

        if (orderPlan == VerdictLegOutcome.Closed)
        {
            // Already cancelled (or rejected): make sure its stock is back before the refund, which
            // also retries a restore an earlier run could not finish. Idempotent per order.
            var restock = order != null ? await RestoreStockAsync(order, orderId!) : null;
            return restock != null
                ? Result(VerdictLegOutcome.Blocked, restock, deliveryOutcome, deliveryReason, orderId, deliveryId, order)
                : Result(orderPlan, null, deliveryOutcome, deliveryReason, orderId, deliveryId, order);
        }

        if (orderPlan != VerdictLegOutcome.Cancelled)
        {
            return Result(orderPlan, null, deliveryOutcome, deliveryReason, orderId, deliveryId, order);
        }

        var (orderOutcome, orderReason, cancelled) = await CancelOrderAsync(order!, orderId!, deliveryId, context);
        return Result(orderOutcome, orderReason, deliveryOutcome, deliveryReason, orderId, deliveryId, cancelled ?? order);
    }

    private async Task<(VerdictLegOutcome, string?, Dictionary<string, object>?)> CancelOrderAsync(
        Dictionary<string, object> order, string orderId, string? cancelledDeliveryId, ILambdaContext? context)
    {
        try
        {
            for (var conflicts = 0; ;)
            {
                var status = Str(order, "status") ?? "";
                if (status is "cancelled" or "rejected")
                {
                    return (VerdictLegOutcome.Closed, null, order);
                }

                if (status is not ("funded" or "accepted"))
                {
                    // Accepted and picked up in between: the refund waits for the order.
                    return (VerdictLegOutcome.Mismatch, $"mismatch:{status}", order);
                }

                var update = await _orderRepo!.UpdateAsync(orderId, new Dictionary<string, object>
                {
                    { "status", "cancelled" },
                    { "cancelledAt", DateTime.UtcNow.ToString("o") },
                    { "cancelReason", "escrow_refunded" }
                }, Int(order, "version"));
                if (update.IsSuccess)
                {
                    await TransactionAuditWriter.EmitAsync(_txAuditRepo, context, orderId, "order", orderId,
                        "order_cancelled", status, "cancelled", AuditContext.ForSystem(),
                        metadata: "resolution=buyer_wins;cancelReason=escrow_refunded");
                    await ReleaseDeliverySlotAsync(orderId, cancelledDeliveryId);

                    // The order is cancelled; a stock restore that did not land blocks this run's
                    // refund, and the next run (order now closed) retries the restore first.
                    var restock = await RestoreStockAsync(order, orderId);
                    return restock != null
                        ? (VerdictLegOutcome.Blocked, restock, update.Value)
                        : (VerdictLegOutcome.Cancelled, null, update.Value);
                }

                // The cancel did not land: the order may still be live, so the refund must not run.
                if (update.Error?.HttpStatus != 409)
                {
                    return (VerdictLegOutcome.Blocked, $"cancel:{update.Error?.Code ?? "unknown"}", order);
                }

                if (++conflicts >= MaxOrderAttempts)
                {
                    return (VerdictLegOutcome.Blocked, "cancel:conflict", order);
                }

                var latest = await _orderRepo.GetByIdAsync(orderId, consistentRead: true);
                if (latest == null)
                {
                    return (VerdictLegOutcome.Missing, "missing", null);
                }

                order = latest;
            }
        }
        catch (Exception ex)
        {
            // Unknown whether the cancel landed: fail closed, the next run re-reads the order.
            return (VerdictLegOutcome.Blocked, $"cancel:[{ex.GetType().Name}]", order);
        }
    }

    /// <summary>Free the order's delivery slot for the cancelled delivery. Best-effort: the order is closed either way.</summary>
    private async Task ReleaseDeliverySlotAsync(string orderId, string? deliveryId)
    {
        if (string.IsNullOrEmpty(deliveryId))
        {
            return;
        }

        try
        {
            await _orderRepo!.ReleaseDeliverySlotAsync(orderId, deliveryId);
        }
        catch (Exception ex)
        {
            Console.WriteLine(
                $"DISPUTE_VERDICT_SLOT_RELEASE_FAILED orderId={MoneyAlarmLog.LogId(orderId)} deliveryId={MoneyAlarmLog.LogId(deliveryId)}: [{ex.GetType().Name}]");
        }
    }

    /// <summary>
    /// The cancelled order's quintals go back on its listing (SIM-13), idempotent per order.
    /// Returns null when the stock is back (or there is nothing to give back), or the reason it
    /// is not, which blocks the refund on this run.
    /// </summary>
    private async Task<string?> RestoreStockAsync(Dictionary<string, object> order, string orderId)
    {
        var listingId = Str(order, "listingId");
        var quintals = double.TryParse(Str(order, "quantity"), NumberStyles.Any, CultureInfo.InvariantCulture, out var kg)
            ? kg / KgPerQuintal
            : 0.0;
        if (_cropRepo == null || string.IsNullOrEmpty(listingId) || quintals <= 0)
        {
            return null;
        }

        string reason;
        try
        {
            var result = await _cropRepo.RestoreStockAsync(listingId, orderId, quintals);
            if (result is StockRestoreResult.Restored or StockRestoreResult.AlreadyRestored or StockRestoreResult.ListingNotFound)
            {
                return null;
            }

            reason = "restock:conflict";
        }
        catch (Exception ex)
        {
            reason = $"restock:[{ex.GetType().Name}]";
        }

        Console.WriteLine(
            $"ORDER_STOCK_RESTORE_FAILED orderId={MoneyAlarmLog.LogId(orderId)} listingId={MoneyAlarmLog.LogId(listingId)} reason={MoneyAlarmLog.LogText(reason)}");
        return reason;
    }

    private static bool IsRefusal(VerdictLegOutcome plan)
        => plan is VerdictLegOutcome.Mismatch or VerdictLegOutcome.Missing;

    private static bool IsWritePlan(VerdictLegOutcome plan)
        => plan is VerdictLegOutcome.Resolved or VerdictLegOutcome.Cancelled;

    private static DisputeVerdictMirrorResult Result(
        VerdictLegOutcome order, string? orderReason, VerdictLegOutcome delivery, string? deliveryReason,
        string? orderId, string? deliveryId, Dictionary<string, object>? orderItem)
    {
        var failures = new List<string>();
        if (IsFailure(order))
        {
            failures.Add($"order:{orderReason ?? "unknown"}");
        }

        if (IsFailure(delivery))
        {
            failures.Add($"delivery:{deliveryReason ?? "unknown"}");
        }

        return new DisputeVerdictMirrorResult
        {
            Order = order,
            Delivery = delivery,
            OrderId = orderId,
            DeliveryId = deliveryId,
            OrderItem = orderItem,
            FailedLegs = failures.Count > 0 ? string.Join(",", failures) : null
        };
    }

    private static bool IsFailure(VerdictLegOutcome outcome)
        => outcome is VerdictLegOutcome.Mismatch or VerdictLegOutcome.Missing or VerdictLegOutcome.Conflict
            or VerdictLegOutcome.Failed or VerdictLegOutcome.Unknown or VerdictLegOutcome.Blocked;

    private static string? Str(Dictionary<string, object> item, string key)
        => item.TryGetValue(key, out var value) && value != null ? value.ToString() : null;

    private static int Int(Dictionary<string, object> item, string key)
        => item.TryGetValue(key, out var value) && int.TryParse(value?.ToString(), out var parsed) ? parsed : 1;
}
