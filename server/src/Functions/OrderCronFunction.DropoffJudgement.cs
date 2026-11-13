using System.Globalization;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Services;

namespace WeredaConnect.Server.Functions;

/// <summary>
/// ESC-36 (spec 4.5 T8 and T9, 4.8, E3 and E4; delivery-verification-api.md P.4): the drop-off judgement
/// of a <c>pickup_confirmed</c> or <c>in_transit</c> order stamped <c>dropoffJudgement: pending</c>, due at
/// T8 (<c>dropoffDeadlineAt</c>, kind <c>delivery_overdue</c>) or, once the trucker checked in at the
/// drop-off, at T9 (the check-in's receipt plus 48 hours, kind <c>silent_buyer</c>), each plus
/// <see cref="PickupJudgement.SettleMargin"/>. The order and its delivery are read again (strongly
/// consistent) and decided by <see cref="DropoffJudgement.Decide"/>:
/// <list type="bullet">
/// <item>T8, no counted check-in: the escrow is frozen first, then the delivery and the order go to
/// <c>disputed</c> with the reason <c>delivery_overdue</c> (reputation rule R12: <c>DELIVERY_OVERDUE</c> and
/// the trucker's restriction pending review), and the alarmed <see cref="DeliveryOverdueToken"/> line is
/// logged. No money moves;</item>
/// <item>T9, complete evidence, a trucker neither low nor restricted, the money plainly held: the
/// delivery is confirmed as the system (<c>autoConfirmedAt</c>), the order follows to <c>delivered</c> and
/// the escrow is stamped as a buyer's confirm stamps it (the 48 hour dispute window from
/// <c>confirmedAt</c>, the FRD-13 payout delay); the release then waits for the escrow cron like any
/// other, with its fraud hold;</item>
/// <item>T9, anything missing: the escrow frozen, then <c>disputed</c> with the reason
/// <c>silent_buyer</c>. No reputation event;</item>
/// <item>T8 with the trucker's out-of-range attempt (FUP-204): the same freeze and dispute with the reason
/// <c>dropoff_out_of_range</c>, which R12 does not read: no reputation event, no signal; an admin decides
/// through the verdict;</item>
/// <item>money that can be neither frozen nor confirmed (no escrow, a refund armed, the manual payout
/// queue, a settlement in flight): nothing moves, the order enters the admin queue (reason
/// <c>delivery_overdue</c>, <c>dropoff_out_of_range</c> or <c>silent_buyer</c>), then is stamped <c>queued</c>.</item>
/// </list>
/// A delivery a party confirmed or disputed first is only followed (<c>closed</c>). Every write is
/// conditioned on the row as read (the delivery's status and version, the order's version, the stamp); a
/// lost race is a conflict and the row is read again on its next due time. A system dispute whose order
/// write was lost is finished on the next run.
/// </summary>
public partial class OrderCronFunction
{
    /// <summary>The alarmed line of an overdue delivery (spec 4.5 T8: "alarm").</summary>
    internal const string DeliveryOverdueToken = "ORDER_DELIVERY_OVERDUE";

    /// <summary>The log token of each drop-off judgement (not alarmed).</summary>
    internal const string DropoffJudgedToken = "DROPOFF_JUDGED";

    /// <summary>The buyer's dispute window after a confirm, as the sync's stamp of a buyer's confirm.</summary>
    private static readonly TimeSpan ConfirmDisputeWindow = TimeSpan.FromHours(48);

    /// <summary>
    /// ESC-36: <c>dropoffClassification</c>. On, an order stamped <c>dropoffJudgement: pending</c> is judged at
    /// T8 or T9; off, its stamp is cleared when it falls due (<see cref="EndDropoffJudgementAsync"/>).
    /// </summary>
    public bool DropoffClassificationEnabled { get; init; } = DropoffJudgement.IsEnabled;

    /// <summary>ESC-36: the escrow rows T9 reads and stamps; wired with the escrow service.</summary>
    internal IEscrowRepository? EscrowRepo { get; init; }

    /// <summary>ESC-36: the trucker's users row (the reputation tier T9 reads); wired when <c>USERS_TABLE</c> is set.</summary>
    internal IUserRepository? UserRepo { get; init; }

    /// <summary>ESC-36: FRD-13's payout delay on T9's escrow stamp; wired with the users and fraud signals tables.</summary>
    internal PayoutDelayPolicy? PayoutDelay { get; init; }

    /// <summary>
    /// A due order whose goods are on the road (<c>pickup_confirmed</c>, <c>in_transit</c>): only an order
    /// stamped at pickup is judged; any other has no deadline of the sweep.
    /// </summary>
    private async Task<DeadlineOutcome> DecideOnTheRoadAsync(
        Dictionary<string, object> order, string orderId, CommitmentRun run, EscrowService escrow)
    {
        if (!DropoffJudgement.IsPending(order))
        {
            return DeadlineOutcome.None;
        }

        return DropoffClassificationEnabled
            ? await JudgeDropoffAsync(order, orderId, run, escrow)
            : await EndDropoffJudgementAsync(order, orderId, run);
    }

    private async Task<DeadlineOutcome> JudgeDropoffAsync(
        Dictionary<string, object> order, string orderId, CommitmentRun run, EscrowService escrow)
    {
        if (DropoffJudgement.Due(order, OrderTimings.Current) is not { } due || run.Now <= due.At)
        {
            return DeadlineOutcome.None;
        }

        var deliveryId = GetString(order, "activeDeliveryId");
        if (deliveryId.Length == 0 || _deliveryRepo == null)
        {
            run.Context.Logger.LogWarning(
                $"Drop-off judgement: order {MoneyAlarmLog.LogId(orderId)} has no readable delivery (no id or no delivery table); it waits.");
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
                $"Drop-off judgement: delivery {MoneyAlarmLog.LogId(deliveryId)} of order {MoneyAlarmLog.LogId(orderId)} could not be read [{ex.GetType().Name}]; it waits.");
            return DeadlineOutcome.Waiting;
        }

        if (delivery == null)
        {
            run.Context.Logger.LogWarning(
                $"Drop-off judgement: delivery {MoneyAlarmLog.LogId(deliveryId)} of order {MoneyAlarmLog.LogId(orderId)} was not found; it waits.");
            return DeadlineOutcome.Waiting;
        }

        var deliveryStatus = GetString(delivery, "status");
        if (!DropoffJudgement.OnTheRoadDeliveryStatuses.Contains(deliveryStatus))
        {
            return await FollowSettledDeliveryAsync(order, orderId, delivery, run);
        }

        DropoffJudgement.Decision decision;
        if (DropoffJudgement.CountedCheckIn(order) == null)
        {
            // T8. FUP-204: a trucker who came but was out of range of every point goes to an admin.
            decision = DropoffJudgement.CountedAttempt(order) != null
                ? DropoffJudgement.Decision.OutOfRangeReview
                : DropoffJudgement.Decision.DeliveryOverdue;
        }
        else
        {
            bool truckerHeld;
            Dictionary<string, object>? escrowRow;
            try
            {
                truckerHeld = await TruckerHoldsAutoConfirmAsync(order, run);
                escrowRow = EscrowRepo == null ? null : await EscrowRepo.GetByIdAsync($"escrow-order-{orderId}", consistentRead: true);
            }
            catch (Exception ex)
            {
                run.Context.Logger.LogWarning(
                    $"Drop-off judgement: the trucker or the escrow of order {MoneyAlarmLog.LogId(orderId)} could not be read [{ex.GetType().Name}]; it waits.");
                return DeadlineOutcome.Waiting;
            }

            var arrivalPhoto = GetString(delivery, DeliveryPhotoKeys.FieldOf(DeliveryPhotoKeys.Arrival)).Length > 0;
            decision = DropoffJudgement.Decide(order, arrivalPhoto, truckerHeld, DropoffJudgement.IsPlainForConfirm(escrowRow, deliveryId));
        }

        return decision switch
        {
            DropoffJudgement.Decision.AutoConfirm => await AutoConfirmAsync(order, orderId, delivery, deliveryStatus, run),
            // FUP-204: the trucker came but was out of range of every point (maybe only the buyer's own pin): the
            // system's dispute with its own reason, so an admin decides through the verdict; R12 reads only
            // delivery_overdue, so no reputation event and no signal follow.
            DropoffJudgement.Decision.OutOfRangeReview
                => await OpenDropoffDisputeAsync(order, orderId, delivery, deliveryStatus, DropoffJudgement.OutOfRangeReason, run, escrow),
            DropoffJudgement.Decision.DeliveryOverdue
                => await OpenDropoffDisputeAsync(order, orderId, delivery, deliveryStatus, DropoffJudgement.OverdueReason, run, escrow),
            _ => await OpenDropoffDisputeAsync(order, orderId, delivery, deliveryStatus, DropoffJudgement.SilentBuyerReason, run, escrow)
        };
    }

    /// <summary>
    /// The delivery left the road before the judgement. The system's own dispute whose order write was lost
    /// is finished; a delivery confirmed by its buyer (or by an earlier run whose order write was lost) is
    /// followed by the order with the escrow stamped as a confirm stamps it; anything else (a party's
    /// dispute) only ends the judgement.
    /// </summary>
    private async Task<DeadlineOutcome> FollowSettledDeliveryAsync(
        Dictionary<string, object> order, string orderId, Dictionary<string, object> delivery, CommitmentRun run)
    {
        if (DropoffJudgement.SystemDisputeReasonOf(delivery) is { } reason)
        {
            return await DisputeOrderAsync(order, orderId, GetString(delivery, "deliveryId"), reason, run);
        }

        if (GetString(delivery, "status") == "delivered")
        {
            var outcome = GetString(delivery, DropoffJudgement.AutoConfirmedAtField).Length > 0
                ? DropoffJudgement.Outcome.AutoConfirmed
                : DropoffJudgement.Outcome.Closed;
            return await FollowConfirmedDeliveryAsync(order, orderId, delivery, outcome, run);
        }

        return await StampDropoffAsync(order, orderId, DropoffJudgement.Outcome.Closed, run);
    }

    /// <summary>
    /// Spec 4.8 and E3: a low or restricted trucker never has a silent buyer's delivery confirmed. Without
    /// a users table, or without the trucker's row, nothing can be checked, so nothing is confirmed.
    /// </summary>
    private async Task<bool> TruckerHoldsAutoConfirmAsync(Dictionary<string, object> order, CommitmentRun run)
    {
        var truckerId = GetString(order, "truckerId");
        if (UserRepo == null || truckerId.Length == 0)
        {
            return true;
        }

        var trucker = await UserRepo.GetUserByIdAsync(truckerId);
        return trucker == null || DropoffJudgement.HoldsAutoConfirm(trucker.Reputation, run.Now);
    }

    // ── T9: the confirm for a silent buyer ──

    private async Task<DeadlineOutcome> AutoConfirmAsync(
        Dictionary<string, object> order, string orderId, Dictionary<string, object> delivery, string deliveryStatus,
        CommitmentRun run)
    {
        var deliveryId = GetString(delivery, "deliveryId");
        if (!await _deliveryRepo!.TryAutoConfirmAsync(
                deliveryId, GetString(delivery, "tripId"), deliveryStatus, GetInt(delivery, "version"), run.NowIso))
        {
            // The buyer confirmed or disputed, or the trucker moved it on, since the read.
            run.Conflicts++;
            run.Context.Logger.LogInformation(
                $"Drop-off judgement: delivery {MoneyAlarmLog.LogId(deliveryId)} of order {MoneyAlarmLog.LogId(orderId)} moved on before its confirm.");
            return DeadlineOutcome.Conflict;
        }

        await TransactionAuditWriter.EmitAsync(
            _transactionAuditRepo, run.Context, orderId, "delivery", deliveryId,
            "delivery_auto_confirmed", deliveryStatus, "delivered", AuditContext.ForSystem(),
            metadata: $"reason={DropoffJudgement.SilentBuyerReason}");

        var confirmed = new Dictionary<string, object>(delivery)
        {
            ["status"] = "delivered",
            ["confirmedAt"] = run.NowIso,
            [DropoffJudgement.AutoConfirmedAtField] = run.NowIso
        };
        return await FollowConfirmedDeliveryAsync(order, orderId, confirmed, DropoffJudgement.Outcome.AutoConfirmed, run);
    }

    /// <summary>
    /// The order follows its confirmed delivery to <c>delivered</c> with the judgement (version CAS), and
    /// the escrow is stamped as the buyer's confirm stamps it (<see cref="StampConfirmedEscrowAsync"/>),
    /// whether or not the order write landed: the delivery is confirmed either way.
    /// </summary>
    private async Task<DeadlineOutcome> FollowConfirmedDeliveryAsync(
        Dictionary<string, object> order, string orderId, Dictionary<string, object> delivery, string outcome, CommitmentRun run)
    {
        var fields = new Dictionary<string, object>
        {
            { "status", "delivered" },
            { DropoffJudgement.Field, outcome },
            { DropoffJudgement.JudgedAtField, run.NowIso }
        };
        var moved = await ApplyTransitionAsync(order, orderId, fields, GetString(order, "status"), "delivered",
            "order_updated", $"dropoff={outcome}", run);
        await StampConfirmedEscrowAsync(orderId, delivery, run);
        if (!moved)
        {
            return DeadlineOutcome.Conflict;
        }

        NoteDropoffJudged(orderId, outcome, run);
        return DeadlineOutcome.Applied;
    }

    /// <summary>
    /// The order escrow stamped as <c>SyncFunction.StampEscrowDeliveryConfirmedAsync</c> stamps a buyer's
    /// confirm: <c>deliveryConfirmedAt</c> from the delivery's <c>confirmedAt</c>, the 48 hour
    /// <c>disputeWindowEndsAt</c> and FRD-13's <c>releaseNotBefore</c>, only while it is <c>escrowed</c>,
    /// unstamped and not another delivery's. A failure logs the alarmed
    /// <see cref="MoneyAlarmLog.EscrowDeliveryStampFailedToken"/>; the escrow cron's sweep of delivered orders
    /// stamps it later, and an unstamped escrow is never released.
    /// </summary>
    private async Task StampConfirmedEscrowAsync(string orderId, Dictionary<string, object> delivery, CommitmentRun run)
    {
        var deliveryId = GetString(delivery, "deliveryId");
        if (EscrowRepo == null)
        {
            run.Context.Logger.LogWarning(
                $"Drop-off judgement: no escrow table for order {MoneyAlarmLog.LogId(orderId)}; the escrow cron stamps its escrow.");
            return;
        }

        try
        {
            var escrowRow = await EscrowRepo.GetByIdAsync($"escrow-order-{orderId}", consistentRead: true);
            var linked = escrowRow == null ? null : GetString(escrowRow, "deliveryId");
            if (escrowRow == null
                || GetString(escrowRow, "status") != "escrowed"
                || GetString(escrowRow, "deliveryConfirmedAt").Length > 0
                || (!string.IsNullOrEmpty(linked) && linked != deliveryId))
            {
                return;
            }

            var confirmedAt = GetString(delivery, "confirmedAt") is { Length: > 0 } stored ? stored : run.NowIso;
            var windowBase = DateTime.TryParse(confirmedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
                ? parsed.ToUniversalTime()
                : run.Now;
            var fields = new Dictionary<string, object>
            {
                { "deliveryConfirmedAt", confirmedAt },
                { "disputeWindowEndsAt", (windowBase + ConfirmDisputeWindow).ToString("o", CultureInfo.InvariantCulture) }
            };

            PayoutDelayDecision? delay = null;
            if (PayoutDelay != null)
            {
                delay = await PayoutDelay.DecideAsync(escrowRow, GetString(delivery, "truckerId"), windowBase,
                    windowBase + ConfirmDisputeWindow, run.Now);
                fields[PayoutDelayPolicy.ReleaseNotBeforeField] = delay.Value.ReleaseNotBeforeIso;
            }

            await EscrowRepo.UpdateStatusAsync(
                GetString(escrowRow, "escrowId"), GetString(escrowRow, "createdAt"),
                newStatus: "escrowed", expectedCurrentStatus: "escrowed", additionalFields: fields);
            if (delay is { } decided)
            {
                run.Context.Logger.LogInformation(decided.LogLine(GetString(escrowRow, "escrowId")));
            }
        }
        catch (Exception ex)
        {
            run.Context.Logger.LogError(
                $"{MoneyAlarmLog.EscrowDeliveryStampFailedToken} deliveryId={MoneyAlarmLog.LogId(deliveryId)} source=order-cron "
                + $"reason=[{ex.GetType().Name}]: delivery confirmed but its escrow is not marked delivery-confirmed; "
                + "the hourly order-money sweep retries the stamp.");
        }
    }

    // ── T8 and T9's dispute ──

    /// <summary>
    /// The system's dispute of a delivery still on the road: the escrow frozen first (ESC-07), then the
    /// delivery (status-conditioned) and the order (version CAS). Money that cannot be frozen (no escrow, a
    /// refund armed, the manual payout queue, a settlement in flight, another delivery's escrow) moves
    /// nothing: the order is stamped <c>queued</c> and enters the admin queue.
    /// </summary>
    private async Task<DeadlineOutcome> OpenDropoffDisputeAsync(
        Dictionary<string, object> order, string orderId, Dictionary<string, object> delivery, string deliveryStatus,
        string reason, CommitmentRun run, EscrowService escrow)
    {
        var deliveryId = GetString(delivery, "deliveryId");
        DeliveryDisputeFreezeResult freeze;
        try
        {
            freeze = await escrow.FreezeForDeliveryDisputeAsync(deliveryId, orderId, DropoffJudgement.SystemActor, DisputeDescription(reason));
        }
        catch (Exception ex)
        {
            run.Context.Logger.LogWarning(
                $"Drop-off judgement: the freeze for order {MoneyAlarmLog.LogId(orderId)} is undecided, nothing written [{ex.GetType().Name}]; it waits.");
            return DeadlineOutcome.Waiting;
        }

        if (!freeze.MayMirror)
        {
            return await QueueDropoffAsync(order, orderId, deliveryId, reason, freeze.Outcome.ToString(), run);
        }

        if (!await _deliveryRepo!.TryMarkDropoffDisputedAsync(deliveryId, GetString(delivery, "tripId"), deliveryStatus, reason, run.NowIso))
        {
            // The buyer confirmed (or the trucker moved it on) after the read. The escrow stays frozen as the
            // system's dispute, fail closed: an admin decides it in Disputes, and the verdict mirror moves the
            // order and the delivery then. The next run follows the delivery as it is.
            run.Conflicts++;
            run.Context.Logger.LogWarning(
                $"Drop-off judgement: delivery {MoneyAlarmLog.LogId(deliveryId)} of order {MoneyAlarmLog.LogId(orderId)} moved on after its escrow froze ({reason}).");
            return DeadlineOutcome.Conflict;
        }

        await TransactionAuditWriter.EmitAsync(
            _transactionAuditRepo, run.Context, orderId, "delivery", deliveryId,
            "dispute", deliveryStatus, "disputed", AuditContext.ForSystem(), metadata: $"reason={reason}");
        return await DisputeOrderAsync(order, orderId, deliveryId, reason, run);
    }

    /// <summary>
    /// The order to <c>disputed</c> with <c>disputeReason</c> (the field reputation rule R12 reads) and the
    /// judgement, in one version-checked write. A <c>delivery_overdue</c> logs the alarmed
    /// <see cref="DeliveryOverdueToken"/>.
    /// </summary>
    private async Task<DeadlineOutcome> DisputeOrderAsync(
        Dictionary<string, object> order, string orderId, string deliveryId, string reason, CommitmentRun run)
    {
        var fields = new Dictionary<string, object>
        {
            { "status", "disputed" },
            { DropoffJudgement.DisputeReasonField, reason },
            { DropoffJudgement.Field, reason },
            { DropoffJudgement.JudgedAtField, run.NowIso }
        };
        if (!await ApplyTransitionAsync(order, orderId, fields, GetString(order, "status"), "disputed", "dispute", $"reason={reason}", run))
        {
            return DeadlineOutcome.Conflict;
        }

        NoteDropoffJudged(orderId, reason, run);
        if (reason is DropoffJudgement.OverdueReason or DropoffJudgement.OutOfRangeReason)
        {
            LogOverdue(orderId, deliveryId, "disputed", "frozen", reason, run);
        }

        return DeadlineOutcome.Applied;
    }

    /// <summary>The alarmed line of a drop-off judgement whose admin queue entry was not written (the order queued for another reason).</summary>
    internal const string DropoffQueueNotWrittenToken = "DROPOFF_QUEUE_NOT_WRITTEN";

    /// <summary>
    /// The judgement an admin decides: nothing moves. The admin queue entry is written first (a failure writes
    /// nothing and the row waits an hour); then the order is stamped <c>queued</c>, which removes its deadline.
    /// When the entry is not written because the order is already in the queue for another reason (the escrow's
    /// hold cap), the alarmed <see cref="DropoffQueueNotWrittenToken"/> line names it, so the drop-off reason
    /// is never lost silently. An overdue delivery (and an out-of-range one) also logs the alarmed
    /// <see cref="DeliveryOverdueToken"/> line with <c>outcome=queued</c>.
    /// </summary>
    private async Task<DeadlineOutcome> QueueDropoffAsync(
        Dictionary<string, object> order, string orderId, string deliveryId, string reason, string escrowState, CommitmentRun run)
    {
        var status = GetString(order, "status");
        bool entered;
        try
        {
            entered = await _orderRepo.TryEnterAdminQueueAsync(orderId, reason, $"escrow-order-{orderId}", run.NowIso, status);
        }
        catch (Exception ex)
        {
            run.Context.Logger.LogWarning(
                $"Drop-off judgement: order {MoneyAlarmLog.LogId(orderId)} could not enter the admin queue ({reason}) [{ex.GetType().Name}]; it waits.");
            return DeadlineOutcome.Waiting;
        }

        if (entered)
        {
            await TransactionAuditWriter.EmitAsync(
                _transactionAuditRepo, run.Context, orderId, "order", orderId,
                "admin_queue_entered", status, status, AuditContext.ForSystem(), metadata: $"reason={reason}");
        }
        else
        {
            var fresh = await _orderRepo.GetByIdAsync(orderId, consistentRead: true);
            if (fresh == null || GetString(fresh, "status") != status || !DropoffJudgement.IsPending(fresh))
            {
                run.Conflicts++;
                return DeadlineOutcome.Conflict;
            }

            // The order's own entry already stands (an earlier run wrote it, then its stamp failed): nothing to alarm.
            if (GetString(fresh, AdminQueueFields.Reason) != reason)
            {
                run.Context.Logger.LogError(
                $"{DropoffQueueNotWrittenToken} orderId={MoneyAlarmLog.LogId(orderId)} reason={reason} "
                    + $"queued={MoneyAlarmLog.LogId(GetString(fresh, AdminQueueFields.Reason))}: the order waits in the admin queue for "
                    + "another reason; decide its drop-off too.");
            }
        }

        var stamped = await StampDropoffAsync(order, orderId, DropoffJudgement.Outcome.Queued, run);
        if (stamped != DeadlineOutcome.Applied)
        {
            return stamped;
        }

        NoteDropoffJudged(orderId, DropoffJudgement.Outcome.Queued, run);
        if (reason is DropoffJudgement.OverdueReason or DropoffJudgement.OutOfRangeReason)
        {
            LogOverdue(orderId, deliveryId, DropoffJudgement.Outcome.Queued, escrowState, reason, run);
        }

        return DeadlineOutcome.Applied;
    }

    private static string DisputeDescription(string reason) => reason switch
    {
        DropoffJudgement.OverdueReason => "delivery_overdue: the delivery was not made by its deadline (T8); opened by the system.",
        DropoffJudgement.OutOfRangeReason => "dropoff_out_of_range: the trucker checked in at the drop-off with the arrival photo but more "
                                             + "than 5 km from every drop-off point, and nobody confirmed by the deadline (T8); opened by "
                                             + "the system. The attempt's distance and time are on the transaction timeline (handoff_check_in).",
        _ => "silent_buyer: the buyer neither confirmed nor disputed after the trucker's drop-off check-in (T9); opened by the system."
    };

    private static void LogOverdue(string orderId, string deliveryId, string outcome, string escrow, string reason, CommitmentRun run)
        => run.Context.Logger.LogError(
            $"{DeliveryOverdueToken} orderId={MoneyAlarmLog.LogId(orderId)} deliveryId={MoneyAlarmLog.LogId(deliveryId)} "
            + $"outcome={outcome} escrow={MoneyAlarmLog.LogId(escrow)} reason={reason}: no delivery by its deadline; an admin reviews "
            + "the dispute and the trucker.");

    private static void NoteDropoffJudged(string orderId, string outcome, CommitmentRun run)
    {
        run.DropoffJudged++;
        run.Context.Logger.LogInformation($"{DropoffJudgedToken} orderId={MoneyAlarmLog.LogId(orderId)} outcome={outcome}");
    }

    /// <summary>The order stays where it is: only the judgement is stamped (no version bump).</summary>
    private async Task<DeadlineOutcome> StampDropoffAsync(
        Dictionary<string, object> order, string orderId, string outcome, CommitmentRun run)
    {
        var status = GetString(order, "status");
        var stamped = await StampAsync(orderId,
            new Dictionary<string, object>
            {
                { DropoffJudgement.Field, outcome },
                { DropoffJudgement.JudgedAtField, run.NowIso }
            },
            new Dictionary<string, string> { { "status", status }, { DropoffJudgement.Field, DropoffJudgement.Outcome.Pending } },
            "drop-off judgement", run, () => { });
        if (stamped == DeadlineOutcome.Applied)
        {
            await TransactionAuditWriter.EmitAsync(
                _transactionAuditRepo, run.Context, orderId, "order", orderId,
                "dropoff_judged", status, status, AuditContext.ForSystem(), metadata: $"outcome={outcome}");
            if (outcome == DropoffJudgement.Outcome.Closed)
            {
                NoteDropoffJudged(orderId, outcome, run);
            }
        }

        return stamped;
    }

    /// <summary>
    /// ESC-36, the flag turned off while an order waited for its drop-off judgement: the stamp is cleared
    /// when it falls due (stored as a null attribute, which no read returns), so the order keeps the rules
    /// it had before and leaves the deadline index.
    /// </summary>
    private async Task<DeadlineOutcome> EndDropoffJudgementAsync(Dictionary<string, object> order, string orderId, CommitmentRun run)
    {
        if (DropoffJudgement.Due(order, OrderTimings.Current) is not { } due || run.Now <= due.At)
        {
            return DeadlineOutcome.None;
        }

        return await StampAsync(orderId,
            new Dictionary<string, object> { { DropoffJudgement.Field, null! } },
            new Dictionary<string, string>
            {
                { "status", GetString(order, "status") }, { DropoffJudgement.Field, DropoffJudgement.Outcome.Pending }
            },
            "drop-off judgement end", run, () => { });
    }
}
