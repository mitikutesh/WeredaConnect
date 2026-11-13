using Amazon.DynamoDBv2.Model;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.Services;

namespace WeredaConnect.Server.Functions;

/// <summary>
/// FUP-330 (delivery-verification-api.md 2.11.0 P.2 "Re-open of a trucker no-show", order-lifecycle.md 2.20.0): an
/// admin re-opens a judged trucker no-show after reconciling a real late scan, which FUP-287's re-open refuses. The
/// rules are <see cref="NoShowReopen"/>; this file reads the order, its escrow, its delivery and its trip, writes one
/// transaction, then excuses the trucker's <c>NO_SHOW</c> through ESC-32's decision path. Admin group only, a reason
/// of 10 to 500 characters, the order version the page read. No money moves.
/// </summary>
public partial class AdminFunction
{
    /// <summary>
    /// POST /admin/orders/{orderId}/reopen-no-show with <c>{exit, reason, expectedVersion, deadlineHours?}</c>, only
    /// while the order is still <c>awaiting_transport</c> from the judgement (the buyer has not chosen) and its escrow
    /// still simply holds the money:
    /// <list type="bullet">
    /// <item><c>pending</c>: back to <c>accepted</c> with <c>pickupJudgement: pending</c> and <c>pickupDeadlineAt</c>
    /// now plus <c>deadlineHours</c> (<c>pickupJudgedAt</c> and the overdue notice removed, the check-ins kept), so the
    /// order cron judges it again; the trucker creates the new pending delivery as after the accept.</item>
    /// <item><c>handoff_confirmed</c>: the delivery the judgement cancelled <c>cancelled → pickedUp</c>, the slot claimed
    /// for it, the order <c>pickup_confirmed</c>, judged <c>scanned</c>.</item>
    /// </list>
    /// Both restore <c>truckerResponse: accepted</c>, remove <c>transportFailure</c>, <c>faultParty</c>,
    /// <c>buyerChoiceDeadlineAt</c> and <c>buyerChoiceReminderAt</c>, take the trip booking back, answer the judgement's
    /// admin queue entry as FUP-287 does and stamp <c>pickupReopen</c>, in one transaction conditioned on the order as
    /// read (version, status, judgement, transport failure, trucker response, queue entry, slot), the delivery as read,
    /// the trip's room and status, and the escrow still holding the money. Then the trucker's <c>NO_SHOW</c> is excused
    /// (answered in <c>noShowEvent</c>). The same request again answers <c>alreadyApplied</c>, writes nothing on the
    /// order and retries the excuse.
    /// </summary>
    private async Task<APIGatewayProxyResponse> HandleNoShowReopenAsync(
        string orderId, APIGatewayProxyRequest request, ILambdaContext context)
    {
        // It moves an order, its delivery and a trip booking: the admin pool's admin group only.
        if (!AdminAuthHelper.IsInAdminGroup(request))
        {
            return AdminAuthHelper.Forbidden();
        }

        if (string.IsNullOrEmpty(_ordersTable) || string.IsNullOrEmpty(_deliveriesTable) || string.IsNullOrEmpty(_escrowsTable)
            || string.IsNullOrEmpty(_tripsTable))
        {
            context.Logger.LogError("No-show re-open is not configured (ORDERS_TABLE, DELIVERIES_TABLE, ESCROWS_TABLE or TRIPS_TABLE missing).");
            return LambdaResponse.Error(503, "ADMIN_004", "Order tables not configured.");
        }

        PickupReopenRequest? body;
        try
        {
            body = JsonSerializer.Deserialize(request.Body ?? "", AppJsonContext.Default.PickupReopenRequest);
        }
        catch (JsonException)
        {
            return LambdaResponse.Error(400, "ADMIN_005", "Invalid JSON in request body.");
        }

        if (body == null)
        {
            return LambdaResponse.Error(400, "ADMIN_005", "Request body is required.");
        }

        if (ReopenRequestProblem(body) is { } invalid)
        {
            return invalid;
        }

        var exit = body.Exit!;
        var reason = body.Reason!.Trim();
        var expectedVersion = body.ExpectedVersion!.Value;
        var adminUserId = AuthHelper.GetUserId(request) ?? "admin";
        var correlationId = AuditLogger.GetCorrelationId(request);

        var orders = new OrderRepository(_dynamoDb, _ordersTable);
        var order = await orders.GetByIdAsync(orderId, consistentRead: true);
        if (order == null)
        {
            return LambdaResponse.Error(404, "ADMIN_012", "Transaction not found.");
        }

        if (NoShowReopen.IsReplay(order, expectedVersion, exit))
        {
            // The order write is done; the excuse after it may not be, so a replay tries it again.
            LogNoShowReopen(adminUserId, correlationId, orderId, "replay", null, exit);
            var again = await ExcuseNoShowAsync(order, orderId, adminUserId, request, context);
            return ReopenOk(orderId, exit, order, alreadyApplied: true, "This re-open was already applied.", again);
        }

        if (GetInt(order, "version") != expectedVersion)
        {
            LogNoShowReopen(adminUserId, correlationId, orderId, "failure", PickupReopen.ChangedCode, exit);
            return LambdaResponse.Error(409, PickupReopen.ChangedCode, "The order changed since the page read it; nothing was written. Reload and check.");
        }

        if (NoShowReopen.RefusalOf(order) is { } refusal)
        {
            LogNoShowReopen(adminUserId, correlationId, orderId, "failure", PickupReopen.NotAllowedCode, exit);
            return LambdaResponse.Error(409, PickupReopen.NotAllowedCode, refusal);
        }

        var escrow = await ReadOrderEscrowAsync(orderId);
        if (escrow != null && !PickupReopen.HoldsMoney(escrow))
        {
            LogNoShowReopen(adminUserId, correlationId, orderId, "failure", PickupReopen.MoneyNotHeldCode, exit);
            return LambdaResponse.Error(409, PickupReopen.MoneyNotHeldCode,
                "The order's payment is no longer simply held (refunding, settling, frozen for review or confirmed): "
                + "decide the money first, then reload.");
        }

        var deliveries = new DeliveryRepository(_dynamoDb, _deliveriesTable);
        Dictionary<string, object>? delivery = null;
        if (exit == PickupReopen.Exit.HandoffConfirmed)
        {
            var deliveryId = NoShowReopen.DeliveryIdOf(order, escrow);
            delivery = deliveryId.Length > 0 ? await deliveries.GetByIdAsync(deliveryId, consistentRead: true) : null;
            if (NoShowReopen.HandoffRefusalOf(order, delivery) is { } handoffRefusal)
            {
                LogNoShowReopen(adminUserId, correlationId, orderId, "failure", PickupReopen.NotAllowedCode, exit);
                return LambdaResponse.Error(409, PickupReopen.NotAllowedCode, handoffRefusal);
            }
        }

        var tripId = GetString(order, "tripId");
        var trips = new TripRepository(_dynamoDb, _tripsTable);
        var trip = NoShowReopen.TripStateOf(await trips.GetRawItemAsync(tripId), orderId);
        var (tripPlan, tripRefusal) = NoShowReopen.TripPlanOf(order, trip);
        if (tripPlan == null || trip == null)
        {
            LogNoShowReopen(adminUserId, correlationId, orderId, "failure", NoShowReopen.TripUnavailableCode, exit);
            return LambdaResponse.Error(409, NoShowReopen.TripUnavailableCode, tripRefusal ?? "The order's trip cannot take the booking back.");
        }

        var now = Clock();
        var nowIso = OrderCommitment.Iso(now);
        var fromJudgement = PickupJudgement.Outcome.TruckerNoShow;
        var deadlineAt = exit == PickupReopen.Exit.Pending
            ? PickupDeadline.Format(now.AddHours(body.DeadlineHours!.Value))
            : null;
        var quintals = NoShowReopen.QuintalsOf(order);

        var set = new Dictionary<string, object>
        {
            { PickupReopen.Field, PickupReopen.Stamp(exit, fromJudgement, expectedVersion, nowIso, deadlineAt) },
            { OrderCommitment.TruckerResponseField, OrderCommitment.Response.Accepted }
        };
        var remove = new List<string>
        {
            OrderCommitment.TransportFailureField, OrderCommitment.FaultPartyField,
            OrderCommitment.BuyerChoiceDeadlineAtField, OrderCommitment.BuyerChoiceReminderAtField
        };
        var expected = new Dictionary<string, string>
        {
            { "status", OrderCommitment.AwaitingTransportStatus },
            { PickupJudgement.Field, fromJudgement },
            { OrderCommitment.TransportFailureField, PickupJudgement.TruckerNoShowFailure },
            { OrderCommitment.TruckerResponseField, PickupJudgement.NoShowResponse }
        };
        var absent = new List<string>();
        if (exit == PickupReopen.Exit.Pending)
        {
            set["status"] = "accepted";
            set[PickupJudgement.Field] = PickupJudgement.Outcome.Pending;
            set[PickupDeadline.Field] = deadlineAt!;
            remove.Add(PickupJudgement.JudgedAtField);
            remove.Add(OrderCommitment.PickupOverdueNotifiedAtField);
        }
        else
        {
            set["status"] = "pickup_confirmed";
            set[PickupJudgement.Field] = PickupJudgement.Outcome.Scanned;
            set["activeDeliveryId"] = GetString(delivery!, "deliveryId");
            set["activeDeliveryClaimedAt"] = nowIso;
            // The slot as read: free, or still naming this delivery (a release that never landed).
            if (GetString(order, "activeDeliveryId") is { Length: > 0 } slot)
            {
                expected["activeDeliveryId"] = slot;
            }
            else
            {
                absent.Add("activeDeliveryId");
            }
        }

        // The judgement's queue entry (a late scan or check-in puts the order there as late_pickup_evidence) is
        // answered as FUP-287 answers it: removed, kept as hold_cap, left alone, or none may land before the write.
        var queuePlan = PickupReopen.QueuePlanOf(order, escrow);
        var queuedAt = GetString(order, AdminQueueFields.QueuedAt);
        switch (queuePlan)
        {
            case PickupReopen.QueuePlan.NotQueued:
                absent.Add(AdminQueueFields.Marker);
                break;
            case PickupReopen.QueuePlan.Remove:
                remove.AddRange(new[] { AdminQueueFields.Marker, AdminQueueFields.QueuedAt, AdminQueueFields.Reason, AdminQueueFields.EscrowId });
                expected[AdminQueueFields.QueuedAt] = queuedAt;
                break;
            case PickupReopen.QueuePlan.ToHoldCap:
                set[AdminQueueFields.Reason] = AdminQueueFields.ReasonHoldCap;
                expected[AdminQueueFields.QueuedAt] = queuedAt;
                break;
        }

        var linked = new List<TransactWriteItem>
        {
            tripPlan == NoShowReopen.TripPlan.Reserve
                ? trips.BuildReclaimBookingItem(tripId, trip.CreatedAt, orderId, quintals, nowIso)
                : trips.BuildConfirmBookingItem(tripId, trip.CreatedAt, orderId, nowIso)
        };
        if (delivery != null)
        {
            linked.Add(deliveries.BuildRevivedPickupItem(
                GetString(delivery, "deliveryId"), GetString(delivery, "tripId"), GetInt(delivery, "version"), nowIso));
        }

        if (escrow != null)
        {
            linked.Add(OrderRepository.EscrowHoldsMoneyCheck(
                new EscrowRowKey(_escrowsTable, GetString(escrow, "escrowId"), GetString(escrow, "createdAt")),
                notHoldCapped: queuePlan == PickupReopen.QueuePlan.Remove));
        }

        var write = await orders.TryUpdateGuardedAsync(orderId, set, remove, expectedVersion, expected, absent, linked);
        switch (write.Outcome)
        {
            case OrderLinkedWriteOutcome.Written:
                break;
            case OrderLinkedWriteOutcome.OrderNotFound:
                return LambdaResponse.Error(404, "ADMIN_012", "Transaction not found.");
            default:
                // A replay that landed in between answers as one; anything else changed under the write.
                var fresh = await orders.GetByIdAsync(orderId, consistentRead: true);
                if (fresh != null && NoShowReopen.IsReplay(fresh, expectedVersion, exit))
                {
                    LogNoShowReopen(adminUserId, correlationId, orderId, "replay", null, exit);
                    var raced = await ExcuseNoShowAsync(fresh, orderId, adminUserId, request, context);
                    return ReopenOk(orderId, exit, fresh, alreadyApplied: true, "This re-open was already applied.", raced);
                }

                LogNoShowReopen(adminUserId, correlationId, orderId, "failure", PickupReopen.ChangedCode, exit);
                return LambdaResponse.Error(409, PickupReopen.ChangedCode, write.Outcome == OrderLinkedWriteOutcome.LinkedItemRefused
                    ? "The trip, the delivery or the payment changed since the page read them (the trip may have filled up); "
                      + "nothing was written. Reload and check."
                    : "The order changed since the page read it; nothing was written. Reload and check.");
        }

        if (tripPlan == NoShowReopen.TripPlan.Reserve && trip.Status == "open" && trip.RemainingQuintals - quintals <= 0)
        {
            await MarkTripFullAsync(trips, tripId, orderId, context);
        }

        var auditContext = AuditContext.From(request, adminUserId, "admin");
        var status = exit == PickupReopen.Exit.Pending ? "accepted" : "pickup_confirmed";
        var deliveryNote = delivery != null ? $";deliveryId={GetString(delivery, "deliveryId")}" : "";
        var deadlineNote = deadlineAt != null ? $";deadlineAt={deadlineAt}" : "";
        var tripNote = tripPlan == NoShowReopen.TripPlan.Reserve ? "reserved" : "kept";
        await TransactionAuditWriter.EmitAsync(
            TxAuditRepo(), context, orderId, "order", orderId, PickupReopen.AuditAction, OrderCommitment.AwaitingTransportStatus, status,
            auditContext,
            metadata: $"exit={exit};judgement={fromJudgement}{deadlineNote}{deliveryNote};trip={tripNote};queue={QueueNote(queuePlan)};reason={reason}");
        if (delivery != null)
        {
            // The delivery leg of the timeline, as a pickup scan writes it (handoff=qr or code there).
            await TransactionAuditWriter.EmitAsync(
                TxAuditRepo(), context, orderId, "delivery", GetString(delivery, "deliveryId"), "picked_up", "cancelled", "pickedUp",
                auditContext, metadata: "handoff=admin");
        }

        context.Logger.LogInformation(
            $"{NoShowReopen.LogToken} orderId={MoneyAlarmLog.LogId(orderId)} exit={exit} trip={tripNote}");
        LogNoShowReopen(adminUserId, correlationId, orderId, "success", null, exit);

        var noShowEvent = await ExcuseNoShowAsync(order, orderId, adminUserId, request, context);
        return ReopenOk(orderId, exit, write.Order!, alreadyApplied: false,
            exit == PickupReopen.Exit.Pending
                ? "The no-show is re-opened: the trucker has a new pickup window until the new deadline."
                : "The handoff is recorded as picked up and the trip booking is back.",
            noShowEvent);
    }

    /// <summary>
    /// The trucker's <c>NO_SHOW</c> of this order (rule R10's slot), excused through ESC-32's decision path with the
    /// admin as the decider and <see cref="NoShowReopen.ExcuseNote"/> as the note the trucker reads; audited
    /// <c>reputation_excused</c> when applied. Never fails the re-open: the answer says what happened, and the same
    /// request again retries it.
    /// </summary>
    private async Task<string> ExcuseNoShowAsync(
        Dictionary<string, object> order, string orderId, string adminId, APIGatewayProxyRequest request, ILambdaContext context)
    {
        var truckerId = GetString(order, "truckerId");
        if (!ReputationPenaltiesEnabled || truckerId.Length == 0 || ReputationRepo == null || ReputationSvc is not { } service)
        {
            return NoShowReopen.NoShowEvent.Off;
        }

        var eventId = NoShowReopen.NoShowEventId(orderId);
        try
        {
            var result = await service.DecideAsync(
                truckerId, eventId, ReputationService.DecisionExcuse, NoShowReopen.ExcuseNote, until: null, adminId, context.Logger);
            switch (result.Outcome)
            {
                case ReputationAdminOutcome.Applied:
                    await AuditReputationAsync(request, context, truckerId, adminId, "reputation_excused", result,
                        $"event={eventId};note={NoShowReopen.ExcuseNote};source={NoShowReopen.Action}");
                    return NoShowReopen.NoShowEvent.Excused;
                case ReputationAdminOutcome.AlreadyApplied:
                    return NoShowReopen.NoShowEvent.AlreadyExcused;
                case ReputationAdminOutcome.EventNotFound or ReputationAdminOutcome.UserNotFound:
                    return NoShowReopen.NoShowEvent.NotRecorded;
                case ReputationAdminOutcome.EventDecided:
                    return NoShowReopen.NoShowEvent.Upheld;
                case ReputationAdminOutcome.AppealClosed:
                    return NoShowReopen.NoShowEvent.NotCounted;
                default:
                    return NoShowReopen.NoShowEvent.Failed;
            }
        }
        catch (Exception ex)
        {
            context.Logger.LogWarning(
                $"PICKUP_NO_SHOW_EXCUSE_FAILED orderId={MoneyAlarmLog.LogId(orderId)} [{ex.GetType().Name}]: the same request again retries it.");
            return NoShowReopen.NoShowEvent.Failed;
        }
    }

    /// <summary>
    /// A trip the reclaim left with no room leaves the open browse list, as after a reservation. Best effort: a lost
    /// race only leaves the trip open with zero room, which the next reservation's guard still refuses.
    /// </summary>
    private static async Task MarkTripFullAsync(TripRepository trips, string tripId, string orderId, ILambdaContext context)
    {
        try
        {
            await trips.TrySetStatusAsync(tripId, "open", "matched");
        }
        catch (Exception ex)
        {
            context.Logger.LogWarning(
                $"No-show re-open: trip {MoneyAlarmLog.LogId(tripId)} of order {MoneyAlarmLog.LogId(orderId)} stays open with no room [{ex.GetType().Name}].");
        }
    }

    /// <summary>The structured audit log line of every answer; the reason goes only to the transaction-audit row.</summary>
    private static void LogNoShowReopen(string adminUserId, string? correlationId, string orderId, string outcome, string? errorCode, string exit)
        => AuditLogger.LogAudit("ADMIN", "PICKUP_NO_SHOW_REOPEN",
            userId: adminUserId, correlationId: correlationId,
            entityType: "order", entityId: orderId, outcome: outcome, errorCode: errorCode, detail: $"exit={exit}");
}
