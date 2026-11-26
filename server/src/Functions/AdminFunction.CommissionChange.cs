using System.Globalization;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.Services;

namespace WeredaConnect.Server.Functions;

/// <summary>
/// Order-lifecycle 2.26.0, "Admin commission change": the commission is negotiable per order, so before the buyer pays
/// an admin may set another rate on one order. The rules are <see cref="OrderCommissionChange"/>; this file reads the
/// order and writes it once, guarded on the version, the status and the payment fields as read. Admin group only, a
/// reason of 10 to 500 characters, the order version the page read. The buyer hears it from the orders stream
/// (<c>order_commission_changed</c>, notifications-api.md).
/// </summary>
public partial class AdminFunction
{
    /// <summary>
    /// POST /admin/orders/{orderId}/commission with <c>{commissionRate, reason, expectedVersion}</c>: the order's
    /// <c>commissionRate</c>, <c>commissionAmount</c> and <c>orderTotal</c> at the new rate and the
    /// <c>commissionChange</c> stamp, in one conditional write. The same request again answers <c>alreadyApplied</c>
    /// and writes nothing.
    /// </summary>
    private async Task<APIGatewayProxyResponse> HandleCommissionChangeAsync(
        string orderId, APIGatewayProxyRequest request, ILambdaContext context)
    {
        // It changes what the buyer pays: the admin pool's admin group only, as for the money routes.
        if (!AdminAuthHelper.IsInAdminGroup(request))
        {
            return AdminAuthHelper.Forbidden();
        }

        if (string.IsNullOrEmpty(_ordersTable))
        {
            context.Logger.LogError("Commission change is not configured (ORDERS_TABLE missing).");
            return LambdaResponse.Error(503, "ADMIN_004", "Order tables not configured.");
        }

        CommissionChangeRequest? body;
        try
        {
            body = JsonSerializer.Deserialize(request.Body ?? "", AppJsonContext.Default.CommissionChangeRequest);
        }
        catch (JsonException)
        {
            return LambdaResponse.Error(400, "ADMIN_005", "Invalid JSON in request body.");
        }

        if (body == null)
        {
            return LambdaResponse.Error(400, "ADMIN_005", "Request body is required.");
        }

        if (CommissionRequestProblem(body) is { } invalid)
        {
            return invalid;
        }

        var rate = body.CommissionRate!.Value;
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

        if (OrderCommissionChange.IsReplay(order, expectedVersion, rate))
        {
            LogCommissionChange(adminUserId, correlationId, orderId, "replay", null, rate);
            return CommissionOk(orderId, order, alreadyApplied: true, "This commission was already set.");
        }

        if (GetInt(order, "version") != expectedVersion)
        {
            LogCommissionChange(adminUserId, correlationId, orderId, "failure", OrderCommissionChange.ChangedCode, rate);
            return LambdaResponse.Error(409, OrderCommissionChange.ChangedCode,
                "The order changed since the page read it; nothing was written. Reload and check.");
        }

        var now = Clock();
        var status = GetString(order, "status");
        var holdUntil = GetStringOrNull(order, PaymentHoldFields.HoldUntil);
        var cropCost = GetDecimal(order, "cropCost");
        var fromRate = GetDecimal(order, "commissionRate");
        if (OrderCommissionChange.RefusalOf(status, GetStringOrNull(order, "paymentVerifiedAt"), holdUntil, cropCost, now) is { } refusal)
        {
            LogCommissionChange(adminUserId, correlationId, orderId, "failure", OrderCommissionChange.NotAllowedCode, rate);
            return LambdaResponse.Error(409, OrderCommissionChange.NotAllowedCode, refusal);
        }

        if (fromRate == rate)
        {
            LogCommissionChange(adminUserId, correlationId, orderId, "failure", OrderCommissionChange.NotAllowedCode, rate);
            return LambdaResponse.Error(409, OrderCommissionChange.NotAllowedCode, "The order already has this commission rate.");
        }

        var fromTotal = GetDecimal(order, "orderTotal");
        var money = OrderCommissionChange.Compute(cropCost, GetDecimal(order, "transportCost"), rate);
        // Stored as numbers the way the order create stores them (doubles of the santim-exact decimals).
        var set = new Dictionary<string, object>
        {
            { "commissionRate", (double)rate },
            { "commissionAmount", (double)money.CommissionAmount },
            { "orderTotal", (double)money.OrderTotal },
            { OrderCommissionChange.Field, OrderCommissionChange.Stamp(fromRate, rate, fromTotal, expectedVersion, OrderCommitment.Iso(now)) }
        };

        // Lands only on the order as read: its version, its status, no verified payment, and the webhook's payment
        // hold as it was (it is written without a version bump, so it is checked on its own).
        var expected = new Dictionary<string, string> { { "status", status } };
        var absent = new List<string> { "paymentVerifiedAt" };
        if (string.IsNullOrEmpty(holdUntil))
        {
            absent.Add(PaymentHoldFields.HoldUntil);
        }
        else
        {
            expected[PaymentHoldFields.HoldUntil] = holdUntil;
        }

        var write = await orders.TryUpdateGuardedAsync(
            orderId, set, Array.Empty<string>(), expectedVersion, expected, absent, Array.Empty<Amazon.DynamoDBv2.Model.TransactWriteItem>());
        switch (write.Outcome)
        {
            case OrderLinkedWriteOutcome.Written:
                break;
            case OrderLinkedWriteOutcome.OrderNotFound:
                return LambdaResponse.Error(404, "ADMIN_012", "Transaction not found.");
            default:
                // A replay that landed in between answers as one; anything else changed under the write.
                var fresh = await orders.GetByIdAsync(orderId, consistentRead: true);
                if (fresh != null && OrderCommissionChange.IsReplay(fresh, expectedVersion, rate))
                {
                    LogCommissionChange(adminUserId, correlationId, orderId, "replay", null, rate);
                    return CommissionOk(orderId, fresh, alreadyApplied: true, "This commission was already set.");
                }

                LogCommissionChange(adminUserId, correlationId, orderId, "failure", OrderCommissionChange.ChangedCode, rate);
                return LambdaResponse.Error(409, OrderCommissionChange.ChangedCode,
                    "The order changed since the page read it; nothing was written. Reload and check.");
        }

        var auditContext = AuditContext.From(request, adminUserId, "admin");
        await TransactionAuditWriter.EmitAsync(
            TxAuditRepo(), context, orderId, "order", orderId, OrderCommissionChange.AuditAction, status, status, auditContext,
            metadata: $"fromRate={Invariant(fromRate)};toRate={Invariant(rate)};fromTotal={Invariant(fromTotal)};"
                + $"toTotal={Invariant(money.OrderTotal)};reason={reason}");

        context.Logger.LogInformation(
            $"{OrderCommissionChange.LogToken} orderId={MoneyAlarmLog.LogId(orderId)} fromRate={Invariant(fromRate)} toRate={Invariant(rate)}");
        LogCommissionChange(adminUserId, correlationId, orderId, "success", null, rate);
        return CommissionOk(orderId, write.Order!, alreadyApplied: false, "The new commission is set. The buyer is told.");
    }

    /// <summary>400 <c>COMMISSION_CHANGE_INVALID</c> naming the first field that is missing or out of range, or null.</summary>
    private static APIGatewayProxyResponse? CommissionRequestProblem(CommissionChangeRequest body)
    {
        if (body.CommissionRate is not { } rate || !OrderCommissionChange.IsValidRate(rate))
        {
            return LambdaResponse.Error(400, OrderCommissionChange.InvalidCode,
                "commissionRate is required: 0 to 0.20 (20 %), at most 4 decimals.", "commissionRate");
        }

        if (!OrderCommissionChange.IsValidReason(body.Reason))
        {
            return LambdaResponse.Error(400, OrderCommissionChange.InvalidCode,
                "reason is required: 10 to 500 characters saying what was agreed and with whom.", "reason");
        }

        return body.ExpectedVersion is > 0
            ? null
            : LambdaResponse.Error(400, OrderCommissionChange.InvalidCode,
                "expectedVersion is required: the order version the page read.", "expectedVersion");
    }

    /// <summary>The answer of a change: the order's money now and, from the stamp, before.</summary>
    private static APIGatewayProxyResponse CommissionOk(
        string orderId, Dictionary<string, object> order, bool alreadyApplied, string message)
    {
        var before = OrderCommissionChange.BeforeOf(order);
        var response = new CommissionChangeResponse
        {
            OrderId = orderId,
            Status = GetStringOrNull(order, "status"),
            CommissionRate = GetDecimal(order, "commissionRate"),
            CommissionAmount = GetDecimal(order, "commissionAmount"),
            OrderTotal = GetDecimal(order, "orderTotal"),
            CommissionRateBefore = before?.Rate,
            OrderTotalBefore = before?.Total,
            Version = GetInt(order, "version"),
            AlreadyApplied = alreadyApplied,
            Message = message
        };
        return LambdaResponse.Ok(response, AppJsonContext.Default.CommissionChangeResponse);
    }

    private static string Invariant(decimal value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>The structured audit log line of every answer; the reason goes only to the transaction-audit row.</summary>
    private static void LogCommissionChange(
        string adminUserId, string? correlationId, string orderId, string outcome, string? errorCode, decimal rate)
        => AuditLogger.LogAudit("ADMIN", "COMMISSION_CHANGE",
            userId: adminUserId, correlationId: correlationId,
            entityType: "order", entityId: orderId, outcome: outcome, errorCode: errorCode,
            detail: $"rate={rate.ToString(CultureInfo.InvariantCulture)}");
}
