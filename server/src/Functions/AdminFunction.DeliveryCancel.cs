using Amazon.DynamoDBv2.Model;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Models;

namespace WeredaConnect.Server.Functions;

/// <summary>
/// FUP-19 (spec E9, delivery-verification-api.md 2.9.0 "Admin cancel after pickup"): an admin cancels a delivery that
/// was picked up and never delivered, after the money behind it was decided. The rules are
/// <see cref="AdminDeliveryCancel"/>; this file reads the delivery, its order and every escrow of the two, and writes
/// one transaction (<see cref="AdminDeliveryCancelRepository"/>). Admin group only, a reason of 10 to 500 characters,
/// the delivery version the page read.
/// </summary>
public partial class AdminFunction
{
    /// <summary>
    /// POST /admin/deliveries/{id}/{action}: <c>cancel</c> with <c>{reason, expectedVersion}</c>. Any other action is
    /// <c>404 ADMIN_002</c>. The delivery goes <c>pickedUp</c> or <c>inTransit</c> to <c>cancelled</c>
    /// (<c>cancelReason: admin_cancelled</c>, <c>cancelledAt</c>, version plus one) with its transaction-audit row
    /// (<c>admin_delivery_cancelled</c>, the admin and the reason), conditioned on the delivery's status and version, the
    /// order's status and every escrow's status as read. No order, money, trip, booking or stock is written. The same
    /// request again answers <c>alreadyCancelled</c> and writes nothing.
    /// </summary>
    private async Task<APIGatewayProxyResponse> HandleDeliveryActionAsync(
        string deliveryId, string action, APIGatewayProxyRequest request, ILambdaContext context)
    {
        if (action != AdminDeliveryCancel.Action)
        {
            return LambdaResponse.Error(404, "ADMIN_002", "Admin endpoint not found.");
        }

        // It closes a delivery behind money: the admin pool's admin group only, as for the money routes.
        if (!AdminAuthHelper.IsInAdminGroup(request))
        {
            return AdminAuthHelper.Forbidden();
        }

        if (string.IsNullOrEmpty(_deliveriesTable) || string.IsNullOrEmpty(_ordersTable)
            || string.IsNullOrEmpty(_escrowsTable) || string.IsNullOrEmpty(_transactionAuditTable))
        {
            context.Logger.LogError(
                "Delivery cancel is not configured (DELIVERIES_TABLE, ORDERS_TABLE, ESCROWS_TABLE or TRANSACTION_AUDIT_TABLE missing).");
            return LambdaResponse.Error(503, "ADMIN_004", "Delivery tables not configured.");
        }

        AdminDeliveryCancelRequest? body;
        try
        {
            body = JsonSerializer.Deserialize(request.Body ?? "", AppJsonContext.Default.AdminDeliveryCancelRequest);
        }
        catch (JsonException)
        {
            return LambdaResponse.Error(400, "ADMIN_005", "Invalid JSON in request body.");
        }

        if (body == null)
        {
            return LambdaResponse.Error(400, "ADMIN_005", "Request body is required.");
        }

        var reason = (body.Reason ?? "").Trim();
        if (reason.Length is < AdminDeliveryCancel.ReasonMinLength or > AdminDeliveryCancel.ReasonMaxLength)
        {
            return LambdaResponse.Error(400, AdminDeliveryCancel.InvalidCode,
                "reason is required: 10 to 500 characters saying why the delivery is cancelled.", "reason");
        }

        if (body.ExpectedVersion is not > 0)
        {
            return LambdaResponse.Error(400, AdminDeliveryCancel.InvalidCode,
                "expectedVersion is required: the delivery version the page read.", "expectedVersion");
        }

        var expectedVersion = body.ExpectedVersion.Value;
        var adminUserId = AuthHelper.GetUserId(request) ?? "admin";
        var correlationId = AuditLogger.GetCorrelationId(request);

        var deliveries = new DeliveryRepository(_dynamoDb, _deliveriesTable);
        var delivery = await deliveries.GetByIdAsync(deliveryId, consistentRead: true);
        if (delivery == null || !string.IsNullOrEmpty(GetStringOrNull(delivery, "deletedAt")))
        {
            return LambdaResponse.Error(404, "ADMIN_018", "Delivery not found.");
        }

        if (AdminDeliveryCancel.IsReplay(delivery, expectedVersion))
        {
            LogDeliveryCancel(adminUserId, correlationId, deliveryId, "replay", null);
            return DeliveryCancelOk(delivery, null, alreadyCancelled: true, "This delivery was already cancelled by an admin.");
        }

        if (GetInt(delivery, "version") != expectedVersion)
        {
            LogDeliveryCancel(adminUserId, correlationId, deliveryId, "failure", AdminDeliveryCancel.ChangedCode);
            return LambdaResponse.Error(409, AdminDeliveryCancel.ChangedCode,
                "The delivery changed since the page read it; nothing was written. Reload and check.");
        }

        if (AdminDeliveryCancel.DeliveryRefusalOf(delivery) is { } deliveryRefusal)
        {
            LogDeliveryCancel(adminUserId, correlationId, deliveryId, "failure", AdminDeliveryCancel.NotAllowedCode);
            return LambdaResponse.Error(409, AdminDeliveryCancel.NotAllowedCode, deliveryRefusal);
        }

        var orderId = GetStringOrNull(delivery, "orderId");
        var order = string.IsNullOrEmpty(orderId)
            ? null
            : await new OrderRepository(_dynamoDb, _ordersTable).GetByIdAsync(orderId, consistentRead: true);
        if (AdminDeliveryCancel.OrderRefusalOf(orderId, order) is { } orderRefusal)
        {
            LogDeliveryCancel(adminUserId, correlationId, deliveryId, "failure", AdminDeliveryCancel.NotAllowedCode);
            return LambdaResponse.Error(409, AdminDeliveryCancel.NotAllowedCode, orderRefusal);
        }

        var escrows = await DeliveryEscrowsAsync(orderId, deliveryId);
        if (AdminDeliveryCancel.FirstHeldEscrow(escrows) is { } held)
        {
            LogDeliveryCancel(adminUserId, correlationId, deliveryId, "failure", AdminDeliveryCancel.MoneyHeldCode);
            return LambdaResponse.Error(409, AdminDeliveryCancel.MoneyHeldCode, AdminDeliveryCancel.MoneyHeldMessage(held));
        }

        var fromStatus = GetString(delivery, "status");
        var now = Clock().ToString("o");
        var transactionId = string.IsNullOrEmpty(orderId) ? deliveryId : orderId;
        var auditContext = AuditContext.From(request, adminUserId, "admin");
        var checks = new List<AdminDeliveryCancelCheck>();
        if (order != null)
        {
            checks.Add(new AdminDeliveryCancelCheck(_ordersTable, new Dictionary<string, AttributeValue>
            {
                ["orderId"] = new() { S = GetString(order, "orderId") },
                ["createdAt"] = new() { S = GetString(order, "createdAt") }
            }, GetString(order, "status")));
        }

        checks.AddRange(escrows.Select(e => new AdminDeliveryCancelCheck(_escrowsTable, new Dictionary<string, AttributeValue>
        {
            ["escrowId"] = new() { S = GetString(e, "escrowId") },
            ["createdAt"] = new() { S = GetString(e, "createdAt") }
        }, GetString(e, "status"))));

        var plan = new AdminDeliveryCancelPlan
        {
            DeliveryId = deliveryId,
            TripId = GetString(delivery, "tripId"),
            FromStatus = fromStatus,
            ExpectedVersion = expectedVersion,
            CancelReason = AdminDeliveryCancel.CancelReason,
            Now = now,
            AuditEntry = TransactionAuditWriter.BuildOnceEntry(
                transactionId, "delivery", deliveryId, AdminDeliveryCancel.AuditAction, fromStatus, "cancelled", auditContext, now,
                AdminDeliveryCancel.AuditMetadata(order != null ? GetString(order, "status") : null, escrows, reason)),
            Checks = checks
        };

        var write = await new AdminDeliveryCancelRepository(_dynamoDb, _deliveriesTable, _transactionAuditTable).CancelAsync(plan);
        if (write != AdminDeliveryCancelWrite.Written)
        {
            // A replay that landed in between answers as one; anything else changed under the write.
            var fresh = await deliveries.GetByIdAsync(deliveryId, consistentRead: true);
            if (fresh != null && AdminDeliveryCancel.IsReplay(fresh, expectedVersion))
            {
                LogDeliveryCancel(adminUserId, correlationId, deliveryId, "replay", null);
                return DeliveryCancelOk(fresh, null, alreadyCancelled: true, "This delivery was already cancelled by an admin.");
            }

            LogDeliveryCancel(adminUserId, correlationId, deliveryId, "failure", AdminDeliveryCancel.ChangedCode);
            return LambdaResponse.Error(409, AdminDeliveryCancel.ChangedCode, write == AdminDeliveryCancelWrite.DeliveryChanged
                ? "The delivery changed since the page read it; nothing was written. Reload and check."
                : "The order or its payment changed since they were read; nothing was written. Reload and check.");
        }

        context.Logger.LogInformation(
            $"{AdminDeliveryCancel.LogToken} deliveryId={MoneyAlarmLog.LogId(deliveryId)} from={fromStatus}");
        LogDeliveryCancel(adminUserId, correlationId, deliveryId, "success", null);
        var written = new Dictionary<string, object>(delivery)
        {
            ["status"] = "cancelled",
            ["cancelReason"] = AdminDeliveryCancel.CancelReason,
            ["cancelledAt"] = now,
            ["version"] = expectedVersion + 1
        };
        return DeliveryCancelOk(written, fromStatus, alreadyCancelled: false,
            "The delivery is cancelled. The order and the money are as they were.");
    }

    /// <summary>
    /// Every escrow behind the delivery, read strongly consistent: the order's own (<c>escrow-order-{orderId}</c>),
    /// the ones its order index names, and the one the delivery index names (a legacy <c>/escrow/initiate</c>
    /// escrow). The indexes only find the ids; each row is read again from the table.
    /// </summary>
    private async Task<List<Dictionary<string, object>>> DeliveryEscrowsAsync(string? orderId, string deliveryId)
    {
        var repo = new EscrowRepository(_dynamoDb, _escrowsTable);
        var ids = new List<string>();
        if (!string.IsNullOrEmpty(orderId))
        {
            ids.Add($"{OrderEscrowIdPrefix}{orderId}");
            ids.AddRange((await repo.QueryByOrderIdAsync(orderId)).Select(e => GetString(e, "escrowId")));
        }

        if (await repo.GetByDeliveryIdAsync(deliveryId) is { } byDelivery)
        {
            ids.Add(GetString(byDelivery, "escrowId"));
        }

        var escrows = new List<Dictionary<string, object>>();
        foreach (var id in ids.Where(id => id.Length > 0).Distinct(StringComparer.Ordinal))
        {
            if (await repo.GetByIdAsync(id, consistentRead: true) is { } escrow)
            {
                escrows.Add(escrow);
            }
        }

        return escrows;
    }

    private static APIGatewayProxyResponse DeliveryCancelOk(
        Dictionary<string, object> delivery, string? fromStatus, bool alreadyCancelled, string message)
    {
        var response = new AdminDeliveryCancelResponse
        {
            DeliveryId = GetString(delivery, "deliveryId"),
            OrderId = GetStringOrNull(delivery, "orderId"),
            FromStatus = fromStatus,
            Status = GetString(delivery, "status"),
            CancelReason = GetString(delivery, "cancelReason"),
            CancelledAt = GetStringOrNull(delivery, "cancelledAt"),
            Version = GetInt(delivery, "version"),
            AlreadyCancelled = alreadyCancelled,
            Message = message
        };
        return LambdaResponse.Ok(response, AppJsonContext.Default.AdminDeliveryCancelResponse);
    }

    /// <summary>The structured audit log line of every answer; the reason goes only to the transaction-audit row.</summary>
    private static void LogDeliveryCancel(string adminUserId, string? correlationId, string deliveryId, string outcome, string? errorCode)
        => AuditLogger.LogAudit("ADMIN", "DELIVERY_CANCEL",
            userId: adminUserId, correlationId: correlationId,
            entityType: "delivery", entityId: deliveryId, outcome: outcome, errorCode: errorCode);
}
