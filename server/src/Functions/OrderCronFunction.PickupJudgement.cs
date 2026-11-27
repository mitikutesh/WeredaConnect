using WeredaConnect.Server.Data;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Services;

namespace WeredaConnect.Server.Functions;

/// <summary>
/// ESC-34 (spec 4.8, delivery-verification-api.md 2.6.0): the pickup judgement at T7 of an order stamped
/// <c>pickupJudgement: pending</c>. Due at <c>pickupDeadlineAt</c> plus
/// <see cref="PickupJudgement.SettleMargin"/> (<c>OrderDeadlines</c> kind <c>pickup_judgement</c>); the
/// outcome is <see cref="PickupJudgement.Decide"/> on the row and its delivery as read again:
/// <list type="bullet">
/// <item>a delivery that left <c>pending</c>: <c>scanned</c>, stamped only (the normal flow);</item>
/// <item>trucker no-show: <c>awaiting_transport</c> (<c>transportFailure: trucker_no_show</c>,
/// <c>truckerResponse: no_show</c>, <c>faultParty: trucker</c>), the pending delivery cancelled first and
/// the trip capacity released. The reputation stream turns it into <c>NO_SHOW</c> (rule R10);</item>
/// <item>farmer no-show: <c>cancelled</c> (<c>cancelReason: farmer_no_show</c>, <c>farmerResponse:
/// no_show</c>, <c>faultParty: farmer</c>) with the full refund armed in the same transaction, stock and
/// capacity back (rule R11);</item>
/// <item>self-pickup buyer no-show: <c>cancelled</c> (<c>buyer_no_show</c>, <c>faultParty: buyer</c>), full
/// refund, no points in P3;</item>
/// <item>both present, or a farmer no-show resting on an unmeasured check-in: the order stays
/// <c>accepted</c>, stamped, in the admin queue; the money stays held.</item>
/// </list>
/// Every write is conditioned on the row as read (version CAS, or the stamp's expected values); a lost
/// race is a conflict and the row is read again on its next due time. An order whose absent party has a
/// check-in that came too late also enters the admin queue as <c>late_pickup_evidence</c>.
/// </summary>
public partial class OrderCronFunction
{
    /// <summary>The log token of each judgement (not alarmed).</summary>
    internal const string PickupJudgedToken = "PICKUP_JUDGED";

    private async Task<DeadlineOutcome> JudgePickupAsync(
        Dictionary<string, object> order, string orderId, CommitmentRun run,
        EscrowService escrow, ICropRepository crops, ITripRepository trips)
    {
        if (PickupJudgement.JudgeAt(order) is not { } judgeAt || PickupJudgement.DeadlineOf(order) is not { } deadline)
        {
            return DeadlineOutcome.None;
        }

        if (run.Now <= judgeAt)
        {
            // FUP-286: before the judgement, the check-in reminder when it falls due.
            return await RemindPickupCheckInAsync(order, orderId, run);
        }

        // The order can lag its delivery: a delivery that left pending was picked up (the scan).
        var deliveryId = GetString(order, "activeDeliveryId");
        var pickedUp = false;
        if (deliveryId.Length > 0)
        {
            if (_deliveryRepo == null)
            {
                run.Context.Logger.LogWarning(
                    $"Pickup judgement: order {MoneyAlarmLog.LogId(orderId)} has a delivery and no delivery table is configured; it waits.");
                return DeadlineOutcome.Waiting;
            }

            Dictionary<string, object>? delivery;
            try
            {
                delivery = await _deliveryRepo.GetByIdAsync(deliveryId, consistentRead: true);
            }
            catch (Exception ex)
            {
                run.Context.Logger.LogWarning(
                    $"Pickup judgement: delivery {MoneyAlarmLog.LogId(deliveryId)} of order {MoneyAlarmLog.LogId(orderId)} could not be read [{ex.GetType().Name}]; it waits.");
                return DeadlineOutcome.Waiting;
            }

            pickedUp = delivery != null && GetString(delivery, "status") is { Length: > 0 } status
                       && status is not ("pending" or "cancelled");
        }

        var outcome = PickupJudgement.Decide(order, pickedUp, deadline);
        var result = outcome switch
        {
            PickupJudgement.Outcome.TruckerNoShow => await JudgeTruckerNoShowAsync(order, orderId, deliveryId, run, trips),
            PickupJudgement.Outcome.FarmerNoShow or PickupJudgement.Outcome.BuyerNoShow
                => await JudgeCancelAsync(order, orderId, deliveryId, outcome, run, escrow, crops, trips),
            _ => await StampJudgedAsync(order, orderId, outcome, run)
        };

        if (result != DeadlineOutcome.Applied)
        {
            return result;
        }

        run.PickupJudged++;
        run.Context.Logger.LogInformation($"{PickupJudgedToken} orderId={MoneyAlarmLog.LogId(orderId)} outcome={outcome}");

        if (outcome is PickupJudgement.Outcome.BothPresentNoScan or PickupJudgement.Outcome.UncheckedCheckIn)
        {
            await EnterJudgementQueueAsync(orderId, outcome, "accepted", run);
        }
        else if (PickupJudgement.HasLateEvidenceAgainst(order, outcome))
        {
            await EnterJudgementQueueAsync(orderId, PickupJudgement.ReasonLateEvidence,
                outcome == PickupJudgement.Outcome.TruckerNoShow ? OrderCommitment.AwaitingTransportStatus : "cancelled", run);
        }

        return DeadlineOutcome.Applied;
    }

    /// <summary>
    /// FUP-286: the check-in reminder, <see cref="OrderTimings.ReminderLead"/> before <c>pickupDeadlineAt</c>, while a
    /// party has not checked in: the marker <see cref="PickupJudgement.ReminderAtField"/> is stamped once, while the
    /// order is still <c>accepted</c> and pending (no version bump); the order notifier tells each party who has not
    /// checked in when it appears. A stamp the cron makes only after the deadline (a cron that was down) tells nobody.
    /// </summary>
    private async Task<DeadlineOutcome> RemindPickupCheckInAsync(Dictionary<string, object> order, string orderId, CommitmentRun run)
    {
        if (PickupJudgement.ReminderDueAt(order, OrderTimings.Current) is not { } remindAt || run.Now < remindAt)
        {
            return DeadlineOutcome.None;
        }

        return await StampAsync(orderId,
            new Dictionary<string, object> { { PickupJudgement.ReminderAtField, run.NowIso } },
            new Dictionary<string, string>
            {
                { "status", "accepted" }, { PickupJudgement.Field, PickupJudgement.Outcome.Pending }
            },
            "pickup check-in reminder", run, () => run.PickupReminders++);
    }

    /// <summary>The judgement fields every outcome writes; they also end the P1 marker's deadline.</summary>
    private static Dictionary<string, object> JudgedFields(string outcome, CommitmentRun run) => new()
    {
        { PickupJudgement.Field, outcome },
        { PickupJudgement.JudgedAtField, run.NowIso },
        { OrderCommitment.PickupOverdueNotifiedAtField, run.NowIso }
    };

    /// <summary>The order stays where it is: only the judgement is stamped (no version bump).</summary>
    private async Task<DeadlineOutcome> StampJudgedAsync(
        Dictionary<string, object> order, string orderId, string outcome, CommitmentRun run)
    {
        var stamped = await StampAsync(orderId, JudgedFields(outcome, run),
            new Dictionary<string, string>
            {
                { "status", "accepted" }, { PickupJudgement.Field, PickupJudgement.Outcome.Pending }
            },
            "pickup judgement", run, () => { });
        if (stamped == DeadlineOutcome.Applied)
        {
            await TransactionAuditWriter.EmitAsync(
                _transactionAuditRepo, run.Context, orderId, "order", orderId,
                "pickup_judged", "accepted", "accepted", AuditContext.ForSystem(), metadata: $"outcome={outcome}");
        }

        return stamped;
    }

    /// <summary>
    /// The pending delivery goes first (conditional on <c>pending</c>), so a pickup racing in wins and the
    /// next run judges it picked up. Null when the order has no delivery or it is already cancelled;
    /// <see cref="DeadlineOutcome.Waiting"/> when it could not be cancelled. FUP-363: the cancel carries the
    /// judgement's own stamp (<see cref="PickupJudgement.DeliveryCancelledAtField"/>), so when the order write
    /// after it fails and a later run judges again (a later <c>pickupJudgedAt</c>), a no-show re-open still
    /// knows the judgement cancelled it.
    /// </summary>
    private async Task<DeadlineOutcome?> CancelPendingDeliveryFirstAsync(string orderId, string deliveryId, string reason, CommitmentRun run)
    {
        if (deliveryId.Length == 0 || _deliveryRepo == null)
        {
            return null;
        }

        try
        {
            if (await _deliveryRepo.TryCancelPendingForJudgementAsync(deliveryId))
            {
                await TransactionAuditWriter.EmitAsync(
                    _transactionAuditRepo, run.Context, orderId, "delivery", deliveryId,
                    "delivery_cancelled", "pending", "cancelled", AuditContext.ForSystem(), metadata: $"reason={reason}");
                return null;
            }

            var delivery = await _deliveryRepo.GetByIdAsync(deliveryId, consistentRead: true);
            if (delivery == null || GetString(delivery, "status") == "cancelled")
            {
                return null;
            }
        }
        catch (Exception ex)
        {
            run.Context.Logger.LogWarning(
                $"Pickup judgement: delivery {MoneyAlarmLog.LogId(deliveryId)} of order {MoneyAlarmLog.LogId(orderId)} could not be cancelled [{ex.GetType().Name}]; it waits.");
            return DeadlineOutcome.Waiting;
        }

        // Picked up since the read: judged again (as scanned) on the next run.
        run.Context.Logger.LogInformation(
            $"Pickup judgement: delivery {MoneyAlarmLog.LogId(deliveryId)} of order {MoneyAlarmLog.LogId(orderId)} left pending; judged again later.");
        return DeadlineOutcome.Waiting;
    }

    private async Task<DeadlineOutcome> JudgeTruckerNoShowAsync(
        Dictionary<string, object> order, string orderId, string deliveryId, CommitmentRun run, ITripRepository trips)
    {
        if (await CancelPendingDeliveryFirstAsync(orderId, deliveryId, PickupJudgement.TruckerNoShowFailure, run) is { } wait)
        {
            return wait;
        }

        var fields = OrderCommitment.TransportFailedFields(
            PickupJudgement.TruckerNoShowFailure, PickupJudgement.NoShowResponse, run.Now);
        foreach (var (key, value) in JudgedFields(PickupJudgement.Outcome.TruckerNoShow, run))
        {
            fields[key] = value;
        }

        if (!await ApplyTransitionAsync(order, orderId, fields, "accepted", OrderCommitment.AwaitingTransportStatus,
                "order_updated", PickupJudgement.TruckerNoShowFailure, run))
        {
            return DeadlineOutcome.Conflict;
        }

        await ReleaseJudgedDeliverySlotAsync(orderId, deliveryId, run);
        await ReleaseTripCapacityAsync(order, orderId, run, trips);
        return DeadlineOutcome.Applied;
    }

    private async Task<DeadlineOutcome> JudgeCancelAsync(
        Dictionary<string, object> order, string orderId, string deliveryId, string outcome, CommitmentRun run,
        EscrowService escrow, ICropRepository crops, ITripRepository trips)
    {
        var farmerNoShow = outcome == PickupJudgement.Outcome.FarmerNoShow;
        var reason = farmerNoShow ? PickupJudgement.FarmerNoShowReason : PickupJudgement.BuyerNoShowReason;
        if (await CancelPendingDeliveryFirstAsync(orderId, deliveryId, reason, run) is { } wait)
        {
            return wait;
        }

        var fields = new Dictionary<string, object>
        {
            { "status", "cancelled" },
            { "cancelledAt", run.NowIso },
            { "cancelReason", reason },
            { OrderCommitment.CancelledByField, OrderCommitment.Party.System },
            { OrderCommitment.FaultPartyField, farmerNoShow ? OrderCommitment.Party.Farmer : OrderCommitment.Party.Buyer }
        };
        if (farmerNoShow)
        {
            fields[OrderCommitment.FarmerResponseField] = PickupJudgement.NoShowResponse;
        }

        foreach (var (key, value) in JudgedFields(outcome, run))
        {
            fields[key] = value;
        }

        var cancel = await CancelWithRefundAsync(order, orderId, fields, "accepted", reason, run, escrow);
        if (cancel != DeadlineOutcome.Applied)
        {
            return cancel;
        }

        await ReleaseJudgedDeliverySlotAsync(orderId, deliveryId, run);
        await RestoreStockAsync(order, orderId, run, crops);
        if (OrderCommitment.HasTrip(order))
        {
            await ReleaseTripCapacityAsync(order, orderId, run, trips);
        }

        return DeadlineOutcome.Applied;
    }

    private async Task ReleaseJudgedDeliverySlotAsync(string orderId, string deliveryId, CommitmentRun run)
    {
        if (deliveryId.Length == 0)
        {
            return;
        }

        try
        {
            await _orderRepo.ReleaseDeliverySlotAsync(orderId, deliveryId);
        }
        catch (Exception ex)
        {
            run.Context.Logger.LogWarning(
                $"Pickup judgement: the delivery slot of order {MoneyAlarmLog.LogId(orderId)} could not be released [{ex.GetType().Name}].");
        }
    }

    /// <summary>
    /// The admin queue entry of a judgement (<c>both_present_no_scan</c>, <c>unchecked_check_in</c>,
    /// <c>late_pickup_evidence</c>), without a version bump and only while the order is in
    /// <paramref name="status"/> and not queued yet. Best effort: the judgement stands either way.
    /// </summary>
    private async Task EnterJudgementQueueAsync(string orderId, string reason, string status, CommitmentRun run)
    {
        try
        {
            if (await _orderRepo.TryEnterAdminQueueAsync(orderId, reason, $"escrow-order-{orderId}", run.NowIso, status))
            {
                await TransactionAuditWriter.EmitAsync(
                    _transactionAuditRepo, run.Context, orderId, "order", orderId,
                    "admin_queue_entered", status, status, AuditContext.ForSystem(), metadata: $"reason={reason}");
            }
        }
        catch (Exception ex)
        {
            run.Context.Logger.LogWarning(
                $"Pickup judgement: order {MoneyAlarmLog.LogId(orderId)} could not enter the admin queue ({reason}) [{ex.GetType().Name}].");
        }
    }

    /// <summary>
    /// ESC-34, the flag turned off while an order waited for its judgement: the P1 marker as for any
    /// accepted order, and the stamp cleared (stored as a null attribute, which no read returns), so the
    /// apps show the buyer's deadline cancel again and the check-ins close. FUP-286: when the check-in reminder falls
    /// due first, it is never sent with the flag off: the stamp alone is cleared then, and the P1 notice waits for the
    /// deadline as on any accepted order.
    /// </summary>
    private async Task<DeadlineOutcome> EndPickupJudgementAsync(Dictionary<string, object> order, string orderId, CommitmentRun run)
    {
        if (PickupJudgement.DeadlineOf(order) is not { } deadline)
        {
            return DeadlineOutcome.None;
        }

        if (deadline >= run.Now)
        {
            if (PickupJudgement.ReminderDueAt(order, OrderTimings.Current) is not { } remindAt || run.Now < remindAt)
            {
                return DeadlineOutcome.None;
            }

            return await StampAsync(orderId,
                new Dictionary<string, object> { { PickupJudgement.Field, null! } },
                new Dictionary<string, string>
                {
                    { "status", "accepted" }, { PickupJudgement.Field, PickupJudgement.Outcome.Pending }
                },
                "pickup judgement end", run, () => { });
        }

        return await StampAsync(orderId,
            new Dictionary<string, object>
            {
                { OrderCommitment.PickupOverdueNotifiedAtField, run.NowIso },
                { PickupJudgement.Field, null! }
            },
            new Dictionary<string, string>
            {
                { "status", "accepted" }, { PickupJudgement.Field, PickupJudgement.Outcome.Pending }
            },
            "pickup overdue marker", run, () => run.PickupOverdue++);
    }
}
