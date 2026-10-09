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

    /// <summary>The leg is in a status the verdict cannot follow from (or carries another verdict).</summary>
    Mismatch,

    /// <summary>The escrow names a row that does not exist.</summary>
    Missing,

    /// <summary>Version conflicts on every attempt.</summary>
    Conflict,

    /// <summary>A write failed for another reason.</summary>
    Failed,

    /// <summary>Could follow, but nothing was written because the other leg cannot.</summary>
    Skipped
}

/// <summary>What <see cref="DisputeVerdictMirror.ApplyAsync"/> did to the order and the delivery.</summary>
public sealed record DisputeVerdictMirrorResult
{
    public required VerdictLegOutcome Order { get; init; }
    public required VerdictLegOutcome Delivery { get; init; }
    public string? OrderId { get; init; }
    public string? DeliveryId { get; init; }

    /// <summary>
    /// <c>order:&lt;reason&gt;,delivery:&lt;reason&gt;</c> for every leg that did not reach the
    /// verdict; null when both did or had nothing to do.
    /// </summary>
    public string? FailedLegs { get; init; }

    /// <summary>True when a leg was refused before any write (FRD-03 refuses the refund then).</summary>
    public bool IsMismatch => Order is VerdictLegOutcome.Mismatch or VerdictLegOutcome.Missing
                              || Delivery is VerdictLegOutcome.Mismatch or VerdictLegOutcome.Missing;
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
/// transaction-audit row; a version conflict re-reads (strongly consistent) and retries, three
/// attempts in all. Delivery: a <c>delivered</c> delivery is marked <c>disputed</c>, then the
/// verdict is stamped on it (<c>disputeResolution</c>, <c>disputeResolvedAt</c>); the delivery
/// status stays <c>disputed</c>.
///
/// Both legs are decided before anything is written: when either one cannot follow the
/// verdict (<see cref="DisputeVerdictMirrorResult.IsMismatch"/>), nothing is written. Never
/// throws; the caller logs and audits <see cref="DisputeVerdictMirrorResult.FailedLegs"/>.
/// </summary>
public sealed class DisputeVerdictMirror
{
    private const int MaxOrderAttempts = 3;

    /// <summary>Order statuses that may be advanced to <c>delivered</c> when the delivery already is.</summary>
    private static readonly HashSet<string> LogisticsOrderStatuses = new(StringComparer.Ordinal)
    {
        "accepted", "pickup_confirmed", "in_transit"
    };

    private readonly IOrderRepository? _orderRepo;
    private readonly IDeliveryRepository? _deliveryRepo;
    private readonly ITransactionAuditRepository? _txAuditRepo;

    public DisputeVerdictMirror(
        IOrderRepository? orderRepo, IDeliveryRepository? deliveryRepo, ITransactionAuditRepository? txAuditRepo)
    {
        _orderRepo = orderRepo;
        _deliveryRepo = deliveryRepo;
        _txAuditRepo = txAuditRepo;
    }

    /// <param name="escrow">The escrow row (reads <c>escrowId</c>, <c>orderId</c>, <c>deliveryId</c>, <c>disputedBy</c>).</param>
    /// <param name="resolution"><c>farmer_wins</c> or <c>buyer_wins</c>.</param>
    /// <param name="resolvedAt">The verdict time stamped on the delivery.</param>
    public async Task<DisputeVerdictMirrorResult> ApplyAsync(
        Dictionary<string, object> escrow, string resolution, string resolvedAt, ILambdaContext? context = null)
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
                if (string.IsNullOrEmpty(deliveryId) && order != null)
                {
                    deliveryId = Str(order, "activeDeliveryId");
                }
            }

            if (delivery == null && _deliveryRepo != null && !string.IsNullOrEmpty(deliveryId))
            {
                delivery = await _deliveryRepo.GetByIdAsync(deliveryId, consistentRead: true);
            }
        }
        catch (Exception ex)
        {
            return Result(VerdictLegOutcome.Failed, $"[{ex.GetType().Name}]", VerdictLegOutcome.Failed,
                $"[{ex.GetType().Name}]", orderId, deliveryId);
        }

        // Decide both legs before any write.
        var deliveryStatus = delivery != null ? Str(delivery, "status") : null;
        var (orderPlan, orderReason) = PlanOrder(order, orderId, deliveryStatus);
        var (deliveryPlan, deliveryReason) = PlanDelivery(delivery, deliveryId, resolution);
        if (IsRefusal(orderPlan) || IsRefusal(deliveryPlan))
        {
            return Result(
                orderPlan == VerdictLegOutcome.Resolved ? VerdictLegOutcome.Skipped : orderPlan, orderReason,
                deliveryPlan == VerdictLegOutcome.Resolved ? VerdictLegOutcome.Skipped : deliveryPlan, deliveryReason,
                orderId, deliveryId);
        }

        var transactionId = string.IsNullOrEmpty(orderId) ? escrowId : orderId;
        var (orderOutcome, orderFailure) = orderPlan == VerdictLegOutcome.Resolved
            ? await MoveOrderAsync(order!, orderId!, deliveryStatus, resolution, context)
            : (orderPlan, null);
        var (deliveryOutcome, deliveryFailure) = deliveryPlan == VerdictLegOutcome.Resolved
            ? await MoveDeliveryAsync(delivery!, deliveryId!, escrow, resolution, resolvedAt, transactionId, context)
            : (deliveryPlan, null);

        return Result(orderOutcome, orderFailure, deliveryOutcome, deliveryFailure, orderId, deliveryId);
    }

    private (VerdictLegOutcome Plan, string? Reason) PlanOrder(
        Dictionary<string, object>? order, string? orderId, string? deliveryStatus)
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
        return status switch
        {
            "dispute_resolved" => (VerdictLegOutcome.AlreadyResolved, null),
            "disputed" or "delivered" => (VerdictLegOutcome.Resolved, null),
            _ when LogisticsOrderStatuses.Contains(status) && deliveryStatus is "delivered" or "disputed"
                => (VerdictLegOutcome.Resolved, null),
            _ => (VerdictLegOutcome.Mismatch, $"mismatch:{status}")
        };
    }

    private (VerdictLegOutcome Plan, string? Reason) PlanDelivery(
        Dictionary<string, object>? delivery, string? deliveryId, string resolution)
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
            for (var attempt = 1; ; attempt++)
            {
                var (plan, reason) = PlanOrder(order, orderId, deliveryStatus);
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

                if (attempt >= MaxOrderAttempts)
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
        }
        catch (Exception ex)
        {
            return (VerdictLegOutcome.Failed, $"[{ex.GetType().Name}]");
        }
    }

    private async Task<(VerdictLegOutcome, string?)> MoveDeliveryAsync(
        Dictionary<string, object> delivery, string deliveryId, Dictionary<string, object> escrow,
        string resolution, string resolvedAt, string transactionId, ILambdaContext? context)
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
            var (plan, reason) = PlanDelivery(latest, deliveryId, resolution);
            return plan == VerdictLegOutcome.Resolved ? (VerdictLegOutcome.Conflict, "conflict") : (plan, reason);
        }
        catch (Exception ex)
        {
            return (VerdictLegOutcome.Failed, $"[{ex.GetType().Name}]");
        }
    }

    private static bool IsRefusal(VerdictLegOutcome plan)
        => plan is VerdictLegOutcome.Mismatch or VerdictLegOutcome.Missing or VerdictLegOutcome.Failed;

    private static DisputeVerdictMirrorResult Result(
        VerdictLegOutcome order, string? orderReason, VerdictLegOutcome delivery, string? deliveryReason,
        string? orderId, string? deliveryId)
    {
        var failures = new List<string>();
        if (order is not (VerdictLegOutcome.None or VerdictLegOutcome.Resolved or VerdictLegOutcome.AlreadyResolved
            or VerdictLegOutcome.Skipped))
        {
            failures.Add($"order:{orderReason ?? "unknown"}");
        }

        if (delivery is not (VerdictLegOutcome.None or VerdictLegOutcome.Resolved or VerdictLegOutcome.AlreadyResolved
            or VerdictLegOutcome.Skipped))
        {
            failures.Add($"delivery:{deliveryReason ?? "unknown"}");
        }

        return new DisputeVerdictMirrorResult
        {
            Order = order,
            Delivery = delivery,
            OrderId = orderId,
            DeliveryId = deliveryId,
            FailedLegs = failures.Count > 0 ? string.Join(",", failures) : null
        };
    }

    private static string? Str(Dictionary<string, object> item, string key)
        => item.TryGetValue(key, out var value) && value != null ? value.ToString() : null;

    private static int Int(Dictionary<string, object> item, string key)
        => item.TryGetValue(key, out var value) && int.TryParse(value?.ToString(), out var parsed) ? parsed : 1;
}
