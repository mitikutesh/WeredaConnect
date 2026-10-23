using System.Globalization;
using Amazon.DynamoDBv2;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Models;

namespace WeredaConnect.Server.Functions;

/// <summary>
/// EventBridge scheduled event handler for order background jobs.
/// Routes based on "action" field in the request body:
///   - auto_cancel_expired (hourly): cancel unpaid orders older than 24h
///
/// Error codes:
/// - 400: ORDER_CRON_VALIDATION - Invalid or missing action
/// - 500: ORDER_CRON_ERROR - Unexpected failure during processing
/// </summary>
public class OrderCronFunction
{
    private readonly IOrderRepository _orderRepo;
    // Optional: unified transaction-audit trail. Auto-cancel is a system-driven
    // order transition that would otherwise be invisible in the monitor.
    private readonly ITransactionAuditRepository? _transactionAuditRepo;
    // ESC-45: the Telebirr webhook inbox; an order whose verified payment is still pending there is
    // never failed. Absent env: only the order's payment hold protects it.
    private readonly IWebhookInboxRepository? _inboxRepo;

    private const int ExpirationHours = 24;

    /// <summary>ESC-45: pending inbox rows read per run (pages of 100); past it the payment-failed step waits a run.</summary>
    private const int MaxPendingInboxPages = 20;

    /// <summary>
    /// Default constructor for Lambda runtime.
    /// </summary>
    public OrderCronFunction()
    {
        var ordersTable = Environment.GetEnvironmentVariable("ORDERS_TABLE")
                          ?? throw new InvalidOperationException("ORDERS_TABLE is not set.");

        var dynamoDb = new AmazonDynamoDBClient();
        _orderRepo = new OrderRepository(dynamoDb, ordersTable);

        var txAuditTable = Environment.GetEnvironmentVariable("TRANSACTION_AUDIT_TABLE");
        _transactionAuditRepo = txAuditTable != null
            ? new TransactionAuditRepository(dynamoDb, txAuditTable)
            : null;

        var idempotencyTable = Environment.GetEnvironmentVariable("ESCROW_IDEMPOTENCY_TABLE");
        _inboxRepo = !string.IsNullOrEmpty(idempotencyTable)
            ? new WebhookInboxRepository(dynamoDb, idempotencyTable)
            : null;
    }

    /// <summary>
    /// Constructor for testing with mocked dependencies.
    /// </summary>
    public OrderCronFunction(
        IOrderRepository orderRepo,
        ITransactionAuditRepository? transactionAuditRepo = null,
        IWebhookInboxRepository? inboxRepo = null)
    {
        _orderRepo = orderRepo ?? throw new ArgumentNullException(nameof(orderRepo));
        _transactionAuditRepo = transactionAuditRepo;
        _inboxRepo = inboxRepo;
    }

    public async Task<APIGatewayProxyResponse> FunctionHandler(
        APIGatewayProxyRequest request,
        ILambdaContext context)
    {
        context.Logger.LogInformation("OrderCronFunction invoked.");

        // Parse action from request body
        string? action = null;
        if (!string.IsNullOrEmpty(request.Body))
        {
            try
            {
                var payload = JsonSerializer.Deserialize(request.Body, AppJsonContext.Default.DictionaryStringString);
                payload?.TryGetValue("action", out action);
            }
            catch (JsonException)
            {
                return LambdaResponse.Error(400, "ORDER_CRON_VALIDATION",
                    "Invalid JSON in request body.");
            }
        }

        // Fallback: check query string parameters
        if (string.IsNullOrEmpty(action))
        {
            request.QueryStringParameters?.TryGetValue("action", out action);
        }

        if (string.IsNullOrEmpty(action))
        {
            return LambdaResponse.Error(400, "ORDER_CRON_VALIDATION",
                "Missing 'action' field. Expected: auto_cancel_expired.");
        }

        var safeAction = action.Length > 50 ? action[..50] : action;
        context.Logger.LogInformation($"Order cron action: {safeAction}");

        try
        {
            return action switch
            {
                "auto_cancel_expired" => await HandleAutoCancelExpiredAsync(context),
                _ => LambdaResponse.Error(400, "ORDER_CRON_VALIDATION",
                    $"Unknown action '{safeAction}'. Expected: auto_cancel_expired.")
            };
        }
        catch (Exception ex)
        {
            context.Logger.LogError($"OrderCronFunction error for action '{safeAction}': [{ex.GetType().Name}]");
            return LambdaResponse.Error(500, "ORDER_CRON_ERROR",
                $"Order cron job '{safeAction}' failed unexpectedly.");
        }
    }

    /// <summary>
    /// Auto-cancel orders left in "created" or "pending_payment" status for 24 hours.
    /// These are orders where the buyer never completed payment. A "created" order counts from
    /// createdAt, a "pending_payment" order from when it last entered that status (ESC-47).
    /// </summary>
    private async Task<APIGatewayProxyResponse> HandleAutoCancelExpiredAsync(ILambdaContext context)
    {
        var now = DateTime.UtcNow;
        var cutoff = now.AddHours(-ExpirationHours).ToString("o");

        var cancelledCount = 0;

        // Cancel expired "created" orders
        cancelledCount += await CancelExpiredByStatusAsync("created", cutoff, now, context);

        // Cancel expired "pending_payment" orders
        cancelledCount += await CancelExpiredByStatusAsync("pending_payment", cutoff, now, context);

        // ESC-01: a submitted payment that was never confirmed (abandoned or refused checkout,
        // or a mock auto-fund that failed) fails after 24 hours, so the buyer can pay again or
        // cancel. No checkout is still open then: they stop opening 20 hours after submission.
        var paymentFailedCount = await FailStalePaymentsAsync(now, context);

        var summary = new Dictionary<string, string>
        {
            { "action", "auto_cancel_expired" },
            { "cancelled", cancelledCount.ToString() },
            { "paymentFailed", paymentFailedCount.ToString() },
            { "completedAt", now.ToString("o") }
        };

        context.Logger.LogInformation(
            $"Order auto-cancel complete: {cancelledCount} orders cancelled, {paymentFailedCount} payments failed.");

        return new APIGatewayProxyResponse
        {
            StatusCode = 200,
            Body = JsonSerializer.Serialize(summary, AppJsonContext.Default.DictionaryStringString),
            Headers = new Dictionary<string, string> { { "Content-Type", "application/json" } }
        };
    }

    private async Task<int> CancelExpiredByStatusAsync(
        string status, string createdBefore, DateTime now, ILambdaContext context)
    {
        var orders = await _orderRepo.QueryByStatusForCronAsync(status, createdBefore);
        var threshold = now.AddHours(-ExpirationHours);
        var cancelledCount = 0;

        foreach (var order in orders)
        {
            // ESC-47: a pending_payment order gets its 24 hours from when it entered that status
            // (pendingPaymentAt, stamped by sync), so a buyer who retries a failed payment on an old
            // order through pending_payment is not cancelled at the next run. createdAt is never
            // later, so the query's createdAt filter stays a safe pre-filter; orders from before
            // the stamp keep counting from createdAt.
            if (status == "pending_payment"
                && DateTime.TryParse(GetString(order, "pendingPaymentAt"), CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var enteredAt)
                && enteredAt > threshold)
            {
                continue;
            }

            var orderId = GetString(order, "orderId");
            var version = GetInt(order, "version");

            context.Logger.LogInformation($"Auto-cancelling expired order {orderId} (was {status}).");

            var updateData = new Dictionary<string, object>
            {
                { "status", "cancelled" },
                { "cancelledAt", now.ToString("o") },
                { "cancelReason", $"Auto-cancelled: no payment within {ExpirationHours} hours." }
            };

            var result = await _orderRepo.UpdateAsync(orderId, updateData, version);

            if (result.IsSuccess)
            {
                cancelledCount++;
                await TransactionAuditWriter.EmitAsync(
                    _transactionAuditRepo, context, orderId, "order", orderId,
                    "order_cancelled", status, "cancelled",
                    AuditContext.ForSystem(),
                    metadata: $"Auto-cancelled: no payment within {ExpirationHours}h.");
            }
            else
            {
                context.Logger.LogError(
                    $"Auto-cancel failed for order {orderId}: {result.Error?.Code} - {result.Error?.Message}");
            }
        }

        return cancelledCount;
    }

    /// <summary>
    /// ESC-01: move <c>payment_submitted</c> orders whose payment clock (<c>paymentSubmittedAt</c>,
    /// or <c>createdAt</c> for orders from before it was stamped) passed 24 hours to
    /// <c>payment_failed</c>, version-checked so a concurrent funding wins.
    /// ESC-45 (D8): never while a verified Telebirr payment for the order is pending in the webhook
    /// inbox (its replay may still fund it): such orders are skipped, both by the inbox's pending
    /// partition and by the order's own <c>paymentHoldUntil</c>, and the write itself is
    /// conditional on the hold. If the inbox cannot be read, the step waits for the next run.
    /// </summary>
    private async Task<int> FailStalePaymentsAsync(DateTime now, ILambdaContext context)
    {
        var threshold = now.AddHours(-ExpirationHours);
        var nowIso = WebhookInbox.Iso(now);

        HashSet<string>? pendingPaymentOrders = null;
        if (_inboxRepo != null)
        {
            try
            {
                pendingPaymentOrders = await PendingPaymentOrdersAsync();
            }
            catch (Exception ex)
            {
                context.Logger.LogError(
                    $"Payment-failed sweep skipped this run: the webhook inbox could not be read [{ex.GetType().Name}].");
                return 0;
            }

            if (pendingPaymentOrders == null)
            {
                context.Logger.LogError(
                    $"Payment-failed sweep skipped this run: more than {MaxPendingInboxPages * 100} pending inbox rows.");
                return 0;
            }
        }

        // createdAt is never later than paymentSubmittedAt, so it is a safe pre-filter.
        var orders = await _orderRepo.QueryByStatusForCronAsync("payment_submitted", threshold.ToString("o"))
            ?? new List<Dictionary<string, object>>();
        var failedCount = 0;

        foreach (var order in orders)
        {
            if (DateTime.TryParse(GetString(order, "paymentSubmittedAt"), CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var submittedAt)
                && submittedAt > threshold)
            {
                continue;
            }

            var orderId = GetString(order, "orderId");
            if (pendingPaymentOrders?.Contains(orderId) == true)
            {
                context.Logger.LogInformation(
                    $"Payment-failed sweep: order {MoneyAlarmLog.LogId(orderId)} has a verified payment pending in the webhook inbox.");
                continue;
            }

            if (DateTime.TryParse(GetString(order, PaymentHoldFields.HoldUntil), CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var holdUntil)
                && holdUntil > now)
            {
                context.Logger.LogInformation(
                    $"Payment-failed sweep: order {MoneyAlarmLog.LogId(orderId)} held for a pending payment until {WebhookInbox.Iso(holdUntil)}.");
                continue;
            }

            if (await _orderRepo.TryMarkPaymentFailedAsync(orderId, GetInt(order, "version"), nowIso))
            {
                failedCount++;
                await TransactionAuditWriter.EmitAsync(
                    _transactionAuditRepo, context, orderId, "order", orderId,
                    "order_updated", "payment_submitted", "payment_failed",
                    AuditContext.ForSystem(),
                    metadata: $"No confirmed payment within {ExpirationHours}h.");
            }
            else
            {
                // Most often a funding that landed in the meantime (version moved) or a payment hold
                // written after the read: nothing to do.
                context.Logger.LogWarning(
                    $"Payment-failed sweep skipped order {MoneyAlarmLog.LogId(orderId)}: changed, funded or held since it was read.");
            }
        }

        return failedCount;
    }

    /// <summary>
    /// ESC-45: the orders of every pending row in the webhook inbox, or null when there are more
    /// rows than one run reads (then nothing is failed this run).
    /// </summary>
    private async Task<HashSet<string>?> PendingPaymentOrdersAsync()
    {
        var orders = new HashSet<string>(StringComparer.Ordinal);
        Dictionary<string, Amazon.DynamoDBv2.Model.AttributeValue>? startKey = null;
        for (var page = 0; page < MaxPendingInboxPages; page++)
        {
            var (rows, next) = await _inboxRepo!.QueryPendingAsync(100, startKey);
            foreach (var row in rows)
            {
                if (!string.IsNullOrEmpty(row.OrderId))
                {
                    orders.Add(row.OrderId);
                }
            }

            if (next == null)
            {
                return orders;
            }

            startKey = next;
        }

        return null;
    }

    private static string GetString(Dictionary<string, object> item, string key)
        => item.TryGetValue(key, out var value) ? value.ToString() ?? "" : "";

    private static int GetInt(Dictionary<string, object> item, string key)
    {
        if (!item.TryGetValue(key, out var value))
        {
            return 0;
        }

        return value switch
        {
            int i => i,
            string s => int.TryParse(s, out var result) ? result : 0,
            _ => 0
        };
    }
}
