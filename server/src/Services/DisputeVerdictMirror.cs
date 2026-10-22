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

    /// <summary>
    /// FUP-16: <see cref="DisputeVerdictMirror.ApplyDispositionAsync"/> found the legs already following the
    /// verdict (they moved since the caller decided), so it wrote nothing; the caller mirrors the plain verdict.
    /// </summary>
    public bool DispositionNotNeeded { get; init; }

    /// <summary>The escrow-audit action that records <see cref="FailedLegs"/>.</summary>
    public string AuditAction => IsMismatch
        ? DisputeVerdictMirror.MismatchAuditAction
        : DisputeVerdictMirror.PropagationFailedAuditAction;
}

/// <summary>
/// FUP-16: what an admin chooses for a verdict whose order or delivery cannot follow it as it is
/// (escrow-protocol.md 1.21.0). Each value belongs to one verdict.
/// </summary>
public static class DisputeDisposition
{
    /// <summary><c>buyer_wins</c>, goods moving: the delivery is cancelled and the goods go back to the farmer.</summary>
    public const string ReturnToFarmer = "return_to_farmer";

    /// <summary><c>buyer_wins</c>, goods moving: the goods count as delivered and the buyer keeps them.</summary>
    public const string DeliveredAnyway = "delivered_anyway";

    /// <summary><c>farmer_wins</c> after pickup: the delivery is cancelled and the farmer is paid now.</summary>
    public const string SettleNow = "settle_now";

    /// <summary><c>farmer_wins</c> before delivery: nothing moves; the farmer is paid once the order is delivered.</summary>
    public const string WaitForDelivery = "wait_for_delivery";

    /// <summary>The verdict a disposition belongs to; null for an unknown value.</summary>
    public static string? VerdictOf(string? disposition) => disposition switch
    {
        ReturnToFarmer or DeliveredAnyway => "buyer_wins",
        SettleNow or WaitForDelivery => "farmer_wins",
        _ => null
    };

    /// <summary>The escrow-audit and transaction-audit action that records the admin's choice.</summary>
    public static string AuditAction(string disposition) => disposition switch
    {
        ReturnToFarmer => "dispute_resolved_goods_returned",
        DeliveredAnyway => "dispute_resolved_delivered_anyway",
        SettleNow => "dispute_resolved_settled_early",
        _ => "dispute_resolved_waiting_for_delivery"
    };

    /// <summary>The status the delivery ends at; null when the disposition moves nothing.</summary>
    public static string? DeliveryTarget(string disposition) => disposition switch
    {
        ReturnToFarmer or SettleNow => "cancelled",
        DeliveredAnyway => "disputed",
        _ => null
    };

    internal static bool IsAuditAction(string action) => action is "dispute_resolved_goods_returned"
        or "dispute_resolved_delivered_anyway" or "dispute_resolved_settled_early" or "dispute_resolved_waiting_for_delivery";
}

/// <summary>FUP-16: the escrow row attributes that record a disposition (escrow-protocol.md 1.21.0).</summary>
public static class DisputeDispositionFields
{
    public const string Disposition = "disputeDisposition";
    public const string Reason = "disputeDispositionReason";
    public const string By = "disputeDispositionBy";
    public const string At = "disputeDispositionAt";
}

/// <summary>FUP-16: how the legs behind a verdict stand, decided before anything is written.</summary>
public enum DispositionCase
{
    /// <summary>The order and the delivery can follow the verdict: no disposition is needed.</summary>
    NotNeeded,

    /// <summary><c>buyer_wins</c> with the goods moving: <c>return_to_farmer</c> or <c>delivered_anyway</c>.</summary>
    GoodsMoving,

    /// <summary><c>farmer_wins</c> before delivery: <c>wait_for_delivery</c>, or <c>settle_now</c> once picked up.</summary>
    BeforeDelivery,

    /// <summary>A leg cannot follow and no disposition fits (a missing row, another verdict, a closed order, ...).</summary>
    NoFit,

    /// <summary>A read failed: nothing is known, so nothing may be written.</summary>
    Unknown
}

/// <summary>FUP-16: the result of <see cref="DisputeVerdictMirror.PlanDispositionAsync"/>.</summary>
/// <param name="Case">Which choices the admin has.</param>
/// <param name="GoodsHandedOver">The goods left the farm (picked up, or a leg already closed by a disposition).</param>
/// <param name="ClosedDeliveryStatus">The status of a delivery an earlier run of a disposition already closed, else null.</param>
/// <param name="Started">An earlier run already closed a leg (the delivery, or the order at <c>dispute_resolved</c>).</param>
/// <param name="Legs">The legs that cannot follow, as <c>order:&lt;reason&gt;,delivery:&lt;reason&gt;</c>.</param>
public sealed record DispositionPlan(
    DispositionCase Case, bool GoodsHandedOver, string? ClosedDeliveryStatus, bool Started, string? Legs)
{
    /// <summary>Whether <paramref name="disposition"/> resolves this case.</summary>
    public bool Fits(string disposition)
    {
        var fitsCase = disposition switch
        {
            DisputeDisposition.ReturnToFarmer or DisputeDisposition.DeliveredAnyway => Case == DispositionCase.GoodsMoving,
            // The farmer keeps the money for goods already handed over, never for goods still at the farm.
            DisputeDisposition.SettleNow => Case == DispositionCase.BeforeDelivery && GoodsHandedOver,
            // Waiting makes no sense once a leg was closed by another choice.
            DisputeDisposition.WaitForDelivery => Case == DispositionCase.BeforeDelivery && !Started,
            _ => false
        };
        return fitsCase
               && (ClosedDeliveryStatus == null || ClosedDeliveryStatus == DisputeDisposition.DeliveryTarget(disposition));
    }
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
    /// blocked); a disposition row (FUP-16) is the choice's <c>disputeDispositionAt</c> plus four. Written with
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
            // FUP-16: a disposition row sorts after the verdict's own rows, at its own choice time.
            _ when DisputeDisposition.IsAuditAction(action) => 4,
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
        var (orderId, deliveryId, order, delivery, readError) = await ReadLegsAsync(escrow);
        if (readError != null)
        {
            // Nothing is known about the order: the caller must not settle on this run.
            return Result(VerdictLegOutcome.Unknown, readError, VerdictLegOutcome.Unknown, readError, orderId, deliveryId, null);
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
                order, orderId, orderPlan, delivery, deliveryId, deliveryPlan, transactionId, context, VerdictAuditTag);
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
    /// ESC-09 Part B: before an admin refunds a plain <c>escrowed</c> order escrow, end an order that never
    /// got going, as a <c>buyer_wins</c> refund does: an order <c>funded</c>, or <c>accepted</c> with no
    /// delivery or a <c>pending</c> one, has its pending delivery cancelled first, then the order
    /// (<c>cancelReason: escrow_refunded</c>), its slot freed and its stock restored. Returns the order leg
    /// as <see cref="VerdictLegOutcome.Cancelled"/>, <see cref="VerdictLegOutcome.Blocked"/> (a write the
    /// refund depends on did not land), <see cref="VerdictLegOutcome.Unknown"/> (a read failed),
    /// <see cref="VerdictLegOutcome.Mismatch"/> (the goods are moving or the order is anything else) or
    /// <see cref="VerdictLegOutcome.None"/> (no order). Never moves an order toward a dispute. Never throws.
    /// </summary>
    public async Task<DisputeVerdictMirrorResult> CancelBeforeRefundAsync(
        Dictionary<string, object> escrow, ILambdaContext? context = null)
    {
        var escrowId = Str(escrow, "escrowId") ?? "";
        var (orderId, deliveryId, order, delivery, readError) = await ReadLegsAsync(escrow);
        if (readError != null)
        {
            return Result(VerdictLegOutcome.Unknown, readError, VerdictLegOutcome.Unknown, readError, orderId, deliveryId, null);
        }

        if (_orderRepo == null || string.IsNullOrEmpty(orderId))
        {
            return Result(VerdictLegOutcome.None, null, VerdictLegOutcome.None, null, orderId, deliveryId, null);
        }

        if (order == null)
        {
            return Result(VerdictLegOutcome.Missing, "missing", VerdictLegOutcome.None, null, orderId, deliveryId, null);
        }

        var status = Str(order, "status") ?? "";
        var deliveryStatus = delivery != null ? Str(delivery, "status") : string.IsNullOrEmpty(deliveryId) ? null : "unknown";
        if (!(status == "funded" || (status == "accepted" && deliveryStatus is null or "pending" or "cancelled")))
        {
            return Result(VerdictLegOutcome.Mismatch, $"mismatch:{status}", VerdictLegOutcome.None, null, orderId, deliveryId, order);
        }

        var (deliveryPlan, deliveryReason) = PlanDelivery(delivery, deliveryId, "buyer_wins", VerdictLegOutcome.Cancelled, cancelAllowed: true);
        if (IsRefusal(deliveryPlan))
        {
            return Result(VerdictLegOutcome.Skipped, null, deliveryPlan, deliveryReason, orderId, deliveryId, order);
        }

        var transactionId = string.IsNullOrEmpty(orderId) ? escrowId : orderId;
        return await CancelBeforePickupAsync(order, orderId, VerdictLegOutcome.Cancelled, delivery, deliveryId, deliveryPlan,
            transactionId, context, AdminRefundAuditTag);
    }

    /// <summary>Order statuses a disposition may close: the order is live and not delivered yet.</summary>
    private static readonly HashSet<string> PreDeliveryOrderStatuses = new(StringComparer.Ordinal)
    {
        "funded", "accepted", "pickup_confirmed", "in_transit"
    };

    /// <summary>
    /// FUP-16: decide, without writing, whether the order and the delivery can follow
    /// <paramref name="resolution"/> as they are (<see cref="DispositionCase.NotNeeded"/>) and, when they
    /// cannot, which admin disposition resolves it. Reads strongly consistent, like <see cref="ApplyAsync"/>.
    /// Never throws.
    /// </summary>
    public async Task<DispositionPlan> PlanDispositionAsync(Dictionary<string, object> escrow, string resolution)
        => (await ReadDispositionAsync(escrow, resolution)).Plan;

    /// <summary>
    /// FUP-16: apply an admin disposition to the legs behind a verdict, as a system actor. The legs are
    /// decided first (<see cref="PlanDispositionAsync"/>); a disposition that does not fit writes nothing
    /// and comes back as a <see cref="VerdictLegOutcome.Mismatch"/>. Then the delivery is closed
    /// (<c>cancelled</c> with <c>cancelReason</c>, or <c>disputed</c> for <c>delivered_anyway</c>, the
    /// verdict stamped on it), and only then the order moves to <c>dispute_resolved</c>, so a handoff
    /// racing in never leaves a closed order behind a live delivery. Each write is conditional on the
    /// status it was decided on and has one transaction-audit row. A leg already closed by an earlier run
    /// is left as it is, so a replay repairs a run that stopped half way. <c>wait_for_delivery</c> writes
    /// nothing. Listing stock is never restored. Never throws.
    /// </summary>
    public async Task<DisputeVerdictMirrorResult> ApplyDispositionAsync(
        Dictionary<string, object> escrow, string resolution, string disposition, string resolvedAt,
        ILambdaContext? context = null)
    {
        var read = await ReadDispositionAsync(escrow, resolution);
        var plan = read.Plan;
        if (plan.Case == DispositionCase.Unknown)
        {
            return Result(VerdictLegOutcome.Unknown, plan.Legs, VerdictLegOutcome.Unknown, plan.Legs, read.OrderId, read.DeliveryId, null);
        }

        if (plan.Case == DispositionCase.NotNeeded)
        {
            // The legs follow the verdict now: nothing to apply, the plain verdict mirror does the rest.
            return Result(VerdictLegOutcome.None, null, VerdictLegOutcome.None, null, read.OrderId, read.DeliveryId, read.Order)
                with { DispositionNotNeeded = true };
        }

        if (!plan.Fits(disposition))
        {
            // Nothing that fits (the legs moved since the caller decided): nothing written.
            return Result(VerdictLegOutcome.Mismatch, $"disposition:{disposition}",
                VerdictLegOutcome.None, null, read.OrderId, read.DeliveryId, read.Order);
        }

        var target = DisputeDisposition.DeliveryTarget(disposition);
        if (target == null)
        {
            return Result(VerdictLegOutcome.None, null, VerdictLegOutcome.None, null, read.OrderId, read.DeliveryId, read.Order);
        }

        var escrowId = Str(escrow, "escrowId") ?? "";
        var transactionId = string.IsNullOrEmpty(read.OrderId) ? escrowId : read.OrderId;
        var metadata = $"resolution={resolution};disposition={disposition}";
        try
        {
            var (deliveryOutcome, deliveryReason) = await CloseDeliveryAsync(
                read.Delivery, read.DeliveryId, target, resolution, disposition, resolvedAt, transactionId, metadata, context);
            if (deliveryOutcome is not (VerdictLegOutcome.None or VerdictLegOutcome.Resolved or VerdictLegOutcome.AlreadyResolved))
            {
                // The delivery moved on (a handoff won the race): the order is left alone.
                return Result(read.Order != null ? VerdictLegOutcome.Skipped : VerdictLegOutcome.None, null,
                    deliveryOutcome, deliveryReason, read.OrderId, read.DeliveryId, read.Order);
            }

            var (orderOutcome, orderReason, orderItem) = await CloseOrderAsync(read.Order, read.OrderId, metadata, context);
            return Result(orderOutcome, orderReason, deliveryOutcome, deliveryReason, read.OrderId, read.DeliveryId, orderItem ?? read.Order);
        }
        catch (Exception ex)
        {
            // Unknown how far it got; every write is conditional, so the replay finishes the job.
            var reason = $"[{ex.GetType().Name}]";
            return Result(VerdictLegOutcome.Failed, reason, VerdictLegOutcome.Failed, reason, read.OrderId, read.DeliveryId, read.Order);
        }
    }

    private sealed record DispositionRead(
        DispositionPlan Plan, string? OrderId, string? DeliveryId,
        Dictionary<string, object>? Order, Dictionary<string, object>? Delivery);

    private async Task<DispositionRead> ReadDispositionAsync(Dictionary<string, object> escrow, string resolution)
    {
        var (orderId, deliveryId, order, delivery, readError) = await ReadLegsAsync(escrow);
        if (readError != null)
        {
            return new DispositionRead(
                new DispositionPlan(DispositionCase.Unknown, false, null, false, readError), orderId, deliveryId, null, null);
        }

        var deliveryStatus = delivery != null ? Str(delivery, "status") : string.IsNullOrEmpty(deliveryId) ? null : "unknown";
        var (orderPlan, orderReason) = PlanOrder(order, orderId, deliveryStatus, resolution, cancelAllowed: false);
        var (deliveryPlan, deliveryReason) = PlanDelivery(delivery, deliveryId, resolution, orderPlan, cancelAllowed: false);
        if (!IsRefusal(orderPlan) && !IsRefusal(deliveryPlan))
        {
            return new DispositionRead(
                new DispositionPlan(DispositionCase.NotNeeded, false, null, false, null), orderId, deliveryId, order, delivery);
        }

        var legs = Result(
            IsRefusal(orderPlan) ? orderPlan : VerdictLegOutcome.None, orderReason,
            IsRefusal(deliveryPlan) ? deliveryPlan : VerdictLegOutcome.None, deliveryReason, orderId, deliveryId, order).FailedLegs;
        var hasOrderLeg = _orderRepo != null && !string.IsNullOrEmpty(orderId);
        var hasDeliveryLeg = _deliveryRepo != null && !string.IsNullOrEmpty(deliveryId);
        var orderStatus = order != null ? Str(order, "status") : null;
        var recorded = delivery != null ? Str(delivery, "disputeResolution") : null;
        var deliveryOpen = deliveryStatus is "pending" or "pickedUp" or "inTransit" && recorded == null;
        var closedDeliveryStatus = recorded == resolution && deliveryStatus is "cancelled" or "disputed" ? deliveryStatus : null;

        var orderOk = !hasOrderLeg || (orderStatus != null
                                       && (PreDeliveryOrderStatuses.Contains(orderStatus) || orderStatus == "dispute_resolved"));
        var deliveryOk = !hasDeliveryLeg || deliveryOpen || closedDeliveryStatus != null;
        if ((!hasOrderLeg && !hasDeliveryLeg) || !orderOk || !deliveryOk)
        {
            return new DispositionRead(
                new DispositionPlan(DispositionCase.NoFit, false, null, false, legs), orderId, deliveryId, order, delivery);
        }

        var started = closedDeliveryStatus != null || orderStatus == "dispute_resolved";
        var handedOver = started || orderStatus is "pickup_confirmed" or "in_transit" || deliveryStatus is "pickedUp" or "inTransit";
        var dispositionCase = resolution == "farmer_wins"
            ? DispositionCase.BeforeDelivery
            : handedOver ? DispositionCase.GoodsMoving : DispositionCase.NoFit;
        return new DispositionRead(
            new DispositionPlan(dispositionCase, handedOver, closedDeliveryStatus, started, legs), orderId, deliveryId, order, delivery);
    }

    /// <summary>The delivery leg of a disposition: closed under the verdict, or left when an earlier run closed it.</summary>
    private async Task<(VerdictLegOutcome, string?)> CloseDeliveryAsync(
        Dictionary<string, object>? delivery, string? deliveryId, string target, string resolution, string disposition,
        string resolvedAt, string transactionId, string metadata, ILambdaContext? context)
    {
        if (delivery == null || string.IsNullOrEmpty(deliveryId))
        {
            return (VerdictLegOutcome.None, null);
        }

        var status = Str(delivery, "status") ?? "";
        if (status == target && Str(delivery, "disputeResolution") == resolution)
        {
            return (VerdictLegOutcome.AlreadyResolved, null);
        }

        var cancelReason = target == "cancelled" ? $"dispute_{resolution}" : null;
        if (await _deliveryRepo!.TryCloseForVerdictAsync(deliveryId, status, target, resolution, resolvedAt, cancelReason))
        {
            await TransactionAuditWriter.EmitAsync(_txAuditRepo, context, transactionId, "delivery", deliveryId,
                target == "cancelled" ? "delivery_cancelled" : "dispute_resolved", status, target, AuditContext.ForSystem(),
                metadata: cancelReason == null ? metadata : $"{metadata};cancelReason={cancelReason}");
            return (VerdictLegOutcome.Resolved, null);
        }

        // Lost a race: done by a concurrent replay, or the delivery moved on (a handoff, a party's cancel).
        var latest = await _deliveryRepo.GetByIdAsync(deliveryId, consistentRead: true);
        if (latest == null)
        {
            return (VerdictLegOutcome.Missing, "missing");
        }

        var latestStatus = Str(latest, "status");
        return latestStatus == target && Str(latest, "disputeResolution") == resolution
            ? (VerdictLegOutcome.AlreadyResolved, null)
            : (VerdictLegOutcome.Mismatch, $"mismatch:{latestStatus}");
    }

    /// <summary>The order leg of a disposition: straight to <c>dispute_resolved</c>, conditional on the status it was read at.</summary>
    private async Task<(VerdictLegOutcome, string?, Dictionary<string, object>?)> CloseOrderAsync(
        Dictionary<string, object>? order, string? orderId, string metadata, ILambdaContext? context)
    {
        if (order == null || string.IsNullOrEmpty(orderId))
        {
            return (VerdictLegOutcome.None, null, null);
        }

        var status = Str(order, "status") ?? "";
        if (status == "dispute_resolved")
        {
            return (VerdictLegOutcome.AlreadyResolved, null, order);
        }

        var updated = await _orderRepo!.TryAdvanceStatusAsync(orderId, status, "dispute_resolved");
        if (updated != null)
        {
            await TransactionAuditWriter.EmitAsync(_txAuditRepo, context, orderId, "order", orderId,
                "dispute_resolved", status, "dispute_resolved", AuditContext.ForSystem(), metadata: metadata);
            return (VerdictLegOutcome.Resolved, null, updated);
        }

        var latest = await _orderRepo.GetByIdAsync(orderId, consistentRead: true);
        if (latest == null)
        {
            return (VerdictLegOutcome.Missing, "missing", null);
        }

        var latestStatus = Str(latest, "status");
        return latestStatus == "dispute_resolved"
            ? (VerdictLegOutcome.AlreadyResolved, null, latest)
            : (VerdictLegOutcome.Conflict, $"mismatch:{latestStatus}", latest);
    }

    /// <summary>
    /// ESC-09 Part B: give a cancelled order's quintals back before its refund (idempotent per order).
    /// Null when the stock is back or there is nothing to give back, else the reason it is not.
    /// </summary>
    public async Task<string?> EnsureStockRestoredAsync(Dictionary<string, object> order)
        => await RestoreStockAsync(order, Str(order, "orderId") ?? "");

    /// <summary>The audit tag of the cancels a verdict makes (ESC-03).</summary>
    private const string VerdictAuditTag = "resolution=buyer_wins";

    /// <summary>The audit tag of the cancels an admin refund makes (ESC-09 Part B).</summary>
    private const string AdminRefundAuditTag = "source=admin_refund";

    /// <summary>
    /// The escrow's order and delivery, strongly consistent: the order from the escrow (or its delivery),
    /// and the order's active delivery, which wins over an earlier delivery the escrow may still name.
    /// A read that throws gives the reason and nothing else is decided.
    /// </summary>
    private async Task<(string? OrderId, string? DeliveryId, Dictionary<string, object>? Order, Dictionary<string, object>? Delivery, string? Error)>
        ReadLegsAsync(Dictionary<string, object> escrow)
    {
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
            return (orderId, deliveryId, null, null, $"read:[{ex.GetType().Name}]");
        }

        return (orderId, deliveryId, order, delivery, null);
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
            // FUP-16: an admin disposition (return_to_farmer, settle_now) cancelled it under this verdict.
            "cancelled" when recorded == resolution => (VerdictLegOutcome.AlreadyResolved, null),
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
        string transactionId, ILambdaContext? context, string auditTag)
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
                        "delivery_cancelled", "pending", "cancelled", AuditContext.ForSystem(),
                        metadata: $"{auditTag};reason=escrow_refunded");
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

        var (orderOutcome, orderReason, cancelled) = await CancelOrderAsync(order!, orderId!, deliveryId, context, auditTag);
        return Result(orderOutcome, orderReason, deliveryOutcome, deliveryReason, orderId, deliveryId, cancelled ?? order);
    }

    private async Task<(VerdictLegOutcome, string?, Dictionary<string, object>?)> CancelOrderAsync(
        Dictionary<string, object> order, string orderId, string? cancelledDeliveryId, ILambdaContext? context, string auditTag)
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
                        metadata: $"{auditTag};cancelReason=escrow_refunded");
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
