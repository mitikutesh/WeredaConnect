using Amazon.DynamoDBv2.Model;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.Services;

namespace WeredaConnect.Server.Functions;

/// <summary>
/// FUP-287 (delivery-verification-api.md 2.8.0 P.2 "The admin side", order-lifecycle.md 2.16.0): an admin
/// re-opens a judged pickup after reconciling a real handoff, since a late scan stays refused. The rules are
/// <see cref="PickupReopen"/>; this file reads the order, its delivery and its escrow, and writes one
/// transaction. Admin group only, a reason of 10 to 500 characters, the order version the page read.
/// </summary>
public partial class AdminFunction
{
    /// <summary>
    /// POST /admin/orders/{orderId}/{action}: <c>reopen-pickup</c> with
    /// <c>{exit, reason, expectedVersion, deadlineHours?}</c>. Any other action is <c>404 ADMIN_002</c>.
    /// <list type="bullet">
    /// <item><c>pending</c>: <c>pickupJudgement</c> back to <c>pending</c>, <c>pickupDeadlineAt</c> now plus
    /// <c>deadlineHours</c>, <c>pickupJudgedAt</c> and the overdue notice removed, the check-ins kept (a late
    /// one stays late), so the order cron judges it again a minute after the new deadline (the repository
    /// derives the <c>DeadlineIndex</c> entry from the row).</item>
    /// <item><c>handoff_confirmed</c>: the active delivery <c>pending → pickedUp</c> and the order
    /// <c>accepted → pickup_confirmed</c>, judged <c>scanned</c>, as a pickup scan leaves them.</item>
    /// </list>
    /// Both remove the judgement's admin queue entry (or keep it as <c>hold_cap</c> when the escrow's hold cap rides
    /// on it) and stamp <c>pickupReopen</c>, in one transaction with the order's version, status, judgement and queue
    /// entry as read (no entry may land on an order read without one), the delivery's status and version, and the
    /// escrow still holding the money (and not capped, when the entry goes). The same request again answers
    /// <c>alreadyApplied</c> and writes nothing.
    /// </summary>
    private async Task<APIGatewayProxyResponse> HandleOrderActionAsync(
        string orderId, string action, APIGatewayProxyRequest request, ILambdaContext context)
    {
        if (action != PickupReopen.Action)
        {
            return LambdaResponse.Error(404, "ADMIN_002", "Admin endpoint not found.");
        }

        // It moves an order and its delivery: the admin pool's admin group only, as for the money routes.
        if (!AdminAuthHelper.IsInAdminGroup(request))
        {
            return AdminAuthHelper.Forbidden();
        }

        if (string.IsNullOrEmpty(_ordersTable) || string.IsNullOrEmpty(_deliveriesTable) || string.IsNullOrEmpty(_escrowsTable))
        {
            context.Logger.LogError("Pickup re-open is not configured (ORDERS_TABLE, DELIVERIES_TABLE or ESCROWS_TABLE missing).");
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

        if (PickupReopen.IsReplay(order, expectedVersion, exit))
        {
            LogReopen(adminUserId, correlationId, orderId, "replay", null, exit);
            return ReopenOk(orderId, exit, order, alreadyApplied: true, "This re-open was already applied.");
        }

        if (ReopenRefusal(order, expectedVersion) is { } refused)
        {
            LogReopen(adminUserId, correlationId, orderId, "failure", refused.Code, exit);
            return LambdaResponse.Error(409, refused.Code, refused.Message);
        }

        var deliveries = new DeliveryRepository(_dynamoDb, _deliveriesTable);
        Dictionary<string, object>? delivery = null;
        if (exit == PickupReopen.Exit.HandoffConfirmed)
        {
            var deliveryId = GetString(order, "activeDeliveryId");
            delivery = deliveryId.Length > 0 ? await deliveries.GetByIdAsync(deliveryId, consistentRead: true) : null;
            if (PickupReopen.HandoffRefusalOf(order, delivery) is { } handoffRefusal)
            {
                LogReopen(adminUserId, correlationId, orderId, "failure", PickupReopen.NotAllowedCode, exit);
                return LambdaResponse.Error(409, PickupReopen.NotAllowedCode, handoffRefusal);
            }
        }

        var escrow = await ReadOrderEscrowAsync(orderId);
        if (escrow != null && !PickupReopen.HoldsMoney(escrow))
        {
            LogReopen(adminUserId, correlationId, orderId, "failure", PickupReopen.MoneyNotHeldCode, exit);
            return LambdaResponse.Error(409, PickupReopen.MoneyNotHeldCode,
                "The order's payment is no longer simply held (refunding, settling, frozen for review or confirmed): "
                + "decide the money first, then reload.");
        }

        var now = Clock();
        var nowIso = OrderCommitment.Iso(now);
        var fromJudgement = GetString(order, PickupJudgement.Field);
        var deadlineAt = exit == PickupReopen.Exit.Pending
            ? PickupDeadline.Format(now.AddHours(body.DeadlineHours!.Value))
            : null;

        var set = new Dictionary<string, object>
        {
            { PickupReopen.Field, PickupReopen.Stamp(exit, fromJudgement, expectedVersion, nowIso, deadlineAt) }
        };
        var remove = new List<string>();
        var expected = new Dictionary<string, string>
        {
            { "status", "accepted" },
            { PickupJudgement.Field, fromJudgement }
        };
        if (exit == PickupReopen.Exit.Pending)
        {
            set[PickupJudgement.Field] = PickupJudgement.Outcome.Pending;
            set[PickupDeadline.Field] = deadlineAt!;
            remove.Add(PickupJudgement.JudgedAtField);
            remove.Add(OrderCommitment.PickupOverdueNotifiedAtField);
        }
        else
        {
            set["status"] = "pickup_confirmed";
            set[PickupJudgement.Field] = PickupJudgement.Outcome.Scanned;
        }

        // The judgement's queue entry is answered by this action, unless the escrow's hold cap rides on it; an entry
        // for another reason stays; with none at read, none may land before the write (PickupReopen.QueuePlanOf).
        var queuePlan = PickupReopen.QueuePlanOf(order, escrow);
        var queuedAt = GetString(order, AdminQueueFields.QueuedAt);
        var absent = new List<string>();
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

        var linked = new List<TransactWriteItem>();
        if (delivery != null)
        {
            linked.Add(deliveries.BuildConfirmedPickupItem(
                GetString(delivery, "deliveryId"), GetString(delivery, "tripId"), GetInt(delivery, "version"), nowIso));
        }

        if (escrow != null)
        {
            // Removing the entry: also only while the escrow's hold cap is not stamped (as read), since a capped
            // escrow has left the sweep windows and waits on that entry.
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
                if (fresh != null && PickupReopen.IsReplay(fresh, expectedVersion, exit))
                {
                    LogReopen(adminUserId, correlationId, orderId, "replay", null, exit);
                    return ReopenOk(orderId, exit, fresh, alreadyApplied: true, "This re-open was already applied.");
                }

                LogReopen(adminUserId, correlationId, orderId, "failure", PickupReopen.ChangedCode, exit);
                return LambdaResponse.Error(409, PickupReopen.ChangedCode, write.Outcome == OrderLinkedWriteOutcome.LinkedItemRefused
                    ? "The delivery or the payment changed since the page read them; nothing was written. Reload and check."
                    : "The order changed since the page read it; nothing was written. Reload and check.");
        }

        var auditContext = AuditContext.From(request, adminUserId, "admin");
        var status = exit == PickupReopen.Exit.Pending ? "accepted" : "pickup_confirmed";
        var deliveryNote = delivery != null ? $";deliveryId={GetString(delivery, "deliveryId")}" : "";
        var deadlineNote = deadlineAt != null ? $";deadlineAt={deadlineAt}" : "";
        await TransactionAuditWriter.EmitAsync(
            TxAuditRepo(), context, orderId, "order", orderId, PickupReopen.AuditAction, "accepted", status, auditContext,
            metadata: $"exit={exit};judgement={fromJudgement}{deadlineNote}{deliveryNote};queue={QueueNote(queuePlan)};reason={reason}");
        if (delivery != null)
        {
            // The delivery leg of the timeline, as a pickup scan writes it (handoff=qr or code there).
            await TransactionAuditWriter.EmitAsync(
                TxAuditRepo(), context, orderId, "delivery", GetString(delivery, "deliveryId"), "picked_up", "pending", "pickedUp",
                auditContext, metadata: "handoff=admin");
        }

        context.Logger.LogInformation(
            $"{PickupReopen.LogToken} orderId={MoneyAlarmLog.LogId(orderId)} exit={exit} judgement={fromJudgement}");
        LogReopen(adminUserId, correlationId, orderId, "success", null, exit);
        return ReopenOk(orderId, exit, write.Order!, alreadyApplied: false,
            exit == PickupReopen.Exit.Pending ? "The pickup is open again until the new deadline." : "The handoff is recorded as picked up.");
    }

    /// <summary>400 <c>PICKUP_REOPEN_INVALID</c> naming the first field that is missing or out of range, or null.</summary>
    private static APIGatewayProxyResponse? ReopenRequestProblem(PickupReopenRequest body)
    {
        if (body.Exit is not (PickupReopen.Exit.Pending or PickupReopen.Exit.HandoffConfirmed))
        {
            return LambdaResponse.Error(400, PickupReopen.InvalidCode, "exit must be pending or handoff_confirmed.", "exit");
        }

        var reason = (body.Reason ?? "").Trim();
        if (reason.Length is < PickupReopen.ReasonMinLength or > PickupReopen.ReasonMaxLength)
        {
            return LambdaResponse.Error(400, PickupReopen.InvalidCode,
                "reason is required: 10 to 500 characters saying how the handoff was reconciled.", "reason");
        }

        if (body.ExpectedVersion is not > 0)
        {
            return LambdaResponse.Error(400, PickupReopen.InvalidCode,
                "expectedVersion is required: the order version the page read.", "expectedVersion");
        }

        if (body.Exit == PickupReopen.Exit.Pending)
        {
            return body.DeadlineHours is >= PickupReopen.MinDeadlineHours and <= PickupReopen.MaxDeadlineHours
                ? null
                : LambdaResponse.Error(400, PickupReopen.InvalidCode,
                    "deadlineHours is required with pending: 1 to 168 hours from now.", "deadlineHours");
        }

        return body.DeadlineHours == null
            ? null
            : LambdaResponse.Error(400, PickupReopen.InvalidCode,
                "deadlineHours goes only with pending: a confirmed handoff keeps the deadline.", "deadlineHours");
    }

    /// <summary>The 409 for an order whose version or judgement does not allow the re-open, or null.</summary>
    private static (string Code, string Message)? ReopenRefusal(Dictionary<string, object> order, int expectedVersion)
    {
        if (GetInt(order, "version") != expectedVersion)
        {
            return (PickupReopen.ChangedCode, "The order changed since the page read it; nothing was written. Reload and check.");
        }

        return PickupReopen.RefusalOf(order) is { } refusal ? (PickupReopen.NotAllowedCode, refusal) : null;
    }

    /// <summary>
    /// The order's money: its order escrow (<c>escrow-order-{orderId}</c>, else the newest escrow the order index
    /// names), read strongly consistent, or null when no escrow record holds it (nothing to check then).
    /// </summary>
    private async Task<Dictionary<string, object>?> ReadOrderEscrowAsync(string orderId)
    {
        var escrows = new EscrowRepository(_dynamoDb, _escrowsTable);
        var escrow = await escrows.GetByIdAsync($"{OrderEscrowIdPrefix}{orderId}", consistentRead: true);
        if (escrow == null && await GetEscrowByOrderIdAsync(orderId) is { } indexed
            && GetString(indexed, "escrowId") is { Length: > 0 } escrowId)
        {
            escrow = await escrows.GetByIdAsync(escrowId, consistentRead: true);
        }

        return escrow;
    }

    /// <summary>What the audit row says of the queue entry: <c>none</c>, <c>removed</c>, <c>hold_cap</c> or <c>kept</c>.</summary>
    private static string QueueNote(PickupReopen.QueuePlan plan) => plan switch
    {
        PickupReopen.QueuePlan.Remove => "removed",
        PickupReopen.QueuePlan.ToHoldCap => AdminQueueFields.ReasonHoldCap,
        PickupReopen.QueuePlan.Keep => "kept",
        _ => "none"
    };

    private static APIGatewayProxyResponse ReopenOk(
        string orderId, string exit, Dictionary<string, object> order, bool alreadyApplied, string message)
    {
        var stamp = PickupReopen.LastReopenOf(order);
        var response = new PickupReopenResponse
        {
            OrderId = orderId,
            Exit = exit,
            FromJudgement = stamp?.FromJudgement,
            Status = GetStringOrNull(order, "status"),
            PickupJudgement = GetStringOrNull(order, PickupJudgement.Field),
            PickupDeadlineAt = GetStringOrNull(order, PickupDeadline.Field),
            DeliveryId = exit == PickupReopen.Exit.HandoffConfirmed ? GetStringOrNull(order, "activeDeliveryId") : null,
            Version = GetInt(order, "version"),
            AlreadyApplied = alreadyApplied,
            Message = message
        };
        return LambdaResponse.Ok(response, AppJsonContext.Default.PickupReopenResponse);
    }

    /// <summary>The structured audit log line of every answer; the reason goes only to the transaction-audit row.</summary>
    private static void LogReopen(string adminUserId, string? correlationId, string orderId, string outcome, string? errorCode, string exit)
        => AuditLogger.LogAudit("ADMIN", "PICKUP_REOPEN",
            userId: adminUserId, correlationId: correlationId,
            entityType: "order", entityId: orderId, outcome: outcome, errorCode: errorCode, detail: $"exit={exit}");
}
